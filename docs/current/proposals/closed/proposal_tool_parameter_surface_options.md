# One options DTO for tool parameter hiding and aliasing, fed by code, env/flag, toolset files and harnesses

**Status:** IMPLEMENTED 2026-11-24 through [plan_tool_surface_controller_stage_d.md](../../plans/closed/plan_tool_surface_controller_stage_d.md) (rule model, per-host surface, Tool.param scoping); parameter/value aliases deferred to toolset stage G; status provenance fields deferred (INBOX). Was PROPOSED 2026-11-23. Owner accepted the recommendations; plan drafted in [plan_tool_parameter_surface_options.md](../../plans/closed/plan_tool_parameter_surface_options.md), which supersedes the open questions below where they differ. Folded into [proposal_tool_surface_controller.md](../proposal_tool_surface_controller.md) the same day: the rule model below stands, the standalone DI wiring does not.

## Motivation

Parameter hiding has three sources today, each with its own shape, merged ad hoc in a static class:

- Lean profile constant: `SchemaOptions.LeanHiddenParameters` (`RoslynSentinel.Common/SchemaOptions.cs:26`),
  active only when `--schema-profile=lean`.
- `--hide-params` / `ROSLYNSENTINEL_HIDE_PARAMS`, parsed into `SchemaOptions.HiddenParameters`
  (`SchemaOptions.cs:29`, `:87-98`).
- Harness code: each model-eval world calls `SchemaOptions.ConfigureHiddenParameters(...)`
  (`RoslynSentinel.Tests.ModelEval/ModelEvalMcpEnvironment.cs:125`), which *replaces* the explicit set.

`SchemaOptions.EffectiveHiddenParameters` (`SchemaOptions.cs:62-79`) unions them into a flat name list and
`McpToolSchemaPatcher.ApplyHiddenParameters` (`RoslynSentinel.Common/McpToolSchemaPatcher.cs:625`) strips each
name from every tool's top-level `properties`, then records what it stripped in the static
`HiddenSchemaParams` registry (`RoslynSentinel.Common/HiddenSchemaParams.cs:17`) so the call-time validator
still accepts it (`RoslynSentinel.Server.Advanced/ToolArgumentValidator.cs:1321`).

Concrete costs of that shape:

1. **No per-tool scope.** A name is hidden from every tool that declares it; `validateOnApply` cannot be
   hidden on `ReplaceSnippet` while staying visible on another tool.
2. **No provenance.** Nothing can answer "why is this parameter missing from the schema?". Gated tools
   already answer the equivalent question via `McpServerStatus` `enabledBy`; parameters cannot.
3. **Process-static mutation.** The harness must clear and re-set three statics per world
   (`ModelEvalMcpEnvironment.cs:118-125`), and the validator carries a defensive check because
   "HiddenSchemaParams and SchemaCache are process-wide and keyed by tool name"
   (`ToolArgumentValidator.cs:1294-1295`). The server test fixtures repeat the same reset
   (`Tests.Server/SchemaHiddenParametersTests.cs:43-46`, `SchemaLeanProfileTests.cs:40-42`).
4. **No validation.** `ParseHiddenParameters` "keeps unknown names" (`SchemaOptions.cs:31`); a misspelt or
   renamed parameter silently hides nothing.
5. **No file source.** The JSON toolset definitions
   ([proposal_json_toolset_definitions.md](../proposal_json_toolset_definitions.md), plan in DRAFT) will
   describe surfaces such as eval worlds, which is exactly where per-surface hiding belongs, but would
   have to add a fourth ad hoc source.

Aliasing has the same single-source problem in a different place: three code dictionaries in
`ToolArgumentValidator.cs` (`ParameterAliases` `:612`, `GlobalParameterAliases` `:728`, `EnumValueAliases`
`:741`) applied by `ApplyParameterAliases` (`:868`), with no provenance, no startup check that an alias
target still exists, and no way to add one without a code change.

## Proposal

### Single representation (decided in principle by the owner 2026-11-23)

Every source produces rules in one shape; one resolver merges them; the resolved result is the only thing
the chokepoints read.

