# Plan: Pilot shared engine/tool construction factories in Tests.Tools.Basic

**Status:** IN-PROGRESS 2026-10-27. Steps 0 (tool gap), 1 (survey), 2 and 3 done; the prerequisites below are done (8417587a); Steps 4-5 are open.

## Re-sequencing (2026-10-25, updated 2026-10-27)
Test DI was reconsidered: it needs no `Server.*` reference once the engine and tool-dependency
registration lists live in `Engines.*` / `Tools.*`. The factories stay (needed by `replaceConstruction`,
which keeps the call's arguments verbatim) but resolve whatever the call site does not pass from a
container, so a new constructor parameter is absorbed in one place. Order:
1. DONE 2026-10-27: Fold `Server.Basic` into `Server.Advanced` (`docs/current/proposals/closed/proposal_unify_server_projects.md`).
2. DONE 2026-10-27: registration move-down (`docs/current/proposals/closed/proposal_registration_move_down.md`):
   `AddRoslynSentinelEngines{Basic,Advanced}` in `Engines.*`; `AddRoslynSentinelToolImpls*` /
   `AddRoslynSentinelToolClasses*` in `Tools.*`; `Tests.Tools.Basic` and `Tests.Tools.Advanced` already
   resolve every tool class from a plain `ServiceCollection`.
3. Steps 2-3 below. Design (2026-10-27): each factory is `ActivatorUtilities.CreateInstance<T>(provider, args...)`
   with explicit arguments from the call site (matched by type) and every other constructor parameter
   resolved from a container built from the flat registration methods. Use a fresh provider per call, so no
   state is shared between tests (the process-global `Common` option classes are out of scope). Step 3
   becomes a resolve test (every factory overload returns an instance), not a signature-drift guard.
4. Steps 4-5 unchanged.

## Progress (2026-10-27)
- Steps 2-3 DONE (6eace8f3): `Tests.Tools.Basic/TestEngines.cs` (15 overloads, 11 classes), `TestTools.cs`
  (`WorkspaceTools` short and 15-argument forms, `WorkspaceReadNavigationImpl`), `TestFactoryResolveTests.cs` (20 tests).
  A passed workspace manager overrides `IWorkspaceManager` / `ISolutionProvider` / `IWorkspaceReader` in the per-call
  provider, otherwise defaulted engines would each get a second `PersistentWorkspaceManager`. Not yet factored:
  `WholeFileWriteTools` (25 sites) and the other tool classes (`// TODO` in `TestTools.cs`).
- 2026-10-27: in a fresh session, `McpToolsetControl(moveExtract)` made `SemanticFindReplace` visible to the main session
  (schema loadable). Subagent visibility is still unverified: do the Step 4 pre-step first.
- Step 4 was BLOCKED (see 2026-10-27 note above): `replaceConstruction` could not be called. `McpToolsetControl(moveExtract)` reports the
  toolset enabled, but `SemanticFindReplace` never reaches the calling session's tool list, and two subagents
  (one enabling the toolset itself, one spawned afterwards) saw the same, so a subagent inherits the parent's tool list. Resume in
  a FRESH chat session started after `moveExtract` is enabled (or with it enabled at startup), then run the slices in
  descending site count: PersistentWorkspaceManager (103), SymbolNavigationEngine (77), ValidationEngine (75), DiffEngine (64),
  SentinelConfiguration (43), StructuralRefinementEngine (35), DiagnosticEngine (32), BuildEngine (27), then the 23-site engines,
  WorkspaceReadNavigationImpl (26) and WorkspaceTools (22). Per slice: preview, apply, Build, RunTest `Tests.Tools.Basic`.
- `Tests.Tools.Basic` full run: one flaky failure, `ServerBinaryStalenessGitTests.LaterCommitTouchingOnlyDocs_IsNotStale`
  ("git failed to run: init --quiet"), passes alone.

## Problem
Test projects hand-construct engines and tool classes with `new X(...)` at roughly 1,800-2,000 sites
over about 99 classes (two counts from a survey, not reconciled). Biggest: `Tests.Tools.Basic` 719,
`Tests.Advanced` 519, `Tests.Battery.Advanced` 322, `Tests.Tools.Advanced` 220. Top classes:
`PersistentWorkspaceManager` 352, `SentinelConfiguration` 193, `ValidationEngine` 95, `DiffEngine` 88,
`SymbolNavigationEngine` 88. About 193 sites are `BuildTools` helpers that wire about 12 engines plus a
15-parameter `WorkspaceTools` (e.g. `Tests.Tools.Basic/ApplyDiffSizeGuardTests.cs:40-65`). Every
constructor change cascades to all of them (open as Bug 1 in
`project_engine_reorg_final_closeout_bugs_1_2_3`).

Parameterless constructors were rejected (each engine needs a workspace manager; they would create
invalid-state objects and add DI constructor-selection ambiguity, see `TODO.md:102`). Full DI in tests
was not adopted in the first pass because most test projects cannot reference `Server.*` and tool
registration is coupled to `IMcpServerBuilder`; see Re-sequencing for the revised route.

## Decision
Per-test-project static factories that mirror the constructor shapes tests actually use, building the
same dependency graph production does and defaulting the rest (logger, config, sibling engines).
Existing `new X(...)` sites are rewritten to factory calls by a semantic tool, not text replace.
Pilot in `Tests.Tools.Basic` only; roll out to other projects after measuring.

**SemanticFindReplace today cannot do this.** Its only operation is `invertBoolean`
(`SemanticFindReplaceTools`, gated by toolset `moveExtract`). Step 0 extends it.

**Pre-renaming `_engine` / `_tools` fields is not a prerequisite.** A semantic tool matches the
constructed type's symbol, not the variable name, so field names do not change how many sites one pass
hits. What does limit a pass is argument-shape variety, so the survey groups by argument shape instead.
Field renames can follow later purely for readability (`RenameSymbol`, one call per field).

## Execution rules
- Dispatch all C# edits to a subagent, sequentially, never in parallel (CLAUDE.md).
- Migration-first: add the factory beside the existing constructors; move sites in slices; never change
  production constructors in this plan.
- One `Build` or `RunTest` at a time. Build 0 errors after every slice; run `Tests.Tools.Basic` after
  each class migrated and compare against `reference_known_failing_tests`.
- Each slice is one class, one project, preview before apply.

## Steps

### Step 0 - Close the tool gap (TOOL-GAP) - DONE 2026-10-18 (committed f9be549a, 39a968cd)
- Files: `Tools.Basic/SemanticFindReplaceTools.cs`, `Engines.Basic/ConstructionReplacer.cs` (new),
  `SemanticReplaceEngine.cs`, `SemanticReplaceTypes.cs`, `Common/ToolEnums.cs`.
- Change: `SemanticFindReplace(operation: replaceConstruction, symbol, factory, mode, projectFilter?)`.
  `symbol` is the constructed type or one constructor; `factory` is a static non-generic method. Rewrites
  `new X(...)` and target-typed `new(...)` to the factory call, keeping arguments verbatim. The argument
  shape is checked by binding the rewritten call (no `argShape` parameter). Skipped sites are reported
  with a reason: initializer, shape mismatch, inside the factory's own type, inaccessible factory,
  project lacks a reference. Refuses if the factory return type is not assignable to the type.
- Done when: 10 new tests and the full `Tests.Tools.Basic` suite pass (1062/1062); Build 0 errors.
  Not yet exercised through a live server call (binary stale until restart).
- If declined: fall back to `implementer-sweep` batches of `ReplaceSnippet`, which is over tier limits for
  most classes; report rather than brute-force.

### Step 1 - Survey construction sites in Tests.Tools.Basic - DONE 2026-10-17
- Result: 766 sites in 62 files, 49 constructed types (earlier count said 719; not reconciled). 336 sites
  are in 32 `BuildTools`-style helpers, 199 in `[SetUp]`, 230 in test methods. CSV (766 rows):
  `.claude/handoff/ctor_sites_tests_tools_basic.csv` (local-only; regenerate with `Search`).
- Findings that shape Step 2: most classes have one dominant shape (e.g. `PersistentWorkspaceManager[logger]`
  103, `SymbolNavigationEngine[wm,logger]` 71, `DiffEngine[]` 64, `ValidationEngine[wm,diff,logger]` 59,
  `SentinelConfiguration[]` 43). 513 sites take a workspace manager, and in 86 of 105 methods two or more
  constructions must share one manager, so factories take it as a parameter typed `IWorkspaceManager`
  (also satisfies the `ISolutionProvider` ctors). 3 sites use object initializers and will be skipped
  (`GitToolsSmokeTests.cs:44`, `DocumentationToolsTests.cs:32`, `SearchModeTests.cs:71`). 23 files build
  10+ classes and hold 69% of sites. 15 `BuildTools` helpers share the 15-arg `WorkspaceTools` plus
  `WorkspaceReadNavigationImpl` shape, so one `TestTools.WorkspaceTools(wm)` covers them.
- Original brief: dispatch `explore-sonnet`. Use `Search(mode: constructor/references)`, not Grep.
- Output: a deduped CSV in the scratchpad: class, file, line, arity, argument kinds (field, local,
  `new`, fake, literal), enclosing construct (field initializer, `[SetUp]`, `BuildTools` helper).
- Group by (class, argument shape); list the distinct shapes per class and their site counts. Flag
  classes whose sites need a shared `PersistentWorkspaceManager` instance across several constructions.
- Done when: CSV exists; the top 10 classes and their shape counts are reported.

### Step 2 - Factory skeleton - DONE 2026-10-27 (6eace8f3)
- Files: new `Tests.Tools.Basic/TestEngines.cs` and `TestTools.cs`.
- Change: one static method per class, named after it. Overloads only for shapes found in Step 1 plus a
  short form taking just the workspace manager. Start with the top 10 engine classes and `WorkspaceTools`.
  Methods take the manager as a parameter rather than creating it, so sharing is preserved.
- Done when: Build 0 errors; factory used by one hand-migrated test class.

### Step 3 - Factory resolve test - DONE 2026-10-27 (6eace8f3)
- Files: `Tests.Tools.Basic/TestFactoryResolveTests.cs` (20 tests). Replaced the original drift guard: factories
  resolve unpassed constructor parameters from the container, so a new parameter is absorbed there.
- Change: every factory overload returns an instance.
- Done when: test passes and fails if a registration a factory depends on is removed.

### Step 4 - Migrate in slices
- Pre-step (visibility): dispatch `implementer-sweep` for the first slice's `replaceConstruction` preview
  (PersistentWorkspaceManager). If the subagent cannot see `SemanticFindReplace`, report `TOOL-GAP:` and write a
  blocker doc; do not fall back to manual edits. This is also the first live exercise of the Step 0 tool, and the
  server binary may be stale (`isServerBinaryStale`): read the preview's skip reasons before applying.
- Per class, in descending site count: `replaceConstruction` preview, review, apply; Build; RunTest on
  `Tests.Tools.Basic`; commit with an explicit file list.
- Then replace the `BuildTools` helpers with `TestTools` calls.
- Done when: every site whose shape the factory covers is migrated; remaining sites are listed.

### Step 5 - Measure and decide
- Baseline (record before slice 1): files/sites a trial constructor change touches today, e.g. adding a
  parameter to `ValidationEngine` (75 sites in `Tests.Tools.Basic`).
- Report: sites migrated vs remaining, number of overloads, tool skips by reason, the edit count a
  trial constructor change now needs, the `Tests.Tools.Basic` run time before/after (a `ServiceProvider` is
  built per factory call and never disposed; check tests that dispose their manager in `TearDown`), and the
  tool classes with no factory yet (`WholeFileWriteTools`, 25 sites; `// TODO` in `TestTools.cs`).
- Reconcile the 719 vs 766 site counts, and confirm `ServerBinaryStalenessGitTests.LaterCommitTouchingOnlyDocs_IsNotStale`
  carries a flaky-on-full-run comment. Decide whether to roll out to `Tests.Advanced`,
  `Tests.Battery.Advanced` and `Tests.Tools.Advanced`.

## Out of scope
- Production constructors and DI registration.
- Process-global static option classes in `Common` (cause of the `093be31c` leak); factories do not fix them.
- Other test projects until Step 5.

## Risks and open decisions
- Constructor shapes were only sampled (about 99 classes constructed in tests); the real shape count is
  unknown until Step 1.
- Factory location: one per test project (this plan) vs one `Tests.Support.*` project per layer. Shared
  `RoslynSentinel.Tests` references only `Common` and `Engines.Basic`, so it cannot host tool or Advanced
  factories.
- Overload growth could recreate the constructor surface in the factory; cap by migrating odd shapes by hand.
- Factory graph can drift from production; factories resolve unpassed parameters from the real registration
  lists and Step 3 checks every overload resolves.
