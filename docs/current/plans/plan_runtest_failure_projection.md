# Plan: Project RunTest results so failed tests are visible inline

**Status:** IMPLEMENTED 2026-10-17 (single commit; find it with git log on this file). Steps 1-5 built and tested (TestRunResultProjectorTests 18/18, RunTestTests 20/20); Step 6 partly done: server restarted and the new fields (`omittedResultCount`, `listSummary`, `totalRecords`) are live, but the original failing case could not be reproduced because the characterization tests now pass (15/15), so inline failure listing was verified only through `RunTestTests`.

## Problem
A RunTest over `RoslynSentinel.Tests.Advanced` (17 tests, 14 failed) returned only counts inline: the result was
34284 bytes, offloaded, with `listSummary: null` and `itemCount: 21`. Which tests failed was visible only through
`GetLargeResult`.

Traced causes (source, verified by an explore agent 2026-10-17):
- `WorkspaceBuildTestImpl.cs:316` returns the engine's `TestRunResult` raw via a bare `SentinelCallToolResult<object>`;
  the incomplete-run branch at `:313` does the same. No projection, no `ForPossiblyLargeDataAsync`.
- Each failed row carries uncapped `ErrorMessage` and `ErrorStackTrace` (`TestRunEngine.cs:508-509`) plus up to
  `MaxTestOutputChars` = 2000 chars of `Output` (`:82`, `:512`). A failing run also keeps the 40-line `StdoutTail`/`StderrTail`.
- The offload filter only relays a `listSummary` the tool already set; it never computes one. `RunTest` sets none.
- `itemCount` is the sum of all arrays under `successData` (Results + FailureSummary + ProjectSummaries), not a test count.

`Build` avoids this: `BuildResultProjector.Project` (`RoslynSentinel.Common/BuildResultProjector.cs:38`) caps and truncates
before the result is sent, and `StoreFullDiagnosticsAsync` (`WorkspaceBuildTestImpl.cs:211`) keeps the full list behind
`FullDiagnosticsResultId`.

## Decision
Add a pure `TestRunResultProjector` in `RoslynSentinel.Engines.Basic` (not Common: `Common` cannot reference
`TestRunResult`; not an MCP type, so it fits the engine layer). The tool layer (`WorkspaceBuildTestImpl.RunTest`) applies it,
stores the unprojected result via `LargeResultHelper.StoreRawJsonAsync` when anything was cut, and returns through
`ForPossiblyLargeDataAsync` with `totalRecords`, `statusMessage` and a `ListSummary` grouped by test class. Truncation stays at
the tool boundary, not in `ParseTrx` (the engine intentionally returns full material; `TestRunEngineTrxOutputTests:180` asserts
an intact stack trace).

