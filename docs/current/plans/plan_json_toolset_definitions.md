# Plan: Named JSON toolset files as the one way to choose a tool surface

**Status:** DRAFT 2026-11-23. Implements [proposal_json_toolset_definitions.md](../proposals/proposal_json_toolset_definitions.md); owner decisions settled 2026-11-22/23 (below). Not started.

## Problem

Four inputs combine to build the active tool surface: modes, `--include-tools`, `--exclude-tools`
(union, then subtract) and `--hide-tools` (applied at registration). They work in two different units,
and seven separate places encode the same idea of "a lean surface". Evidence, the trigger incident and
the full list of consumers: see the proposal's Motivation section.

Facts verified for this plan on 2026-11-23 (two read-only traces):

- **Startup parsing.** `ServerStartupHelpers.ParseArgs` returns `void` with 8 `out` parameters
  (`ServerStartupHelpers.cs:52`). Its production callers are `ServerStdio.cs:41` and `ServerHttp.cs:26`.
- **The registry holds strings only.** `ToolClassRegistry.ModeToToolClasses` (`ToolClassRegistry.cs:100`)
  maps mode names to class-name strings. DI is one hard-coded block per class: 20 `if` blocks in
  `ServiceRegistrationExtensionsBasic.cs` and 11 in `...Advanced.cs`.
- **Per-tool gating is generic.** `ToolAllowList` (`RoslynSentinel.Common/ToolAllowList.cs:11`) is read
  by `McpToolSchemaPatcher.WithSentinelTools<T>` (`McpToolSchemaPatcher.cs:131`).
- **Only the source of allowed names is hard-wired**, at `ServiceRegistrationExtensionsBasic.cs:286-295`.
- **Tool-to-class mapping already exists.** `McpToolSchemaPatcher.DiscoverAllDeclaredTools(params Assembly[])`
  (`:254`) returns `(ToolName, ClassName)`. Each tool has exactly one declaring class, guarded by
  `McpServerStatusToolListingTests.EveryToolName_IsDeclaredByExactlyOneClass`.
- **Nothing guarantees the tools assemblies are loaded** before discovery runs. `ChainEvalToolSurface.cs:~104-114`
  documents this hazard and loads them explicitly.
- **The runtime groups are an enum.** `enum ToolSetName` and `ToolsetCatalog` live in
  `RoslynSentinel.Common/ToolsetCatalog.cs`. `ToolSetName` has 117 references in 14 files and
  `ToolsetCatalog` has 27 references in 11 files.
- **HTTP runs stateless, and a runtime enable is lost there.** `ServerHttp.cs:91` sets `Stateless = true`,
  which newer MCP protocol versions need; commit `7595cff6` restored it. The pinned SDK v2.4.0 was traced
  on 2026-11-23:
  - `StreamableHttpHandler.cs:585-589` builds fresh `McpServerOptions` per stateless request through
    `IOptionsFactory`.
  - `McpServerOptionsSetup.cs:30-39` fills that copy's `ToolCollection` from the DI-registered tools only.
  - So `ToolsetService.SetEnabled`'s change to the singleton collection (`ToolsetService.cs:140`) is never
    read, and the next call fails "Unknown tool", the symptom `9e88c086` recorded.
  - `McpServerImpl.cs:117-121` sends `tools/list_changed` only with a stateful transport.
  - Tools registered in DI at startup do reach every request, which is why folding works.
- **The owner's config gets less than it asks for.** `--mode=claude-lean,subagenteval` (the owner's
  `~/.mcp.json`) does not serve `SubAgentEval`: the live `McpServerStatus` reports it inactive with
  "not in the claude-lean core toolset".
- **The eval lean list count disagrees.** `AgentToolAllowLists.Lean` holds 19 names.
  `FilteringToolHostTests.cs:95` asserts 18 (looks red, not run), while
  `ChainEvalToolSurfaceLeanTests.LeanAllowList_HasExactly19DistinctNames_AllDiscovered` asserts 19.

## Decision

The decisions come from the proposal plus the owner rulings of 2026-11-23:

- **One surface source per launch.** Exactly one of `--toolset=<name>` (a shipped file),
  `--toolset-file=<path>`, or `--tools=<a,b>` (inline, meaning exactly these). More than one fails
  startup.
- **Names.** Entries are tool names. A class name is accepted as shorthand for all of that class's tools,
  in `tools`, `classes`, `exclude` and `--tools`.
- **The wildcard.** `"*"` in `classes` means the classes that `all` expands to today: every
  `ModeToToolClasses` entry except `ExclusiveModes` and `OptInModes`. Deriving it from the registry keeps
  adding a tool a three-place change.
- **Composition.** `extends` is resolved depth first; a cycle is an error. The core set is the union of
  `tools` and `classes` over the whole chain. Then the union of every `exclude` list in the chain is
  removed from both the core set and the groups. A group left empty is dropped.
- **Always-on tools.** `McpServerStatus` is always in the core unless excluded. `McpToolsetControl` is
  added to the core if and only if the resolved surface has groups and runtime switching is supported.
- **`onDemand` groups** (owner, 2026-11-23). Each group is
  `{"description": "<one line>", "tools": [...]}`, with no `enabledAtStart` field.
  `McpToolsetControl`'s `toolSet` enum and description are generated from the active groups. Groups are
  enabled at start by `--toolsets` and by the per-tab saved state, unchanged.
- **HTTP folds groups at startup** (owner, 2026-11-23). Stateless HTTP has no per-request toolset state.
  `ServerHttp` resolves with runtime switching off: groups named in `--toolsets` become core tools,
  `McpToolsetControl` and `ToolsetService` are not registered, and `McpServerStatus` says so. Runtime
  switching over HTTP is a follow-up, not this plan.
- **Validation.** An unknown name on the new flags fails startup with closest-match hints from
  `NameCandidates` (`RoslynSentinel.Common/NameCandidates.cs:8`) and exit code 2, the existing code from
  `HandleNoActiveTools`.
- **Old flags** (owner, 2026-11-23). The last step removes `--include-tools`, `--exclude-tools` and
  `--hide-tools`. `--mode` stays as a warned alias: a mode with a shipped file loads that file; any other
  mode, or a list, builds an unnamed union from `ModeToToolClasses`. If a list includes `claude-lean`,
  it loads `claude-lean.json` and warns that the other modes add no tools, which is today's behaviour.
- **Where things live.** The schema, loader and resolver are in `RoslynSentinel.Common/ToolSets/`. The
  shipped files are embedded resources under `RoslynSentinel.Common/ToolSets/Shipped/`. Tool discovery is
  in `Server.Advanced`, behind an interface in Common.
- **Eval surfaces are gated by the server.** `AgentToolAllowLists` and `FilteringToolHost` are deleted.
  The loop guard needs no change: `ModelAgentRunner.ConsecutiveUnavailableToolLimit` (`ModelAgentRunner.cs:51`)
  keys on names missing from the host's `ListToolsAsync` result (`:237-238`, `:600-618`), so it also
  fires on tools the server never served.
- **Snapshots prove each surface.** Every shipped toolset's resolved tool names are pinned under
  `docs/generated/toolsets/<name>.txt`. The current mode surfaces are pinned first (Step 1), so every
  later step must show a zero diff.

**Shipped files.** These are the modes with live callers (survey of 2026-11-23), plus the composite
surfaces:

| Shipped file | Source |
| --- | --- |
| `all`, `claude-lean`, `claude`, `workspace`, `refactor` | mode of the same name |
| `wholefilewrite`, `editsnippetsexperiment`, `subagenteval`, `subagent` | mode of the same name |
| `experimental`, `generation`, `admin`, `intelligence`, `asyncify` | mode of the same name |
| `symbolnavigation`, `testcategories` | mode of the same name |
| `subagent-child` | `SubAgentChildServerLauncher` today |
| `eval-lean` | `AgentToolAllowLists.Lean`, without `McpServerStatus` |
| `eval-full` | the ModelEval `full` surface |
| `planstep-runner` | extends `eval-lean`, adds `GetDiagnostics` |

The modes with no caller are reachable as `--tools=<ClassName>` and stay reachable through the `--mode`
alias until it goes: Modernize, Quality, NamedArguments, QualifyCrefs, SemanticFindReplace,
WorkspaceReadNav, RefactorSignature, RefactoringStructural, RefactorExtractionDocs, SymbolRelationship,
and the `Toolset1` alias.

## Execution rules

- **Builds and tests.** `Build` 0 errors per slice. The test baseline is 0 failures, so any red test is
  new. Run one `Build` or `RunTest` at a time, and have at most two implementing agents at once.
- **Slice order.** Steps 1-8 are text-neutral: no wire, schema or startup change. Run them first.
  Steps 9-23 change behaviour and have to wait for Step 7's parity proof.
- **Migration first.** Add the new member beside the old one, mark the old one `[Obsolete("Use X")]`, and
  move callers in slices. CS0618 warnings during the migration are expected.
- **DTOs.** Each new record carries the `NEW-DTO:` block below in its creating call's `reason`. None of
  them is a tool output, so the wire impact is none, except Step 21 (`McpServerStatus` fields).
- **Live checks.** A server-source change needs a commit, then a restart of the dispatcher's own server
  before any live check (CLAUDE.md, Verification).
- **Snapshot files.** Regenerate them only with `ROSLYNSENTINEL_UPDATE_GENERATED_DOCS=1`, and only in
  Step 1 or when a step says the surface changes on purpose. A snapshot diff anywhere else is a defect.

## Steps

### Phase A - safety net and contract (text-neutral)

### Step 1 - Pin today's surfaces as generated snapshots
- Files: `RoslynSentinel.Tests.Server/ToolsetSurfaceSnapshotTests.cs` (new),
  `RoslynSentinel.Tests.Server/ArchitectureDocFreshnessTests.cs`, `scripts/Generate-ArchitectureMap.ps1`;
  output `docs/generated/toolsets/*.txt` (new, written by the regenerate run).
- Depends on: none.
- Effect: text-neutral (tests and generated files only).
- Change:
  - **Share the freshness helpers.** In `ArchitectureDocFreshnessTests`, change `RegenerateWhenRequested`
    (`:94`) and `AssertCommittedFileIsFresh` (`:107`) from `private static` to `internal static`.
    `RegenerateWhenRequested` must also create the subfolder of a relative `fileName` such as
    `toolsets/all.txt`.
  - **Build each surface.** For each surface below, build an in-process host the way
    `McpToolsetControlTests.BuildHost` (`McpToolsetControlTests.cs:319`) does. Collect the served tool
    names, sorted with Ordinal comparison, one per line. For claude-lean, add one `[onDemand <set>]`
    block per `ToolSetName`, listing `ToolsetCatalog.GetToolNames(set)` sorted.
  - **Pin it.** Each surface gets one regenerate test and one `[Category("Ratchet")]` freshness test,
    the same pair `ArchitectureDocFreshnessTests` has (`:151`/`:157`).
  - **The surfaces:** the 16 modes in the Decision table, plus:
    - `claude-lean-subagenteval`: mode `claude-lean,subagenteval`;
    - `subagent-child`: the three values of `SubAgentChildServerLauncher.BuildServerArguments`
      (`SubAgentChildServerLauncher.cs:50`), passed as mode, exclude and hide.
  - **Not here:** the eval and PlanStepRunner surfaces stay pinned by their own tests
    (`ChainEvalToolSurfaceLeanTests`, `PlanStepRunnerSurfaceTests`).
  - **Script:** add `|FullyQualifiedName~ToolsetSurfaceSnapshot` to the test filter in
    `Generate-ArchitectureMap.ps1`.
- Done when:
  - `Build` 0 errors.
  - The dispatcher runs `RunTest(scope: project, scopeName: RoslynSentinel.Tests.Server, filter: "FullyQualifiedName~ToolsetSurfaceSnapshot")`
    once with `environment: {ROSLYNSENTINEL_UPDATE_GENERATED_DOCS: "1"}` and once without. The second
    run is all green.
  - `claude-lean.txt` has 28 core names.
- Lane: `test-writer`.

### Step 2 - Contract skeleton in Common
- Files: `RoslynSentinel.Common/ToolSets/ToolsetModels.cs` (new),
  `RoslynSentinel.Common/ToolSets/ToolsetFileLoader.cs` (new),
  `RoslynSentinel.Common/ToolSets/ToolsetResolver.cs` (new).
- Depends on: none (pairs with Step 1: disjoint project).
- Effect: text-neutral (new types, no callers).
- Change: three `CreateFile` calls, models first. Namespace `RoslynSentinel.Common.ToolSets`. Every method
  body is `throw new NotImplementedException(); // TODO implement (Step 3)`, or `(Step 4)` for the resolver.
  ```csharp
  // ToolsetModels.cs
  public sealed record ToolsetDefinition(string Name, string? Description, IReadOnlyList<string> Extends,
      IReadOnlyList<string> Classes, IReadOnlyList<string> Tools, IReadOnlyList<string> Exclude,
      IReadOnlyDictionary<string, OnDemandGroupDefinition> OnDemand);
  public sealed record OnDemandGroupDefinition(string Description, IReadOnlyList<string> Tools);
  public enum ToolSurfaceSource { Shipped, File, Inline, ModeAlias }
  public sealed record ResolvedOnDemandGroup(string Name, string Description, IReadOnlyList<string> ToolNames);
  public sealed record ToolsetResolveResult(string ToolsetName, ToolSurfaceSource Source,
      IReadOnlySet<string> CoreToolNames, IReadOnlySet<string> ActiveToolClasses,
      IReadOnlyList<ResolvedOnDemandGroup> OnDemandGroups, IReadOnlyList<string> FoldedGroups);
  public interface IDeclaredToolCatalog
  {
      IReadOnlyDictionary<string, string> ClassByTool { get; }   // tool name -> declaring class
      IReadOnlySet<string> WildcardClasses { get; }              // what "*" expands to
      IReadOnlySet<string> AlwaysOnTools { get; }                // McpServerStatus
      string ToolsetControlToolName { get; }                     // McpToolsetControl
  }
  public sealed class ToolsetValidationException(IReadOnlyList<string> errors)
      : Exception(string.Join(Environment.NewLine, errors))
  { public IReadOnlyList<string> Errors { get; } = errors; }

  // ToolsetFileLoader.cs
  public static class ToolsetFileLoader
  {
      public const string ResourcePrefix = "RoslynSentinel.Common.ToolSets.Shipped.";
      public static IReadOnlyList<string> ShippedNames { get; }
      public static ToolsetDefinition LoadShipped(string name);
      public static ToolsetDefinition LoadFile(string path);
      public static ToolsetDefinition Parse(string json, string sourceLabel);
      public static ToolsetDefinition FromInlineList(string commaSeparatedNames);
  }

  // ToolsetResolver.cs
  public static class ToolsetResolver
  {
      public static ToolsetResolveResult Resolve(ToolsetDefinition root, ToolSurfaceSource source,
          Func<string, ToolsetDefinition> loadByName, IDeclaredToolCatalog catalog,
          IReadOnlyCollection<string> startupGroups, bool runtimeGroupsSupported);
  }
  ```
  NEW-DTO justifications, one per creating call:
  - `NEW-DTO: ToolsetDefinition (Request, Common.ToolSets)`. Evaluated: `ActiveToolSurface` (a resolved
    snapshot of mode, include and exclude, with no extends or groups); `TabToolsetState` (persisted
    enabled-set names only). Wire impact: none.
  - `NEW-DTO: OnDemandGroupDefinition (Item, Common.ToolSets)`. Evaluated: the private
    `ToolsetCatalog.ToolsBySet` and `Summaries` dictionaries (code, not data; enum-keyed). Wire impact:
    none.
  - `NEW-DTO: ResolvedOnDemandGroup (Item, Common.ToolSets)`. Evaluated: `OnDemandGroupDefinition` (holds
    unresolved names that may be classes; the resolved form holds tool names only). Wire impact: none.
  - `NEW-DTO: ToolsetResolveResult (Result, Common.ToolSets)`. Evaluated: `ActiveToolSurface` (no group
    or source fields, and built after registration; Step 21 extends it from this result); `ToolAllowList`
    (a name set only). It is not an MCP operation result, so `IOperationResult` is not implemented
    (startup-only, never on the wire). Wire impact: none.
