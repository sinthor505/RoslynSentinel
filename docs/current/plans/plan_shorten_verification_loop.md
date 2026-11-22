# Plan: Shorten the build/test verification loop (measure, fast tier, serial-tail cuts, flake causes)

**Status:** IN PROGRESS 2026-11-22. Steps 1-4, 5b, 6, 9, 11a done (d07a4bdc, c0a82f6b, 68db5c28, 4754cde6, c6d8c55d); fast run 567 s; Step 8 dropped (D5); Steps 13-14 stay open past this run.

## Problem

The build/test loop is the largest measured cost of a cross-cutting change, larger than the edits
(context: [finding_core_change_cost_causes_and_non_split_remedies.md](../findings/finding_core_change_cost_causes_and_non_split_remedies.md)).

**Where the time went (measured 2026-11-21).**

- `.claude/journal/*.calls.jsonl` (251 files, `durationMs`, no arguments): `Build` 3,099 calls (median 14.4 s,
  p90 35.2 s, 15.8 h summed), `RunTest` 4,983 calls (median 14.9 s, p90 114.6 s, p99 600.6 s, 71.2 h summed).
  `RunTest` calls of 120 s or more are 474 (9.5% of calls) and 61% of `RunTest` time.
- The loop changed on 2026-11-11 (owner rule: implementers never run tests; dispatchers start detached runs with
  `scripts/Invoke-WorktreeTests.ps1`, see `.claude/skills/dispatch-implementer/SKILL.md` "Tests and test runs"). Split
  at that date: `RunTest` 4,381 calls / 62.7 h before, 602 calls / 8.4 h since (3.1 h on 2026-11-11, 3.4 h on
  2026-11-12, at most 0.6 h per day after). `Build` since: 1,042 calls, median 18.9 s, 7.4 h. The foreground cost moved
  to background worktree runs: `C:\tmp\rs-testtrees\_results` holds 408 `summary.json` files, 29.4 h of run time, 15.4 h
  of it in 40 full runs. So the current cost is the wait for the per-batch full run and the machine time it occupies,
  not the agent-call time that dominated the older journal.
- 40 full runs (no filter, 9 or more projects) since 2026-11-11: median 1,317 s, p90 1,695 s, range 900 to 2,338 s;
  333 filtered runs: median 85 s, p90 153 s (about 80 s of a filtered run is the solution build and startup).
  Run windows reconstructed from `summary.json` timestamps (approximate): the 16 runs that overlapped no other run
  took 900 to 1,328 s (median about 1,230 s); all 12 runs of 1,455 s or more overlapped at least 2 other runs.
- 16 of the 40 full runs were red: 7 only from ratchet or source-scan tests, 1 from both, 8 from other tests. All 8
  runs with a ratchet failure are dated 2026-11-12 to 2026-11-15; none since (`-WithRatchets` joined the per-slice runs
  in commit 54d071e8, 2026-11-15).
- No affected-test selection exists. The proposal [proposal_runtest_tests_for_members.md](../proposals/proposal_runtest_tests_for_members.md)
  is PROPOSED with no code.

**Baseline run `a3d98b2e-20261121-163845-46852` (10,740 tests, 1,295 s total).** Wall is the first test start to the last
test end in the project's TRX; "serial" is the time with at most one test running (computed from TRX start/end times;
NUnit runs every project with `[assembly: Parallelizable(ParallelScope.Fixtures)]`, 20 logical processors).

| Project | Tests | Wall s | Test-seconds | Serial s |
| --- | --- | --- | --- | --- |
| Tests.Tools.Basic | 3,438 | 921 | 4,906 | 618 |
| Tests.Server | 903 | 158 | 243 | 137 |
| Tests.Advanced / Asyncify / Battery.Advanced / Tools.Advanced (the four Advanced projects) | 2,978 | 83 | 416 | - |
| Tests.Basic | 1,845 | 20 | 235 | 1 |
| Tests.Battery.Basic | 237 | 19 | 128 | 12 |
| Tests | 1,098 | 14 | 128 | 5 |
| Tests.ErrorVariants | 241 | 1 | 1 | - |

Findings from that run, each checked against the TRX files:

