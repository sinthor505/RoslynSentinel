# Unify autoStage=false / autoStage=true return shape via AppliedChangeSummary.ChangedContent

## Motivation

`docs/current/proposal_envelope_field_promotion.md`'s Pilot 2 abandoned converting `MoveMember`/
`ChangeSignature` to `SentinelCallToolResult<TSuccess, TError>` because `autoStage=false` and
`autoStage=true` return genuinely different C# types on the success path - a single closed generic
cannot represent both. A follow-up survey (this doc's motivating research, not yet written up
elsewhere) found this is not a two-tool problem: **17 tools** share the identical pattern -
`Member`, `ModifyEnum`, `ModifyAttribute`, `ModifyModifier`, `ModifyBaseType`, `MethodSignature`,
`ChangeAccessibility`, `ConstructorParameter`, `SummaryComment`, `ExtractMethodSafe`,
`UsingDirective`, `ChangeSignature`, `MoveMember`, `MoveAllTypesToFiles`, `ExtractMembers`,
`WrapRange`, `MoveType`. Every one of them declares both `autoStage` and `dryRun` as independent
booleans, and every one of them short-circuits before the write chokepoint when `autoStage=false`:

```csharp
var updated = await _refactoringEngine.SomeAsync(...);
if (!autoStage)
    return new SentinelCallToolResult<object>() { IsSuccess = true, SuccessDetails = updated.ToJsonSummary() };

var apply = await ValidateAndApplyAsync(changes, description, "ToolName", dryRun, returnDiff, cancellationToken);
...
return new SentinelCallToolResult<object>() { IsSuccess = true, SuccessDetails = new AppliedChangeSummary(apply.ChangeId, changes.Keys.ToList(), description, apply.DryRun, apply.Diff) };
```

