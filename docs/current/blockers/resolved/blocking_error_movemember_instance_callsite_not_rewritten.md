# `MoveMember` silently skips rewriting every genuinely-unambiguous instance call site, then correctly refuses to apply the resulting broken change

**Status:** FIXED 2026-09-27, `RoslynSentinel.Advanced/AdvancedStructuralEngine.cs`
(`PreviewInstanceMoveCallSitesAsync`). Confirmed root cause: `previewChanges` (the trial changeset
handed to `ValidationEngine.ValidateChangesAsync`) contained only the source file's edit (member
removed) and never the destination file's edit (member added) -- exactly this doc's original
hypothesis in "What unblocks it" below. Fixed by resolving the destination class's declaring
document/node (already computed as `destinationType` a few lines earlier in the same method, just
not retained) and building the same same-file/different-file target-side edit that
`MoveInstanceMembersAsync` already builds for the real apply, folding it into `previewChanges`
before validating -- so the preview's trial compile now reflects the true post-move state instead of
a source-only partial edit.

Regression test added: `PreviewInstanceMoveCallSitesTests.ExistingNonEmptyDestinationClass_ClassifiesAsValidWithSuggestedFixAsync`
(`RoslynSentinel.Tests.Battery/PreviewInstanceMoveCallSitesTests.cs`) -- reproduces the incident's
shape (an *existing* destination class with real pre-existing members, not a fresh near-empty one,
receiving a moved single-candidate instance method) and asserts the call site classifies `Valid`
with a populated `SuggestedFix`. All 7 tests in that file pass; full solution build is 0 errors/0
warnings.

**Not independently re-confirmed via debugger against the original real-world classes**
(`ApiIntegrationEngine`/`ApiAutomationEngine`) -- this doc's own "Not yet independently confirmed"
caveat about *why* the source-only changeset failed to reproduce the same `CS1061`s was not
resolved by tracing the exact compiler-internal mechanism; the fix instead makes the changeset
complete so that whatever the mechanism was, the preview now validates against the real two-file
edit and cannot misclassify on that basis. If this class of bug recurs with a complete changeset
already in place, the "why" caveat above is the next place to look.

**Update 2026-09-27:** the "why" caveat above is now answered, and this fix is already
confirmed to still hold via runtime debug logging against the exact real-world repro
(`brokenHere=True` for all 15 call sites, matching the real `CS1061`s every time) -- see
`docs/current/blockers/blocking_error_movemember_ledger_discarded_on_apply.md`, which re-ran this
same repro post-fix and found a second, different defect one layer above this one (the MCP tool
wrapper discards the engine's already-correct call-site ledger instead of surfacing it). That doc
is the current one for this repro; this doc's FIXED status and regression test remain accurate
and unaffected.

---

**Original status: OPEN.** First real-world exercise of `MoveMember`'s instance-member path (newly enabled
this session; previously untested against a real codebase, only against fixture tests). No files
were corrupted or left in a bad state -- `ValidateAndApplyAsync` refused the write because it does
not compile, exactly as designed. This doc records a confirmed defect in
`PreviewInstanceMoveCallSitesAsync`'s call-site classification, not a misunderstanding of
`MoveMember`'s static-only scope.

## What was being attempted

Moving a single instance method, `AddValidationToPocoAsync`, from `ApiIntegrationEngine`
(`RoslynSentinel.Advanced/ApiIntegrationEngine.cs`) to the existing, unrelated class
`ApiAutomationEngine` (`RoslynSentinel.Advanced/ApiAutomationEngine.cs`), as one step of an approved
plan to merge both classes into `ApiGenerationEngine`. Both classes have an identical constructor
shape (`_workspaceManager` only), so there is no DI-gap complication -- this was chosen deliberately
as the simplest possible instance-move case to exercise the tool for the first time.

Call made (via the `MoveMember` MCP tool, both `dryRun: true` and then `dryRun: false` -- identical
failure both times):

