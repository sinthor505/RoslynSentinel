# Plan: Nightly full test run in the background, reported by a SessionStart line when red or missed

**Status:** IN PROGRESS 2026-11-27. Steps 1-3 done (f788e25f, 6adc2df2, b8c73e76); Step 4 (live run, registration) left. Minimal nightly full run with a SessionStart line when red or missed; replaces `plans/closed/plan_full_suite_nightly_and_debt_check.md` (owner cut, 2026-11-27).

## Problem

Full test runs take 15-30 minutes (1,519 s and 1,934 s sequential on 2026-11-27, `c:\tmp\rs-testtrees\_results\6c573134-*` and
`5e5a814c-*`), so agents mostly run the fast suite (`TestCategory!=OutOfProcess`) and the slow OutOfProcess tests run rarely.
The owner wants the full suite to run unattended every night and to hear about it only when it is red or did not run.

The earlier design (`plan_full_suite_nightly_and_debt_check.md`, 11 steps: red-aware marker with head commit, area watch list,
debt script, retry, busy wait, skill wording) was cut by the owner on 2026-11-27 as too complex for that goal and is SUPERSEDED.
The speed work (`plan_full_suite_nightly_and_tail_shard.md`) is PARKED pending a CPU/disk measurement of test runs.

What already exists and is reused unchanged:

- `scripts/Invoke-WorktreeTests.ps1` runs a committed ref in a fresh worktree under `-WorktreeRoot`, writes
  `<WorktreeRoot>\_results\<sha8>-<yyyyMMdd-HHmmss>-<PID>\summary.json` (`$runName` at line 185, `$resultsDir` at line 275; the
  PID is the script's own process), and exits 0 (all passed), 1 (test failures) or 2 (build or setup failure) (help, line 19;
  `Fail` exits 2, line 170). Full runs are sequential across projects by default (line 177). `summary.json` has `projects[]`
  rows with `project`, `failed`, `failures[]` (`test`, `message`), `note` (set when a project wrote no TRX) and `exitCode`
  (lines 445-474).
- An unfiltered full run already rewrites `.roslynsentinel/full-test-marker.json` in the main tree (lines 479-497), so a night
  that runs also refreshes the server's existing full-suite staleness warning; a skipped night (N2) leaves the marker as it is.
  No marker change is needed.
- `scripts/roslynsentinel-scratch.ps1` (`Get-RoslynSentinelScratchRoot`, PowerShell 5.1 compatible) resolves the scratch root.
- S4U scheduled task probe (2026-11-27, recorded in the superseded plan as D12): a task under S4U, `-RunLevel Limited`, runs
  `pwsh -NoProfile -File ...` with the owner's profile, dotnet 11.0.100, git 2.56.0 and nuget reachable;
  `ROSLYNSENTINEL_SCRATCH_ROOT` is unset there, so the default scratch root applies.

## Decision

- N1: a scheduled task runs `scripts/Invoke-NightlyFullRun.ps1` daily at 06:00 under S4U (`-RunLevel Limited`, no stored
  password). It tests `master` with `Invoke-WorktreeTests.ps1 -WorktreeRoot c:\tmp\rs-testtrees -Ref <sha> -Suite full
  -NoFailFast` (sequential, today's full-run shape).
- N2: it skips the run when `master` equals the last result's `sha` and that result is `passed` or `skipped` (a skip only ever
  follows a passed run at the same sha, so the chain holds), and still writes the result (so the "missed" check sees a fresh
  date).
- N3: the result goes to `<scratch root>\nightly\last-nightly.json` (fields `date`, `sha`, `outcome`, `seconds`, `resultsDir`,
  `failedTests`); `outcome` is one of `passed`, `failed`, `incomplete`, `skipped`. A line is appended to
  `<scratch root>\nightly\nightly.log`.
- N4: a SessionStart hook prints one line when the last result is `failed` or `incomplete`, or when it is older than 26 hours;
  otherwise, and when no result exists yet, it prints nothing.
- N5: one small dot-source helper, `scripts/NightlyResult.ps1` (PowerShell 5.1 compatible), owns the result file's location,
  field names and outcome values; the nightly script and the hook both use it (checklist item 2).
