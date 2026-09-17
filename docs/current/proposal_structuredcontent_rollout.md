# Rolling out MCP StructuredContent beyond the McpServerStatus spike

## Motivation

Every RoslynSentinel MCP tool returns a free-text/plain-object result wrapped in
`ToolResult<T>` (`RoslynSentinel.Common/ToolResult.cs`), where `T` is almost always erased to
`object` at compile time. A model calling a tool cannot see the shape of what the tool will return
ahead of time - it has to infer field names and structure from prose in the tool's `[Description]`
and parameter docs, or from having seen the pairing before in training data.

This is a concrete problem for multi-step tool chaining. `Member`'s `docCommentId` parameter
expects a value that `LocateSymbol`'s response happens to produce in a field also named
`docCommentId`, but nothing declares that contract - the model has to discover or guess it from two
separate, unstructured prose descriptions. There's also a theory (unconfirmed, flagged as such
here) that some models treat an entire text-block tool response as one weakly-differentiated
signal rather than distinct structured fields, causing them to miss information by "skimming" it
rather than reading each field.

A spike already succeeded proving the mechanism works: `McpServerStatus`
(`RoslynSentinel.Server.Basic/SentinelServerStatusTools.cs:26-76`) now returns real MCP
`StructuredContent`. `RoslynSentinel.Tests.Battery/McpServerStatusStructuredContentTests.cs:63-104`
asserts the advertised `OutputSchema` is a real JSON Schema (not a bare `true` node) and that
`CallToolResult.StructuredContent` gets populated matching the text content. This proposal is the
inventory and rollout plan for extending that mechanism to the rest of the tool surface.

## The mechanism (confirmed working, not itself under proposal)

MCP C# SDK 2.2.0's `[McpServerTool]` attribute supports `UseStructuredContent = true` and
`OutputSchemaType = typeof(SomeType)`. Concretely, at `SentinelServerStatusTools.cs:26-29`:

```csharp
[McpServerTool(Name = "McpServerStatus", UseStructuredContent = true, OutputSchemaType = typeof(McpServerStatusResult))]
[Produces(DataTag.ResultOnly)]
[Description("...")]
public object McpServerStatus(CancellationToken cancellationToken = default)
{
    return new { sessionHalted = ..., breakers = new { ... }, toolSurface = new { ... }, ... };
}
```

The tool method itself is **unchanged** - it still returns a plain anonymous `object`, built the
same way every other tool builds its result today. `OutputSchemaType` points at a hand-written
parallel record (`McpServerStatusResult`, `SentinelServerStatusTools.cs:108-115`, plus three nested
records at lines 80-100: `McpServerStatusBreakerState`, `McpServerStatusBreakers`,
`McpServerStatusToolSurface`) that mirrors the anonymous object's shape field-for-field. That
record is **never constructed or returned** by the method - it exists purely so the SDK can derive
a JSON Schema to advertise to clients ahead of the call, and to populate `StructuredContent`
alongside the existing text content at call time.

## Why this does not generalize for free

This is the crux of the proposal - the mechanism working once does not mean rollout is
infrastructure work:

- Every other tool returns `Task<ToolResult<object>>` - the outer envelope
  (`ServerVersion`/`Success`/`Data`/`Error`/`Findings`/etc, see `ToolResult.cs`) is uniform and
  shared, but the inner `Data` payload's actual C# type is erased to `object` almost everywhere,
  populated at runtime with different anonymous shapes depending on code path.
- `OutputSchemaType` needs **one concrete, compile-time-committed named type per tool** (or per
  shape, for multi-shape tools). This cannot be derived generically from an anonymous object at
  runtime - it has to be hand-authored and kept in sync, the same duplication done for
  `McpServerStatus`.
- The registration layer (`RoslynSentinel.Common/McpToolSchemaFix.cs:73-103`,
  `WithToolsFixed<TToolType>()`) already has a generic hook for fixing broken *input* schemas
  (`SchemaCreateOptions`/`RewriteBrokenSchemaNode`), but `OutputSchemaType` is a compile-time
  attribute argument resolved directly by the SDK per-method - there is no equivalent dynamic
  output-schema hook at the registration layer, and none is proposed here. Each tool needs its own
  attribute argument and a matching hand-authored type, one tool (or shape) at a time.
