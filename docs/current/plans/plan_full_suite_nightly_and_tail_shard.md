# Plan: Keep OutOfProcess tests out of routine runs safely (nightly full run, debt check) and cut the full run's serial tail

**Status:** DRAFT 2026-11-27 (run speed only). Safety part APPROVED 2026-11-27. Plan: plans/plan_full_suite_nightly_and_debt_check.md. plan-design phase 4 for the speed plan: 3 of 32 questions open (Q14, Q16, Q24 run in progress).

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

plan-design log (2026-11-27):

- Q15 (investigation, explore-tier2, 2026-11-27): no full run has ever been parallel. Of 334 full summaries, 326 have
  `parallel=false` and 8 predate the key. The only recorded reason is the help comment at
  `scripts/Invoke-WorktreeTests.ps1:80-81` (CPU contention makes build-timeout fixtures time out more often), which is an untested
  hypothesis. `-Parallel` starts every project at once with no throttle (`:408-417`). `plan_shorten_verification_loop.md:425-427`
  (Step 12b) allows a parallel full default only after a measured full run with equal failure sets; never done. Fast parallel vs
  sequential at `cc990855`: 272 s vs 562 s, equal failure sets. Verdict: cross-project parallel full is untested, not known bad.
- Q7 (owner, 2026-11-27): D7 two plans. Deferral safety (marker, nightly run, debt hook, skill wording) and run speed (shard or
  parallel full run, measured separately); both designed in this session, safety first. Why: the speed design waits on the new
  Tests.Git timeline and a parallel-full measurement; the safety plan need not.
- Q6 (owner, 2026-11-27): D6 this work takes over the "E-row sharding for full runs" item of `plan_shorten_verification_loop.md`
  (it lands in the speed plan).
- Q1 (owner, 2026-11-27): D1 keep `[Category("OutOfProcess")]` plus `suite: fast`; no `[Explicit]`. Why: a forgotten filter then
  runs too much rather than silently too little, and TRX totals and the marker's `totalCount` stay honest.
- Q2 (owner, 2026-11-27): D2 a red full run counts as stale (new cause; warning stays until a green full run).
- Q9 (owner, 2026-11-27): D9 a project that exits non-zero with 0 TRX failures makes the run incomplete: the worktree script
  writes no marker, matching the server's `RanToCompletion` rule (`FullTestMarkerStore.cs:84`). No new field.
- Q3 (owner, 2026-11-27): D3 both writers record `headSha`: the worktree script (tested commit) and the server (`RecordRun` passes
  `RepoWorktrees.ReadHeadCommit(solutionRoot)`, `RoslynSentinel.Git/RepoWorktrees.cs:134`). Accepted edge: uncommitted changes in a
  server run show as debt once committed (false positive, safe direction). No `dirtyTree` flag.
- Q10 (owner, 2026-11-27): D10 `FullSuiteStale` gains `IReadOnlyList<FullSuiteStaleCause> Causes` (enum `NoFullRun`, `TooOld`,
  `TooManyFastRuns`, `LastRunFailed`, beside the record in Engines.Basic); `Reason` stays, built from the causes; tests assert
  `Causes`; one wording test against shared format constants.
- Q11 (owner, 2026-11-27): D11 shared dot-source helper `scripts/FullTestMarker.ps1` (`Read-FullTestMarker`, `Write-FullTestMarker`),
  an early text-neutral step that moves the worktree script onto it; the nightly script and hook use it.
- Q25 (owner, 2026-11-27): D25 item-2 exception accepted: the marker's JSON field names are defined in C# (`FullTestMarker`) and in
  `scripts/FullTestMarker.ps1`, each with a comment pointing to the other. Why: the format changes rarely; a guard test would add
  out-of-process work.
