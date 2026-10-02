# Finding: Battery.Basic's remaining slow fixtures mostly need the disk tier; one big in-memory candidate is left

**Status:** OPEN 2026-10-02. Eight fixtures converted to the in-memory tier (MutatingToolRejectionMessageTests added); the rest are classified below, with the disk-required ones still hypotheses unless marked "confirmed".

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

## Recommendation
1. Convert `PreviewInstanceMoveCallSitesTests` test by test in its own session (highest remaining leverage). Check first
   whether the ledger test needs a disk-backed ledger and whether `InMemoryWorkspace` can host two projects for the
   cross-project test.
2. Decision needed: make the whole-file size guard read old content from the workspace document instead of the disk
   (production change, would let `ApplyDiffSizeGuardTests` run in-memory). Otherwise leave it on the disk tier.
3. Read the "hypothesis" fixtures before judging them; `OrientationBreakerFilterTests` is confirmed disk
   (real MCP + LoadSolution). `MutatingToolRejectionMessageTests` confirmed in-memory (now converted).
4. Optional: mutation checks for the converted fixtures; run Battery.Basic several times to look for flakiness.

## Out of scope
- Rewriting watcher/drift/undo/path-case tests onto the fake.
- Changing production apply behaviour (other than the decision in item 2).
