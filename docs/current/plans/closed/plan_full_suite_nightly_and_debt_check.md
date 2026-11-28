# Plan: Nightly full test run, red-aware full-suite marker with head commit, and a full-suite debt check

**Status:** SUPERSEDED 2026-11-27. Never started; the owner cut it to a minimal nightly run as too complex for the goal: plans/closed/plan_nightly_full_test_run.md.

## Problem

Full test runs take 20-30 minutes (run `6c573134-20261127-022016`: 1,519 s; `5e5a814c-20261127-014008`: 1,934 s; both 12,633
tests, 0 failures, `C:\tmp\rs-testtrees\_results`). Routine runs already skip the slow tests: `RunTest`'s default `suite: fast`
excludes `[Category("OutOfProcess")]` (`RoslynSentinel.Engines.Basic/TestSuiteFilter.cs:14-16`), as do
`scripts/Invoke-WorktreeTests.ps1 -Suite fast` and `scripts/build.ps1 -Fast`. What is missing is making that deferral safe:

1. A red full run resets the staleness marker as if green: `FullTestMarkerStore.Evaluate`
   (`RoslynSentinel.Engines.Basic/FullTestMarkerStore.cs:121`) ignores `FailedCount`.
2. The marker (`.roslynsentinel/full-test-marker.json`, record `FullTestMarker` at `FullTestMarkerStore.cs:17`) has no commit, so
   nothing can say which slow-test areas changed since the last full run, and an unattended run cannot tell whether HEAD moved.
3. Nothing runs the full suite unattended (no scheduled task or nightly script exists).
4. The task-end rule (`docs/current/references/reference_autonomous_run_conventions.md:106-109`,
   `.claude/skills/dispatch-implementer/SKILL.md:119-123`) requires a full run on a hand-kept list of touched areas, with no
   on-demand check an agent can call.

Source: the 2026-11-24 draft `plan_full_suite_nightly_and_tail_shard.md`, split by D7; that file keeps the run-speed work and
holds the full plan-design question log (Q1-Q38) with evidence. This plan is Phase 1 of `plan_roadmap_layering_and_project_splits.md`.

## Decision

Owner decisions (plan-design 2026-11-27):

- D1: keep `[Category("OutOfProcess")]` plus `suite: fast`; no `[Explicit]`.
- D2: a red full run counts as stale.
- D3: both writers record `headSha`: `scripts/Invoke-WorktreeTests.ps1` (the tested commit) and the server (`RunTest` full,
  solution scope, unfiltered; HEAD read before the run starts). Accepted edge: uncommitted changes in a server run show as debt once
  committed (false positive, safe direction). No `dirtyTree` flag.
- D4: a scheduled task runs `scripts/Invoke-NightlyFullRun.ps1` daily at 06:00; it tests committed `master` in a worktree under
  `c:\tmp\rs-testtrees`; skips when the marker's `headSha` equals `master` and the marker is green; a still-red night is reported
  by the SessionStart hook only.
- D5: the debt check is report-only, through one script `scripts/Get-FullSuiteDebt.ps1`; the SessionStart hook is a thin
  wrapper; skills call the script at task end. No server-side area logic.
- D8: the roadmap's Phase 1 Done-when is reworded (Step 8).
- D9: a project that exits non-zero with 0 TRX failures, or has no TRX, makes the run incomplete: no marker is written, matching
  the server, which treats a non-zero exit with 0 failed tests as not `RanToCompletion`
  (`RoslynSentinel.Tools.Basic/WorkspaceBuildTestImpl.cs:534-535`).
- D10: `FullSuiteStale` gains `IReadOnlyList<FullSuiteStaleCause> Causes` (new enum `NoFullRun`, `TooOld`, `TooManyFastRuns`,
  `LastRunFailed`); `Reason` stays, built from the causes through shared format constants; tests assert `Causes`.
- D11: one dot-source helper `scripts/FullTestMarker.ps1` reads and writes the marker and the nightly result file.
- D12: the task runs under an S4U principal (runs whether logged on or not, no stored password), `-RunLevel Limited`. Probe
  2026-11-27: a throwaway S4U task ran `pwsh` with the owner's profile, dotnet 11.0.100 and git on PATH, nuget.org reachable,
  `ROSLYNSENTINEL_SCRATCH_ROOT` unset (default scratch root).
- D13: watch list: area `git` as project directories plus three tool-side files; `build`, `runtest`, `process`, `scripts` as file
  globs; no test-file list: a changed test `.cs` containing `[Category("OutOfProcess")]` counts as area `outofprocess-tests`.
- D22: live acceptance is one unfiltered full worktree run in the closing step (Step 9).
- D25 and D38: the marker's JSON field names and file location (`.roslynsentinel/full-test-marker.json`) are defined in C#
  (`FullTestMarker`, `FullTestMarkerStore.FileName`, `PathFor`) and in `scripts/FullTestMarker.ps1`, each with a comment pointing
  to the other; `scripts/FullTestMarker.Tests.ps1` reads `FullTestMarkerStore.cs` as text and asserts they match.
- D26: the nightly run defers while another test run is active (recheck every 30 minutes, up to 4 times, then outcome `busy`).
- D27 and D35: on a red night, re-run the failing classes once, in one invocation (`-Projects <all failing> -Filter <all failing
  classes>`; projects run one at a time inside it).
- D28: a pass on retry makes the marker green; the flaky classes are recorded and the hook names them.
- D29: the nightly run keeps today's full-run shape (projects one at a time, NUnit parallel inside each).
- D30: a headless Claude Code session that investigates a still-red night is a separate follow-up plan; this plan leaves an
  unused `-OnStillRed <script>` parameter and the D31 file as the hook-in point (owner-approved knob without a user in this plan).
- D31: the nightly run writes `<scratch root>\nightly\last-nightly.json`; `nightly.log` is the human log; the hook reads only typed
  fields.
- D32: one PowerShell definition of the category name, `$OutOfProcessCategory` in `scripts/FullTestMarker.ps1`. Amended
  2026-11-27 (owner, after approval): the D38 guard also covers it: `scripts/FullTestMarker.Tests.ps1` reads
  `RoslynSentinel.Engines.Basic/TestSuiteFilter.cs` as text and asserts the `OutOfProcessCategory` constant's value equals
  `$OutOfProcessCategory`.
- D34: a nightly `skip` rewrites the marker's `LastFullRunUtc` to now (counts and `headSha` kept): the field means "last time a
  full run's green result was confirmed current". So the hook's 26-hour age cause means "the nightly run missed", and the server's
  2-day `TooOld` does not fire on quiet days.
