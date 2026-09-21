# A converging ReplaceSnippetResult DTO for ReplaceSnippet (singular and batch)

## Status

**Implemented (2026-09-20).** `RoslynSentinel.Common/ReplaceSnippetResult.cs` was added as a record
with `ReplacementResult: ApplyChangesResult?`, `ValidationReport: DiagnosticReport?`,
`DiffContent: string?`. Both `ReplaceSnippet` and `ReplaceSnippetBatch`
(`RoslynSentinel.Server.Basic/SentinelWorkspaceTools.cs`) were converted from
`Task<SentinelCallToolResult<object>>` to `Task<SentinelCallToolResult<ReplaceSnippetResult>>`, with
every success-path return site constructing a real `ReplaceSnippetResult` instead of an anonymous
object or a bare `validationResult`/`strippedResult`. Full solution `Build` (fullBuild) came back
clean: 0 errors, 0 new warnings. Two of the "Open items before implementation" below were resolved
during implementation rather than left open - see the corrections noted there. This section
previously read "Proposed, not implemented"; the rest of this doc (Motivation, findings,
recommendation) is left as-drafted below since it accurately describes the reasoning that produced
the implementation.

This was a spike-equivalent investigation of one "Complex" tool named in
`docs/current/proposal_structuredcontent_rollout.md`'s Cost/risk section ("Phase 4 should include a
small spike on one Complex tool before committing effort estimates for the rest of that phase") and
Phase 4 list (`Member`, `ReplaceSnippet`, `ApplyDiff`). This doc is that spike for `ReplaceSnippet`
only - `Member` and `ApplyDiff` remain untouched.

## Motivation

`proposal_structuredcontent_rollout.md`'s per-tool inventory tags `ReplaceSnippet`
(`SentinelWorkspaceTools.cs:569-774` per that doc's citation) as **Complex** with the note "many:
match-failure diagnostics, applied summary, dry-run preview," and lists it alongside `Member` and
`ApplyDiff` as a candidate for the "spike one Complex tool before committing effort estimates"
exercise that doc explicitly says has not yet happened. `proposal_envelope_field_promotion.md`'s
Phase 2 pilot work established the pattern this doc follows - convert a tool's actual C# return type
to a single named success DTO (`SentinelCallToolResult<TSuccess, ResultError>`) rather than layering
a shadow `OutputSchemaType` on top of an untouched `object`-typed method - but explicitly excluded
any tool whose success path branches into more than one shape (`MoveMember`/`ChangeSignature` were
abandoned there for exactly this reason). Before `ReplaceSnippet` can be a candidate for that same
treatment, its actual success-path shape count needs to be established from source, not assumed from
the rollout doc's one-line "many" tag.

This session read `ReplaceSnippet`'s singular non-batch body and its private batch helper
`ReplaceSnippetBatch`, both in `RoslynSentinel.Server.Basic/SentinelWorkspaceTools.cs`, via
`GetMethodSource` (singular method returned as roughly lines 182-387 of that paginated read; the
batch helper roughly lines 431-624). **Caveat stated plainly:** those are `GetMethodSource`'s
returned-line-range numbers from a paginated read, not independently re-verified against a full-file
view or `GetFileOutline` - treat them as approximate location pointers, not confirmed `file:line`
citations, until someone re-checks them against the live file.

## What was found: three success shapes, not the "match-failure vs. applied vs. dry-run" framing floated earlier

An earlier framing in this session's discussion assumed `ReplaceSnippet` had an `autoStage`-style
fork like `ModifyModifier`'s (dry-run preview vs. applied receipt, per
`proposal_structuredcontent_rollout.md`'s "`autoStage` is an architectural fork, not a shape variant"
section). That framing does not hold for this tool: **`ReplaceSnippet` has no `autoStage` parameter
at all.** Its mode selector is `action: apply | validate`. This is a materially different mechanism
from the `autoStage` fork described in the rollout doc, not the same fork under a different name -
flagging this explicitly since the wrong mental model was in play for part of this investigation
before the source was actually read.

With error paths already filtered out (see below), the singular (non-batch) success path has three
shapes, keyed by `action` and `returnDiff`:

1. **`action == validate`:** returns `SuccessDetails = validationResult`, where `validationResult`
   comes from `_validationEngine.ValidateChangesAsync(snippetChanges)`. This is shaped like
   `DiagnosticReport` (`RoslynSentinel.Common/DiagnosticReport.cs:5`,
   `record DiagnosticReport(bool Success, List<DiagnosticInfo> Diagnostics)` - confirmed directly in
   this session by reading that file) per the same type `proposal_envelope_field_promotion.md`
   already cites for `ApplyChangesResult.ValidationResult`. Never writes to disk.
2. **`action == apply`, `returnDiff == false`:** returns `SuccessDetails = strippedResult`, where
   `strippedResult` is `result with { PreImages = null }` and `result` is an `ApplyChangesResult`
   from `_workspaceManager.ApplyProposedChangesAsync(...)`.
3. **`action == apply`, `returnDiff == true`:** returns
   `SuccessDetails = new { result = strippedResult, diff = SentinelRefactoringTools.BuildDiffFromPreImages(snippetChanges, result.PreImages) }`
   - an anonymous object wrapping the same `ApplyChangesResult` plus a diff object.

All error paths - missing `filepath`/`oldContent`/`newContent`, both-supplied-when-should-be-
either/or, oversized snippet bounds, file-not-found, `SolutionNotLoaded`, a compiler error blocking
the write, or an unhandled exception - go through `ErrorDetails`/`IsSuccess=false`, not through any
of the three shapes above. A converging success DTO only needs to model the `IsSuccess=true` paths;
error shapes are already unified by the existing `ResultError` envelope field and are out of scope
for this proposal, consistent with how `proposal_envelope_field_promotion.md` treats `ErrorDetails`
as already-solved infrastructure.

## What was found: batch is one atomic multi-file operation, not N independent per-edit results

`ReplaceSnippetBatch` runs when the `edits` parameter is supplied instead of the singular
`filepath`/`oldContent`/`newContent` triple. The important finding is that **batch is not "run
`ReplaceSnippet` N times and collect N results."** It is a single atomic multi-file operation:

- Every edit's match position is resolved against each file's ORIGINAL text snapshot - never against
  a prior edit's already-spliced output within the same batch. A code comment in the batch helper
  calls this out deliberately as removing a sequencing hazard that a series of individual
  `ReplaceSnippet` calls would otherwise have (an earlier edit shifting offsets out from under a
  later edit's match).
- All edits across all files are grouped by file, then spliced together highest-offset-first per
  file, so an earlier splice's growth/shrink never invalidates a later match's offset within the same
  file. This produces one `finalContents: Dictionary<FilePathWrapper, string>` covering every touched
  file across the whole batch.
- That combined dictionary goes through exactly **one** `_workspaceManager.ApplyProposedChangesAsync(finalContents, ...)`
  call - one atomic write covering every file in the batch, not one write per edit.
- The success shapes returned by batch are **exactly the same two apply-path shapes** as the singular
  path (bare stripped `ApplyChangesResult`, or `{ result, diff }` wrapping it), plus the same
  validate-path `DiagnosticReport`-shaped result - just built from the multi-file combined result
  instead of a single file's. Batching introduces **no new shape**.
- Per-edit validation failures (bad filepath, empty `oldContent`, null `newContent`, oversized
  bounds, snippet-not-found via `ContextHelper.FindExactSnippetPosition` throwing `ToolException`,
  overlapping-match detection between two edits targeting the same file) accumulate into a
  `List<string> perEditErrors`, which is then newline-joined into a single `ErrorDetails.message`
  string via `new ResultError(ToolErrorCode.InvalidArgument, "...:\n" + string.Join("\n", perEditErrors))`.
  So there **is** a natural per-edit list already in the code (`perEditErrors`), but it is collapsed
  into one pre-formatted error-message string today rather than surfaced as structured per-edit data.
  There is no partial-success-per-edit state in the current design - a batch either succeeds
  atomically for every edit, or fails atomically for all of them.

## The specific question this proposal resolves: scalar `ReplaceSnippetResult`, not `List<ReplaceSnippetResult>`

The idea floated at the start of this investigation was: define a `ReplaceSnippetResult` DTO with
fields for `replacementResult`, `validationReport`, `diffContent` (nullable, populated per branch),
and have `ReplaceSnippet` return `List<ReplaceSnippetResult>` so a singular call is a 1-item list and
a batch call is an N-item list - matching the "always a list" convention
`proposal_structuredcontent_rollout.md` recommends for read/lookup tools.

**Finding: this does not fit, and the mismatch is a category error, not a missing detail.** The
scalar `ReplaceSnippetResult` DTO idea itself is good and should be kept - but as a **scalar**, never
wrapped in a list:

- Singular call -> naturally 1 result either way; a list wrapper costs nothing here but also buys
  nothing.
- Batch call -> is **not** N independent per-edit results, per the finding above. It is one combined
  multi-file `ApplyChangesResult`/diff, exactly like the singular apply path just covering more
  files. Forcing this into `List<ReplaceSnippetResult>` would produce a list with exactly **one**
  element even for a 20-edit batch spanning 5 files - actively misrepresenting the operation as
  having per-edit granularity it does not have. A caller skimming `list.Count == 1` on a large batch
  would wrongly conclude "one edit happened" when N edits across multiple files were actually applied
  atomically.
- The "always a list" convention in `proposal_structuredcontent_rollout.md` was scoped explicitly to
  **read/lookup** tools (`LocateSymbol`, `GetBestInsertionPoint`, `PreviewRenameImpact`, etc.), where
  a list naturally reflects "0, 1, or N records found" against a stable underlying collection.
  `ReplaceSnippet` is a **mutating** tool whose batch mode is an atomic multi-file write, not a
  multi-record read - the two situations are not analogous, and applying the read/lookup list
  convention here would generalize past where the convention's own justification holds.
- The one place a genuine per-edit list already exists in the code today is `perEditErrors`, but it
  is used only on the batch-rejected-before-anchoring and batch-rejected-no-changes-written error
  paths, and only ever as pre-formatted joined text inside a single `ResultError.message` - never as
  structured per-edit data, and never on any success path.

### Recommended shape (recommended, not decided)

```csharp
public sealed record ReplaceSnippetResult(
    ApplyChangesResult? ReplacementResult,   // set when action=apply (singular or batch)
    DiagnosticReport? ValidationReport,       // set when action=validate (singular or batch)
    DiffReport? DiffContent);                 // set when action=apply && returnDiff=true
```

- Returned as a **scalar** `SuccessDetails: ReplaceSnippetResult`, not `List<ReplaceSnippetResult>`,
  for both the singular and batch call paths. Batch does not gain a second list dimension - it
  populates the same scalar's fields from the multi-file combined result instead of a single file's.
- Exactly one of the three fields is expected to be non-null per response in practice: `validate` ->
  `ValidationReport` only; `apply` without `returnDiff` -> `ReplacementResult` only; `apply` with
  `returnDiff` -> `ReplacementResult` and `DiffContent` both set, `ValidationReport` null. **Flagged
  explicitly as a real cost, not glossed over:** C# nullable reference fields do not enforce this
  mutual exclusivity at the type level the way a true discriminated union would - a malformed or
  future code path could set two or three fields at once with nothing at compile time or in the
  schema to prevent it. This is a documented convention, not a compiler-enforced guarantee.
  `proposal_structuredcontent_rollout.md`'s Cost/risk section already flags that a discriminated-
  union `OutputSchemaType` is unproven in this repo's SDK usage - every tool with `OutputSchemaType`
  attached so far (`McpServerStatus`, `LocateSymbol`, `RenameSymbol`, `MethodSignature`'s view
  branch, `ModifyModifier`) is single-shape. This proposal does not resolve that open question; it
  just inherits the same tradeoff by using an optional-fields record instead.
- This DOES satisfy `proposal_structuredcontent_rollout.md`'s "DTO consolidation has to happen
  before/alongside schema-attachment" sequencing: `ReplaceSnippetResult` is a converging named type
  that both the singular and batch code paths can be changed to construct and return directly
  (`Task<SentinelCallToolResult<ReplaceSnippetResult, ResultError>>` instead of
  `Task<SentinelCallToolResult<object, ResultError>>`, following the two-generic-parameter base
  established in `proposal_envelope_field_promotion.md`'s Phase 2), not just a shadow schema type
  layered on top of an untouched `object`-typed method the way `McpServerStatus`'s original POC did.

### Open items before implementation

- **RESOLVED during implementation: `BuildDiffFromPreImages` returns `string`, not an object.**
  Confirmed via `GetMethodSource` on `SentinelRefactoringTools.BuildDiffFromPreImages`
  (`internal static string BuildDiffFromPreImages(Dictionary<FilePathWrapper, string> changes,
  IReadOnlyDictionary<string, string?>? preImages)` - a thin wrapper over
  `ValidateAndApplyHelper.BuildDiffFromPreImages`). The placeholder `DiffReport` type named above was
  wrong; the record's real field is `DiffContent: string?`.
- **RESOLVED during implementation: `ReplaceSnippet` has exactly one MCP registration.** Confirmed via
  `SearchSolutionText` for `Name = "ReplaceSnippet"` - a single `[McpServerTool]` site in
  `SentinelWorkspaceTools.cs`. No dual-registration lockstep update (of the kind several
  `proposal_envelope_field_promotion.md` Phase 2 tools needed) was required.
- **Still open, not addressed by this implementation: whether batch's accumulated error data should
  also become structured, as a separate, smaller follow-on.** The `perEditErrors` list
  (`List<string>`) is the one place per-edit granularity already exists in the code, but only on
  error paths, and only as pre-joined text inside `ResultError.message` today. Restructuring it into
  a real `List<string>` or `List<SnippetEditError>`-style field somewhere in the error envelope was
  not required to ship the `ReplaceSnippetResult` success DTO - errors already go through
  `ErrorDetails` regardless - and remains a candidate for separate future work.
- **Still open: exact file:line citations for both methods were not independently re-verified against
  a full-file view before this implementation.** `GetFileOutline` (run during implementation)
  confirmed `ReplaceSnippet` at lines 182-387 and `ReplaceSnippetBatch` at lines 431-624 in
  `SentinelWorkspaceTools.cs`, matching this doc's earlier approximate citations - so this item is
  resolved for the pre-implementation line numbers, though post-implementation line numbers will have
  shifted slightly and were not re-confirmed.

## Cost / risk

- Changing `ReplaceSnippet`'s and `ReplaceSnippetBatch`'s declared return types from
  `Task<SentinelCallToolResult<object, ResultError>>` to a fixed
  `Task<SentinelCallToolResult<ReplaceSnippetResult, ResultError>>` is a real signature change, not
  purely additive - consistent with how `proposal_envelope_field_promotion.md` characterizes the
  same category of change for its own Phase 2 pilots. Both known MCP registration sites need to be
  updated in lockstep if `ReplaceSnippet` has a dual registration the way several tools flagged in
  `proposal_envelope_field_promotion.md`'s Phase 2 follow-on did (not checked this session - flagged
  as something to verify before implementing, not assumed either way).
- The mutual-exclusivity-by-convention-not-by-type tradeoff above is a real, named cost: a future
  code change could silently set more than one of `ReplacementResult`/`ValidationReport`/
  `DiffContent` at once, and nothing in the type system or the emitted JSON Schema would catch it.
  If a proper discriminated union becomes viable in this repo's SDK usage later, this record should
  be revisited rather than assumed permanent.
- Low risk to existing behavior otherwise: this proposal does not change what `ReplaceSnippet` or
  `ReplaceSnippetBatch` actually compute or write - only the declared C# return type and the shape
  advertised to schema consumers, mirroring the "additive, does not touch behavior" framing
  `proposal_structuredcontent_rollout.md` uses for `OutputSchemaType` attachment generally.
- This proposal is scoped to `ReplaceSnippet` only. `Member` and `ApplyDiff` - the other two tools
  named alongside it in `proposal_structuredcontent_rollout.md`'s Phase 4 list - have not been
  investigated here and should not be assumed to share this same three-shapes-collapsing-to-one-
  scalar structure; each needs its own read before a DTO is designed for it.

## Related

- `docs/current/proposal_structuredcontent_rollout.md` - parent proposal; names `ReplaceSnippet` as
  a Complex Phase-4 candidate and calls for a spike on one Complex tool before committing effort
  estimates for the rest of that phase. This doc is that spike, for `ReplaceSnippet` only.
- `docs/current/proposal_envelope_field_promotion.md` - source of the
  `SentinelCallToolResult<TSuccess, ResultError>` two-generic-parameter base this proposal's
  recommended signature change depends on, and of the precedent (`MoveMember`/`ChangeSignature`
  abandonment) for why a genuinely branching-success-shape tool cannot use a single closed generic
  return type without first collapsing to one shape - which this proposal argues `ReplaceSnippet`
  already does, once batch is understood as one atomic multi-file operation rather than N per-edit
  results.

Author: Claude (Sonnet 5), for Andrew Almond (andrew.almond@clearbridge.ca).
