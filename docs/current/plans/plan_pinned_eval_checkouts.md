# Plan: evalqueue runs each job from a pinned commit in its own checkout outside the repo

**Status:** APPROVED 2026-11-19. Queue jobs pin a commit at submit and build and run from a shared clone under C:\rs-eval, with results written centrally; implementation waits for the held modeleval/queue-test edits to be committed.

## Problem

An evalqueue job builds and runs from the dev working tree. `Invoke-JobRun` starts `modeleval.ps1` with
`WorkingDirectory = $Job.repoRoot` (`scripts/roslynsentinel-evalqueue.ps1:738`), and `modeleval.ps1` resolves
everything from its own location (`$repoRoot = Split-Path $PSScriptRoot -Parent`, `roslynsentinel-modeleval.ps1:397`;
`dotnet test` at 778-781). To keep runs honest the worker re-fingerprints the tree at dispatch
(`Get-TreeFingerprint`, 267-286, checked at 851-871) and refuses a changed tree with state `TreeChanged`.
So every edit or commit in the repo between submit and dispatch costs the queued runs, and an edit
during a run's build changes what it measures. Sessions working in the repo and queued evals block each
other. Manual workarounds exist (`C:\tmp\rs-evaltree-*`,
`rs_eval_a918`; `docs/current/evals/eval_tenchain_reasoning_replay_2026_11_10.md:14`), with results
stranded in each worktree's own `ModelTestingResults`.

