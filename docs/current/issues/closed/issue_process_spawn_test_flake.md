# Tests that spawn git fail intermittently on full worktree runs; GitProcess drops output on a late pipe EOF - RESOLVED

**Status:** IMPLEMENTED 2026-11-10. d0ceef51 (GitProcess dedicated reader threads + LastFailure pool counts), e3cdc6df..44a2944f (`Tests.Support.Basic/GitTestRepo.cs` replaces the 8 copies), 93160409..919cdccf and 8cfdb157 (known-flake class comments). Acceptance: 0 git-spawn failures in 5 full worktree runs. Item 4 (`ChildProcessCapture`) stays conditional on the new instrumentation recording starvation.

## Summary
Tests that spawn `git` fail intermittently, but only on full worktree runs
(`scripts/Invoke-WorktreeTests.ps1`). The failure text, read from `GitProcess.LastFailure`, is:

```
git failed to run: rev-parse HEAD|commit|add -A (stdout/stderr did not reach EOF within 2s after the process exited (a grandchild may hold the pipe))
```

Failing tests seen: `ServerBinaryStalenessGitTests.ShortStampedSha_Works`,
`ServerBinaryStalenessGitTests.LaterCommitTouchingOnlyDocs_IsNotStale`, and the `DocTrailer*` / `DocClose*`
tests. Every one passes when run alone (the class re-run alone passed 8/8; the 3 failures of the final
run passed 45/45 alone). Frequency: 3 of 7 full runs on 2026-11-10 (batches B, F and G in
`.claude/handoff/2026-11-10_todo_punchlist_91761378.progress.md`; B at line 68 and G at line 73 were read,
the F entry is on a long line that was not individually re-read).

The owner reports the same kind of flake in the `Build` / `RunTest` tests, which spawn `dotnet` through
`ChildProcessCapture` (ef4301b6, 4 s drain grace, async). That claim was not traced here; it is the owner's
observation and the basis for the comment-only decision below.

## Why it matters
- Full-run results are noisy: 3 of 7 runs on one day had a spurious red, so a real regression in the same
  classes would be dismissed as "the flake" (the failure-analysis rule is to treat any failure as new; this
  flake erodes that rule).
- `GitProcess` also has production callers that silently get `null` on this failure and degrade without
  an error: `ServerBinaryStaleness` (`RoslynSentinel.Common/ServerBinaryStaleness.cs:88,241,271`),
  `NotFoundEvidence` (`RoslynSentinel.Common/NotFoundEvidence.cs:334`), `DocTrailerEngine.ReadTrailerHits`
  (`RoslynSentinel.Engines.Basic/DocTrailerEngine.cs:920`), `ResidualMentionScanner`
  (`RoslynSentinel.Engines.Basic/ResidualMentionScanner.cs:235`) and `TestInstanceLauncher.ReadGitStamp`
  (`RoslynSentinel.Engines.Basic/TestInstanceLauncher.cs:679,686`). A starved read there means a wrong
  "not stale" / "no evidence" / "no trailer hits" answer, not a visible failure. The flake is therefore a
  production defect as well as a test defect.

## What is known
Verified against the code on 2026-11-10:

- `RoslynSentinel.Common/GitProcess.cs` `TryRun` is synchronous. It starts
  `process.StandardOutput.ReadToEndAsync()` and `StandardError.ReadToEndAsync()` (pool-backed tasks),
  calls `process.WaitForExit(timeout)`, then `Task.WaitAll([stdOutTask, stdErrTask], TimeSpan.FromSeconds(2))`.
  On that wait timing out it sets `LastFailure` to the "did not reach EOF within 2s" text and returns `null`,
  discarding everything already read. The message's "a grandchild may hold the pipe" is a guess baked into
  the string, not something the code measures.
- `RoslynSentinel.Tests.Tools.Basic/AssemblyInfo.cs:8` sets `[assembly: Parallelizable(ParallelScope.Fixtures)]`,
  so fixtures run concurrently and several can block pool threads in `WaitForExit` / `WaitAll` at once.
- Test classes that hand-roll git setup / a `RunGit` helper (found by Search; there may be more):
  `DocTrailerEngineTests`, `DocCloseTests`, `ServerBinaryStalenessGitTests` (all three call
  `GitProcess.TryRun` in their `RunGit`), plus `GitToolsSmokeTests`, `ResidualMentionScannerTests`,
  `GitStatusAttributionFindingsTests` and `WorktreeTeardownTests` (their `RunGit` bodies were not read here,
  so whether each goes through `GitProcess` is unverified). `NotFoundEvidenceTests` is also named in the
  brief but its helper was not located by the search used here. None of the helpers read set `gc.auto`,
  `maintenance.auto` or `core.fsmonitor`; a search for `gc.auto|fsmonitor` across the repo finds nothing.
  `GIT_CONFIG_NOSYSTEM` is set only once, process-wide, in
  `GitToolsSmokeTests.Git_Pull_DivergentBranchesWithNoPullConfig_MergesInsteadOfFailingAsync`
  (`RoslynSentinel.Tests.Tools.Basic/GitToolsSmokeTests.cs:1431`, restored at 1454).
