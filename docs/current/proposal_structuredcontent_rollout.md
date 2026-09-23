# Per-branch DTO rollout (supersedes the original StructuredContent framing)

## Status change (2026-09-22)

This proposal originally chased two things bundled together: (1) replacing
`SentinelCallToolResult<object>`'s anonymous/erased payloads with real DTOs, and (2) wiring MCP
`OutputSchemaType`/`UseStructuredContent` so those DTOs get advertised as a JSON Schema and mirrored
into `CallToolResult.StructuredContent`. Goal (2) is now dropped. Goal (1) is what remains, and it
is rescoped narrower than the original Phase 0 (see "What changes from the original Phase 0" below).

Three findings, gathered after the original inventory was drafted, killed goal (2):

1. **`structuredContent` is not visible to the model in the harnesses this repo targets.** Claude's
   tool-use loop and LM Studio-backed local-model harnesses build the assistant-visible turn from
   `content` (the text block), not `structuredContent`. A payload that exists only in
   `structuredContent` is effectively invisible to the agent calling the tool. `content.data` is the
   only channel that matters here.
2. **Empirically, the three approaches serialize identically anyway.** Direct comparison of tool
   output across `object`, `OutputSchemaType`-annotated `object`, and a real DTO return type showed
   `content.data` comes out as the same serialized JSON text in all three cases. `OutputSchemaType`
   changes what's advertised in `tools/list` ahead of the call; it does not change what the model
   reads back from the call it just made.
3. **`structuredContent`'s real audience is non-model consumers** - client-side code validating a
   response against `outputSchema`, or an API-style integration consuming the tool programmatically.
   Reports from other MCP users converge on the same read: models don't validate against
   `outputSchema` or reach for `structuredContent` over `content`, so paying schema-token overhead to
   populate it is pointless for an agent-facing tool surface. RoslynSentinel's tools are called by
   models, not by non-model API consumers, so this audience doesn't exist here.
4. **The token cost is not free.** `OutputSchemaType` adds the derived JSON Schema to every advertised
   tool in `tools/list`, which every model sees on every session regardless of whether that tool is
   ever called. For a tool surface this large, that's a standing tax paid on a mechanism whose payoff
   (per finding 1-3) doesn't reach the model anyway.

Net: `UseStructuredContent = true` / `OutputSchemaType = typeof(...)` should not be added to further
tools, and existing usages are a cost (schema-list token bloat) without a corresponding benefit for
this repo's model-facing tool surface. Whether to unwind the existing POC points
(`McpServerStatus`, `LocateSymbol`, `RenameSymbol`, `MethodSignature`'s view branch,
`ModifyModifier`) is a separate follow-up decision, not made here - they are called out below under
"Existing OutputSchemaType usages" so they aren't lost track of.

## What survives: per-branch DTOs, decoupled from schema-attachment entirely

The original Motivation section's core complaint stands on its own, independent of
`structuredContent`: every RoslynSentinel MCP tool returns `SentinelCallToolResult<object>`, so a
tool's actual response shape is erased at compile time and only exists as whatever anonymous object
a given code branch happens to construct at runtime. That has real costs with nothing to do with
schema advertisement:

- No compile-time check that a branch's shape stays consistent across edits - shape drift is only
  caught by reading the code or by a test asserting on serialized JSON.
- No single place to look to see what a tool can return - you have to read every branch.
- Renaming or removing a field is invisible to the type system; it's a silent behavior change.

None of this requires `OutputSchemaType`, `UseStructuredContent`, or anything to be advertised in
`tools/list`. It's satisfied entirely by changing each branch's C# return type from
`SentinelCallToolResult<object>` to `SentinelCallToolResult<TSomeConcreteDto>` and constructing a
real named type instead of an anonymous one. The payoff is compile-time shape safety and
readability for whoever maintains the tool - not anything the model sees differently, since
`content.data`'s serialized JSON is unchanged either way (per empirical finding 2 above).