```
NEW-DTO: ParameterVisibilityRule (Item, Common.Schema)
Evaluated: SchemaOptions - static holder of flat unscoped name sets, no provenance, not a DTO;
           HiddenSchemaParams - registry of what the patcher already stripped (output), not intent (input)
Wire impact: none (McpServerStatus gains a field in slice 4)

NEW-DTO: ParameterAliasRule (Item, Common.Schema)
Evaluated: ToolArgumentValidator.ParameterAliases/GlobalParameterAliases - nested string dictionaries,
           no provenance, live in Server.Advanced (invisible to Common, where the toolset loader goes)
Wire impact: none

NEW-DTO: ToolParameterSurfaceOptions (Options, Common.Schema)
Evaluated: SchemaOptions - see above; ToolsetDefinition (planned, plan_json_toolset_definitions) - one
           source among several, would couple env/harness rules to the file format
Wire impact: none

NEW-DTO: HiddenParameter (Item, Common.Schema)
Evaluated: McpServerStatusDeclaredTool - one row per tool, no parameter or rule; ParameterVisibilityRule -
           the rule alone, without the tools it actually stripped
Wire impact: new tool output field(s) (McpServerStatus hiddenParameters, with toolListing)
```

`ParameterSurface` itself is a service (resolver plus per-host strip record), not a DTO: `DTO-EXEMPT`.

Sketch (names provisional):

```csharp
public enum ParameterRuleSource { Profile, ToolsetFile, EnvOrFlag, Harness }
public enum ParameterVisibility { Hide, Show }

// Tool null = every tool. Parameter "*" allowed only with Show.
public sealed record ParameterVisibilityRule(string? Tool, string Parameter, ParameterVisibility Action, ParameterRuleSource Source, string Origin);
public sealed record ParameterAliasRule(string? Tool, string Alias, string Target, ParameterRuleSource Source, string Origin);

public sealed record ToolParameterSurfaceOptions(IReadOnlyList<ParameterVisibilityRule> Visibility, IReadOnlyList<ParameterAliasRule> Aliases);
```

`Origin` is a human-readable pointer (`"--schema-profile=lean"`, `"ROSLYNSENTINEL_HIDE_PARAMS"`,
`"ToolSets/Shipped/eval-lean.json"`, `"ModelEvalMcpEnvironment"`) for diagnostics.

A resolver turns the options plus the registered tool schemas into an immutable `ParameterSurface`
(service object, not a DTO): per tool, the hidden names with the winning rule, and the alias map. It feeds
all three consumers:

- `McpToolSchemaPatcher.ApplyHiddenParameters` - what to strip from `tools/list`.
- The call-time validator - hidden-but-accepted names (today `HiddenSchemaParams.IsHidden`) and aliases
  (today `ApplyParameterAliases`).
- `McpServerStatus` - provenance and validation warnings.

### Source adapters

Each existing source becomes a small adapter that emits rules; none applies anything itself.

| Source | Adapter output |
| --- | --- |
| Lean profile constant | `Hide` rules, `Tool = null`, `Source = Profile` |
| `--hide-params` / env | `Hide` rules; accepts `name` (all tools) and new `Tool.name` form |
| Toolset JSON | new `hideParams` string array, same syntax as `--hide-params` (added by the JSON toolsets plan) |
| Harness code | rules built in code; `OfferFullToolSchema` becomes one `Show *` rule |
| Alias dictionaries | `ParameterAliasRule`s, `Source = Profile` (code defaults) |

Keeping the file and flag syntax a plain string list (`"ReplaceSnippet.validateOnApply"`) means the JSON
toolset format needs no nested per-parameter objects.

### Recommendations on the open decisions

1. **Merge rule: ordered layers, later Show un-hides.** Layer order Profile < ToolsetFile < EnvOrFlag <
   Harness; within a layer, rules apply in order; for a given (tool, parameter) the last matching rule wins.
   A plain union cannot express today's full-schema opt-in, which replaces the set with an empty one
   (`ModelEvalMcpEnvironment.cs:125`); a `Show *` rule in the Harness layer can. Tool-specific rules beat
   `Tool = null` rules in the same layer.
2. **Aliases in the same options DTO, built second.** They gain the same provenance, startup validation
   and file source, but are applied at a different chokepoint (call-time argument rewrite, not
   `tools/list`), so they land as a later slice. Aliases stay surface-independent by default: the code
   tables remain the primary source; a toolset file may *add* aliases but not remove code ones, so an
   eval world never sees weaker argument correction than production.
3. **Own proposal; the JSON toolsets plan takes `hideParams` as a dependency.** This change spans
   startup, schema patching, validation, status and the eval harness - wider than toolsets.

### Slices

1. Contract: rule/option records, `ParameterSurface` resolver in `Common`, unit tests for layering, scope,
   `Show *` and unknown-name reporting. No behaviour change.
