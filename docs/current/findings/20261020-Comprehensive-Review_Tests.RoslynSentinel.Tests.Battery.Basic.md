# Finding: RoslynSentinel.Tests.Battery.Basic has no null-only tests, but 8 tests assert too little and 2 can false-pass

**Status:** OPEN 2026-10-20. No fix decided; this is an audit report for the test owner to triage.

## Context

Read-only audit of the test project `RoslynSentinel.Tests.Battery.Basic` on 2026-10-20, looking for weak NUnit
assertions: tests whose only meaningful assertion is `Is.Null` / `Is.Not.Null`, `Is.Not.Empty`-only,
`Count > 0`-only, `Assert.Pass`, or "does not throw" with no outcome check, on a rich DTO or result
(engine result, `ApplyChangesResult`, `MoveMemberResult`, status flags, message text, edited source text,
changed-file lists). Method: Roslyn MCP text search over the project, then every flagged test and most of
the unflagged tests were read in full. All 15 test files were reviewed. `BatteryFourTests.cs` had
uncommitted edits from another session at audit time, so its line numbers may shift.

DTO shapes inspected (from source):
- `ApplyChangesResult` (`RoslynSentinel.Common/ApplyChangesResult.cs:19`): `IsError`, `SucceededFiles`,
  `FailedFiles`, `Summary`, `WorkspaceInSync`, `WorkspaceVersion`, `PreImages`, `ValidationResult`,
  `RolledBackFiles`, `NoOpFiles`, `RefusalCode`; derived `Status`, `Code`, `Message`.
- `MoveMemberResult` (`RoslynSentinel.Engines.Basic/MemberRefactoringEngine.cs:20`): `Changes`,
  `SkippedCallSites`, `PendingLedgerEntries`, `PendingLedgerOperationName`, `AppliedFixups`, `Notes`,
  `AppliedEdits`, `RewrittenCallSites`. It has no status flag: failures throw (`ToolInvalidArgumentException`,
  `ToolTargetIneligibleException`), so a `Changes`-based text assertion is a sound outcome check.
- `StaticConversionResult` (`RoslynSentinel.Engines.Basic/StaticConversionEdits.cs:17`): `HandledIndexes`,
  `Changes`, `Notes`.

## What is broken

### 1. Totals

| Measure | Value |
| --- | --- |
| Test files | 15 |
| Test methods | 171 (167 `[Test]` + 4 `[TestCase]`-only methods) |
| Test cases | 179 (12 `[TestCase]` rows) |
| Tests with a weak-only assertion | 8 (about 4.7%): 2 false-pass candidates, 4 weaker versions of the same risk, 2 low |
| `Is.Null` / `Is.Not.Null` asserts (incl. `Null.Or.Empty`, `Not.Null.And.Not.Empty`) | 40 |
| Other matches (`Is.Empty`, `Is.Not.Empty`, `GreaterThan(0)`) | 33 (total pattern hits 73) |
| Null asserts guarded by a follow-up or a contract | 40 of 40 (100%) |
| Tests where a null/non-null check is the ONLY assertion | 0 |

Breakdown of the 40 null asserts (all verified):
- About 20 are guards followed by member use on the same value, or a control with later disk/text asserts
  (for example `CrefSpikeTests.cs:115`, `CrefQualifierTests.cs:608`, `MoveMemberCollateralTripwireRouteTests.cs:159`).
- 7 are contract-null on `StateMessage()` / `CheckBreaker()` paired with `IsTripped` / `Open`
  (`OrientationBreakerTests.cs:39,85,106,111,128,138,142`).
- 5 are `Null.Or.Empty` on `PendingLedgerEntries` / `AppliedFixups` next to strong sibling asserts
  (`PreviewInstanceMoveCallSitesTests.cs:915,980,1066,1218,1219`).