- D36: the debt script reports watch-list entries that match no tracked file (cause `watch-list-stale`, counts as debt).
- D37: script slices go to `implementer` with one parse check as acceptance; the dispatcher runs the `*.Tests.ps1` scripts.
  Step 8 is done by the main session, owner-approved over the CLAUDE.md main-session cap.
- Q33 (implementer default): one rule `FullTestMarkerStore.ResetsMarker` says which runs reset the marker; `RunTest` reads HEAD
  only for such a run, so fast runs never open the repo.

- Readiness (plan-design, 2026-11-27): independent review clean at 512d7738 (explore-tier2 Sonnet, three rounds: round 1
  1 blocker and 15 should-fix, round 2 seven nits, round 3 one nit; all fixed).
- Amendment (owner, 2026-11-27, after readiness): D32 gains the text-read guard (Decision D32, Step 1 tests, test table row for
  `FullTestMarker.Tests.ps1`). Test-only, one bullet in Step 1; no step, file or lane changes.

Design review (checklist items 1-7) passed with the D25/D38 and D32 notes; the simplest alternative (marker `headSha` plus a
hook, no nightly run) was rejected because a report-only check (D5) needs an unattended backstop.

## Execution rules

- One `Build`/`RunTest`/`dotnet test` at a time in the main tree (CLAUDE.md). Implementer acceptance: a clean `Build` (C#
  slices) or one PowerShell parse check over the slice's files (script slices). The dispatcher runs `RunTest` and every
  `*.Tests.ps1` (`pwsh -NoProfile -File <script>`, exit 0 = pass).
- C# through the MCP edit tools; non-C# with Edit/Write.
- **PowerShell 5.1 compatibility** for `scripts/FullTestMarker.ps1`, `scripts/Get-FullSuiteDebt.ps1` and the hook: SessionStart
  hooks run under `powershell.exe` (`.claude/settings.json`, repo-hygiene entry at lines 208-219). No `-AsHashtable`, no `??`, no
  ternary. `ConvertFrom-Json` in PowerShell 7 turns ISO date strings into `[DateTime]` while 5.1 leaves strings: the helper parses
  `lastFullRunUtc` from either. Files are written as UTF-8 without BOM
  (`[IO.File]::WriteAllText($path, $text, (New-Object Text.UTF8Encoding $false))`).
- PowerShell objects use PascalCase properties; the JSON on disk is camelCase; a missing numeric JSON field reads as 0 (the C#
  default).
- PowerShell tests are plain assert scripts like `scripts/Get-TestRunTimings.Tests.ps1` (no Pester).
- Test baseline is 0 failures (`docs/current/references/reference_known_failing_tests.md`).
- Server source changes in Steps 2a-2b: commit them, restart this session's server and check `lastLaunch.servedSha` in
  `McpServerStatus` before Step 4 (CLAUDE.md, Verification).
- Re-read `scripts/Invoke-WorktreeTests.ps1`, `scripts/Get-TestRunTimings.ps1` and the skill/reference files before Steps 4 and 8;
  another session may edit them.

## Steps

Slice map (two lanes at most; each pair touches disjoint files):

