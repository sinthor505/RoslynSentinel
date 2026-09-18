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

**Note on current state (2026-09-17):** since this proposal was first drafted, `OutputSchemaType`
has also been wired onto `LocateSymbol`, `RenameSymbol`, `MethodSignature`, and `ModifyModifier` as
further POC points (`SentinelSymbolTools.cs:71`, `SentinelRefactoringTools.cs:451`,
`SentinelRefactoringTools.cs:1313`, `SentinelRefactoringTools.cs:1669`) - so "not yet implemented
beyond the McpServerStatus spike" in the rest of this doc's prose is now stale as a literal claim.
It is left as-is below (this revision's job is to fold in a design discussion, not re-audit every
sentence for drift) but the reader should treat those four tools as additional live precedent, not
still-hypothetical Phase-1/2 candidates. The `ModifyModifier` case in particular is the direct
source for the `autoStage` finding below - see `ModifyModifierResultEnvelope`
(`SentinelRefactoringTools.cs:1980-1982`), which only models the `autoStage=true` branch
(`AppliedChangeSummary`) and deliberately leaves the `autoStage=false` branch out of the schema.

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

### Worked example: what the tags actually look like on the wire

Captured live (2026-09-17) from `LocateSymbol`'s actual `outputSchema`, via a hand-driven MCP
`tools/list` call against the built server. This confirms `x-produces-tag` (and, by the same
mechanism, `x-consumes-tag`) appear as a sibling of `"type"` directly on the property node, with a
single string value naming the `DataTag`:

```json
"data": {
  "type": ["array", "null"],
  "items": {
    "type": "object",
    "properties": {
      "docCommentId": {
        "type": ["string", "null"],
        "x-produces-tag": "DocCommentId"
      },
      "filePath": {
        "type": ["string", "null"],
        "x-produces-tag": "SourceFilepath"
      },
      "fullyQualifiedName": {
        "type": "string"
      }
    }
  }
}
```

