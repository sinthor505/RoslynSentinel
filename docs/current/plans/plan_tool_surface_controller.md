# Plan: Tool-surface controller - phase A read-only snapshot, phase B pluggable schema patches

**Status:** IN PROGRESS 2026-11-23. Steps A1-A7, B1-B5 done (ad3b5879, 3ce6f373, f7f9c00f, c3974c36, 1e57e2da, 5a95aab7, 33e9e00f, 61be309e, f4c3e4e4, c9602226, 07e8d5ca); B6 full run pending. Approved 2026-11-23: Phase A read-only snapshot and phase B patch seam, with the 2026-11-23 review fixes (stale toolset state, hosted-service startup refresh, instance registration of default patches). Implements stage 1 of [proposal_tool_surface_controller.md](../proposals/proposal_tool_surface_controller.md) (phase A) plus the schema-patch seam (phase B); later stages are outlined at the end and get detailed steps here before they start.

## Problem

See the proposal. In short: nothing in the server can state what it actually exposes. `McpServerStatus`
derives "active" from reflection over declared tools plus `ActiveToolSurface` plus
`ToolsetService.DynamicToolNames` (`ServerStatusTools.cs:76-91`); it never reads the live tool collection, so a
tool that is allowed and in an active class but was never registered (a missing DI block, the "silent dead tool"
in the architecture map) is reported active. Schema patches are four hard-wired private static calls
(`McpToolSchemaPatcher.cs:211-214`), so an eval or A/B variant of a patch means editing production code.

## Decision

- **Phase A (stage 1, read-only):** a `ToolSurfaceController` DI singleton builds an immutable, versioned
  `ToolSurfaceSnapshot` from the live tool collection (the authority, as the comment at
  `ServiceRegistrationExtensionsBasic.cs:1141-1143` already says), with each tool's class, origin (startup or a
  runtime toolset), listed parameters and hidden parameters. It refreshes once at startup and on every
  `ToolCollection.Changed`. `McpServerStatus` reads it. Nothing else changes behaviour.
- **Phase B (patch seam):** the four `Apply*` calls become `IToolSchemaPatch` implementations resolved from DI as an
  ordered collection, with the current four as defaults. A host adds a patch by registering it, and replaces or
  removes one with `RemoveAll`/`Add` on its own service collection, without touching production code. The snapshot
  records the ordered patch names, so every eval result can name the pipeline that built its schemas.
- **Kept concrete (not interfaces):** the controller and the snapshot. They are the single source of truth; making
  them swappable reintroduces "which implementation is live?". Variation belongs in layers (data, later stages) and
  in patches (behaviour).
- **Deviation from proposal stage 1, deliberately:** `ActiveToolSurface` does not become a snapshot view in phase A.
  Its `IsCallable` is captured by 13 optional constructor parameters, an engine closure
  (`EngineServiceCollectionExtensionsBasic.cs:60`) and `EditImpactGate`, and switching it to the live surface makes
  runtime-toolset tools callable there - a behaviour change that gets its own step in a later phase.
- **No new status DTO:** `McpServerStatusToolSurface` (`ServerStatusTools.cs:244`) and
  `McpServerStatusDeclaredTool` (`:321`) gain optional trailing fields (reuse-first).

Owner decisions, 2026-11-23 (plan review):
- **Stale toolset state in `Changed` (review defect 1): reorder and gather outside the lock.** `ToolsetService.SetEnabled`
  raises `Changed` when its `DeferChangedEvents` scope ends, before it updates `_enabled`, so a snapshot built in the
  handler would label a just-enabled tool `Startup` and keep a just-disabled set listed. Fix in A2: set `_enabled` inside
  the scope, before it is disposed. `Refresh` reads the collection and toolset state before taking its own lock and only
  compares and swaps `Current` under it, so the toolset lock and the controller lock are never taken in opposite orders.
