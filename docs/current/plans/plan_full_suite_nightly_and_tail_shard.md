# Plan: Cut the full test run's wall time (parallel full runs, a second process for Tests.Git's serial classes, an NUnit worker cap)

**Status:** PARKED 2026-11-27. Pending a CPU/disk measurement of test runs (owner, 2026-11-27: wall time may reflect contention, waits or serialization, not slow tests; the design grew too complex before the root cause was known). Redesign from the measurement, if still needed. The nightly run moved to plans/plan_nightly_full_test_run.md (minimal); the earlier safety plan is SUPERSEDED. Open when parked: Q57-Q61 (readiness round 1).

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
  Amended 2026-11-27 (owner): guarded like D38: `FullTestMarker.Tests.ps1` reads `TestSuiteFilter.cs` as text and asserts
  `public const string OutOfProcessCategory = "OutOfProcess";` (`:14`) matches `$OutOfProcessCategory` (safety plan amended).
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
  speed plan draft (it keeps the E-row item, D6). Committed in `56b0ffd3`.
- Q14 (owner, 2026-11-27): D14 scope is row 2: (a) `-Parallel` becomes the default for `-Suite full` worktree runs, adopted only
  after 2 more parallel full runs show the same failure set as a sequential neighbour; (b) Tests.Git work: its class-level
  `[NonParallelizable]` classes run in a second, concurrent `dotnet test` process, and the parity fixtures that own a repo per test
  run their tests in parallel (old Step 9). Left out: sharding in other projects and a generic source-scan shard (row 3); the
  Tools.Basic tail (433 s in Q24, the next bound) is a later item. Hypothesis to measure: Tests.Git from 780 s to about 350 s, full
  run about 9-10 min. Why: row 2 gets nearly all of row 3's gain (Tools.Basic's tail is method-level, Server gains little) for M
  instead of L effort.
- Q39 (owner, 2026-11-27): D39 the second process selects by category: the class-level `[NonParallelizable]` classes in
  Tests.Git (8 at grounding; recount at step time) get `[Category("ProcessExclusive")]`; the main process adds
  `TestCategory!=ProcessExclusive`, the second runs `TestCategory=ProcessExclusive`. The name is one C# constant beside
  `TestSuiteFilter.OutOfProcessCategory` (`RoslynSentinel.Engines.Basic/TestSuiteFilter.cs:13`) plus the PowerShell copy in
  `scripts/FullTestMarker.ps1` (safety plan Step 1, D32 pattern; extend D38's text-read guard to it). A ratchet test requires every
  class-level `[NonParallelizable]` class in Tests.Git to carry the category. No `FullyQualifiedName` filters, so Q16 (filter
  probe) is dropped. Why: no parser, no fragile nested-class filter clauses, drift caught by the ratchet.
- Q40 (owner, 2026-11-27): D40 the nightly run stays sequential across projects (D29): once D14a lands, the nightly script passes
  `-Sequential` explicitly. Why: speed does not matter at 06:00 and sequential runs have fewer contention flakes for D27's retry.
  This edits `Invoke-NightlyFullRun.ps1` from the safety plan (Step 6b), so the speed plan's step depends on it.
- Phase 5 grounding (explore-tier2 sonnet + explore-tier1, 2026-11-27):
  - Tests.Git has 8 class-level `[NonParallelizable]` classes, none inherited or nested: `GitCasCommitEnvironmentTests:17`,
    `GitConfigOperationTests:18`, `GitConfigReadParityTests:16`, `GitHooksGlobalConfigTests:17`,
    `GitInProcessCommitRouteSetupTests:18`, `GitInProcessCommitRouteTests:21`, `GitReaderEnvironmentTests:24` (all
    `OutOfProcess`), and `McpServerStatusGitBackendTests:18` (no category, so it stays in the fast suite). One method-level site:
    `GitToolsSmokeTests.cs:1382` (file dirty by a peer). `AssemblyInfo.cs:7` `Parallelizable(Fixtures)`; no `LevelOfParallelism`
    or `.runsettings` in the repo.
  - Ratchets: stage at `Invoke-WorktreeTests.ps1:341-403` (`TestCategory=Ratchet`, full unfiltered only). Tests.Git has one,
    `GitCommitRoutePinRatchetTests.cs:11` (source text scan). No test reflects over `NonParallelizable`/`Category` yet.
  - `TestSuiteFilter.OutOfProcessCategory` is at `TestSuiteFilter.cs:14` (not `:13`); Tests.Git references Engines.Basic.
  - `Invoke-WorktreeTests.ps1` is 544 lines: `$runParallel` at `:177` (`-Sequential` is a no-op for full today), `Get-TestArgs`
    `:321-328`, unthrottled `Start-Process` per project `:408-417`, TRX `:444`, summary row `:445-477`, incomplete rule `:473`,
    `:483`. No project ever runs as two processes today.
  - `Get-TestRunTimings.ps1` (756 lines): `Get-TrxPath:187-189` returns one `<project>.trx`; callers `Get-ProjectsView:517`,
    `Get-FixturesView:560`. `Get-RunWindow:197` already globs `*.trx`. The safety plan's nightly `Get-FailedClasses` (Step 6a/6b)
    also reads one TRX per project.
  - Server `RunTest` full is unaffected (one process per project, `TestRunEngine.cs:596`; `Compose` passes full through).
  - Moving the 8 classes to their own project would need `CasCommitTestSupport` (used by 4 of the 8 and 7 other files) and
    `GitHookTestSupport` (`internal`, 1 of the 8 and 6 other files) in a shared support project, a `LeakDetectorSetUp` copy, a
    slnx entry (`Invoke-WorktreeTests.ps1:264-272` selects slnx projects by default) and a watch-list update (safety plan D13).
  - Old Step 9 fixtures: all 10 have only the assembly `Fixtures` scope, `[SetUp]`/`[TearDown]`, no `[OneTimeSetUp]`; 15 Tests.Git
    fixtures already use `ParallelScope.All` + `InstancePerTestCase`. The plan's per-fixture test counts are stale.
- Phase 5 design review (2026-11-27): items 1, 4, 6 pass; item 2 raises Q42 (category name in C# and PowerShell) and Q43 (a
  project's TRX set read by 3 consumers); item 3 passes with Q46; item 5 passes once Q43 and Q44 are settled; item 7 raises Q41
  (separate project instead of a second process). Implementer defaults:
  - Q45: the second process runs only for an unfiltered `-Suite full` run; fast and filtered runs (including the nightly retry)
    keep one process and add no exclusion.
  - Q46: the ratchet reflects over Tests.Git's own assembly (`ProcessExclusiveCategoryRatchetTests`, `[Category("Ratchet")]`):
    every type with class-level `NonParallelizableAttribute` carries `[Category(<constant>)]`, and every type with the category
    is class-level `[NonParallelizable]`.
  - Q47: the project's summary row sums both TRX files' counts and failures; `exitCode` is the first non-zero of the two; a
    missing TRX gets a `note` naming which process.
  - Q48: old Step 9 drops 9e (`GitCommitSeamParityTests`, process-wide `GIT_TRACE2_EVENT`) and `GitCasCommitTests` (Q18); test
    counts are refreshed at step time.
- Q41 (owner, 2026-11-27): D41 keep D39's second process (no separate test project). Why: less churn (no helper moves to a
  support project, no cross-project file moves, no slnx or watch-list change), tests stay with their area, and extending it is
  one list entry (D44).
- Q42 (owner, 2026-11-27): D42 the `ProcessExclusive` name lives in C# (constant beside `OutOfProcessCategory` in
  `TestSuiteFilter.cs`) and in `scripts/FullTestMarker.ps1`; the D38 guard in `FullTestMarker.Tests.ps1` reads `TestSuiteFilter.cs`
  as text and asserts the two values match (owner-approved item-2 exception, guarded).