Prior design: [proposal_peer_session_conflict_resolution.md](../proposals/proposal_peer_session_conflict_resolution.md)
lines 89-93 and 111-138 ("at dispatch the worker makes (or reuses) a worktree at the job's commit ...
This replaces `TreeChanged` and `-OnTreeChange RunAnyway`").

## Decision

- **Pin at submit.** `submit` resolves `-Commit <rev>` (default `HEAD` of the dev repo) to a full SHA and
  stores it on the job. A dirty tree (the existing fingerprint pathspecs, line 135) with no explicit
  `-Commit` is refused with a message naming the two options (commit first, or pass `-Commit HEAD` to test
  the committed tree). Uncommitted changes are not testable through the queue.
- **One checkout per SHA, outside the repo**, created at submit (so a bad SHA fails the submitter, not
  the worker): `git clone --shared --no-checkout <devRepo> <root>\<sha12>.<token>`, then
  `git checkout --detach <sha>`, write a `complete` marker, rename-publish to `<root>\<sha12>` (first
  rename wins, as the launcher's `Invoke-ProdBuild`, `scripts/roslynsentinel-mcp-launch.ps1:640-706`).
  `--shared` is cheap (alternates to the dev repo objects), writes nothing into the dev repo's `.git`
  (no `worktree add`, so a PlanStepRunner `worktree prune` cannot touch it), and gives a real `.git`
  directory, so `ModelEvalAssemblySetUp.ReadWorkingTreeStamp` (libgit2, `ModelEvalAssemblySetUp.cs:125-169`)
  stamps the exact SHA.
- **The job runs the checkout's own scripts.** `job.repoRoot` = the checkout; `job.evalScript` = the
  checkout path plus the submitted `-EvalScript` path relative to the dev repo root (an absolute
  `-EvalScript` outside the repo is refused). Build artifacts stay under the checkout
  (`_scratchbuild_<suffix>`), which the `.slnx` walk-ups require (`ModelTestingResultsArchiver.cs:133-148`,
  `TestSolutionFixture.cs:225-241`, `ResumeScenarios.cs:93-102`).
- **Results stay central.** A new `ROSLYNSENTINEL_RESULTS_ROOT` (absolute) redirects every writer and
  reader of `ModelTestingResults`; the worker sets it to `<devRepo>\ModelTestingResults` for each pinned
  job, so existing readers (`scripts/Archive-ModelTestingResults.ps1:31`, `Get-ScenarioSummary.ps1`,
  `Parse-AgentLog.ps1`, docs) keep working and pruning a checkout loses nothing.
- **Cold build outside the time limit.** Before starting the clock, the worker pre-builds the eval
  project into the same artifacts path `modeleval.ps1` uses; a failure ends the job as `BuildFailed`.
- **TreeChanged retires migration-first.** Jobs without a pinned commit (already on disk) keep the old
  fingerprint path until the queue drains; `-OnTreeChange` is accepted with a deprecation warning, then
  removed in a later cleanup (left as a `# TODO`).
- **Retention.** After each job the worker sweeps checkouts not referenced by a Queued, Building or
  Running job on any host, older than the minimum age, keeping the newest N.

### Settled 2026-11-19 (owner, after the source review)

- **D1 checkout root: `C:\rs-eval`.** Measured run paths are about 203-228 chars there versus 264-289
  under `%LOCALAPPDATA%\RoslynSentinel\evalqueue\checkouts` (over MAX_PATH). Override:
  `ROSLYNSENTINEL_EVALQUEUE_CHECKOUTS_ROOT` (the `ROSLYNSENTINEL_EVALQUEUE_` prefix is never captured into
  a job, `evalqueue.ps1:436-438`). `Get-EvalCheckoutRoot` warns when the root is longer than 16 chars.
  The worker uses `job.checkoutRoot`, never its own environment (a detached worker inherits whichever
  session started it).
- **Pre-build: new `Building` state.** The worker flips Queued -> Building inside the lock, releases it,
  pre-builds outside the lock (a cold build cannot hold the 60 s queue mutex, `evalqueue.ps1:212-214`),
  then re-takes the lock and flips Building -> Running (or Cancelled if a cancel arrived, or
  `BuildFailed`). Status, `Get-QueueSchedule`, `Repair-OrphanedJobs` (324) and cancel (650) learn
  `Building`. The time limit starts at Running. Test seam: `-PreBuildScript` (default: the inline
  `dotnet build`), decision D3.
- **D4 dirty submit: refuse and list the first few dirty paths** (so a submitter can tell their edits
  from another session's), with the two options. Dirty `scripts/*.ps1` files (outside the fingerprint
  pathspecs) produce a warning naming them, not a refusal: the job runs the committed scripts.
- **D2 retention:** minimum age 24 h, keep newest 5; revisit with the Step 6 BUILD-TIME numbers. The
  sweep's keep-list is the SHAs of Queued/Building/Running jobs across all host folders (checkouts are
  global, queue locks are per host).
- **Defaults adopted from the review (no owner question needed):** `BuildFailed` joins `$terminalStates`
  (`evalqueue.ps1:126`) and the help header's state list (40-44); pinned jobs set `fingerprint` and
  `testedFingerprint` to the pinned SHA so dedup (456-457) matches; `-EvalArgs` and the `-Test` alias are
  validated against the checkout's script after the checkout exists, not the dev repo's (421-429);
  `ROSLYNSENTINEL_RESULTS_ROOT` joins `$envNeverCaptured` (132); the pre-build sets
  `MSBUILDDISABLENODEREUSE=1` (as `modeleval.ps1:565`); `modeleval.ps1` validates the results root is
  rooted before the run (the archivers' own catch, `ModelTestingResultsArchiver.cs:56-59`, would swallow
  a throw); the old Steps 3 and 4 merge into one commit (Step 3 alone would strand results in
  checkouts); the old Step 5 splits into checkout-function tests (Step 3) and submit/worker tests (Step 5).
- **Prerequisite (met: a0d4b1e9, queues idle 2026-11-19):** roslynsentinel-b2 held uncommitted edits to `scripts/roslynsentinel-modeleval.ps1`
  (archive step: `$modelEvalRoot`, `$resultsRoot`, `<TestName>(case)` folders) and
  `scripts/roslynsentinel-evalqueue.Tests.ps1` (AST-lifted archive cases, 222-247), under HOLD until their
  Ornith queue drains and they commit. Steps 1, 3, 4 and 5 start after that commit. Step 2 (a new file)
  may be written earlier but not committed: the fingerprint includes `HEAD` (`evalqueue.ps1:270-284`), so
  any commit, even docs-only, ends every queued unpinned job `TreeChanged`.
- **Owner present during the run:** mid-run decisions are asked with a recommendation.

## Execution rules

- All steps are PowerShell except Step 1's two archiver edits (C#, MCP edit tools). Script steps are
  verified with `pwsh -NoProfile -File scripts/roslynsentinel-evalqueue.Tests.ps1` (exit 0); C# with
  `Build` 0 errors.
- Keep `Get-RunLeafDirs` and `Copy-NewRunDirectories` in `modeleval.ps1` under those names, and the
  script variables `$modelEvalRoot`, `$resultsRoot`, `$resultsDir`, `$testName`: the queue tests lift the
  functions by AST and set those variables (`roslynsentinel-evalqueue.Tests.ps1:225-247`).
- Do not edit `modeleval.ps1`, the queue or the harness, and do not commit anything (the fingerprint
  includes `HEAD`), while a queue job is Queued or Running (check `evalqueue status`); land each step
  between runs. After Step 4 lands, this
  rule is no longer needed for pinned jobs.
- `scripts/roslynsentinel-instances-common.ps1` must stay PowerShell 5.1 compatible and silent on
  stdout; reuse its helpers (`Test-PathUnderRoot` 74, `New-RoslynSentinelToken` 66,
  `Invoke-NativeCaptured` 85) by dot-sourcing, do not change them (the open
  `plan_prebuild_and_idle_stale_server_restart.md` also edits that file).

## Steps

### Step 1 - Results-root override
- Files: `scripts/roslynsentinel-modeleval.ps1`, `RoslynSentinel.Tests.ModelEval/ModelTestingResultsArchiver.cs`,
  `RoslynSentinel.Tests.ErrorVariants/ModelTestingResultsArchiver.cs`
- Depends on: the prerequisite commit (roslynsentinel-b2)
- Effect: text-neutral when `ROSLYNSENTINEL_RESULTS_ROOT` is unset.
- Change: `modeleval.ps1:707` - `$resultsRoot` = `$env:ROSLYNSENTINEL_RESULTS_ROOT\<suffix>` when set (must be
  rooted, else throw before the run starts), else today's value; same for the `-Clean` destination (528).
  The `ROSLYNSENTINEL_*` clear/set block (561-685) clears only named variables; leave the new one alone.
  Both archivers (`:47` and `:44`): use the variable as the base instead of
  `<FindRepoRoot()>\ModelTestingResults` when set and rooted, else today's value. The ErrorVariants copy is a
  deliberate duplicate (TODO at its line 9) - edit both the same way. `roslynsentinel-errorvariants.ps1`
  (140, 186-189) does not run through the queue: leave a `# TODO honour ROSLYNSENTINEL_RESULTS_ROOT` there.
- Done when: `Build` 0 errors; `pwsh -NoProfile -File scripts/roslynsentinel-evalqueue.Tests.ps1` exits 0
  (the archive cases still pass); a manual `modeleval.ps1` run is not required (Step 6 covers it).
- Lane: `implementer` (3 files, about 5 edits).

### Step 2 - Checkout helper script
- Files: `scripts/roslynsentinel-evalcheckout.ps1` (new)
- Depends on: none (pairs with Step 1)
- Effect: text-neutral (no callers yet).
- Change: dot-sourceable functions, no top-level side effects, in this order:
  - `Get-EvalCheckoutRoot` -> `$env:ROSLYNSENTINEL_EVALQUEUE_CHECKOUTS_ROOT`, else `C:\rs-eval`; warns when longer than 16 chars.
  - `Resolve-EvalCommit -RepoRoot <string> -Rev <string>` -> full SHA (`git rev-parse --verify <rev>^{commit}`); throws with the rev and repo on failure.
  - `Get-EvalCheckout -RepoRoot <string> -Sha <string> -CheckoutRoot <string>` -> checkout path; fast path when `<root>\<sha12>\complete` exists (touch `lastused`); otherwise clone `--shared --no-checkout`, `checkout --detach`, write `complete` (`sha=<sha> createdUtc=...`), rename-publish, loser deletes its copy.
  - `Invoke-EvalCheckoutSweep -CheckoutRoot <string> -KeepShas <string[]> -MinimumAge <timespan> -KeepNewest <int>` -> removes unreferenced published checkouts past the age (by `lastused`), `*.<token>` partials older than the age; never a path outside the root (`Test-PathUnderRoot`).
- Done when: `pwsh -NoProfile -Command ". ./scripts/roslynsentinel-evalcheckout.ps1"` exits 0 with no output; Step 3 tests pass.
- Lane: `implementer-senior` (new script, concurrency-sensitive publish).

### Step 3 - Checkout-function tests and test-harness groundwork
- Files: `scripts/roslynsentinel-evalqueue.Tests.ps1`
- Depends on: 2, the prerequisite commit
- Effect: text-neutral.
- Change: point `ROSLYNSENTINEL_EVALQUEUE_CHECKOUTS_ROOT` at a temp folder. Commit `scripts\stub-eval.ps1`
  into the throwaway repo's first commit (today only `Lib.cs` is committed, 51-54, so a pinned checkout
  would lack the stub); make the stub write under `ROSLYNSENTINEL_RESULTS_ROOT` when set (today
  `$root\ModelTestingResults`, 45) and write the content of a tracked marker file into its result,
  compared with line endings normalized (global `core.autocrlf=true` on this box). Cases:
  `Resolve-EvalCommit` (valid rev, bad rev throws); `Get-EvalCheckout` (creates, second call reuses the
  same path, checkout is at the SHA); sweep keeps a listed SHA and a young one, removes an old unlisted
  one and an old partial. Keep the archive block (222-247) untouched.