- **Conclusion:** this is a per-tool authoring effort, not an infrastructure change. The
  registration call site itself (`WithToolsFixed<T>`) needs no change to support rollout.

## Scope of this inventory

Covers every tool in `RoslynSentinel.Server.Basic`, plus `RoslynSentinel.Server.Advanced`'s
`SentinelAdvancedRefactoringTools` class specifically. All other Advanced tool classes are
explicitly deferred to a later phase and appear below as a name-only list - no shape investigation
was done for them. This split was a direct scope decision: the remaining Advanced tools are rarely
used and can wait; they're listed here only for visibility.

## Cross-cutting findings

These matter for prioritization more than any single row in the per-tool tables below.

- **`AppliedChangeSummary` is by far the most common `Data` payload** across mutating tools
  (`RenameSymbol`, `SyncTypeAndFilename`, `GenerateMapping`, `ModifyEnum`, `ChangeAccessibility`,
  `ExtractLocalVariable`, `ModifyModifier`/Batch, `ModifyBaseType`/Batch, `ConvertAnonymousToNamed`,
  `InlineClass`, `IntroduceParameterObject`, `Introduce`, `Inline`, `MoveType`'s `outerScope` branch,
  etc). A single shared `OutputSchemaType = typeof(AppliedChangeSummary)` could cover a large
  fraction of tools in one move, **provided** their `autoStage=false` escape hatches are handled
  separately (see next point).
- **`MemberChangedContentResult`** (used via `ForPossiblyLargeDataAsync`) is the second most common
  shared shape (`Member`'s `addMember` paths, `MethodSignature`, `UsingDirective`'s add,
  `SummaryComment`'s add, `ConstructorParameter`'s add, `ModifyAttribute`'s add/replace) - another
  strong shared-schema candidate, but each has an inline-vs-`LargeResult`-offload duality to design
  around (the same value can come back inline or as a pointer into `GetLargeResult`, and both need
  to satisfy one schema or the schema needs to model both).
- **`ToJsonSummary()`** (the non-`autoStage` escape hatch) appears on nearly every structural-edit
  tool (`Member`, `MethodSignature`, `UsingDirective`, `ModifyEnum`, `SummaryComment`,
  `ConstructorParameter`, `ModifyAttribute`, `ModifyModifier`, `ModifyBaseType`, `WrapRange`) and
  returns a distinct raw-`DocumentEditResult`-derived shape whenever `autoStage=false` - this is a
  second schema needed per tool unless `autoStage=false` is deliberately excluded from structured-
  content support. **Recommendation, not a decision:** exclude `autoStage=false` from
  `OutputSchemaType` coverage as the pragmatic default and revisit later if it proves worth the
  extra schema per tool.
- **`SentinelGitTools` already has the most 1:1 mapping** between named result records and
  operations (`GitStatusResult`, `GitLogResult`, etc. all pre-exist as named records) - good
  low-effort candidate once the `Git` tool's single-entry-point erasure is addressed (see defect
  below).
- **`SentinelAdminTools`** (all 4 methods) and most of `SentinelSymbolTools`'s single-shape tools are
  the cheapest wins - already-scalar or already-single-shape returns.

## Known defect to flag (out of scope for this proposal, must be named)

`SentinelGitTools.Git` (`SentinelGitTools.cs:366-444`) has 12 branches
(status/log/diff/stage/unstage/commit/revert/branch/checkout/push/fetch/pull), each dispatching to
a private async worker that already returns a concretely-named record (`GitStatusResult`,
`GitLogResult`, etc. - 12 total, all pre-existing). But the switch expression at the `Git` method
boundary (`SentinelGitTools.cs:428-443`) infers its common type as `object` - there's an explicit
`(object)new { Success = false, Error = ... }` cast in the default arm (line 442) - so even though
the 12 named types already exist, `Git` erases all of them before they reach the caller. Also worth
noting precisely: `Git`'s own declared return type is `Task<object>` (line 369), not
`Task<ToolResult<object>>` like the rest of the surface - it doesn't go through the shared envelope
at all.

This needs either a discriminated-union `OutputSchemaType` or splitting `Git` into per-operation
methods before `OutputSchemaType` can be attached cleanly. Flagged as a **prerequisite refactor**,
not something the attribute alone fixes - out of scope for this proposal.

## Open verification items (blocking, not resolved - do not trust "Uncertain" effort tags until these are answered)