- Q43 (owner, 2026-11-27): D43 `Get-ProjectTrxPaths -RunDirectory -Project` in `scripts/FullTestMarker.ps1` returns `<leaf>.trx`
  and, when present, `<leaf>.exclusive.trx` (suffix defined once there); used by the worktree script's summary, `Get-ProjectsView`
  and `Get-FixturesView` in `Get-TestRunTimings.ps1`, and the nightly `Get-FailedClasses` (safety plan Step 6a function, edited by
  this plan after that plan lands).
- Q44 (owner, 2026-11-27): D44 `$ProcessExclusiveProjects = @('RoslynSentinel.Tests.Git')` in `scripts/FullTestMarker.ps1`; a
  project gets the second process (and the main process's exclusion) only when listed, so an unlisted project runs its tagged
  classes in its main process, as today.
- Phase 6 implementer defaults (2026-11-27): Q49 `Get-ProjectProcessPlan`, `Resolve-RunParallel` and `Get-ProjectSummaryRow`
  are pure functions in `scripts/FullTestMarker.ps1` (the summary row is extracted from `Invoke-WorktreeTests.ps1:445-477`) so the
  PowerShell tests reach them; Q50 the baseline is Q24's run (`512d7738-20261127-135332-45012`, 853 s, Tests.Git 780 s) plus one
  sequential run in the closing step.
- Q51 (owner, 2026-11-27): D51 the D14a gate is tolerant: 3 parallel full runs at the final commit, each with at most 1 failed
  test, every failure passing when re-run alone, no test failing in 2 runs, and an INBOX line per flake. Why: the nightly run
  (D40) stays sequential and retries, so a rare contention flake in a manual parallel run costs one re-run.
- Q52 (owner, 2026-11-27): D52 second-process outcome classes on the median Tests.Git wall of the D51 runs against Q24's 780 s:
  kept when at least 25 % faster (at most 585 s) and D51 holds; kept opt-in (`-NoExclusiveProcess` restores one process) when
  faster but D51 fails; kept with the bound recorded at 10-25 %; reverted below 10 % (record what bounds the wall).
- Q53 probe (2026-11-27): inline RunSettings reach the NUnit adapter in this repo's setup (VSTest mode; no MTP, no
  `.runsettings`; NUnit's default is one worker per logical processor, 20 here). Scratch project
  `<scratch>\plan_design_full_suite\workers-probe` (Microsoft.NET.Test.Sdk 18.12.0, NUnit3TestAdapter 6.3.1, NUnit 4, 20 tests
  of `Thread.Sleep(1000)`, `Parallelizable(Children)`): `dotnet test --no-build` 3.5 s with 20 distinct `WorkerId`s;
  `dotnet test --no-build -- NUnit.NumberOfTestWorkers=2` 12.3 s with 2 distinct `WorkerId`s. Owner context: `LoadSolution`
  timeouts are common on this box and likely CPU contention (Q24's flake `LoadSolution_ForceReloadMidLoad_WaitsThenReloads`).
- Q53 (owner, 2026-11-27): D53 `Invoke-WorktreeTests.ps1 -TestWorkers <n>` appends `-- NUnit.NumberOfTestWorkers=<n>` to every
  `dotnet test` it starts in a parallel run (fast or full, the ratchet stage and the second process included); default
  `Get-DefaultTestWorkers` = `max(2, floor(ProcessorCount / 4))` (5 here), defined once in `scripts/FullTestMarker.ps1`. Sequential
  runs (the nightly run, D40) keep NUnit's default. Server `RunTest`, the IDE and `build.ps1` unchanged. Measured in the closing
  step: the D51 runs use the default; one parallel fast run against the 272 s fast baseline (`cc990855`) may be at most 15 % slower
  (313 s), else the fast-run default is raised; a `LoadSolution` timeout in a D51 run triggers one more set at 3 workers to pick
  the default.
- Resource probe (2026-11-27, after parking): sequential full run `45fe5686-20261127-175551-16780`, 1,455 s, 0 failures, with a
  2 s sampler (`<scratch>\plan_design_full_suite\resource-probe\sequential-20261127-175550\analysis.txt`). Share of each project's
  wall at machine CPU under 15 %: Tests.Git 57 % of 762 s, Tools.Basic 58 % of 260 s, Tests.Server 92 % of 196 s; disk under 1 %
  busy throughout. One 401 s idle window inside Tests.Git ran `GitWorktreeRepoLocalConfigTests` (`RepoLocalSetting_*`, about
  9-10 s each) and `GitReaderEnvironmentTests` (`WorktreeConfig_*`, up to 15 s) one at a time; the same `RepoLocalSetting_*`
  tests took 29-31 s at 98 % CPU during the parallel phase. Test-seconds by machine-CPU band: saturated 3,428 s, busy 1,855 s,
  low 813 s, idle 574 s. Defender real-time protection is off; a `git` process costs about 75-80 ms to spawn (`cmd` 22 ms).
  Child-process CPU is under-counted (short-lived `git` processes end between samples).
  Correction: on 20 logical CPUs one busy thread reads as 5 % machine CPU, so "under 15 %" means "under 3 cores busy", that is
  serial work, not necessarily waiting.
- VS Instrumentation profiles (owner, 2026-11-27; `<scratch>\Profiler\Report-*.diagsession`). Tests.Git `GitRelativeWorktreeTests`:
  5.72 s instrumented, 22 `GitTestRepo.RunGitRaw` calls take 1.81 s (about 82 ms each), and the rest is `TextReader.ReadToEnd`
  (3.36 s) and `Process.WaitForExit` (1.70 s) blocked on child `git` processes; test code itself is negligible. Tests.Server
  `LargeResultOffloadFilterTests` (4 tests, 5.78 s): `LoadSolution` 2.24 s over 5 calls, `TestSolutionFixture.AddFileToSolution`
  1.36 s, a once-per-process 1.44 s `ToolSignatureCatalogReflection.FromLoadedAssemblies` (169 `McpServerTool.Create` calls, via
  `BuildPostLoadHint`). Causes for a redesign: (1) Tests.Git serial classes rebuild a repo per test with `git` processes
  (`GitWorktreeRepoLocalConfigTests.SetUp` runs about 15 `git` calls before the readers under test spawn their own); (2) about 35
  Tests.Server fixtures are `[NonParallelizable]` because they mutate process-wide statics (`SchemaOptions`,
  `ToolCallEchoOptions`, env vars), and each test loads a solution in `SetUp`; (3) the parallel phase oversubscribes the CPU.
- Q54 (implementer default, 2026-11-27): the second process runs only in a parallel, unfiltered `-Suite full` run (extends Q45):
  `-Sequential` runs, including the nightly first pass (D40), keep one process per project, as old Step 7 said.
- Q55 (implementer default, 2026-11-27): the parallel default for full runs ships off (`$FullRunParallelDefault = $false` in the
  helper, read by `Resolve-RunParallel`); the D51 runs pass `-Parallel` explicitly and the closing step flips the constant only
  when D51 holds. So "adopted only after the gate" (D14a) needs no revert.
- Q56 (implementer default, 2026-11-27): the 8 classes carry
  `[Category(RoslynSentinel.Engines.Basic.TestSuiteFilter.ProcessExclusiveCategory)]` (fully qualified: the test namespace is
  `RoslynSentinel.Tests.Tools.Basic`, so no using is added), placed on the line after the class-level `[NonParallelizable]`.

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
  [plan_full_suite_nightly_and_debt_check.md](./closed/plan_full_suite_nightly_and_debt_check.md) (D7; SUPERSEDED by [plan_nightly_full_test_run.md](./plan_nightly_full_test_run.md)) and removed from this file.
- This file is the run-speed plan draft (old Steps 7-10, D6). Settled scope (D14, D39, D40):
  - Built: parallel default for full worktree runs after an adoption gate; `ProcessExclusive` category on Tests.Git's class-level
    `[NonParallelizable]` classes plus a ratchet test; a concurrent second `dotnet test` process for that category in Tests.Git
    (script); parallel children for repo-per-test Git parity fixtures (old Step 9, Q17/Q18 defaults); nightly passes
    `-Sequential`; timing script reads the second TRX; measure and close.
  - Left out: source-scan sharding, sharding outside Tests.Git, the Tools.Basic tail, `GitCasCommitTests` (race flake).
  - Phase 5 additions: `Get-ProjectTrxPaths` and `$ProcessExclusiveProjects` in `scripts/FullTestMarker.ps1` (D43, D44);
    implementer defaults Q45-Q48.
  - Exceptions: the category name in C# and PowerShell (D42, guarded by D38's text read).
  - Phase 6: worker cap for parallel runs (D53); adoption gate D51; outcome classes D52; test table below the steps.
  - Phase 7 done (2026-11-27): Steps 1a-7 written (12 slices). Still to do (resume here): show the owner the step list, phase 8
    readiness gate (fresh explore-tier2 sonnet), phase 9 finalize (Status APPROVED, TODO.md line `[READY]`, commit with both
    plans and the INBOX line).
  - Session state not in git: the INBOX.md line for `LoadSolution_ForceReloadMidLoad_WaitsThenReloads` (Q24 flake) is appended
    but uncommitted (a peer has edits in INBOX.md); commit it with this plan's finalize commit if the peer's edits are gone.

## Execution rules

- One `Build`/`RunTest`/`dotnet test` at a time in the main tree (CLAUDE.md). Implementer acceptance is a clean `Build` (C#
  slices) or a PowerShell parse check (script slices); test runs are the dispatcher's. Verification runs use
  `scripts/Invoke-WorktreeTests.ps1 -WorktreeRoot c:\tmp\rs-testtrees`.
- Cross-plan order: Steps 3, 4a, 4b, 5a and 5b start only after `plan_full_suite_nightly_and_debt_check.md` Steps 1, 4 and 6b
  have landed (they create `scripts/FullTestMarker.ps1` and `scripts/Invoke-NightlyFullRun.ps1` and edit the same lines of
  `Invoke-WorktreeTests.ps1` and `Get-TestRunTimings.ps1`). Steps 1a, 1b, 2 and 6a-6d can start at once.
- Non-C# files with Edit/Write; C# with the MCP edit tools. The only new C# members are one constant (Step 1a) and one test class
  (Step 2); they do not reference each other beyond the constant, so no contract skeleton.
- PowerShell scripts stay PowerShell 5.1 compatible where the safety plan requires it (`scripts/FullTestMarker.ps1` is loaded by
  a SessionStart hook under `powershell.exe`).
- Compare results with the 0-failure baseline (`reference_known_failing_tests.md`); a load timeout is handled by the D5 rule in
  that file. Re-read each target file before editing (peer sessions; `Search` it for `TODO`).

Slice map:

| Lane | Steps | Files |
| --- | --- | --- |
| C# category | 1a -> 1b -> 2 | `TestSuiteFilter.cs`; the 8 Tests.Git classes; `ProcessExclusiveCategoryRatchetTests.cs` (new) |
| C# parallel fixtures | 6a, 6b, 6c, 6d (independent) | 9 Tests.Git fixtures, disjoint per slice |
| Scripts | 3 -> 4a -> 4b; 3 -> 5a; 3 -> 5b | helper and its tests; worktree script; timing script and its tests; nightly script and its tests |
| Close | 7 (after all) | this plan, helper constant flip |

Pairs: 1a/1b/2 with 3; 6x with any script step; 4a with 5a or 5b (disjoint files: 4a edits the helper, 5a and 5b only call it).

## Steps

Old Steps 1-6 (safety part) moved to `plan_full_suite_nightly_and_debt_check.md` (D7); old Steps 7-10 were replaced by the
steps below on 2026-11-27 (D14-D56). Line numbers are as of `45fe5686`; the safety plan's Steps 4 and 6b shift them, so locate
by the quoted code.

### Step 1a - Category constant
- Files: `RoslynSentinel.Engines.Basic/TestSuiteFilter.cs`
- Depends on: none. Pairs with: Step 3, Step 6x.
- Effect: text-neutral (no users yet).
- Change: add, after `ExcludeOutOfProcess` (line 17), `public const string ProcessExclusiveCategory = "ProcessExclusive";` (new)
  with the doc comment: "The category on test classes that are class-level `[NonParallelizable]` because they change
  process-wide state; `scripts/Invoke-WorktreeTests.ps1` runs them in a second `dotnet test` process for the projects listed in
  `$ProcessExclusiveProjects` (`scripts/FullTestMarker.ps1`, which mirrors this value; its test checks the two match)." One
  `Member` add.
- Done when: `Build` 0 errors. Lane: `implementer`. Effort: S

### Step 1b - Tag the 8 class-level [NonParallelizable] classes
- Files (all `RoslynSentinel.Tests.Git/`): `GitCasCommitEnvironmentTests.cs`, `GitConfigOperationTests.cs`,
  `GitConfigReadParityTests.cs`, `GitHooksGlobalConfigTests.cs`, `GitInProcessCommitRouteSetupTests.cs`,
  `GitInProcessCommitRouteTests.cs`, `GitReaderEnvironmentTests.cs`, `McpServerStatusGitBackendTests.cs`
- Depends on: Step 1a.
- Effect: text-neutral (categories change no run until Step 4b).
- Change: one `ReplaceSnippet` per file: the class-level line `[NonParallelizable]` (the only `[NonParallelizable]` in each of
  these files) becomes that line followed by
  `[Category(RoslynSentinel.Engines.Basic.TestSuiteFilter.ProcessExclusiveCategory)] // runs in the second process (Invoke-WorktreeTests.ps1)`
  (Q56: fully qualified, no using added). Existing `[Category("OutOfProcess")]` lines stay. Not touched:
  `GitToolsSmokeTests.cs` (method-level site, dirty by a peer on 2026-11-27).
- Done when: `Build` 0 errors. Lane: `implementer-sweep` (one operation, 8 files, 1 project). Effort: S

### Step 2 - Ratchet test for the category
- Files: `RoslynSentinel.Tests.Git/ProcessExclusiveCategoryRatchetTests.cs` (new)
- Depends on: Step 1b.
- Effect: text-neutral (test only).
- Change: class `ProcessExclusiveCategoryRatchetTests` (new) in namespace `RoslynSentinel.Tests.Tools.Basic`, `[TestFixture]`,
  `[Category("Ratchet")]` (the stage at `Invoke-WorktreeTests.ps1:341-403` runs it). Reflection over
  `typeof(ProcessExclusiveCategoryRatchetTests).Assembly.GetTypes()`; "class-level non-parallelizable" means
  `type.GetCustomAttributes(typeof(NonParallelizableAttribute), inherit: false)` is non-empty; "carries the category" means a
  `CategoryAttribute` on the type whose `Name` equals `TestSuiteFilter.ProcessExclusiveCategory`. Tests (Q46):
  - `ClassLevelNonParallelizable_CarriesProcessExclusiveCategory`: the list of non-parallelizable types without the category
    is empty (`Assert.That(names, Is.Empty)`, names in the message).
  - `ProcessExclusiveCategory_OnlyOnClassLevelNonParallelizable`: the list of types with the category but without class-level
    `[NonParallelizable]` is empty.
  Class comment: why the category exists (D39) and that a new class-level `[NonParallelizable]` class needs it.
- Done when: `Build` 0 errors. Dispatcher: `RunTest(scope: project, project: RoslynSentinel.Tests.Git, suite: full,
  filter: "FullyQualifiedName~ProcessExclusiveCategoryRatchetTests")` green (2 tests). Lane: `test-writer`. Effort: S

### Step 3 - Helper: constants and pure functions for the second process, workers and TRX files
- Files: `scripts/FullTestMarker.ps1`, `scripts/FullTestMarker.Tests.ps1`
- Depends on: safety plan Step 1 (creates both files). The guard test needs Step 1a. Pairs with: Steps 1a-2, 6x.
- Effect: text-neutral (no callers yet).
- Change (PowerShell 5.1 compatible; new names marked):
  - Constants (new): `$ProcessExclusiveCategory = 'ProcessExclusive'` (comment: keep in step with
    `TestSuiteFilter.ProcessExclusiveCategory`; D42); `$ProcessExclusiveProjects = @('RoslynSentinel.Tests.Git')` (D44);
    `$ExclusiveTrxSuffix = '.exclusive'` (D43); `$FullRunParallelDefault = $false` (Q55, flipped by Step 7).
  - `Get-ProjectTrxPaths -RunDirectory -Project` (new): `<RunDirectory>\<Project>.trx`, then
    `<RunDirectory>\<Project>.exclusive.trx` when that file exists; only existing files (D43).
  - `Get-DefaultTestWorkers` (new): `[Math]::Max(2, [Math]::Floor([Environment]::ProcessorCount / 4))` as `[int]` (D53).
  - `Resolve-RunParallel -Suite -Parallel -Sequential` (new): throws when both switches are set (today's check,
    `Invoke-WorktreeTests.ps1:176`); `-Parallel` -> true; `-Sequential` -> false; else fast -> true, full ->
    `$FullRunParallelDefault`.
  - `Get-ProjectProcessPlan -Project -Suite -Filter -RunParallel` (new): returns an object with `MainFilter` and
    `ExclusiveFilter`. `ExclusiveFilter` is `TestCategory=<cat>` only when the project is in `$ProcessExclusiveProjects`, Suite is
    `full`, Filter is empty and RunParallel is true (Q45, Q54), and then `MainFilter` is `TestCategory!=<cat>`; otherwise
    `ExclusiveFilter` is `$null` and `MainFilter` is the filter the script composes today (passed in by the caller as `-Filter`,
    `$null` when empty).
  - Extend the D38/D32 text-read guard: the string value of `const string ProcessExclusiveCategory` in
    `RoslynSentinel.Engines.Basic/TestSuiteFilter.cs` equals `$ProcessExclusiveCategory`.
  - Tests: `Get-ProjectTrxPaths` with only the main TRX, both, neither (temp dir); `Get-DefaultTestWorkers` is at least 2;
    `Resolve-RunParallel` for full with no switch (equals `$FullRunParallelDefault`), full `-Parallel`, full `-Sequential`, fast
    with no switch, both switches (throws); `Get-ProjectProcessPlan` for a listed project in a parallel unfiltered full run (both
    filters), a fast run, a filtered run, a sequential run, an unlisted project (`ExclusiveFilter` `$null` in the last four).
- Done when: the parser reports no errors for both files. Dispatcher: `pwsh -NoProfile -File scripts\FullTestMarker.Tests.ps1` and
  `powershell.exe -NoProfile -File scripts\FullTestMarker.Tests.ps1` exit 0. Lane: `implementer`. Effort: S

### Step 4a - Extract the project summary row into the helper
- Files: `scripts/FullTestMarker.ps1`, `scripts/FullTestMarker.Tests.ps1`, `scripts/Invoke-WorktreeTests.ps1`
- Depends on: Step 3; safety plan Step 4 (same script lines). Pairs with: Step 5a or 5b.
- Effect: text-neutral (same `summary.json` rows).
- Change: move the per-project row construction (today `Invoke-WorktreeTests.ps1:444-477`: TRX path, missing-TRX `note`, TRX
  Counters, skipped and failed `UnitTestResult` lists, non-zero exit rule) into `Get-ProjectSummaryRow -Project -TrxPaths
  -ExitCodes` (new) in the helper. With several TRX paths it sums `total`, `passed`, `failed`, `skipped`, concatenates `failures`
  and `skippedTests`; `exitCode` is the first non-zero of `-ExitCodes` (else 0); an expected TRX that is missing sets `note` to
  today's text plus which process (`main` or `exclusive`) (Q47). The caller passes `Get-ProjectTrxPaths` plus the expected
  count. The worktree script calls it with one TRX and one exit code. Tests (sample TRX files written by the test): one passing
  TRX; two TRX files with a failure in the second (counts summed, failure listed); second TRX missing with exit code 1 (`note`
  names `exclusive`, `exitCode` 1).
- Done when: the parser reports no errors for the three files. Dispatcher: `FullTestMarker.Tests.ps1` exits 0 (pwsh and
  powershell.exe) and one `Invoke-WorktreeTests.ps1 -WorktreeRoot c:\tmp\rs-testtrees -Projects RoslynSentinel.Tests.Asyncify
  -Suite fast` run writes a `summary.json` row with the same fields as before. Lane: `implementer`. Effort: S

### Step 4b - Worktree script: second process, worker cap, parallel resolution
- Files: `scripts/Invoke-WorktreeTests.ps1`
- Depends on: Steps 1b, 3, 4a (without Step 1b's tags the second process would match no test).
- Effect: behaviour-changing for `-Parallel` full runs (Tests.Git runs as two processes) and for every parallel run (worker cap).
  The full-run default stays sequential until Step 7 (Q55).
- Change:
  - `$runParallel` (line 177) comes from `Resolve-RunParallel` (the check at line 176 moves into it).
  - New parameter `-TestWorkers [int]` (default `Get-DefaultTestWorkers`). When `$runParallel`, `Get-TestArgs` (lines 321-328)
    appends `--` and `NUnit.NumberOfTestWorkers=<n>` as the last arguments; the ratchet stage's `dotnet test` (line 353-358) gets
    the same. Sequential runs pass nothing (D53).
  - Per project, `Get-ProjectProcessPlan`. With an `ExclusiveFilter`: start the main process with `MainFilter` and the
    exclusive process with `ExclusiveFilter`, `--logger trx;LogFileName=<leaf>.exclusive.trx`, logs `<leaf>.exclusive.log`/`.err`,
    both through the existing `Start-Process -PassThru` path (lines 408-417) at the same time as the other projects; wait for
    both; exit codes keyed `<leaf>` and `<leaf>.exclusive`.
  - Rows from `Get-ProjectSummaryRow` with both TRX paths and both exit codes; the incomplete rules (lines 473, 483) and the
    marker write are unchanged.
  - Help text (lines 15-17, 60, 78-84): parallel default per suite, `-TestWorkers`, the second process and its TRX name.
- Done when: the parser reports no errors. Dispatcher: `Invoke-WorktreeTests.ps1 -WorktreeRoot c:\tmp\rs-testtrees -Projects
  RoslynSentinel.Tests.Git -Suite full -Parallel` writes `RoslynSentinel.Tests.Git.trx` and `RoslynSentinel.Tests.Git.exclusive.trx`,
  0 failures, and a `total` equal to a `-Sequential` run of the same project at the same commit. Lane: `implementer-senior`
  (two processes per project and the aggregation are the risky part). Effort: M

### Step 5a - Timing script reads every TRX of a project
- Files: `scripts/Get-TestRunTimings.ps1`, `scripts/Get-TestRunTimings.Tests.ps1`
- Depends on: Step 3. Pairs with: Step 4a.
- Effect: text-neutral (a run without a second TRX gives the same output).
- Change: `Get-TrxPath` (lines 187-189; callers `Get-ProjectsView` line 517 and `Get-FixturesView` line 560) is replaced by
  `Get-ProjectTrxPaths` (the script dot-sources the helper after the safety plan's Step 4); both views read the rows of every
  returned TRX, and wall and serial seconds are computed over their union. Add one test case: a run directory with
  `<project>.trx` and `<project>.exclusive.trx` (tests and time from both).
- Done when: the parser reports no errors for both files. Dispatcher: `pwsh -NoProfile -File scripts\Get-TestRunTimings.Tests.ps1`
  exits 0. Lane: `implementer`. Effort: S

### Step 5b - Nightly run: sequential first pass and every TRX
- Files: `scripts/Invoke-NightlyFullRun.ps1`, `scripts/Invoke-NightlyFullRun.Tests.ps1`
- Depends on: Step 3; safety plan Step 6b. Pairs with: Step 4a.
- Effect: behaviour-changing once Step 7 flips the full-run default (before that `-Sequential` matches today's default).
- Change: extract the first-pass argument list (safety plan Step 6b, flow item 3) into `Get-NightlyRunArguments -Sha` (new, pure),
  which adds `-Sequential` (D40) to the existing `-WorktreeRoot $DefaultWorktreeRoot -Ref <sha> -Suite full -NoFailFast`; flow
  item 4 reads each project's TRX files through `Get-ProjectTrxPaths` (D43). Tests: `Get-NightlyRunArguments` contains
  `-Sequential` and `-NoFailFast`; `Get-FailedClasses` over rows read from a main and an exclusive sample TRX.
- Done when: the parser reports no errors for both files. Dispatcher: `pwsh -NoProfile -File scripts\Invoke-NightlyFullRun.Tests.ps1`
  exits 0 and `Invoke-NightlyFullRun.ps1 -DryRun` still prints a decision. Lane: `implementer`. Effort: S

### Steps 6a-6d - Parallel children for Git parity fixtures that build a repo per test
- Files, per slice (all `RoslynSentinel.Tests.Git/`, namespace `RoslynSentinel.Tests.Tools.Basic`):
  - 6a: `GitWorktreeRepoLocalConfigTests.cs`, `GitStageToolParityTests.cs`
  - 6b: `GitCommitReadsParityTests.cs`, `GitDiffShowToolParityTests.cs`, `GitLogToolParityTests.cs`
  - 6c: `GitPatchParityCorpusMoreTests.cs`, `GitCommitUntrackedParityTests.cs`, `GitRelativeWorktreeTests.cs`
  - 6d: `GitReaderParityTests.cs`
- Depends on: none; the slices are independent of each other. Pairs with: any script step.
- Effect: text-neutral (test code only).
- Change: on each class add `[Parallelizable(ParallelScope.All)]` and `[FixtureLifeCycle(LifeCycle.InstancePerTestCase)]` (Q17;
  the pattern of the 15 fixtures that already use it, for example `GitToolsSmokeTests.cs:20-21`) with the comment "every test
  builds its own repo or temp root in [SetUp]; no process-wide state". All 9 have `[SetUp]`/`[TearDown]` and no
  `[OneTimeSetUp]`/`[OneTimeTearDown]` (grounding 2026-11-27); a class found to share state across tests (a static mutable field,
  a fixed path, an environment variable) is dropped from its slice and named in the report. Excluded (Q18, Q48):
  `GitCasCommitTests` (race flake, `INBOX.md:21`), `GitCommitSeamParityTests` (process-wide `GIT_TRACE2_EVENT`),
  `GitCommitMessageParityTests` (one repo shared by its cases).
- Done when: per slice `Build` 0 errors. Dispatcher: each touched class three times in a worktree
  (`Invoke-WorktreeTests.ps1 -WorktreeRoot c:\tmp\rs-testtrees -Projects RoslynSentinel.Tests.Git -Suite full -Filter
  "FullyQualifiedName~.<Class>"`), 0 failures each time. Lane: `implementer` per slice. Effort: S each (M total)

### Step 7 - Measure, adopt and close
- Files: this plan; `scripts/FullTestMarker.ps1` (`$FullRunParallelDefault`, and only per the outcomes below
  `Get-DefaultTestWorkers` or `$ProcessExclusiveProjects`); `scripts/Invoke-WorktreeTests.ps1` help text if the default flips
- Depends on: all other steps, and `plan_full_suite_nightly_and_debt_check.md` landed.
- Effect: behaviour-changing when the default flips.
- Change: at the final commit, on an idle box (no test processes, peers idle), in `c:\tmp\rs-testtrees`:
  1. Three `-Suite full -Parallel` runs (default workers). Gate D51: each at most 1 failed test, every failure passes when re-run
     alone (`RunTest`, scope project, filter), no test fails in 2 runs; one INBOX line per flake.
  2. One `-Suite full -Sequential` run (Q50 neighbour).
  3. One `-Suite fast` run (parallel by default): at most 313 s (D53, 15 % over the 272 s at `cc990855`), else
     `Get-DefaultTestWorkers` takes `-Suite` and returns `max(2, floor(ProcessorCount / 2))` for fast.
  4. Only if a run in item 1 had a `LoadSolution` timeout: three more `-Parallel` full runs with `-TestWorkers 3`; the setting
     with fewer timeouts (ties: faster median) becomes the default.
  Outcomes:
  - D51 holds: set `$FullRunParallelDefault = $true` and update the help text (D14a). D51 fails: the default stays sequential;
    record the failing tests.
  - D52, on the median `RoslynSentinel.Tests.Git` wall of item 1 against 780 s (`Get-TestRunTimings.ps1 -View Projects`): at
    most 585 s and D51 holds -> kept; faster but D51 fails -> kept, and a `-NoExclusiveProcess` switch (one process per project)
    is added to `Invoke-WorktreeTests.ps1` for opt-out; 10-25 % faster -> kept, bound recorded; under 10 % -> set
    `$ProcessExclusiveProjects = @()` and record what bounds the wall.
  - Comparison table in this plan: run wall, per-project wall for Tests.Git, Tools.Basic and Tests.Server, Tests.Git serial
    seconds, failure sets, `LoadSolution` timeouts, workers; baselines Q24 (`512d7738-20261127-135332-45012`, 853 s) and the
    item 2 run.
- Done when: the table and outcomes are in this plan, the edits above are committed, Status is set; close per
  `docs/current/templates/README.md` (`Closes: plan_full_suite_nightly_and_tail_shard`). Lane: dispatcher (edits by the main
  session, at most 3 in 2 files). Effort: M (about 1.5 h of runs)

## Test table

| Test | Covers | Fixture | Asserts | Pass |
| --- | --- | --- | --- | --- |
| `ProcessExclusiveCategoryRatchetTests.ClassLevelNonParallelizable_CarriesProcessExclusiveCategory` | D39, Q46 | reflection over Tests.Git's own assembly, in-process | offending type names | empty |
| `ProcessExclusiveCategoryRatchetTests.ProcessExclusiveCategory_OnlyOnClassLevelNonParallelizable` | D39, Q46 | same | offending type names | empty |
| `FullTestMarker.Tests.ps1`: `ProcessExclusive` text-read guard | D42 | reads `TestSuiteFilter.cs` | values equal | exit 0 |
| `FullTestMarker.Tests.ps1`: `Get-ProjectTrxPaths` (main only, both, neither) | D43 | temp dir | returned paths | exit 0 |
| `FullTestMarker.Tests.ps1`: `Get-ProjectProcessPlan` (5 cases) | D44, Q45, Q54 | pure | `MainFilter`, `ExclusiveFilter` | exit 0 |
| `FullTestMarker.Tests.ps1`: `Resolve-RunParallel` (5 cases), `Get-DefaultTestWorkers` | D14a, Q55, D53 | pure | bool or throw; int at least 2 | exit 0 |
| `FullTestMarker.Tests.ps1`: `Get-ProjectSummaryRow` (3 cases) | Q47 | sample TRX files | counts, `failures`, `exitCode`, `note` | exit 0 |
| `Get-TestRunTimings.Tests.ps1`: run directory with an exclusive TRX | D43 | sample run directory | tests and wall cover both files | exit 0 |
| `Invoke-NightlyFullRun.Tests.ps1`: `Get-NightlyRunArguments`; `Get-FailedClasses` over both TRX | D40, D43 | pure; sample TRX | argument present; class list | exit 0 |
| Live: Step 4b Tests.Git run | D39 end to end | worktree run (needs real `dotnet test` processes: the point is two processes) | 2 TRX files, total equal to a sequential run | equal, 0 failures |
| Live: Steps 6a-6d, 3 runs per class | Q17 | worktree run (parallel git repos are the behaviour under test) | failures | 0 in 3 of 3 |
| Live: Step 7 runs | D51, D52, D53 | worktree runs | failure sets, walls, timeouts | per Step 7 outcomes |

No C# behaviour changes outside tests, so no server-side test. Blast radius: one new constant (no existing callers changed);
`Get-TrxPath` has 2 callers (Step 5a); `Get-TestArgs` 2 call sites (Step 4b).


## Out of scope

- `[Explicit]` on OutOfProcess tests (D1).
- Moving more fixtures to in-memory workspaces: the non-OutOfProcess Tools.Basic serial time is about 78 s and the fast run is
  238-272 s; `plan_in_memory_test_tier_for_battery.md` owns that work.
- A `BuildGate` seam or per-solution gate for `RunTestTests`: off the critical path (finishes by 318 s).
- Affected-test selection inside `RunTest` (`plan_shorten_verification_loop.md` Step 13, `proposal_runtest_tests_for_members.md`).
- `RunTest` reading HEAD in-process to fill `HeadSha` (D3).
- A template-repo helper for heavy Git `[SetUp]`s (`GitToolsSmokeTests.cs:29-42`, `BuildTemplateRepo` at about line 3145):
  revisit only if Step 7 shows a 6a fixture still on the critical path.
- Sharding or a second process outside Tests.Git, a source-scan shard, and the Tools.Basic tail (433 s in Q24, method-level
  sites) (D14).
- A worker cap for sequential runs, the server's `RunTest`, the IDE or `build.ps1` (D53).

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
- Q24 (settled, measured 2026-11-27, see Decision).
- Q16 probe: dropped by D39 (category filter, no `FullyQualifiedName` clauses).
- G5 speed (settled): Q14 (D14), Q39 (D39) second-process selection, Q40 (D40) nightly shape.
- G7 readiness round 1 (2026-11-27, explore-tier2 sonnet: 1 blocker, 12 should-fix, 8 nits), open:
  - Q57: worker cap default and measurement. Q24 TRX: Tests.Git main-process tests sum 3,930 s, so 5 workers floor it near
    786 s (measured under contention; real floor lower), against D52's 585 s; the cap ships on while the parallel default ships off.
  - Q58: an outcome class on total run wall (D52 judges only Tests.Git; Tools.Basic 433 s could become the bound).
  - Q59: reopens D43's nightly consumer: the nightly first pass (`-Sequential`) and retry (filtered) never produce an exclusive TRX.
  - Q60: reopens the file location of D42-D44: `scripts/FullTestMarker.ps1` would host run-planning functions unrelated to the
    marker, parsed by the SessionStart hook at every start.
  - Q61 (fixes, applied after Q57-Q60): `Get-ProjectSummaryRow` takes per-process inputs (expected TRX path, exit code) and the
    caller recomputes `$anyFailed`; one leaf/suffix function for TRX, log and exit-code keys; one worker-argument function used
    by `Get-TestArgs` and the ratchet stage, with a test; `Get-TestArgs` takes filter and leaf; failure-report loop reads the
    exclusive log; `Resolve-RunParallel` errors go through `Fail` (exit 2); Step 1b keeps trailing comments on 3 lines; Step 2
    `scopeName`; nightly retry also `-Sequential`; Steps 6 effect is behaviour-changing, one `|` filter per slice; Step 7 split
    into measure and apply slices, marker and debt side effects stated; execution-rule ordering per step; Step 5a merges
    `.Results`; typed `missingTrx` field; stale Problem/Decision/Risks anchors marked.
- G6 phase 5 (settled: D41-D44):
  - Q41 (item 7): keep D39's second process, or move the 8 classes to their own test project (reopens D39).
  - Q42 (item 2): accept the category name in C# and PowerShell, guarded by D38's text read.
  - Q43 (item 2): one PowerShell definition of a project's TRX files, used by the worktree summary, the timing script and the
    nightly `Get-FailedClasses`.
  - Q44 (item 5): which projects get a second process (explicit list, every project, or a source scan).

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

Risks and unverified points (rewritten 2026-11-27 for D14-D56):

- The second process assumes every one of the 8 classes needs exclusivity only within its own process (environment variables,
  libgit2 search paths, `GIT_TRACE2_EVENT`, thread-pool timing). Grounding found per-process state only; a class that touched
  machine-wide state would now overlap the main process. Step 4b's live run and Step 7 compare failure sets.
- Two testhosts load the same Tests.Git `bin` output and each runs its own `LeakDetectorSetUp` working-directory check
  (hypothesis: harmless; the same already happens for different projects under `-Parallel`). Step 4b's live run shows it.
- `McpServerStatusGitBackendTests` asserts timing on a quiet thread pool; in the second process it overlaps the main process's
  parallel phase. If it fails in Step 4b or Step 7, drop its category (it then needs a ratchet exception, a new question).
- The worker cap (D53) may lengthen Tests.Git's parallel phase (about 356 s of its 780 s at 20 workers); D52 judges the combined
  result, and Step 7 item 4 retunes on timeouts.
- Estimates (Tests.Git about 350 s, full run 9-10 min) are hypotheses from one parallel run (Q24); Step 7 measures.
- Peer sessions may edit `scripts/Invoke-WorktreeTests.ps1` or Tests.Git files; re-read before each step.
- `GitCommitMessageParityTests`, `GitCommitSeamParityTests` and `GitCasCommitTests` stay intra-fixture serial (Steps 6a-6d
  exclude them).

## Related

- [plan_shorten_verification_loop.md](./plan_shorten_verification_loop.md) (IN PROGRESS; Appendix A is the serial-tail inventory)
- [plan_in_memory_test_tier_for_battery.md](./plan_in_memory_test_tier_for_battery.md)
- [proposal_runtest_tests_for_members.md](../proposals/proposal_runtest_tests_for_members.md)
- `scripts/Invoke-WorktreeTests.ps1`, `scripts/Get-TestRunTimings.ps1`, `RoslynSentinel.Engines.Basic/FullTestMarkerStore.cs`
