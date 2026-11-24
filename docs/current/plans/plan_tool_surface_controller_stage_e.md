# Plan: Tool-surface controller stage E - caches keyed by snapshot, one captured snapshot per call, runtime-toolset tools callable

**Status:** IN PROGRESS 2026-11-24. Steps 1-16, 19-20 done (07a48893..615805a8); Steps 17-18 deferred (HOLD, R8); Step 21 full run pending. Approved 2026-11-24: Stage E: validator and hints read the snapshot, schema cache keyed per snapshot, HiddenSchemaParams deleted; Steps 17-18 (runtime-toolset tools callable) on HOLD for the owner (R8, SubAgent children are eval surfaces). Review fixes applied; other decisions taken at recommendation (owner away).

Stage 4 of [proposal_tool_surface_controller.md](../proposals/proposal_tool_surface_controller.md). Follows phase A/B
([closed/plan_tool_surface_controller.md](closed/plan_tool_surface_controller.md)), stage C
([closed/plan_tool_surface_controller_stage_c.md](closed/plan_tool_surface_controller_stage_c.md)) and stage D
([closed/plan_tool_surface_controller_stage_d.md](closed/plan_tool_surface_controller_stage_d.md), 1df39603..fa12d989), whose
"Out of scope" lists the validator `SchemaCache`, one captured snapshot per call, and `ToolCallHint` plus the `HiddenSchemaParams`
deletion as stage E work.

## Problem

After stage D the snapshot owns hidden parameters, but four readers still answer from process statics or startup configuration
(all verified 2026-11-24):

- **`ActiveToolSurface.IsCallable` ignores runtime toolsets.** It answers "class active AND name allowed"
  (`RoslynSentinel.Common/ActiveToolSurface.cs:54-64`); its own doc comment says it "does not see tools a caller switched on at
  runtime through McpToolsetControl" (`:47-53`). The instance is built once at registration
  (`RoslynSentinel.Server.Advanced/ServiceRegistrationExtensionsBasic.cs:393-401`). So after `McpToolsetControl(projectAdmin, true)`
  every hint gated by it still omits the enabled tools; for example the ReadFile not-found hint offers `ListSolutionItems` only
  when it is callable (`CallableToolHintTests.ReadFileNotFound_DefaultAndListSolutionItemsCallable_OffersListSolutionItems`), and
  `ListSolutionItems` is in the `projectAdmin` toolset (`RoslynSentinel.Common/ToolsetCatalog.cs:53`). Measured blast radius
  (`Search(mode: references)` on `IsCallable`, 2026-11-24): 48 hits counted per line, of which **36 production lines in 15 files**
  (37 calls: `WorkspaceFileEditImpl.cs:73` holds two), 11 in tests
  (`CallableToolHintTests` 8, `EditImpactToolNamesTests` 3) and 1 doc cref (`ICircuitBreaker.cs:54`). The instance reaches them
  through 14 constructor parameters (`PersistentWorkspaceManager.cs:352`, `AdvancedRefactoringTools`, `GenerationTools`,
  `AdminTools`, `CoreRefactoringTools`, `RefactoringExtractionDocsImpl`, `RefactoringSignatureImpl`, `RefactoringStructuralImpl`,
  `ServerStatusTools`, `WholeFileWriteTools`, `WorkspaceBuildTestImpl`, `WorkspaceFileEditImpl`, `WorkspaceReadNavigationImpl`,
  `WorkspaceTools`), 5 `EditImpactGate` method parameters (`EditImpactGate.cs:27`, `:107`, `:125`, `:144`, `:162`), one
  `MoveFileCopyAndObsolete.RunAsync` parameter, the engine closure (`EngineServiceCollectionExtensionsBasic.cs:56-60`) and three
  filter lookups (`ServiceRegistrationExtensionsBasic.cs:1055`, `:1444`, `:1574`). Phase A/B deliberately left it alone because
  switching it is a behaviour change (its Decision section).
- **The validator schema cache is process-wide and keyed by tool name.** `SchemaCache` (`ToolArgumentValidator.cs:15`) is filled
  on first use (`:117`) and never invalidated; its doc says "the schema is fixed for the process lifetime" (`:8-14`), which stops
  being true once the controller can change a schema. Every multi-host process works around it: the eval harness calls
  `ClearSchemaCacheForTests()` per world (`RoslynSentinel.Tests.ModelEval/ModelEvalMcpEnvironment.cs:123`) and 13 more call sites in
  10 test files do the same (`Search` 2026-11-24, 15 matches in 12 files including the declaration). `Validate` keeps a defensive
  `!declared.Contains("validateOnApply")` guard only because of it (`ToolArgumentValidator.cs:1307-1308`).
- **No call is validated against one snapshot.** `IsHiddenParameter` reads `controller.Current` on every invocation
  (`ToolArgumentValidator.cs:1033`): once per flat key in `WrapFlatBatchParameters` (`:1070`, inside a `Where`), once at the
  `validateOnApply:false` refusal (`:1314`) and once per argument in `Validate` (`:1334`). An apply that commits mid-call can give
  one call two answers. Proposal decision 3 ("a call is validated against the snapshot it started under") does not hold.
- **`ToolCallHint` still reads the process registry.** `ToolCallHint.Call` keeps a supplied pair that the catalog's schema lacks
  only when `HiddenSchemaParams.IsHidden(toolName, name)` (`ToolCallHint.cs:96-97`, TODO from stage D Step 11). The registry is
  written by every tool build in the process (`McpToolSchemaPatcher.cs:680-686`), including the reflection catalog's own builds
  (`ToolSignatureCatalogReflection.cs:77`, `services: null!`). `HiddenSchemaParams` (`HiddenSchemaParams.cs:12-13`, `[Obsolete]` since
  stage D) has 30 references in 12 files (production 5 in 3 files, ModelEval 1, tests 24 in 8 files) plus comment mentions in 17 files.

INBOX line 2026-11-24 03:30 (ToolCallHint mirror) is closed by this plan.

## Decision

- **Callability: additive rule (R1), switch on HOLD (R8).** `IsCallable(name)` = today's startup rule **OR** the committed snapshot
  lists `name` with `Origin == RuntimeToolset`. Every consumer keeps its predicate (method group `surface.IsCallable` on the one DI
  instance), so no consumer is edited. The rule only differs from today where a runtime toolset is on, which needs `ToolsetService`,
  registered only in claude-lean with `ToolsetControlTools` active (`ServiceRegistrationExtensionsBasic.cs:437`). No in-process
  ModelEval or Harness host uses claude-lean or `ToolsetService` (`Search` over `RoslynSentinel.Tests.ModelEval/**` for
  `claude-lean|ClaudeLeanMode|ToolsetService|SetEnabled\(`: 0 matches), and Step 5's test pins it per surface. **But SubAgent and
  SubAgentEval child servers are eval surfaces in claude-lean** (R8), so the switch itself (Steps 17-18) is held for the owner. The
  binding plumbing (Steps 1, 2, 4, 5) lands now and is text-neutral. The full switch "callable = exposed in the snapshot" is not
  taken here (R1).
- **Binding without a constructor change.** `ActiveToolSurface.WithLiveSnapshot(Func<ToolSurfaceSnapshot?>)` (new) returns a
  `with`-copy carrying a live-snapshot delegate; registration becomes a factory over the eagerly built instance, so the instance
  keeps the class set captured before the claude-lean rebinding (`ServiceRegistrationExtensionsBasic.cs:413-427`). The delegate reads
  `ToolSurfaceController.CurrentIfBuilt` (new), which never triggers a build (unlike `Current`, whose first read refreshes), so
  `IsCallable` cannot re-enter options construction (stage D R1) and falls back to the startup rule before the first snapshot.
  `Refresh` sets `_built = true` (`ToolSurfaceController.cs:301`) before it assigns `_current` (`:321`), so a lock-free read during the
  first build can see the static `Empty` (version 0); `CurrentIfBuilt` therefore returns `_current.Version > 0 ? _current : null`, and
  the validator treats a version-0 snapshot as no snapshot (uncached).
- **Validator: one captured snapshot per call (R3).** The seven passes that read the schema (`NormalizeParameterCase`,
  `NormalizeEnumCase`, `ApplyParameterAliases`, `ApplyArgumentRewrites`, `WrapFlatBatchParameters`, `CoerceStringToStructured`,
  `Validate`) gain an optional trailing `ToolSurfaceSnapshot? surface = null`. The filter reads `Current` once per call and passes it
  to all seven; a direct caller that omits it gets the server's `Current`, resolved once per pass. Optional trailing parameters keep
  every test call compiling (`Validate`: 1 production + about 17 test calls; `WrapFlatBatchParameters`: 1 caller). Scope: the captured
  snapshot decides **hidden names and the cache key**; the schema itself is still read from the live collection (R2 residual). The
  filter now reads `Current` at the top of every call; if that read throws, the filter's existing outer `catch` skips validation and
  the call runs unvalidated, as for any other validator exception (`ServiceRegistrationExtensionsBasic.cs:1593-1598`).
