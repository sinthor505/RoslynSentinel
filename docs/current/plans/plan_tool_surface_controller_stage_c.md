# Plan: Tool-surface controller stage C - runtime toolsets go through the controller (one writer, exact attribution)

**Status:** APPROVED 2026-11-24. Stage C: runtime toolsets applied through ToolSurfaceController with exact attribution; ToolsetService becomes a facade. Review fixes applied; decisions R1-R9 taken at recommendation (owner away), R3 needs owner confirmation (non-blocking).

Stage 2 of [proposal_tool_surface_controller.md](../proposals/proposal_tool_surface_controller.md); follows phase A/B ([closed/plan_tool_surface_controller.md](closed/plan_tool_surface_controller.md), ad3b5879..9619e344: B5 a3007c77, close 9619e344), whose "Later phases" bullet C this details.

## Problem

Phase A made `ToolSurfaceController` the read side of the tool surface, but the write side is still elsewhere:

- **Two owners of runtime-toolset state.** `ToolsetService.SetEnabled` (`RoslynSentinel.Common/ToolsetService.cs:159`)
  mutates the live `McpServerOptions.ToolCollection` itself (`DeferChangedEvents` scopes at `:193` and `:214`) and keeps
  the only record of which set added which tool (`_enabled`). The controller has to ask it
  (`ToolSurfaceController.cs:106` `_toolsets?.DynamicToolNames`, `:129` `_toolsets?.EnabledToolSets`). Proposal property 2
  ("Nothing else mutates the live tool collection") does not hold.
- **The lock dance exists only because of that split.** `SetEnabled` raises `Changed` while it holds the toolset lock, the
  controller's handler reads the toolset service, so `Refresh` must gather outside its own lock
  (`ToolSurfaceController.cs:24-28`), and `SetEnabled` must update `_enabled` before its scope is disposed (comment in
  the enable branch, A2 fix). Each new reader of toolset state has to re-learn both rules.
- **Attribution is a catalog guess.** The snapshot's `ToolSet` is `ToolsetCatalog.FindSet(name)`
  (`ToolSurfaceController.cs:121`), not the set whose apply added the tool. It is right today only because the sets are
  disjoint (`McpToolsetControlTests.Catalog_SetsAreDisjoint_AndExcludeCoreTools`); JSON toolsets
  ([plan_json_toolset_definitions.md](plan_json_toolset_definitions.md)) may overlap. Phase A/B deferred this to stage C
  (its Risk "Toolset attribution").
- **A zero-tool apply leaves the snapshot stale (hypothesis).** `SetEnabled` marks a set on even when every member is
  unavailable (`_enabled[toolSet] = built.Where(...)` with an empty list), but no tool moved, so no `Changed` fires and the
  snapshot's `EnabledToolSets` and version stay old until some later refresh. That `DeferChangedEvents` raises nothing for
  an empty scope is inferred from the SDK citation in phase A/B (`McpServerPrimitiveCollection.cs:92-122`), not traced;
  Step 8's test confirms or refutes it.

## Decision

- **The controller is the single writer.** New `ToolSurfaceController.ApplyToolset(toolSet, enabled, toolNames, createTool)`
  does what `SetEnabled` does today (same checks, same order, same `DeferChangedEvents` scope, same `ToolsetChange`
  result) and stores the runtime-toolset layer itself as an immutable `IReadOnlyDictionary<ToolSetName,
  IReadOnlyList<McpServerTool>>` that is replaced wholesale on each apply, before the scope is disposed.
- **`ToolsetService` becomes the runtime-toolset layer source and a thin facade.** It keeps what only it knows (which
  classes are supported, how to discover and build a set's tools, the `--toolsets`/tab-state event
  `EnabledToolSetsChanged`, the `ToolListChangedRaised`/`ToolListSubscriptionRequests` counters) and its public surface:
  `SetEnabled` keeps its signature and result and calls `ApplyToolset`; `EnabledToolSets` and `DynamicToolNames` read the
  controller. So `McpToolsetControl` (`ToolsetControlTools.cs:43`) and the tab-state/`--toolsets` restore
  (`ServerStartupHelpers.ApplyStartupToolsets`, starting near `ServerStartupHelpers.cs:150`; its `SetEnabled` call is
  `:219`) become controller applies with no edit
  to either caller.
- **Exact attribution.** `Refresh` reads the layer: a tool is `RuntimeToolset` with `ToolSet` = the set whose apply added
  it. `ToolsetCatalog.FindSet` stays only where the question really is "which set would carry this tool"
  (`ServerStatusTools.DescribeHowToEnable`, a tool that is not exposed).
- **Locks.** `_applyGate` (new) serializes applies; `_lock` (existing) guards only the snapshot swap; the layer is a
  volatile immutable reference that `Refresh` reads without a lock. The only nesting is `_applyGate -> _lock` (an apply's
  own `Changed` handler); the controller never calls `ToolsetService`, and `SetEnabled` calls `ApplyToolset` outside its
  own `_gate`. This replaces the A2 "gather outside the lock" rule with "one lock order", and keeps the A2 ordering
  guarantee (state consistent before `Changed`).
