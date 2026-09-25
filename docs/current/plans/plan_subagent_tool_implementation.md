# Implementation plan: `SubAgentEval` and `SubAgent` MCP tools

Source of truth for every design decision below: [design_subagent_tool.md](../design_subagent_tool.md).
This plan does not re-litigate anything that doc marks as settled; it resolves the doc's remaining
Open Items into concrete choices and lays out a buildable, commit-by-commit sequence.

**Note on citations:** the design doc references four names by memory-slug convention -
`project_dependency_direction`, `project_tools_impl_split_amendment`,
`project_testing_philosophy_and_categories`, `feedback_prefer_mandatory_params_to_close_footgun_roundtrips`.
These are entries in the user's Claude memory index, not files in this repo - there is nothing to open
under `docs/current/` for them. The conventions they describe are independently verifiable in the
actual source (cited below wherever used), so this plan does not depend on the memory files existing,
but a reader should not go looking for them as repo docs.

## Step 0 (decision only): relocation destination

**Decision: `RoslynSentinel.Common`, not a new `RoslynSentinel.AgentLoop` project.**

- `RoslynSentinel.Common.csproj` has no `PackageReference`s beyond `ImplicitUsings`/`Nullable`, but
  already hosts `LmStudioClient.cs` - an `HttpClient`-based streaming LM Studio client, registered via
  `services.AddHttpClient<LmStudioClient>(...)` in `ServiceRegistrationExtensionsAdvanced.cs:78-82`.
  `Common` already carries this shape of code; only `ModelContextProtocol.Client` (for `McpClient`) is
  a genuinely new package reference, and every downstream project already depends on a
  `ModelContextProtocol*` package transitively (`Server.Advanced.csproj` has
  `ModelContextProtocol.AspNetCore`; `Server.Basic.csproj` has `ModelContextProtocol`), so this is a
  low-risk, precedented addition.
