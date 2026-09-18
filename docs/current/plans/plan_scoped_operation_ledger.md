# Implement the scoped operation ledger + MoveMember instance-callsite resolution

## Context

Three design docs, written across one session (2026-09-17/18) of iterative review, describe a
coherent feature that is not yet implemented:

- `docs/current/proposal_movemember_instance_callsite_resolution.md` - lifts `MoveMember`'s
  static-members-only restriction for instance moves, via auto-resolution of unambiguous call sites,
  a `callSiteFixups` map for ambiguous ones, and a `dryRun: true` preview (`PreviewCallSite` rows,
  one per call site, `Status` of `Valid`/`Ambiguous`/`NoCandidateIntroducible`/`NoCandidateBlocked`/
  `MoveOrderDependent`) built from the same in-memory candidate-solution mechanism
  `ValidateAndApplyHelper.ValidateAndApplyAsync` already uses before its `dryRun` check.
- `docs/current/proposal_scoped_operation_ledger.md` - a `ScopedOperationLedgerEngine` that tracks
  unresolved call sites from a blast-radius operation as append-only ledger entries
  (`LedgerEntryBase` + `CallSiteLedgerEntry`), scopes a circuit breaker to only the ledger's files
  until every entry is `IsFixed`, and is exposed via an `IScopedOperationLedger` interface cast on
  `IWorkspaceManager`, matching the existing `IUnrecoverableBreaker` cast pattern at
  `ValidateAndApplyHelper.cs:93`.
- `docs/current/proposal_nonblocking_validation_mode.md` - referenced for context/precedent
  (`GroupBySeverity`, the `_sessionHalted`/breaker precedence rules) but not itself in scope here;
  its own feature (report-don't-reject validation) is independent and not a prerequisite.

This plan sequences the actual implementation. It does not re-litigate design decisions already made
in the three docs above - where this plan disagrees with or narrows something from those docs, it
says so explicitly with a reason, rather than silently diverging.

## Blocking precondition (see blocker doc) - RESOLVED 2026-09-18

`docs/current/blockers/resolved/blocking_error_loadsolution_missing_msbuild_workspaces_assembly.md`:
was a `LoadSolution` `FileNotFoundException` for `Microsoft.CodeAnalysis.Workspaces.MSBuild` in one
per-window stdio instance. Fixed by a VS Code restart (fresh single-build instance, no race). Decision
1 below is implemented and committed as of this note.

## Progress as of 2026-09-18 (overnight session)

Decision 1 is DONE (commits `725adf9` docs, `59353b8` code) - `ScopedOperationLedgerEngine`,
`IScopedOperationLedger`, `LedgerEntryBase`/`CallSiteLedgerEntry` all exist, DI-registered, wired into
`PersistentWorkspaceManager` via pass-through, zero behavior change, full solution build 0 errors.

Decision 2 is DONE (commit `f28b459`). Correction to this plan's original assumption, confirmed by a
dispatched Explore subagent: `ValidateAndApplyHelper.ValidateAndApplyAsync` does NOT itself contain any
`_sessionHalted`/breaker check, and never did - it delegates to `workspaceManager.ApplyProposedChangesAsync`
(`ValidateAndApplyHelper.cs:65`), and **that** method is the real chokepoint. Confirmed guard order inside
`PersistentWorkspaceManager.ApplyProposedChangesAsync` (`PersistentWorkspaceManager.cs:1228`): `_sessionHalted`
field read (line 1243) -> `_unrecoverableHaltMessage` field read (line 1255-1260) -> **ledger `IsBlocked`
check (newly added, line ~1265)** -> delete/write overlap refusal -> drift detection (may newly trip
`_sessionHalted`) -> optional re-validation -> lock -> write. Both existing halts read their backing fields
directly rather than through `IsSessionHalted()`/`IsTripped()`, which is why the original `SearchSolutionText`
for those method names found nothing - the enforcement point never calls its own public accessors.
Separately, the MCP request-filter layer (`ServiceRegistrationExtensionsBasic.cs:594-625`) fast-fails on
`IUnrecoverableBreaker.IsTripped()` pre-dispatch, but has no equivalent for `_sessionHalted` or the new
ledger check - noted as a possible follow-up, not required by this plan.

`IsBlocked`'s real semantics (confirmed by writing and running `ScopedOperationLedgerBlockingTests.cs`,
which initially failed against my own wrong assumption): while a ledger is open, its **own** tracked files
stay writable (that's where `RecordFix`'s resolving edits land) and every **other** file is refused - not
the reverse. Decisions 3-5 below should be read with this in mind.

