# Move engine and tool-dependency DI registration down into Engines.* / Tools.*

**Status:** DONE 2026-10-27 (8417587a), slices 1-5; slice 6 is the pilot plan. Open questions resolved: Q1 recommendation
taken (server registers classes per active class, calls only the Impls methods unconditionally); Q2 LLM client stays in Server
(`AddRoslynSentinelLlmClient`); Q3 no shared fake (private `StubLlmClient` in `ToolClassRegistrationAdvancedTests`, `// TODO`
to share); Q4 `AddRoslynSentinelHostOptions` stays; Q5 transitive DI packages sufficed. Deviations: old methods removed in
the same edit as the new ones (identical extension signatures are ambiguous, CS0121); the plain `ILogger` registration moved
into `AddRoslynSentinelToolImplsBasic`; `DependencyInjectionTests` needed an explicit `AddLogging()`. Server tool surface
verified identical after restart (18 classes under claude-lean,subagenttools; 120 declared).

Audience: the repo owner and implementer agents.

## Motivation
Test projects hand-construct engines and tool classes with `new X(...)` at roughly 1,800 sites (see
`plans/plan_test_construction_factories_pilot.md`, Problem). The pilot's re-sequencing (2026-10-25)
decided to build a per-test `ServiceCollection` instead, so the real constructors are resolved and
constructor changes stop cascading. That needs a test project that references only `Common` /
`Engines.*` / `Tools.*` to register and resolve engines and tool classes. It cannot today, because all
registration lives in `Server.Advanced` (the former `Server.Basic` was folded in; see
`proposals/closed/proposal_unify_server_projects.md`):

- `ServiceRegistrationExtensionsBasic.cs`: `AddRoslynSentinelEnginesBasic` :43-86,
  `RegisterRoslynSentinelToolsBasic` :121-403.
- `ServiceRegistrationExtensionsAdvanced.cs`: `AddRoslynSentinelEnginesAdvanced` :26-84,
  `AddRoslynSentinelToolsAdvanced` :156-248.

The ~39 `AddRoslynSentinelEngines*` call sites in 35 test files (Tests.Server 25, ModelEval 10,
Advanced 3, SubAgent 1) all `using RoslynSentinel.Server.Advanced`. `Tests.Tools.Basic`,
`Tests.Tools.Advanced`, `Battery.*` and `Tests.Basic` reference no Server project and cannot use them.

Facts established from source on 2026-10-27:
- `AddRoslynSentinelEnginesBasic` registers only Common / Engines.Basic types: `SentinelConfiguration`,
  a TryAdd default `SentinelHostOptions`, ~28 engine singletons, `PersistentWorkspaceManager` plus
  forwarders `ISolutionProvider` / `IWorkspaceManager` / `IWorkspaceReader`, `TestInstanceLauncher`, and a
  TryAdd `StoppedByScriptMarker`. It can move unchanged.
- `AddRoslynSentinelEnginesAdvanced` = EnginesBasic + ~35 Advanced engines + `MigrationLedger`
  (movable) + a tail that cannot move as-is: `AddHttpClient<LmStudioClient>` and the `ILlmClient`
  forwarder (:63-72; needs `Microsoft.Extensions.Http`, which no lower layer references; the
  `LmStudioClient` ctor throws if `LlmOptions.Model` is null), then `BuildToolGraph(typeof(AsyncifyTools))`,
  `ToolGraph` and `FailureRouter` (:74-81; `AsyncifyTools` is in `Tools.Advanced`, so this cannot live in
  `Engines.Advanced`).
- No Engines/Tools csproj references `Microsoft.Extensions.DependencyInjection` directly; Abstractions,
  Logging and Hosting.Abstractions arrive transitively through `ModelContextProtocol` in `Common`, so
  `IServiceCollection` already compiles. Refs: Engines.Advanced -> Common + Engines.Basic; Tools.Basic ->
  Common + Engines.Basic; Tools.Advanced and Tools.Experimental -> Common + Engines.Advanced + Engines.Basic.