- 4 are message-presence checks beside a status assert (`BatteryFourTests.cs:218,351,381,433`).
- The rest are contract or control cases (`PreviewInstanceMoveCallSitesTests.cs:1370` SuggestedFix null on a
  cref row, `UnrecoverableBreakerTests.cs:46,182`).

### 2. Pattern categories

| Pattern | Count | Examples |
| --- | --- | --- |
| `Is.Not.Null` guard immediately used (ignore) | about 20 | `CrefSpikeTests.cs:115`, `MoveMemberCollateralTripwireRouteTests.cs:159` |
| Contract-null with a status companion (ignore) | about 12 | `OrientationBreakerTests.cs:39`, `UnrecoverableBreakerTests.cs:46` |
| Message only checked non-empty, status asserted | 3 | `BatteryFourTests.cs:218`, `:351`, `:433` |
| Success-only `!result.IsError` with no outcome check | 4 | `ScopedOperationLedgerBlockingTests.cs:71`, `:95`; `UnrecoverableBreakerTests.cs:129`; `PreviewInstanceMoveCallSitesTests.cs:767` |
| Negative-only or "does not throw" with no outcome check | 3 | `PreviewInstanceMoveCallSitesTests.cs:457-494`; `MakeStaticRewritesCallersTests.cs:88-101`; `MoveMemberPreservesUntouchedTextTests.cs:278-326` |

### 3. Genuine false-pass candidates (read these first)

1. `PreviewInstanceMoveCallSitesTests.cs:457-494`, `MoveMemberAsync_FieldMapSatisfiesSourceFieldDependency_DoesNotRefuseAsync`.
   The `MoveMemberAsync` result is discarded. The only check is
   `catch (ToolInvalidArgumentException ex) when (ex.Message.Contains("private/internal")) { Assert.Fail(...) }`.
   The test passes whenever the call returns normally, even with an empty or wrong `Changes`.
2. `MakeStaticRewritesCallersTests.cs:88-101`, `AddStatic_DoesNotReportReceiverFieldThatIsStillRead`.
   The only assert is `result.Notes, Has.None.Contain("no longer read")`. It passes if the conversion did
   nothing at all (empty `Notes`).

Weaker versions of the same risk (would pass if the operation silently did the wrong thing):
3. `MoveMemberPreservesUntouchedTextTests.cs:278-326`: only `sourceNewText.EndsWith("\n"), Is.False`.
4. `ScopedOperationLedgerBlockingTests.cs:33-72`, `:74-96`; `UnrecoverableBreakerTests.cs:~108-130`:
   only `!result.IsError`. The in-test comments say the write "must actually reach a real file", but the
   file is never read back.

### 4. Offenders table

`ApplyChangesResult`, `MoveMemberResult` and `StaticConversionResult` members are named in Context.