- This machine has no gc, maintenance or fsmonitor config (owner-reported). Git is 2.55.0.windows.5.
- Partial flake comments already exist and are inconsistent: `ServerBinaryStalenessGitTests.cs` (class comment
  line 7, a stray comment at the `SameSha_IsNotStale` test, a TODO in `RunGit`), `DocCloseTests.cs:11` and
  `DocTrailerEngineTests.cs:8`. They say "suspected flaky" but not what to do about a failure, nor the suspected
  cause, nor a fix pointer.
- `ChildProcessCapture` (`RoslynSentinel.Common/ChildProcessCapture.cs`) reads via async `PumpAsync` loops
  (`reader.ReadAsync`), also continuation-driven on the pool, with a 4 s `DefaultDrainGrace` (line 29).
  Its own doc comment contrasts it with `GitProcess`. Whether it starves the same way is a hypothesis.
- `GitWorktreeManager.RunGit` (`RoslynSentinel.Common/GitWorktree/GitWorktreeManager.cs:210`) is a separate,
  sequential, timeout-less implementation with an existing TODO at line 233; it is not the code failing here.

### Hypotheses, ranked (none confirmed; no run has recorded pool state)
1. **Thread-pool starvation delays the EOF reads past the 2 s grace** (most likely). Parallel fixtures block
   pool threads in `WaitForExit`/`WaitAll`; the `ReadToEndAsync` continuations that would observe EOF queue
   behind them. Fits "only on full parallel runs, passes alone".
2. A detached `gc` / `maintenance` / `fsmonitor` grandchild holds the pipe (weak: the repos are tiny, and
   `rev-parse` and `add` start no background work; the failing commands include `rev-parse HEAD`).
3. Antivirus or slow process start (not excluded; would not explain the specific EOF-after-exit message
   by itself, but could add latency on top of 1).

## What would resolve it
Decided 2026-11-10 (owner's decisions are final):

1. **GitProcess fix (one slice, Small).** Read stdout and stderr on dedicated threads (`LongRunning`, not the
   pool), with a longer grace capped by the overall timeout. On failure `LastFailure` records the elapsed time
   since exit, `ThreadPool.ThreadCount` and `ThreadPool.PendingWorkItemCount`. Why: removes the pool dependence
   and, if it still fails, the recorded numbers confirm or reject hypothesis 1 on the next failure.
2. **Test hygiene (Medium).** Add a shared test-support git repo helper now (`InitRepo` / `RunGit`, with
   isolated config: `GIT_CONFIG_NOSYSTEM`, `gc.auto=0`, `maintenance.auto=false`, `core.fsmonitor=false`)
   and migrate the 8 classes listed above. Kills hypothesis 2 for the tests and removes the 8 copies of the
   helper. Open detail for the implementer: the helper must live where every test project that spawns git can
   reference it (the classes sit in `Tests.Tools.Basic`, `Tests.Basic` and `Tests.SubAgent`; sibling projects
   cannot see each other, so this likely means an existing shared test-support project - not checked here).
3. **Comments.** Every git-spawning test class and every `Build` / `RunTest`-spawning test class gets a
   class-level comment saying: this class is a known flake on full/parallel runs; disregard a failure here when
   the change did not touch process spawning; suspected cause (pipe EOF reads starved under load); fix pointer.
   For the `Build` / `RunTest` classes the comment names `ChildProcessCapture`'s pool-backed reads as the
   suspected cause and the `GitProcess` dedicated-thread fix as the reference fix to copy. Replace the
   inconsistent existing comments rather than stacking another.
4. **Build / RunTest fix: not now.** Only if the new instrumentation confirms starvation, apply the same
   dedicated-thread change to `ChildProcessCapture`.
5. **Helper location (decided 2026-11-10, implement run).** `Tests.Support.Basic`, on plain `Process` with
   the isolated config set per process through `psi.Environment` (not the process-wide env var used at
   `GitToolsSmokeTests.cs` ~1431, which races under parallel fixtures). `Tests.SubAgent` gets a project
   reference to `Tests.Support.Basic` so `WorktreeTeardownTests` uses it too. Slice 2 splits into 2a (helper
   plus the classes calling `GitProcess`) and 2b (the plain-`Process` classes); slice 3 runs after slice 2.

**Acceptance:** 5 consecutive full `scripts/Invoke-WorktreeTests.ps1` runs with zero "did not reach EOF" or
"git failed to run" failures. The baseline rate was 3 of 7 runs; if the true rate were unchanged, 5 clean
runs in a row would happen about 6% of the time ((4/7)^5), so this is evidence, not proof.

## Alternatives considered, not pursued
- Retry loops in the tests: rejected (the existing comment in `ServerBinaryStalenessGitTests` says "do not
  paper over it with retry loops"); it would hide the production-path defect.
- Raise only the 2 s grace: not chosen alone; under starvation any fixed grace is a guess. The decision keeps
  a longer grace but removes the pool dependence and records the evidence.
- `[NonParallelizable]` on the git fixtures: one test already carries it
  (`LaterCommitTouchingServerSource_IsStaleAndListed`) and the flake still hit other tests in the class, and it
  would not help the production callers.

## Related
- `RoslynSentinel.Common/GitProcess.cs`, `RoslynSentinel.Common/ChildProcessCapture.cs`
- `.claude/handoff/2026-11-10_todo_punchlist_91761378.progress.md` (batches B, F, G)
- `docs/current/TODO.md` (flake entry; the owner is editing it separately - this issue does not edit it)
- Memory: `project_modeleval_testhost_gotchas` (a different silent-crash / hang pair, not this flake)
