# Plan: Tool-surface controller stage F - the controller fills the tool collection at startup; per-class DI blocks and WithSentinelTools go

**Status:** IMPLEMENTED on branch tool-surface-stage-f (unmerged, for owner review) 2026-11-24: 1be4aa87..afb49bbb in worktree c:\tmp\rs-stage-f; full run green at afb49bbb (c:\tmp\rs-testtrees\_results\afb49bbb-20261124-062801-404), ModelEval gate 71/71; live check A-F ok. Owner review 2026-11-24: master merged in (5cb1930e, goldens unchanged) and review fixes applied (13d3fe86, 28012d18, fc4356e6, 45effcdb, 847dd027; filtered runs green apart from the pre-existing stale modeleval_scenarios.md check, summary c:\tmp\rs-testtrees\_results\847dd027-20261124-110512-66308). Close after the merge (the merge or the first master commit after it carries Closes:). Approved 2026-11-24: Stage F: the controller fills the tool collection at startup from ordered registry tables; per-class DI blocks and WithSentinelTools removed. Implemented on branch tool-surface-stage-f (worktree), unmerged, for owner review. Review fixes applied; decisions taken at recommendation (owner away).

Stage 5 of [proposal_tool_surface_controller.md](../proposals/proposal_tool_surface_controller.md). Follows phase A/B
([closed/plan_tool_surface_controller.md](closed/plan_tool_surface_controller.md)), stage C
([closed/plan_tool_surface_controller_stage_c.md](closed/plan_tool_surface_controller_stage_c.md)), stage D
([closed/plan_tool_surface_controller_stage_d.md](closed/plan_tool_surface_controller_stage_d.md)) and stage E
([closed/plan_tool_surface_controller_stage_e.md](closed/plan_tool_surface_controller_stage_e.md); being implemented on master
on 2026-11-24, its Steps 17-18 on HOLD). Phase A/B's risk "Empty startup leaves no collection" and stage E's R5 hand the
empty-collection case to this stage.

## Problem

Registering a tool still means hand-writing a DI block, and a forgotten block is a tool that silently does not exist. Verified
2026-11-24 with `Search` (line numbers drift while stage E lands on master; locate by statement):

- **35 hand-written registration sites (34 classes plus the `RefactoringTools` umbrella at `:579`), 37 `WithSentinelTools<T>` calls.** `RegisterRoslynSentinelToolsBasic`
  (`RoslynSentinel.Server.Advanced/ServiceRegistrationExtensionsBasic.cs`) holds 20 `if (activeToolClasses.Contains("X"))` blocks
  (`:484` SemanticFindReplaceTools ... `:614` CoreRefactoringTools), 3 blocks gated by locals inside the claude-lean branch
  (ToolsetControlTools in `if (toolsetControlActive)` `:440`, DeclarationTools `:466`, ParameterEditTools `:474`) and the always-on
  `AddSingleton<ServerStatusTools>()` + `WithSentinelTools<ServerStatusTools>()` (`:418-419`). `AddRoslynSentinelToolsAdvanced`
  (`ServiceRegistrationExtensionsAdvanced.cs:88-143`) holds 11 more. `Search(mode: text, query: "WithSentinelTools<")` counts 26 calls
  in Basic and 11 in Advanced (the `RefactoringTools` block calls it three times).
- **The rule agents must follow is three places** (CLAUDE.md "A new MCP tool needs three things", `reference_architecture_map.md`
  "Tool registration: three places, none sufficient alone" `:55-69`), and the map says "A missing step yields a dead tool with no
  startup error" (`:68`, Gotchas `:285`). The DI block is the step with no compiler or test guard in a server host.
- **The blocks register no engine or Impl dependency.** The map's pattern `AddSingleton<XImpl>(); AddSingleton<X>();` (`:64`) is stale:
  every `*Impl` is registered unconditionally by `AddRoslynSentinelToolImplsBasic` (`RoslynSentinel.Tools.Basic/ToolServiceCollectionExtensionsBasic.cs:25-48`,
  called at `ServiceRegistrationExtensionsBasic.cs:385`) and `AddRoslynSentinelToolImplsAdvanced` (`ServiceRegistrationExtensionsAdvanced.cs:69`),
  engines by `AddRoslynSentinelEngines*`. Inside the blocks there is only (a) the tool class singleton, (b) one extra
  `AddSingleton<WorkspaceReadNavigationTools>()` in the WorkspaceTools block (`:517`), which is DI presence only because `WorkspaceTools`
  builds its own instance (`RoslynSentinel.Tools.Basic/WorkspaceTools.cs:75`), and (c) host state in the claude-lean branch
  (`ToolsetService`, `TabToolsetStateStore`, `IKnownToolClassNames`), which is not per-class registration.
- **The tool-class singletons do work nobody wrote down.** Tools are constructed per call by `ActivatorUtilities`
  (`RoslynSentinel.Common/McpToolSchemaPatcher.cs:394-395`), so the singletons are not needed to call a tool, but: the stdio server builds
  with `ValidateOnBuild = true` (`ServerStartupHelpers.EnableValidateOnBuild`, called at `ServerStdio.cs:107`), which validates
  constructor graphs only of registered services (the HTTP host does the same, `ServerHttp.cs:74-78`), so the singletons are today's
  only startup dependency check for active classes;
  `SmokeResolveToolTypes` resolves them with `GetRequiredService` (`ServerStartupHelpers.cs:881-906`, `ServerStdio.cs:149`); 18 test lines
  in 9 files resolve tool classes from DI, and `SubAgentRegistrationTests` asserts that inactive classes are absent (`:55`, `:57`, `:69-70`).
- **tools/list order is registration order.** `McpServerPrimitiveCollection` enumerates "in the order they were added" (SDK v2.4.0
  `McpServerPrimitiveCollection.cs:10-11`), and `McpServerOptionsSetup` adds the DI-registered tools in registration order with `TryAdd`
  (first one wins, `McpServerOptionsSetup.cs:30-34`). The order of the DI blocks is therefore part of what every model sees.
- **No tools means no collection.** `McpServerOptionsSetup` leaves `ToolCollection` null when no tool is registered
  (`McpServerOptionsSetup.cs:36-39`); then the server advertises no tools capability (`McpServerImpl.cs:1507-1511`), the controller never
  builds a snapshot (`ToolSurfaceController.Refresh` returns early) and `ApplyToolset` throws "The server exposes no mutable tool
  collection" (`ToolSurfaceController.cs:121`).
- **A dead block that contradicts its own warning.** `if (activeToolClasses.Contains("RefactoringTools"))` (`:579`) names no registry
  class, yet `--include-tools=RefactoringTools` passes `NormalizeToolClassNames` unchanged (`ServerStartupHelpers.cs:260-287`) and fires it,
  while `DescribeUnknownIncludeTools` tells the operator the name was "not recognised and ignored" (`:566-584`). Only the two debug
  profiles in `RoslynSentinel.Server.Advanced/Properties/launchSettings.json:31,36` use it.

The two public registration methods have 66 call sites (`Search` for `AddRoslynSentinelTools(Basic|Advanced)\(`, production and tests);
this plan keeps both signatures, so none of them is edited.

## Decision

- **The controller builds the startup tools; an options post-configure delivers them.** New static
  `ToolSurfaceController.BuildStartupTools(requests, allowList, services)` builds every startup tool through `CreateSentinelTool`, in
  request order, then table order, then the method order `WithSentinelTools` uses today. New `ToolSurfaceStartupPopulation`
  (DI singleton, `IPostConfigureOptions<McpServerOptions>`) calls it once (cached) and adds the same instances to every
  `McpServerOptions` it configures, creating the collection when it is null. Static, so building the tools never resolves the
  controller from inside options construction (the re-entrancy stage D R1 avoided). Cached, because the stateless HTTP transport
  builds a fresh `McpServerOptions` per request through `IOptionsFactory` (`plan_json_toolset_definitions.md` trace of
  `StreamableHttpHandler.cs:585-589`): every request gets the same instances, as with today's DI singletons. Post-configure, so it runs
  after the SDK's own setup; the SDK still adds any tool a host registers with `WithTools<T>` (test probes), and keeps the collection's
  identity (`McpServerOptionsSetup.cs:30-39`). R1.
- **The input is data in `ToolClassRegistry`.** Two ordered tables, `BasicToolClassTypes` and `AdvancedToolClassTypes` (new), list the
  tool-class types in today's block order. Each registration method selects the types whose class name is in its own active set
  (after its claude-lean widening, exactly the set its blocks test today), registers `services.TryAddSingleton(type)` for each selected
  type (R5) and one `ToolSurfaceStartupRequest` (new) with the selected types. `ClaudeLeanOnlyToolClasses` (new) keeps the three blocks'
  claude-lean condition (R7); `AlwaysOnToolClasses` (new) keeps ServerStatusTools always selected. The allow-list is today's
  `ToolAllowList` singleton, applied per tool name exactly as `WithSentinelTools` does.