- Readiness round 1 (2026-11-27, independent review, 1 blocker and 7 should-fix): all settled as implementer defaults, written
  into the steps: R1 skip after `skipped` too (N2, blocker); R2 a `note` row (project wrote no TRX) is `incomplete`, matching
  the INCOMPLETE line; R3 any throw in the nightly body, or an unparsable `summary.json`, writes `incomplete`; R4 the run/skip
  decision is a pure function `Get-NightlyAction` with unit cases, and the hook gets a positive smoke; R5 boundary shapes pinned
  (PascalCase objects in PowerShell, camelCase on disk, `date` = run start, `Get-NightlyOutcome` returns `Outcome` and
  `FailedTests`); R6 status-line formats and the 26 h threshold are constants in `NightlyResult.ps1`; R7 the hook prints only on
  `startup` and `clear` sources, like `repo-hygiene-session-start.ps1:12-18`; R8 one `$WorktreeRoot`; R9-R13 nits (cross-host
  test justification, BOM-free `[IO.File]` I/O, `note` null or empty, quoted task argument, plan link).
- Readiness round 2 (2026-11-27): all 13 fixed; 1 should-fix (cap the INCOMPLETE list like FAILED) and 3 nits (no duplicate
  exit entry for a `note` row, one date format for all lines, a skipped night leaves the marker as is) fixed in place.
- Readiness (plan-design): independent review clean at 45fe5686.
- 2026-11-27 /implement-plan: staleness check clean (no commits since 45fe5686 touch the reused scripts, settings or hook; anchors
  hold). Owner present; mode Direct (Steps 1-2 `implementer` D1, Step 3 main session, Step 4 dispatcher). Roadmap Phase 1 repointed
  to this plan.
- Left out on purpose (owner cut): retry of failing tests, waiting while another test run is busy, marker changes (head commit,
  red counts stale), the area watch list and debt script, skill and roadmap wording changes, notifications other than the hook
  line. A red night caused by a flake or an overlapping run is re-run by hand.

## Execution rules

- Script slices go to `implementer`; acceptance is a PowerShell parse check
  (`[System.Management.Automation.Language.Parser]::ParseFile` with no errors). The dispatcher runs the `*.Tests.ps1` scripts.
- PowerShell tests are plain assert scripts like `scripts/Get-TestRunTimings.Tests.ps1` (no Pester).
- `scripts/NightlyResult.ps1` and the hook must run under Windows PowerShell 5.1 (SessionStart hooks run `powershell.exe`,
  `.claude/settings.json` lines 193-219); the nightly script runs under `pwsh`.
- ASCII only; UTF-8 without BOM for the JSON file.

## Steps

### Step 1 - Result helper
- Files: `scripts/NightlyResult.ps1` (new), `scripts/NightlyResult.Tests.ps1` (new)
- Depends on: none. Pairs with: none (Steps 2 and 3 use it).
- Effect: text-neutral (no callers yet).
- Change: a dot-source-only script (pattern: `scripts/roslynsentinel-scratch.ps1`, which it dot-sources from `$PSScriptRoot`):
  - `$NightlyOutcomes` (new): `Passed = 'passed'`, `Failed = 'failed'`, `Incomplete = 'incomplete'`, `Skipped = 'skipped'`.
  - `$NightlyMissedAfterHours = 26` (new) and `$NightlyStatusFormats` (new, `-f` format strings, the single definition of the
    wording): `Failed = 'Nightly full test run FAILED at {0} ({1} UTC): {2} failing: {3}. Results: {4}'`, `Incomplete =
    'Nightly full test run INCOMPLETE at {0} ({1} UTC) - build or setup failed or a project wrote no results: {2}. Results:
    {3}'`, `Missed = 'Nightly full test run missed: last result {0} UTC ({1}).'` (R6).
  - Result object (R5): a `[pscustomobject]` with PascalCase properties `Date` (UTC `[DateTime]`, the run's start), `Sha` (40
    hex, or `$null` when the sha could not be read), `Outcome`, `Seconds` (int), `ResultsDir` (string or `$null`), `FailedTests`
    (string array, empty when none). `Write-LastNightly` takes it and `Read-LastNightly` returns it.
  - `Get-NightlyDirectory` (new): `<Get-RoslynSentinelScratchRoot>\nightly`.
  - `Write-LastNightly -Result` (new): writes `last-nightly.json` in that directory (creates it) with camelCase fields `date`
    (`yyyy-MM-ddTHH:mm:ssZ`), `sha`, `outcome`, `seconds`, `resultsDir`, `failedTests`, via a temp file moved over the target,
    with `[IO.File]::WriteAllText(..., [Text.UTF8Encoding]::new($false))` (R10: `Set-Content -Encoding utf8` writes a BOM
    under 5.1).
  - `Read-LastNightly` (new): reads with `[IO.File]::ReadAllText` (R10); `$null` when the file is missing, not valid JSON or has
    no parseable `date`; otherwise the result object. `Date` is parsed with `[DateTime]::Parse(..., [Globalization.CultureInfo]::
    InvariantCulture, 'AdjustToUniversal,AssumeUniversal')` when it is a string; PowerShell 7's `ConvertFrom-Json` may already
    return a UTC `[DateTime]`.
  - `Get-NightlyStatusLine -Result -UtcNow` (new, pure), using `$NightlyStatusFormats`; the date argument is always `Date` as
    `yyyy-MM-dd HH:mm`, and a "capped list" is the first 3 `FailedTests` entries comma-separated plus ` (+<k> more)` when there
    are more: `$null` for a `$null` result; `failed` -> `Failed` with `<sha8>`, the date, the failed count, the capped list and
    `<ResultsDir>`; `incomplete` -> `Incomplete` with `<sha8>`, the date, the capped list (or `no detail` when empty) and
    `<ResultsDir>`; otherwise, when `UtcNow - Date` is over
    `$NightlyMissedAfterHours` hours -> `Missed` with the date and outcome; else `$null`. Failed and incomplete win over missed.
    `<sha8>` is `unknown` when `Sha` is `$null`; `<ResultsDir>` is `none` when `$null`.
  - Tests (temp directory; point the helper at it by setting `ROSLYNSENTINEL_SCRATCH_ROOT` for the test process): write/read
    round trip; missing and corrupt files read as `$null`; a file written by the current host reads back the same `Date` when
    read by the other host (a child `pwsh` or `powershell.exe` process; justified because the hook runs under 5.1 and reads
    what the nightly script writes under 7, R9); `Get-NightlyStatusLine` for passed fresh (`$null`), passed 27 h old (missed
    line), skipped fresh (`$null`), failed with 1 and with 5 tests (`(+2 more)`), incomplete, `$null` result, `$null` sha.
    Assert with `-eq` against the line built from `$NightlyStatusFormats` with the expected arguments.