- Q12 (owner, 2026-11-27): D12 the nightly task uses an S4U principal (runs whether logged on or not, no stored password),
  `-RunLevel Limited`. Probe 2026-11-27: throwaway task `RS-PlanDesign-S4U-Probe` running
  `pwsh -NoProfile -File <scratch>\plan_design_full_suite\s4u-probe.ps1`, LastTaskResult 0; whoami
  `win-6fvivbdm0rr\administrator`, USERPROFILE `C:\Users\Administrator`, dotnet 11.0.100 and git 2.56.0 on PATH, pwsh 7.5.0,
  `https://api.nuget.org/v3/index.json` returned 200; `ROSLYNSENTINEL_SCRATCH_ROOT` unset (the default resolver applies). Task removed.
- Q26 (owner, 2026-11-27): D26 the nightly run defers while another test run is active: it checks for `testhost` or `dotnet test`
  processes, rechecks every 30 minutes up to 4 times, then logs a `busy` line and exits.
- Q4 (owner, partial, 2026-11-27): D4a the nightly task runs daily at 06:00 (fewer overlapping sessions than 03:00). Notification
  on a red night still open.
- Q27 (owner, 2026-11-27): D27 on a red night, re-run the failing classes once, one project at a time
  (`-Projects <p> -Filter "FullyQualifiedName~.C1.|FullyQualifiedName~.C2."`, failing classes read from the TRX). Implementer
  default: the nightly run passes `-NoFailFast`.
- Q28 (owner, 2026-11-27): D28 a pass on retry makes the marker green (`failedCount 0`, written by the nightly script through
  `scripts/FullTestMarker.ps1`); the flaky classes are recorded and the SessionStart hook prints one line naming them. A failed
  retry leaves the marker red and the hook names the classes still failing.
- Q29 (owner, 2026-11-27): D29 the nightly run keeps today's full-run shape (projects one at a time, NUnit parallel inside each);
  no forced single worker. Why: D27's retry settles contention flakes; a serial run would take hours.
- Q30 (owner, 2026-11-27): D30 a headless Claude Code session that investigates a still-red night is a separate follow-up plan.
  This plan leaves the hook-in point: the D31 result file and an optional `-OnStillRed <script>` parameter on
  `Invoke-NightlyFullRun.ps1`, unused here. Follow-up decisions noted for that plan: edit location (worktree plus branch
  `nightly-fix/<date>`, never `master`, never pushed), fix scope (`reference_harness_bug_autofix.md`; product bugs get a finding
  and an INBOX line), verification, turn and time budget, tool permissions, auth under S4U and headless MCP (probes).
- Q31 (owner, 2026-11-27): D31 the nightly run writes `<scratch>\nightly\last-nightly.json` (`date`, `sha`, `outcome`
  ran|skip|busy, `seconds`, `failedClasses`, `flakyClasses`); `nightly.log` stays the human log; the hook reads only the JSON. Its
  read and write functions live in `scripts/FullTestMarker.ps1` (D11).
- Q4 (owner, 2026-11-27): D4 daily at 06:00 (D4a), S4U (D12), tests committed `master` in `c:\tmp\rs-testtrees`, skips when the
  marker's `headSha` equals `master` and the marker is green; a red night is reported by the SessionStart hook line only.
  Implementer defaults: `StartWhenAvailable`, 4-hour execution limit (covers D26's up to 2 h of busy waits plus the run and
  retries), `MultipleInstances IgnoreNew`, `-DryRun` prints the decision.
- Q5 (owner, 2026-11-27): D5 the area check is report-only, through one script `scripts/Get-FullSuiteDebt.ps1` (causes: missing,
  red, older than 26 h, changed areas; matched areas; flaky and failing classes from D31; exit code). The SessionStart hook is a
  thin wrapper printing its one-line form; skills and references call the script at task end. No server-side area logic.
- Q8 (owner, 2026-11-27): D8 the roadmap's Phase 1 Done-when is reworded: a task-end check whose changes touch no watched area
  reports no debt, the skill runs the fast suite (which skips the `OutOfProcess` fixtures, most of `Tests.Git`), and the nightly
  run covers the rest.