| # | file:line | Test | DTO | What to assert | Effort |
| --- | --- | --- | --- | --- | --- |
| 1 | `PreviewInstanceMoveCallSitesTests.cs:456-494` | `MoveMemberAsync_FieldMapSatisfiesSourceFieldDependency_DoesNotRefuseAsync` | `MoveMemberResult` (discarded) | Assign the result. Assert `Changes` has Source and Target keys, target text reads `_b` and has no `_a` (same shape as the passing sibling at `:496-550`). | moderate |
| 2 | `MoveMemberPreservesUntouchedTextTests.cs:278-326` | `StaticMethodToExistingClass_WithoutFinalNewline_PreservesMissing` | `MoveMemberResult` | Add `AssertOnlySpansChanged` for Source (member removed, cref retargeted), Target and Caller, as the sibling tests at `:25-160` do. | trivial |
| 3 | `MakeStaticRewritesCallersTests.cs:88-101` | `AddStatic_DoesNotReportReceiverFieldThatIsStillRead` | `StaticConversionResult` | `HandledIndexes` equals `{0}`; `Consumer.cs` text has `Helper.Normalize(x)` and still has `_helper.Length(x)`; keep the `Notes` assert as the negative. | trivial |
| 4 | `ScopedOperationLedgerBlockingTests.cs:33-72` (assert at `:71`) | `OpenLedgerEntry_AllowsWriteToItsOwnTrackedFileAsync` | `ApplyChangesResult` | `SucceededFiles` contains the file; `FailedFiles` empty; `NoOpFiles` does not contain it; disk text ends with the appended line. | trivial |
| 5 | `ScopedOperationLedgerBlockingTests.cs:74-96` (assert at `:95`) | `NoOpenLedger_WritesStillSucceedAsync` | `ApplyChangesResult` | Same as #4. | trivial |
| 6 | `UnrecoverableBreakerTests.cs:~108-130` (assert at `:129`) | `NotTripped_WritesStillSucceedAsync` | `ApplyChangesResult` | Same as #4. | trivial |
| 7 | `PreviewInstanceMoveCallSitesTests.cs:767`, `:799` (test at `:724-800`) | `MoveMemberAsync_UnresolvedCallSite_OpensLedgerThatBlocksUnrelatedFileAsync` | `ApplyChangesResult` | The setup apply (`:767`) is `!IsError` only; assert `SucceededFiles`. The "tracked file still writable" apply (`:799`) replaces `_f1.Foo` with itself, a no-op write; the ledger gate runs before no-op detection (`PersistentWorkspaceManager.cs:1592`) so a block would still fail it, so this is low value. | trivial (low) |
| 8 | `MakeStaticRewritesCallersTests.cs:174-185` | `AddStatic_NotAMethodOrAlreadyStatic_IsLeftToThePlainModifierPath` | `StaticConversionResult` | `HandledIndexes` and `Changes` are only `Is.Empty`. Add a `Notes` assert or a control showing the requests were parsed. | trivial (low) |

Thin but acceptable (at most a one-line strengthening each; not counted as weak-only):
- `BatteryFourTests.cs:218`, `:351`, `:433`: `Does.Contain` on `BlockingReason` / `Error` instead of non-empty.
- `OrientationBreakerTests.cs:106`, `:138`: assert the `CheckBreaker()` message instead of `Is.Not.Null`.
- `PreviewInstanceMoveCallSitesTests.cs:1287-1323`: only `Status == MoveOrderDependent`; could add `BlockReason`.
- `PreviewInstanceMoveCallSitesTests.cs:1560-1594`: a single `Does.Contain("KnownLimitTgtS.Foo(2)")`; the
  member's arrival in the target is not asserted.

### 5. NotFound-style outcomes that let failures slip through

These are paths where a "not found / nothing to do / null / empty" result (or a null `Notes` / pending list)
is accepted as success or as an unremarkable outcome, so a failure can pass silently. Each is a question for
the owner: should the engine throw (as `MoveMember` does with `ToolInvalidArgumentException`) rather than
return a soft result? Items marked hypothesis were not traced into engine source.

A. Analysis engines return "unsafe + reason string" or "safe" for lookups that found nothing, instead of throwing.
- `BatteryFourTests.cs:185-197` (`AnalyzeForeach_FileNotFound...`) and `:199-222` (snippet not found): the
  engine returns `IsSafeToConvert=false` with a `BlockingReason`; the tests accept that. A caller that only
  reads one flag cannot tell "file missing" from "unsafe construct". `:218` and `:351` check only that
  `BlockingReason` is non-empty.
- `BatteryFourTests.cs:325-353`: same shape for `AnalyzeSwitchForPatternConversionAsync` (file not in workspace,
  snippet missing).
- `BatteryFourTests.cs:258-292` (`AnalyzeForeach_CollectionIsFieldNotLocal_ReturnsSafeWithNote`): the test
  comment records that "declIndex=-1" (declaration not found) returns `IsSafeToConvert=true` with a note. A
  not-found lookup is reported as SAFE. The test pins it (`:287-290` asserts `IsSafeToConvert` true and
  `CollectionVariableName == "_events"`). Highest-impact item in this section.