1. **`SentinelWorkspaceTools` vs `WorkspaceReadNavigationTools` duplicate tool names.**
   `GetMethodSource`, `GetFileOutline`, `ListAll`, `SearchSolutionText`, `GetOperationDetail`, and
   `GetLargeResult` all exist under both class names. Which copy is actually registered/active in
   DI / `ToolClassRegistry` was not resolved this session. Do not commit effort estimates for either
   copy until this is resolved.
2. **`WorkspaceReadNavigationImpl.cs` was never opened this session.** The real shape/branch count
   for the six tools above lives there, unconfirmed.
3. **`MoveAllTypesToFilesCore`** (`SentinelAdvancedRefactoringTools.cs:339-384`), the private helper
   shared by all three `MoveAllTypesToFiles` scope branches, was not independently read - its shape
   count is inferred (Moderate), not confirmed.
4. **`ExtractMethodSafe`'s non-`autoStage` branch** returns the raw `_msToolAugmentEngine` result
   type directly (not `AppliedChangeSummary`) - worth confirming that engine result's own shape
   stability before treating it as an `OutputSchemaType` candidate.

## Per-tool inventory

Effort legend: **Trivial** (0-1 shapes, simple/already concrete) / **Moderate** (2-3 shapes, one
hand-authored type needed) / **Complex** (4+ shapes or nested/dynamic shapes, discriminated union
needed) / **Uncertain** (needs verification first, do not commit a tag).

### `RoslynSentinel.Server.Basic/SentinelWorkspaceTools.cs`

| Tool | file:line | Return type | Shape count + note | Effort |
|---|---|---|---|---|
| Features | SentinelWorkspaceTools.cs:94-139 | Task<ToolResult<object>> | 3 shapes: list/get/update anonymous dicts | Moderate |
| ListSolutionItems | SentinelWorkspaceTools.cs:146-326 | Task<ToolResult<object>> | many: multiple list-item shapes + ForPossiblyLargeDataAsync offload duality | Complex |
| ListWorkspaceSolutions | SentinelWorkspaceTools.cs:335-409 | Task<ToolResult<object>> | 1-2 shapes (list result) | Trivial |
| LoadSolution | SentinelWorkspaceTools.cs:413-481 | Task<ToolResult<object>> | 2-3 shapes (success/error/diagnostics) | Moderate |
| ReplaceSnippet | SentinelWorkspaceTools.cs:569-774 | Task<ToolResult<object>> | many: match-failure diagnostics, applied summary, dry-run preview | Complex |
| CreateFile | SentinelWorkspaceTools.cs:1016-1131 | Task<ToolResult<object>> | 2-3 shapes | Moderate |
| ReadFile | SentinelWorkspaceTools.cs:1893-2027 | Task<ToolResult<object>> | 3 shapes: line-range slice, full-text-inline, full-text-offloaded-to-LargeResult | Complex |
| RetryFailedChanges | SentinelWorkspaceTools.cs:1407-1432 | Task<ToolResult<object>> | 1-2 shapes | Trivial |
| GetDiagnostics | SentinelWorkspaceTools.cs:1484-1588 | Task<ToolResult<object>> | many: per-severity/per-scope diagnostic lists | Complex |
| Build | SentinelWorkspaceTools.cs:1589-1627 | Task<ToolResult<object>> | 2+ shapes + ForPossiblyLargeDataAsync offload duality | Moderate |
| RunTest | SentinelWorkspaceTools.cs:1629-1670 | Task<ToolResult<object>> | 2-3 shapes | Moderate |
| SafeDeleteUnusedSymbol | SentinelWorkspaceTools.cs:1676-1790 | Task<ToolResult<object>> | 2-3 shapes (usages-found vs deleted) | Moderate |
| CreateProject | SentinelWorkspaceTools.cs:1792-1818 | Task<ToolResult<object>> | 1 shape | Trivial |
| SplitProjectByFolder | SentinelWorkspaceTools.cs:1820-1846 | Task<ToolResult<object>> | 1-2 shapes | Trivial |
| UndoLastApply | SentinelWorkspaceTools.cs:2079-2203 | Task<ToolResult<object>> | 2-3 shapes | Moderate |
| GetWorkspaceHealth | SentinelWorkspaceTools.cs:2248-2292 | Task<ToolResult<object>> | 1-2 shapes | Trivial |
| ListProjectFrameworkTargets | SentinelWorkspaceTools.cs:2294-2320 | Task<ToolResult<object>> | 1 shape | Trivial |
| GetLargeResult | SentinelWorkspaceTools.cs:2324-2343 | delegates to _readNav.GetLargeResult | needs verification (duplicate-name concern) | Uncertain |
| GetMethodSource / GetFileOutline / ListAll / SearchSolutionText / GetOperationDetail | SentinelWorkspaceTools.cs:1881-1891, 2029-2078 | presumed Task<ToolResult<object>> | not independently confirmed; duplicate-name concern with WorkspaceReadNavigationTools.cs | Uncertain |