| Round | Lane 1 | Lane 2 |
| --- | --- | --- |
| 1 | Step 1 (PowerShell helper, new files) | Step 2a (`FullTestMarkerStore.cs`, test constructor fix-ups) |
| 2 | Step 5 (debt script, new files) | Step 2b (`WorkspaceBuildTestImpl.cs`) |
| 3 | Step 6a (nightly pure functions, new files) | Step 3 (C# tests) |
| 4 | Step 4 (worktree script, `build.ps1`, timings script; after the restart above) | Step 6b (nightly flow, registration) |
| 5 | Step 7 (hook), then Step 8 (wording), then Step 9 (close) | - |

Text-neutral first: Steps 1, 5, 6a, 6b add files nothing calls yet. Behaviour-changing: Steps 2a-2b (wire field, red warns,
wording), Step 4 (marker gains `headSha`, D9), Step 7 (session-start output), Step 8 (agent rules).

### Step 1 - PowerShell marker helper
- Files: `scripts/FullTestMarker.ps1` (new), `scripts/FullTestMarker.Tests.ps1` (new)
- Depends on: none. Pairs with: Step 2a.
- Effect: text-neutral (no callers yet).
- Change: a dot-source-only script (pattern: `scripts/roslynsentinel-scratch.ps1`), PowerShell 5.1 compatible, defining:
  - Constants: `$OutOfProcessCategory = 'OutOfProcess'` (comment: keep in step with `TestSuiteFilter.OutOfProcessCategory`,
    `RoslynSentinel.Engines.Basic/TestSuiteFilter.cs:14`; D32); `$FailedTrxOutcomes = @('Failed', 'Error', 'Timeout', 'Aborted')`
    (the outcomes `Invoke-WorktreeTests.ps1` counts as failed, line 458); `$NightlyOutcomes` with `Ran = 'ran'`, `Skip = 'skip'`,
    `Busy = 'busy'`, `Incomplete = 'incomplete'`; `$DefaultWorktreeRoot = 'c:\tmp\rs-testtrees'`.
  - `Get-FullTestMarkerPath -SolutionRoot` returning `<root>\.roslynsentinel\full-test-marker.json` (comment: same location as
    `FullTestMarkerStore.FileName`/`PathFor`; D38).
  - `Read-FullTestMarker -SolutionRoot`: `$null` when the file is missing, unreadable, corrupt, has no valid `lastFullRunUtc`, or a
    negative count (the rules of `FullTestMarkerStore.TryRead`, `FullTestMarkerStore.cs:60`); otherwise an object with
    `LastFullRunUtc` (UTC `[DateTime]`), `TotalCount`, `FailedCount`, `FastRunsSince`, `HeadSha` (`$null` when absent).
  - `Write-FullTestMarker -SolutionRoot -Marker`: camelCase JSON (`lastFullRunUtc` as `yyyy-MM-ddTHH:mm:ssZ`, `totalCount`,
    `failedCount`, `fastRunsSince`, `headSha`), written to a temp file in the same folder then moved over the target; creates
    `.roslynsentinel`. Comment above the field names: the C# record `FullTestMarker` in camelCase; keep both in step (D25).
  - Pure marker builders: `New-FullRunMarker -UtcNow -TotalCount -FailedCount -HeadSha` (`FastRunsSince` 0);
    `Step-FastRunMarker -Marker` (copy with `FastRunsSince + 1`, `HeadSha` kept); `New-ConfirmedMarker -Marker -UtcNow` (D34: copy
    with `LastFullRunUtc = UtcNow`); `New-RetriedMarker -Marker -FirstPassSha` (D28: copy with `FailedCount` 0; returns `$null`
    when `Marker.HeadSha` is not `FirstPassSha`, so a marker another run rewrote meanwhile is left alone).
  - `Get-RunCompleteness -ProjectRows`: rows carry `exitCode`, `failed`, `note` (the `summary.json` project shape); returns an
    object with `Complete` (bool) and `Reason` (`no-results`, `non-test-failure`, or `$null`). Incomplete when there are no rows,
    any row has a non-empty `note` (`$null` and `''` count as empty; no TRX, `Invoke-WorktreeTests.ps1:446-448`), or any row has a non-zero `exitCode` with `failed` 0 (D9).
  - `Test-FullRunMarkerWritable -Suite -Filter -Projects -ProjectRows`: true only for no filter, no project list and a complete
    run (`Get-RunCompleteness`); `Suite` selects the full-run write or the fast-run rewrite rule (both need a complete run).
  - `Get-NightlyDirectory` (`<scratch root>\nightly`, via `Get-RoslynSentinelScratchRoot`; the helper dot-sources
    `scripts/roslynsentinel-scratch.ps1` from `$PSScriptRoot`), `Read-LastNightly`, `Write-LastNightly -Result` (object properties
    `Date`, `Sha`, `Outcome`, `Seconds`, `FailedClasses`, `FlakyClasses`): JSON fields `date` (ISO 8601 UTC), `sha` (40 hex), `outcome` (one of
    `$NightlyOutcomes`), `seconds`, `failedClasses`, `flakyClasses` (arrays of namespace-qualified class names) (D31).
  - Tests (`FullTestMarker.Tests.ps1`, temp directory): marker round trip with and without `headSha`; corrupt and negative-count
    files read as `$null`; a PowerShell-7-written marker reads back the same `LastFullRunUtc`; `Step-FastRunMarker`,
    `New-ConfirmedMarker`, `New-RetriedMarker` (matching and mismatched SHA); `Get-RunCompleteness` for complete, a note row, a
    non-test failure and no rows; `Test-FullRunMarkerWritable` for a clean full run (true), filtered, project list and incomplete
    (false); `last-nightly.json` round trip; the D25/D38 guard: read `RoslynSentinel.Engines.Basic/FullTestMarkerStore.cs` as text
    and assert that the `FileName` constant, the `".roslynsentinel"` folder in `PathFor` and the camelCased parameter names of
    `public record FullTestMarker(...)` match the helper; the D32 guard: read `RoslynSentinel.Engines.Basic/TestSuiteFilter.cs`
    as text and assert the string value of `const string OutOfProcessCategory` equals `$OutOfProcessCategory`.
- Done when: `[System.Management.Automation.Language.Parser]::ParseFile` reports no errors for both files. Dispatcher:
  `pwsh -NoProfile -File scripts\FullTestMarker.Tests.ps1` exits 0. Lane: `implementer`.
- Effort: S

### Step 2a - C# marker: head SHA, typed causes, red counts stale, one reset rule
- Files: `RoslynSentinel.Engines.Basic/FullTestMarkerStore.cs`, `RoslynSentinel.Tests.Basic/FullTestMarkerStoreTests.cs`
  (constructor fix-ups only)
- Depends on: none. Pairs with: Step 1.
- Effect: behaviour-changing: a fast `RunTest` result's `fullSuiteStale` gains a `causes` array; a fast `RunTest` and a `Git`
  commit warn when the last full run had failures; the warning line now reads "Full suite stale (<reason>). Run ... before
  committing.".
- Change (`FullTestMarkerStore.cs`, 217 lines):
  - `public record FullTestMarker(DateTime LastFullRunUtc, int TotalCount, int FailedCount, int FastRunsSince)` (line 17) gains a
    trailing `string? HeadSha = null`. Doc comment (lines 8-16): the format example gains `"headSha":"<40 hex>"`; `lastFullRunUtc`
    is "the last time a full run's green result was confirmed current (a full run, or a nightly skip that found `master` unchanged)";
    replace "There is no head SHA field: ..." (line 15) with "`headSha` is the commit the full run tested (null when unknown). The
    field names and file location are mirrored in `scripts/FullTestMarker.ps1`; keep both in step (D25)." Existing positional
    constructions (line 92, tests) keep compiling.
  - New enum `FullSuiteStaleCause { NoFullRun, TooOld, TooManyFastRuns, LastRunFailed }` beside the record, one-line doc per member.
    Not a DTO (DTO-EXEMPT: enum).
  - `public record FullSuiteStale(string Reason, DateTime? LastFullRunUtc, int FastRunsSince)` (line 24) gains a trailing
    `IReadOnlyList<FullSuiteStaleCause> Causes` (required); update its doc comment. dto-conventions rule 1 (extend an existing DTO
    by one property); wire impact: one added `causes` field on `fullSuiteStale`.
  - Public constants in `FullTestMarkerStore` (one definition for code and tests): `NoFullRunReason = "no full run recorded"`,
    `TooOldReasonFormat = "last full run {0} ago (limit {1} days)"`, `TooManyFastRunsReasonFormat = "{0} fast runs since the last
    full run (limit {1})"`, `LastRunFailedReasonFormat = "last full run had {0} failed tests"`, `DescribeNoRunFormat = "No full
    suite run recorded. Run {0} before committing."`, `DescribeStaleFormat = "Full suite stale ({0}). Run {1} before committing."`.
    The literal at line 125 and the interpolated strings at lines 132, 137, 164 and 167 use them
    (`string.Format(CultureInfo.InvariantCulture, ...)`).
  - New `public static bool ResetsMarker(TestSuite suite, ToolScope scope, bool filtered) => suite == TestSuite.full && scope ==
    ToolScope.solution && !filtered;` with a doc line; `NextMarker`'s full branch (line 92) uses it.
  - `Evaluate` (line 121): build a `List<FullSuiteStaleCause>` beside the reasons, in the order `TooOld`, `TooManyFastRuns`,
    `LastRunFailed` (new: when `marker.FailedCount > 0`, with `LastRunFailedReasonFormat`); the missing-marker branch returns cause
    `NoFullRun`. Both constructions (lines 125, 140) pass the list.
  - `Describe` (line 159): the stale branch uses `DescribeStaleFormat` with `stale.Reason`; update the doc comment example.
  - `NextMarker` (line 82) gains a trailing `string? headSha = null`; the full branch passes it to the constructor; the fast
    branch's `with` keeps `HeadSha`. Its 8 call sites (1 production, 7 tests) keep compiling.
  - `RecordRun` (line 99, `public static void RecordRun(string? solutionRoot, TestSuite suite, ToolScope scope, TestRunResult run,
    DateTime? utcNow = null, string? filter = null)`) gains a trailing `string? headSha = null`, passed to `NextMarker`.
- Change (`FullTestMarkerStoreTests.cs`): only the three `new FullSuiteStale(...)` constructions (lines 223, 287, 297) gain a
  causes argument so the project compiles; assertion rewrites are Step 3.
- Done when: `Build` 0 errors. Lane: `implementer` (2 files, about 11 edits).
- Effort: S

### Step 2b - RunTest records the head commit
- Files: `RoslynSentinel.Tools.Basic/WorkspaceBuildTestImpl.cs`
- Depends on: Step 2a. Pairs with: Step 5.
- Effect: behaviour-changing: a full, solution-scope, unfiltered `RunTest` writes `headSha` into the marker.
- Change, inside `RunTest` (591-line file; the run starts at the `_testRunEngine.RunAsync` call, about line 519): move
  `var solutionRoot = _workspaceManager.GetSolutionRoot();` (about line 522) above `RunAsync`, and also above it read
  `var headSha = worktreeRoot is null && FullTestMarkerStore.ResetsMarker(suite, scope, !string.IsNullOrWhiteSpace(filter)) &&
  !string.IsNullOrEmpty(solutionRoot) ? RepoWorktrees.ReadHeadCommit(solutionRoot) : null;` with a comment: read before the run so
  a commit made during it is not recorded as tested (D3). Pass `headSha` to `FullTestMarkerStore.RecordRun` (line 524).
  `RepoWorktrees.ReadHeadCommit` (`RoslynSentinel.Git/RepoWorktrees.cs:133`, `public static string? ReadHeadCommit(string
  worktreeRoot)`) never throws and returns null when git cannot answer; this file already calls it (lines 457, 529).
- Done when: `Build` 0 errors. Lane: `implementer`.
- Effort: S

### Step 3 - Tests for Step 2a
- Files: `RoslynSentinel.Tests.Basic/FullTestMarkerStoreTests.cs`
- Depends on: Step 2a. Pairs with: Step 6a.
- Effect: text-neutral.
- Change (existing fixture: Guid temp root, in-process, no fakes needed):
  - New: `Evaluate_RecentRedMarker_HasLastRunFailedCause` (`Causes` is exactly `[LastRunFailed]`, `LastFullRunUtc` set);
    `Evaluate_OldRedMarkerWithManyFastRuns_ListsAllCausesInOrder` (`Causes` = `[TooOld, TooManyFastRuns, LastRunFailed]`, `Reason`
    = the three formatted constants joined by `"; "`); `ResetsMarker_OnlyFullSolutionUnfilteredRuns` (`[TestCase]` rows over suite,
    scope, filtered); `TryRead_MarkerWithHeadSha_ReadsIt`; `TryRead_MarkerWithoutHeadSha_HeadShaIsNull`;
    `NextMarker_FullSolutionRun_RecordsHeadSha`; `NextMarker_FastRun_KeepsHeadSha`; `RecordRun_FullSolutionRun_WritesHeadSha`
    (marker on disk); `Describe_RedRun_UsesStaleFormatWithReason` (equals `DescribeStaleFormat` applied to the formatted reason
    and the `ToolCallHint` call).
  - Rewrite to structured asserts: line 191 and `Evaluate_MissingMarker_IsStaleWithNoFullRunRecorded` (lines 218-223; record
    equality no longer holds with a list property: assert `Causes`, `LastFullRunUtc`, `FastRunsSince`);
    `Evaluate_OlderThanMaxAge_IsStaleByDays` (246) and `Evaluate_MoreFastRunsThanTheLimit_IsStaleByCount` (257) assert `Causes`;
    `EvaluateForRoot_UsesTheConfiguredThresholds` (269, 272) asserts `Causes`.
  - Rewrite against the format constants: `Describe_StaleRun_NamesAgeFastRunsAndTheFullRunCall` (291),
    `Describe_NoRecordedRun_SaysSo` (299), `StaleFindings_StaleSuite_YieldsOneWarningFindingAndLeavesTheMarkerAlone` (313),
    `StaleFindings_MissingMarker_WarnsThatNoFullRunIsRecorded` (332).
  - The existing `Evaluate_RecentRunWithFewFastRuns_IsFresh` already covers a recent green marker; no duplicate.
- Done when: `Build` 0 errors. Dispatcher: `RunTest(scope: project, scopeName: RoslynSentinel.Tests.Basic,
  filter: "FullyQualifiedName~FullTestMarkerStoreTests")` green. Lane: `test-writer`.
- Effort: S

### Step 4 - Worktree script, build.ps1 and the timings script use the helper
- Files: `scripts/Invoke-WorktreeTests.ps1`, `scripts/build.ps1`, `scripts/Get-TestRunTimings.ps1`
- Depends on: Step 1 (helper); Steps 2a-2b committed and served by this session's server (until then the server's fast-run rewrite
  drops unknown JSON properties, `FullTestMarkerStore.cs:61`, `:192`, and would lose `headSha`). Pairs with: Step 6b.
