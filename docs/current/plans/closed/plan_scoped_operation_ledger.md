# Implement the scoped operation ledger + MoveMember instance-callsite resolution

**Status:** CLOSED 2026-11-08. Historical record filed under closed/ by the docs reorganization; the body keeps the original outcome.

**Merged from:** proposal_scoped_operation_ledger, proposal_movemember_instance_callsite_resolution

## Context

Three design docs, written across one session (2026-09-17/18) of iterative review, describe a
coherent feature that is not yet implemented:

- `docs/current/plans/closed/plan_scoped_operation_ledger.md` - lifts `MoveMember`'s
  static-members-only restriction for instance moves, via auto-resolution of unambiguous call sites,
  a `callSiteFixups` map for ambiguous ones, and a `dryRun: true` preview (`PreviewCallSite` rows,
  one per call site, `Status` of `Valid`/`Ambiguous`/`NoCandidateIntroducible`/`NoCandidateBlocked`/
  `MoveOrderDependent`) built from the same in-memory candidate-solution mechanism
  `ValidateAndApplyHelper.ValidateAndApplyAsync` already uses before its `dryRun` check.
- `docs/current/plans/closed/plan_scoped_operation_ledger.md` - a `ScopedOperationLedgerEngine` that tracks
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

`docs/current/blockers/closed/blocking_error_loadsolution_missing_msbuild_workspaces_assembly.md`:
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
directly rather than through `ExternalFileDrift`'s Status operation or `IsTripped()`, which is why the original `SearchSolutionText`
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
  recovered in-session via `ExternalFileDrift(operation: Acknowledge)` (after `ExternalFileDrift(operation: List)`
  confirmed the drift list matched exactly the one file involved) rather than needing an operator stop - noted
  as a documentation gap, since neither the halt message nor `ExternalFileDrift`'s Status operation mentioned this
  recovery path at the time.

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

Decision 5 is DONE. See its own section below for what landed - the key correction was that `TryOpen`
had to move out of the engine method and into the MCP tool layer, called only after the atomic apply
succeeds, to avoid the newly-opened ledger immediately self-blocking that same apply's write to the
moved member's own source/target files.

Two session-level blockers were hit and resolved between Decision 4 and Decision 5, unrelated to any
code in this plan:
`docs/current/blockers/blocking_error_mcp_server_disconnected_connection_closed.md` (server process
disconnected entirely; resolved by reconnect) and
`docs/current/blockers/blocking_error_reconnected_server_is_wrong_flavor_basic_not_advanced.md` (the
reconnected instance was running `Server.Basic` in the Advanced slot, per an uncommitted edit to
`scripts/roslynsentinel-mcp-launch.ps1` from outside this session; resolved when the operator reverted
that edit and restarted VS Code, confirmed via `serverBinaryPath` in the next tool response).

Next step: Decision 6 (`UndoLastApply` / re-tripping integration).

## Facts to confirm once the solution loads (not yet verified this session - blocked)

Before Decision 1, re-verify against live source (per this repo's "verify before theorizing"
discipline) rather than trusting the proposal docs' citations as still-accurate:

- `MoveMember`'s current signature and static-only guard, expected around
  `RoslynSentinel.Server.Advanced/SentinelAdvancedRefactoringTools.cs:413` per
  `plan_scoped_operation_ledger.md`'s citation - confirm line number and the
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
  `plan_scoped_operation_ledger.md` #1 as "reuse that fixed resolution path" but
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
  `SuggestedFix`, `CandidatesInScope`) - exact shape per `plan_scoped_operation_ledger.md`'s
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

Implements `plan_scoped_operation_ledger.md` sections 1, 5, 6. See "Progress as of
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

Implements `plan_scoped_operation_ledger.md` sections 1-3, behind the
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

## Decision 5 - Open a ledger for unresolved rows instead of rejecting - DONE

Connects Decision 4's "reject if anything unresolved" fallback to the real ledger from Decisions 1-2.
See "Progress as of 2026-09-18" above (to be extended) for what actually landed; kept below for the
record of what was intended going in.

**Design correction found during implementation, not anticipated in the original bullets below:**
`TryOpen` cannot be called from inside `MoveInstanceMembersAsync` itself. That method only *proposes*
a change set (`MoveMemberResult.Changes`) - the actual write happens later, back in the `MoveMember`
MCP tool, via `ValidateAndApplyAsync` -> `ApplyProposedChangesAsync`. If the ledger were opened before
that write, Decision 2's `IsBlocked` gate (`PersistentWorkspaceManager.cs:1265`) would immediately
refuse the move's own source/target-file write in the *same* apply: `IsBlocked` only allows a file
through if it either has an unresolved entry for itself or there are no unresolved entries at all,
and the source/target class files being moved essentially never coincide with an unresolved call
site's file. Caught before it shipped by tracing the exact `IsBlocked` call site rather than assuming
"open the ledger, then apply" would work.

