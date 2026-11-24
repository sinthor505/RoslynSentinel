# Plan: one ParameterSurface for tool parameter hiding (phase 1) and aliasing (phase 2)

**Status:** SUPERSEDED 2026-11-24. Reshaped into [plan_tool_surface_controller_stage_d.md](plan_tool_surface_controller_stage_d.md) (stage 3 of the controller proposal); Steps 1-3 never landed, Steps 4-15 are mapped in that plan's "What happened to the paused plan" table.

> **Why paused.** Steps 4-10 wire a standalone `ParameterSurface` DI singleton into the patcher, validator, eval
> harness and status. Under the controller proposal that wiring is replaced: the controller builds a
> `ParameterSurface` view per snapshot, consumers read the snapshot, and the harness supplies a layer. The rule
> and resolver contract (Steps 1-3) carries over unchanged. When reshaping, make the surface immutable per
> snapshot (strips recorded while the snapshot is built) rather than a long-lived mutable registry.

## Problem

See [proposal_tool_parameter_surface_options.md](../../proposals/proposal_tool_parameter_surface_options.md).
In short: hiding has three ad hoc sources (Lean constant, `--hide-params`, harness
`SchemaOptions.ConfigureHiddenParameters`), merged into a flat name list
(`SchemaOptions.EffectiveHiddenParameters`, `RoslynSentinel.Common/SchemaOptions.cs:62`) that hides a name
from every tool, has no provenance, silently keeps unknown names and lives in process statics. Aliases are
three code dictionaries in `ToolArgumentValidator` (`:612`, `:728`, `:741`) with the same gaps.

## Decision

- Every source emits rules (`ParameterVisibilityRule`, `ParameterAliasRule`) into one
  `ToolParameterSurfaceOptions`; one `ParameterSurface` instance per host resolves them and records what the
  patcher stripped. It is a DI singleton.
- **Merge:** layers ordered by `ParameterRuleSource` (Profile < ToolsetFile < EnvOrFlag < Harness); within a
  layer, unscoped rules (`Tool == null`) before tool-scoped ones, then list order; the last matching rule wins.
  `Show` with `Parameter == "*"` un-hides every parameter in its scope. Tool names compare
  `OrdinalIgnoreCase`, parameter names `Ordinal` (as `HiddenSchemaParams` does today).
- **How consumers reach it** (traced 2026-11-23):
  - Patcher: `McpToolSchemaPatcher.CreateSentinelTool` already receives `IServiceProvider services`
    (`McpToolSchemaPatcher.cs:168`, `:182`) but does not pass it to `ApplyHiddenParameters` (`:214`). It will
    resolve `services?.GetService<ParameterSurface>()`, falling back to a surface built from the startup
    options (`ToolSignatureCatalogReflection.cs:77` passes `services: null!`; several tests build tools
    directly).
  - Runtime toolsets: `ToolsetService.SetEnabled` calls `CreateSentinelTool(..., _services)`
    (`ToolsetService.cs:189`) with the root provider, so runtime-enabled tools resolve the same singleton.
  - Validator: `Validate` (`ToolArgumentValidator.cs:1280`), `WrapFlatBatchParameters` (`:1041`) and
    `ApplyParameterAliases` (`:868`) all take `McpServer? server`; the filter already resolves services via
    `context.Server.Services` (`ServiceRegistrationExtensionsBasic.cs` filter at `:1490-1589`). No signature
    change: each reads `server?.Services?.GetService<ParameterSurface>()`, falling back to today's statics.
  - Registration: `TryAddSingleton` beside `services.AddSingleton(toolAllowList)`
    (`ServiceRegistrationExtensionsBasic.cs:364`, in `RegisterRoslynSentinelToolsBasic`), so a harness that
    registers its own surface first wins and production is unchanged.
- **Kept, deliberately:** `HiddenSchemaParams` stays as a mirror the patcher still writes, because
  `ToolCallHint.Call` reads it (`ToolCallHint.cs:96`) and has 452 static callers with no services
  (follow-up, out of scope). The validator's static `SchemaCache` (`ToolArgumentValidator.cs:15`) also stays.
  The eval harness therefore keeps its `HiddenSchemaParams.Clear()` and `ClearSchemaCacheForTests()` calls;
  only `ConfigureHiddenParameters` goes.
