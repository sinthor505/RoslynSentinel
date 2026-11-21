# Plan: move all test/eval/run/scratch output to one out-of-repo scratch root, add runtime-state retention and a repo hygiene guard

**Status:** IN PROGRESS 2026-11-20. Steps 1a-15b done (last 13788ee9; hashes under each step); docs (16a-16c) and main-session Steps 17-19 left. Batched run by session 3e5e7eba.

## Problem

The repo working tree collects output that is not source, and nothing bounds the server's own runtime state.
Measured 2026-11-20:

- Repo root holds `ModelTestingResults\` (2,922 MB), 13 `_scratchbuild*` folders, an empty `.scratch\`, and
  `TestResults\parallel-run`. They are git-ignored by name (`.gitignore`: `_scratchbuild/`, `_scratchbuild_*/`,
  `ModelTestingResults/`, `/.scratch`, `TestResults/`), so a stray new output folder is invisible, and the blanket
  `*.json` / `*.csv` ignores hide stray data files too.
- `..\RoslynSentinel-TestRuns\` (1,890 MB; 36 SubAgent runs, 5 PlanStepRunner runs, one registered git worktree under
  `PlanStepRunner\20261110-112338-062\02-phase1-types-and-engine-fix\Worktree`) is a second sibling output root.
- `<solution>\.roslynsentinel\` has no retention except `largeresults` (`LargeResultHelper.SweepExpired`, 7 days):
  `operations\` 14,252 files / 861 MB (oldest 2026-06-07), `build\` 4,218 files / 1,086 MB (two dirs, `test-10752-1`
  from 2026-11-05 and `test-26936-1` from 2026-10-23), `validation\` 659 files, `scans\` last written 2026-08-27
  (no writer in current source).
- Server log folders keep every start: `RoslynSentinel.Server.Advanced\bin\Debug\net10.0\logs` has 137 files (44 MB,
  oldest 2026-08-23). Serilog is configured with `RollingInterval.Infinite` (`ServerStartupHelpers.cs:632`, `:655`),
  and a retained-file-count limit does not apply to a non-rolling file, so the stock option cannot fix this.
- `scripts\roslynsentinel-errorvariants.ps1:131` carries a TODO that it ignores `ROSLYNSENTINEL_RESULTS_ROOT`
  (also a READY item in `docs/current/TODO.md:53`); `scripts\Test-Parallel.ps1:19` overwrites its own
  `$ResultsDirectory` parameter.

## Decision

1. One scratch root. `ROSLYNSENTINEL_SCRATCH_ROOT` (absolute) wins; unset means
   `<parent of the source repo root>\RoslynSentinel-ScratchDir`. Layout:
   `ModelTestingResults\`, `build\<suffix>` and `build\ev_<suffix>`, `TestRuns\{PlanStepRunner,SubAgent,...}`,
   `TestResults\parallel-run`, `probe-inputs\` (already there, untouched), `checkouts` is NOT folded (see Risks).
   `ROSLYNSENTINEL_RESULTS_ROOT` stays as the more specific override; its default becomes `<scratch>\ModelTestingResults`.
2. One resolver per language, both with identical rules: `RoslynSentinel.Common.ScratchRoot` (C#) and
   `scripts\roslynsentinel-scratch.ps1` (PowerShell, dot-sourced, same pattern as `roslynsentinel-instances-common.ps1`).
3. A testhost that runs from `<scratch>\build\<suffix>\bin\...` can no longer find the repo by walking up to
   `RoslynSentinel.slnx` (six places do, verified below). A second Common helper, `RepoRootLocator`, reads
   `ROSLYNSENTINEL_REPO_ROOT` first, then walks up; the two eval scripts export it.
4. Pinned eval checkouts keep building inside the throwaway checkout as `<checkout>\_scratchbuild_<suffix>`
   (short path, unique per checkout, deleted with it). The PS helper detects a checkout by its `complete` +
   `lastused` marker files, so old-commit scripts and new ones agree on the path with no new env var.
5. Retention for server runtime state and server logs reuses the `LargeResultHelper.SweepExpired` model: one Common
   static class, invoked at solution load (operations, validation, build) and at logging start (logs), never throwing.
6. A read-only hygiene guard `scripts\Test-RepoHygiene.ps1`, wired as a SessionStart hook warning (not into
   `build.ps1`).
7. Last: data migration by the main session, then remove the four obsolete `.gitignore` entries so a regression shows
   up as untracked.

Owner rulings 2026-11-20 (Risks 1-4, 8): operation blobs 14 days / newest 5,000 / newest 24 h always kept;
validation and build instances 7 days; logs 14 days or 50 files; pinned checkouts keep `<checkout>\_scratchbuild_<suffix>`
plus the `.git\info\exclude` entry; `Get-EvalCheckoutRoot` not folded; hygiene guard is a SessionStart warning only;
blanket `*.json`/`*.csv` ignores stay. Owner away during the run: mid-run decisions are deferred to the tracker.

## Execution rules

- Dispatcher runs `Build` and the named tests; implementers run only `Build` (default level) and never tests.
  One `Build`/`RunTest` at a time across all agents (shared `bin/obj`); pairs below are file-disjoint but their builds
  serialize.
- Every slice is compile-green. Text-neutral slices are marked; schedule them first when a hold is needed.
- C# edits go through the RoslynSentinel MCP tools; `.ps1`, `.md`, `.json` through Edit/Write. New `.cs` files are CRLF.
- Server-source slices (Common, Engines.Basic, Tools.Basic, Tools.Advanced, Server.Advanced: Steps 1a, 8a, 12, 13a, 13b)
  need Build + commit + server restart before any live check (CLAUDE.md, Verification) - done once in Step 17.
- Commit trailers per slice: `Plan: plan_scratch_root_and_repo_hygiene`; the last commit carries `Closes:`.
- ASCII-only punctuation in code comments, docs and messages.
- A `[Test]` that touches environment variables is `[NonParallelizable]`, restores the variable in `finally`, and carries
  a comment saying why (CLAUDE.md convention).
- No step edits historical docs (`evals/`, `findings/`, `proposals/`, `closed/`, `archive/`, `CLOSED.md`).

## Verified inventory (2026-11-20, `Search`/`GetFileOutline`/`Select-String`; `*/Worktree/` excluded)

Writers of repo-local output:

| Site | Anchor |
| --- | --- |
| ModelEval results base | `RoslynSentinel.Tests.ModelEval\ModelTestingResultsArchiver.cs:108-132` (`ResolveResultsBase`, private `FindRepoRoot`) |
| ErrorVariants results base (duplicate class) | `RoslynSentinel.Tests.ErrorVariants\ModelTestingResultsArchiver.cs:97-121` |
| modeleval script | `scripts\roslynsentinel-modeleval.ps1:502` (`_scratchbuild_$suffix`), `:509-517` (results base) |
| errorvariants script | `scripts\roslynsentinel-errorvariants.ps1:128` (`_scratchbuild_ev_$suffix`), `:131-132` (TODO + `ModelTestingResults`) |
| queue worker | `scripts\roslynsentinel-evalqueue.ps1:206` (`$envNeverCaptured`), `:704` (job `sourceRepoRoot`), `:988-991` (`Get-RunFolders`), `:1000` (testhost path), `:1033` (pinned `RESULTS_ROOT`), `:1114` (pre-build `--artifacts-path`), `:1331` (run index root) |
| parallel tests | `scripts\Test-Parallel.ps1:11` (param default), `:19` (overwrites the parameter) |
| TestRuns (PlanStepRunner) | `RoslynSentinel.Utilities.PlanStepRunner\RunnerOptions.cs:83`; `scripts\roslynsentinel-planstep.ps1:20, 83, 205` |
| TestRuns (SubAgent, server code) | `RoslynSentinel.Tools.Advanced\SubAgentRunNaming.cs:26` (`TestRunsRootFolderName`), `:36-42` (`RunDirectory`); one caller `SubAgentChildServerLauncher.cs:81`; tests `RoslynSentinel.Tests.SubAgent\SubAgentLaunchTests.cs:44, 63, 64` |
| `.scratch` fallback | `RoslynSentinel.Tests.Tools.Basic\TestInstanceLauncherTests.cs:438` (message), `:574` (root list in `FindBuiltServerOutput`) |

Readers / defaults that name repo-local paths: `Archive-ModelTestingResults.ps1:31`, `Convert-RunEscapes.ps1:42`,
`Get-ShippingKindTypoCounts.ps1:75` (behavioural defaults); help text only in `Backfill-TranscriptSummary.ps1`,
`Get-NotFoundRecoveryReport.ps1`, `Parse-AgentLog.ps1`, `roslynsentinel-interrogate.ps1`, `Get-BomAudit.ps1:24`,
`Get-EolAudit.ps1:32`, `Probe-OvernightLogprobs.ps1:19, 233`, `Get-TodoMarkers.ps1:8` (comment).

Repo-root walk-ups that break once a testhost runs outside the repo (all walk up from `AppContext.BaseDirectory` to
`RoslynSentinel.slnx`): both `ModelTestingResultsArchiver.FindRepoRoot`, `ModelEvalAssemblySetUp.ReadWorkingTreeStamp`
(`:129-137`, git stamp; returns null, so runs would silently be unstamped), `ResumeScenarios.CapturePath`
(`:89-102`), `RunStampTests.FindRepoRoot` (`:50-59`), `RoslynSentinel.Tests\TestSolutionFixture.FindRepoRoot` (`:226`,
finds `Samples\ContosoOrders`; `new TestSolutionFixture` is used 18 times in ModelEval).
`OrderPricingRefactorChainAgentTests.cs:1327` walks up for the fixture's own `ContosoOrders.Core` (not the repo) and is
unaffected.

Leads from the brief that turned out different:

- `ModelEvalAssemblySetUp.cs:129` is the git-stamp walk-up, not a results writer; it stays a repo-root lookup (Step 3a).
- The ~17 ModelEval test files listed for `ModelTestingResults` only call `ModelTestingResultsArchiver.ArchiveRunDirectory`
  (a class name); none changes. `ModelTestingResultsArchiverTests` tests `EvalHostTable.DeriveSuffix` only.
- `.claude/` (agents, skills, hooks) names none of the old paths (only `enforce-dogfood.ps1.bak` and two hook tests use
  the literal `...-TestRuns` as a path-boundary test string; they stay valid because `RoslynSentinel-ScratchDir` is
  equally a sibling).
- `Get-EvalCheckoutRoot` defaults to `C:\rs-eval` on purpose (warns above 16 chars for MAX_PATH headroom); folding it
  under `<scratch>\checkouts` (about 75 chars) would defeat that. Not folded.
- `BuildIsolationTests.cs:59, 90` assert that messages do NOT contain `.scratch`; they remain correct and are kept.
- Build output of pinned jobs cannot simply move to `<scratch>\build` (path length, and old-commit scripts); see Decision 4.

Retention facts (Step 12-13):

- Writers: `OperationBlobWriter.WriteAsync` (`operations\{tool}_{yyyyMMddTHHmmssZ}_{fraction}_{changeId}.json`, plus
  `{same name}.undone` markers from `MarkUndone`), `WriteValidationFailureAsync` (`validation\validation_{stamp}_{id}.json`),
  `BuildIsolation.GetArtifactsPath` (`build\{instanceId}`).
- Readers of old operation blobs: `UndoLastApply` (`WorkspaceFileEditImpl.cs:353`) and `GetOperationDetail`
  (`WorkspaceReadNavigationImpl.cs:1638`) look up by `changeId` with no age limit; `NotFoundEvidence` (`:144, :204`) and
  `EditEffectSeeder` (`:40`) read only the last 60 minutes (`EditEffectSeeder.DefaultWindow`). Missing blob is already an
  actionable error (`NoOperationBlobFound`, `UndoLastApplyTests` pins the code only).
- `PruneDirectoriesOfExitedProcesses` (`BuildIsolation.cs:346`) removes only numeric pid-named dirs; named instance dirs
  (`test-<pid>-<n>` from `TestInstanceLauncher.ResolveChildInstanceId`) are never pruned (`// TODO Part 3`, `:74`, `:345`).