Projection rules (constants live in the projector):
- `ErrorMessage` cut to 300 chars, "..." replacing the cut text (same as Build's `MaxMessageLength`).
- First 10 returned rows: message plus `ErrorStackTrace` reduced to its first 2 non-empty lines, capped at 300 chars.
  Later rows: message cut to 150 chars, no stack trace.
- `Output` on rows: dropped unless `includeOutput` is true (then kept as the engine returned it, <= 2000 chars).
- `StdoutTail`/`StderrTail`: dropped unless `includeOutput` is true, or the run did not complete, or no tests were parsed
  (`TotalCount == 0`), because then they are the only diagnostic.
- `FailureSummary` capped at 10 groups.
- `OmittedResultCount` = matching-outcome count minus `Results.Count`, where the matching count is `FailedCount` for
  `resultsType=failed`, `SkippedCount` for `skipped`, `TotalCount` for `all`. It is 0 when `summary=true` (Results is empty by request).
- `FullResultsId`: set by the tool (not the projector) to the stored unprojected result's id, only when a row was truncated,
  `Output`/tails were dropped, or `OmittedResultCount > 0`.
- The projector (Step 1, built) does not special-case `summary=true`: with an empty `Results` it reports
  `OmittedResultCount` = the matching count. The tool layer (Step 3) must zero it when `summary` is true.

## Execution rules
- Dispatch sequentially, one implementer slice at a time; one `Build`/`RunTest` at a time. Tier D0 for every slice
  (`.claude/implementer-tiers.json`); tests go to `test-writer`.
- New record fields are appended as optional trailing parameters of `TestRunResult`, so none of its ~30 existing
  references (4 files: `TestRunEngine.cs`, `WorkspaceBuildTestImpl.cs`, `RunTestTests.cs`, `ResultStatusTests.cs`) break.
  Any new computed property needs `[JsonIgnore]` (`ResultStatusTests.cs:846` checks this by reflection).
- `RunTest` is declared twice and both are live: `WorkspaceBuildTestTools.cs:50-64` and `WorkspaceTools.cs:506-520`
  (delegates to the former). A new tool parameter or description change must be made in both.
- New tool parameters go before `CancellationToken`. Because `WorkspaceBuildTestTools` forwards positionally to
  `WorkspaceBuildTestImpl.RunTest`, the impl and both tool declarations change in the same slice (Step 4).
- Compare test results to the known-failing baseline (`reference_known_failing_tests`); report only new failures.

## Steps

### Step 1 - Fields and pure projector (implementer, D0)
- Files: `RoslynSentinel.Engines.Basic/TestRunEngine.cs`, new `RoslynSentinel.Engines.Basic/TestRunResultProjector.cs`
- Change: append `string? FullResultsId = null, int OmittedResultCount = 0` to `TestRunResult` (after `ProjectSummaries`).
  Create `public static class TestRunResultProjector` with
  `public static TestRunResult Project(TestRunResult result, TestResultsFilter resultsType, bool includeOutput)`
  implementing the rules above. Pure: no I/O, no MCP types. Does not set `FullResultsId`.
- Done when: `Build` 0 errors.

### Step 2 - Projector unit tests (test-writer)
- Files: new `RoslynSentinel.Tests.Basic/TestRunResultProjectorTests.cs`
- Change: pure tests: green run unchanged except tails; message cut at 300; stack trace reduced to 2 lines / 300 chars for
  the first 10 rows and absent after; Output dropped by default and kept with `includeOutput`; tails kept when
  `RunCompleted == false` or `TotalCount == 0`; `FailureSummary` capped at 10; `OmittedResultCount` for failed/skipped/all;
  14 failed rows with realistic 600-char messages and 1 KB stack traces serialize to < 15360 bytes (mirrors
  `BuildResultProjectorTests.ProjectedFailedResult_SerializesUnder15KB`).
- Done when: the new test class passes via `RunTest` (project scope `RoslynSentinel.Tests.Basic`).

### Step 3 - Wire the projector into RunTest (implementer, D0)
- Files: `RoslynSentinel.Tools.Basic/WorkspaceBuildTestImpl.cs`
- Change: in `RunTest`, project with `includeOutput: false` on both the incomplete-run return (`:313`) and the normal return
  (`:316`); replace `WithoutTailsWhenClean(testRunResult)` use accordingly (the projector now owns tail dropping; keep the
  overload only if still referenced). Add `StoreFullTestResultAsync` mirroring `StoreFullDiagnosticsAsync` (`:211`) and set
  `FullResultsId` with `with` when anything was cut. Return the normal path through
  `SentinelCallToolResult<object>.ForPossiblyLargeDataAsync(projected, solutionRoot, "TestRunResult", ResultWrapperType.Raw,
  totalRecords: failedCount, workspaceVersion: ..., statusMessage: SummarizeTestRun(...), listSummary: ...)` then
  `with { Findings = result.Findings }`. `listSummary` = `SummarizeListResult.Build(failedRows, TestClassOf)` where
  `TestClassOf` strips the last dotted segment of `TestName`; do not use `ToSummaryMessage` (its "files" wording is wrong).
  Keep `SummarizeTestRun` (public static, `RunTestTests.cs:402` calls it) unchanged.
- Done when: `Build` 0 errors. (Existing `RunTestTests` are run in Step 5, which fixes any that assert removed fields.)
- As dispatched: `FullResultsId` is stored whenever `FailedCount > 0 && !summary` (simpler than detecting "anything cut";
  equivalent in practice because failed rows nearly always carry a stack trace). `WithoutTailsWhenClean` is kept and applied
  after projection so clean 0-test runs still drop tails.

### Step 4 - includeOutput parameter (implementer, D0)
- Files: `RoslynSentinel.Tools.Basic/WorkspaceBuildTestImpl.cs`, `RoslynSentinel.Tools.Basic/WorkspaceBuildTestTools.cs`,
  `RoslynSentinel.Tools.Basic/WorkspaceTools.cs`
- Change: add `bool includeOutput = false` before `CancellationToken` to `WorkspaceBuildTestImpl.RunTest`, `RunTestTool` in
  `WorkspaceBuildTestTools.cs` and `RunTestTool` in `WorkspaceTools.cs`, forward it, pass it to the projector in place of the
  Step 3 constant, and add the same `[Description]` to both tool declarations ("keep per-test Output and the stdout/stderr
  tails on a completed run"). Update both method descriptions to mention `FullResultsId`/`OmittedResultCount`.
- Done when: `Build` 0 errors.

### Step 5 - Tool-level tests and existing-test fixes (test-writer)
- Files: `RoslynSentinel.Tests.Tools.Basic/RunTestTests.cs`
- Change: fix any existing test that asserted a field the projection now drops (`StdoutTail` at `:136`/`:155` is the likely
  one; keep the assertion only where tails are still kept). Add: failing run returns failed test names inline with truncated
  messages and a `FullResultsId` readable by `GetLargeResult`; `includeOutput: true` keeps `StdoutTail`/`Output`;
  `OmittedResultCount > 0` when `maxDetails` is lower than the failure count.
- Done when: `RunTest` over `RoslynSentinel.Tests.Tools.Basic` shows no new failures versus baseline.

### Step 6 - Verify live
- Files: none (main session).
- Change: `Build(level: fullBuild)`, restart the server (`McpServerControl StopServer`), `LoadSolution`, re-run the RunTest from
  the problem statement and confirm the failed test names appear inline. Update this plan's status with commit hashes.
- Done when: the 14 failures are listed inline and `isServerBinaryStale` is absent.

## Out of scope
- Changing `ParseTrx` or the engine's stored values (full material stays in the engine).
- Moving `TestRunResult` into `Common` (would be a cross-project type relocation; see engine-reorg Bug 2).
- A generic projector abstraction shared by Build and RunTest.
- PlanStepRunner/ModelEval prompts that may mention RunTest JSON fields (not searched; see risks).

## Risks and open decisions
- **Full list limit:** the engine already caps `Results` at `maxDetails` (default 50) before the tool sees it, so
  `FullResultsId` recovers untruncated text for the returned rows only, not rows beyond `maxDetails`. Recovering those needs an
  engine change (return all rows) or a re-run with a larger `maxDetails`. Decision deferred.
- **Size at default maxDetails:** the projection is sized for ~14 failures (< 15360 bytes). 50 failed rows still exceed the
  threshold and will be offloaded, but `statusMessage`, `listSummary` and `totalRecords` are then populated.
- **Unchecked:** whether `GetLargeResult` pages a `TestRunResult` JSON shape; whether PlanStepRunner/ModelEval prompts or eval
  code depend on `Output`/`StdoutTail` being present on failed runs; whether generated tool docs or schema snapshots pin the
  `RunTest` description text; `WithoutTailsWhenClean(TestRunResult)` may become dead after Step 3.
- **Step 3 may RESCOPE** if more than 10 edits are needed; split the store helper out as its own slice.