- **Status output:** `McpServerStatus` gains `hiddenParameters` and `unmatchedHideRules`, filled only when
  `toolListing` is not `none`, so the default status call costs no extra tokens.
- **Aliases:** same options object, phase 2. Code tables are the Profile layer; a later file source may add
  aliases but never remove or override a code alias. `EnumValueAliases` (value aliases) stay as they are.

## Execution rules

- One `Build`/`RunTest` at a time. Each step: `Build` 0 errors. Implementers do not run tests; the dispatcher
  runs the named tests.
- Test baseline is 0 failures; any failure is new.
- CS0618 warnings from the `[Obsolete]` members are expected during the migration.
- Tests named below that touch process statics (`SchemaHiddenParametersTests`, `SchemaLeanProfileTests`,
  `EvalToolSchemaVisibilityTests`) must stay green at every step; they are the regression net for
  `tools/list` output.
- New DTOs carry the NEW-DTO block from the proposal in the creating call's `reason`.

## Steps

### Phase 1 - hiding

### Step 1 - Contract skeleton
- Files: `RoslynSentinel.Common/ToolParameterSurfaceOptions.cs` (new), `RoslynSentinel.Common/ParameterSurface.cs` (new)
- Depends on: none
- Effect: text-neutral (no callers)
- Lane: implementer (2 `CreateFile` calls, options file first)
- Change: namespace `RoslynSentinel.Common`. `ToolParameterSurfaceOptions.cs`:
  ```csharp
  public enum ParameterRuleSource { Profile, ToolsetFile, EnvOrFlag, Harness }
  public enum ParameterVisibility { Hide, Show }
  // Tool null = every tool. Parameter "*" only with Show. Origin: human-readable pointer for diagnostics.
  public sealed record ParameterVisibilityRule(string? Tool, string Parameter, ParameterVisibility Action, ParameterRuleSource Source, string Origin);
  public sealed record ParameterAliasRule(string? Tool, string Alias, string Target, ParameterRuleSource Source, string Origin);
  // One row per winning rule: the parameter, where the rule came from, and the tools it was stripped from.
  public sealed record HiddenParameter(string Parameter, ParameterRuleSource Source, string Origin, IReadOnlyList<string> Tools);
  public sealed record ToolParameterSurfaceOptions(IReadOnlyList<ParameterVisibilityRule> Visibility, IReadOnlyList<ParameterAliasRule> Aliases)
  {
      public static ToolParameterSurfaceOptions Empty { get; } = new([], []);
      // Lean list (when SchemaOptions.Profile is Lean) as Profile rules, then SchemaOptions.HiddenParameters as EnvOrFlag rules.
      public static ToolParameterSurfaceOptions FromStartup() => throw new NotImplementedException(); // TODO implement (Step 2)
      // Splits on ',' and ';', trims, drops empties; "Tool.param" scopes to a tool, a bare name to every tool. Never throws.
      public static IReadOnlyList<ParameterVisibilityRule> ParseHideList(string? raw, ParameterRuleSource source, string origin) => throw new NotImplementedException(); // TODO implement (Step 2)
  }
  ```
  `ParameterSurface.cs`:
  ```csharp
  public sealed class ParameterSurface
  {
      public ParameterSurface(ToolParameterSurfaceOptions options) => throw new NotImplementedException(); // TODO implement (Step 2)
      public ToolParameterSurfaceOptions Options { get; }
      public ParameterVisibilityRule? ResolveHidden(string toolName, string parameterName) => throw new NotImplementedException(); // TODO implement (Step 2)
      public void RecordStripped(string toolName, IReadOnlyList<string> parameterNames) => throw new NotImplementedException(); // TODO implement (Step 2)
      public bool IsStripped(string? toolName, string parameterName) => throw new NotImplementedException(); // TODO implement (Step 2)
      public IReadOnlyList<HiddenParameter> StrippedParameters() => throw new NotImplementedException(); // TODO implement (Step 2)
      public IReadOnlyList<ParameterVisibilityRule> UnmatchedHideRules() => throw new NotImplementedException(); // TODO implement (Step 2)
  }
  ```
