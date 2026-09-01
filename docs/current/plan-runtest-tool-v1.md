# Plan — Add a `RunTest` MCP tool (+ grouped-summary retrofit for `Build`)

## Title
Add a `RunTest` tool that shells out to `dotnet test` against the currently loaded solution
(or a project/filter within it) and returns structured pass/fail results — the test-side
counterpart to the existing `Build` tool (`RoslynSentinel.Server.Basic\SentinelWorkspaceTools.cs:1237`).
Along the way, retrofit `Build` to return a grouped error/warning summary too — reusing the
pre-existing `DiagnosticGroupSummary` type rather than a new one, see Task 4.

## Background
`Build` (`fullBuild` level) already shells out to `dotnet build` and parses MSBuild diagnostic
lines into `List<DiagnosticInfo>` (`RoslynSentinel.Basic\BuildEngine.cs:80-162`). There is no
equivalent for running tests: an agent working against *any* solution loaded into RoslynSentinel
(not just this repo) currently has no MCP tool to execute `dotnet test` and get structured results
— it would have to shell out itself outside the tool surface, which defeats the point of an
in-editor agent loop that's supposed to stay inside RoslynSentinel's tool-mediated workflow.

Repo-wide grep confirms no existing code parses `dotnet test` output (TRX or console) anywhere —
`RoslynSentinel.Tests.ModelEval`'s own scripts (`roslynsentinel-modeleval.ps1`) invoke `dotnet test`
via PowerShell, outside the tool surface, purely to test *RoslynSentinel itself* during development.
`RunTest` is a different thing: a tool an agent calls, against an arbitrary target solution, the
same way `Build` is.

This plan follows `Build`'s precedent deliberately: same engine/tool split, same subprocess
pattern as `GitTools.RunGitAsync` (`RoslynSentinel.Server.Basic\GitTools.cs:191-207`) and
`BuildEngine.RunFullBuildAsync`, same registration path, same test-file precedent
(`RoslynSentinel.Tests.Battery\*`).

## Assumptions
- `dotnet` is on PATH in the environment the server runs in — same assumption `Build` and
  `GitTools` already make.
- No new NuGet package needed. `dotnet test --logger "trx;LogFileName=<path>"` writes a TRX
  (XML) file; .NET's `System.Xml` (already implicitly available, no package) is enough to parse
  it — avoid adding a third-party TRX parser package when the schema needed here is narrow
  (test name, outcome, duration, error message per `UnitTestResult` element).
- Line numbers cited below are current as of this plan's writing and will drift — re-locate with
  Grep before editing.
- Build (0 errors) and test after each task; commit each task separately, per
  [[feedback_build_before_commit]].
- `SentinelWorkspaceTools` is registered identically for both server flavors via
  `ServiceRegistrationExtensionsBasic.cs` — Advanced project-references Basic
  ([[project_advanced_extends_basic]]), so wiring the new tool/engine once in Basic covers both
  flavors for free, exactly as `BuildEngine` already does
  (`RoslynSentinel.Server.Basic\ServiceRegistrationExtensionsBasic.cs:64`).
- Scope is a *new tool*, not a change to `Build`. Tests are conceptually distinct from compilation
  (pass/fail per test case, not per diagnostic), so a separate `RunTest` tool with its own result
  shape is a cleaner fit than overloading `Build` with a `runTests` flag.

## Known operational caveats
- **Runtime, not just exit code, matters.** `dotnet test`'s process exit code is 0 only when the
  whole run succeeds; a single failing test still produces exit code 1 with useful TRX detail —
  unlike `Build`, where a nonzero exit code alone is sufficient signal. `RunTest` must always parse
  the TRX even when the process exits nonzero (mirror `BuildEngine.RunFullBuildAsync`'s pattern of
  parsing stdout regardless of `process.ExitCode`).
- **No solution/project loaded, or no test projects found** should surface as a normal
  `ToolErrorCode.InvalidArgument`/`NotFound`, not `TestFailed` — distinguish "the tool couldn't run
  a test" from "tests ran and some failed" the same way `Build`'s plan distinguished
  `BuildFailed` (tool-level) from `Success=true, Data.BuildSucceeded=false` (real outcome).
- **Long-running / hung test runs.** A test run can hang (deadlock, infinite loop) far more easily
  than a build. Needs an explicit timeout distinct from external cancellation — the same open
  question `Build`'s own plan left unresolved for `fullBuild`
  (`docs/obsolete/plan-build-verification-tool-v1.md:177-178`); resolve it here rather than
  deferring twice. Default suggestion: 300s, overridable via a `timeoutSeconds` parameter.
