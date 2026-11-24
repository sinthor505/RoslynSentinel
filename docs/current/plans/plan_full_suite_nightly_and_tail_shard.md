# Plan: Keep OutOfProcess tests out of routine runs safely (nightly full run, debt check) and cut the full run's serial tail

**Status:** DRAFT 2026-11-24. Not started; open decisions D1-D6 below.

## Problem

Full test runs take 15-25 minutes (median 1,331 s over 77 full runs in `C:\tmp\rs-testtrees\_results`; today's
`48fb08e2-20261124-111028-65836` took 1,378 s). The owner asked whether slow OutOfProcess tests (Git parity, Build, RunTest)
should be skipped from "full suite" runs unless their areas changed, with a nightly run and a 24-hour staleness flag.

**What already exists (verified 2026-11-24):**

- `[Category("OutOfProcess")]` on 58 fixtures in `RoslynSentinel.Tests.Tools.Basic`. `RunTest`'s default suite is `fast`
  (`TestSuite` enum, `RoslynSentinel.Common/ToolEnums.cs:356`; filter composed by `TestSuiteFilter.Compose`,
  `RoslynSentinel.Engines.Basic/TestSuiteFilter.cs`), and `scripts/Invoke-WorktreeTests.ps1 -Suite fast` (line 125) and
  `scripts/build.ps1 -Fast` do the same. Fast runs take about 238-272 s (`plan_shorten_verification_loop.md`, Step 14).
- A staleness gate: `FullTestMarkerStore` (`RoslynSentinel.Engines.Basic/FullTestMarkerStore.cs:32`) keeps
  `.roslynsentinel/full-test-marker.json` and warns on fast `RunTest` results and on `Git` commit results
  (`GitTools.cs:224`) when the last full run is older than 2 days or more than 40 fast runs ago
  (`RoslynSentinel.Common/FullSuiteStalenessOptions.cs:15-16`, overridable by `ROSLYNSENTINEL_FULL_SUITE_MAX_AGE_DAYS` /
  `ROSLYNSENTINEL_FULL_SUITE_MAX_FAST_RUNS`). `Invoke-WorktreeTests.ps1` writes the same marker (lines 484-517).
- Owner decision D1 of `plan_shorten_verification_loop.md`: a full run is required before a push, when the marker is stale, and
  when the batch touched git, `RunTest`/`Build` or process-spawning code.

**Gaps:**

1. A red full run resets the marker as if green: `FullTestMarkerStore.Evaluate` (line 121) ignores `FailedCount`. The current
   marker reads `failedCount: 1` and counts as fresh.
2. The marker has no commit (`FullTestMarker`, line 17: "There is no head SHA field"), so nothing can say which OutOfProcess
   areas changed since the last full run, and a nightly job cannot tell whether HEAD moved.
3. Nothing runs the full suite unattended: no scheduled task or nightly script exists (searched `scripts/`, `docs/current`).
4. `implement-plan/SKILL.md:81` and `todo-punchlist/SKILL.md:70` require one full `RunTest` per plan or batch end
   regardless of what changed.

**Where the full run's time goes (TRX of `48fb08e2-20261124-111028-65836`, `Get-TestRunTimings.ps1 -View Projects/Fixtures`
plus a per-test timeline):**

- `Tests.Tools.Basic` is the critical path: 918 s wall. Every other project ends within 196 s. The machine has 20 logical
  processors; average concurrency over the run was 6.3.
- Tools.Basic timeline: 0-330 s parallel phase (20 tests running). 330-480 s: two Git parity fixtures whose tests run one
  after another (`GitWorktreeRepoLocalConfigTests` 15 tests 145 s, `GitStageToolParityTests` 17 tests 80 s); they carry no
  fixture-level `[Parallelizable]`, so they inherit `[assembly: Parallelizable(ParallelScope.Fixtures)]`
  (`RoslynSentinel.Tests.Tools.Basic/AssemblyInfo.cs:8`). 480-918 s: the `[NonParallelizable]` tail, one test at a time,
  about 440 s; the largest items are `GitReaderEnvironmentTests` 173 s, `GitInProcessCommitRouteTests` 74 s,
  `GitConfigOperationTests` 23 s, the Build fixtures about 41 s and the workspace reload fixtures about 62 s.