- **First snapshot (review defect 2): hosted service plus lazy guard.** Test hosts (`McpToolsetControlTests.LiveServer`)
  share only the DI registration and `host.RunAsync()` with `ServerStdio.Run`/`ServerHttp`, so an explicit call in those
  files never runs in tests. A4 registers a `ToolSurfaceStartup : IHostedService` beside the controller whose `StartAsync`
  calls `Refresh("startup")` (it runs after `ApplyStartupToolsets` in both paths). `Current` also builds once on first read
  while it is still `Empty` (trigger `"firstRead"`), for hosts that never start. `McpServerStatus` keeps today's rule
  while `Current.Version == 0` (no collection yet).
- **A5 description clause: raise the claude-lean budget** in `ClaudeLeanSchemaBudgetTests` by what the clause costs,
  with the reason in the commit message.
- **Order versus `plan_stateless_http_toolset_enablement.md`: controller first.** Temporary HTTP inaccuracy (runtime
  toolset tools reported active over stateless HTTP) is accepted until that plan's Step 2 lands.
- **Default patches stay in `Common`** (owner asked whether they could move to `Server.Advanced`: no, `Common` itself
  calls `CreateSentinelTool` from `ToolsetService.SetEnabled` and `ToolSignatureCatalogReflection`, which need the
  defaults with no `Server.Advanced` in sight). The four classes stay `internal`; `Server.Advanced` registers the
  instances from `DefaultToolSchemaPatches.All` (review defect 3: `ServiceDescriptor.Singleton<IToolSchemaPatch, X>()`
  with an internal `X` is CS0122 there).
- **Taken at the recommendation (owner away for the run):** `ActiveToolSurface.DeclaringClassByTool` becomes
  `internal static` and A2 reuses it; "remove every patch" is not expressible (an empty registration falls back to the
  defaults) and B5 documents it.
- **Run:** Batched mode; owner away (mid-run decisions deferred to the tracker).

## Execution rules

- One `Build`/`RunTest` at a time; `Build` 0 errors per step; the dispatcher runs the named tests; baseline 0 failures.
- Dispatch per the `dispatch-implementer` skill (depth tier D1 at writing: 3 files, 15 edits, 35 tool calls).
- `McpServerStatus` is a claude-lean tool: `ClaudeLeanSchemaBudgetTests` (48400) had 13 chars of headroom after
  a86b5afd. Step A5's description clause must be paid for elsewhere in that tool's description, or the budget raised
  with the reason in the commit message.
- New DTOs carry their NEW-DTO block (below, per step) in the creating call's `reason`; services carry `DTO-EXEMPT`.
- Live checks only after commit and a server restart.

## Steps

Order: text-neutral first (A1-A4, B1-B3), then the behaviour-changing A5. Phase B's B1-B2 do not depend on phase A
and can run beside it, except that B2 and A4 both edit `ServiceRegistrationExtensionsBasic.cs` (A4 after B2).
Slice map (review 2026-11-23): A1 || B1; A2 || B2; A3 || A4 (after B2 commits); B3 || A5; B4 || A6; then A7, B5, B6.

### Phase A - read-only snapshot