- **MSB3027/MSB3021 file-lock detection** (`BuildEngine.cs:140-144`) applies here too, since
  `dotnet test` builds before running — reuse the same detection, don't reimplement.
- **Result volume.** A large test project can produce thousands of results. Two mitigations, in
  order of how much they actually help an agent: (1) `FailureSummary` groups all failures by cause
  and is never capped, so the dominant failure pattern is visible in one field regardless of run
  size (see Task 1 below); (2) the per-test `Results` list is filtered by `resultsType`
  (`all`/`failed`/`skipped`) and then capped by `maxDetails`, ordered failures/skipped-first,
  passed-last, so what gets cut is the tests an agent least needs to see individually. Defer
  `ForPossiblyLargeDataAsync`/`GetLargeResult` offload (`ToolResult.cs:101-125`) until real usage
  shows an agent actually needs the full uncapped `Results` list — add it later as a follow-up once
  that's observed rather than speculatively now; `FailureSummary` may make that need rare in
  practice.
- **Filter/zero-result ambiguity.** A filter typo that matches nothing looks identical to "there
  are no test projects" unless distinguished explicitly — an agent seeing a bare empty result is
  liable to loop retrying the same broken filter or hallucinate a cause. `Detail` must say which
  case occurred:
  - No test projects found under the resolved scope → `Detail = "No test projects found under
    {scope}."`
  - Test projects ran, but `filter` matched zero tests → `Detail = "0 tests matched filter
    '{filter}' ({projectCount} test project(s) ran)."` — confirm during Task 1 whether this count
    needs to come from `dotnet test`'s stdout summary line rather than the TRX, since a
    filtered-to-zero run may produce a near-empty TRX with no discovered-count to read.

## Approach
Tasks 1–2 build the shared engine and tool; Task 3 is tests; Task 4 retrofits `Build` with the
same grouped-summary shape; Task 5 is docs. Do them in order — Task 4 depends on Task 1's
`GroupedCountSummary` type.

### Task 1 — `TestRunEngine` (new file, `RoslynSentinel.Basic\TestRunEngine.cs`)
Follow `BuildEngine`'s exact convention: engine returns `EngineResultWrapper<T>`; tool layer
translates to `ToolResult<object>`.

