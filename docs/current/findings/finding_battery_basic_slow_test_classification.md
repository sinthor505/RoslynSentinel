# Finding: Battery.Basic's remaining slow fixtures mostly need the disk tier; one big in-memory candidate is left

**Status:** ANALYSIS COMPLETE 2026-10-02. Eight fixtures converted to the in-memory tier; PreviewInstanceMoveCallSitesTests analyzed and classified - see test-by-test table below. 13 of 15 tests are convertible; 2 require disk/ledger support.

## Context
After test-level parallelism (commit 3037e5b) Battery.Basic was CPU-bound: ~933 test-seconds, ~78 s wall on 16 cores.
Per-test seconds were taken from `TestResults/parallel-run/RoslynSentinel.Tests.Battery.Basic.trx`, a contended
pre-conversion run, so absolute numbers are inflated; only the ranking is reliable. Plan:
`docs/current/plans/plan_in_memory_test_tier_for_battery.md`.

## What is broken
Pre-conversion test-seconds, top fixtures (tests / seconds):

| Fixture | Tests | Sec | Class | State |
| --- | --- | --- | --- | --- |
| GitToolsSmokeTests | 75 | 146 | disk (git repo on disk) | hypothesis, not read |
| PreviewInstanceMoveCallSitesTests | 15 | 112 | in-memory candidate | PARKED: 897 lines, shared SetUp, ledger test, cross-project test |
| PathCaseLookupRegressionTests | 18 | 95 | disk (path case) | hypothesis, not read |
| RunTestTests | 11 | 88 | disk (spawns dotnet test) | hypothesis, not read |
| ModifyModifierBatchTests | 8 | 72 | in-memory | converted (5bc79a1) |
| ModifyAttributeBatchTests | 8 | 71 | in-memory | converted (830337c) |
| MutatingToolRejectionMessageTests | 3 | 71 | in-memory | converted (f2be3c2) |
| ModifyBaseTypeBatchTests | 8 | 70 | in-memory | converted (830337c) |
| LargeResultOffloadFilterTests | 3 | 60 | disk (offload files) | hypothesis, not read |
| OrientationBreakerFilterTests | 5 | 58 | unknown | not read |
| MemberSingleDeclarationTests | 5 | 56 | in-memory | converted (8042edf) |
| MemberInsertAfterEolTests | 4 | 53 | in-memory | converted (8fa4c6c) |
| ApplyDiffSizeGuardTests | 4 | 53 | disk | CONFIRMED: see below |
| ReplaceSnippetBatchTests | 9 | 48 | in-memory | converted (39be3d3) |
| ReplaceSnippetErrorCodeTests | 4 | 40 | in-memory | converted (c4865a4) |
| ListSolutionItemsAllTests | 3 | 39 | disk? | hypothesis, not read |
| BuildEngineTests | 3 | 38 | disk (real build) | hypothesis, not read |
| ReplaceSnippetSizeGuardTests | 4 | 30 | in-memory | converted (c4865a4) |

Converted fixtures now run at ~0.7-1.5 s per test. Battery.Basic alone after all conversions: 397 pass, 6 skip,
1m04 test duration (1m15 including build), against 1m10 before: the gain is small because the biggest
remaining costs are the disk-bound fixtures above.

## Root cause
`ApplyDiffSizeGuardTests` (confirmed, read in full): the whole-file size guard reads old content through
`FileIoHelper.ReadAllTextIfExistsAsync` from the real disk. An in-memory file does not exist there, so the guard would
treat it as a new file and skip the check. The fixture's own header says so. Other disk classifications are
hypotheses from fixture names and the earlier duration scan, not from reading each file.

## Why it matters
The wall-time floor of Battery.Basic is now set by a handful of fixtures (git, path case, RunTest, build) that
legitimately need the disk, plus `PreviewInstanceMoveCallSitesTests` (112 s), the largest remaining convertible cost.

## PreviewInstanceMoveCallSitesTests: per-test classification (15 tests, 112 s)

Lines 1-897. Analysis completed 2026-10-02. All tests analyzed for fixture dependencies.

### Category A: Convertible to in-memory tier (13 tests)
These tests use only MemberRefactoringEngine.Preview/MoveInstanceMembersAsync APIs, no ledger/IScopedOperationLedger, no real file I/O, no multi-project.