- The one-way dependency graph (`Common <- Basic <- Advanced`, `Server.Basic <- Server.Advanced`,
  confirmed by reading every `.csproj`'s `ProjectReference`s) stays exactly as-is: `Server.Advanced`
  already transitively reaches `Common`. A new `RoslynSentinel.AgentLoop` project would need 3 new
  project references (`Server.Advanced`, `Tests.ModelEval`, `Tools.PlanStepRunner`) plus a new `.slnx`
  entry, for no isolation benefit - nothing in the graph would ever need the agent loop without also
  depending on `Common`.
- `GitWorktreeManager`/`IStepBranchStrategy` add zero new dependencies (`System.Diagnostics.Process`/
  `System.Text.RegularExpressions` only), so there's no counter-argument on that side either.

## Step 1: Relocate the agent-loop and worktree code into `RoslynSentinel.Common` (behavior-preserving)

**Files moving from `RoslynSentinel.Tests.ModelEval/AgentLoop/` to `RoslynSentinel.Common/AgentLoop/`**
(new subfolder, namespace `RoslynSentinel.Common.AgentLoop`, to keep the moved code visually distinct
from `Common`'s existing root-level types):

- `ModelAgentRunner.cs` - drop its now-redundant `using RoslynSentinel.Common;` (same-namespace-tree
  post-move); its `LlmOptions.Model` read stays valid unchanged (`LlmOptions` already lives in
  `Common`).
- `LmStudioAgentClient.cs`, including its co-located types in the same file:
  `StreamIdleTimeoutException`, `AgentChatMessage`, `AgentToolCall`, `AgentToolDefinition`.
- `AgentTranscript.cs`, including its co-located types in the same file: `AgentTranscriptTurn`,
  `AgentToolCallRecord`, `AgentStopReason` (enum), `RepeatedFailureDetail` (record), `AgentRunResult`.
- `AgentSystemPrompts.cs`.

**Files moving from `RoslynSentinel.Tools.PlanStepRunner/` to `RoslynSentinel.Common/GitWorktree/`**
(separate sibling folder - worktree management is not agent-loop code and shouldn't be grouped under
that name):

- `GitWorktreeManager.cs`.
- `IStepBranchStrategy.cs`, including its co-located `SharedBranchStrategy` and `StackedBranchStrategy`
  implementations in the same file.

**Files that stay in place** (do not move, despite living under the same `AgentLoop/` folder today):

- `AgentToolErrorAssertions.cs` - an `internal static class`, NUnit-assertion-only, consumed solely by
  ModelEval's own fixture tests. Neither new tool needs it.
- `FlushingFileLoggerProvider.cs` - consumed by both `Tests.ModelEval`'s fixtures and
  `PlanStepRunner/Program.cs`. Generically useful, but neither new tool's contract requires it and the
  design doc's own file list doesn't name it. Leave it in `Tests.ModelEval` for this step; if Step 3's
  launch helper wants flush-per-write logging, reimplement the small provider locally or move it in a
  follow-up - not bundled here, to keep this step's diff reviewable against one concern.
- `PlanStepFile.cs`, `RunnerOptions.cs`, `DotnetProcess.cs`, `Program.cs` stay in
  `RoslynSentinel.Tools.PlanStepRunner` - PlanStepRunner-specific orchestration, not shared engine
  code. `DotnetProcess.BuildAsync`'s *shape* is what Step 3 imitates for the new tools' own build step;
  this plan does not relocate `DotnetProcess` itself, since `Server.Advanced`/`Advanced` cannot
  reference `Tools.PlanStepRunner` (an exe) without inverting the dependency graph, and pulling in its
  PlanStepRunner-flavored doc comments/config assumptions unchanged is not worth it for one method.

**Mechanical reference updates required to keep the solution compiling:**

- `RoslynSentinel.Tests.ModelEval.csproj` already references `RoslynSentinel.Common` - no
  `ProjectReference` change needed, only `namespace`/`using` updates across every fixture file that
  references the moved types (`WholeFileRewriteAgentTests.cs`, `SizeThresholdAgentTests.cs`,
  `RepeatedToolFailureBreakerTests.cs`, `PlanThenExecuteAgentTests.cs`, `PlanOnlyAgentTests.cs`,
  `PlanImplementVerifyAgentTests.cs`, `OrderPricingRefactorChainAgentTests.cs`,
  `OrderPricingRefactorAgentTests.cs`, the assembly setup file, plus `AgentToolErrorAssertions.cs`/
  `FlushingFileLoggerProvider.cs` which stay but reference the moved types). Add
  `<Using Include="RoslynSentinel.Common.AgentLoop" />` (and the `GitWorktree` namespace, if anything
  in this project needs it) to the csproj's global-usings `ItemGroup`, matching how it already
  global-uses `RoslynSentinel.Common`, rather than touching every file's own `using` line individually.
- `RoslynSentinel.Tools.PlanStepRunner.csproj` currently references
  `..\RoslynSentinel.Tests.ModelEval\RoslynSentinel.Tests.ModelEval.csproj` solely to reach
  `ModelAgentRunner`/`LmStudioAgentClient` (`Program.cs`'s only cross-project `using` is
  `RoslynSentinel.Tests.ModelEval.AgentLoop`). Post-move, swap that `ProjectReference` to
  `..\RoslynSentinel.Common\RoslynSentinel.Common.csproj` (verify nothing else in this project still
  needs `Tests.ModelEval` before removing the reference outright - a grep should confirm `Program.cs`
  is the only consumer). Update `Program.cs`'s `using` to `RoslynSentinel.Common.AgentLoop`, and add
  `using RoslynSentinel.Common.GitWorktree;` for the moved `GitWorktreeManager`/`IStepBranchStrategy`
  (previously same-project, unqualified).
- `RoslynSentinel.slnx`: no new entries needed - relocating into an existing project, not creating one.

**Explicitly not changed in this step:** no behavior change to any moved type. `ModelAgentRunner`'s
turn loop, `LmStudioAgentClient`'s SSE parsing/truncation detection, `GitWorktreeManager`'s
worktree/branch/cleanup logic, and both branch strategies move verbatim (namespace line only). The
`LlmOptions.Model` fix is Step 2, kept strictly separate so each step's diff maps to one concern.

**Verification before commit:** full solution build (0 errors), plus a run of
`RoslynSentinel.Tests.ModelEval` and `RoslynSentinel.Tests.PlanStepRunner`'s existing suites to
confirm the move didn't change any consumed behavior - these don't require a live LM Studio instance
to compile and mostly gate on `LlmOptions.Model` being set before doing anything LM-Studio-dependent;
confirm they still skip/no-op cleanly without one, same as before the move.

## Step 2: Fix the `LlmOptions.Model` static-vs-per-call mismatch

**Problem:** `LmStudioAgentClient`'s constructor reads `_model = LlmOptions.Model ?? throw ...` once,
at construction - a process-wide static set once at server startup, incompatible with either new
tool's per-call `model` parameter.

**Second call site found during verification, out of scope for this fix:**
`RoslynSentinel.Common/LmStudioClient.cs` does the identical `LlmOptions.Model ?? throw ...` read, and
is DI-registered as a long-lived singleton (`AddHttpClient<LmStudioClient>`,
`ServiceRegistrationExtensionsAdvanced.cs:78-83`) backing `CommentingEngine` via `ILlmClient`. This fix
must **not** touch `LmStudioClient` - nothing in this design touches `CommentingEngine`, and it has no
per-call model requirement. Flagged only so the fix isn't over-applied.

**Fix shape:** thread the model name through `LmStudioAgentClient`'s constructor as an explicit
required parameter, replacing the static read:

```csharp
// Before:
public LmStudioAgentClient(HttpClient httpClient, ILogger<LmStudioAgentClient> logger)
{
    _httpClient = httpClient;
    _logger = logger;
    _model = LlmOptions.Model ?? throw new InvalidOperationException(...);
    ...
}

// After:
public LmStudioAgentClient(HttpClient httpClient, string model, ILogger<LmStudioAgentClient> logger)
{
    _httpClient = httpClient;
    _logger = logger;
    _model = string.IsNullOrWhiteSpace(model)
        ? throw new ArgumentException("model must be a non-empty LM Studio model name.", nameof(model))
        : model;
    ...
}
```

`ModelAgentRunner`'s own separate read of `LlmOptions.Model` (a token-usage warning heuristic, not a
correctness path) is unaffected by this change; leave a one-line comment there noting the same
multi-model-per-process caveat now applies to that heuristic too.

**Every existing call site that needs updating (mechanical, same value, now passed explicitly):**

- `RoslynSentinel.Tools.PlanStepRunner/Program.cs`'s `new LmStudioAgentClient(httpClient,
  loggerFactory.CreateLogger<LmStudioAgentClient>())` becomes `new LmStudioAgentClient(httpClient,
  LlmOptions.Model!, loggerFactory.CreateLogger<...>())` - PlanStepRunner already validates
  `LlmOptions.Model` is non-null before this point, so `!` is safe.
- Every ModelEval fixture that directly constructs `LmStudioAgentClient` (the same fixture file list
  from Step 1) - each becomes `new LmStudioAgentClient(httpClient, LlmOptions.Model!, ...)`, since
  these fixtures already gate on `LlmOptions.Model` being set. No behavior change, just an explicit
  pass-through of the same value.

**Interaction with the design doc's decision 7 (nested call inherits the parent's model):** this fix
*is* the mechanism decision 7 needs. Once `LmStudioAgentClient`'s model is an explicit constructor
parameter, the nested child-server launch (Step 3 / `SubAgentImpl`, Step 5) constructs its own
`LmStudioAgentClient` inside the nested worktree's own freshly-spawned process - and that process's
`LlmOptions.Model` is set via its own `--llm-model=<inherited-model>` CLI arg at launch (a brand-new
process calls `LlmOptions.Configure(args)` fresh at its own startup, confirmed unconditional in both
`ServerStdio.cs` and `ServerHttp.cs`). The top-level `SubAgent` call's own `model` parameter is passed
as one of the launch arguments to the nested child server it spawns, the same way
`PlanStepRunner/Program.cs` already passes other explicit CLI args to its own spawned child. The
nested model is inherited via process-launch-argument propagation, not in-process state sharing -
which also sidesteps the original concurrency hazard: two calls in flight in the *parent* process
never share an `LmStudioAgentClient`, because each tool call constructs its own scoped `HttpClient` +
`LmStudioAgentClient` (Step 3/5).

## Step 3: Build the shared child-server launch/teardown helper

**Decision: one shared class, not duplicated code**, since `SubAgentEvalImpl` and `SubAgentImpl` need
the identical sequence (build the worktree's `Server.Advanced.csproj`, launch `StdioClientTransport`,
explicit `LoadSolution`, run `ModelAgentRunner`, dispose + cleanup).

**Recursion-guard scope correction:** the design doc's decision 6 text states "a model running inside
a `SubAgent` (or `SubAgentEval`) call must not see `SubAgent`/`SubAgentEval`," but its migration-step
wording only mentions `SubAgent`'s own launch args carrying the exclusion flag. Read literally that
would leave a back door: a `SubAgentEval` call's dispatched model could still call `SubAgent`, making
the real depth cap 2 rather than 1 through `SubAgentEval` specifically. **Resolution: the
`--exclude-tools=SubAgentTools,SubAgentEvalTools` flag is applied unconditionally by the shared
helper, on every child-server launch it performs, regardless of which tool spawned it.** This also
means the design doc's separate Open Item ("nested call's `model` parameter: schema omission vs.
server-side validation") is moot - once the nested child server never exposes either tool class at
all, there is no scenario where a nested caller can even construct a `SubAgent`/`SubAgentEval` call to
misuse a `model` parameter on. Tool-class exclusion alone satisfies decision 7's intent as a side
effect; no separate model-rejection mechanism is implemented.

Given the above, duplicating the launch sequence buys nothing. New file,
`RoslynSentinel.Advanced/SubAgentChildServerLauncher.cs` (Advanced-only orchestration - lives next to
its two callers, not in `Common`, since it is not shared engine code):

```csharp
internal sealed class SubAgentChildServerLauncher
{
    // Returns a live McpClient plus an IAsyncDisposable teardown that kills the child process and
    // removes the worktree, mirroring GitWorktreeManager.TryRemoveWorktree's tolerant-of-failure
    // cleanup (logs, does not throw, on a Windows path-length removal failure).
    public async Task<SubAgentChildServer> LaunchAsync(
        string sourceRepo, string runDir, string branchName, string model,
        CancellationToken cancellationToken);
}