Single-file tools (`ModifyModifier` et al.) return `DocumentEditResult.ToJsonSummary()` - a JSON
string, not even an object, serializing `Outcome`/`FilePath`/`UpdatedText`/`IsCommitted`/`ChangeId`
(`RoslynSentinel.Common/DocumentEditResult.cs:19-44`). Multi-file tools (`ExtractMembers`,
`MoveAllTypesToFiles`, `MoveType`) return an anonymous `{ Changes = changes }` or the raw
`Dictionary<FilePathWrapper, string>` itself (`SentinelAdvancedRefactoringTools.cs:697,700-701,712,
720`, confirmed live in `ExtractMembers`'s three branches). Neither shape is `AppliedChangeSummary`,
which is what the same tool returns when `autoStage=true`.

This blocks 17 tools from the de-genericization phase (`SentinelCallToolResult<TSuccess, TError>`)
and, per the envelope-promotion proposal's own framing, keeps a genuinely different-shaped payload
sitting under the same tool name depending on a boolean - exactly the kind of hard-to-predict wire
shape that mission statement's target audience (weak/self-hosted models) is least equipped to
handle by inference.

## What autoStage=false and dryRun=true actually mean (not redundant)

Confirmed by reading `ValidateAndApplyHelper.ValidateAndApplyAsync`
(`RoslynSentinel.Common/ValidateAndApplyHelper.cs:18-118`) and cross-checking against `MethodSignature`
(`RoslynSentinel.Basic/RefactoringSignatureImpl.cs:211-214`):

- **`ValidateAndApplyAsync` always runs `ValidationEngine.ValidateChangesAsync` before checking
  `dryRun`** (`ValidateAndApplyHelper.cs:38`). If the proposed change introduces new compiler
  errors, it returns a hard failure - `IsSuccess=false`, `ResultError` naming "introduces new
  compiler errors - change not applied" (`ValidateAndApplyHelper.cs:46-51`) - regardless of whether
  `dryRun` is true or false. `dryRun` is only consulted afterward (`ValidateAndApplyHelper.cs:53`),
  purely to choose between building a diff preview and calling
  `workspaceManager.ApplyProposedChangesAsync` (line 59).
- **`autoStage=false` skips this chokepoint entirely.** The early-return happens before
  `ValidateAndApplyAsync` is ever called, so **no compile-check runs on that path today.** A change
  that the engine can construct syntactically but that would introduce a compiler error elsewhere
  (e.g. `MethodSignature(operation: remove, autoStage: false)` removing a parameter that breaks a
  call site the engine can't safely rewrite) currently returns `IsSuccess=true` with a preview,
  where the same call under `autoStage=true, dryRun=true` would be rejected.

So `autoStage=false` is not "the same as `dryRun=true` but cheaper" - it is a genuinely unvalidated
preview, a distinct capability from "prove this would apply cleanly, don't write it." Any unification
must preserve that distinction as data, not erase it by routing every `autoStage=false` call through
the chokepoint. Doing so would flip an unknown number of currently-succeeding `autoStage=false` calls
to failures - confirmed as a real regression risk, not a hypothetical, during this proposal's
research phase.

## Proposed shape change

Add one field to `AppliedChangeSummary` (`RoslynSentinel.Common/AppliedChangeSummary.cs:8-14`):

```csharp
public record AppliedChangeSummary(
    string? ChangeId,
    List<FilePathWrapper> AffectedFiles,
    string Description,
    bool DryRun,
    string? Diff = null,
    int? WorkspaceVersion = null,
    Dictionary<FilePathWrapper, string>? ChangedContent = null,   // NEW
    bool Validated = false                                         // NEW
)
```

`ChangedContent` is a dictionary (not a scalar string) so it covers both the single-file tools
(`ModifyModifier`-family: one entry) and the multi-file tools (`ExtractMembers`-family: one entry
per touched file) with the same field - a single-entry dict subsumes the scalar case, so no
tool-family-specific branching is needed in the record itself.

`Validated` is the tri-state signal the "not redundant" section above requires: `true` when the
return traveled through `ValidateAndApplyAsync` (i.e. `autoStage=true`, whether or not `dryRun` was
also true), `false` when it did not (`autoStage=false`). This is the field that keeps "did this
pass a compile-check" legible on the wire instead of silently implied by which branch produced the
object - the same rationale the envelope-promotion proposal already used for `IsSuccess`/
`ErrorDetails` (explicit signal a weak model can key off directly, not an inference).

### Both branches converge on the same call shape

```csharp
var updated = await _refactoringEngine.SomeAsync(...);   // or the multi-file equivalent
if (!autoStage)
{
    return new SentinelCallToolResult<object>()
    {
        IsSuccess = true,
        SuccessDetails = new AppliedChangeSummary(
            ChangeId: null,
            AffectedFiles: changes.Keys.ToList(),
            Description: description,
            DryRun: false,             // no chokepoint reached; not a validated dry-run
            Diff: null,
            ChangedContent: changes,   // the actual proposed text, previously ToJsonSummary()/anon-typed
            Validated: false)
    };
}

var apply = await ValidateAndApplyAsync(changes, description, "ToolName", dryRun, returnDiff, cancellationToken);
if (apply.Error is not null)
    return new SentinelCallToolResult<object> { IsSuccess = false, ErrorDetails = apply.Error };
return new SentinelCallToolResult<object>()
{
    IsSuccess = true,
    SuccessDetails = new AppliedChangeSummary(
        apply.ChangeId, changes.Keys.ToList(), description, apply.DryRun, apply.Diff,
        ChangedContent: changes, Validated: true)
};
```

No behavior change: `autoStage=false` still returns immediately, still never calls
`ValidateAndApplyAsync`, still can't fail validation. The only change is the *shape* of what it
returns - `AppliedChangeSummary` instead of `ToJsonSummary()`/anonymous-type/raw-dict - and the
explicit `Validated: false` marker that makes the skipped check visible instead of implicit.

### Single-file tools lose nothing

`DocumentEditResult`'s other fields (`Outcome`, `IsCommitted`) are dropped from the direct
`autoStage=false` payload under this change. `Outcome` is redundant with the early-return already
having happened only on a non-error engine result (an `Outcome` like `TargetNotFound`/`CannotEdit`
would have already produced an error return before reaching the `!autoStage` branch in every
sampled tool - not re-verified across all 17, flagged as a thing to confirm during implementation).
`IsCommitted` is always `false` on this path by construction (`autoStage=false` never writes) and is
superseded by the new `Validated` field's more useful signal.

## Consequences for the de-genericization follow-up

Once this lands, all 17 tools have exactly one success shape (`AppliedChangeSummary`) regardless of
`autoStage`, removing the disqualifier that stopped `MoveMember`/`ChangeSignature` in Pilot 2. This
does not itself convert them to `SentinelCallToolResult<AppliedChangeSummary, ResultError>` - that
remains a separate, mechanical follow-on pass once this shape change is verified, per the same
pattern already used for the 5 read-only tools converted after Pilot 1 in
`proposal_envelope_field_promotion.md`.

## Cost / risk

- **Record change is additive** - two new optional fields with defaults, so existing `AppliedChangeSummary`
  construction call sites elsewhere in the codebase (outside the 17 tools) do not need to change.
- **17 call sites need their `!autoStage` early-return rewritten** from `ToJsonSummary()`/anonymous-type/
  raw-dict to the `AppliedChangeSummary` construction shown above. Mechanical per-tool, not a
  `RenameSymbol`-shaped change - each site's `changes`/`updated` variable name differs, so this is
  `ReplaceSnippet`/`ApplyDiff` work, not a single solution-wide rename.
- **Any existing caller (test fixtures, prompt examples, ModelAgentRunner/PlanStepRunner parsing)
  reading `autoStage=false`'s current ad hoc shape breaks.** Same category of breaking change the
  envelope-promotion proposal already accepted for its own renames - not new risk, but real and
  needs the same search-and-update pass across `RoslynSentinel.Tests.*`.
- **Not a validation-behavior change.** This proposal is strictly about wire shape. It does not touch
  whether `autoStage=false` validates (it still doesn't) - that question belongs to
  `docs/current/proposal_nonblocking_validation_mode.md`, which is about relaxing validation on the
  *write* path (`ValidateAndApplyAsync` applying despite new errors), a different mechanism from
  `autoStage=false`'s pre-chokepoint bypass. The two proposals are compatible and independent: this
  one makes the bypassed path's *output shape* legible; that one makes the chokepoint itself more
  permissive. Neither depends on the other landing first. Worth cross-linking `Validated: false`
  from this proposal against that doc's own open item ("whether `dryRun` remains meaningful in \[a
  non-blocking\] mode") if both are ever implemented together, since a future non-blocking write
  would also want to report `Validated` accurately (true, but with diagnostics attached) rather than
  collapsing to the same boolean this proposal defines for the unvalidated-preview case.

## Open questions

- ~~Should `ChangedContent` be gated behind `returnDiff`?~~ **Resolved: no gating, populate
  unconditionally on both branches.** Checked directly: `Diff` (`ValidateAndApplyHelper.cs:53-56,79`)
  is a unified-diff string built by `BuildDiffAsync`/`BuildDiffFromPreImages`, an orthogonal
  representation of the same underlying change, not a superset or substitute for the literal text -
  tying `ChangedContent` to the `returnDiff` flag would recreate the same kind of implicit,
  hard-to-predict coupling this proposal exists to remove. It's also effectively dead weight to
  design around: `returnDiff` defaults to `false` on all 17 tools, and a solution-wide search found
  exactly one call site in the entire test suite
  (`RoslynSentinel.Tests.Battery/BatteryTwentyFourTests.cs:864`, `InlineClass_CrossFile_MovesMembers`)
  that ever passes `returnDiff: true`. This tracks the tool surface's broader move away from
  diff-shaped output: `ApplyDiff`/`ApplyUnifiedDiff` have been superseded in practice by
  `ReplaceSnippet`, which already deals in literal before/after text rather than unified-diff
  syntax - `ChangedContent` following that same literal-text convention is consistent with where the
  tool surface already is, not a new direction.
- **Whether `Outcome`-equivalent information needs to survive** for the single-file tools - see
  "Single-file tools lose nothing" above; needs a pass across all 17 tools' error-handling to confirm
  no caller currently branches on a specific `DocumentEditResult.Outcome` value surfaced via
  `ToJsonSummary()`, not just the aggregate success/fail split already visible via `IsSuccess`.
- **Field naming**: `ChangedContent` vs `ChangedText` (the question that prompted this proposal) -
  `ChangedContent` is used throughout this doc since it must hold a dictionary (multi-file), and
  `...Text` reads as singular/scalar. Open to the repo owner's preference; purely cosmetic, no
  behavior implication either way.

## Status

Design proposal only - not yet implemented. Follow-on to `proposal_envelope_field_promotion.md`'s
Pilot 2 (abandoned `MoveMember`/`ChangeSignature` conversion) and its "Explicitly not done" list.
Scoped to the 17-tool `autoStage`+`dryRun` family found across `RoslynSentinel.Server.Basic`'s
`RefactoringStructuralTools.cs`/`RefactoringSignatureTools.cs`/`RefactoringExtractionDocsTools.cs`
and `RoslynSentinel.Server.Advanced/SentinelAdvancedRefactoringTools.cs`. Does not cover the
`dryRun`-only tools (already envelope-promotion-clean, no shape branching) or the `action:
apply|validate` family (`ApplyDiff`/`WriteFile`/`ReplaceSnippet`/`Git`), which use an unrelated,
separately-invented staging vocabulary outside this proposal's scope.