- Every per-tool block is `if (activeToolClasses.Contains(X)) { AddSingleton<Impl>; AddSingleton<Tool>;
  mcpBuilder.WithSentinelTools<Tool>() }`, mixing `AddSingleton` and `TryAddSingleton` for shared Impls
  (WorkspaceReadNavigationImpl, SymbolNavigationImpl, SymbolRelationshipImpl, WorkspaceFileEditImpl,
  WorkspaceBuildTestImpl, WorkspaceProjectManagementImpl, WorkspaceHealthMiscImpl,
  TestCategoryTaggingImpl, QualifyCrefsImpl, RefactoringSignatureImpl / StructuralImpl /
  ExtractionDocsImpl). `WithSentinelTools<T>` (`Common/McpToolSchemaPatcher.cs:131-159`) only adds
  `McpServerTool` factories that resolve `T` from the container, so dependency registration and MCP
  exposure are independent and can be separated.
- `WorkspaceTools` has 15 ctor params; 13 come from the engines registration. Only `ILogger<T>`
  (`AddLogging`) and `WriteToolAdviceHelper` need extra care.

## Proposal
Decided unless listed under Open questions.

### 1. Engine registration moves down, namespace unchanged for callers
The extension methods move into the lower projects but stay in namespace
`Microsoft.Extensions.DependencyInjection`, so the ~35 test files need no `using` changes.
- `AddRoslynSentinelEnginesBasic` -> `Engines.Basic`, body unchanged.
- `AddRoslynSentinelEnginesAdvanced` (engines + `MigrationLedger` only, tail removed) -> `Engines.Advanced`.
- LLM client registration (`AddHttpClient<LmStudioClient>` + `ILlmClient` forwarder) stays in Server as a new
  `AddRoslynSentinelLlmClient`. Tests that need `ILlmClient` register a fake.
- `ToolGraph` / `FailureRouter` registration moves into the `Tools.Advanced` method below.

### 2. New flat, unconditional, TryAdd-only tool-dependency methods
Two methods per Tools project (`Basic`, `Advanced`, `Experimental`), both in namespace
`Microsoft.Extensions.DependencyInjection`:
- `AddRoslynSentinelToolImpls<Flavor>`: that project's `*Impl` helpers (Basic: all of the Impls listed
  above), plus `TryAddSingleton(WriteToolAdviceHelper.WithAllToolsExposed())` in Basic
  (`Common/WriteToolAdviceHelper.cs:119`; the real ctor takes `activeToolClasses` / `allowedToolNames` and
  is not DI-constructible). Advanced's also registers `ToolGraph` and `FailureRouter`. Experimental
  has no Impls; the method exists only if a tool class there needs one (decide in slice 3).
- `AddRoslynSentinelToolClasses<Flavor>`: every `[McpServerToolType]` class of that project, one
  `TryAddSingleton` each.
- All use `TryAdd*` so host-supplied registrations win regardless of call order.
- **Recommended split to protect ValidateOnBuild:** the server calls the Impls methods unconditionally and
  keeps registering tool CLASSES per active class (gated); tests call both methods to get every class. The
  alternative (server also calls the Classes methods) is Open question 1.

### 3. Server shrinks to gating, exposure and host state
Per-class blocks reduce to the gated tool-class registration plus `WithSentinelTools<Tool>()`. Host-state
registrations stay in Server and use plain `AddSingleton` after the flat TryAdds so they override:
`ToolAllowList` (claude-lean only), `WriteToolAdviceHelper(activeToolClasses, allowedToolNames)`,
`ActiveToolSurface`, `ToolsetService` (needs `IOptions<McpServerOptions>`), `ServerStatusTools`,
`WithRequestFilters`, `AddRoslynSentinelHostOptions`. Ordering matters: the host already registers
`StoppedByScriptMarker` with plain `AddSingleton` after the engines TryAdd (`ServerStdio.cs:106`,
`ServerHttp.cs:78`), relying on last-wins.

### 4. Slices
Each slice compile-green; verify with `Build`, `Tests.Server` and `Tests.Advanced`. Migration-first: add
the new method, delegate the old one to it, remove the old body last.
1. Move `AddRoslynSentinelEnginesBasic` to `Engines.Basic`.
2. Move `AddRoslynSentinelEnginesAdvanced` to `Engines.Advanced`; relocate the tail
   (`AddRoslynSentinelLlmClient` in Server; ToolGraph/FailureRouter deferred to slice 3).
3. Add the flat tool methods in `Tools.*` (nothing calls them yet except tests).
4. Server blocks delegate to the flat methods / Impls method.
5. New test in `Tests.Tools.Basic` (references only Common / Engines.Basic / Tools.Basic / Tests): build a
   `ServiceCollection` with the flat methods + `AddLogging`, enable `ValidateOnBuild`, resolve every
   `Tools.Basic` tool class including `WorkspaceTools`. This is the first path that exercises the new
   registration from a Server-free project; passing Tests.Server alone does not, because those tests
   still use the server's own call path.
