# `SubAgentEval` and `SubAgent`: two MCP tools for ad-hoc micro-evals and general sub-dispatch

## Motivation

The current way to get a quick read on "can a weak local model actually do X against this repo" is
a manual loop: Claude writes a finding/proposal doc, tells the user what would need testing, the
user decides whether and how to decompose it into a runnable step, manually points LM Studio at a
model, runs it, and manually reports the transcript/outcome back to Claude. Each of those handoffs
costs a context switch and a round trip, and the alternative - writing a one-off PlanStepRunner
step or a new ModelEval fixture test per ad-hoc question - drifts out of sync with the current repo
almost immediately, which is exactly the failure mode `project_testing_philosophy_and_categories.md`
already names: synthetic tests narrow enough to control precisely are also narrow enough to stop
resembling the real task, and a dedicated fixture per question doesn't survive the repo changing
under it.

What's actually needed is something in between "write a permanent test" and "ask the user to run
one by hand": a tool Claude can call directly, mid-conversation, to hand a prompt and a model name
to a real LM Studio model, let it attempt a small task against an isolated copy of the repo, and get
back a structured pass/fail report without leaving the session. This also directly serves the
failure doctrine in `CLAUDE.md`: most of the value of running a weak model against RoslynSentinel's
own tools is discovering environment defects (a misleading tool description, a missing guardrail, an
error message that doesn't name the fix) - and that discovery currently requires the multi-party
manual loop above just to get one data point. Cheapening that loop is the whole point of this tool,
not a side benefit.

**Two tools, not one.** Once `ModelAgentRunner` is reachable from the live server at all, it is
reachable by *any* model driving that server's tool-calling loop, not just Claude - `RunAsync`
builds its exposed tool list from `_mcpClient.ListToolsAsync()` with no caller-identity filtering
(`ModelAgentRunner.cs:114-121`), so a weak model mid-task would see and could call the exact same
tool Claude does. That is a legitimate, wanted capability - a weak model delegating a sub-piece of
its own task is exactly the kind of composability this server should support - but it is a
different capability from Claude's own eval-comparison use case, with a different trust and cost
model attached (see "Proposed contract" below), so this design splits it into two tools rather than
one tool serving both audiences with conditional behavior:

- **`SubAgentEval`** - Claude-facing (in practice: whichever caller sits at the top of a call tree,
  human-driven or not). Opinionated: always builds/tests the result, always returns the structured
  pass/fail comparison report this doc's Motivation describes. This is what the rest of this
  document primarily designs.
- **`SubAgent`** - general-purpose, callable by any model including one already running inside a
  `SubAgentEval` or `SubAgent` call. Unopinionated: runs a prompt on a model, returns the output in
  the requested format, with the same large-result offload guard other tools already use (see
  "Proposed contract"). No build/test opinion, no result-comparison shape - just prompt in, formatted
  output out.

## Non-goals

- **Not a new execution engine.** This does not propose writing a new tool-calling loop; see
  "Proposed contract" below for why the existing one is reused as-is, by both tools.
- **Not a built-in weak-vs-capable comparison mode (for `SubAgentEval`).** `SubAgentEval` runs
  exactly one model per call. Comparing two models' behavior on the same task is done by Claude
  calling `SubAgentEval` twice (once per model) and reasoning over the two structured results
  itself, the same way Claude already synthesizes any other pair of tool results. A dedicated "run
  both and diff" mode was considered and explicitly deferred - see Open items.
- **Not a hosted-model integration.** v1 targets LM Studio local models only, matching every existing
  piece this tool reuses (`LmStudioAgentClient`, `LlmOptions`, `reference_model_eval_procedure.md`).
  Wiring in a hosted Anthropic (or other) API call is out of scope for this design, for either tool.
- **Not an in-place / caller-controlled isolation option, for either tool.** Every `SubAgentEval` or
  `SubAgent` call runs in a fresh git worktree; there is no parameter on either tool to run against
  the live loaded solution instead. See "Proposed contract" for why - this applies uniformly to a
  top-level call from Claude and to a nested call from a model already running inside one.
- **Not unbounded recursion.** A model invoked via `SubAgent` does not itself see `SubAgent` (or
  `SubAgentEval`) in its own exposed tool list - nesting depth is hard-capped at 1. See "Proposed
  contract" for the mechanism.
- **Not a way for a nested call to pick its own model.** Only the top-level caller (Claude, or
  whatever sits at the root of a call tree) chooses which LM Studio model a `SubAgent` call targets.
  A weak model calling `SubAgent` from inside a `SubAgentEval`/`SubAgent` run does not get a `model`
  parameter of its own - see "Proposed contract."
- **Not a change to `ModelAgentRunner`, `LmStudioAgentClient`, `GitWorktreeManager`, or
  `IStepBranchStrategy` themselves.** All four are reused as-is (module relocation aside - see
  "Migration path"); this document does not propose behavior changes to any of them.

## Current shape

Three pieces of machinery already do almost everything `SubAgent` needs, but all three currently
live where the live server cannot reach them:

- **`ModelAgentRunner`** (`RoslynSentinel.Tests.ModelEval/AgentLoop/ModelAgentRunner.cs:21-96`) drives
  an LM Studio model through a real MCP tool-calling loop against a real `McpClient`, with a turn cap,
  a wall-clock cap (`_wallClockCap`, default 10 minutes, `ModelAgentRunner.cs:93`), and a
  repeated-failure breaker (`_repeatedFailureLimit`, mandatory constructor parameter, guarded by an
  `ArgumentOutOfRangeException` throw at `ModelAgentRunner.cs:81-87`). `RunAsync`
  (`ModelAgentRunner.cs:105-110`) takes a system prompt, a user prompt, and a transcript directory,
  and returns an `AgentRunResult` (`AgentTranscript.cs:135-162`): `StopReason` (an
  `AgentStopReason` enum - `ModelFinished`/`TurnCapExceeded`/`WallClockCapExceeded`/
  `UnknownToolRequested`/`RepeatedToolFailure`, `AgentTranscript.cs:99-113`), the full `Transcript`,
  its on-disk `TranscriptPath`, `TurnCount`, and (when the breaker tripped) a `RepeatedFailureDetail`.
  `Converged` (`AgentTranscript.cs:161`) is true only when the model stopped on its own within the
  caps - it says nothing about whether the task was done correctly, only that the model didn't get
  cut off.
- **`LmStudioAgentClient`** (`RoslynSentinel.Tests.ModelEval/AgentLoop/LmStudioAgentClient.cs:23-48`)
  is the actual LM Studio HTTP wiring: streams the OpenAI-compatible `/v1/responses` endpoint, logs a
  best-effort loaded-model check on construction, and - critically - is where the mid-turn-truncation
  detection lives: `CompleteOnceAsync` throws when the Responses API reports `Status="incomplete"`
  (`LmStudioAgentClient.cs:228-234`), which is what closed the false-positive no-op step from run
  `20260916-211352-610` referenced throughout this file's own comments.
- **`GitWorktreeManager`** + **`IStepBranchStrategy`**
  (`RoslynSentinel.Tools.PlanStepRunner/GitWorktreeManager.cs`,
  `RoslynSentinel.Tools.PlanStepRunner/IStepBranchStrategy.cs`) create a worktree per step, off a
  branch decided by the chosen strategy (`SharedBranchStrategy` - one branch reused by every step in
  a run; `StackedBranchStrategy` - one branch per step, stacked). `CreateWorktree`
  (`GitWorktreeManager.cs:33-67`) already raises a specific, actionable error when a branch is already
  checked out in another worktree left over from a different run
  (`GitWorktreeManager.cs:52-60`, naming the blocking path and how to resolve it), and worktree paths
  are namespaced under `<runDir>/<step-name>/Worktree` (`GitWorktreeManager.cs:18-19`) - i.e.
  collision avoidance across concurrent runs is a property of the caller-supplied `runDir` /
  branch-name inputs, not something baked into the manager. This matters directly for
  `project_concurrent_sessions.md`: multiple VS Code windows running RoslynSentinel simultaneously is
  expected, so `SubAgent` must mint a `runDir`/branch name that can't collide with another session's
  concurrent `SubAgent` call or PlanStepRunner run, the same way each PlanStepRunner invocation's own
  `RunId`/`RunDir` already has to be distinct per run.
- **`RoslynSentinel.Tools.PlanStepRunner/Program.cs`** ties all three together as a worked example of
  exactly the shape `SubAgent` needs, end to end:
  - `DotnetProcess.BuildAsync` (`DotnetProcess.cs:15-46`) builds the worktree's own
    `Server.Advanced.csproj` to an isolated output directory - deliberately not `build.ps1`, whose own
    doc comment (`DotnetProcess.cs:6-11`) explains why: `build.ps1` unconditionally kills every
    `*RoslynSentinel*` process system-wide by name, which would kill the very server this tool is
    running inside of, plus any other concurrent worktree's in-flight run.
  - `Program.cs:194-207` launches that freshly-built server exe as a `StdioClientTransport` child
    process, with `--transport` deliberately omitted (defaults to stdio) and `--solution` deliberately
    omitted too - the comment at `Program.cs:190-193` explains that auto-load on `--solution` is
    fire-and-forget/unawaited (`WarmupAndAutoLoadAdvanced`,
    `ServiceRegistrationExtensionsAdvanced.cs:243`), so it can't be trusted to have finished before the
    first tool call arrives; `LoadSolution` is called explicitly instead (`Program.cs:209-216`), which
    blocks until the real load completes.
  - `Program.cs:198-202` documents a basename-collision hazard: `--testing` puts `ProjectDoc` on
    `docs/testing/` instead of `docs/`, because the worktree is a copy of the same repo and shares
    plan/doc basenames with the production copy - without it, `ProjectDoc`'s basename fallback
    silently resolved to the *production* file, and run `20260910-013550-398` spent all 60 turns
    implementing a plan nobody asked for as a result.
  - `Program.cs:233-238` inlines the step's prompt content by value into the user prompt rather than
    handing over a path, for the same reason: a path forces the model to re-resolve it through
    `ProjectDoc`, reopening the same substitution risk.
  - `ModelAgentRunner`'s `maxTokensPerTurn` and `repeatedFailureLimit` are both explicit literals at
    the call site (`Program.cs:220-228`), each with an inline comment citing the incident that made
    them mandatory rather than defaulted.

None of this is reachable from the live server today. `ModelAgentRunner`, `LmStudioAgentClient`, and
`AgentTranscript` all live in `RoslynSentinel.Tests.ModelEval`, a test project - confirmed by its
`.csproj` (`RoslynSentinel.Tests.ModelEval.csproj:39-44`), which project-references
`RoslynSentinel.Advanced`, `RoslynSentinel.Basic`, `RoslynSentinel.Common`,
`RoslynSentinel.Server.Advanced`, and `RoslynSentinel.Server.Basic` - i.e. it depends on the server
projects, not the other way around. `RoslynSentinel.Tools.PlanStepRunner` (an exe) in turn
project-references `Tests.ModelEval` to reach `ModelAgentRunner`
(`RoslynSentinel.Tools.PlanStepRunner.csproj:31-33`). `RoslynSentinel.Server.Advanced` itself only
references `RoslynSentinel.Advanced`, `RoslynSentinel.Basic`, `RoslynSentinel.Common`, and
`RoslynSentinel.Server.Basic` (`RoslynSentinel.Server.Advanced.csproj:35-38`) - it cannot reference a
test project without inverting the dependency graph `project_dependency_direction.md` documents
(`Common` <- `Basic` <- `Advanced`, `Server.Basic` <- `Server.Advanced`, one-way only). A `SubAgentEval`
or `SubAgent` tool living in `Server.Advanced` (where every other `[McpServerTool]`-attributed class
lives - confirmed by grepping `RoslynSentinel.Server.Advanced/*.cs` for `McpServerTool`, which turns
up `ScanTools.cs`, `AsyncifyTools.cs`, `CodemodTools.cs`, `IntelligenceTools.cs`,
`AdvancedRefactoringTools.cs`, `GenerationTools.cs`, `QualityTools.cs`, `ModernizationTools.cs`,
`CommentingTools.cs`) cannot call `ModelAgentRunner` where it sits today.

One more piece of current shape matters for the parameter design below: `LmStudioAgentClient`'s
constructor reads its target model from `LlmOptions.Model`
(`LmStudioAgentClient.cs:42-44`), a process-wide static set once via `LlmOptions.Configure(args)` at
server startup (`LlmOptions.cs:12-13`, `78-...`). The live Advanced server process `SubAgent` runs
inside of already called `Configure` once, for its own purposes, at its own startup - it is not
something `SubAgent` can silently overwrite call-to-call without risking cross-talk if two
`SubAgent` calls (or a `SubAgent` call and something else reading `LlmOptions.Model`) are in flight
concurrently in the same process. See "New/blocking work" below.

## Proposed contract

Two new tools, both added as `*Tools`/`*Impl` pairs (`SubAgentEvalTools`/`SubAgentEvalImpl` and
`SubAgentTools`/`SubAgentImpl`, both in `RoslynSentinel.Server.Advanced`/`RoslynSentinel.Advanced`,
per `project_tools_impl_split_amendment.md`'s convention - see the existing
`WorkspaceBuildTestTools`/`WorkspaceBuildTestImpl` pair, `WorkspaceBuildTestTools.cs:10-18`, as the
worked pattern to imitate). They share the same underlying engine and most of the same design
points; the differences between them are called out explicitly in their own subsections below.

```
SubAgentEval(
    reason: ToolCallReason,
    prompt: string,
    model: string,
    baseBranch: string? = null,
    turnCap: int? = null,
    maxTokensPerTurn: int,          // no default - see below
    wallClockCapMinutes: int? = null)

SubAgent(
    reason: ToolCallReason,
    prompt: string,
    model: string,                  // ignored/rejected when called from inside a nested run - see below
    turnCap: int? = null,
    maxTokensPerTurn: int,          // no default - see below
    wallClockCapMinutes: int? = null,
    responseFormat: string? = null) // e.g. "text" | "json" - shape TBD, see Open items
```

Eight decisions below are settled (discussed and agreed with the user via `AskUserQuestion`), not
open for this doc to re-litigate. The first five apply to both tools; the last three are specific to
`SubAgent`'s general-purpose/nestable nature and have no `SubAgentEval` equivalent, since
`SubAgentEval` is always the top-level call by design (see Non-goals - Claude is the intended
caller, and nothing calls `SubAgentEval` from inside a nested run).

1. **Engine: reuse `ModelAgentRunner` as-is**, not a new lightweight runner, for both tools. It
   already implements exactly the loop each needs - turn cap, wall-clock cap, repeated-failure
   breaker, transcript.json + agent.log output, and truncation detection via `LmStudioAgentClient`'s
   `Status="incomplete"` check. Building a second, thinner loop would duplicate all of that and give
   it its own copy of the same bugs to rediscover independently.
2. **Result shape: a structured comparison report for `SubAgentEval`, not a raw transcript dump.**
   Modeled on the `SummarizeListResult`/`ListSummary` pattern
   (`RoslynSentinel.Common/SummarizeListResult.cs:1-57`, landed in commit `ae1ac91` and wired into
   `FindReferences`/`SearchSolutionText`/the offload backstop) - a per-tool-result grouping/summary a
   caller's `StatusMessage` can surface without forcing the caller to open the full payload.
   `SubAgentEval`'s result should carry, at minimum: build and test outcome (reusing `Build`/
   `RunTest`'s own outcome shapes where practical), turn count, `StopReason`, a files-touched /
   diff-stat summary (`GetDirtyPaths`, `GitWorktreeManager.cs:95-99`, already returns exactly this),
   and a short failure excerpt when the run didn't converge cleanly. The full `transcript.json` and
   `agent.log` stay on disk at their existing paths (`AgentRunResult.TranscriptPath`,
   `AgentTranscript.cs:145-148`); the tool result includes that path for a follow-up read rather than
   inlining the transcript into Claude's own context. This is new code to write, not a port -
   `SummarizeListResult` itself is the pattern to imitate, not something `SubAgentEval` calls
   directly (its per-file grouping shape doesn't fit a per-run agent outcome). `SubAgent`'s result
   shape is different by design - see its own subsection below.
3. **Model scope: LM Studio local models only for v1.** No hosted-API branch, for either tool.
   `model` names an already-loaded LM Studio model the same way `--llm-model`/
   `ROSLYNSENTINEL_LLM_MODEL` do today.
4. **Isolation: always worktree-isolated, no in-place option, for either tool.** Every call creates a
   fresh git worktree via the same `GitWorktreeManager`/`IStepBranchStrategy` machinery PlanStepRunner
   uses, never touches the live loaded solution or session state. A caller-controlled "run in place"
   toggle was not proposed - dog-fooding's own chokepoint principle (`CLAUDE.md`) argues against ever
   giving a sub-agent's uncontrolled edits a path into the live workspace the primary session is also
   using. This applies identically to a nested `SubAgent` call from a weak model: it gets its own
   fresh worktree, never the parent call's worktree and never the live session's workspace, closing
   off a confused weak model from corrupting state a human isn't watching in real time.
5. **Transport: stdio, not Http.** Explicitly considered and rejected. Launching the worktree's child
   server over Http/Kestrel does not remove the "must launch and own a server process for the
   worktree" requirement - `ServerHttp.cs:103` still blocks on `app.RunAsync()`, and something still
   has to track and kill that process's PID. Http only adds port-collision risk (against the user's
   own live dev server, or another concurrent worktree run - `project_concurrent_sessions.md`) plus a
   firewall/bind surface, for zero benefit. `StdioClientTransport` already owns the full child-process
   lifecycle (launch, pipe, dispose-kills) for free - exactly the reasoning
   `RoslynSentinel.Tools.PlanStepRunner/Program.cs:186-190`'s own comment gives for using stdio over
   Http, which both tools should inherit unchanged.
6. **Recursion: hard depth limit of 1, enforced by making the tool unrepresentable rather than
   checked at runtime.** A model running inside a `SubAgent` (or `SubAgentEval`) call must not see
   `SubAgent`/`SubAgentEval` in its own exposed tool list at all - the nested worktree's child server
   is launched with `--exclude-tools=SubAgentTools,SubAgentEvalTools` (the existing `--include-tools`/
   `--exclude-tools` flag pair, parsed by `ServerStartupHelpers.ParseArgs`
   (`RoslynSentinel.Server.Basic/ServerStartupHelpers.cs:45-48`) and consumed identically by both
   `Server.Basic`'s and `Server.Advanced`'s startup; note the flag operates at tool-*class* name
   granularity - `SubAgentTools`, not individual `[McpServerTool]` method names, per
   `ServerStartupHelpers.cs:103`'s own comment on shortened class-name matching), so
   `ListToolsAsync` on that nested `McpClient` never returns either tool and
   `ModelAgentRunner.RunAsync`'s tool-definition list
   (`ModelAgentRunner.cs:114-121`) is built without them. This is the "make the invalid call
   unrepresentable" approach `CLAUDE.md`'s failure doctrine prefers over a runtime depth counter that
   would let a nested call attempt recursion and then have to reject it after the fact - the model
   never has the option to try, and no depth-tracking parameter needs to be threaded through the call
   chain (which would itself be one more parameter a weak model could get wrong). A depth of exactly
   1 was chosen (a `SubAgent` call itself can dispatch no further `SubAgent` calls) rather than a
   deeper configurable limit, since nothing in the stated use case needs more than one level and each
   additional level compounds cost and blast radius multiplicatively.
7. **Model selection trust: only the top-level caller picks the model.** A nested `SubAgent` call
   (one made by a model already running inside a `SubAgentEval`/`SubAgent` run) does not get to
   choose which LM Studio model handles its own dispatched sub-task. Concretely: the nested worktree's
   child server is launched with `LlmOptions` already configured to the *same* model the top-level
   call specified (see migration step 2 below), and the nested `SubAgent` tool's `model` parameter is
   either omitted from the schema the nested model sees or is present but validated to equal the
   inherited model, rejecting any other value with a structured error naming the inherited model - the
   exact mechanism is an implementation choice, not designed further here (see Open items), but the
   outcome either way is that a weak model cannot escalate its own sub-dispatch to a different or more
   expensive model on its own initiative. Only the call tree's root caller (Claude, or a human driving
   `SubAgentEval` directly) decides which model is under test for the whole tree.
8. **Large-result guard for `SubAgent`: the existing generic offload filter, not a bespoke one.**
   `SubAgent`'s free-form output is already covered by `LargeResultHelper.OffloadThresholdBytes`
   (`RoslynSentinel.Common/LargeResultHelper.cs:19`, 30 KiB) - the same generic
   store-and-return-a-pointer mechanism every sufficiently large tool result already goes through
   (`StoreLargeResultAsync`, `LargeResultHelper.cs:29-36`, readable back via `GetLargeResult`; see
   `docs/current/proposal_centralized_large_result_filter.md` and
   `RoslynSentinel.Tests.Battery/LargeResultOffloadFilterTests.cs` for the filter this exercises).
   `SubAgent` does not need its own size-guard logic - wiring its result through the same return path
   every other tool uses is sufficient, confirmed by `LargeResultOffloadFilterTests.cs:37-44`'s own
   comment noting some tools are individually wired into a typed offload path
   (`ForPossiblyLargeDataAsync`) while others fall through to a generic filter regardless; either path
   already existing means `SubAgent` gets this for free rather than needing new offload code.

Design points on the parameters:

- **`maxTokensPerTurn` must stay a required parameter at the tool-schema layer, with no default
  anywhere in the call chain.** `ModelAgentRunner`'s constructor already enforces this at the C# API
  level - `maxTokensPerTurn` has no default value in its signature (`ModelAgentRunner.cs:72-79`), and
  its doc comment (`ModelAgentRunner.cs:61-71`) exists specifically because a silent 8192 default
  once let a model's turn get truncated mid-reasoning with zero tool calls emitted, which
  `LmStudioAgentClient` at the time could not distinguish from the model genuinely finishing -
  PlanStepRunner committed the truncated turn as a successful step (run `20260916-211352-610`, step
  `04-rename-symbol-decode`) before the `Status="incomplete"` check existed. The C# constructor being
  safe does not automatically make the *MCP tool schema* safe: an `[McpServerTool]` method parameter
  with a C# default value (`int maxTokensPerTurn = 8192`) would silently reintroduce exactly this
  default at the layer the model actually sees, even though the constructor two calls down still
  enforces "no default" internally. This is the exact class of bug
  `feedback_prefer_mandatory_params_to_close_footgun_roundtrips.md` names: an optional parameter
  meant to prevent a known failure mode just relocates the failure to the caller who omits it. Keep
  `maxTokensPerTurn` a required, non-defaulted MCP parameter.
- **Prompt content must be inlined by value, never referenced by path, for both tools.** If either
  tool's `prompt` parameter ever accepts a doc path instead of inline text, the same
  basename-collision hazard `Program.cs:198-202` and `Program.cs:233-238` already hit
  (run `20260910-013550-398`) reapplies: a worktree mirroring the production repo's own plan/doc
  files means `ProjectDoc`'s name-based resolution can silently answer with the wrong copy. The
  contract above takes `prompt: string` (inline content) specifically to close this off from the
  start rather than needing the same fix bolted on after a repeat incident.
- **`reason: ToolCallReason`** is included on both tools for parity with every other mutating/
  expensive tool in this server (see `ToolParams.Reason` used throughout
  `WorkspaceBuildTestTools.cs`) - not called out further here since it's not a design point specific
  to either tool.
- **`SubAgent`'s `responseFormat` parameter is a placeholder, not a designed contract.** The intent
  is that the caller (Claude, or a nested model) can ask for the sub-dispatched model's output back
  as plain text or as a specific structured shape (e.g. "answer in JSON matching this schema") rather
  than always getting freeform prose. The exact parameter shape - a format-name enum, an inline JSON
  Schema string, something else - is left to implementation; see Open items.

## Migration path

The blocking work, in rough dependency order:

1. **Relocate the agent-loop code out of `RoslynSentinel.Tests.ModelEval`.** This is the largest real
   chunk of work, not the tool class itself. `ModelAgentRunner`, `LmStudioAgentClient`,
   `AgentTranscript`/`AgentRunResult`/`AgentStopReason`/`RepeatedFailureDetail`, and
   `AgentSystemPrompts` all currently live under `RoslynSentinel.Tests.ModelEval/AgentLoop/`, a test
   project the live server does not and, per `project_dependency_direction.md`, should not reference.
   Two candidate destinations, deliberately not chosen here:
   - **`RoslynSentinel.Common`** - already the one project every other project can reach
     (`Common` <- `Basic` <- `Advanced`), and already hosts `LlmOptions`
     (`RoslynSentinel.Common/LlmOptions.cs`), so the agent-loop code would sit next to the options
     type it already depends on. Downside: `Common` is meant to be broadly shared low-level
     infrastructure, and the agent loop pulls in an HTTP client, JSON streaming, and MCP client
     dependencies that nothing else in `Common` currently needs.
   - **A new `RoslynSentinel.AgentLoop` project**, referenced by `Server.Advanced` (for both
     `SubAgentEval` and `SubAgent`), `Tests.ModelEval` (for the existing eval fixtures), and
     `Tools.PlanStepRunner` (for the existing runner). Keeps the dependency graph's shape closer to
     today's (a dedicated project, not a grab-bag addition to `Common`) at the cost of one more
     project to maintain.

   Whichever lands, `Tests.ModelEval` and `Tools.PlanStepRunner` update their references to depend on
   the relocated code instead of owning it - this should be achievable as a close-to-mechanical move
   (namespace/using changes, `.csproj` reference swaps) since the classes themselves are not proposed
   to change behavior as part of the move.
2. **Resolve the `LlmOptions.Model` static-vs-per-call-parameter mismatch.** `LmStudioAgentClient`
   currently reads its target model from the process-wide static `LlmOptions.Model`
   (`LmStudioAgentClient.cs:42-44`), set once at server startup. Both new tools' proposed `model`
   parameter is per-call. Today's single-shot CLI tools (`PlanStepRunner`, the ModelEval test host)
   never need to change `LlmOptions.Model` mid-process, so this mismatch has never surfaced before -
   `SubAgentEval`/`SubAgent` are the first callers that need a different model per invocation from
   inside one long-lived process, and worse, the live Advanced server process they run inside of
   already called `LlmOptions.Configure` once for its own startup purposes. This needs a concrete fix
   before either tool can safely support concurrent or sequential calls with different `model` values
   - either threading the model name through `LmStudioAgentClient`'s constructor/call sites as an
   explicit parameter instead of a static read, or some other mechanism that doesn't risk one call
   silently reading a model name a different concurrent call (or the server's own startup
   configuration) set. This is new, not something the relocation in step 1 fixes for free. This same
   fix is also where decision 7's "nested call inherits the parent's model" requirement gets
   implemented: whatever mechanism replaces the static read needs to thread the *inherited* model
   into the nested worktree's child-server launch, not just support an arbitrary per-call value.
3. **Build the in-process child-server launch/teardown sequence**, following
   `RoslynSentinel.Tools.PlanStepRunner/Program.cs`'s existing shape as a worked template rather than
   inventing a new one, shared by both tools:
   - `DotnetProcess.BuildAsync`-equivalent build of the worktree's own `Server.Advanced.csproj` to an
     isolated output directory - never `build.ps1` (see "Current shape" above for why).
   - Launch the built exe as a `StdioClientTransport` child (owns process lifetime for free). For a
     nested `SubAgent` call specifically, the launch args include
     `--exclude-tools=SubAgentTools,SubAgentEvalTools` per decision 6 above.
   - Explicit `LoadSolution` call, not reliance on `--solution` auto-load (fire-and-forget, unreliable
     to await from inside a tool call that needs the load to have actually finished).
   - Run the prompt via the relocated `ModelAgentRunner`.
   - Dispose the `McpClient`/transport (kills the child process) and remove the worktree, mirroring
     `GitWorktreeManager.TryRemoveWorktree`'s tolerant-of-failure cleanup
     (`GitWorktreeManager.cs:163-167` - a Windows path-length failure removing deeply-nested build
     output is logged, not fatal, and a leftover worktree is harmless).
4. **Add the `SubAgentEvalTools`/`SubAgentEvalImpl` and `SubAgentTools`/`SubAgentImpl` pairs**, wiring
   the above into the contract in "Proposed contract." Sequencing these two relative to each other is
   not fixed by this doc - `SubAgentEval` is the primary motivating use case and could reasonably land
   first with `SubAgent` following, or both could land together since they share nearly all of steps
   1-3; not designed further here.
5. **Write the structured result builder** (`SummarizeListResult`-style helper for a `SubAgentEval`
   run's outcome, and separately, whatever formatting `SubAgent`'s `responseFormat` parameter ends up
   needing - see Open items). New code, not a port.

Each of these is independently substantial; this doc does not propose attempting them in one pass.

## Relationship to other in-flight proposals

- **`docs/current/design_read_chokepoint.md`** - unrelated in mechanism (that doc is about
  `CurrentSolution` read access inside a single running server), but both share the same underlying
  concern about a scattered/duplicated access pattern accumulating call sites before a chokepoint
  exists to route them through. Not a dependency either direction.
- **`project_di_tool_split_plan_2026_09_05` / `project_tools_impl_split_amendment.md`** - both new
  tools adopt the `*Tools`/`*Impl` split as a matter of course, not as something this doc is
  proposing freshly; no changes to that convention are needed or proposed here.
- **PlanStepRunner itself** - not superseded or replaced by this proposal. PlanStepRunner remains the
  right tool for a sequenced, multi-step, committed run across several plan files; `SubAgentEval` is
  for a single ad-hoc micro-eval Claude wants an answer to inline, without pre-writing a step file or
  deciding on a branch/worktree strategy by hand, and `SubAgent` is for a model (Claude or a weak
  model mid-task) dispatching a single general sub-prompt the same way. The tools are expected to keep
  coexisting, and step 1 of "Migration path" (relocating the shared agent-loop code) benefits all
  three rather than favoring one.

## Open items

- **Common vs. new `RoslynSentinel.AgentLoop` project** for the relocation in migration step 1 - both
  named above as candidates, neither chosen.
- **The `LlmOptions.Model` static-vs-per-call mismatch's actual fix shape** (migration step 2) - named
  as a concrete blocker above, not designed. Needs its own decision before implementation starts. Now
  also needs to account for decision 7's model-inheritance requirement (a nested call is pinned to the
  parent's model), not just per-call override in isolation.
- **Nested `model`-parameter rejection mechanism** (decision 7) - whether a nested `SubAgent` call's
  `model` parameter is omitted from the schema the nested model sees entirely, or present but
  server-validated to equal the inherited model with a structured rejection otherwise. Not chosen
  here; either satisfies the "only the root caller picks the model" requirement.
- **Built-in "compare weak vs. capable model" mode for `SubAgentEval`** - explicitly out of scope for
  v1 (see Non-goals), noted here as a plausible future addition if calling `SubAgentEval` twice and
  comparing by hand turns out to be a recurring enough pattern to be worth automating.
- **`SubAgent`'s `responseFormat` parameter shape** - flagged as a placeholder in "Proposed contract."
  Whether it's a small enum of canned formats, an inline JSON Schema string the model is asked to
  conform to, or something else, is not designed here.
- **Exact structured-result field list for `SubAgentEval`** - "Proposed contract" names the fields
  that clearly matter (build/test outcome, turn count, stop reason, files-touched/diff-stat, failure
  excerpt, transcript path) but the precise shape should be finalized against `Build`/`RunTest`'s own
  existing result types where they overlap, rather than inventing parallel ones, once implementation
  starts.
- **Concrete worktree/branch naming scheme for either tool's calls** - "Current shape" asserts
  `GitWorktreeManager`'s existing collision handling should cover concurrent-session safety as long as
  each call mints a sufficiently distinct `runDir`/branch name, matching how each PlanStepRunner run
  already avoids colliding with another - and, for `SubAgent`, distinct from its own parent call's
  worktree too. The exact naming scheme (timestamp-based? GUID-based? something identifying the
  calling session and, for nested calls, the parent run?) is not decided here.
- **Timeout/cleanup behavior if the worktree build itself fails** (step 3's `DotnetProcess.BuildAsync`
  equivalent) - `DotnetProcess.BuildAsync` throws on a nonzero exit code
  (`DotnetProcess.cs:38-44`) with stdout/stderr inlined into the exception message; both tools'
  boundary needs to catch this and return a structured, non-leaking error per `CLAUDE.md`'s "never
  leak raw exceptions... into a tool's `ResultError`" convention, rather than this doc prescribing the
  exact wording now.

## Status

Design proposed, implementation plan drafted 2026-09-24:
`docs/current/plans/plan_subagent_tool_implementation.md`. Tracked in `docs/current/TODO.md` under
"`SubAgentEval`/`SubAgent` MCP tools". No `SubAgentTools`/`SubAgentImpl` or
`SubAgentEvalTools`/`SubAgentEvalImpl` code exists anywhere in the repo as of 2026-09-24 (confirmed by
search) - pending user review of the plan before implementation starts.