- `RunTestTests` is not on the critical path: its 16 process-spawning tests queue on `BuildGate.Shared`
  (`BuildIsolation.cs:439`, taken at `TestRunEngine.cs:221`) but all finish by 318 s, inside the parallel phase. Its 640 s
  fixture span comes from one `[NonParallelizable]` method that runs in the tail at 869 s.
- The tail is serial because those fixtures change process-wide state (environment variables, libgit2 search path) or need
  exclusivity (Appendix A of `plan_shorten_verification_loop.md`, fix classes E, S, X). That state is per process: in a
  separate `dotnet test` process the tail can run alongside the parallel phase instead of after it.

## Decision

1. Keep `[Category("OutOfProcess")]` plus `suite: fast` as the routine-run mechanism; do not mark tests `[Explicit]` (D1).
2. Make deferral safe: a red full run counts as stale, the marker records the commit, a nightly scheduled task runs the full
   suite when HEAD moved or the last run was red, and a SessionStart hook reports full-suite debt (stale, red, or OutOfProcess
   areas changed since the marker's commit). Plan- and batch-end skills then require a full run only when that check reports
   debt (Steps 1-6).
3. Shorten the full run itself: run each project's class-level `[NonParallelizable]` fixtures in a second, concurrent
   `dotnet test` process (the "E-row sharding" left open by `plan_shorten_verification_loop.md` Step 12b), and let the Git
   parity fixtures that own a repo per test run their tests in parallel (Steps 7-9). Estimate, unmeasured: Tools.Basic wall
   from about 918 s to about 450 s, full run from about 19-23 to about 10-12 minutes.

## Execution rules

- One `Build`/`RunTest`/`dotnet test` at a time in the main tree (CLAUDE.md). Implementer acceptance is a clean `Build` (C#
  slices) or a PowerShell parse check (script slices); test runs are the dispatcher's. Verification runs use
  `scripts/Invoke-WorktreeTests.ps1 -WorktreeRoot c:\tmp\rs-testtrees`.
- Slices that edit `scripts/Invoke-WorktreeTests.ps1` (Steps 3, 7) are strictly sequential.
- Non-C# files with Edit/Write; C# with the MCP edit tools. No new C# types, so no contract skeleton.
- Text-neutral slices first: Steps 3, 4, 7, 8, 9 can start at once; Step 1 changes warning text on `RunTest` and `Git` commit
  results.
- Compare results with the 0-failure baseline (`reference_known_failing_tests.md`); a load timeout is handled by the D5 rule in
  that file.

## Steps

### Step 1 - Marker: red counts as stale, optional HeadSha
- Files: `RoslynSentinel.Engines.Basic/FullTestMarkerStore.cs`
- Depends on: none (D2, D3)
- Effect: behaviour-changing: a fast `RunTest` result and a `Git` commit result warn when the last full run had failures; the
  warning line names the reason.
- Change:
  - `public record FullTestMarker(DateTime LastFullRunUtc, int TotalCount, int FailedCount, int FastRunsSince)` (line 17) gains a
    trailing optional `string? HeadSha = null`. Existing positional construction (`NextMarker` line 94,
    `FullTestMarkerStoreTests.cs:140`) keeps compiling. Update the type's doc comment: format line gains `"headSha":"<40 hex>"`,
    written only by `scripts/Invoke-WorktreeTests.ps1`; the server writes null (it does not read HEAD).
  - `NextMarker` (line 82): the full-run branch keeps `HeadSha` null; the fast-run branch keeps the current value (the `with`
    expression already does).
  - `Evaluate` (line 121): when `marker.FailedCount > 0`, add the reason `last full run had {FailedCount} failed tests`.
  - `Describe` (line 159): append ` ({stale.Reason})` after "fast runs ago" so a recent but red run does not read as only
    age-related.
  - No new DTO: `FullSuiteStale` (line 24, on the `RunTest` result) is unchanged; `FullTestMarker` is a persisted file record.
- Done when: `Build` 0 errors. Lane: `implementer` (1 file, about 4 edits).

### Step 2 - Tests for Step 1
- Files: `RoslynSentinel.Tests.Basic/FullTestMarkerStoreTests.cs`
- Depends on: Step 1. Pairs with: Step 3.
- Effect: text-neutral.
- Change: new tests `Evaluate_RecentRedMarker_IsStaleWithFailureReason`, `Evaluate_RecentGreenMarker_IsFresh`,
  `TryRead_MarkerWithHeadSha_ReadsIt`, `TryRead_MarkerWithoutHeadSha_HeadShaIsNull`,
  `NextMarker_FastRun_KeepsHeadSha`; update any existing assertion on `Describe` text.
- Done when: `Build` 0 errors; dispatcher runs `FullTestMarkerStoreTests` green. Lane: `test-writer`.

### Step 3 - Worktree script writes headSha
- Files: `scripts/Invoke-WorktreeTests.ps1`
- Depends on: none (unknown JSON properties are ignored by `TryRead` until Step 1 lands)
- Effect: text-neutral.
- Change: the full-run marker object (lines 487-493) gains `headSha = <full SHA of the tested commit>` (the value already used for
  `summary.json`'s `commit`, line 245). The fast-run rewrite (lines 499-517) carries an existing `headSha` through unchanged,
  read from the raw text the same way as `lastFullRunUtc`.
- Done when: PowerShell parse check; dispatcher runs `-Projects RoslynSentinel.Tests.Integration -Suite full` and the marker
  shows the run's SHA. Lane: `implementer`.

### Step 4 - Nightly full run script and task registration (new)
- Files: `scripts/Invoke-NightlyFullRun.ps1` (new), `scripts/Register-NightlyFullRun.ps1` (new)
- Depends on: Step 3 (reads `headSha`)
- Effect: text-neutral (nothing runs until the owner registers the task, D4).
- Change:
  - `Invoke-NightlyFullRun.ps1`: read the marker; resolve `master`'s SHA with `git -C <repo> rev-parse master`; when the
    marker's `headSha` equals it and `failedCount` is 0, append a `skip` line and exit 0; otherwise run
    `Invoke-WorktreeTests.ps1 -WorktreeRoot c:\tmp\rs-testtrees -Ref master -Suite full`. Append one line per night
    (`yyyy-MM-dd HH:mm | ran|skip | <sha8> | <seconds> | <failed>`) to `<scratch root>\nightly\nightly.log` (resolve with
    `scripts\roslynsentinel-scratch.ps1`). `-DryRun` prints the decision only.
  - `Register-NightlyFullRun.ps1`: `Register-ScheduledTask` for the current user, daily at `-Time` (default `03:00`), running
    `pwsh -NoProfile -File scripts\Invoke-NightlyFullRun.ps1`; `-Unregister` removes it. The script prints what it registered;
    the owner runs it.
- Done when: parse check both; `Invoke-NightlyFullRun.ps1 -DryRun` prints `ran` for the current marker (no headSha yet).
  Lane: `implementer` (2 new files).

### Step 5 - SessionStart full-suite debt check (new)
- Files: `.claude/hooks/full-suite-debt-session-start.ps1` (new), `scripts/oop-areas.json` (new), `.claude/settings.json`
- Depends on: Step 3
- Effect: behaviour-changing for agents: a session start prints one block when there is full-suite debt; silent otherwise.
- Change:
  - `oop-areas.json`: area name to path globs, from the OutOfProcess inventory (2026-11-24): `git` (`RoslynSentinel.Common/Git*.cs`,
    `RoslynSentinel.Common/Libgit2*.cs`, `RoslynSentinel.Common/CliGitReader*.cs`, `RoslynSentinel.Common/FallbackGitReader*.cs`,
    `RoslynSentinel.Common/RepoWorktrees*.cs`, `RoslynSentinel.Common/ShortHashLengths*.cs`, `RoslynSentinel.Tools.Basic/Git*.cs`),
    `runtest` (`RoslynSentinel.Engines.Basic/TestRun*.cs`, `RoslynSentinel.Engines.Basic/TestSuiteFilter.cs`,
    `RoslynSentinel.Engines.Basic/TestProjectDetector.cs`, `RoslynSentinel.Tools.Basic/WorkspaceBuildTestImpl.cs`),
    `build` (`RoslynSentinel.Engines.Basic/BuildEngine.cs`, `RoslynSentinel.Engines.Basic/BuildIsolation.cs`,
    `RoslynSentinel.Common/BuildResult*.cs`), `process` (`RoslynSentinel.Common/ChildProcessCapture.cs`,
    `RoslynSentinel.Engines.Basic/KillOnCloseJob.cs`, `RoslynSentinel.Engines.Basic/TestInstanceLauncher*.cs`,
    `RoslynSentinel.Engines.Basic/DocTrailerEngine.cs`), `scripts` (`scripts/Invoke-WorktreeTests.ps1`), and the test side
    (`RoslynSentinel.Tests.Tools.Basic/Git*.cs`, `RunTestTests.cs`, `Build*Tests.cs`).
  - Hook (runs on `startup` and `clear`, like `repo-hygiene-session-start.ps1`): read the marker; report when it is missing,
    red, older than 26 hours (the nightly missed), or when `git diff --name-only <headSha> HEAD` plus uncommitted changes match
    an area. Output: one line per reason and the matched areas, e.g.
    `Full-suite debt: areas git, runtest changed since 3f2a9c1d (last full run 14 h ago, green).`. No marker headSha: report
    "no commit recorded" and skip the area check.
  - Wire it in `.claude/settings.json` `SessionStart` beside the two existing hooks.
- Done when: parse check; run the hook by hand against the current tree and get the expected line. Lane: main session or
  `implementer` (non-C#).

### Step 6 - Skill and plan wording
- Files: `.claude/skills/implement-plan/SKILL.md` (line 81), `.claude/skills/todo-punchlist/SKILL.md` (line 70),
  `docs/current/plans/plan_shorten_verification_loop.md` ("Left open after the 2026-11-22 run", E-row item)
- Depends on: Step 5 (and D5)
- Effect: behaviour-changing for agents.
- Change: the two skill lines become: run `RunTest` with `suite: "full"` when the full-suite debt check (Step 5 hook, or
  `RunTest`'s staleness line) reports debt or the work touched an area in `scripts/oop-areas.json`; otherwise a fast run, with
  the nightly run as the backstop. The old plan's E-row item points to this plan.
- Done when: the three edits are in. Lane: main session.

### Step 7 - Shard the class-level [NonParallelizable] fixtures into a second process
- Files: `scripts/Invoke-WorktreeTests.ps1`
- Depends on: Step 3 (same file, sequential)
- Effect: text-neutral (run time only; the same tests run).
- Change:
  - For each selected project in a `-Suite full` run, scan its sources for classes with a class-level `[NonParallelizable]`
    (reuse the attribute-block parsing of `Read-ClassCategoriesFromLines`, `scripts/Get-TestRunTimings.ps1:235`; dot-sourcing loads
    only its functions). Method-level sites stay in the main process.
  - With one or more such classes: start the main process with the composed filter plus `&FullyQualifiedName!~.<Class>.` per
    class, and a shard process with `FullyQualifiedName~.<Class>.|...`, concurrently, the shard writing `<leaf>.shard.trx`. The
    leading and trailing dots stop a prefix match (`GitInProcessCommitRouteTests` against `GitInProcessCommitRouteSetupTests`).
  - The project's `summary.json` row sums both TRX files; a missing shard TRX with a non-zero exit is a failure like a missing
    main TRX (lines 341-343). `-Sequential` and filtered runs keep today's single process.
- Done when: parse check; dispatcher runs `-Projects RoslynSentinel.Tests.Tools.Basic,RoslynSentinel.Tests.Server -Suite full`
  and gets the same test totals as `48fb08e2` (3,783 and 1,029) with two TRX files per project. Lane: `implementer-senior` (the
  TRX and summary aggregation is the risky part).

### Step 8 - Timing script reads shard TRX files
- Files: `scripts/Get-TestRunTimings.ps1` (`Get-TrxPath`, line 187, and its callers), `scripts/Get-TestRunTimings.Tests.ps1`
- Depends on: Step 7's file naming (`<leaf>.shard.trx`). Pairs with: Step 9.
- Effect: text-neutral.
- Change: a project's rows read `<project>.trx` and, when present, `<project>.shard.trx`; wall and serial seconds are computed
  over the union of both. Add one canned-TRX case with a shard file.
- Done when: parse check; `pwsh -NoProfile -File scripts\Get-TestRunTimings.Tests.ps1` passes (dispatcher). Lane: `implementer`.

### Step 9 - Parallel children for Git parity fixtures that own a repo per test
- Files, per slice (each at most 3 test files in `RoslynSentinel.Tests.Tools.Basic/`):
  - 9a: `GitWorktreeRepoLocalConfigTests.cs`, `GitStageToolParityTests.cs` (the 330-480 s tail)
  - 9b: `GitCommitReadsParityTests.cs`, `GitDiffShowToolParityTests.cs`, `GitLogToolParityTests.cs`
  - 9c: `GitPatchParityCorpusMoreTests.cs`, `GitCasCommitTests.cs`, `GitCommitUntrackedParityTests.cs`
  - 9d: `GitRelativeWorktreeTests.cs`, `GitReaderParityTests.cs`
  - 9e (optional, last): `GitCommitSeamParityTests.cs` (sets `GIT_TRACE2_EVENT` process-wide under a static `TraceGate`;
    parallel siblings would inherit it while a trace test runs)
- Depends on: none. 9b-9e each after the previous slice's three green runs.
- Effect: text-neutral (test code only).
- Change: add `[Parallelizable(ParallelScope.Children)]` and `[FixtureLifeCycle(LifeCycle.InstancePerTestCase)]` with a comment
  stating that every test builds its own repo in `[SetUp]`. Any instance `[OneTimeSetUp]`/`[OneTimeTearDown]` becomes `static`
  (NUnit requires it under `InstancePerTestCase`); a fixture where that is not possible is dropped from the slice and named in
  the report. Excluded: `GitCommitMessageParityTests` (one `[OneTimeSetUp]` repo shared by about 78 cases) and
  `GitReaderEnvironmentTests` (process-wide `GIT_*` variables, stays `[NonParallelizable]`, Step 7 shards it).
- Done when: per slice `Build` 0 errors; dispatcher runs each touched class three times in a worktree
  (`-Filter FullyQualifiedName~<Class> -Suite full`) with the same result each time. Lane: `implementer` per slice.

### Step 10 - Measure and close
- Files: this plan
- Depends on: Steps 7, 8, 9 (and Step 1-5 in place)
- Effect: text-neutral.
- Change: three full runs at the final commit on an otherwise idle machine; compare with `48fb08e2-20261124-111028-65836`
  (1,378 s) and the `f77f1923` runs (1,128-1,131 s): run wall, Tools.Basic wall and serial seconds, Tests.Server wall, failure
  sets, load-timeout count. Outcome classes: adopted (equal failure sets, faster); faster with new failures (revert the slice or
  keep the shard opt-in, record the cause); not faster (record what bounds the wall). Confirm a nightly `ran` and a `skip` line
  in `nightly.log` if D4 registered the task.
- Done when: the comparison table is in this plan and Status is set; close per `docs/current/templates/README.md`
  (`Closes: plan_full_suite_nightly_and_tail_shard`). Lane: dispatcher.

## Out of scope

- `[Explicit]` on OutOfProcess tests (D1).
- Moving more fixtures to in-memory workspaces: the non-OutOfProcess Tools.Basic serial time is about 78 s and the fast run is
  238-272 s; `plan_in_memory_test_tier_for_battery.md` owns that work.
- A `BuildGate` seam or per-solution gate for `RunTestTests`: off the critical path (finishes by 318 s).
- Affected-test selection inside `RunTest` (`plan_shorten_verification_loop.md` Step 13, `proposal_runtest_tests_for_members.md`).
- `RunTest` reading HEAD in-process to fill `HeadSha` (D3).
- A template-repo helper for heavy Git `[SetUp]`s (`GitToolsSmokeTests.cs:29-42`, `BuildTemplateRepo` at about line 3145):
  revisit only if Step 10 shows a 9a fixture still on the critical path.

## Risks and open decisions

Decisions for the owner (each with a recommendation):

- **D1 - `[Explicit]` or category.** Recommend the category (no `[Explicit]`). Reason: `[Explicit]` tests run only when a
  filter names them, show as skipped in every other run, and are never run by an IDE "run all"; `OutOfProcess` plus `suite`
  already gives the routine/full split, and the marker, nightly run and hook are the safety net either way.
- **D2 - A red full run counts as stale.** Recommend yes. Reason: today a run with failures silences the warning for 2 days.
  Cost: a flake leaves the warning on until the next green full run (the nightly run usually clears it).
- **D3 - Who writes `headSha`.** Recommend the worktree script only (it already knows the commit); the server writes null, so a
  full run started through `RunTest` makes the hook report "no commit recorded" rather than a wrong area list. Alternative:
  `RunTest` reads HEAD through the in-process libgit2 reader (`Libgit2GitReader`, `RoslynSentinel.Common`); unverified that
  it exposes HEAD cheaply.
- **D4 - Nightly task.** Recommend daily at 03:00 for the owner's account, registered by the owner running
  `Register-NightlyFullRun.ps1`, testing `master` in `c:\tmp\rs-testtrees`, skipping when `master` equals the marker's
  `headSha` and the marker is green. Open: whether to notify on a red night (for example `PushNotification` from a session, or
  only the SessionStart hook line); recommend the hook line only.
- **D5 - Area check: report-only or required.** `plan_shorten_verification_loop.md` D1 warned that an automatic path map
  drifts. Recommend report-only: the hook names areas, agents and skills decide, the nightly run is the backstop, so a stale
  glob costs one missed hint for at most a day. The 26-hour threshold applies only to the hook; the server's 2-day / 40-run
  warning is unchanged.
- **D6 - Ownership of the E-row sharding.** Recommend this plan takes over the open "E-row sharding for full runs" item of
  `plan_shorten_verification_loop.md` (Step 6 edits that item to point here); that plan then closes after its Step 13.

Risks and unverified points:

- Step 7 assumes every class-level `[NonParallelizable]` reason is per-process state or CPU exclusivity. A fixture that changes
  machine-wide state (a real global git config file, a fixed temp path) would now overlap other processes. Appendix A of
  `plan_shorten_verification_loop.md` lists no such case but marks some causes "hyp" (for example `GitConfigReadParityTests`);
  Step 7's live run compares failure sets.
- The shard process adds CPU load early in the run; the X-row Build fixtures (`BuildScopeFullBuildTests` and others) time out
  under load (Appendix A). If Step 10 shows new timeouts, keep those classes in the main process tail via an exclusion list in
  the script.
- `FullyQualifiedName!~` clauses: one per sharded class (about 20 in Tools.Basic, plus Tests.Server's); filter length is far
  below command-line limits, but the NUnit adapter's handling of a long `&`-chained filter is untested.
- Estimates in the Decision rest on today's timeline (one run); Step 10 measures.
- Another session may be editing `scripts/Invoke-WorktreeTests.ps1` or the skills; re-read before Steps 3, 6, 7.
- `GitCommitMessageParityTests` (excluded from Step 9) stays intra-fixture serial; it was not on today's critical path.

## Related

- [plan_shorten_verification_loop.md](./plan_shorten_verification_loop.md) (IN PROGRESS; Appendix A is the serial-tail inventory)
- [plan_in_memory_test_tier_for_battery.md](./plan_in_memory_test_tier_for_battery.md)
- [proposal_runtest_tests_for_members.md](../proposals/proposal_runtest_tests_for_members.md)
- `scripts/Invoke-WorktreeTests.ps1`, `scripts/Get-TestRunTimings.ps1`, `RoslynSentinel.Engines.Basic/FullTestMarkerStore.cs`
