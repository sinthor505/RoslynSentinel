# Isolate the heavy test areas (Git, then Build/RunTest) into their own projects so their slow tests run only when they change

**Status:** ACCEPTED 2026-11-24 for Area 1 (Git): [plan_isolate_git_area_into_own_project.md](../plans/plan_isolate_git_area_into_own_project.md) (open questions 2, 4, 5 settled there). Areas 2 and 3 and the selection rule (owned by `plan_full_suite_nightly_and_tail_shard.md`) remain proposed.

## Motivation

Every implementation task ends with a full test run (`implement-plan/SKILL.md:81`, `todo-punchlist/SKILL.md:70`; D1 of
`plan_shorten_verification_loop.md`), so the task pays for the slow OutOfProcess fixtures even when it did not touch them. Run
`48fb08e2-20261124-111028-65836` (1,378 s, 12,082 tests, 8,134 summed test-seconds):

| Area | Fixtures | Summed test-seconds | Share | Commits touching its production files, last ~60 days (of about 3,300) |
| --- | --- | --- | --- | --- |
| Git (44 OutOfProcess + 5 in-process fixtures) | 49 | 4,516 | 55% | 110 (about 3.3%) |
| Build (`BuildEngineTests`, `BuildScopeFullBuildTests`, `BuildIsolationTests`, `BuildCascadeFixtureTests`, ...) | 11 | 48 | 0.6% | 28 engine (0.8%) |
| RunTest (`RunTestTests`) | 1 | 659 | 8.1% | 35 engine (1.1%) |
| Build + RunTest + Git via `repoPath` (`RepoPathWorktreeTests`) | 1 | 98 | 1.2% | (shared) |
| Fixtures that load a real solution from disk (all subjects) | 82 | 2,633 | 32% | PersistentWorkspaceManager.cs: 98 (3.0%) |
| of which PersistentWorkspaceManager mechanics (debounce, fold, stale-read, write lock, ...) | about 46 | about 250 | 3% | |

Notes on the numbers:

- `RunTestTests`' 659 s are mostly waiting on `BuildGate.Shared` (`BuildIsolation.cs:459`, taken at `TestRunEngine.cs:221`):
  its 16 spawning tests run concurrently and finish between 236 s and 318 s of the Tools.Basic timeline, about 80 s of wall.
- Churn denominators: about 3,300 first-parent commits in 60 days (`HEAD~3300` boundary, about 100 commits a day); Git's
  numerator is exact over the Git production files (`Git(operation: log, paths: ...)`), the others likewise.