- Done when: `Build` 0 errors.
- Lane: `implementer` (D1: 3 files, 3 writes).

### Step 3 - Loader implementation and tests
- Files: `RoslynSentinel.Common/ToolSets/ToolsetFileLoader.cs`, `RoslynSentinel.Common/RoslynSentinel.Common.csproj`,
  `RoslynSentinel.Tests.Server/ToolsetFileLoaderTests.cs` (new; `test-writer` half).
- Depends on: 2.
- Effect: text-neutral.
- Change:
  - **csproj:** add `<EmbeddedResource Include="ToolSets\Shipped\*.json" LogicalName="RoslynSentinel.Common.ToolSets.Shipped.%(Filename)%(Extension)" />`.
  - **Parsing:** reflection-based `System.Text.Json` with camelCase, case-insensitive names, comments and
    trailing commas allowed. Missing arrays and objects become empty. The same style as
    `TabToolsetStateStore.JsonOptions` (`TabToolsetState.cs:19-23`).
  - **`LoadShipped`:** a case-insensitive name lookup against `ShippedNames`, which come from the resource
    names. An unknown name throws `ToolsetValidationException` with `NameCandidates` closest matches.
  - **`LoadFile`:** a missing file or invalid JSON throws `ToolsetValidationException` naming the path and
    the JSON error line, with no stack trace.
  - **`FromInlineList`:** splits on commas, trims and drops empty entries. The result is named `inline`
    and every entry goes to `Tools`. An empty list throws.
- Done when:
  - `Build` 0 errors.
  - The dispatcher runs `ToolsetFileLoaderTests`:
    - `Parse_MinimalFile_DefaultsEmptyCollections`
    - `Parse_InvalidJson_ThrowsWithLine`
    - `LoadShipped_UnknownName_SuggestsClosest`
    - `FromInlineList_TrimsAndDropsEmpty`
- Lane: `implementer` (D1) for the loader and csproj, then `test-writer`.

### Step 4 - Resolver implementation and tests
- Files: `RoslynSentinel.Common/ToolSets/ToolsetResolver.cs`, `RoslynSentinel.Tests.Server/ToolsetResolverTests.cs`
  (new; `test-writer` half).
- Depends on: 2 (pairs with Step 3: disjoint files).
- Effect: text-neutral.
- Change: implement the resolution in the Decision, in this order:
  1. Walk `extends`, detecting cycles.
  2. Expand names. A tool name is the tool; a class name means all its tools, via `catalog.ClassByTool`;
     `*` is valid only in `classes`.
  3. Union the core set. Apply the union of the `exclude` lists. Drop empty groups. A tool both in the
     core and in a group stays core only.
  4. Add `AlwaysOnTools` unless excluded.
  5. If `runtimeGroupsSupported` and groups exist, add `ToolsetControlToolName`. If runtime switching is
     not supported, move the tools of each group named in `startupGroups` into the core, list those
     groups in `FoldedGroups`, and return no groups.
  6. `ActiveToolClasses` is every class owning a core or group tool.
  7. Collect every unknown tool, class, toolset or group name (including `startupGroups`) and throw one
     `ToolsetValidationException` with `NameCandidates` hints.

  Tests use a fake `IDeclaredToolCatalog`.
- Done when:
  - `Build` 0 errors.
  - The dispatcher runs `ToolsetResolverTests`:
    - `ClassShorthand_ExpandsToItsTools`
    - `Wildcard_UsesCatalogWildcardClasses`
    - `Extends_UnionsAndExcludeAppliesAcrossChain`
    - `Cycle_Throws`
    - `UnknownNames_AllReportedWithHints`
    - `AlwaysOn_AddedUnlessExcluded`
    - `Groups_AddControlTool_WhenRuntimeSupported`
    - `StartupGroups_FoldIntoCore_WhenRuntimeUnsupported`
    - `EmptyGroupAfterExclude_IsDropped`
- Lane: `implementer` (D1), then `test-writer`.

### Step 5 - Declared tool catalog in Server.Advanced
- Files: `RoslynSentinel.Server.Advanced/DeclaredToolCatalog.cs` (new),
  `RoslynSentinel.Tests.Server/DeclaredToolCatalogTests.cs` (new; `test-writer` half).
- Depends on: 2.
- Effect: text-neutral.
- Change: `public sealed class DeclaredToolCatalog : IDeclaredToolCatalog`, with a static
  `Instance` built lazily:
  - **Load the assemblies first.** Before discovery, call `Assembly.Load` on every referenced assembly of
    `typeof(ToolClassRegistry).Assembly` whose name starts with `RoslynSentinel.Tools.`, as
    `ChainEvalToolSurface.LoadToolAssemblies` (`ChainEvalToolSurface.cs:106`) does. This closes the
    assembly-timing hazard.
  - **`ClassByTool`:** from `McpToolSchemaPatcher.DiscoverAllDeclaredTools`.
  - **`WildcardClasses`:** the union of `ModeToToolClasses` values for modes not in
    `ToolClassRegistry.ExclusiveModes` or `OptInModes`. This is the `ParseArgs` `all` expansion
    (`ServerStartupHelpers.cs:74-90`); share one helper rather than copying it.
  - **The rest:** `AlwaysOnTools = {"McpServerStatus"}` and `ToolsetControlToolName = ToolsetCatalog.ControlToolName`
    (a `// TODO` notes it becomes a literal at Step 18).
- Done when:
  - `Build` 0 errors.
  - The dispatcher runs `DeclaredToolCatalogTests.ClassByTool_CoversEveryDeclaredTool_AndWildcardMatchesAllMode`.
- Lane: `implementer` (D1), then `test-writer`.

