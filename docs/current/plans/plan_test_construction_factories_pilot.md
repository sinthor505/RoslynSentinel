# Plan: Pilot shared engine/tool construction factories in Tests.Tools.Basic

**Status:** DRAFT 2026-10-18, re-sequenced 2026-10-25. Step 0 (tool gap) and Step 1 (survey) are done and committed; Steps 2-5 wait on the server fold and registration move-down below.

## Re-sequencing (2026-10-25)
Test DI was reconsidered: it needs no `Server.*` reference once the engine and tool-dependency
registration lists live in `Engines.*` / `Tools.*`. The factories stay (needed by `replaceConstruction`)
but are implemented through a per-test DI container, so they track production wiring. Order:
1. Fold `Server.Basic` into `Server.Advanced` (`docs/current/proposals/proposal_unify_server_projects.md`, approved).
2. Move `AddRoslynSentinelEngines*` into `Engines.*` and add a flat `AddRoslynSentinelToolDependencies*`
   in `Tools.*` (own proposal, not yet written).
3. Steps 2-3 below, with the factory methods built on `ServiceCollection` and Step 3's drift guard
   mostly unnecessary (the container resolves the real constructors).
4. Steps 4-5 unchanged.

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

### Step 2 - Factory skeleton
- Files: new `Tests.Tools.Basic/TestEngines.cs` and `TestTools.cs`.
- Change: one static method per class, named after it. Overloads only for shapes found in Step 1 plus a
  short form taking just the workspace manager. Start with the top 10 engine classes and `WorkspaceTools`.
  Methods take the manager as a parameter rather than creating it, so sharing is preserved.
- Done when: Build 0 errors; factory used by one hand-migrated test class.

### Step 3 - Drift guard test
- Files: new test in `Tests.Tools.Basic` (needs a `Server.Basic` reference, or put it in `Tests.Server`;
  after the fold and registration move-down it needs neither, and may reduce to a factory-resolves test).
- Change: for each factory method, resolve the class from `AddRoslynSentinelEnginesBasic` and assert the
  constructor parameter types match what the factory passes.
- Done when: test passes and fails when a constructor parameter is added without updating the factory.

### Step 4 - Migrate in slices
- Per class, in descending site count: `replaceConstruction` preview, review, apply; Build; RunTest on
  `Tests.Tools.Basic`; commit with an explicit file list.
- Then replace the `BuildTools` helpers with `TestTools` calls.
- Done when: every site whose shape the factory covers is migrated; remaining sites are listed.

### Step 5 - Measure and decide
- Report: sites migrated vs remaining, number of overloads, tool skips by reason, and the edit count a
  trial constructor change now needs. Decide whether to roll out to `Tests.Advanced`,
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
- Whether `Server.*` can be referenced from `Tests.Tools.Basic` without a cycle was not checked (Step 3).
- Factory graph can drift from production; Step 3 is the mitigation.