internal sealed class SubAgentChildServer : IAsyncDisposable
{
    public McpClient McpClient { get; }
    public string WorktreePath { get; }
    public GitWorktreeManager GitWorktreeManager { get; }
    // DisposeAsync: disposes McpClient first (kills the child process - StdioClientTransport owns
    // this per the same reasoning PlanStepRunner/Program.cs's own comment gives for stdio over Http),
    // then calls TryRemoveWorktree, logging rather than throwing on cleanup failure.
}
```

Internals, modeled directly on `PlanStepRunner/Program.cs`'s existing sequence but trimmed to what
these two tools need (no `RunnerOptions`, no step-file concept, no `BranchMode` CLI parsing - those
are PlanStepRunner-specific):

1. Build via a small local equivalent of `DotnetProcess.BuildAsync` - `dotnet build
   <worktree>\RoslynSentinel.Server.Advanced\RoslynSentinel.Server.Advanced.csproj -c Debug -o
   <isolated-output-dir> --nologo -v quiet`. Throws with stdout/stderr inlined on nonzero exit,
   matching `DotnetProcess.BuildAsync`'s own behavior; the caller (`SubAgentEvalImpl`/`SubAgentImpl`)
   catches this and converts it to a structured `ResultError` (Step 5's error-boundary note).
2. Launch via `StdioClientTransport`: `Command = <built-exe-path>`, `Arguments = ["--base-repo-dir=" +
   worktreePath, "--include-tools=Claude", "--exclude-tools=SubAgentTools,SubAgentEvalTools",
   "--testing", "--llm-model=" + model, "--log-dir=" + logDir, "--run-id=" + runId], WorkingDirectory
   = worktreePath` - mirroring `Program.cs`'s existing argument shape, with `--llm-model` added (Step
   2's fix) and `--exclude-tools` added (decision 6, applied unconditionally per above). `--testing`
   is kept for the same basename-collision reason `Program.cs` already documents (the worktree mirrors
   the production repo's own plan/doc basenames). `--include-tools=Claude` is this plan's own default
   tool surface for the dispatched model (the existing `"Claude"` mode alias present in both
   `ToolClassRegistry.BasicModeToToolClasses` and `AdvancedModeToToolClasses`) - not specified by the
   design doc, chosen here as a reasonable starting surface matching what Claude itself typically
   drives with; could become its own parameter later if a call site needs something narrower or wider.
3. `await McpClient.CreateAsync(transport, ...)`, then an explicit `LoadSolution` call (never relying
   on `--solution` auto-load, which is fire-and-forget and unreliable to await).
4. Caller constructs `LmStudioAgentClient`/`ModelAgentRunner` itself using the now-explicit-model
   constructor from Step 2, and calls `RunAsync`.
5. `DisposeAsync`: dispose `McpClient` first, then `TryRemoveWorktree`.

**Worktree/branch naming scheme (Open Item, resolved here):** `runDir =
Path.Combine(sourceRepo, ".roslynsentinel", "subagent-runs",
$"{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}-{Guid.NewGuid():N}"[..40])` - timestamp matching PlanStepRunner's
own `RunId` convention, plus a GUID suffix (PlanStepRunner doesn't need one since each invocation is a
distinct human-launched process; a live server handling concurrent tool calls within the same second
needs the extra entropy). Branch name: `subagent/{runDirLeaf}` via `SharedBranchStrategy(branch,
"HEAD")` - a single-shot call never needs `StackedBranchStrategy`'s per-step stacking. A nested
`SubAgent` call mints its own fresh `runDir` the same way at the point it's dispatched, automatically
distinct from its parent's by GUID alone - no explicit parent-run-id needs threading through.

## Step 4: `SubAgentEvalTools`/`SubAgentEvalImpl`

Follows the `WorkspaceBuildTestTools`/`WorkspaceBuildTestImpl` pattern (`Server.Basic`'s thin `*Tools`
class constructs `_impl` in its own constructor; every `[McpServerTool]` method is a one-line
delegation). This is the first realized `Server.Advanced`/`Advanced`-namespace instance of that split
- existing Advanced tool classes (`AsyncifyTools.cs`, `ScanTools.cs`, etc.) are still monolithic, but
the split convention itself is namespace-agnostic.

`RoslynSentinel.Server.Advanced/SubAgentEvalTools.cs`:

```csharp
[McpServerToolType]
public class SubAgentEvalTools
{
    private readonly SubAgentEvalImpl _impl;