### Step 6 - Author the shipped toolset files
- Files: `RoslynSentinel.Common/ToolSets/Shipped/*.json` (20 new; split as below).
- Depends on: 3 (the csproj glob).
- Effect: text-neutral (embedded data, no reader yet).
- Change: one file per row of the Decision table. Each mode file is
  `{"name": "<file name>", "classes": [<ModeToToolClasses entry for the mode, verbatim>]}`, except as
  follows:
  - **`all.json`:** `{"name": "all", "classes": ["*"]}`.
  - **`claude-lean.json`:** `tools` = the 28 `ClaudeLeanToolNames` (`ToolClassRegistry.cs:49`) minus
    `McpToolsetControl` (the resolver adds it). `onDemand` has one group per `ToolSetName`; `tools` comes
    from `ToolsetCatalog.ToolsBySet` (`ToolsetCatalog.cs:33`) and `description` from
    `ToolsetCatalog.Summaries` (`:73`).
  - **`subagent-child.json`:** `{"extends": ["claude-lean"], "exclude": ["SubAgentTools", "SubAgentEvalTools", "WriteFile"]}`.
  - **`eval-lean.json`:** `tools` = the 19 names of `AgentToolAllowLists.Lean`, with
    `"exclude": ["McpServerStatus"]`.
  - **`eval-full.json`:** `classes` = the `Refactor`, `Workspace` and `WholeFileWrite` classes, with
    `exclude` = `ChainEvalToolSurface.NewFullSurfaceHiddenTools()` (11 names, `ChainEvalToolSurface.cs:45`).
  - **`planstep-runner.json`:** `{"extends": ["eval-lean"], "tools": ["GetDiagnostics"]}`.

  Split into three disjoint slices:
  - 6a: `all` plus 7 mode files.
  - 6b: the remaining 8 mode files.
  - 6c: `claude-lean` plus the 4 composite files.