### Step A1 - Contract skeleton
- Done: ad3b5879
- Files: `RoslynSentinel.Common/ToolSurface/ToolSurfaceSnapshot.cs` (new), `RoslynSentinel.Common/ToolSurface/ToolSurfaceController.cs` (new), `RoslynSentinel.Common/HiddenSchemaParams.cs`
- Depends on: none
- Effect: text-neutral (no callers)
- Lane: implementer (two `CreateFile` calls, snapshot file first; one `Member` add on `HiddenSchemaParams`)
- Change: namespace `RoslynSentinel.Common`.
  ```csharp
  // ToolSurfaceSnapshot.cs
  public enum ToolSurfaceOrigin { Startup, RuntimeToolset }
  public sealed record ToolSurfaceTool(string Name, string ClassName, ToolSurfaceOrigin Origin, string? ToolSet,
      IReadOnlyList<string> ListedParameters, IReadOnlyList<string> HiddenParameters);
  // Name keeps the repo's *Snapshot convention (ToolTelemetrySnapshot, GitBackendSnapshot) rather than *Result.
  public sealed record ToolSurfaceSnapshot(long Version, DateTime BuiltUtc, string Trigger, string Fingerprint,
      IReadOnlyList<ToolSurfaceTool> Tools, IReadOnlyList<string> EnabledToolSets, IReadOnlyList<string> SchemaPatches)
  {
      public static ToolSurfaceSnapshot Empty { get; } = new(0, DateTime.MinValue, "none", "", [], [], []);
      public bool IsExposed(string toolName) => throw new NotImplementedException(); // TODO implement (Step A2)
      public ToolSurfaceTool? Find(string toolName) => throw new NotImplementedException(); // TODO implement (Step A2)
  }
  // ToolSurfaceController.cs
  public sealed class ToolSurfaceController
  {
      public ToolSurfaceController(IOptions<McpServerOptions> serverOptions, ToolsetService? toolsets = null)
          => throw new NotImplementedException(); // TODO implement (Step A2)
      public ToolSurfaceSnapshot Current { get; }
      public ToolSurfaceSnapshot Refresh(string trigger) => throw new NotImplementedException(); // TODO implement (Step A2)
  }
  // HiddenSchemaParams.cs (existing static class), new member:
  public static IReadOnlyList<string> HiddenFor(string toolName) => throw new NotImplementedException(); // TODO implement (Step A2)
  ```
  NEW-DTO blocks:
  ```
  NEW-DTO: ToolSurfaceTool (Item, Common.ToolSurface)
  Evaluated: McpServerStatusDeclaredTool - Tools.Basic wire row, invisible to Common, no origin/parameters;
             ActiveToolSurface - mode-level config, no per-tool rows
  Wire impact: none
  NEW-DTO: ToolSurfaceSnapshot (Result, Common.ToolSurface)
  Evaluated: ActiveToolSurface - startup configuration, not what is exposed; no version; captured by reference;
             McpServerStatusToolSurface - Tools.Basic wire view of ActiveToolSurface
  Wire impact: none (status reads it in A5 through existing DTOs)
  ```
  `ToolSurfaceController`: `DTO-EXEMPT: service (builds and holds the snapshot)`.
- Done when: `Build` 0 errors.

