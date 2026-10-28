# Plan: Tests.Support.* projects and construction-factory rollout to every test project

**Status:** IN PROGRESS 2026-10-28 (Steps 1-3 done; Step 4 next). Approved by the user (support project(s) named `Tests.Support.*`, more factories, roll out to the other test projects). Continues `plan_test_construction_factories_pilot.md`.

## Progress
- 2026-10-28 Step 1 DONE: `RoslynSentinel.Tests.Support.Basic` created (`CreateProject` made the project and slnx entry; csproj then replaced by hand to the plan's shape; template `Class1.cs` removed with `DeleteFile`),
  referenced from `Tests.Tools.Basic`, excluded from `scripts/Test-Parallel.ps1`. Build 0 errors. `CreateProject` drifted 3 files (csproj, slnx, Class1.cs) and halted the session until `ExternalFileDrift(Acknowledge)` (successful bypass, reported). Commit eca89584.
- 2026-10-28 Step 2 DONE: `TestEngines.cs` / `TestTools.cs` moved to `Tests.Support.Basic` (namespace and names unchanged; `WriteFile(operation: CreateFile, content)` + `DeleteFile`, since the shell-only `CreateFile` takes no content). `CreateProvider` stays `internal` (used only in those two files). Build 0 errors; `Tests.Tools.Basic` 1281 passed, 0 failed, 1 skipped (no flake this run). Agent: `implementer-senior`, resumed per phase. Commit 6b00c4ce.
- 2026-10-28 Step 3 slice A DONE: `TestEngines.TestRunEngine(wm)` (13 sites) and `TestTools.WholeFileWriteTools(6 args)` (23 sites) added and rewritten in `Tests.Tools.Basic`, 0 skipped; 2 resolve tests added. Build 0 errors; `Tests.Tools.Basic` 1282/1284 + 1 skipped, only the known staleness flake (8/8 alone). 2 `new TestRunEngine` remain outside this project (`Tests.Asyncify` MigrationScanResultTests:119, `Tests.Tools.Advanced` ComprehensiveToolTests:123; Step 4/5). TOOL-GAP-ish finding: a freshly created project had no restored `obj/project.assets.json`, so the workspace loaded it degraded (33 phantom errors; `SemanticFindReplace` NotFound with no pointer to the cause); fix was `Build(fullBuild, scope: project)` + `LoadSolution(forceReload)`. Suggest: `LoadSolution` hint to run fullBuild/restore on "Unable to find project information". Commit d2b794a4.
- 2026-10-28 Step 3 slice B DONE: `BasicRefactoringEngine` 1 overload (wm, logger, config; 19 sites), `MemberRefactoringEngine` 2 overloads (3-arg 5 sites, 4-arg 12), `MsToolAugmentEngine` 1 overload (12): 48 sites rewritten, 0 skipped, 0 left in `Tests.Tools.Basic`; 4 resolve tests. Build 0 errors; `Tests.Tools.Basic` 1286 passed, 1 skipped, 1 failure = staleness flake (a different test of that fixture each full run; 8/8 alone). One run showed 2 skipped (unidentified, next run 1). `BasicRefactoringEngine` 1-arg ctor has no site, no overload. Tool defect: `Member(addMember)` flattens continuation-line indentation of expression-bodied methods (fixed by one `ReplaceSnippet`; also re-indented slice A's `TestRunEngine`). Commit 9139055d.
- 2026-10-28 Step 3 slice C DONE: `RefactoringStructuralImpl` (7 args, 13 sites), `RefactoringStructuralTools` (impl, 16), `WorkspaceHealthMiscImpl` (2 overloads, 5), `DiscoveryEngine` (wm, 5), `TestCategoryTaggingTools` (impl, 5): 44 sites rewritten, 0 left; 6 resolve tests. Build 0 errors; `Tests.Tools.Basic` 1293 passed, 0 failed, 1 skipped (the intentional `TestInstanceLauncher_EndToEnd...` skip; flake absent). Nested construction: rewrite the inner class first. Remaining 4-7 site classes in `Tests.Tools.Basic`: `PersistentWorkspaceManager` 7 (other shape), `TestCategoryTaggingImpl` 6, `SymbolRelationshipImpl` 4, `ServerStatusTools` 4, `TestCategoryTaggingEngine` 4, `TestCategoryApplyEngine` 4 (left, below the 5-site bar). Commit edf9be66.
- 2026-10-28 Step 3 slice D DONE (Step 3 COMPLETE): ~22 helper/Setup bodies collapsed onto factories (7 `RefactoringStructuralTools` builders, structural/extraction-docs/signature impl builders, `BuildWholeFileWriteTools`, 10 `WorkspaceTools` Setup/harness methods incl. `ReadFile`/`GetLargeResult` that the pilot report listed as done but were not). Factories added: short forms `RefactoringStructuralImpl/Tools`, `RefactoringExtractionDocsImpl`, `RefactoringSignatureImpl` (wm only; non-generic `ILogger` -> `NullLogger<T>`), `WholeFileWriteTools(wm, workspaceTools)`, `SymbolRelationshipImpl`, `TestCategoryTaggingImpl`, `TestCategoryTaggingEngine`, `TestCategoryApplyEngine`; 9 resolve tests. Build 0 errors; `Tests.Tools.Basic` 1301/1303, 1 skipped (intentional), 1 failure = staleness flake (8/8 alone). Left as `new` (skips): `ReplaceSnippetSizeGuardTests.BuildTools`, `BatteryTwentyTests` Setup, `ConstructorParameterCallSiteFixupTests.cs:73` (RefactoringSignatureImpl, fields), `ServerStatusTools` x4 (optional ctor params; factory would change what tests exercise), 3 `PersistentWorkspaceManager` object initializers (`SearchModeTests:71`, `SearchSolutionTextIncludeTestsTests:47`, `SearchSolutionTextRecoveryHintTests:52`) plus 4 inside a `/* */` block in `ApplyDiffSizeGuardTests`, `SemanticReplaceEngine` x3, one-off tool wrappers (`RefactoringSignatureTools`, `ParameterEditTools`, `DeclarationTools`, `RefactoringExtractionDocsTools`), `TestCategoryPlanOptions` (data record). Next-candidate: `CreateFileDeleteFileTests` 14 inline blocks, `UndoLastApplyTests` 1. Commit 8bab559c.
- 2026-10-28 Step 4 `Tests.Basic` DONE: Support.Basic reference added (no ambiguity); 77 sites rewritten (SentinelConfiguration 16, PersistentWorkspaceManager 21, BasicRefactoringEngine 11, SymbolNavigationEngine 5, DiffEngine/ValidationEngine(wm)/DiagnosticEngine/MsToolAugmentEngine/MemberRefactoringEngine 4 each, ValidationEngine 3-arg 3, DependencyEngine 2, StructuralRefinementEngine 1). Build 0 errors; `Tests.Basic` 1027/1027. Left (no factory, shape `(wm, config)`): `SolutionStructureEngine` x2 (`DependencyEngineFixtureTests:94`, `ProjectStructureNormalizeCharacterizationTests:32`), `SyntaxUpgradeEngine` x2 (`SyntaxUpgradeNormalizeCharacterizationTests:31`, `UseFieldBackedPropertiesFormatCharacterizationTests:28`); add factories if later projects share the shape. Nested-construction order: SentinelConfiguration/PersistentWorkspaceManager/DiffEngine, then Validation/SymbolNavigation/MsToolAugment/Diagnostic/Dependency/StructuralRefinement, then MemberRefactoring, then BasicRefactoring.

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