```
MoveMember(
  filepath: "RoslynSentinel.Advanced\ApiIntegrationEngine.cs",
  className: "ApiIntegrationEngine",
  memberNames: ["AddValidationToPocoAsync"],
  targetClassName: "ApiAutomationEngine",
  targetFilepath: "RoslynSentinel.Advanced\ApiAutomationEngine.cs",
  autoResolveCallSites: true,   // default
  returnDiff: true
)
```

## The exact symptom

The tool returned `isSuccess: false`, `errorCode: "Exception"`, with a wall of `CS1061` compiler
diagnostics, one per real call site:

```
'ApiIntegrationEngine' does not contain a definition for 'AddValidationToPocoAsync'
```

`SearchSolutionText` confirms 15 real call sites across 6 files, every one a plain
`receiver.AddValidationToPocoAsync(...)` call where `receiver` is a field or local of static type
`ApiIntegrationEngine` -- genuinely unambiguous, single-candidate rewrite targets, i.e. exactly the
case `autoResolveCallSites` (default `true`) is documented to auto-fix:

- `RoslynSentinel.Server.Advanced/CodemodTools.cs:811`
- `RoslynSentinel.Tests.Advanced/BugFixTests.cs:1825, 1852, 1871`
- `RoslynSentinel.Tests.Battery/BatteryFifteenTests.cs:31, 48, 65`
- `RoslynSentinel.Tests.Battery/BatteryNineteenTests.cs:236, 244`
- `RoslynSentinel.Tests.Battery/BatteryThirtyTests.cs:57, 77, 95, 111, 126, 128`

None of the 15 were rewritten. `ValidateAndApplyAsync` correctly refused to apply the change since
the result does not compile, so no corruption occurred -- the working tree was reported clean
(`isClean: true`) after the attempt. The failure is a no-op move, not a partial or corrupting one.

## Where this happened

- Tool: `MoveMember` (Advanced-flavor server) -> `AdvancedStructuralEngine.MoveMemberAsync`
  (`RoslynSentinel.Advanced/AdvancedStructuralEngine.cs:173`).
- Dispatch: `MoveMemberAsync` correctly identifies `AddValidationToPocoAsync` as non-static (checked
  against `SyntaxKind.StaticKeyword`) and routes to the instance-member path at line 290
  (`return await MoveInstanceMembersAsync(...)`), not the static-only
  `MoveMembersToExistingClassAsync` path (line 301). **The dispatch logic itself is correct** -- this
  is not the old static-only limitation described in
  `docs/current/proposal_movemember_instance_callsite_resolution.md`; that gap has genuinely been
  built out (see `docs/current/plans/plan_scoped_operation_ledger.md` Decision 4/5, both marked
  DONE).
- `MoveInstanceMembersAsync` (`AdvancedStructuralEngine.cs:1013-1176`) calls
  `PreviewInstanceMoveCallSitesAsync` (`AdvancedStructuralEngine.cs:838-1010`) at line 1026 to
  classify every call site, then only rewrites rows where `Status == CallSiteStatus.Valid` **and**
  `SuggestedFix != null` (lines 1033-1041). Rows classified `Valid` with a null `SuggestedFix` are
  silently excluded from both the rewrite (`resolvedReceivers`) and the `unresolvedRows`/ledger path
  (line 1050 filters on `Status != CallSiteStatus.Valid`, so a `Valid`-with-null-fix row never gets a
  `CallSiteLedgerEntry` or a `SkippedCallSite` warning either) -- it is treated as "no fix needed"
  when it actually needed one.

## Root cause -- traced to source, not a hypothesis

`PreviewInstanceMoveCallSitesAsync` (`AdvancedStructuralEngine.cs:838-1010`) builds its trial change
set at lines 919-923:

```csharp
var updatedSourceRoot = root.ReplaceNode(classNode, classNode.RemoveNodes(membersToMove, SyntaxRemoveOptions.KeepNoTrivia)!);
var previewChanges = new Dictionary<FilePathWrapper, string>
{
    [filePath] = RoslynFormattingHelper.NormalizeWholeSubtreeWhitespace(updatedSourceRoot).ToFullString()
};
var validation = await _validationEngine.ValidateChangesAsync(previewChanges, cancellationToken);
```

`previewChanges` contains **only** the source file's edit -- the member removed from
`ApiIntegrationEngine` -- and never the target file's edit (the member added to
`ApiAutomationEngine`). `ValidationEngine.ValidateChangesAsync` itself is a generic, correctly
behaving helper (`RoslynSentinel.Common/ValidationEngine.cs:79`, takes an arbitrary
`Dictionary<FilePathWrapper, string>`); the bug is entirely in what changeset this caller hands it.

Per-call-site classification then runs at lines 929-1006. Each site computes:

```csharp
var brokenHere = validation.Diagnostics.Any(d => string.Equals(d.FilePath, refDoc.FilePath, StringComparison.OrdinalIgnoreCase) && d.StartLine == lineSpan.StartLinePosition.Line + 1);

if (!brokenHere)
{
    results.Add(new PreviewCallSite(refDoc.FilePath!, lineSpan.StartLinePosition.Line + 1, callExpression, CallSiteStatus.Valid, null, null, []));
    continue;
}
```

(lines 948-954). If `brokenHere` is false, the row is stamped `Valid` with a `null` `SuggestedFix`
and the method moves to the next reference **without ever reaching** the
`isMoveOrderDependent`/`destinationType`/`LookupSymbols`-based candidate-resolution logic later in
the loop (lines 956-1005) that would have populated a real `SuggestedFix` for exactly this
single-candidate case (the `candidates.Count == 1` branch at lines 997-1002).

Given that all 15 real call sites ended up unrewritten with no `Ambiguous`/`NoCandidate*`
classification text appearing anywhere in the tool's final output (which showed only the raw
compiler `CS1061` diagnostics, not any ledger/skipped-call-site detail), the most consistent
explanation is that `brokenHere` evaluated `false` for all 15 -- i.e. the source-only partial
changeset does not reproduce the same `CS1061`s that the real two-file apply produces, so every
call site was misclassified as already-`Valid`/unbroken and the candidate-resolution logic that
would have supplied `_someField.AddValidationToPocoAsync(...)` as `SuggestedFix` was never run for
any of them. The *real* apply later (which does build both file edits together, in
`MoveInstanceMembersAsync` lines 1086-1132) correctly reproduces the actual `CS1061`s and
`ValidateAndApplyAsync` correctly refuses to write -- which is why this was caught safely rather
than landing as silent corruption.

**Not yet independently confirmed by a debugger/breakpoint trace:** exactly *why* the source-only
changeset fails to reproduce the `CS1061` at the call site's line (plausible candidates: Roslyn's
diagnostic engine may resolve `receiver.AddValidationToPocoAsync(...)` differently when only the
member declaration is removed vs. when the whole solution's second file also changes; or a stale/
reused compilation from a prior step in the same call). This doc states the classification-input
bug (previewChanges omits the target-file edit) as confirmed via direct source read; the precise
compiler-level reason the omission causes `brokenHere` to read `false` is inferred from the observed
end-to-end behavior, not instrumented -- flagged as an assumption for whoever fixes this to confirm
before considering the fix complete.

## What was ruled out

- **Not the known static-only restriction.** `MoveMemberAsync`'s own static/non-static branch
  (`AdvancedStructuralEngine.cs` around line 231-290) correctly detects this member as an instance
  method and routes to `MoveInstanceMembersAsync`, not the static-only path. Confirmed by reading the
  dispatcher directly, not inferred from the error text.
- **Not a DI/constructor-shape mismatch.** Both classes take only `_workspaceManager`; this was
  deliberately chosen to eliminate that variable.