6. Consumers: `plans/plan_test_construction_factories_pilot.md` Steps 2-3.

No behaviour change for the running server. The mode / tool surface must be identical before and after;
compare `McpServerStatus` output after a restart (per CLAUDE.md Verification, rebuilding alone does not
change the running server).

## Alternatives considered, not pursued
- **Give test projects a `Server.Advanced` reference.** Rejected: tests would pull in hosting, filters and
  the whole startup graph, and the layering rule (`Common` <- `Engines` <- `Tools` <- `Server`) is
  deliberately one-way. Also leaves Tests.Tools.Basic / Battery.* coupled to the server.
- **Per-test-project hand-written factories only (the original pilot decision).** Kept for
  `replaceConstruction`, but implemented through a container; a pure hand-wired factory duplicates the
  production graph and needs a drift guard (pilot Step 3).
- **Parameterless engine constructors.** Rejected earlier (pilot Problem; `TODO.md:102`): invalid-state
  objects and DI constructor-selection ambiguity.
- **Move registration but keep the per-class `activeToolClasses` gating inside the lower projects.**
  Rejected: `activeToolClasses` is a host concept and would leak mode logic into `Tools.*`.

## Open questions
1. **ValidateOnBuild trade-off.** `ServerStartupHelpers.EnableValidateOnBuild` (`ServerStartupHelpers.cs:634`;
   also `ServerHttp.cs:67`) validates every registered constructor graph. Recommended: server registers
   classes per active class only (so a mode still validates only what it serves). Alternative: server also
   registers all classes unconditionally, validating everything in every mode, in exchange for one
   registration path shared with tests. Undecided; the recommendation is the default if no one objects.
2. **Where `AddHttpClient<LmStudioClient>` lives.** `Microsoft.Extensions.Http` PackageReference on
   `Engines.Advanced`, or keep it in Server (proposed above).
3. **Fake `ILlmClient` for tests.** Whether the test project that needs `CommentingTools` gets a shared fake
   helper in `Tests`, versus each project registering its own.
4. **`AddRoslynSentinelHostOptions` (:24-32 of the Basic registration file) into `Common`?** It would let tests
   get a default `SentinelHostOptions` consistently; currently only a TryAdd default exists in EnginesBasic.
5. **Explicit `Microsoft.Extensions.DependencyInjection.Abstractions` PackageReference** on the lower
   projects, versus continuing to rely on the transitive reference via `ModelContextProtocol`.

## Cost / risk
- Touches `Engines.Basic`, `Engines.Advanced`, `Tools.Basic`, `Tools.Advanced`, `Tools.Experimental` (new
  files), and shrinks the two `ServiceRegistrationExtensions*` files in Server.
- **ValidateOnBuild:** registering every tool class unconditionally in the server would make it validate all
  constructor graphs in every mode (Open question 1).
- **Multi-constructor classes** (`AdvancedRefactoringTools`, `GenerationTools`) and `ServerStatusTools` with
  optional params: DI constructor selection must be checked per class; the all-classes resolve test (slice 5
  and its Advanced equivalent) is the guard.
- **Workspace swapping:** `TestServiceProviderBuilder` replaces `FakeWorkspaceManager` via `IWorkspaceManager`
  / `ISolutionProvider`; the forwarders must stay as registered.
- **Eager side effects:** `PersistentWorkspaceManager`'s ctor runs `MSBuildLocator.RegisterDefaults` and starts
  a timer. Harmless while registration stays lazy (only on resolve), but a test that resolves it pays that cost.
- **Silent-dead tool risk:** CLAUDE.md notes a new tool needs a class, a registry entry and a DI block or it is
  silently dead. Moving the DI block down must not break that rule; the slice 5 test should make a tool class
  missing from the flat method fail loudly.
- Makes harder: nothing structural; a new tool class now needs adding to its project's flat method as well as
  the mode dictionary.

## Related
- `plans/plan_test_construction_factories_pilot.md` (consumer; Re-sequencing 2026-10-25)
- `proposals/closed/proposal_unify_server_projects.md` (the fold this builds on)
- Memory: `project_engine_reorg_final_closeout_bugs_1_2_3` (Bug 1, constructor-change cascade)