    public SubAgentEvalTools(IWorkspaceManager workspaceManager, ILogger logger)
    {
        _impl = new SubAgentEvalImpl(workspaceManager, logger);
    }

    [McpServerTool(Name = "SubAgentEval")]
    [Produces(DataTag.Report)]
    [Description("Runs a prompt against a real LM Studio model in a fresh, worktree-isolated copy " +
        "of the repo, then builds and tests the result. Returns a structured pass/fail report - " +
        "build/test outcome, turn count, stop reason, files touched, and a failure excerpt when the " +
        "run didn't converge cleanly. Use this for an ad-hoc micro-eval question (\"can this model " +
        "do X\"), not for a scoped multi-step plan (use PlanStepRunner for that).")]
    public Task<SentinelCallToolResult<SubAgentEvalResult, ResultError>> SubAgentEval(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [Description("Full task text for the dispatched model, inlined by value - never a file path.")]
        string prompt,
        [Description("An already-loaded LM Studio model name, same as --llm-model/ROSLYNSENTINEL_LLM_MODEL.")]
        string model,
        [Description("Required. Per-turn output token ceiling sent to LM Studio. No default - size it for the prompt's expected reasoning load.")]
        int maxTokensPerTurn,
        string? baseBranch = null,
        int? turnCap = null,
        int? wallClockCapMinutes = null,
        CancellationToken cancellationToken = default)
        => _impl.SubAgentEval(reason, prompt, model, maxTokensPerTurn, baseBranch, turnCap, wallClockCapMinutes, cancellationToken);
}
```

**Parameter ordering note:** the design doc's own contract listing interleaves required and optional
parameters (`prompt, model, baseBranch?, turnCap?, maxTokensPerTurn, wallClockCapMinutes?`). In C#,
required parameters cannot follow optional ones positionally unless every call names its arguments -
which MCP tool calls always do (arguments arrive as a JSON object, not positionally), so this is a
compilability constraint, not a contract change. This plan declares all required parameters first
(`reason, prompt, model, maxTokensPerTurn`), then all optional ones - the *set* of required vs.
optional parameters is unchanged from the design doc, only their declaration order.

**Structured result type** (new code, `RoslynSentinel.Common/SubAgentEvalResult.cs` - a plain data
record with no Advanced-only dependency, consistent with where `SummarizeListResult.cs`'s own
`ListSummary`/`FileHitCount` types live):

```csharp
public sealed record SubAgentEvalResult
{
    public required bool Converged { get; init; }          // AgentRunResult.Converged
    public required string StopReason { get; init; }       // AgentStopReason, stringified
    public required int TurnCount { get; init; }
    public required bool BuildSucceeded { get; init; }
    public required int BuildErrorCount { get; init; }
    public required int TestPassedCount { get; init; }
    public required int TestFailedCount { get; init; }
    public required IReadOnlyList<string> TouchedPaths { get; init; }   // GitWorktreeManager.GetDirtyPaths
    public string? FailureExcerpt { get; init; }             // last turn's model content, when not Converged
    public required string TranscriptPath { get; init; }     // AgentRunResult.TranscriptPath, for follow-up read
    public string? RepeatedFailureSignature { get; init; }   // RepeatedFailureDetail.Signature, when set
}
```

This is this plan's concretization of the design doc's "exact structured-result field list" Open
Item, built from the fields the design doc names as clearly mattering: build/test outcome, turn count,
stop reason, files-touched, failure excerpt, transcript path. Per the design doc, `Build`/`RunTest`'s
own outcome shapes should be reused "where practical" - this plan keeps `SubAgentEvalResult` itself
flat rather than nesting their full result types wholesale, since only summary counts are needed here
(their full capped-list/summary payloads stay discoverable via the transcript if ever needed). Build
this via a `SubAgentEvalResult.Build`-style static helper (imitating `SummarizeListResult.Build`'s
shape) that takes the `AgentRunResult`, the child server's `Build`/`RunTest` results, and
`GetDirtyPaths`' output, and assembles the record - new code, not a port, exactly as the design doc
states.

**DI wiring**, following `ServiceRegistrationExtensionsAdvanced.AddRoslynSentinelToolsAdvanced` as the
exact template:

1. Add `["SubAgentEval"] = ["SubAgentEvalTools"]` to `ToolClassRegistry.AdvancedModeToToolClasses` - a
   new dedicated mode key, since `SubAgentEval`/`SubAgent` don't naturally belong under any existing
   mode (`Intelligence`, `Modernize`, `Quality`, `Generation`, `Asyncify` are unrelated concerns) and
   forcing them under one would make them activate/deactivate for reasons unrelated to their own
   purpose.
2. Add a block mirroring the existing per-tool-class registration pattern:
   ```csharp
   if (activeToolClasses.Contains("SubAgentEvalTools"))
   {
       services.AddSingleton<SubAgentEvalTools>();
       mcpBuilder.WithSentinelTools<SubAgentEvalTools>();
   }
   ```
3. `SubAgentEvalTools`'s constructor needs whatever `SubAgentEvalImpl` needs resolved from DI - at
   minimum `IWorkspaceManager` (for `sourceRepo`, i.e. the currently-loaded solution's root, needed to
   know where to create the worktree) and `ILogger`. It does not need any of the heavy Advanced
   engines - it delegates all actual work to a spawned child server process, not in-process engines.

## Step 5: `SubAgentTools`/`SubAgentImpl`

Same DI/class-split pattern as Step 4. Key differences from `SubAgentEval`:

**`responseFormat` parameter shape (Open Item, resolved here):** a small enum, not an inline JSON
Schema string. An inline-schema option adds real complexity (validating arbitrary caller-supplied
schemas, deciding what happens on mismatch) that isn't warranted for a v1 already scoped down hard (no
hosted models, no comparison mode). Default:

```csharp
public enum SubAgentResponseFormat { Text, Json }
```

`Text` (default/omittable) returns the model's final turn content verbatim. `Json` appends an
instruction to the prompt asking the model to respond with a single JSON value as its final message,
and passes that content through unchanged - no server-side schema validation of the model's JSON, just
a best-effort prompt instruction. This keeps `SubAgent` a thin dispatch tool rather than growing its
own structured-output enforcement layer, consistent with the design doc's "unopinionated... just
prompt in, formatted output out" framing.

`RoslynSentinel.Server.Advanced/SubAgentTools.cs`:

```csharp
[McpServerTool(Name = "SubAgent")]
[Produces(DataTag.Report)]
[Description("Runs a prompt against a real LM Studio model in a fresh, worktree-isolated copy of " +
    "the repo and returns its output. General-purpose sub-dispatch - no build/test opinion. Nestable " +
    "one level deep: a model already running inside a SubAgent/SubAgentEval call can call this too, " +
    "but its dispatched model targets the same model as the top-level call (model selection is not " +
    "delegable) and cannot call SubAgent/SubAgentEval itself.")]