- Done when: the parser reports no errors for both files. Dispatcher: `pwsh -NoProfile -File scripts\NightlyResult.Tests.ps1`
  and `powershell.exe -NoProfile -File scripts\NightlyResult.Tests.ps1` exit 0. Lane: `implementer`.
- Effort: S
- Done: f788e25f (tests pass under both hosts; the dispatcher fixed one test-script bug: a `[string]` parameter turned `$null` into `''`).

### Step 2 - Nightly script and task registration
- Files: `scripts/Invoke-NightlyFullRun.ps1` (new), `scripts/Invoke-NightlyFullRun.Tests.ps1` (new),
  `scripts/Register-NightlyFullRun.ps1` (new)
- Depends on: Step 1. Pairs with: Step 3.
- Effect: text-neutral (nothing runs until the owner registers the task).
- Change:
  - `Invoke-NightlyFullRun.ps1` (pwsh), parameters `-DryRun` (prints the `master` sha and `run` or `skip`; runs and writes
    nothing) and `-WorktreeRoot` (default `c:\tmp\rs-testtrees`, the single definition used for both the run and the results
    lookup, R8). Dot-sources `NightlyResult.ps1`. Flow:
    1. `$start = [DateTime]::UtcNow`; `$sha = git -C <repo root> rev-parse master` (repo root = parent of `$PSScriptRoot`).
       `Get-NightlyAction -Sha $sha -Last (Read-LastNightly)` returns `skip` or `run`. On `skip`: write a result with outcome
       `skipped` (same `Sha`, `Seconds` 0, `ResultsDir` and `FailedTests` from the last result), append the log line, exit 0.
    2. Start `pwsh -NoProfile -File "<repo>\scripts\Invoke-WorktreeTests.ps1" -WorktreeRoot $WorktreeRoot -Ref $sha -Suite full
       -NoFailFast` with `Start-Process -PassThru -Wait`, stdout and stderr to `<nightly dir>\<yyyyMMdd>-run.log` and
       `<yyyyMMdd>-run.err.log`. Results directory: the single directory matching
       `<WorktreeRoot>\_results\<sha8>-*-<child process Id>` (`$null` when none or several).
    3. `Get-NightlyOutcome -ExitCode <child exit code> -SummaryPath <results dir>\summary.json` (or `$null` path).
    4. `Write-LastNightly` (`Date` = `$start`, `Seconds` = run duration) and append to `nightly.log`:
       `yyyy-MM-dd HH:mm | <outcome> | <sha8> | <seconds> s | failed <n>`.
    The whole body (steps 1-4) is wrapped in `try`/`catch`: a throw (including a failed `git rev-parse`) writes outcome
    `incomplete` with `FailedTests = @("error: <exception message>")`, the sha if known, and appends the log line (R3). The body
    runs only when the file is not dot-sourced (`if ($MyInvocation.InvocationName -ne '.')`, as in
    `scripts/Get-TestRunTimings.ps1:752`).
  - `Get-NightlyAction -Sha -Last` (new, pure, R4): `skip` when `Last` is not `$null`, `Last.Sha -eq Sha`, and `Last.Outcome`
    is `passed` or `skipped` (N2, R1); otherwise `run`.
  - `Get-NightlyOutcome -ExitCode -SummaryPath` (new, pure apart from reading the file) returns
    `[pscustomobject]@{ Outcome = ...; FailedTests = [string[]] }` (R5):
    - exit 0 -> `passed`, no tests;
    - exit 1 and a parsable `summary.json`: `FailedTests` = every `projects[].failures[].test`, plus `<project>: <note>` for each
      row whose `note` is not null or empty (R11: the script writes `$null`, other tools write `''`), plus `<project>: exit
      <code>` for each row with a non-zero `exitCode`, no failures and no `note`; `Outcome` = `incomplete` when any row has a `note` (a
      project wrote no results, R2), else `failed`;
    - anything else (exit 2, other codes, `$null` or missing path, unparsable `summary.json`, R3) -> `incomplete`, with
      `FailedTests` = one entry naming the reason (`exit <code>`, `no results directory`, `summary.json unreadable`).
  - `Register-NightlyFullRun.ps1`: `-Time` (default `06:00`), `-Unregister`. Registers task `RoslynSentinel-NightlyFullRun` for
    `$env:USERDOMAIN\$env:USERNAME` with `New-ScheduledTaskPrincipal -LogonType S4U -RunLevel Limited`, action `pwsh.exe` with
    `-Argument "-NoProfile -File `"<repo>\scripts\Invoke-NightlyFullRun.ps1`""` (path quoted, R12), a daily trigger at `-Time`,
    settings `New-ScheduledTaskSettingsSet -StartWhenAvailable -ExecutionTimeLimit (New-TimeSpan -Hours 3) -MultipleInstances
    IgnoreNew`. Prints what it registered and that it needs an elevated shell. The owner runs it; no step runs it. A run killed
    by the 3 h limit writes no result; the hook's missed line covers it after 26 h.
  - Tests in `scripts/Invoke-NightlyFullRun.Tests.ps1` (dot-sources the script):
    - `Get-NightlyOutcome` with sample `summary.json` files written by the test: exit 0; exit 1 with two failures in one
      project (`failed`); exit 1 with a `note` row, once with `note` = `$null` on the other rows and once with `''`
      (`incomplete`); exit 1 with a non-zero `exitCode` row and no failures (`failed`); exit 2; missing summary; unparsable
      summary.
    - `Get-NightlyAction`: `$null` last (`run`); same sha passed (`skip`); same sha skipped (`skip`); same sha failed (`run`);
      same sha incomplete (`run`); other sha passed (`run`).
    Assert on `Outcome`, `FailedTests` and the action string.