- Sweep is invoked from `PersistentWorkspaceManager.LoadSolutionAsync` (`:1021`, `SweepLargeResultsInBackground`, `:1061`).

## Steps

Batch map (`||` = run as a file-disjoint pair; batches run in order; commit per batch, `Plan:` trailer):

- A: [1a || 1b], [3a || 2], [3b || 4]
- B: [5a || 6] (5a commits first), [5b || 7], [8b || 9], [10]
- C: [8a || 11]
- D: [12 || 15a], [13a || 13b], [14 || 15b]
- E: [16a || 16b], [16c]
- main session: 17, 18, 19

Lane / dependency / effect per step:

| Step | Lane | Depends on | Effect |
| --- | --- | --- | --- |
| 1a Common contracts | implementer | none | text-neutral |
| 1b PS helper + test | implementer | none | text-neutral |
| 2 resolver tests | test-writer | 1a | text-neutral |
| 3a archivers + stamp | implementer | 1a | behaviour-changing |
| 3b other repo-root sites | implementer | 1a | text-neutral in-repo |
| 4 archiver test | test-writer | 3a | text-neutral |
| 5a modeleval script | implementer | 1b, 3a, 3b | behaviour-changing |
| 5b errorvariants script | implementer | 1b, 3a, 3b | behaviour-changing |
| 6 queue worker + checkout exclude | implementer | 1b | behaviour-changing |
| 7 queue test scripts | implementer | 6 | text-neutral |
| 8a TestRuns C# | implementer | 1a | behaviour-changing |
| 8b planstep + Test-Parallel | implementer | 1b | behaviour-changing |
| 9 default-root scripts | implementer | 1b | behaviour-changing |
| 10 help-text sweep | implementer-sweep | 5a, 5b | text-neutral |
| 11 launcher test fallback | implementer | none | text-neutral |
| 12 retention bodies | implementer-senior | 1a | text-neutral (no callers) |
| 13a load-time wiring | implementer | 12 | behaviour-changing |
| 13b log wiring + error text | implementer | 12 | behaviour-changing |
| 14 retention tests | test-writer | 13a, 13b | text-neutral |
| 15a hygiene script | implementer | none | text-neutral |
| 15b hygiene hook | implementer | 15a | behaviour-changing |
| 16a docs sweep (procedure) | implementer-sweep | 5a, 5b, 6 | text-neutral |
| 16b docs sweep (5 refs) | implementer-sweep | 5a, 5b, 8a | text-neutral |
| 16c new prose | implementer | 16a, 16b, 13b, 15b, 8a | text-neutral |
| 17 stub scan, build, commit, restart | main/dispatcher | all above | verification |
| 18 data migration | main session | 17 | behaviour-changing |
| 19 remove ignores, hygiene run, close | main session | 18 | closing |

### Step 1a - Common contracts: `ScratchRoot`, `RepoRootLocator`, `RuntimeStateRetention` skeleton

