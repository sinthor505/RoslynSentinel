# Plan: Move the Build/RunTest area into its own production project and its own test project, and finish the Git namespace rename

**Status:** DRAFT 2026-11-26. ON HOLD for a roadmap (owner direction, last Decision entry): this draft will be split into a Git namespace rename plan,
a mechanical BuildTest move plan and a hint/fact extraction plan. plan-design phase 4: Q37 and Q38 open (they go to the extraction plan); 12 implementer defaults.

## Problem

[proposal_isolate_heavy_test_areas_by_project.md](../proposals/proposal_isolate_heavy_test_areas_by_project.md), Area 2. Area 1 (Git) was done by
[plan_isolate_git_area_into_own_project.md](closed/plan_isolate_git_area_into_own_project.md); the lessons and the tool route are in
[reference_splitting_functionality_out_of_common.md](../references/reference_splitting_functionality_out_of_common.md).

The Build and RunTest engines (`BuildEngine`, `BuildIsolation`/`BuildGate`, `TestRunEngine`, `TestSuiteFilter`, `FullTestMarkerStore`, ...) live in
`RoslynSentinel.Engines.Basic` and their slow OutOfProcess fixtures (`RunTestTests`, the Build fixtures, `RepoPathWorktreeTests`) share a test project with
fast tests. About 800 summed test-seconds per full run (run `48fb08e2-20261124-111028-65836`: `RunTestTests` 659 s, mostly waiting on `BuildGate`;
Build fixtures 48 s; `RepoPathWorktreeTests` 98 s) against about 1.6% of commits touching the engines.

Leftover from Area 1: the 60 files in `RoslynSentinel.Git/` still declare three old namespaces (grounding 2026-11-26): `RoslynSentinel.Common`
(29 files: plumbing, readers), `RoslynSentinel.Tools.Basic` (28: `GitEngine`, `IGitOperations`, `GitHunkParser`, `GitErrorCodes`, the `Git*Result`/`*Entry`
DTOs; INBOX:13) and `RoslynSentinel.Common.GitWorktree` (3). `// TODO rename namespace` at `RoslynSentinel.Git/GitEngine.cs:13` and `GitProcess.cs:7`.
Probe 2026-11-26 (`ChangeNamespace(filePath: "RoslynSentinel.Git", newNamespace: "RoslynSentinel.Git", dryRun: true)`): `dry_run_ok`, validated, 144 files
rewritten, 1,866 references updated, one call.

Grounding facts that correct the proposal (2026-11-26): the move set is 10 files, about 3,366 lines (`BuildEngine` 457, `BuildIsolation` 516, `TestRunEngine`
789, `FullTestMarkerStore` 217, `TestSuiteFilter` 100, `TestRunResultProjector` 79, `TestRunOutcome` 23, `TestInstanceLauncher` 874, `KillOnCloseJob` 254,
`TestInstanceModels` 57); `TestProjectDetector` is already in Engines.Basic (not Common) and stays; the only Engines.Basic edge back into the move set is
`SolutionRestore.cs:70` -> `BuildIsolation.RemoveInheritedMsBuildPins`; `TestInstanceLauncher.ReadGitStamp` uses the internal `GitReaders` (Git IVT needed);
`System.Management` is used only by `BuildIsolation.cs` and moves with it; DI registrations are at `EngineServiceCollectionExtensionsBasic.cs:36,65,66`;
`GitTools.cs:224` (Tools.Basic) uses `FullTestMarkerStore`; 4 `BareFileWriteRatchetTests` keys (`:50,52,54,56`) and the path-keyed `BuildIsolationTests.cs:88-96`
name moving files; the server-source lists match the `RoslynSentinel.Engines.` prefix, so an `Engines.*` name is covered without edits.

## Decision

Dated entries; later entries do not override earlier ones unless they say so.

- **2026-11-26, Q1 scope (owner):** Area 2 (Build/RunTest) only. Area 3 (PersistentWorkspaceManager test-side work) and the selection rule
  (`scripts/expensive-test-watch-list.json`, owned by [plan_full_suite_nightly_and_tail_shard.md](plan_full_suite_nightly_and_tail_shard.md)) are
  out of scope. Why (owner): the saving is that the heavy tests can be excluded unless their project changed, with less frequent regression-guard
  runs; a project boundary makes "did it change" a directory question instead of a hand-kept glob list.