Fix: `MoveInstanceMembersAsync` now builds the `CallSiteLedgerEntry` list but does not call `TryOpen`
- it returns them via two new fields on `MoveMemberResult` (`PendingLedgerEntries`,
`PendingLedgerOperationName`). The `MoveMember` MCP tool calls `TryOpen` itself, after
`ValidateAndApplyAsync` has already succeeded, and only when `!dryRun` (a dry-run writes nothing, so
opening a ledger for it would track entries against a change that never landed). `SkippedCallSite`
(pre-existing record, previously always empty for this path) is now populated from the same
unresolved rows and surfaces in the tool's `summaryNote`, alongside a new line reporting whether the
ledger opened successfully or - the one remaining fail-safe case - had to fall back to a manual-fix
warning because another ledger was already open (`TryOpen` returning false).

Tests: `MoveMemberAsync_AmbiguousInstanceMemberNoFixup_ReturnsPendingLedgerEntryAsync` (replaces
Decision 4's now-obsolete reject test - the engine call no longer throws for an unresolved row, it
returns `PendingLedgerEntries`) and
`MoveMemberAsync_UnresolvedCallSite_OpensLedgerThatBlocksUnrelatedFileAsync` (the real end-to-end
path: apply the move's `Changes`, then `TryOpen` the returned pending entries, then confirm an
unrelated file is refused per Decision 2's gate while the ledger entry's own tracked file stays
writable). Both pass; full Battery-fixture run 9/9. Full-solution run afterward showed one unrelated,
pre-existing flaky failure (`McpServerStatus_Call_PopulatesStructuredContent_MatchingTextContent`,
not touched this session, fails under full/parallel runs but passes in isolation) plus environmental
LM Studio-connection failures in `RoslynSentinel.Tests.ModelEval` (no LM Studio server running this
session) - neither caused by this change.

Original plan bullets, for the record:

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

## Decision 6 - `UndoLastApply` / re-tripping integration - DONE