- Done: 1520b74f
- Files (all new, in `RoslynSentinel.Common\`): `ScratchRoot.cs`, `RepoRootLocator.cs`, `RuntimeStateRetention.cs`.
- Depends on: none. Lane: implementer (3 files, 3 `CreateFile` calls, definitions first: `RepoRootLocator`, `ScratchRoot`, `RuntimeStateRetention`).
- Effect: text-neutral (no callers yet).
- Names verified free: `Search(mode: symbol)` for `ScratchRoot`, `RepoRoot*`, `*Retention*` found none (2026-11-20).
- Change: namespace `RoslynSentinel.Common`, `public static class` each. `ScratchRoot` and `RepoRootLocator` get final bodies
  (small); `RuntimeStateRetention` keeps constants real and every method body
  `throw new NotImplementedException(); // TODO implement (Step 12)`.

```csharp
public static class RepoRootLocator
{
    public const string EnvironmentVariable = "ROSLYNSENTINEL_REPO_ROOT";
    public const string SolutionFileName = "RoslynSentinel.slnx";

    /// <summary>startDirectory null: the env var when it is rooted and holds RoslynSentinel.slnx, else the first
    /// ancestor of AppContext.BaseDirectory that holds it. startDirectory given: ONLY the walk-up from that
    /// directory, the env var is never consulted (RunStampTests.ReadWorkingTreeStamp_OutsideARepo_ReturnsNull,
    /// RunStampTests.cs:26, passes a temp folder and must get null while Step 5a exports the variable).
    /// Null when nothing matches. Never throws.</summary>
    public static string? TryFind(string? startDirectory = null);

    /// <summary>TryFind, or throws DirectoryNotFoundException naming the env var and the start directory.</summary>
    public static string Find(string? startDirectory = null);
}

public static class ScratchRoot
{
    public const string EnvironmentVariable = "ROSLYNSENTINEL_SCRATCH_ROOT";
    public const string ResultsRootEnvironmentVariable = "ROSLYNSENTINEL_RESULTS_ROOT";
    public const string DefaultFolderName = "RoslynSentinel-ScratchDir";

    /// <summary>Checks ROSLYNSENTINEL_SCRATCH_ROOT FIRST (expanded; must be absolute, else InvalidOperationException
    /// with the value and an example) and returns without touching RepoRootLocator when it is set; unset/blank ->
    /// Combine(parent of full(sourceRepoRoot ?? RepoRootLocator.Find()),
    /// DefaultFolderName); repo at a drive root (no parent) -> Combine(Path.GetTempPath(), DefaultFolderName).
    /// Result is a full path without a trailing separator.</summary>
    public static string Resolve(string? sourceRepoRoot = null);

    /// <summary>ROSLYNSENTINEL_RESULTS_ROOT (absolute, else InvalidOperationException) else Combine(Resolve(), "ModelTestingResults").</summary>
    public static string ResultsRoot(string? sourceRepoRoot = null);

    /// <summary>Combine(Resolve(), "TestRuns").</summary>
    public static string TestRunsRoot(string? sourceRepoRoot = null);
}

public static class RuntimeStateRetention
{
    public static readonly TimeSpan OperationsMaxAge = TimeSpan.FromDays(14);
    public const int OperationsMaxCount = 5000;
    public static readonly TimeSpan OperationsProtectedAge = TimeSpan.FromDays(1);
    public static readonly TimeSpan ValidationMaxAge = TimeSpan.FromDays(7);
    public static readonly TimeSpan BuildInstanceMaxAge = TimeSpan.FromDays(7);
    public static readonly TimeSpan LogMaxAge = TimeSpan.FromDays(14);
    public const int LogMaxFiles = 50;

    public static int SweepOperations(string solutionRoot, TimeSpan maxAge, int maxCount, DateTime? utcNow = null);
    public static int SweepValidation(string solutionRoot, TimeSpan maxAge, DateTime? utcNow = null);
    public static int SweepBuildInstances(string buildRoot, string currentInstanceId, TimeSpan maxAge, DateTime? utcNow = null);
    public static int SweepLogs(string logDirectory, string fileStem, string currentLogPath, int maxFiles, TimeSpan maxAge, DateTime? utcNow = null);
}
```
  Env values go through `Environment.ExpandEnvironmentVariables`; a value that is still `%...%` or not rooted is rejected, matching
  `Get-RoslynSentinelInstancesRoot` in `scripts\roslynsentinel-instances-common.ps1`.
  The exception message for a relative scratch root names the variable, the value, and an example full path (facts, no policy).
- Done when: `Build` (default level) 0 errors. The dispatcher runs `Search(mode: text, query: "NotImplementedException", fileGlob: "RoslynSentinel.Common/RuntimeStateRetention.cs")`
  and expects exactly four hits until Step 12.

### Step 1b - PowerShell helper `scripts\roslynsentinel-scratch.ps1` and its test

- Done: fe343823
- Files: `scripts\roslynsentinel-scratch.ps1` (new), `scripts\roslynsentinel-scratch.Tests.ps1` (new; copy the harness idiom - `Assert-That`, temp root,
  summary and exit code - from `scripts\roslynsentinel-instances-common.Tests.ps1`).
- Depends on: none. Lane: implementer (2 new files).
- Effect: text-neutral (functions only, no top-level side effects, PowerShell 5.1 compatible like `roslynsentinel-instances-common.ps1`).
- Change: contract (every function returns a full path without trailing `\`; `-RepoRoot` omitted means `Split-Path $PSScriptRoot -Parent`):

```powershell
function Get-RoslynSentinelScratchRoot   { [CmdletBinding()] param([string]$RepoRoot) }   # [string]
function Get-RoslynSentinelResultsRoot   { [CmdletBinding()] param([string]$RepoRoot, [string]$ScratchRoot) }   # RESULTS_ROOT else <scratch>\ModelTestingResults
function Get-RoslynSentinelTestRunsRoot  { [CmdletBinding()] param([string]$RepoRoot, [string]$ScratchRoot) }   # <scratch>\TestRuns
function Test-RoslynSentinelEvalCheckout { [CmdletBinding()] param([Parameter(Mandatory)][string]$RepoRoot) }   # [bool]: <RepoRoot>\complete and <RepoRoot>\lastused both exist
function Get-RoslynSentinelBuildDir {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$RepoRoot, [Parameter(Mandatory)][string]$Suffix, [string]$Prefix = '', [string]$ScratchRoot)
}   # eval checkout -> <RepoRoot>\_scratchbuild_<Prefix><Suffix>; else <scratch>\build\<Prefix><Suffix>
```
  `-ScratchRoot` (an already-resolved absolute path, for `Get-RoslynSentinelResultsRoot` and `Get-RoslynSentinelTestRunsRoot`; add the same optional
  parameter to `Get-RoslynSentinelBuildDir`): when given it replaces the env/default lookup, so a caller that holds a recorded root (the queue worker,
  `Get-JobScratchRoot($Job)` in Step 6) never depends on its own environment. With `-ScratchRoot` given, `Get-RoslynSentinelResultsRoot` returns
  `<ScratchRoot>\ModelTestingResults` and ignores the process `ROSLYNSENTINEL_RESULTS_ROOT`. Without it the functions behave as above.
  Rules identical to Step 1a (`ROSLYNSENTINEL_SCRATCH_ROOT` expanded and required absolute, `throw` otherwise; default
  `<parent of RepoRoot>\RoslynSentinel-ScratchDir`; drive-root repo falls back to `$env:TEMP`). Put a comment in
  `Get-RoslynSentinelBuildDir` that the checkout-local name is deliberate (short path; old-commit scripts write the same path).
  Test cases (same table as Step 2 so the two stay in parity): env absolute wins and trailing `\` trimmed; env relative throws;
  default sits beside the repo; `RESULTS_ROOT` override beats scratch default and relative throws; `-ScratchRoot` beats env and default; `-Prefix 'ev_'`;
  marker files flip the build dir to the checkout-local name.
- Done when: `pwsh scripts\roslynsentinel-scratch.Tests.ps1` exits 0 (dispatcher runs; implementer checks the file parses).

### Step 2 - Tests for `ScratchRoot` and `RepoRootLocator`

- Done: 840c35a3
- Files: `RoslynSentinel.Tests.Basic\ScratchRootTests.cs` (new, fixtures `ScratchRootTests` and `RepoRootLocatorTests`; Tests.Basic references Common).
- Depends on: 1a. Lane: test-writer.
- Effect: text-neutral.
- Change: same case table as Step 1b, using temp directories; env tests `[NonParallelizable]`, variables saved and restored in `finally`.
  `RepoRootLocator`: with `startDirectory` null a valid env value wins over the walk-up; with `startDirectory` given the env var is ignored (set it to a valid repo and
  assert `TryFind(<temp folder without a slnx>)` is null - the `RunStampTests.cs:26` contract); an env value pointing at a folder without `RoslynSentinel.slnx`, or relative, falls through to the walk-up;
  walk-up from a nested temp directory finds the temp "repo"; `TryFind` returns null and `Find` throws when nothing matches.
- Done when: `RunTest` filter `ScratchRootTests|RepoRootLocatorTests` passes (dispatcher); `Build` 0 errors.

### Step 3a - Repoint both archivers and the git stamp

- Done: 37130409
- Files: `RoslynSentinel.Tests.ModelEval\ModelTestingResultsArchiver.cs`, `RoslynSentinel.Tests.ErrorVariants\ModelTestingResultsArchiver.cs`,
  `RoslynSentinel.Tests.ModelEval\ModelEvalAssemblySetUp.cs`.
- Depends on: 1a. Lane: implementer (3 files, about 7 edits).
- Effect: behaviour-changing: archived runs land in `<scratch>\ModelTestingResults\<suffix>\...`.
- Change: in each archiver replace `ResolveResultsBase()` + private `FindRepoRoot()` with one call, `ScratchRoot.ResultsRoot()` (delete the
  private `FindRepoRoot` and its `DirectoryNotFoundException` message; delete the stale comment on `:108`/`:97` and replace it with
  `// ScratchRoot.ResultsRoot: ROSLYNSENTINEL_RESULTS_ROOT, else <scratch>\ModelTestingResults.`). Make `ResolveResultsBase` removal safe:
  `Search(mode: references)` for `ResolveResultsBase` first (expect only `ArchiveRunDirectory`). The two archiver classes stay duplicated;
  add `using RoslynSentinel.Common;` to the ErrorVariants archiver (verified: its csproj has only implicit usings and `NUnit.Framework`, and the file imports `RoslynSentinel.Common.AgentLoop` only, which does not bring `ScratchRoot` into scope; the ModelEval csproj already has a global `RoslynSentinel.Common` using). Leave `// TODO fold the duplicate ModelTestingResultsArchiver (ModelEval and ErrorVariants) into a shared helper` at the class and add the
  INBOX line (CLAUDE.md deferred-issue rule). In `ModelEvalAssemblySetUp.ReadWorkingTreeStamp` replace the inline walk-up (`:129-137`, including
  the "Copy of ModelTestingResultsArchiver.FindRepoRoot" comment) with `var root = RepoRootLocator.TryFind(startDirectory); if (root is null) { return null; }`
  keeping the null-return behaviour and everything after it. Because `TryFind(startDirectory)` ignores the env var, also change the production call at `ModelEvalAssemblySetUp.cs:51` to `ReadWorkingTreeStamp(RepoRootLocator.TryFind() ?? AppContext.BaseDirectory)`: the parameterless `TryFind` reads `ROSLYNSENTINEL_REPO_ROOT` (set by Step 5a), so a testhost running from `<scratch>\build\...` still stamps the source repo.
- Done when: `Build` 0 errors; dispatcher runs `RunStampTests` (`ReadWorkingTreeStamp_ReturnsTheRepoHead`, `ReadWorkingTreeStamp_OutsideARepo_ReturnsNull`) after Step 3b.

### Step 3b - Remaining repo-root lookups

- Done: a3f8b565
- Files: `RoslynSentinel.Tests.ModelEval\ResumeScenarios.cs`, `RoslynSentinel.Tests\TestSolutionFixture.cs`, `RoslynSentinel.Tests.ModelEval\RunStampTests.cs`.
- Depends on: 1a. Lane: implementer (3 files, 4 edits).
- Effect: text-neutral inside the repo (same result when the bin folder is under the repo; `ROSLYNSENTINEL_REPO_ROOT` wins when set).
- Change: `ResumeScenarios.CapturePath` uses `RepoRootLocator.Find()` then `Path.Combine(root, "RoslynSentinel.Tests.ModelEval", "ResumeTranscripts", fileName)`;
  `TestSolutionFixture.FindRepoRoot()` body becomes `return RepoRootLocator.Find();` (keep the method so the one call site at `:35` is untouched);
  `RunStampTests.FindRepoRoot` likewise returns `RepoRootLocator.Find()` (its expected value must be what `ReadWorkingTreeStamp` used), and `RunStampTests.cs:17` becomes `ReadWorkingTreeStamp(RepoRootLocator.Find())` so the test also passes when the testhost runs from a scratch build. Leave `:26-28` (`OutsideARepo`) untouched: it passes a temp folder and relies on `TryFind(startDirectory)` ignoring the env var.
  `RoslynSentinel.Tests` already references Common (verified in its csproj). Leave the other `FindRepoRoot` copies (Tests.Basic, Tests.Server,
  Tests.PlanStepRunner, Tests.Tools.*): those run from in-repo `bin` and are out of scope.
- Done when: `Build` 0 errors; dispatcher runs `RunStampTests` and one `TestSolutionFixture` user (`BuildIsolationTests.RunFullBuildAsync_FullBuildThrowaway_WritesOutputUnderInstanceDirNotProjectBinAsync`).

### Step 4 - Archiver destination test

- Done: 09191c1a
- Files: `RoslynSentinel.Tests.ModelEval\ModelTestingResultsArchiverTests.cs` (add tests; the fixture sits in namespace `RoslynSentinel.Tests.ModelEval`, outside the `.LiveModel` SetUpFixture that can `Assert.Ignore` the assembly).
- Depends on: 3a. Lane: test-writer.
- Effect: text-neutral.
- Change: two `[NonParallelizable]` tests with env save/restore: (1) `ROSLYNSENTINEL_SCRATCH_ROOT=<tmp>\scratch`, `ROSLYNSENTINEL_RESULTS_ROOT` cleared; create `<tmp>\work\model-eval\SomeTest\20260101-000000-000\agent.log`;
  call `ModelTestingResultsArchiver.ArchiveRunDirectory`; assert `<tmp>\scratch\ModelTestingResults\<EvalHostTable.DeriveSuffix(LlmOptions.BaseUrl)>\SomeTest\20260101-000000-000\agent.log` exists and that nothing was created under the repo root.
  (2) `ROSLYNSENTINEL_RESULTS_ROOT=<tmp>\results` beats the scratch root.
- Done when: `RunTest` filter `ModelTestingResultsArchiverTests` passes (dispatcher).

### Step 5a - modeleval script

- Done: 43b98119
- Files: `scripts\roslynsentinel-modeleval.ps1`.
- Depends on: 1b, 3a, 3b. Lane: implementer (1 file, about 7 edits). Commits before Step 5b (batch B).
- Effect: behaviour-changing: builds go to `<scratch>\build\<suffix>`, results to `<scratch>\ModelTestingResults`.
- Change: dot-source `roslynsentinel-scratch.ps1` after `$repoRoot` (`:394`); `$scratchRoot = Get-RoslynSentinelScratchRoot -RepoRoot $repoRoot`;
  `$artifactsPath = Get-RoslynSentinelBuildDir -RepoRoot $repoRoot -Suffix $suffix` (`:502`); export `$env:ROSLYNSENTINEL_REPO_ROOT = $repoRoot` and `$env:ROSLYNSENTINEL_SCRATCH_ROOT = $scratchRoot`
  before the first `dotnet` call, so the testhost (which no longer sits under the repo) resolves both the same way the script did (the C# side reads them: `RepoRootLocator.TryFind()` with no start directory,
  `ScratchRoot.Resolve()`); replace the results-base block (`:509-517`) with `$resultsBase = Get-RoslynSentinelResultsRoot -RepoRoot $repoRoot` (the helper throws on a relative `RESULTS_ROOT`; keep the comment that the archivers swallow their own exceptions, so it is validated here).
  Help text `:105, 109, 158-159, 171` and the archive-step comments `:733, 740` name the new locations and `ROSLYNSENTINEL_SCRATCH_ROOT`; no logic change to the archive step (`:725-745`).
- Done when: the file parses (`[System.Management.Automation.Language.Parser]::ParseFile(<path>, [ref]$null, [ref]$err)` with `$err.Count -eq 0`); `Select-String` finds no `_scratchbuild` and no `Join-Path $repoRoot 'ModelTestingResults'` left; dispatcher runs `pwsh scripts\roslynsentinel-evalqueue.Tests.ps1` after Step 7 (it lifts `Get-RunLeafDirs`/`Copy-NewRunDirectories` from modeleval by AST).

### Step 5b - errorvariants script

- Done: cc021d4e
- Files: `scripts\roslynsentinel-errorvariants.ps1`.
- Depends on: 1b, 3a, 3b, 5a (same pattern, committed second). Lane: implementer (1 file, about 7 edits).
- Effect: behaviour-changing: build to `<scratch>\build\ev_<suffix>`, results to `<scratch>\ModelTestingResults`.
- Change: dot-source the helper after `$repoRoot = Split-Path $PSScriptRoot -Parent` (`:103`); `$artifactsPath = Get-RoslynSentinelBuildDir -RepoRoot $repoRoot -Suffix $suffix -Prefix 'ev_'` (`:128`);
  resolve the TODO at `:131` with `$resultsDir = Join-Path (Get-RoslynSentinelResultsRoot -RepoRoot $repoRoot) "$suffix\$testName"` (`:132`) and delete the TODO line; export `ROSLYNSENTINEL_REPO_ROOT` and `ROSLYNSENTINEL_SCRATCH_ROOT` as in 5a before the first `dotnet` call.
  Help text `:10, 14, 63, 68` names the new locations.
- Done when: parses (as 5a); `Select-String` finds no `_scratchbuild` and no `ModelTestingResults\$suffix` built from `$repoRoot` left.

### Step 6 - Queue worker and checkout exclude

- Done: 7550685c
- Files: `scripts\roslynsentinel-evalqueue.ps1`, `scripts\roslynsentinel-evalcheckout.ps1`.
- Depends on: 1b. Lane: implementer (2 files, about 14 edits).
- Effect: behaviour-changing: every job (pinned or legacy) writes results under the job's scratch root; pinned build path is unchanged.
- Change, `roslynsentinel-evalqueue.ps1`: dot-source the helper beside `:196-198`; add `'ROSLYNSENTINEL_SCRATCH_ROOT'` and `'ROSLYNSENTINEL_REPO_ROOT'` to `$envNeverCaptured` (`:206`; the capture filter is at `:652`) so a submitter's value never rides along with the job (the run sets its own from the job);
  at submit (job object, `:704`) record `scratchRoot = Get-RoslynSentinelScratchRoot -RepoRoot $RepoRoot`; add `function Get-JobScratchRoot($Job)` returning `$Job.scratchRoot` when present, else `Get-RoslynSentinelScratchRoot -RepoRoot $Job.sourceRepoRoot` (jobs written before this change);
  `Get-RunFolders` (`:988-991`) uses `Join-Path (Get-RoslynSentinelResultsRoot -RepoRoot $Job.sourceRepoRoot -ScratchRoot (Get-JobScratchRoot $Job)) $Job.suffix` (results root = `<jobScratch>\ModelTestingResults`, independent of the worker's own environment);
  `Wait-ForTesthostExit` (`:1000`) and `Start-PinnedJob` (`:1114`) use `Get-RoslynSentinelBuildDir -RepoRoot $Job.repoRoot -Suffix $Job.suffix -ScratchRoot (Get-JobScratchRoot $Job)` (pinned: unchanged checkout-local path; legacy: `<scratch>\build\<suffix>`);
  `Invoke-JobRun` (`:1033`) always sets `ROSLYNSENTINEL_RESULTS_ROOT = <jobScratch>\ModelTestingResults` and `ROSLYNSENTINEL_SCRATCH_ROOT = <jobScratch>` after the submitter's variables (so old-commit scripts in a checkout, which only know `RESULTS_ROOT`, still land in the right place);
  the run-index root (`:1331`) uses the same results root (`-ScratchRoot (Get-JobScratchRoot $final)`). Update help text at `:61-67, 84, 139`.
  `roslynsentinel-evalcheckout.ps1`: after `git clone --shared` in `Get-EvalCheckout` (`:129-130`), append `_scratchbuild_*/`, `complete`, `lastused` to `<partial>\.git\info\exclude` (UTF-8 no BOM, create the `info` folder if missing), so a post-change commit (whose `.gitignore` no longer lists `_scratchbuild_*/`) does not look dirty to the run stamp (`ModelEvalAssemblySetUp.IsDirty` counts untracked `.cs`).
- Done when: both parse; `Select-String` shows no `Join-Path $Job.sourceRepoRoot 'ModelTestingResults'` and no `Join-Path $Job.repoRoot "_scratchbuild_` left in the queue script; dispatcher runs `pwsh scripts\roslynsentinel-evalqueue.Tests.ps1` after Step 7.

### Step 7 - Queue test scripts

- Done: 0d7efd6a
- Files: `scripts\roslynsentinel-evalqueue.Tests.ps1`, `scripts\roslynsentinel-evalqueue.TestHarness.ps1`, `scripts\roslynsentinel-evalqueue.Preflight.Tests.ps1`.
- Depends on: 6. Lane: implementer (3 files, about 8 edits).
- Effect: text-neutral.
- Change: the stubs (`TestHarness:58`, `Tests:47`) already prefer `$env:ROSLYNSENTINEL_RESULTS_ROOT`, which the queue now always sets; change only their fallback to `Join-Path $env:ROSLYNSENTINEL_SCRATCH_ROOT 'ModelTestingResults'`-style. Add to each test file's setup `. (Join-Path $PSScriptRoot 'roslynsentinel-scratch.ps1')`, set `$env:ROSLYNSENTINEL_SCRATCH_ROOT = Join-Path $tempRoot 'scratch'` (restored at the end of the file) and `$scratch = Get-RoslynSentinelScratchRoot -RepoRoot $repo`
  (explicit temp path, so no test can write beside the real repo or depend on the machine's own setting). Replace `Join-Path $repo 'ModelTestingResults'` with `Join-Path $scratch 'ModelTestingResults'` in `Tests.ps1:347` (assertion text `Results: a pinned run lands under <scratch>\ModelTestingResults, not in the checkout`),
  `Preflight.Tests.ps1:25, 68`; `Tests.ps1:448` (`$base 'ModelTestingResults\url_host'`) is a temp path for the archive test and stays. Add one assertion that a created checkout's `.git\info\exclude` contains `_scratchbuild_*/`.
- Done when: `pwsh scripts\roslynsentinel-evalqueue.Tests.ps1`, `...Preflight.Tests.ps1`, `...Hosts.Tests.ps1`, `...Visibility.Tests.ps1` all exit 0 (dispatcher), and `git status` shows no `ModelTestingResults` or `RoslynSentinel-ScratchDir` created under or beside the real repo by the run.

### Step 8a - TestRuns root in C# (PlanStepRunner and SubAgent)

- Done: 7773d2c8
- Files: `RoslynSentinel.Utilities.PlanStepRunner\RunnerOptions.cs`, `RoslynSentinel.Tools.Advanced\SubAgentRunNaming.cs`, `RoslynSentinel.Tests.SubAgent\SubAgentLaunchTests.cs`.
- Depends on: 1a. Lane: implementer (3 files, about 7 edits; `Utilities.PlanStepRunner` and `Tools.Advanced` both reference Common).
- Effect: behaviour-changing: new SubAgent/PlanStepRunner runs go to `<scratch>\TestRuns\...`. Server code.
- Change: `RunnerOptions.Parse` (`:83`): `?? Path.Combine(ScratchRoot.TestRunsRoot(sourceRepo), "PlanStepRunner", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff"))` and fix the comment above it (sibling -> scratch root; add `using RoslynSentinel.Common;` if absent).
  `SubAgentRunNaming`: delete `TestRunsRootFolderName`; `RunDirectory(sourceRepo, runId)` returns `Path.Combine(ScratchRoot.TestRunsRoot(sourceRepo), RunsFolderName, runId)` (keep the trailing-separator tolerance, now provided by `ScratchRoot.Resolve`); rewrite the `<remarks>` paragraph at `:15-18` (scratch root, never inside the repo: a run folder in the repo would show as untracked content).
  `ScratchRoot.Resolve` can throw `InvalidOperationException` for a relative `ROSLYNSENTINEL_SCRATCH_ROOT`, which must not surface raw from the launch: `RunDirectory` itself catches `InvalidOperationException` and rethrows `new SubAgentLaunchException(ToolErrorCode.InvalidArgument, ex.Message, inner: ex)` (the exception class is declared in the same file, `SubAgentRunNaming.cs:69`; `ToolErrorCode` is already used by the launcher in this assembly, add its `using` if the file lacks it). The single caller `SubAgentChildServerLauncher.LaunchAsync` (`:81`, blast radius 1, documented `<exception cref="SubAgentLaunchException">`) stays untouched, so the slice is 3 files.
  Tests (`SubAgentLaunchTests.cs:40-65`): `RunDirectory_IsASiblingOfTheRepo_NotInsideIt` becomes `RunDirectory_IsUnderTheScratchRoot_NotInsideTheRepo` (assert `Path.GetDirectoryName` x4 equals `Path.GetDirectoryName(repo)` with the folder `RoslynSentinel-ScratchDir\TestRuns\SubAgent\run-1`, and an env override case) plus a new `RunDirectory_RelativeScratchRoot_ThrowsSubAgentLaunchException`; all tests that touch it save, clear and restore `ROSLYNSENTINEL_SCRATCH_ROOT` and are `[NonParallelizable]`.
- Done when: `Build` 0 errors; dispatcher runs `RunTest` filter `SubAgentLaunchTests` and `PlanStepRunnerSurfaceTests` (the latter calls `RunnerOptions.Parse(["--plan-dir","p","--repo","r"])`).

### Step 8b - planstep script and Test-Parallel

- Done: 7e4f5a36
- Files: `scripts\roslynsentinel-planstep.ps1`, `scripts\Test-Parallel.ps1`.
- Depends on: 1b. Lane: implementer (2 files, about 8 edits).
- Effect: behaviour-changing (output location).
- Change: planstep: dot-source the helper; `$testRunsRoot = Join-Path (Get-RoslynSentinelTestRunsRoot -RepoRoot $ImplRepo) 'PlanStepRunner'` (`:205`); help text `:20, 83`. Test-Parallel: dot-source helper; make `[string]$ResultsDirectory` default empty;
  after `$repoRoot` is computed, `if (-not $ResultsDirectory) { $ResultsDirectory = Join-Path (Get-RoslynSentinelScratchRoot -RepoRoot $repoRoot) 'TestResults\parallel-run' }` and delete the overwriting line `:19` (an explicit `-ResultsDirectory` now sticks); create parents with `New-Item -Force`. Check `scripts\build.ps1` callers: `Select-String 'Test-Parallel|parallel-run' scripts\build.ps1` and update any literal `TestResults\parallel-run` it reads (TRX parsing).
- Done when: both parse (`[System.Management.Automation.Language.Parser]::ParseFile(<path>, [ref]$null, [ref]$err)` with `$err.Count -eq 0`); `Select-String -Path scripts\Test-Parallel.ps1 -Pattern 'Join-Path \$repoRoot "TestResults'` returns no hits. Do not run `Test-Parallel.ps1` itself (it runs the whole test suite and deletes its results folder); the first real run is in Step 17.

### Step 9 - Behavioural default roots in report scripts

- Done: 86a45912
- Files: `scripts\Archive-ModelTestingResults.ps1` (`:31`), `scripts\Convert-RunEscapes.ps1` (`:42`), `scripts\Get-ShippingKindTypoCounts.ps1` (`:75`).
- Depends on: 1b. Lane: implementer (3 files, about 6 edits).
- Effect: behaviour-changing (default scan root).
- Change: a parameter default cannot dot-source before binding, so make `[string]$Root` (resp. `$Path`) default empty and add right after the `param` block:
  `. (Join-Path $PSScriptRoot 'roslynsentinel-scratch.ps1'); if (-not $Root) { $Root = Get-RoslynSentinelResultsRoot }`. Update the `.PARAMETER Root` help ("Defaults to `<scratch>\ModelTestingResults`").
  `Get-ShippingKindTypoCounts.ps1` is different: `:75` is a fallback for `$Path` (`if (-not $Path -and -not $ReplayResults) { $Path = @(Join-Path $repoRoot 'ModelTestingResults') }`), and `$pathGiven = [bool]$Path` (`:74`) must keep its meaning (it feeds the `ShippingKind` filter at `:139`). Keep both lines in place and only swap the expression to `@(Get-RoslynSentinelResultsRoot -RepoRoot $repoRoot)`, with the helper dot-sourced after `$repoRoot` (`:73`); its help lines (`:25, 54, 58`) are rewritten in Step 10.
- Done when: all three parse; `pwsh scripts\Archive-ModelTestingResults.ps1 -WhatIf -Days 3650` reports the scratch path (after Step 18; before it, the script reports `Results root not found` with the scratch path - a clean failure).

### Step 10 - Help-text and example sweep

- Done: 006b332f
- Files: `scripts\Backfill-TranscriptSummary.ps1` (`:41, 44`), `scripts\Get-NotFoundRecoveryReport.ps1` (`:49, 50`), `scripts\Parse-AgentLog.ps1` (`:46, 59, 63, 70, 71`), `scripts\roslynsentinel-interrogate.ps1` (`:80, 88, 95`), `scripts\Get-BomAudit.ps1` (`:24`), `scripts\Get-EolAudit.ps1` (`:32`), `scripts\Probe-OvernightLogprobs.ps1` (`:19, 233`), `scripts\Get-ShippingKindTypoCounts.ps1` (help lines `:25, 54, 58`).
- Depends on: 5a, 5b, 9 (same file as the ShippingKind default). Lane: implementer-sweep (one operation: rewrite a path in comment/help text; 8 files, about 22 sites; within sweep tier 0: 1 op, 8 files, 25 sites). Tool: `Edit` per site (non-C#). `Get-TodoMarkers.ps1:8` (a comment) is left as is to stay at 8 files.
- Effect: text-neutral.
- Change rules: `ModelTestingResults\<...>` in examples -> `<scratch>\ModelTestingResults\<...>` and one line under `.EXAMPLE` or `.NOTES` saying `<scratch>` is `ROSLYNSENTINEL_SCRATCH_ROOT` or `..\RoslynSentinel-ScratchDir` (only where no such line exists yet);
  `.scratch\bom.csv` / `.scratch\eol.csv` -> `<scratch>\bom.csv` / `<scratch>\eol.csv`; `.scratch/chat_template_probe.txt` and `.scratch/chat_template.txt` -> `<scratch>\probe-inputs\...` (folder exists; leave its contents).
- Done when: `Select-String -Path scripts\*.ps1 -Pattern '\.scratch[\\/]'` returns no hits; `Select-String -Path scripts\Get-ShippingKindTypoCounts.ps1 -Pattern 'ModelTestingResults\\113'` shows only `<scratch>`-prefixed lines; all eight files parse.

### Step 11 - Drop the `.scratch` fallback in the launcher test

- Done: dfa819f4
- Files: `RoslynSentinel.Tests.Tools.Basic\TestInstanceLauncherTests.cs`.
- Depends on: none. Lane: implementer (1 file, 2 edits; `ReplaceSnippet`).
- Effect: text-neutral.
- Change: `:438` message becomes `No built RoslynSentinel.Server.Advanced output found under .roslynsentinel/build; run Build first.`; in `FindBuiltServerOutput` (`:565-580`) delete the second array element (`.scratch\roslynsentinel-builds`) leaving a one-element roots array. `BuildIsolationTests.cs:59, 90` stay (they guard against reintroducing `.scratch` in messages).
- Done when: `Build` 0 errors; `Search(mode: text, query: "\\.scratch", fileGlob: "RoslynSentinel.Tests.Tools.Basic/**/*.cs")` shows only the two `BuildIsolationTests` lines.

### Step 12 - `RuntimeStateRetention` bodies

- Done: 3840a054
- Files: `RoslynSentinel.Common\RuntimeStateRetention.cs`.
- Depends on: 1a. Lane: implementer-senior (1 file, 4 method bodies with concurrency-tolerant deletes and a count-cap rule; the safety of operation-blob deletion is the point of the slice).
- Effect: text-neutral (no callers until Step 13).
- Change: model on `LargeResultHelper.SweepExpired` (`LargeResultHelper.cs:95-117`): `LastWriteTimeUtc` based, `utcNow` seam, never throws, `IOException`/`UnauthorizedAccessException` skipped per file, returns the number of files (or folders) deleted.
  - `SweepOperations(solutionRoot, maxAge, maxCount, utcNow)`: folder `<root>\.roslynsentinel\operations`, top directory only, `*.json`. Blobs newer than `OperationsProtectedAge` (24 h) are never deleted. Delete blobs older than `maxAge`; then, if the survivors exceed `maxCount`, delete the oldest survivors that are older than the protected age until the count is `maxCount`.
    Every deleted blob also deletes `Path.ChangeExtension(blob, OperationBlobWriter.UndoneMarkerExtension)`; an `.undone` marker with no blob and older than `maxAge` is deleted too. Return the number of blobs deleted.
  - `SweepValidation(solutionRoot, maxAge, utcNow)`: `<root>\.roslynsentinel\validation`, `validation_*.json` older than `maxAge`.
  - `SweepBuildInstances(buildRoot, currentInstanceId, maxAge, utcNow)`: every immediate subfolder of `buildRoot` except `currentInstanceId` whose newest write time (the folder, its children, and its children's children - a depth-2 scan, because a rebuild changes files deep in `bin`/`obj` without touching the instance folder) is older than the cutoff is deleted recursively. The `.gitignore` file in the root is never touched. Return the number of folders deleted.
  - `SweepLogs(logDirectory, fileStem, currentLogPath, maxFiles, maxAge, utcNow)`: files matching `{fileStem}*.log` directly in `logDirectory`, never `currentLogPath`, never `crash.log`: delete those older than `maxAge`; then, if `others + 1 > maxFiles`, delete the oldest others until `others + 1 == maxFiles`. Return the number deleted.
  Every `// TODO implement (Step 12)` marker is removed.
- Done when: `Build` 0 errors; `Search(mode: text, query: "NotImplementedException", fileGlob: "RoslynSentinel.Common/RuntimeStateRetention.cs")` returns 0 hits.

### Step 13a - Load-time wiring (operations, validation, build instances)

- Done: 88477c27
- Files: `RoslynSentinel.Common\PersistentWorkspaceManager.cs`, `RoslynSentinel.Engines.Basic\BuildIsolation.cs`.
- Depends on: 12. Lane: implementer (2 files, about 4 edits).
- Effect: behaviour-changing: old blobs disappear after the restart (see Risks for thresholds).
- Change: `PersistentWorkspaceManager`: add `private void SweepRuntimeStateInBackground(string? solutionRoot)` beside `SweepLargeResultsInBackground` (`:1061`), same shape (`Task.Run`, try/catch logging a warning, information log with the three counts) calling `SweepOperations(solutionRoot, OperationsMaxAge, OperationsMaxCount)` and `SweepValidation(solutionRoot, ValidationMaxAge)`; call it right after `SweepLargeResultsInBackground(GetSolutionRoot())` at `:1021`.
  `BuildIsolation.GetArtifactsPath` (`:64`): after `PruneDirectoriesOfExitedProcesses(root, instanceId)` call `RuntimeStateRetention.SweepBuildInstances(root, instanceId, RuntimeStateRetention.BuildInstanceMaxAge)` inside a `try`/`catch (Exception)` that swallows (a cleanup must not fail a build); update the `// TODO Part 3` comment at `:345` to say named instance dirs are now swept by age.
- Done when: `Build` 0 errors; dispatcher runs `LargeResultLoadSweepTests` and `BuildIsolationTests` unchanged (regression).

### Step 13b - Log wiring and missing-blob error text

- Done: d6767e6f
- Files: `RoslynSentinel.Server.Advanced\ServerStartupHelpers.cs`, `RoslynSentinel.Tools.Basic\WorkspaceFileEditImpl.cs`, `RoslynSentinel.Tools.Basic\WorkspaceReadNavigationImpl.cs`.
- Depends on: 12. Lane: implementer (3 files, 4 edits).
- Effect: behaviour-changing: old server logs are deleted at startup; two error texts gain one fact.
- Change: in `ConfigureStdioLogging` (`:623`) and `ConfigureHttpLogging` (`:646`), before `Log.Logger = ...`, call `RuntimeStateRetention.SweepLogs(Path.GetDirectoryName(logPath)!, Path.GetFileNameWithoutExtension(logFileName), logPath, RuntimeStateRetention.LogMaxFiles, RuntimeStateRetention.LogMaxAge)` wrapped so it cannot throw; add a one-line comment that Serilog's `retainedFileCountLimit` does not apply to `RollingInterval.Infinite`.
  Append one fact sentence to the two missing-blob messages (`WorkspaceFileEditImpl.cs:360-366`, `WorkspaceReadNavigationImpl.cs:1644`): `Operation blobs older than 14 days, and all but the newest 5000, are deleted when a solution loads (the newest 24 hours are always kept).` (facts only, no policy; build the numbers from `RuntimeStateRetention.OperationsMaxAge.TotalDays` and `RuntimeStateRetention.OperationsMaxCount` rather than literals).
- Done when: `Build` 0 errors; dispatcher runs `UndoLastApplyTests`, `ToolHintRatchetTests`, `ServerInstancePathsTests`.

### Step 14 - Retention tests

- Done: b0d073da
- Files: `RoslynSentinel.Tests.Basic\RuntimeStateRetentionTests.cs` (new; same temp-root pattern as `LargeResultSweepTests.cs`), `RoslynSentinel.Tests.Tools.Basic\LargeResultLoadSweepTests.cs` (add one load-time test, modelled on its existing test).
- Depends on: 13a, 13b. Lane: test-writer.
- Effect: text-neutral.
- Change: `SweepOperations`: deletes an 15-day-old blob and its `.undone`; keeps a 13-day-old blob; keeps a 12-hour-old blob even when over `maxCount`; trims oldest survivors to `maxCount`; deletes an orphan `.undone` older than `maxAge`; ignores subfolders and non-matching names; missing folder returns 0; held-open file is skipped without throwing (mirror `SweepExpired_FileHeldOpen_IsSkippedWithoutThrowing`).
  `SweepValidation`: age rule. `SweepBuildInstances`: removes a stale named dir, keeps the current id, keeps a dir whose only fresh file is two levels down, never touches `.gitignore`. `SweepLogs`: keeps current, deletes older than `maxAge`, trims to `maxFiles`, ignores `crash.log` and other stems.
  Load-time test: `LoadSolution` on a temp solution root holding a 20-day-old operation blob and a 20-day-old validation blob removes both (wait with the same bounded poll the existing test uses).
- Done when: `RunTest` filter `RuntimeStateRetentionTests|LargeResultLoadSweepTests|LargeResultSweepTests` passes (dispatcher).

### Step 15a - Hygiene guard script

- Done: 17eefd51
- Files: `scripts\Test-RepoHygiene.ps1` (new), `scripts\Test-RepoHygiene.Tests.ps1` (new; throwaway git repo in a temp folder, `Invoke-Git` idiom from `roslynsentinel-evalqueue.TestHarness.ps1`).
- Depends on: none. Lane: implementer (2 new files).
- Effect: text-neutral (read-only report).
- Change: `param([string]$RepoRoot, [switch]$Quiet, [switch]$Strict)`; `$RepoRoot` defaults to the parent of `$PSScriptRoot`. Native `git` only, read-only. Output lines, each prefixed with a code, nothing when clean; exit 0 always, 1 only with `-Strict` and findings (same report-not-gate stance as `Test-GuidanceHygiene.ps1`):
  - `UNEXPECTED-ROOT <name>`: any top-level entry (file or folder, ignored or not) that is neither in `git ls-tree --name-only HEAD` nor in the `$KnownIgnoredRoot` list defined at the top of the script (`.git`, `.vs`, `.vscode`, `.idea`, `.claude`, `.roslynsentinel`, `global.json`; comment: add here, with a reason, only what a tool legitimately creates at the root). `global.json` is verified ignored by the blanket `*.json` (`.gitignore:10`) and absent from `git ls-tree --name-only HEAD`, so without this entry a clean tree would report it.
  - `UNTRACKED <path>`: each line of `git ls-files --others --exclude-standard` whose path has no `bin`/`obj` segment and no `Worktree` segment (a single note line `may belong to a peer session sharing this tree` is printed once when any exist).
  - `IGNORED-DATA <n> files, e.g. <first 3>`: ignored `*.json`/`*.csv`/`*.trx` files from `git ls-files --others --ignored --exclude-standard --directory` (entries ending in `/` are collapsed ignored folders and are skipped), outside `bin`, `obj`, `TestResults`, `.claude`, `.roslynsentinel`, `.vs`, `.vscode`, and not in the `$KnownIgnoredData` list at the top of the script: `global.json` and `RoslynSentinel.Server.Advanced/Properties/launchSettings.json` (both verified ignored by `*.json` on a clean tree; add here, with a reason, only what a tool legitimately keeps). `--directory` is required for speed: measured 0.10 s on this repo against about 6.3 s without it (134k ignored files), and the hook timeout is 15 s; root data files such as `global.json` are still listed.
  Test cases: clean repo prints nothing; an extra root folder, an extra root file, an untracked `.cs`, an untracked file under `obj` (not reported), an ignored root `data.json` (reported as IGNORED-DATA), `-Strict` exit code, `-Quiet` suppresses the header, and a throwaway repo with an ignored `global.json` and `Properties/launchSettings.json` stays silent.
- Done when: `pwsh scripts\Test-RepoHygiene.Tests.ps1` exits 0 (dispatcher). Live run before Step 18 is expected to report only the legacy folders (and any peer-session untracked files); it must be clean after Step 19, which is why the two allowlists above are part of this step.

### Step 15b - SessionStart warning hook

- Done: 13788ee9
- Files: `.claude\hooks\repo-hygiene-session-start.ps1` (new), `.claude\settings.json` (append a second command object to the `hooks` array of the existing `SessionStart` entry - do not add a second `SessionStart` entry - `timeout: 15`, same `powershell.exe -NoProfile -ExecutionPolicy Bypass -File ${CLAUDE_PROJECT_DIR}/.claude/hooks/...` form as `journal-session-start.ps1`).
- Depends on: 15a. Lane: implementer (2 files).
- Effect: behaviour-changing: a session start prints a warning when the repo root has unexpected entries.
- Change: the hook reads the SessionStart JSON from stdin, acts only for `source` `startup` or `clear` (not `resume`/`compact`, which fire repeatedly), runs `scripts\Test-RepoHygiene.ps1 -Quiet`, and prints at most 15 lines as `Repo hygiene (scripts/Test-RepoHygiene.ps1): <n> finding(s) ...` with the findings and the reminder that untracked files may belong to a peer session. Fail-open: any error prints nothing and exits 0 (same contract as `journal-session-start.ps1`). Prints nothing when clean, so a clean repo costs no context.
  Justification for SessionStart over `build.ps1`: it runs once per session when the agent can act on it, costs nothing when clean, and cannot fail a build for another session's untracked file; `build.ps1` runs inside many sessions' tool calls and a repo-hygiene failure there would be noise attached to an unrelated gate.
- Done when: `echo '{"source":"startup","session_id":"x"}' | pwsh -NoProfile -File .claude\hooks\repo-hygiene-session-start.ps1` runs and exits 0 (prints the warning before Step 19, nothing after); `.claude\settings.json` parses (`Get-Content -Raw | ConvertFrom-Json`).

### Step 16a - Docs sweep: model-eval procedure
- Files: `docs\current\references\reference_model_eval_procedure.md` (16 sites at `:127, 131-133, 135, 139, 178, 188, 212, 228, 251, 266, 271, 288, 294, 399`).
- Depends on: 5a, 5b, 6. Lane: implementer-sweep (1 file, 16 sites, one operation: path rewrite via `Edit`).
- Effect: text-neutral.
- Change rules: repo-relative `ModelTestingResults` -> `<scratch>\ModelTestingResults`; `_scratchbuild_<suffix>` -> `<scratch>\build\<suffix>` (pinned checkouts: `<checkout>\_scratchbuild_<suffix>`); `ROSLYNSENTINEL_RESULTS_ROOT` text says it overrides the scratch default.
- Done when: `Select-String -Pattern 'ModelTestingResults'` hits in the file all carry `<scratch>` or `RESULTS_ROOT` context; no bare `_scratchbuild_<suffix>` left except the pinned-checkout form.

### Step 16b - Docs sweep: five more live references
- Files: `docs\current\references\reference_model_testing_results_archiver.md` (11 sites), `reference_parse_agent_log_script.md` (`:34, 37, 42, 49`), `reference_interrogate_script.md` (`:8, 32`), `reference_log_sources_and_cross_referencing.md` (`:62, 137`), `docs\current\design\design_subagent_tool.md` (`:451`).
- Depends on: 5a, 5b, 8a. Lane: implementer-sweep (5 files, about 20 sites, same rewrite rules as 16a plus `RoslynSentinel-TestRuns` -> `<scratch>\TestRuns`).
- Effect: text-neutral.
- Change: as 16a. The archiver reference also states where the archiver resolves its base (now `ScratchRoot.ResultsRoot()`; `RESULTS_ROOT` override) and that the walk-up is replaced by `RepoRootLocator`.
  Leave `reference_lmstudio_reasoning_budget_message.md:20` and `issue_tool_hygiene_open_items.md:34` (dated evidence quotes of run folders).
- Done when: `Select-String -Pattern 'RoslynSentinel-TestRuns' -Path docs\current\references\*.md, docs\current\design\*.md` returns no hits.

### Step 16c - New prose: scratch root, retention and hygiene
- Files: `docs\current\references\reference_working_conventions.md` (new section "Scratch root and repo hygiene"), `docs\current\references\reference_live_server_freshness.md` (one sentence near `:26` and `:50` on build-instance and log retention), `docs\current\references\reference_model_eval_procedure.md` (a short "Where output goes" section: scratch root, `ROSLYNSENTINEL_SCRATCH_ROOT`, `ROSLYNSENTINEL_RESULTS_ROOT`, `ROSLYNSENTINEL_REPO_ROOT`, checkout-local pinned builds, `C:\rs-eval` kept).
- Depends on: 16a, 16b, 13b, 15b, 8a. Lane: implementer (3 files, 3 prose additions).
- Effect: text-neutral.
- Change: working conventions: the layout table, the resolution rule, the retention table (largeresults 7 d; operations 14 d / 5000 / newest 24 h kept; validation 7 d; build instances 7 d; server logs 14 d / 50 files), the hygiene guard and where it runs, and the rule "no tool writes into the repo root; a new output folder goes under `<scratch>`".
  Do not read other docs for style (templates README rule 1); this is a reference, copy `docs/current/templates/reference.md`. Do not edit CLAUDE.md (main session).
- Done when: `pwsh scripts\Test-GuidanceHygiene.ps1` shows no new `LINK-FORM` or broken-link findings for these three files.

### Step 17 - Stub scan, full build, commit, restart (main session / dispatcher; not an implementer)
- Depends on: all implementer steps.
- Effect: verification.
- Change: (1) `Search(mode: text, query: "NotImplementedException", fileGlob: "RoslynSentinel.Common/Scratch*.cs")`, same for `RepoRootLocator.cs` and `RuntimeStateRetention.cs`: zero hits, else fail. (2) `Build` default level 0 errors, then `scripts\build.ps1 -Flavor Solution` for the additive-layering check (new files are in Common; Advanced must still build). (3) Run in this order, one at a time:
  `ScratchRootTests|RepoRootLocatorTests`, `RuntimeStateRetentionTests`, `LargeResultSweepTests`, `LargeResultLoadSweepTests`, `ModelTestingResultsArchiverTests`, `RunStampTests`, `SubAgentLaunchTests`, `TestInstanceLauncherTests`, `BuildIsolationTests`, `ServerInstancePathsTests`, `UndoLastApplyTests`, `ToolHintRatchetTests`, `PlanStepRunnerSurfaceTests`;
  `pwsh scripts\roslynsentinel-scratch.Tests.ps1`, `...evalqueue.Tests.ps1`, `...evalqueue.Preflight.Tests.ps1`, `...evalqueue.Hosts.Tests.ps1`, `...evalqueue.Visibility.Tests.ps1`, `...evalruns.Tests.ps1`, `scripts\Test-RepoHygiene.Tests.ps1`. Compare with the baselines (`docs/known-failing-tests.*.txt`, currently 0 failures: any failure is new).
  (4) Commit (listed scope, explicit files, `Plan:` trailer), then restart this session's server (`McpServerControl StopServer`, `LoadSolution`) with no subagent mid-operation; confirm `lastLaunch.servedSha` is the new HEAD.
  (5) Live checks after the restart (outcome classes: ideal / limit / regression): the next `LoadSolution` deletes operations older than 14 days (count under `.roslynsentinel\operations` drops from about 14,250 toward the newest 5,000 and nothing newer than 24 h is gone: ideal; count unchanged: limit, sweep not reached; any blob under 24 h missing: regression); a new log file appears in the server `logs` folder and the folder is at most 50 files; `UndoLastApply` of a change made this session still works (undo path unaffected: ideal) - do one tiny `Member` edit and undo it.
- Done when: all of the above pass and the commit hash is recorded in the plan Status.

### Step 18 - Data migration (main session only; no implementer, no eval/test/queue process running)
- Depends on: 17 committed and the server restarted.
- Effect: behaviour-changing (moves 4.8 GB, same volume, so `Move-Item` is a rename).
- Preconditions (all must hold, else stop): `pwsh scripts\roslynsentinel-evalqueue.ps1 status` shows no Running/Building job on any host (a worker exits when its queue is empty and reloads the script on the next start); `Get-Process testhost, dotnet -ErrorAction SilentlyContinue` has none whose path is under `_scratchbuild*`; `git worktree list` captured to a scratch note.
- Change, in order (PowerShell tool; `git worktree move` is an allowed shell git operation):
  1. Create `<scratch>\build`, `<scratch>\TestRuns`, `<scratch>\TestResults` (`<scratch>` = `..\RoslynSentinel-ScratchDir`, which already holds `probe-inputs\`; leave that alone).
  2. Registered worktree first: the one inside `..\RoslynSentinel-TestRuns\PlanStepRunner\20261110-112338-062\02-phase1-types-and-engine-fix\Worktree` -> create the destination parent under `<scratch>\TestRuns\PlanStepRunner\20261110-112338-062\02-phase1-types-and-engine-fix\` and run `git worktree move <old> <new>`; re-check `git worktree list` for any other entry under `RoslynSentinel-TestRuns` (list at plan time: exactly one). If the move is refused (locked), `git worktree remove --force` it only after the owner confirms its run is abandoned.
  3. `Move-Item ModelTestingResults <scratch>\ModelTestingResults` (if the target exists, move the children instead; never overwrite).
  4. `_scratchbuild_112 -> build\112`, `_scratchbuild_113 -> build\113`, `_scratchbuild_85 -> build\85`, `_scratchbuild_ev_112 -> build\ev_112`, `_scratchbuild_ev_113 -> build\ev_113`. First build after the move may rebuild from scratch (absolute paths inside `obj`); if a build errors on stale paths, delete that one build dir and rebuild (cold build, minutes).
  5. Children of `..\RoslynSentinel-TestRuns\` (`CreateFileProbe`, `PlanStepRunner`, `SubAgent`, `SubAgentSmoke`) -> `<scratch>\TestRuns\`; remove the then-empty `..\RoslynSentinel-TestRuns`. `git worktree prune`, then `git worktree list` shows the moved worktree at its new path.
  6. `TestResults\parallel-run -> <scratch>\TestResults\parallel-run`; remove the empty repo `TestResults\`, and the empty `.scratch\`.
  7. Delete the legacy folders `_scratchbuild`, `_scratchbuild_192_168_1_85` and the six `_scratchbuild_http___*` (untracked, ignored; `Remove-Item -Recurse -Force`). Optional: delete `.roslynsentinel\scans` (stale since 2026-08-27, no writer in source; confirm with `Search(mode: text, query: "\"scans\"")` first).
  8. Smoke eval via the queue, one Quick job on one host (`roslynsentinel-evalqueue.ps1 submit ...`): outcome classes - results folder under `<scratch>\ModelTestingResults\<suffix>` and build dir under `<scratch>\build\<suffix>` (ideal); results in the repo (regression: Step 3a/6); build dir in the repo (regression: Step 5a/5b/6); run stamp missing in `transcript.json` (limit: `RepoRootLocator` env not reaching the testhost, Step 5a/5b).
- Done when: `Test-Path` is false for `ModelTestingResults`, `_scratchbuild*`, `.scratch`, `TestResults`, `..\RoslynSentinel-TestRuns`; `pwsh scripts\Test-RepoHygiene.ps1` prints no `UNEXPECTED-ROOT`; the smoke run lands in scratch.

### Step 19 - Remove obsolete ignores, final hygiene run, close (main session)
- Files: `.gitignore`.
- Depends on: 18. Effect: closing.
- Change: delete `_scratchbuild/`, `_scratchbuild_*/`, `ModelTestingResults/`, `/.scratch` (keep `TestResults/`: `dotnet test` still writes per-project `TestResults` such as `RoslynSentinel.Tests.Basic\TestResults\`; keep the `*.json`/`*.csv` blanket ignores, see Risks). Run `pwsh scripts\Test-RepoHygiene.ps1`: no findings. Commit with `Closes: plan_scratch_root_and_repo_hygiene`, set Status to IMPLEMENTED with hashes, move this plan to `plans/closed/` and the `docs/current/TODO.md:53` READY item (errorvariants RESULTS_ROOT) to `CLOSED.md` (`ProjectDoc action: close`). CLAUDE.md one-liner is the main session's own task, not this plan's.
- Done when: `git status` clean apart from other sessions' files; hygiene guard silent.

## Out of scope

- Folding the duplicated `ModelTestingResultsArchiver` classes (TODO + INBOX line only, Step 3a).
- Moving the pinned eval checkouts root (`C:\rs-eval`) or the `_scratchbuild_<suffix>` folder inside a checkout.
- Server `RoslynSentinelInstances` layout, its builds and instance-folder sweep (separate plan), `largeresults` retention (already exists), telemetry/`schema-sizes` and `test-instances` folders (small).
- Editing the walk-up `FindRepoRoot` copies in test projects that always run from in-repo `bin` (Tests.Basic, Tests.Server, Tests.PlanStepRunner, Tests.Tools.*), and `ServerBinaryStaleness.FindRepoRoot` (returns null for an out-of-repo binary and reports "not stale", which is the intended fail-open).
- The stale comment at `scripts\Get-TodoMarkers.ps1:8` (names `ModelTestingResults` in its exclusion sentence; no logic change needed, kept out of Step 10 to stay at the sweep tier's 8-file cap).
- Editing historical docs, CLAUDE.md, `tool-comparison\tasks.psd1:21` (its `_scratchbuild[^/]*` exclude regex is harmless and still matches pinned-checkout build folders).
- Replacing the blanket `*.json` / `*.csv` ignores (Risks).

## Risks and open decisions

1. **Operation-blob thresholds** (recommend: 14 days, newest 5,000, newest 24 h always kept). Evidence: only `UndoLastApply`/`GetOperationDetail` read old blobs, by `changeId` from the agent's own transcript; `NotFoundEvidence` and `EditEffectSeeder` read the last 60 minutes. Files are about 60 KB on average; 14 days is about 5,800 files today (the Nov 6-20 days), so the count cap bites only on a burst. Options: 7 days (smaller, matches `largeresults`, but a Monday undo of Friday work fails), 30 days (about 10,000 files, 600 MB, little gain). A sweep removes a blob and its `.undone` marker together. The error text now states the retention so a missed undo is explainable.
2. **Validation 7 d, build instances 7 d, logs 14 d / 50 files** (recommend as written). No reader of `validation\` exists in source (only the writer and the filename in tool results). Build instance age is measured over a depth-2 newest-write scan so an active test instance is never swept; a swept instance only costs a rebuild. Logs: five weeks of history is not needed; crash logs are separate (`crash.log` is never swept).
3. **Pinned-checkout build path** (recommend: keep checkout-local `_scratchbuild_<suffix>` and add the checkout `.git\info\exclude` entry in Step 6). Alternatives: (a) `<scratch>\build\<suffix>-<sha12>` for pinned jobs - needs a new env var because old-commit scripts compute their own path, adds about 3 chars of path and unbounded folders to retire; (b) keep one `_scratchbuild_*/` line in `.gitignore` forever - simplest, but the repo then still advertises the old writer and the hygiene guard cannot tell a regression from a pinned checkout.
4. **`Get-EvalCheckoutRoot` not folded** (recommend not folding; brief said fold if unsure it is outside the repo). It is already outside the repo (`C:\rs-eval`) and short on purpose.
5. **Path length under the new TestRuns root.** `<scratch>\TestRuns\SubAgent\<28-char runId>\subagent\Worktree\...` is 11 characters longer than `..\RoslynSentinel-TestRuns\SubAgent\...` (`issue_tool_hygiene_open_items.md:34` already lists path length as a candidate cause of a SubAgentEval failure). Mitigation: `ROSLYNSENTINEL_SCRATCH_ROOT` set to a short path (for example `C:\rs-scratch`) on any box that hits it. Not changed by this plan.
6. **A relative `ROSLYNSENTINEL_SCRATCH_ROOT` now throws** in both resolvers, while the old C# archivers silently fell back to the repo. `modeleval.ps1` validates up front (the archivers catch and log their own exceptions), SubAgent launch returns a structured `InvalidArgument` (Step 8a). Judged right: a silent fallback would put output back in the repo.
7. **Testhost repo-root dependency.** Moving the build out of the repo removes the implicit "repo above bin" fact for ModelEval and ErrorVariants testhosts. The plan fixes the six known walk-ups and exports `ROSLYNSENTINEL_REPO_ROOT` from the two scripts, but a testhost started directly (VS Test Explorer) with a scratch artifacts path and no env var gets a clear `DirectoryNotFoundException` naming the variable, not a silent null. The git stamp is the exception (it returns null by design); Step 18's smoke run checks the stamp is present.
8. **Blanket `*.json` / `*.csv` ignores** (recommend: leave as is). Replacing them with location-based ignores means auditing every tracked-by-negation json/csv (`!.claude/settings.json`, `Resources/Golden`, `ResumeTranscripts`, `TestData`, `scripts/eval-hosts.json`) and risks silently dropping a new data file from a commit either way. The hygiene guard's `IGNORED-DATA` check covers the visibility gap at no risk. Revisit only if it reports noise.
9. **Hygiene noise from peer sessions.** Other sessions share this tree; their untracked files will show as `UNTRACKED`. The hook wording says so and never blocks; `-Strict` exists only for manual use.
10. **Queue jobs queued before this change** lack `scratchRoot`; `Get-JobScratchRoot` derives it from `sourceRepoRoot`, so they run correctly. Workers load the script at start, so migration (Step 18) waits for an empty queue.
11. **SubAgent/PlanStepRunner runs already in flight** keep their old folder; a PlanStepRunner `-ExistingRun <ts>` of a pre-change run looks under the new TestRuns root, so Step 18 must have moved the folders first (it does) and the one registered worktree is moved with `git worktree move`, not a file move.
12. **Cross-plan baselines:** none (no wire text or tool output shape changes except two error sentences in Step 13b; `ToolHintRatchetTests` is the guard).