2. Adapters for the profile constant and `--hide-params` (with `Tool.name`); the patcher and validator read
   `ParameterSurface` instead of `SchemaOptions.EffectiveHiddenParameters` / `HiddenSchemaParams`.
   `SchemaOptions.ConfigureHiddenParameters` kept, marked `[Obsolete("Use ToolParameterSurfaceOptions")]`.
3. Eval harness builds options per world; the static clear/re-set at `ModelEvalMcpEnvironment.cs:118-125`
   and the fixture resets go away once nothing reads the statics.
4. Provenance and validation warnings in `McpServerStatus`.
5. `hideParams` in the toolset JSON (tracked in `plan_json_toolset_definitions.md`).
6. Alias tables move to `ParameterAliasRule`s; startup check that each target is declared; optional file
   source.

## Alternatives considered, not pursued

- **Only a `hideParams` string array in the toolset JSON**, feeding `SchemaOptions` as a fourth source.
  Smallest change, but keeps the flat name set: no per-tool scope, no provenance, no replace/un-hide
  semantics, static mutation stays.
- **Keep `SchemaOptions`, add a file reader to it.** Same problems; adds another branch to
  `EffectiveHiddenParameters` rather than removing the ad hoc merge.
- **Per-toolset alias sets that can remove aliases.** Lets eval worlds run with weaker argument correction
  than production, which skews eval results; rejected in favour of add-only file aliases.

## Open questions

- **Static vs instance.** Tool schemas are built lazily from statics when the host resolves the tool
  singletons (`ModelEvalMcpEnvironment.cs:118-119`). Slice 3 needs `ParameterSurface` reachable per host
  (DI singleton read by the patcher); whether the patcher's call path can take an injected instance is
  not yet traced. Hypothesis: a DI-registered `ParameterSurface` with a static fallback during migration.
  Resolved in the plan: `CreateSentinelTool` already receives `IServiceProvider` and the validator methods
  take `McpServer`, so both reach a DI singleton without signature changes.
- **Validation severity.** Recommendation: an unknown tool or undeclared parameter is a `McpServerStatus`
  warning (a toolset may name a tool that is inactive in this mode), and a test asserts zero warnings for
  the shipped toolsets and code defaults. Not decided.
- **Toolsets toggled at runtime.** `McpToolsetControl` enables toolsets per tab mid-session. Recommendation:
  `hideParams` is honoured only for toolsets chosen at startup (modes), since `tools/list` and the
  validator's schema cache are per process. Needs confirming against the JSON toolsets plan.
- **Nested schemas.** Hiding touches only top-level `properties` today (`McpToolSchemaPatcher.cs` remarks
  on `ApplyHiddenParameters`); batch item fields stay out of scope unless a need appears.
- **Hidden-value refusal.** The `validateOnApply:false` refusal is hard-coded
  (`ToolArgumentValidator.cs:1296-1308`). A rule could later carry "values refused while hidden"; out of
  scope for this proposal.

## Cost / risk

- Touches the `tools/list` build path (`McpToolSchemaPatcher`), the call-time validator, startup
  (`ServerStdio.cs:52`, `ServerHttp.cs:40` call `SchemaOptions.Configure`) and the eval harness.
- Wire impact: `tools/list` output is unchanged for existing configurations (an unscoped name still hides
  from every tool); `McpServerStatus` gains a field in slice 4.
- Existing coverage to keep green: `SchemaHiddenParametersTests`, `SchemaLeanProfileTests`,
  `ToolCallHintTests`, the eval schema-visibility tests added in 9cec311d.
- Migration is additive (new path beside `SchemaOptions`, old members `[Obsolete]`), so it can land in
  slices and touches nothing the workflow-contract phase 1 plan needs.

## Related

- [proposal_json_toolset_definitions.md](../proposal_json_toolset_definitions.md) and
  [plan_json_toolset_definitions.md](../../plans/plan_json_toolset_definitions.md) - consumer of `hideParams`.
- [plan_modeleval_workflow_contract_phase1.md](../../plans/closed/plan_modeleval_workflow_contract_phase1.md) - added
  the eval-world hiding of `validateOnApply` (Steps 12-14, 20).
- [proposal_reduce_tool_schema_token_cost.md](proposal_reduce_tool_schema_token_cost.md) - origin of
  `SchemaOptions` and the Lean profile.
- [plan_unknown_parameter_rejection_reduction.md](../../plans/plan_unknown_parameter_rejection_reduction.md) -
  origin of the alias tables (not re-read for this proposal).