public Task<SentinelCallToolResult<object, ResultError>> SubAgent(
    [Description(ToolParams.Reason)] ToolCallReason reason,
    [Description("Full task text for the dispatched model, inlined by value - never a file path.")]
    string prompt,
    [Description("An already-loaded LM Studio model name. Ignored when this call itself runs nested inside another SubAgent/SubAgentEval - the nested child server never exposes this tool's caller-visible variant at all.")]
    string model,
    [Description("Required. Per-turn output token ceiling sent to LM Studio. No default.")]
    int maxTokensPerTurn,
    int? turnCap = null,
    int? wallClockCapMinutes = null,
    SubAgentResponseFormat responseFormat = SubAgentResponseFormat.Text,
    CancellationToken cancellationToken = default)
    => _impl.SubAgent(reason, prompt, model, maxTokensPerTurn, turnCap, wallClockCapMinutes, responseFormat, cancellationToken);
```

(`responseFormat` legitimately keeps a C# default of `Text` - it's genuinely optional per the design
doc's own framing, unlike `maxTokensPerTurn`, which the design doc explicitly forbids defaulting.)

**Large-result offload:** per the design doc's decision 8, `SubAgent`'s result goes through the same
generic path every tool result already uses - `LargeResultHelper.OffloadThresholdBytes` (30 KiB) and
its store-and-return-a-pointer mechanism apply automatically to any `SentinelCallToolResult<T>`
response exceeding the threshold. **No new offload code is needed.** This is a deliberate difference
from `SubAgentEvalImpl`, whose result (`SubAgentEvalResult`) is already small/structured by design
(the transcript stays on disk; only a path is returned).

**DI wiring:** identical pattern to Step 4 - add `["SubAgent"] = ["SubAgentTools"]` to
`AdvancedModeToToolClasses`, add the matching `if (activeToolClasses.Contains("SubAgentTools")) { ...
}` registration block.

## Step 6: Confirm `maxTokensPerTurn` follows the required-parameter convention

Already reflected in Steps 4/5's signatures above; stated as its own checkpoint since the design doc
treats this as correctness-critical, not a style preference. Confirmed pattern from two real examples
in this codebase: a parameter declared with no `=` in its C# signature emits as *required* in the MCP
schema (e.g. `ReadFile`'s `filePath`, `WorkspaceBuildTestTools`'s `reason`); any `=` makes it optional
there too (e.g. `GetDiagnostics`'s `summarize`/`maxDetails`/`topN`). Both new tools' `maxTokensPerTurn`
has no `=` in either signature above, satisfying this. Re-check at code-review time before merging:
grep the final `SubAgentEvalTools.cs`/`SubAgentTools.cs` for `maxTokensPerTurn` and confirm no `=`
follows it anywhere in either method signature.

## Step 7: Testing

**Unit/integration coverage:** this repo has no existing "spin up a real child MCP server process and
assert on the result" test pattern - `Tests.Advanced`'s `McpTasksHarness*` tests use an in-process
`McpClient` against the same test host, not a spawned child process. New coverage, in a new
`RoslynSentinel.Tests.SubAgent` project (kept separate from the already-large `Tests.Advanced` suite,
since neither new tool needs anything else that project sets up):

- `SubAgentEvalResult`'s builder helper, against synthetic `AgentRunResult`/`Build`/`RunTest` inputs -
  pure unit test, no process spawn.
- The worktree/branch-naming helper's collision-avoidance property - two calls in quick succession
  produce distinct `runDir`s.
- DI registration itself - mirroring the existing DEBUG-only constructor-resolution smoke check
  pattern (`ServerStartupHelpers`'s tool-type resolution check) to confirm `SubAgentEvalTools`/
  `SubAgentTools` construct cleanly given real DI.

The child-server-launch helper (Step 3) and both `*Impl` classes' full end-to-end path (real worktree,
real spawned child process, real `LoadSolution`) are not practical to cover with a fast, hermetic unit
test - spawning a real `dotnet build` plus a child process per test run is exactly the kind of slow,
environment-dependent test that stops resembling the real task once narrowed enough to run fast. This
plan follows the same PlanStepRunner-style exploratory-validation posture the design doc's own
"Relationship to other in-flight proposals" section points to, not commercial-QA exhaustive coverage.

**Required before considering this mergeable: at least one real end-to-end smoke run against an actual
LM Studio model, for each tool:**

- `SubAgentEval`: one live call with a real loaded model and a small, cheap, verifiable prompt (e.g.
  "add a trivial doc comment to method X"), confirming the full pipeline - worktree creation, child
  server launch, `LoadSolution`, model turn(s), `Build`/`RunTest` snapshot, worktree teardown,
  `SubAgentEvalResult` shape - works end-to-end and the worktree is actually removed afterward.
- `SubAgent`: one live top-level call confirming plain dispatch, plus, if feasible in the same
  session, one live *nested* call (a top-level `SubAgent` prompt that itself asks the dispatched model
  to call `SubAgent` again) to confirm the `--exclude-tools` flag actually removes the tool from the
  nested model's `ListToolsAsync` result in practice, not just in the launch-argument code. This is
  the single highest-value smoke check in this whole plan - a silent failure here (the nested child
  still exposing `SubAgentTools`) is exactly the class of environment defect worth catching before a
  weak model ever exploits it.

Neither smoke run needs to be automated/repeatable CI coverage - a manually-run, transcript-logged
session is sufficient, consistent with how PlanStepRunner itself was validated.

## Step 8: Build-before-commit and doc bookkeeping

- Every step above ends with a full solution build to 0 errors before its own commit.
- Any unhandled `CS####` surfacing during implementation gets an immediate
  `docs/current/blockers/` writeup, not deferred.