- **Dependency direction flips.** The controller loses its `ToolsetService? toolsets` constructor parameter and
  `ToolsetService` gains the controller. The controller constructor changes in place, not migration-first: with an old
  and a new constructor side by side, `new ToolSurfaceController(options)` is CS0121, and MS.DI would pick the longer
  constructor and hit a circular dependency (controller -> `ToolsetService` -> controller). The `ToolsetService`
  constructor does go migration-first (its DI registration is a factory lambda, so two constructors are safe).
- **Concrete classes, no new DTO.** Controller and snapshot stay concrete (phase A decision). `ApplyToolset` returns the
  existing `ToolsetChange` (`ToolsetService.cs:19`, same assembly; it is exactly the payload `McpToolsetControl` returns),
  so the wire result is the same object built the same way. `ToolSurfaceTool` keeps its shape.
- **Wire stays the same** except Step 8 (status-only, zero-tool apply). Same tools added and removed, same
  `McpToolsetControl` fields and status text, one `tools/list_changed` per real change, same tab-state file, same
  `surfaceTrigger` (`toolCollectionChanged`) for runtime applies.

Decisions, 2026-11-24 (review verdict "sound after small fixes"; review fixes D1-D4 and the follow-up notes applied;
owner away, so R1-R9 were taken at the recommendation):
- R1 facade: `ToolsetService.SetEnabled` stays the entry point and calls `ApplyToolset`.
- R2 per-set apply now; a whole-layer API waits for the JSON layer source.
- R3 enum key (`ToolSetName`) and `ToolsetChange` reuse; **needs owner confirmation, non-blocking** (it reshapes
  `plan_json_toolset_definitions.md` Steps 12-13, not a stage C step).
- R4 the obsolete `ToolsetService` constructor is removed (Step 9).
- R5 Step 8 is included (status-only change).
- R6 Steps 5 and 6 land back to back. R7 lock design as stated. R8 HTTP-plan re-anchoring as listed. R9 exact
  attribution accepted.

## Execution rules

- Dispatch per the `dispatch-implementer` skill; depth tier D1 at writing (3 files, 15 edits, 35 tool calls), sweep tier
  S0 (8 files, 25 call sites, 2 projects).
- One `Build`/`RunTest` at a time; builds are serialized by the dispatcher (one `Build` at a time, paired lanes
  included); `Build` 0 errors per step; the dispatcher runs the named tests; baseline 0 failures.
- CS0618 on the old `ToolsetService` constructor is expected from Step 1 until Step 9 removes it.
- No tool description or schema text changes in this plan: `ClaudeLeanSchemaBudgetTests` must pass unchanged (run at Step 6).
- `Search` each file for `TODO` before editing it (CLAUDE.md).
- No new DTOs (Decision). Edits that add no type need no `NEW-DTO`/`DTO-EXEMPT` line.
- Peer work: the restored-start filter change and `PlainStart_SendsOneToolListChangedAfterInitialized` landed in
  b0242718 and the peer released the files; no hold is needed for `ServiceRegistrationExtensionsBasic.cs`. Before Step 3
  (`McpToolsetControlTests.cs`) and Step 5 (`ToolsetControlTools.cs`), check `Git(status)`: the HTTP plan's Step 4 also
  edits both files.
- Commits carry `Plan: plan_tool_surface_controller_stage_c`; the last one `Closes:`.
- Live checks only after commit and an own-server restart.

## Steps

Order: text-neutral Steps 1-7 and 9 first, then behaviour-changing Step 8, then LIVE, docs, closing.
Slice map: 1; 2 || 3; 4 || 5; 6; 7 || 8; 9; 10; 11; 12. Steps 4 and 5 edit in parallel, but Step 5's acceptance runs
`ToolSurfaceControllerApplyTests`, so it is run after Step 4 has landed. Step 9 may run any time after Steps 3 and 6;
the map places it after 7 || 8. Steps 5 and 6 must land in that order (between them the DI controller still reads
attribution through its `ToolsetService` fallback, which is text-neutral; see Risks).