### `RoslynSentinel.Server.Basic/WorkspaceReadNavigationTools.cs`

| Tool | file:line | Return type | Shape count + note | Effort |
|---|---|---|---|---|
| GetMethodSource | WorkspaceReadNavigationTools.cs:33-42 | Task<ToolResult<object>> (delegate to _impl) | depends on WorkspaceReadNavigationImpl (not read) | Uncertain |
| GetFileOutline | WorkspaceReadNavigationTools.cs:44-51 | Task<ToolResult<object>> (delegate) | same caveat | Uncertain |
| ListAll | WorkspaceReadNavigationTools.cs:53-63 | Task<ToolResult<object>> (delegate) | same caveat | Uncertain |
| SearchSolutionText | WorkspaceReadNavigationTools.cs:64-77 | Task<ToolResult<object>> (delegate) | same caveat | Uncertain |
| GetOperationDetail | WorkspaceReadNavigationTools.cs:78-91 | Task<ToolResult<object>> (delegate) | same caveat | Uncertain |
| GetLargeResult | WorkspaceReadNavigationTools.cs:92-108 | Task<ToolResult<object>> (delegate) | same caveat | Uncertain |

### `RoslynSentinel.Server.Basic/SentinelDocumentationTools.cs`

| Tool | file:line | Return type | Shape count + note | Effort |
|---|---|---|---|---|
| ProjectDoc | SentinelDocumentationTools.cs:344-483 | `object` (bare - no Task, no ToolResult<T> wrapper) | 3 shapes, but all already concrete named types in-file: DocReadResult, DocWriteResult, DocListResult (lines 13-68) | Trivial - architecturally distinct, needs its own OutputSchema wiring approach since it bypasses the ToolResult<T> convention entirely |

### `RoslynSentinel.Server.Basic/SentinelSymbolTools.cs`

| Tool | file:line | Return type | Shape count + note | Effort |
|---|---|---|---|---|
| LocateSymbol | SentinelSymbolTools.cs:71-125 | Task<ToolResult<object>> | 1 main shape (list result) | Trivial |
| InspectSymbol | SentinelSymbolTools.cs:126-192 | Task<ToolResult<object>> | 2 shapes: info vs blastRadius | Moderate |
| QuerySymbolRelationships | SentinelSymbolTools.cs:226-325 | Task<ToolResult<object>> | 3 shapes: direct list (+ LargeResult offload), broadened dict-by-kind, empty-with-warning | Complex |
| GetBestInsertionPoint | SentinelSymbolTools.cs:326-359 | Task<ToolResult<object>> | 1 shape | Trivial |
| PreviewRenameImpact | SentinelSymbolTools.cs:360-399 | Task<ToolResult<object>> | 1 shape | Trivial |
| FindReferences | SentinelSymbolTools.cs:400-463 | Task<ToolResult<object>> | 3 shapes by kind: callers-only, implementations-only, combined anonymous | Moderate |
| GetTypeInfo | SentinelSymbolTools.cs:464-544 | Task<ToolResult<object>> | 3 shapes by include: hierarchy-only (TypeHierarchyReport), members-only (List<TypeMemberDetail>), combo anonymous | Moderate |

### `RoslynSentinel.Server.Basic/SentinelGitTools.cs`

| Tool | file:line | Return type | Shape count + note | Effort |
|---|---|---|---|---|
| Git | SentinelGitTools.cs:366-444 | Task<object> (not ToolResult<T> at all) | 12 branches, each dispatching to a private worker already returning a concretely-named record (GitStatusResult/GitLogResult/etc, 12 total pre-existing) but erased to object at the Git method boundary via a switch expression with an explicit (object) cast in the default arm | Complex - prerequisite refactor needed (discriminated union or per-operation split) before OutputSchemaType fits; the 12 named types already existing is a major head start once that refactor happens |

