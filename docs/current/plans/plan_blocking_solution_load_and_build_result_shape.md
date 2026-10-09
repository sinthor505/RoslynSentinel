# Plan: Make tool calls wait for the solution load (30 s), and give Build a small, root-cause-first result

**Status:** DRAFT 2026-10-09. Not approved; nothing is built. Addresses decisions #8 (solution auto-load) and #14 (Build result shape) in `docs/current/proposals/proposal_journal_digest_followup_decisions.md`, and supersedes `docs/current/findings/finding_build_offload_hides_error_count.md` (journal sessions `cc715aa0` 17:45 and 17:59, `93fd2d31`).

## Problem

### Part A - a call that arrives mid-load fails or hangs instead of waiting

Evidence, all traced to source this session:

- `WarmupAndAutoLoadBasic` (`RoslynSentinel.Server.Basic/ServiceRegistrationExtensionsBasic.cs:375`) fires `_ = workspaceManager.LoadSolutionAsync(solutionPath).ContinueWith(..., OnlyOnFaulted)`; `WarmupAndAutoLoadAdvanced` (`RoslynSentinel.Server.Advanced/ServiceRegistrationExtensionsAdvanced.cs:258`) does the same. Both run before `host.RunAsync` (for example `Server.Advanced/ServerStdio.cs:134`), so `_solutionLock` is already held when the first request arrives.
- Premise correction: the owner note says tool calls get "load in progress, retry". That is only true on the **lock-free** paths. `GetCurrentSolutionAsync` (`RoslynSentinel.Common/PersistentWorkspaceManager.cs:1109-1120`) does `await _solutionLock.WaitAsync(...)` with no timeout, so most tools already queue silently behind the load (and can hit the client's own timeout with no explanation). The "still in progress ... Retry" text (`RoslynSentinel.Common/SolutionLoadState.cs`, `SolutionNotLoadedMessage.Build`) is reached only by code that reads `CurrentSolution`/`LoadState` without the lock: `Engines.Basic/BuildEngine.cs:139`, `MsToolAugmentEngine.cs:897`, `Tools.Advanced/SubAgentImpl.cs:55` and `SubAgentEvalImpl.cs:29`, `Tools.Basic/WorkspaceReadNavigationImpl.cs:1019`, `WorkspaceFileEditImpl.cs:203,210,543,750,917`, `WholeFileWriteTools.cs:470,621`, `WorkspaceHealthMiscImpl.cs:99`, `Tools.Experimental/CodeTransformTools.cs:947`, and `ResolveFromWire` (`PersistentWorkspaceManager.cs` ~2051-2072, `NoSolutionLoaded`).
- So the real defects are: (1) lock-free paths fail with "retry" instead of waiting; (2) lock-taking paths wait with no bound and no explanation; (3) the manual `LoadSolution` tool (`Tools.Basic/WorkspaceProjectManagementImpl.cs:320`) has no timeout, and because `IsAlreadyLoadedPath` (`:279-288`) is false while the auto-load is still running, a manual `LoadSolution` issued mid-load queues on the lock and then **reloads the same solution a second time**.
- `McpServerStatus` (`Tools.Basic/ServerStatusTools.cs`, `McpServerStatusResult` at line 196) exposes `IsFreshStartup` but not whether a load is running, although `SolutionLoadState.LoadInProgress` already exists (`PersistentWorkspaceManager.cs:153`, backed by `_loadsInProgress` at `:150`).
- The lock cannot carry the timeout itself: writers hold `_solutionLock` too (lock users at `:804, 1093, 1357-1708, 1881`), so a timeout on the lock would also time out ordinary writes. A dedicated load gate is needed.

### Part B - Build returns a payload the agent cannot use inline

Measured read-only over the 94 `BuildResult` files in `.roslynsentinel/largeresults/` (all `fullBuild`):

| Quantity | Value |
| --- | --- |
| Files under the 15 KB offload threshold | 0 of 94 (min 19.6 KB, median 41 KB, max 135 KB) |
| Failed builds in the sample | 14 (max 122 errors, 67 KB payload) |
| Max warnings in one result | 550 |
| `StdoutTail` median | about 16.6 K characters, the biggest single field even on a green build |
| `ProjectsCompiled` median | 19 names |

So every `fullBuild` result is offloaded and the agent gets only a pointer; the journal entries above show it then reads the file with a shell command. Cause traced: `WorkspaceBuildTestImpl.Build` (`Tools.Basic/WorkspaceBuildTestImpl.cs:172-225`) passes the whole `BuildResult` (`Common/BuildResult.cs`) to `ForPossiblyLargeDataAsync`; `RunFullBuildAsync` always fills `StdoutTail` (40 lines) and up to `maxDetails` (default 50) errors and 50 warnings (`Engines.Basic/BuildEngine.cs:269-299`).

Further defects found while tracing:

- `DiagnosticLineRegex` (`BuildEngine.cs:128-130`) matches `path(line,col): error ID: msg [project]` but discards the `[project]` suffix, and `DiagnosticInfo` (`Common/DiagnosticReport.cs:44`) has no project field. The agent therefore cannot be told which project an error belongs to.
- The same regex requires `(line,col)`, so project-level errors of the form `X.csproj : error NU1101: ... [X.csproj]` (restore/SDK errors) are not parsed at all. A failed build can then report `ErrorCount: 0` with `Outcome: Failed` and show only a stdout tail. Hypothesis: standard MSBuild output; Step B4 pins it with canned lines.
- quickBuild at solution scope (`BuildEngine.RunQuickBuildAsync`, `:40-126`, via `DiagnosticEngine.GetSolutionDiagnosticsAsync`, `Engines.Basic/DiagnosticEngine.cs`) compiles every project and reports downstream-project errors that are consequences of an upstream project that does not compile. Those are the "thousands of downstream test-project errors" the owner describes. The detail list is also capped *before* `BuildResult` is built, so the full list cannot be stored later.
- Premise correction for the owner's "re-run project by project" idea: for `dotnet build <sln>` MSBuild builds a project's references first and does not compile a project whose reference failed, so a full build probably already reports only the root layer. Unverified; Step B1 verifies it with a fixture before any dependency-order machinery is built. The cascade problem is mainly a quickBuild/Roslyn phenomenon, where classification from the project graph is cheap.

### What an agent needs from Build (answers the proposal's open question)

| Errors | Situation | What the response must contain |
| --- | --- | --- |
| 0 | green | outcome, projects built, warning count, duration. No stdout, no warning list, no errors list. |
| 1 | one project, one file | that error in full: id, project, `file:line:col`, message. |
| 10 | a few files | all 10 details, grouped by project (about 3 KB). |
| 100 | several files, maybe two projects | counts by project and by file (top 10) and by code (top 10); the first `maxDetails` (default 20) details, root-cause project first; `OmittedErrorCount`; a `FullDiagnosticsResultId` for the rest. |
| 1000 | usually one broken upstream project | same as 100, plus an explicit `SuppressedDownstreamErrorCount` and the root-cause project names, so the agent fixes the upstream project and rebuilds instead of paging. |

Stdout is not needed when the build parsed correctly. It is the only evidence when a failed build parsed zero errors, so it is returned in that case and when `includeOutput: true`.

## Decision

### Part A

1. Add a **load gate** to `PersistentWorkspaceManager`: a `TaskCompletionSource` created inside `LoadSolutionAsync` right after `_solutionLock` is acquired and completed in its `finally` before the lock is released. `WaitForLoadAsync(TimeSpan timeout, CancellationToken)` awaits the gate with `Task.WaitAsync(timeout, ct)` and returns `NotLoading`, `Completed` or `TimedOut`. A timeout cancels the **wait only**, never the load.
2. **Where the wait lives: a request filter**, not `GetCurrentSolutionAsync`. A filter covers the lock-free paths (the ones that actually say "retry") and the lock-taking paths in one place, adds one clear timeout message, and needs no change in the 389 `GetCurrentSolutionAsync` call sites. Only a small exempt set of tools skips it (`LoadSolution`, `McpServerStatus`, `McpServerControl`, `McpToolsetControl`, `IsSessionHalted`, `GetLargeResult`, `Git`). The filter is registered last, so it is the innermost filter and a breaker refusal still returns immediately.
3. **On timeout** the filter returns `IsError: true` with a message that says the load is still running and was not cancelled, that this call did not run, and to retry or watch `McpServerStatus.loadInProgress`.
4. **Manual `LoadSolution`** shares the same 30 s timeout: it first waits for any in-progress load, re-checks "already loaded" (this removes the double load), and otherwise starts its own load and waits for it with the same timeout. Its load runs with `CancellationToken.None` so a timeout or client cancel does not kill a half-finished load; the same timeout message is returned.
5. `McpServerStatus` gains `loadInProgress`.
6. **Launcher heuristic** (`scripts/roslynsentinel-mcp-launch.ps1:315-333`): keep as is (see Risks, option table). No launcher change in this plan.

### Part B

1. Keep `BuildResult` as the tool's `SuccessData` type and its existing JSON names (`outcome`, `errorCount`, `errors`, ...). `SubAgentEvalResult.Build` (`Common/SubAgentEvalResult.cs:69-73`) and `PlanStepRunner/Program.cs:273` read `outcome`/`errorCount`; six tests in `Tests.Tools.Basic/BatteryTwentyTests.cs` (lines 987, 1000, 1020, 1071, 1084, 1107) cast `SuccessData` to `BuildResult`. A new response type would break all of them for no gain.
2. Add trailing optional members to `BuildResult`: `Breakdown` (counts by project, file, with a root-cause flag per project), `OmittedErrorCount`, `SuppressedDownstreamErrorCount`, `FullDiagnosticsResultId`, and a `[JsonIgnore]` `FullDiagnostics` carrier so the engine can hand the uncapped lists to the tool boundary without changing what other callers (`GetDiagnostics`, `GetWorkspaceHealth`) serialise.
3. The engines produce the full picture (project on every diagnostic, uncapped lists); a pure projector in `Common` shrinks it for the wire at the tool boundary:
   - green: `Outcome`, `ProjectsCompiled`, `WarningCount`, `Duration`; no lists, no tails, no summaries.
   - failed: `Breakdown`, `ErrorSummary` (top 10), the first `maxDetails` errors (root-cause projects first), `OmittedErrorCount`, `SuppressedDownstreamErrorCount`; tails only when nothing parsed or `includeOutput: true`; warnings only when `includeWarnings: true`.
   - messages are cut to 300 characters so the typed payload stays under the 15 KB offload threshold (20 details at about 300 B each is about 6 KB).
4. **Dependency order** is by classification, not by re-running. A project with errors is a root cause when none of the projects it transitively depends on has errors; otherwise its errors are downstream. The map comes from Roslyn's `ProjectDependencyGraph` (`GetProjectsThatThisProjectTransitivelyDependsOn`). It works for both levels, costs nothing extra, and keeps counts exact. The literal "re-run project by project in dependency order" is Option 2 in Risks (costly; needed only if Step B1 shows MSBuild cascades).
5. **Offload rule:** the full lists are stored with `LargeResultHelper.StoreRawJsonAsync(json, solutionRoot, ct)` (`Common/LargeResultHelper.cs:80`; precedent `ValidateAndApplyHelper.StoreChangedContentAsync`, `Common/ValidateAndApplyHelper.cs:176`) only when the build failed or the lists were truncated; the id goes in `FullDiagnosticsResultId`. A green build stores nothing.
6. `StatusMessage` stays the one-line headline (`SummarizeBuild`, `WorkspaceBuildTestImpl.cs:169`); the generic offload filter already relays it (`ServiceRegistrationExtensionsBasic.cs:647-651`), so even a response that is somehow too large keeps its verdict inline.

## Execution rules

- C# edits only through MCP tools (`ReplaceSnippet` with `batchEdits`, `Member`, `MethodSignature`); Markdown with Edit/Write. Definitions before call sites in every batch.
- Every step ends with a clean `Build` (0 errors) before the next starts. Parts A and B are independent; do Part A first (smaller, unblocks the verification restarts), then Part B in order.
- Tests use NUnit 5: `Assert.ThrowsAsync`/`CatchAsync` must be awaited.
- ASCII-only punctuation in code comments, docs and messages.
- Each step's brief to the implementer must say: "edit nothing outside the named symbols; if a test seems to need another change, reply RESCOPE:" and "no Write/Edit/shell writes on .cs files".
- Coordinate with `plan_session_halt_recovery_and_git_gaps.md`: it renames `IsSessionHalted` (and the two drift tools) to `ExternalFileDrift`. Step A4 must use the tool names that exist when it is implemented.

## Steps

### Part A - blocking solution load

#### Step A1 - Wait types and timeout message
- Files: `RoslynSentinel.Common/SolutionLoadState.cs`, `RoslynSentinel.Tests/SolutionNotLoadedMessageTests.cs`
- Symbols: add to `SolutionLoadState.cs` (definitions only, no call sites yet): `public enum SolutionLoadWaitOutcome { NotLoading, Completed, TimedOut }`; `public static class SolutionLoadWait { public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30); }`; `SolutionNotLoadedMessage.LoadWaitTimedOut(TimeSpan waited)` returning: `"The solution is still loading after waiting {n} s. The load has NOT been cancelled and is continuing in the background; this call did not run. Retry in a few seconds, or call McpServerStatus and wait until loadInProgress is false. If it never finishes, the server log has the MSBuild errors."` (n = whole seconds).
- Call sites: none (new members).
- Change: one `ReplaceSnippet` batch (the three additions, then the test).
- Done when: `SolutionNotLoadedMessageTests.LoadWaitTimedOut_NamesTheWaitStatesNotCancelledAndTheStatusField` passes (asserts "30 s", "NOT been cancelled", "loadInProgress") and `Build` is clean.

#### Step A2 - Load gate in the workspace manager
- Files: `RoslynSentinel.Common/ISolutionProvider.cs`, `RoslynSentinel.Common/PersistentWorkspaceManager.cs`, `RoslynSentinel.Tests/Fakes/FakeWorkspaceManager.cs`
- Symbols: add to `ISolutionProvider`: `Task<SolutionLoadWaitOutcome> WaitForLoadAsync(TimeSpan timeout, CancellationToken cancellationToken);` (doc: waits for a load in progress; returns `NotLoading` when none is running; a timeout never cancels the load). In `PersistentWorkspaceManager`: field `private TaskCompletionSource? _loadGate;`; in `LoadSolutionAsync(string, string?, CancellationToken)` (`:437-540`) create the gate (`RunContinuationsAsynchronously`) immediately after `Interlocked.Increment(ref _loadsInProgress);` and, in the `finally` (`:536-539`), call `_loadGate?.TrySetResult()` and clear the field **before** `_solutionLock.Release()`; implement `WaitForLoadAsync` (`Volatile.Read(ref _loadGate)`; null -> `NotLoading`; else `await gate.Task.WaitAsync(timeout, ct)` catching `TimeoutException` -> `TimedOut`; a cancelled `ct` propagates). In `FakeWorkspaceManager`: `WaitForLoadAsync` returns `Task.FromResult(SolutionLoadWaitOutcome.NotLoading)`.
- Call sites: implementers of `ISolutionProvider` are exactly `PersistentWorkspaceManager` and `FakeWorkspaceManager` (Search for `: ISolutionProvider` / `, ISolutionProvider` returned only those two plus consumers that take the interface as a parameter). `LoadSolutionAsync` has no signature change.
- Change: one `ReplaceSnippet` batch, interface first.
- Done when: `Build` has 0 errors (behaviour is covered by Step A3, which must land immediately after).

#### Step A3 - Regression test for the gate
- Files: `RoslynSentinel.Tests.Tools.Basic/SolutionLoadWaitTests.cs` (new)
- Change: copy the `TestSolutionFixture` + real `PersistentWorkspaceManager` setup from `RoslynSentinel.Tests.Tools.Basic/ApplyDiffSizeGuardTests.cs`. Tests:
  - `WaitForLoadAsync_NoLoadRunning_ReturnsNotLoading`.
  - `WaitForLoadAsync_LoadRunning_ReturnsCompletedAndSolutionIsReadable`: start `LoadSolutionAsync` without awaiting, `await WaitForLoadAsync(SolutionLoadWait.DefaultTimeout, ct)` -> `Completed`, then `GetCurrentSolutionAsync` returns a solution.
  - `WaitForLoadAsync_ShortTimeoutMidLoad_ReturnsTimedOutAndLoadStillFinishes`: timeout of a few ms mid-load -> `TimedOut`; then await the load task and assert `LoadState.HasLoadedSolutionSinceStart`.
  Comment in the file that the mid-load cases depend on MSBuild taking longer than a few ms (they do; a real load takes seconds).
- Done when: the three tests pass.

#### Step A4 - Request filter and exempt-tool policy
- Files: `RoslynSentinel.Server.Basic/LoadWaitPolicy.cs` (new), `RoslynSentinel.Server.Basic/ServiceRegistrationExtensionsBasic.cs`, `RoslynSentinel.Tests.Server/LoadWaitPolicyTests.cs` (new)
- Symbols: `LoadWaitPolicy` (static, internal or public as the test project requires): `ExemptTools` (case-insensitive set: `LoadSolution`, `McpServerStatus`, `McpServerControl`, `McpToolsetControl`, `IsSessionHalted`, `GetLargeResult`, `Git`), `IsExempt(string? toolName)`, and `static async Task<string?> RefusalIfStillLoadingAsync(string? toolName, Func<TimeSpan, CancellationToken, Task<SolutionLoadWaitOutcome>> wait, TimeSpan timeout, CancellationToken ct)` returning `null` to proceed or `SolutionNotLoadedMessage.LoadWaitTimedOut(timeout)`. In `ServiceRegistrationExtensionsBasic.cs`: new `private static void AddLoadWaitFilter(IMcpRequestFilterBuilder filters)` modelled on `AddUnrecoverableBreakerFilter` (`:859` ff.): get `PersistentWorkspaceManager` via `context.Server.Services?.GetService<PersistentWorkspaceManager>()`, call the helper with `manager.WaitForLoadAsync`, return `CallToolResult { IsError = true, Content = [TextContentBlock] }` on a non-null refusal, else `next(...)`. A missing service means proceed. Register it as the **last** line of the `WithRequestFilters` lambda (after `AddUnrecoverableBreakerFilter(filters);`, `:109`).
- Call sites: `AddRoslynSentinelToolsBasic` (`:84-112`) is the only registration; the Advanced server calls it too (single filter list), so no change in `Server.Advanced`.
- Change: one `ReplaceSnippet` batch (new file first via `Member(addTopLevelType)` or `WriteFile`, then the filter method, then the registration line, then the test).
- Done when: `LoadWaitPolicyTests` pass: `ExemptTools_GoldenSet` (exact set; update it deliberately if a tool is renamed), `RefusalIfStillLoading_ExemptTool_NeverWaits`, `RefusalIfStillLoading_TimedOut_ReturnsTimeoutMessage`, `RefusalIfStillLoading_Completed_ReturnsNull` (use lambdas returning each outcome). `Build` clean.

#### Step A5a - LoadSolution waits, shares the timeout, no double load
- Files: `RoslynSentinel.Tools.Basic/WorkspaceProjectManagementImpl.cs`, `RoslynSentinel.Tools.Basic/WorkspaceProjectManagementTools.cs`, `RoslynSentinel.Tools.Basic/WorkspaceTools.cs`
- Symbols: `WorkspaceProjectManagementImpl.LoadSolution` (`:320`). New order inside the existing `try`: (1) `var wait = await _workspaceManager.WaitForLoadAsync(SolutionLoadWait.DefaultTimeout, cancellationToken);` on `TimedOut` return `IsError = true` with `ResultError(ToolErrorCode.Exception, SolutionNotLoadedMessage.LoadWaitTimedOut(...))`; (2) the existing `IsAlreadyLoadedPath` check (now correct after an auto-load finished); (3) start `var loadTask = _workspaceManager.LoadSolutionAsync(solutionPath, baseRepoDir, CancellationToken.None);` and `await loadTask.WaitAsync(SolutionLoadWait.DefaultTimeout, cancellationToken)`; on `TimeoutException` attach `loadTask.ContinueWith(t => _logger.LogError(...), TaskContinuationOptions.OnlyOnFaulted)` and return the same timeout message. Update the `[Description]` of both `LoadSolution` declarations (`WorkspaceProjectManagementTools.cs:43`, `WorkspaceTools.cs:122`): "waits up to 30 s; if the server is already loading this solution at start-up, returns 'already loaded' when it finishes; on timeout the load keeps running".
- Call sites: `Impl.LoadSolution` is called by `WorkspaceProjectManagementTools.LoadSolution` and the `WorkspaceTools.LoadSolution` facade only (`FindReferences` before editing; signature unchanged). Tests calling the tool: `Tests.Tools.Advanced/ComprehensiveToolTests.cs:170`, `Tests.Tools.Basic/BatteryTwentyTests.cs:691` (both expect an error for a bad path; the bad-path error text must stay the same).
- Done when: `Build` has 0 errors and `BatteryTwentyTests.LoadSolution_NonExistentPath_ReturnsErrorString` plus `ComprehensiveToolTests.LoadSolution_NonExistentFile_ReturnsErrorResult` still pass.

#### Step A5b - Regression test for LoadSolution mid-load
- Files: `RoslynSentinel.Tests.Tools.Basic/LoadSolutionWaitTests.cs` (new)
- Change: construct `WorkspaceProjectManagementImpl` the way `BatteryTwentyTests` builds its `_workspaceTools` (read its setup first) over a real `PersistentWorkspaceManager`. Tests: `LoadSolution_WhileSameSolutionLoading_WaitsThenReportsAlreadyLoaded` (start `LoadSolutionAsync` unawaited on an absolute path, call the tool with the same path, assert text contains "already loaded" and `LoadState` shows one successful load, not a second); `LoadSolution_ForceReloadMidLoad_WaitsThenReloads`.
- Done when: both tests pass.

#### Step A6 - `loadInProgress` in McpServerStatus
- Files: `RoslynSentinel.Tools.Basic/ServerStatusTools.cs`, `RoslynSentinel.Tests.Server/McpServerStatusStructuredContentTests.cs`
- Symbols: `McpServerStatusResult` (`:196`): add `bool LoadInProgress` right after `IsFreshStartup` (the record has one construction site, `ServerStatusTools.McpServerStatus` at about `:81-104`, which uses named arguments: add `LoadInProgress: _workspaceManager.LoadState.LoadInProgress`). Extend the `McpServerStatus` `[Description]` (`:27`) with "loadInProgress = a solution load is running now; tool calls wait up to 30 s for it".
- Call sites: `McpServerStatusResult` is constructed only in that method; deserialised in `Tests.Server/McpServerStatusStructuredContentTests.cs` and `Tests.Tools.Basic/McpServerStatusToolListingTests.cs` (read both for positional constructions before editing).
- Done when: new test `McpServerStatus_ReportsLoadInProgressField` (schema contains `loadInProgress`; value false for a fake manager) passes and the existing status tests stay green.

### Part B - Build result shape

#### Step B1 - Characterise MSBuild's behaviour on a failed upstream project
- Files: `RoslynSentinel.Tests.Tools.Basic/BuildCascadeFixtureTests.cs` (new)
- Change: read `Tests.Tools.Basic/BuildIsolationTests.cs` (it already runs `RunFullBuildAsync` against a temp solution, line 55) and copy its setup. Create a temp 3-project solution `Base` (one compile error) <- `Mid` (references Base, valid code) <- `Top` (references Mid, valid code). Load it and run `RunFullBuildAsync(CancellationToken.None, maxDetails: 50, useScratchDir: true)`. Assert the **expected** hypothesis: every error line is from `Base`; `Mid` and `Top` contribute none.
- Done when: the test `FullBuild_UpstreamProjectFails_DownstreamProjectsReportNoErrors` passes. **If it fails** (MSBuild did report downstream errors), stop and report `RESCOPE:`: Option 2 (project-by-project re-run) then becomes necessary for fullBuild and goes back to the human.

#### Step B2 - DiagnosticInfo gets a project
- Files: `RoslynSentinel.Common/DiagnosticReport.cs`
- Symbols: `record DiagnosticInfo` (`:44`): add trailing `string? Project = null` as the last positional parameter.
- Call sites (all keep compiling because of the default; none edited): `Common/DiagnosticReport.cs:60`, `Common/ValidationEngine.cs:52,68,203,217`, `Engines.Basic/BuildEngine.cs:248`, `Engines.Basic/DiagnosticEngine.cs:122`, `Tests/CompilerErrorLookupHelperTests.cs:136,139`. `with` expressions (`WithRelativePath`) keep the field.
- Done when: `Build` has 0 errors.

#### Step B3 - BuildResult members and carrier types
- Files: `RoslynSentinel.Common/BuildResult.cs`
- Symbols: append to the `BuildResult` positional record, after `Detail`, in this order: `BuildFailureBreakdown? Breakdown = null`, `int OmittedErrorCount = 0`, `int SuppressedDownstreamErrorCount = 0`, `string? FullDiagnosticsResultId = null`, `[property: JsonIgnore] BuildFullDiagnostics? FullDiagnostics = null`. New records in the same file: `BuildFailureBreakdown(List<BuildProjectErrorCount> ByProject, List<BuildFileErrorCount> ByFile)`, `BuildProjectErrorCount(string Project, int ErrorCount, bool IsRootCause)`, `BuildFileErrorCount(string File, int ErrorCount)`, `BuildFullDiagnostics(List<DiagnosticInfo> Errors, List<DiagnosticInfo> Warnings)`. Add `using System.Text.Json.Serialization;`.
- Call sites: `new BuildResult(` appears only at `Engines.Basic/BuildEngine.cs:111` and `:277`, both with named arguments, so the new defaults compile. Other mentions are type references (`DiagnosticSummary`, `DiagnosticsSummaryResult`, `WorkspaceHealthReport`) that need no change. `[JsonIgnore]` keeps the lists out of `GetDiagnostics`/`GetWorkspaceHealth` output.
- Done when: `Build` has 0 errors.

#### Step B4 - Pure projector and root-cause classifier
- Files: `RoslynSentinel.Common/BuildResultProjector.cs` (new), `RoslynSentinel.Tests/BuildResultProjectorTests.cs` (new)
- Symbols: `public sealed record BuildProjection(int MaxDetails, bool IncludeOutput, bool IncludeWarnings)`; `public static class BuildResultProjector { public static BuildResult Project(BuildResult full, BuildProjection options, IReadOnlyDictionary<string, IReadOnlySet<string>>? transitiveDependencies); }` The dictionary maps a project name to the names of projects it transitively depends on; null means "no graph known" (every erroring project counts as a root cause). `Project` implements Decision B3/B4: reads `full.FullDiagnostics` (falls back to `full.Errors` when null), computes `Breakdown`, root-cause flags, `SuppressedDownstreamErrorCount`, orders root-cause errors first, caps to `MaxDetails`, cuts messages to 300 characters, sets `OmittedErrorCount`, drops lists/tails per the green/failed rules, never sets `FullDiagnosticsResultId` (the tool layer does, it needs I/O).
- Change: pure code, no I/O. Tests (all in `BuildResultProjectorTests`): `Green_ReturnsOnlyProjectsAndCounts`; `OneError_ReturnedInFull`; `TenErrors_AllReturnedGroupedByProject`; `HundredErrors_CapsDetailsAndReportsOmitted`; `ThousandErrorsAcrossRootAndDownstream_SuppressesDownstreamAndFlagsRootCause`; `FailedWithZeroParsedErrors_KeepsTails`; `IncludeOutputAndIncludeWarnings_AreHonoured`; `LongMessage_IsCutTo300Characters`; and `ProjectedFailedResult_SerializesUnder15KB` (100 errors in, serialise with `System.Text.Json` and assert `< LargeResultHelper.OffloadThresholdBytes`).
- Done when: `BuildResultProjectorTests` pass.

#### Step B5 - Engine: project attribution, uncapped carrier, parser fix
- Files: `RoslynSentinel.Engines.Basic/BuildEngine.cs`, `RoslynSentinel.Engines.Basic/DiagnosticEngine.cs`, `RoslynSentinel.Tests.Tools.Basic/BuildEngineParseTests.cs` (new)
- Symbols and call sites:
  - `BuildEngine`: extract the diagnostic loop of `RunFullBuildAsync` (`:243-262`) into `public static (List<DiagnosticInfo> Errors, List<DiagnosticInfo> Warnings) ParseDiagnostics(string stdout)`; widen `DiagnosticLineRegex` (`:128-130`) to `^(?<path>.+?)(?:\((?<line>\d+),(?<col>\d+)\))?\s*:\s*(?<severity>error|warning)\s+(?<id>[A-Za-z0-9]+):\s*(?<message>.+?)\s*\[(?<project>[^\]]+?\.[A-Za-z]+proj)(?:::[^\]]*)?\]\r?$` (line/col default 0; project = file name without extension). Set `Project` on each `DiagnosticInfo`. In both `new BuildResult(` calls (`:111`, `:277`) pass `FullDiagnostics: new BuildFullDiagnostics(allErrors, allWarnings)` while keeping `Errors:`/`Warnings:` capped as today. In `RunQuickBuildAsync` (solution scope) call `GetSolutionDiagnosticsAsync(int.MaxValue, ct)` instead of `maxDetails` so the uncapped list exists, then cap `Errors`/`Warnings` with `Take(maxDetails)`; ErrorCount stays exact. Consequence: `RunQuickBuildAsync` callers (`WorkspaceBuildTestImpl.cs:88,193`, `WorkspaceHealthMiscImpl.cs:144`, `BuildEngineTests.cs:21`) see the same capped `Errors` as before.
  - `DiagnosticEngine.GetSolutionDiagnosticsAsync` (`:~95-132`): in the per-project loop, set `Project` on each info (`d.ToInfo().WithRelativePath(solutionDir) with { Project = project.Name }`). No signature change; the one caller of this method is `BuildEngine.RunQuickBuildAsync` (verify with `FindReferences` before editing).
- Done when: `BuildEngineParseTests` pass: `ParseDiagnostics_CompilerLine_CapturesProjectName`, `ParseDiagnostics_ProjectLevelNuGetError_IsParsed` (canned line `C:\r\X.csproj : error NU1101: Unable to find package Foo. No packages exist with this id [C:\r\X.csproj]`), `ParseDiagnostics_MultiTargetProjectSuffix_IsParsed` (`[C:\r\X.csproj::TargetFramework=net9.0]`), `ParseDiagnostics_MessageContainingBrackets_IsKept`; and the existing `BuildEngineTests`/`BuildIsolationTests` stay green.

#### Step B6 - Tool boundary: project, store, new parameters, descriptions
- Files: `RoslynSentinel.Tools.Basic/WorkspaceBuildTestImpl.cs`, `RoslynSentinel.Tools.Basic/WorkspaceBuildTestTools.cs`, `RoslynSentinel.Tools.Basic/WorkspaceTools.cs`
- Symbols: `WorkspaceBuildTestImpl.Build` (`:172`): add parameters `bool includeOutput = false, bool includeWarnings = false` before `cancellationToken`. Change the default of `maxDetails` to 20 in all three declarations (decision for the human, see Risks). After the engine returns: build the dependency map from `await _workspaceManager.GetSolutionAsync(ReadSource.Committed, ct)` (`solution.GetProjectDependencyGraph()`; per project `GetProjectsThatThisProjectTransitivelyDependsOn(id)` mapped to names); call `BuildResultProjector.Project(...)`; when `Outcome == Failed` or `OmittedErrorCount > 0`, serialise `full.FullDiagnostics` and call `LargeResultHelper.StoreRawJsonAsync(json, _workspaceManager.GetSolutionRoot(), ct)` and set `FullDiagnosticsResultId`; keep `WithoutTailsWhenClean` semantics (the projector already drops tails). Keep `ForPossiblyLargeDataAsync` as the final step (a safety net, normally not triggered now). Replace the long `Build` Description in `WorkspaceBuildTestTools.cs:35` and the short one in `WorkspaceTools.cs:~485` with the same text: what green returns, what failed returns (counts by project/file/code, first `maxDetails` errors, root-cause project first, `SuppressedDownstreamErrorCount`), `FullDiagnosticsResultId` for the rest (read it with `GetLargeResult`), and that stdout/warnings need `includeOutput`/`includeWarnings`. Use `MethodSignature` or one `ReplaceSnippet` batch (Impl first) so the two declarations and their delegating calls (`WorkspaceBuildTestTools.cs:46` `_impl.Build(...)`, `WorkspaceTools.cs:~495` `_buildTest.Build(...)`) change together.
- Call sites: `FindReferences` on `WorkspaceBuildTestImpl.Build` found one caller (`WorkspaceBuildTestTools.cs:46`); re-measure the `WorkspaceTools.cs` delegate before editing. `PlanStepRunner/Program.cs:265` and `SubAgentEvalImpl` call the tool by name with `level` only (unaffected).
- Done when: `Build` has 0 errors.

#### Step B7 - Tool-level regression tests and existing-test updates
- Files: `RoslynSentinel.Tests.Tools.Basic/BatteryTwentyTests.cs`, `RoslynSentinel.Tests.Tools.Basic/BuildToolResultShapeTests.cs` (new)
- Change: run the `Build_*` group in `BatteryTwentyTests` (lines about 975-1110); fix only assertions that relied on warnings, stdout or 50 details being present by default (expected: clean-solution and tail tests keep passing; `RepeatedDiagnosticAcrossFiles_ErrorSummaryGroupsById` must still see `ErrorSummary`). New tests reuse that file's fixture pattern: `Build_QuickBuild_FailedSolution_ReturnsBreakdownByProjectAndFirstNDetails`, `Build_QuickBuild_Green_HasNoListsAndNoTails`, `Build_QuickBuild_FailedWithMoreThanMaxDetails_ReturnsFullDiagnosticsResultIdReadableByGetLargeResult`, `Build_QuickBuild_IncludeWarnings_ReturnsWarningList`.
- Done when: `BuildToolResultShapeTests` and the `Build_*` tests in `BatteryTwentyTests` pass.

#### Step B8 - Documentation
- Files: `docs/current/findings/finding_build_offload_hides_error_count.md`, `docs/current/TODO.md`
- Change: set the finding to `RESOLVED <date>` with a pointer to this plan and the commit; if `ArchitectureDocFreshnessTests` fails after the description changes, regenerate with `scripts/Generate-ArchitectureMap.ps1` (non-C#, allowed); add one TODO line if Option 2 or the `GetDiagnostics`/`GetWorkspaceHealth` follow-ups below are accepted. Update `reference_architecture_map.md` request-pipeline list with the new innermost load-wait filter.
- Done when: `ArchitectureDocFreshnessTests` passes.

### Final step - Verification
- `Build` at solution scope: 0 errors.
- `RunTest` at solution scope; compare with the known-failure baseline (`reference_known_failing_tests`); report only new failures.
- `McpServerControl(operation: StopServer, confirmServerStop: ConfirmServerStop)`, wait for the respawn, `LoadSolution` the repo `.slnx`. Live checks: (a) immediately after the next restart, call any read tool (for example `ListAll`) and see it return normally rather than "retry"; `McpServerStatus.loadInProgress` is `false` afterwards; (b) `Build(level: fullBuild)` on this repo returns an inline result (no pointer) with a few hundred bytes of `projectsCompiled`, and a deliberately broken file gives a `breakdown` with the broken project flagged as root cause.
- Done when: all three hold.

## Out of scope

- Cancelling a running load on timeout (wait-only; see Risks).
- Making the timeout configurable; fixing the swallow-all `catch (Exception)` around `OpenSolutionAsync` in `LoadSolutionAsync` (`:471-486`) that also swallows cancellation; surfacing a failed start-time load (the plan only stops a failed load from looking like "in progress").
- Any change to `GetCurrentSolutionAsync` or its 389 call sites.
- Launcher changes (`scripts/roslynsentinel-mcp-launch.ps1`), `C:\Users\Administrator\.mcp.json`.
- `RunTest` output shape (separate plan `plan_mutating_and_test_tool_result_noise.md` territory); `GetDiagnostics`/`GetWorkspaceHealth` embedded `BuildVerification` (they still carry up to 50 details; they are not projected).
- The literal project-by-project re-run (Option 2 below).
- The stale comment at `RoslynSentinel.Utilities.PlanStepRunner/Program.cs:191` ("auto-load is unawaited"): harmless after this plan (its explicit `LoadSolution` returns "already loaded"), can be edited later.

## Risks and open decisions

Decisions for the human:

1. **Wait-only vs cancel on timeout.** Recommended: wait-only (the load continues, the call gets a clear error, a retry succeeds). Cancelling would throw away up to 30 s of MSBuild work on a large solution and, with the current swallow-all catch, would leave a half-loaded `CurrentSolution`. Tradeoff: a hung load keeps the lock, so every later call also times out at 30 s until the server is restarted.
2. **Launcher auto-load heuristic** (`roslynsentinel-mcp-launch.ps1:315-333`: add `--solution=<repoRoot>\RoslynSentinel.slnx` only when no `--solution` was passed, the file exists and cwd is inside the repo). Confirmed working in `bin-vscode/d87a019a-59b2a8d4/launch.log` (the `Auto-load:` line). Options:
   - **Keep.** Zero risk; windows opened on other repos get no auto-load, and the first call there says "Call LoadSolution" (after this plan the wait only helps when a load is actually running). **Recommended now.**
   - **Discover a single `.slnx`/`.sln` at the git root of cwd.** Helps other repos; but it loads whatever it finds into every VS Code window (memory and CPU for windows that never call the server), needs a PowerShell 5.1-safe git-root walk, and guesses wrong when a repo has several solutions. Revisit after Part A ships, with the multi-solution case decided first.
   - **Lazy-load** (do not load at start; load on first tool call). Saves resources for idle windows and removes the start-time race entirely, but needs a trigger point in the filter, a default-path source, and changes `McpServerStatus` semantics; it is feature-sized (*needs design*).
3. **`maxDetails` default 50 -> 20 for `Build`.** A public default change. 20 details is about 6 KB and keeps the typed response under the 15 KB offload threshold; 50 details (about 12 KB) plus the breakdown can cross it. The remainder is always reachable through `FullDiagnosticsResultId`. Alternative: keep 50 and rely on the 300-character message cut.
4. **Extending `BuildResult` (additive members) rather than a new response type.** Chosen to keep `outcome`/`errorCount` and the six `BatteryTwentyTests` casts working. A cleaner `BuildReport` type is possible later; it would be a public API shape change and needs sign-off.
5. **Option 2, literal project-by-project re-run in dependency order** (`dotnet build <proj> --no-dependencies` for each project in topological order, stop at the first failing layer). Not planned: it re-pays MSBuild start-up per project (about 19-30 projects here; hypothesis: roughly 3-5 s each, +60-150 s over a single solution build, unmeasured, and it loses MSBuild's parallelism) and the classifier gives the same root-cause view for free. It becomes necessary only if Step B1 fails.

Unverified or uncertain:

- Exempt-tool names: `McpServerControl`, `IsSessionHalted`, `LoadSolution`, `GetLargeResult` confirmed in `Tools.Basic/AdminTools.cs:38,156`, `WorkspaceProjectManagementTools.cs:43`, `WorkspaceReadNavigationTools.cs:102`; `McpToolsetControl` is referenced by `Tests.Server/ClaudeLeanModeTests.cs:34` but its declaration was not located (Step A4 must confirm with `McpServerStatus(toolListing: all)`). The three drift tools are being merged into `ExternalFileDrift` by another plan.
- Start-time load duration is not measured; if loads regularly exceed 30 s on a big solution, every call in that window gets the timeout message. The message tells the caller to retry, but the constant may need to be an option (not planned).
- `ReadFile` is not exempt, although it has a no-solution path for absolute paths (`WorkspaceFileEditImpl.ReadFileWithoutSolutionAsync`, `:210`); it waits for the load like everything else. A failed start-time load makes the gate release with no solution, after which calls get the normal "No solution is loaded" message (the failure reason is only in the server log).
- `ResolveFromWire`'s relative-path `LoadSolution` case: `IsAlreadyLoadedPath` returns false for non-rooted paths (`:279-288`), so a relative-path manual `LoadSolution` after an auto-load still reloads. Not changed here.
- Cancellation of `LoadSolutionAsync` by the client now no longer cancels the load (token `None`); a deliberate change, flagged for review.
- Part B timings: the green-payload savings are measured (94 files); the projector's runtime cost is negligible but unmeasured. Whether quickBuild's `int.MaxValue` request (Step B5) is memory-significant on a 1000-error solution is a hypothesis; the diagnostics already exist in memory in `allDiagnostics`.
- Journal entries are impressions: the two `cc715aa0`/`93fd2d31` Build notes were traced to source (every fullBuild payload exceeds 15 KB; measured), not taken on trust.