- Q13 (owner, 2026-11-27): D13 watch list: area `git` as project directories (`RoslynSentinel.Git/`, `RoslynSentinel.Tests.Git/`)
  plus the three tool-side Git files; `build`, `runtest`, `process` as today's file globs (they become
  `RoslynSentinel.Engines.BuildTest/` when roadmap Phase 4 creates it); no test-file list: a changed test `.cs` containing
  `[Category("OutOfProcess")]` counts as area `outofprocess-tests`.
- Phase 5 design review (2026-11-27): items 1, 3-7 pass; item 2 raises Q32. Server headSha (D3): `RunTest` does not read HEAD for
  the loaded tree today (`WorkspaceBuildTestImpl.cs:524-529`); implementer default Q33: `FullTestMarkerStore.RecordRun` gains
  `string? headSha = null`, and `WorkspaceBuildTestImpl.RunTest` passes `RepoWorktrees.ReadHeadCommit(solutionRoot)` only for a
  full, solution-scope, unfiltered run (no repo open on fast runs). `FullSuiteStale` gains one property (dto-conventions rule 1,
  extend); `FullSuiteStaleCause` is an enum (DTO-EXEMPT). The server already treats a non-zero exit with 0 failed tests as
  incomplete (`WorkspaceBuildTestImpl.cs:534-535`), matching D9.
- Q32 (owner, 2026-11-27): D32 one PowerShell definition of the category name: `scripts/FullTestMarker.ps1` exports
  `$OutOfProcessCategory` (comment pointing to `TestSuiteFilter.OutOfProcessCategory`, `TestSuiteFilter.cs:13`); the worktree
  script (`:147`), `build.ps1` (`:83`) and the debt script use it. The one cross-language copy is accepted as in D25.
- Q22 (owner, 2026-11-27): D22 live acceptance is one unfiltered `-Suite full` worktree run in the closing step at the final
  commit: the marker carries that commit's `headSha`, `Invoke-NightlyFullRun.ps1 -DryRun` prints `skip`, `Get-FullSuiteDebt.ps1`
  reports no debt. The first real `nightly.log` line after the owner registers the task is checked opportunistically, not gated.
- Q34 (owner, 2026-11-27): D34 a nightly `skip` rewrites the marker's `LastFullRunUtc` to now (counts and `headSha` kept); the
  field means "last time a full run's green result was confirmed current". Every ran or skip night resets the age, so the hook's
  26 h cause means "the nightly run missed", and the server's 2-day `TooOld` stops firing on quiet days without a server change.
- Q35 (implementer default, 2026-11-27): D35 the D27 retry is one invocation, `-Projects <all failing> -Filter <all failing
  classes>` (one worktree, one build; projects still run one at a time).
- Q36 (owner, 2026-11-27): D36 the debt script checks every watch-list entry against `git ls-files` and adds cause
  `watch-list-stale` (counts as debt) naming each entry that matches no tracked file.
- Q37 (owner, 2026-11-27): D37 script slices go to `implementer` with one parse check as acceptance; the dispatcher runs the
  `*.Tests.ps1` scripts. Step 8 (docs, about 8 edits in 6 files) is done by the main session, owner-approved over the CLAUDE.md cap.
- Q38 (owner, 2026-11-27): D38 the marker's file location is duplicated in C# and PowerShell under D25, guarded: the PowerShell
  test script reads `RoslynSentinel.Engines.Basic/FullTestMarkerStore.cs` as text and asserts that `FileName`, the
  `.roslynsentinel` folder and the `FullTestMarker` record's parameter names (camelCased) match the helper. The same guard covers
  D25's field names.