| Test Name | Line | Status | Evidence |
| --- | --- | --- | --- |
| UnambiguousSingleCandidate_ClassifiesAsValidWithSuggestedFixAsync | 68 | Convertible | Calls PreviewInstanceMoveCallSitesAsync only; _fixture.AddFileToSolution reloadable into InMemoryWorkspace |
| MultipleCandidates_ClassifiesAsAmbiguousAsync | 107 | Convertible | Calls PreviewInstanceMoveCallSitesAsync only; static fixture sources |
| ExistingNonEmptyDestinationClass_ClassifiesAsValidWithSuggestedFixAsync | 144 | Convertible | Calls PreviewInstanceMoveCallSitesAsync only; fixture content inline in test |
| NoInScopeCandidate_ClassifiesAsNoCandidateIntroducibleAsync | 218 | Convertible | Calls PreviewInstanceMoveCallSitesAsync only; static fixture sources |
| OuterClassFieldOfDestinationType_NotTreatedAsUnqualifiedCandidateForNestedCallSiteAsync | 255 | Convertible | Calls PreviewInstanceMoveCallSitesAsync only; fixture content inline |
| MoveMemberAsync_UnambiguousInstanceMember_AppliesAutomaticallyAsync | 304 | Convertible | Calls MoveMemberAsync; no ledger use; checks only Changes, SkippedCallSites |
| MoveMemberAsync_FieldDependencyAlreadySatisfiedOnTarget_MovesWithoutDuplicatingFieldAsync | 353 | Convertible | Calls MoveMemberAsync; no ledger use; checks only Changes |
| MoveMemberAsync_FieldDependencyIncompatibleTypeOnTarget_FailsWithCollisionMessageAsync | 393 | Convertible | Calls MoveMemberAsync; no ledger use; only checks for exception, no disk read |
| MoveMemberAsync_AmbiguousInstanceMemberNoFixup_ReturnsPendingLedgerEntryAsync | 430 | Convertible | Calls MoveMemberAsync; does NOT open ledger, only checks result.PendingLedgerEntries |
| MoveMemberAsync_NewFixupTargetWithoutZeroArgCtor_RefusedWithSignatureAsync | 568 | Convertible | Calls PreviewInstanceMoveCallSitesAsync and MoveMemberAsync with fixup dict; File.ReadAllTextAsync reads from fixture, no disk needed (inline) |
| MoveMemberAsync_NewFixupParameterlessTarget_RewritesToNewTargetAsync | 629 | Convertible | Calls PreviewInstanceMoveCallSitesAsync and MoveMemberAsync with fixup dict; no File I/O |
| MoveMemberAsync_GlobalWildcardFixup_ResolvesEveryUnresolvedSiteAsync | 682 | Convertible | Calls MoveMemberAsync with wildcard fixup; no ledger use; checks only Changes |
| MoveMemberAsync_FixupKeyPrecedence_ExactBeatsFileWildcardBeatsGlobalAsync | 745 | Convertible | Calls PreviewInstanceMoveCallSitesAsync and MoveMemberAsync with fixup dict precedence; no ledger use |

### Category B: Disk-tier only (2 tests)

| Test Name | Line | Status | Reason |
| --- | --- | --- | --- |
| MoveMemberAsync_UnresolvedCallSite_OpensLedgerThatBlocksUnrelatedFileAsync | 485 | DISK | Calls `IScopedOperationLedger.TryOpen()` + ledger validation gate (`IsBlocked`); requires working scoped-operation ledger from PersistentWorkspaceManager with blocking semantics. FakeWorkspaceManager.TryOpen always returns false. Would need: (1) ledger support in FakeWorkspaceManager, (2) working `File.ReadAllTextAsync` to read back real modifications for ledger entry's FilePath. Cannot inline. |
| CrossProjectSiblingField_ClassifiesAsValidNotNoCandidateIntroducibleAsync | 835 | DISK | Calls `TestSolutionBuilder.CreateTwoProjectSolution()` to create two AdhocWorkspace projects with ProjectReference. InMemoryWorkspace holds a single project only. Test's comment explains why two separate compilations are essential (cross-compilation symbol equality). Would need: multi-project InMemoryWorkspace or (simpler) leave on disk tier. |

## Recommendation
1. **Stage 1:** Convert Category A tests (13 tests) in batches of 3-4 in a focused session:
   - Batch 1: tests 68, 107, 144 (preview tests, no MoveMemberAsync, minimal setup)
   - Batch 2: tests 218, 255 (more preview tests)
   - Batch 3: tests 304, 353, 393 (basic MoveMemberAsync)
   - Batch 4: tests 430, 568, 629 (pending ledger, fixup dicts)
   - Batch 5: tests 682, 745 (wildcard fixups)
   - Commit after each green batch with `RunTest(scopeName: RoslynSentinel.Tests.Battery.Basic, filter: FullyQualifiedName~PreviewInstanceMoveCallSitesTests)`
2. **Stage 2:** Leave Category B tests on disk tier with clear comments explaining why.
3. **Decision:** Whether to extend FakeWorkspaceManager with ledger support (to enable test 485 in-memory) or leave both on disk. The single-project constraint is harder to relax (not just FakeWorkspaceManager; InMemoryWorkspace.Create itself).

## Out of scope
- Rewriting watcher/drift/undo/path-case tests onto the fake.
- Changing production apply behaviour.
- Multi-project support in InMemoryWorkspace (separate task if ledger support is decided).