### `RoslynSentinel.Server.Basic/SentinelAdminTools.cs`

| Tool | file:line | Return type | Shape count + note | Effort |
|---|---|---|---|---|
| ListExternalDiskChanges | SentinelAdminTools.cs:26-35 | List<string> | 1 shape, already concrete | Trivial |
| IsSessionHalted | SentinelAdminTools.cs:37-46 | bool | 1 shape, already concrete (scalar) | Trivial |
| AcknowledgeExternalFileChanges | SentinelAdminTools.cs:48-63 | string | 1 shape, already concrete (scalar) | Trivial |
| McpServerControl | SentinelAdminTools.cs:67-106 | string | 1 shape, already concrete (scalar) | Trivial |

### `RoslynSentinel.Server.Basic/SentinelWholeFileWriteTools.cs`

| Tool | file:line | Return type | Shape count + note | Effort |
|---|---|---|---|---|
| WriteFile | SentinelWholeFileWriteTools.cs:30-109 | Task<ToolResult<object>> | 2-3 shapes (invalid-arg errors, compiler-error-blocked, success strippedResult/{result, diff}) | Moderate |
| DeleteFile | SentinelWholeFileWriteTools.cs:111-160 | Task<ToolResult<object>> | 2 shapes (error, success strippedResult) | Moderate |
| ApplyDiff | SentinelWholeFileWriteTools.cs:292-549 | Task<ToolResult<object>> | many: files-mode apply/validate x diff-mode apply/validate, each with distinct sub-shapes | Complex |
| ApplyUnifiedDiff | SentinelWholeFileWriteTools.cs:561-685 | Task<ToolResult<object>> | 2-3 shapes (apply success w/ diff report findings, validate result) | Moderate |

### `RoslynSentinel.Server.Basic/SentinelRefactoringTools.cs`

| Tool | file:line | Return type | Shape count + note | Effort |
|---|---|---|---|---|
| RenameSymbol | :451-538 | Task<ToolResult<object>> | 1 shape: rich anonymous object (changeId/oldName/newName/updatedHandle/residualMentions) | Moderate |
| Member | :580-937 | Task<ToolResult<object>> | many: 5 operations (addMember/addTopLevelType/addTypedMember/remove/replace/view), largest/most complex method in file | Complex |
| MethodSignature | :1310-1417 | Task<ToolResult<object>> | 3 shapes: view ({Parameters}), non-autoStage (ToJsonSummary), applied (MemberChangedContentResult via ForPossiblyLargeDataAsync) | Complex |
| SyncTypeAndFilename | :1859-1929 | Task<ToolResult<object>> | 2-3 shapes (AppliedChangeSummary variants, dryRun vs applied) | Moderate |
| ModifyModifierBatch (private helper, reached via ModifyModifier) | :175-259 | Task<ToolResult<object>> | 1 shape (AppliedChangeSummary) | Trivial |
| ModifyAttributeBatch (private) | :263-356 | Task<ToolResult<object>> | 1 shape | Trivial |
| ModifyBaseTypeBatch (private) | :360-448 | Task<ToolResult<object>> | 1 shape | Trivial |
| GenerateMapping | :540-573 | Task<ToolResult<object>> | 1 shape (AppliedChangeSummary) | Trivial |
| UsingDirective | :938-1027 | Task<ToolResult<object>> | 3 shapes: view ({Usings}), remove (AppliedChangeSummary), add (MemberChangedContentResult offload) | Complex |
| ModifyEnum | :1028-1075 | Task<ToolResult<object>> | 1 shape | Trivial |
| ChangeAccessibility | :1077-1126 | Task<ToolResult<object>> | 1 shape | Trivial |
| SummaryComment | :1127-1203 | Task<ToolResult<object>> | 3 shapes: view ({SummaryText}), remove (AppliedChangeSummary), add (MemberChangedContentResult offload) | Complex |
| ConstructorParameter | :1204-1309 | Task<ToolResult<object>> | 3 shapes: view ({Parameters}), non-autoStage, applied (MemberChangedContentResult offload) | Complex |
| ExtractLocalVariable | :1419-1467 | Task<ToolResult<object>> | 1 shape (AppliedChangeSummary) | Trivial |
| ExtractMethodSafe | :1469-1544 | Task<ToolResult<object>> | 2 shapes (non-autoStage raw engine result vs AppliedChangeSummary) | Moderate |
| ModifyAttribute | :1545-1664 | Task<ToolResult<object>> | 4+ shapes: batch-delegate, view-less add/replace (MemberChangedContentResult offload), remove (AppliedChangeSummary), non-autoStage | Complex |
| ModifyModifier | :1666-1761 | Task<ToolResult<object>> | 3 shapes: batch-delegate, non-autoStage, applied (AppliedChangeSummary) | Moderate |
| ModifyBaseType | :1762-1857 | Task<ToolResult<object>> | 3 shapes: batch-delegate, non-autoStage, applied (AppliedChangeSummary) | Moderate |

