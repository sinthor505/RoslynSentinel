# `MoveMember`'s outer apply path discards its own engine's structured call-site ledger, surfacing a well-classified move as an opaque raw-compiler-error rejection

**Status:** FIXED 2026-09-27, `RoslynSentinel.Server.Advanced/AdvancedRefactoringTools.cs` (`MoveMember`).
Implemented this doc's "preferred" option in spirit (a short-circuit ahead of the raw `apply.Error`,
rather than a `describeValidationFailure` callback): immediately after `ValidateAndApplyAsync` returns
a non-null `apply.Error`, the code now checks `result.PendingLedgerEntries` and
`result.SkippedCallSites` first and, if either is non-empty, returns a structured
`ToolErrorCode.UnresolvedCallSites` naming every unresolved site's full `FilePath:Line`,
`BrokenExpression`, `Status`, `BlockReason`, and `CandidatesInScope` - in the exact `"FilePath:Line"`
key shape `callSiteFixups` expects - instead of ever surfacing the raw `apply.Error` compiler-diagnostic
dump. Only falls through to the raw `apply.Error` when neither collection has entries (a genuinely
different failure class this doc's repro doesn't cover).

Verified via source (the check is unconditional whenever `apply.Error is not null`, so it structurally
cannot be bypassed by this repro's shape) and via the real end-to-end retest this session: the same
`ApiIntegrationEngine.AddValidationToPocoAsync` -> `ApiAutomationEngine` move, with all 14 (post
cross-document-`MoveOrderDependent`-fix) unresolved call sites supplied via `callSiteFixups`, applied
cleanly with `changeId` returned and the ledger path correctly *not* triggered (zero remaining
unresolved entries, so nothing to track) - see git commit for this session's `MoveMember` fix batch.
The ledger's `TryOpen` itself still has not been exercised with entries actually present at apply time
(this retest resolved every site, so `PendingLedgerEntries` was empty by the time the real apply ran);
exercising `TryOpen` with a genuinely-partial resolution remains open, tracked in
`docs/current/TODO.md`, not blocking.

**Original status: OPEN. Root cause traced to source.** This doc supersedes the "Not yet independently
confirmed" caveat in `docs/current/blockers/resolved/blocking_error_movemember_instance_callsite_not_rewritten.md`
with a corrected, fuller finding from re-running the exact same repro after that doc's fix landed.
See "Relationship to existing docs" below before treating the two as contradictory - they are not:
the earlier doc's fix is confirmed genuinely working; this doc documents a second, different defect
one layer up that the same repro exposes once the first one is out of the way.

## What was being attempted

Re-running the identical end-to-end repro from the original doc, after its `previewChanges`
fix (`AdvancedStructuralEngine.cs`, `PreviewInstanceMoveCallSitesAsync`) was applied and the server
rebuilt/restarted, to confirm the fix actually resolved the incident:

```
MoveMember(
  filepath: "RoslynSentinel.Advanced\ApiIntegrationEngine.cs",
  className: "ApiIntegrationEngine",
  memberNames: ["AddValidationToPocoAsync"],
  targetClassName: "ApiAutomationEngine",
  dryRun: true
)
```

## The exact result reported

Byte-for-byte identical to the original doc's symptom - the tool still returns `isSuccess: false`,
`errorCode: "Exception"`, with the same wall of `CS1061` diagnostics, one per real call site:

```
'ApiIntegrationEngine' does not contain a definition for 'AddValidationToPocoAsync'
```

at the same 15 locations (`CodemodTools.cs:811`, `BugFixTests.cs:1825/1852/1871`,
`BatteryFifteenTests.cs:31/48/65`, `BatteryNineteenTests.cs:236/244`,
`BatteryThirtyTests.cs:57/77/95/111/126/128`).

On first read this looks like the earlier fix did not work. **It did work** - see "What was
confirmed working" below. The identical surface error has two different causes at two different
layers, and this doc is about the second one.

## Where this happened

- Tool: `MoveMember` (Advanced-flavor server), `RoslynSentinel.Server.Advanced/AdvancedRefactoringTools.cs:553`.
- Shared apply chokepoint: `ValidateAndApplyHelper.ValidateAndApplyAsync`, `RoslynSentinel.Common/ValidateAndApplyHelper.cs:18-118`.
- Engine: `AdvancedStructuralEngine.MoveMemberAsync` (`RoslynSentinel.Advanced/AdvancedStructuralEngine.cs:173`)
  -> `MoveInstanceMembersAsync` (`AdvancedStructuralEngine.cs:1045`) -> `PreviewInstanceMoveCallSitesAsync`
  (`AdvancedStructuralEngine.cs:838-1042`, exact range approximate - see the fixed doc for the
  originally-cited 838-1010).

## What was confirmed working (the original doc's fix holds)

Temporary `Debug`-level logging was inserted directly before the `brokenHere` computation inside the
`foreach (var refLocation in locations)` loop in `PreviewInstanceMoveCallSitesAsync`
(`AdvancedStructuralEngine.cs`), the server rebuilt, the identical `MoveMember` call re-run, the log
captured, and the logging then removed (git tree confirmed clean afterward, 0 build errors both
before and after the temporary edit - the rebuild-instrument-rebuild-revert cycle left no residue).

The captured log showed `brokenHere=True` for all 15 call sites, every time, with
`validation.Diagnostics` containing all 15 real `CS1061`s at matching `FilePath`+`StartLine`. This
confirms the fix from the earlier doc (folding the destination-side edit into `previewChanges` before
the trial compile) is doing its job: the trial compile now correctly reproduces the real post-move
break at every one of the 15 sites, and every site is correctly routed into the classification logic
that follows the `brokenHere` check, rather than being misclassified `Valid`-with-null-`SuggestedFix`
and silently skipped the way the original doc found.

## What was confirmed as correct, expected classification, not a bug

With `brokenHere=True`, each of the 15 sites falls through to the `LookupSymbols`-based
candidate search at `AdvancedStructuralEngine.cs:1014-1020`, looking for an in-scope
field/property/parameter/local of type `ApiAutomationEngine` at that call site. None of the 15 real
call sites in this repo have such a variable in scope - every one calls through a
field/variable statically typed `ApiIntegrationEngine` (e.g. `_apiIntegrationEngine.AddValidationToPocoAsync(...)`
at `CodemodTools.cs:811`, or `engine.AddValidationToPocoAsync(...)` in the test files). So
`candidates.Count == 0` for all 15, and `PreviewInstanceMoveCallSitesAsync` correctly classifies
every one as `CallSiteStatus.NoCandidateIntroducible` (`AdvancedStructuralEngine.cs:1022-1026`), not
`Valid`.

This is the **correct, designed** classification for this test case: there genuinely is no
unambiguous receiver of the destination type in scope at any of these 15 call sites, which is
precisely the situation `callSiteFixups` exists to resolve (a human or model must name a receiver
expression per site, or `"new"`). The test case simply has no ambiguity-free rewrite target - this is
not a defect in the classification logic, and nothing here should be "fixed" by trying to make
`NoCandidateIntroducible` behave more permissively.

## Root cause of the remaining defect - traced to source, not a hypothesis

`MoveInstanceMembersAsync` (`AdvancedStructuralEngine.cs:1045-1112`) correctly consumes this
classification: `unresolvedRows` (line 1082) collects every row that is not `Valid` and has no
matching `callSiteFixups` entry, all 15 of them here. It then builds, correctly and completely:

- `pendingLedgerEntries` (lines 1095-1106) - one `CallSiteLedgerEntry` per unresolved site, each with
  `FilePath`, `Line`, `BrokenExpression`, `Status` (`NoCandidateIntroducible`), `BlockReason` (the
  human-readable "No in-scope reference of type... Add a 'using' directive or introduce a
  field/parameter of that type." text from line 1025), `SuggestedFix` (null here - there is none),
  and `CandidatesInScope` (empty here).
- `pendingLedgerOperationName` (line 1107).
- `skippedCallSites` (lines 1109-1111), derived from the same entries.

All three are returned inside the `MoveMemberResult` record (`AdvancedStructuralEngine.cs:11`) from
`MoveInstanceMembersAsync`, which `MoveMemberAsync` (the dispatcher) returns unchanged at line 290
when `nonStaticMembers.Count > 0` and `autoResolveCallSites` is true (the default, and what this
repro used). **The engine layer is doing exactly what the design calls for**: it has already
identified, explained, and structured every one of the 15 unresolved call sites before returning.

The defect is in what the MCP tool layer does with that already-correct result. `MoveMember`
(`AdvancedRefactoringTools.cs:503-587`) receives `result` at line 536, and at line 553 calls:

```csharp
var apply = await ValidateAndApplyAsync(result.Changes, $"Move [...] from '{className}' to '{targetClassName}'.", "MoveMember", dryRun, returnDiff, cancellationToken: cancellationToken);
if (apply.Error is not null)
    return new SentinelCallToolResult<AppliedChangeSummary> { IsSuccess = false, ErrorData = apply.Error };
```

`ValidateAndApplyAsync` is only ever handed `result.Changes` - a plain
`Dictionary<FilePathWrapper, string>` containing the source-file edit and the destination-file edit,
and nothing else (confirmed: `MoveInstanceMembersAsync` only ever writes into its local `result`
dict for the source file, the destination file, and files with a `resolvedReceivers` entry - none of
the 15 unresolved sites have one, so none of their files appear in `result.Changes` at all). It has
no parameter through which `result.SkippedCallSites` or `result.PendingLedgerEntries` could reach it;
the helper does accept an optional `describeValidationFailure` callback
(`ValidateAndApplyHelper.cs:30`) for exactly this kind of richer-explanation use case, but `MoveMember`
does not supply one.

Inside `ValidateAndApplyAsync` (`ValidateAndApplyHelper.cs:41`), `ValidateChangesAsync` runs a trial
compile of `result.Changes` - which still leaves all 15 real call sites referencing the
now-removed method - and finds the same 15 `CS1061`s (this is the *actual apply's* trial compile,
distinct from the *preview's* trial compile inside `PreviewInstanceMoveCallSitesAsync`, but built
from an equally incomplete changeset for the same underlying reason: the unresolved call sites were
never rewritten because there was nothing unambiguous to rewrite them to). `validation.Success` is
false, so `ValidateAndApplyAsync` returns at line 54-56:

```csharp
return new ApplyOutcome(null, new ResultError(ToolErrorCode.Exception,
    $"{operationName}: the change was valid and matched its target(s), but introduces new compiler errors - change not applied. " +
    $"Fix the issue(s) below and retry:\n{detail}"), dryRun);
```

Back in `MoveMember`, `apply.Error is not null` is true, so the tool method returns immediately at
`AdvancedRefactoringTools.cs:554-555` with that raw `ResultError`. **This return happens before the
method ever reaches lines 563-575**, which are the only place `result.PendingLedgerEntries` and
`result.SkippedCallSites` are read and folded into the response (`summaryNote` and, for a real
non-dry-run apply, `TryOpen`-ing the scoped operation ledger). Those lines are structurally
unreachable whenever `ValidateAndApplyAsync` rejects the changeset - which is exactly what happens
every time `MoveInstanceMembersAsync` reports any unresolved call site, since an unresolved call site
by definition means its file was never rewritten and the trial compile will fail there.

Net effect: a `MoveMemberResult` that already contains a complete, structured, human-and-machine
-readable explanation of every one of the 15 broken sites (which file, which line, which expression,
why it's unresolved, what would resolve it) is computed, then thrown away, and replaced with a
generic `validation.Diagnostics.ToJson()` dump of the same 15 `CS1061`s with none of that structure -
indistinguishable, from the caller's point of view, from an actual tool defect that failed to rewrite
call sites it should have been able to resolve.

## Why this is a blocking tool defect, not a routing-around opportunity

Per `CLAUDE.md`'s failure doctrine, this is an environment gap, not a case of the model/user calling
the tool wrong: the call as made is the exact documented "some call sites cannot be auto-resolved"
case `autoResolveCallSites`'s own `[Description]` at `AdvancedRefactoringTools.cs:517` and
`callSiteFixups`'s own `[Description]` at line 519 describe - "Manual resolution for call sites
autoResolveCallSites couldn't handle." The tool's own parameter documentation promises a workflow
(get told which sites need `callSiteFixups`, then retry with them) that the current code path cannot
deliver, because the error path that fires first never reaches the code that would deliver it. A
caller reading only the tool's response has no way to discover that `callSiteFixups` is what they
need, or which keys/values to put in it - they would have to independently re-derive the 15 call
sites and their fix candidates from the raw `CS1061` list, which is exactly the manual, error-prone
work `autoResolveCallSites`/`callSiteFixups` exists to avoid.

## The ledger mechanism has still never been exercised end to end this session

Worth stating explicitly: `CallSiteLedgerEntry` and the `pendingLedgerEntries` ->
`((IScopedOperationLedger)_workspaceManager).TryOpen(...)` call at `AdvancedRefactoringTools.cs:565`
have still never actually run in this session's testing, for either the original bug or this one.
The write is rejected by `ValidateAndApplyAsync` before `MoveMember` ever reaches the `!dryRun &&
result.PendingLedgerEntries is { Count: > 0 }` check at line 563 - and this repro used `dryRun: true`
regardless, which would skip `TryOpen` even if the apply had succeeded (correctly - nothing was
written, so nothing needs tracking). Confirming the ledger actually opens, blocks unrelated writes,
and gets resolved by a subsequent `callSiteFixups`-carrying retry requires either fixing the defect
below first, or manually constructing a case where `result.Changes` alone happens to compile (which
this repo's real call sites do not allow).

## What was ruled out

- **Not a recurrence of the `previewChanges` bug.** Confirmed via runtime debug logging (see above)
  that `brokenHere=True` for all 15 sites and the trial compile inside
  `PreviewInstanceMoveCallSitesAsync` correctly reproduces the real break. That fix is holding.
- **Not a misclassification.** All 15 sites are correctly `NoCandidateIntroducible`, not `Valid`,
  not `Ambiguous` (there is zero in-scope candidate, not multiple), and not `MoveOrderDependent` or
  `NoCandidateBlocked` (the destination type resolves fine and is accessible - only the per-call-site
  receiver is missing).
- **Not corruption or a partial write.** `dryRun: true` was used for this repro specifically to keep
  this session's re-verification non-destructive; nothing was written to disk either way, since
  `ValidateAndApplyAsync` refuses before the dry-run/real-write branch is reached at all.
- **Not something `describeValidationFailure` already solves generically.** It is an optional
  delegate on the shared helper (`ValidateAndApplyHelper.cs:30`) that *could* carry richer detail
  into the rejection message, but `MoveMember`'s call site does not pass one, so this is not "the
  facility already exists and the caller declined it" - it is unused for this tool.

## What unblocks it

The `MoveMember` tool wrapper (`AdvancedRefactoringTools.cs:503-587`) needs to detect the specific
"the engine already explained every unresolved call site" case and surface that structured
information as the actual response, instead of letting the raw compiler-diagnostic rejection from
`ValidateAndApplyAsync` be the last word. Two ways to get there, in order of how much they disturb
the existing chokepoint:

1. **Preferred - use the escape hatch that already exists on the helper.** Pass a
   `describeValidationFailure` callback into the `ValidateAndApplyAsync` call at
   `AdvancedRefactoringTools.cs:553` that, when `result.SkippedCallSites.Count > 0`, formats
   `result.PendingLedgerEntries` (file, line, `BlockReason`, `CandidatesInScope`, and - critically -
   the exact `"FilePath:Line"` key shape `callSiteFixups` expects, so a caller can copy it directly)
   instead of (or in addition to) the raw diagnostics dump, so the *first* response the caller sees
   already tells them what to put in `callSiteFixups`.
2. **Alternative - reorder the tool method.** Move the `result.SkippedCallSites`/`PendingLedgerEntries`
   check ahead of the `ValidateAndApplyAsync` call, and short-circuit straight to a structured
   `ResultError` (naming every unresolved site, its reason, and its candidates) whenever
   `unresolvedRows`/`SkippedCallSites` is non-empty and none of them have a matching `callSiteFixups`
   entry - never handing the necessarily-still-broken `result.Changes` to `ValidateAndApplyAsync` at
   all in that case, since its rejection is guaranteed and adds no new information. This changes
   control flow more than option 1 but avoids ever running a trial compile known in advance to fail.

Either way, the fixed behavior should be verified against this exact repro:

1. Re-run the identical `MoveMember(dryRun: true)` call from this doc.
2. Confirm the response's `ErrorData` (or a still-`IsSuccess` response, if the design instead treats
   "N sites need fixups" as a partial success - a judgment call for whoever implements this) names
   all 15 sites individually with file, line, and the `BlockReason` text from
   `AdvancedStructuralEngine.cs:1025`, not a raw `CS1061` dump.
3. Retry with a constructed `callSiteFixups` covering all 15 keys (e.g. picking `"new"` or a
   plausible receiver per site) and confirm the ledger-opening path
   (`AdvancedRefactoringTools.cs:563-571`) is finally reached and exercised for the first time -
   closing the gap noted above.
4. Add a regression test alongside `PreviewInstanceMoveCallSitesTests` (referenced in the resolved
   doc below) that specifically exercises `MoveMember`'s outer tool-layer response shape when
   `unresolvedRows` is non-empty and `ValidateAndApplyAsync` would otherwise reject - the existing
   `PreviewInstanceMoveCallSitesTests` only covers the engine's classification, not what the MCP tool
   method does with a classification result once it flows back through `ValidateAndApplyAsync`.

## Relationship to existing docs

- `docs/current/blockers/resolved/blocking_error_movemember_instance_callsite_not_rewritten.md` -
  the original incident from the same repro. Its fix (folding the destination-side edit into
  `previewChanges`) is confirmed still correct and working by this doc's debug-logging evidence
  above; do not re-open that doc or treat this doc as contradicting its FIXED status. That doc's own
  unresolved caveat ("Not independently re-confirmed via debugger... why the source-only changeset
  failed to reproduce the same CS1061s") is answered by this doc's confirmation that `brokenHere` is
  now `True` for all 15 sites - the mechanism question that doc left open is resolved as a side effect
  of tracing this second defect.
- `docs/current/proposal_movemember_instance_callsite_resolution.md` - describes the
  `autoResolveCallSites`/`callSiteFixups` design this doc's repro exercises correctly at the engine
  layer; does not describe how the MCP tool layer should behave when resolution is incomplete, which
  is the gap this doc identifies.
- `docs/current/plans/plan_scoped_operation_ledger.md` - Decision 4/5 (both DONE) implemented the
  ledger machinery this doc confirms has still never been opened in practice; Decision 5's own tests
  (`MoveMemberAsync_AmbiguousInstanceMemberNoFixup_ReturnsPendingLedgerEntryAsync`,
  `MoveMemberAsync_UnresolvedCallSite_OpensLedgerThatBlocksUnrelatedFileAsync`, both referenced in the
  resolved doc) apparently exercise the ledger against synthetic fixtures - worth checking whether
  those tests call the engine method directly (bypassing `AdvancedRefactoringTools.MoveMember` and
  therefore bypassing `ValidateAndApplyAsync` entirely) rather than through the MCP tool surface,
  which would explain why this tool-layer gap was never caught by them.