### Step A2 - Implement the snapshot build
- Done: f7f9c00f (A2a), 1e57e2da (A2b)
- Files: the three A1 files, `RoslynSentinel.Common/ToolsetService.cs`, `RoslynSentinel.Common/ActiveToolSurface.cs`
- Depends on: A1
- Effect: text-neutral (no callers; the `ToolsetService` reorder changes no observable result)
- Lane: implementer (D1 by count, but the `Refresh` logic is dense: brief it in full or use `implementer-senior`)
- Change:
  - `ToolsetService.SetEnabled`: move each `_enabled[...]` update (enable: `_enabled[toolSet] = ...`; disable:
    `_enabled.Remove(toolSet)`) inside its `using (collection.DeferChangedEvents())` block, after the add/remove loop, so
    `Changed` (raised when the scope is disposed) sees the new state. Comment why (the controller's `Changed` handler).
  - `ActiveToolSurface.DeclaringClassByTool`: `private static` -> `internal static` (same assembly), reused below.
  - `Refresh(trigger)`: gather first, outside any controller lock: read `serverOptions.Value.ToolCollection` (null = no
    tools: return `Current` unchanged); for each tool build a `ToolSurfaceTool`: `ClassName` from
    `ActiveToolSurface.DeclaringClassByTool` (empty string when the tool has no declaring class there, e.g. a test
    fixture tool); `Origin`/`ToolSet` = `RuntimeToolset` and `ToolsetCatalog.FindSet(name)` (as a string) when
    `toolsets?.DynamicToolNames` contains the name, else `Startup`;
    `ListedParameters` = top-level `properties` names of `tool.ProtocolTool.InputSchema`; `HiddenParameters` =
    `HiddenSchemaParams.HiddenFor(name)`. Sort tools by name. `EnabledToolSets` = `toolsets?.EnabledToolSets` mapped with
    `ToString()` (they are `ToolSetName` values). Then, under a private lock, compare with `Current` and swap. Comment why
    the gather runs outside the lock (`SetEnabled` raises `Changed` while holding the toolset lock).
    `SchemaPatches` = the fixed list `["ConsumesTags", "BatchItemFields", "ReplaceSnippetLimits", "HiddenParameters"]`
    with `// TODO read from the IToolSchemaPatch pipeline (Step B3)`.
  - `Current`: if no `Refresh` has run yet, the getter runs `Refresh("firstRead")` once (lazy guard for hosts that never
    start; the hosted service from A4 is the normal path).
  - `Fingerprint`: SHA-256 hex over each tool's name and raw `InputSchema` text in name order. `Version` increments
    only when the fingerprint or enabled sets differ from `Current`; otherwise `Refresh` returns `Current` unchanged.
  - On the first `Refresh` that finds a non-null collection, subscribe to its `Changed` event:
    `(_, _) => Refresh("toolCollectionChanged")`. Swallow and log nothing from the handler's exceptions
    (comment why: the SDK raises `Changed` on the mutating thread, `McpServerPrimitiveCollection.cs:92-122`).
  - `HiddenFor`: copy of the registered set for the tool (case-insensitive tool key), sorted; empty if none. The
    registry is `ConcurrentDictionary<string, HashSet<string>>` (`HiddenSchemaParams.cs:14`): copy the set under
    `lock (set)` the way the existing readers do (check them first).
  - `IsExposed`/`Find`: ordinal lookup on `Tools`.
- Done when: `Build` 0 errors.

### Step A3 - Controller tests
- Done: 5a95aab7
- Files: `RoslynSentinel.Tests.Server/ToolSurfaceControllerTests.cs` (new)
- Depends on: A2
- Pairs with: B1
- Effect: text-neutral
- Lane: test-writer
- Change: build hosts the way `ClaudeLeanSchemaBudgetTests.Measure` does (`ClaudeLeanSchemaBudgetTests.cs:73-87`,
  reads `IOptions<McpServerOptions>` from `host.Services`); construct the controller with
  `ActivatorUtilities.CreateInstance<ToolSurfaceController>(host.Services)` (A4's DI registration is not a prerequisite).
  Tests: `Snapshot_ListsExactlyTheLiveCollection` (names equal the collection's), `Snapshot_ClassNameMatchesDeclaringClass`,
  `ToolsetEnable_BumpsVersion_AndMarksRuntimeOrigin` (claude-lean; `ToolsetService.SetEnabled(ToolSetName.declarations, true)`
  from the host's services, with no explicit `Refresh` afterwards, so the `Changed` path is what updates it; new version;
  `TypedMember` has `Origin == RuntimeToolset`, `ToolSet == "declarations"`; `EnabledToolSets` contains `"declarations"`),
  `ToolsetDisable_DropsSetAndTools`, `RefreshWithoutChange_KeepsVersion`, `FirstRead_BuildsWithoutRefresh`,
  `HiddenParameters_ReportedPerTool`
  (`--hide-params=validateOnApply`; mark `[NonParallelizable]` and reset statics in TearDown as
  `SchemaHiddenParametersTests.cs:43-46` does).
- Done when: dispatcher runs `ToolSurfaceControllerTests`: all pass.

### Step A4 - Register and refresh at startup
- Done: 33e9e00f
- Files: `RoslynSentinel.Server.Advanced/ServiceRegistrationExtensionsBasic.cs`, `RoslynSentinel.Server.Advanced/ToolSurfaceStartup.cs` (new)
- Depends on: A2, B2 (same registration file)
- Effect: text-neutral
- Lane: implementer
- Change: `services.AddSingleton<ToolSurfaceController>();` and `services.AddHostedService<ToolSurfaceStartup>();` right
  after `services.AddSingleton(activeToolSurface);` (`ServiceRegistrationExtensionsBasic.cs:386`; `ToolsetService` is
  optional in the constructor, so modes without it resolve). New `internal sealed class ToolSurfaceStartup(ToolSurfaceController surface) : IHostedService`
  whose `StartAsync` calls `surface.Refresh("startup")` and whose `StopAsync` does nothing (`DTO-EXEMPT: service`).
  Doc comment: hosted services start in `host.RunAsync()`, which production (`ServerStdio.Run`, `ServerHttp`) and the
  test hosts (`McpToolsetControlTests.LiveServer`) share, after `ApplyStartupToolsets` in both, so this is the first
  point where startup tools and startup toolsets are both in place. `ServerStdio.cs`/`ServerHttp.cs` are not edited.
- Done when: `Build` 0 errors; dispatcher runs `McpToolsetControlTests` and `NoActiveToolsStartupTests`: all pass.

### Step A5 - McpServerStatus reads the snapshot
- Done: f4c3e4e4 (claude-lean budget 48400 -> 48500)
- Files: `RoslynSentinel.Tools.Basic/ServerStatusTools.cs`, `RoslynSentinel.Tests.Server/ClaudeLeanSchemaBudgetTests.cs`
- Depends on: A4
- Effect: behaviour-changing (wire). `activeForThisMode` now means "in the live collection" when a controller is
  present; a declared tool that is allowed and in an active class but not registered is reported inactive with an
  `enabledBy` naming the missing registration; `toolSurface` gains `surfaceVersion`, `surfaceBuiltUtc`,
  `surfaceTrigger`, `schemaPatches`; declared tools gain `exposedBy` and `hiddenParameters` (both null unless set)
- Lane: implementer
- Change:
  - Constructor (`:25-34`) gains a trailing `ToolSurfaceController? surface = null`.
  - `IsActive` (`:80-83`): when `surface` is present and `surface.Current.Version > 0`, `surface.Current.IsExposed(toolName)`;
    else today's expression.
  - `DescribeHowToEnable` (`:195-224`): new first case when the class is active, the tool is allowed, and it is not
    exposed: "declared and in an active class, but not registered with the server (ToolClassRegistry entry or DI block
    missing)". Facts only (`reference_tool_error_text_facts_options_policy.md`).
  - `McpServerStatusToolSurface` (`:244`): optional trailing `long? SurfaceVersion = null, DateTime? SurfaceBuiltUtc = null,
    string? SurfaceTrigger = null, IReadOnlyList<string>? SchemaPatches = null`; fill at `:137-144`.
  - `McpServerStatusDeclaredTool` (`:321`): optional trailing `string? ExposedBy = null` ("startup" or
    "toolset:<name>") and `IReadOnlyList<string>? HiddenParameters = null` (null when empty); fill at `:88-91`.
  - Tool `[Description]`: one short clause for `surfaceVersion`. Raise the claude-lean total budget constant in
    `ClaudeLeanSchemaBudgetTests` by the measured overrun rounded up to the next 50 (owner decision 2026-11-23); the
    commit message states the old and new budget and why.
- Done when: `Build` 0 errors; dispatcher runs `McpServerStatusToolListingTests`, `McpServerStatusToolsetCountTests`,
  `McpToolsetControlTests.McpServerStatus_PointsInactiveToolsAtTheToolset_AndReportsEnabledOnesActive`,
  `ClaudeLeanSchemaBudgetTests`: all pass.

### Step A6 - Status tests
- Done: 07e8d5ca
- Files: `RoslynSentinel.Tests.Tools.Basic/McpServerStatusToolListingTests.cs`
- Depends on: A5
- Effect: text-neutral
- Lane: test-writer
- Change: construct `ToolSurfaceController` over `Options.Create(new McpServerOptions { ToolCollection = ... })` with a
  hand-built collection, then call `Refresh("test")` explicitly. `ToolsetService` is sealed: for the runtime-toolset case
  build a real one over the same options; if its constructor needs more than this test project can supply, move that one
  test to A3's `ToolSurfaceControllerTests` host pattern and say so in the report. Tests: `Listing_WithController_RuntimeToolsetToolIsActive_WithExposedBy` (closes the gap that
  only a text-level live test covers the `DynamicToolNames` branch today),
  `Listing_WithController_AllowedButUnregisteredTool_ReportsMissingRegistration`,
  `Listing_WithController_ReportsSurfaceVersionAndTrigger`, `Listing_WithoutController_KeepsLegacyAnswer`.
- Done when: dispatcher runs `McpServerStatusToolListingTests`: all pass.

### Step A7 - LIVE check
- Done: 2026-11-24 on servedSha 2a5681b9: A ok (v1, startup), B ok (v2, toolCollectionChanged, GitRemote exposedBy toolset:git), C ok (v3, GitRemote inactive with the toolset hint)
- Files: none
- Depends on: A4, A5 committed; restart
- Effect: none
- Change and outcome classes (record with the date here):
  - **A.** `McpServerStatus(toolListing: all)` after start: `surfaceVersion` 1, trigger `startup`
    (`ok` | `missing` | `wrong-trigger`).
  - **B.** `McpToolsetControl(git, true)`, then status: version 2, trigger `toolCollectionChanged`, `GitRemote`
    `exposedBy: toolset:git` (`ok` | `stale-version` | `no-origin`).
  - **C.** Disable it again: version 3, `GitRemote` inactive with the toolset hint (`ok` | `stale`).
- Done when: A-C recorded.

### Phase B - pluggable schema patches

### Step B1 - Patch contract and default patches
- Done: 3ce6f373
- Files: `RoslynSentinel.Common/ToolSurface/IToolSchemaPatch.cs` (new), `RoslynSentinel.Common/ToolSurface/DefaultToolSchemaPatches.cs` (new), `RoslynSentinel.Common/McpToolSchemaPatcher.cs`
- Depends on: none
- Effect: text-neutral (no callers)
- Lane: implementer
- Change:
  ```csharp
  public interface IToolSchemaPatch
  {
      string Name { get; }  // recorded in ToolSurfaceSnapshot.SchemaPatches
      int Order { get; }    // ascending; defaults use 100, 200, 300, 400
      void Apply(McpServerTool tool, MethodInfo toolMethod);
  }
  ```
  Four `internal sealed` classes in `DefaultToolSchemaPatches.cs` (`ConsumesTagsPatch` 100, `BatchItemFieldsPatch` 200,
  `ReplaceSnippetLimitsPatch` 300, `HiddenParametersPatch` 400), each delegating to the existing method, plus
  `public static class DefaultToolSchemaPatches { public static IReadOnlyList<IToolSchemaPatch> All { get; } }` in that
  order (the classes stay `internal`: hosts reach the defaults only through `All`). In `McpToolSchemaPatcher.cs` change `ApplyConsumesTags` (`:406`), `ApplyBatchItemFields` (`:290`),
  `ApplyReplaceSnippetLimits` (`:475`) and `ApplyHiddenParameters` (`:625`) from `private static` to `internal static`;
  no logic moves. `IToolSchemaPatch` and the defaults: `DTO-EXEMPT: behaviour seam, not data`.
- Done when: `Build` 0 errors.

### Step B2 - CreateSentinelTool runs the pipeline
- Done: c3974c36
- Files: `RoslynSentinel.Common/McpToolSchemaPatcher.cs`, `RoslynSentinel.Server.Advanced/ServiceRegistrationExtensionsBasic.cs`
- Depends on: B1
- Effect: text-neutral (same four patches, same order, so every schema is byte-identical)
- Lane: implementer
- Change: in the `:182` overload replace the four calls (`:211-214`) with: resolve
  `services?.GetService<IEnumerable<IToolSchemaPatch>>()`; if null or empty use `DefaultToolSchemaPatches.All`
  (`services` is `null!` from `ToolSignatureCatalogReflection.cs:77`, and test hosts build tools directly); apply in
  `Order`, then `Name` order. Fix the stale "three Apply* patches" doc comment (`:177-181`). In
  `RegisterRoslynSentinelToolsBasic` register each default instance with
  `foreach (var patch in DefaultToolSchemaPatches.All) services.TryAddEnumerable(ServiceDescriptor.Singleton(typeof(IToolSchemaPatch), patch));`
  beside `services.AddSingleton(toolAllowList);` (`:364`; the classes are internal to `Common`, so the generic
  `Singleton<IToolSchemaPatch, X>()` form is CS0122 here), with a comment: a host adds a patch by registering it before
  or after this call, and replaces one with `RemoveAll<IToolSchemaPatch>()` plus its own list; an empty registration
  falls back to the defaults, so "no patches" is not expressible.
- Done when: `Build` 0 errors; dispatcher runs `ReplaceSnippetSchemaTests`, `BatchItemSchemaTests`,
  `SchemaHiddenParametersTests`, `SchemaLeanProfileTests`, `SchemaDataTagOptionTests`, `ClaudeLeanSchemaBudgetTests`: all pass.

### Step B3 - Snapshot records the pipeline
- Done: 61be309e
- Files: `RoslynSentinel.Common/ToolSurface/ToolSurfaceController.cs`
- Depends on: A2, B2
- Effect: text-neutral until A5 is live (then `schemaPatches` shows the real list)
- Lane: implementer
- Change: the constructor takes `IEnumerable<IToolSchemaPatch>? patches = null`; `SchemaPatches` = their names in
  apply order, or `DefaultToolSchemaPatches.All` names when none are registered. Remove the B3 TODO from A2.
- Done when: `Build` 0 errors.

### Step B4 - Pipeline tests
- Done: c9602226
- Files: `RoslynSentinel.Tests.Server/ToolSchemaPatchPipelineTests.cs` (new)
- Depends on: B2, B3
- Pairs with: A5
- Effect: text-neutral
- Lane: test-writer
- Change: `DefaultPipeline_SchemasMatchUnregisteredFallback` (a host with the DI defaults and one with none registered
  list byte-identical schemas for every tool), `ExtraPatch_RegisteredByHost_ChangesListedSchema` (a test patch that
  appends a marker to one tool's description), `RemovedDefaultPatch_IsNotApplied` (`RemoveAll` then the three others:
  hidden parameters stay listed under `--hide-params`), `Snapshot_ListsPatchNamesInApplyOrder`. Mark
  `[NonParallelizable]` where `SchemaOptions` statics are set.
- Done when: dispatcher runs `ToolSchemaPatchPipelineTests`: all pass.

### Step B5 - Docs
- Done: this commit
- Files: `docs/current/references/reference_architecture_map.md`, `docs/current/references/reference_model_eval_procedure.md`
- Depends on: A5, B2
- Effect: none (docs)
- Lane: main session
- Change: architecture map: "Startup flow" gains the startup `Refresh`; new short "Tool surface" paragraph (controller,
  snapshot, patch pipeline, `McpServerStatus` as ground truth); Usage row "Change how a schema is patched" ->
  `IToolSchemaPatch`. Eval procedure: how a harness adds or replaces a patch in its own service collection and where
  the run records it (`schemaPatches`).
- Done when: both docs updated; links resolve.

### Step B6 - No stubs left
- Done: stub sweep 0 matches 2026-11-24; full run pending
- Files: none (check only)
- Depends on: all above
- Effect: none
- Lane: dispatcher
- Change: `Search(mode: "text", query: "NotImplementedException|TODO implement \(Step A2\)|TODO read from the IToolSchemaPatch pipeline", fileGlob: "RoslynSentinel.Common/ToolSurface/*.cs")`
  plus `RoslynSentinel.Common/HiddenSchemaParams.cs`.
- Done when: zero matches; full `RunTest` with 0 failures.

## Later phases (outline; detailed here before each starts)

- **C (stage 2):** runtime toolsets go through the controller: `McpToolsetControl` and tab-state restores become layer
  applies; `ToolsetService` stops mutating the collection itself; exact toolset attribution replaces the catalog lookup.
- **D (stage 3):** parameter rules in the spec: reshape `plan_tool_parameter_surface_options.md` Steps 4-15 onto the
  controller (`ParameterSurface` as a per-snapshot view; `HiddenParametersPatch` reads it).
- **E (stage 4):** caches keyed by snapshot version: validator `SchemaCache`, `HiddenSchemaParams`, `ToolCallHint`;
  `ActiveToolSurface.IsCallable` reads the snapshot (behaviour-changing: runtime-toolset tools become callable there).
- **F (stage 5):** the controller fills the collection at startup; per-class DI blocks and `WithSentinelTools` go.
- **G (stage 6):** JSON toolset layer source; runtime mode switch on `McpServerControl`.

## Out of scope

- Any change to what is exposed (phases A and B only observe and re-plumb).
- `ConsoleMode.WriteStartupDump` reading the snapshot (it reads DI singletons, `ConsoleMode.cs:47-97`, so it omits
  runtime-added tools); candidate for phase C.
- HTTP specifics: the controller reads the one shared collection either way; `plan_stateless_http_toolset_enablement.md`
  makes HTTP sessions use it.

## Risks and open decisions

- **`Changed` re-entrancy: resolved (verified against SDK v2.4.0 source).** The handler calls `Refresh`, which
  enumerates the collection. No deadlock: `TryAdd`/`Remove`/`Clear` release `_mutationLock` before `RaiseChanged()`
  (`McpServerPrimitiveCollection.cs:186-198`); `RaiseChanged` and `EndDeferral` invoke `Changed` after leaving
  `_deferralLock` (`:92-122`); enumeration reads a cached array copy and locks only to rebuild it (`:295-317`). The
  handler runs on the mutating (or last scope-disposing) thread. A3's toolset test still exercises the path.
- **Eager options outside claude-lean.** `Refresh("startup")` reads `IOptions<McpServerOptions>.Value`, which builds every
  lazy tool singleton. In claude-lean `ToolsetService`'s constructor already does this before host start; in other
  modes it is new at that point (hypothesis: harmless). A4's `NoActiveToolsStartupTests` run covers the no-tools path.
- **Toolset attribution** uses `ToolsetCatalog.FindSet`, which can disagree with what was added when a name lands in
  `UnavailableTools`. Exact attribution needs a `ToolsetService` accessor; deferred to phase C.
- **Status token cost.** New fields are null unless set; `exposedBy` appears per active tool only under `toolListing`.
- **Name proximity.** `plan_json_toolset_definitions.md` reserves `ToolsetSurfaceSnapshotTests` for its own resolver
  snapshot (plan-only, no code). Rename it when that plan is reshaped under the controller, to avoid two "surface
  snapshots".
- **Proposal citations: verified at SDK v2.4.0.** `McpServerImpl.cs:117-130` (subscribes `ToolCollection.Changed`
  only with a stateful transport), `:1504` (captures `options.ToolCollection` once), `:1603` ("Unknown tool"),
  `:1751-1760` (`MatchTool` resolves per call via `TryGetPrimitive`), and `:1552-1565` (`tools/list` reads
  `t.ProtocolTool` per request, legacy wire shaping included) all match. Re-check if the package version changes.
- **Empty startup leaves no collection (phase E).** `McpServerOptionsSetup.Configure` (SDK
  `src/ModelContextProtocol/McpServerOptionsSetup.cs:26-39`) keeps an existing `options.ToolCollection` but leaves it
  null when no tools are registered; then `McpServerImpl` never subscribes and `ToolsetService.SetEnabled` throws
  (`ToolsetService.cs:166`). Today the always-on status and control tools keep it non-empty. When the controller owns
  startup population (phase E), it must create the collection itself before the setup runs.