- Done when: `Build` 0 errors. `ToolsetFileLoader.ShippedNames` has 20 entries; Step 7 is the real check.
- Lane: `implementer` x3 (non-C# `Write` calls; 6a and 6b pair).

### Step 7 - Parity proof: resolved files equal today's surfaces
- Files: `RoslynSentinel.Tests.Server/ToolsetSurfaceSnapshotTests.cs`.
- Depends on: 1, 4, 5, 6.
- Effect: text-neutral.
- Change: add a second generator per Step-1 surface. It resolves the matching shipped file with
  `ToolsetResolver.Resolve(..., DeclaredToolCatalog.Instance, startupGroups: [], runtimeGroupsSupported: true)`,
  renders the same text format, and asserts it equals the committed snapshot. The
  `claude-lean-subagenteval` surface resolves `claude-lean`. Add tests for the eval surfaces against
  their existing oracles:
  - `eval-lean` resolves to exactly `ChainEvalToolSurface.LeanAllowList` (19 names).
  - `planstep-runner` resolves to exactly `AgentToolAllowLists.PlanStepRunnerSurface`.

  The eval comparisons need `Tests.Server` to see AgentLoop. If it cannot, put those two tests in
  `RoslynSentinel.Tests.ModelEval/ChainEvalToolSurfaceLeanTests.cs`.
- Done when: `Build` 0 errors. The dispatcher runs `FullyQualifiedName~ToolsetSurfaceSnapshot` and it is
  all green, with no snapshot regeneration. A mismatch is fixed in the JSON file, never in the snapshot.
- Lane: `test-writer`.

### Step 8 - Registration overload that takes a resolved surface
- Files: `RoslynSentinel.Server.Advanced/ServiceRegistrationExtensionsBasic.cs`,
  `RoslynSentinel.Server.Advanced/ServiceRegistrationExtensionsAdvanced.cs`.
- Depends on: 4.
- Effect: text-neutral (new overloads; the old ones forward to them and produce the same surface).
- Change:
  - **New overloads** beside `AddRoslynSentinelToolsBasic` (`ExtBasic:43`) and
    `AddRoslynSentinelToolsAdvanced` (`ExtAdvanced:50`):
    `public static IMcpServerBuilder AddRoslynSentinelToolsBasic(this IMcpServerBuilder mcpBuilder, IServiceCollection services, ToolsetResolveResult surface)`,
    and the same for Advanced.
  - **Inside the new path:**
    - `activeToolClasses = surface.ActiveToolClasses`.
    - The allow list is always `new ToolAllowList(surface.CoreToolNames)`, registered before the first
      `WithSentinelTools`; this replaces the block at `ExtBasic:286-295`.
    - Group classes are registered (they are already in `ActiveToolClasses`).
    - `ToolsetService` is registered when `surface.OnDemandGroups.Count > 0`. Until Step 13 it is still
      built from `ClaudeLeanOnDemandToolClasses`, with a `// TODO` there.
  - **Old overloads** build a `ToolsetResolveResult` through `LegacyModeToolset.Build` (Step 9) and call
    the new one. Mark them `[Obsolete("Use the ToolsetResolveResult overload")]`.

  Until Step 9 lands, the old overloads keep their bodies, and this step adds only the new overloads plus
  a shared private core. Split if needed: 8a Basic, then 8b Advanced.
- Done when: `Build` 0 errors. The dispatcher runs `FullyQualifiedName~ToolsetSurfaceSnapshot|FullyQualifiedName~ClaudeLeanModeTests|FullyQualifiedName~McpToolsetControlTests`
  and it is all green.
- Lane: `implementer-senior` (20+ registration blocks, ordering constraint on the allow list).

### Phase B - startup wiring (behaviour-changing)

### Step 9 - Startup resolves through the toolset path; new flags; `--mode` alias
- Files: `RoslynSentinel.Server.Advanced/ToolSurfaceArgs.cs` (new; holds `ResolveFromArgs` and
  `LegacyModeToolset`), `RoslynSentinel.Server.Advanced/ServerStdio.cs`, `RoslynSentinel.Server.Advanced/ServerHttp.cs`.
- Depends on: 5, 7, 8.
- Effect: behaviour-changing. `--toolset`, `--toolset-file` and `--tools` exist. Using more than one, or
  an unknown name, fails with exit code 2. `--mode`, `--include-tools`, `--exclude-tools` and
  `--hide-tools` print a deprecation warning and resolve as today. On HTTP, `--toolsets` folds groups
  into the core.
- Change:
  - **`ResolveFromArgs`:**
    `public static ToolsetResolveResult ResolveFromArgs(string[] args, IDeclaredToolCatalog catalog, bool runtimeGroupsSupported, Action<string> warn)`.
    The startup groups come from the existing `--toolsets` parsing.
  - **`LegacyModeToolset.Build`:**
    `public static ToolsetResolveResult Build(HashSet<string> activeModes, HashSet<string> includeTools, HashSet<string> excludeTools, IReadOnlySet<string> hideTools, IDeclaredToolCatalog catalog, bool runtimeGroupsSupported)`.
    It reproduces today's semantics:
    - a single mode with a shipped file and no include, exclude or hide loads that file;
    - `claude-lean` among the modes loads `claude-lean.json`, applies `hide` as `exclude`, and warns that
      the other modes add no tools;
    - otherwise it builds an unnamed `ToolsetDefinition` with the classes from `ResolveActiveToolClasses`
      and `exclude` = `hide`.
  - **Wiring:** `ServerStdio.cs:41` and `ServerHttp.cs:26` call `ResolveFromArgs` (HTTP with
    `runtimeGroupsSupported: false`) and pass the result to the new registration overloads.
    `HandleNoActiveTools`, `HandleListTools` (`ServerStartupHelpers.cs:460`) and `ConsoleMode.WriteStartupDump`
    (it uses the toolset name in the file name) take the resolved surface.
  - **Exit code:** catch `ToolsetValidationException`, write its `Errors` to stderr, and set
    `Environment.ExitCode = 2`.
  - **HTTP follow-up:** runtime groups over stateless HTTP are covered by
    `plan_stateless_http_toolset_enablement.md` (Out of scope); no INBOX line is needed.
- Done when:
  - `Build` 0 errors.
  - The dispatcher runs Tests.Server `FullyQualifiedName~ToolsetSurfaceSnapshot|FullyQualifiedName~NoActiveToolsStartupTests|FullyQualifiedName~ClaudeLeanModeTests|FullyQualifiedName~ToolClassNameResolutionTests|FullyQualifiedName~ExperimentalModeTests`.
    A message-text assertion that changes because of the warnings is updated in this step.
- Lane: `implementer-senior`.

### Step 10 - Startup and flag tests
- Files: `RoslynSentinel.Tests.Server/ToolSurfaceArgsTests.cs` (new).
- Depends on: 9.
- Effect: text-neutral.
- Change: tests for
  - `--tools=ReadFile,GetFileOutline`, which serves exactly those two tools plus `McpServerStatus`;
  - `--tools=ReadFiel`, which fails with "ReadFile" suggested;
  - two surface flags at once, which fails;
  - `--toolset-file` with `extends` and `exclude`;
  - `--mode=claude-lean,subagenteval`, which warns and serves the `claude-lean` surface;
  - HTTP `--toolsets=git` with `runtimeGroupsSupported: false`, which puts `GitRemote` in the core with no
    `McpToolsetControl`;
  - an unknown `--toolsets` group, which fails.
- Done when: `Build` 0 errors. The dispatcher runs `ToolSurfaceArgsTests` and it is all green.
- Lane: `test-writer` (pairs with Step 11).

### Step 11 - Snapshot tests switch to the real startup path
- Files: `RoslynSentinel.Tests.Server/ToolsetSurfaceSnapshotTests.cs`.
- Depends on: 9.
- Effect: text-neutral.
- Change: the Step-1 mode-path generators build their hosts with `ResolveFromArgs(["--mode=<m>", ...])`
  plus the new overload, and add a `--toolset=<name>` variant per shipped file. All of them compare
  against the same committed snapshots.
- Done when: `Build` 0 errors. The `FullyQualifiedName~ToolsetSurfaceSnapshot` run is all green with no
  regeneration.
- Lane: `test-writer`.

### Phase C - groups replace `ToolSetName` and `ToolsetCatalog`

### Step 12 - String-group API on ToolsetService and TabToolsetStateStore
- Files: `RoslynSentinel.Common/ToolsetService.cs`, `RoslynSentinel.Common/TabToolsetState.cs`.
- Depends on: 2.
- Effect: text-neutral (new members beside the enum ones).
- Change:
  - **`ToolsetService`:**
    - a ctor overload
      `ToolsetService(IOptions<McpServerOptions> serverOptions, IServiceProvider services, IReadOnlyList<ResolvedOnDemandGroup> groups, Func<IEnumerable<Assembly>>? assemblySource = null)`;
    - `public IReadOnlyList<string> EnabledGroups`;
    - `public ToolsetChange SetEnabled(string group, bool enabled)`;
    - `public event Action<IReadOnlyList<string>>? EnabledGroupsChanged`;
    - `public IReadOnlyList<ResolvedOnDemandGroup> Groups`.

    The enum members (`SetEnabled(ToolSetName, bool)` at `ToolsetService.cs:133`, `EnabledToolSets`,
    `EnabledToolSetsChanged`) forward to the new ones and become `[Obsolete]`. Candidate resolution uses
    the group's `ToolNames` instead of `ToolsetCatalog.AllToolNames`.
  - **`TabToolsetStateStore`:** add `Save(IReadOnlyList<string> enabled)` and
    `IReadOnlyList<string> ConfiguredGroups`. The file format is unchanged, because it already stores the
    names as strings (`Save` writes `s.ToString()`).
- Done when:
  - `Build` 0 errors (CS0618 expected).
  - The dispatcher runs `McpToolsetControlTests`, `StartupToolsetRestoreTests` and
    `TabToolsetStateStoreTests`, all green.
- Lane: `implementer` (D1).

### Step 13 - Groups from the resolved surface, in any toolset
- Files: `RoslynSentinel.Server.Advanced/ServiceRegistrationExtensionsBasic.cs`,
  `RoslynSentinel.Server.Advanced/ServerStartupHelpers.cs`.
- Depends on: 9, 12.
- Effect: behaviour-changing. Any toolset with groups gets `McpToolsetControl`. On HTTP the server has no
  `ToolsetService`, so `McpToolsetControl` is gone there; today it is offered there and does nothing.
- Change:
  - **Registration:** register `ToolsetService` with the group ctor from `surface.OnDemandGroups`,
    replacing the claude-lean-only block at `ExtBasic:334-386`. `TabToolsetStateStore` stays conditional
    on `TabStateDir` (`:359-366`).
  - **Startup groups:** `ApplyStartupToolsets` (`ServerStartupHelpers.cs:149`) and `ParseToolsets`
    (`:120`) work on group names. Validation already happened in `ResolveFromArgs`. Remove the warning
    "toolsets exist only in --mode claude-lean" (`:202`).
- Done when:
  - `Build` 0 errors.
  - The dispatcher runs `McpToolsetControlTests`, `StartupToolsetRestoreTests` and
    `FullyQualifiedName~ToolsetSurfaceSnapshot`. Tests that assert the claude-lean-only warning, such as
    `ApplyStartupToolsets_OutsideClaudeLean_WarnsThatToolsetsNeedClaudeLean` (`:664`), are updated to the
    new facts in this step.
- Lane: `implementer-senior`.

### Step 14 - McpToolsetControl: a string parameter with a generated enum and description
- Files: `RoslynSentinel.Tools.Basic/ToolsetControlTools.cs`, `RoslynSentinel.Common/McpToolSchemaPatcher.cs`,
  `RoslynSentinel.Tests.Server/McpToolsetControlTests.cs`.
- Depends on: 13.
- Effect: behaviour-changing (tool schema). `toolSet` becomes a string whose JSON-schema `enum` lists the
  active groups. The description is generated as "`<group>` = `<description>` (`<tools>`)" per group, plus
  the existing fixed sentences.
- Change:
  - **Signature:** `McpToolsetControlTool(... string toolSet ...)` replaces `ToolSetName toolSet`
    (`ToolsetControlTools.cs:22`). The tool calls `ToolsetService.SetEnabled(string, bool)`. An unknown
    group returns `InvalidArgument` listing the groups.
  - **Schema:** at registration, `McpToolSchemaPatcher` injects the enum and the description for the
    `McpToolsetControl` tool from the registered `ToolsetService.Groups`. It is the same post-processing
    path that writes `x-consumes-tag` into a property object (`ApplyConsumesTags`, `McpToolSchemaPatcher.cs:450`).
    Verify first that the tool's `ProtocolTool.Description` and `InputSchema` can be set at that point;
    see Risks R1.
  - **Tests:** replace `Catalog_EveryToolIsDeclared_AndTheDescriptionNamesEverySetAndTool` (`:267`) and
    `Catalog_EverySetIsRegisteredAndDescribed` (`:253`) with
    `GeneratedDescription_NamesEveryActiveGroupAndTool` and `GeneratedEnum_EqualsActiveGroups`.
- Done when:
  - `Build` 0 errors.
  - The dispatcher runs `McpToolsetControlTests` and `ClaudeLeanSchemaBudgetTests`. If the budget is
    exceeded, trim the generated text before raising the budget.
  - `scripts/Measure-ToolSchemaCost.ps1` before and after is recorded in the commit message.
- Lane: `implementer-senior`.

### Step 15 - Move production callers off ToolSetName and ToolsetCatalog (Server.Advanced)
- Files: `RoslynSentinel.Server.Advanced/CallToolFailureHandler.cs`, `RoslynSentinel.Server.Advanced/ToolClassRegistry.cs`,
  `RoslynSentinel.Server.Advanced/ServerStartupHelpers.cs`.
- Depends on: 13.
- Effect: text-neutral. `UnknownToolError` gives the same text, but finds the group through the active
  `ToolsetService.Groups` instead of `ToolsetCatalog.FindSet`.
- Change:
  - **`UnknownToolError`** (`CallToolFailureHandler.cs:91`, `:103`) resolves the group from the services
    of the call context.
  - **`ToolClassRegistry`'s 2 `ToolsetCatalog` references** (the `ControlToolName` in `ClaudeLeanToolNames`)
    are replaced by the literal.
  - **The remaining `ToolSetName` uses in `ServerStartupHelpers`** become strings.
- Done when: `Build` 0 errors. In these files, `Search(mode: text, query: "ToolSetName|ToolsetCatalog")`
  returns nothing.
- Lane: `implementer` (D1; pairs with Step 16).

### Step 16 - Move production callers off ToolSetName and ToolsetCatalog (Tools.Basic, Engines.Basic)
- Files: `RoslynSentinel.Tools.Basic/AdminTools.cs`, `RoslynSentinel.Tools.Basic/ServerStatusTools.cs`,
  `RoslynSentinel.Engines.Basic/TestInstanceLauncher.cs`.
- Depends on: 12.
- Effect: text-neutral (same facts; group names were already the enum's names).
- Change:
  - **`AdminTools` StopServer** (`:583-600`, `ApplyStopToolsets` `:713`) parses group names against
    `ToolsetService.Groups`.
  - **`ServerStatusTools.DescribeHowToEnable`** (`:193`) uses `ToolsetService.Groups` instead of
    `ToolsetCatalog.FindSet`.
  - **`TestInstanceLauncher.ValidateToolSelection`** (`:549`) checks toolsets as non-empty identifiers.
    The child server validates them against its own toolset and fails startup with exit code 2.
- Done when: `Build` 0 errors. In these files, `Search(mode: text, query: "ToolSetName|ToolsetCatalog")`
  returns nothing.
- Lane: `implementer` (D1).

### Step 17 - Test sweep off ToolSetName and ToolsetCatalog
- Files: `McpToolsetControlTests.cs` (42 refs), `StartupToolsetRestoreTests.cs`, `ClaudeLeanModeTests.cs`,
  `TabToolsetStateStoreTests.cs`, `ArchitectureDocGenerator.cs` (Tests.Server); `AdminToolsTests.cs`,
  `McpServerStatusToolsetCountTests.cs`, `TestCategoryTaggingToolTests.cs` (Tests.Tools.Basic).
- Depends on: 14, 15, 16.
- Effect: text-neutral.
- Change: replace enum members with group-name strings and `ToolsetCatalog` lookups with the
  `claude-lean.json` groups, through `ToolsetFileLoader.LoadShipped("claude-lean")`.
  `ArchitectureDocGenerator`'s "claude-lean (on demand)" column reads the same file.
  `ClaudeLeanModeTests.CoreTools` (`:34`) becomes a read of the `claude-lean` snapshot file. Split by
  project: 17a Tests.Server, 17b Tests.Tools.Basic.
- Done when:
  - `Build` 0 errors.
  - `Search(mode: text, query: "ToolSetName|ToolsetCatalog", includeTests: true)` lists only
    `ToolsetCatalog.cs`.
  - The dispatcher runs both projects' affected fixtures, plus `ArchitectureDocFreshnessTests` with no
    regeneration.
- Lane: `test-writer` x2.

### Step 18 - Delete ToolSetName, ToolsetCatalog and the claude-lean lists
- Files: `RoslynSentinel.Common/ToolsetCatalog.cs` (delete via `DeleteFile`), `RoslynSentinel.Server.Advanced/ToolClassRegistry.cs`,
  `RoslynSentinel.Common/ToolsetService.cs`.
- Depends on: 17.
- Effect: text-neutral.
- Change:
  - **Remove the claude-lean lists:** `ClaudeLeanToolNames`, `ClaudeLeanToolClasses` and
    `ClaudeLeanOnDemandToolClasses` from `ToolClassRegistry`. `claude-lean.json` is now the definition.
  - **The `claude-lean` mode entry** in `ModeToToolClasses` is kept for the alias's `ExclusiveModes` check
    only, with a `// TODO` to remove it with `--mode`.
  - **Remove the `[Obsolete]` enum members** from `ToolsetService` and `TabToolsetStateStore`, and the
    Step 5 `// TODO`.
- Done when: `Build` 0 errors and the full Tests.Server run passes.
- Lane: `implementer` (D1).

### Phase D - consumers

### Step 19 - Subagent child and PlanStepRunner launch with a toolset
- Files: `RoslynSentinel.Tools.Advanced/SubAgentChildServerLauncher.cs`, `RoslynSentinel.Utilities.PlanStepRunner/RunnerOptions.cs`,
  `RoslynSentinel.Utilities.PlanStepRunner/Program.cs`.
- Depends on: 9.
- Effect: behaviour-changing for harness runs. The PlanStepRunner server now serves exactly the
  `planstep-runner` surface, where today it serves `DefaultIncludeTools` classes with a client filter on
  top.
- Change:
  - **Subagent child:** `BuildServerArguments` (`:50`) emits `--toolset=subagent-child`, replacing the
    mode, exclude and hide entries (`:53-57`). Remove the `ExcludedToolClasses`, `HiddenTools` and
    `ChildToolMode` constants (`:30`, `:38`, `:45`).
  - **`RunnerOptions`:** `DefaultIncludeTools` (`:72`) and `IncludeTools` (`:57`) are replaced by
    `public const string DefaultToolset = "planstep-runner";` and `public required string Toolset { get; init; }`,
    read from `--toolset`.
  - **`Program.cs`:** `:205` emits `"--toolset=" + options.Toolset`. At `:223`, pass
    `new McpClientToolHost(mcpClient)` without the `FilteringToolHost` wrap.
- Done when: `Build` 0 errors.
- Lane: `implementer` (D1).

### Step 20 - Consumer tests, the planstep script, and deleting the client filter
- Files: `scripts/roslynsentinel-planstep.ps1`, `RoslynSentinel.AgentLoop/FilteringToolHost.cs` and
  `AgentToolAllowLists.cs` (delete via `DeleteFile`), `Tests.Harness/SubAgentLaunchTests.cs`,
  `Tests.Harness/PlanStepRunnerSurfaceTests.cs`, `Tests.Harness/FilteringToolHostTests.cs` (delete),
  `Tests.ErrorVariants/RunnerToolHostTests.cs`.
- Depends on: 7, 19.
- Effect: text-neutral.
- Change:
  - **Script:** `-IncludeTools` (`:172`, `:259`, help `:105-111`) becomes `-Toolset` (default
    `planstep-runner`), passed as `--toolset`.
  - **`SubAgentLaunchTests`:** the `ChildServerArguments_*` tests (`:216-266`) assert
    `--toolset=subagent-child`.
  - **`PlanStepRunnerSurfaceTests`:** these tests assert against `planstep-runner.json`:
    - `DefaultIncludeTools_CoversTheClassOfEveryAllowListedTool`
    - `PlanStepScript_IncludeToolsDefault_MatchesRunnerOptions`
    - `Prompts_NameNoToolOutsideTheAllowList`
  - **`RunnerToolHostTests`:** the 4 tests at `:153-221` use a fake inner host whose `ListToolsAsync` omits
    the tool, replacing `FilteringToolHost`. The loop-guard tests (`KeepsCallingUnavailableTools_EndsWithUnknownToolRequested`,
    `ListedCallResetsTheUnavailableCount`) must still pass unchanged in intent.
  - **Deletes:** `FilteringToolHost.cs`, `AgentToolAllowLists.cs` and `FilteringToolHostTests.cs`. The last
    one removes the stale `Has.Count.EqualTo(18)`.

  Split: 20a script plus Tests.Harness, 20b AgentLoop deletes plus Tests.ErrorVariants.
- Done when:
  - `Build` 0 errors.
  - `Search(mode: text, query: "AgentToolAllowLists|FilteringToolHost", includeTests: true)` returns
    nothing.
  - The dispatcher runs Tests.Harness and Tests.ErrorVariants.
- Lane: `test-writer` x2.

### Step 21 - ModelEval surfaces from shipped toolsets
- Files: `RoslynSentinel.Tests.ModelEval/ChainEvalToolSurface.cs`, `RoslynSentinel.Tests.ModelEval/ModelEvalMcpEnvironment.cs`,
  `RoslynSentinel.Tests.ModelEval/ChainEvalToolSurfaceLeanTests.cs`.
- Depends on: 6, 8.
- Effect: text-neutral (the same 19-name lean surface and the same full surface).
- Change:
  - **Hosts:** `ModelEvalMcpEnvironment.CreateAsync`'s default surface (`:116-118`) and
    `ChainEvalToolSurfaceTests.SetUp` (`:142-145`) call the new registration overload with
    `ToolsetResolver.Resolve` of `eval-lean` or `eval-full`, chosen from `ROSLYNSENTINEL_MODELEVAL_TOOLSET`.
    The value mapping is unchanged: only `full` selects full.
  - **`ChainEvalToolSurface`:** `NewHiddenTools`, `NewFullSurfaceHiddenTools`, `LeanAllowList` and
    `LoadToolAssemblies` are deleted. A helper `public static ToolsetResolveResult ResolveSurface(string? toolsetValue, IEnumerable<string>? extraAllowed = null)`
    replaces them. `extraAllowed` becomes an inline extension: a `ToolsetDefinition` that extends the
    eval file.
  - **Text:** fix the stale "18" in the comment at `:19`.
  - **Other `hideTools:` callers** stay on the obsolete overload with a `// TODO` each:
    `OrderPricingRefactorChainAgentTests.cs:125,133`, `OrderPricingRefactorAgentTests.cs:192`,
    `WholeFileRewriteAgentTests.cs:354`, `TranscriptReplayTests.cs:57`. Moving them waits until
    `--hide-tools` is removed in Step 23.
- Done when:
  - `Build` 0 errors.
  - The dispatcher runs the non-live fixtures: `ChainEvalToolSurfaceLeanTests` (including
    `Lean_InProcessServerOffersExactlyTheAllowList`) and `ChainEvalToolSurfaceTests.OfferedTools_HaveDeleteFile_AndNoWriteFileGitOrProjectTools`.
- Lane: `implementer-senior` (test-project production helpers, three callers with different shapes).

### Step 22 - Status surface, test-instance launcher and McpServerControl parameters
- Files: `RoslynSentinel.Common/ActiveToolSurface.cs`, `RoslynSentinel.Tools.Basic/ServerStatusTools.cs`,
  `RoslynSentinel.Tools.Basic/AdminTools.cs`, plus `RoslynSentinel.Engines.Basic/TestInstanceLauncher.cs`
  in slice 22b.
- Depends on: 9, 16.
- Effect: behaviour-changing (wire).
  - `McpServerStatus.toolSurface` gains `toolsetName` and `toolsetSource`, plus `runtimeToolsets` (for
    example "not available on stateless HTTP; groups named by --toolsets are folded into the core").
  - `enabledBy` hints name a toolset or `--tools` where they named `--mode X or --include-tools Y`.
  - `McpServerControl` gains `toolset` and `tools` parameters. `includeTools` is deprecated, and a child
    with no `toolset` inherits the parent's toolset name.
- Change:
  - **22a:**
    - `ActiveToolSurface` gets optional ctor params `string? toolsetName = null, ToolSurfaceSource? toolsetSource = null`
      at the end (`ActiveToolSurface.cs:95`; 59 refs, and optional parameters keep them compiling). The
      Step-9 path fills them.
    - `McpServerStatusToolSurface` (`ServerStatusTools.cs:242`) gets the three fields. Extending an
      existing DTO needs no `NEW-DTO`. Wire impact: three new tool output fields.
    - `DescribeHowToEnable` (`:193`) gets the wording.
    - `McpServerControlTool` (`AdminTools.cs:547-564`) gets `toolset` and `tools` before `pid`;
      `cancellationToken` stays last.
  - **22b:**
    - `TestInstanceLauncher.LaunchAsync` (`:62`), `LaunchFromOutputAsync` (`:127`) and
      `BuildServerArguments` (`:364`) take `string? toolset, string? tools` beside `includeTools`.
    - `BuildServerArguments` emits `--toolset=` or `--tools=`, and no `--mode` when one of them is set.
    - `ValidateToolSelection` (`:549`) validates `tools` and `toolset` names through `ToolsetResolver`
      with an `IDeclaredToolCatalog` injected beside `IKnownToolClassNames` (ctor `:51`).
    - `IKnownToolClassNames` becomes `[Obsolete]`.
  - **Tests to update:**
    - `McpServerStatusToolListingTests.InactiveListing_ExcludesActiveTools_AndEachEntryExplainsHowToEnableIt`
      (`:129`, literal `EnabledBy`)
    - `TestInstanceLauncherTests.McpServerControl_NewOperationsAndParameters_AreInTheToolSignature`
      (`:361`)
    - `BuildServerArguments_Toolsets_EmittedOnlyWhenNonBlank` (`:372`)
    - `ValidateToolSelection_*` (`:385`, `:400`)
- Done when: `Build` 0 errors. The dispatcher runs `McpServerStatusToolListingTests`,
  `TestInstanceLauncherTests` and `ClaudeLeanSchemaBudgetTests` (McpServerControl schema growth; trim
  before raising the budget).
- Lane: `implementer` (22a, D1), then `implementer` (22b, D1), then `test-writer`.

### Step 23 - Scripts, configs and docs; remove the three old flags
- Files (non-C#):
  - `scripts/Launch-RoslynSentinelHttpServer.ps1`: param block `:58-70`, args `:89-92`, help `:22-32`,
    examples `:51-55`;
  - `scripts/roslynsentinel-vscode-control.ps1:339` (and the stale comment `:321-326`);
  - `scripts/install.ps1:37`;
  - `scripts/Measure-ToolSchemaCost.ps1:28,41`;
  - `scripts/Invoke-ToolComparison.ps1:29,194`;
  - `RoslynSentinel.Server.Advanced/Properties/launchSettings.json`;
  - `docs/current/references/reference_vscode_control_script.md:16`;
  - `docs/current/references/reference_changing_claude_lean_tools.md` (rewrite: "edit `claude-lean.json`,
    regenerate the snapshot");
  - `docs/current/references/reference_model_eval_procedure.md:507`.
- Files (C#): `ServerStartupHelpers.cs` (`ParseArgs` include and exclude, `ParseHideTools`,
  `DescribeUnknownIncludeTools`), `ToolSurfaceArgs.cs` (drop the legacy include, exclude and hide inputs),
  and `ServiceRegistrationExtensionsBasic.cs` (the dead `Contains("RefactoringTools")` block at `:486`).
- Depends on: 9, 19, 20, 21, 22.
- Effect: behaviour-changing. `--include-tools`, `--exclude-tools` and `--hide-tools` are rejected, with
  exit code 2 and an error naming `--toolset`, `--toolset-file` and `--tools`. `--mode` still works with
  its warning.
- Change:
  - **HTTP launch script:** `-Mode` and `-ExcludeTools` become `-ToolSet` (default `all`). `-Tools` maps
    to `--tools` and suppresses `--toolset`.
  - **Other scripts:** `--mode=all` becomes `--toolset=all` and `--mode=$Mode` becomes `--toolset=$ToolSet`.
  - **`launchSettings.json`:** replace every invalid mode or flag value (`RefactoringTools`,
    `MoveExtract`, `admintools`, `--exclude-tools="WholeFileWrite"`) with a real toolset.
  - **Remaining obsolete callers:** the test callers of the obsolete registration overload with
    `includeTools` or `hideTools` (survey of 2026-11-23: `SubAgentLaunchTests.cs:236`,
    `ExperimentalModeTests.cs:76`, `ToolClassNameResolutionTests.cs:33-79`, `ClaudeLeanModeTests.cs:124-206`,
    plus the Step-21 TODO callers) move to `--toolset-file`, an inline `ToolsetDefinition`, or are deleted
    where they only tested the removed flags. Split: 23a scripts and docs, 23b C# flag removal, 23c test
    callers.
  - **Tracking:** leave `// TODO remove --mode alias and ModeToToolClasses once ~/.mcp.json moves to --toolset`
    at the alias in `ToolSurfaceArgs`, and add a TODO.md line.
- Done when:
  - `Build` 0 errors.
  - `Search(mode: text, query: "--include-tools|--exclude-tools|--hide-tools", includeTests: true)` has
    only rejection-test hits.
  - `Grep` finds no `--mode=` in `scripts/`.
  - The full Tests.Server and Tests.Tools.Basic runs pass.
- Lane: `implementer` (23a, non-C#), `implementer-senior` (23b), `test-writer` (23c).

### Step 24 - Stub sweep, full run, live check
- Files: none.
- Depends on: 23.
- Effect: none.
- Change:
  - **Stub sweep:** `Search(mode: text, query: "NotImplementedException")` over `RoslynSentinel.Common/ToolSets/`
    returns no match.
  - **Full run:** `RunTest(suite: full)` across the solution, compared with the 0-failure baseline.
  - **Live check,** after the dispatcher's server restart on the new build, with one outcome class per
    case:
    - **A.** `Launch-RoslynSentinelHttpServer.ps1 -Tools "ReadFile,GetFileOutline"`: `tools/list` returns
      exactly 3 tools (`exact` | `widened`).
    - **B.** `-Tools "ReadFiel"`: exit code 2 with a "ReadFile" hint (`fails-with-hint` | `starts`).
    - **C.** A stdio session with `--toolset=claude-lean`: `McpToolsetControl`'s schema enum lists the 6
      groups, and enabling `git` works (`generated` | `stale-enum`).
    - **D.** HTTP with `--toolsets=git`: `GitRemote` is in `tools/list` and `McpToolsetControl` is not
      (`folded` | `missing`).
    - **E.** The owner's `--mode=claude-lean, subagenteval`: starts with a deprecation warning and serves
      the `claude-lean` surface (`alias-ok` | `broken`).
- Done when: every case has an outcome class recorded in this plan's Status. A non-ideal class gets a
  `// TODO` and an INBOX line.
- Lane: dispatcher (main session; case E needs the owner's config).

## Out of scope

- **Runtime group switching over stateless HTTP.** It needs an `IConfigureOptions<McpServerOptions>`
  (or `ConfigureSessionOptions`) hook that adds `ToolsetService`'s enabled tools to each per-request
  options copy. That would make the enable process-wide. Clients would still see the change only after
  re-listing, through a `/mcp` reconnect, because stateless sends no `tools/list_changed`. This is the
  approach of [plan_stateless_http_toolset_enablement.md](plan_stateless_http_toolset_enablement.md), a
  never-run DRAFT that was reopened from `closed/` on 2026-11-23.
- **Removing the `--mode` alias and `ModeToToolClasses`.** That waits for the owner's `~/.mcp.json` to
  move to `--toolset`, and is a TODO from Step 23.
- **Replacing the hard-coded per-class DI blocks.** Registration by reflection over the active classes is
  a separate refactor.
- **Migrating test hosts that pass plain modes.** About 25 Tests.Server and Tests.Advanced fixtures with
  `ActiveModes = {Workspace}` and the like keep the obsolete mode overload while the alias exists.
- **Per-session surfaces on a shared server** (proposal open question).
- **Fixing the owner's config.** `subagenteval` under claude-lean is not served today, and the owner
  decides whether `~/.mcp.json` should use a toolset that includes it.

## Risks and open decisions

- **R1. Can the description and enum be generated at registration?** Step 14 assumes
  `McpToolSchemaPatcher` can set `McpToolsetControl`'s `ProtocolTool.Description` and the input schema's
  `toolSet` enum after the SDK builds the tool. Unverified.
  - Options: (a) patch the `McpServerTool.ProtocolTool` in the patcher's post-processing (recommended,
    same path as the `x-consumes-tag` injection); (b) register `McpToolsetControl` through
    `CreateSentinelTool` with a hand-built `Tool` object.
  - Step 14 checks (a) before editing and escalates if it fails.
- **R2. Snapshot parity with claude-lean classes.** Today `--mode=claude-lean,subagenteval` registers
  `SubAgentEvalTools` with zero allowed tools. The new path registers only classes that own a served or
  group tool. Served tools are identical, but `McpServerStatus.activeToolClasses` gets shorter. Accepted:
  Step 1 snapshots tool names, not classes. Recommendation: note it in the Step 9 commit message.
- **R3. What eval surfaces Tests.Server can see.** Step 7's eval-parity tests need `AgentToolAllowLists`
  (AgentLoop) and `ChainEvalToolSurface` (Tests.ModelEval). If `Tests.Server` does not reference them,
  the tests go into Tests.ModelEval (named fallback in Step 7).
- **R4. The `McpServerStatus` always-on rule.** Today `ServerStatusTools` is registered outside the
  registry in every mode, and only hide lists remove it. The resolver adds it unless excluded, and the
  eval files exclude it. If another caller relies on it being impossible to remove, Step 10 surfaces
  that.
- **R5. Assembly timing.** `DeclaredToolCatalog` loads the referenced `RoslynSentinel.Tools.*`
  assemblies explicitly. `Tools.Experimental` is referenced by `Server.Advanced.csproj:28-30`, so it is
  covered. A tools assembly that `Server.Advanced` does not reference would be missed. None exists today.
- **R6. A pre-existing red test.** `FilteringToolHostTests.cs:95` asserts 18 against 19 names. Step 20
  deletes it. Until then it may show red in a full Tests.Harness run; it is not caused by this plan.
- **R7. Schema budget.** Steps 14 and 22 grow schemas (the generated `McpToolsetControl` description, two
  `McpServerControl` parameters). `ClaudeLeanSchemaBudgetTests` catches an overrun. Trim before raising
  the budget, which follows the precedent of the tab-scoped plan's D-budget ruling.
- **R8. Peer sessions.** `ServiceRegistrationExtensionsBasic.cs` and `AdminTools.cs` are frequent edit
  targets. Run `Git status` before Steps 8, 13, 22 and 23.
