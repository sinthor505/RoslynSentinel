# RoslynSentinel

An MCP server exposing Roslyn-backed C# analysis and refactoring to AI agents. It bridges what a
language model can reason about and what the compiler actually knows.

## Mission

Enable agents — **especially weak or self-hosted models** — to safely and efficiently navigate and
refactor C# codebases. The explicit goal is to let a small local model punch above its weight: it
should drive meaningful, semantically-correct refactoring by calling structured tools, never by
falling back to grep, regex search-and-replace, file-read loops, or manual text edits.

If an agent reaches for a shell command to inspect or modify C# code, that is a gap in the tool
surface, not a deficiency in the agent.

## Where guidance belongs: CLAUDE.md vs. memory

This file is loaded into **every** session unconditionally, from turn one. The auto-memory system
(`MEMORY.md` and its linked files) is loaded **reactively**, by relevance — which usually means an
agent finds a memory entry only *after* it has already made the mistake the entry warns about, not
before. That difference in *when* something is seen, not how well-written it is, is what decides
where a piece of guidance goes:

- **Put it here** if it's a standing rule, convention, or gotcha an agent must follow or avoid
  *before* acting — anywhere a fresh session doing the wrong thing on turn one, having never
  searched memory, would be a real and repeatable failure. This is true almost by default for
  anything that would otherwise be a `type: feedback` memory (that type exists specifically to stop
  repeating corrections) and for `type: project` facts describing a still-true tool/architecture
  limitation or required workaround (not a one-time resolved incident).
- **Leave it in memory** if reactive discovery is fine — open ideas not yet built, one-off historical
  incidents that are fully resolved, scoped facts about in-progress work, or pointers to external
  systems. These are legitimately notes/WIP, not conventions every session needs up front.
- **When promoting a memory entry into this file:** distill it — cut the incident narrative,
  dated "updated on"/"superseded" history, and hedging, down to the current rule plus the one-line
  "why" that makes it stick. Then mark the source memory file as superseded (banner + pointer to the
  section here) and move its index line to `MEMORY_CLOSED.md`, so a future session that opens the raw
  file directly doesn't mistake the incident history for the live procedure.
- If unsure which side a piece of guidance belongs on, the test is: "would getting this wrong on
  turn one, before ever searching memory, be a costly, repeatable mistake?" If yes, it belongs here.

## Shell tool choice

This is a **Windows environment: default to the PowerShell tool for every shell command.** Bash is
fallback-only, for a genuine POSIX/Git-Bash-only need. Decide from the platform, not from how simple
or "plain" a given command looks — one-line cmdlets (`Get-Process`, `.\script.ps1`, anything piped
into `Select-String`/`Where-Object`) still fail when run through Bash: cmdlet resolution, `.`-relative
path syntax, and non-terminating-error-to-exit-code translation all silently break crossing that
boundary, and it's never the same failure mode twice. If a Bash call's result looks even slightly
off, re-run it via the PowerShell tool before trusting it.

## Diagnosing a "missing" or gated tool