- This plan's own tracking entry lives in `docs/current/TODO.md` under a heading naming these 8 steps
  as sub-items, checked off in place as each completes. `TODO.md` is open-items-only - the whole entry
  moves to `CLOSED.md` only once all 8 steps (including both live smoke runs from Step 7) are done,
  never deleted outright.
- `design_subagent_tool.md`'s own "Status" section should be updated once the TODO entry exists, so
  the design doc and TODO stay in sync rather than one silently going stale.

## Critical files for implementation

- `RoslynSentinel.Tests.ModelEval/AgentLoop/ModelAgentRunner.cs` - the engine both tools reuse as-is;
  relocation target, Step 1.
- `RoslynSentinel.Tests.ModelEval/AgentLoop/LmStudioAgentClient.cs` - constructor is the exact site of
  Step 2's required fix.
- `RoslynSentinel.Tools.PlanStepRunner/Program.cs` - the worked template Step 3's child-server launch
  helper imitates almost line-for-line.
- `RoslynSentinel.Tools.PlanStepRunner/GitWorktreeManager.cs` and `IStepBranchStrategy.cs` -
  relocation targets; `GetDirtyPaths`/`TryRemoveWorktree` are what Steps 3-4 build the result/teardown
  logic on top of.
- `RoslynSentinel.Server.Advanced/ServiceRegistrationExtensionsAdvanced.cs` - exact DI-registration
  template both Steps 4 and 5 extend (`AddRoslynSentinelToolsAdvanced`).
- `RoslynSentinel.Server.Basic/ToolClassRegistry.cs` - where the new `SubAgentEval`/`SubAgent` mode
  keys get added (`AdvancedModeToToolClasses`).
- `RoslynSentinel.Server.Basic/WorkspaceBuildTestTools.cs` (+
  `RoslynSentinel.Basic/WorkspaceBuildTestImpl.cs`) - the concrete `*Tools`/`*Impl` split pattern both
  new tool pairs follow.

## Status

Plan drafted 2026-09-24, not yet implemented, pending user review. Tracking entry to be added to
`docs/current/TODO.md`.