- Today the slow fixtures are found by category (`[Category("OutOfProcess")]`, `TestSuiteFilter.cs`) and by file name. A Git
  change cannot be told from a non-Git change without a hand-kept path list, and Git's tests share
  `RoslynSentinel.Tests.Tools.Basic` (918 s wall, the full run's critical path) with everything else.

Premise check: the owner expected Build, RunTest and PersistentWorkspaceManager to change least. Build and RunTest do (1.6%
together). PersistentWorkspaceManager does not: it is the busiest production file of the four (98 commits, mostly feature
work: read ledger, stale-read, edit attribution, cross-process write lock), and its own mechanics tests are cheap (about 250 s).
Its cost is the real-disk solution load that other fixtures use as scaffolding.

## Proposal

### Principle

An area that is heavy to test and rarely changed gets (a) its own production project, so "did it change" is a project
question, not a file-name guess; (b) its own test project holding its slow fixtures, so selecting or skipping them is a
project selection, and the project runs as its own process in parallel runs; (c) thin tool wrappers, so an edit to tool
surface text does not count as a behaviour change of the area.

### Selection rule (applies to every area; decided independently of the splits)

`scripts/test-areas.json` (new; supersedes `scripts/oop-areas.json` from Step 5 of
`plan_full_suite_nightly_and_tail_shard.md`) maps each area test project to the production projects and the few shared files
it watches. A task-end verification run is the fast suite plus every area test project whose watched paths changed since the
marker's `headSha` (or are uncommitted); the nightly full run covers the rest. Before a split, an area's watch list is file
globs; after it, a project directory. Until the splits land, the glob form gives most of the saving for task-end runs, so
this rule should ship first (via the nightly plan's Steps 1-6).

### Area 1 - Git (do first: 55% of test-seconds, 3.3% of commits)

Inventory (2026-11-24): 36 production files, 13,925 lines. Common: 27 plumbing files (7,701 lines: `IGitReader` and its CLI,
libgit2 and fallback readers, `GitProcess`, `GitHooks`, `GitCommitMessage`, `InProcessCommitRoute`, `GitBackendWarmup`,
`RepoWorktrees`, `ShortHashLengths`, ...) plus `GitWorktree/` (3 files, 311 lines). Tools.Basic: `GitImpl.cs` 4,642 lines
(about 600 lines of wire DTOs and error codes, about 4,030 lines of Git behaviour), `GitHunkParser` 433, `GitStatusRenameHints`
413, `GitTools` 246, `GitRemoteTools` 149, `IGitOperations` 30. Git code uses only the BCL and LibGit2Sharp from below
(name search, not compiled), so it can sit on top of Common.

0. **First, inside Tools.Basic: take the tool-surface text out of `GitImpl` (decided 2026-11-24).** All 53 rendered tool
   calls (`ToolCallHint.Call(...)`) are in Tools.Basic (51 in `GitImpl.cs`, 2 in `GitTools.cs`); the Common plumbing has none.
   Three kinds, each with its own treatment:
   - The `GitErrorDetails` table (`GitImpl.cs:112-136`, about 16 texts keyed by `GitErrorCodes`): moves to the tool side as a
     lookup; the engine sets `Code`, the tool fills `ErrorDetail`.
   - About 20 inline error details with facts (existing tag hash `:1038`, stash entry counts `:1200`/`:1235`, worktree paths
     `:1684-1842`): the engine returns the facts in `[JsonIgnore]` fields plus a hint key; the tool renders the sentence.
   - About 15 success notes that point at other tools ("To undo: GitRemote(tag create...)" `:1113`, "call
     LoadSolution(forceReload...)" `:1363`, the LoadSolution note after a worktree add `:1742`): the engine sets a flag; the
     tool appends the note.
   The split (engine part plus a tool-side `GitHintText`, working name) is text-neutral: the existing pins
   (`CallableToolHintLiveSurfaceTests`, the long-message pins, the tool-call hint ratchets) fail on any drift. It also makes
   the later move mechanical (the engine names no tools) and means a hint-text edit touches only the tools, never the Git
   area. New `[JsonIgnore]` fields follow the `dto-conventions` skill.
1. **New project `RoslynSentinel.Git`** between Common and Engines: `Common <- Git <- Engines.* <- Tools.* <- Server.*`. It
   holds the Common plumbing, `GitWorktree/` (Open question 1, resolved), `GitHunkParser`, and the behaviour part of
   `GitImpl` as an engine (working name `GitEngine`, behind the existing `IGitOperations` seam). The wire DTOs (`GitResult`,
   `GitStatusResult` and the other 27 records, `GitImpl.cs:11-611`) move with the engine, unchanged on the wire, because the
   engine cannot return Tools.Basic types; the tools fill `ErrorDetail` and the notes after the call. `GitTools`,
   `GitRemoteTools`, `GitStatusRenameHints` and `GitHintText` stay in Tools.Basic. LibGit2Sharp moves from
   `RoslynSentinel.Common.csproj:11` to the new project. Namespace stays `RoslynSentinel.Common` for the moved plumbing and
   `RoslynSentinel.Common.GitWorktree` for the worktree harness (every project already has
   `<Using Include="RoslynSentinel.Common" />`, so zero `using` ripple; `AgentLoop` is the precedent for a sub-namespace if
   wanted later).
2. **Break the three Common-to-Git edges** with a narrow seam left in Common: `ServerBinaryStaleness.cs` (uses
   `GitReaders.Default`, `WorkingTreeChange`, `ChangedCommit`), `NotFoundEvidence.cs:469` (`ReadBlobAtHead`),
   `PersistentWorkspaceManager.cs:1036` (`GitBackendWarmup.Shared.StartRepoProbe`). Together they use four reader methods
   (`GetHeadSha`, `GetCommitsSince`, `GetStatus`, `ReadBlobAtHead`) and the probe; a Common interface (working name
   `IRepoQuery`) plus a probe hook, set by Server at startup (`ServiceRegistrationExtensionsBasic.cs:503` already starts the
   warmup there). The two records move to Common or the interface returns Common-side records.
3. **Thin tools.** Git behaviour leaves Tools.Basic; the tool classes keep validation, dispatch, result shaping, findings and
   (after item 0) all hint text (`GitTools.cs` is already about 230 of 246 lines of tool concern).
4. **New test project `RoslynSentinel.Tests.Git`** with the 44 OutOfProcess Git fixtures and their support files
   (`CasCommitTestSupport`, `GitCommitMessageTestSupport`, `GitHookTestSupport`, `GitPatchParityHarness`). `GitTestRepo`
   (`Tests.Support.Basic`, 91 references in 58 files across four test projects) stays shared. `ChildProcessCaptureTests` is not
   Git and stays. Fixtures that construct `GitImpl` (17 files, 22 sites) move to the engine where they test behaviour.
5. **Wiring:** `InternalsVisibleTo` from the Git project to Engines.Basic, Tools.Basic, Tests.Git, Tests.ModelEval and
   Tests.Harness (most Git types are `internal`), and from Common to Git; `ProjectReference`s from Engines.Basic, Tools.Basic,
   Tools.Advanced (worktree harness users `SubAgentChildServerLauncher`, `SubAgentEvalImpl`, `SubAgentRunNaming`; reachable
   transitively, explicit for clarity), Server.Advanced, `Utilities.PlanStepRunner` (`Program.cs`, `PlanStepFile.cs`,
   `RunnerOptions.cs`) and the test projects (`Tests.Harness` for `WorktreeTeardownTests`); `RoslynSentinel.slnx`; regenerate
   `docs/generated/architecture_projects.md` (`ArchitectureDocFreshnessTests`); update the CLAUDE.md layering lines (the Git
   layer, and "Utilities.PlanStepRunner references only Common and AgentLoop" gains "and Git") and
   `reference_architecture_map.md`. PlanStepRunner then carries LibGit2Sharp's native library; `Directory.Build.targets`
   already trims it to win-x64.

### Area 2 - Build and RunTest (second: about 800 summed test-seconds, 1.6% of commits)

Inventory: `BuildEngine` 457, `BuildIsolation` (with `BuildGate`) 515, `TestRunEngine` 789, `FullTestMarkerStore` 217,
`TestSuiteFilter` 100, `TestRunResultProjector` 79, `TestRunOutcome` 23 (Engines.Basic); `BuildResult`, `BuildResultProjector`,
`FullSuiteStalenessOptions` (Common).

1. **New project `RoslynSentinel.Engines.BuildTest` above Engines.Basic** (precedent: `Engines.Advanced` references
   Engines.Basic, `Engines.Advanced.csproj:15`), because `BuildEngine.RunQuickBuildAsync` needs `DiagnosticEngine`
   (Engines.Basic). It holds `BuildEngine`, `BuildIsolation`/`BuildGate`, `TestRunEngine` and its result types,
   `TestSuiteFilter`, `TestRunResultProjector`, `TestRunOutcome`, `FullTestMarkerStore`, and `TestInstanceLauncher` plus
   `KillOnCloseJob` (`TestInstanceLauncher` calls `BuildEngine.RunFullBuildAsync`, `TestInstanceLauncher.cs:318`).
2. **Stays below:** `BuildResult`, the build/test exceptions and enums (Common; used by `DiagnosticReport`,
   `WorkspaceHealthReport`, `DiagnosticEngine`); `TestProjectDetector` (used by `DiscoveryEngine`, navigation) moves to Common
   as the `SymbolWireResolver.cs:206` TODO already proposes; `BuildIsolation.RemoveInheritedMsBuildPins` (pure static, used
   by `SolutionRestore.cs:70`) drops to Common; `ChildProcessCapture` stays in Common.
3. **Inbound edges:** `IdleRestartMonitor.cs:83` (`BuildGate.Shared.IsBusy`, Server may reference the new project);
   Tools.Basic (`WorkspaceBuildTestImpl`, `WorkspaceHealthMiscImpl.cs:25,33`, `AdminTools.cs:41` construct `BuildEngine`).
   New `AddRoslynSentinelEnginesBuildTest` DI extension replacing `EngineServiceCollectionExtensionsBasic.cs:36,62,63`.
4. **Tool layer:** `WorkspaceBuildTestImpl.Build`/`RunTest` are already almost all tool concern; optional extraction of
   `GetTransitiveDependenciesAsync` and `WithUnrestoredProjectFindings` (about 140 lines). No tool class split (that would cost
   a registry entry, goldens and a fresh session for little gain).
5. **New test project `RoslynSentinel.Tests.BuildTest`** with the OutOfProcess Build fixtures, `RunTestTests` and
   `RepoPathWorktreeTests` (which watches Git too). The in-process engine tests (`TestRunOutcomeTests`, `TestSuiteFilterTests`,
   `FullTestMarkerStoreTests`, ... in Tests.Basic, under 1 s together) stay.

### Area 3 - PersistentWorkspaceManager: test-side only

Do not move the production class: it is in Common with the MSBuild/Roslyn workspace packages (`Common.csproj:15-20`), is the
write chokepoint, churns more than Git, and a move would need IVT for many of Common's 76 internal types plus a new DI home.
Its mechanics tests are about 250 s. The lever is the 82 fixtures (2,633 s; 1,730 s excluding `RunTestTests` and
`GitToolsSmokeTests`) that load a real solution from disk to test something else:

1. Tag or relocate the disk-loading fixtures (`TestSolutionFixture` users, 84 files in 8 test projects) so they are
   selectable (working name `[Category("RealWorkspace")]`, or a `Tests.RealWorkspace` project for the PWM mechanics fixtures
   only).
2. Move fixtures whose subject is not loading or disk behaviour to the in-memory tier, continuing
   `plan_in_memory_test_tier_for_battery.md` (455 fixtures already use the real manager with an in-memory solution, 3,265 s,
   no disk load).

## Alternatives considered, not pursued

- **`[Explicit]` on the slow tests.** They would run only when a filter names them, show as skipped elsewhere, and never run
  under an IDE "run all"; the category plus a selection rule does the same without those costs
  (`plan_full_suite_nightly_and_tail_shard.md` D1).
- **Category and file-glob selection only, no project split.** Ships first (Selection rule) and captures most of the
  task-end saving, but the glob list drifts as files are added (the concern recorded in D1 of
  `plan_shorten_verification_loop.md`), and Git stays on Tests.Tools.Basic's critical path.
- **Moving PersistentWorkspaceManager out of Common.** Highest cost of the three, lowest test payoff (Area 3).
- **A separate project per Build and per RunTest.** `TestRunEngine` depends on `BuildGate`/`BuildIsolation`, and together they
  are 1.6% of commits; one project is enough.
- **Affected-test selection by symbol (`proposal_runtest_tests_for_members.md`).** Finer-grained, but adds a resolver engine
  and a `RunTest` signature change; the area rule is coarse, cheap, and covers the heavy fixtures, which is where the time is.

## Open questions

1. **`GitWorktree/` and PlanStepRunner. Resolved 2026-11-24: option (a).** `GitWorktree/` moves to the Git project and
   PlanStepRunner gains a reference to it. Checked consumers: Tools.Advanced (3 files), PlanStepRunner (3 files),
   Tests.Harness (`WorktreeTeardownTests`), AgentLoop (a `<c>` doc mention only, no reference needed); no Common code uses it,
   so no cycle. The move also puts `GitWorktreeManager` in the same assembly as the internal `GitProcess` it calls
   (`GitWorktreeManager.cs:219`). Rejected: (b) keep it in Common with its own process call through `ChildProcessCapture`.
2. **Seam style for Common-to-Git.** A static provider set at startup (matches `GitReaders.Default` and `GitBackendWarmup.Shared`
   today) or DI injection into `ServerBinaryStaleness`, `NotFoundEvidence` and `PersistentWorkspaceManager`. Recommend the
   static provider for the move (no constructor changes; the open blocker
   `blocking_error_movemember_no_retype_tool_for_di_chain_call_sites.md` makes constructor retyping costly); DI later if wanted.
3. **Tool text inside Git behaviour. Resolved 2026-11-24: extract first** (Area 1, item 0). Rejected: moving `GitImpl` as-is
   with the rendered tool calls inside the engine, which would make every hint edit count as a Git change and put tool names
   in a library layer. Remaining detail for the plan: the hint-key shape (one enum per result kind, or one shared enum) and
   which `[JsonIgnore]` fact fields each result carries.
4. **Move mechanics.** `MoveFile` moves whole files across projects (namespace kept), but one at a time; the Common Git files
   reference each other and are referenced by remaining Common code, so a file-by-file move cannot stay compile-green between
   calls. Candidate route: `MoveFile` the Git files into a `RoslynSentinel.Common/Git/` folder (same project, green after each
   call), then `SplitProjectByFolder` (`WorkspaceTools.cs:297`, toolset `projectAdmin`). Unverified: how it handles
   `ProjectReference`s, IVT and package references. A spike decides; if the tool falls short, fix it or use an
   owner-authorized bypass for that one step (owner has offered bypasses where the net gain justifies them).
5. **Does the Git code use non-Git Common internals?** Not compiled; if so, Common needs IVT to the Git project (cheap).
6. **Build/RunTest placement.** Above Engines.Basic (recommended, because of `DiagnosticEngine`) or beside it with quick build
   left behind.

## Cost / risk

- **Git:** 36 production files (about 13,900 lines) and about 65 test files move; 8 or so `.csproj` files, `.slnx`, IVT lines,
  CLAUDE.md layering line, the architecture map and generated docs change. No `.csproj`/`.slnx` editing tool exists, so those
  are Edit/Write (allowed for non-C# files).
- **Build/RunTest:** about 9 production files, a new DI extension, 7 test fixtures; `TestProjectDetector` and
  `RemoveInheritedMsBuildPins` move down.
- **Path-keyed ratchets break on moves:** `BareFileWriteRatchetTests.cs:50-52` allowlist keys
  (`Engines.Basic/BuildIsolation.cs::GetArtifactsPath`, `.../FullTestMarkerStore.cs::Write`); update them in the moving slice.
- **Tool readiness:** `MoveType` is same-project only (gated `moveExtract`); `MoveFile` cross-project is new (2026-11-22);
  `SplitProjectByFolder` and `CreateProject` are gated (`projectAdmin`) and untested for this shape. Each gap found becomes a
  `TOOL-GAP` with a fix-or-bypass decision.
- **Concurrent sessions:** a move touches files other sessions edit; each move slice needs `HOLD` coordination and a clean tree.
- **Test coverage drift:** fixtures moving between projects change what `-Projects` runs and the per-project baselines
  (`docs/known-failing-tests.*.txt`); `Test-Parallel.ps1` discovers `RoslynSentinel.Tests*.csproj` by glob (new projects are
  picked up; verify).
- **Expected gain:** task-end runs skip about 4,500 summed test-seconds (Git) and about 800 (Build/RunTest) when those areas
  are untouched; with the projects split, full runs also lose the Git tail on Tests.Tools.Basic's critical path. Not measured.

## Related

- [plan_full_suite_nightly_and_tail_shard.md](../plans/plan_full_suite_nightly_and_tail_shard.md) (marker, nightly run, debt
  check; its Step 5 area list becomes `test-areas.json` here)
- [plan_shorten_verification_loop.md](../plans/plan_shorten_verification_loop.md) (D1 task-end rule; Appendix A serial tail)
- [plan_in_memory_test_tier_for_battery.md](../plans/plan_in_memory_test_tier_for_battery.md) (Area 3 direction)
- [proposal_runtest_tests_for_members.md](./proposal_runtest_tests_for_members.md)
- `docs/current/references/reference_architecture_map.md`; blockers `blocking_error_movemember_no_retype_tool_for_di_chain_call_sites.md`,
  `closed/blocking_error_movetype_cannot_relocate_namespace_type_across_projects.md`