`RoslynSentinel.Server.Basic/SentinelServerStatusTools.cs` - `McpServerStatus` already covered
above as the working spike, not re-listed as a to-do item.

### `RoslynSentinel.Server.Advanced/SentinelAdvancedRefactoringTools.cs` (all 13 tool methods)

| Tool | file:line | Return type | Shape count + note | Effort |
|---|---|---|---|---|
| ChangeSignature | :146-207 | Task<ToolResult<object>> | 2-3 shapes (non-autoStage {Changes, SkippedCallSites}, applied AppliedChangeSummary w/ skipped-call-site warning text) | Moderate |
| ConvertAnonymousToNamed | :209-243 | Task<ToolResult<object>> | 1 shape (AppliedChangeSummary) | Trivial |
| InlineClass | :245-276 | Task<ToolResult<object>> | 1 shape | Trivial |
| MoveAllTypesToFiles | :278-337 (+ MoveAllTypesToFilesCore :339-384, not independently read) | Task<ToolResult<object>> | 3 scope branches (file/project/solution) delegating to shared MoveAllTypesToFilesCore - shape count depends on that helper, not independently confirmed | Uncertain (likely Moderate) |
| InvertAssignments | :386-443 | Task<ToolResult<object>> | 2 code-path branches (snippet-based vs line-range-based), same output shape (AppliedChangeSummary) - effectively 1 output shape | Trivial |
| MoveMember | :445-502 | Task<ToolResult<object>> | 2 shapes: non-autoStage {Changes, SkippedCallSites}, applied AppliedChangeSummary | Moderate |
| IntroduceParameterObject | :504-552 | Task<ToolResult<object>> | 1 shape | Trivial |
| Introduce | :554-626 | Task<ToolResult<object>> | 1 shape (AppliedChangeSummary, all 4 newType branches converge to same shape) | Trivial |
| ExtractMembers | :635-727 | Task<ToolResult<object>> | 3 branches (interface/partialClass/superclass), each with non-autoStage raw + applied variants - effectively 2-3 distinct shapes | Moderate |
| SyncInterface | :729-812 | Task<ToolResult<object>> | 3 shapes: implement/sync (AppliedChangeSummary), verify (raw engine result, different type) | Complex |
| Inline | :814-897 | Task<ToolResult<object>> | 1 shape (AppliedChangeSummary, all 4 kind branches converge) | Trivial |
| WrapRange | :899-1055 | Task<ToolResult<object>> | 1 shape (AppliedChangeSummary/ToJsonSummary, both snippet- and line-based paths converge across 3 wrapper types) | Moderate (converges in output shape but has 6 code paths worth reviewing for consistency) |
| MoveType | :1057-1133 | Task<ToolResult<object>> | 2 shapes: ownFile (rich anonymous object w/ ContentPreviews), outerScope (AppliedChangeSummary) | Moderate |

### Deferred Advanced tool classes (name + rough method count only, not investigated - later phase)

Per direct scope decision: these are rarely used, listed here for visibility only, no shape
investigation performed this session.

- SentinelCommentingTools.cs (373 lines) - ~1-2 methods (BulkComment)
- SentinelGenerationTools.cs (225 lines) - ~4 methods
- SentinelIntelligenceTools.cs (361 lines) - ~7 methods
- SentinelModernizationTools.cs (94 lines) - 1 method (InvertBooleanLogic)
- SentinelQualityTools.cs (333 lines) - ~8 methods
- SentinelCodemodTools.cs (1501 lines) - ~4 tool methods
- SentinelScanTools.cs (973 lines) - ~6 tool methods
- SentinelAsyncifyTools.cs (3685 lines, largest Advanced file) - ~16 tool methods
- RoslynSentinelTaskTools.cs (32 lines) - 1 method (SelectExecutionMode, likely infrastructure/
  dispatch, not a typical data tool)