- **Order is preserved by construction.** The tables are in today's block order, and classes migrate tail-first (Advanced, then
  Basic bottom-up): while some blocks remain, they deliver through the SDK setup and come first, and the migrated suffix comes
  after, so tools/list order equals today's after every slice (R3).
- **Engine and Impl dependencies stay where they already are** (`AddRoslynSentinelToolImplsBasic`, `AddRoslynSentinelToolImplsAdvanced`,
  `AddRoslynSentinelEngines*`). The claude-lean host services (`ToolsetService`, `TabToolsetStateStore`, `IKnownToolClassNames`) stay in the
  claude-lean branch, which keeps its `toolsetControlActive` check because that registers host state, not a tool class. The
  WorkspaceTools block's extra `WorkspaceReadNavigationTools` singleton is dropped (R8).
- **Silent dead tool: startup error plus test (R6).** `ToolClassRegistry.DescribeToolClassTableProblems` (new) lists any registry class
  with no table row, any table type in both or neither table or outside the registry (except always-on), and any table type with no
  `[McpServerTool]` method. `RegisterRoslynSentinelToolsBasic` throws `InvalidOperationException` with that list when it is non-empty;
  the input is static, so every host and every test host fails the same way. A test also checks that every `[McpServerToolType]`
  class in the three Tools assemblies has a row (a class nobody put in the registry cannot be detected at startup). An unknown
  user-typed `--include-tools` name stays a warning.
- **`--list-tools` and the startup dump read the collection.** `ConsoleMode.ExtractToolManifest` (`ConsoleMode.cs:56-97`) reads
  `GetServices<McpServerTool>()`, which would miss every migrated tool. It switches, before the first class migrates, to the
  options' `ToolCollection` minus the controller's `RuntimeToolNames`, so the output stays the startup surface (R10).
- **Two behaviour changes, neither eval-visible**, each its own step: the `RefactoringTools` block goes (Step 16, R4), and an empty
  collection is created when no tool is registered (Step 19, R9). Every eval and production golden stays unchanged in both.
- **Migration-first.** The population lands beside the blocks with empty tables (Step 7); classes move in five slices; `WithSentinelTools`
  is `[Obsolete]` from Step 5 and deleted in Step 17, after its last caller.
- **Not in this stage:** stage E R1(b) ("callable = exposed in the snapshot"; eval-visible, STOP-worthy there), per-source provenance on
  snapshot rows, the JSON layer source and runtime mode switch (stage G).
- **One new DTO**, `ToolSurfaceStartupRequest` (Step 1, NEW-DTO block there). The population class is a service (`DTO-EXEMPT`).

Decisions, 2026-11-24 (review verdict "sound design, 12 fixes before dispatch"; fixes applied; owner away, so R1-R14 are taken at the
recommendation):
- R1 (b) cached post-configure, R4 (a) delete the umbrella, R5 keep the class singletons, R6 startup error plus test, R7 keep the
  claude-lean condition, R8 drop the extra singleton, R9 (a) create the empty collection, R10 startup surface only, R11 order as recommended.
- Review fixes: HTTP test instances do exercise `ValidateOnBuild` and `WriteStartupDump` (R12, Step 21 F); golden hosts register a stub
  `ILlmClient`, resolve their folder from `[CallerFilePath]` and reset statics and `ROSLYNSENTINEL_*` variables; Step 3 split into 3a/3b;
  Step 4 reuses `EvalSurfaceCallabilityTests.SurfaceCases()` and adds the EditSnippets and transcript-replay surfaces; manifest comparisons
  are set-based (culture sort); Step 21 compares canonical JSON and confirms the binary; CLAUDE.md wording says "startup or tests fail";
  master-drift list extended; `McpToolsetControl(git)` before `GitRemote`; `ArchitectureDocFreshnessTests` in the gate.

## Execution rules

- **Start condition.** Start only after stage E has closed on master (its Steps 1-16 and 19-21 landed; Steps 17-18 held) and this plan doc
  is committed on master; branch from that master HEAD. Stage F must not depend on stage E Steps 17-18.
- **Branch and worktree (owner, 2026-11-24).** `GitRemote` is gated in claude-lean: the orchestrator first calls
  `McpToolsetControl(toolSet: git, enabled: true)`, then creates the worktree from master HEAD with
  `GitRemote(operation: worktree, action: add, worktreePath: "c:\tmp\rs-stage-f", branchName: "tool-surface-stage-f", createBranch: true, startPoint: <master HEAD sha>)`,
  then `LoadSolution(solutionPath: "c:\tmp\rs-stage-f\RoslynSentinel.slnx", forceReload: true)`, so every MCP edit, `Build` and `Git`
  call of this session and its subagents works on the worktree. `Git(operation: status)` must show branch `tool-surface-stage-f`
  before the first edit and before every commit. While loaded on the worktree, this session does no master work (one loaded
  solution per session server); peer sessions on master are unaffected.
- **Commits on the branch only**, `Git` scope `listed` with an explicit file list, trailer `Plan: plan_tool_surface_controller_stage_f`.
  Nothing is merged, rebased or pushed; no commit carries `Closes:` (that belongs to the merge, after owner review).