```csharp
public enum TestOutcome { Passed, Failed, Skipped, NotExecuted }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TestResultsFilter { all, failed, skipped }

public record TestCaseResult(
    string TestName,
    TestOutcome Outcome,
    TimeSpan Duration,
    string? ErrorMessage,   // failure message, capped length
    string? ErrorStackTrace // capped, null unless Failed
);

`GroupedCountSummary` is a new type, `RunTest`-only (see Task 4 for why it does *not* end up
shared with `Build`, which reuses the pre-existing `DiagnosticGroupSummary` instead). Placed in
`RoslynSentinel.Common\GroupedCountSummary.cs` for consistency with `DiagnosticInfo`'s own
location, not because anything currently requires cross-project sharing:
```csharp
/// <summary>One bucket in a capped-list's full-population summary (e.g.
/// <see cref="TestRunResult.FailureSummary"/>, <c>BuildResult.ErrorSummary</c>) — every item that
/// groups under the same signature, so an agent sees "45 of 50 failures share one cause" in one
/// line instead of paginating hundreds of results to notice the pattern itself.</summary>
public record GroupedCountSummary(
    string Signature,   // grouping key — meaning is caller-defined (diagnostic Id, failure cause, ...)
    int Count,
    string ExampleRef   // one representative identifier from the group (test name, file:line, ...)
);
```

```csharp
public record TestRunResult(
    bool RunSucceeded,       // true only if every executed test passed AND the process exited 0
    int ExitCode,
    int TotalCount,
    int PassedCount,
    int FailedCount,
    int SkippedCount,
    List<GroupedCountSummary> FailureSummary,  // always full (never capped by maxDetails) — see below
    List<TestCaseResult> Results,  // filtered by resultsType, then capped — see below
    string? StdoutTail,
    string? StderrTail,
    TimeSpan Duration,
    string? Detail = null    // e.g. MSB3027 lock hint, or "no test projects found"
);
```

**`FailureSummary` is the first thing an agent should read.** Group failed `TestCaseResult`s by a
signature derived from `ErrorMessage`: prefer the exception type + first line if the message looks
like `<ExceptionTypeName>: <first line>` (xUnit/NUnit assertion messages start this way — e.g.
`Assert.Equal() Failure` or `NUnit.Framework.AssertionException: Expected: 5 But was: 3`),
otherwise fall back to the raw first line of `ErrorMessage` truncated to ~120 chars. This mirrors
`BatchTypes.cs:156`'s `failures.GroupBy(f => f.Reason).ToDictionary(...)` pattern — group-and-count
failures by cause — but exposed as a `List<GroupedCountSummary>` (`Signature` = the derived
message signature, `ExampleRef` = one representative `TestName`, ordered by `Count` descending) rather
than a bare dictionary, so `ExampleTestName` gives the agent one concrete test to open with
`GetMethodSource` rather than every group being an anonymous count. `FailureSummary` is never
truncated by `maxDetails` — it collapses N failures into a handful of groups already, so it stays
small regardless of run size; only `Results` (the per-test list) is capped.

**`resultsType` filters `Results`, not `FailureSummary` or the summary counts.**
`TotalCount`/`PassedCount`/`FailedCount`/`SkippedCount` always reflect the whole run regardless of
`resultsType` — only the inline `Results` list is filtered:
- `all` (default) — every `TestCaseResult`, failures/skipped-first ordering, then `maxDetails` cap.
- `failed` — only `Outcome == Failed`, then `maxDetails` cap. Since `FailureSummary` already gives
  full failure coverage grouped by cause, this is for when an agent wants individual failing test
  names/messages rather than the grouped view.
- `skipped` — only `Outcome == Skipped || Outcome == NotExecuted`.
Filter first, then cap — a `maxDetails=50` with `resultsType=failed` on a run with 200 failures
returns the first 50 failures, not 50 passed-and-filtered-away-to-nothing.

- `RunAsync(ToolScope scope, string? scopeName, string? filter, TestResultsFilter resultsType,
  int maxDetails, int timeoutSeconds, CancellationToken cancellationToken)`:
  - Build `FailureSummary` from the full failed-test set (pre-filter, pre-cap) — grouping rule
    above. Then derive `Results` by applying `resultsType`, ordering failed/skipped-first, and
    capping to `maxDetails`.
  - Resolves the target path the same way `BuildEngine.RunFullBuildAsync` does:
    `_workspaceManager.CurrentSolution?.FilePath ?? _workspaceManager.SolutionPath`, or when
    `scope == ToolScope.project`, resolve the named project's `.csproj` path from the current
    solution instead (return `InvalidInput` if `scopeName` doesn't resolve to a loaded project —
    mirror `RunQuickBuildAsync`'s `scope == file/project` validation at `BuildEngine.cs:24-49`).
  - `scope == ToolScope.file` is not meaningful for tests (no such thing as "run the tests in this
    file" via `dotnet test` alone) — reject with `InvalidInput` explaining only `project`/`solution`
    are supported, rather than silently falling back to solution scope.
  - Subprocess: `FileName = "dotnet"`, working directory = target's containing directory,
    `ArgumentList = ["test", targetPath, "--nologo", "-v", "quiet", "--logger",
    $"trx;LogFileName={trxPath}"]`, plus `["--filter", filter]` appended when `filter` is non-null
    (pass-through of `dotnet test --filter` syntax — same syntax `roslynsentinel-modeleval.ps1`
    already uses, so this is a familiar convention, not a new one to invent).
    `trxPath` should be a fresh temp file per call (`Path.GetTempFileName()`-style, `.trx`
    extension) — clean it up in a `finally` after parsing, don't leave TRX litter under the
    target's `TestResults\` folder.
  - Match `BuildEngine.RunFullBuildAsync`'s exact `ProcessStartInfo`/`BeginOutputReadLine`/
    `BeginErrorReadLine` shape. Wrap `WaitForExitAsync(cancellationToken)` in a linked
    `CancellationTokenSource` combining the caller's token with a `timeoutSeconds` timer; on
    timeout, kill the process tree (`process.Kill(entireProcessTree: true)`) before returning, and
    set `Detail = "Test run exceeded {timeoutSeconds}s and was terminated."`.
  - Parse the TRX file (`System.Xml.Linq.XDocument`) for `<UnitTestResult>` elements: `testName`,
    `outcome` (`Passed`/`Failed`/`NotExecuted`), `duration`, and for failures the nested
    `<Output><ErrorInfo><Message>`/`<StackTrace>`. If the TRX file is missing after the process
    exits (e.g. crashed before any logger flush, or zero test projects matched), fall back to
    `Detail` describing that plus the raw `StderrTail`, rather than throwing.
  - Reuse `BuildEngine.cs`'s `MSB3027`/`MSB3021` stderr/stdout detection verbatim (extract to a
    shared helper in `RoslynSentinel.Basic` if duplicating the two-line check feels wrong, but
    don't invent a different detection mechanism).
  - Consult `_workspaceManager.CheckRateLimit("RunTest", <limit>)` before running, matching
    `Build`'s guard (`SentinelWorkspaceTools.cs:1249`) — a test run is at least as expensive as a
    build.

### Task 2 — `RunTest` MCP tool (`SentinelWorkspaceTools.cs`, alongside `Build`)
```csharp
[McpServerTool(Name = "RunTest")]
[Produces(DataTag.Report)]
[Description("Runs `dotnet test` against the loaded solution (or a single project) and reports pass/fail results. scope=solution (default) runs every test project; scope=project requires scopeName. filter applies dotnet test's --filter syntax (e.g. \"FullyQualifiedName~Foo\"). resultsType narrows the returned Results list: all (default)/failed/skipped — FailureSummary (failures grouped by cause, always complete) is returned regardless. Returns RunSucceeded, ExitCode, TotalCount/PassedCount/FailedCount/SkippedCount, FailureSummary, capped Results list, Duration.")]
public async Task<ToolResult<object>> RunTest(
    ToolScope scope = ToolScope.solution,
    string? scopeName = null,
    string? filter = null,
    TestResultsFilter resultsType = TestResultsFilter.all,
    [ToolOptionAttribute(ToolOptionTag.ResultLimit)] int maxDetails = 50,
    int timeoutSeconds = 300,
    CancellationToken cancellationToken = default)