- **Validator: schema cache keyed by snapshot instance (R2).** `SchemaCache` becomes a
  `ConditionalWeakTable<ToolSurfaceSnapshot, ConcurrentDictionary<string, ...>>`. A new snapshot (toolset toggle, schema change, any
  fingerprint move) gets a new table; an old one is collected with its snapshot. Not keyed by `Version`: every controller numbers
  from 1 (`ToolSurfaceController.Refresh` builds `current.Version + 1` from `ToolSurfaceSnapshot.Empty`, version 0), so two hosts in
  one process share version numbers. Without a snapshot (no controller) the schema is read uncached.
- **Hints: the catalog carries its own hidden parameters (R4).** `ToolSignatureCatalog` gains a hidden-parameter map and
  `IsHidden(tool, parameter)`; `ToolSignatureCatalogReflection.FromLoadedAssemblies` fills it from `StrippedToolParameters.For(tool)`
  for each tool it builds, i.e. exactly what was stripped from the schema that catalog holds. `ToolCallHint.Call` reads
  `catalog.IsHidden`. This removes the process-mutable input rather than keying it: the catalog is immutable, so there is nothing to
  invalidate. A live per-host hint catalog built from the snapshot (the proposal's consumer row) changes hint text and is not in
  this plan (R4, STOP-worthy).
- **Then delete `HiddenSchemaParams`** and `ClearSchemaCacheForTests`, migration-first: the validator fallback goes (Step 12), the
  resets go (Steps 13-15), the mirror write and the class go last (Step 16).
- **Empty collection: left to stage F (R5).** Stage E changes no population. With a null collection the controller never builds
  (`Refresh` returns early, `CurrentIfBuilt` stays null), `IsCallable` uses the startup rule, `TryGetSchemaParameters` returns null
  as today (`ToolArgumentValidator.cs:36-38`), and `ApplyToolset` throws its existing "no mutable tool collection" error. Creating
  the collection belongs with the controller filling it (stage F).
- **No new DTOs.** Every change adds members to existing types (`ActiveToolSurface`, `ToolSurfaceController`,
  `ToolSignatureCatalog`) or private fields; the validator keeps its tuple. No `NEW-DTO`/`DTO-EXEMPT` lines are needed.

Decisions, 2026-11-24 (review verdict "sound design, not dispatch-ready"; review fixes 1-9 applied; owner away):
- R1-R7 and R9-R13 taken at the recommendation.
- R8 is an owner decision: SubAgent/SubAgentEval child servers are eval surfaces, so Steps 17-18 are on **HOLD** and do not run
  overnight; Step 21 records them as deferred.
- Batches B1 (Steps 1-8) and B2 (Steps 9-16) run overnight; B3 (Steps 17-18) is held.

## Execution rules

- Start after stage D Step 18 (full run) is recorded and that plan is closed; the expected baseline is that run.
- Dispatch per the `dispatch-implementer` skill; depth tier D1 at writing (files <= 3, edits <= 15, acceptance <= 1, tool calls
  <= 35), sweep tier S0 (1 operation, 8 files, 25 call sites, 2 projects). At most two implementing agents at once.
- One `Build`/`RunTest` at a time (paired lanes included; the dispatcher serializes builds); `Build` 0 errors per step; the
  dispatcher runs the named tests; test baseline 0 failures.
- **Synchronous pre-commit gate** for Steps 3, 4, 6, 7, 8, 9, 11, 12, 15, 16 and 17 (they touch the validator, hints, schema patch
  code, the eval harness or the callability predicate): the dispatcher runs, as one synchronous `RunTest` on the uncommitted change,
  the **standard gate** `EvalToolSchemaVisibilityTests`, `ChainEvalToolSurfaceTests`, `ChainEvalToolSurfaceLeanTests`,
  `EditArmToolSurfaceTests`, `HiddenParametersParityTests`, `ToolSchemaPatchPipelineTests`, `ClaudeLeanSchemaBudgetTests`, plus the
  step's own Done-when tests, plus `EvalSurfaceCallabilityTests` once Step 5 has landed. A red gate means the step is not committed.
  A gate failure is never fixed by editing descriptions, budgets or assertions.
- **Paired gated steps** (4 || 6, 5 || 7, 9 || 11): when both slices of a pair are ready, the gate runs once on the combined
  uncommitted tree (the union of both steps' test lists plus the standard gate); if green, each step is committed separately with
  `Git` scope `listed` and its own file list. If red, re-run the gate with one slice's changes only (the other stashed aside by its
  file list) to find which slice broke it; commit the green one, report the red one.
- **Eval STOP rule (owner away overnight).** A changed outcome in `EvalToolSchemaVisibilityTests`, `ChainEvalToolSurfaceTests`,
  `ChainEvalToolSurfaceLeanTests`, `EditArmToolSurfaceTests` or `EvalSurfaceCallabilityTests` means: do not commit, record the diff
  in this plan, stop the run. No step that runs overnight is meant to change any tool text.
- **HOLD: Steps 17-18 do not run without the owner's R8 decision.** Step 17 is the only step that changes tool text, and it is
  visible on SubAgent/SubAgentEval child servers, which are eval surfaces that `EvalSurfaceCallabilityTests` does not cover (R8).
  While held: Step 19 records its D case `n/a (held)`, Step 20 keeps the "IsCallable is startup configuration" sentence, Step 21
  records 17-18 as deferred with an INBOX line.
- No tool `[Description]` or schema text changes in any step; `ClaudeLeanSchemaBudgetTests` must pass unchanged.
- Expected CS0618 warnings: `ToolArgumentValidator.ClearSchemaCacheForTests` from Step 9 (14 call sites) until Steps 13 and 15
  remove them; the existing `HiddenSchemaParams` warnings shrink step by step and are gone after Step 16. Report them as expected.
- `Search` each file for `TODO` before editing it (CLAUDE.md); a new `// TODO` gets its `docs/current/INBOX.md` line.
- Peer check (`Git(status)`, `ListAgents`) before Steps 4 and 8 (`ServiceRegistrationExtensionsBasic.cs`, also edited by
  `plan_stateless_http_toolset_enablement.md`) and Step 15 (`ModelEvalMcpEnvironment.cs`, also edited by
  `plan_json_toolset_definitions.md` Step 21).
- Commits carry `Plan: plan_tool_surface_controller_stage_e`; Step 21's commit carries `Closes: plan_tool_surface_controller_stage_e`.
- Live checks only after commit and an own-server restart, with no subagent mid-operation.

## Steps

Order: contract skeleton (1); callability wiring, text-neutral (2, 4) and its eval proof test (5); catalog hidden map (3);
validator per-call capture and cache (6-10); hints off the registry (11); registry removal (12-16); the one behaviour change,
on HOLD for the owner (17-18); LIVE (19); docs (20); closing (21).

Slice map: 1; 2 || 3; 4 || 6; 5 || 7; 8; 9 || 11; 10 || 12; 13 || 14; 15; 16; 17; 18; 19; 20; 21.
(4 edits `ServiceRegistrationExtensionsBasic.cs`, 6 edits `ToolArgumentValidator.cs`; 5 is a ModelEval test file, 7 the validator;
9 the validator, 11 `ToolCallHint.cs` and its tests; 10 a new test file, 12 the validator plus two test files; 13 and 14 disjoint test
files.)

Suggested orchestrator batches:
- **B1** = Steps 1-8 (1; 2 || 3; 4 || 6; 5 || 7; 8): contract, binding, eval pin test, per-call capture. Runs overnight.
- **B2** = Steps 9-16 (9 || 11; 10 || 12; 13 || 14; 15; 16): snapshot-keyed cache, hints off the registry, `HiddenSchemaParams`
  deleted. Runs overnight.
- **B3** = Steps 17-18: **HOLD** for the owner (R8); not run overnight.
- Main session: Steps 19-21 after B2 (Step 19 cases that need Step 17 recorded `n/a (held)`; Step 21 records 17-18 as deferred and
  its commit carries `Closes:`).

### Step 1 - Contract skeleton
- Done: 07a48893
- Files: `RoslynSentinel.Common/ActiveToolSurface.cs`, `RoslynSentinel.Common/ToolSurface/ToolSurfaceController.cs`,
  `RoslynSentinel.Common/ToolSignatureCatalog.cs`
- Depends on: none
- Effect: text-neutral (no callers)
- Lane: implementer (one `ReplaceSnippet` `batchEdits` call per file; XML doc comments on each new member)
- Change: new members (all `(new)`):
  ```csharp
  // ActiveToolSurface.cs
  /// Returns a copy whose IsCallable also counts tools a runtime toolset added, read from liveSnapshot (null while no
  /// snapshot is built). The copy is made with a record with-expression, so every other member carries over.
  public ActiveToolSurface WithLiveSnapshot(Func<ToolSurfaceSnapshot?> liveSnapshot)
      => throw new NotImplementedException(); // TODO implement (Step 2)

  // ToolSurfaceController.cs
  /// The committed snapshot, or null while no refresh has found a tool collection. Unlike Current it never triggers a build.
  public ToolSurfaceSnapshot? CurrentIfBuilt => throw new NotImplementedException(); // TODO implement (Step 2)

  // ToolSignatureCatalog.cs
  /// Builds a catalog from (tool name, InputSchema) pairs plus, per tool, the parameter names stripped from that schema but
  /// still accepted. A null map means no hidden parameters.
  public static ToolSignatureCatalog FromSchemas(IEnumerable<KeyValuePair<string, JsonElement>> inputSchemasByToolName,
      IReadOnlyDictionary<string, IReadOnlyCollection<string>>? hiddenParametersByToolName)
      => throw new NotImplementedException(); // TODO implement (Step 3)
  /// True when parameterName was stripped from toolName's schema in this catalog but is still accepted (ordinal, both names).
  public bool IsHidden(string toolName, string parameterName) => throw new NotImplementedException(); // TODO implement (Step 3)
  ```
  The existing `FromSchemas(IEnumerable<KeyValuePair<string, JsonElement>>)` (`ToolSignatureCatalog.cs:36`) stays; the arities differ,
  so no call becomes ambiguous.
- Done when: `Build` 0 errors.

### Step 2 - Implement CurrentIfBuilt and the live-snapshot binding (stored, not yet read)
- Done: ef86f1c9
- Files: `RoslynSentinel.Common/ActiveToolSurface.cs`, `RoslynSentinel.Common/ToolSurface/ToolSurfaceController.cs`
- Depends on: 1
- Pairs with: Step 3
- Effect: text-neutral (`IsCallable` is unchanged; nothing calls `WithLiveSnapshot` yet)
- Lane: implementer
- Change:
  - `CurrentIfBuilt` reads `_current` once into a local and returns it when its `Version > 0`, else null (no lock, no `Refresh`).
    Not `_built`: `Refresh` sets `_built = true` (`ToolSurfaceController.cs:301`) before it assigns `_current` (`:321`), so during the
    first build `_built` can be true while `_current` is still the static `Empty`. Comment why.
  - `ActiveToolSurface`: private non-readonly field `Func<ToolSurfaceSnapshot?>? _liveSnapshot`; `WithLiveSnapshot` returns
    `this with { _liveSnapshot = liveSnapshot }`. Comment: a with-copy, so constructor parameters a later plan adds
    (`plan_json_toolset_definitions.md` Step 22) carry over; record equality now includes the delegate, and nothing compares
    `ActiveToolSurface` values (`Search` 2026-11-24).
- Done when: `Build` 0 errors.

### Step 3 - Catalog hidden-parameter map; the reflection catalog fills it
- Done: 16863873
- Files: `RoslynSentinel.Common/ToolSignatureCatalog.cs`, `RoslynSentinel.Common/ToolSignatureCatalogReflection.cs`
- Depends on: 1
- Pairs with: Step 2
- Effect: text-neutral (no reader of `IsHidden` until Step 11; schemas and tool lists of every catalog are unchanged)
- Lane: implementer
- Change:
  - New private field (frozen, ordinal) for the hidden map; the private constructor (`:19`) takes it; `Empty` passes an empty map.
  - The two-argument `FromSchemas` builds the same tools as today and copies the map (skip null keys; ordinal sets); the
    one-argument overload calls it with `null`.
  - `IsHidden`: false for null names or an unknown tool; else ordinal set lookup.
  - `FromLoadedAssemblies` (`ToolSignatureCatalogReflection.cs:50-89`): after each `CreateSentinelTool`, record
    `StrippedToolParameters.For(tool)` when non-empty, keyed by tool name (first declaration wins, as for the schema); pass the map to
    the two-argument `FromSchemas`. Comment: the strips belong to the schema this catalog holds, so the pair is consistent.
- Done when: `Build` 0 errors; synchronous pre-commit gate: standard gate plus `ToolCallHintTests`, `FullTestMarkerStoreTests`,
  `TestSuiteFilterTests`: all pass.

### Step 4 - Register the bound ActiveToolSurface
- Done: b91f9439
- Files: `RoslynSentinel.Server.Advanced/ServiceRegistrationExtensionsBasic.cs`
- Depends on: 2
- Pairs with: Step 6
- Effect: text-neutral (`IsCallable` does not read the delegate until Step 17; every resolve now returns the bound copy)
- Lane: implementer
- Change: replace `services.AddSingleton(activeToolSurface);` (`:401`) with a factory over the eager instance:
  ```csharp
  services.AddSingleton(sp =>
  {
      var controller = new Lazy<ToolSurfaceController?>(() => sp.GetService<ToolSurfaceController>());
      return activeToolSurface.WithLiveSnapshot(() => controller.Value?.CurrentIfBuilt);
  });
  ```
  Comment: the instance is built eagerly above so the claude-lean rebinding of `activeToolClasses` below cannot leak into it; the
  controller is resolved lazily and read through `CurrentIfBuilt`, so resolving `ActiveToolSurface` (14 constructors, the engine
  closure, three filters) never builds the tool collection. No consumer is edited: they all take the DI instance.
  `ImplementationInstance` is read only for `ToolAllowList` (`McpToolSchemaPatcher.cs:139`), so the factory form breaks no reader.
- Done when: `Build` 0 errors (also `scripts/build.ps1 -Flavor Solution`); synchronous pre-commit gate: standard gate plus
  `CallableToolHintTests`, `EditImpactToolNamesTests`, `McpServerStatusToolListingTests`, `ToolSurfaceControllerWiringTests`,
  `NoActiveToolsStartupTests`, `McpToolsetControlTests`: all pass.

### Step 5 - Eval-surface callability proof test
- Done: d2c8a06a
- Files: `RoslynSentinel.Tests.ModelEval/EvalSurfaceCallabilityTests.cs` (new)
- Depends on: 4
- Pairs with: Step 7
- Effect: text-neutral (pins today's behaviour). Until Step 17 lands, assertion (c) passes vacuously (both sides run the same rule);
  it becomes a real check only under Step 17, and even then it covers in-process eval hosts only, not SubAgent child servers or
  `scripts/Invoke-ToolComparison.ps1` (R8). Assertions (a) and (b) are real now.
- Lane: test-writer
- Change: one `[TestCaseSource]` case per eval surface registration, each a host built like `ChainEvalToolSurfaceTests.SetUp` but
  never started (no LM Studio): the `ModelEvalMcpEnvironment` default (`AddRoslynSentinelToolsAdvanced` with
  `ChainEvalToolSurface.NewModes()` and `NewHiddenTools(toolsetValue, null)` for `"lean"` and `"full"`); each `EditArm` on the Advanced
  surface (`NewAdvancedModes(arm)`, `NewAdvancedHiddenTools(arm, "lean")`); each `EditArm` on the Basic surface
  (`AddRoslynSentinelToolsBasic(services, EditArmToolSurface.NewBasicModes(arm), hideTools: EditArmToolSurface.ArmHiddenTools(arm))`);
  `ModelEvalToolSurfaces.Basic` and `ModelEvalToolSurfaces.WorkspaceAndSymbolNavigation`. Per case: read the controller's `Current`
  first (forces the build), then assert (a) `GetService<ToolsetService>()` is null, (b) no snapshot row has
  `Origin == RuntimeToolset`, (c) for every name in `McpToolSchemaPatcher.DiscoverAllDeclaredTools(<RoslynSentinel assemblies>)`,
  the DI `ActiveToolSurface.IsCallable(name)` equals an unbound copy's (`new ActiveToolSurface(s.ModeArg, s.ActiveModes,
  s.IncludeTools, s.ExcludeTools, s.ActiveToolClasses, s.ClassModes, s.AllowedToolNames)`). Also write to `TestContext.Out`, without
  asserting, the names where "exposed in the snapshot" and the startup rule disagree (the measurement R1 asks for). Test name:
  `EvalSurface_HasNoRuntimeToolsets_SoIsCallableEqualsTheStartupRule`.
- Done when: dispatcher runs `EvalSurfaceCallabilityTests`: all cases pass; the printed deltas are copied into R1.

### Step 6 - Validator: one snapshot per pass for the hidden-reading paths
- Done: c5594d85
- Files: `RoslynSentinel.Server.Advanced/ToolArgumentValidator.cs`
- Depends on: none in this plan (pairs with Step 4)
- Effect: text-neutral (the same `Current` is read once per pass instead of once per hidden check). The snapshot decides hidden
  names only; the schema is still read from the live collection (R2 residual).
- Lane: implementer
- Change:
  - New private `static RoslynSentinel.Common.ToolSurfaceSnapshot? ResolveSurface(ModelContextProtocol.Server.McpServer? server,
    RoslynSentinel.Common.ToolSurfaceSnapshot? captured)` = `captured ?? (server?.Services?.GetService(typeof(ToolSurfaceController))
    as ToolSurfaceController)?.Current`, then null when the result has `Version == 0` (the static `Empty`: no snapshot yet).
  - `TryGetSchemaParameters` (`:25-26`) gains a trailing **optional** `RoslynSentinel.Common.ToolSurfaceSnapshot? surface = null`
    (used for caching in Step 9). It must be optional: its five other callers (`:567`, `:775`, `:879`, `:1003`, `:1135`) move in
    Step 7, and a required parameter would not compile in between.
  - `IsHiddenParameter` (`:1024`) becomes `(ToolSurfaceSnapshot? surface, McpServer? server, string? toolName, string parameterName)`:
    with a surface, the row lookup it does today; without one, the existing registry fallback and its TODO (removed in Step 12).
  - `Validate` (`:1293`, today `(McpServer? server, string? toolName, IDictionary<string, JsonElement>? arguments,
    Func<string, bool>? isCallable = null)`) and `WrapFlatBatchParameters` (`:1054`) gain a trailing
    `RoslynSentinel.Common.ToolSurfaceSnapshot? surface = null`; each resolves `surface = ResolveSurface(server, surface)` once at entry
    and passes it to `TryGetSchemaParameters` (`:1062`, `:1299`) and to every `IsHiddenParameter` call (`:1070`, `:1314`, `:1334`).
- Done when: `Build` 0 errors; synchronous pre-commit gate: standard gate plus `SchemaHiddenParametersTests`, `SchemaLeanProfileTests`,
  `ToolSurfaceParameterTests`, `SurfaceAwareHintsArgumentValidatorTests`, `ToolParameterAliasTests`, `CoerceStringToStructuredTests`,
  `NumericEnumArgumentTests`: all pass.

### Step 7 - Validator: the other five passes take the snapshot
- Done: b1dda85a
- Files: `RoslynSentinel.Server.Advanced/ToolArgumentValidator.cs`
- Depends on: 6
- Pairs with: Step 5
- Effect: text-neutral
- Lane: implementer
- Change: `NormalizeParameterCase` (`:559`), `NormalizeEnumCase` (`:767`), `ApplyParameterAliases` (`:868`), `ApplyArgumentRewrites`
  (`:992`, after its existing `string? solutionRoot`) and `CoerceStringToStructured` (`:1127`) gain a trailing
  `RoslynSentinel.Common.ToolSurfaceSnapshot? surface = null` and call `TryGetSchemaParameters(server, toolName, ResolveSurface(server, surface))`
  (`:567`, `:775`, `:879`, `:1003`, `:1135`). `ApplyLegacySearchReferencesKind` reads no schema and is not changed. 10 edits.
- Done when: `Build` 0 errors; synchronous pre-commit gate: the Step 6 list: all pass.

### Step 8 - The validation filter captures the snapshot once per call
- Done: 5413d71b (R1 measurement eee9c423)
- Files: `RoslynSentinel.Server.Advanced/ServiceRegistrationExtensionsBasic.cs`
- Depends on: 4 (same file), 6, 7
- Effect: text-neutral (single snapshot per call for hidden names and the cache key; answers equal today's unless an apply commits
  during the call). New cost and failure mode: `Current` is read at the top of every call; it is placed inside the existing `try`,
  so if the read throws, the filter's outer `catch` (`:1593-1598`) skips validation and the call runs unvalidated, as for any
  other validator exception.
- Lane: implementer
- Change: in `AddArgumentValidationFilter` (`:1528`), first statement inside the `try`:
  `var surface = context.Server.Services?.GetService<ToolSurfaceController>()?.Current;` with a comment (proposal decision 3: a call
  is validated against the surface it started under; the schema itself is still read live; a throwing read is caught below and
  skips validation). Pass `surface: surface` to the seven calls (`:1538`, `:1541`, `:1546`, `:1559`,
  `:1564`, `:1569`, `:1576`). `validationIsCallable` stays as it is.
- Done when: `Build` 0 errors; synchronous pre-commit gate: the Step 6 list plus `McpToolsetControlTests`: all pass.

### Step 9 - Schema cache keyed by snapshot instance
- Done: 91bd37d6
- Files: `RoslynSentinel.Server.Advanced/ToolArgumentValidator.cs`
- Depends on: 6 (the `surface` parameter), 8 (so production passes a snapshot)
- Pairs with: Step 11
- Effect: text-neutral in a single-host process; in a multi-host process (tests, eval worlds) each host validates against its own
  schemas without a reset
- Lane: implementer
- Change:
  - Replace `SchemaCache` (`:15`) with `private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ToolSurfaceSnapshot,
    ConcurrentDictionary<string, (...same tuple...)>> SchemaCacheBySnapshot = new();` (a file-level `using` alias for the tuple is
    allowed; it declares no type).
  - `TryGetSchemaParameters`: with a non-null `surface` whose `Version > 0`, read/write the table from
    `SchemaCacheBySnapshot.GetValue(surface, _ => new(StringComparer.Ordinal))`; with null or a version-0 snapshot (the static
    `Empty`, which every controller shares before its first build), compute without caching. The tool is still looked up in
    `server.ServerOptions.ToolCollection` (R2 option a).
  - `ClearSchemaCacheForTests` (`:135`): `[Obsolete("Schema caches are per tool-surface snapshot; nothing to clear.")]`, body empty
    (14 CS0618 sites until Steps 13 and 15).
  - Rewrite the stale comments: cache summary (`:8-14`), "fixed for the process lifetime" in the case-lookup comment (`:93`), the
    `validateOnApply` guard comment (`:1307-1308`; the guard stays, cheap and harmless).
- Done when: `Build` 0 errors; synchronous pre-commit gate: the Step 6 list plus `McpToolsetControlTests` and
  `ToolSurfaceControllerTests`: all pass.

### Step 10 - Cache and per-call capture tests
- Done: 53f95fa8
- Files: `RoslynSentinel.Tests.Server/ToolSurfaceCacheTests.cs` (new)
- Depends on: 8, 9
- Pairs with: Step 12
- Effect: text-neutral
- Lane: test-writer
- Change: `[NonParallelizable]`, statics reset as `SchemaHiddenParametersTests.ResetStatics` does, hosts through an in-process client
  as in `SchemaHiddenParametersTests`; never call `ClearSchemaCacheForTests`. Tests:
  - `TwoHostsInOneProcess_ValidatorUsesEachHostsOwnSchema`: host A `--hide-params=validateOnApply`, a `ReplaceSnippet` call with
    `validateOnApply: false` gets the "validateOnApply:false is not available" refusal; then host B without hide args, no reset: the
    same call gets neither that refusal nor "Unknown parameter" (assert on the text only; the tool may fail later on its arguments).
  - `SchemaChangeThenRefresh_ValidatorReadsTheNewSchema` (proxy for a runtime parameter-rule apply): a declared parameter is accepted;
    remove it from `tool.ProtocolTool.InputSchema` in place and call `controller.Refresh("test")` (version + 1); the same call is now
    rejected as "Unknown parameter".
  - `ToolsetReenable_NewSnapshotEachTime_ValidatorStillValidatesTheReaddedTool` (claude-lean, `ToolsetService.SetEnabled(ToolSetName.git, ...)`):
    after enable, disable, enable, the version strictly increases and a `GitRemote` call with `bogusParam` is rejected as "Unknown
    parameter" after the second enable (no stale or missing entry).
  - `CapturedSnapshot_IsWhatThePassReads`: `Validate(server, "ReplaceSnippet", {notAParameter}, surface: fabricated)` where
    `fabricated = current with { Tools = ... }` lists `notAParameter` as hidden on `ReplaceSnippet`: accepted; with `surface` omitted:
    rejected.
- Done when: dispatcher runs `ToolSurfaceCacheTests`: all pass.

### Step 11 - ToolCallHint reads the catalog's hidden map
- Done: 0971300a
- Files: `RoslynSentinel.Common/ToolCallHint.cs`, `RoslynSentinel.Tests/ToolCallHintTests.cs`
- Depends on: 3
- Pairs with: Step 9
- Effect: text-neutral. In production the catalog is the reflection fallback (`ToolCallHint.Current`, `:29-42`); its strips and the
  host's strips come from the same startup rules, and toolset tools are built with the same surface, so the hidden set per tool is
  the same as the registry's. In eval worlds the catalog lists `validateOnApply` as a schema parameter since stage D Step 10, so the
  hidden branch is never reached for it either way. One latent difference: the eval harness drops the EnvOrFlag layer from its
  `ParameterSurface` (`ModelEvalMcpEnvironment.cs:124-125`) while the reflection catalog builds with `FromStartup()`, which keeps it;
  this matters only if `ROSLYNSENTINEL_HIDE_PARAMS` is set in an eval process, and the old registry held the same reflection strips,
  so hint text is unchanged even then. See R12 for multi-host test processes.
- Lane: implementer
- Change: in `Call` (`:96-97`) replace `HiddenSchemaParams.IsHidden(toolName, pair.Name)` with `catalog.IsHidden(toolName, pair.Name)`
  and delete the stage E TODO; rewrite the `Call` summary (`:64-68`, its cref names `HiddenSchemaParams`). In `ToolCallHintTests`:
  `Call_HiddenParameter_IsRenderedOnlyWhenRegistered` and `Call_HiddenStringValue_IsQuotedEvenWhenNumeric` build their probe catalog
  with `FromSchemas(pairs, new Dictionary<string, IReadOnlyCollection<string>> { ["HiddenParamProbe"] = ["lineBefore"] })` instead of
  `HiddenSchemaParams.Register`/`Clear` (and lose `[NonParallelizable]` if nothing else needs it); new
  `Call_HiddenPair_DependsOnlyOnTheCatalogPassed` (same schema, one catalog with the map and one without: only the first renders the
  pair) and `FromLoadedAssemblies_UnderLeanProfile_MarksStrippedParametersHidden` (`SchemaOptions.Configure(["--schema-profile=lean"])`,
  `FromLoadedAssemblies().IsHidden("ReplaceSnippet", "returnDiff")` true and `IsHidden("ReplaceSnippet", "reason")` false;
  `[NonParallelizable]`, statics reset in TearDown as `SchemaLeanProfileTests.ResetStatics` does).
- Done when: `Build` 0 errors; synchronous pre-commit gate: standard gate plus `ToolCallHintTests`, `FullTestMarkerStoreTests`,
  `TestSuiteFilterTests`, `SchemaLeanProfileTests`, `SchemaHiddenParametersTests`: all pass.

### Step 12 - The validator drops the registry fallback
- Done: d3ef14ae
- Files: `RoslynSentinel.Server.Advanced/ToolArgumentValidator.cs`, `RoslynSentinel.Tests.Server/SchemaHiddenParametersTests.cs`,
  `RoslynSentinel.Tests.Server/ToolSurfaceParameterTests.cs`
- Depends on: 6, 11 (no other production reader of the registry is left)
- Pairs with: Step 10
- Effect: text-neutral for every host that registers the validation filter: the filter is added only by
  `BuildCallToolFilterRegistrations` (`ServiceRegistrationExtensionsBasic.cs:248`), and the implementer confirms with
  `Search(mode: callers)` that the same registration path registers `ToolSurfaceController` (`:403`); if a filter host without a
  controller is found, stop and report it (R6).
- Lane: implementer
- Change:
  - `IsHiddenParameter`: without a surface return false; drop the `server` parameter and the stage E TODO (3 call sites).
  - Delete `SchemaHiddenParametersTests.Refusal_WhenSchemaDeclaresTheParameter_IgnoresAStaleHiddenRegistration` and
    `ToolSurfaceParameterTests.StaleRegistryEntry_IsIgnored_WhenAControllerIsRegistered`: both plant a registry entry the validator no
    longer reads (commit message says so).
  - `SchemaHiddenParametersTests`: delete the two resets in `ResetStatics` (`:45-46`); rewrite
    `HideParams_RegistersExactlyValidateOnApply` (`:181-182`) onto the host controller's `Current.Find("ReplaceSnippet")!.HiddenParameters`;
    reword the header comments (`:3`, `:23`) that name `HiddenSchemaParams`. That test gets its data from the file's
    `ListInputSchemasAsync(params string[] startupArgs)` helper, which builds and disposes the host internally and exposes only what
    the client lists (`SchemaHiddenParametersTests.cs:52-84`, used at `:175-183`): add a sibling helper in this file that also hands
    out the host's `IServiceProvider` (or reads `GetRequiredService<ToolSurfaceController>().Current` before disposing and returns
    the snapshot with the schemas), and use it here.
  - `ToolSurfaceParameterTests`: reword the comment at `:200` ("No HiddenSchemaParams.Clear() between the hosts" -> no reset between
    the hosts).
- Done when: `Build` 0 errors; synchronous pre-commit gate: the Step 6 list: all pass.

### Step 13 - Sweep: delete the obsolete static resets
- Done: 8484a2b4
- Files (all `RoslynSentinel.Tests.Server/`): `HiddenParametersParityTests.cs` (`ResetStatics` `:39-40`), `ToolSchemaPatchPipelineTests.cs`
  (`:47-48`), `ToolSurfaceControllerTests.cs` (`:35-36`), `ToolSurfaceControllerWiringTests.cs` (`:37-38`), `ToolSurfaceParameterTests.cs`
  (`ResetStatics` `:43-44`, `HarnessRegisteredSurface_WinsOverStartupDefault` `:224` plus the comment line that explains it),
  `CoerceStringToStructuredTests.cs` (`SetUp` `:46`, `TearDown` `:89`), `NumericEnumArgumentTests.cs` (`:40`, `:84`),
  `SurfaceAwareHintsArgumentValidatorTests.cs` (`:36`, `:85`). Line numbers are from 2026-11-24; locate by member name.
- Depends on: 9, 12 (same file `ToolSurfaceParameterTests.cs`)
- Pairs with: Step 14
- Effect: text-neutral
- Lane: implementer-sweep (S0: 1 operation, 8 files, 17 sites, 1 project). Operation: delete each statement
  `HiddenSchemaParams.Clear();` and `ToolArgumentValidator.ClearSchemaCacheForTests();` (one `ReplaceSnippet` per site, or one
  `batchEdits` call per file); a `[SetUp]`/`[TearDown]` method left empty is deleted. No other edit.
- Done when: `Build` 0 errors; `Search(mode: "text", query: "HiddenSchemaParams\\.Clear|ClearSchemaCacheForTests", includeTests: true)` shows
  only `SchemaLeanProfileTests.cs` (Step 14), `ModelEvalMcpEnvironment.cs` (Step 15) and the declaration; dispatcher runs the eight
  touched classes: all pass.

### Step 14 - SchemaLeanProfileTests asserts move onto snapshot rows
- Done: 95ec7e20
- Files: `RoslynSentinel.Tests.Server/SchemaLeanProfileTests.cs`
- Depends on: none in this plan
- Pairs with: Step 13
- Effect: text-neutral
- Lane: test-writer
- Change: delete the resets in `ResetStatics` (`:41-42`); rewrite `Full_RegistersNoHiddenParams` (`:198-199`) and
  `Lean_RegistersExactlyTheStrippedNames` (`:208-215`) onto the host controller's `Current.Find(tool)!.HiddenParameters`. The
  case-insensitive tool-name clause at `:209` (`"replacesnippet"`) was a property of the registry that no reader needs any more (the
  snapshot and the validator match ordinally): assert the ordinal name and say so in a comment. Reword the comments at `:3`, `:23`,
  `:189`. Both tests get their data from this file's `ListInputSchemasAsync` helper, which builds and disposes the host internally and
  exposes only what the client lists (same shape as `SchemaHiddenParametersTests.cs:52-84`): add a sibling helper in this file that
  also hands out the host's `IServiceProvider` (or reads the controller's `Current` before disposing and returns the snapshot with the
  schemas).
- Done when: dispatcher runs `SchemaLeanProfileTests`: all pass.

### Step 15 - The eval harness drops the static resets
- Done: 86b74f2a
- Files: `RoslynSentinel.Tests.ModelEval/ModelEvalMcpEnvironment.cs`
- Depends on: 9, 11, 12 (nothing reads the registry or the name-keyed cache any more)
- Effect: text-neutral by construction; **eval-surface file** (the eval STOP rule applies)
- Lane: implementer
- Change: delete `HiddenSchemaParams.Clear();` and `ToolArgumentValidator.ClearSchemaCacheForTests();` (`:122-123`) and rewrite the
  comment at `:118-121`: hidden-parameter rules, the hint catalog's hidden map and the validator schema cache are per host or per
  snapshot, so a later world needs no reset; `SchemaOptions` is still process-static.
- Done when: `Build` 0 errors; synchronous pre-commit gate: standard gate (all seven `EvalToolSchemaVisibilityTests`, including
  `OptIn_AfterADefaultWorld_IsNotRefused` and `Default_AfterAnOptInWorld_HidesAgain`) plus `EvalSurfaceCallabilityTests`: all pass with
  unchanged outcomes (any change: STOP, do not commit).

### Step 16 - Delete HiddenSchemaParams and ClearSchemaCacheForTests
- Done: 615805a8
- Files: `RoslynSentinel.Common/McpToolSchemaPatcher.cs`, `RoslynSentinel.Common/HiddenSchemaParams.cs` (deleted with `DeleteFile`),
  `RoslynSentinel.Server.Advanced/ToolArgumentValidator.cs`
- Depends on: 11, 12, 13, 14, 15
- Effect: text-neutral (removes a write-only mirror and a no-op)
- Lane: implementer
- Change: first `Search(mode: "text", query: "HiddenSchemaParams\\.|ClearSchemaCacheForTests\\(", includeTests: true)` must show only
  the mirror write (`McpToolSchemaPatcher.cs:685`) and the declaration (`ToolArgumentValidator.cs:135`); otherwise stop and report.
  Delete the TODO, pragma pair, comment and `Register` call (`McpToolSchemaPatcher.cs:682-686`); rewrite the `ApplyHiddenParameters`
  summary (`:632`, cref to `HiddenSchemaParams`, which would become CS1574); delete `ClearSchemaCacheForTests`; `DeleteFile`
  `HiddenSchemaParams.cs`.
- Done when: `Build` 0 errors with no CS0618 for `HiddenSchemaParams`/`ClearSchemaCacheForTests` and no CS1574; synchronous
  pre-commit gate: standard gate plus `SchemaLeanProfileTests`, `SchemaHiddenParametersTests`, `ToolSurfaceParameterTests`,
  `ToolCallHintTests`, `ToolSurfaceCacheTests`: all pass.

### Step 17 - HOLD (owner decision R8), BEHAVIOUR-CHANGING: IsCallable counts runtime-toolset tools
- Done: DEFERRED (HOLD, owner decision R8; INBOX line)
- Files: `RoslynSentinel.Common/ActiveToolSurface.cs` (plus the R8 option's files: (b) adds the launcher flag, (c) adds the Harness
  pin test as a prerequisite step)
- Depends on: 4, 5 (landed and green); **owner approval of R8**. Not run overnight.
- Effect: **behaviour-changing (tool text)**. On a server where a runtime toolset is on (claude-lean with `ToolsetControlTools`
  active, after `McpToolsetControl(set, true)` or a `--toolsets`/tab-state restore) and a snapshot has been built, every `IsCallable`
  consumer (table in R1) treats that set's tools as callable, so error and hint text that left them out may now name them (for
  example the ReadFile not-found hint offers `ListSolutionItems` once `projectAdmin` is on). After the set is disabled, the text is as
  before. Unchanged: in-process ModelEval and Harness hosts (no runtime toolsets, pinned by `EvalSurfaceCallabilityTests`), any host
  before its first snapshot, tool lists, schemas, descriptions, `McpServerStatus`. **Visible on eval surfaces:** SubAgent and
  SubAgentEval child servers run claude-lean with `McpToolsetControl` allowed, so a model under test that enables a set gets different
  hint text, and every SubAgent run after this commit differs from earlier runs (R8). Also visible in main claude-lean sessions.
- Lane: implementer
- Change: move today's body into a private `IsCallableByStartupConfiguration(string toolName)`; `IsCallable` returns true when that
  does, else `_liveSnapshot?.Invoke()?.Find(toolName) is { Origin: ToolSurfaceOrigin.RuntimeToolset }`. Rewrite the summary
  (`:47-53`): runtime-toolset tools count once a snapshot is built; the remaining deliberate difference is the always-on
  `ServerStatusTools` class.
- Done when: `Build` 0 errors; synchronous pre-commit gate: standard gate plus `EvalSurfaceCallabilityTests`, the R8 option's test
  (the child-surface pin for (c), or a test that the flag keeps the startup rule in a child for (b)), `CallableToolHintTests`, `EditImpactToolNamesTests`, `SurfaceAwareHintsArgumentValidatorTests`,
  `SurfaceAwareHintsOrientationNavigationTests`, `SurfaceAwareHintsBuildTestingTests`, `SurfaceAwareHintsNavigationEngineTests`,
  `SurfaceAwareHintsSymbolResolverTests`, `RejectedSnippetEditTests`, `McpToolsetControlTests`, `ToolSurfaceControllerWiringTests`:
  all pass.

### Step 18 - HOLD (with Step 17): callability tests
- Done: DEFERRED (HOLD, depends on 17)
- Files: `RoslynSentinel.Tests.Server/ActiveToolSurfaceLiveTests.cs` (new), `RoslynSentinel.Tests.Tools.Basic/CallableToolHintLiveSurfaceTests.cs` (new)
- Depends on: 17
- Effect: text-neutral
- Lane: test-writer
- Change:
  - `ActiveToolSurfaceLiveTests` (claude-lean host built like `ToolSurfaceControllerTests.BuildHarness`):
    `ToolsetEnable_MakesItsToolsCallable_DisableRevokes` (`SetEnabled(ToolSetName.projectAdmin, true)`: `IsCallable("ListSolutionItems")`
    true; disable: false); `BeforeFirstSnapshot_UsesTheStartupRule_AndForcesNoBuild` (call `IsCallable` before anything reads
    `Current`: answers the startup rule and `controller.CurrentIfBuilt` is still null afterwards);
    `StartupToolsetRestore_IsCallableAfterTheFirstSnapshot` (`ServerStartupHelpers.ApplyStartupToolsets(host.Services, ["--toolsets=git"])`,
    then `Refresh("startup")`: `IsCallable("GitRemote")` true); `UnboundSurface_KeepsTheStartupRule` (direct construction, as tests do).
  - `CallableToolHintLiveSurfaceTests`: the builder call of `CallableToolHintTests.ReadFileNotFound_DefaultAndListSolutionItemsCallable_OffersListSolutionItems`
    (`CallableToolHintTests.cs:170`) with a surface whose class set excludes `ListSolutionItems`, bound with `WithLiveSnapshot` to a
    hand-built snapshot listing `ListSolutionItems` as `RuntimeToolset`: the hint offers it; bound to a snapshot without the row: it
    does not (consumer-level coverage for one consumer, R7).
- Done when: dispatcher runs `ActiveToolSurfaceLiveTests` and `CallableToolHintLiveSurfaceTests`: all pass.

### Step 19 - LIVE check
- Done: 2026-11-24 on servedSha 615805a8: A ok-fresh (v1 startup, ListSolutionItems inactive with hint), B ok-rejected (Unknown parameter bogusParam on runtime tool ListSolutionItems), C ok-rejected (validateOnApply on Search), D n/a (held; baseline: ReadFile not-found hint does not name ListSolutionItems with projectAdmin on), E restored (projectAdmin off)
- Files: none
- Depends on: Steps 1-16 committed (17-18 are held: cases that need them record `n/a (held)`); own-server restart (`McpServerControl(StopServer)`, then `LoadSolution`);
  no subagent mid-operation
- Effect: none
- Lane: main session
- Change and outcome classes (record each with the date and `lastLaunch.servedSha` here):
  - **A.** `McpServerStatus(toolListing: all, toolNameFilter: "ListSolutionItems")` after the restart: `surfaceVersion` 1, trigger
    `startup`; the tool inactive with the `McpToolsetControl` hint, or `exposedBy: toolset:projectAdmin` if the tab restored it
    (`ok-fresh` | `ok-restored` | `wrong-version`). If restored, disable `projectAdmin` first and record that call.
  - **B.** Validator on a runtime tool (per-snapshot cache): `McpToolsetControl(projectAdmin, true)`, then a `ListSolutionItems` call
    with an extra `bogusParam: 1`: "Unknown parameter 'bogusParam'" and nothing runs (`ok-rejected` | `accepted` - stop and write a
    blocker | `no-validation`).
  - **C.** Hidden-name rejection still holds: `Search(reason: ..., mode: symbol, query: "ActiveToolSurface", validateOnApply: true)`
    returns "Unknown parameter 'validateOnApply'" (`ok-rejected` | `accepted` - blocker).
  - **D.** Callability (Step 17): `ReadFile(filePath: "NoSuchFolder/NoSuchFile.cs")` with `projectAdmin` on: the hint names
    `ListSolutionItems` (`ok-named` | `not-named` | `n/a (held)` while Steps 17-18 are on HOLD; optionally record `not-named` as the
    pre-change baseline); then `McpToolsetControl(projectAdmin, false)` and repeat: not named (`ok-gone` | `still-named` | `n/a (held)`).
  - **E.** Restore the starting toolset state found in A.
- Done when: A-E recorded.

### Step 20 - Docs
- Done: this commit
- Files: `docs/current/references/reference_architecture_map.md`
- Depends on: 9, 11, 16 (17 is held)
- Effect: none (docs)
- Lane: main session
- Change: "Tool surface" section (`:81-112`): the validator captures one snapshot per call and caches schemas per snapshot
  instance (no reset needed between hosts); `ToolCallHint` reads hidden parameters from its catalog (built from the same schemas);
  `HiddenSchemaParams` is gone (delete the `:109-110` sentence); `ActiveToolSurface.IsCallable` counts runtime-toolset tools through
  `WithLiveSnapshot`/`CurrentIfBuilt` only once Step 17 lands; while it is held, say the binding exists but `IsCallable` still answers from startup configuration (R8). Replace the
  "Not yet on the snapshot" line (`:111-112`) with what remains: the live per-host hint catalog (R4), `WriteToolAdviceHelper` (R10),
  population by the controller (stage F).
- Done when: the section matches the code; links resolve.

### Step 21 - No stubs left, full run, close
- Done: both sweeps 0 matches (non-test); INBOX lines added; full run pending
- Files: `docs/current/INBOX.md` (lines below), `docs/current/TODO.md` (the stages line), this plan (via `ProjectDoc close`)
- Depends on: all above except the held Steps 17-18
- Effect: none
- Lane: dispatcher (checks), main session (close)
- Change:
  - `Search(mode: "text", query: "NotImplementedException|TODO implement \\(Step", fileGlob: "RoslynSentinel.Common/{ActiveToolSurface.cs,ToolSignatureCatalog.cs,ToolSurface/ToolSurfaceController.cs}")`;
    `Search(mode: "text", query: "HiddenSchemaParams\\.|ClearSchemaCacheForTests|SchemaCache\\b")` (non-test files only: the test
    comment at `McpToolsetControlTests.cs:7` names `ToolArgumentValidator.SchemaCache` and no step edits it; it goes on the INBOX list).
  - Full `RunTest`.
  - INBOX: delete nothing (triage does that); append one line each for:
    - Steps 17-18 deferred (HOLD, owner decision R8: runtime-toolset tools callable; options (a)-(c) in this plan).
    - `WriteToolAdviceHelper.IsExposed` is a second startup-only callability rule (R10).
    - `CallToolFailureHandler.UnknownToolError` names `McpToolsetControl`/`McpServerStatus` without checking the surface (R11).
    - Stale mentions of removed statics in test comments: the `[NonParallelizable]` reasons naming `HiddenSchemaParams` and the
      validator schema cache in the five Step 13 fixtures with a `ResetStatics` (`HiddenParametersParityTests.cs:22`,
      `ToolSchemaPatchPipelineTests.cs:22`, `ToolSurfaceControllerTests.cs:17`, `ToolSurfaceControllerWiringTests.cs:17`,
      `ToolSurfaceParameterTests.cs:25`); `McpToolsetControlTests.cs:7` (`ToolArgumentValidator.SchemaCache`); the "process-static"
      header comments of `EvalToolSchemaVisibilityTests`, `RunTestOnFixtureTests`, `StopAdherenceControlRunTestTests`,
      `WideningGateMessageTests`.
    The 2026-11-24 09:55 line (`WrapFlatBatchParameters` hidden-key test gap) stays.
  - TODO.md stages line: E done; move "the controller must create an empty collection itself" to F (R5). The INBOX 2026-11-24 03:30
    line is resolved by Steps 11 and 16: note it in the close so triage moves it to CLOSED.md.
  - Record Steps 17-18 as deferred (HOLD, R8) in this plan's Status; close with `ProjectDoc(action: close)`; this step's commit
    carries `Closes: plan_tool_surface_controller_stage_e`.
- Done when: zero matches for both searches; full `RunTest` with 0 failures; INBOX lines present; plan closed.

## Out of scope

- A live per-host hint catalog built from the snapshot (`ToolCallHint.Current` per call or per host): R4, STOP-worthy.
- The full switch "callable = exposed in the snapshot" (drops allowed-but-unregistered tools, adds the always-on `ServerStatusTools`
  tools): R1, revisit at stage F.
- `WriteToolAdviceHelper` (R10) and `CallToolFailureHandler.UnknownToolError` (R11).
- Snapshot rows carrying input schemas (R2 option d); runtime parameter-rule layers; aliases (stage D R4).
- Creating the tool collection when none exists, and controller-owned population: stage F (R5).
- The `WrapFlatBatchParameters` hidden-key coverage gap (INBOX 2026-11-24 09:55): Step 6 touches the line, no new probe is attempted.

## Risks and open decisions

- **R1. Callability rule (taken: additive; Steps 17-18 on HOLD under R8).** Options: (a) startup rule OR runtime-toolset row; (b) callable =
  exposed in the snapshot; (c) leave `IsCallable` on startup configuration. (b) differs from today in two more ways: a tool allowed in
  an active class but never registered becomes uncallable, and the always-on `ServerStatusTools` tools become callable. On
  `ModelEvalToolSurfaces.Basic` and `WorkspaceAndSymbolNavigation` `McpServerStatus` is registered (no hide list) while
  `ServerStatusTools` is not in `ActiveToolClasses` (it is deliberately outside `ToolClassRegistry`,
  `ServiceRegistrationExtensionsBasic.cs:406-411`), so (b) is eval-visible wherever a consumer asks about such a tool: STOP-worthy.
  Step 5 prints the per-surface delta; record it here. (c) keeps the defect. Recommendation: (a) now, (b) with stage F, when
  "registered" equals "exposed" by construction.
  **Measured 2026-11-24 (Step 5, commit d2c8a06a, run on 5413d71b):** no delta on the Advanced surfaces (harness default lean/full, the
  four Advanced edit arms). On every Basic surface (four Basic edit arms, `ModelEvalToolSurfaces.Basic`): callable but not exposed =
  `ConvertAnonymousToNamed`, `ExtractMembers`, `Inline`, `InlineClass`, `Introduce`, `IntroduceParameterObject`, `InvertAssignments`,
  `MoveType`, `SyncInterface`, `WrapRange`. Exposed but not callable = `McpServerStatus` on every Basic surface and on
  `WorkspaceAndSymbolNavigation` (the always-on `ServerStatusTools` class, as predicted). So option (b) would change eval-visible text on
  the Basic surfaces: stays with stage F.
  Consumers and eval-visibility verdict under (a) (36 production lines, 37 calls): compile-rejection text via
  `CompilerErrorLookupHelper.DescribeAsync(isToolActive:)` (15 sites: `CoreRefactoringTools:65`, `AdvancedRefactoringTools:90`,
  `GenerationTools:79`, `RefactoringSignatureImpl:65`, `RefactoringExtractionDocsImpl:60`, `RefactoringStructuralImpl:74`,
  `MoveFileCopyAndObsolete:76`, `WholeFileWriteTools:99`, `:391`, `:721`, `:870`, `:1019`, `WorkspaceFileEditImpl:579`, `:1559`,
  `:2590`); edit-impact gate next-step names (`EditImpactGate.cs:172`, reached through `Describer` from `CoreRefactoringTools:65` and
  `AdvancedRefactoringTools:90`); not-found hints (`AdvancedRefactoringTools:80`, `GenerationTools:131`, `:240`, `WholeFileWriteTools:170`,
  `WorkspaceFileEditImpl:954`); refusal recovery tools (`WorkspaceFileEditImpl:73`, `:81`, `:1179`, `:1216`, `WholeFileWriteTools:282`,
  `:337`, `RefactoringSignatureImpl:86`); build/test and read hints (`WorkspaceBuildTestImpl:34`, `WorkspaceReadNavigationImpl:69`);
  member removal (`RefactoringStructuralImpl:808`); path resolution (`PersistentWorkspaceManager.cs:3761`); navigation engine
  (`EngineServiceCollectionExtensionsBasic.cs:60`); orientation breaker, loop latch and validator hints
  (`ServiceRegistrationExtensionsBasic.cs:1056`, `:1445`, `:1575`). All of them: **unchanged on in-process ModelEval/Harness hosts**
  (no runtime toolset exists there, Step 5) and in PlanStepRunner (R8); **changed on claude-lean surfaces after a toolset enable**,
  which includes the SubAgent/SubAgentEval child servers - eval surfaces (R8, STOP).
- **R2. Validator cache key (taken: snapshot instance).** Options: (a) `ConditionalWeakTable` keyed by the snapshot, filled from the
  live collection; (b) `Version` - collides across hosts (every controller starts at 1); (c) per `McpServerTool` instance - misses an
  in-place schema change on the same instance, which is the proposal's replace mechanism for same-name tools; (d) snapshot rows carry
  their input schema, so a call validates entirely against its snapshot - a `ToolSurfaceTool` field addition, better done in stage F
  when the controller builds the tools. Residual window in (a): between a collection change and its synchronous `Changed` refresh a
  call can key the old snapshot while reading the new collection; for add/remove this shows as "tool not found, validation skipped",
  exactly as today. Not STOP-worthy.
- **R3. Per-call capture (taken: optional trailing parameter).** Options: (a) `surface` parameter on the seven passes; (b) an
  `AsyncLocal` ambient set by the filter (no signature change, hidden state); (c) keep resolving per pass (up to one `Current` read
  per argument). (a) is explicit and source-compatible with every test call. Not STOP-worthy.
- **R4. Hint hidden parameters (taken: catalog-scoped; live catalog STOP-worthy, not planned).** Options: (a) the catalog carries the
  strips of its own schemas (Step 3/11); (b) `ToolCallHint` reads a per-host catalog built from the live snapshot, through an
  `AsyncLocal` call scope or a per-host registration. (b) changes hint text wherever the reflection catalog and the live surface
  differ: a tool not on the surface renders as a bare name (`Call` returns `toolName` when the catalog lacks it, `ToolCallHint.cs:84`), and eval worlds hide `validateOnApply` live but not in the reflection catalog. Eval-visible: owner decision. With (a), hint
  rendering of hidden parameters still follows startup rules, not a harness `ParameterSurface` - the same as today.
- **R5. Empty collection (taken: stage F).** See Decision. Note: phase A/B's risk "Empty startup leaves no collection (phase E)" uses
  "phase E" for controller-owned population, which its own "Later phases" list calls F; the TODO.md stages line repeats it under E.
  Step 21 moves that clause to F. Not STOP-worthy.
- **R6. Fallback removal (Step 12).** After it, a host that runs the validator without a controller treats no parameter as hidden.
  None is known: the three validator probe fixtures (`SurfaceAwareHintsArgumentValidatorTests`, `NumericEnumArgumentTests`,
  `CoerceStringToStructuredTests`) build their hosts with `AddRoslynSentinelToolsBasic`, which registers the controller. Step 12
  confirms the registration path before editing. Not STOP-worthy.
- **R7. Coverage gaps (planner-side, stated so the reviewer checks them).** Step 18 (held with 17) tests the predicate in a live
  claude-lean host and one consumer (the ReadFile not-found hint); the other consumer lines are covered only structurally (each takes the predicate by
  method group from the one DI instance; the reviewer of Step 4 confirms with `Search` that no production code builds its own
  `ActiveToolSurface` - today only the registration does, `ServiceRegistrationExtensionsBasic.cs:393`). The hidden-key path of
  `WrapFlatBatchParameters` (`:1070`) still has no test (INBOX 2026-11-24 09:55); the reviewer of Step 6 confirms its
  `IsHiddenParameter` call is the same expression as `Validate`'s. A green run does not cover either path beyond that.
- **R8. Step 17 is visible on SubAgent eval surfaces - OWNER DECISION, STOP; Steps 17-18 on HOLD.** SubAgent and SubAgentEval child
  servers are eval surfaces: `SubAgentChildServerLauncher.BuildServerArguments` passes `--mode=` + `ChildToolMode` (`"Claude-Lean"`,
  `SubAgentChildServerLauncher.cs:46`, `:61`; asserted by
  `SubAgentLaunchTests.ChildServerArguments_HideWriteFile_AndStayClaudeLeanWithSubAgentExcluded`, `SubAgentLaunchTests.cs:267-275`),
  `McpToolsetControl` is allowed there, and `SubAgentEvalImpl` runs the model under test through an unfiltered `McpClient`. After
  the model enables a set, hint text changes (for example the ReadFile not-found hint starts naming `ListSolutionItems`), and every
  SubAgent run after the Step 17 commit differs from earlier runs, which breaks A/B comparison across that commit.
  `EvalSurfaceCallabilityTests` covers only in-process hosts, not SubAgent children or `scripts/Invoke-ToolComparison.ps1`.
  PlanStepRunner is unaffected: it starts its server with `--include-tools` and no claude-lean mode (`Program.cs:205`).
  Options:
  (a) accept the change and record the A/B break (the Step 17 commit hash as the boundary in the eval procedure and run logs);
  (b) gate the additive rule off in SubAgent children, e.g. a server flag the launcher passes, under which `IsCallable` keeps the
  startup rule;
  (c) first add a Harness test pinning the child surface (the child's arguments built into an in-process host, a toolset enabled, the
  consumer text asserted), so the change is at least detected and recorded per run.
  **Recommendation: (b), with (c)'s pin test as its proof** (child text identical with the flag, before and after Step 17). It keeps
  SubAgent A/B series continuous and still ships the fix to main sessions; switching children over later is a deliberate, dated
  eval decision. The flag must survive `plan_json_toolset_definitions.md` Step 19, which replaces the child's `--mode=Claude-Lean`
  with `--toolset=subagent-child` (R9). Not run overnight.
- **R9. Overlap with other plans.** `plan_json_toolset_definitions.md` (DRAFT, not started): Step 22 adds constructor parameters to
  `ActiveToolSurface` (`WithLiveSnapshot` is a with-copy, so they carry over); Step 21 edits `ModelEvalMcpEnvironment.cs` (Step 15
  here; its anchors there are already stale) and **deletes `ChainEvalToolSurface.NewHiddenTools`**, which Step 5's new
  `EvalSurfaceCallabilityTests` calls, so that step must move the test to its replacement; Step 19 **replaces the SubAgent child's
  `--mode=Claude-Lean` with `--toolset=subagent-child`**, which changes how a child gets runtime toolsets and must carry any R8 (b)
  flag. `plan_stateless_http_toolset_enablement.md` (APPROVED, not started) edits
  `ServiceRegistrationExtensionsBasic.cs` and `McpToolsetControlTests.cs` (Steps 4 and 8 here edit the former in other methods). Both
  should rebase on this plan; peer checks are in Execution rules. Not STOP-worthy.
- **R10. `WriteToolAdviceHelper` is a second startup-only callability rule.** `IsExposed` = class active AND name allowed over eight
  escape-hatch tools (`WriteToolAdviceHelper.cs:147-150`), built from startup sets (`ServiceRegistrationExtensionsBasic.cs:387`); it
  also ignores runtime toolsets. Options: (a) follow-up that delegates it to `ActiveToolSurface.IsCallable`; (b) fold into stage F's
  registration rewrite. Recommendation: (a) as an INBOX item (Step 21); its text is eval-visible only where those tools differ, which
  under the additive rule is again claude-lean only.
- **R11. `CallToolFailureHandler.UnknownToolError` ignores the surface.** It tells the caller to call `McpToolsetControl` for any tool
  in `ToolsetCatalog` (`CallToolFailureHandler.cs:103-110`) and names `McpServerStatus` (`:120`, `:131`) on surfaces where both may be
  hidden (every ModelEval lean/full surface hides `McpServerStatus`, `ChainEvalToolSurface.NewFullSurfaceHiddenTools`, and none has
  `McpToolsetControl`). Found while measuring; out of scope; fixing it changes eval text (STOP-worthy). INBOX line in Step 21.
- **R12. Reflection catalog cache timing in multi-host test processes.** `ToolCallHint.Current` caches the fallback once per process
  (`ToolCallHint.cs:44-59`); its hidden map reflects `SchemaOptions` at first build, while the old registry was a growing union. A
  test that switches the Lean profile after the cache is built and expects a hidden pair through `ToolCallHint.Current` would differ;
  Step 11's gate (`SchemaLeanProfileTests`, `ToolCallHintTests`, `FullTestMarkerStoreTests`, `TestSuiteFilterTests`) runs the known
  hint tests. Production runs one configuration per process. Not STOP-worthy; a red gate here is a finding, not a test to adjust.
- **R13. Record equality.** Step 2 adds a delegate field to the `ActiveToolSurface` record, so value equality now includes it. No code
  compares `ActiveToolSurface` values (`Search` 2026-11-24). Not STOP-worthy.