Three MCP tool defects were found and documented while landing Decision 1 (all routed around via the
overnight narrow-bypass authorization, none blocked further progress):
- `docs/current/blockers/blocking_error_changesignature_silent_noop_on_valid_constructor.md` -
  `ChangeSignature` silently returns `status:"no_changes"` instead of erroring when it can't safely
  rewrite existing call sites.
- `docs/current/blockers/blocking_error_git_stage_listed_scope_over_stages_unrequested_file.md` -
  `Git(stage, scope:"listed")` staged a file that was never named in the call, which then landed in
  the wrong commit.
- (Re-confirmed, not newly filed) `Member(addMember)`'s known multi-declaration `newMemberSource`
  silent-partial-write bug, per the already-open `blocking_error_member_addmember_silent_partial_write.md`.

Decision 3 is DONE (commit `8ea7557`). `AdvancedStructuralEngine.PreviewInstanceMoveCallSitesAsync`
implemented as a reporting-only scan - takes an optional `ValidationEngine?` constructor dependency
(DI auto-resolves it; all 13 existing direct-construction test call sites are unaffected since it's
an optional parameter). Uses `SemanticModel.LookupSymbols(position)` for the in-scope-candidate scan
rather than the `FindReferences`/`FindCallers` ctor-parameter-vs-field fix originally cited in the
"Facts to confirm" section below - that fix turned out to be a different, single-node resolver, not
reusable for a scope-walk; `LookupSymbols` was confirmed (via a dispatched Explore subagent) to be
the correct primitive since it natively respects C# shadowing. Accessibility check uses
`DeclaredAccessibility`/`ContainingAssembly` comparison, not `IsSymbolAccessibleWithin` as the
proposal doc's section 6 suggested - that member does not exist on `INamedTypeSymbol` in the
installed Roslyn 5.9.0 (`Microsoft.CodeAnalysis.Workspaces.Common`).