### Step 1 - Contract skeleton
- Files: `RoslynSentinel.Common/ToolSurface/ToolSurfaceController.cs`, `RoslynSentinel.Common/ToolsetService.cs`
- Depends on: none
- Effect: text-neutral (no callers)
- Lane: implementer (one `ReplaceSnippet` `batchEdits` call per file)
- Change:
  ```csharp
  // ToolSurfaceController.cs, new members (XML doc comments on each):
  /// Applies one runtime toolset: enable adds the tools createTool builds for toolNames, disable removes exactly the
  /// instances this controller added for the set. The only writer of runtime tools to the live collection.
  public ToolsetChange ApplyToolset(ToolSetName toolSet, bool enabled, IReadOnlyList<string> toolNames,
      Func<string, McpServerTool?> createTool)
      => throw new NotImplementedException(); // TODO implement (Step 2)
  /// The runtime toolsets currently on, in enum order.
  public IReadOnlyList<ToolSetName> RuntimeToolSets => throw new NotImplementedException(); // TODO implement (Step 2)
  /// The tool names runtime toolsets currently have on the live surface, ordinal order.
  public IReadOnlyCollection<string> RuntimeToolNames => throw new NotImplementedException(); // TODO implement (Step 2)

  // ToolsetService.cs: new field and constructor; the existing constructor (ToolsetService.cs:80) chains to it.
  private readonly ToolSurfaceController _surface;
  public ToolsetService(ToolSurfaceController surface, IServiceProvider services,
      IReadOnlySet<string> supportedClassNames, Func<IEnumerable<Assembly>>? assemblySource = null)
  [Obsolete("Use the constructor that takes the ToolSurfaceController; this one builds a private controller.")]
  public ToolsetService(IOptions<McpServerOptions> serverOptions, IServiceProvider services,
      IReadOnlySet<string> supportedClassNames, Func<IEnumerable<Assembly>>? assemblySource = null)
      : this(new ToolSurfaceController(serverOptions), services, supportedClassNames, assemblySource)
  ```
  Also on the controller, implemented (not a stub): `internal McpServerOptions ServerOptions => _serverOptions.Value;`.
  The new `ToolsetService` constructor assigns `_surface`, `_services`, `_supportedClassNames`, `_assemblySource`, and
  `_serverOptions = surface.ServerOptions`: `SetEnabled` still reads `_serverOptions` until Step 5, and Step 3 can move
  test sites to the new constructor before Step 5 lands. `_surface` is unused until Step 5.
  The first parameters differ in type, so no existing call becomes ambiguous (all eight construction sites - the
  production factory at `ServiceRegistrationExtensionsBasic.cs:428`, the six test sites of Step 3 and
  `McpServerStatusToolListingTests.cs:299` - pass an options object or `Options.Create(...)` first; none passes `null`).
- Done when: `Build` 0 errors (CS0618 at the eight old-constructor sites is expected; the production factory stays a
  CS0618 site until Step 6).

### Step 2 - Implement ApplyToolset, the runtime layer and exact attribution
- Files: `RoslynSentinel.Common/ToolSurface/ToolSurfaceController.cs`, `RoslynSentinel.Common/ToolSurface/ToolSurfaceSnapshot.cs`
- Depends on: 1
- Effect: text-neutral (no production caller of `ApplyToolset` yet; with an empty layer `Refresh` falls back to today's
  attribution, so every snapshot is unchanged)
- Lane: implementer-senior (two files, the second a doc-comment edit; D1 by count, but the lock and ordering rules are
  the point of the step)