Open item this raises (not resolved here, flagged for a future decision): this schema fragment
lives three levels deep inside `ToolResult<object>`'s outer envelope - the real top-level shape is
`success`/`data`/`totalRecords`/`workspaceVersion`/etc, and only `data`'s inner shape has any tag
coverage right now. The envelope's own top-level fields (`totalRecords`, `workspaceVersion`, and
friends) carry no tags at all today. Andrew raised this directly: the failure mode he is worried
about is a model skimming past *metadata or the message*, not past `data` itself ("the issue the
model skims past is the metadata or message, not the 'data'"). Whether `OutputSchemaType` schemas
should eventually cover envelope-level fields too, and not just `Data`'s inner shape, is an open
question this doc surfaces but does not answer.

### `[Produces]`/`[Consumes]` resolution differs by attribute placement

Two different resolution paths exist for how a `DataTag` attribute turns into an `x-produces-tag`/
`x-consumes-tag` schema annotation, and they matter for anyone extending tag coverage:

- **On a DTO/record property** (e.g. `LocateSymbolResult`'s `DocCommentId` property above):
  resolves natively via the SDK's own `AIJsonSchemaCreateContext.PropertyAttributeProvider`,
  consumed inside `RewriteBrokenSchemaNode`'s `TransformSchemaNode` callback in
  `McpToolSchemaPatcher.cs:50`. This is why `x-produces-tag` already worked before today's
  `ApplyConsumesTags` fix landed - no patch was needed for record/DTO properties, the SDK threads
  the attribute provider through on its own.
- **On a tool method's bare top-level parameter:** needed today's new `ApplyConsumesTags` post-hoc
  patch (`McpToolSchemaPatcher.cs:195-234`, wired into `WithSentinelTools<T>()`), because the SDK's
  schema-creation context never threads a method's own `ParameterInfo` through for top-level
  parameters - so `[Consumes]` on a parameter like `ModifyModifier`'s `filepath` silently produced
  no tag at all until this patch was added.
- **Unresolved, flagged as an open follow-up, not yet investigated:** whether a bare
  *method-level* `[Produces(DataTag.X)]` - tagging the whole method's return value rather than a
  DTO field, as `Git` already does today (`SentinelGitTools.cs:367`,
  `[Produces(DataTag.Report)]`) - actually surfaces anywhere in the emitted output schema, or
  silently gets dropped the same way method-level `[Consumes]` did before the `ApplyConsumesTags`
  fix. This needs the same kind of live-schema verification done for `LocateSymbol` above. Do not
  assume either answer until it's checked.

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
- The registration layer (`RoslynSentinel.Common/McpToolSchemaPatcher.cs:43-176`,
  `WithSentinelTools<TToolType>()` - renamed this session from `McpToolSchemaFix.cs`/
  `WithToolsFixed<T>()`) already has a generic hook for fixing broken *input* schemas
  (`SchemaCreateOptions`/`RewriteBrokenSchemaNode`), but `OutputSchemaType` is a compile-time
  attribute argument resolved directly by the SDK per-method - there is no equivalent dynamic
  output-schema hook at the registration layer, and none is proposed here. Each tool needs its own
  attribute argument and a matching hand-authored type, one tool (or shape) at a time.
- **Conclusion:** this is a per-tool authoring effort, not an infrastructure change. The
  registration call site itself (`WithSentinelTools<T>`) needs no change to support rollout.

## Scope of this inventory

Covers every tool in `RoslynSentinel.Server.Basic`, plus `RoslynSentinel.Server.Advanced`'s
`SentinelAdvancedRefactoringTools` class specifically. All other Advanced tool classes are
explicitly deferred to a later phase and appear below as a name-only list - no shape investigation
was done for them. This split was a direct scope decision: the remaining Advanced tools are rarely
used and can wait; they're listed here only for visibility.

## `autoStage` is an architectural fork, not a shape variant

This generalizes beyond the single "exclude `autoStage=false` from schema coverage" recommendation
already in the Cross-cutting findings below - it's worth calling out on its own because the
underlying reason is stronger than "it's extra work."

`ModifyModifier` (`SentinelRefactoringTools.cs:1666-1761`) is the clean worked example, and the same
`autoStage` split repeats near-identically in `MethodSignature`, `UsingDirective`,
`SummaryComment`, `ConstructorParameter`, `ModifyAttribute`, `ModifyBaseType`, and `WrapRange`:

- **`autoStage=false`:** returns `Data = updated.ToJsonSummary()` (`SentinelRefactoringTools.cs:1745`)
  - a raw dump of an in-memory, **not-yet-applied** `DocumentEditResult`. This branch never goes
  through the `ValidateAndApplyAsync` write-chokepoint (`SentinelRefactoringTools.cs:1751`). It is
  not a stable, named DTO - it's whatever properties `DocumentEditResult` happens to expose via
  `ToJsonSummary()`.
- **`autoStage=true`** (the default): the edit is staged/written via `ValidateAndApplyAsync`, and
  the method returns `Data = new AppliedChangeSummary(...)` (`SentinelRefactoringTools.cs:1756`) - a
  proper named record.

These are not two shapes of the same result - they are two different subsystems' output sharing a
return slot. One is a preview of an edit that was never validated or written (closer to a dry-run
artifact); the other is a receipt of a completed, validated write. Treating `autoStage` as "the tool
has a `bool` parameter that mildly changes its output" undersells it: it is closer to two different
tools that happen to share a name and a `[McpServerTool]` attribute than one tool with two output
shapes.

This strengthens the doc's existing recommendation (see Cross-cutting findings) to exclude
`autoStage=false` from `OutputSchemaType` coverage - not only because it is extra authoring work,
but because that branch is architecturally a preview/dry-run-adjacent path, not the tool's primary
contract, and schema-modeling it forces a decision about a codepath that arguably shouldn't be
first-class.

**Idea for later, not part of this phasing:** `autoStage=false` could eventually get its own small
shared preview DTO - something like `PendingEditPreview { string ProposedText, IReadOnlyList<string>
Warnings }` - instead of each tool exposing an ad hoc slice of `DocumentEditResult` forever. Flagged
here as a possible future direction only; no design work has been done on it and it is explicitly
not scheduled in any phase below.

## Read/lookup tools: standardize on ONE shape - always a list

This is the centerpiece of this revision and supersedes an earlier, more tentative framing.

**Where this started.** Read/search-type tools return either a single result or a list of results,
so the original framing was that they could probably be adapted onto one of two common shapes:
`ToolResult<T>` for the single-item case (already effectively the model for
`GetBestInsertionPoint`/`PreviewRenameImpact`) and `ToolResult<IReadOnlyList<T>>` for the list case
(already effectively the model for `LocateSymbol`) - with envelope fields carrying meta/status, and
null/empty values distinguishing "no result" from "one result" within whichever of the two shapes
applied.

**Where it landed, in direct response to the live `LocateSymbol` schema example above.** The
simpler and recommended approach is that a "single result" tool just returns a **1-item list**.
This collapses the entire read/lookup family onto **one shape** - `IReadOnlyList<T>`, always - with
no discriminant field needed anywhere:

- Empty list = no results.
- One item = found-one.
- N items = found-many.

A schema consumer never has to branch on "which of the two shapes is this response." This
supersedes the two-shape (`ToolResult<T>` vs `ToolResult<IReadOnlyList<T>>`) framing above - both
are no longer needed once every read/lookup tool commits to always returning a list; noted
explicitly here so this doesn't read as two live options still on the table.

**Compatibility cost, stated honestly, not a blocker.** Tools that currently return a bare scalar
`Data` object - `GetBestInsertionPoint` (`SentinelSymbolTools.cs:326-359`) and `PreviewRenameImpact`
(`SentinelSymbolTools.cs:360-399`) - would change their `Data` shape from `T` to `[T]`. That is a
breaking change for any current caller expecting a scalar directly out of `Data`. This is an
acceptable one-time break given the rollout is still pre-adoption (no external StructuredContent
consumers depend on the current scalar shape yet), but it should be called what it is rather than
waved through silently.

## The real lever on "Complex" read/lookup tools: split the variant-selecting parameter

Several tools tagged Complex in the per-tool inventory below aren't complex because their *data* is
complex - they're complex because one tool method is doing the job of two or three tools, selected
by a variant-picking parameter:

- `GetTypeInfo`'s `include` parameter: `hierarchy` | `members` | `both`
  (`SentinelSymbolTools.cs:464-544`).
- `FindReferences`'s kind-style parameter: callers-only | implementations-only | combined
  (`SentinelSymbolTools.cs:400-463`).
- `InspectSymbol`'s `aspect` parameter: `info` | `blastRadius` (`SentinelSymbolTools.cs:126-192`).
- `QuerySymbolRelationships` similarly combines a direct-list branch, a broadened-by-kind branch,
  and an empty-with-warning branch (`SentinelSymbolTools.cs:226-325`).

Splitting each of these into separate single-shape tool calls - or, at minimum, into separate typed
response variants keyed directly to the selecting parameter - would collapse most of this
inventory's Complex tags down to Trivial: each half becomes single-shape the moment it isn't also
trying to be its sibling's fallback case in the same method.

This is presented as a recommendation worth serious consideration, **not a firm decision**. It has
an obvious cost (more tool entries in the surface a model has to choose between up front) that
trades against the schema-simplicity win, and that trade-off has not been weighed against the rest
of RoslynSentinel's tool-count/tool-surface conventions.

## DTO consolidation has to happen before/alongside schema-attachment, not just be phased by current complexity

This is a sequencing principle that should govern how the phasing below is read, not just another
finding to log alongside the others.

Introducing `OutputSchemaType` alone does not fix the underlying problem described in "Why this
does not generalize for free" - it just requires a hand-maintained shadow record describing
whatever shape a method happens to return at runtime. That shadow record can drift from what the
method actually returns: two independently-maintained descriptions of the same data, one of which
(the schema) is never actually exercised by the code path that produces the real response. That is
a new silent-drift failure mode, not a solved problem, and it is already named below under Cost /
risk.

The more durable fix is upstream of `OutputSchemaType` entirely: make tool methods themselves
return real generic types - `Task<ToolResult<AppliedChangeSummary>>`,
`Task<ToolResult<IReadOnlyList<SymbolHandle>>>` - instead of `Task<ToolResult<object>>`. Once a
method's C# signature already commits to a real return type, `OutputSchemaType` becomes a
near-mechanical reflection of that signature rather than a hand-authored shadow copy someone has to
remember to keep in sync.

Framed plainly: **this is a DTO/data-exchange enforcement project first, and a schema-attachment
project second.** The phasing below is reframed around that: variant-splitting (previous section)
and DTO consolidation onto shared, named types happen first; `OutputSchemaType` attachment follows
once methods already have real return types, at which point it should be close to mechanical rather
than a second independent authoring effort per tool.

## Cross-cutting findings

These matter for prioritization more than any single row in the per-tool tables below.

- **`AppliedChangeSummary` is by far the most common `Data` payload** across mutating tools
  (`RenameSymbol`, `SyncTypeAndFilename`, `GenerateMapping`, `ModifyEnum`, `ChangeAccessibility`,
  `ExtractLocalVariable`, `ModifyModifier`/Batch, `ModifyBaseType`/Batch, `ConvertAnonymousToNamed`,
  `InlineClass`, `IntroduceParameterObject`, `Introduce`, `Inline`, `MoveType`'s `outerScope` branch,
  etc). A single shared `OutputSchemaType = typeof(AppliedChangeSummary)` could cover a large
  fraction of tools in one move, **provided** their `autoStage=false` escape hatches are handled
  separately (see the dedicated `autoStage` section above - this is no longer a minor caveat, it's
  an architectural fork).
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
  extra schema per tool. (See the dedicated `autoStage` section above for why this is now framed as
  an architectural fork rather than just a scoping convenience.)
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
at all. Separately, `Git` already carries a method-level `[Produces(DataTag.Report)]`
(`SentinelGitTools.cs:367`) - see the open question above about whether method-level `[Produces]`
tags surface in the schema at all; if they don't, this attribute is currently a no-op.

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
5. **Method-level `[Produces]` schema surfacing** (new this revision, see the `[Produces]`/
   `[Consumes]` resolution-path section above). Whether a bare method-level `[Produces(DataTag.X)]`
   - as opposed to a property-level one - shows up anywhere in the emitted output schema has not
   been checked. `Git`'s existing `[Produces(DataTag.Report)]` is the concrete case to verify
   against.

## Per-tool inventory

Effort legend: **Trivial** (0-1 shapes, simple/already concrete) / **Moderate** (2-3 shapes, one
hand-authored type needed) / **Complex** (4+ shapes or nested/dynamic shapes, discriminated union
needed) / **Uncertain** (needs verification first, do not commit a tag).

Note on reading the Complex tags below in light of the two new sections above: several of these
(`InspectSymbol`, `QuerySymbolRelationships`, `FindReferences`, `GetTypeInfo`) may collapse toward
Trivial/Moderate if the variant-splitting recommendation is taken up, and the read/lookup rows in
particular should be read against the always-a-list recommendation rather than assumed to need a
bespoke shape each. The tags below were not re-scored against either recommendation - they still
reflect the original per-tool-as-is reading.

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
| LocateSymbol | SentinelSymbolTools.cs:71-125 | Task<ToolResult<object>> - **now has OutputSchemaType = LocateSymbolResult, see worked example above** | 1 main shape (list result) | Trivial |
| InspectSymbol | SentinelSymbolTools.cs:126-192 | Task<ToolResult<object>> | 2 shapes: info vs blastRadius - candidate for the aspect-splitting recommendation above | Moderate |
| QuerySymbolRelationships | SentinelSymbolTools.cs:226-325 | Task<ToolResult<object>> | 3 shapes: direct list (+ LargeResult offload), broadened dict-by-kind, empty-with-warning - candidate for variant-splitting above | Complex |
| GetBestInsertionPoint | SentinelSymbolTools.cs:326-359 | Task<ToolResult<object>> | 1 shape (scalar) - would become a 1-item list under the always-a-list recommendation | Trivial |
| PreviewRenameImpact | SentinelSymbolTools.cs:360-399 | Task<ToolResult<object>> | 1 shape (scalar) - would become a 1-item list under the always-a-list recommendation | Trivial |
| FindReferences | SentinelSymbolTools.cs:400-463 | Task<ToolResult<object>> | 3 shapes by kind: callers-only, implementations-only, combined - candidate for variant-splitting above | Moderate |
| GetTypeInfo | SentinelSymbolTools.cs:464-544 | Task<ToolResult<object>> | 3 shapes by include: hierarchy-only (TypeHierarchyReport), members-only (List<TypeMemberDetail>), combo anonymous - candidate for variant-splitting above | Moderate |

### `RoslynSentinel.Server.Basic/SentinelGitTools.cs`

| Tool | file:line | Return type | Shape count + note | Effort |
|---|---|---|---|---|
| Git | SentinelGitTools.cs:366-444 | Task<object> (not ToolResult<T> at all) | 12 branches, each dispatching to a private worker already returning a concretely-named record (GitStatusResult/GitLogResult/etc, 12 total pre-existing) but erased to object at the Git method boundary via a switch expression with an explicit (object) cast in the default arm; carries a method-level [Produces(DataTag.Report)] whose schema visibility is unverified (see open items) | Complex - prerequisite refactor needed (discriminated union or per-operation split) before OutputSchemaType fits; the 12 named types already existing is a major head start once that refactor happens |

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
| RenameSymbol | :451-538 | Task<ToolResult<object>> - **now has OutputSchemaType = RenameSymbolResultEnvelope** | 1 shape: rich anonymous object (changeId/oldName/newName/updatedHandle/residualMentions) | Moderate |
| Member | :580-937 | Task<ToolResult<object>> | many: 5 operations (addMember/addTopLevelType/addTypedMember/remove/replace/view), largest/most complex method in file | Complex |
| MethodSignature | :1310-1417 | Task<ToolResult<object>> - **now has OutputSchemaType = MethodSignatureViewResultEnvelope on the view branch** | 3 shapes: view ({Parameters}), non-autoStage (ToJsonSummary), applied (MemberChangedContentResult via ForPossiblyLargeDataAsync) | Complex |
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
| ModifyModifier | :1666-1761 | Task<ToolResult<object>> - **now has OutputSchemaType = ModifyModifierResultEnvelope; this is the worked autoStage example above, and the envelope deliberately only models the autoStage=true/AppliedChangeSummary branch** | 3 shapes: batch-delegate, non-autoStage, applied (AppliedChangeSummary) | Moderate |
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

This phasing is re-sequenced from the original draft to reflect the "DTO consolidation before/
alongside schema-attachment" principle above: splitting multi-mode tools and consolidating onto
shared, named DTOs now comes *before* attaching `OutputSchemaType`, rather than being treated as a
same-effort peer of it.

- **Phase 0 - variant-splitting and DTO consolidation (new, precedes schema-attachment).**
  - Split the variant-selecting parameters identified above (`GetTypeInfo`'s `include`,
    `FindReferences`'s kind-selector, `InspectSymbol`'s `aspect`, `QuerySymbolRelationships`'s
    kind-broadening) into single-shape calls or typed variants, per the recommendation above (still
    open, not decided).
  - Land the always-a-list convention for read/lookup tools (`LocateSymbol`,
    `GetBestInsertionPoint`, `PreviewRenameImpact`, and the split-out halves of the tools above),
    including the accepted breaking change to `GetBestInsertionPoint`/`PreviewRenameImpact`'s `Data`
    shape.
  - Consolidate mutating tools' applied-edit path onto `AppliedChangeSummary` and read/lookup tools
    onto the new always-a-list shape and `MemberChangedContentResult` for the offload-capable tools,
    at the C# method-signature level (`Task<ToolResult<AppliedChangeSummary>>` etc.), not just at
    the `OutputSchemaType` attribute level.
  - This phase is where most of the actual design risk and authoring effort lives. Once it's done,
    Phase 1 below becomes close to mechanical.
- **Phase 1 - cheapest wins, schema-attachment now near-mechanical.** `SentinelAdminTools` (all 4,
  already-scalar), `SentinelSymbolTools`'s single-shape tools (`LocateSymbol` - done -,
  `GetBestInsertionPoint`, `PreviewRenameImpact`), `SentinelDocumentationTools.ProjectDoc` (already
  has 3 named types, just needs its own OutputSchema wiring since it bypasses `ToolResult<T>`).
  `McpServerStatus`, `LocateSymbol`, `RenameSymbol`, `MethodSignature`'s view branch, and
  `ModifyModifier` are already done and not part of this phase's remaining work.
- **Phase 2 - the `AppliedChangeSummary` shared-schema play.** One `OutputSchemaType` covering the
  large set of mutating tools that already converge on `AppliedChangeSummary` for their applied-edit
  path (see Cross-cutting findings above for the list). Explicitly scopes out `autoStage=false`
  responses per the recommendation above - those keep returning unstructured content until a
  separate pass decides they're worth a second schema (or the `PendingEditPreview` idea above is
  taken up).