Tests: `RoslynSentinel.Tests.Battery/PreviewInstanceMoveCallSitesTests.cs`, 3 passing
(`Valid`-with-`SuggestedFix`, `Ambiguous`, `NoCandidateIntroducible`). **`MoveOrderDependent` has no
test and may be unreachable as currently implemented** - the obvious fixture (a call inside a member
that's also being moved in the same batch) classifies as `Valid`, not `MoveOrderDependent`: when
caller and callee move together, the call site's line is removed from the trial compile along with
its container, so no diagnostic ever fires there and `isMoveOrderDependent` - checked only after
`brokenHere` is true - is never reached. `NoCandidateBlocked` (inaccessible destination type) also
has no test yet. Both are open follow-ups for whoever next touches this method, not blocking for
Decision 4.

Two tool defects found and documented while landing this (see blocker docs, not the
already-known-and-tracked `Member(addMember)` multi-declaration bug from Decision 1-2 notes above -
this is a different symptom of adjacent machinery):
- `docs/current/blockers/blocking_error_member_addmember_silently_drops_second_declaration.md` -
  `Member(addMember)` silently truncates a `newMemberSource` payload to its first top-level
  declaration (via `SyntaxFactory.ParseMemberDeclaration`) rather than inserting all declarations or
  rejecting multi-declaration payloads up front; the resulting compile-check error blames a missing
  symbol instead of naming the truncation.
- `docs/current/blockers/blocking_error_self_inflicted_drift_halt_from_edit_tool_on_tracked_cs_file.md` -
  self-inflicted `SessionHalted` from using the generic `Edit` tool (not MCP) on a tracked test file;
  recovered in-session via `AcknowledgeExternalFileChanges` (after `ListExternalDiskChanges` confirmed
  the drift list matched exactly the one file involved) rather than needing an operator stop - noted
  as a documentation gap, since neither the halt message nor `IsSessionHalted` mentions this recovery
  path.

Decision 4 is DONE (commits pending this session). `AdvancedStructuralEngine.MoveInstanceMembersAsync`
implemented as a new dedicated method (per the confirmed design choice, not a branch inside the
existing static-only methods): calls `PreviewInstanceMoveCallSitesAsync` for the scan, auto-resolves
`Valid` rows via their `SuggestedFix`, resolves any other row via a `callSiteFixups` entry keyed
`"{FilePath}:{Line}"` (`FilePathWrapper.ToString()` returns `Absolute`, so this is consistent with
`PreviewCallSite.FilePath`), and rejects the whole call with a `ToolNotFoundException` naming every
still-unresolved site + its candidates if anything remains. `"new"` is the fixup shorthand for
constructing the target type inline via a zero-arg `ObjectCreationExpression` - no explicit
constructor-arity validation was added (Roslyn's own post-apply compiler check catches a missing
zero-arg constructor as a real CS error, which is an acceptable failure mode here since it's the same
"the atomic apply already validates" pattern `MoveMember` uses everywhere else, not a silent partial
write). The member-relocation logic (existing-class same-file / existing-class cross-file / new-class
synthesis) is duplicated from `MoveMembersToExistingClassAsync`/`MoveMembersToNewClassAsync` rather
than shared, since those methods' call-site-rewrite loops are static-only and not reusable - flagged
as a candidate for the "Caller-fixup tool audit" follow-up, not done here.

`MoveMemberAsync`'s static-only guard (previously an unconditional throw for any non-static member
moving to a non-base-type destination) now branches: `autoResolveCallSites` (new parameter, default
`true`) routes to `MoveInstanceMembersAsync`; `autoResolveCallSites:false` keeps the original
reject-with-guidance behavior, reworded to mention the new parameter instead of "move to a base class
or make it static" as the only options. The existing/new-class target-resolution block (previously
computed after the static-only throw) was moved earlier so both branches can use it.

Wired through to the `MoveMember` MCP tool (`SentinelAdvancedRefactoringTools.cs`): added
`autoResolveCallSites` (bool, default true) and `callSiteFixups` (`Dictionary<string,string>?`,
default null) parameters with `[Description]` text explaining the fixup key format and the `"new"`
shorthand's zero-arg-constructor requirement; `memberNames`' own `[Description]` updated to drop the
stale "static first" wording. `MoveInstanceMembersAsync` does not call `ValidateAndApplyAsync`
directly - it returns the same `MoveMemberResult` shape the static paths do, so the existing
`MoveMember` tool's single `ValidateAndApplyAsync` call (unchanged) still provides the "one compiler
check, one outcome" atomicity for both static and instance moves alike.

Tests added to `PreviewInstanceMoveCallSitesTests.cs`: `MoveMemberAsync_UnambiguousInstanceMember_AppliesAutomaticallyAsync`
(single in-scope candidate, zero manual input, asserts the caller's rewritten receiver and the
target class's new member both land correctly) and
`MoveMemberAsync_AmbiguousInstanceMemberNoFixup_RejectsWithSpecificErrorAsync` (two same-type
candidates, no fixup, asserts the thrown exception names the specific caller file and mentions
`callSiteFixups`). Both pass; full solution build 0 errors / 0 warnings.

Re-encountered (not a new defect) the `Member(addMember)` multi-declaration truncation bug from
Decision 3's blocker doc while adding these two tests as one call - second test method was silently
dropped, same symptom, same workaround (split into two sequential `addMember` calls).

Next step: Decision 5 (open a ledger for unresolved rows instead of rejecting).

## Facts to confirm once the solution loads (not yet verified this session - blocked)

Before Decision 1, re-verify against live source (per this repo's "verify before theorizing"
discipline) rather than trusting the proposal docs' citations as still-accurate:

- `MoveMember`'s current signature and static-only guard, expected around
  `RoslynSentinel.Server.Advanced/SentinelAdvancedRefactoringTools.cs:413` per
  `proposal_movemember_instance_callsite_resolution.md`'s citation - confirm line number and the
  exact guard condition/error message being replaced.
- `ValidateAndApplyHelper.ValidateAndApplyAsync`'s current signature
  (`RoslynSentinel.Common/ValidateAndApplyHelper.cs`) - confirmed already this session (read in full
  during the design discussion), but re-check for drift if other commits landed between then and
  implementation start.
- `PersistentWorkspaceManager`'s current interface list and line count
  (`RoslynSentinel.Common/PersistentWorkspaceManager.cs`) - confirmed at 2,200+ lines / 9 interfaces
  this session; re-check since Decision 7's tool-class split (recently committed) touched adjacent
  DI wiring and could plausibly have touched this file too.
- Where `FindReferences`' fixed ctor-parameter-vs-field shadowing resolution actually lives (cited in
  `proposal_movemember_instance_callsite_resolution.md` #1 as "reuse that fixed resolution path" but
  not given a file:line in that doc) - locate it before Decision 3 so the auto-resolution scan calls
  it rather than re-implementing scope resolution.