Before concluding a tool doesn't exist, theorizing about a stale binary, or building a duplicate:
call `McpServerStatus` and check its `allDeclaredTools` field — a reflection-based, ground-truth list
of every declared tool with `className` and `activeForThisMode`. Mode-gating (the tool's class isn't
in the current session's active list) is far more common than a missing tool, and this answers both
"does it exist" and "is it gated" in one call. Only fall back to a text search (`Search(mode: "text",
query: "Name = \"ToolName\"")`, for a tool declared in an assembly this server flavor doesn't load)
or stale-binary theories once this has come back empty.

## Subagent choice: verification is never a supervisor job

`modeleval-runner` and `planstep-runner` exist only to supervise a run in which **a model is under
test** (a ModelEval test or PlanStepRunner run against an LLM): monitoring it, triaging its logs,
assisting it. Ordinary build/test verification - a post-change `Build`, a `RunTest` over a project or
the full suite, a baseline comparison - is a plain `Build`/`RunTest` call, backgrounded if it must
stay off the main context. Never dispatch a runner agent for it: there is nothing to supervise, and
it costs more than the direct call. (The agents were renamed from `test-runner` /
`planstep-test-runner` because the generic name kept attracting this misuse.)

## Dog-fooding is mandatory — this is an instruction, not background

**All C# reads and writes, and all git operations, go through the RoslynSentinel MCP tools.** This
applies to work on RoslynSentinel's own source, which is nearly every task here — "I'm editing the
server itself" is *not* an exemption, and treating it as one has silently voided this policy in past
sessions.

| Instead of | Use |
| --- | --- |
| `Read` a `.cs` | `ReadFile`, `GetFileOutline`, `GetMethodSource` (methods only — use `ReadFile` for fields/properties/constants) |
| `Grep` / `Glob` for C# symbols | `Search(mode: text/symbol/references/declaration-kind)`, `FindReferences` |
| `Edit` / `Write` a `.cs` | `Member`, `MethodSignature`, `ModifyModifier`, `ReplaceSnippet`, `ApplyDiff`, `RenameSymbol` |
| `Bash(git status/log/diff/add/commit/revert)` | `Git(operation: ...)` |
| `Bash(dotnet build/test)` | `Build`, `RunTest` |

**Common first-try mistakes with the mutating tools:**
- Never pass `filePath` together with `batchEdits` on `ReplaceSnippet` — each batch edit carries its
  own path.
- Order batch edits so definitions (methods, enum members, fields) come before the call sites that
  reference them; the compile gate rejects any intermediate state that doesn't compile.
- Rename a field and all of its usages in the same atomic batch.
- `Member` cannot operate on top-level records — use `ReplaceSnippet` for those.
- For disambiguation context, copy real surrounding lines from a `ReadFile`/`GetMethodSource` result.
  Never invent context lines, and match whitespace/line endings exactly.
- `ModifyEnum`'s `values` parameter is the complete member list, not add-only — omitting a member
  deletes it.

**Scope boundary:** non-C# files — `.md`, `.ps1`, `.json`, `.csproj` — are outside the Roslyn
workspace and have no tool coverage. Use the normal file tools for those directly; that is the edge
of where the tools apply, not a bypass. Likewise git operations the `Git` tool doesn't implement
(branch, push, checkout, worktree, stash) legitimately use the shell — see `docs/current/TODO.md`.

**Why it outranks convenience:** chokepointing every operation is the only way to surface
in-memory-vs-on-disk drift, multi-call sequencing bugs, and edge cases that never appear in an
isolated test. Falling back whenever a tool is awkward hides precisely the failures this exists to
find — and the awkwardness *is itself the finding*, per the failure doctrine below.

**A tool failure is a blocking finding.** If a needed MCP tool fails, returns wrong data, is
unreachable, or has no equivalent operation: finish any in-flight edit, stop advancing the task,
write `docs/current/blockers/blocking_error_<slug>.md` (slug from the real symptom — never a generic
name, since several can be open at once), and end the turn. Do not retry speculatively, do not route
around it with shell tools, and do not resume until told the issue is fixed.

A `PreToolUse` hook (`.claude/hooks/enforce-dogfood.ps1`) blocks the common violations mechanically,
because a rule enforced only by remembering decays across hundreds of calls. The hook is a backstop,
not the policy — it cannot see intent, and reading a `.cs` with `Read` still violates this section
even though nothing stops it. If the hook ever blocks something genuinely necessary, say so and stop;
do not reword the command to slip past it.

**Recovering from an accidental bypass.** If you catch yourself having used `Read`/`Edit`/`Write`/
`Bash` on a `.cs` file, or a shell git command, when an MCP tool should have been used instead: that
is a self-inflicted process violation, not a tool defect. Report it in your response (so the pattern
gets noticed if it recurs) and continue the task — do not write a `docs/current/blockers/` doc and
do not stop the turn. A blocker doc is for the environment failing the agent; this is the reverse.
The one exception is if the bypass itself caused a *new* tool-side symptom you can't explain (e.g. a
mutating tool now fails, or reports state inconsistent with what's on disk) — that residual effect
gets the normal tool-failure treatment above, scoped to the actual anomaly rather than the violation
that triggered it.

**Never delete a tracked file with a shell command.** Deleting a file the server has touched
(created via `CreateFile`, or just loaded into the solution) via `rm`/`Remove-Item` trips the
external-drift detector on the very next mutating call and halts every mutating tool for the rest of
the session with `errorCode: SessionHalted` — read-only tools keep working. Use `DeleteFile` instead,
even for a file you're sure is safe to remove. If a session is already halted this way: call
`ListExternalDiskChanges()` then `AcknowledgeExternalFileChanges()` to clear the latch — no fresh
session needed. This is a self-inflicted bypass per the recovery rule above (report and continue),
not a tool defect.

**Never dispatch parallel subagents that all touch the same shared MCP server process for C#
edits.** Subagents share the parent session's single server process, not a process each. If even one
parallel participant bypasses dogfooding with a plain `Edit`/`Write` on a tracked `.cs` file, the
write lands outside the drift detector's tracked chokepoint and trips the same session-wide
`SessionHalted` latch above — for every other subagent and the parent session too, even callers that
did nothing wrong, and there's no in-band reset short of killing the specific stdio server process by
its full command line. Dispatch subagents for repo-wide mechanical edits **sequentially** instead.

## Failure doctrine: the environment is responsible

This is the governing frame for interpreting **every** model-eval run, PlanStepRunner step, and
task failure in this repo.

The model under test is a **novice**. The environment — server, tools, schemas, tool descriptions,
error messages, prompts, harness, test assertions — is the **expert**. When the novice fails, that
is first and foremost a failure of the expert to guide, constrain, or protect.

This holds *even when the model is plainly wrong*. A model calling a tool with invalid parameters
is not the end of the analysis; it is the beginning. The question is never "was the model wrong?"
(usually yes, and you cannot change the model). The question is:

> **What change to the environment would have prevented this, made it impossible, or made recovery
> immediate?**

Physical-safety analogues for the kind of answer we want:

- A welder wears a helmet — the hazard is not removed, but the operator is protected by default.
- A stove has a "surface hot" light — invisible danger made visible before contact.
- An electrician uses a non-contact voltage detector — a safe way to check, instead of repeatedly
  grabbing the wire to find out.

Environment fixes look like: a clearer tool `[Description]`; a schema that makes the invalid call
unrepresentable; a required parameter instead of an optional one that relocates the failure; an
error message that names the exact parameter and the correct value; a guardrail that halts before
corruption instead of after; a preview/dry-run affordance so the model can check instead of guess.

Blaming the model, or reporting "the model should have known X", is not an actionable finding.

## Root-cause discipline: never stop at the surface

Skimming a transcript and concluding *"the model called ModifyEnum with invalid parameters, the
value wasn't added, it burned 4 turns and eventually succeeded with a different tool"* is an
accurate **restatement of the log**. It is not a root cause, and it will never expose an
environmental defect.

Taking transcripts and tool results at face value is the single most common analysis failure in
this repo. Tool results can be wrong, misleading, or silently truncated; a "success" result does
not prove the write landed, and an error message does not prove its own stated reason is the real
one.

For any failed or inefficient model action, trace to source and gather evidence:

1. **What did the environment actually tell the model?** Read the tool's real `[Description]`,
   parameter `<summary>` docs, and the **schema actually emitted** (not the C# signature — this
   repo has a history of schema-emission bugs where the emitted JSON schema differed from what the
   source implied). Was the model's call reasonable given only what it could see?
2. **Why did the call actually fail?** Read the tool's implementation and find the specific branch
   that produced that error. The message may be inaccurate, generic, or describing a symptom of a
   different underlying fault.
3. **Did the error message enable recovery?** Did it name the offending parameter and a correct
   value, or merely reject? Count the turns to recovery — a slow recovery is an error-message
   defect, not model slowness.
4. **Is the harness or assertion itself at fault?** A test can fail because the assertion is wrong,
   the fixture drifted, the prompt omitted something, or the server binary is stale. Rule these out
   rather than assuming the model-facing path is where the bug lives.
5. **Cite evidence.** Every claimed cause gets a `file:line`, a quoted error string, or a specific
   turn number. A cause you have not traced to source is a hypothesis — label it as one.

Prefer verification over theorising: check the literal error's named identifier against the actual
source before constructing an explanation for it.

## Working conventions

- Build (0 errors) before committing.
- Never leak raw exceptions, stack traces, or internal paths into a tool's `ResultError` — catch at
  the tool boundary and return a structured, actionable error.
- Any unhandled `CS####` surfacing during automated work gets a writeup in
  `docs/current/blockers/` immediately, not deferred.
- `docs/current/TODO.md` is open-items-only; resolved entries move to `docs/current/CLOSED.md`.
- `*/Worktree/` folders under a PlanStepRunner run are harness clones — exclude from diffs,
  searches, and reviews. Never run git commands inside one and treat the output as authoritative.
- Use ASCII-only punctuation in any comment, doc, commit message, or prompt text you write —
  `-`/`--` instead of en/em dashes, straight quotes instead of curly ones, `<=`/`>=` instead of the
  single-character Unicode comparison-operator glyphs. Non-ASCII punctuation is exactly the content that turns into mojibake
  (e.g. `Γçö`) when it crosses an encoding mismatch somewhere in a serialize/deserialize chain
  (file -> server -> MCP tool -> harness -> MCP tool -> server -> file). ASCII bytes are identical
  under every encoding in play, so this class of corruption is structurally impossible for them.
  This does not apply to non-ASCII characters that are the actual subject of a task (e.g. test
  fixture content intentionally containing accented characters).
- In new method signatures, `CancellationToken` is always the last parameter.
- Before hand-rolling a shell loop for a repeated task (e.g. N model-eval runs), check the repo root
  for an existing front-door `.ps1` script first — it likely already handles the failure modes (build
  races against a still-exiting `testhost.exe`, env vars, filter syntax) that a naive loop will hit.
- The repo's top-level solution file is `RoslynSentinel.slnx` (XML format) — there is no root
  `.sln`. Target `RoslynSentinel.slnx` directly for solution-wide `dotnet build`/`dotnet test`.

## Architecture

- **Layering is one-way:** `Common` <- `Engines.*` <- `Tools.*` <- `Server.*`. Each layer has one job:
  - `RoslynSentinel.Engines.Basic` / `.Engines.Advanced` - Roslyn analysis and refactoring logic.
    Keep MCP protocol types (`RequestContext<>`, tool attributes) out of engine code.
  - `RoslynSentinel.Tools.Basic` / `.Tools.Advanced` / `.Tools.Experimental` - the
    `[McpServerToolType]` classes. The `*Impl` classes backing the Basic tools live in `Tools.Basic`.
  - `RoslynSentinel.Server.Basic` / `.Server.Advanced` - hosting, transport, mode resolution and DI
    registration only. No tool or engine logic.
  - `RoslynSentinel.Utilities.PlanStepRunner` - the plan-step harness exe. It references only
    `Common`; the shared agent loop lives in `Common/AgentLoop`.
- **Sibling projects cannot see each other.** `Tools.Advanced` does not reference `Tools.Basic`, and
  `Engines.Basic` cannot reference `Engines.Advanced`. A helper needed on both sides goes one layer
  down: `Engines.Basic` for engine logic, `Common` for result/MCP plumbing. Check each project's
  `.csproj` `<ProjectReference>` list if unsure.
- **Advanced is additive, not a fork.** `Engines.Advanced` builds on `Engines.Basic`, and
  `Server.Advanced` references `Server.Basic` and `Tools.Basic` rather than re-declaring them, so a fix
  in a Basic project is sufficient on its own; there is no duplicate to hunt for in Advanced. Verify
  with `build.ps1 -Flavor Solution` (or both `-Flavor Basic` and `-Flavor Advanced`).
- **Adding a new MCP tool requires three things, not one:**
  1. the `[McpServerToolType]`/`[McpServerTool(Name = ...)]` class, in `Tools.Basic`,
     `Tools.Advanced` or `Tools.Experimental`;
  2. an entry in a mode dictionary in `Server.Basic/ToolClassRegistry.cs` (shared by both servers);
  3. an explicit `if (activeToolClasses.Contains("..."))` DI-registration block in
     `Server.Basic/ServiceRegistrationExtensionsBasic.cs` or
     `Server.Advanced/ServiceRegistrationExtensionsAdvanced.cs`.

  None of these alone makes a tool callable - attributes without a registry entry and DI block
  silently produce a dead tool with no error at startup. A new `Tools.*` project also needs a
  `<ProjectReference>` from the `Server.*` project that hosts it (`Tools.Experimental` is hosted by
  `Server.Advanced` only).
- **The `*Tools` suffix is reserved for classes that declare `[McpServerTool]` methods.** Helpers
  used by tool classes take a different suffix (e.g. `TaskEnabledToolsHelper`), so the name alone
  tells you whether a class exposes tools.

## Commits

- Commit **only** the files changed in the current session — other sessions or in-flight work may
  share this worktree, so leave unrelated dirty or untracked files unstaged. Before committing,
  double-check that no session file was missed, so no amend is needed.
- Stage explicitly via the `Git` tool naming every file (scope `listed`, not a blanket add). Never
  use shell `git add`/`git commit`.
- Every commit message includes the `Co-Authored-By` trailer (see attribution instructions).
- Never count commit hash characters manually — read `CommitHashLength` off the `Git` tool's result.

## Blocker workflow

- Before writing a fix for an open blocker, check `git log` and `docs/current/CLOSED.md` /
  `docs/current/proposals/` for prior attempts — the fix may already exist, or may have been tried
  and reverted.
- When a blocker is fixed: add a regression test, verify against the pre-existing-failure baseline,
  then move its doc from `docs/current/blockers/` to resolved with a resolution note citing the
  commit hash (see also the CS#### rule above and `docs/current/CLOSED.md` conventions).

## Verification

**Changed server source and a tool's live behavior contradicts current source? Stop the server.
That's the whole fix.** VS Code only builds and spawns a fresh server when a session starts — it
never rebuilds a server that's already running, and there is no version banner in tool responses
that flags this for you (`McpServerStatus.buildTimeUtc` reads the on-disk DLL's mtime, not the
loaded assembly's build identity, so it can look fresh while the running process still executes
pre-fix logic). If you edited RoslynSentinel's own source this session, assume the live server is
running the *old* binary. Do this immediately, don't troubleshoot around it:
1. Call `McpServerControl(operation: stop)`. Give it a few real seconds to return before assuming
   it's hung — a clean `Connection closed` is normal, not an error.
2. VS Code relaunches it on a fresh build automatically. Reconnect and re-run `LoadSolution`.
Today this is safe to do any time in your own session: writes go straight to disk, so nothing is
held only in memory. It stops being automatically safe once in-memory-only edits are supported, or
if other subagents are actively mid-operation against the same server — in either case, stopping the
server can lose unwritten work, so check for that before reaching for step 1 reflexively.

**`LoadSolution` does not rebind which binary executes tool logic — only which files it analyzes.**
Pointing a live session's server at a different worktree's `.slnx` (e.g. a PlanStepRunner worktree)
loads that worktree's *files*, but every tool call still runs the *already-running process's*
compiled code — whatever branch it was originally built and launched from. This fails silently: file
paths resolve, every call succeeds, the output just reflects the wrong branch's logic with no error
anywhere. Before trusting a live tool result as evidence about a specific worktree's code, confirm
the connected server was actually built from that worktree (`serverBuildTimeUtc`/DLL mtime vs. `git
log` on the relevant file). If it wasn't and can't be rebuilt/rebound for that worktree from the
current session, prefer `dotnet test` against that worktree directly over live MCP calls.

- If `Build` reports a suspicious warning/error count, force a full rebuild; incremental builds can
  skip recompiles and under-report.
- Reload the workspace (`LoadSolution`) before concluding a change is "missing" — a stale in-memory
  workspace looks identical to a real gap.
- Compare test results against the known pre-existing-failure baseline
  (`docs/current` / memory `reference_known_failing_tests`) and report only *new* failures.