1. **The cost is a serial tail, not test volume.** Tests.Tools.Basic runs at about 19.5 concurrent tests for the first
   ~250 s, then at 1.0 from ~300 s to 921 s (618 s). The 410 tests that start after 300 s sum to 617 test-seconds
   (200 `OutOfProcess` fixtures' tests = 512 s, 210 others = 105 s), i.e. they ran one at a time. They are the
   `[NonParallelizable]` sites: 63 in Tests.Tools.Basic and 21 in Tests.Server (`Search(mode: attribute, query:
   NonParallelizable)`), which NUnit runs exclusively. Tests.Server shows the same shape (137 of 158 s serial).
   Stated reasons in source: `GitReaderEnvironmentTests` (169 s serial) sets process-wide `GIT_*` variables
   (`GitReaderEnvironmentTests.cs`, class comment); `GitPatchParityCorpusTests` "appends to one shared table"
   (`GitPatchParityCorpusTests.cs:15`; the table path is fixed, `GitPatchParityHarness.cs:238`, `rs-patch-parity`).
2. **The slow fixtures are already tagged.** 58 `[Category("OutOfProcess")]` attributes exist (57 on classes, 1 on a method;
   `Search(mode: text, query: Category("OutOfProcess"))`: 61 matches, 3 of them doc comments in `ToolEnums.cs`,
   `TestSuiteFilter.cs`, `TestSuiteFilterTests.cs`). Their share is 4,045 of 4,906 test-seconds in Tests.Tools.Basic (82%) and 4,058 of 6,057
   suite-wide (67%); the "about 40%" in `RoslynSentinel.Engines.Basic/TestSuiteFilter.cs:6` is stale. `RunTest` already
   defaults to `suite: fast` (excludes them, `WorkspaceTools.cs:238`, `TestSuiteFilter.Compose`), `build.ps1 -Fast` and
   `Test-Parallel.ps1 -Filter` use the same filter. What is missing is a fast mode in the worktree script that runs the
   per-batch full runs: `Invoke-WorktreeTests.ps1` has `-Filter`, `-WithRatchets`, `-Parallel` but no suite option, so every
   batch pays for the `OutOfProcess` fixtures.
3. **The brief's "2,700 of 4,906 test-seconds" is not reproduced.** Git* fixtures plus `RunTestTests` are 3,793 test-seconds
   (3,138 + 655); `*Parity*` fixtures plus `RunTestTests` are 2,363. Test-seconds are also not wall seconds
   (concurrency 20 then 1), so the wall impact must be measured, not inferred. Note: TRX class names carry the source
   namespace, not the project (`RunTestTests` is in project Tests.Tools.Basic with namespace
   `RoslynSentinel.Tests.Battery.Basic`); group by TRX file name.
4. **The Advanced test projects are 83 s of 1,216 s (6.8%).** Nothing here depends on the owner's decision about freezing
   unused Advanced tools out of the solution.
5. **Hypothesis, to be tested by Steps 4 and 14:** a fast-suite full run needs about 640 s sequentially (Tools.Basic
   non-`OutOfProcess` busy time is 264 s; others unchanged) and about 350 s with projects run concurrently.

**Baseline over all full runs (Step 3, 2026-11-22).** Produced by `scripts/Get-TestRunTimings.ps1 -FullOnly -View Projects -Json`, with
the medians computed per project, and by `-View Runs` and `-View Failures`. It covers 47 full runs (2026-11-12 to 2026-11-22), 18 of them
red. Each project's median is taken over the runs that included it.

- **Run totals:** the run median is 1,328 s and p90 is 1,695 s. The 17 runs that overlapped no other run took 900 to 1,328 s, median 1,229 s.

| Project | Tests | Wall s | Test-seconds | Serial s | `OutOfProcess` test-s (share) | Serial s outside `OutOfProcess` |
| --- | --- | --- | --- | --- | --- | --- |
| Tests.Tools.Basic | 3,398 | 929 | 4,850 | 628 | 3,812 (79%) | 108 |
| Tests.Server | 892 | 159 | 243 | 138 | 0 | 138 |
| Tests.Advanced | 1,691 | 33 | 262 | 17 | 0 | 17 |
| Tests.Tools.Advanced | 684 | 29 | 103 | 19 | 0 | 19 |
| Tests.Basic | 1,774 | 20 | 234 | 1 | 2 (1%) | 1 |
| Tests.Battery.Basic | 237 | 19 | 127 | 12 | 0 | 12 |
| Tests.Battery.Advanced | 440 | 16 | 16 | 16 | 0 | 16 |
| Tests | 1,041 | 13 | 107 | 6 | 12 (11%) | 0 |
| Tests.Asyncify | 155 | 4 | 24 | 0 | 0 | 0 |
| Tests.ErrorVariants | 225 | 1 | 1 | 1 | 0 | 1 |

Corrections and additions from the script:

- **Run counts:** 47 full runs and 18 red, not 40 and 16. 450 result folders, not 408. The run median is 1,328 s, not 1,317 s.
- **The Advanced four** are 82 s median wall.
- **Tests.Tools.Basic serial tail:** about 520 of its 628 serial seconds are `OutOfProcess` tests, which `-Suite fast` drops. The 108 s left
  is Step 6's target, together with Tests.Server's 138 s.
- **Test-seconds inflate under overlap:** the 18 runs since 2026-11-20 have a Tests.Tools.Basic test-seconds median of 6,047, against 4,906
  in the isolated `a3d98b2e`. So test-seconds measure contention as well as test cost.
- **Two red runs after the plan was written:**
  - `8036d20c` (first run): an `IOException` on a locked file in `Scenario03b_Rename_WithEditsAsync`, the same file-lock class as `6dda31f3`.
  - `10eef7a0`: `ToolsArchitectureDocFreshness_WhenCommittedFileExists_MatchesRegeneratedContent`, an untagged freshness ratchet. It is the
    third red run from these two tests, so "no ratchet-class failure since 2026-11-15" no longer holds. Step 11a tags them.
- **Load-timeout victims:** the three `SolutionLoadTimeoutException` tests in `9b00b68c` are all `RunTest_*` tests in Tests.Tools.Basic.

**Flakes (the 8 non-ratchet red runs), by the failure text stored in `summary.json`:**

| Run | Cause class | Evidence |
| --- | --- | --- |
| `9b00b68c`, `e4c186c5` (and `e000f0cf`, which also had ratchet failures) | `SolutionLoadTimeoutException: The solution load did not finish within 30 s` in 9 tests | `SolutionLoadWait.DefaultTimeout` is 30 s (`RoslynSentinel.Common/SolutionLoadState.cs:31`); the 3-argument `LoadSolutionAsync(path, baseRepoDir, ct)` overload uses it (`PersistentWorkspaceManager.cs:823-824`). `TestSolutionFixture.LoadTimeout` (120 s) exists but only 22 of 241 test call sites use it (`Search(mode: text, query: "LoadSolutionAsync(")`, 63 files). Commit `b5db95b2` (2026-11-21) fixed the 19 `RunTestTests` sites only. |
| `0fbe1aad` (a 14-project run: includes ModelEval and Integration) | `TestSolutionFixture 'dotnet restore'` failed (2 tests) plus a ModelEval capture test | Message starts at `TestSolutionFixture` restore; cause (network, NuGet cache, load) not in `summary.json`. |
| `6dda31f3` | `IOException ... rs-hooklaunch-...\cwd-main.marker ... being used by another process` | Already commented as a flake at `GitHooksLaunchTests.cs:98`. |
| `72b419e1`, `e821013a`, `96f9e660`, `b4d3cabf` | Assertion mismatches (hint text `RenameSymbol(`, enum-member listings, a probe class name, a variant-id list); each test passes in the previous and the next full run | Hypothesis: wording or behaviour changed by a commit in flight and fixed by the next commit, i.e. regressions found late, not flakes. Unverified until Step 9. |

Only one non-ratchet test fails in more than one run (`MoveTypeToOuterScopeAsync_UnknownFile_ThrowsNotFound`, in the two
consecutive runs `e4c186c5` and `e821013a`); the other failing tests are one-offs, so a repeat-offender list will not
shorten the loop. The causes above will.

## Decision

Measure first, then take the levers in the order of measured payoff, each as a slice that is re-measured with the same
script:

1. A timing script over the stored TRX/`summary.json` files (per project, per fixture, serial seconds, failure causes) and a
   journal change that records `suite`/`scope` so the long `RunTest` calls can be explained (Steps 1-3).
2. A `-Suite fast|full` option in `Invoke-WorktreeTests.ps1` so routine batch runs skip the `OutOfProcess` fixtures
   exactly as `RunTest`'s default does, with the full-suite marker still reset only by a full run (Steps 4-5).
3. Cut the serial tail: inventory the `[NonParallelizable]` sites with their reason, remove the cheap constraints (shared
   paths, static options), and leave the irreducible ones (process-wide environment variables) for process-level sharding
   (Steps 6-7, 12).
4. Remove the load-timeout flake class at one chokepoint instead of at 215 call sites; triage the assertion-mismatch reds
   before calling them flakes (Steps 8-10).
5. Optional, owner decision: run the ratchet tests first so a ratchet failure costs seconds (Step 11).
6. Affected-test selection stays a separate dependent plan, gated on journal data (Step 13).

Owner decisions, 2026-11-22 (settled before the run; source review the same day):

- **D1:** as recommended. The per-batch run uses `-Suite fast`. A full run is required before a push, when the marker is stale, and when the batch
  touched git, `RunTest`/`Build` or process-spawning code. New (the review found the worktree script resets the marker but never increments
  `fastRunsSince`; only `FullTestMarkerStore.cs:95` does): Step 4 also increments `fastRunsSince` on a fast unfiltered run, so both bounds
  (2 days, 40 runs) apply.
- **D2:** default `-Suite full` (as recommended).
- **D3:** 11a now, 11b last (as recommended).
- **D4:** separate plan, gated on Step 13 (as recommended).
- **D5:** no code. The owner's diagnosis is contention: the test solutions load well under 30 s, and loads time out only when 20 workers per
  process (often alongside other worktree runs) oversubscribe the CPU. Tagging the victims `[NonParallelizable]` was rejected, because the
  victims vary and the attribute lengthens the serial tail. The rule is recorded in `reference_known_failing_tests.md` (Gotchas): re-run the
  failing tests alone, and treat the timeout as a flake if they pass. Step 8 is dropped. Step 12 (worker cap) is the code lever if timeouts
  stay frequent.
- **D6:** draft targets stand; revise after Step 3 and Step 4's live run.
- Run mode: Batched; owner away (mid-run decisions are deferred to the tracker).
- Review corrections folded into the steps below: Step 1 split into 1a/1b (overlap from TRX `<Times>`, class names from the TRX
  `UnitTest/TestMethod/@className` join, fast runs before Step 4 classified by `filter == 'TestCategory!=OutOfProcess'`); Step 5 re-anchored;
  Step 4 mirrors `Compose`'s `Trim()` and keys `-WithRatchets` on the original `-Filter`.

Relation to other work: [plan_in_memory_test_tier_for_battery.md](./plan_in_memory_test_tier_for_battery.md) (IN PROGRESS) made
Tests.Battery.Basic cheap (19 s wall in the baseline run); this plan does not touch Battery fixtures and does not duplicate
it. If a Step 7 slice would edit a fixture that plan still lists as a candidate, take it from that plan's list and say so in
the commit. The build warning-level work another session is doing is out of scope.

## Execution rules

- One `Build`/`RunTest`/`dotnet test` at a time in the main tree (CLAUDE.md). Implementer acceptance is a clean `Build`
  (C# slices) or a PowerShell parse check (script slices); tests and script runs are the dispatcher's, per the
  dispatch-implementer skill ("Tests and test runs"). Verification runs use
  `scripts/Invoke-WorktreeTests.ps1 -WorktreeRoot c:\tmp\rs-testtrees`, not the main tree.
- Slices that edit `scripts/Invoke-WorktreeTests.ps1` (Steps 4, 8, 11, 12) are strictly sequential: one file, shared lines.
- Non-C# files (scripts, hooks, docs) with Edit/Write; C# with the MCP edit tools. No new C# types are added, so no
  contract skeleton is needed; the only new C# members are named with final signatures in Step 8.
- Every behaviour claim in a doc or comment gets its date and the script or run that measured it; no bare run-time numbers
  in reference docs (Step 5).
- Compare results with the baseline (0 failures per `reference_known_failing_tests.md`).

## Steps

### Step 1 - Timing report script (new)
- Files: `scripts/Get-TestRunTimings.ps1` (new), `scripts/Get-TestRunTimings.Tests.ps1` (new; plain assert script in the style
  of `scripts/Get-ScenarioSummary.Tests.ps1`, run with `pwsh -NoProfile -File`, canned TRX in a temp folder).
- Depends on: none
- Effect: text-neutral (read-only reporting).
- Change: parameters `-ResultsRoot` (default `C:\tmp\rs-testtrees\_results`), `-Run <dir-name-prefix>`, `-Last <n>`,
  `-FullOnly` (a full run = `filter` empty and 9 or more projects in `summary.json`), `-View Runs|Projects|Fixtures|Failures`,
  `-Json`. Views:
  - Runs: dir, commit8, filter, seconds, project count, total, failed, `overlappingRuns` (windows reconstructed from the
    `summary.json` timestamp minus `seconds`, labelled approximate).
  - Projects (per run, from the TRX): project (from the TRX file name), tests, wall (first start to last end), test-seconds,
    serial seconds (time at concurrency <= 1), and the same split for tests whose class is `OutOfProcess`
    (class-level `[Category("OutOfProcess")]` found by scanning the test projects' source for the attribute directly above a
    class declaration; method-level attributes such as `ShortHashLengthsTests.cs:102` are reported as "not attributed").
  - Fixtures: project, short class name (last dotted segment, nested `+` kept), tests, test-seconds, first/last offset from
    project start, serial-tail membership (test started after concurrency first fell to 1 for good).
  - Failures: per red full run the failing tests with project and the first message line (already stored in
    `summary.json`), grouped by exception-type prefix, and a flake-candidate flag (the test passes in the previous and the
    next full run) plus the run-count per test.
  Mark tests that carry `[Category("Ratchet")]` the same way as `OutOfProcess`.
- Done when: `pwsh -NoProfile -File scripts/Get-TestRunTimings.Tests.ps1` exits 0 (dispatcher runs it), and
  `Get-TestRunTimings.ps1 -Run a3d98b2e -View Projects` reproduces Tests.Tools.Basic: 3,438 tests, wall 921 s, test-seconds 4,906,
  serial about 618 s (within 2%); `-FullOnly -View Failures` lists the red full runs (16 on 2026-11-21, 18 of 47 on 2026-11-22).
  Lane: `implementer` (2 files, one new file each). Pairs with: Step 2.
- Done: 1a d07a4bdc, 1b c0a82f6b (a3d98b2e Tools.Basic: 3,438 tests, wall 921.5 s, test-seconds 4,906.1, serial 617.9 s; 18 of 47 full runs red).
- Review 2026-11-22: split into **1a** (Runs and Projects views, script skeleton with all parameters, tests for those views) and
  **1b** (Fixtures and Failures views plus their tests); 1b depends on 1a. `summary.json` has no timestamp, no `suite` and no class name
  (`failures[].test` is the method name with arguments). Facts: overlap windows come from each TRX's `<Times start= finish=>`, falling back to
  the file mtime. Class names come from joining `UnitTestResult/@testId` to `UnitTest/@id` and reading `TestMethod/@className`. Before Step 4,
  a run is fast when `filter -eq 'TestCategory!=OutOfProcess'`. A TRX does not carry categories, so a source scan is required. The script
  must be dot-sourceable, as `Get-ScenarioSummary.Tests.ps1` dot-sources its script.

### Step 2 - Journal records suite, scope and level
- Files: `.claude/hooks/journal-log-call.ps1` (the entry fields are set around line 104), `.claude/hooks/journal-hooks.Tests.ps1`.
- Depends on: none
- Effect: text-neutral (one more field per journal row for `RunTest` and `Build` calls: `suite`, `scope`, `level`,
  `hasFilter` as a boolean; no filter text, so the journal still holds no arguments beyond enum values).
- Change: when `tool_input` carries `suite`, `scope` or `level`, copy the enum string into the entry; set `hasFilter` when
  `filter` is non-empty. Add a test with a canned `RunTest` payload.
- Done when: `pwsh -NoProfile -File .claude/hooks/journal-hooks.Tests.ps1` exits 0 (dispatcher runs it); a parse check of the
  hook script passes. Lane: `implementer`. Pairs with: Step 1.
- Done: 68db5c28.
- Review 2026-11-22: copy the fields only for `RunTest` and `Build` (line 103 strips the `mcp__*__` prefix; line 104 copies `operation`
  the same way, so use it as the model). The tests run the hook under `powershell.exe` 5.1, so the change must not use pwsh-only syntax.
- Why: the journal cannot say whether the 474 `RunTest` calls of 120 s or more are `suite: full`, `scope: solution` or fast
  runs of a big project; Step 13 needs that answer.

### Step 3 - Freeze the baseline
- Files: this plan (the "Baseline" table in Problem), no code.
- Depends on: Step 1
- Effect: text-neutral.
- Change: run `Get-TestRunTimings.ps1 -FullOnly` for Runs, Projects and Failures over every full run and the median per
  project; paste the median table (Tests.Tools.Basic, Tests.Server, the Advanced four, serial seconds, share of
  `OutOfProcess`) into Problem next to the single-run table, and record the red-run causes it prints. Correct any figure in
  Problem that the script contradicts and say which.
- Done when: Problem names the per-project medians over all full runs and the script command that produced them. Lane:
  dispatcher (no agent; one command and one doc edit).
- Done: 2026-11-22, the "Baseline over all full runs" subsection in Problem.

### Step 4 - `-Suite fast|full` in the worktree script
- Files: `scripts/Invoke-WorktreeTests.ps1`.
- Depends on: none (owner decision D2 sets the default; the step ships `full` as default)
- Effect: behaviour-neutral by default. With `-Suite fast` the run excludes `OutOfProcess` fixtures.
- Change: new parameter `[ValidateSet('full','fast')][string]$Suite = 'full'`. Effective filter (mirrors
  `RoslynSentinel.Engines.Basic/TestSuiteFilter.cs` `Compose`, which is the source of truth): fast with no `-Filter` ->
  `TestCategory!=OutOfProcess`; fast with `-Filter F` -> `(F)&TestCategory!=OutOfProcess`; then the existing `-WithRatchets`
  union with parentheses: `(<filter>)|TestCategory=Ratchet` (line 113 today builds `"$Filter|TestCategory=Ratchet"`).
  Add `suite` to `summary.json`. The full-suite marker reset (line 379, `-not $Filter -and -not $Projects -and $allCompleted`)
  also requires `$Suite -eq 'full'`, so a fast unfiltered run never silences `fullSuiteStale`. Document the parameter in the
  help header and note that `build.ps1 -Fast` and `Test-Parallel.ps1 -Filter` apply the same category filter to every project
  (precedent for applying it to all default projects; `TestProjectDetector.UsesNUnitAdapter` is the guard `RunTest` uses
  for non-NUnit projects, and every default-selected project is NUnit).
  Additions (review and D1, 2026-11-22):
  - Trim `-Filter` before wrapping it, as `Compose` does.
  - `-WithRatchets` keeps keying on the original `-Filter`, not the composed string. With fast and no filter, the ratchets already run.
  - On a fast unfiltered run with no `-Projects` that completed every project, increment `fastRunsSince` in the existing marker. Leave the
    marker absent if it does not exist, and do not reset `lastFullRunUtc`.
- Done when: parse check `[void][scriptblock]::Create((Get-Content scripts/Invoke-WorktreeTests.ps1 -Raw))` raises nothing.
  Dispatcher live check (one run, `-Suite fast -Ref HEAD`): `summary.json` has `"suite": "fast"`, `total` is the full total
  minus the `OutOfProcess` tests (about 1,300 in Tests.Tools.Basic alone), and `.roslynsentinel/full-test-marker.json` is
  unchanged. Outcome classes reported by that run: fast wall under 700 s (hypothesis met), 700-1,000 s (partly), above
  1,000 s (not met; the serial tail outside `OutOfProcess` is larger than measured). Lane: `implementer`. Pairs with: Step 1.
- Done: 4754cde6. **Live check, 2026-11-22:** run `c6d8c55d-20261122-152032-35632` with `-Suite fast` and no filter, on a machine that was
  not idle (two read-only agents running).
  - **Result:** **567 s in total** (61 s of it the build). **Hypothesis met** (under 700 s).
  - **Tests:** 9,588 tests, all green.
  - **Marker:** `fastRunsSince` went from 0 to 1, and `lastFullRunUtc` was unchanged.
  - **Per-project walls:** Tools.Basic 185 s (serial 112 s), Server 162 s (serial 141 s), Advanced 33 s, Tools.Advanced 28 s, Basic 21 s,
    Battery.Basic 19 s, Battery.Advanced 16 s.
  - **Next bound:** the remaining sequential cost is the Tools.Basic plus Server tails, so `-Parallel` (Step 12) should bound the run at
    about the longer of the two plus the build.

### Step 5 - Stale run-time figures and the workflow wording
- Files (5a): `docs/current/references/reference_changing_claude_lean_tools.md` (line 70: "A whole-solution run takes about 5.5
  minutes"), `docs/current/references/reference_autonomous_run_conventions.md` ("After each batch"), `.claude/skills/dispatch-implementer/SKILL.md`
  ("Tests and test runs", per-batch full run paragraph).
  Files (5b): `scripts/build.ps1` (comment at lines 252-254 as of 2026-11-22: "~118s summed as of 2026-09-09", "~59s", "all 9 RoslynSentinel.Tests*
  projects"), `RoslynSentinel.Engines.Basic/TestSuiteFilter.cs` (class comment line 6: "about 40% of the test time").
- Depends on: 5a Steps 3 and 4 and decision D1/D2; 5b Step 3
- Effect: 5a behaviour-changing for agents (which run to start after a batch); 5b text-neutral.
- Change: 5a: replace the 5.5-minute figure with the measured median and the date, naming `Get-TestRunTimings.ps1` as the
  source; state the routine rule chosen in D1 (fast batch run with `-Suite fast`; full run before a push or when the marker is
  stale or the batch touched git, build or `RunTest` code). 5b: the two comments use the measured shares (4,045 of 4,906 in
  Tests.Tools.Basic, 67% suite-wide, dated). The `docs/current/INBOX.md` line about the 5.5-minute figure is the
  other session's entry; closing it is that session's or the owner's.
- Done when: 5a: a search of `reference_changing_claude_lean_tools.md` for "5.5 minutes" finds nothing. The dispatch-implementer skill's
  per-batch paragraph (lines 112-117 on 2026-11-22; it says the script "covers every category") names `-Suite fast` and the D1 rule. Its
  line 105 ratchet count ("about 35 quick tests") matches the count after 11a. The conventions doc's "After each batch" names `-Suite fast`.
  5b: `Build` 0 errors. Lane: `implementer` for each. Pairs with: 5b with Step 6.
- Done (5b): this commit (dispatcher edit, comments only; Engines.Basic `Build` 0 errors).
- Deferred (review 2026-11-22): the `RunTest` `filter` parameter text at `RoslynSentinel.Tools.Basic/WorkspaceTools.cs:250` carries a
  stale "about 1.5 minute" figure. Changing it is a tool-schema edit (schema budget tests, generated docs), so it is out of this step.

### Step 6 - Serial-tail inventory
- Files: none edited; the result is appended to this plan as Appendix A.
- Depends on: Step 3
- Effect: text-neutral.
- Change: `explore-tier2` (read-only). For each `[NonParallelizable]` site in `RoslynSentinel.Tests.Tools.Basic` (63) and
  `RoslynSentinel.Tests.Server` (21; a `Search(mode: attribute, query: NonParallelizable)` lists them), record: class or method, serial
  seconds from `Get-TestRunTimings.ps1 -View Fixtures`, `OutOfProcess` yes/no, the stated reason (comment) or "none stated", and a
  fix class: **E** process-wide environment variable or current directory, **S** static option or singleton, **P** shared fixed
  path or file, **X** needs exclusivity (installed git, machine-wide state), **U** reason unknown. Include the measured
  starting points: `GitReaderEnvironmentTests` (E, 169 s), `GitInProcessCommitRouteTests` (73 s), `GitPatchParityCorpusTests` and
  `GitPatchParityCorpusMoreTests` (P, 52 + 29 s), `GitConfigOperationTests` (22 s), and in Tests.Server `ToolParameterAliasTests` (51 s),
  `RelativePathInputAuditTests` (21 s), `HaltStampFilterTests` (14 s), `LargeResultOffloadFilterTests` (13 s),
  `ToolCallEchoFilterTests` (11 s), `WirePathRelativizeFilterTests` (9 s). Also list every fixed (non-Guid) path under
  `Path.GetTempPath()` or the repo that a test writes (starts with `rs-patch-parity`), and every non-ephemeral listening port (found
  so far: none; `GitToolsSmokeTests.cs:1639` binds port 0).
- Done when: Appendix A exists with one row per site and a column with the serial seconds, sorted by seconds, with the sum per
  fix class. Lane: `explore-tier2` (pass `model: sonnet` for the reason column). Pairs with: Step 8.
- Done: 2026-11-22, Appendix A.

### Step 7 - Remove the cheap serial constraints (slices from Appendix A)
- Files: per slice, at most 3 test files; first known slice: `RoslynSentinel.Tests.Tools.Basic/GitPatchParityHarness.cs`,
  `GitPatchParityCorpusTests.cs`, `GitPatchParityCorpusMoreTests.cs`.
- Depends on: Step 6
- Effect: text-neutral (test code only).
- Change: the slice list is Appendix A's P and S rows in descending serial seconds; each slice removes the shared path or static
  state (for P: a unique directory per run, for example under the test's own temp root, replacing the fixed
  `rs-patch-parity/table.tsv`; for S: pass the option instance instead of mutating a static), then drops `[NonParallelizable]`
  with a comment stating what changed. E rows stay serial (Step 12 shards them). Each slice is re-measured with Step 1's script
  on one worktree run before the next. Stop when the serial seconds outside E rows are under 60 s per project.
- Done when: per slice `Build` 0 errors; dispatcher runs the touched classes three times in a worktree
  (`-Filter FullyQualifiedName~<Class>`) with the same result each time, then one full worktree run shows the project's serial
  seconds fall by about the slice's inventory seconds. Lane: `implementer` per slice (`test-writer` where the class needs new
  fixture design). Slices that touch different projects can pair.

### Step 8 - One chokepoint for the solution-load timeout

**DROPPED 2026-11-22 (D5).** No code. The contention-flake rule is in `reference_known_failing_tests.md`, and Step 12 is the code lever. The
text below is kept for the record. The review found the anchors stale: the field is at line 32, there are 8 code reads, and there are 244
call sites in 60 files. It also found a second 30 s default, the `LoadSolution` tool's `timeoutSeconds`.

- Files: `RoslynSentinel.Common/SolutionLoadState.cs` (class `SolutionLoadWait`, lines 29-33), `RoslynSentinel.Tests.Basic/SolutionLoadWaitTests.cs`
  (new), `scripts/Invoke-WorktreeTests.ps1` (next to `$env:MSBUILDDISABLENODEREUSE = '1'`, line 264).
- Depends on: Step 4 (same script) and decision D5
- Effect: behaviour-neutral for the server (unset variable keeps 30 s); test processes started by the worktree script get 120 s.
- Change: `public static readonly TimeSpan DefaultTimeout` stays a field (all 10 references are reads: `PersistentWorkspaceManager.cs` x5,
  `SubAgentModelRun.cs` x3, `ServiceRegistrationExtensionsBasic.cs:546`, `WorkspaceReloadFailureStatusTests.cs:71`; no caller changes) but
  is initialised from new `public static TimeSpan ParseTimeout(string? raw)` (new): null, empty, non-integer or outside 1-3600 seconds -> 30 s,
  else that many seconds; the variable is `ROSLYNSENTINEL_SOLUTION_LOAD_TIMEOUT_SECONDS`, read once at type initialisation. The new test
  pins `ParseTimeout(null) == 30 s`, a valid value, and each rejected shape. The script sets the variable to `120` (the value
  `TestSolutionFixture.LoadTimeout` already uses) for every test process it starts. No test asserts the "30 s" text (searched: one
  unrelated match for "within 30 s").
- Done when: `Build` 0 errors; dispatcher runs `SolutionLoadWaitTests` and one worktree run where Tests.Tools.Basic reports no
  `SolutionLoadTimeoutException`. Lane: `implementer` (3 files). Pairs with: Step 6.

### Step 9 - Triage the non-load reds
- Files: none edited; result appended to this plan as Appendix B.
- Depends on: Step 3
- Effect: text-neutral.
- Change: `explore-tier2` classifies, for runs `72b419e1`, `e821013a`, `96f9e660`, `b4d3cabf` and for the `0fbe1aad` restore failures,
  whether the failure was (a) a regression that the next commit fixed (compare the commit range between the failing run's commit
  and the next full run's commit with `Git(operation: log)`/`diff` on the asserted text), (b) a load or ordering flake, (c) an
  environment failure (NuGet or network; read the full TRX message, not the first line). Evidence per row: commit hashes, the
  asserting line, the TRX message. Unclassifiable rows say what is missing.
- Done when: Appendix B has one row per test with class a, b or c and evidence. Lane: `explore-tier2` (`model: sonnet`).
- Done: 2026-11-22, Appendix B.

### Step 10 - Fix or comment per Appendix B
- Files: per slice, at most 3 test files (the failing fixtures).
- Depends on: Step 9
- Effect: text-neutral.
- Change: class a rows: nothing to fix in tests; record in Appendix B that the loop caught late regressions (this is a
  process finding for the owner). Class b rows: fix the cause (shared state, ordering, too short a timeout) or, when the cause is
  unknown, add the CLAUDE.md inline flake comment (what failed, run id, "passed alone"), as the already-commented fixtures do
  (`Search(mode: text, query: "KNOWN FLAKE")` lists them; `RunTestTests.cs:13-16` is a model). Class c rows: make the fixture report an environment failure distinctly (for
  `TestSolutionFixture.RestorePackages` the exception already names the restore; add the exit code and last output lines to its
  message if missing). `GitHooksLaunchTests.SubdirectoryAndLinkedWorktree_RunTheHookInTheirOwnWorkTreeRoot` is already commented
  (`GitHooksLaunchTests.cs:98`); do not add a second comment.
- Done when: `Build` 0 errors; each touched class passes three times in a worktree run. Lane: `implementer` or `test-writer` per slice.

### Step 11 - Ratchets first (owner decision D3; skip if declined)
- Files (11a): `RoslynSentinel.Tests.Server/ArchitectureDocFreshnessTests.cs` (the two `_WhenCommittedFileExists_MatchesRegeneratedContent` tests,
  lines 158 and 170). Files (11b): `scripts/Invoke-WorktreeTests.ps1`.
- Depends on: 11a none; 11b Step 4 (Step 8 dropped)
- Effect: 11a text-neutral; 11b behaviour-changing for full runs (a red ratchet stops the run early).
- Done (11a): c6d8c55d (Tests.Server count under `-WithRatchets` and a non-matching filter: 16 -> 18).
- Change: 11a: add `[Category("Ratchet")]` to those two methods only (the `_WhenUpdateEnvVarSet_RegeneratesFile` tests write files and stay
  untagged). They fail in 2 of the 8 ratchet-red runs (`069455d6`, `54e9052e`) but are missed by `-WithRatchets` today. 11b: for an
  unfiltered run, after the build and before the project loop, start `dotnet test <project> --no-build --filter TestCategory=Ratchet`
  for every selected project concurrently (the `$Parallel` `Start-Process` pattern, lines 306-315), read the TRX of each, and on any failure
  write `summary.json` (field `stage: "ratchet"`), print the failed tests, and `Finish 1` without running the project loop;
  `-NoFailFast` continues. A project with no ratchet test yields no TRX and exit 0 (zero match): treat that as pass, unlike the main loop
  (lines 341-343), which treats a missing TRX as a failure. Measured basis: the 42 ratchet and freshness tests took 90 test-seconds in
  the baseline run (longest class 17 s) against a 1,295 s run; wall of the stage is unmeasured (target under 60 s).
- Done when: 11a `Build` 0 errors, and a worktree run with `-Filter` set to a nonexistent class plus `-WithRatchets` runs 2 more tests in
  Tests.Server than before the change (the dispatcher reports the Tests.Server count before and after); 11b parse check, then a live run with
  a deliberately failing ratchet in a throwaway worktree (`-KeepWorktree`) stops with `stage: "ratchet"` in under 90 s. Lane: `implementer`
  for each. 11a pairs with Step 7.

### Step 12 - Process-level parallelism (measure, then decide D4)
- Files (12b): `scripts/Invoke-WorktreeTests.ps1`; possibly a `.runsettings` file or `-- NUnit.NumberOfTestWorkers=<n>` arguments (unverified:
  confirm the NUnit3 adapter option name in the adapter's documentation first).
- Depends on: 12a Step 4 (and 7 for the final numbers; Step 8 dropped); 12b Step 12a
- Effect: 12a text-neutral; 12b changes run time and machine load, not results.
- Change: 12a (dispatcher, no code): on an otherwise idle machine, 3 runs of `-Suite fast` sequential and 3 of `-Suite fast -Parallel`
  (the existing switch, lines 306-315, which launches one `dotnet test --no-build` per project at once); compare wall, failure sets and
  `SolutionLoadTimeoutException` counts using Step 1's script. What does not block it, verified: bin/obj (tests run `--no-build` from one
  built tree), ports (only ephemeral port 0 at `GitToolsSmokeTests.cs:1639`), TRX paths (one TRX per project). What can block it, to
  confirm in Appendix A: fixed paths (`rs-patch-parity`), `.roslynsentinel/` files under the worktree, machine-wide git state
  (`GitHooksGlobalConfigTests`, `OutOfProcess`, excluded by fast), and CPU oversubscription (10 processes x 20 NUnit workers on 20 cores;
  the script's own `-Parallel` help says the build-timeout fixtures then time out, which are `OutOfProcess` and excluded by fast).
  12b: if 12a shows equal failure sets and a wall below 450 s, make `-Parallel` the default for `-Suite fast`; cap workers per process if
  12a shows timeouts. For `-Suite full`, shard the E rows of Appendix A into their own process (filter by class list) so their serial
  time overlaps the parallel phase of other projects; only after a measured full run with equal failure sets.
- Done when: 12a table of 6 runs (wall, red or green, timeouts) in this plan; 12b parse check and a worktree run reporting the new wall.
  Outcome classes: parallel fast run equal failure sets and faster (adopt), faster but new failures (keep opt-in, record causes), no
  faster (record why; the serial tail is the bound). Lane: 12a dispatcher, 12b `implementer`.

### Step 13 - Affected-test selection gate (no code in this plan)
- Files: this plan; possibly a new plan from `proposal_runtest_tests_for_members.md`.
- Depends on: Step 2 plus at least one week of journal rows, and Step 3
- Effect: text-neutral.
- Change: with the journal's `suite`/`scope` fields, count how many of the `RunTest` calls of 120 s or more are `suite: full`,
  `scope: solution` or a fast project run, and what the median fast project run costs. Decision rule: if most of the time is `suite: full` or
  solution scope, selection would not shorten it (the fix is Steps 4-12); if fast `scope: project` calls with a filter dominate and exceed
  60 s each, write the dependent plan from the proposal. Reasons it is a separate plan: it adds a resolver engine, a per-project filter map in
  `TestRunEngine.RunAsync` and a signature change across the `RunTest` declarations (`WorkspaceTools.cs:230`, `WorkspaceBuildTestImpl.cs:471`,
  the Tools class), which the proposal itself sizes at 2-3 slices; and its premises are stale. Corrections for that plan: the proposal says about 2,600
  tests (now 10,740) and "13 projects" (the default run is 10); `RunOneProjectAsync` still does not pass `--no-build`
  (`TestRunEngine.cs:471-535`), so its open question 2 (skip the rebuild after a clean `Build`) is still a separate, independent lever worth its own entry;
  `RunTest` already has `suite`, `repoPath` and the staleness marker the proposal predates.
- Done when: the decision and its numbers are recorded in this plan; if "write the plan", a `plan_runtest_tests_for_members` draft exists.
  Lane: dispatcher.

### Step 14 - Re-measure and close
- Files: this plan.
- Depends on: Steps 4, 7, 8, 10, 12 (those that were taken)
- Effect: text-neutral.
- Change: three fresh full runs and three fresh fast runs (idle machine, no other worktree run), compare with Step 3 per project and in total.
  Targets (owner decision D6 may change them): fast full-solution run median under 10 minutes sequential and under 6 minutes with process
  parallelism; full run under 15 minutes; Tests.Tools.Basic serial seconds under 300; `SolutionLoadTimeoutException` count across the 6 runs
  recorded (no target after D5; frequent timeouts argue for Step 12's worker cap).
- Done when: the comparison table is in this plan and each target is marked met, partly met or not met with the measured value; set Status
  and close the plan per `docs/current/templates/README.md` rule 11 (`Closes: plan_shorten_verification_loop`).
  Lane: dispatcher.

## Out of scope

- Whether to freeze unused Advanced tools out of the main solution: the four Advanced test projects are 83 s of a 1,216 s run (6.8%), so nothing
  here depends on that decision.
- Build warning levels (another session is working on them; see the 2026-11-21 lines in `docs/current/INBOX.md`).
- Converting more Battery fixtures to the in-memory tier ([plan_in_memory_test_tier_for_battery.md](./plan_in_memory_test_tier_for_battery.md)).
- `RunTest` skipping the rebuild after a clean `Build` (`--no-build`; proposal open question 2): separate lever, own entry if Step 13 shows it matters.
- Reducing `ChildProcessCapture`/`GitProcess` pool starvation further ([issue_process_spawn_test_flake.md](../issues/issue_process_spawn_test_flake.md) is IMPLEMENTED; its
  item 4 stays conditional).
- Making the full run itself faster by splitting `Tests.Tools.Basic` into several projects (a larger structural change; revisit only if Steps 7 and 12 leave the
  serial tail above target).

## Risks and open decisions

Decisions for the owner (each with a recommendation):

- **D1 - What counts as routine verification.** Recommend: the per-batch background run uses `-Suite fast`; a full run is required before a push, when
  `full-test-marker.json` is stale (the existing 2 days or 40 fast runs rule, `FullTestMarkerStore`), and when the batch touched git, `RunTest`/`Build`, or process-spawning
  code (`GitProcess`, `ChildProcessCapture`, `TestRunEngine`, `Git*` tools, `scripts/Invoke-WorktreeTests.ps1`). Reason: those are exactly the
  areas the `OutOfProcess` fixtures cover, the marker already bounds staleness, and a path map that picks the suite automatically would drift. The
  `implement-plan` and `todo-punchlist` skills currently say "one full `RunTest` (`suite: "full"`)" and keep that wording for the end of a plan.
- **D2 - Default of `-Suite` in `Invoke-WorktreeTests.ps1`.** Recommend `full` (unchanged behaviour for existing handoffs and skill commands); the
  docs name `-Suite fast` for batch runs. Reason: flipping the default silently weakens the gate for anyone using the old command.
- **D3 - Ratchet-first stage (Step 11).** Recommend: do 11a (it makes `-WithRatchets` complete) and 11b, last in the order. Reason: a ratchet failure
  at the end of a 22-minute run costs a full re-run (7 of 40 runs were red only from ratchets, 10,697 s = about 3.0 h of the 15.4 h of full-run time) and the stage should cost seconds; but no full
  run has been red from a ratchet since 2026-11-15, so the expected saving today is small and it is acceptable to drop 11b.
- **D4 - Affected-test selection.** Recommend a separate dependent plan, started only if Step 13's data says it pays. Reason above (Step 13).
- **D5 - Load-timeout fix: environment seam (Step 8) or migrating call sites.** Recommend the seam. Reason: one chokepoint against 241 call sites in 63 files
  (the 22 that pass `TestSolutionFixture.LoadTimeout` stay valid); no behaviour change when the variable is unset. Cost: tests stop exercising the 30 s default
  under the worktree script; `SolutionLoadWaitTests` pins the default. The alternative, a `TestSolutionFixture.LoadAsync(manager)` helper plus a sweep, is a
  migration of about 215 sites and is the right path only if the owner does not want a production environment variable.
- **D6 - Targets for Step 14.** Recommend the targets listed there, to be revised after Step 3 and Step 4's live run (they rest on the hypothesis in
  Problem item 5).

- **Step 7 re-scope (2026-11-22, dispatcher, from Appendix A; owner away, so it is open for review).**
  - **Taken now:** the parity P slice (81 s off the full run's tail).
  - **Then:** a read-only pass over the five largest U rows outside `OutOfProcess`: `WorkspaceDebounceReloadTests`,
    `WorkspaceBrokenProjectReloadTests`, `CreateFileDeleteFileTests`, `RelativePathInputAuditTests` and `HaltStampFilterTests`, 89 s
    together. Any row whose cause turns out to be a static or a path becomes a slice; the rest stay serial.
  - **Skipped:** the S slices outside `OutOfProcess`, about 12 s in total across 7 slices. The payoff does not justify the risk.
  - **Left for a later decision:** splitting `ToolParameterAliasTests` (68 s in the Tests.Server tail) and the Server E rows (34 s). They
    need either a fixture split or the Step 12 sharding.
  - **Open question for the owner:** take the U-row fixes and the fixture split as Step 7b, or accept the fast-run tail.

Risks and unverified points:

- The `OutOfProcess` attribution in Step 1 is by source scan; `dotnet test --list-tests --filter TestCategory=OutOfProcess --no-build` could replace it but whether the
  adapter honours the filter at discovery is unverified (not tried: no test command was run while writing this plan).
- "Serial seconds" are derived from TRX start/end times; the claim that NUnit runs `[NonParallelizable]` work exclusively after the parallel phase is consistent
  with the data (concurrency 1.0 while test-seconds equal wall seconds) but is not read from NUnit documentation.
- Run overlap and the correlation of slow runs with overlap use timestamps reconstructed from file times; treat as approximate. A 10x oversubscription
  (10 processes x 20 workers) in `-Parallel` runs is an assumption: no `LevelOfParallelism` setting was found in source, so each process uses the NUnit default.
- Removing `[NonParallelizable]` can expose real shared-state races; each Step 7 slice repeats the class three times and then the full run, and a new red
  stays attributed to that slice.
- The journal covers 2026-10-02 to 2026-11-22; its totals double-count concurrent agents. The 62.7 h versus 8.4 h split shows the loop moved, not that the cost
  disappeared: the background worktree runs hold 29.4 h of machine time.
- Another session is active in this tree; `Invoke-WorktreeTests.ps1` was last changed by commits `54d071e8` and `d032b303` (2026-11-15). Re-read it before Step 4.

## Related

- [finding_core_change_cost_causes_and_non_split_remedies.md](../findings/finding_core_change_cost_causes_and_non_split_remedies.md)
- [proposal_runtest_tests_for_members.md](../proposals/proposal_runtest_tests_for_members.md)
- [plan_in_memory_test_tier_for_battery.md](./plan_in_memory_test_tier_for_battery.md)
- [issue_process_spawn_test_flake.md](../issues/issue_process_spawn_test_flake.md)
- `scripts/Invoke-WorktreeTests.ps1`, `scripts/Test-Parallel.ps1`, `scripts/build.ps1`, `RoslynSentinel.Engines.Basic/TestSuiteFilter.cs`

## Appendix A - Serial-tail inventory (Step 6, 2026-11-22)

**Sources.** `Get-TestRunTimings.ps1 -Run a3d98b2e -View Fixtures -Json`, cross-checked with `0a4dc923`. Both are isolated full runs, and
per-fixture seconds agree within about 10%.

**Where the tail starts.** Tools.Basic: 305 s (a3d98b2e) and 273 s (0a4dc923). Tests.Server: 23 s and 22 s.

**Site counts.** `Search(mode: attribute, query: NonParallelizable)` finds 230 sites: 63 in Tools.Basic (34 classes), 21 in Tests.Server
(19 classes), and 146 in other test projects (Battery.Advanced 95, ModelEval 15, Tests.Basic 12, ErrorVariants 7, Tools.Advanced 7,
SubAgent 4, Tests 3, Advanced/PlanStepRunner/Battery.Basic 1 each).

**How "Serial s" is counted.** For a class-level site it is the fixture's test-seconds; all of these fixtures are in the tail. For a
method-level site, marked (m), it is the summed duration of the `[NonParallelizable]` methods only.

**Fix classes.** E = process-wide environment variable or current directory. S = static option or singleton. P = shared fixed path.
X = needs exclusivity. U = unknown. "hyp" marks a cause that was not traced.

### Tools.Basic

| Class (sites) | Serial s | OOP | Stated reason | Fix | Mutates / note |
| --- | --- | --- | --- | --- | --- |
| GitReaderEnvironmentTests (1) | 168.6 | yes | "set process-wide GIT_* variables" | E | GIT_* env |
| GitInProcessCommitRouteTests (1) | 72.5 | yes | "GIT_TRACE2_EVENT is process-wide" | E | GIT_TRACE2_EVENT |
| GitPatchParityCorpusMoreTests (1) | 51.8 | yes | "appends to one shared table" | P | `<tmp>/rs-patch-parity/table.tsv` |
| WorkspaceDebounceReloadTests (10) | 31.8 | no | "real PersistentWorkspaceManager, cannot run in parallel" | U | hyp: debounce timer or watcher timing |
| GitPatchParityCorpusTests (1) | 29.4 | yes | same | P | same table |
| WorkspaceBrokenProjectReloadTests (6) | 22.2 | no | none stated | U | same family |
| GitConfigOperationTests (1) | 22.1 | yes | "sets HOME and USERPROFILE" | E | HOME, USERPROFILE, GIT_* |
| CreateProjectOwnWritesTests (1) | 19.7 | yes | "spawns dotnet new / dotnet sln" | X | dotnet CLI |
| BuildScopeFullBuildTests (6) | 17.2 | yes | none stated | X | full builds; hyp: build timeouts under load |
| CreateFileDeleteFileTests (3 m) | 14.8 | no | none stated | U | drift/debounce after a real reload |
| WorkspaceBuildConfigAndFoldTests (1) | 10.9 | no | none stated | U | real manager, fold and reload |
| WorkspaceStaleRefusalTests (1) | 10.3 | no | "loads a real solution through PersistentWorkspaceManager" | U | |
| BuildEngineTests (1 m) | 10.2 | yes | TODO: build timeout under load | X | |
| BuildIsolationTests (4 m) | 9.9 | yes | 2 methods "process-wide env var" | X / E | |
| DebounceFoldWriteLockTests (4) | 9.0 | no | "real PersistentWorkspaceManager" | U | CrossProcessWriteLock |
| RunTestTests (1 m) | 8.6 | yes | "diffs the shared temp dir" | P | `roslynsentinel_runtest_*.trx` |
| GitInProcessCommitRouteSetupTests (1) | 7.2 | yes | "kept serial like ...RouteTests" | E | |
| BuildCascadeFixtureTests (1 m) | 6.4 | yes | none stated | X | |
| GitHooksGlobalConfigTests (2) | 6.2 | yes | "libgit2's config search path is process-wide" | S | libgit2 search path |
| GitCasCommitEnvironmentTests (1) | 3.7 | yes | "process-wide GIT_* variables" | E | |
| GitConfigReadParityTests (1) | 3.2 | yes | none stated | U | hyp: global git config |
| GitToolsSmokeTests.Git_Pull... (1 m) | 2.4 | yes | "sets GIT_CONFIG_NOSYSTEM" | E | |
| McpServerStatusGitBackendTests (1) | 2.3 | no | timing assertions need an unsaturated pool | X | |
| BatteryTwentyTests (1) | 1.7 | no | "[SetUp] fields shared" | E | shared cwd |
| ServerBinaryStalenessGitTests (1 m) | 1.0 | yes | "known flake" | X | |
| RenameSymbolRenamesFileTests (1) | 0.5 | no | FileMoveMemory.Shared | S | |
| BatteryEighteenTests (1) | 0.4 | no | wipes `*.cs` in the shared cwd | E | |
| McpServerStatusOperatingModeTests (1) | 0.4 | no | "statics" | S | AgentOperatingModeOptions |
| CompactResultDefaultTests (1) | 0.2 | no | one test flips it | S | ChangedContentOptions.InlineOnApply |
| MoveMemberToolMultiAgentRefusalTests (1) | 0.1 | no | "process-wide static" | S | AgentOperatingModeOptions |
| MemberAddResultSizeTests (1) | 0.1 | no | another fixture flips it | S | ChangedContentOptions.InlineOnApply |
| KillOnCloseJobTests (1) | 0.0 | yes | none stated | U | hyp: X |
| LaunchBuildMarkerTests (3 m) | 0.0 | no | once-only flag is process-global | S | |
| FileMoveMemoryTests (1) | 0.0 | no | FileMoveMemory.Shared | S | |

### Tests.Server

| Class (sites) | Serial s | OOP | Stated reason | Fix | Mutates / note |
| --- | --- | --- | --- | --- | --- |
| RelativePathInputAuditTests (1) | 20.8 | no | none stated | U | in-process host, real solution |
| HaltStampFilterTests (1) | 14.3 | no | none stated | U | in-process host |
| LargeResultOffloadFilterTests (1) | 13.5 | no | flips ROSLYNSENTINEL_RESULT_MARSHALLER | E | |
| ToolCallEchoFilterTests (1) | 11.3 | no | flips process-wide options | E | ToolCallEchoOptions.Enabled plus env |
| WirePathRelativizeFilterTests (1) | 9.3 | no | test 4 flips ROSLYNSENTINEL_RELATIVE_PATHS | E | one test only |
| McpToolsetControlTests (1) | 8.3 | no | "share process-wide statics" | S | SchemaCache, SchemaOptions |
| WorkspaceStaleStampFilterTests (1) | 5.5 | no | none stated | U | |
| SchemaLeanProfileTests (1) | 1.1 | no | mutates process-wide options | S | SchemaOptions |
| (11 more S/U/E sites) | 0.0-0.4 each | no | | S, U, E | SchemaCache, LoopLatchOptions, ErrorPolicyOptions, ... |

**Long tail members without `[NonParallelizable]`.** These are fixtures whose tests run one at a time inside the fixture; the remedy is to
split the fixture, not to remove an attribute.
- `ToolParameterAliasTests` (Server): 68.2 s, 26 of its 35 tests in the tail.
- `GitWorktreeRepoLocalConfigTests`: 349.6 s, 8 tests in the tail.
- `GitToolsSmokeTests`: 206 s.

### Sums per fix class (serial s, a3d98b2e)

| Fix | Tools.Basic OOP | Tools.Basic not OOP | Server (all not OOP) |
| --- | --- | --- | --- |
| E | 276.5 | 2.1 | 34.1 |
| P | 89.8 | 0 | 0 |
| X | 64.4 | 2.3 | 0 |
| S | 6.2 | 1.3 | 11.0 |
| U | 3.2 | 99.0 | 41.0 |
| Total | 440.1 | 104.7 | 86.1 |

### Fixed (non-Guid) paths

Writes confirmed in source:
- `GitPatchParityHarness.cs:238`: `<tmp>/rs-patch-parity/table.tsv`.
- `ResumeDriftProbeTests.cs:134`: `<tmp>/rs-drift-probe/drift-probe.txt` (ModelEval).
- `BatteryTwentyFourTests.cs:125`: `<tmp>/<dirName>` (Tools.Advanced).
- `DocPathGuardTests.cs:199`: `<tmp>/DocPathGuardTests` (Tests).

Other fixed names found are likely in-memory fake paths, unverified.

Listening ports: none. The only listener binds port 0 (`GitToolsSmokeTests.cs:1639`).

### Consequence for Step 7

- **Small payoff from P and S outside `OutOfProcess`.** The P and S rows outside `OutOfProcess` total about 12 s, so Step 7 as written
  recovers little for fast runs.
- **What remains after `-Suite fast`.** It is mostly U rows: 99 s in Tools.Basic from the real-`PersistentWorkspaceManager`
  reload/debounce fixtures, and 41 s in Server from the in-process-host fixtures. Server also has 34 s of E rows and the
  `ToolParameterAliasTests` fixture.
- **The parity P slice.** The `GitPatchParityHarness`, `GitPatchParityCorpusTests` and `GitPatchParityCorpusMoreTests` slice (81 s) is
  `OutOfProcess`, so it shortens full runs only.
- **Re-scoping.** See the 2026-11-22 entry under "Risks and open decisions".

## Appendix B - Triage of the non-load reds (Step 9, 2026-11-22)

Classes: a = a regression the next commit fixed; b = a load, ordering or shared-state flake; c = an environment or contention failure.
Evidence comes from the TRX in each run folder and from `Git log` over the range between the failing run's commit and the next full run's.

| Run -> next full run | Test | Class | Evidence |
| --- | --- | --- | --- |
| `b4d3cabf` -> `0a0b0ecc` | `ResolveVariants_WildcardAndBareIdAndRejections` | a | Extra arms H and I came from harvest `cf95c8ff`. Fixed by `57e9d4c8`, whose message reads "failed in the full run at b4d3cabf". |
| `e4c186c5`, `e821013a` -> `e9013c37` | `MoveTypeToOuterScopeAsync_UnknownFile_ThrowsNotFound` | a | The not-found sweep `9b28f90b` changed the wording. Fixed by `e9013c37`. |
| `e821013a` | `FlagOff_SameEdit_GetsTheFirstSliceRedirectAndNothingIsWritten` | b | Race on the static `ToolCallHint.Current` (`ToolCallHint.cs:18, 30`). `ToolCallHintTests.Current_NoOverride_UsesFallbackAndNullClearsTestCatalog` and `CurrentOverloads_UseCurrentCatalog` swap in a catalog without RenameSymbol, so the hint prints the bare name. The two tests overlap in TRX time. |
| `72b419e1` -> `fff004a0` | `ReplaceSnippet_RenameDeclarationOnly_RejectsAndNamesTheUntouchedCaller` | b | The same `ToolCallHint.Current` race (TRX times overlap). No fix in the range. |
| `72b419e1` -> `fff004a0` | `GetFileOutline_EnumOnlyFile_...`, `ListAll_EnumMemberKindFilter_...`, `ListAll_MultiFileSolution_...` | a | Qualified enum-member names came in as Step 7 fallout. Fixed by `fff004a0`. |
| `96f9e660` -> `55d64dc5` | `RelativeFilePath_RoundTripsIntoReadFile` | a | The probe file grew past the inline limit, so the result was offloaded. Fixed by `3368d53b`, which now reads lines 1-20. |
| `0fbe1aad` -> `b8ef6d71` | `RestoreFixture_ProducesAssetsForTheTestProject`, `QuickBuild_RestoredFixture_HasNoCs0246InTheTestProject` | a | The restore exits 1 deterministically under pinned MSBuild locator variables. It is not NuGet or the network. Fixed by `a1d28a75`, which also puts the exit code, the drain flag and the binlog in the message. |
| `0fbe1aad` | `Capture_LoadsWithElevenTurnsSchemasAndCommit` | a | Stale turn positions. Fixed by `4d79e355`. ModelEval is not in later full runs, so no green run confirms it. |
| `8036d20c` (rerun at the same commit green) | `Scenario03b_Rename_WithEditsAsync` | c | An `IOException` on `<tmp>/rs-patch-parity/table.tsv` (`GitPatchParityHarness.cs:240`). The in-process lock does not cover concurrent worktree runs. The other writer is inferred: 8 runs overlapped. |

Process finding for the owner: 9 of the 13 red tests were regressions that the per-batch full run caught one or more commits late, and that the next
commit fixed. That is the loop working as designed (owner rules 2026-11-09 and 2026-11-11), at the price of a re-run. A fast run per batch catches
the same regressions sooner, since none of these 9 tests is `OutOfProcess`.

Step 10 slices:
- **10-1**, `RoslynSentinel.Tests/ToolCallHintTests.cs`: mark the two `Current` overriding tests `[NonParallelizable]` with a comment.
  Lines 78 and 102 of the same file already do this for a similar static.
- **10-2**, merged into Step 7's parity slice: a per-run table path removes both the serial constraint and the cross-process contention.