## What changes from the original Phase 0: no shape forcing across branches

The original Phase 0 bundled three distinct moves together. Only the first survives as proposed
here; the other two are explicitly rejected as goals of this effort:

- **Kept: replace `object` with a named DTO, per branch.** `Member`'s five operations
  (addMember/addTopLevelType/addTypedMember/remove/replace/view), `ModifyModifier`'s
  autoStage=true/autoStage=false split, `GetTypeInfo`'s three `include` variants, etc. each get
  their own DTO reflecting what that branch actually returns today. No two branches are required to
  share a type unless they already naturally return the same data.
- **Rejected: forcing every read/lookup tool onto "always a list."** The original doc's
  recommendation to make `GetBestInsertionPoint` and `PreviewRenameImpact` return a 1-item list
  instead of a scalar, purely so every read tool shares one schema shape, is dropped. A scalar
  result is the correct representation for a tool that returns exactly one thing; coercing it into
  a list buys schema uniformity nobody consumes (per findings above) at the cost of a less natural
  API and a breaking change to current callers. Each branch keeps whatever shape - scalar, list, or
  otherwise - best represents what it actually returns.
- **Rejected: the `AppliedChangeSummary` / `MemberChangedContentResult` shared-schema plays
  (original Phase 2/3).** Coercing every `autoStage=true` mutating tool onto one shared
  `AppliedChangeSummary` DTO, or every `ForPossiblyLargeDataAsync`-backed tool onto one shared
  `MemberChangedContentResult` DTO, was originally justified as "one `OutputSchemaType` covers many
  tools in one move." That justification is gone. Where tools already naturally converge on
  `AppliedChangeSummary` (many already do - see the list in the original Cross-cutting findings,
  still accurate as a survey of current behavior), keep using it, because it's already the right
  type for what they return - not because of any schema-consolidation goal. Where a branch's actual
  data doesn't fit an existing shared type, it gets its own DTO rather than being bent to fit one.

The `autoStage` architectural-fork analysis from the original doc is unaffected by any of this and
still holds: `autoStage=false` (unapplied `DocumentEditResult`/`ToJsonSummary()` preview) and
`autoStage=true` (applied `AppliedChangeSummary` receipt) are two different subsystems' output
sharing a return slot, not two shapes of one result, and each should get its own DTO rather than
being forced into a single type. See the original analysis (`SentinelRefactoringTools.cs` -
now split into `RefactoringStructuralTools.cs`/`RefactoringSignatureTools.cs`, verify current
location before citing line numbers) for the full argument; nothing here changes it.

## Variant-parameter splitting: no longer load-bearing, still worth considering separately

The original doc's recommendation to split variant-selecting parameters (`GetTypeInfo`'s `include`,
`FindReferences`'s kind-selector, `InspectSymbol`'s `aspect`, `QuerySymbolRelationships`'s
kind-broadening) into separate tool calls was motivated by collapsing Complex schema shapes to
Trivial ones. That motivation is gone along with schema-attachment. It may still be worth doing on
independent UX/tool-surface-clarity grounds, but it is no longer part of this proposal and should be
raised separately if someone wants to pursue it - don't cite this document as the reason.

## Existing `OutputSchemaType` usages (unwind decision deferred)

These tools currently carry `UseStructuredContent = true` / `OutputSchemaType = typeof(...)`:

- `McpServerStatus` (`SentinelServerStatusTools.cs`) - the original spike.
- `LocateSymbol` (`SymbolNavigationTools.cs` per current file layout - verify before citing a line
  number, the original doc's `SentinelSymbolTools.cs` reference predates a file split).
- `RenameSymbol`, `MethodSignature`'s view branch (`RefactoringSignatureTools.cs`/
  `RefactoringStructuralTools.cs` per current file layout).
- `ModifyModifier` (same files).