- Done when: `Build` 0 errors.

### Step 2 - Implement the options factory, parser and resolver
- Files: `RoslynSentinel.Common/ToolParameterSurfaceOptions.cs`, `RoslynSentinel.Common/ParameterSurface.cs`
- Depends on: Step 1
- Effect: text-neutral (no callers yet)
- Lane: implementer
- Change: bodies per the Decision's merge rule. `FromStartup` reads `SchemaOptions.Profile`,
  `SchemaOptions.LeanHiddenParameters` (origin `"--schema-profile=lean"`) and `SchemaOptions.HiddenParameters`
  (origin `"--hide-params"`; parse each entry through `ParseHideList` so `Tool.param` works). The constructor
  orders rules once (stable sort by `Source`, then unscoped before scoped). `ResolveHidden` returns the last
  matching rule if it is a `Hide`, else null. `RecordStripped` keeps, per tool (case-insensitive), the stripped
  names and the winning rule for each; thread-safe (`ConcurrentDictionary`, as `HiddenSchemaParams`).
  `StrippedParameters` groups by winning rule. `UnmatchedHideRules` = `Hide` rules that won for no recorded
  strip.
- Done when: `Build` 0 errors.

### Step 3 - Unit tests for the resolver
- Files: `RoslynSentinel.Tests/ParameterSurfaceTests.cs` (new)
- Depends on: Step 2
- Pairs with: Step 4
- Effect: text-neutral
- Lane: test-writer
- Change: tests `ParameterSurfaceTests.LaterLayerWins`, `ScopedRuleBeatsUnscopedInSameLayer`,
  `ShowStar_UnhidesLowerLayers`, `ParseHideList_ToolDotParam_ScopesToTool`,
  `ParseHideList_BlankOrSeparatorsOnly_GivesEmpty`, `FromStartup_LeanAndExplicit_MatchesEffectiveHiddenParameters`
  (same name set as `SchemaOptions.EffectiveHiddenParameters()` for Lean plus `--hide-params`; reset statics
  in TearDown as `SchemaHiddenParametersTests.cs:43-46` does), `IsStripped_FalseUntilRecorded`,
  `UnmatchedHideRules_ListsRuleThatStrippedNothing`, `StrippedParameters_GroupsToolsByRule`.
- Done when: the dispatcher runs `ParameterSurfaceTests`: all pass.

### Step 4 - Register the singleton
- Files: `RoslynSentinel.Server.Advanced/ServiceRegistrationExtensionsBasic.cs`
- Depends on: Step 2
- Effect: text-neutral (nothing resolves it yet)
- Lane: implementer
- Change: beside `services.AddSingleton(toolAllowList);` (`:364`, `RegisterRoslynSentinelToolsBasic`) add
  `services.TryAddSingleton(_ => new ParameterSurface(ToolParameterSurfaceOptions.FromStartup()));` with a
  comment that a host may register its own surface first (the eval harness does, Step 8).
- Done when: `Build` 0 errors.

### Step 5 - Patcher strips by ParameterSurface
- Files: `RoslynSentinel.Common/McpToolSchemaPatcher.cs`, `RoslynSentinel.Common/SchemaOptions.cs`
- Depends on: Step 4
- Effect: behaviour-changing only for a `Tool.param` entry in `--hide-params` (newly scoped); `tools/list` is
  identical for every existing configuration