- **2026-11-26, Q2 Git namespace rename (owner):** folded into this plan as an early step (finishes Area 1's Step 19; first real use of
  `ChangeNamespace`, which this plan's own rename step needs too).
- **2026-11-26, constraint (owner; clarified the same day):** Common is overgrown (it took on several major roles by becoming the home for
  anything without one) and needs splitting regardless. The standing objective is: do not add new functionality to Common; a class without a home
  gets a proper home, not Common. It is not a mandate to evacuate Common in this plan. Applied here: `TestProjectDetector` and
  `RemoveInheritedMsBuildPins` (proposal Area 2 item 2 put them in Common) need another home; build/test logic already in Common moves out only where
  the move is mechanical. A wider Common split is not this plan's scope unless the owner widens it (open question).
- **2026-11-26, goal (owner):** the splits also aim at focused, single-responsibility projects, not only test isolation. Placement
  questions are judged by "one job per project" as well as by test selection: the new project should own the whole build/test-run responsibility
  (engines, result types, options), not just the slow-to-test classes.
- **2026-11-26, DTOs and mechanics (owner; refines the two entries above):** DTOs may stay in Common: they are lightweight and usually belong at
  that layer, so moving a high-blast-radius DTO (`BuildResult` and the like) is not required. The constraint targets behaviour (logic classes), not
  data types. The move should be almost entirely mechanical: file moves, project wiring, a namespace rename; no redesign, no new seams unless a
  compile edge forces one.
- **2026-11-26, DTO rule (owner; refines the entry above):** a build/test DTO moves with the area when the move is low friction, i.e. all its
  users end up at or above the new project and the only fixes are mechanical (references, `using`, IVT). A DTO whose move would need more than
  mechanical fixes (a user pinned below the new project, a seam, a type split) stays where it is in this plan.
- **2026-11-26, Q5 Area 2 namespace (owner):** keep `RoslynSentinel.Engines.Basic` during the moves; one `ChangeNamespace` to the new project's
  namespace in the final rename step. (Namespace-sensitive test: `Tests.Server/ToolResultMarshallerDetachedTests.cs:186-198` uses fully qualified
  `RoslynSentinel.Engines.Basic.*` names; `ChangeNamespace` rewrites references.)
- **2026-11-26, Q4 partial (owner):** the worktree harness gets a `Git.*` sub-namespace and its folder is renamed to match. Folder/namespace
  spelling open as Q30 (a folder named `Worktree` collides with the harness-clone exemption).
- **2026-11-26, Q29 one project (owner):** Build and RunTest go into ONE production project and ONE test project. Why: `TestRunEngine` takes
  `BuildGate` (`TestRunEngine.cs:221`) and `TestInstanceLauncher` calls `BuildEngine.RunFullBuildAsync` (`:318`), so two projects would stack
  (Basic <- Build <- TestRun) and a Build change would still select the TestRun tests; the split would save only the Build fixtures (about 48 s) on a
  TestRun-only change. A later split is a possible follow-up (Out of scope).
- **2026-11-26, Q30 worktree harness spelling (owner):** folder `RoslynSentinel.Git/Worktrees/`, namespace `RoslynSentinel.Git.Worktrees`. Why: a
  folder named `Worktree` matches the `[\\/]Worktree[\\/]` harness-clone exemption in `.claude/hooks/enforce-dogfood.ps1` (`:632,999,1035,1089,1417,1451`)
  and CLAUDE.md's "exclude */Worktree/" rule; the plural does not. Route: same-project `MoveFile` folder pair `GitWorktree` -> `Worktrees`, then
  `ChangeNamespace(matchFolder: true)` on it, after the parent `ChangeNamespace` call.
- **2026-11-26, Q6 hint text (owner): move verbatim, extract in this plan's last steps.** Why (owner): the bulk of the work (the moves) does not wait on
  writing the text pins. The moves carry the about 20 `ToolCallHint`/tool-name sites unchanged; after the moves and renames, golden pins through the
  tool classes, then the extraction to a tool-side renderer (design: Q33, inventory: Q34).
- **2026-11-26, Q7 tests that move (owner):** only the OutOfProcess (process-spawning) fixtures whose subject is the area, tool-level ones included.
  The in-process area tests (Tests.Basic, `Tests/BuildResultProjectorTests`) stay and keep running at every task end; their projects gain a reference.
- **2026-11-26, observation (owner):** having everything in Common masked a coupling issue: engines throw `ToolException`s and build tool-surface text
  (`ToolCallHint`, tool names, the tool surface's `isCallable` predicate) because Common lets every layer reach tool-layer concepts. The project splits
  surface it; the hint extraction (Q33) is where this plan meets it. Owner, same day: this is an unseen benefit of the splits; the masked coupling has
  likely added complexity and amplified the blast radius of changes. The closing docs step adds this lesson to
  `reference_splitting_functionality_out_of_common.md` (expect masked coupling; record each instance as a finding with its own step or proposal).
- **2026-11-26, Q35 scope (owner):** the `ToolException` fix (engine-layer exception family, rename, Common's 21 throws, the other roughly 138 engine
  `ToolCallHint` sites) is its own proposal, written at this design's finalize step from the Q39 evidence. This plan keeps the existing exception base
  and builds only the Build/RunTest facts-plus-builder slice, in the shape that proposal can adopt.
- **2026-11-26, root cause (owner):** the `isCallable`-into-engine pattern likely came from "the engine knows exactly what went wrong and how to resolve
  it, so let it write the hint". That inverted the layering: the engine reports what went wrong and which kind of next step resolves it (facts,
  enums); only the tool layer knows the caller's tool surface and names tools. Design rule for Q37/Q38 and the proposal: engines never take
  `isCallable` or call `ToolCallHint`.
- **2026-11-26, roadmap first (owner):** the work is too wide for one plan. Order of work: (1) review the existing plans and proposals in this
  area; (2) survey Common and the engines for the same coupling issues, by area; (3) write a roadmap (master plan) that ties the sub-plans together,
  with a coverage ledger for every finding of this design session; (4) tackle areas in order of complexity, easier splits and fixes first, so later
  ones get easier. Owner lean: implement the existing plans and fix the existing issues first, so the BuildTest move comes last and is clean and
  mechanical. This reverses Q6's order (extraction now precedes the move). Planner boundary, proposed and not yet confirmed: only work that touches the
  Build/RunTest files gates the move; the repo-wide exception fix does not.
- **2026-11-26, Q3 names (owner):** `RoslynSentinel.Engines.BuildTest` and `RoslynSentinel.Tests.BuildTest` (prefix-covered by the server-source lists;
  no `Tests` in the production name, so the ratchets scan it).
- **2026-11-26, Q8 (owner):** `RepoPathWorktreeTests` moves from Tests.Git to Tests.BuildTest (subject is Build/RunTest's `repoPath` handling).
- **2026-11-26, Q9 (owner):** one `MoveMember` moves `RemoveInheritedMsBuildPins` and `MsBuildPinnedVariables` from `BuildIsolation` to a new static
  class `MsBuildPins` in Engines.Basic, before the project move.
- **2026-11-26, Q10 (owner):** the owner is PRESENT during the run; the dispatcher may stop and ask at a decision point.
- **2026-11-26, investigations Q11-Q15 settled (explore, evidence in the agents' reports):**
  - Q11: `RemoveInheritedMsBuildPins` (`BuildIsolation.cs:45-51`) depends only on `MsBuildPinnedVariables` (`:34-38`); one `MoveMember` with both
    names moves them with call-site updates. Callers: `SolutionRestore.cs:70` (stays), `BuildEngine.cs:389`, `TestRunEngine.cs:582`,
    `TestInstanceLauncher.cs:155`; tests `Tests/TestSolutionFixture.cs:206`, `LoadSolutionForceReloadRestoreIntegrationTests.cs:93`,
    `LoadSolutionHandWrittenCsprojRestoreIntegrationTests.cs:121`, `BuildIsolationTests.cs:67,74,76,82`. Source-text asserts at
    `BuildIsolationTests.cs:102-103` (`Does.Contain("BuildIsolation.RemoveInheritedMsBuildPins(")`) need a hand edit in the same slice. No existing
    class owns MSBuild pin knowledge. Home: Q9.
  - Q12: no `GetService` resolves the three engines; constructor users are `WorkspaceTools.cs:46` and `WorkspaceBuildTestImpl.cs:24` (required),
    `WorkspaceHealthMiscImpl` (falls back to `new BuildEngine`), `AdminTools.cs:36` (optional `TestInstanceLauncher`). `AddRoslynSentinelToolImplsBasic`
    (`Tools.Basic/ToolServiceCollectionExtensionsBasic.cs:26`) reaches both production hosts (via `ServiceRegistrationExtensionsBasic.cs:396`),
    `TestEngines.CreateProvider` (`TestEngines.cs:37`) and both `ToolClassRegistration*Tests`. `ValidateOnBuild` hosts that fail loudly if a
    registration is missing: `ServerStdio.cs:95`, `ServerHttp.cs:65`, `ToolSurfaceEmptyCollectionTests.cs:137`, `ToolSurfaceGolden.cs:95`,
    `ToolClassRegistrationTests.cs:41`, `ToolClassRegistrationAdvancedTests.cs:43`. Calling the new extension from `Engines.Advanced` is a sibling edge
    (forbidden).
  - Q13: `ToolErrorMapper` (`ToolException.cs:323-394`) names none of the build/test exceptions; `BuildNotRunException`, `TestRunNotRunException`,
    `ToolTimedOutException` (only `BuildEngine.cs:402`), `NoProjectsException` (only `BuildEngine`, `TestRunEngine`) and the enums `TestOutcome`,
    `TestResultsFilter`, `TestSuite` have no Common user; all are movable mechanically (they share `ToolException.cs` / `ToolEnums.cs`, so a `MoveType`
    split first).
  - Q14: edits needed: `ErrorPolicyKindRatchetTests.cs:26` (`ScannedProjects`, else `MinimumKindSites = 194` loses 3 sites), `BareFileWriteRatchetTests.cs:50,52,54,56`,
    `BuildIsolationTests.cs:88,89,91,102`; regenerate `docs/generated/architecture_projects.md` (`ArchitectureDocFreshnessTests`) and
    `docs/generated/todo_markers.md`; `RoslynSentinel.slnx`. Prefix-covered, no edit: `FirstMatch`, `Whitespace`, `NewlineLiteral`, `StaleReadPolicy`,
    `EnvVarDeclarationGuard`, `TestPlacement`, `ServerBinaryStaleness.cs:44-52`, `scripts/server-source-paths.txt:8`, `TestInstanceLauncher.GetSourceRoots`.
    Name hazard: ratchet exclusions use `Contains("Tests")`; a production project name containing `Tests` would be silently unscanned.
    `configuration.md` keys `FullSuiteStalenessOptions` by class, not assembly; `StartupSettings.Discover` walks references, so the moved group is found.
  - Q15: no moving type is used by Engines.Advanced, Tools.Advanced, Tools.Experimental, AgentLoop, Utilities.* or Git; `BuildGate*` users outside
    the set: only `IdleRestartMonitor.cs:83` (Server.Advanced).
  - New csproj carries `<Using Include="RoslynSentinel.Common" />`, `System.Management`, IVT to Tests.Tools.Basic/Tests.Basic (`Engines.Basic.csproj:20,11,24-25`);
    internals used by tests: `TestInstanceLauncher.cs:355,540,748`.

## Execution rules

To be written in phase 7 (copy and adapt from the reference doc's Execution rules).

## Steps

To be written in phase 7 (reference doc Step template).

## Out of scope

- Area 3 (PersistentWorkspaceManager test-side work).
- The selection rule, the watch-list file, the nightly run and the debt check (nightly plan).
- Any tool class split, new `[McpServerTool]`, wire text, DTO property or tool schema change.
- Splitting Build and RunTest into separate projects (Q29; possible follow-up).

## Risks and open decisions

Question inventory (phase 3, 2026-11-26). Numbers are fixed.

Owner decisions:
- Q33 hint extraction design (written up in chat 2026-11-26). Owner leaning (2026-11-26): option B, reshaped: an engine-layer exception family
  (working name `EngineException`) carrying typed facts instead of the misnamed `ToolException` in Common; Build/RunTest result DTOs carry facts; a
  tool-layer `BuildTestHintBuilder` (Tools.Basic) turns exceptions and DTOs into the wire text. Raises Q35-Q38 once investigation Q39 is back.
- Q35 scope of the `ToolException` -> engine-exception fix: repo-wide in this plan, or a separate proposal with Build/RunTest as the first adopter.
- Q36 settled by evidence (Q39): the exception base stays in Common while Common throws it (21 sites: `DiffEngine`, `DocumentLookup`,
  `NameTargetLookup`, `PersistentWorkspaceManager`, `SnippetErrorFormatter`); a base in Engines.Basic would be invisible to Common. Build/test-specific
  types (`BuildNotRunException`, `TestRunNotRunException`, `BuildGateBusyException`, `TestInstanceException`) live in Engines.BuildTest (Q31).
- Q37 fact shape on exceptions: typed subclasses with fact properties (pattern-matched by the builder) vs one type with a key enum plus a facts record.
- Q38 render point: one tool-side mapping helper replacing the 8 catch bodies, so each catch site is a single call.

Investigation Q39 done 2026-11-26: `ToolException` (`Common/ToolException.cs:13`, abstract, `ErrorCode`/`Category`/`StructuredDetail`) has 17 sealed
subclasses there plus 5 elsewhere; throws: Engines.Basic 323, Engines.Advanced 234, Common 21, Tools.Basic 8, Tools.Advanced 1, Git/Server/AgentLoop/
Utilities 0. Base type: 93 references in 7 projects; family names: 1,747 text matches in 250 files; 623 exact-type test asserts. No reflection or wire
pin on type names (`ResultError` carries only the code); `ErrorPolicyKindRatchetTests.cs:115-120` matches the literal `"ToolNotFoundException"`.
`EngineError`/`EngineErrorCode` (`Common/EngineError.cs`) is a leftover of the deleted `EngineResultWrapper`, not an engine-exception precursor.
`ToolErrorMapper` has no hint logic. 152 `ToolCallHint.` text matches across `Engines.*` (14 call sites in the build/test files; an earlier looser
count said 29): the coupling is repo-wide.
Prior decisions: typed exceptions are the single error carrier (`plan_error_codes_follow_cause.md`, `proposal_throw_typed_exceptions_not_failure_results.md`);
`plan_shared_tool_execution_pipeline.md` (DRAFT) plans a central `FromException` helper (its step 14i), the same idea as Q38. Pre-existing finds:
`BuildEngine.cs:299` message does not match its cause; `TestInstanceException` NotFound carries no `MissingTargetKind`; the gate-busy re-wraps
(`BuildEngine.cs:333-336`, `TestRunEngine.cs:223-226`) drop `BuildGateBusyException`'s facts.

Investigation Q34 done 2026-11-26: 24 sites S1-S24 (14 `ToolCallHint` calls, 7 plain tool-name texts). Channels: exception message 15 (+5 that name
only parameters), result DTO field 5 (`BuildResult.Detail`, `TestRunResult.Detail`, `ProjectTestSummary.Detail`, `LaunchedTestInstance.HowToCall`),
returned string appended by the tool 3 (`TestSuiteFilter.ZeroMatchHint`, `AllSkippedNote`, `FullTestMarkerStore.Describe`), Finding 1
(`FullTestMarkerStore.StaleFindings`, used by `GitTools.cs:224`). No generic hint-key mechanism exists; `GitHintText` is Git-specific code (shape
reusable). Error code alone cannot pick the hint (same code, different hint per site). Engine-internal re-wraps: `TestInstanceLauncher.cs:320-323`,
`TestRunEngine.cs:166-170`. Tool catch sites: `WorkspaceBuildTestImpl.cs:125,338,480,484,574,578`, `WorkspaceHealthMiscImpl.cs:176`, `AdminTools.cs:486`.
Existing full-text goldens: `SurfaceAwareHintsBuildTestingTests` (S1, S2 two variants, S4), `FullTestMarkerStoreTests.cs:291` (S23); the rest
fragment-only or unpinned (S3, S8, S9, S10, S16, S2 variants `:213`/`:221`, S4 BuildNotRun variant).

Investigations: Q11-Q15 settled (Decision). Q16 test inventory: first pass done 2026-11-26 (Tools.Basic OutOfProcess
area fixtures: `BuildIsolationTests`, `RunTestTests`, `TestInstanceLauncherTests`, `TestInstanceLauncherGitStampTests`, `BuildEngineTests`,
`BuildCascadeFixtureTests`, `BuildScopeFullBuildTests`, `BuildDiagnosticsAttributionFindingsTests`, `KillOnCloseJobTests`, `RunTestMsBuildPinStripTests`;
unchecked: `BuildEngineParseTests`, `BuildUnrestoredProjectFindingTests`, `BuildVerificationNotRunTests`, `TestInstanceStopMissTests`, `RunTestMissTests`,
`LaunchBuildMarkerTests`, `SurfaceAwareHintsBuildTestingTests`; in-process area tests in Tests.Basic and `Tests/BuildResultProjectorTests`; not area:
`ChildProcessCaptureTests`, `ServerBinaryStalenessGitTests`; `BuildToolResultShapeTests` may build without an OutOfProcess tag, hypothesis). The exact
list is a plan step (reference Step 13).
Q17 settled 2026-11-26: no shadowing. The five `Git` symbols in Tests.Git are private methods (`GitCommitHashRaceTests.cs:81` etc.); tests use no
`Git.X`; Tests.Git declares `RoslynSentinel.Tests.Tools.Basic` and `RoslynSentinel.Tests.Battery.Basic` (30 of 62 files paged).

Probe: Q18 Git rename dry run - done (Problem section): one call, 144 files, validated.

Implementer defaults (no longer open unless the owner reclassifies):
- Q19 placement above Engines.Basic (`BuildEngine.cs:44` needs `DiagnosticEngine`; "beside" would need a redesign, against the mechanical rule).
- Q20 `TestProjectDetector` stays in Engines.Basic.
- Q21 `ChildProcessCapture` stays in Common (generic process plumbing, pinned by `SolutionRestore.cs:91`; not build/test specific).
- Q22 `BuildResultProjector` (logic; only Tools.Basic and test users) and `FullSuiteStalenessOptions` (only user moves) move to the new project;
  `BuildResult` and `BuildVerifyLevel` stay (pinned by `DiagnosticReport.cs:135`, `WorkspaceHealthReport.cs:18`).
- Q23 `System.Management` package moves with `BuildIsolation.cs`.
- Q24 `BuildIsolation.cs` TODOs (BuildGate types to own file, `:100`, `:414`) out of scope.
- Q25 route per the reference doc: `CreateProject` + csproj edits, `LoadSolution(forceReload: true)`, `MoveFile` set dry run, real call with
  `addProjectReferences`/`addInternalsVisibleTo`; `Git.csproj` IVT to the new project for `GitReaders`.
- Q26 baseline full run before the first move, final full run, outcome classes as in the reference doc.
- Q27 heads-up messages (no hold) to the owners of `plan_full_suite_nightly_and_tail_shard.md` (watch-list globs `:141-142`),
  `plan_shorten_verification_loop.md` (`TestSuiteFilter`) and `plan_prebuild_and_idle_stale_server_restart.md` (`IdleRestartMonitor`, `BuildGate.IsBusy`);
  `HOLD` on the moving files during move slices.
- Q31 (from Q13) move with the area: `BuildNotRunException`, `TestRunNotRunException` (split out of `ToolException.cs` with `MoveType`) and the enums
  `TestOutcome`, `TestResultsFilter`, `TestSuite` (out of `ToolEnums.cs`). `ToolTimedOutException` and `NoProjectsException` stay in Common: generic
  names that the next non-build user would want there.
- Q32 (from Q12) DI: new `AddRoslynSentinelEnginesBuildTest()` in the new project, called from `AddRoslynSentinelToolImplsBasic` (one point reaching
  both production hosts, `TestEngines.CreateProvider` and the registration tests); the three registrations leave `AddRoslynSentinelEnginesBasic`.
  The `ValidateOnBuild` hosts and the full run are the check.
- Q28 order: Git rename first (it touches 6 Engines.Basic files incl. 3 moving ones), Area 2 moves next, Area 2 namespace rename (if Q5) last.