## Proposed phasing (recommendation only - not a decision)

Only the *scope* of this inventory (which tools got investigated) was actually decided by the user.
Everything below is a recommendation grounded in the cross-cutting findings, for the reader to
accept or reorder.

- **Phase 1 - cheapest wins.** `SentinelAdminTools` (all 4, already-scalar), `SentinelSymbolTools`'s
  single-shape tools (`LocateSymbol`, `GetBestInsertionPoint`, `PreviewRenameImpact`),
  `SentinelDocumentationTools.ProjectDoc` (already has 3 named types, just needs its own
  OutputSchema wiring since it bypasses `ToolResult<T>`). `McpServerStatus` is already done and not
  part of this phase's remaining work.
- **Phase 2 - the `AppliedChangeSummary` shared-schema play.** One `OutputSchemaType` covering the
  large set of mutating tools that already converge on `AppliedChangeSummary` for their applied-edit
  path (see Cross-cutting findings above for the list). Explicitly scopes out `autoStage=false`
  responses per the recommendation above - those keep returning unstructured content until a
  separate pass decides they're worth a second schema.
- **Phase 3 - the `MemberChangedContentResult` shared-schema play.** Same idea, for the
  `ForPossiblyLargeDataAsync`-backed tools (`Member`'s addMember paths, `MethodSignature`,
  `UsingDirective`'s add, `SummaryComment`'s add, `ConstructorParameter`'s add, `ModifyAttribute`'s
  add/replace). Needs a design decision on how to represent the inline-vs-`LargeResult`-offload
  duality in one schema before starting.
- **Phase 4 - the Complex/multi-shape tools.** `Member`, `ReplaceSnippet`, `ApplyDiff`,
  `GetDiagnostics`, `ListSolutionItems`, `Git` (blocked on its erasure-refactor prerequisite above),
  and the rest of the Complex-tagged rows. Each likely needs its own discriminated-union design, not
  a shared type - highest effort, do last.

**Before trusting any Phase 1 effort estimate for the Uncertain-tagged `SentinelWorkspaceTools` /
`WorkspaceReadNavigationTools` rows**, resolve the open verification items above (which class is
actually registered, and what `WorkspaceReadNavigationImpl.cs` actually returns). Estimating a
duplicate-named tool's schema effort from the wrong class would misprice that tool's whole entry in
whichever phase it lands in.

## Cost / risk

- Every `OutputSchemaType` record added is a second place a tool's shape has to be kept in sync by
  hand - drift between the anonymous object actually returned and the named record advertised as
  its schema is a new, silent failure mode this rollout introduces (the schema would validate fine,
  clients would trust it, and it could still be wrong). Worth a lint/test convention (e.g. one
  reflection-based structural-equality test per tool, mirroring
  `McpServerStatusStructuredContentTests.cs`) rather than relying on authors remembering to update
  both sides.
- Touches only `Server.Basic`/`Server.Advanced` tool-attribute declarations and adds new record
  types alongside them - does not touch `ToolResult<T>`, `McpToolSchemaFix.cs`, or the
  registration call sites (`WithToolsFixed<T>`), so this is additive and low-risk to the existing
  surface. Nothing about existing tool behavior changes; only the advertised schema and the
  populated `StructuredContent` field are new.
- Real cost is authoring time and shape-count risk on the Complex-tagged tools (`Member`,
  `ReplaceSnippet`, `ApplyDiff`, `GetDiagnostics`, etc.) - those may need a discriminated-union
  schema, which the MCP C# SDK's `OutputSchemaType` mechanism has not been proven against in this
  repo yet (only single-shape `McpServerStatus` has been tried). Phase 4 should include a small
  spike on one Complex tool before committing effort estimates for the rest of that phase.

## Status

Proposal only - inventory and phasing recommendation, not yet implemented beyond the
`McpServerStatus` spike. Author: Claude (Sonnet 5), for Andrew Almond
(andrew.almond@clearbridge.ca).