- Lane: implementer
- Change:
  - `private static void ApplyHiddenParameters(McpServerTool tool, MethodInfo toolMethod)` (`:625`, one caller
    at `:214`) gains a third parameter `ParameterSurface surface`. At `:214` pass
    `services?.GetService<ParameterSurface>() ?? new ParameterSurface(ToolParameterSurfaceOptions.FromStartup())`
    (`services` may be `null!` from `ToolSignatureCatalogReflection.cs:77`; comment the fallback).
  - Body: iterate the top-level `properties` names and remove each one for which
    `surface.ResolveHidden(tool.ProtocolTool.Name, name)` is non-null; `required` handling unchanged. Then
    `surface.RecordStripped(...)` and keep `HiddenSchemaParams.Register(...)` with
    `// TODO HiddenSchemaParams is a mirror for ToolCallHint (452 static callers); remove when ToolCallHint can reach ParameterSurface`
    plus an INBOX.md line.
  - `SchemaOptions.EffectiveHiddenParameters` gets `[Obsolete("Use ParameterSurface.ResolveHidden")]`
    (remaining caller: `SchemaHiddenParametersTests.cs:259`). Update the patcher's doc comment at `:611-624`.
- Done when: `Build` 0 errors; dispatcher runs `SchemaHiddenParametersTests`, `SchemaLeanProfileTests`,
  `ReplaceSnippetSchemaTests`, `McpToolsetControlTests`: all pass.

### Step 6 - Validator reads ParameterSurface
- Files: `RoslynSentinel.Server.Advanced/ToolArgumentValidator.cs`
- Depends on: Step 4
- Pairs with: Step 5 (disjoint files)
- Effect: text-neutral (same answers while the mirror and the surface agree)
- Lane: implementer
- Change: add `private static bool IsHiddenParameter(ModelContextProtocol.Server.McpServer? server, string? toolName, string parameterName)`
  returning `server?.Services?.GetService<ParameterSurface>()?.IsStripped(toolName, parameterName) ?? HiddenSchemaParams.IsHidden(toolName, parameterName)`.
  Replace the three `HiddenSchemaParams.IsHidden` calls at `:1057` (`WrapFlatBatchParameters`), `:1301` and
  `:1321` (`Validate`); both methods already have `server`.
- Done when: `Build` 0 errors; dispatcher runs `EvalToolSchemaVisibilityTests` (7 tests),
  `SchemaHiddenParametersTests`, `SchemaLeanProfileTests`: all pass.

### Step 7 - Tests for per-tool scope
- Files: `RoslynSentinel.Tests.Server/SchemaHiddenParametersTests.cs`
- Depends on: Steps 5, 6
- Pairs with: Step 8
- Effect: text-neutral
- Lane: test-writer
- Change: add `HideParams_ToolScoped_HidesOnlyOnThatTool` (`--hide-params=ReplaceSnippet.validateOnApply`:
  absent from `ReplaceSnippet`, still listed on another tool that declares `validateOnApply`, e.g. a
  `WholeFileWriteTools` tool) and `HideParams_ToolScoped_HiddenArgumentStillAccepted` (call `ReplaceSnippet`
  with `validateOnApply: true`: no "Unknown parameter").
- Done when: dispatcher runs `SchemaHiddenParametersTests`: all pass.

### Step 8 - Eval harness registers its own surface
- Files: `RoslynSentinel.Tests.ModelEval/ModelEvalMcpEnvironment.cs`, `RoslynSentinel.Common/SchemaOptions.cs`
- Depends on: Steps 5, 6
- Effect: text-neutral (same schemas per world; hidden state now per host)
- Lane: implementer
- Change: replace `SchemaOptions.ConfigureHiddenParameters(...)` (`:125`) with a
  `services.AddSingleton(new ParameterSurface(...))` registered before the `configureToolSurface` branch
  (`:127`), built from `ToolParameterSurfaceOptions.FromStartup()` plus one Harness rule, origin
  `"ModelEvalMcpEnvironment"`: `Show *` when `fullToolSchema`, else `Hide validateOnApply`. Keep
  `HiddenSchemaParams.Clear()` and `ClearSchemaCacheForTests()` and reword the comment at `:118-122`
  (mirror and schema cache are still process-static). Mark `SchemaOptions.ConfigureHiddenParameters`
  `[Obsolete("Register a ParameterSurface in DI")]` (remaining caller `SchemaHiddenParametersTests.cs:44`).
- Done when: `Build` 0 errors; dispatcher runs `EvalToolSchemaVisibilityTests`: all 7 pass.

