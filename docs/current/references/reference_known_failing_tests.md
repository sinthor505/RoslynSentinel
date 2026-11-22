---
name: reference_known_failing_tests
description: "Where the known-failing-test and known-build-warning baselines live (docs/known-*.txt, written by scripts/build.ps1) and how to use them; the old ReadFile offload failure is fixed"
metadata:
  node_type: memory
  type: reference
  originSessionId: baae58f2-ea41-48a8-b6da-6d65bc32d78d
  modified: 2026-10-17T00:00:00.000Z
---

# Reference: known-failing-test and known-build-warning baselines

**Status:** CURRENT 2026-10-17. Verified against `docs/` and `scripts/build.ps1`.

## Purpose
Before blaming a change for a red test or a new warning, diff against the recorded baseline. Report
only NEW failures/warnings.

## How it works
`scripts/build.ps1` records and diffs two baselines per flavor (`Advanced`, `Solution`; the `Basic` flavor and its baseline files were removed when Server.Basic was folded into Server.Advanced):

- `docs/known-failing-tests.<Flavor>.txt` (tests)
- `docs/known-build-warnings.<Flavor>.txt` (warnings)

`build.ps1 -UpdateBaseline` regenerates them (header line records the date and the
`dotnet build|test <Flavor> <Config>` command). As of 2026-10-17 (before the Basic flavor was removed): `known-failing-tests.Advanced.txt` listed
10 tests, `.Solution.txt` none; `known-build-warnings.Advanced.txt` listed 2 warnings,
`.Solution.txt` none. Note the Solution test baseline header is dated 2026-08-26, so
treat an empty solution list as "not regenerated recently" before treating it as proof of a clean suite.

The single `docs/known-failing-tests.txt` / `docs/known-build-warnings.txt` named in older notes and in
`build.ps1`'s header comments does not exist; the per-flavor files replaced it.

## Gotchas
- `ReadFile_LargerThanThreshold_OffloadsAndReturnsLargeResultInfoAsync`
  (`RoslynSentinel.Tests.Battery.ReadFileTests`) failed on master `f3aeee1` (2026-08-30) with
  `result.LargeResult` null, but a full-solution `RunTest` on 2026-09-20 passed 2558 of 2558 and the test
  carries no skip attribute. Treat it as fixed: if it fails again it is a real regression.
- `SolutionLoadTimeoutException: The solution load did not finish within 30 s` in a full or parallel run
  is a known contention flake (owner ruling 2026-11-22, plan_shorten_verification_loop D5): the
  test solutions load well under 30 s alone, but a full run puts 20 NUnit workers in each test process,
  often alongside other worktree runs, and a load that waits on that oversubscribed CPU passes the 30 s
  default (`SolutionLoadWait.DefaultTimeout`). Re-run the failing tests alone (`Invoke-WorktreeTests.ps1
  -Filter "FullyQualifiedName~<Class>"`); if they pass, record the run as this flake and move on. A
  timeout that repeats when the test runs alone is a real regression. Do not add `[NonParallelizable]`
  for it: the victims change from run to run, and the attribute lengthens the serial tail.