- Q24 (owner OK, 2026-11-27): measured run started: `Invoke-WorktreeTests.ps1 -WorktreeRoot c:\tmp\rs-testtrees -Ref 512d7738
  -Suite full -Parallel` (box idle: no test processes, peers idle, CPU 22 %); log
  `<scratch>\plan_design_full_suite\q24-parallel-full.log`. Result (2026-11-27): run `512d7738-20261127-135332-45012`, 853 s
  total (build 55 s, ratchet 13 s, tests 780 s wall), 12,633 tests, 1 failed. Per project wall: Tests.Git 780 s (serial 424 s,
  OutOfProcess test time 4,256 s), Tools.Basic 433 s, Tests.Server 261 s, all others under 60 s. Sequential neighbours: 1,519 s
  (`6c573134`) and 1,934 s (`5e5a814c`), so parallel alone cuts the full run by about 44-56 %; Tests.Git is still the whole
  critical path. The one failure, `LoadSolution_ForceReloadMidLoad_WaitsThenReloads` (Tools.Basic), passed alone 35 s later
  (RunTest, scope project, filter); recorded in INBOX.md as a contention flake candidate. Side effect: the run rewrote
  `.roslynsentinel/full-test-marker.json` (failedCount 1, no headSha).
- Phase 7 layout (2026-11-27): the safety plan is written as `plan_full_suite_nightly_and_debt_check.md`; this file stays as the
  speed plan draft (it keeps the E-row item, D6).

Original 2026-11-24 decisions:

1. Keep `[Category("OutOfProcess")]` plus `suite: fast` as the routine-run mechanism; do not mark tests `[Explicit]` (D1).
2. Make deferral safe: a red full run counts as stale, the marker records the commit, a nightly scheduled task runs the full
   suite when HEAD moved or the last run was red, and a SessionStart hook reports full-suite debt (stale, red, or OutOfProcess
   areas changed since the marker's commit). Plan- and batch-end skills then require a full run only when that check reports
   debt (moved to `plan_full_suite_nightly_and_debt_check.md`, D7).
3. Shorten the full run itself: run each project's class-level `[NonParallelizable]` fixtures in a second, concurrent
   `dotnet test` process (the "E-row sharding" left open by `plan_shorten_verification_loop.md` Step 12b), and let the Git
   parity fixtures that own a repo per test run their tests in parallel (Steps 7-9). Estimate, unmeasured: Tools.Basic wall
   from about 918 s to about 450 s, full run from about 19-23 to about 10-12 minutes.

## Current design (plan-design 2026-11-27)

- Safety part (marker, nightly run, debt check, wording; old Steps 1-6): moved to
  [plan_full_suite_nightly_and_debt_check.md](./plan_full_suite_nightly_and_debt_check.md) (D7) and removed from this file.
- This file is the run-speed plan draft (old Steps 7-10, D6). Still open: Q14 scope, Q16 filter probe, Q24 parallel full run
  (running). Steps 7-10 below predate the 2026-11-27 grounding (critical path now `Tests.Git`) and will be redesigned.

## Execution rules

- One `Build`/`RunTest`/`dotnet test` at a time in the main tree (CLAUDE.md). Implementer acceptance is a clean `Build` (C#
  slices) or a PowerShell parse check (script slices); test runs are the dispatcher's. Verification runs use
  `scripts/Invoke-WorktreeTests.ps1 -WorktreeRoot c:\tmp\rs-testtrees`.
- Step 7 edits `scripts/Invoke-WorktreeTests.ps1` after `plan_full_suite_nightly_and_debt_check.md` Step 4 has landed.
- Non-C# files with Edit/Write; C# with the MCP edit tools. No new C# types, so no contract skeleton.
- Text-neutral slices first: Steps 7, 8, 9 can start at once.
- Compare results with the 0-failure baseline (`reference_known_failing_tests.md`); a load timeout is handled by the D5 rule in
  that file.

## Steps

Old Steps 1-6 (safety part) were moved to `plan_full_suite_nightly_and_debt_check.md` on 2026-11-27 (D7).

### Step 7 - Shard the class-level [NonParallelizable] fixtures into a second process
- Files: `scripts/Invoke-WorktreeTests.ps1`
- Depends on: `plan_full_suite_nightly_and_debt_check.md` Step 4 (same file, sequential)
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
- Done when: parse check; dispatcher runs `-Projects RoslynSentinel.Tests.Tools.Basic,RoslynSentinel.Tests.Git,RoslynSentinel.Tests.Server -Suite full`
  and gets the same test totals as an unsharded run at the starting commit (the `48fb08e2` totals predate the Tests.Git
  split) with two TRX files per project that has a class-level `[NonParallelizable]` fixture. Lane: `implementer-senior` (the
  TRX and summary aggregation is the risky part).

### Step 8 - Timing script reads shard TRX files
- Files: `scripts/Get-TestRunTimings.ps1` (`Get-TrxPath`, line 187, and its callers), `scripts/Get-TestRunTimings.Tests.ps1`
- Depends on: Step 7's file naming (`<leaf>.shard.trx`). Pairs with: Step 9.
- Effect: text-neutral.
- Change: a project's rows read `<project>.trx` and, when present, `<project>.shard.trx`; wall and serial seconds are computed
  over the union of both. Add one canned-TRX case with a shard file.
- Done when: parse check; `pwsh -NoProfile -File scripts\Get-TestRunTimings.Tests.ps1` passes (dispatcher). Lane: `implementer`.

### Step 9 - Parallel children for Git parity fixtures that own a repo per test
- Files, per slice (each at most 3 test files, all in `RoslynSentinel.Tests.Git/`, namespace still
  `RoslynSentinel.Tests.Tools.Basic`):
  - 9a: `GitWorktreeRepoLocalConfigTests.cs`, `GitStageToolParityTests.cs` (the 330-480 s tail)
  - 9b: `GitCommitReadsParityTests.cs`, `GitDiffShowToolParityTests.cs`, `GitLogToolParityTests.cs`
  - 9c: `GitPatchParityCorpusMoreTests.cs`, `GitCasCommitTests.cs`, `GitCommitUntrackedParityTests.cs`
  - 9d: `GitRelativeWorktreeTests.cs`, `GitReaderParityTests.cs`
  - 9e (optional, last): `GitCommitSeamParityTests.cs` (sets `GIT_TRACE2_EVENT` process-wide under a static `TraceGate`;
    parallel siblings would inherit it while a trace test runs)
- Depends on: none (the move into `RoslynSentinel.Tests.Git` landed with `plan_isolate_git_area_into_own_project.md`
  Steps 16-18). 9b-9e each after the previous slice's three green runs.
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
- Depends on: Steps 7, 8, 9 (and `plan_full_suite_nightly_and_debt_check.md` in place)
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

### plan-design question inventory (2026-11-27)

Grounding (explore-tier2, 2026-11-27) found: the critical path moved to `Tests.Git` (about 800 s in run
`6c573134-20261127-022016`, 1,519 s wall; Tools.Basic now 282 s); full runs are sequential across projects
(`parallel=false` in 326 of 334 full summaries), so the 10-12 min estimate does not hold; `RepoWorktrees.ReadHeadCommit`
(`RoslynSentinel.Git/RepoWorktrees.cs:134`) reads HEAD cheaply and Engines.Basic already reaches it; several anchors stale.

Owner decisions (groups in asking order):
- G1 structure: Q7 split into two plans (deferral safety / run speed); Q6 (D6) E-row ownership; Q1 (D1) category, no `[Explicit]`.
- G2 marker: Q2 (D2) red counts stale; Q9 what counts as red (non-zero exit with 0 TRX failures, `Invoke-WorktreeTests.ps1:473`);
  Q3 (D3) who writes `headSha` (reopened: server can via `RepoWorktrees.ReadHeadCommit`); Q10 stale-reason shape (typed field vs
  reason constants; `FullTestMarkerStoreTests.cs:291`, `:313` assert literal text); Q11 shared PowerShell marker helper (script
  writer plus nightly and hook readers).
- G3 nightly: Q4 (D4) schedule, target, notification; Q12 scheduled-task logon type (interactive only, S4U, stored password).
- G4 debt check: Q5 (D5) report-only vs gating; Q8 roadmap Phase 1 "a change outside the Git directories skips Tests.Git" vs
  report-only (depends on Q5); Q13 watch list as file globs (about 13 OutOfProcess fixtures' sources uncovered) vs project
  directories (roadmap Q44).
- G5 speed: Q14 scope (generic per-project shard / Tests.Git only / cross-project parallel full run); depends on Q16, Q24.
- Readiness gate round 1 (2026-11-27, safety plan): Q34 marker age vs nightly `skip` (blocker: a skip never refreshes
  `LastFullRunUtc`, so the 26 h cause and the server's 2-day `TooOld` fire with nothing changed); Q35 retry as one invocation
  (implementer default: one `-Projects <all failing> -Filter <all classes>` run; projects still run one at a time); Q36 watch-list
  entries that match no tracked file; Q37 lanes for script slices and Step 8 over the main-session cap; Q38 D25 extended to the
  marker file location (`.roslynsentinel/full-test-marker.json` in C# and PowerShell).
- G3 additions: Q27 nightly retry on red (granularity, settled); Q28 marker outcome when the retry passes (settled); Q29
  serial-by-default nightly; Q30 a headless Claude Code session that investigates and fixes a still-red night (owner idea,
  2026-11-27); Q31 the nightly run writes a structured result file the hook reads.
- G3 addition (settled): Q26 what the nightly run does when the box is busy (another test run, an overnight autonomous run).
- G2 addition (settled): Q25 accept the item-2 exception that the marker's JSON field names live in C# (`FullTestMarker`) and in the
  PowerShell helper, or add a guard.

Investigations and probes:
- Q24 (owner OK needed): one measured `Invoke-WorktreeTests.ps1 -Suite full -Parallel` run (15-25 min, occupies the box's test runs).
- Q16 probe: NUnit adapter with a long `&`-chained `FullyQualifiedName!~` filter and nested-class FQNs; only if Q14 picks script-scan sharding.

Implementer defaults (no longer open once the step states them):
- Q17: Step 9 uses `[Parallelizable(ParallelScope.All)]` plus `InstancePerTestCase` (15 Tests.Git fixtures already do), not `Children`.
- Q18: `GitCasCommitTests` leaves Step 9 (committer-race flake, `INBOX.md:21`).
- Q19: Step 3 depends on Step 1 (the pre-Step-1 server drops unknown JSON properties on its fast-run rewrite, `FullTestMarkerStore.cs:61`, `:192`).
- Q20: Step 6 also edits `reference_autonomous_run_conventions.md:106-109`, `dispatch-implementer/SKILL.md:115-121` (the two
  skill lines only point there), `TODO.md:9`, `:16` and roadmap lines 257-258.
- Q21: a note in `plan_isolate_build_test_area_into_own_project.md` that its move updates the watch list and Step 1's path.
- Q23: filtered and per-project runs never clear debt (the marker stays all-or-nothing); stated in the hook's doc comment.

Phase 6: Q22 Step 3's acceptance (needs an unfiltered full run to write the marker, `Invoke-WorktreeTests.ps1:485`) and a fresh
baseline (current full runs 1,519 s and 1,934 s, high variance).

### Original decisions D1-D6 (from the 2026-11-24 draft)

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
- Another session may be editing `scripts/Invoke-WorktreeTests.ps1` or the skills; re-read before Step 7.
- `GitCommitMessageParityTests` (excluded from Step 9) stays intra-fixture serial; it was not on today's critical path.

## Related

- [plan_shorten_verification_loop.md](./plan_shorten_verification_loop.md) (IN PROGRESS; Appendix A is the serial-tail inventory)
- [plan_in_memory_test_tier_for_battery.md](./plan_in_memory_test_tier_for_battery.md)
- [proposal_runtest_tests_for_members.md](../proposals/proposal_runtest_tests_for_members.md)
- `scripts/Invoke-WorktreeTests.ps1`, `scripts/Get-TestRunTimings.ps1`, `RoslynSentinel.Engines.Basic/FullTestMarkerStore.cs`