## Decision 1 - `IScopedOperationLedger` interface + `ScopedOperationLedgerEngine`, wired but inert

Land the ledger's plumbing first, with no caller yet - lowest-risk slice, independently testable,
and everything else in this plan depends on it existing.

- New file `RoslynSentinel.Common/LedgerEntryBase.cs`: `LedgerEntryBase` abstract record
  (`EntryId`, `FilePath`, `Line`, `IsFixed`, `ChangeId`) and `CallSiteLedgerEntry` sealed record
  (adds `BrokenExpression`, `OldStaticType`, `Status` (new `CallSiteStatus` enum:
  `Ambiguous`/`NoCandidateIntroducible`/`NoCandidateBlocked`/`MoveOrderDependent` - deliberately no
  `Valid` member, since valid rows never become ledger entries per the proposal doc), `BlockReason`,
  `SuggestedFix`, `CandidatesInScope`) - exact shape per `proposal_scoped_operation_ledger.md`'s
  "Ledger entries" section.
- New file `RoslynSentinel.Common/IScopedOperationLedger.cs`: interface with `TryOpen`,
  `IsBlocked(FilePathWrapper)`, `RecordFix(IReadOnlyList<string> entryIds, string changeId)`,
  `RecordUndo(string changeId)`, `TryRelease`, `GetOpenEntries()` - finalize exact signatures during
  implementation (proposal doc's "Open items" flags this as sketched, not finalized).
- New file `RoslynSentinel.Common/ScopedOperationLedgerEngine.cs`: the engine itself, singleton,
  holding the ledger's in-memory state (a single open ledger's entries, or none) plus the
  single-open-ledger-at-a-time rejection.
- Register in `RoslynSentinel.Server.Advanced/ServiceRegistrationExtensionsAdvanced.cs` alongside the
  other `*Engine` singletons (`services.AddSingleton<ScopedOperationLedgerEngine>();`).
- `PersistentWorkspaceManager` takes a `ScopedOperationLedgerEngine` constructor dependency and adds
  `IScopedOperationLedger` to its interface list, implementing each method as a one-line delegation to
  the injected engine - per the proposal doc, zero new state/fields on `PersistentWorkspaceManager`
  itself, pure pass-through.
- Unit tests for the engine in isolation (open/reject-concurrent/record-fix/record-undo/re-trip/
  release) before wiring anything else to it - this is the piece most worth getting right in
  isolation, since every other step builds on its correctness.

**Deliberately not done in this step:** no caller opens a ledger yet, and `ValidateAndApplyAsync`
does not yet check it. This step should build and pass its own tests with zero behavior change to
any existing tool.

## Decision 2 - Wire `IsBlocked` into the write chokepoint - DONE (commit `f28b459`)

The real chokepoint turned out to be `PersistentWorkspaceManager.ApplyProposedChangesAsync`
(`PersistentWorkspaceManager.cs:1228`), not `ValidateAndApplyHelper.ValidateAndApplyAsync` as this
plan originally assumed - see "Progress as of 2026-09-18" above for the corrected call chain.

- Implemented: for each target in `changes.Keys.Concat(deletePaths)`, ask `_ledger.IsBlocked(target,
  out reason)`; if blocked, return a failed `ApplyChangesResult` (not a thrown exception) naming the
  open ledger operation and the reason.