- Design correction found during implementation: the proposal doc's claim that cascade-invalidation
  "falls out of ledger semantics for free" from `RecordUndo(changeId)` matching `entry.ChangeId` is
  **not actually true** as `ScopedOperationLedgerEngine` was built through Decision 5.
  `CallSiteLedgerEntry.ChangeId` starts `null` and is only ever set by `RecordFix` (the *fix's* own
  changeId) - the ledger had no record anywhere of the changeId that *opened* it (the original
  move's `apply.ChangeId` from `MoveMember`). So `RecordUndo(moveChangeId)` against the
  Decision-5-era engine would match zero entries and silently no-op instead of cascading - verified
  by reading `ScopedOperationLedgerEngine.RecordUndo` and `TryOpen` source directly before writing
  any test, not inferred from the proposal doc's description.
  - Fix: `IScopedOperationLedger.TryOpen` gained an optional `string? openingChangeId = null`
    parameter; `ScopedOperationLedgerEngine` stores it in a new `_openingChangeId` field (cleared by
    `TryRelease`). `RecordUndo(changeId)` now checks `changeId == _openingChangeId` first - if true,
    it flips **every** entry back to `IsFixed = false` regardless of that entry's own `ChangeId`
    (including entries already individually fixed by a *different* changeId); otherwise it falls
    back to the original per-entry `entry.ChangeId == changeId` match, unchanged from Decision 5.
  - "Invalidated" decided to mean exactly "unresolved again" (`IsFixed = false`), reusing the
    existing state rather than adding a third entry state - simplest option that still re-trips
    `IsBlocked`, and per-entry data (`BrokenExpression`, `SuggestedFix`, etc.) stays intact for
    whoever re-resolves it, which a "cleared" entry would have lost.
  - `SentinelAdvancedRefactoringTools.MoveMember` now passes `apply.ChangeId` (the move's own
    changeId, already in scope right before the `TryOpen` call) as `openingChangeId`.
  - `WorkspaceFileEditImpl.UndoLastApply` calls
    `((IScopedOperationLedger)_workspaceManager).RecordUndo(changeId)` unconditionally after a
    successful revert - a no-op when no ledger is open or `changeId` matches nothing tracked, same
    pattern as the proposal doc intended, just now backed by real opening-changeId tracking.
- Second defect caught only by running the full solution, not just the new fixture:
  `RoslynSentinel.Tests.Battery.UndoLastApplyTests` uses `FakeWorkspaceManager`, which implemented
  none of `IScopedOperationLedger` - `UndoLastApply`'s new unconditional
  `((IScopedOperationLedger)_workspaceManager).RecordUndo(...)` cast threw `InvalidCastException`
  (caught by the method's own try/catch, surfacing as `Success = false`) on every fake-backed test,
  including ones with nothing to do with the ledger. Fixed by giving `FakeWorkspaceManager` a real
  (not throw-stub) `IScopedOperationLedger` implementation - same rationale already documented next
  to its existing `IUnrecoverableBreaker.Trip` implementation: a cross-cutting interface that a
  newly-unconditional call site now touches on every invocation cannot be a `NotImplementedException`
  stub without breaking every fake-backed test that exercises that call site, whether or not the
  test cares about the ledger.
- Tests added to `ScopedOperationLedgerBlockingTests.cs` (exercises `TryOpen`/`RecordFix`/
  `RecordUndo`/`TryRelease` directly against a real `PersistentWorkspaceManager`, no file I/O
  needed for these two):
  - `RecordUndo_OfFixChangeId_ReTripsBreakerAfterRelease` - resolve the only entry (breaker
    releases via `TryRelease`), undo that fix's own changeId, assert the entry is unresolved again
    and `TryRelease` now fails.
  - `RecordUndo_OfOpeningChangeId_InvalidatesEveryEntryEvenIfAlreadyFixed` - two entries, fix one,
    undo the *opening* changeId, assert both entries (including the already-fixed one) are
    unresolved and `TryRelease` fails.
  - Full `RoslynSentinel.Tests.Battery.UndoLastApplyTests` fixture (6 tests, including the
    real-revert-against-`PersistentWorkspaceManager` test) also re-run and passing after the
    `FakeWorkspaceManager` fix.
- Full-solution test run after both fixes: 2454/2567 passed, 19 failed - all 19 pre-existing/
  environmental (15 ModelEval `LM Studio request failed with BadRequest` - no LM Studio server
  running this session; 2 `TaskCapableClient` sync/async equivalence failures; 1
  `T5_GetLargeResult_ReadsFile_PagingWorks_TotalRecordsMatchesT4` NullReferenceException; 1
  `McpServerStatus_Call_PopulatesStructuredContent_MatchingTextContent`, the documented
  full-solution-only flaky test) - none touch ledger code and none regressed by this decision's
  changes; `RoslynSentinel.Tests.Battery` went from 2 failures (before the `FakeWorkspaceManager`
  fix, one being the `UndoLastApply` regression) to 1 (just the flaky test).

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

---

## Merged: ScopedOperationLedgerEngine: tracking blast-radius operations to completion without bypassing validation

### Motivation

`plan_scoped_operation_ledger.md` and `proposal_nonblocking_validation_mode.md`
both hit the same underlying wall from different directions: an operation with a legitimately large
blast radius (an instance member move, a coordinated multi-file type change) cannot be both (a) safe
- never leaving the solution in a state the caller loses track of - and (b) achievable without either
rejecting outright (today's `MoveMember` instance-move behavior) or asking `ValidateAndApplyAsync` to
accept broken intermediate state (the non-blocking-validation-mode proposal's opt-in bypass).

This doc proposes a third path for this specific shape of problem: apply the causing change
atomically, surface every resulting compile break as a tracked, individually-resolvable worklist
item, and mechanically prevent unrelated work from proceeding until every item is resolved - without
ever turning off the compiler-error gate itself. Validation stays strict throughout; what changes is
the *scope* of what a rejection blocks.

### Core idea

For an operation like an instance `MoveMember` move:

1. **Discover call sites before applying anything**, via the same `dryRun: true` scan proposed in
   `plan_scoped_operation_ledger.md` (its `PreviewCallSite` report, one row per
   call site with a `Status` of `Valid`/`Ambiguous`/`NoCandidateIntroducible`/`NoCandidateBlocked`/
   `MoveOrderDependent`). This is strictly better than discovering breakage after the fact from
   post-apply compiler diagnostics: it costs nothing extra (the detection scan already has to visit
   every call site to know what to report), it never leaves the solution in a broken state even
   transiently, and it distinguishes cases - like `NoCandidateBlocked`'s real architectural
   inaccessibility - that a bare CS1061 diagnostic alone can't tell apart from an ordinary ambiguous
   case. For a tool with no equivalent pre-apply scan, post-apply `ValidationEngine.ValidateChangesAsync`
   diagnostics remain an acceptable fallback seeding source (file, line, and diagnostic are still real,
   compiler-sourced data) - but pre-apply discovery is the preferred path wherever the tool supports it.
2. Apply the move as a single atomic write: every `Valid` row's call site is rewritten as part of the
   same write as the member relocation itself. Nothing left in a `Valid` state before apply requires
   any tracking after apply - it's just done.
3. **Open a ledger** seeded with one entry per non-`Valid` row: `Ambiguous`, `NoCandidateIntroducible`,
   `NoCandidateBlocked`, or `MoveOrderDependent`. Report it to the caller as the affected-call-site list
   - carrying forward the same `BlockReason`/`SuggestedFix`/`Candidates` data the dry-run preview
   already computed, so nothing needs to be re-derived between preview and real run.
4. Trip a **scoped** circuit breaker: while the ledger has unresolved entries, `ValidateAndApplyAsync`
   only accepts mutating calls that touch a file appearing in the ledger. Anything else is rejected,
   with a message naming the open ledger and pointing at its remaining entries.
5. Each subsequent call that resolves a call site - a delegating-stub fix, a throwing placeholder, a
   manual `ReplaceSnippet`, an auto-resolved field reference, whatever the reviewer chooses per site -
   validates normally against that file's own remaining errors. The resolution strategy is
   deliberately not the ledger's concern; see "Relationship to MoveMember's resolution strategies"
   below.
6. The breaker releases automatically once every entry is resolved. No manual reset step, unlike
   `IManualCircuitBreaker`.

The caller (human or agent) decides *how* to resolve each site. The ledger's only job is making sure
none get silently forgotten and nothing unrelated proceeds while they're open.

A real, non-dry-run `MoveMember` call and its own `dryRun: true` preview should produce the *same*
`PreviewCallSite` rows for the non-`Valid` cases - the real call just additionally applies the `Valid`
rows and opens a ledger from the rest, rather than only reporting them. This symmetry is what makes the
dry-run genuinely predictive: a caller who reviews the preview and proceeds should never be surprised
by what the ledger contains afterward.

### Ledger entries: base type + per-operation extension

Different blast-radius operations produce structurally different "what's broken" data - an instance
move's call-site entries need a broken expression, its static type, and in-scope candidates; a future
`SplitFacadeMember`-style helper-left-behind tracker (`project_caller_fixup_tool_audit_idea.md`) would
need a helper name and which still-resident members keep it alive; something else again may need a
third shape entirely.

Rather than one flat DTO accumulating optional fields per new operation type - the same unbounded-
width growth already visible in `PersistentWorkspaceManager` itself - the ledger uses a common base
record with sealed per-operation extensions, following the existing `EngineResultBase` convention
already used across `RoslynSentinel.Common` (`BatchResultSummary`, `BestInsertionResult`,
`BridgeBatchResult`, etc. all extend it; `MigrationEnvelope<T>` shows this codebase is already
comfortable with a generic envelope over a variable payload for the same class of problem).

```csharp
public abstract record LedgerEntryBase
{
    public required string EntryId { get; init; }
    public required string FilePath { get; init; }
    public required int Line { get; init; }
    public bool IsFixed { get; init; }
    public string? ChangeId { get; init; }
}

public sealed record CallSiteLedgerEntry : LedgerEntryBase
{
    public required string BrokenExpression { get; init; }
    public required string OldStaticType { get; init; }
    public required CallSiteStatus Status { get; init; }   // Ambiguous | NoCandidateIntroducible | NoCandidateBlocked | MoveOrderDependent - never Valid, those never become entries
    public string? BlockReason { get; init; }
    public string? SuggestedFix { get; init; }
    public IReadOnlyList<string> CandidatesInScope { get; init; } = [];
}
```

`Status`/`BlockReason`/`SuggestedFix` are carried straight over from the `PreviewCallSite` row that
seeded this entry (`plan_scoped_operation_ledger.md` #5-6) - the ledger doesn't
recompute anything the dry-run scan already worked out, it just adds the fix-tracking fields on top.

`IsFixed`/`ChangeId` live on the base since every entry, regardless of operation type, needs the same
fix-tracking and undo-reconciliation; only the description of *what's broken* varies per operation.

#### Entries are a ledger, not a queue

Resolved entries are never removed - `IsFixed` flips `true` and `ChangeId` records which applied
change resolved it, but the entry stays. This makes the collection append-only and self-auditing:

- **Breaker release** becomes "fold over all entries, all `IsFixed`" rather than "collection is
  empty" - same effect, no separate history needed to reconcile against.
- **`UndoLastApply` needs no special case.** It looks up the `changeId` being undone, finds every
  ledger entry carrying that `ChangeId` (one fix call can resolve more than one call site at once,
  e.g. a `ReplaceSnippet` batch touching two call expressions in one method - `ChangeId` is
  many-to-one, not 1:1), and flips `IsFixed` back to `false`. This is the same mechanism whether the
  undone change was the original move or a later fix to entry #3.
- **Re-tripping.** If an undo flips an entry back to unfixed after the breaker had already released,
  the breaker re-trips. This is a real state transition to build for, not an edge case - it's the
  same "a run that ends green must be distinguishable from one that never broke" concern
  `proposal_nonblocking_validation_mode.md`'s audit-trail section already raises.
- **Abandoning a move.** Undoing the *original* move's `ChangeId` should cascade-invalidate every
  entry that depends on it (their `ChangeId`s only make sense relative to a move that still exists).
  This falls out of ledger semantics for free rather than needing a bolt-on "abandon" operation.

### Single ledger at a time

The ledger rejects opening a second concurrent ledger outright while one is open. Allowing two
in-flight blast-radius operations simultaneously just enables causing two breakages and losing track
of - or getting distracted out of finishing - the first one while working the second. A second
`MoveMember`-style call that would open a new ledger is rejected with a pointer at the ledger already
open, the same rejection shape used for an unrelated file blocked by the open ledger.

### Owning engine, not a tenth interface on `PersistentWorkspaceManager`

`PersistentWorkspaceManager` (`RoslynSentinel.Common/PersistentWorkspaceManager.cs`) is a single
2,200+ line `partial` class (partial in name only - one file) already implementing nine interfaces:
`IDisposable`, `IWorkspaceManager`, `ISolutionProvider`, `IManualCircuitBreaker`,
`IAutomaticCircuitBreaker`, `IUnrecoverableBreaker`, `IWorkspaceHealthReporter`, `IWorkspaceMutator`,
`IRateLimiter`, `ISymbolResolver`. It already carries file-watching, changeset caching, three flavors
of breaker state, symbol resolution, and batch-outcome tracking as fields/methods on one object. This
is exactly the god-class shape the Decision 7 `*Tools`/`*Impl` DI-split plan
(`project_di_tool_split_plan_2026_09_05`) is actively fixing elsewhere in the codebase - adding the
ledger as a tenth interface here would be new coupling in the wrong direction while every sibling
class is being decoupled.

The ledger's actual dependency surface is thin: read "is this file part of an open ledger's entries,"
write "record a fix / record an undo." It doesn't need file-watching, symbol resolution, or changeset
caching - none of `PersistentWorkspaceManager`'s other concerns. That makes it a genuine candidate for
extraction rather than the kind of superficial, naming-driven grouping flagged as a risk in
`project_di_engine_audit_open_question` (memory) - the ledger's cohesion is by shared usage, not just
by name.

Proposed: a new `ScopedOperationLedgerEngine`, singleton-registered the same way every other `*Engine`
in this codebase is (`services.AddSingleton<ScopedOperationLedgerEngine>()`, alongside `MetricsEngine`,
`SecurityEngine`, etc. in `ServiceRegistrationExtensionsAdvanced.cs`), implementing a new
`IScopedOperationLedger` interface. `PersistentWorkspaceManager` holds a reference to the engine and
exposes a thin pass-through cast, matching the existing call pattern at the one confirmed precedent
site: `ValidateAndApplyHelper.cs:93` already does
`((IUnrecoverableBreaker)workspaceManager).Trip(operationName, changeId, reason)` - a cast on the
`workspaceManager` parameter `ValidateAndApplyAsync` already receives, not a new parameter. Keeping
`((IScopedOperationLedger)workspaceManager)...` as the call shape means `ValidateAndApplyAsync`
doesn't fork into two different conventions for reaching cross-cutting state - one cast-based, one
parameter-based - while the actual ledger state and logic live in the new engine, not on
`PersistentWorkspaceManager`. Net effect on the god-class: zero new state, zero new fields - pure
delegation, and a first concrete step toward narrowing it rather than the more common direction of
growth.

`ValidateAndApplyAsync` (`RoslynSentinel.Common/ValidateAndApplyHelper.cs`) gets one more check
alongside its existing `_sessionHalted`/breaker checks, before apply: ask the ledger whether the
target file is blocked by an open ledger's unresolved entries; if so, reject with the ledger's own
report instead of a generic validation error.

### Relationship to MoveMember's resolution strategies

This ledger design is orthogonal to *how* an individual call site gets fixed - it's the tracking and
gating layer underneath whichever resolution approach `plan_scoped_operation_ledger.md`
settles on (auto-resolution of the unambiguous case, `callSiteFixups` map entries, a delegating-stub
field, a throwing placeholder, or plain manual edits). Each of those is just "a call that resolves one
or more ledger entries." The ledger doesn't pick a strategy; it makes sure none of them can be applied
and then forgotten.

### Relationship to the existing Asyncify bridge/ledger machinery

`SentinelAsyncifyTools.cs` and `MigrationLedger.cs` (`RoslynSentinel.Common/MigrationLedger.cs`) are
the closest existing precedent for a large-scale batch refactor that avoids breakage, and are worth
being precise about rather than assuming they already solve this proposal's problem.

**What Asyncify already validates:** `BridgeAsyncMethods` converts `Foo()` into an `[Obsolete(...
"Asyncify-bridge" ...)]` sync wrapper plus a new `FooAsync()` body - the old member never disappears,
it becomes a thin, marked-obsolete delegator. `UpliftCallers` then rewrites every caller of the
obsolete wrapper to call the async method directly, *in the same tool call*, with its own per-call-site
success/fail/skip accounting (`OperationItemRecord`/`FailureDetail`). This is idea 2 from the
`MoveMember` discussion (delegating stub instead of a throwing placeholder), already running at scale
in production in this repo - real evidence that a delegating-stub default is the right resolution
strategy to prefer over a throwing placeholder for `MoveMember`'s instance case too.

**Why it doesn't need a scoped ledger:** `UpliftCallers` never leaves an incomplete migration staged
across turns - bridge and uplift happen in the same call, eagerly, with immediate accounting, so there
is no window in which a caller could walk away from a half-fixed operation. A caller Asyncify can't
safely rewrite mechanically (e.g. a sync-only call context) is simply left on the obsolete wrapper and
surfaces as `PendingObsoleteCallers` in `GetAsyncMigrationProgress` - a read-only progress signal, not
a gate. Nothing about Asyncify's design blocks unrelated mutating calls while pending callers exist.
This works specifically *because* Asyncify's rewrite is mechanical and unambiguous (every caller of the
obsolete wrapper gets the same rewrite); it sidesteps `MoveMember`'s hard case rather than solving it.
This proposal's scoped ledger exists for exactly the case Asyncify doesn't have: call sites where the
correct rewrite is not mechanically determined and genuinely needs a per-site human/agent judgment call
(`plan_scoped_operation_ledger.md`'s ambiguous-receiver problem).

**What `MigrationLedger` already is, precisely:** cross-run, cross-session observability - has this
method already been touched, by which phase, how many times (`HitCount > 1` flags re-entry/thrashing).
It never blocks a subsequent call and every entry it holds already represents a completed action
(success or a deliberate idempotency skip), never an open item awaiting resolution. It is not a
worklist and doesn't need to become one - but its cross-run persistence-to-disk design
(`.roslynsentinel/migration-ledger.json`, survives server restarts) is worth reusing as a pattern if
`ScopedOperationLedgerEngine`'s "does ledger state need to survive a restart" open item (below) is ever
answered yes.

### Interaction with non-blocking validation mode

Complementary, not overlapping, with `proposal_nonblocking_validation_mode.md`: that proposal lets a
*known-coordinated* multi-file change land incrementally without per-call rejection, and needs its own
stall backstop because it removes the per-call compile gate entirely for its scope. This proposal
never removes the gate - every call is still validated strictly - it only narrows *what's allowed to
be attempted* while a ledger is open. The two could compose (a ledger-tracked operation resolved via a
non-blocking-mode multi-file fix), but neither depends on the other landing first.

### Open items

- Exact trip/query API shape for `IScopedOperationLedger` (`TryOpen`, `IsBlocked(filePath)`,
  `RecordFix(entryIds, changeId)`, `RecordUndo(changeId)`, `TryRelease`) - sketched above, not
  finalized against real call-site signatures.
- Whether ledger state needs to persist across server restarts, or is acceptably session-scoped like
  `_sessionHalted` - leaning session-scoped, since a restart mid-ledger is already an unusual enough
  event that requiring the caller to re-survey with a fresh `MoveMember`/validation pass seems
  acceptable, but not decided.
- Whether other tools beyond `MoveMember` (`SplitFacadeMember`'s helper-left-behind tracking; see
  `project_caller_fixup_tool_audit_idea` memory) should feed the same ledger via their own
  `LedgerEntryBase` subtype, or get their own ledger instance - leaning same ledger given the
  single-open-ledger-at-a-time rule already assumes one global scope to protect.

### Original status

Design proposal only - not yet implemented or scheduled. Grew out of a design discussion following
`plan_scoped_operation_ledger.md`; cross-referenced from that doc and from
`proposal_nonblocking_validation_mode.md`. Also referenced from the `project_di_engine_audit_open_question`
memory as a candidate first step in narrowing `PersistentWorkspaceManager`.

---

## Merged: MoveMember: resolving instance-member call sites (design notes)

`MoveMember` (RoslynSentinel.Server.Advanced/SentinelAdvancedRefactoringTools.cs:413) currently
restricts destinations (2) unrelated existing class and (3) synthesized new class to **static
members only**. Moving an instance member to either destination is rejected outright. This doc
captures the reasoning for that restriction and a refinement design to lift it safely, from a
conversation following the `WorkspaceReadNavigationTools`/`Impl` trial-slice migration (see
`plan_split_workspace_refactoring_tools_for_di.md`).

### Why instance moves are hard

```csharp
var classCLocalVarA = new ClassA();
classCLocalVarA.Foo();   // ClassA.Foo() moved to ClassB.Foo()
classCLocalVarA.Bar();   // ClassA.Bar() stays put
```

After moving `Foo` to `ClassB`:
- Leaving `classCLocalVarA.Foo()` unchanged breaks — `ClassA` no longer declares `Foo`.
- Rewriting the declaration to `new ClassB()` breaks the *other* call, `Bar()`, which only exists on
  `ClassA`.

There is no single mechanical rewrite of the call site that's correct in general — it depends on
whether some other reachable expression of the destination type already exists at that call site,
and if not, on a judgment call only the caller (human or agent) can make. This is why static moves
are simple (no receiver to resolve — `ClassA.Foo()` → `ClassB.Foo()` is unconditionally correct) and
instance moves aren't.

### Design: auto-resolve the unambiguous case, defer the rest

#### 1. Auto-detect the trivial case

If the calling class already has a reachable expression of the destination type in scope at the call
site — a field, a property, or a parameter — that's a lookup, not a judgment call, and can be
rewritten automatically:

```csharp
public class ClassC
{
    private readonly ClassB _classB;   // <- reachable, unambiguous
    public void Do()
    {
        var a = new ClassA();
        a.Foo();   // rewrite to _classB.Foo() automatically
    }
}
```

Judgment is needed only when **no** such reference exists anywhere in scope, or when more than one
candidate exists and none is clearly preferred.

**Implementation risk to design around up front:** symbol resolution here must respect normal C#
shadowing rules (a closer-scope local/parameter of the destination type shadowing an outer field of
the same type, etc.) — this exact class of bug (field vs. constructor-parameter disambiguation) was
previously found and fixed in `FindReferences` (see
`CLOSED.md`'s closed `FindReferences`/`FindCallers` ctor-parameter-vs-field entry). Reuse that fixed resolution path rather
than writing a second, independent scope-walk for this feature — the bug class is proven to be easy
to get subtly wrong.

#### 2. `callSiteFixups` map for the genuinely ambiguous cases

For call sites with no unambiguous auto-resolution, accept a caller-supplied map (proposed shape:
`Dictionary<string, string>` keyed by a call-site identifier — e.g. `"File.cs:42"` — to a
resolution). The resolution value should accept:

- A **reference expression string** (e.g. `"_myClassBField"`) — use this expression as the new
  receiver.
- The **shorthand `"new"`** — construct a destination instance inline at the call site.

This keeps the common case a one-word answer per flagged site rather than requiring the caller to
hand-write C# expressions for every ambiguous call.

`"new"` needs a strict, documented contract: **zero-argument constructor only**. If the destination
type has no public parameterless constructor, fail loudly and specifically at that call site rather
than attempting to infer constructor arguments from the enclosing scope — inferring construction
arguments is a second layer of guessing stacked on top of the first, and should not be added
implicitly.

#### 3. Single combined write

Batch three things into one `Dictionary<FilePath, string>` passed to `ValidateAndApplyAsync`:
member removal/addition, every auto-resolved call site (from #1), and every caller-supplied
`callSiteFixups` entry (from #2). One compiler check, one atomic outcome — consistent with how
`MoveMember` already frames itself ("a single atomic change... so it can't fail halfway the way a
manual remove-then-add would").

#### 4. Dry-run report is the judgment-call interface

`SkippedCallSites` (already present in `MoveMember`'s result shape, per its `MoveMemberAsync`
return) should be returned whenever ambiguous sites exist — not gated behind `dryRun` — and should
carry, per site:

- File and line.
- The actual receiver expression text as it appears in source.
- Its static type.
- Every candidate destination-typed member/parameter found in scope at that site, even when more
  than one exists — a short list of plausible candidates is strictly more useful to a caller
  (human or agent) deciding a `callSiteFixups` value than an empty list forcing them to go read the
  surrounding code from scratch.

**Additional case to flag in the report:** whether an ambiguous call site sits inside a method that
is *itself* being moved in the same batch (`memberNames` containing multiple members). Whether a
given reference is "reachable in scope" can depend on move order when the caller and the ambiguous
call site are both mid-flight in the same operation — this should be called out explicitly in the
report rather than left for the caller to notice only after a confusing result.

#### 5. `dryRun: true` reports every call site, not only the ambiguous ones, using the same in-memory candidate solution `ValidateAndApplyHelper` already builds

`ValidateAndApplyHelper.ValidateAndApplyAsync` (`RoslynSentinel.Common/ValidateAndApplyHelper.cs:41`)
already runs `ValidationEngine.ValidateChangesAsync` against an in-memory candidate solution built from
the proposed change set *before* checking `dryRun` — the compile-and-diagnose step always happens; only
the subsequent disk write (`ApplyProposedChangesAsync`, gated on `!dryRun` at line 65) is skipped for a
dry run. `MoveMember`'s dry-run should follow this exact, already-proven pattern rather than a
hand-rolled prediction of what would break:

1. Build the proposed change set in memory (member removed from the source class, added to the
   destination) - no call sites rewritten yet.
2. Run it through `ValidationEngine.ValidateChangesAsync` the same way `ValidateAndApplyHelper` does.
   Every resulting diagnostic at a now-broken call site is the compiler's own answer, not a guess -
   this catches edge cases (extension methods, generic variance, an implicit conversion that changes
   overload resolution) that a hand-rolled semantic-model scope-walk could plausibly miss, since it asks
   the compiler rather than predicting it.
3. Cross-reference each diagnostic's location against the in-scope-candidate scan from #1 to classify
   it into `Valid`/`Ambiguous`/`NoCandidateIntroducible`/`NoCandidateBlocked`/`MoveOrderDependent` - the
   diagnostic proves the site is broken; the scope-walk explains *why* and supplies `SuggestedFix`.
4. Discard the candidate solution. Nothing is written to disk, matching every other `dryRun: true` path
   in this codebase.

This narrows the scope-walk's job from *predicting* breakage to *classifying and explaining* breakage
the compiler already confirmed - a strictly safer division of responsibility, and reuses machinery
rather than adding a second, parallel "will this break" mechanism next to the one `ValidateAndApplyHelper`
already has.

The detection scan in #1 already has to visit every call site of the member(s) being moved, to decide
which ones it can auto-resolve — so a `dryRun: true` call can return a complete, one-row-per-call-site
report at zero extra detection cost, rather than only surfacing the problem cases. This is the actual
value of dry-run here: it makes the *blast radius* visible before anything changes. "2 call sites are
affected, both auto-resolve cleanly" and "100 call sites are affected, 41 need review" are both things
a caller needs to know before committing to a move that a batch of tool calls has already made
practically irreversible-by-inspection — deciding after the fact whether 41 flagged sites were worth
it is a much worse position than deciding before.

Proposed dry-run report shape, one entry per call site found (real, non-dry-run calls populate the
identical rows and additionally apply the `Valid` ones as part of the same atomic write):

```
PreviewCallSite {
    FilePath, Line
    CallExpression          // e.g. "classCLocalVarA.Foo()"
    Status                  // Valid | Ambiguous | NoCandidateIntroducible | NoCandidateBlocked | MoveOrderDependent
    BlockReason?            // human-readable detail, null when Status = Valid
    SuggestedFix?           // e.g. "_classB.Foo()" - populated for Valid, and for NoCandidateIntroducible per #6 below
    Candidates: string[]    // every in-scope destination-typed field/property/param; empty for the NoCandidate* statuses
}
```

`Status` needs more than a boolean `IsValid`/`BlockReason` pair, because "no candidate exists" is not
one failure mode - see #6.

#### 6. Distinguishing "no candidate in scope" from "destination type isn't even reachable"

A call site with zero in-scope candidates of the destination type splits into two materially different
situations that a flat "no candidate" bucket would hide:

- **`NoCandidateIntroducible`** — the destination type is accessible from the call site's compilation
  context (same assembly, or a referenced assembly with adequate visibility) but nothing of that type
  is currently in scope — typically just a missing `using` directive, or a field/DI wiring that hasn't
  been added yet. This is mechanically fixable *before* the move: the dry-run report should say so and,
  where the fix is unambiguous (e.g. exactly one namespace exports the destination type), populate
  `SuggestedFix` with the concrete prep step (`"add 'using Namespace.Of.ClassB;'"`).
- **`NoCandidateBlocked`** — the destination type is not reachable at all from the call site's
  compilation context: `ClassB` is `internal` in a different assembly, there's no project reference in
  that direction, or introducing one would create a circular project reference. No amount of
  `callSiteFixups` cleverness fixes this; it's a real architectural constraint the move cannot paper
  over, and the caller needs to know *before* deciding to move `Foo`, not discover it after 41 other
  sites already went smoothly.

Collapsing these into one bucket defeats the actual point of surfacing blast radius: "100 call sites
affected, 3 need a using directive and 97 are then trivial" and "100 call sites affected, 12 are
architecturally blocked" call for completely different decisions, and only look the same if the dry-run
report doesn't distinguish them.

The accessibility check itself is a symbol-visibility question Roslyn already answers directly
(`IsSymbolAccessibleWithin` against the call site's containing assembly/type) — no new detection
mechanism needed, just an explicit check to run and label per no-candidate site rather than reporting
a single undifferentiated miss.

### Risk posture

Auto-resolution — even the "trivial" case in #1 — changes a caller's receiver expression, which
changes what instance state a call reads or mutates. That's a behavior-affecting rewrite, not a pure
mechanical rename, and carries a real (if usually small) risk of altering program behavior in ways
neither the tool nor a quick review would catch. Static moves carry none of this risk since there's
no receiver to resolve at all.

Given that, auto-resolution for #1 should ship behind an explicit, visible switch —
`autoResolveCallSites` (default `true`, but present and documented) — so a cautious caller can force
every call site through the explicit `callSiteFixups` + dry-run-report path instead of implicitly
trusting the heuristic. This is a dual-use refactoring tool touching call sites solution-wide; a
one-parameter off-switch is cheap insurance against silent trust in an automatic judgment call.

### Relationship to plan_scoped_operation_ledger.md

`plan_scoped_operation_ledger.md` proposes a tracking/gating layer that sits underneath whichever
resolution strategy this doc settles on: apply the move atomically, open a ledger entry per broken
call site (sourced directly from the compiler diagnostics this doc's `SkippedCallSites` design already
wants), and scope a circuit breaker to only the ledger's files until every entry is resolved. It does
not pick a resolution strategy — auto-resolution, `callSiteFixups`, or any other approach here is just
"a call that resolves a ledger entry" from that proposal's point of view. The two are meant to compose:
this doc decides *how* a site gets fixed; the ledger makes sure no site can be fixed and then forgotten,
without ever bypassing validation.

### Original status

Design discussion only — not yet implemented or scheduled. Captured here so it can be picked up
alongside `MoveMember`'s existing static-only behavior, and cross-referenced from
`proposal_splitfacademember_tool.md` (a narrower, related tool for the *Tools/*Impl facade-split
pattern, which sidesteps this whole problem by construction) and from
`plan_scoped_operation_ledger.md` (the tracking/gating layer this doc's resolution strategies would
run underneath).
