---
name: project-codebase-consistency-audit-v1
description: "Survey-pass audit of internal (non-Roslyn-API) duplication/inconsistency across RoslynSentinel, per docs/current/plan-codebase-consistency-review-v1.md"
metadata: 
  node_type: memory
  type: project
  originSessionId: 07a780d3-d133-41fc-8b23-27a7537d6a25
  modified: 2026-08-26T04:33:28.882Z
---

Ran 2026-08-25 as 7 parallel research passes, one per scope area in
`docs/current/plan-codebase-consistency-review-v1.md`. Full findings table:
`docs/current/codebase-consistency-audit-v1.md`. Survey pass found findings only; items 3-6 fixed
first (see [[project_write_path_chokepoint_unified]] and consistency-audit-items-3-6 commit), then
items 1, 2, 7 fixed 2026-08-25 (this entry updated accordingly). All 7 items now `fixed`.

**Headline findings:**

1. **Test-solution construction was worse than the known seed example — fixed.** [[project_deferred_cleanup_todos]]
   named 4 patterns; this pass found 7. `TestSolutionBuilder` is the de facto standard (452 call
   sites/82 files). `AsyncifyTestHelper` was a near-dead fork — used in exactly 1 file — **deleted**;
   its sole caller now calls `TestSolutionBuilder.CreateSolutionWithProject` directly. The two inline
   `AdhocWorkspace` builders (`ComprehensiveToolTests.CreateSolution`,
   `NamespacePathMismatchTests.CreateSolutionWithAbsolutePaths`) are now thin wrappers delegating to
   `TestSolutionBuilder`'s two overloads — no call-site changes needed in either file.
   `ContosoOrders.Tests`'s xUnit-vs-NUnit split is a separate axis (test framework, not
   solution-construction) and was left alone.
2. **DI registration (Basic/Advanced) is clean — comment added.** No true duplicate registrations,
   lifetimes all Singleton, Advanced correctly delegates to Basic. Added a `<remarks>` doc comment
   on `AddRoslynSentinelEnginesBasic` pointing at Advanced's live registrations, so a future editor
   doesn't uncomment one of the ~25 commented-out engine lines in Basic blind (shadow-list drift risk).
3. **A third hand-rolled diff implementation found** — beyond the known `RefactoringEngine.ComputeRenameHunks`
   vs `DiffEngine.CreateDiff` duplication (already deferred), `RefactoringEngine.ComputeFormatHunks`
   (used by `FormatDocumentPreviewAsync`) is a third, independently-diverging diff algorithm that
   also merges adjacent changed lines into ranges — a shape `CreateDiff` doesn't produce.
4. **Real write-chokepoint bypass found**: `OutParamRefactoringEngine.ConvertOutParamsToValueTupleAsync`
   calls raw `workspace.TryApplyChanges(...)` instead of `ApplyProposedChangesAsync`/`ValidateAndApplyAsync`
   — reachable live from the `ApplyClassCodemod` MCP tool (`convert_out_params_to_value_tuple` case).
   Loses drift detection, undo capture, and rollback. Not listed in
   `docs/current/reference-code-file-write-paths-v1.md` — that doc needs an update once fixed.
5. **Error-mapping is inconsistent**: of ~14 tool classes with MCP tool methods, ~8 use
   `ToolErrorMapper` consistently, 4 partially hand-roll `ResultError` in specific catches, and 2
   (`GitTools`, `DocumentationTools`) skip the shared envelope entirely with their own bespoke
   result types. `SentinelWorkspaceTools.Features` returns a bare anonymous object on error,
   breaking the `ToolResult<T>` contract outright.
6. **`ItemRecordOutcome` JSON gotcha ([[project_operation_blob_json_gotchas]]) confirmed still live** —
   still missing `[JsonConverter(JsonStringEnumConverter)]` that its sibling enums in the same file
   (`ItemOutcome`, `OperationOutcome`) both have. Broader pattern: ~19 independently-constructed
   `JsonSerializerOptions` instances solution-wide, no shared canonical instance/helper.
7. **Engine construction pattern itself is clean** (all DI-singleton, constructor-injected, zero
   production `new Engine(...)` calls) but 4 fully-built engine classes were wired up nowhere:
   `UniversalRefactoringLibrary`, `ExhaustiveAnalyzerEngine`, `MassiveAnalyzerEngine`,
   `CodeSmellAndStyleEngine`. **Deleted, not wired up** — deeper investigation (git history,
   reference search, redundancy check) found all 4 were superseded, not merely forgotten:
   `UniversalRefactoringLibrary` is a self-admitted stub ("simulation mode"), zero test coverage;
   `ExhaustiveAnalyzerEngine`/`MassiveAnalyzerEngine` are byte-identical duplicates of each other and
   both redundant with the registered `DiagnosticEngine.GetFileDiagnosticsAsync`;
   `CodeSmellAndStyleEngine`'s working method duplicates `SyntaxUpgradeEngine`'s switch-expression
   transform, and its other method only returns results for 1 of 3 documented rules. Removed the 4
   production files plus their Battery-suite unit tests (kept unrelated tests in the same files:
   `BatteryFiveTests.cs`, `BatterySixteenTests.cs`, `BatterySeventeenTests.cs`,
   `BatteryTwentyNineTests.cs`). User confirmed deletion via AskUserQuestion before removing
   production code. Solution builds 0 errors; Battery/Advanced/Basic test projects pass (Asyncify
   has 1 pre-existing flaky test, `T19_LargeResult_Message_ContainsGetScanResult_AndOperationId` in
   `MigrationScanResultTests.cs`, unrelated — fails only under full-suite run, passes isolated,
   commented inline per [[feedback_comment_suspected_flaky_tests]]).

**How to apply:** items 1, 2, 7 are done (this session, 2026-08-25). Items 3-6 were already fixed
in an earlier session (see commit "Fix consistency-audit items 3-6"). All 7 audit findings are now
resolved — `docs/current/codebase-consistency-audit-v1.md` reflects `fixed` for every row.