- Inserted immediately after the `_unrecoverableHaltMessage` check and before the delete/write overlap
  refusal - so `_sessionHalted` and the unrecoverable halt both keep winning unconditionally ahead of
  the ledger check, per `proposal_nonblocking_validation_mode.md`'s ordering requirement.
- Test: `RoslynSentinel.Tests.Battery/ScopedOperationLedgerBlockingTests.cs`, modeled on
  `UnrecoverableBreakerTests.cs` - covers no-ledger writes succeeding, a ledger's own tracked file
  staying writable, and an unrelated file being refused. All 3 pass.

**Still no caller opens a ledger for real** - this step proves the gate works using a
directly-engineered test ledger, not a real `MoveMember` call yet.

## Decision 3 - `MoveMember` instance-move dry-run: `PreviewCallSite` report - DONE (commit `8ea7557`)

Implements `proposal_movemember_instance_callsite_resolution.md` sections 1, 5, 6. See "Progress as of
2026-09-18" above for what actually landed vs. this section's original plan (kept below for the
record of what was intended going in).

- Locate every call site of the member(s) being moved (reuse existing `FindReferences`/call-site
  discovery machinery - do not write a second one).
- For each call site, build the proposed change set (member moved, that one call site *not yet*
  rewritten) and run it through `ValidationEngine.ValidateChangesAsync` per section 5's design -
  the compiler's diagnostics are the source of truth for "does this call site break," not a
  hand-rolled prediction.
- Cross-reference each diagnostic's location against an in-scope-candidate scan (reusing the fixed
  ctor-parameter-vs-field shadowing resolution located in the "Facts to confirm" step above) to
  classify into `Valid`/`Ambiguous`/`NoCandidateIntroducible`/`NoCandidateBlocked`/
  `MoveOrderDependent`, per section 6's accessibility check (`IsSymbolAccessibleWithin`) for the two
  no-candidate sub-cases.
- Emit one `PreviewCallSite` row per call site (`FilePath`, `Line`, `CallExpression`, `Status`,
  `BlockReason?`, `SuggestedFix?`, `Candidates`) regardless of `dryRun` value - `dryRun: true` returns
  the full report without applying; a real call uses the same rows to drive Decision 4.
- This step lands the *reporting* capability only. `MoveMember`'s static-only guard is not yet lifted
  - a real (non-dry-run) call to move an instance member still rejects, same as today. This isolates
  "can we accurately predict blast radius" from "do we act on that prediction," so the harder half
  (Decision 4/5) can be reviewed against real preview output from real codebases first.
- Test against a fixture with a mix of: an unambiguous single-candidate call site, a
  multiple-candidate ambiguous one, a missing-using-directive one, an inaccessible-type one, and one
  call site inside a method that's itself being moved in the same batch (`MoveOrderDependent`).

## Decision 4 - Lift the static-only restriction: auto-resolution + atomic apply of `Valid` rows - DONE

See "Progress as of 2026-09-18" above for what actually landed. Kept below for the record of what
was intended going in.

Implements `proposal_movemember_instance_callsite_resolution.md` sections 1-3, behind the
`autoResolveCallSites` switch from the "Risk posture" section (default `true`).

- Real (non-dry-run) `MoveMember` call for an instance member: run the same scan as Decision 3, then
  apply the move plus every `Valid` row's rewrite as a single atomic change set through
  `ValidateAndApplyAsync` (one compiler check, one outcome - per the existing "atomic change" framing
  `MoveMember` already uses for its static case).
- `callSiteFixups` parameter (`Dictionary<string, string>` keyed by call-site identifier, values
  either a reference-expression string or the `"new"` shorthand) applied in the same atomic write,
  per section 2 - including the strict zero-arg-constructor-only contract for `"new"`, failing loudly
  and specifically (not inferring constructor arguments) if violated.
- Any row not resolved by auto-resolution or `callSiteFixups` becomes a **candidate ledger entry** -
  wired to the ledger in Decision 5, not this step. Until Decision 5 lands, an unresolved row after
  auto-resolution + fixups should cause the whole call to reject (same fail-safe behavior as today's
  static-only restriction, just with a much more specific, actionable error) rather than silently
  applying a partial move - do not ship "some call sites fixed, others silently left broken" as an
  intermediate state even temporarily.