- `BatteryFourTests.cs:422-435` and `:437-481`: `ConvertSwitchToPatternSafeAsync` reports failure through
  `IsError` / `Error` text; the throw-only case (`:437`) is a documented analysis/conversion disagreement
  (`Analyze` says safe, `Convert` fails). The failure is only observable if the caller reads `IsError`.
  `:433` checks only a non-empty `Error`.

B. Soft refusal channel is inconsistent with the throw channel.
- The ledger refusal (`RoslynSentinel.Common/PersistentWorkspaceManager.cs:1592-1598`) returns
  `IsError: true` with `FailedFiles` and `Summary` but no `RefusalCode`, so `ApplyChangesResult.Code` falls
  back to `ToolErrorCode.Exception` (`ApplyChangesResult.cs`, `Code`). The test
  (`ScopedOperationLedgerBlockingTests.cs:98-147`, asserts at `:142-143`) must match `Summary` text
  ("scoped operation ledger"). Meanwhile the session-halt and unrecoverable-breaker paths throw
  `SessionHaltedException` (`PersistentWorkspaceManager.cs:1573-1577`; tested at `UnrecoverableBreakerTests.cs:~108-127`).
  Owner question: should the ledger refusal carry a typed `RefusalCode`, or throw like the halts?

C. Null or absent is treated as success.
- `PreviewInstanceMoveCallSitesTests.cs:915,980,1066,1218` use `PendingLedgerEntries, Is.Null.Or.Empty` and
  `:1219` uses `AppliedFixups, Is.Null.Or.Empty`. Null and empty are indistinguishable, so "ledger step never
  ran" passes as "no unresolved sites". Companion asserts (`Changes` text, `AppliedFixups` count) make these
  tests safe today; the exposure is the contract, not the tests.
- `PreviewInstanceMoveCallSitesTests.cs:~1640` (`InstanceMove_BareSameClassCall_WithSingleTargetField...`):
  `result.PendingLedgerEntries?.Any(...) ?? false, Is.False` treats a null list as "no NoCandidateIntroducible
  entry". The same test's `RewrittenCallSites` count of 1 guards it.
- `MoveMemberInstanceCrefTests.cs` tests at `:637-689`, `:691-735`, `:737-770`, `:772-812`, `:919-971` read
  `var notes = result.Notes ?? Array.Empty<string>();` and then assert `Does.Not.Contain(...)` or
  `Any(...) Is.False`. A null `Notes` (for example if note generation were skipped) passes the negative
  asserts; only the paired `Count(...) Is.EqualTo(1)` or `Is.EqualTo(...)` asserts in the other tests catch it.
  The two purely negative ones are `:637-689` (`BareCrefToMovedSibling...`) and `:919-971`
  (`NoBareCrefInMovedDoc...`), which still have full-text equality on `Target.cs` as protection.
- `MoveMemberInstanceCrefTests.cs:988-992` (`TextOrOriginal`) returns the ORIGINAL text when the key is absent
  from `Changes`. It is used by `NoCrefsAnywhere_...` (`:443-493`) to say "untouched file is identical". A
  file that should have been edited but was dropped from `Changes` would also pass that assert; the
  `Notes` equality assert in the same test is the only other guard.
- `MoveMemberPreservesUntouchedTextTests.cs:~415-418`: "caller must not be touched" is asserted by absence
  (`Changes.Keys.Any(... "Caller.cs") Is.False`). Intended for the base-type pull-up, but absence is also what
  a dropped edit looks like; the text equality asserts on Source and Base mitigate it.