- **Phase 3 - the `MemberChangedContentResult` shared-schema play.** Same idea, for the
  `ForPossiblyLargeDataAsync`-backed tools (`Member`'s addMember paths, `MethodSignature`,
  `UsingDirective`'s add, `SummaryComment`'s add, `ConstructorParameter`'s add, `ModifyAttribute`'s
  add/replace). Needs a design decision on how to represent the inline-vs-`LargeResult`-offload
  duality in one schema before starting.
- **Phase 4 - the Complex/multi-shape tools that don't collapse under Phase 0's variant-splitting.**
  `Member`, `ReplaceSnippet`, `ApplyDiff`, `GetDiagnostics`, `ListSolutionItems`, `Git` (blocked on
  its erasure-refactor prerequisite above), and whatever's left Complex-tagged after Phase 0. Each
  likely needs its own discriminated-union design, not a shared type - highest effort, do last.

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
  both sides. The Phase 0 DTO-consolidation-first sequencing above is the more durable fix for this
  same risk - once a method's actual C# return type is the real DTO, there is no second copy to
  drift from.
- Touches only `Server.Basic`/`Server.Advanced` tool-attribute declarations and adds new record
  types alongside them - does not touch `ToolResult<T>` or the registration call sites
  (`WithSentinelTools<T>`), so this is additive and low-risk to the existing surface. Nothing about
  existing tool behavior changes; only the advertised schema and the populated `StructuredContent`
  field are new. (Phase 0's method-signature changes are a partial exception to "does not touch"
  claim - changing a method's declared return type from `Task<ToolResult<object>>` to
  `Task<ToolResult<AppliedChangeSummary>>` is a real signature change, even though the runtime
  behavior and JSON on the wire are meant to stay identical.)