```
Body follows the standard try/catch + `_logger.LogError(ex, "RunTest failed", ...)` +
`ToolErrorCode.Exception` shape every other tool in this file uses (see `Build` at
`SentinelWorkspaceTools.cs:1246-1270` verbatim). Stamp `WorkspaceVersion` on success. Add
`TestRunFailed` to `ToolErrorCode` (`RoslynSentinel.Common\ToolResult.cs:26-55`) for tool-level
failure (dotnet not found, no solution loaded, no matching test projects) — distinct from
`Success=true, Data.RunSucceeded=false` (the run executed and some tests genuinely failed, which
is a normal/expected outcome, not a tool failure — same distinction `Build` draws for
`BuildFailed` vs. `BuildSucceeded=false`).

Register `TestRunEngine` as a singleton in `RoslynSentinel.Server.Basic\ServiceRegistrationExtensionsBasic.cs`
next to `services.AddSingleton<BuildEngine>();` (line 64), and inject it into
`SentinelWorkspaceTools`'s constructor alongside `_buildEngine`.

### Task 3 — Tests
New test file `RoslynSentinel.Tests.Battery\RunTestTests.cs`, following the constructor-wiring
pattern every other Battery test file uses (`new BuildEngine(workspaceManager, diagnosticEngine)`
style — see `ApplyDiffSizeGuardTests.cs:42` for the reference shape). Cover:
- A fixture solution with one passing and one failing test → `RunSucceeded=false`,
  `PassedCount=1`, `FailedCount=1`, and the failing entry's `ErrorMessage` is populated.
- `scope=project` with a `scopeName` that doesn't resolve to a loaded project → clean
  `InvalidArgument` error, not an exception.
- `scope=file` → clean `InvalidArgument` error explaining it's unsupported for this tool.
- `filter` narrows results (fixture with 3 tests, filter matching 1, `TotalCount=1`).
- `filter` matching zero tests (fixture has passing test projects, filter matches none) → `Detail`
  reports "0 tests matched filter", distinct from the "no test projects found" test case below.
- `FailureSummary` groups correctly: fixture with 5 failing tests sharing one assertion message and
  1 failing test with a distinct message → 2 `FailureGroup` entries, `Count=5` and `Count=1`,
  ordered descending by `Count`; `FailureSummary` is present and complete even when
  `resultsType=skipped` (i.e. it's independent of the `Results` filter).
- `resultsType=failed` on a mixed-outcome fixture returns only failed entries in `Results`, capped
  by `maxDetails` but not by `resultsType` itself narrowing the cap; `resultsType=skipped` returns
  only skipped/not-executed entries; `resultsType=all` (default) returns the failures/skipped-first
  ordering across all outcomes.
- No test projects in the target → tool-level error (`TestRunFailed` or `NotFound`), not a crash;
  `Detail` reads "No test projects found", distinct from the zero-filter-match case above.
- Rate-limit rejection surfaces a clean `ToolResult` error rather than throwing (mirror whatever
  `Build`'s equivalent test does, if one exists — check before writing a new one from scratch).
- TRX temp file is deleted after the call regardless of outcome (assert `!File.Exists(trxPath)`
  requires exposing the path somehow for the test, or just assert no `*.trx` litter remains in
  `Path.GetTempPath()` matching a known-unique marker — pick whichever is less invasive to the
  engine's public surface).

### Task 4 — Retrofit `Build` with the same grouped summary
**Correction found during implementation:** `Build` doesn't need the new `GroupedCountSummary`
type at all — `RoslynSentinel.Common\DiagnosticReport.cs:75-81` already has
`DiagnosticGroupSummary(DiagnosticId, Severity, MessageTemplate, Count, Locations)`, and
`GetDiagnostics`' `summarize=true` path (`SentinelWorkspaceTools.cs:1213-1219`) already implements
the exact grouping logic this task needs:
```csharp
var groups = relevant.GroupBy(d => d.Id).Select(g =>
{
    var first = g.First();
    var locations = g.Select(d => $"{d.FilePath}:{d.StartLine}").Distinct().Take(10).ToList();
    return new DiagnosticGroupSummary(DiagnosticId: g.Key, Severity: first.Severity, MessageTemplate: first.Message, Count: g.Count(), Locations: locations);
}).OrderByDescending(g => g.Count).Take(topN).ToList();
```
Reuse this verbatim rather than introducing `GroupedCountSummary` a second time for `Build` — two
grouped-summary shapes doing the same job in the same repo would be the kind of duplication this
retrofit should avoid, not add. Extract the lambda above into a small static helper (e.g.
`DiagnosticReportExtensions.GroupBySeverity(this IEnumerable<DiagnosticInfo>, int topN)` in
`DiagnosticReport.cs`) so `GetDiagnostics` and `Build` both call the same code instead of forking
it. `GroupedCountSummary` (Task 1) stays scoped to `RunTest` only, where the grouping key (a
derived message signature) doesn't fit `DiagnosticGroupSummary`'s diagnostic-code-shaped fields —
it does not need to move to `RoslynSentinel.Common` for sharing purposes; place it in
`RoslynSentinel.Basic` alongside `TestRunEngine` instead, unless `ToolResult`-adjacent shared types
already live in `Common` by convention (check `DiagnosticInfo`'s own location as precedent — it's
in `Common`, so match that for consistency even though nothing currently requires it).

- Add to `BuildResult` (re-locate the current record with `GetFileOutline` before editing — it's
  evolved since `docs/obsolete/plan-build-verification-tool-v1.md:74-86` was written):
  ```csharp
  List<DiagnosticGroupSummary> ErrorSummary,    // relevant.Where(Severity=="Error"), via GroupBySeverity, never capped
  List<DiagnosticGroupSummary> WarningSummary,  // same, Severity=="Warning"
  ```
- Build both summaries in `BuildEngine` from the *full* `errors`/`warnings` lists before either
  `RunQuickBuildAsync` or `RunFullBuildAsync` caps them via `maxDetails` — same
  full-population-before-cap ordering `RunTest`'s `FailureSummary` uses, for the same reason (the
  summary must reflect the whole run, not the truncated view). Use `topN` = a generous fixed cap
  (e.g. 50) independent of `maxDetails`, matching `GetDiagnostics`' existing `topN` parameter
  default rather than inventing a new knob.
- Update `Build`'s `[Description(...)]` to mention `ErrorSummary`/`WarningSummary` tersely,
  matching this codebase's token-conscious description-length convention (see `Build`'s current
  description for the existing tone/length to match).
- Extend whichever Battery test file already covers `Build` (or add one if none exists — check
  first) with a case asserting grouped counts on a fixture with repeated diagnostic Ids (e.g. 3
  files each missing the same using directive → one `ErrorSummary` group with `Count=3`).

### Task 5 — Docs
Add a short `RunTest` entry, and a short `ErrorSummary`/`WarningSummary` addition to `Build`'s
entry, wherever `Build` is documented for tool consumers (confirm the doc is still current before
editing, same caveat `Build`'s own plan noted). No `SCENARIOS.md`-style gap to close here since
none currently mentions test execution specifically — skip that cross-reference unless research
turns one up while implementing.

## Open questions to resolve before/while implementing
- Exact TRX schema quirks across SDK versions (attribute names, nested `Output` element shape) —
  verify against a real TRX produced by this repo's own `dotnet test` before finalizing the parser,
  rather than trusting the schema from memory.
- Whether `GroupedCountSummary`'s message-derived `Signature` for `RunTest` needs a smarter
  heuristic than "exception type + first line" once tested against real xUnit/NUnit failure output
  from this repo's own suites — some assertion messages (e.g. parameterized test failures with
  embedded values) may need value-normalization to group correctly (e.g. `Expected: 5 But was: 3`
  and `Expected: 5 But was: 7` are the same underlying failure but currently distinct signatures).