- Done when: the parser reports no errors for the three files. Dispatcher: `pwsh -NoProfile -File
  scripts\Invoke-NightlyFullRun.Tests.ps1` exits 0 and `pwsh -NoProfile -File scripts\Invoke-NightlyFullRun.ps1 -DryRun` prints a
  sha and `run` or `skip`. Lane: `implementer`.
- Effort: S
- Done: 6adc2df2 (14 test cases pass; `-DryRun` printed `master <sha>` and `run`). Implementer defaults: exit 1 with a missing
  `summary.json` is `incomplete` (`summary.json unreadable`); an exit-1 summary with no failures, notes or exit rows is `failed` with an
  empty list; the recorded-result path always exits 0 (the outcome lives in the result file), `-DryRun` exits 2 on error.

### Step 3 - SessionStart hook
- Files: `.claude/hooks/nightly-status-session-start.ps1` (new), `.claude/settings.json`
- Depends on: Step 1. Pairs with: Step 2.
- Effect: behaviour-changing: a session start prints one line when the last night was red, incomplete or missed.
- Change: the hook (PowerShell 5.1) reads the SessionStart JSON from stdin and continues only when `source` is `startup` or
  `clear` (same pattern as `.claude/hooks/repo-hygiene-session-start.ps1:12-18`; resume and compact would repeat the line, R7).
  It dot-sources `<repo>\scripts\NightlyResult.ps1` (repo = two levels above `$PSScriptRoot`),
  calls `Get-NightlyStatusLine (Read-LastNightly) ([DateTime]::UtcNow)` and writes the line to stdout when not `$null`. Any
  exception is swallowed (prints nothing, exit 0): a broken hook must not block a session. Add an entry to the `SessionStart`
  hooks array in `.claude/settings.json` after `repo-hygiene-session-start.ps1` (lines 208-219), same shape (`powershell.exe`,
  `-NoProfile -ExecutionPolicy Bypass -File ${CLAUDE_PROJECT_DIR}/.claude/hooks/nightly-status-session-start.ps1`, timeout 15).