- Real cost is authoring time and shape-count risk on the Complex-tagged tools (`Member`,
  `ReplaceSnippet`, `ApplyDiff`, `GetDiagnostics`, etc.) - those may need a discriminated-union
  schema, which the MCP C# SDK's `OutputSchemaType` mechanism has not been proven against in this
  repo yet (only single-shape tools have been tried so far: `McpServerStatus`, `LocateSymbol`,
  `RenameSymbol`, `MethodSignature`'s view branch, `ModifyModifier`). Phase 4 should include a small
  spike on one Complex tool before committing effort estimates for the rest of that phase.
- The always-a-list convention and the variant-splitting recommendation both add tool-surface
  churn (changed `Data` shapes, potentially more tool entries) on top of the schema-attachment
  churn already called out above - neither has been weighed against the cost of a model needing to
  re-learn a shape or a name it already knew from training data or a prior session.

## Status

This is a revised proposal, incorporating a follow-up design discussion (autoStage as an
architectural fork, the always-a-list convention for read/lookup tools, variant-parameter
splitting, and DTO-consolidation-before-schema-attachment sequencing) that happened after the
original inventory was drafted. It remains **proposal only** - the phasing and recommendations
above are not decisions, only the inventory's *scope* was. Implementation beyond the existing POC
points (`McpServerStatus`, and now also `LocateSymbol`, `RenameSymbol`, `MethodSignature`'s view
branch, and `ModifyModifier` - see the note under Motivation) has not been undertaken as part of
this proposal. The `x-produces-tag`/`x-consumes-tag` schema-annotation mechanism referenced in the
Worked Example section is separate, already-shipped infrastructure (including the `ApplyConsumesTags`
fix and the `McpToolSchemaFix.cs` -> `McpToolSchemaPatcher.cs` / `WithToolsFixed<T>` ->
`WithSentinelTools<T>` rename, both landed this session) - its shipped status should not be read as
this proposal's own rollout having progressed; the two are tracked independently.

Author: Claude (Sonnet 5), for Andrew Almond (andrew.almond@clearbridge.ca).