D. Known limitations pinned as success (silent skip rather than refuse or warn).
- `CrefQualifierTests.cs:492-504`, `CrefSpikeTests.cs:108-161` and `:163-200`: bare crefs to inherited members
  do not bind; the engine counts them in `SkippedUnresolved`, leaves the text unedited, and the tests pin
  that as "observed". Whether the user is told is not asserted at the tool level (hypothesis: only the
  counter reports it).
- `PreviewInstanceMoveCallSitesTests.cs:1513-1555` (`KnownLimitation_InstanceMove_BareSameClassCall_IsNotRewrittenAsync`):
  the move is NOT refused; the source keeps `return Foo(2);` (does not compile, per the test's own comment at
  `:1507-1509`) and a pending ledger entry with `NoCandidateIntroducible` is returned. A compile of the result
  is not checked by the test.
- `MakeStaticRewritesCallersTests.cs:174-185`: ineligible targets (a field, an already-static method) yield an
  empty `HandledIndexes` and empty `Changes` with no `Notes` entry, so the caller is not told why a request
  was skipped (hypothesis: `Notes` stays empty; not traced into `ConvertMembersToStaticAsync`).

E. Swallowed exception path.
- `PreviewInstanceMoveCallSitesTests.cs:487-493`: the test catches `ToolInvalidArgumentException` filtered on
  message text "private/internal" and turns it into `Assert.Fail`. Any other `ToolInvalidArgumentException`
  message is rethrown (still a failure), so this is fine as a failure signal, but the test has no success
  assertion (see offender #1).

## Root cause

Not traced to a single cause. Observed patterns, each cited above:
- Tests that guard "should not refuse / should not throw" were written as negative checks without a positive
  outcome assert (offenders #1-#3).
- The "writes still succeed" tests were written as smoke tests of the gate and never read the file back
  (offenders #4-#6).
- Engines that return soft results (`IsSafeToConvert`, `IsError`, null/empty lists) force tests to choose
  between status asserts and message asserts; where the DTO has no typed code (`RefusalCode` null on the
  ledger refusal) tests fall back to text matching (section 5, B).

## Why it matters

- Offenders #1-#3 can stay green after a regression that makes the operation do nothing, so a refactor of
  the move/convert engines could ship unnoticed.
- Offenders #4-#6 guard the write chokepoint; `!IsError` would also hold for a skipped write.
- Section 5 items A and B are environment defects for weak agents: a tool result that says "safe" for a
  not-found lookup, or a refusal with no typed code, cannot be acted on mechanically.

## Verdict

The project is mostly clean. 171 test methods, 40 null asserts all guarded or contract-null, and the large
move/cref tests assert full edited-source equality and exact `Notes` arrays, which is strong. Only 8 tests
need a strengthening, 2 of them are real false-pass candidates, and all but one fix is trivial.

## Recommendation

Decided: none. Suggested, ranked by leverage (owner to decide):
1. Strengthen offenders #1 and #3 (false-pass candidates), then #2.
2. Add `SucceededFiles` and disk read-back to offenders #4-#6 (one shared helper would do).
3. Owner judgement on section 5: A (`IsSafeToConvert=true` for a not-found declaration, `BatteryFourTests.cs:258-292`)
   and B (typed `RefusalCode` or throw for the ledger refusal) have the best payoff; C-E are contract
   tightening that the current tests already mostly compensate for.

## Not checked

- `MoveMemberPreservesUntouchedTextTests.cs` lines 330-380 and 840-998 (fixture and helper code only).
- `MakeStaticRewritesCallersTests.cs` lines 1-52 (fixture helpers).
- Engine source behind the section 5 hypotheses (`ConvertMembersToStaticAsync`, the analysis engines'
  not-found paths, `MoveMemberAsync` note generation) was not read; those items rest on test code and
  in-test comments.
- No tests were built or run; pass/fail status was not confirmed.
- `Worktree/`, `bin/` and `obj/` folders were excluded.
- Other test projects were out of scope.

## Out of scope

Test style (naming, categories, fixture structure) and any non-assertion quality issue.