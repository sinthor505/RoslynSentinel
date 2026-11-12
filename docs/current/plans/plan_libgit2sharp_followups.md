# Plan: LibGit2Sharp follow-ups - route trial evidence, audit, default-on, config operation, patch text

**Status:** IN PROGRESS 2026-11-12. Steps 1-12 done (6a58ffd6..dc8c2520). Approved 2026-11-12. Follow-ups to proposal_libgit2sharp_in_process_git; decisions recorded; Steps 22-23 (lists) skipped unless a caller shows cost.

## Problem

The in-process commit route (`git config roslynsentinel.inProcessCommit`, default off, trial on in this repo)
landed on 2026-11-12 (see [the proposal](../proposals/proposal_libgit2sharp_in_process_git.md), "Current state and
next" and 5c). It is wired as a trial with no way to judge it:

- **The trial log cannot answer "was any commit wrong?".** Each line of
  `<common git dir>/roslynsentinel/commit-route.jsonl` carries only `utc, outcome, commit, reason, pid`
  (`InProcessCommitRoute.AppendOutcome`, `RoslynSentinel.Common/InProcessCommitRoute.cs:196`, anonymous object
  near :210). It does not say whether the commit was a plain or an only-paths commit, which paths were listed,
  which ref moved, or which parent the commit was built on, so nothing can be checked against git afterwards.
- **No test shows a decline reaches the log through the real entry point.** The decline tests assert
  the counters (`AssertDeclined`/`AssertTaken`, `GitInProcessCommitRouteTests.cs:127`, `:137`). Only two tests read
  the log (`:602`, `:620`) and both call `route.RecordDecline` / `route.Commit` directly, not
  `GitImpl.CommitAsync`. The planner gap (what "verified" means for the log) is closed by Step 4.
- **There was no written bar for default-on.** The proposal says "review the trial log after a few days and decide";
  it named no thresholds and no setups. Criteria are now accepted (see Decision).
- **Two setups are barely covered.** `NewRepo` pins `core.autocrlf=false` and `core.hooksPath` repo-locally
  (`GitInProcessCommitRouteTests.cs:84-111`), so the LF scenarios are covered at the route level, but a linked
  worktree is covered only at the primitive level (`GitCasCommitTests.cs:676`), never through `CommitAsync`.
- **`git config` needed a shell twice** (proposal, "Current state and next", candidate 2). That is a tool-surface
  gap per CLAUDE.md "Mission".
- **Diff/show patch text always spawns git.** `DiffAsync`/`ShowAsync` send stat and patch to `RunGitAsync`
  unconditionally (`GitImpl.cs:2550`, `:2645`), and two tests pin "never reaches the reader". The reasons given
  for not serving it in process are listed in a comment in `DiffAsync` (about `GitImpl.cs:2597-2600`: stat graph
  widths, abbreviated `index` lines, rename headers, no-newline markers, binary notes, hunk-header function
  context), but nothing in source or tests measures any of them. They are unmeasured, not disproved.
- **Tag and worktree lists** stay on the CLI; the proposal gives "too many decline rules" as the reason, but I found
  no TODO, comment or doc in source recording which rules (see Steps 22-23 for a reconstruction, labelled a hypothesis).
- **A latent double-commit path (hypothesis, not observed).** In `TryCommitInProcessAsync` the commit exists after
  `_commitRoute.Commit` returns, but the code below it is still inside the outer `try` whose `catch` records a
  decline and returns null (`GitImpl.cs:3556`), after which `CommitAsync` runs the CLI commit. An exception thrown
  after the commit (for example from the `_logger.LogDebug` call in the inner catch, `GitImpl.cs:~3536`) would log
  a `declined` line after a `taken` line and commit a second time. Step 2 closes it structurally and Step 3 pins it.

### Facts verified against source on 2026-11-12 (line numbers drift: another session edits `GitImpl.cs`; cite symbols first)

Where each route decision is made and whether it reaches the log. "Logged" means `RecordDecline` (which calls
`AppendOutcome`, `InProcessCommitRoute.cs:70`) runs. The route code lives in `RoslynSentinel.Common`, not
`Tools.Basic` as the brief assumed; only the caller `GitImpl.TryCommitInProcessAsync` (`GitImpl.cs:3473`) is in
`Tools.Basic`.

| Decision | Decided at | Logged today |
| --- | --- | --- |
| Route off / key absent / not boolean | `InProcessCommitRoute.IsEnabled` `:55-67`, called `GitImpl.cs:3478` | No, by design (a disabled route counts nothing) |
| `IsEnabled` throws (any exception) | same, `catch` returns false | No; indistinguishable from "off" |
| Amend (also a blank message) | `GitImpl.cs:3486`; reason text says "an amend rewrites HEAD" | Yes |
| Paths given without `scope=listed` | `GitImpl.cs:3493` | Yes |
| Merge, cherry-pick, revert, rebase, am in progress | `DeclineBeforeStaging` `InProcessCommitRoute.cs:84` via `reader.GetInProgressOperation`, logged by `GitImpl.cs:3501` | Yes |
| `commit.gpgsign`, `commit.signoff`, bare repo, conversion attributes (filter/ident/encoding), more than 5000 files under a listed directory | `Libgit2GitReader.InProcessCommit.cs:30`, `:35`, `:13`, returned through `DeclineBeforeStaging`, logged `GitImpl.cs:3501` | Yes |
| pre-commit / prepare-commit-msg / commit-msg hook, `hook.*` config (Unknown) | `GitHooks.HasBlockingHook` (`GitHooks.cs:90` for `hook.*`), `DeclineBeforeStaging`, logged `GitImpl.cs:3501` | Yes |
| reference-transaction / post-index-change hook; post-commit hook that cannot be launched | `DeclineBeforeStaging` (`UnsupportedCommitHooks`, `PostHookLaunchBlocker`), logged `GitImpl.cs:3501` | Yes |
| Staging the listed tracked paths failed | `GitImpl.cs:3513` | Yes |
| Any exception in `TryCommitInProcessAsync` | `GitImpl.cs:3556` | Yes (but see the double-commit hypothesis above) |
| Message formatting declined (`commit.cleanup`, trailer config) | `InProcessCommitRoute.Commit` `:150` | Yes |
| Exception while formatting or committing | `Commit` `:166` | Yes |
| Primitive outcome Declined (env var, NUL byte, unmerged/intent-to-add, working tree differs, ...), BranchMoved, LockContention, NothingToCommit, Failed | `Commit` `:188`, from `Libgit2GitReader.CommitCompareAndSwapCore` (`CasCommit.cs:76-157`) | Yes |
| Taken | `Commit` after `result.IsCommitted` | Yes (`outcome: taken`, `commit`) |
| `GitImpl.CommitAsync` returns before the route is reached (message required, amend of a published HEAD, path-list parse error, missing path, ignored path, `git add` failure, `scope=all/tracked` pre-stage error) | `GitImpl.cs:3182-3327`, route call at `:3328` | No: these are tool errors, not route decisions. `taken + declined` is therefore not the number of `Git(commit)` calls |
| No repository found by `ReadHookLayout`, or the append throws | `AppendOutcome` (`:200`; `catch` at `:221`) | No, silently, by design ("best effort") |
| Result of the CLI commit that follows a decline | `CommitAsync` after `:3328` | No: a decline line never says whether the CLI commit then succeeded or what its hash was |

Conclusion for step A: every decline of an **enabled** route reaches the log today. What is missing is context on
each line, a test that proves it end to end, and (optionally) visibility of append failures. No decline needs to be
re-routed through `RecordDecline`.

Constraint on the log writer: `RoslynSentinel.Tests.Basic/BareFileWriteRatchetTests.cs:27` allowlists exactly one
file write in `InProcessCommitRoute.cs::AppendOutcome` (`["...::AppendOutcome"] = 1`). Keep a single
`File.AppendAllText` call there (no second file, no rotation write) or the ratchet fails.

## Decision

The work is eight pieces (A-G) drafted by the planner from the owner's approved list. The order is the planner's.
Text-neutral steps run first; the 3-day trial gate overlaps with them; behaviour-changing steps come last.

| Letter | Work | Steps |
| --- | --- | --- |
| A | Trial instrumentation: kind, listed paths, ref, parent on every line; double-commit guard; end-to-end log tests | 1-4 |
| B | Audit script `scripts/Test-CommitRouteLog.ps1` | 5-6 |
| C | Route-level second-setup tests (linked worktree, unpinned LF repo) plus a manual second-repo checklist | 7-8, 14 |
| D | Default-on criteria (accepted) and the flip | 14-19 |
| E | `GitRemote` `config` operation | 9-10, 20 |
| F | Patch text in process: parity corpus and measurement first, then route only byte-identical scenarios | 11-13, 21 |
| G | In-process tag list and worktree list (SKIPPED-UNLESS) | 22-23 |

### Owner decisions (2026-11-12)

- **2026-11-12, E1/E3 (config operation):** the operation is `GitRemoteOperation.config` (not on the `Git` tool).
  Writes run the CLI, `git config --local`; reads run in process through the reader seam. (libgit2 write rejected:
  in a linked worktree `repo.Config` addresses a file git ignores; see the old E3 reasoning kept in Risks.)
- **2026-11-12, E2/E4 (scope and keys):** `set`/`unset` are local scope only (no `--global`, no `--system`).
  `set`/`unset` accept only this allowlist, exactly: `roslynsentinel.*`, `user.name`, `user.email`,
  `core.autocrlf`, `core.eol`, `core.safecrlf`, `core.longpaths`, `pull.rebase`, `push.default`,
  `init.defaultBranch`, `branch.<name>.remote`, `branch.<name>.merge`, `branch.<name>.rebase`. `get` reads any key
  except this deny-list: `credential.*`, `*.password`, `*.token`, `http.*.extraheader`, and any `url.*` value that
  carries userinfo. E5 (refused-key wording, facts and options only), E6 (result type reuse-first with the
  `dto-conventions` skill) and E7 (store as given, report what git reads back) stay as implementer guidance.
- **2026-11-12, D1-D3 (default-on):** the criteria below are accepted as proposed. The flip applies to all
  repositories the server commits in. A config value that is present but not a boolean counts as off; an absent
  key counts as on (three-state read, the old D2 option b).
- **2026-11-12, F1/F2/G1 (patch text and lists):** measure first. Route only scenarios whose tool-visible text and
  `Warning` are byte-identical to the CLI's; decline the rest. Stop after the Step 11 measurement if the saving is
  under about 30 ms per call (Step 12 is that gate). Steps 22-23 (tag list, worktree list) are SKIPPED-UNLESS a
  caller shows real cost; they are kept in the plan, not deleted.
- **2026-11-12, Step 12 gate (patch text): PROCEED, BROAD DECLINE.** Measured saving (11b, staged, median of 10):
  1 file 71.7 -> 4.2 ms, 20 files 91.9 -> 23.9 ms. Steps 13 and 21 route staged and `refA..refB` patch text through
  libgit2 and decline to the CLI on: a rename, an empty-file add/delete, a non-ASCII path, a typechange; and on ANY
  `diff.*` or `color.*` key, or `core.whitespace`, unless it is on the verified-identical list (`diff.algorithm`,
  `diff.indentHeuristic`, `diff.noprefix`, `diff.mnemonicPrefix`, `diff.renames=false`, `diff.wsErrorHighlight`,
  `core.whitespace`), widened only by a new corpus scenario. An untested key goes to git.

### Main-session technical defaults (2026-11-12; planner-adopted, not owner decisions)

- **Default flip and the test suite.** `GitTestRepo.InitRepo` writes `roslynsentinel.inProcessCommit=false`
  (Step 15), plus a ratchet test that fails when a test source commits through `GitImpl`/`GitTool` without a pin
  (Step 18). The `NewRepo` helpers of the Reads and Seam fixtures (and the others the sweep finds) pin explicitly
  (Step 16).
- **Spawn-count test.** `Commit_StartsOnlyGitCommitItself_NoOtherGitProcessAsync`
  (`GitCommitSeamParityTests.cs:1180`) keeps a pinned-false copy and gets an in-process twin (Step 17).
- **Byte-exact stdout in the corpus.** The Step 11 test starts `git` itself for raw stdout bytes; it does not touch
  the private `RunGitBytesAsync`, and it does not use `GitTestRepo.RunGit`, which trims stdout.
- **Slice size.** Anything the depth ceiling cannot hold (tier 1, at most 3 files) is split into D1 slices; a
  reader-seam addition is a pair (interface + CLI + fallback; then the libgit2 partial) that lands in one commit
  because the first half alone does not compile.

### Accepted default-on criteria (ACCEPTED 2026-11-12; thresholds are the planner's numbers, accepted by the owner)

Default-on is considered only when all hold, measured by Step 14 with the Step 5-6 script:

1. **Volume and time.** At least 30 `taken` entries, on at least 3 distinct UTC dates, across all audited logs.
2. **Setups.** At least 2 setups, where a setup is a repository (one log file per common git dir) that differs
   from the other in at least one of: `core.autocrlf` true vs false/unset; main working tree vs linked worktree used for
   commits; repo-local hooks present vs none. At least 10 `taken` entries in the second setup, so it is not one
   lucky commit. Test scenarios (Step 8) do not count as setups; they only de-risk before the real run.
3. **Audit.** `Test-CommitRouteLog.ps1` exits 0 for every setup: zero FAIL (missing commit, parent not equal to the
   reflog's previous value, tree differing outside the listed paths, duplicate commit, fsck not clean). WARN
   (reflog expired, branch deleted) is reviewed by hand and listed in the Step 14 report.
4. **Declines.** Every distinct decline reason class is in the script's known-reason catalogue
   (the table above), and none is `libgit2 failed:` or an exception-type reason unless explained in the Step 14
   report. The taken/declined ratio is reported but is not a gate: a decline costs only speed.
5. **No human report.** No peer session or the owner reports a wrong, lost or duplicated commit in the window.

Any FAIL resets the clock and keeps the default off until its cause is fixed and explained.

Flip step (Step 19): `InProcessCommitRoute.IsEnabled` (`InProcessCommitRoute.cs:61`) is the only place the default
is read: `return reader.GetBoolConfig(workingDirectory, ConfigKey, cliTimeout) == true;`. The flip reads the key as
text through Step 9's `GetConfigValue` and decides: unset -> `DefaultEnabled` (true); true-like (`true`/`yes`/`on`/
non-zero integer) -> on; any other text, including garbage -> off; an exception stays off. `GetBoolConfig` cannot
make that distinction (`CliGitReader.cs:360-373` and `Libgit2GitReader.cs:668-690` return null for unset, for not
a boolean and for a CLI failure alike), which is why Step 9 comes first.

## Execution rules

- Build with 0 errors before each commit. One `Build`/`RunTest` at a time, subagents included: they share
  `bin/` and `obj/`. "Lane" below means two implementing agents on disjoint files whose builds are serialised.
- Route and CAS tests are `[Category("OutOfProcess")]` and `[NonParallelizable]` (`GitInProcessCommitRouteTests.cs:20-23`):
  run them with `RunTest` suite `full`, not the default `fast`. Compare failures with `docs/known-failing-tests.*.txt`
  (the memory note says the test baseline is 0 failures: any failure is new).
- Steps that change server source (1, 2, 9, 10, 13, 19-21) need a restart of your own server instance after the
  build (`McpServerControl StopServer`, then `LoadSolution`) before any LIVE check; an enum addition (Step 20a) also
  needs one. The running server is stale until then.
- Commit trailers per `docs/current/templates/README.md` rule 11: `Plan: plan_libgit2sharp_followups` on each slice;
  `Closes: plan_libgit2sharp_followups` on the last non-optional slice. Steps 22-23 do not hold the close.
- Size column uses `.claude/implementer-tiers.json`, read as 0-based indices: D0/D1/D2 = `depth.tiers[0..2]`
  (at most 3 files; 10/15/20 edits; 25/35/45 tool calls), S0 = `sweep.tiers[0]` (one operation, at most 8 files, 25
  sites). The file's current ceiling is depth tier 1 (D1) and sweep tier 0 (S0), so every step below is D0, D1 or S0
  (a D2-sized step was split). Test-only steps marked "test-writer" go to that agent.
- New tests go in per-slice test files (template rule), except where a step says it edits one shared helper.
- A `// TODO` is left at any deferred spot (CLAUDE.md); search a file for `TODO` before editing it.

### Schedule and lane map

Text-neutral first. Two lanes at most; each row's two cells touch disjoint files. Steps 1-13 are text-neutral
(steps 12 and 14 need no code); 15-18 are tests only; 19-21 are behaviour-changing.

| Slot | Lane 1 | Lane 2 | Why disjoint |
| --- | --- | --- | --- |
| 1 | Step 1 (route, CAS result) | Step 5 (script checks) | Common C# vs `scripts/` |
| 2 | Step 2 (`GitImpl.cs`) | Steps 9a then 9b (reader interface/CLI/fallback, then libgit2 partial) | `GitImpl.cs` vs reader files |
| 3 | Step 3 (route test file) | Step 6 (script summary, same new file as 5, so after it) | test file vs script |
| 4 | Step 4 (route test file, after 3) | Step 9c (config-read parity tests, new file) | |
| 5 | Step 7 (`GitTestRepo.cs`, Seam and Reads test files) | Step 10a (`GitImpl.cs`, `IGitOperations.cs`, DTO) | support/test files vs Tools.Basic |
| 6 | Step 8 (new setup test file) | Step 10b (`GitImpl.cs`) | |
| 7 | Step 10c (new test file) | Step 11a (new test file) | |
| 8 | Step 11b (new test file) | Step 12 (gate: owner reads the measurement) | |
| 9 | Steps 13a then 13b (only if Step 12 passes) | Step 14 (LIVE review; waits for the clock, not for a lane) | Step 14 changes no files |
| 10 | Step 15 then Step 16 (sweep over fixtures) | none (the sweep touches Seam, Reads and others) | |
| 11 | Step 17 (Seam test file) | Step 18 (new ratchet file) | |
| 12 | Step 19 (flip: route, `GitImpl.cs` comments, route tests) | Steps 20a, 20b (`ToolEnums.cs`, `GitRemoteTools.cs`, validator) | |
| 13 | Step 20c (new test file) | Step 21 (`GitImpl.cs`, `GitDiffShowToolParityTests.cs`; only if Step 12 passed) | |
| optional | Steps 22, 23 | | SKIPPED-UNLESS |

Cross-step file conflicts to respect: Steps 9, 13, 22 and 23 all edit the reader interface files, so never together;
Steps 10a, 10b, 21 and 2 all edit `GitImpl.cs`, so never two at once (slots above already separate them); Step 7
edits the Seam and Reads test files that Steps 16 and 17 also edit (7 before 16/17).

## Steps

### Step 1 (A) - carry kind, listed paths, ref and parent into the log line

- Done: 6a58ffd6
- Files: `RoslynSentinel.Common/CasCommitResult.cs`, `RoslynSentinel.Common/Libgit2GitReader.CasCommit.cs`,
  `RoslynSentinel.Common/InProcessCommitRoute.cs`
- Depends on: none
- Effect: text-neutral (no tool output changes; the log line gains optional fields, and `WhenWritingNull` keeps
  today's shape for a caller that passes nothing)
- Change:
  - `CasCommitResult.Committed` (`CasCommitResult.cs:47`): add optional `ReferenceName` (the full ref the CAS moved,
    `refs/heads/x`, or `HEAD` when detached; `ReadCommitTarget`, `CasCommit.cs:192`, already computes it) and
    `ParentId` (the tip the commit was built on, null for an unborn branch). Fill both where the primitive returns
    `Committed(commit.Sha)` (`CommitCompareAndSwapCore`, `CasCommit.cs:76-157`).
  - In `InProcessCommitRoute.cs` add `internal sealed record CommitRouteContext(string Kind, IReadOnlyList<string>? Paths)`
    (same file, to stay within three files). `Kind` is one of `plain`, `only-paths`, `amend`.
  - `RecordDecline(string reason, string workingDirectory)` (`:70`) and `AppendOutcome` (`:196`; its `catch` is at
    `:221`) take an optional trailing `CommitRouteContext? context = null` (migration-first: existing callers,
    including the tests, compile unchanged). `AppendOutcome` writes `kind`, `pathCount`, `paths` (first 20, each cut
    at 200 characters, `pathsTruncated: true` when anything was cut) and, for `taken`, `ref` and `parent`. Keep the
    single `File.AppendAllText` call (the write ratchet, see Facts).
  - Inside `Commit` (`:138-190`) build the context from `listedPaths` for the three `RecordDecline` calls (`:150`,
    `:166`, `:188`) and for the taken line.
- Done when: Build 0 errors; `Commit_AppendsATakenLine_WithTheNewCommitSha` (`:620`),
  `RecordDecline_AppendsADeclinedLine_ToTheCommitRouteLog` (`:602`) and `BareFileWriteRatchetTests` still pass
  unchanged.
- Size: D1 (3 files, about 8 edits).

### Step 2 (A) - pass the context from `GitImpl`, and close the double-commit path

- Done: fe85e8a0
- Files: `RoslynSentinel.Tools.Basic/GitImpl.cs`
- Depends on: 1
- Effect: text-neutral
- Change:
  - In `TryCommitInProcessAsync` (`GitImpl.cs:3473`) derive `kind` once after `IsEnabled` returns true
    (`amend` -> `amend`; `paths` given -> `only-paths`; else `plain`) and pass `new CommitRouteContext(kind, listedFilePaths)`
    to the five `RecordDecline` calls (`:3486`, `:3493`, `:3501`, `:3513`, `:3556`).
  - Guard: declare the committed outcome before the `try`; once `_commitRoute.Commit` returns committed, assign it.
    In the outer `catch` (`:3556`), when a commit was made, return a success `GitCommitResult` built from it
    (hash, stored message, empty `RemainingStaged`) and do not call `RecordDecline` or return null. Nothing after
    the commit may route to the CLI. Leave a comment saying why (the CLI would make a second commit).
  - `// TODO` at `:3486`: the condition is `amend || IsNullOrWhiteSpace(message)` but a blank message without amend
    is already refused at the top of `CommitAsync`, so only amend reaches it. Do not change reason text in this step
    (the reason is part of the log contract the Step 5 catalogue reads).
- Done when: Build 0 errors; `GitInProcessCommitRouteTests` still passes; restart the server so the live log gets
  the new fields.
- Size: D1 (1 file, about 8 edits).

### Step 3 (A) - tests for the new fields and the guard

- Done: c89600f1 (the revert-the-guard proof was not run)
- Files: `RoslynSentinel.Tests.Tools.Basic/GitInProcessCommitRouteTests.cs`
- Depends on: 2
- Effect: text-neutral (tests only)
- Change: new tests in the existing fixture: (a) a taken only-paths commit through `CommitAsync` logs
  `kind: only-paths`, `pathCount`, the listed paths, `ref` equal to `refs/heads/<BranchName>`, `parent` equal to the
  previous HEAD; (b) a plain commit logs `kind: plain` with no `paths`; (c) 25 listed paths log 20 and
  `pathsTruncated: true`; (d) an amend decline logs `kind: amend`; (e) the guard: a `GitImpl` built with a reader
  decorator whose `GetNameStatus` throws and a logger whose `LogDebug` throws makes exactly one commit, returns
  success with that hash, and the log holds one `taken` line and no `declined` line (`GitImpl`'s internal
  constructor takes both, `GitImpl.cs:622`).
- Done when: Build 0 errors; the fixture passes with suite full; with the Step 2 guard reverted locally, (e) fails
  by producing two commits (proof the test covers the path).
- Size: D1 (1 file, about 10 edits).

### Step 4 (A) - every route decline test proves the log line

- Done: d7aac389, fe9dd429, f9580035 (signoff, hook.* and nothing-to-commit declines added; the installed git does
  not honour `commit.signoff`, so no Signed-off-by assertion)
- Files: `RoslynSentinel.Tests.Tools.Basic/GitInProcessCommitRouteTests.cs`
- Depends on: 3
- Effect: text-neutral (tests only)
- Change: `AssertDeclined(route, fragment)` (`:127`) and `AssertTaken(route)` (`:137`) gain a `directory` parameter and,
  besides the counters, read the last line of `ReadCommitRouteLog(directory)` (`:146`) and assert `outcome`, that
  `reason` contains the same fragment, and `kind`. The existing decline tests (blocking hook `:417`, amend `:432`,
  merge in progress `:450`, gpgsign `:474`, filter attribute `:501`, branch moves `:535`) pass their directory.
  Add the missing classes through `CommitAsync`: `commit.signoff`, a `hook.*` config entry, nothing to commit.
  "Paths without `scope=listed`" is not reachable from the tool (it infers `listed`), so it stays covered by
  `RecordDecline` alone.
- Done when: Build 0 errors; the fixture passes; deleting the `AppendOutcome` call from `RecordDecline` in a local
  experiment makes the decline tests fail (the proof that the log is covered).
- Size: D1 (1 file, about 14 edits).

### Step 5 (B) - audit script: the checks

- Done: 6fab1959
- Files: `scripts/Test-CommitRouteLog.ps1` (new)
- Depends on: none to write; Step 1 for the checks that use `ref`, `parent`, `kind`, `paths` (lines without those
  fields are reported `unchecked: pre-step-1 line`, never FAIL)
- Effect: text-neutral (outside the server)
- Conventions: `scripts/` already holds `Test-*.ps1` pass/fail checks (`Test-GuidanceHygiene.ps1`,
  `Test-TelemetryRollup.ps1`) and report scripts (`Get-*.ps1`); no existing script reads this log, so there is no
  front door to reuse (`reference_working_conventions.md`, "Scripts"). Pure PowerShell and the git CLI; no MCP,
  no .NET.
- Change: parameters `-RepoPath` (default current directory), `-LogPath` (override), `-Since [datetime]`,
  `-SkipFsck`; exit 0 when no FAIL, 1 otherwise. Locate the log through
  `git -C <repo> rev-parse --git-common-dir` + `roslynsentinel/commit-route.jsonl`. For each `taken` entry:
  1. **exists:** `git cat-file -e <commit>^{commit}`; FAIL `missing` (an unreachable commit can be pruned about 2
     weeks after a reset, so the message says "check reflog/gc").
  2. **parent vs reflog:** read the raw reflog `<common>/logs/<ref>` (a branch ref; a detached `HEAD` commit uses the
     worktree's `logs/HEAD`), find the entry whose new value is the commit, and require its old value to equal
     `git rev-parse <commit>^` (all zeros for a root commit) and the message to be `commit: <subject>` or
     `commit (initial): <subject>`. FAIL `parent-mismatch`; WARN `reflog-expired` when no entry exists; WARN
     `branch-gone` when the reflog file is gone.
  3. **tree vs listed paths** (only when `paths` is logged and `pathsTruncated` is not true):
     `git diff-tree -r --no-renames --name-only -z <parent> <commit>` (empty tree for a root commit) must equal the
     same command restricted with `--literal-pathspecs -- <paths>`; a listed path may be a directory, so compare by
     pathspec, not by string. FAIL `tree-outside-listed-paths`. A plain (whole-index) commit has no listed paths:
     `unchecked: whole-index`.
  4. **duplicate:** the same commit id logged twice. FAIL.
  5. once per log: `git fsck --no-dangling` exits 0 (skipped by `-SkipFsck`; slow on large repos).
  Outcome classes per entry: `ok`, `unchecked:<why>`, `warn:<kind>`, `fail:<kind>`. This step stops at per-entry
  results and the exit code.
- Done when: running it on this repo's log prints a class per entry and exits 0 or lists only WARN/`unchecked`.
- Size: D1 (1 new file; the check logic is what makes it long: split from the summary to stay under 35 calls).

### Step 6 (B) - audit script: summary, setup header, fixture

- Done: b0391f79 (`Test-CommitRouteLog.Tests.ps1` 9/9; this repo's log 2026-11-12: 89 taken, 2 ok, 87 unchecked
  because older lines lack paths, 0 fail)
- Files: `scripts/Test-CommitRouteLog.ps1`; optionally `scripts/Test-CommitRouteLog.Tests.ps1` (new, only if the
  owner wants the self-test: see Risks B1)
- Depends on: 5
- Effect: text-neutral
- Change: add `-AsJson`; the summary: counts by outcome; declines grouped by reason after replacing hex ids with
  `<sha>`; taken per UTC date and per `kind`; the distinct `pid` count; and a "setup" header (`core.autocrlf`,
  `core.hooksPath`, repo-local hooks present, number of linked worktrees, git version) so Step 14's setup criterion
  can be read off the output. Acceptance fixture: a hand-built log with one wrong `commit`, one wrong `parent` and
  one path outside the list exits 1 with each named FAIL, and a clean hand-built log exits 0.
- Done when: both fixtures behave as stated and the summary prints on this repo's log.
- Size: D1 (1-2 files).

### Step 7 (C) - promote `AddLinkedWorktree` into `GitTestRepo`

- Done: ac573324
- Files: `RoslynSentinel.Tests.Support.Basic/GitTestRepo.cs`, `RoslynSentinel.Tests.Tools.Basic/GitCommitSeamParityTests.cs`
  (private copy at `:122`), `RoslynSentinel.Tests.Tools.Basic/GitCommitReadsParityTests.cs` (private copy at `:113`)
- Depends on: none
- Effect: text-neutral (tests only)
- Change: add `GitTestRepo.AddLinkedWorktree(main, name, branch?)` with the Seam copy's behaviour (absolute paths
  even when the machine's git config prefers relative ones); leave `// TODO use GitTestRepo.AddLinkedWorktree` on the
  two old copies (a deferred migration, CLAUDE.md "Deferred issues"). This avoids a third copy in Step 8.
- Done when: Build 0 errors; the Seam and Reads fixtures still pass.
- Size: D0 (3 files, about 4 edits).

### Step 8 (C) - route-level second-setup tests

- Done: 3b42858a
- Files: `RoslynSentinel.Tests.Tools.Basic/GitInProcessCommitRouteSetupTests.cs` (new)
- Depends on: 3 (log field assertions), 7
- Effect: text-neutral (tests only)
- Decision (NUnit vs manual): NUnit hosts it. `GitInProcessCommitRouteTests` already runs the route end to end
  through `CommitAsync` with real git in temp repos (`RunScenarioAsync` `:162`, `ParityScenarios` `:152`), including
  the LF/`autocrlf=false` setup (`NewRepo` `:88` pins it) and CRLF with `autocrlf=true` / `text=auto`. What is not
  covered is the linked worktree at route level. A manual checklist is kept only for real second repositories
  (Step 14), because a temp repo is not a second *setup* for criterion D.2.
- Change: a new fixture with `[NonParallelizable]`, `[Category("OutOfProcess")]`, the same SetUp/TearDown pattern,
  and short names (a long scenario name pushed a linked-worktree git dir past MAX_PATH once and libgit2 failed:
  `GitCommitSeamParityTests.cs:893-895`). Tests:
  - `InProcessCommit_LinkedWorktree_IsTakenOnTheWorktreeBranch_AndLogsToTheCommonDirectoryAsync`: route enabled only in
    the main repository's local config; commit in the worktree is taken (`AssertTaken`), the main branch does not
    move, the line lands in `<main>/.git/roslynsentinel/commit-route.jsonl` (not under `.git/worktrees/`), `ref` is
    the worktree's branch, and the HEAD and branch reflog entries match the CLI twin (as `GitCasCommitTests.cs:676` does for the primitive).
  - `InProcessCommit_TwoLinkedWorktrees_CommitInParallel_BothTakenAndBothLoggedAsync`: two worktrees on two branches, two
    commits on two tasks; both taken, two log lines, `git fsck` clean (the `AssertFsckClean` pattern, `:276`).
  - `InProcessCommit_UnpinnedLfRepo_NoAutocrlfEntry_EolLfAttribute_IsTakenAndStoresLfAsync`: a repo with no
    `core.autocrlf` entry at all and `.gitattributes` `* text eol=lf`, a listed file with CRLF content; taken, the
    stored blob is LF, the working file is untouched.
- Done when: Build 0 errors; the new fixture passes with suite full.
- Size: D1 (1 new file); test-writer fits.

### Step 9 (E) - read a config value as text through the reader seam

- Done: 9a bab7f3d2; 9b e1c6921c, 573680dd (a valueless key now reads "" and declines to the CLI); 9c 16d773f3,
  c54cc915, b262bccf (probe-table rows). The [Explicit] global-key-from-linked-worktree test passed when run by hand
  2026-11-12.
Why: the only config read on the seam is `GetBoolConfig`, which returns null for "unset", for "not a boolean" and
for a CLI failure alike (`CliGitReader.cs:360-373`, `Libgit2GitReader.cs:668-690`). The `config get` operation and
the three-state flip (Step 19) need the text and the distinction.

Helper to build on (decision, planner recommendation): `ConfigValue` (`Libgit2GitReader.cs:324`), not
`EffectiveConfigValue` (`Libgit2GitReader.IndexWrites.cs:305`). Both exist because LibGit2Sharp's `repo.Config` in a
linked worktree misses the main repository's config. They differ: `ConfigValue` reads a linked worktree through
`Configuration.BuildFrom(<commondir>/config)` and never opens a second `Repository`; `EffectiveConfigValue` opens the
main repository (`new Repository(CommonDirectory(repo))`) for the read, which sees the global and system files
explicitly. `GetBoolConfig` already uses `ConfigValue`, so building the text read on it keeps the two reads
consistent. Risk to check in Step 9c: whether `BuildFrom` with only the repository path also sees the global and
system levels (the existing comment says every level, no test names the linked-worktree-plus-global case). If it
does not, fix `ConfigValue`. Leave `// TODO fold EffectiveConfigValue into ConfigValue` on `EffectiveConfigValue`
(one helper is the goal; its eight or so callers move in a later slice).

- Step 9a - interface, CLI and fallback
  - Files: `RoslynSentinel.Common/GitReader.cs` (beside `GetBoolConfig` `:201`), `RoslynSentinel.Common/CliGitReader.cs`
    (beside `:360`), `RoslynSentinel.Common/FallbackGitReader.cs` (beside `:133`)
  - Depends on: none
  - Effect: text-neutral (a new member with no caller)
  - Change: `string? GetConfigValue(string workingDirectory, string key, TimeSpan cliTimeout)` returning the effective
    value, or null when unset. CLI side: `git config --get <key>` (exit 1 = unset; 128 = not a repository: null);
    multi-valued keys return the last value like git. Lands in one commit with 9b (9a alone does not compile).
  - Size: D1 (3 files).
- Step 9b - libgit2 implementation
  - Files: `RoslynSentinel.Common/Libgit2GitReader.Config.cs` (new partial of the `sealed partial class`)
  - Depends on: 9a
  - Effect: text-neutral
  - Change: `GetConfigValue` over `ConfigValue`; declines (throws `NotSupportedException`, the file's idiom) on an
    empty or valueless entry, as `GetBoolConfig` does at `:675-680`. Add the `// TODO fold` comment to
    `EffectiveConfigValue` only if a second file is acceptable; otherwise leave the TODO in the new file naming it.
  - Done when (9a+9b): Build 0 errors.
  - Size: D1 (1 new file).
- Step 9c - parity tests
  - Files: new test file in `Tests.Tools.Basic` (for example `GitConfigReadParityTests.cs`)
  - Depends on: 9a, 9b
  - Change: both readers and the composite agree for set, unset, empty value, valueless key, multi-valued key, key
    set only globally, key set only in the main repository seen from a linked worktree, key set only globally seen
    from a linked worktree (the open `BuildFrom` question).
  - Done when: the fixture passes with suite full and the composite serves it from libgit2 (counters show a
    libgit2 call: the pattern of `DiffAndShowNameOnly_InTheNormalEnvironment_AreServedByLibgit2NotTheFallbackAsync`
    in `GitDiffShowToolParityTests.cs`, about `:1051`).
  - Size: D1 (test-writer).

### Step 10 (E) - config operation in `GitImpl`: result type, read, write

- Done: 10a 32aa50f9 (new `GitConfigResult` with a NEW-DTO justification), 10b 1d1ed3a5 and f9ef1ee4 (hint call
  literals removed until Step 20 adds the enum value), 10c 4fc91608. `ConfigAsync` takes `action` as a string until
  Step 20 adds `GitAction` members.
Result type: the `dto-conventions` skill applies; the implementer loads it and first checks whether an existing
result can carry key/value/previous value/changed before adding a new type (owner decision E6, guidance).

- Step 10a - DTO, interface, read side
  - Files: `RoslynSentinel.Tools.Basic/IGitOperations.cs` (add the member beside `:9`/`:22`/`:25`), the result DTO file
    (location per the skill), `RoslynSentinel.Tools.Basic/GitImpl.cs`
  - Depends on: 9a, 9b
  - Effect: text-neutral until Step 20 wires it (no caller)
  - Change: `ConfigAsync(gitRoot, action, key, value, ...)` with the `get` path: key-shape validation (`section.name`
    or `section.subsection.name`, no leading `-`, no whitespace), the deny-list (Decision) refusing without reading,
    and the read through `GetConfigValue`. Refusals use the standard `GitErrorCodes` shape with facts and options
    only, never policy text (CLAUDE.md "Working conventions"; owner guidance E5).
  - Size: D1 (3 files).
- Step 10b - write side
  - Files: `RoslynSentinel.Tools.Basic/GitImpl.cs`
  - Depends on: 10a
  - Effect: text-neutral until Step 20
  - Change: `set` and `unset` through the CLI (`git config --local <key> <value>` / `git config --local --unset <key>`),
    allowlist check first (Decision; a `branch.<name>.*` entry matches only `remote`, `merge`, `rebase`). A
    successful `set` returns the key, the new value as git reads it back (E7), and the previous value (null when
    unset); `unset` of an absent key is a reported no-op, not an error. In a linked worktree `--local` writes the
    main repository's config, the file `ConfigValue` reads.
  - Size: D1 (1 file).
- Step 10c - tests
  - Files: new test file in `Tests.Tools.Basic`
  - Depends on: 10b
  - Done when: tests cover `get` (set, unset, global-only, linked worktree, deny-listed key refused unread),
    `set` (writes the main repository's `.git/config` from a linked worktree, previous value reported, refused key
    leaves the file unchanged byte for byte, each allowlist pattern accepted, a near-miss like
    `core.hooksPath` or `alias.x` refused), `unset` (absent key is a no-op).
  - Size: D1 (test-writer).

### Step 11 (F) - patch parity corpus and measurement (measure before building)

- Done: 11a fa85eacb, 11b dc8c2520. Outcome table and gate result: Decision, "Step 12 gate".
Anchor, verified: what the tool returns today for a patch is `RejoinLines(utf8-decode(stdout))` (`RunGitAsync`
`GitImpl.cs:1765` calls `RejoinLines` `:1727`: every line re-terminated with `Environment.NewLine`, a missing final
newline added, a lone CR also a line break), then `CapToUtf8Bytes`, `CountChangedFiles`,
`DetectDecodeCorruption`. Parity is therefore measured on two layers: raw `git` stdout bytes (diagnostic) and the
tool-visible text after the same `RejoinLines`. A scenario may be routed only if both the text and the `Warning`
(decode corruption) are equal. Raw bytes: the test starts `git` itself (Process, stdout read as bytes). Do not use
`GitTestRepo.RunGit` (it trims stdout) and do not change the private `RunGitBytesAsync` (technical default).

libgit2 side to probe: `repo.Diff.Compare<Patch>` with `CompareOptions { Similarity = None, ContextLines = 3 }`, as
`GetNameStatus` builds its options (`Libgit2GitReader.cs:~1215`), and `Patch.Content` (`Patch.cs:163` in the
reference clone) / `PatchEntryChanges.Patch`, `Oid`, `OldOid`, `Mode`, `OldMode`. Option gaps in the clone
(LibGit2Sharp 0.32.0): `Diff.BuildOptions` (`Diff.cs:20-81`) sets context and interhunk lines and flags but never
the native `IdAbbrev` (`Core/GitDiff.cs:233`), so `index <a>..<b>` lines use libgit2's default abbreviation, not
this repo's 8 (`ShortHashLengths`); `CompareOptions.IndentHeuristic` exists (`CompareOptions.cs:52`) and defaults to
false while git has it on since 2.14; `GIT_DIFF_INCLUDE_TYPECHANGE` is always set (`Diff.cs:24`). These are
hypotheses the corpus confirms or refutes; none is verified against bytes.

Scenarios (one `GitTestRepo` repo each; staged and `refA..refB` for every one; each reports one outcome class:
`parity`, `differs:<first differing line>`, `libgit2-throws`, `declined-by-existing-rule`):
1. plain modify, 3 context lines, 2 hunks;
2. large file, many hunks, hunks separated by exactly 6 and 7 unchanged lines (context merge boundary);
3. rename with and without edits (expect declined: similarity detection is not reproduced);
4. binary file add and modify (git prints `Binary files a/x and b/x differ`);
5. mode change only (644 to 755) and mode change plus content;
6. CRLF content with `core.autocrlf=true` and `* text=auto` (tool-visible layer after `RejoinLines`);
7. no newline at end of file, added and removed (`\ No newline at end of file`);
8. empty file added and deleted;
9. submodule entry (expect declined: `Diff_RepositoryWithASubmodule_IsAnsweredByTheCliAsync`, `GitDiffShowToolParityTests.cs:795`);
10. unicode and space paths with `core.quotePath=false` (the tool passes it);
11. non-UTF-8 text content (Latin-1 bytes): compare `Warning` equality, not only text;
12. ambiguous hunk placement (repeated blocks): the indent-heuristic case;
13. hunk-header function context: a change inside a C-like function body so git prints `@@ -10,7 +10,8 @@ void Foo()`;
    plus one inside a Markdown or plain-text file where no function line exists, and one with a `diff=` attribute
    (expected decline);
14. `diff.*` and `color.*` config present: `diff.context`, `diff.interHunkContext`, `diff.algorithm`,
    `diff.indentHeuristic=false`, `diff.noprefix`, `diff.mnemonicPrefix`, `color.diff=always`, `diff.external`. Today
    only `diff.renames` and `diff.orderfile` are declined for the name-status read (`RequireNameStatusRepository`,
    `Libgit2GitReader.cs:1528-1535`), so each key that changes the patch needs a new decline;
15. typechange (file to symlink) and a deleted-then-re-added path;
16. a merge commit (`show` against the first parent) and a root commit (`show` against the empty tree).

Measurement (also this step's output): median of 10 runs, fresh `Repository` per call, native warmup excluded, for
a one-file staged diff and a 20-file staged diff: CLI patch (the exact command `DiffAsync` runs) versus the libgit2
`Patch`. The libgit2 probe has no decline logic yet, so its time is a lower bound and the saving an upper bound.

- Step 11a - harness, measurement, scenarios 1-8
  - Files: `RoslynSentinel.Tests.Tools.Basic/GitPatchParityHarness.cs` (new, internal static helpers: byte-exact
    git runner, libgit2 probe, comparer, `RejoinLines` copy or access) and `GitPatchParityCorpusTests.cs` (new)
  - Depends on: none (production untouched)
  - Effect: text-neutral
  - Size: D1 (2 new files; test-writer)
- Step 11b - scenarios 9-16
  - Files: `RoslynSentinel.Tests.Tools.Basic/GitPatchParityCorpusMoreTests.cs` (new, uses the harness)
  - Depends on: 11a
  - Size: D1 (test-writer)
- Done when (11a+11b): Build 0 errors; the fixtures run under suite full and print the table scenario -> outcome
  class and the two medians; the table is pasted into Step 13 as the routing scope. A scenario that differs is kept
  as an `[Explicit]` or asserted-different test with its reason, so the corpus is not silently shrunk.

### Step 12 (F) - gate: is the saving worth building (owner decision, no code)

- Done: owner 2026-11-12, proceed with broad decline (Decision).
- Depends on: 11a, 11b
- Rule (owner 2026-11-12): if the measured saving per call (CLI median minus libgit2 median) is under about 30 ms,
  stop: mark Steps 13 and 21 SKIPPED and record the numbers here. Otherwise continue with 13 and 21, routing only
  scenarios whose tool-visible text and `Warning` are identical.
- Done when: the numbers and the decision are written into this plan's Status.

### Step 13 (F) - patch text on the seam, no caller (runs only if Step 12 passes)
- Step 13a - interface, CLI and fallback
  - Files: `RoslynSentinel.Common/GitReader.cs`, `CliGitReader.cs`, `FallbackGitReader.cs`
  - Depends on: 12 (and 9: same files, so not in parallel)
  - Effect: text-neutral (a new member with no caller)
  - Change: `string? GetPatchText(string workingDirectory, string target, IReadOnlyList<string>? paths, TimeSpan cliTimeout)`
    for `staged` and plain `refA..refB` (the targets `IsSeamDiffTarget` accepts, `GitImpl.cs:2547`) and
    `GetCommitPatchText(workingDirectory, revision, paths, cliTimeout)` for `show` (first parent or the empty tree, the
    shape of `GetCommitNameStatus`, `Libgit2GitReader.cs:~1263`). The CLI side runs the exact command `DiffAsync` /
    `ShowAsync` run today (`-c core.quotePath=false diff ...`). Lands in one commit with 13b.
  - Size: D1 (3 files).
- Step 13b - libgit2 implementation
  - Files: `RoslynSentinel.Common/Libgit2GitReader.Patch.cs` (new partial)
  - Depends on: 13a
  - Change: reuse `RequireNameStatusRepository`, `RequirePlainHistory`, `ParseLogPathspecs` and the rename decline;
    add the declines Step 11 found (config keys, attributes); set `IndentHeuristic`; re-abbreviate `index` lines from
    the entry's `OldOid`/`Oid` with the repository's cached short length if the corpus shows that is the only
    difference. Working-tree targets stay CLI and never reach the member.
  - Done when (13a+13b): Build 0 errors; Step 11's fixtures, now calling this member through the composite, show
    `parity` for every scenario Step 21 will route and a decline for every other; counters show libgit2 served the
    parity ones.
  - Size: D1 (1 new file).

### Step 14 (C, D) - LIVE trial review (gate; needs no code)
- Files: none (report appended to this plan)
- Depends on: Steps 1, 2, 5 and 6 landed and the server restarted; at least 3 calendar days of log after the
  restart; a second repository with the route enabled (`git config roslynsentinel.inProcessCommit true`)
- Effect: text-neutral
- Procedure: run `scripts/Test-CommitRouteLog.ps1` in this repo and in each second repository; the second setup
  must differ from this repo in at least one dimension of criterion D.2 (suggestion: an LF repository with
  `core.autocrlf=false`, and a commit made from a linked worktree). Manual checklist for the second repository, each
  with the audit run afterwards: a plain commit; an only-paths commit of a tracked file; an only-paths commit of
  a new untracked file; a commit with a repo-local `commit-msg` hook present (expect a decline, then a CLI
  commit); an amend (expect a decline); a commit while a merge is in progress (expect a decline); two sessions committing
  within the same second if a peer session is available.
- Outcome classes reported, one per entry: `ok`, `unchecked:<why>`, `warn:reflog-expired`, `warn:branch-gone`,
  `fail:missing`, `fail:parent-mismatch`, `fail:tree-outside-listed-paths`, `fail:duplicate`, `fail:fsck`; and per
  decline reason class a count. Then a table: each criterion D.1-D.5, measured value, pass/fail.
- Done when: the table is in this plan and the criteria are judged against it. No FAIL is the bar to proceed; any
  FAIL stops Steps 19 until fixed. (The owner has accepted the criteria; the go to flip is the table passing.)

### Step 15 (D) - pin the route off in `GitTestRepo.InitRepo`
- Files: `RoslynSentinel.Tests.Support.Basic/GitTestRepo.cs` (`InitRepo` `:33-37`),
  `RoslynSentinel.Tests.Tools.Basic/GitInProcessCommitRouteTests.cs`
- Depends on: 7 (same file)
- Effect: text-neutral (tests only; production default still off)
- Fact: `InitRepo` writes no `roslynsentinel.inProcessCommit` key today, so nothing is pinned yet. Its callers are not
  inventoried here; Step 16 and the ratchet in Step 18 find the ones that do not use it.
- Change: `InitRepo` writes `roslynsentinel.inProcessCommit=false` after `git init`. In the route fixture,
  `ConfigAbsent_TheRouteIsNotTaken...` (`:560`) must `git config --unset` the key to keep testing "absent"; `NewRepo`
  (`:84`) already sets `true` after `InitRepo` for the in-process twins.
- Done when: Build 0 errors; the route fixture and every fixture that uses `InitRepo` pass unchanged.
- Size: D1 (2 files, about 4 edits).

### Step 16 (D) - pin the commit-through-`GitImpl` fixtures explicitly
- Files: the fixtures that commit through `GitImpl`/`GitTool` without `GitTestRepo.InitRepo`. Reviewer-verified
  inventory (2026-11-12): `.CommitAsync(` has 53 hits in 4 test files, including `GitCommitReadsParityTests.cs:547`,
  `GitCommitSeamParityTests.cs`, `GitInProcessCommitRouteTests.cs` and `GitCommitUntrackedParityTests.cs:213,279,281`;
  22 more commits go through `GitTool(GitOperation.commit)` in `GitToolsSmokeTests` (19), `GitCommitHashRaceTests`,
  `GitCommitTrailerTests` and `GitRevertTrailerTests`. The Reads and Seam fixtures use their own `NewRepo` helpers and
  are pinned there (technical default). The seven or so files are the sweep.
- Depends on: 15; 7 (same Seam and Reads files)
- Effect: text-neutral (tests only)
- Change: one operation over the fixtures: write `roslynsentinel.inProcessCommit=false` in each fixture's repo setup
  (explicit, even where `InitRepo` already does, for the helpers that bypass it). The route fixture is excluded.
- Done when: Build 0 errors; the full Tools.Basic and Tests.Basic runs are green with the production default
  unchanged (proof the pin is harmless).
- Size: S0 (one operation, at most 8 files, about 25 sites).

### Step 17 (D) - spawn-count test: a pinned-false copy and an in-process twin
- Files: `RoslynSentinel.Tests.Tools.Basic/GitCommitSeamParityTests.cs`
- Depends on: 16
- Effect: text-neutral (tests only)
- Why: `Commit_StartsOnlyGitCommitItself_NoOtherGitProcessAsync` (`:1180`; asserts at about `:1182-1184`) asserts a
  commit spawns exactly `git commit`. After the flip it would fail loudly (zero spawns), not silently, so it needs
  a deliberate form rather than a repair after the fact.
- Change: keep the test with the route pinned off (the CLI-route claim), and add the twin with the route on that
  asserts no git process at all (the pattern of `InProcessCommit_StartsNoGitProcessAtAllAsync`,
  `GitInProcessCommitRouteTests.cs:375`).
- Done when: Build 0 errors; both tests pass with suite full.
- Size: D1 (1 file).

### Step 18 (D) - ratchet: a commit-through-`GitImpl` test must pin the route
- Files: `RoslynSentinel.Tests.Tools.Basic/GitCommitRoutePinRatchetTests.cs` (new)
- Depends on: 16
- Effect: text-neutral (tests only)
- Change: a source-scan ratchet in the style of `BareFileWriteRatchetTests`: every test source in
  `Tests.Tools.Basic` (and any other project that calls them) that contains `.CommitAsync(` or
  `GitOperation.commit` must also contain `inProcessCommit` / `InProcessCommitRoute.ConfigKey` or call
  `GitTestRepo.InitRepo`, or be on a short, commented allowlist (the route fixtures). Known limit, stated in the
  test: it is a text heuristic (a file that calls `InitRepo` for one repo and `git init` for another passes).
- Done when: Build 0 errors; the ratchet passes; removing the pin from one fixture in a local experiment fails it.
- Size: D1 (1 new file).

### Step 19 (D) - flip the default (behaviour-changing)
- Files: `RoslynSentinel.Common/InProcessCommitRoute.cs` (`IsEnabled` `:55-67`, class summary `:7`),
  `RoslynSentinel.Tools.Basic/GitImpl.cs` (comment at `:609`, `TryCommitInProcessAsync` summary), the
  route test file (`:560`)
- Depends on: 9 (text read), 14 passing, 15-18
- Effect: behaviour-changing: every repository the server commits in (including PlanStepRunner clones and
  third-party repos) now commits through the route unless `roslynsentinel.inProcessCommit` is false or not a boolean;
  a declined commit behaves as before. `Git(commit)` text is the same for a taken commit (hash, message,
  `RemainingStaged` parity is pinned by `RunScenarioAsync`).
- Change: `DefaultEnabled = true` constant; `IsEnabled` reads the key with `GetConfigValue` and applies the
  three-state rule in Decision (unset -> default, true-like -> on, anything else -> off, exception -> off). Rewrite
  the "default off" wording in the three comments. Rename and invert test `:560` to pin "key absent is on", and add
  tests that pin "explicit false is the CLI path and counts nothing" and "garbage value is the CLI path". Grep
  `docs/` for `roslynsentinel.inProcessCommit` and update every statement of the default.
- Done when: Build 0 errors; route fixture and the full Tools.Basic suite green; live check after restart:
  `McpServerStatus` `gitBackend` shows the counters rising on a commit in a repo with no key set, and a commit in a repo
  with `false` leaves them untouched.
- Size: D1 (3 files).

### Step 20 (E) - wire the config operation into the `GitRemote` tool (behaviour-changing)
- Step 20a - enums
  - Files: `RoslynSentinel.Common/ToolEnums.cs`
  - Depends on: 10b
  - Effect: behaviour-changing: the tool schema grows (a new operation value and action values), and an enum
    addition needs a server restart.
  - Change: `GitRemoteOperation.config`, appended last (as `ChangeSignatureOldSignature.auto` was, to keep earlier
    numeric values); `GitAction.get`, `set`, `unset` appended last. Update the `GitAction` summary
    (`ToolEnums.cs:395-398`, "Required for tag, stash, worktree ... Each member lists the operation(s) that use it") and give each new
    member its own doc line. Lands in one commit with 20b.
  - Size: D1 (1 file).
- Step 20b - tool wiring
  - Files: `RoslynSentinel.Tools.Basic/GitRemoteTools.cs`, `RoslynSentinel.Server.Advanced/ToolArgumentValidator.cs`
    (only if the `GitRemote` entry in `ParameterAliases`, `:607`, has aliases for analogous parameters)
  - Depends on: 20a
  - Effect: behaviour-changing (see 20a)
  - Change: parameters `configKey`, `configValue` with `CONDITIONAL-PARAM-REVIEW-REQUIRED` comments; the action
    guard (`GitRemoteTools.cs:93`: `action` is refused unless the operation is tag/stash/worktree: add config);
    the comment above `action` (`:41`) and its description; `isReadOnlyOperation` (`:77`) must also be true for
    `config` with `action == get`, so `repoPath` is allowed for it; the `repoPath` description (`:52`, "list actions
    only") must say `get` too; a switch arm beside `:120-128`; the operation's text in the `[Description]` (and the
    `Requirements` line); one phrase in the `Git` tool description (`GitTools.cs:47`) pointing at `GitRemote`
    config. Then run `scripts/Measure-ToolSchemaCost.ps1` before and after. Its default `-Mode claude-lean` does not
    emit `GitRemote` (it is on demand, `ToolsetCatalog.cs:69`, toolset `git`), so use a mode that registers it:
    `-Mode claude` (the `ToolsetCatalog` comment says on-demand tools remain registered in `claude` and the other
    modes; confirm `GitRemote` appears in the output, else pick the mode that does). Put the delta in this plan.
    Regenerate `docs/generated/` if its script covers tool descriptions (unverified).
  - Done when: Build 0 errors; live after restart: reading `roslynsentinel.inProcessCommit` from this repo and from a
    linked worktree works, and setting it back works.
  - Size: D1 (2 files).
- Step 20c - tool-level tests
  - Files: new test file in `Tests.Tools.Basic`
  - Depends on: 20b
  - Done when: tests pass for get, set, unset, a refused key, `repoPath` accepted for get and refused for set, and
    the existing `GitRemote` operations are unchanged.
  - Size: D1 (test-writer).

### Step 21 (F) - route patch text for the parity scenarios (only if Step 12 passed)
- Files: `RoslynSentinel.Tools.Basic/GitImpl.cs` (`DiffAsync` `:2550`, `ShowAsync` `:2645`),
  `RoslynSentinel.Tests.Tools.Basic/GitDiffShowToolParityTests.cs`
- Depends on: 13a, 13b (not in parallel with another `GitImpl.cs` step)
- Effect: behaviour-changing by routing only: for staged and `refA..refB` patches (and `show` without `stat`) the text is
  produced by libgit2 instead of git. For every routed scenario the returned text and `Warning` must be
  identical to the CLI's; a difference is a defect, not an accepted change. Stat and working-tree targets stay CLI.
- Change: in `DiffAsync` use the seam when `!nameOnly && !stat && IsSeamDiffTarget(target)`, run the result through
  the same `RejoinLines` (`GitImpl.cs:1727`) before `CapToUtf8Bytes`/`CountChangedFiles`/`DetectDecodeCorruption`,
  and fall through to the existing `RunGitAsync` call on a null answer (git's own error text stays what the caller
  sees, as for name-only); the same in `ShowAsync` where it builds `diffArgs`. Two tests pin the old routing and
  must change: `Diff_StatAndPatch_NeverReachTheReaderAsync` (`GitDiffShowToolParityTests.cs:1029`) and
  `Show_StatAndPatch_ReachTheCliAndNeverTheNameStatusReadAsync` (`:1004`): split each into "stat never reaches the
  reader" (unchanged) and "patch is served by libgit2 in the normal environment" (the pattern of
  `DiffAndShowNameOnly_InTheNormalEnvironment_AreServedByLibgit2NotTheFallbackAsync`, about `:1051`).
  `DiffWorkingTreeTargets_StayWithGit_NeverReachTheReaderAsync` (`:329`) stays as is.
- Done when: Build 0 errors; the Step 11 corpus, run at tool level against both backends, is green for every routed
  scenario; the changed pinned tests pass; `DiffNameOnly_ThroughTheProductionDefaultReader_CountsALibgit2CallAsync`
  (about `:1080`) still passes.
- Size: D1 (2 files).

### Step 22 (G) - tag list in process: SKIPPED-UNLESS a caller shows real cost
- Trigger to un-skip (planner's wording, numbers open): `scripts/Get-TelemetryRollup.ps1` shows `GitRemote` tag list
  called repeatedly in one session, or with a CLI time over about 100 ms.
- Files: reader interface files (`GitReader.cs`, `CliGitReader.cs`, `FallbackGitReader.cs`), a new libgit2 partial,
  `GitImpl.cs` (`TagListAsync` `:696`), a parity test: split like Step 13 into a D1 pair plus a routing slice.
- Depends on: 13 (reader files), 21 (`GitImpl.cs`)
- Effect: behaviour-changing by routing only (identical `GitTagEntry` lists or decline)
- Candidate decline rules (HYPOTHESIS, reconstructed from the CLI format at `GitImpl.cs:696-728`, not recorded in
  any source note): `--sort=-creatordate` (tagger date for annotated, committer date for lightweight, tie order);
  `tag.sort`/`versionsort` config; `%(contents:subject)` (a lightweight tag shows the commit's subject, an
  annotated tag's first paragraph joined by spaces, a signed tag's block excluded); `%(*objectname:short)`
  peels one level only (tag of tag, tag of tree or blob); both short names depend on the repository's abbreviation
  length (the cached probe exists, `Libgit2GitReader.cs:~340`); `%(refname:short)` becomes `tags/x` when a branch has the
  same name; a ref that is symbolic or packed.
- Done when: parity scenarios for lightweight, annotated, signed (a hand-built tag object if gpg is not available),
  tag-of-tag, same-name branch, and `tag.sort` set; outcome classes `parity`, `declined:<rule>`.

### Step 23 (G) - worktree list in process: SKIPPED-UNLESS a caller shows real cost (last)
- Trigger and files as Step 22, for `ListWorktreesAsync` (`GitImpl.cs:1282`); depends on 22.
- Effect: behaviour-changing by routing only
- Why last: `ListWorktreesAsync` has five callers, three of them inside the mutating `add` and `remove`
  flows (`:1195` list, `:1426`/`:1447` add, `:1489`/`:1550` remove), where the result is compared with git's
  own state around a CLI mutation. A subtly different list breaks a safety check there, not just a display.
- Candidate decline rules (HYPOTHESIS, not recorded): `prunable` (the worktree's gitdir target is missing, plus a
  reason) and `locked` with its reason (the clone's `Worktree` exposes `Name`, `WorktreeRepository`, `IsLocked`,
  `LockReason`, no prunable flag); the main worktree is not in `Repository.Worktrees` and must be derived from the
  common directory (and is different for a bare repository, where git prints `bare`); detached vs branch
  fields; `HEAD <full sha>` for an unborn branch; path normalisation (`NormalizeWorktreePath`, Windows 8.3 names,
  `GitImpl.cs:~1260`); `extensions.relativeWorktrees` (libgit2 1.8.6 rejects the repository, so the read already
  falls back, proposal Phase I); opening each worktree's `Repository` costs handles that Windows holds.
- Done when: parity scenarios for main only, one linked, locked with reason, prunable (directory deleted by hand),
  detached, branch, in-linked-worktree invocation, 8.3 path; `add`/`remove` fixtures still green.

## Out of scope

- Amend, merge conclusion, signing, blocking hooks, push/fetch/pull, revert, stash apply/pop, worktree add/remove,
  hunk staging: unchanged, on the CLI (proposal, "Stays on the CLI").
- Working-tree diffs and `--stat` text in process (`core.autocrlf` EOL semantics and stat graph widths; Step 21
  does not widen them).
- Log rotation or compression of `commit-route.jsonl`; logging of the CLI fallback commit's result; logging of
  `scope`; a counter for failed log appends (open decisions A1-A3 below, none planned).
- `config --global/--system` writes and `git config --list` (owner decision E2/E4).
- Promoting `reference-transaction` or `post-index-change` hook support, or `gc --auto` after an in-process commit
  (owner decision 2026-11-12: accepted as is).

## Risks and open decisions

Open items carry options and a recommendation (the planner's, not a decision). Items resolved by the owner on
2026-11-12 are marked RESOLVED and point at the Decision section.

**A. Instrumentation** (open)
- A1. Should the CLI fallback's result be logged after a decline (a second line `cli-committed` with the hash)?
  Options: (a) no, as planned; (b) yes. Recommendation: (a). The audit only needs taken commits, and a second line
  doubles the log for no check the criteria use; revisit if a "declined and then nothing was committed" question comes up.
- A2. Count failed log appends (`AppendOutcome` swallows every exception: the `catch` at `InProcessCommitRoute.cs:221`)?
  Options: (a) leave; (b) a counter beside `DeclineCount` in the `gitBackend` block. Recommendation: (b) only if a
  Step 14 audit ever shows a gap between commits and lines; otherwise (a). Not planned. Any change must keep one
  write in `AppendOutcome` (ratchet).
- A3. Log growth: no rotation; each line is about 250 bytes plus up to 20 paths of 200 characters. Recommendation:
  leave; `-Since` in the script bounds reads.
- A4. Listed paths are logged in clear under `.git/roslynsentinel/`: local, not committed, same sensitivity as the
  reflog. Recommendation: accept.
- A5. The double-commit hypothesis (Problem): closed structurally by Step 2 and pinned by Step 3(e); it stays a
  hypothesis until Step 3(e) is seen to fail without the guard.

**B. Audit script** (open)
- B1. A `Test-CommitRouteLog.Tests.ps1` self-test like `Get-ScenarioSummary.Tests.ps1`? Options: (a) hand-built log
  acceptance in Step 6 only; (b) a test file. Recommendation: (b), because the false positives (pruned commit,
  expired reflog) are exactly what a fixture pins.
- B2. A "missing" commit can be legitimate after `git reset` plus gc (unreachable commits expire after about 2
  weeks). The script reports it as FAIL with a "check reflog/gc" note; whether a missing commit older than 14 days
  is WARN is open. Recommendation: WARN beyond 14 days.

**C. Second setup** (planner recommendation adopted, owner not asked)
- C1. Promote `AddLinkedWorktree` instead of a third copy: adopted as Step 7.

**D. Default-on**
- D1-D3. RESOLVED 2026-11-12 (owner): criteria accepted, flip applies to all repos, invalid value counts as off.
- D4. Cross-plan baseline dependency (open): Step 19 changes the commit path every other plan's runs use. Do it when no
  other plan holds a commit-timing A/B baseline (none is known to me; ask the peer sessions).
- D5. With the three-state read, `GetConfigValue` returns null for unset and also for a CLI exit 128 (not a
  repository). Such a repo would read "unset" and turn the route on, after which the route's own declines decide
  (a non-repository declines at the first reader call). Accepted; revisit if the log ever shows it.

**E. Config operation**
- E1, E2, E3, E4. RESOLVED 2026-11-12 (owner): `GitRemoteOperation.config`; local scope; allowlist and deny-list as
  listed in Decision; CLI write, in-process read. Reasoning kept for the record: the `Git` description already says
  branch, checkout, push, fetch, pull, tag, stash and worktree "are the GitRemote tool" and `GitRemoteOperation`'s
  doc comment says it exists "so the always-loaded Git schema stays small"; libgit2 `Configuration.Set` would, in a
  linked worktree, write the per-worktree directory's config (`Libgit2GitReader.cs:317-322`), a file git ignores, and
  report success; many keys execute programs (`alias.*` with `!`, `core.hooksPath`, `core.fsmonitor`,
  `core.sshCommand`, `core.editor`, `diff.external`, `filter.*`, `credential.helper`, `url.*.insteadOf`).
- E5, E6, E7. Implementer guidance (owner decision): E5 refused-key error wording is facts and options only, drafted
  against `reference_tool_error_text_facts_options_policy.md`; E6 result type reuse-first via the `dto-conventions`
  skill, no new type until the existing ones are checked; E7 store the value as given and report what git reads back.
- E8 (open, new). `ToolArgumentValidator.cs:607` holds a `GitRemote` entry in `ParameterAliases`: Step 20b checks
  whether analogous parameters have aliases and follows suit. Unverified which.

**F. Patch text**
- F1, F2. RESOLVED 2026-11-12 (owner): route only byte-identical (tool-visible) scenarios; measure first; stop after
  Step 11 (gate: Step 12) if the saving is under about 30 ms per call.
- F3. `show` patch uses `diff <rev>^ <rev>` (or the empty tree) after a spawned `rev-parse` (`ShowAsync`); the routed
  path replaces both. A merge commit is diffed against its first parent only, as today.
- F4. Test-coverage caveat (the planner owns what must be covered): the corpus proves parity only for the scenarios it
  builds; repositories with attributes, `diff.*` keys, `core.whitespace`, textconv or `diff=` drivers are covered
  only by Step 11's decline scenarios, so each such key the corpus does not name will be served from libgit2 with
  git's setting ignored. Reviewer to confirm scenario 14's key list against `git help config` before Step 21.

**G. Lists**
- G1. RESOLVED 2026-11-12 (owner): Steps 22-23 are SKIPPED-UNLESS a caller shows real cost. The reason the proposal
  gives ("too many decline rules") is not recorded in source; the rule lists in Steps 22-23 are reconstructed and
  must be confirmed by a parity corpus before anyone relies on them.

**Contradictions between the proposal or brief and the source**
- The route class is `RoslynSentinel.Common/InProcessCommitRoute.cs`, not `Tools.Basic`; the counters and log writer
  are in Common, the only Tools.Basic piece is `GitImpl.TryCommitInProcessAsync` (`:3473`), called from
  `CommitAsync` (`:3328`).
- The brief expected `Git` tool operations to be declared in a Tools.Basic enum; `GitOperation` and `GitRemoteOperation` are in
  `Common/ToolEnums.cs`, and branch/tag/stash/worktree are on `GitRemote` by design (now settled for config).
- The proposal's "(too many decline rules)" for tag and worktree lists has no recorded evidence in source, tests or
  docs. "(not byte-identical)" for patch text rests on a `DiffAsync` comment that lists reasons; nothing measures
  them. Both are treated as hypotheses here.
- `GitRemoteTools` builds its own `GitImpl` with `GitReaders.Default` and the shared route
  (`GitRemoteTools.cs:23`, constructor chain `GitImpl.cs:612-627`; the three-argument path falls back to
  `InProcessCommitRoute.Shared` at `GitImpl.cs:627`), so a config operation there uses the same reader as the `Git`
  tool.
- Every decline of an enabled route is already logged; only the log's context and an end-to-end test were missing
  (table in Problem). The brief's worry that early declines might bypass the log did not hold for any decline found.
- `GitTestRepo.InitRepo` (`:33-37`) writes no route key today; an earlier draft of this plan implied a pin existed.

**Other risks**
- `GitImpl.cs` and `Libgit2GitReader.cs` are being edited by other sessions (line numbers moved several times while
  this plan was drafted). Re-read the symbol before each edit; `HOLD`/`RELEASE` peer requests may apply.
- Steps 9, 13, 22 and 23 all touch the reader interface files: never schedule two of them together.
- This plan advances the TODO.md item "LibGit2Sharp in-process git: trial review and next stages (2026-11-12)"
  (`docs/current/TODO.md:15`). When the last non-optional step closes the plan, move that entry to `CLOSED.md`; the
  entry stays open while Steps 22-23 are SKIPPED-UNLESS only if the owner wants them tracked there.
