# Plan: Tests.Support.* projects and construction-factory rollout to every test project

**Status:** IN PROGRESS 2026-10-28 (Steps 1-4 done; Step 5 next). Approved by the user (support project(s) named `Tests.Support.*`, more factories, roll out to the other test projects). Continues `plan_test_construction_factories_pilot.md`.

## Progress
- 2026-10-28 Step 1 DONE: `RoslynSentinel.Tests.Support.Basic` created (`CreateProject` made the project and slnx entry; csproj then replaced by hand to the plan's shape; template `Class1.cs` removed with `DeleteFile`),
  referenced from `Tests.Tools.Basic`, excluded from `scripts/Test-Parallel.ps1`. Build 0 errors. `CreateProject` drifted 3 files (csproj, slnx, Class1.cs) and halted the session until `ExternalFileDrift(Acknowledge)` (successful bypass, reported). Commit eca89584.
- 2026-10-28 Step 2 DONE: `TestEngines.cs` / `TestTools.cs` moved to `Tests.Support.Basic` (namespace and names unchanged; `WriteFile(operation: CreateFile, content)` + `DeleteFile`, since the shell-only `CreateFile` takes no content). `CreateProvider` stays `internal` (used only in those two files). Build 0 errors; `Tests.Tools.Basic` 1281 passed, 0 failed, 1 skipped (no flake this run). Agent: `implementer-senior`, resumed per phase. Commit 6b00c4ce.
- 2026-10-28 Step 3 slice A DONE: `TestEngines.TestRunEngine(wm)` (13 sites) and `TestTools.WholeFileWriteTools(6 args)` (23 sites) added and rewritten in `Tests.Tools.Basic`, 0 skipped; 2 resolve tests added. Build 0 errors; `Tests.Tools.Basic` 1282/1284 + 1 skipped, only the known staleness flake (8/8 alone). 2 `new TestRunEngine` remain outside this project (`Tests.Asyncify` MigrationScanResultTests:119, `Tests.Tools.Advanced` ComprehensiveToolTests:123; Step 4/5). TOOL-GAP-ish finding: a freshly created project had no restored `obj/project.assets.json`, so the workspace loaded it degraded (33 phantom errors; `SemanticFindReplace` NotFound with no pointer to the cause); fix was `Build(fullBuild, scope: project)` + `LoadSolution(forceReload)`. Suggest: `LoadSolution` hint to run fullBuild/restore on "Unable to find project information". Commit d2b794a4.
- 2026-10-28 Step 3 slice B DONE: `BasicRefactoringEngine` 1 overload (wm, logger, config; 19 sites), `MemberRefactoringEngine` 2 overloads (3-arg 5 sites, 4-arg 12), `MsToolAugmentEngine` 1 overload (12): 48 sites rewritten, 0 skipped, 0 left in `Tests.Tools.Basic`; 4 resolve tests. Build 0 errors; `Tests.Tools.Basic` 1286 passed, 1 skipped, 1 failure = staleness flake (a different test of that fixture each full run; 8/8 alone). One run showed 2 skipped (unidentified, next run 1). `BasicRefactoringEngine` 1-arg ctor has no site, no overload. Tool defect: `Member(addMember)` flattens continuation-line indentation of expression-bodied methods (fixed by one `ReplaceSnippet`; also re-indented slice A's `TestRunEngine`). Commit 9139055d.
- 2026-10-28 Step 3 slice C DONE: `RefactoringStructuralImpl` (7 args, 13 sites), `RefactoringStructuralTools` (impl, 16), `WorkspaceHealthMiscImpl` (2 overloads, 5), `DiscoveryEngine` (wm, 5), `TestCategoryTaggingTools` (impl, 5): 44 sites rewritten, 0 left; 6 resolve tests. Build 0 errors; `Tests.Tools.Basic` 1293 passed, 0 failed, 1 skipped (the intentional `TestInstanceLauncher_EndToEnd...` skip; flake absent). Nested construction: rewrite the inner class first. Remaining 4-7 site classes in `Tests.Tools.Basic`: `PersistentWorkspaceManager` 7 (other shape), `TestCategoryTaggingImpl` 6, `SymbolRelationshipImpl` 4, `ServerStatusTools` 4, `TestCategoryTaggingEngine` 4, `TestCategoryApplyEngine` 4 (left, below the 5-site bar). Commit edf9be66.
- 2026-10-28 Step 3 slice D DONE (Step 3 COMPLETE): ~22 helper/Setup bodies collapsed onto factories (7 `RefactoringStructuralTools` builders, structural/extraction-docs/signature impl builders, `BuildWholeFileWriteTools`, 10 `WorkspaceTools` Setup/harness methods incl. `ReadFile`/`GetLargeResult` that the pilot report listed as done but were not). Factories added: short forms `RefactoringStructuralImpl/Tools`, `RefactoringExtractionDocsImpl`, `RefactoringSignatureImpl` (wm only; non-generic `ILogger` -> `NullLogger<T>`), `WholeFileWriteTools(wm, workspaceTools)`, `SymbolRelationshipImpl`, `TestCategoryTaggingImpl`, `TestCategoryTaggingEngine`, `TestCategoryApplyEngine`; 9 resolve tests. Build 0 errors; `Tests.Tools.Basic` 1301/1303, 1 skipped (intentional), 1 failure = staleness flake (8/8 alone). Left as `new` (skips): `ReplaceSnippetSizeGuardTests.BuildTools`, `BatteryTwentyTests` Setup, `ConstructorParameterCallSiteFixupTests.cs:73` (RefactoringSignatureImpl, fields), `ServerStatusTools` x4 (optional ctor params; factory would change what tests exercise), 3 `PersistentWorkspaceManager` object initializers (`SearchModeTests:71`, `SearchSolutionTextIncludeTestsTests:47`, `SearchSolutionTextRecoveryHintTests:52`) plus 4 inside a `/* */` block in `ApplyDiffSizeGuardTests`, `SemanticReplaceEngine` x3, one-off tool wrappers (`RefactoringSignatureTools`, `ParameterEditTools`, `DeclarationTools`, `RefactoringExtractionDocsTools`), `TestCategoryPlanOptions` (data record). Next-candidate: `CreateFileDeleteFileTests` 14 inline blocks, `UndoLastApplyTests` 1. Commit 8bab559c.
- 2026-10-28 Step 4 `Tests.Basic` DONE: Support.Basic reference added (no ambiguity); 77 sites rewritten (SentinelConfiguration 16, PersistentWorkspaceManager 21, BasicRefactoringEngine 11, SymbolNavigationEngine 5, DiffEngine/ValidationEngine(wm)/DiagnosticEngine/MsToolAugmentEngine/MemberRefactoringEngine 4 each, ValidationEngine 3-arg 3, DependencyEngine 2, StructuralRefinementEngine 1). Build 0 errors; `Tests.Basic` 1027/1027. Left (no factory, shape `(wm, config)`): `SolutionStructureEngine` x2 (`DependencyEngineFixtureTests:94`, `ProjectStructureNormalizeCharacterizationTests:32`), `SyntaxUpgradeEngine` x2 (`SyntaxUpgradeNormalizeCharacterizationTests:31`, `UseFieldBackedPropertiesFormatCharacterizationTests:28`); add factories if later projects share the shape. Nested-construction order: SentinelConfiguration/PersistentWorkspaceManager/DiffEngine, then Validation/SymbolNavigation/MsToolAugment/Diagnostic/Dependency/StructuralRefinement, then MemberRefactoring, then BasicRefactoring. Commit dd3606ee.
- 2026-10-28 Step 4 `Tests.Battery.Basic` DONE: reference added (no cycle/ambiguity; project needed `Build(fullBuild, scope: project)` + `LoadSolution(forceReload)` to load healthy); 43 sites rewritten (PersistentWorkspaceManager 12, DiffEngine/ValidationEngine/SymbolNavigationEngine/MemberRefactoringEngine 7 each, MsToolAugmentEngine 3), 0 left, 0 skipped. Build 0 errors; `Tests.Battery.Basic` 179/179. Calls read `TestEngines.X` (same namespace). Note: an agent claim that `Tests` -> `Support.Basic` would form a cycle is wrong (Support.Basic references Common/Engines.Basic/Tools.Basic only, not `Tests`); the real concern for `Tests` is Tools.Basic visibility leaking to dependants (Battery.Basic, Tests.Tools.*). Commit 3fa88d18.
- 2026-10-28 Step 4 `Tests.Server` NO-OP: the real count is 8 sites (not 17), all `new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance) { SolutionPath = ... }`, all skipped by `replaceConstruction` (object initializer; TOOL-GAP recorded in TODO.md); Support.Basic reference added then reverted (unused). Sites: `LargeResultOffloadFilterTests:52,237`, `RelativePathInputAuditTests:51`, `StructuredContentDataTagTests:46`, `ToolArgumentCaseNormalizationTests:66`, `ToolCallEchoFilterTests:60`, `ToolParameterAliasTests:59`, `WirePathRelativizeFilterTests:54`.
- 2026-10-28 Step 4 `Tests` DONE: Support.Basic reference added (whole-solution Build 0 errors, no ambiguity from Tools.Basic leakage); 9 sites rewritten (PersistentWorkspaceManager 7, SentinelConfiguration 1, SymbolNavigationEngine 1). Build 0 errors; `Tests` 711/711. Left: `SolutionStructureEngine(wm, config)` `StructureTests:25`, 2 `FakeWorkspaceManager` (test double). `(wm, config)` SolutionStructureEngine x3 + SyntaxUpgradeEngine x2 across Tests.Basic/Tests: a factory for each is defensible but below the 4-per-type bar. Step 4 COMPLETE. Commit f8da7e98.
- 2026-10-28 Step 5a DONE (Support.Advanced + factories, no call sites yet): answers to the open questions: Tools.Advanced does NOT reference Tools.Basic (Common/Engines.Advanced/Engines.Basic), but Support.Advanced gets Tools.Basic via Support.Basic; Tools.Advanced ctors resolve from the Advanced provider (`AddRoslynSentinelToolImplsAdvanced` supplies `ToolGraph`/`FailureRouter`; nothing needs ToolImplsBasic). Survey (`explore-sonnet`): 1,115 engine/tool sites in the 4 Advanced projects; 436 on Basic types with existing factories (PersistentWorkspaceManager 229, SymbolNavigationEngine 36); 664 on 51 types without. Added: `TestEnginesAdvanced` (29 methods incl. `(wm,cfg)` for AntiPattern/DeadCode/SyntaxModernization/CodeStyle/CodeHealing/ResourceSafety, `(wm,logger)` AdvancedRefactoring, DI-resolved `HealthOrchestrationEngine(wm)`), `TestToolsAdvanced` (7 `(wm)` tool factories), Support.Basic `TestEngines` +6 (`DiscoveryEngine(wm, nav)`, `SolutionStructureEngine(wm,cfg)`, `SyntaxUpgradeEngine(wm,cfg)`, `ThreadSafetyEngine`, `SemanticRefactoringEngine`, `InventoryEngine`). 37 resolve tests in new `Tests.Tools.Advanced/TestFactoryResolveAdvancedTests.cs` + 6 in `Tests.Tools.Basic`. Build 0 errors; `Tests.Tools.Advanced` 523/523; `Tests.Tools.Basic` 1308, 1 skipped, 0 failed. Inline 8-9 engine chains for the tools (and `HealthOrchestrationEngine` 7-file chain) cannot be rewritten by `replaceConstruction` (argument shape differs from the `(wm)` factory); they are hand-edit/helper-collapse candidates (`BatteryToolFactory.cs` 50 sites, `BugFixTests` Setup 52/CreateTools 10, `ComprehensiveToolTests.Setup` 55, `MoveMemberToolCollateralRefusalTests.CreateTools` 9). Left as `new`: below-bar shapes, `AsyncBatchEngine` (6 shapes), test-local subclasses, `CommentingEngine`. `docs/generated/architecture_projects.md` hard-codes a project table without either Support project (regenerate in Step 6). Risks: factory param type is `IWorkspaceManager`; engines with optional `config = null` get a container-resolved config. Commit 6b4d20a8.
- 2026-10-28 Step 5b `Tests.Tools.Advanced` DONE: ~319 sites rewritten (~168 Basic types, ~151 Advanced types; nested constructions each count). Overloads added with resolve tests: `TestEngines.ImpactAnalyzer(wm, logger)`, `TestEnginesAdvanced.HealthOrchestrationEngine(wm, SolutionStructureEngine, cfg, PerformanceEngine, AntiPatternEngine)`, `TestEnginesAdvanced.AsyncBatchEngine(wm, AsyncOptimizationEngine, ValidationEngine, AntiPatternEngine, MigrationLedger, logger)`, `TestToolsAdvanced.GenerationTools(wm, logger)`. Helpers hand-collapsed onto `TestToolsAdvanced.X(wm)`: `BatteryToolFactory` Generation/Modernization/Quality/Intelligence/Scan creators (~40 constructions), `ComprehensiveToolTests.Setup` 3 tools (`_config` not mutated). Build 0 errors; `Tests.Tools.Advanced` 526/526 (flake absent); `TestFactoryResolveTests` 48/48. Left as `new`: `AdvancedToolsTests:26`, `BatteryNineteenTests:75` (7 explicit engines), `BatteryToolFactory:28,29,37,53,54,76,83` (one-off impls/wrappers), `MoveMemberToolCollateralRefusalTests:104,144,282,353` (test-local engine subclasses/custom engines), `new MigrationLedger()` x4 (data class arg). Optional-config engines (AntiPattern/DeadCode/Performance/StructuralRefactoring `(wm)`) now get a container-resolved config; no test depended on the difference. Tooling notes: `replaceConstruction` echoes full before/after for every skipped site (a wrong-overload call costs thousands of tokens); no cheap docCommentId lookup for type ids. Commit 55a98f93.
- 2026-10-28 Step 5c `Tests.Battery.Advanced` DONE: Support.Basic + Support.Advanced references added (no ambiguity, no fullBuild needed); ~331 sites rewritten (PersistentWorkspaceManager 95, SentinelConfiguration 56, SyntaxModernizationEngine 25, LogicSimplificationEngine 21, StructuralRefactoringEngine 18, SolutionStructureEngine 15, ... 40 types), no Support changes, no helpers to collapse. Left as `new`: `InstrumentationEngine(wm)` x2 (`BatteryFiveTests:277`, `BatteryTwentySevenTests:135`; 3 across Advanced projects), `PathDrivenTestEngine` `BatteryThirtyEightTests:37`, `BasicRefactoringEngine(wm)` `BatterySevenTests:33`. Build 0 errors; `Tests.Battery.Advanced` 437/438: `BatteryTwentyFiveTests` `CheckPackageInconsistencyAsync_InMemorySolution_ReturnsNonNull` (fails alone; expects FileNotFound, gets DirectoryNotFound; depends on a leftover temp dir) is listed as a pre-existing unrelated failure in `plan_eol_policy_and_write_chokepoint.md:254`, `plan_in_memory_test_tier_for_battery.md:56` and the 2026-10-20 comprehensive review, and its only factory use is `DependencyEngine(wm)` with the same manager; CONFIRMED pre-existing in 5d: with the `DependencyEngine(wm)`/`PersistentWorkspaceManager` factory lines reverted to `new` it still fails ("In-memory project path does not exist on disk"); lines restored. Commit 483f0678.
- 2026-10-28 Step 5d `Tests.Advanced` DONE: Support.Basic + Support.Advanced references added, no ambiguity; ~450 sites rewritten in ~57 files (PersistentWorkspaceManager 113, SentinelConfiguration 84, StructuralRefactoringEngine 49, SyntaxModernizationEngine 31, AntiPatternEngine 40, SolutionStructureEngine 26, LogicSimplificationEngine 24, BasicRefactoringEngine 16, ... ~45 types) plus 1 leftover in `Tests.Tools.Advanced/AdvancedToolsTests.cs:26`. New overload `TestEnginesAdvanced.AdvancedRefactoringEngine(wm, logger, cfg)` (4 sites across projects) with resolve test. Helpers: 4 `BugFixTests` nested-fixture `CreateTools` now call `TestToolsAdvanced.QualityTools/ModernizationTools(wm)` and are never called (`// TODO ... delete it` added at each); `MassiveModernizationTests.Setup` 11-arg chain collapsed (tools now hold container engines, suite green). Build 0 errors; `Tests.Advanced` 1500/1500 (35 s). Left as `new`: `CloneDetectionEngine` x2 (`AmbiguousNameTargetTests:34`, `NewFeaturesTests:25`; 3 across projects), `InstrumentationEngine` `InstrumentationEngineFormatCharacterizationTests:28` (3 across), `PerformanceEngine(wm,cfg)` x2 (`FeatureToggleTests:22`, `PerformanceEngineTests:22`), `OutParamRefactoringEngine` `NewFeaturesTests:26`, `MassiveRefactoringTests:52,60` (Impl chains from the fixture's own engines). Commit 56865274.
- 2026-10-28 Step 5e `Tests.Asyncify` DONE: references added, no ambiguity; 65 sites rewritten incl. ~14 tool-argument sites folded when `MigrationScanResultTests.SetUp` `_qualityTools`/`_scanTools` collapsed onto `TestToolsAdvanced`; `AsyncBatchEngine` 6-arg 4. Both `AsyncifyTools` chains (`MigrationScanResultTests:65`, `SentinelAsyncifyToolsTests:107`) stay hand-built (two separate `MigrationLedger` instances + `FailureRouter(ToolGraph.Empty)`; a factory would share one ledger and use the real tool graph). Also rewrote `MassiveRefactoringTests:52` (missed in 5d). Build 0 errors; `Tests.Asyncify` 116/116; `Tests.Advanced` 1500/1500. Final sweep (text search, 39 matches incl. comments): 10 rewritable-now sites (factories added after those projects were rolled out): `Tests/StructureTests:25`; `Tests.Basic` `AmbiguousNameTargetStep9Tests:36,38`, `InlineVariableTests:35`, `MemberRefactoringTests:936`, `DependencyEngineFixtureTests:94`, `ProjectStructureNormalizeCharacterizationTests:32`, `SyntaxUpgradeNormalizeCharacterizationTests:31`, `UseFieldBackedPropertiesFormatCharacterizationTests:28`; `Tests.Tools.Basic` `CreateFileDeleteFileTests:811`, `LocateSymbolIncludeTestsTests:53`. Not rewritable: 11 `PersistentWorkspaceManager { SolutionPath }` initializer sites (8 Tests.Server, 3 Tests.Tools.Basic), 4 commented-out in `ApplyDiffSizeGuardTests`, custom-argument one-offs (see earlier notes). Factory calls per project: Tests 9, Tests.Basic 73, Tests.Advanced 493, Tests.Asyncify 36, Tests.Battery.Basic 41, Tests.Battery.Advanced 275, Tests.Tools.Basic 462, Tests.Tools.Advanced 300, Tests.Server 0.

## Problem
The pilot (`plan_test_construction_factories_pilot.md`, commits 88318c0d .. 9c800d26) put static factories
(`TestEngines`, `TestTools`) in `RoslynSentinel.Tests.Tools.Basic` and rewrote about 600 `new X(...)` sites plus
11 helper bodies there. The factories cannot be used by any other test project (sibling projects cannot see
each other). Survey on 2026-10-28 (regex over `*.cs`, includes some comment/string hits; type-to-assembly mapping by declaration search):

| Test project | Sites on Common types | Engines.Basic | Engines.Advanced | Tools.Basic | Tools.Advanced |
| --- | --- | --- | --- | --- | --- |
| Tests | 7 | 2 | 0 | 0 | 0 |
| Tests.Basic | 38 | 41 | 0 | 0 | 0 |
| Tests.Advanced | 158 | 86 | 246 | 2 | 7 |
| Tests.Asyncify | 15 | 13 | 30 | 3 | 4 |
| Tests.Battery.Basic | 24 | 17 | 0 | 0 | 0 |
| Tests.Battery.Advanced | 104 | 46 | 129 | 0 | 0 |
| Tests.Server | 17 | 0 | 0 | 0 | 0 |
| Tests.Tools.Basic (remaining) | 8 | 76 | 0 | 79 | 0 |
| Tests.Tools.Advanced | 47 | 88 | 120 | 13 | 14 |

Tests.Tools.Basic's remaining sites are mostly classes with no factory yet (census in the pilot plan:
WholeFileWriteTools 24, TestRunEngine 23, BasicRefactoringEngine 19, MemberRefactoringEngine 17,
RefactoringStructuralTools 16, RefactoringStructuralImpl 13, MsToolAugmentEngine 12, then 5-site classes).
`Tests.ModelEval`, `Tests.SubAgent`, `Tests.PlanStepRunner` and `Tests.Integration` have no sites.

## Decision
- **Two new non-test projects**, no test SDK, no NUnit:
  - `RoslynSentinel.Tests.Support.Basic`: references Common, Engines.Basic, Tools.Basic. Holds the existing
    `TestEngines.cs` and `TestTools.cs` (moved from `Tests.Tools.Basic`) plus the new Basic factories.
  - `RoslynSentinel.Tests.Support.Advanced`: references Support.Basic, Engines.Advanced, Tools.Advanced. Holds
    `TestEnginesAdvanced` / `TestToolsAdvanced` (distinct names so a project that references both has no
    ambiguous type), built on a provider from `AddLogging()` + `AddRoslynSentinelEnginesAdvanced()` (includes the
    Basic engines) + `AddRoslynSentinelToolImplsBasic()` + `AddRoslynSentinelToolImplsAdvanced()`.
- **Factory namespaces stay as they are** (`RoslynSentinel.Tests.Battery.Basic`; Advanced: `RoslynSentinel.Tests.Battery.Advanced`).
  Reason: moving them to `RoslynSentinel.Tests` (so no qualifier is needed anywhere) would break every existing
  `Battery.Basic.TestEngines.X(...)` / `Basic.TestEngines.X(...)` call, and `RenameSymbol` cannot rename a namespace
  (`docs/current/TODO.md:18`). `replaceConstruction` already emits whatever qualifier compiles in each file, so rollout needs no
  `using` edits. A namespace tidy is a later cosmetic slice once a namespace-rename tool exists.
- **Each test project references the Support project(s) it needs directly** (not through `RoslynSentinel.Tests`), so
  Tools.* visibility does not leak into projects that do not have it today (`Tests.Basic`, `Tests.Battery.*`, `Tests.PlanStepRunner`).
  Advanced references go to `Tests.Tools.Advanced`, `Tests.Battery.Advanced`, `Tests.Advanced`, `Tests.Asyncify`.
- Per-type, non-generic, one-overload-per-constructor-shape factories remain (a generic `Create<T>` cannot be the target of
  `replaceConstruction`). Odd one-off shapes stay `new` (cap on overload growth).

## Execution rules
- Needs a fresh chat session started after `McpToolsetControl(moveExtract, enabled: true)` (and `projectAdmin` for `CreateProject`).
- Dispatch all C# edits (CLAUDE.md): `implementer-senior` for large `replaceConstruction` runs (over the sweep tiers),
  `implementer-sweep` for small mechanical batches, `explore-sonnet` for surveys. Resume one agent per phase with `SendMessage`.
- One `Build` or `RunTest` at a time. After each slice: Build 0 errors, `RunTest` on the touched project(s), and a full
  `RunTest` of `Tests.Tools.Basic` after any Support.Basic change. Known flake: `ServerBinaryStalenessGitTests` fixture
  ("git failed to run: rev-parse HEAD"), passes re-run alone; any other failure is new.
- Commit per slice with an explicit file list (`Git(operation: diff, nameOnly: true)` to build it, not shell git) and a `Plan: plan_tests_support_projects_factory_rollout` trailer above `Co-Authored-By`.
- Migration-first: new project and moved factories land before any call site changes. Preview every `replaceConstruction` before applying.
- Tool results of `SemanticFindReplace` are offloaded; read counts and skip reasons with `GetLargeResult`.
- A rewrite that leaves `new X(` sites because of a missing overload: list them (file:line, argument shape) and decide add-overload vs leave.

## Steps

### Step 1 - Create RoslynSentinel.Tests.Support.Basic (empty)
- Files: new `RoslynSentinel.Tests.Support.Basic/RoslynSentinel.Tests.Support.Basic.csproj`; `RoslynSentinel.slnx`; `scripts/Test-Parallel.ps1`; `RoslynSentinel.Tests.Tools.Basic/*.csproj` (reference).
- Change: csproj modelled on `RoslynSentinel.Engines.Advanced.csproj` (net10.0, LangVersion latest, ImplicitUsings, Nullable,
  `Configurations` Debug;Release;DebugVisualStudio, the three conditional WarningLevel groups, `IsPackable=false`, ProjectReference to
  Common, Engines.Basic, Tools.Basic, `<Using Include="RoslynSentinel.Common"/>`); no `Microsoft.NET.Test.Sdk`. Add `<Project Path=...>` to the
  slnx. `scripts/Test-Parallel.ps1:22-23` collects `RoslynSentinel.Tests*.csproj` by name: exclude `Support`. Use `CreateProject` if it fits, else write the
  csproj by hand (non-C#, so Write/Edit is allowed). Reload with `LoadSolution`.
- Done when: Build 0 errors; the project appears in `LoadSolution` output; the test-script exclusion is in place.

### Step 2 - Move TestEngines.cs and TestTools.cs into Support.Basic
- Files: `RoslynSentinel.Tests.Tools.Basic/TestEngines.cs`, `TestTools.cs` -> `RoslynSentinel.Tests.Support.Basic/`.
- Change: keep namespace `RoslynSentinel.Tests.Battery.Basic` and the class names. Cross-project `MoveType` has an open bug
  (project memory "Engine reorg leftovers", bug 2): use `CreateFile` with the exact content in the new project then `DeleteFile` on the old file (never a shell delete).
  `TestFactoryResolveTests.cs` stays in `Tests.Tools.Basic`. If it (or any test) calls `TestEngines.CreateProvider`, which is `internal`,
  make it `public` (it has no `InternalsVisibleTo`). Add the Support.Basic reference to `Tests.Tools.Basic`.
- Done when: Build 0 errors; `RunTest` `RoslynSentinel.Tests.Tools.Basic` shows no new failure; commit.

### Step 3 - More Basic factories (Tests.Tools.Basic first)
- Files: `RoslynSentinel.Tests.Support.Basic/TestEngines.cs`, `TestTools.cs` (add), then call sites in `Tests.Tools.Basic`.
- Change: add one method per constructor shape for, in order, `WholeFileWriteTools` (has a `// TODO` in `TestTools.cs`), `TestRunEngine`,
  `BasicRefactoringEngine`, `MemberRefactoringEngine`, `RefactoringStructuralImpl`, `RefactoringStructuralTools`, `MsToolAugmentEngine`, then the 5-site
  classes. Take the shapes from `Search(references)` per class. Run `replaceConstruction` per class and overload (preview, apply, Build, RunTest, commit).
  Then collapse the remaining `BuildTools`-style helpers (7 `RefactoringStructuralTools` builders, 3 impl builders,
  `BuildWholeFileWriteTools`, harness/Setup methods that hold no engine fields); leave `ReplaceSnippetSizeGuardTests.BuildTools`
  (custom `writeAdvice`) and `BatteryTwentyTests` Setup (shares engines).
- Extend `TestFactoryResolveTests.cs` for each new overload (it asserts every factory returns an instance).
- Done when: every factored type has no `new` site in `Tests.Tools.Basic` except the listed skips; Build 0 errors; no new test failure.

### Step 4 - Roll out Support.Basic to Basic-flavoured test projects
- Projects, by site count: `Tests.Basic` (38 + 41), `Tests.Battery.Basic` (24 + 17), `Tests.Server` (17 Common), `Tests` (7 + 2). Not `Tests.Server` Engines.* (none).
- Change per project: add `<ProjectReference>` to Support.Basic; for each type with a factory run `replaceConstruction` with `projectFilter` = that project; Build; `RunTest` that project; commit.
  Check for type-name ambiguities when `Tests` gets Tools.Basic visibility (it does not unless it references Support.Basic; keep it out unless needed for its own 9 sites).
- Done when: those projects have no remaining `new` of a factored type except listed skips.

### Step 5 - Create Support.Advanced and roll out to the Advanced projects
- Files: new `RoslynSentinel.Tests.Support.Advanced/` (csproj as Step 1, references Support.Basic, Engines.Advanced, Tools.Advanced), `TestEnginesAdvanced.cs`, `TestToolsAdvanced.cs`, slnx, `scripts/Test-Parallel.ps1`.
- Open before starting: do the Tools.Advanced classes need `AddRoslynSentinelToolImplsBasic` to resolve (constructors not read)? Does Tools.Advanced reference Tools.Basic (it does not: Common, Engines.Advanced, Engines.Basic)?
- Change: factories for the Engines.Advanced types first (246 + 129 + 120 + 30 sites): survey shapes per type with `Search(references)`, add methods, resolve test for them
  (new `TestFactoryResolveAdvancedTests` in `Tests.Tools.Advanced`). Then roll out, smallest first: `Tests.Tools.Advanced` (220 sites), `Tests.Battery.Advanced` (322), `Tests.Advanced` (519), `Tests.Asyncify` (65).
  For each: add `<ProjectReference>`s, `replaceConstruction` per type and overload with `projectFilter`, Build, `RunTest` the project, commit.
- Done when: those projects have no remaining `new` of a factored type except listed skips.

### Step 6 - Measure and close
- Report: sites migrated vs remaining per project, overload counts per Support project, skip reasons, test-run time of `Tests.Tools.Basic` and `Tests.Advanced` compared with the pre-pilot commit (`171037b6`; carried over from the pilot, not yet measured),
  and the trial constructor change (add a resolvable parameter to `ValidationEngine`; expected 0 test edits).
- Update Status, move this plan and `plan_test_construction_factories_pilot.md` to `plans/closed/` (with commit hashes in the Status line), move TODO.md entries to CLOSED.md.

## Out of scope
- Production constructors and DI registration; process-global `Common` option classes (the `093be31c` leak).
- Renaming the factory namespaces (needs a namespace-rename tool; TODO.md:18).
- `Tests.ModelEval`, `Tests.SubAgent`, `Tests.PlanStepRunner`, `Tests.Integration` (no construction sites).
- `new FakeWorkspaceManager(...)` and other test-local helper types (only a few sites; not engine/tool classes).

## Risks and open decisions
- The site counts come from a regex that also matches comments and strings; some type names did not map to an assembly. Re-measure per type with `Search(references)` before each slice.
- Type-name collisions between the Basic and Advanced factories: avoided only by the `...Advanced` suffix; verify when a project references both.
- Whether the `Search` tool's `includeTests` flag classifies the Support projects as test projects (it may key on `Microsoft.NET.Test.Sdk`, as `TestProjectDetector` does); if not, factory sites in Support are reported as production code.
- `docs/generated/` project tables and any layering check may need regenerating for the two new project names (not looked at).
- `FakeWorkspaceManager` was only read for its first 40 lines; confirm it needs no `InternalsVisibleTo` if a Support factory ever takes it.
- A `ServiceProvider` is built per factory call and never disposed; the cost across about 1,450 more sites is unmeasured (Step 6).
- Cross-project file move depends on `CreateFile`/`DeleteFile` rather than `MoveType` (open bug); if `DeleteFile` trips drift detection, `ExternalFileDrift(List)` then `Acknowledge` and report it as a bypass.