- **Bookkeeping docs stay on master.** This plan doc, `TODO.md`, `INBOX.md` and `CLOSED.md` are edited only by the main session on master
  (it reloads the master solution for that, or uses the built-in Edit tool on master's files, which are not `.cs`). A new `// TODO` in
  branch code gets its INBOX line on master, marked "(branch tool-surface-stage-f)". CLAUDE.md and the architecture map describe the
  branch code, so Step 22 edits them on the branch.
- **Tests.** Implementers never run tests. After each production commit the dispatcher runs
  `scripts/Invoke-WorktreeTests.ps1 -WorktreeRoot c:\tmp\rs-testtrees -Ref <commit sha> -Suite full -Projects <list> -Filter <filter>`.
  Always pass `-Projects` explicitly: an unfiltered run without it resets the main tree's `.roslynsentinel/full-test-marker.json`
  (script header, `-Filter`), which must keep describing master. `RoslynSentinel.Tests.ModelEval` is not in the script's default list;
  name it explicitly (the gate classes there are model-free).
- **Standard gate** (every production step: 5, 7, 9-14, 16, 17, 19): `-Projects RoslynSentinel.Tests.Harness,RoslynSentinel.Tests.ModelEval,RoslynSentinel.Tests.Server,RoslynSentinel.Tests.Tools.Basic,RoslynSentinel.Tests.Tools.Advanced`
  and a `FullyQualifiedName~` filter over `ToolSurfaceParityTests`, `EvalToolSurfaceParityTests`, `EvalToolSchemaVisibilityTests`,
  `ChainEvalToolSurfaceTests`, `ChainEvalToolSurfaceLeanTests`, `EditArmToolSurfaceTests`, `EvalSurfaceCallabilityTests` (stage E Step 5),
  `ClaudeLeanSchemaBudgetTests`, `ClaudeLeanModeTests`, `McpToolsetControlTests`, `ToolSurfaceControllerTests`,
  `ToolSurfaceControllerWiringTests`, `McpServerStatusToolListingTests`, `NoActiveToolsStartupTests`, `SubAgentRegistrationTests`,
  `SubAgentLaunchTests`, `PlanStepRunnerSurfaceTests`, `ExperimentalModeTests`, `DependencyInjectionTests`, `ToolClassRegistrationTests`,
  `ToolClassRegistrationAdvancedTests`, `HiddenParametersParityTests`, `ToolSchemaPatchPipelineTests`, `ArchitectureDocFreshnessTests`,
  plus the step's own Done-when tests.
- **Generated docs.** `ArchitectureDocGenerator` reads `ToolClassRegistry.BuildClassToModes()` and `ModeToToolClasses`
  (`Tests.Server/ArchitectureDocGenerator.cs:123`, `:246`). Every step that reshapes `ToolClassRegistry.cs` (2, 9-14) keeps
  `ArchitectureDocFreshnessTests` green; if `docs/generated/architecture_tools.md` changes, regenerate it in the editing worktree with
  `scripts/Generate-ArchitectureMap.ps1` and commit it with that step (a content change there is reported, since the mode map itself
  is not meant to change). For a pair, one gate run at the later commit; if red, re-run at the earlier commit to attribute it.
- **Red gate: revert, do not fix forward.** A red gate reverts that step's commit (`Git(operation: revert)`) and the step is reported.
  A gate failure is never fixed by editing a golden file, a description, a budget or an assertion.
- **Eval STOP rule.** Any diff in a parity golden (Steps 3-4) or a changed outcome in an eval-surface test means: revert, record the
  diff in this plan (on master), stop the run. No step is meant to change a golden; Steps 16 and 19 are behaviour-changing but leave
  every golden unchanged by design. The owner reviews the branch anyway; anything that changes what eval models see is STOP-worthy.
- **Golden regeneration** (`ROSLYNSENTINEL_UPDATE_TOOL_SURFACE_GOLDEN=1`, new) runs only in Steps 3a, 3b and 4, in place on the editing
  worktree (`scripts/Invoke-WorktreeTests.ps1 -RepoPath c:\tmp\rs-stage-f` without `-Ref`, the variable set in the calling shell, a
  filter on the parity class), at a point where the worktree's production code differs from master only by the no-caller members of
  Steps 1-2. The goldens are committed before Step 5 starts.
- **Sizing.** Depth tier D1 at writing (files <= 3, edits <= 15, acceptance <= 1, tool calls <= 35), sweep tier S0 (1 operation, 8 files,
  25 call sites, 2 projects). At most two implementing agents at once; one `Build` at a time in the editing worktree (a gate run uses its
  own worktree and may overlap the next slice).
- `Build` 0 errors per step (the loaded worktree). Expected warnings: CS0618 for `WithSentinelTools` from Step 5 (37 sites, shrinking per
  slice, gone after Step 13; Step 17 deletes the method).
- `Search` each file for `TODO` before editing it (CLAUDE.md).
- **Master drift.** Before Step 21 and Step 23 the main session compares master with the branch base for the files this plan touches
  (`ServiceRegistrationExtensions*.cs`, `ToolClassRegistry.cs`, `McpToolSchemaPatcher.cs`, `ConsoleMode.cs`, `ServerHttp.cs`, `ServerStdio.cs`,
  `ServerStartupHelpers.cs`, `ModelEvalMcpEnvironment.cs` (stage E Step 15 edits it), `McpToolsetControlTests.cs`, `ToolClassRegistrationTests.cs`,
  `ToolServiceCollectionExtensionsBasic.cs`, `Server.Advanced/Properties/launchSettings.json` `:31,36`) and reports any change; it does not
  rebase without the owner (R11).
- Live checks only against a `LaunchTestInstance` built from the worktree (Step 21), never the session's own server.

## Steps

Order: contract skeleton (1-2); parity goldens (3a, 3b, 4); population implemented and tested (5-6); wired beside the blocks, manifest
reader switched (7-8); classes migrate tail-first (9-13); dead-tool check (14-15); behaviour change 1 and the old path removed (16-18);
behaviour change 2 (19-20); LIVE (21); docs (22); closing (23).

Slice map: 1 || 2; 3a; 3b || 4; 5; 6 || 7; 8 || 9; 10; 11; 12; 13; 14; 15 || 16; 17; 18 || 19; 20; 21; 22; 23.
(1 Common, 2 `ToolClassRegistry.cs`; 3b Tests.Harness, 4 Tests.ModelEval, both on 3a's helper; 6 a new Tests.Server file, 7 Server.Advanced; 8 a new
Tests.Server file, 9 the Advanced registration and registry; 15 a new Tests.Server file, 16 the Basic registration and launchSettings;
18 Tests/Tools.Basic comments, 19 Common. Steps 9-14 all edit `ToolClassRegistry.cs` and run in sequence.)

Suggested orchestrator batches:
- **B1** = Steps 1-8 (with 3a, 3b): contract, goldens, population, wiring beside the blocks, manifest reader. Text-neutral.
- **B2** = Steps 9-15: the five migration slices and the dead-tool check. Text-neutral.
- **B3** = Steps 16-20: the two behaviour changes and the old-path removal.
- **Main session:** Steps 21-23.

### Step 1 - Contract skeleton (Common)
- Done: 1be4aa87 (branch)
- Files: `RoslynSentinel.Common/ToolSurface/ToolSurfaceStartupRequest.cs` (new), `RoslynSentinel.Common/ToolSurface/ToolSurfaceStartupPopulation.cs` (new),
  `RoslynSentinel.Common/ToolSurface/ToolSurfaceController.cs`
- Depends on: none
- Pairs with: Step 2
- Effect: text-neutral (no callers)
- Lane: implementer (two `CreateFile` calls, the record first; one `ReplaceSnippet` on the controller; XML doc comments on every member)
- Change: namespace `RoslynSentinel.Common`; all members `(new)`:
  ```csharp
  // ToolSurfaceStartupRequest.cs
  /// One registration method's startup tool classes, already selected for this launch, in tools/list order.
  /// Source names the registration method ("Basic", "Advanced").
  public sealed record ToolSurfaceStartupRequest(string Source, IReadOnlyList<Type> ToolTypes);

  // ToolSurfaceStartupPopulation.cs
  /// Adds the startup tools to every McpServerOptions the options system builds (stateless HTTP builds one per request), creating
  /// the collection when it is null. The tools are built once, on first use, by ToolSurfaceController.BuildStartupTools.
  public sealed class ToolSurfaceStartupPopulation : IPostConfigureOptions<McpServerOptions>
  {
      public ToolSurfaceStartupPopulation(IServiceProvider services, IEnumerable<ToolSurfaceStartupRequest> requests,
          ToolAllowList? allowList = null) { /* store the three; nothing else */ }
      /// The startup tools in tools/list order, built on first read.
      public IReadOnlyList<McpServerTool> Tools => throw new NotImplementedException(); // TODO implement (Step 5)
      public void PostConfigure(string? name, McpServerOptions options) => throw new NotImplementedException(); // TODO implement (Step 5)
  }

  // ToolSurfaceController.cs
  /// Builds the startup tools: requests in order, each request's types in order, each type's [McpServerTool] methods in the order
  /// WithSentinelTools enumerates them, skipping names allowList rejects. Static: it never resolves a controller.
  public static IReadOnlyList<McpServerTool> BuildStartupTools(IEnumerable<ToolSurfaceStartupRequest> requests,
      ToolAllowList? allowList, IServiceProvider services)
      => throw new NotImplementedException(); // TODO implement (Step 5)
  ```
  The record's creating call carries in `reason`:
  ```
  NEW-DTO: ToolSurfaceStartupRequest (Request, Common.ToolSurface)
  Evaluated: ActiveToolSurface - status/callability view of class names (pre-widening set in claude-lean), no types, no order;
  ToolAllowList - tool names only; ToolsetChange - result of a runtime apply
  Wire impact: none
  ```
  The population file's call carries `DTO-EXEMPT: service (options post-configure), not a DTO`.
- Done when: `Build` 0 errors.

### Step 2 - Registry tables and selection (Server.Advanced)
- Done: 9f036b92
- Files: `RoslynSentinel.Server.Advanced/ToolClassRegistry.cs`
- Depends on: none
- Pairs with: Step 1
- Effect: text-neutral (no callers; the tables start empty)
- Lane: implementer (one `ReplaceSnippet` `batchEdits` call)
- Change: members `(new)`, XML doc on each:
  - `public static readonly IReadOnlyList<Type> BasicToolClassTypes = [];` and `AdvancedToolClassTypes = [];` - summary: "tool-class types in
    tools/list order (the order of the per-class DI blocks before stage F); filled tail-first by Steps 9-13; a new class needs a row".
  - `public static readonly IReadOnlySet<string> ClaudeLeanOnlyToolClasses` = `ToolsetControlTools`, `DeclarationTools`, `ParameterEditTools`
    (OrdinalIgnoreCase) - "registered only while claude-lean is active (their blocks sat inside the claude-lean branch)".
  - `public static readonly IReadOnlySet<string> AlwaysOnToolClasses` = `ServerStatusTools` (Ordinal) - "selected in every launch; not a
    registry class, so it is never counted or excludable".
  - `public static IReadOnlyList<Type> SelectStartupToolTypes(IReadOnlyList<Type> table, IReadOnlySet<string> activeToolClasses, bool claudeLean)`,
    implemented: the types of `table`, in table order, whose `Name` is in `AlwaysOnToolClasses`, or is in `activeToolClasses` and (not in
    `ClaudeLeanOnlyToolClasses` or `claudeLean`).
  - `public static IReadOnlyList<string> DescribeToolClassTableProblems(IReadOnlyDictionary<string, string[]> modeToToolClasses, IReadOnlyList<Type> basicTypes, IReadOnlyList<Type> advancedTypes)`
    => `throw new NotImplementedException(); // TODO implement (Step 14)`.
- Done when: `Build` 0 errors; `ArchitectureDocFreshnessTests` green (Execution rules, "Generated docs").

### Step 3a - Parity golden helper and one smoke case
- Done: e22d1008
- Files: `RoslynSentinel.Tests.Harness/ToolSurfaceGolden.cs` (new), `RoslynSentinel.Tests.Harness/ToolSurfaceParityTests.cs` (new); generated
  `RoslynSentinel.Tests.Harness/ToolSurfaceGolden/*` (written only by the regenerate run)
- Depends on: none (all goldens must be committed before Step 5 starts)
- Effect: text-neutral (tests and golden files only)
- Lane: test-writer
- Change:
  - `ToolSurfaceGolden` (public static, reused by Steps 3b, 4, 8 and 20): `ListToolsAsync(Action<IMcpServerBuilder, IServiceCollection> register,
    Action<IServiceCollection>? configureServices = null)` builds an in-process host the way `McpToolsetControlTests.BuildHost`
    (`McpToolsetControlTests.cs:319-365`) does (engines Advanced, `AddMcpServer`, stream transport over two pipes, descriptors copied into
    `Host.CreateApplicationBuilder()`), plus `ServerStartupHelpers.EnableValidateOnBuild(hostBuilder)` so dependency validation matches the
    stdio and HTTP servers, plus a stub `ILlmClient` registered first (`CommentingEngine` needs one and its real registration lives in the
    Server host; copy the `StubLlmClient` pattern of `Tests.Tools.Advanced/ToolClassRegistrationAdvancedTests.cs:23`, `:41`), or every
    Advanced case fails `ValidateOnBuild`. It starts the host, connects an `McpClient`, lists every page, and returns the protocol tools in
    served order. The canonical form of a tool is its `JsonSerializer.Serialize(tool, McpJsonUtilities.DefaultOptions)`.
  - `AssertMatchesGolden(caseName, tools, [CallerFilePath] string callerFile = "")`: the golden folder is `ToolSurfaceGolden/` beside the
    calling test's source file, resolved from `[CallerFilePath]`; goldens are never copied to the output directory (update mode must
    write the source tree, and a `-Ref` run rebuilds in another worktree, where `[CallerFilePath]` points at that worktree's sources).
    Per case a text file, one line per tool, `name<TAB>sha256(canonical)` in served order; per folder one `_catalog.json` mapping hash ->
    canonical JSON (one copy per distinct tool, which keeps the files small). In update mode it writes both (catalog merged, keys sorted);
    otherwise a mismatch reports the first differing line and, for a changed hash, the golden and the current JSON (clipped).
  - Determinism: `ToolSurfaceParityTests` is `[NonParallelizable]` and resets the process statics in `[SetUp]` and `[TearDown]` the way
    `HiddenParametersParityTests.ResetStatics` (`:34-40`) does (`SchemaOptions.Configure([])`, `SchemaOptions.ConfigureHiddenParameters([])` if
    it still exists after stage E, `ReplaceSnippetOptions` back to its defaults), and clears `ROSLYNSENTINEL_SCHEMA_PROFILE`,
    `ROSLYNSENTINEL_HIDE_PARAMS` and every `ROSLYNSENTINEL_REPLACE_SNIPPET_*` variable for the test (restored afterwards). No case passes a
    static-setting flag such as `--schema-profile` or `--hide-params`.
  - One smoke case, `--mode=all` through the Advanced method (argv handling as in Step 3b).
- Done when: the dispatcher runs the regenerate pass once (Execution rules, "Golden regeneration"), commits the files, then runs
  `-Ref <commit>` twice with filter `FullyQualifiedName~ToolSurfaceParityTests`: both pass. Outcome classes: `green` | `nondeterministic`
  (the two plain runs disagree: a finding, stop) | `host-failed` (the host cannot build: report the missing dependency).

### Step 3b - Parity golden: the production and harness case matrix
- Done: f45b97aa
- Files: `RoslynSentinel.Tests.Harness/ToolSurfaceParityTests.cs`; generated goldens as 3a
- Depends on: 3a
- Pairs with: Step 4
- Effect: text-neutral
- Lane: test-writer
- Change: each case is an argv string run through `ServerStartupHelpers.ParseArgs` (with the `AllModes` set `ServerStdio.cs:21-22` builds)
  and `ParseHideTools`, then `AddRoslynSentinelToolsAdvanced` unless marked Basic:
    - `--mode=<m>` for every key of `ToolClassRegistry.ModeToToolClasses` (25 at writing), and `--mode=all`;
    - Basic: `--mode=all`, `--mode=Refactor,Workspace` (`ModelEvalToolSurfaces.Basic`), `--mode=claude-lean`;
    - `--mode=claude-lean,subagenteval`; `--mode=claude-lean --exclude-tools=AdminTools`; `--mode=Workspace --hide-tools=Git,ReadFile`;
    - the SubAgent child: the `--mode`, `--exclude-tools` and `--hide-tools` values of `SubAgentChildServerLauncher.BuildServerArguments`
      (`SubAgentChildServerLauncher.cs:61-65`);
    - PlanStepRunner: `--include-tools=` plus the default include set that `PlanStepRunnerSurfaceTests` pins (`Program.cs:205`);
    - edge pins: `--mode=Workspace --include-tools=DeclarationTools` (a claude-lean-only class outside claude-lean registers nothing
      today), `--include-tools=WorkspaceTools`;
    - the transcript-replay surface: `AddRoslynSentinelToolsAdvanced` with the `ActiveModes` of `TranscriptReplayTests`
      (`Tests.Harness/TranscriptReplayTests.cs:38`; the field is private, so copy its value with a comment naming the source line).
    Not here: `--include-tools=RefactoringTools` (Step 16 changes it on purpose; Step 20 tests it).
- Done when: as 3a (regenerate, commit, two plain `-Ref` runs green).

### Step 4 - Parity golden: eval surfaces
- Done: 34f5931c
- Files: `RoslynSentinel.Tests.ModelEval/EvalToolSurfaceParityTests.cs` (new); generated `RoslynSentinel.Tests.ModelEval/ToolSurfaceGolden/*`
- Depends on: 3a (the helper)
- Pairs with: Step 3b
- Effect: text-neutral; eval-surface test file
- Lane: test-writer
- Change: namespace `RoslynSentinel.Tests.ModelEval` (not `.LiveModel`, which `ModelEvalAssemblySetUp` ignores without a model);
  `[NonParallelizable]` with the statics and environment reset of 3a. One case per eval registration, each through
  `ToolSurfaceGolden.ListToolsAsync` with the harness `ParameterSurface` registered first exactly as `ModelEvalMcpEnvironment.InitializeAsync`
  does (`ModelEvalMcpEnvironment.cs:124-128`; `fullToolSchema` false, and true for the default world). Cases: every surface of
  `EvalSurfaceCallabilityTests.SurfaceCases()` (`EvalSurfaceCallabilityTests.cs:24`; it already builds the default world for `"lean"` and
  `"full"`, each arm through `EditArmToolSurface.NewAdvancedModes`/`NewAdvancedHiddenTools(arm, "lean")` and `NewBasicModes`/`ArmHiddenTools`,
  and the two `ModelEvalToolSurfaces` surfaces), reused as the case source rather than `EditArmToolSurface.RegisterAdvanced`/`RegisterBasic`
  (`EditArmToolSurface.cs:97`, `:126`), which read the environment; plus the EditSnippets arm of `WholeFileRewriteAgentTests`
  (`WholeFileRewriteAgentTests.cs:279`, its `ActiveModes`, copied with a comment). A comment names the replicated harness lines, since the
  test pins the registration path, not the whole world (R12).
- Done when: as 3a, filter `FullyQualifiedName~EvalToolSurfaceParityTests` with `-Projects RoslynSentinel.Tests.ModelEval`: all pass.

### Step 5 - Implement the startup population
- Done: 13424be4
- Files: `RoslynSentinel.Common/ToolSurface/ToolSurfaceController.cs`, `RoslynSentinel.Common/ToolSurface/ToolSurfaceStartupPopulation.cs`,
  `RoslynSentinel.Common/McpToolSchemaPatcher.cs`
- Depends on: 1; the goldens of Steps 3a, 3b and 4 committed
- Effect: text-neutral (nothing registers the population yet; `WithSentinelTools` keeps its behaviour)
- Lane: implementer
- Change:
  - `McpToolSchemaPatcher`: extract the loop of `WithSentinelTools` (`:136-158`) into new
    `internal static IEnumerable<(MethodInfo Method, string ToolName)> SentinelToolMethods([DynamicallyAccessedMembers(PublicConstructors | PublicMethods | NonPublicMethods)] Type toolType, ToolAllowList? allowList)`
    (same `BindingFlags`, same order, same `toolAttribute.Name ?? toolMethod.Name` filter); `WithSentinelTools` calls it. Mark
    `WithSentinelTools` `[Obsolete("Startup tools come from the ToolClassRegistry tables; see ToolSurfaceStartupPopulation.")]`.
  - `BuildStartupTools`: for each request, each type, each `SentinelToolMethods(type, allowList)` entry,
    `McpToolSchemaPatcher.CreateSentinelTool(method, type, services)`.
  - `ToolSurfaceStartupPopulation`: `Tools` is a `Lazy<IReadOnlyList<McpServerTool>>` (thread-safe) over `BuildStartupTools`. `PostConfigure`:
    when `Tools.Count == 0`, return (comment and `// TODO create the collection even when empty (Step 19)`; this keeps Step 7 text-neutral);
    else `options.ToolCollection ??= new McpServerPrimitiveCollection<McpServerTool>();` and `TryAdd` each tool in order. Comment: it
    must never read `IOptions<McpServerOptions>.Value` or resolve `ToolSurfaceController` (it runs inside options construction).
- Done when: `Build` 0 errors (CS0618 at the 37 `WithSentinelTools` call sites expected); standard gate green.

### Step 6 - Population tests
- Done: 6ab65ede
- Files: `RoslynSentinel.Tests.Server/ToolSurfaceStartupPopulationTests.cs` (new)
- Depends on: 5
- Pairs with: Step 7
- Effect: text-neutral
- Lane: test-writer
- Change: no use of `WithSentinelTools` (Step 17 deletes it). Tests:
  - `BuildStartupTools_FollowsRequestTypeMethodOrder_AndTheAllowList`: two requests over real tool classes; names in order equal the
    `[McpServerTool]` methods of `typeof(T).GetMethods(Instance | Static | Public | NonPublic)` in that order; with a `ToolAllowList`, only allowed
    names; canonical JSON of each equals `CreateSentinelTool` for the same method.
  - `PostConfigure_TwoOptionsInstances_GetTheSameToolInstances` (the stateless HTTP per-request build).
  - `PostConfigure_KeepsAnExistingCollection_AndItsEntriesFirst` (identity kept; a pre-added probe stays first; a duplicate name keeps the
    first, like the SDK's `TryAdd`).
  - `PostConfigure_NoTools_LeavesANullCollectionNull` (pins Step 5's rule; Step 19 updates exactly this test).
  - `BuildStartupTools_WithAProviderWithoutAController_Builds` (no `ToolSurfaceController` registered).
- Done when: dispatcher runs `ToolSurfaceStartupPopulationTests`: all pass.

### Step 7 - Wire the population beside the blocks; manifest reads the collection
- Done: bebd2a05
- Files: `RoslynSentinel.Server.Advanced/ServiceRegistrationExtensionsBasic.cs`, `RoslynSentinel.Server.Advanced/ServiceRegistrationExtensionsAdvanced.cs`,
  `RoslynSentinel.Server.Advanced/ConsoleMode.cs`
- Depends on: 2, 5
- Pairs with: Step 6
- Effect: text-neutral (the tables are empty, so the population adds nothing and creates no collection; the manifest reads the same set)
- Lane: implementer
- Change:
  - Basic, in `RegisterRoslynSentinelToolsBasic`: register the population once as a singleton,
    `services.TryAddEnumerable(ServiceDescriptor.Singleton<IPostConfigureOptions<McpServerOptions>, ToolSurfaceStartupPopulation>());`
    (singleton, so its cache outlives the transient `OptionsFactory`); at the very end of the method (after the claude-lean rebinding
    `activeToolClasses = new HashSet<string>(...)`, `:432`, and after the blocks) call a new private helper
    `AddStartupToolClasses(IServiceCollection services, string source, IReadOnlyList<Type> selected)` with
    `ToolClassRegistry.SelectStartupToolTypes(ToolClassRegistry.BasicToolClassTypes, activeToolClasses, activeModes.Contains(ToolClassRegistry.ClaudeLeanMode))`;
    the helper does `services.TryAddSingleton(type)` per type and `services.AddSingleton(new ToolSurfaceStartupRequest(source, selected))`.
    Comment: tools/list order is registration order, the population runs after the SDK setup, so migrated classes are a suffix.
  - Advanced, at the end of `AddRoslynSentinelToolsAdvanced` (after its own claude-lean widening `:79-85` and the blocks): the same helper
    is private to Basic, so either make it `internal static` there or repeat the two lines (comment the choice) with
    `AdvancedToolClassTypes` and source `"Advanced"`.
  - `ConsoleMode.ExtractToolManifest`: read `services.GetService<IOptions<McpServerOptions>>()?.Value.ToolCollection`, drop names in
    `services.GetService<ToolSurfaceController>()?.RuntimeToolNames`, order by name as today; when the collection is null, keep today's
    `GetServices<McpServerTool>()` path. Rewrite the summary (`:48-55`, it names `WithSentinelTools<T>()`).
- Done when: `Build` 0 errors; the selection call sits after the claude-lean rebinding of `activeToolClasses`
  (`ServiceRegistrationExtensionsBasic.cs:432`) and that rebinding stays above every remaining block and the selection through Steps 9-13
  (reviewer checks the order in the diff); standard gate green.

### Step 8 - Manifest test
- Done: b9e8a5c9 (test lives in Tests.Harness: Tests.Server cannot see ToolSurfaceGolden)
- Files: `RoslynSentinel.Tests.Server/ToolManifestListToolsTests.cs` (new)
- Depends on: 7
- Pairs with: Step 9
- Effect: text-neutral
- Lane: test-writer
- Change: `ConsoleMode.ListTools(registerTools, outputPath, full: true)` (public; `BuildToolManifestFor` is internal and Server.Advanced has no
  InternalsVisibleTo) into a file under the scratch root (`ScratchRoot` resolver); for `--mode=all` and `--mode=claude-lean` (args through
  `ParseArgs` as in Step 3b): the written names, compared as an ordinal set (`ExtractToolManifest` sorts with the culture-sensitive
  `OrderBy(t => t.Name)`, `ConsoleMode.cs:56-97`, so order is not compared), equal the names `ToolSurfaceGolden.ListToolsAsync` serves for
  the same registration, with no duplicates, and are non-empty. Test `ListTools_WritesTheServedStartupNames`.
- Done when: dispatcher runs `ToolManifestListToolsTests`: all pass.

### Step 9 - Migrate the Advanced classes (11)
- Done: f00bf153
- Files: `RoslynSentinel.Server.Advanced/ServiceRegistrationExtensionsAdvanced.cs`, `RoslynSentinel.Server.Advanced/ToolClassRegistry.cs`
- Depends on: 7
- Pairs with: Step 8
- Effect: text-neutral (goldens unchanged)
- Lane: implementer (about 4 edits)
- Change: delete the 11 blocks (`:88-143`); set `AdvancedToolClassTypes` to IntelligenceTools, ScanTools, AdvancedRefactoringTools,
  ModernizationTools, QualityTools, GenerationTools, CommentingTools, CodeTransformTools, AsyncifyTools, SubAgentEvalTools, SubAgentTools
  (this order; usings for `RoslynSentinel.Tools.Advanced` and `RoslynSentinel.Tools.Experimental`); rewrite the widening comment
  (`:76-79`, "the per-tool allow-list that WithSentinelTools reads") to name the startup population.
- Done when: `Build` 0 errors; standard gate green (`ArchitectureDocFreshnessTests` included; Execution rules, "Generated docs").

### Step 10 - Migrate the Basic tail (5)
- Done: e9c9cc38
- Files: `RoslynSentinel.Server.Advanced/ServiceRegistrationExtensionsBasic.cs`, `RoslynSentinel.Server.Advanced/ToolClassRegistry.cs`
- Depends on: 9 (same registry file)
- Effect: text-neutral
- Lane: implementer
- Change: delete the blocks RefactoringSignatureTools (`:594`), RefactoringStructuralTools (`:599`), TypedMemberTools (`:604`),
  RefactoringExtractionDocsTools (`:609`), CoreRefactoringTools (`:614`) and the comment above them (`:585-593`, "harmless duplicate
  registration"); `BasicToolClassTypes` = RefactoringSignatureTools, RefactoringStructuralTools, TypedMemberTools,
  RefactoringExtractionDocsTools, CoreRefactoringTools (using `RoslynSentinel.Tools.Basic`). The `RefactoringTools` block (`:579`) stays
  until Step 16.
- Done when: `Build` 0 errors; standard gate green (`ArchitectureDocFreshnessTests` included). Same for Steps 11-14.

### Step 11 - Migrate the Basic middle (6)
- Done: 610b3fdd
- Files: as Step 10
- Depends on: 10
- Effect: text-neutral
- Lane: implementer
- Change: delete SymbolNavigationTools (`:542`), SymbolRelationshipTools (`:547`), GitTools (`:552`), GitRemoteTools (`:557`), AdminTools (`:562`),
  WholeFileWriteTools (`:570`) with their comments; prepend those six, in this order, to `BasicToolClassTypes`.
- Done when: `Build` 0 errors; standard gate green.

### Step 12 - Migrate the Basic upper middle (6)
- Done: 4a8499c9
- Files: as Step 10
- Depends on: 11
- Effect: text-neutral (wire); DI presence of `WorkspaceReadNavigationTools` changes only when WorkspaceTools is active without it (R8)
- Lane: implementer
- Change: delete TestCategoryTaggingTools (`:502`), QualifyCrefsTools (`:509`), WorkspaceTools (`:515`, including the
  `AddSingleton<WorkspaceReadNavigationTools>()` at `:517`), EditSnippetsTools (`:523`), WorkspaceReadNavigationTools (`:530`), DocumentationTools
  (`:535`) with their comments; prepend those six, in this order, to `BasicToolClassTypes`.
- Done when: `Build` 0 errors; standard gate green (`McpToolsetControlTests.WorkspaceTools_AndReadNavigationTools_ResolveFromTheContainer_AndSearchIsDeclaredOnce` included).

### Step 13 - Migrate the Basic head (6) and the claude-lean branch
- Done: 10bcb7dc
- Files: as Step 10
- Depends on: 12
- Effect: text-neutral
- Lane: implementer (about 11 edits)
- Change: delete `AddSingleton<ServerStatusTools>()`/`WithSentinelTools<ServerStatusTools>()` (`:418-419`, keep the comment's substance: always
  registered, outside the mode map), the two ToolsetControlTools lines inside `if (toolsetControlActive)` (the `ToolsetService` and
  `TabToolsetStateStore` registrations stay), the DeclarationTools and ParameterEditTools blocks (`:466`, `:474`) and their locals
  `declarationActive`/`parameterEditActive`, and the SemanticFindReplaceTools and NamedArgumentsTools blocks (`:484`, `:493`); prepend
  ServerStatusTools, ToolsetControlTools, DeclarationTools, ParameterEditTools, SemanticFindReplaceTools, NamedArgumentsTools to
  `BasicToolClassTypes`. Rewrite the comments that describe the old mechanism: the allow-list comment ("before any WithSentinelTools call",
  `:346`) and the claude-lean comment ("makes WithSentinelTools register none of the non-Core tools", `:424`).
  After this step `BasicToolClassTypes` holds 23 types and `AdvancedToolClassTypes` 11: the 33 registry classes plus ServerStatusTools.
- Done when: `Build` 0 errors with no CS0618 for `WithSentinelTools` outside the `RefactoringTools` block; standard gate green.

### Step 14 - Dead-tool check at startup
- Done: 8c2cc7bb
- Files: `RoslynSentinel.Server.Advanced/ToolClassRegistry.cs`, `RoslynSentinel.Server.Advanced/ServiceRegistrationExtensionsBasic.cs`
- Depends on: 13
- Effect: text-neutral (the tables are consistent, so nothing throws)
- Lane: implementer
- Change: implement `DescribeToolClassTableProblems`: one line per problem, naming the class and the fix ("add typeof(X) to
  BasicToolClassTypes or AdvancedToolClassTypes in ToolClassRegistry.cs"): a class of `modeToToolClasses` in neither table; a type in both
  tables or twice in one; a table type whose name is neither a registry class nor in `AlwaysOnToolClasses`; a table type with no
  `[McpServerTool]` method. First statement of `RegisterRoslynSentinelToolsBasic`: when the list for the real registry and tables is
  non-empty, throw `InvalidOperationException` joining it. Rewrite the `ModeToToolClasses` summary sentence "each registration block is
  guarded by activeToolClasses.Contains(...)" (`ToolClassRegistry.cs:96-98`). Limit, stated in the method's summary: it does not detect a
  class in the wrong table (an Advanced class in `BasicToolClassTypes` would register in Basic hosts); the goldens catch that for the
  covered cases (R6).
- Done when: `Build` 0 errors; standard gate green.

### Step 15 - Table tests
- Done: 38753772
- Files: `RoslynSentinel.Tests.Server/ToolClassTableTests.cs` (new)
- Depends on: 14
- Pairs with: Step 16
- Effect: text-neutral
- Lane: test-writer
- Change: `Tables_AreConsistentWithTheRegistry` (real inputs: empty list); `EveryToolTypeInTheToolsAssemblies_HasARow` (every class with
  `[McpServerToolType]` and at least one `[McpServerTool]` in the assemblies of `typeof(WorkspaceTools)`, `typeof(IntelligenceTools)` and
  `typeof(CodeTransformTools)` is in exactly one table); `BrokenInputs_AreReportedByName` (a registry copy with an extra `GhostTools`; a
  table with a type that has no tool method; a type in both tables: each named).
- Done when: dispatcher runs `ToolClassTableTests`: all pass.

### Step 16 - BEHAVIOUR-CHANGING: the RefactoringTools block goes
- Done: 474afe3c (launchSettings.json is gitignored and absent from the worktree: the untracked local copy still names RefactoringTools, fix after the merge)
- Files: `RoslynSentinel.Server.Advanced/ServiceRegistrationExtensionsBasic.cs`, `RoslynSentinel.Server.Advanced/Properties/launchSettings.json`
- Depends on: 13 (same file)
- Pairs with: Step 15
- Effect: behaviour-changing, not eval-visible: `--include-tools=RefactoringTools` no longer registers RefactoringSignatureTools,
  RefactoringStructuralTools and RefactoringExtractionDocsTools; the startup warning that already calls the name ignored becomes true. The
  two launchSettings profiles name the three classes instead and serve the same tools. No eval, PlanStepRunner or child surface uses the
  name (`PlanStepRunnerSurfaceTests.cs:40` asserts it). `plan_json_toolset_definitions.md` Step 23 planned the same deletion. R4.
- Lane: implementer
- Change: delete the block (`:579-584`); in `launchSettings.json:31,36` replace `RefactoringTools` with
  `RefactoringSignatureTools,RefactoringStructuralTools,RefactoringExtractionDocsTools` (Edit tool; it is JSON).
- Done when: `Build` 0 errors with no CS0618 for `WithSentinelTools`; standard gate green (every golden unchanged).

### Step 17 - Delete WithSentinelTools
- Done: 4404d755
- Files: `RoslynSentinel.Common/McpToolSchemaPatcher.cs`, `RoslynSentinel.Common/ToolAllowList.cs`
- Depends on: 16 (its last caller)
- Effect: text-neutral
- Lane: implementer
- Change: first `Search(mode: text, query: "WithSentinelTools<\\w+>\\(", includeTests: true)` must find nothing; else stop and report. Delete
  `WithSentinelTools`; rewrite the mentions that would be stale or CS1574: the class summary (`:14-41`, "reimplements its registration
  loop"), `:59`, the `CreateSentinelTool` summary (`:162-166`, cref), `:481`, and the cref in `ToolAllowList.cs:5`.
- Done when: `Build` 0 errors, no CS1574; standard gate green.

### Step 18 - Stale mentions outside Server
- Done: 16109961
- Files: `RoslynSentinel.Tools.Basic/ToolServiceCollectionExtensionsBasic.cs` (`:13`), `RoslynSentinel.Tests.Tools.Basic/ToolClassRegistrationTests.cs`
  (`:46-48`: ServerStatusTools is now the always-on row of `BasicToolClassTypes`), `RoslynSentinel.Tests.Server/McpToolsetControlTests.cs` (`:293`)
- Depends on: 17
- Pairs with: Step 19
- Effect: text-neutral (comments only)
- Lane: implementer (3 edits)
- Change: reword each comment to name the startup population and the registry tables.
- Done when: `Build` 0 errors; `Search(mode: text, query: "WithSentinelTools", includeTests: true)` finds nothing.

### Step 19 - BEHAVIOUR-CHANGING: create the collection when no tool is registered
- Done: cbab4a64
- Files: `RoslynSentinel.Common/ToolSurface/ToolSurfaceStartupPopulation.cs`, `RoslynSentinel.Tests.Server/ToolSurfaceStartupPopulationTests.cs`
- Depends on: 7, 6
- Pairs with: Step 18
- Effect: behaviour-changing, not eval-visible: a host whose launch registers no tool now has an empty collection, so the server
  advertises the tools capability and answers `tools/list` with an empty list (`McpServerImpl.cs:1507-1513`), the controller builds a
  version-1 snapshot with no rows, and `ApplyToolset` works instead of throwing (`ToolSurfaceController.cs:121`). Reachable only when the
  always-on `McpServerStatus` is hidden and the class set registers nothing; every eval and production golden is unchanged. R9.
- Lane: implementer
- Change: `PostConfigure` creates the collection (`??=`) before the zero-tools return and drops the Step 5 TODO; rename and flip
  `PostConfigure_NoTools_LeavesANullCollectionNull` to `PostConfigure_NoTools_CreatesAnEmptyCollection` (the one pinned assertion, updated in
  this slice).
- Done when: `Build` 0 errors; standard gate plus `ToolSurfaceStartupPopulationTests` green.

### Step 20 - Tests for the two behaviour changes
- Done: 52c95fdf (both fixtures in Tests.Harness)
- Files: `RoslynSentinel.Tests.Server/ToolSurfaceEmptyCollectionTests.cs` (new), `RoslynSentinel.Tests.Harness/UmbrellaIncludeToolsTests.cs` (new)
- Depends on: 16, 19
- Effect: text-neutral
- Lane: test-writer
- Change:
  - `ToolSurfaceEmptyCollectionTests.ZeroToolHost_HasAnEmptyCollection_AndAWorkingController`: Basic registration with no mode, one
    include that registers nothing outside claude-lean (`DeclarationTools`) and `hideTools: {"McpServerStatus"}`; `IOptions<McpServerOptions>.Value.ToolCollection`
    is non-null and empty; `controller.Current.Version == 1` with no rows; `controller.ApplyToolset(ToolSetName.git, true, ["GitRemote"], factory)`
    adds the tool; over an in-process client, the server capabilities include tools and `ListToolsAsync` is empty.
  - `UmbrellaIncludeToolsTests.RefactoringTools_IsIgnored_AsTheStartupWarningSays`: through `ToolSurfaceGolden.ListToolsAsync`,
    `--include-tools=WorkspaceTools,RefactoringTools` serves exactly the tools of `--include-tools=WorkspaceTools`, and
    `ServerStartupHelpers.DescribeUnknownIncludeTools` names `RefactoringTools`.
- Done when: dispatcher runs both classes: all pass.

### Step 21 - LIVE check from the worktree
- Done: 2026-11-24 at 52c95fdf (GitSha 52c95fdf): preconditions ok; A identical (115 tools); B identical (28 tools), git toolset exposedBy toolset:git, surfaceVersion 2 (ok, not n/a-http); C ok; D identical (115 names); E stopped; F identical (115 names). Details c:\tmp\rs-stage-f-live-results.md
- Files: none
- Depends on: Steps 1-20 committed; the session loaded on the worktree; no subagent mid-operation
- Effect: none
- Lane: main session
- Change and outcome classes (record each, with the date, the branch head and the payload's `GitSha`, in this plan on master):
  - **Preconditions.** `LoadSolution` does not rebind the executing binary (`reference_live_server_freshness.md`, "LoadSolution does not
    rebind the executing binary"): before trusting any result, confirm the instance's binary path (the payload's `OutputPath`, and
    `serverBinaryPath` from `McpServerStatus` on the instance) lies in the worktree's throwaway build output and `GitSha` equals the branch
    head (`ok` | `stale-binary`: stop). The child inherits the parent server's environment: check that no `ROSLYNSENTINEL_SCHEMA_PROFILE`,
    `ROSLYNSENTINEL_HIDE_PARAMS` or `ROSLYNSENTINEL_REPLACE_SNIPPET_*` is set for the session's server (they change schemas); if one is,
    record it and mark A, B and F `n/a-env` instead of comparing.
  - **Comparison rule.** Canonicalize both sides before comparing: parse the wire `tools/list` result and the golden catalog entries and
    re-serialize them the same way (for example a PowerShell `ConvertFrom-Json | ConvertTo-Json -Depth 100 -Compress` round trip), so
    whitespace and property-order differences between the SSE payload and the golden serializer do not count.
  - **A. mode all.** `Build`, then `McpServerControl(operation: LaunchTestInstance, build: Always, mode: "all")`. Send the returned
    `tools/list` request; compare name, order and each tool's canonical JSON with the `--mode=all` golden (`identical` | `order-differs` |
    `content-differs` | `names-differ`; any but `identical` is STOP).
  - **B. claude-lean.** The same with `mode: "claude-lean"` against its golden (same classes). Then `McpToolsetControl(git, true)` and
    `McpServerStatus(toolListing: all, toolNameFilter: "GitRemote")`: `exposedBy: toolset:git`, `surfaceVersion` 2 (`ok` | `not-exposed` |
    `n/a-http` when `plan_stateless_http_toolset_enablement.md` Step 2 is not on the branch, the known stateless "Unknown tool" symptom).
  - **C. A populated tool runs.** In the mode-all instance, `LoadSolution` on the worktree's `.slnx`, then `GetFileOutline` on
    `RoslynSentinel.Common/ToolAllowList.cs` (`ok` | `construct-failed`: blocker, the per-call `ActivatorUtilities` path is broken).
  - **D. --list-tools in a real process.** From the launch payload's `OutputPath`, run
    `dotnet <OutputPath>\RoslynSentinel.Server.Advanced.dll --mode=all --list-tools --full --output <scratch root>\stage-f\list-tools-all.json`
    (PowerShell; output under the scratch root): the names, compared as a set (the manifest sorts culture-wise), equal the golden names
    (`identical` | `missing` | `empty`: the manifest reader is broken).
  - **F. Startup dump.** The HTTP host calls `ConsoleMode.WriteStartupDump` (`ServerHttp.cs:113`): `tool_list_all.json` in the mode-all
    instance's working directory (the payload's log paths name it) lists, as a set, exactly the golden names (`identical` | `missing` |
    `absent`: the dump failed, read the instance's stderr). The same instance also built with `ValidateOnBuild` (`ServerHttp.cs:74-78`), so
    a successful launch is itself that check.
  - **E.** `McpServerControl(operation: StopTestInstance, pid: <each>)`.
  - Not covered live: `SmokeResolveToolTypes`, which runs only in the stdio host and only in DEBUG builds (`ServerStdio.cs:149`); the branch
    is never served by a stdio session before the merge (R12).
- Done when: A-F recorded.

### Step 22 - Docs (on the branch)
- Done: afb49bbb (CLAUDE.md 16017 -> 15978 bytes)
- Files: `CLAUDE.md`, `docs/current/references/reference_architecture_map.md`
- Depends on: 17, 19
- Effect: none (docs)
- Lane: main session
- Change:
  - **CLAUDE.md** (over its 15 KB budget at 15,791 bytes on 2026-11-24: the edit must not grow it; measure before and after, and run
    `scripts/Test-GuidanceHygiene.ps1`). Replace the three-line "A new MCP tool needs three things" bullet (`CLAUDE.md:180-182`) with, for
    example: "**A new MCP tool needs two things**, or startup or tests fail: the `[McpServerToolType]` class in a `Tools.*` project and
    its `Server.Advanced/ToolClassRegistry.cs` entries (a mode plus a `BasicToolClassTypes`/`AdvancedToolClassTypes` row, in tools/list
    order)." Not "startup fails": a class with no registry entry at all is still dead at startup and only the Step 15 test catches it.
    The `<ProjectReference>` sentence stays.
  - **Architecture map:** "Tool registration: three places" (`:55-69`) becomes two places plus how startup population works
    (`ToolSurfaceController.BuildStartupTools`, `ToolSurfaceStartupPopulation` post-configure with cached instances, the collection always
    created, table order is tools/list order, class singletons registered for `ValidateOnBuild`); note that the flat
    `AddRoslynSentinelToolClasses{Basic,Advanced,Experimental}` lists stay test-enforced (`ToolClassRegistrationTests`); the stale
    `AddSingleton<XImpl>()` pattern goes. "Tool surface" (`:94-96`, "an allowed tool with a missing DI block") and Gotchas (`:285`, "silent
    dead tools") say a registry class without a row fails startup, and a class with no registry entry fails `ToolClassTableTests`. Usage row "Add a tool" (`:261`) points at the two places.
- Done when: both match the branch code; CLAUDE.md is not larger than before; links resolve.

### Step 23 - No stubs left, full run, master bookkeeping
- Done: branch searches clean; full run green at afb49bbb (c:\tmp\rs-testtrees\_results\afb49bbb-20261124-062801-404, 0 failures); ModelEval gate 71/71 (c:\tmp\rs-testtrees\_results\afb49bbb-20261124-065031-56736); master bookkeeping this commit
- Files: on master only: this plan, `docs/current/TODO.md` (the tool-surface stages line), `docs/current/INBOX.md`
- Depends on: all above
- Effect: none
- Lane: dispatcher (checks on the branch), main session (master)
- Change:
  - On the branch head: `Search(mode: "text", query: "NotImplementedException|TODO implement \\(Step", fileGlob: "{RoslynSentinel.Common/ToolSurface/*.cs,RoslynSentinel.Server.Advanced/ToolClassRegistry.cs}")`
    finds nothing; `Search(mode: "text", query: "activeToolClasses\\.Contains\\(", fileGlob: "RoslynSentinel.Server.Advanced/ServiceRegistrationExtensions*.cs")`
    finds only the `toolsetControlActive` host-state check.
  - Full run: `scripts/Invoke-WorktreeTests.ps1 -WorktreeRoot c:\tmp\rs-testtrees -Ref <branch head> -Suite full -Projects <every default
    project, listed explicitly>`, then the ModelEval gate classes (`-Projects RoslynSentinel.Tests.ModelEval` with the Step 4 and standard-gate
    ModelEval filter). 0 failures.
  - Master: Status line "IMPLEMENTED on branch tool-surface-stage-f (unmerged), <first>..<head>; full run <results folder>; close after the
    merge"; TODO.md stages line: F on the branch awaiting review; INBOX lines for: every new `// TODO` from the branch; the startup-dump
    option of R10; `ConsoleMode.ExtractToolManifest`'s fallback that scans `RoslynSentinel.Server*` assemblies for tool types, which hold none
    (`ConsoleMode.cs:64-90`); the stale `ActiveToolTypes` comment (`ServerStdio.cs:24-29`).
  - No `ProjectDoc close`: the merge (or the first master commit after it) carries `Closes: plan_tool_surface_controller_stage_f` and closes
    the plan and the proposal's stage 5.
- Done when: both searches clean; both runs 0 failures; master bookkeeping committed on master.

## Out of scope

- "Callable = exposed in the snapshot" (stage E R1(b)): eval-visible, STOP-worthy; F makes it possible, it does not take it.
- Stage E Steps 17-18 (HOLD): F touches no `ActiveToolSurface` code and does not depend on them.
- Per-source provenance on snapshot rows (`Source` "Basic"/"Advanced" on `ToolSurfaceTool`); the controller building its whole spec from
  layers (the proposal's compose stage); the JSON layer source and runtime mode switch (stage G).
- Deriving the flat `AddRoslynSentinelToolClasses*` test lists from the tables.
- `WriteToolAdviceHelper` as a second startup-only callability rule (stage E R10, INBOX).
- Reordering tools/list (R3).

## Risks and open decisions

- **R1. Delivery mechanism (recommend: (b)).** (a) Keep DI `IEnumerable<McpServerTool>` but register it from one generic loop: lowest risk,
  the SDK setup fills the collection, but the controller still does not own population and the empty collection needs a separate
  configure. (b) Static controller build plus a cached post-configure (the Decision). (c) The controller instance populates, resolved
  inside options construction: the re-entrancy stage D R1 refused to reason about. (d) Install a new collection object: rejected by the
  proposal (stateful sessions keep the old reference). Why (b) over the cheaper (a): (a) leaves the SDK setup as the writer of the startup
  collection, so the proposal's "one front door" still has a second door, the empty collection needs its own configure anyway, and stage
  G (a runtime mode switch rebuilding the startup layer) would need the population moved again; (b) puts the build in controller code at
  roughly the same cost (one extra class, one cache). Cost of (b): in a test host that also registers SDK probe tools, the probes now come
  before Sentinel tools (production has none; `Search` finds probes only in Tests.Server and Tests.Harness hosts). Not STOP-worthy.
- **R2. Options built more than once.** Stateless HTTP builds `McpServerOptions` per request, and a stateful session can get its own options
  instance too; in every case (b) must cache: the population is a singleton and adds the same instances each time.
  Pinned by `PostConfigure_TwoOptionsInstances_GetTheSameToolInstances` (Step 6). Compatible with `plan_stateless_http_toolset_enablement.md`
  Step 2 (`ConfigureSessionOptions` then swaps in the shared collection). Not STOP-worthy.
- **R3. tools/list order is wire (decided: preserve).** Tables in today's block order and tail-first slices keep the order; the goldens pin
  it per case. Any reorder (for example alphabetical) changes every model's prompt: STOP-worthy, not in this plan.
- **R4. The RefactoringTools block (recommend: (a)).** (a) Delete it and fix `launchSettings.json` (Step 16); (b) keep the name as an alias row:
  keeps a non-registry name alive and contradicts the startup warning; (c) keep the block: blocks Step 17. Not STOP-worthy (debug profiles only).
- **R5. Keep the tool-class singletons (recommend: keep).** The proposal says the blocks' `AddSingleton<X>` lines are not needed. They are
  not needed to call a tool, but they feed `ValidateOnBuild` (the only startup dependency check), `SmokeResolveToolTypes`, 18 test lines in
  9 files, and the absence checks of `SubAgentRegistrationTests`. Dropping them needs a replacement check first. Not STOP-worthy.
- **R6. Dead-tool check form (recommend: startup error plus test).** Options: test only; startup error only; both. The startup check is on
  static data, so it costs nothing and fails every host at once; the reflection test catches a class that has no registry entry at all,
  which startup cannot know. The check cannot be "every active class has a row in this flavor's table": Basic hosts deliberately leave
  Advanced classes inert (`ToolClassRegistry.cs:96-98`). Gap: neither check detects a class in the wrong table; the Basic-method goldens
  catch it for the covered cases only. Not STOP-worthy.
- **R7. Claude-lean-only classes (recommend: keep the condition as data).** Without it, `--include-tools=ToolsetControlTools` outside
  claude-lean would register a class whose `ToolsetService` dependency is absent (with R5's singletons, `ValidateOnBuild` fails stdio
  startup), and DeclarationTool/ParameterEditTool would become exposable outside claude-lean. The Step 3b edge case pins today's behaviour.
- **R8. Drop the extra WorkspaceReadNavigationTools singleton (recommend: drop).** `WorkspaceTools` builds its own (`WorkspaceTools.cs:75`).
  Observable only through `GetService` when WorkspaceTools is active without WorkspaceReadNavigationTools (`--include-tools=WorkspaceTools`,
  e.g. `launchSettings.json:31`); every mode that has one has both. Wire-neutral.
- **R9. Empty collection (recommend: create it, Step 19).** (a) Always create: the zero-tool host advertises tools and the controller and
  `ApplyToolset` work (stage E R5, phase A/B risk); (b) leave null. Behaviour-changing for zero-tool hosts only. Not STOP-worthy.
- **R10. Startup dump content (recommend: startup surface only).** Excluding runtime-toolset tools keeps `tool_list_<mode>.json` exactly as
  today (the map's Usage row says "startup only"); including them would be more accurate but changes the file for claude-lean sessions
  with restored toolsets. `scripts/Get-UnknownParameterReport.ps1` reads `tool_list_all.json` (no runtime toolsets in mode all) either
  way. INBOX the alternative (Step 23).
- **R11. Overlap and order with other plans (recommend: stage E, then F on the branch, owner merge; HTTP plan any time; JSON plan after
  F merges, reshaped as stage G).**
  - `plan_tool_surface_controller_stage_e.md`: the worktree starts after its close; Steps 17-18 (HOLD) do not touch F's files.
  - `plan_stateless_http_toolset_enablement.md` (APPROVED, not started): Step 2 edits `ServerHttp.cs`, Step 6 edits
    `ServiceRegistrationExtensionsBasic.cs` (a different method). Independent of F; if it lands on master first, the owner's merge
    resolves textual conflicts. Step 21 B needs it for the HTTP toolset case (`n/a-http` otherwise).
  - `plan_json_toolset_definitions.md` (DRAFT, not started) is heavily affected: its Step 8 assumes "20+ registration blocks"; after F it
    shrinks to passing `ToolsetResolveResult.ActiveToolClasses` to `SelectStartupToolTypes` and its allow-list to the population. Its Step 1
    snapshot overlaps F's goldens (reuse `ToolSurfaceGolden`); Steps 5, 15 and 18 edit `ToolClassRegistry.cs` (`ClaudeLeanOnlyToolClasses`
    must move with the claude-lean lists); Steps 19 and 21 change the child and ModelEval surfaces, so F's goldens for those cases must be
    regenerated on purpose there; Step 23's `RefactoringTools` deletion is done by F. Starting it before F merges means rebasing a large
    branch: do not.
  - A tool class added on master while the branch is open fails Step 14's check after the merge until it gets its row: no silent dead tool.
  Not STOP-worthy; the merge order is the owner's call.
- **R12. Coverage gaps (planner-side, for the reviewer).** (a) `ValidateOnBuild` and `WriteStartupDump` are exercised live: the HTTP host
  enables `ValidateOnBuild` (`ServerHttp.cs:74-78`) and calls `WriteStartupDump` (`:113`), so a `LaunchTestInstance` child runs both (Step 21
  A and F), and parity hosts build with `EnableValidateOnBuild`; `--list-tools` is checked in Step 8 and Step 21 D. Only
  `SmokeResolveToolTypes` stays uncovered: stdio host, DEBUG builds only (`ServerStdio.cs:149`); it resolves four class singletons that R5
  keeps registered, so it is low risk until the owner runs the branch on stdio. (b) Step 4 replicates `ModelEvalMcpEnvironment`'s
  registration lines instead of calling them, so it pins the registration path, not the whole world. (c) The goldens cover the listed argv
  cases only; a combination outside them (for example claude-lean with an unusual include) is covered by the selection rule's unit shape,
  not by a golden. A green gate does not cover any of these beyond that.
- **R13. Golden format (recommend: name+hash per case plus a deduplicated catalog).** Full JSON per case is several MB of near-duplicate
  schemas; hashes alone are not diagnosable. Location beside the test sources via `[CallerFilePath]` (not `docs/generated`, not copied to output),
  regenerated only in Steps 3a, 3b and 4.
- **R14. Post-commit gate.** The owner's rule tests a commit (`-Ref`), so a red slice is committed before it is caught; the plan reverts it
  rather than fixing forward. Running the script in place without `-Ref` before committing would gate pre-commit but shares the editing
  worktree's `bin`/`obj` with the implementers; not recommended.