- Done when: `pwsh -NoProfile -File scripts/roslynsentinel-evalqueue.Tests.ps1` exits 0.
- Lane: `test-writer` (PowerShell assert script, not NUnit; brief it with the file's `Assert-That` pattern).

### Step 4 - Submit pins the job, worker runs it (two slices, one commit)
- Files: `scripts/roslynsentinel-evalqueue.ps1`
- Depends on: 2, 3
- Effect: behaviour-changing: a dirty submit without `-Commit` is refused; jobs carry `pinnedCommit`;
  pinned jobs never end `TreeChanged`; new states `Building` and `BuildFailed`; results land under the dev repo.
- Slice 4a (submit): add `[string]$Commit` and `[string]$PreBuildScript` to the param block (97-122) and
  the help header (12-38, states at 40-44); add `ROSLYNSENTINEL_RESULTS_ROOT` to `$envNeverCaptured` (132);
  in `Invoke-Submit` (415) after the fingerprint (433): refuse dirty without `-Commit`, listing up to 5
  dirty paths; warn on dirty `scripts/*.ps1`; resolve the SHA; `Get-EvalCheckout`; compute the relative
  eval-script path; move the `-EvalArgs`/`-Test` validation (421-429) to run against the checkout's
  script. New job fields (record at 470-510): `pinnedCommit`, `sourceRepoRoot` (the dev repo),
  `checkoutRoot`; set `repoRoot` and `evalScript` to the checkout paths; `commit` = the pinned SHA;
  `fingerprint` = the SHA. Dedup key (450): replace the lower-cased `RepoRoot` with `sourceRepoRoot`
  lower-cased plus `pinnedCommit`; dedup compares (456-457) work unchanged because both fingerprints are the
  SHA. `-OnTreeChange`: warn that it is ignored for pinned jobs.
- Slice 4b (worker): `$terminalStates` (126) gains `BuildFailed`. At dispatch (851-871), for a job with
  `pinnedCommit`: skip the fingerprint refusal; set `testedCommit` = `pinnedCommit`, `testedFingerprint` =
  the SHA, `testedDirty` = false; flip to `Building` and release the lock; verify the checkout's `complete`
  marker (recreate via `Get-EvalCheckout` if swept); pre-build with `-PreBuildScript` or the default
  `dotnet build RoslynSentinel.Tests.ModelEval.csproj -c Debug --artifacts-path <checkout>\_scratchbuild_<suffix>`
  with `MSBUILDDISABLENODEREUSE=1` (comment: must match `modeleval.ps1:513`); re-take the lock; a cancel
  that arrived wins (Cancelled); non-zero exit -> `BuildFailed` with the log tail in `reason`; else
  Running and `startedUtc` (the time limit starts here). Status (577-580), `Get-QueueSchedule` and
  `Repair-OrphanedJobs` (324) treat `Building` as active; cancel (650) accepts it. `Invoke-JobRun`
  (730-742): set `ROSLYNSENTINEL_RESULTS_ROOT` = `<sourceRepoRoot>\ModelTestingResults` after line 741
  (beside `EVALQUEUE_JOB`), so it wins over the submitter's variables. `Get-RunFolders` (699-706): read
  `<sourceRepoRoot>\ModelTestingResults\<suffix>` for pinned jobs, `<repoRoot>\ModelTestingResults`
  otherwise. `Wait-ForTesthostExit` (708-721) keeps `job.repoRoot` (the checkout). Jobs without
  `pinnedCommit` keep the old path; mark it `# TODO remove the unpinned path and -OnTreeChange once no
  unpinned job remains`. After each job: `Invoke-EvalCheckoutSweep` on `job.checkoutRoot` with the SHAs of
  Queued/Building/Running jobs across all host folders. Note in a comment that `durationSeconds`/ETA
  exclude the cold build.
- Done when: both slices are in, the existing tests pass, and the Step 5 cases pass; committed together.
- Lane: `implementer-senior` (900-line script, queue lock semantics, 8+ touch points).

### Step 5 - Submit and worker tests
- Files: `scripts/roslynsentinel-evalqueue.Tests.ps1`
- Depends on: 4
- Effect: text-neutral.
- Change: replace the TreeChanged section (204-220) with: (a) submit on a clean tree pins HEAD, then
  commit a change before the worker runs - the result shows the old marker and the job is not
  `TreeChanged`; (b) dirty submit without `-Commit` is refused and the message names the dirty path, with
  `-Commit HEAD` accepted; (c) two submits of the same SHA share one checkout, and an identical second
  submit attaches to the first job (dedup); (d) results land under `ROSLYNSENTINEL_RESULTS_ROOT`, not the
  checkout; (e) sweep keeps a SHA a Queued job on another host folder references; (f) a legacy job file
  without `pinnedCommit` still runs and can still end `TreeChanged`; (g) a `-PreBuildScript` that exits 1
  ends `BuildFailed` and `wait` returns; (h) a cancel during `Building` ends Cancelled.
- Done when: `pwsh -NoProfile -File scripts/roslynsentinel-evalqueue.Tests.ps1` exits 0.
- Lane: `test-writer`.

### Step 6 - Live check
- Files: none
- Depends on: 1, 4, 5 committed
- Effect: none (verification).
- Change: submit one short real job (`-Repeats 1`, the cheapest scenario the owner names) on an idle host;
  while it is queued or running, commit an unrelated doc change in the dev repo.
- Done when, outcome classes: **PINNED-OK** - job `Finished` (not `TreeChanged`), the run folder
  under `<devRepo>\ModelTestingResults\<suffix>`, its transcript `CommitHash` (and the `commit` column for a
  scenario `results.csv`) equals `pinnedCommit` and `Dirty` is false; the job log shows `dotnet test` did
  not recompile; **STAMP-NULL** - ran but the stamp is empty or dirty (libgit2 on the clone or autocrlf,
  follow up); **RESULTS-STRANDED** - results only under the checkout (Step 1/4 wiring); **BUILD-TIME** -
  record the cold pre-build duration and checkout disk size for D2.

### Step 7 - Docs
- Files: `docs/current/references/reference_model_eval_procedure.md` (195-228),
  `scripts/roslynsentinel-evalqueue.ps1` help header, the proposal's Status line (Part 3 implemented by this plan),
  `docs/current/TODO.md`
- Depends on: 6
- Effect: text-neutral.
- Change: describe pinning, `-Commit`, the dirty refusal, `Building`/`BuildFailed`, the checkout root and
  sweep, `ROSLYNSENTINEL_RESULTS_ROOT`; rewrite the TreeChanged guidance (217-219, 223-224) as legacy-only;
  add a TODO.md line for the `-OnTreeChange` removal.
- Done when: no doc describes `TreeChanged` as the current behaviour for new jobs.
- Lane: main session or `implementer`.

## Out of scope

- Testing uncommitted changes through the queue (snapshot or patch files); commit first.
- A shared build cache across SHAs (breaks the `.slnx` walk-ups, see Decision).
- Adding the commit to the CSVs that lack it (`TranscriptResume`, `ShippingKindMappingProbeTests`,
  `ShippingKindReplayTests`, `SizeThreshold`); the transcript stamp already carries it.
- PlanStepRunner and `roslynsentinel-errorvariants.ps1` runs; they do not go through the queue.
- A `-NoBuild` flag on `modeleval.ps1` (proposal 136-138); revisit if Step 6 shows `dotnet test` rebuilding.

## Risks and open decisions

- **`--shared` and gc.** The checkout borrows the dev repo's objects; a `git gc --prune` in the dev repo
  could drop an unreferenced throwaway commit a checkout still uses. Low risk for commits on `master`;
  alternative is `git clone --local` (hard links, no alternates) at more disk.
- **Untracked inputs.** Named resume captures (`ResumeScenarios.cs:93-102`) must be committed;
  `-ResumeTranscript` paths are already required absolute (queue 369-375). Gitignored files an eval test
  needs at run time were not audited; a pinned checkout lacks them (Step 6 would show it).
- **libgit2 on a `--shared` clone** is expected to work (`Libgit2GitReader.cs:1445-1452`, detached HEAD via
  `Head.Tip`) but untested; Step 6 STAMP-NULL covers it.
- **Pre-build reuse** by `dotnet test` (no `--no-build`, `modeleval.ps1:778-781`) is a hypothesis; Step 6 checks.
- **Disk.** About 230-320 MB per build folder plus 35 MB tracked tree; keep newest 5 is about 1.7 GB.
- **Time limit.** `TimeLimitMinutes` (default 40) today covers build plus run; moving the build out shortens
  the effective limit for existing callers' expectations only in their favour.