Given findings 1-4 above, these are now believed to cost schema-list tokens on every session without
benefiting the model. Whether to strip `OutputSchemaType`/`UseStructuredContent` back off them is a
real follow-up decision (a small, low-risk change - it only touches the attribute and the shadow
record it points at) but is deliberately left undecided here rather than folded into this rewrite,
since it wasn't what prompted this revision. Flagged so it isn't lost.

## `x-produces-tag` / `x-consumes-tag` are unaffected by any of this

The `[Produces]`/`[Consumes]` DataTag schema-annotation mechanism (`McpToolSchemaPatcher.cs`,
`ApplyConsumesTags`) is separate, already-shipped infrastructure and is not part of what's being
reconsidered here. It surfaces in the **input**-side parameter schema and the property-level output
schema shown in `tools/list` before a call is made - a channel the model/harness does consume when
deciding how to fill a subsequent tool's parameters (e.g. threading `LocateSymbol`'s `docCommentId`
into `Member`'s `docCommentId` parameter). That's a different mechanism from
`structuredContent`/`OutputSchemaType`'s response-shape mirroring, and nothing above argues against
it. See `proposal_datatag_chaining_contract.md` for that work's own tracking.

## Known defect, still open: `SentinelGitTools.Git`'s erasure

`Git` (`SentinelGitTools.cs`) has 12 branches, each dispatching to a private worker that already
returns a concretely-named record (`GitStatusResult`, `GitLogResult`, etc., 12 total, pre-existing),
but the switch expression at the `Git` method boundary erases all of them to `object` before they
reach the caller, and `Git`'s own declared return type is `Task<object>`, not going through the
shared `SentinelCallToolResult<T>` envelope at all. This is exactly the kind of erasure this
narrower proposal still argues should be fixed - the 12 named types already exist, only the method
boundary needs to stop discarding them. Still flagged as needing either a discriminated-union return
or a per-operation method split; still out of scope to fix inline in this document.

## Scope note on the original per-tool inventory

The original doc's per-tool table (shape counts, file:line references, Trivial/Moderate/Complex/
Uncertain tags) was scored against schema-attachment effort, not DTO-authoring effort for its own
sake - the two mostly correlate (more shapes still means more DTOs to write either way) but the
table's line-number references predate at least one file split
(`SentinelRefactoringTools.cs` -> `RefactoringStructuralTools.cs`/`RefactoringSignatureTools.cs`,
`SentinelSymbolTools.cs` -> `SymbolNavigationTools.cs` and others) and were not re-verified as part
of this rewrite. Treat the original inventory as a rough map of which tools have multiple branches
worth DTO-ing, not as current file:line truth - re-locate each tool before citing a specific line
number in future work.

## Cost / risk (revised)

- Per-branch DTO authoring is still real work, proportional to how many distinct shapes a tool's
  branches actually return - same cost the original doc identified, just no longer paired with a
  second, separate schema-authoring cost on top of it (since `OutputSchemaType` is no longer part of
  the goal).
- Removing the shape-forcing goal removes the breaking-change cost the original doc flagged for
  `GetBestInsertionPoint`/`PreviewRenameImpact` (no longer coerced to a list) and removes the
  drift-risk the original doc flagged for `OutputSchemaType` shadow records (a hand-maintained record
  that could silently diverge from what a method actually returns) - since a per-branch DTO that *is*
  the method's real return type has no separate shadow copy to drift from.
- Existing `OutputSchemaType` usages are a standing, currently-unquantified token cost in `tools/list`
  on every session. Not fixed by this document; flagged for a follow-up decision.

## Author

Claude (Sonnet 5), for Andrew Almond (andrew.almond@clearbridge.ca). Rewritten 2026-09-22 at the
user's direction to drop the `structuredContent`/`OutputSchemaType` goal entirely (models don't
consume it in this repo's harnesses, the three serialization paths are empirically identical in
`content.data`, and the schema-token cost is paid on every session regardless) and rescope around
per-branch DTOs without cross-branch shape forcing.