- **Not corruption or a partial write.** `ValidateAndApplyAsync`'s pre-write validation gate did its
  job -- the tool's own refusal, and the clean working tree afterward, confirm nothing landed on
  disk in a broken state.
- **Not an ambiguous or genuinely-hard call site.** All 15 call sites are plain
  `receiver.Method(...)` calls on a field/local of the exact source type, with no overload
  resolution, extension-method, or generic-variance complexity -- confirmed via direct inspection of
  each site through `SearchSolutionText`.

## Why this is a blocking tool defect, not a routing-around opportunity

Per CLAUDE.md's failure doctrine, a tool that returns a wrong classification (`Valid` for a call site
that is not actually valid without a rewrite) and thereby skips work it is documented to do
automatically is an environment defect, not evidence the caller used the tool wrong. The call as made
matches `docs/current/proposal_movemember_instance_callsite_resolution.md` section 1's exact
"trivial case" description (a single reachable field of the destination type in scope) and
section 5's dry-run design ("Build the proposed change set in memory (member removed from the source
class, added to the destination) -- no call sites rewritten yet", step 1) -- the implementation
deviates from the design doc's own step 1 by never including the destination-side edit before
validating.

## What unblocks it

Most likely fix, scoped to `PreviewInstanceMoveCallSitesAsync`
(`AdvancedStructuralEngine.cs:919-923`): `previewChanges` needs to include the target file's edit
(the member added to the destination class) alongside the source file's edit, matching
`proposal_movemember_instance_callsite_resolution.md` section 5 step 1 verbatim, before calling
`ValidateChangesAsync`. That requires computing the destination-side syntax edit earlier in this
method than it currently is (destination-type resolution already happens at lines 901-917; the
member-addition edit itself is currently only built later, inside `MoveInstanceMembersAsync` at
lines 1086-1132, and would need to be duplicated or hoisted here for the preview to validate against
the real, complete post-move state).

Once the changeset is complete, re-verify end to end:

1. `PreviewInstanceMoveCallSitesAsync` in isolation against this exact repro (`AddValidationToPocoAsync`,
   `ApiIntegrationEngine` -> `ApiAutomationEngine`) should classify all 15 sites as `Valid` with a
   non-null `SuggestedFix` (each site's `_workspaceManager`-shaped candidate, or whatever the actual
   in-scope destination-typed reference is).
2. A real (`dryRun: false`) `MoveMember` call against the same repro should then succeed, rewriting
   all 15 call sites, with `Build` reporting 0 errors afterward.
3. Existing tests `MoveMemberAsync_AmbiguousInstanceMemberNoFixup_ReturnsPendingLedgerEntryAsync` and
   `MoveMemberAsync_UnresolvedCallSite_OpensLedgerThatBlocksUnrelatedFileAsync` (referenced in
   `docs/current/plans/plan_scoped_operation_ledger.md` Decision 5) apparently already exercise an
   unresolved/ambiguous row and pass today -- their fixtures evidently do trip `brokenHere` correctly,
   unlike this real-world case. Whoever fixes this should add a new regression test shaped like this
   incident specifically (a single, genuinely-unambiguous instance call site against an *existing*
   target class, not a synthesized new one) to close the gap those two tests do not cover.

Also worth resolving, not strictly required to unblock: `proposal_movemember_instance_callsite_resolution.md`
section 5 should probably note explicitly that the destination-side edit is part of the "proposed
change set" its step 1 describes -- the current wording ("member removed from the source class,
added to the destination") already says this, but the implementation drifted from it silently enough
that this incident happened; worth a cross-reference note pointing back to this doc once fixed.

## Related

- `docs/current/proposal_movemember_instance_callsite_resolution.md` -- sections 1 and 5 describe
  exactly the design this defect deviates from; section 5 step 1 in particular.
- `docs/current/plans/plan_scoped_operation_ledger.md` -- Decision 4 and Decision 5 (both DONE)
  implemented the auto-resolution/ledger machinery this bug bypasses by misclassifying rows as
  `Valid` before they ever reach it.