- Change:
  - Fields: `private readonly object _applyGate = new();` and
    `private volatile IReadOnlyDictionary<ToolSetName, IReadOnlyList<McpServerTool>> _runtimeLayer` (initially empty).
    Rewrite the `_lock` comment: it guards `_current/_built/_subscribed`; lock order is `_applyGate -> _lock`, never the
    reverse; `Refresh` reads `_runtimeLayer` without a lock.
  - `ApplyToolset`, ported from `ToolsetService.SetEnabled` (`ToolsetService.cs:159-258`) with these rules:
    1. Undefined `toolSet`: throw the `ArgumentOutOfRangeException` of `ToolsetService.cs:163` verbatim (its message
       reaches the wire through `McpToolsetControl`'s `UnknownToolset` error).
    2. No collection: throw the `InvalidOperationException` of `ToolsetService.cs:167` verbatim (`ToolsetsUnavailable`).
    3. Under `_applyGate`: `isOn = _runtimeLayer.ContainsKey(toolSet)`.
       Enable and not on: for each name in `toolNames` in order: present in the collection (`TryGetPrimitive`) ->
       unavailable, and `createTool` is not called for it; else `createTool(name)`, null -> unavailable; else collect.
       Build everything first (a throwing `createTool` leaves collection and layer untouched). Then one
       `using (collection.DeferChangedEvents())`: `TryAdd` each (failure -> unavailable); inside the scope, before it is
       disposed, replace `_runtimeLayer` with a copy that maps `toolSet` to the added instances.
       Disable and on: one scope: `Remove` each instance in `_runtimeLayer[toolSet]` (success -> removed); inside the
       scope, replace `_runtimeLayer` with a copy without `toolSet`.
       Comment: the layer is replaced before the scope ends because disposing it raises `Changed`, and the handler's
       `Refresh` reads the layer (A2 guarantee).
    4. Result: `new ToolsetChange(toolSet, Enabled: layer contains toolSet, Changed: enabled != isOn, added, removed,
       unavailable, EnabledToolSets: layer keys in enum order, ActiveTools: collection.PrimitiveNames ordinal-sorted)`,
       the same expressions as `ToolsetService.cs:234-242`.
    5. Comment on the parameter: `createTool` runs under `_applyGate` and must not call back into the controller.
  - `RuntimeToolSets` / `RuntimeToolNames`: from one read of `_runtimeLayer`.
  - `Refresh`: read `_runtimeLayer` once at the start of the gather; build `toolName -> set name` with `TryAdd` (not
    `ToDictionary`), so two sets that share a tool name cannot throw; the first set wins. A tool in it is
    `RuntimeToolset` with `ToolSet` = that set's `ToString()`. Otherwise keep the existing `_toolsets` branch as a
    fallback (`DynamicToolNames` + `FindSet`) with `// TODO remove with the ToolsetService parameter (Step 6)`.
    `EnabledToolSets` = layer keys (enum order, `ToString()`) when the layer is non-empty, else the existing
    `_toolsets?.EnabledToolSets` expression, same TODO.
  - Fingerprint: append each row's `ToolSet ?? ""` after its schema, so a pure attribution change moves the version
    (the fingerprint is not on the wire; `McpServerStatus` shows only version, time, trigger and patches).
  - `ToolSurfaceSnapshot.cs`: `ToolSurfaceTool` param doc for `ToolSet`: "Runtime toolset whose apply added the tool, or
    null for a startup tool." No shape change.
- Done when: `Build` 0 errors; dispatcher runs `ToolSurfaceControllerTests` and `McpServerStatusToolListingTests`: all pass.

### Step 3 - Move the test constructions to the new ToolsetService constructor
- Files: `RoslynSentinel.Tests.Server/McpToolsetControlTests.cs` (2 sites: `CreateService` :43,
  `Enable_ToolFromAnUnsupportedClass_IsReportedUnavailable` :204), `RoslynSentinel.Tests.Server/StartupToolsetRestoreTests.cs`
  (:96), `RoslynSentinel.Tests.Tools.Basic/AdminToolsTests.cs` (:394), `RoslynSentinel.Tests.Tools.Basic/McpServerStatusToolsetCountTests.cs`
  (:31), `RoslynSentinel.Tests.Tools.Basic/ToolClassRegistrationTests.cs` (:37)
- Depends on: 1
- Pairs with: Step 2 (test files only; disjoint)
- Effect: text-neutral
- Lane: implementer-sweep (one operation, 5 files, 6 sites, 2 projects: S0). Pattern, one `ReplaceSnippet` per site:
  the sites are multi-line (five of six put `new ToolsetService(` alone on its line), so wrap the first argument line:
  `options,` -> `new ToolSurfaceController(options),`, or the inline `Options.Create(...)` argument ->
  `new ToolSurfaceController(Options.Create(...))` (on `ToolClassRegistrationTests.cs:37` the whole call is one line).
  No other argument moves. `McpServerStatusToolListingTests.cs:299` is not in this sweep (Step 6 rewrites that test);
  the production factory (`ServiceRegistrationExtensionsBasic.cs:428`) moves in Step 6.
- Done when: `Build` 0 errors; `Search(mode: "text", query: "new ToolsetService\\(", contextLines: 1, includeTests: true)`
  shows a controller as the first argument everywhere except `McpServerStatusToolListingTests.cs:299` and
  `ServiceRegistrationExtensionsBasic.cs:428`; dispatcher runs the five touched test classes: all pass.

### Step 4 - ApplyToolset tests
- Files: `RoslynSentinel.Tests.Server/ToolSurfaceControllerApplyTests.cs` (new)
- Depends on: 2
- Pairs with: Step 5 (edits only; Step 5's acceptance waits for this step)
- Effect: text-neutral
- Lane: test-writer
- Change: controller over `Options.Create(new McpServerOptions { ToolCollection = collection })`; hand-made tools with
  `McpServerTool.Create((string x) => x, new McpServerToolCreateOptions { Name = name })` (pattern of
  `McpToolsetControlTests.Enable_NeverReplacesAnExistingTool_AndDisableLeavesItAlone`). Tests:
  `Enable_AddsEveryCreatedTool_AndReportsTheToolsetChangeFields`, `Enable_NameAlreadyInCollection_IsUnavailable_AndFactoryNotCalled`,
  `Enable_FactoryReturnsNull_ReportsUnavailable`, `Enable_FactoryThrows_LeavesCollectionAndLayerUntouched`,
  `Disable_RemovesExactlyTheInstancesItAdded`, `RepeatedApply_IsNoOp_AndRaisesNoChanged`,
  `Enable_RaisesChangedOnce_WithLayerAlreadyUpdatedInTheHandler` (the `Changed` handler reads `RuntimeToolSets` and
  `RuntimeToolNames` and sees the new set), `Snapshot_AttributesToolToTheSetThatAddedIt_NotTheCatalogSet`
  (`ApplyToolset(ToolSetName.git, true, ["TypedMember"], ...)` after a `Refresh`: `Find("TypedMember").ToolSet == "git"`,
  where `ToolsetCatalog.FindSet` says `declarations`), `ApplyBeforeFirstRefresh_FirstSnapshotIsVersionOne_WithRuntimeOrigin`
  (apply, then `Refresh("startup")`: version 1, trigger `startup`, origin `RuntimeToolset` - pins the startup path),
  `UndefinedToolSet_Throws_AndChangesNothing`, `NoCollection_ThrowsWithToolCollectionMessage`,
  `ConcurrentApplyAndRefresh_FinishesAndStaysConsistent` (parallel applies and `Refresh` calls inside a task awaited with a
  30 s timeout; then collection names equal `RuntimeToolNames`).
- Done when: dispatcher runs `ToolSurfaceControllerApplyTests`: all pass.

### Step 5 - ToolsetService writes through the controller
- Files: `RoslynSentinel.Common/ToolsetService.cs`, `RoslynSentinel.Tests.Server/ToolSurfaceControllerTests.cs`,
  `RoslynSentinel.Tools.Basic/ToolsetControlTools.cs` (class summary only)
- Depends on: 2 (edits); acceptance also needs Step 4 landed (it runs `ToolSurfaceControllerApplyTests`)
- Effect: text-neutral (every `SetEnabled` result, exception, event and counter is unchanged; the collection is now
  mutated by the controller)
- Lane: implementer
- Change:
  - `SetEnabled`: keep the `Enum.IsDefined` check verbatim as its first statement (it must run before
    `ToolsetCatalog.GetToolNames`, which throws `KeyNotFoundException` for an undefined value). Then call
    `_surface.ApplyToolset(toolSet, enabled, ToolsetCatalog.GetToolNames(toolSet), createTool)` outside `_gate`, where
    `createTool` looks the name up in the candidates of `ResolveCandidates` and returns
    `McpToolSchemaPatcher.CreateSentinelTool(candidate.Method, candidate.ToolType, _services)` or null. Today
    `ResolveCandidates` runs eagerly at the start of every enable of a set that is off; resolve it lazily on the first
    `createTool` call instead (once per `SetEnabled` call), which skips the reflection scan for no-op and disable calls
    and changes no result. Then, under
    `_gate`, `_toolListChangedRaised++` when added + removed > 0. Then the existing `EnabledToolSetsChanged` block
    unchanged. Remove the collection read, the `_enabled` dictionary and the mutation code.
  - `EnabledToolSets` -> `_surface.RuntimeToolSets`; `DynamicToolNames` -> `_surface.RuntimeToolNames`.
  - Remove `ToolsetService._serverOptions` and its assignment in the new constructor (the controller accessor
    `ServerOptions` goes in Step 6, which edits the controller).
  - Class summary: the service is the runtime-toolset layer source (which classes are supported, how a set's tools are
    built, persistence event, counters); `ToolSurfaceController.ApplyToolset` owns the collection mutation and the layer.
    Update the `SetEnabled`/`DeferChangedEvents` remarks accordingly.
  - `ToolSurfaceControllerTests.BuildHarness`: take the host's registered controller
    (`host.Services.GetRequiredService<ToolSurfaceController>()`) instead of `ActivatorUtilities.CreateInstance` (after
    Step 6 a second instance would be an observer that never sees the layer). Valid now: the registered controller still
    reads attribution through its `ToolsetService` fallback.
  - `ToolsetControlTools.cs` class summary (`:5-11`): "the state and the tool-collection mutation live in ToolsetService"
    -> state in `ToolSurfaceController`, built by `ToolsetService`. Comment only.
- Done when: `Build` 0 errors; dispatcher runs `McpToolsetControlTests`, `StartupToolsetRestoreTests`,
  `TabToolsetStateStoreTests`, `McpServerStatusToolsetCountTests`, `AdminToolsTests`, `ToolSurfaceControllerTests`,
  `ToolSurfaceControllerApplyTests`: all pass.

### Step 6 - One controller: DI passes it, the controller drops ToolsetService
- Files: `RoslynSentinel.Common/ToolSurface/ToolSurfaceController.cs`, `RoslynSentinel.Server.Advanced/ServiceRegistrationExtensionsBasic.cs`,
  `RoslynSentinel.Tests.Tools.Basic/McpServerStatusToolListingTests.cs`
- Depends on: 5
- Effect: text-neutral (attribution now comes from the layer only, which equals the catalog answer while sets are disjoint)
- Lane: implementer
- Change:
  - Constructor, in place: `public ToolSurfaceController(IOptions<McpServerOptions> serverOptions, IEnumerable<IToolSchemaPatch>? patches = null)`
    (today `ToolSurfaceController.cs:38`). Remove `_toolsets`, both Step 2 fallbacks and their TODOs, and the Step 1
    `ServerOptions` accessor if `Search(mode: references)` finds no use left. Comment why the
    constructor was changed in place rather than migration-first (Decision: CS0121 and the DI cycle).
    Blast radius (2026-11-24): DI `AddSingleton<ToolSurfaceController>()` (`ServiceRegistrationExtensionsBasic.cs:397`),
    three `ActivatorUtilities.CreateInstance` (`ToolSchemaPatchPipelineTests.cs:162`, `:166`; `ToolSurfaceControllerTests.cs:76`,
    gone after Step 5) and three `new` (`McpServerStatusToolListingTests.cs:305`, `:319`, `:342`); only `:305` passes a
    `ToolsetService`.
  - `ServiceRegistrationExtensionsBasic.cs:428`: the factory becomes
    `sp => new ToolsetService(sp.GetRequiredService<ToolSurfaceController>(), sp, new HashSet<string>(...unchanged...))`.
    Replace the comment at `:396` ("ToolsetService is optional in the controller's constructor ...") with: the controller
    owns the runtime-toolset layer; claude-lean's `ToolsetService` takes it, so both see one state.
  - `McpServerStatusToolListingTests.Listing_WithController_RuntimeToolsetToolIsActive_WithExposedBy`: build
    `var controller = new ToolSurfaceController(options);` first, pass it as `ToolsetService`'s first argument, delete the
    later `new ToolSurfaceController(options, toolsets)` line; the rest unchanged.
- Done when: `Build` 0 errors (also `scripts/build.ps1 -Flavor Solution`); dispatcher runs `McpToolsetControlTests` (its
  live tests pin one `tools/list_changed` per toggle, e.g. `Lean_AdvertisesListChanged_AndEnableThenDisableEachSendOneNotification`),
  `ToolSurfaceControllerTests`, `ToolSurfaceControllerApplyTests`, `McpServerStatusToolListingTests`, `ToolSchemaPatchPipelineTests`,
  `ToolClassRegistrationTests`, `NoActiveToolsStartupTests`, `ClaudeLeanSchemaBudgetTests`: all pass.

### Step 7 - Wiring tests
- Files: `RoslynSentinel.Tests.Server/ToolSurfaceControllerWiringTests.cs` (new)
- Depends on: 6
- Pairs with: Step 8
- Effect: text-neutral
- Lane: test-writer
- Change: claude-lean host built like `ToolSurfaceControllerTests.BuildHarness` (never started). Tests:
  `LeanHost_ToolsetServiceWritesThroughTheRegisteredController` (`SetEnabled(git, true)` on the registered service: the
  registered controller's `RuntimeToolSets` contains `git`; after `Refresh`, `GitRemote` is `RuntimeToolset`, `ToolSet` `git`),
  `LeanHost_ToolsetServiceReadsMirrorTheController` (`EnabledToolSets`/`DynamicToolNames` equal
  `RuntimeToolSets`/`RuntimeToolNames`), `StartupToolsets_AppliedBeforeStart_AreAttributedInTheFirstSnapshot`
  (`ServerStartupHelpers.ApplyStartupToolsets(host.Services, new[] { "--toolsets=git" })`, then `Refresh("startup")`:
  version 1, `GitRemote` attributed to `git`), `OtherMode_ControllerResolvesWithoutToolsetService` (`--mode claude`: the
  controller resolves, `ToolsetService` is not registered; guards against reintroducing the cycle).
- Done when: dispatcher runs `ToolSurfaceControllerWiringTests`: all pass.

### Step 8 - A real apply always refreshes a live snapshot
- Files: `RoslynSentinel.Common/ToolSurface/ToolSurfaceController.cs`, `RoslynSentinel.Tests.Server/ToolSurfaceControllerApplyTests.cs`
- Depends on: 2 (and 4, same test file)
- Effect: behaviour-changing, status only: after an apply that changed the layer but moved no tool (every member
  unavailable), `McpServerStatus.toolSurface.surfaceVersion` moves and `surfaceTrigger` is `toolsetApplied`. No tool,
  schema, description or `McpToolsetControl` text changes; a normal apply is unaffected (its `Changed` refresh already
  built the snapshot, so this refresh finds no difference).
- Lane: implementer
- Change: at the end of `ApplyToolset`, after releasing `_applyGate`: `if (result.Changed && _built) Refresh("toolsetApplied");`.
  The `_built` guard keeps the startup path unchanged (applies before the hosted service's `Refresh("startup")` build no
  snapshot). Test `ZeroToolEnable_BumpsVersion_WithToolsetAppliedTrigger` (factory returns null for every name; before:
  `Refresh("test")`; after: version + 1, trigger `toolsetApplied`, `EnabledToolSets` contains the set). If the test shows
  the version already moved without the change (the Problem hypothesis is false), drop the code change, keep the test
  as a pin and record that here.
- Done when: `Build` 0 errors; dispatcher runs `ToolSurfaceControllerApplyTests` (including
  `ApplyBeforeFirstRefresh_FirstSnapshotIsVersionOne_WithRuntimeOrigin`) and `ToolSurfaceControllerTests`
  (`ToolsetEnable_BumpsVersion_AndMarksRuntimeOrigin` still sees `toolCollectionChanged`): all pass.

### Step 9 - Remove the obsolete ToolsetService constructor
- Files: `RoslynSentinel.Common/ToolsetService.cs`
- Depends on: 3, 6
- Effect: text-neutral
- Lane: implementer
- Change: `Search(mode: "text", query: "new ToolsetService\\(", includeTests: true)`: every site passes a controller first;
  then delete the `[Obsolete]` constructor.
- Done when: `Build` 0 errors with no CS0618 for `ToolsetService`.

### Step 10 - LIVE check
- Files: none
- Depends on: 6, 8, 9 committed; own-server restart (`McpServerControl(StopServer)`, then `LoadSolution`); no subagent mid-operation
- Effect: none
- Change and outcome classes (record each with the date and `lastLaunch.servedSha` here):
  - **A.** `McpServerStatus(toolListing: all, toolNameFilter: "GitRemote")` after the restart: `surfaceVersion` 1, trigger
    `startup`; `GitRemote` inactive with the `McpToolsetControl` hint, or `exposedBy: toolset:git` if the tab restored git
    (`ok-fresh` | `ok-restored` | `wrong-version` | `wrong-origin`). If `ok-restored`, B would only report "already on":
    disable git first (record that call's version and count, then run B-E as written), or run B-E with `testCategories`
    and `TagTestCategories` in place of git and `GitRemote`.
  - **B.** `McpToolsetControl(git, true)`: status text equals the `ToolsetControlTools` source strings ("Toolset git is now
    on: 1 tool(s) added, 0 removed." plus the unchanged trailing sentence) (`same-text` | `text-changed`); then status:
    version + 1, trigger `toolCollectionChanged`, `GitRemote` `exposedBy: toolset:git`, `toolListChangedRaised` + 1
    (`ok` | `stale-version` | `wrong-origin` | `count-off`); ToolSearch shows `GitRemote` (`visible` | `not-visible`;
    client side, informational).
  - **C.** Repeat B's call: "already on; nothing changed", version and `toolListChangedRaised` unchanged (`ok` | `bumped`).
  - **D.** Restart again (StopServer keeps toolsets by default, `AdminTools.cs:561`): version 1, trigger `startup`,
    `GitRemote` `exposedBy: toolset:git` (restore went through the controller) (`ok` | `not-restored` | `wrong-origin`).
  - **E.** `McpToolsetControl(git, false)`: version + 1, `GitRemote` inactive with the hint, `toolListChangedRaised` + 1
    (`ok` | `stale` | `count-off`). E restores the starting state: if A was `ok-restored` and git was disabled before B,
    re-enable git after E so the tab state matches what A found.
- Done when: A-E recorded.

### Step 11 - Docs
- Files: `docs/current/references/reference_architecture_map.md`
- Depends on: 6, 8
- Effect: none (docs)
- Lane: main session
- Change: "Tool surface" section (`:80-96`): the controller is the only runtime writer (`ApplyToolset`, runtime layer,
  exact attribution, lock order `_applyGate -> _lock`); `ToolsetService` is the layer source and facade; refresh triggers
  gain `toolsetApplied`. The claude-lean paragraph (`:71-76`) names `ToolsetService` as the mutator: point it at the
  controller. The refresh list keeps "on every `ToolCollection.Changed`" and adds "after a real apply (`toolsetApplied`)"
  if Step 8 kept its code change.
- Done when: the section matches the code; links resolve.

### Step 12 - No stubs left, full run
- Files: none (check only)
- Depends on: all above
- Effect: none
- Lane: dispatcher
- Change: `Search(mode: "text", query: "NotImplementedException|TODO implement \\(Step 2\\)|TODO remove with the ToolsetService parameter", fileGlob: "RoslynSentinel.Common/{ToolsetService.cs,ToolSurface/*.cs}")`.
- Done when: zero matches; full `RunTest` with 0 failures.

## Out of scope

- Delivery: who sends `tools/list_changed` and the counters (`ToolListChangedRaised`, `ToolListSubscriptionRequests`)
  stay with `ToolsetService`/`ToolsetControlTools`; the proposal's "Deliver" stage takes them later, after
  `plan_stateless_http_toolset_enablement.md` Step 4.
- A whole-layer apply API (all enabled sets in one call, one `Changed` for a multi-set restore). See Risks.
- `ActiveToolSurface.IsCallable` reading the snapshot (phase E), `ConsoleMode.WriteStartupDump` reading it (still a
  candidate), startup population by the controller (phase F, including the empty-collection risk carried from phase A/B).
- Richer `surfaceTrigger` text for runtime applies (for example `toolset:git:on`).
- String-keyed groups (JSON toolsets).
- `McpServerStatus` code: it already reads `exposedBy` from the snapshot; `DescribeHowToEnable` keeps `FindSet` on purpose.
  Its legacy branch (`ServerStatusTools.cs:88`, `_toolsets?.DynamicToolNames`, used before the first snapshot) keeps
  working through the facade, which reads the controller after Step 5.
- `AddRestoredToolsetListChangedMessageFilter`: it sends a notification and never touches the collection.

## Risks and open decisions

- **R1. Facade or move the callers (recommend: facade).** Options: (a) `ToolsetService.SetEnabled` stays the entry point
  and calls the controller; (b) `ToolsetControlTools` and `ApplyStartupToolsets` call `ApplyToolset` directly and
  `ToolsetService` shrinks to a tool factory. (a) edits neither caller, keeps every test's `SetEnabled` call (37 call
  sites, 2 in production), and leaves `ToolsetControlTools.cs` free for the HTTP plan's Step 4. It matches the proposal's
  consumer row ("`ToolsetService` - the runtime-toolset layer's source"). Not STOP-worthy.
- **R2. Per-set apply versus whole layer (recommend: per-set now).** The proposal's decision 1 prefers whole layers. A
  whole-layer API would turn a multi-set restore into one apply, so `toolListChangedRaised` after a restore would read 1
  instead of N, and `ToolsetChange` (one set per result) would not fit. The stored layer is already replaced wholesale;
  only the API is per-set. Revisit when the JSON layer source arrives. Not STOP-worthy (the brief's suggested shape).
- **R3. Enum key versus string key - touches another plan: needs owner confirmation, non-blocking.** Stage C keys the
  layer by `ToolSetName` and reuses `ToolsetChange` (enum fields). `plan_json_toolset_definitions.md` (DRAFT, no code)
  Step 12 adds a `ToolsetService` constructor with groups and `SetEnabled(string, bool)` over state kept in
  `ToolsetService`, and Step 13 rewrites the `ToolsetService` registration block that Step 6 edits. After stage C there
  is no state in `ToolsetService` to key by string, so Step 12 must be reshaped onto `ApplyToolset` (a string overload
  there) and the controller-taking constructor, and Step 13's registration must pass the registered controller. Recommendation: proceed with the enum (status quo, nothing to undo) and reshape the
  JSON plan under the controller, as phase A/B already anticipated. No stage C step depends on the owner's answer; what
  needs confirming is the JSON plan's reshape.
- **R4. Old `ToolsetService` constructor: remove (recommend) or keep.** Keeping it as a test convenience is cheap, but a
  production registration that used it would silently create a second controller whose layer the registered one never
  sees (runtime tools labelled `Startup`). Step 9 removes it. Not STOP-worthy.
- **R5. Step 8 changes what `McpServerStatus` shows (recommend: include).** Status only, only for an apply that moves no
  tool; no tool list, schema or description text changes, so eval models see no different tool surface. It can be
  dropped without touching any other step. Not STOP-worthy.
- **R6. Window between Steps 5 and 6.** After Step 5 the registered `ToolsetService` still uses the obsolete constructor
  (a private controller writes) and the registered controller reads attribution through its `ToolsetService` fallback.
  That is text-neutral, but do not run the LIVE check, restart for a release, or stop the run between the two commits.
- **R7. Lock design.** The only nesting is `_applyGate -> _lock`. `createTool` runs under `_applyGate`, so a slow
  factory (reflection scan plus `CreateSentinelTool`) blocks every other apply until it returns; this matches today,
  where the same work runs under `ToolsetService._gate`. Hazard left: a factory that called `ApplyToolset` would re-enter
  the monitor and nest an apply inside an apply (no deadlock, corrupt layer). Documented on the parameter; `ToolsetService`'s factory does not. Step 4's concurrency test pins the rest.
  The SDK side is unchanged from phase A/B (verified there at v2.4.0: `Changed` is raised outside the SDK's own locks).
- **R8. Re-anchoring `plan_stateless_http_toolset_enablement.md` (APPROVED, not started) after stage C.** Nothing breaks,
  but these references go stale:
  - Decision 1 and Step 2's doc comment say the shared collection is "the object `ToolsetService` mutates": now the
    controller mutates it (same object; the code of Step 2 is unaffected).
  - Step 4: `ToolsetControlTools` still takes `ToolsetService`, `ToolListSubscriptionRequests` and the `ToolsetChange`
    added/removed counts still exist, so the step holds; its line numbers in `ToolsetControlTools.cs` shift by the Step 5
    comment edit, and its `McpToolsetControlTests` anchors (`:557`, `:330`, `:406`, `:610`) shift by Step 3. Re-anchor by
    member name. The proposal's "Deliver" stage is the later home for Step 4's logic, which then reads the controller.
  - Step 6 (restored-start filter): has effectively landed (b0242718: the filter condition at
    `ServiceRegistrationExtensionsBasic.cs:139` and `PlainStart_SendsOneToolListChangedAfterInitialized`). Stage C does
    not touch the filter.
  - Steps 3 and 8 (HTTP tests, LIVE): the controller is shared by every session through the one collection, so they
    hold as written.
- **R9. Exact attribution equals today's output only while sets are disjoint.** True today
  (`Catalog_SetsAreDisjoint_AndExcludeCoreTools`). If a later set overlaps, `exposedBy` names the set that added the
  tool, which is the intended behaviour.