- Effect: behaviour-changing: the full-run marker gains `headSha`; a run with a non-test failure (non-zero exit and 0 TRX failures)
  neither writes a full-run marker nor counts as a fast run (D9).
- Change:
  - Dot-source `scripts/FullTestMarker.ps1` near the top of each script.
  - `Invoke-WorktreeTests.ps1` line 147 (`'TestCategory!=OutOfProcess'` twice), `build.ps1` line 83
    (`$fastFilter = 'TestCategory!=OutOfProcess'`) and `Get-TestRunTimings.ps1` lines 76, 356 and 546 build their values from
    `$OutOfProcessCategory` (D32). `Invoke-WorktreeTests.ps1` line 458 (the skipped-outcome XPath that excludes `Passed` and the
    four failed outcomes) builds its exclusion from `$FailedTrxOutcomes`; line 455 stays on the TRX Counters attributes
    (`failed`, `error`, `timeout`, `aborted`), and the ratchet stage (lines 369-370) and line 465 are unchanged.
  - `Invoke-WorktreeTests.ps1` marker block (lines 480-523: full branch 485-497, fast rewrite 498-523): replace the inline JSON
    with the helper. The full branch, when `Test-FullRunMarkerWritable` is true for `$summary.projects`, writes `New-FullRunMarker`
    with `HeadSha = $head` (line 245, read before the run) to `$mainRoot`; the fast branch, under the same completeness rule, reads,
    applies `Step-FastRunMarker` and writes (no marker: nothing written, as today). Help text (lines 53-62) mentions `headSha` and
    the completeness rule.