- Done when: the parser reports no errors; `.claude/settings.json` parses with `ConvertFrom-Json`; two smokes with
  `ROSLYNSENTINEL_SCRATCH_ROOT` pointed at a temp directory and `{"source":"startup"}` piped to `powershell.exe -NoProfile -File
  .claude\hooks\nightly-status-session-start.ps1`: (a) no `last-nightly.json` -> exit 0, no output; (b) a `failed` result
  written with `Write-LastNightly` -> exit 0 and the output equals `Get-NightlyStatusLine` for that result (R4: proves the hook
  is not swallowing an exception). Lane: main session (2 files, 2 edits).
- Effort: S
- Done: b8c73e76 (parse clean, `settings.json` parses, smokes a and b pass; a `resume` source also prints nothing).

### Step 4 - Live acceptance and close
- Files: this plan
- Depends on: Steps 1-3.
- Effect: text-neutral.
- Change: the dispatcher runs `pwsh -NoProfile -File scripts\Invoke-NightlyFullRun.ps1` once by hand (about 25-30 minutes, idle
  box). Outcome classes: `passed` -> `last-nightly.json` has outcome `passed` and the hook prints nothing; a second
  `-DryRun` prints `skip`; `failed` -> the hook prints the FAILED line naming the failing tests (record them; INBOX line per
  failure); `incomplete` -> record the cause from the run log and fix before closing. Then ask the owner to run
  `Register-NightlyFullRun.ps1` from an elevated shell; the first real `nightly.log` line is checked opportunistically, not gated.
- Done when: the outcome is recorded here, Status is set and the plan closes per `docs/current/templates/README.md`
  (`Closes: plan_nightly_full_test_run`). Lane: dispatcher.
- Effort: S

## Test table

| Test | Covers | Fixture | Asserts | Pass |
| --- | --- | --- | --- | --- |
| `NightlyResult.Tests.ps1`: round trip, missing, corrupt, cross-host date | N3, N5 | temp dir | field values, `$null` | exit 0 under pwsh and powershell.exe |
| `NightlyResult.Tests.ps1`: `Get-NightlyStatusLine` (8 cases) | N4, R6 | pure | returned line (built from `$NightlyStatusFormats`) or `$null` | exit 0 |
| `Invoke-NightlyFullRun.Tests.ps1`: `Get-NightlyOutcome` (8 cases) | N3, R2, R3, R11 | sample `summary.json` | `Outcome`, `FailedTests` | exit 0 |
| `Invoke-NightlyFullRun.Tests.ps1`: `Get-NightlyAction` (6 cases) | N2, R1 | pure | action string | exit 0 |
| Step 3 hook smokes (empty, failed result) | N4, R4, R7 | temp scratch root | exit 0; no output / the status line | as stated |
| Step 4 live run | N1, N2 | real worktree run (the point is the unattended full run) | outcome, hook line, second dry run | per Step 4 outcome classes |

## Out of scope

- Everything listed as left out in the Decision (owner cut, 2026-11-27).
- Making full runs faster: `plan_full_suite_nightly_and_tail_shard.md` (PARKED pending a CPU/disk measurement of test runs).

## Risks and open decisions

- An agent session running tests at 06:00 can cause contention flakes; the night then reports red and is re-run by hand (no busy
  wait by design).
- The run rewrites the main tree's `.roslynsentinel/full-test-marker.json` (existing behaviour of `Invoke-WorktreeTests.ps1`), so
  a red night leaves a marker with `failedCount` above 0; today's server ignores that field.
- The scheduled task needs the owner to run `Register-NightlyFullRun.ps1` elevated.
