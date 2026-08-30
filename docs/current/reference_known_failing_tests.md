---
name: reference_known_failing_tests
description: docs/known-failing-tests.txt does not currently exist despite being referenced by an older memory; ReadFile_LargerThanThreshold_OffloadsAndReturnsLargeResultInfoAsync is a confirmed pre-existing failure on master as of f3aeee1
metadata: 
  node_type: memory
  type: reference
  originSessionId: baae58f2-ea41-48a8-b6da-6d65bc32d78d
  modified: 2026-08-30T09:49:30.951Z
---

An older memory entry referenced `docs/known-failing-tests.txt` as the place to check before
blaming a new change for a test failure. As of 2026-08-30 that file does not exist in the repo
(confirmed via Glob) — either it was never created, or it was removed/renamed since that memory
was written. Don't assume it exists; check first.

Confirmed pre-existing failure (verified via `git stash` back to `master` HEAD `f3aeee1`, ran in
isolation, failed identically with changes present and absent):
`RoslynSentinel.Tests.Battery.ReadFileTests.ReadFile_LargerThanThreshold_OffloadsAndReturnsLargeResultInfoAsync`
— `Assert.That(result.LargeResult, Is.Not.Null)` fails, `LargeResult` is null. Likely related to
[[project_offload_helper_partial_wiring]] (offload threshold/wiring gaps in `ReadFile`'s path
specifically). Not caused by the `ChangeAccessibility`/`ListAll` work in
[[project_changeaccessibility_enum_and_listall]] — full-suite run showed exactly this 1 failure
both before and after that commit.

**How to apply:** until `docs/known-failing-tests.txt` is recreated (or this memory is superseded),
treat this specific test as a known pre-existing failure, not a regression, when it shows up in a
full-suite run. If asked to fix it, it's a real bug in the `ReadFile` offload path, not a test bug —
investigate `ReadFile`'s `ForPossiblyLargeDataAsync` call site in `SentinelWorkspaceTools.cs` and the
threshold test's fixture size.