- Done when: the parser reports no errors for the three scripts. Dispatcher: `pwsh -NoProfile -File scripts\FullTestMarker.Tests.ps1`
  and `pwsh -NoProfile -File scripts\Get-TestRunTimings.Tests.ps1` exit 0. Lane: `implementer`.
- Effort: S

### Step 5 - Full-suite debt script and watch list
- Files: `scripts/Get-FullSuiteDebt.ps1` (new), `scripts/expensive-test-watch-list.json` (new),
  `scripts/Get-FullSuiteDebt.Tests.ps1` (new)
- Depends on: Step 1. Pairs with: Step 2b.
- Effect: text-neutral (no callers until Steps 7-8).
- Change:
  - `expensive-test-watch-list.json`: `{ "_comment": "...", "areas": [ { "name": "<area>", "paths": [ ... ] } ] }`; an entry ending
    in `/` is a directory prefix, any other entry a `-like` pattern on the repo-relative path with forward slashes. Areas (D13):
    `git`: `RoslynSentinel.Git/`, `RoslynSentinel.Tests.Git/`, `RoslynSentinel.Tools.Basic/GitTools.cs`,
    `RoslynSentinel.Tools.Basic/GitRemoteTools.cs`, `RoslynSentinel.Tools.Basic/GitStatusRenameHints.cs`;
    `runtest`: `RoslynSentinel.Engines.Basic/TestRun*.cs`, `RoslynSentinel.Engines.Basic/TestSuiteFilter.cs`,
    `RoslynSentinel.Engines.Basic/TestProjectDetector.cs`, `RoslynSentinel.Tools.Basic/WorkspaceBuildTestImpl.cs`;
    `build`: `RoslynSentinel.Engines.Basic/BuildEngine.cs`, `RoslynSentinel.Engines.Basic/BuildIsolation.cs`,
    `RoslynSentinel.Common/BuildResult*.cs`;
    `process`: `RoslynSentinel.Common/ChildProcessCapture.cs`, `RoslynSentinel.Engines.Basic/KillOnCloseJob.cs`,
    `RoslynSentinel.Engines.Basic/TestInstanceLauncher*.cs`, `RoslynSentinel.Engines.Basic/DocTrailerEngine.cs`;
    `scripts`: `scripts/Invoke-WorktreeTests.ps1`, `scripts/FullTestMarker.ps1`.
    `_comment`: the build, runtest and process globs become `RoslynSentinel.Engines.BuildTest/` when the roadmap's Phase 4 creates
    that project; OutOfProcess test files are found by content, not listed.
  - `Get-FullSuiteDebt.ps1` (PowerShell 5.1 compatible; dot-sources the helper; a dot-source guard like
    `scripts/Get-TestRunTimings.ps1:752` so tests and the hook can load its functions). Script-level constants: the cause names
    `$DebtCauses` (`missing`, `red`, `nightly-missed`, `no-commit-recorded`, `commit-unknown`, `areas-changed`,
    `watch-list-stale`) and `$NightlyMissedHours = 26`.
    - Pure `Get-FullSuiteDebtCauses -Marker -ChangedFiles -CommitKnown -TrackedFiles -WatchList -UtcNow -LastNightly -GetFileText`
      returns an object: `HasDebt`, `Causes`, `Areas`, `StaleWatchEntries`, `MarkerSha`, `AgeHours`, `FailingClasses` and
      `FlakyClasses` (from `LastNightly` when its `Sha` equals `MarkerSha`). Rules: no marker -> `missing`; `FailedCount > 0` ->
      `red`; marker age over `$NightlyMissedHours` -> `nightly-missed` (D34 makes every ran or skip night refresh the age); no
      `HeadSha` -> `no-commit-recorded`, no area check; `CommitKnown` false -> `commit-unknown`, no area check; otherwise each changed
      path is matched against the watch list (`areas-changed`), plus area `outofprocess-tests` for a changed `.cs` under a
      directory whose name starts with `RoslynSentinel.Tests` whose text (`GetFileText`) contains `Category("$OutOfProcessCategory")`
      (deleted files: path match only); each watch-list entry matching none of `TrackedFiles` -> `watch-list-stale` (D36). Flaky
      classes alone are not debt.
    - Pure `Format-FullSuiteDebt -Debt` returns the text lines: with debt, one line naming the causes, the areas and the marker,
      for example `Full-suite debt: areas git, runtest changed since 3f2a9c1d (last full run confirmed 14 h ago, green).`, plus
      `Still failing after retry: <classes>.` and `Watch-list entries matching no file: <entries>.` when present; without debt,
      `No full-suite debt (last full run confirmed <n> h ago at <sha8>, green).`; and `Last nightly run: flaky classes <classes>.`
      whenever flaky classes exist.
    - Wrapper `Get-FullSuiteDebt -RepoRoot` (default: the repo above the script): reads the marker (`Read-FullTestMarker`) and
      `Read-LastNightly`; `CommitKnown` from `git -C <root> cat-file -e <headSha>^{commit}`; changed files = union of
      `git -C <root> diff --name-only --no-renames <headSha> HEAD`, `git -C <root> diff --name-only --no-renames HEAD` and
      `git -C <root> ls-files --others --exclude-standard`; `TrackedFiles` from `git -C <root> ls-files`. Run as a script (script
      parameter `-RepoRoot`, passed to the wrapper), it prints `Format-FullSuiteDebt` and exits 0 (no debt), 1 (debt) or 2
      (internal error, message on stderr).
  - Tests (`Get-FullSuiteDebt.Tests.ps1`, canned inputs, no git): missing marker; red marker; 27-hour-old marker (`nightly-missed`);
    no `headSha`; unknown commit; a change under `RoslynSentinel.Git/` (area `git` by directory); a change to
    `RoslynSentinel.Common/BuildResult.cs` (area `build` by glob); a test file whose text has the category (area
    `outofprocess-tests`) and one without (no area); a watch entry matching no tracked file (`watch-list-stale`); clean recent green
    marker, no changes (no debt); flaky classes alone (no debt, listed); `Format-FullSuiteDebt` for the debt, no-debt and
    still-failing cases (lines built from the same values, not literals typed twice).
- Done when: the parser reports no errors for the two scripts and `Get-Content scripts\expensive-test-watch-list.json -Raw |
  ConvertFrom-Json` succeeds. Dispatcher: `pwsh -NoProfile -File scripts\Get-FullSuiteDebt.Tests.ps1` exits 0;
  `powershell.exe -NoProfile -File scripts\Get-FullSuiteDebt.ps1` runs against the current tree and exits 0 or 1. Lane: `implementer`.