- Test: unambiguous-only batch applies cleanly with zero manual input; a batch with one ambiguous
  site and no `callSiteFixups` entry for it rejects with a specific, actionable error naming that
  site and its candidates.

## Decision 5 - Open a ledger for unresolved rows instead of rejecting

Connects Decision 4's "reject if anything unresolved" fallback to the real ledger from Decisions 1-2.

- Replace Decision 4's temporary reject-on-unresolved behavior: instead, apply the move + every
  `Valid`/`callSiteFixups`-resolved row atomically (same as Decision 4), then open a ledger seeded
  with one `CallSiteLedgerEntry` per still-unresolved row, carrying forward `Status`/`BlockReason`/
  `SuggestedFix`/`CandidatesInScope` from the `PreviewCallSite` data computed in the same call - no
  recomputation, per the proposal doc's explicit "doesn't recompute anything the dry-run scan already
  worked out" design.
- Report the open ledger to the caller in the `MoveMember` result (ledger ID, entry count, the
  entries themselves) - this is the caller's worklist.
- Test: a batch with 2 unresolved sites opens a ledger with exactly 2 entries; a subsequent
  unrelated-file `ApplyDiff`/`Member`/etc. call is rejected per Decision 2's gate; a call touching one
  of the ledger's files is accepted and, once it resolves that entry (however - delegating stub,
  `ReplaceSnippet`, manual edit), the ledger's remaining-entry count drops by one (or more, if the fix
  touched multiple sites under one `changeId`); resolving the last entry releases the breaker
  automatically.

## Decision 6 - `UndoLastApply` / re-tripping integration

- `UndoLastApply`'s existing implementation gets one addition: after undoing a change, call
  `((IScopedOperationLedger)workspaceManager).RecordUndo(changeId)` unconditionally - per the
  proposal doc, this needs no special case distinguishing "undid the original move" from "undid a
  fix," the ledger sorts that out from which entries carry the given `changeId`.
  - Undoing the original move's `changeId` should cascade-invalidate every entry that depends on it.
  - Undoing a later fix's `changeId` flips just that entry (or entries) back to `IsFixed: false` and
    re-trips the scoped breaker if it had already released.
- Test: open a ledger, resolve all entries (breaker releases), undo the last fix -> breaker re-trips;
  undo the original move -> ledger's dependent entries are invalidated/cleared per whatever cascade
  behavior gets implemented (finalize the exact user-visible shape of "invalidated" during this step
  - the proposal doc says this "falls out of ledger semantics for free" but doesn't specify the exact
  API surface for an invalidated-vs-fixed entry; decide and document here rather than leaving it
  implicit).

## Explicitly out of scope for this plan

- `proposal_nonblocking_validation_mode.md`'s report-don't-reject mode - independent feature, not a
  prerequisite or dependency of anything above.
- `proposal_splitfacademember_tool.md` - related but separate tool; not touched here.
- Extending the ledger to non-`MoveMember` operations (e.g. a future `SplitFacadeMember`
  helper-left-behind tracker) - the proposal doc's `LedgerEntryBase` extension point is designed for
  this, but no second consumer is built in this plan. Adding one later should not require changing
  `LedgerEntryBase`, `IScopedOperationLedger`, or `ScopedOperationLedgerEngine` - if it does, that's a
  sign Decision 1's abstraction was wrong and worth revisiting before proceeding.
- Ledger persistence across server restarts - proposal doc leans session-scoped (matching
  `_sessionHalted`); this plan builds it session-scoped and does not attempt disk persistence
  (contrast with `MigrationLedger`'s cross-run persistence, which is a different, already-shipped
  component solving a different problem).

## Status

Plan drafted 2026-09-18, overnight session, before any implementation started - blocked immediately
by `blocking_error_loadsolution_missing_msbuild_workspaces_assembly.md`. No steps above have been
implemented. Decision numbering intentionally leaves room for the "Facts to confirm" re-verification
pass to surface a needed Decision 0 (e.g. if `MoveMember`'s actual current signature has drifted
further from the proposal docs' citations than expected).
