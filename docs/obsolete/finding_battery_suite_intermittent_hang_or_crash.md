# Finding — `RoslynSentinel.Tests.Battery` intermittently hangs or crashes the testhost

**Status:** OPEN, not reproduced deterministically. Filed for tracking, not blocking any task.

Noticed while re-verifying no regressions from `docs/current/plan-runtest-tool-v1.md` Task 4
(`Build`'s `ErrorSummary`/`WarningSummary` retrofit). The targeted `Build_*` tests and full
`RoslynSentinel.Tests` (211/211) were already green; this was a belt-and-suspenders full-suite run.

## Symptom

Across 5 consecutive full-suite runs of `RoslynSentinel.Tests.Battery` on 2026-09-01:

1. Two runs (background, piped through `tail`) were reported "running" by the task tracker for
   60-120+ minutes with **zero** `dotnet.exe`/`testhost.exe` processes actually alive and an empty
   output file — an orphaned/zombie tracked-task state, not evidence of the suite itself hanging.
2. One run (`--verbosity=minimal`, output redirected straight to a file, no pipe) completed and
   exited 1, but aborted after 732/780 tests: `The active test run was aborted. Reason: Test host
   process crashed`. No stack trace was captured at minimal verbosity and no `.dmp` was found.
3. One run (`--verbosity=normal`, same direct-redirect approach) did not abort but **hung for 81+
   minutes** with genuinely live `dotnet.exe` (x7) / `testhost.exe` (x1) processes confirmed via
   `Get-Process` — a real stall, not a tracker artifact. Killed via `Stop-Process -Force`.
4. One run, identical command plus a 30-second NUnit `DefaultTimeout` via `--settings
   diag.runsettings`, completed cleanly in 3.8 minutes: 874 passed, 1 failed (known pre-existing —
   see [reference_known_failing_tests.md](reference_known_failing_tests.md)), 89 skipped (needs
   `ROSLYN_SENTINEL_TEST_SLN`). No test ever hit the 30s timeout.

Run 4 is the same 874/1/89 result as the last confirmed-clean baseline (see
`docs/obsolete/blockers/blocking_error_persistentworkspacemanager_dispose_race_crashes_process.md`),
so this is not evidence that fix regressed — but the hang/crash in runs 2-3 is real and unexplained.

## What this is NOT

- Not the `PersistentWorkspaceManager.Dispose()` race fixed in commit `14f8229` — that crash had a
  specific, captured `ObjectDisposedException` stack trace; runs 2-3 here produced no stack trace
  (crash) or no crash at all (hang), and no test ever tripped the 30s per-test timeout in run 4,
  which argues against a deterministic deadlock in any single test's code path.
- Not caused by Task 4's `BuildResult` change — `Build_*` tests pass in isolation every time, and
  the flaky runs' last-logged test before stalling varied between runs (not the same test each time).

## Suspected area

`RunTest`'s Battery tests (`RunTestTests.cs`) spawn real `dotnet test` subprocesses against a
temp-directory fixture (`TestSolutionFixture`). Intermittent subprocess/testhost teardown races are
a plausible cause — same general area as the already-fixed dispose race, and the same fixture noted
a secondary `IOException` symptom in the now-resolved blocker doc. Not confirmed; no repro isolated
to a single test yet.

## Next step (not started)

If this recurs: capture a full crash dump (`--blame-crash` / `--blame-hang` dotnet test flags) on the
next hang/crash to get an actual stack trace, since neither has been captured yet. Until then, treat
solo/targeted test runs as reliable and full-suite background runs as needing a direct-redirect (no
pipe) + generous timeout, per the workaround used to get run 4's clean result.