### Step 9 - McpServerStatus reports hidden parameters
- Files: `RoslynSentinel.Tools.Basic/ServerStatusTools.cs`
- Depends on: Step 5
- Effect: behaviour-changing (wire): with `toolListing` other than `none`, the result gains
  `hiddenParameters` (rows of parameter, source, origin, tools) and `unmatchedHideRules`; both null otherwise
  and when empty
- Lane: implementer
- Change: optional trailing constructor parameter `ParameterSurface? parameterSurface = null` (`:25-33`);
  two trailing record parameters on `McpServerStatusResult` (`:271`):
  `IReadOnlyList<HiddenParameter>? HiddenParameters = null`,
  `IReadOnlyList<ParameterVisibilityRule>? UnmatchedHideRules = null`; fill them at the single construction
  site (`:108`). Extend the tool `[Description]` with one clause: `hiddenParameters = schema parameters hidden
  and which rule hid them; unmatchedHideRules = hide rules that matched no tool (with toolListing).`
- Done when: `Build` 0 errors.

### Step 10 - Status tests
- Files: test file chosen by test-writer next to existing `McpServerStatus` tests
- Depends on: Step 9
- Effect: text-neutral
- Lane: test-writer
- Change: `McpServerStatus_ToolListingInactive_ReportsHiddenParameterWithOrigin` (Lean profile:
  `validateOnApply` row, source Profile, origin `--schema-profile=lean`),
  `McpServerStatus_DefaultListing_OmitsHiddenParameters`,
  `McpServerStatus_ReportsUnmatchedRule_ForMisspeltHideParam` (`--hide-params=validateOnAply`).
- Done when: dispatcher runs the three tests: all pass.

### Phase 2 - aliases

### Step 11 - Alias contract and built-in rules in Common
- Files: `RoslynSentinel.Common/BuiltInParameterAliases.cs` (new), `RoslynSentinel.Common/ParameterSurface.cs`,
  `RoslynSentinel.Common/ToolParameterSurfaceOptions.cs`
