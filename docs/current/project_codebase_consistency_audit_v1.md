---
name: project-codebase-consistency-audit-v1
description: "Survey-pass audit of internal (non-Roslyn-API) duplication/inconsistency across RoslynSentinel, per docs/current/plan-codebase-consistency-review-v1.md"
metadata: 
  node_type: memory
  type: project
  originSessionId: 07a780d3-d133-41fc-8b23-27a7537d6a25
  modified: 2026-08-26T03:21:26.775Z
---

Ran 2026-08-25 as 7 parallel research passes, one per scope area in
`docs/current/plan-codebase-consistency-review-v1.md`. Full findings table:
`docs/current/codebase-consistency-audit-v1.md`. No fixes applied — survey only, same convention
as `roslyn-duplication-audit-v1.md`.

**Headline findings (all filed as `todo`, not fixed):**

1. **Test-solution construction is worse than the known seed example** — [[project_deferred_cleanup_todos]]
   named 4 patterns; this pass found 7. `TestSolutionBuilder` is the de facto standard (452 call
   sites/82 files). `AsyncifyTestHelper` is a near-dead fork — used in exactly 1 file, even its own
   project's other 3 test files bypass it for `TestSolutionBuilder`. Two more inline `AdhocWorkspace`
   builders duplicate it privately (`ComprehensiveToolTests.CreateSolution`,
   `NamespacePathMismatchTests.CreateSolutionWithAbsolutePaths`). `ContosoOrders.Tests` is also the
   only xUnit project in an otherwise-NUnit estate.
2. **DI registration (Basic/Advanced) is clean** — no true duplicate registrations, lifetimes all
   Singleton, Advanced correctly delegates to Basic. Only risk: ~25 commented-out engine
   registrations in Basic that Advanced live-registers — a "shadow list" drift risk if uncommented
   blind later.
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
   production `new Engine(...)` calls) but 4 fully-built engine classes are wired up nowhere:
   `UniversalRefactoringLibrary`, `ExhaustiveAnalyzerEngine`, `MassiveAnalyzerEngine`,
   `CodeSmellAndStyleEngine` — dead code or forgotten registrations, undiagnosable from static
   analysis alone.

**How to apply:** these are backlog items, not urgent bugs. When next touching any of the named
files/areas, check this audit first rather than re-discovering the same inconsistency. Don't
opportunistically fix mid-task — raise as its own scoped session first, same rule as
[[project_deferred_cleanup_todos]].