- Effort: S

### Step 6a - Nightly run: pure functions and tests
- Files: `scripts/Invoke-NightlyFullRun.ps1` (new: functions and a dot-source guard only), `scripts/Invoke-NightlyFullRun.Tests.ps1` (new)
- Depends on: Step 1. Pairs with: Step 3.
- Effect: text-neutral.
- Change (runs under `pwsh`; dot-sources `scripts/FullTestMarker.ps1`):
  - `Get-NightlyDecision -Marker -MasterSha -BusyProcessCount`: `Skip` when the marker's `HeadSha` equals `MasterSha` and
    `FailedCount` is 0 (skip wins over busy); else `Busy` when `BusyProcessCount > 0`; else `Ran` (values from `$NightlyOutcomes`).
  - `Wait-NotBusy -CountBusy <scriptblock> -Sleep <scriptblock> -Rechecks 4`: calls `CountBusy`; while it is above 0, calls
    `Sleep` and rechecks, up to `Rechecks` times; returns the last count (the flow passes `Start-Sleep -Seconds 1800`; tests pass a
    no-op).
  - `Get-RetryFilter -ClassNames`: `FullyQualifiedName~<Ns.Class>.` joined by `|`. A class name containing `(` or `<`
    (parameterised or generic fixture) is cut before that character and gets no trailing dot (`FullyQualifiedName~<Ns.Fixture>`,
    which may also re-run a sibling class sharing the prefix); vstest filter characters `\ ( ) & | = ! ~` are escaped with `\`;
    nested `Outer+Inner` names pass through.
  - `Merge-NightlyRetry -FirstPassFailedClasses -RetryRows` (`RetryRows`: class name and outcome per test): `FailedClasses` and
    `FlakyClasses`; a class is flaky only when the retry ran at least one of its tests and none had an outcome in
    `$FailedTrxOutcomes`; a class with no retry rows stays failing.
  - `Get-FailedClasses -TrxRows`: distinct class names of rows whose outcome is in `$FailedTrxOutcomes`.
  - `New-NightlyResult -Outcome -Sha -Seconds -FailedClasses -FlakyClasses -Previous`: the result object (properties `Date`,
    `Sha`, `Outcome`, `Seconds`, `FailedClasses`, `FlakyClasses`); for `Busy` and `Incomplete`, and for `Skip` when `Previous.Sha`
    equals `Sha`, it carries `FailedClasses` and `FlakyClasses` forward from `Previous`.
  - Tests: decision `Skip` (same SHA, green, even when busy), `Ran` (SHA moved; same SHA but red; no marker; no `HeadSha`),
    `Busy`; `Wait-NotBusy` returning 0 after two busy checks and the last count after all rechecks; `Get-RetryFilter` for one and
    two classes, a parameterised name, a generic name and a nested name, each checked with `-like` against a realistic fully
    qualified test name (for example `Ns.Fixture<Int32>.Test`, `Ns.Fixture("a").Test`) to show the filter would match it;
    `Merge-NightlyRetry` all flaky, all failing, mixed, no retry rows; `Get-FailedClasses` over each outcome in
    `$FailedTrxOutcomes` plus `Passed` and `NotExecuted`; `New-NightlyResult` carrying lists forward on `Busy`, `Incomplete` and a
    same-SHA `Skip`, and not on a `Skip` for a new SHA.
- Done when: the parser reports no errors for both files. Dispatcher: `pwsh -NoProfile -File scripts\Invoke-NightlyFullRun.Tests.ps1`
  exits 0. Lane: `implementer`.
- Effort: S

### Step 6b - Nightly run: flow and task registration
- Files: `scripts/Invoke-NightlyFullRun.ps1`, `scripts/Register-NightlyFullRun.ps1` (new)
- Depends on: Step 6a. Real `headSha` values need Step 4 at run time, not to build. Pairs with: Step 4.
- Effect: text-neutral (nothing runs until the owner registers the task).
- Change:
  - `Invoke-NightlyFullRun.ps1`, under the guard: parameters `-DryRun` (prints the decision and the busy count once; never sleeps,
    runs nothing, writes nothing) and `-OnStillRed <script path>` (D30). Flow:
    1. `git -C <repo> rev-parse master`; read the marker. If `Get-NightlyDecision` (busy count 0) is `Skip`: write
       `New-ConfirmedMarker` (D34; re-read the marker first and write only if its `HeadSha` is still `master`), the result and the
       log line; exit 0.
    2. Count busy processes (`Get-CimInstance Win32_Process`: `testhost.exe`, or `dotnet.exe` whose command line contains a ` test`
       argument or `vstest`); `Wait-NotBusy`; still busy -> outcome `Busy`, write, exit 0.
    3. Run `pwsh -NoProfile -File scripts\Invoke-WorktreeTests.ps1 -WorktreeRoot $DefaultWorktreeRoot -Ref <sha> -Suite full
       -NoFailFast` through `Start-Process -PassThru -Wait`, stdout and stderr to `<nightly dir>\<yyyyMMdd>-run.log` and
       `-run.err.log`. Results directory: the one matching `$DefaultWorktreeRoot\_results\<sha8>-*-<child PID>`
       (`Invoke-WorktreeTests.ps1:185`, `:275`). Exit code 2, no directory or no `summary.json` -> outcome `Incomplete`.
       Otherwise `Get-RunCompleteness` over `summary.json`'s `projects`; incomplete -> `Incomplete` (no marker change, D9).
    4. Failed classes from each project's TRX: call `Read-TrxFile` (`scripts/Get-TestRunTimings.ps1:134`) inside a script block that
       dot-sources `Get-TestRunTimings.ps1`, so its script parameters do not leak into this scope
       (`scripts/Get-TestRunTimings.Tests.ps1:8-9`); `Get-FailedClasses`.
    5. With failed classes: one retry `Invoke-WorktreeTests.ps1 -WorktreeRoot $DefaultWorktreeRoot -Ref <sha> -Projects <failing
       projects> -Filter (Get-RetryFilter ...) -Suite full` (D35), output to `<yyyyMMdd>-retry.log`; read its TRX rows;
       `Merge-NightlyRetry`. Nothing still failing -> `New-RetriedMarker -FirstPassSha <sha>` and write it when not `$null` (D28).
       Still failing and `-OnStillRed` set -> invoke it with the `last-nightly.json` path.
    6. Always: `Write-LastNightly (New-NightlyResult ...)` and append to `<nightly dir>\nightly.log`:
       `yyyy-MM-dd HH:mm | <outcome> | <sha8> | <seconds> s | failed <n> | flaky <n>: <classes>`.
  - `Register-NightlyFullRun.ps1`: `-Time` (default `06:00`), `-Unregister`. Registers task `RoslynSentinel-NightlyFullRun` for
    `$env:USERDOMAIN\$env:USERNAME` with `New-ScheduledTaskPrincipal -LogonType S4U -RunLevel Limited`, action
    `pwsh.exe -NoProfile -File <repo>\scripts\Invoke-NightlyFullRun.ps1`, daily trigger at `-Time`, settings
    `New-ScheduledTaskSettingsSet -StartWhenAvailable -ExecutionTimeLimit (New-TimeSpan -Hours 4) -MultipleInstances IgnoreNew`.
    Prints what it registered and that it needs an elevated shell. The owner runs it; no step runs it.
- Done when: the parser reports no errors for both files. Dispatcher: `pwsh -NoProfile -File scripts\Invoke-NightlyFullRun.Tests.ps1`
  exits 0 and `pwsh -NoProfile -File scripts\Invoke-NightlyFullRun.ps1 -DryRun` prints a decision. Lane: `implementer`.
- Effort: S

### Step 7 - SessionStart debt hook
- Files: `.claude/hooks/full-suite-debt-session-start.ps1` (new), `.claude/settings.json`
- Depends on: Step 5.
- Effect: behaviour-changing for agents: a session start prints the debt lines when there is debt or the last nightly run had
  flaky classes; silent otherwise.
- Change: the hook follows `.claude/hooks/repo-hygiene-session-start.ps1`: reads the payload from stdin, exits 0 unless `source` is
  `startup` or `clear` (its line 18), dot-sources `scripts/Get-FullSuiteDebt.ps1`, calls `Get-FullSuiteDebt`, and when `HasDebt` is
  true or `FlakyClasses` is non-empty prints `Format-FullSuiteDebt` on stdout; fail-open (any error prints nothing and exits 0).
  Register it in `.claude/settings.json` `SessionStart` beside the two existing entries (lines 193-222), shaped like the
  repo-hygiene entry: `"command": "powershell.exe"`, `"args": ["-NoProfile", "-ExecutionPolicy", "Bypass", "-File",
  "${CLAUDE_PROJECT_DIR}/.claude/hooks/full-suite-debt-session-start.ps1"]`, `"timeout": 15`.
- Done when: the hook parses; `'{"source":"startup"}' | powershell.exe -NoProfile -File .claude\hooks\full-suite-debt-session-start.ps1`
  prints the expected lines for the current tree (or nothing) in under 5 seconds (`Measure-Command`); `{"source":"resume"}`
  prints nothing; `.claude/settings.json` parses with `ConvertFrom-Json`. Lane: main session.
- Effort: S

### Step 8 - Rule and roadmap wording
- Files: `docs/current/references/reference_autonomous_run_conventions.md` (lines 106-109),
  `.claude/skills/dispatch-implementer/SKILL.md` (lines 119-123), `.claude/skills/implement-plan/SKILL.md` (line 89),
  `docs/current/plans/plan_roadmap_layering_and_project_splits.md` (line 23, Phase 1 at lines 101-110, status row at line 258),
  `docs/current/plans/plan_shorten_verification_loop.md` (E-row item, line 557),
  `docs/current/plans/plan_isolate_build_test_area_into_own_project.md` (Risks), `docs/current/TODO.md` (line 9)
- Depends on: Steps 5, 7.
- Effect: behaviour-changing for agents.
- Change:
  - The full-run rule in the reference and in `dispatch-implementer` becomes: a full run (`RunTest` with `suite: "full"`, or
    `Invoke-WorktreeTests.ps1 -Suite full`) is required before a push, and at task end when
    `pwsh -NoProfile -File scripts/Get-FullSuiteDebt.ps1` exits 1 (red, nightly missed, no commit recorded, a watched area changed
    since the marker's commit, or a stale watch-list entry); otherwise a fast run, with the nightly run
    (`scripts/Invoke-NightlyFullRun.ps1`) as the backstop. The hand-kept area list (reference lines 108-109) is removed; the watch
    list is `scripts/expensive-test-watch-list.json`.
  - `implement-plan/SKILL.md:89` ("One full `RunTest` (`suite: "full"`) per the reference.") becomes "A full or fast `RunTest` at
    plan end, as the reference's debt check decides." `todo-punchlist/SKILL.md:72` already defers to the reference and stays.
  - Roadmap: line 23 and Phase 1's `Doc:` name this plan (run speed stays with `plan_full_suite_nightly_and_tail_shard.md`); line 105
    reads "its Step 2a changes `FullTestMarkerStore.Describe`"; `Change:` reads "run this plan"; `Done when:` becomes "a task-end
    check whose changes touch no watched area reports no debt, the skill runs the fast suite (which skips the `OutOfProcess`
    fixtures, most of `Tests.Git`), and the nightly run covers the rest" (D8). Status table: add this plan's row; the row for
    `plan_full_suite_nightly_and_tail_shard.md` reads "DRAFT (run speed)".
  - `plan_shorten_verification_loop.md` E-row item: append "Moved to `plan_full_suite_nightly_and_tail_shard.md` (run speed)."
  - `plan_isolate_build_test_area_into_own_project.md`, Risks: "Moving `FullTestMarkerStore`, `TestRun*`, `BuildIsolation`,
    `TestSuiteFilter` or `TestInstanceLauncher` updates `scripts/expensive-test-watch-list.json` (the debt check reports stale
    entries) and the D38 guard in `scripts/FullTestMarker.Tests.ps1`."
  - `TODO.md` line 9: drop "and E-row sharding" from "left:".
- Done when: the edits are in; `pwsh -NoProfile -File scripts/Test-GuidanceHygiene.ps1` reports no new finding. Lane: main
  session (D37).
- Effort: S

### Step 9 - Live acceptance and close
- Files: this plan
- Depends on: Steps 1-8 committed; server restarted on the final commit (`McpServerStatus` `lastLaunch.servedSha`).
- Effect: text-neutral.
- Change (dispatcher): with `master` at the final commit, run one unfiltered
  `pwsh -NoProfile -File scripts\Invoke-WorktreeTests.ps1 -WorktreeRoot c:\tmp\rs-testtrees -Suite full` (about 25 minutes). Then
  check: the marker's `headSha` equals that commit; `scripts\Invoke-NightlyFullRun.ps1 -DryRun` prints `skip`;
  `scripts\Get-FullSuiteDebt.ps1` exits 0. Outcome classes:
  - **adopted**: the run is green against the 0-failure baseline and all three checks pass;
  - **checks fail**: name the failing check, fix it in a new slice, re-run only that check where possible;
  - **run red**: each failure is new per the baseline; apply the load-timeout rule in `reference_known_failing_tests.md`;
  - **run incomplete** (exit 2, no `summary.json`, a hang): re-run once; a second incomplete run gets a
    `docs/current/blockers/` writeup.
  Record the run id, seconds and outcome in this plan. Tell the owner to register the task (`scripts\Register-NightlyFullRun.ps1`
  in an elevated shell). The first real `nightly.log` line and the server path of D3 (the marker after the next `RunTest` full run
  on the restarted server) are checked opportunistically, not gated.
- Done when: the results are in this plan, Status is IMPLEMENTED, closed per `docs/current/templates/README.md`
  (`Closes: plan_full_suite_nightly_and_debt_check`). Lane: dispatcher.
- Effort: S

## Test table

| Test | Behaviour | Fixture | Asserts | Pass |
| --- | --- | --- | --- | --- |
| `FullTestMarkerStoreTests.Evaluate_RecentRedMarker_HasLastRunFailedCause` | D2 | temp root, in-process | `Causes` = `[LastRunFailed]` | green |
| `FullTestMarkerStoreTests.Evaluate_OldRedMarkerWithManyFastRuns_ListsAllCausesInOrder` | D2, D10 | same | `Causes` order, `Reason` from constants | green |
| `FullTestMarkerStoreTests` rewritten Evaluate tests (lines 191, 218-223, 246, 257, 269-272) | D10 | same | `Causes`, `LastFullRunUtc`, `FastRunsSince` | green |
| `FullTestMarkerStoreTests.Describe_RedRun_UsesStaleFormatWithReason` and rewritten Describe/StaleFindings tests (291, 299, 313, 332) | D10 wording | same | text equals the format constants applied (wording under test, item 11) | green |
| `FullTestMarkerStoreTests.ResetsMarker_OnlyFullSolutionUnfilteredRuns` | Q33 | pure | bool per case | green |
| `FullTestMarkerStoreTests.TryRead_MarkerWithHeadSha_ReadsIt`, `TryRead_MarkerWithoutHeadSha_HeadShaIsNull` | D3 | temp file | `HeadSha` | green |
| `FullTestMarkerStoreTests.NextMarker_FullSolutionRun_RecordsHeadSha`, `NextMarker_FastRun_KeepsHeadSha` | D3 | pure | `HeadSha` | green |
| `FullTestMarkerStoreTests.RecordRun_FullSolutionRun_WritesHeadSha` | D3 | temp root | marker on disk | green |
| `scripts/FullTestMarker.Tests.ps1` | D9, D11, D25, D28, D31, D32, D34, D38 | temp dir; reads `FullTestMarkerStore.cs` and `TestSuiteFilter.cs` as text | field values, booleans, guard matches | exit 0 |
| `scripts/Get-FullSuiteDebt.Tests.ps1` | D5, D13, D28, D34, D36 | canned inputs, no git | `Causes`, `Areas`, `HasDebt`, `StaleWatchEntries`, lines | exit 0 |
| `scripts/Invoke-NightlyFullRun.Tests.ps1` | D4, D26, D27, D28, D31, D35 | canned inputs | decision, wait count, filter string, merge lists, result object | exit 0 |
| Step 7 manual check | D5 hook | the current tree | lines printed, under 5 s | as stated |
| Step 9 live run | D3, D4, D9, D22 | one unfiltered worktree full run (out of process: the only end-to-end check that the script writes `headSha`) | marker `headSha`, `-DryRun` = `skip`, debt exit 0 | adopted |

No heavy fixture or out-of-process test is added to the solution; the one live run is the plan-end full run the rules require
anyway. Untested by design: the `RunTest` wiring in Step 2b (one argument; `ResetsMarker` and `RecordRun` are tested, the live
path is checked opportunistically in Step 9) and the nightly flow's process handling in Step 6b (its decisions are the tested pure
functions).

## Out of scope

- `[Explicit]` on slow tests (D1); a `dirtyTree` marker flag (D3).
- Server-side area checks or gating (D5).
- A forced single-worker nightly run (D29); toast or status-line notification (D4).
- The headless investigate-and-fix session (D30): a follow-up plan.
- Run-speed work (sharding, parallel full runs, Git fixture parallelism): `plan_full_suite_nightly_and_tail_shard.md` (D7).
- Pre-existing duplicates outside this plan's files: the `c:\tmp\rs-testtrees` default in `Get-TestRunTimings.ps1` (line 55,
  `C:\tmp\rs-testtrees\_results`) stays.

## Risks and open decisions

- Between Steps 2a and 3, three existing tests fail at run time: line 223 (record equality with a list property), line 291 (exact
  `Describe` text) and line 313 (`StartWith` on the old wording). The dispatcher runs tests after Step 3.
- The roadmap's Phase 4.2 pins `Describe` text; Step 2a must land first (roadmap line 105). The untracked proposal
  `proposal_engines_report_facts_tool_layer_writes_text.md` may later move such wire text from Engines to the tool layer; Step 2a's
  constants are then the single place to move.
- Peer sessions running an older server binary drop `headSha` on their fast-run rewrite until they restart; the next full run or
  nightly run writes it again, and meanwhile the debt check reports `no-commit-recorded` (safe direction).
- `plan_isolate_build_test_area_into_own_project.md` (ON HOLD) will move files named in the watch list and the D38 guard; Step 8
  adds the reminder and D36 reports stale entries.
- Busy detection by process name and command line may miss a test run started just after the check; the nightly run then
  overlaps it and a contention flake is handled by the D27 retry.
- Whether the NUnit adapter's `FullyQualifiedName~` matches nested (`Outer+Inner`) class names is unverified; a class the retry
  filter misses stays failing (`Merge-NightlyRetry`), the safe direction.
- The S4U task and the hook both use the default scratch root while `ROSLYNSENTINEL_SCRATCH_ROOT` is unset (probe 2026-11-27); if
  the owner sets it, it must be set at user level so both see it.
- Coverage gap accepted under D13: some `OutOfProcess` fixtures (for example `SolutionRestoreTests`,
  `ServerBinaryStalenessGitTests`, `WorktreeTeardownTests`) exercise production code no watch-list area names; a change there is
  reported only when the test file itself changes, and otherwise the nightly run is the backstop.
- `plan_full_suite_nightly_and_tail_shard.md` (run speed) also edits `scripts/Invoke-WorktreeTests.ps1`; its sharding step runs
  after this plan's Step 4.
- Another session may be editing the scripts or skills; re-read before Steps 4 and 8.
- No open owner decisions.