- Depends on: Step 2
- Effect: text-neutral (no callers)
- Lane: implementer-senior (data move of ~115 lines of dictionary at `ToolArgumentValidator.cs:612-727`
  plus new members; over a Haiku slice's edit budget)
- Change: `public static class BuiltInParameterAliases { public static IReadOnlyList<ParameterAliasRule> Rules { get; } }`
  holding the entries of `ParameterAliases` (`Tool` set) and `GlobalParameterAliases` (`Tool` null), source
  Profile, origin `"BuiltInParameterAliases"`, same order (first alias wins for a target, as
  `ApplyParameterAliases` does today). `FromStartup` appends `BuiltInParameterAliases.Rules` to `Aliases`.
  `ParameterSurface` gains `public string? ResolveAliasTarget(string toolName, string alias)` (tool-scoped
  first, then unscoped; alias `OrdinalIgnoreCase`; a later rule never overrides an earlier one for the same
  (tool, alias)) and `public IReadOnlyList<ParameterAliasRule> AliasesFor(string toolName)`.
- Done when: `Build` 0 errors.

### Step 12 - Validator resolves aliases through ParameterSurface
- Files: `RoslynSentinel.Server.Advanced/ToolArgumentValidator.cs`
- Depends on: Step 11
- Effect: text-neutral (same tables, new route)
- Lane: implementer
- Change: `ApplyParameterAliases` (`:868`) uses `server?.Services?.GetService<ParameterSurface>()` and falls
  back to the static tables. `private static string? SuggestForUnknown(string toolName, string unknown, IEnumerable<string> declared, ICollection<string> supplied)`
  (`:179`, private) gains a trailing `ParameterSurface? surface` parameter for its alias block (`:187-203`);
  pass it from `Validate`. Rebuild `ParameterAliases` and `GlobalParameterAliases` from
  `BuiltInParameterAliases.Rules` (same shape, so `ToolParameterAliasTests` keeps compiling) and mark both
  `[Obsolete("Use ParameterSurface.ResolveAliasTarget")]`.
- Done when: `Build` 0 errors; dispatcher runs `ToolParameterAliasTests`: all pass.

### Step 13 - Report alias targets no tool declares
- Files: `RoslynSentinel.Common/ParameterSurface.cs`, `RoslynSentinel.Common/McpToolSchemaPatcher.cs`,
  `RoslynSentinel.Tools.Basic/ServerStatusTools.cs`
- Depends on: Steps 9, 12
- Effect: behaviour-changing (wire): `McpServerStatus` with `toolListing` gains `unresolvableAliasRules`
- Lane: implementer
- Change: `ParameterSurface.RecordDeclared(string toolName, IReadOnlyCollection<string> parameterNames)`
  called from `CreateSentinelTool` with the pre-strip top-level names; `UnresolvableAliasRules()` = alias rules
  whose target no recorded tool in scope declares. Third trailing record parameter on `McpServerStatusResult`,
  same `toolListing` gating as Step 9.
- Done when: `Build` 0 errors.

### Step 14 - Alias tests
- Files: `RoslynSentinel.Tests/ParameterSurfaceTests.cs`, `RoslynSentinel.Tests.Server/ToolParameterAliasTests.cs`
- Depends on: Steps 12, 13
- Effect: text-neutral
- Lane: test-writer
- Change: `ParameterSurfaceTests.ResolveAliasTarget_ToolScopedBeforeGlobal`,
  `ResolveAliasTarget_LaterRuleDoesNotOverride`, `BuiltInRules_MatchLegacyTables` (every legacy dictionary
  entry resolves to the same target); `ToolParameterAliasTests.HarnessSurface_AddedAlias_IsApplied` (a host
  registering a surface with one extra alias sees it applied); a status test for
  `unresolvableAliasRules` with a deliberately bad alias.
- Done when: dispatcher runs the named tests: all pass.

### Step 15 - No stubs left
- Files: none (check only)
- Depends on: all
- Effect: text-neutral
- Lane: dispatcher
- Change: `Search(mode: "text", query: "NotImplementedException", fileGlob: "RoslynSentinel.Common/{ParameterSurface,ToolParameterSurfaceOptions,BuiltInParameterAliases}.cs")`.
- Done when: zero matches; full `RunTest` with 0 failures.

## Out of scope

- `hideParams` (and file-added aliases) in the JSON toolset files: added to
  [plan_json_toolset_definitions.md](../plan_json_toolset_definitions.md) as a step that maps a startup
  toolset's `hideParams` through `ToolParameterSurfaceOptions.ParseHideList(raw, ParameterRuleSource.ToolsetFile, <file>)`.
  Depends on this plan's Step 5.
- `ToolCallHint` reading `ParameterSurface` (static, 452 callers) and with it removing the `HiddenSchemaParams`
  mirror; making the validator's `SchemaCache` per host.
- `EnumValueAliases` (value aliases), nested batch-item schemas, generalising the hard-coded
  `validateOnApply:false` refusal (`ToolArgumentValidator.cs:1296-1308`).
- HTTP transport differences (the JSON toolsets plan removes runtime toolsets there).

## Risks and open decisions

- **Mirror drift.** Until `ToolCallHint` migrates, `HiddenSchemaParams` is a union across every host in the
  process; a hint can render a hidden pair for a world that no longer hides it. Cosmetic only (hint text);
  validation no longer reads the mirror once Step 6 lands.
- **Fallback surfaces.** A tool built with no services (catalog reflection, direct test builds) gets a fresh
  surface per tool, so its strips appear in no status report. Acceptable: those tools are never served.
- **Runtime toolset `hideParams`.** Rules are fixed when the singleton is built; a toolset enabled later via
  `McpToolsetControl` cannot add hide rules. Recommended and assumed by the out-of-scope step; confirm when
  that step is written.
- **Enum wire form.** `HiddenParameter.Source` and `ParameterVisibilityRule.Source`/`Action` are enums on the
  status wire; confirm they serialise as strings like the other status enums before Step 9 lands.
- **`toolListing` gating** of the new status fields is a recommendation (keeps the default call cheap);
  alternative is always-on when non-empty, which adds rows to every status call in the Lean profile.
- No cross-plan baseline dependency: `tools/list` output is unchanged for existing configurations, so eval
  A/B baselines are unaffected.
