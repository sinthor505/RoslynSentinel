# Plan: Tool-surface controller stage D - parameter rules become a controller input; the snapshot owns hidden parameters

**Status:** IMPLEMENTED 2026-11-24 (Steps 14-15 DEFERRED for the owner, R3; INBOX line). 1df39603, 0acb22ec, 40d0eb26, 8511eda7, 779ddd51, 7f25720f, 056fabaf, b775ae9d, 1c585b6c, e3227a76, d23875b0, 197c56d9, 290eaa9d, 68d0bf23, 99a2b7ca, 3299ae3c; full run green at 99a2b7ca (1,346 s, 0 failures). Approved 2026-11-24: Stage D: parameter rules as a ParameterSurface input of the controller; HiddenParametersPatch, snapshot and validator read it; Steps 14-15 on HOLD for the owner (R3). Review fixes applied; other decisions taken at recommendation (owner away).

Stage 3 of [proposal_tool_surface_controller.md](../../proposals/proposal_tool_surface_controller.md); follows phase A/B ([closed/plan_tool_surface_controller.md](plan_tool_surface_controller.md)) and stage C ([plan_tool_surface_controller_stage_c.md](plan_tool_surface_controller_stage_c.md), aadefa7c..309e94c1); reshapes the paused [plan_tool_parameter_surface_options.md](plan_tool_parameter_surface_options.md) Steps 4-15 (see "What happened to the paused plan").

## Problem

Parameter hiding is the last piece of tool-surface state that the snapshot reports but does not own:

- **The rules live in process statics with no provenance.** Three sources (Lean constant, `--hide-params`, the eval
  harness's `SchemaOptions.ConfigureHiddenParameters`) are merged into one flat name list by
  `SchemaOptions.EffectiveHiddenParameters` (`RoslynSentinel.Common/SchemaOptions.cs:62`), which the patch reads at tool
  build (`McpToolSchemaPatcher.cs:636`, inside `ApplyHiddenParameters`). A name hides on every tool, nothing records which
  source hid it, and an unknown name is kept silently (`SchemaOptions.cs:31` doc: "keeps unknown names").
- **The result lives in a process-wide registry keyed by tool name.** `ApplyHiddenParameters` writes what it stripped into
  `HiddenSchemaParams` (`HiddenSchemaParams.cs:14`, `ConcurrentDictionary<string, HashSet<string>>`, `Register` at
  `McpToolSchemaPatcher.cs:680`). Every reader goes there: the snapshot (`ToolSurfaceController.cs:278`,
  `HiddenSchemaParams.HiddenFor(name)`), the call-time validator (`ToolArgumentValidator.cs:1057`, `:1301`, `:1321`) and
  `ToolCallHint.Call` (`ToolCallHint.cs:96`). In a process that builds more than one host (test fixtures, model-eval
  worlds) the answer is the union over every host ever built, so the eval harness clears and re-sets three statics per
  world (`RoslynSentinel.Tests.ModelEval/ModelEvalMcpEnvironment.cs:118-125`), five server test fixtures repeat the reset
  (`SchemaHiddenParametersTests.cs:43-46` and the `ResetStatics` of `SchemaLeanProfileTests`, `ToolSchemaPatchPipelineTests`,
  `ToolSurfaceControllerTests`, `ToolSurfaceControllerWiringTests`), and the validator carries a defensive guard whose
  comment says so (`ToolArgumentValidator.cs:1293-1294`: "HiddenSchemaParams and SchemaCache are process-wide and keyed by
  tool name"). Proposal properties 1 (one source of truth) and 6 (provenance) do not hold for parameters.
- **The paused plan's code never landed.** `Search(mode: text, query: "ParameterSurface|ParameterVisibilityRule|ToolParameterSurfaceOptions|ParameterRuleSource", includeTests: true)`
  returns 0 matches and no `*ParameterSurface*` file exists (2026-11-24). Its Steps 1-3 ("may run") were not run, so this
  plan starts from the rule model in [proposal_tool_parameter_surface_options.md](../../proposals/proposal_tool_parameter_surface_options.md),
  not from existing code.
- **Phase B moved the hook the paused plan relied on.** Paused Step 5 resolved the surface from `services` at the patcher's
  call site. Since phase B the call goes through `IToolSchemaPatch.Apply(McpServerTool tool, MethodInfo toolMethod)`
  (`ToolSurface/IToolSchemaPatch.cs:21`), which has no services, so `HiddenParametersPatch`
  (`ToolSurface/DefaultToolSchemaPatches.cs:65-76`) cannot reach a per-host rule set at all today.

## Decision

- **Rules are data, resolved once per host.** The rule records from the parameter-surface proposal (`ParameterVisibilityRule`,
  `ParameterRuleSource`, `ParameterVisibility`, `ToolParameterSurfaceOptions`, and the proposal's `HiddenParameter`, renamed
  `HiddenParameterGroup` here) land in `Common/ToolSurface/`. `ParameterSurface` is an immutable resolver over them (no
  strip ledger on it, per the pause note): ordered layers Profile < ToolsetFile < EnvOrFlag < Harness, within a layer
  unscoped before tool-scoped, then list order, last matching rule wins; `Show` with `Parameter == "*"` un-hides every
  parameter in its scope; tool names `OrdinalIgnoreCase`, parameter names `Ordinal` (as `HiddenSchemaParams` today).
- **Rename `HiddenParameter` -> `HiddenParameterGroup` (review 2026-11-24).** The proposal's name sat next to
  `ToolSurfaceTool.HiddenParameters` (a per-tool name list) and `McpServerStatusDeclaredTool.HiddenParameters`; a record of
  one winning rule plus the tools it stripped is a group, so the record and the snapshot field (`HiddenParameterGroups`)
  say so.
- **The controller takes the rules as an input; the snapshot is the view.** `ToolSurfaceController` gains an optional
  `ParameterSurface` constructor input and a `Parameters` property. Every snapshot carries `Parameters`, the hidden
  parameters grouped by winning rule (`HiddenParameterGroups`) and the hide rules that hid nothing (`UnmatchedHideRules`).
  Each tool row's `HiddenParameters` comes from what the patch stripped from that tool instance, not from the name-keyed
  registry. The validator reads the row from `Current`.
- **One `ParameterSurface` instance per host, registered in DI, read by the patch directly.** `RegisterRoslynSentinelToolsBasic`
  does `TryAddSingleton(_ => ParameterSurface.FromStartup())`; a harness registers its own first. The controller receives
  the same instance. The patch never reads it through the controller: resolving the controller resolves
  `IOptions<McpServerOptions>`, whose `McpServerOptionsSetup` takes `IEnumerable<McpServerTool>` in its constructor (SDK
  v2.4.0 `src/ModelContextProtocol/McpServerOptionsSetup.cs:12-15`, verified in the local clone), so every tool - and its
  patches - is built while the controller is being constructed. The standalone singleton has no dependencies, so no
  cycle can form. (Tools are built before the first snapshot until stage F, where the controller builds them; then the
  patch can read the controller's spec.)
- **The patch seam gains services, additively.** `IToolSchemaPatch` gets a default interface member
  `void Apply(McpServerTool tool, MethodInfo toolMethod, IServiceProvider? services) => Apply(tool, toolMethod);`.
  `CreateSentinelTool` calls the three-argument form; only `HiddenParametersPatch` overrides it. The other three defaults and
  any host patch (test `MarkerPatch`) are untouched (5 implementations: 4 in `DefaultToolSchemaPatches.cs`, 1 in
  `ToolSchemaPatchPipelineTests.cs:172`).
- **What was stripped is recorded per tool instance.** `StrippedToolParameters` (a `ConditionalWeakTable<McpServerTool, ...>`
  keyed by instance identity) is written by `ApplyHiddenParameters` and read by `Refresh`. It replaces
  `HiddenSchemaParams.HiddenFor` as the snapshot's source. Being keyed by instance, it cannot leak between hosts and needs
  no `Clear()`. The alternative "declared minus listed" is not used because it is not text-neutral (Risk R2).
- **Migration-first for the old path.** `SchemaOptions.EffectiveHiddenParameters` and `ConfigureHiddenParameters` get
  `[Obsolete("Use X")]` once their production caller moves. `HiddenSchemaParams.HiddenFor` loses its only caller and is
  deleted. The rest of `HiddenSchemaParams` stays as a write-only mirror for `ToolCallHint.Call`, marked `[Obsolete]` in
  Step 11 (about 25 CS0618 sites accepted, so every remaining reader is visible in the build), and is removed in stage E
  with `ToolCallHint` (proposal stage 4 lists `HiddenSchemaParams` and `ToolCallHint` there).
  `SchemaOptions.Profile`/`HiddenParameters`/`Configure` stay: they are the startup-args layer source `FromStartup` reads.
- **Wire-neutral by default.** For every existing configuration `tools/list` is byte-identical (pinned by the parity oracle,
  Step 4), `--hide-params` behaves the same, the validator gives the same answers in a single-host process, and no tool
  description changes. Behaviour changes are separate steps: `Tool.param` scoping in `--hide-params` (Step 12) and, on
  HOLD for the owner, status provenance fields (Steps 14-15).
- **Rules are startup-only in this stage.** No runtime apply changes parameter rules, so no listed tool changes its
  schema after `tools/list`; the proposal's open question (does Claude Code re-read an already-listed tool's schema
  after `list_changed`) is not relied on.

Decisions, 2026-11-24 (review verdict "design holds"; review fixes D1-D3 and the per-step notes applied; owner away, so
R1, R2 and R4-R12 were taken at the recommendation):
- R1 the patch reads the standalone DI `ParameterSurface` singleton. R2 per-instance strip table.
- R3 status provenance fields: **deferred to the owner**; Steps 14-15 stay on HOLD and are recorded as deferred at closing.
- R4 aliases deferred to a follow-on plan. R5 `HiddenSchemaParams` removal in stage E; `[Obsolete]` lands in Step 11.
- R6 exact-equivalent eval rules. R7 Step 12 included. R8-R12 accepted as stated.
- Record rename `HiddenParameter` -> `HiddenParameterGroup` (above).

NEW-DTO blocks (all three go in the `reason` of Step 2's single `ParameterRules.cs` creating call; carried over from the
parameter-surface proposal, domain updated):
```
NEW-DTO: ParameterVisibilityRule (Item, Common.ToolSurface)
Evaluated: SchemaOptions - static holder of flat unscoped name sets, no provenance, not a DTO;
           HiddenSchemaParams - registry of what the patcher already stripped (output), not intent (input)
Wire impact: none (status fields only in the HOLD Step 14)
NEW-DTO: HiddenParameterGroup (Item, Common.ToolSurface)
Evaluated: ToolSurfaceTool - one row per tool, parameter names only, no rule; ParameterVisibilityRule - the rule
           alone, without the tools it stripped
Wire impact: none (status fields only in the HOLD Step 14)
NEW-DTO: ToolParameterSurfaceOptions (Options, Common.ToolSurface)
Evaluated: SchemaOptions - see above; ToolsetDefinition (planned in plan_json_toolset_definitions, no code) - one
           source among several, would couple env/harness rules to the file format
Wire impact: none
```
`ParameterSurface`: `DTO-EXEMPT: resolver service over immutable rules`. `StrippedToolParameters` is a static class
(the hook does not inspect it). The two enums are not DTOs.

### What happened to the paused plan

| Paused step | Here | Why |
| --- | --- | --- |
| 1-3 rules, resolver, tests (never landed) | Folded into Steps 2, 3a, 3b, 5 | Reshaped: `ParameterSurface` is immutable (no `RecordStripped`/`IsStripped`/`StrippedParameters`); strips are per tool instance (`StrippedToolParameters`); attribution and unmatched rules are pure functions over snapshot rows; aliases left out (R4). |
| 4 register the singleton | Kept, reshaped: Step 6 | Same `TryAddSingleton`, but the controller takes it as input and consumers read the snapshot, not the singleton. |
| 5 patcher strips by the surface | Kept, reshaped: Step 7 (text-neutral part) and Step 12 (`Tool.param`) | Split so the text-neutral move lands first; reaches the surface through the new `IToolSchemaPatch` overload because phase B put the seam in between. |
| 6 validator reads the surface | Folded into Step 8 | The validator reads the snapshot row from `Current` instead of the singleton (proposal: consumers read the snapshot). |
| 7 per-tool scope tests | Kept as-is: Step 13 | Same two tests, paired with Step 12. |
| 8 eval harness registers its own surface | Kept, reshaped: Step 10 | Rules chosen to be exactly equivalent to the old static (R6); STOP gate on any eval schema change. |
| 9-10 status fields and tests | Reshaped, HOLD: Steps 14-15 | Per-tool names already reach status (`ServerStatusTools.cs:114`, phase A5). The new fields grow `McpServerStatus`'s output schema against a claude-lean budget with no headroom (R3). |
| 11-14 aliases | Dropped from this plan, deferred | No second alias source exists until the toolset-file layer (stage G); moving 115 lines of tables now buys provenance only (R4). |
| 15 stub sweep | Kept: Step 18 | Same check over the new files. |

## Execution rules

- Start after stage C Step 12 (full run) is recorded in the stage C plan; the expected baseline is that run at 9c5fdaf2
  (the main session closes stage C before this plan runs).
- Dispatch per the `dispatch-implementer` skill; depth tier D1 at writing (files <= 3, edits <= 15, acceptance <= 1,
  tool calls <= 35). No step is a sweep. At most two implementing agents at once.
- One `Build`/`RunTest` at a time (paired lanes included; the dispatcher serializes builds); `Build` 0 errors per step;
  the dispatcher runs the named tests; test baseline 0 failures.
- **Byte-identity gate.** `HiddenParametersParityTests` is the only true oracle: it compares each listed schema with a
  frozen copy of today's stripping code. `ToolSchemaPatchPipelineTests.DefaultPipeline_SchemasMatchUnregisteredFallback`
  runs the same patch code in both of its hosts (it catches a DI-versus-fallback divergence, not a change in what is
  stripped), and `ClaudeLeanSchemaBudgetTests` only caps size. All three are still run as the gate.
- **Gate timing: synchronous and pre-commit.** For Steps 7, 8, 10 and 11 the dispatcher runs the gate tests (the byte-identity
  gate above, plus the step's own Done-when tests, plus the eval set for Steps 8 and 10) as a synchronous `RunTest` on the
  uncommitted change, before committing - not the default detached post-commit run. A red gate means the step is not
  committed; that is what makes the STOP rule below implementable. A gate failure is never fixed by editing descriptions
  or the budget.
- **Eval STOP rule (owner away overnight).** Any step whose acceptance shows a changed `EvalToolSchemaVisibilityTests` outcome
  (the only test that asserts listed parameter schemas in eval worlds), or a changed `ChainEvalToolSurfaceTests`,
  `ChainEvalToolSurfaceLeanTests` or `EditArmToolSurfaceTests` outcome (tool names only), is not committed. Record the diff
  in this plan and stop the run. No step in this plan is meant to change what an eval model sees.
- No tool `[Description]` or schema text changes outside the HOLD Step 14.
- Expected new CS0618 warnings (compare with `docs/known-build-warnings.*.txt` and report them as expected, not new
  defects): `EffectiveHiddenParameters` (from Step 7: `SchemaHiddenParametersTests.cs:259`, suppressed in
  `HiddenParametersParityTests`), `ConfigureHiddenParameters` (from Step 10: the four test resets at
  `SchemaHiddenParametersTests.cs:44`, `ToolSchemaPatchPipelineTests.cs:46`, `ToolSurfaceControllerTests.cs:34`,
  `ToolSurfaceControllerWiringTests.cs:36`), `HiddenSchemaParams` (from Step 11: about 25 remaining references, mostly
  test resets and asserts, plus `ToolCallHint.cs:96`, the validator fallback and `ModelEvalMcpEnvironment.cs:123`).
- `Search` each file for `TODO` before editing it (CLAUDE.md); a new `// TODO` gets its `docs/current/INBOX.md` line.
- Peer check: before Step 7 (`McpToolSchemaPatcher.cs`) and Step 10 (`RoslynSentinel.Tests.ModelEval/`) run `Git(status)`
  and `ListAgents`; punchlist #20 was trimming schema descriptions on 2026-11-24 (273e6734, a1ea6ac6).
- Commits carry `Plan: plan_tool_surface_controller_stage_d`; Step 18's commit carries
  `Closes: plan_tool_surface_controller_stage_d` with Steps 14-15 recorded as deferred (HOLD, R3).
- Live checks only after commit and an own-server restart, with no subagent mid-operation.

## Steps

Order: docs pointer on approval; text-neutral Steps 2-11; behaviour-changing Steps 12-13; HOLD Steps 14-15 (only with
owner approval); LIVE; docs; closing.

Slice map: 1; 2; 3a || 4; 3b; 5 || 6; 7; 8; 9 || 10; 11 || 12; 13; [14; 15 only after R3 is approved]; 16; 17; 18.
Step 4 depends on nothing (it pins current behaviour) and must be green before Step 7 lands. Step 6 needs 3b, because
`Refresh` calls `AttributeHidden`/`UnmatchedHideRules`, which are stubs until 3b.

Batches for the run:
- **B1** = Steps 1-6 (1; 2; 3a || 4; 3b; 5 || 6).
- **B2** = Steps 7-10 (7; 8; 9 || 10).
- **B3** = Steps 11-13 (11 || 12; 13), then the main session runs Steps 16-18. Steps 14-15 stay on HOLD.

### Step 1 - On approval: point the paused plan here
- Done: this commit (approval)
- Files: `docs/current/plans/plan_tool_parameter_surface_options.md` (Status line only)
- Depends on: owner approval of this plan
- Effect: none (docs)
- Lane: main session
- Change: replace only the Status line with
  `**Status:** SUPERSEDED <approval date>. Reshaped into [plan_tool_surface_controller_stage_d.md](plan_tool_surface_controller_stage_d.md) (stage 3 of the controller proposal); Steps 1-3 never landed, Steps 4-15 are mapped in that plan's "What happened to the paused plan" table.`
  Nothing else in the file changes (it moves to `plans/closed/` when this plan closes, Step 18).
- Done when: the Status line links here; the link resolves.

### Step 2 - Contract skeleton
- Done: 1df39603
- Files: `RoslynSentinel.Common/ToolSurface/ParameterRules.cs` (new), `RoslynSentinel.Common/ToolSurface/ParameterSurface.cs` (new),
  `RoslynSentinel.Common/ToolSurface/IToolSchemaPatch.cs`
- Depends on: Step 1 (approval)
- Effect: text-neutral (no callers)
- Lane: implementer. `ParameterRules.cs` is written in **one full-content creating call** whose `reason` carries all three
  NEW-DTO blocks above (the dto hook checks every record in the call, so no `CreateFile`-then-`Member`-add split);
  `ParameterSurface.cs` in one creating call with the `DTO-EXEMPT` line; one `ReplaceSnippet` on `IToolSchemaPatch.cs`.
- Change: namespace `RoslynSentinel.Common`; XML doc comments on every public member.
  ```csharp
  // ParameterRules.cs (new)
  public enum ParameterRuleSource { Profile, ToolsetFile, EnvOrFlag, Harness }   // later layers win
  public enum ParameterVisibility { Hide, Show }
  // Tool null = every tool. Parameter "*" only with Show. Origin: human-readable pointer for diagnostics.
  public sealed record ParameterVisibilityRule(string? Tool, string Parameter, ParameterVisibility Action,
      ParameterRuleSource Source, string Origin);
  // One group per winning Hide rule: the parameter, the rule's source and origin, the tools it was stripped from (ordinal).
  public sealed record HiddenParameterGroup(string Parameter, ParameterRuleSource Source, string Origin, IReadOnlyList<string> Tools);
  public sealed record ToolParameterSurfaceOptions(IReadOnlyList<ParameterVisibilityRule> Visibility)
  {
      public static ToolParameterSurfaceOptions Empty { get; } = new([]);
      // Lean list as Profile rules when SchemaOptions.Profile is Lean, then each SchemaOptions.HiddenParameters name as an
      // unscoped EnvOrFlag rule (ordinal order). Reads the statics when called.
      public static ToolParameterSurfaceOptions FromStartup() => throw new NotImplementedException(); // TODO implement (Step 3a)
      // Splits on ',' and ';', trims, drops empties; "Tool.param" (exactly one '.', both sides non-empty) scopes to a tool,
      // anything else is an unscoped Hide rule for the literal text. Never throws.
      public static IReadOnlyList<ParameterVisibilityRule> ParseHideList(string? raw, ParameterRuleSource source, string origin)
          => throw new NotImplementedException(); // TODO implement (Step 3a)
  }

  // ParameterSurface.cs (new)
  public sealed class ParameterSurface
  {
      public ParameterSurface(ToolParameterSurfaceOptions options) => throw new NotImplementedException(); // TODO implement (Step 3a)
      public static ParameterSurface FromStartup() => throw new NotImplementedException(); // TODO implement (Step 3a)
      public ToolParameterSurfaceOptions Options { get; }
      public bool HidesAnything { get; }   // true when any Hide rule exists
      public ParameterVisibilityRule? ResolveHidden(string toolName, string parameterName) => throw new NotImplementedException(); // TODO implement (Step 3a)
      public IReadOnlyList<HiddenParameterGroup> AttributeHidden(IReadOnlyList<ToolSurfaceTool> tools) => throw new NotImplementedException(); // TODO implement (Step 3b)
      public IReadOnlyList<ParameterVisibilityRule> UnmatchedHideRules(IReadOnlyList<ToolSurfaceTool> tools) => throw new NotImplementedException(); // TODO implement (Step 3b)
  }
  public static class StrippedToolParameters
  {
      public static void Record(McpServerTool tool, IReadOnlyList<string> parameterNames) => throw new NotImplementedException(); // TODO implement (Step 3a)
      public static IReadOnlyList<string> For(McpServerTool tool) => throw new NotImplementedException(); // TODO implement (Step 3a)
  }

  // IToolSchemaPatch.cs, new member, implemented (not a stub):
  /// Adjusts the tool's schema with access to the host's services; services is null for a tool built without a host
  /// (the reflection catalog, direct test construction). Defaults to Apply(tool, toolMethod).
  void Apply(McpServerTool tool, MethodInfo toolMethod, IServiceProvider? services) => Apply(tool, toolMethod);
  ```
- Done when: `Build` 0 errors.

### Step 3a - Implement options, parser, resolver and the strip table
- Done: 0acb22ec
- Files: `RoslynSentinel.Common/ToolSurface/ParameterRules.cs`, `RoslynSentinel.Common/ToolSurface/ParameterSurface.cs`
- Depends on: 2
- Pairs with: Step 4
- Effect: text-neutral (no callers)
- Lane: implementer
- Change:
  - `FromStartup`: when `SchemaOptions.Profile == SchemaProfile.Lean`, one rule per `SchemaOptions.LeanHiddenParameters`
    entry in list order (`Tool` null, `Hide`, `Profile`, origin `"--schema-profile=lean"`); then one rule per
    `SchemaOptions.HiddenParameters` entry in ordinal order (`Tool` null, `Hide`, `EnvOrFlag`, origin
    `"--hide-params / ROSLYNSENTINEL_HIDE_PARAMS"`), the entry text used verbatim as `Parameter` - a dotted entry stays an
    unscoped literal that matches no property, exactly as today (Step 12 changes this). Comment that point.
  - `ParseHideList` per the skeleton comment.
  - Constructor: keep `options`; order the rules once with a stable sort by `Source`, then unscoped before scoped,
    preserving list order; `HidesAnything` = any `Hide` rule.
  - `ResolveHidden`: walk the ordered rules; a rule matches when (`Tool` is null or equals `toolName`
    `OrdinalIgnoreCase`) and (`Parameter == parameterName` ordinal, or `Action == Show && Parameter == "*"`); the last
    match wins; return it if it is a `Hide`, else null. Return the rule instance from the ordered list (Step 3b groups by
    instance).
  - `StrippedToolParameters`: `private static readonly ConditionalWeakTable<McpServerTool, string[]> Table`; `Record`
    stores an ordinal-sorted copy (`AddOrUpdate`); `For` returns the stored array or `[]`. Comment: keyed by instance
    identity, so a host never sees another host's strips and nothing needs clearing.
  - `ParameterSurface.FromStartup()` = `new ParameterSurface(ToolParameterSurfaceOptions.FromStartup())`.
- Done when: `Build` 0 errors.

### Step 3b - Implement attribution and unmatched rules
- Done: 8511eda7 (+779ddd51 comment cleanup)
- Files: `RoslynSentinel.Common/ToolSurface/ParameterSurface.cs`
- Depends on: 3a
- Effect: text-neutral (no callers)
- Lane: implementer
- Change:
  - `AttributeHidden`: for each row and each name in `row.HiddenParameters`, `ResolveHidden(row.Name, name)`; group by the
    winning rule **by reference** (`ReferenceEqualityComparer.Instance`: rules are records, so value equality would merge two
    identical rules from different layers or origins) into `HiddenParameterGroup(rule.Parameter, rule.Source, rule.Origin,
    tools ordinal-sorted)`, groups ordered by `Parameter` then `Source`. A name with no winning rule (only when a tool was
    built by another surface than this one) is omitted; comment why.
  - `UnmatchedHideRules`: `Hide` rules in the ordered list that are the winning rule (by reference) for no (row, hidden name)
    pair, in list order.
- Done when: `Build` 0 errors.

### Step 4 - Parity oracle for hidden parameters
- Done: 40d0eb26
- Files: `RoslynSentinel.Tests.Server/HiddenParametersParityTests.cs` (new)
- Depends on: none (pins today's behaviour; must pass before Step 7 lands and at every later gate)
- Pairs with: Step 3a
- Effect: text-neutral
- Lane: test-writer
- Change: `[NonParallelizable]` fixture.
  - **One `[TestCaseSource]` case per (config, host flavor, mode set)**, so SetUp/TearDown reset statics per case as
    `ToolSchemaPatchPipelineTests.ResetStatics` does (`HiddenSchemaParams` is a process-wide union, `HiddenSchemaParams.cs:14`).
    Configs: no args; `--schema-profile=lean`; `--hide-params=validateOnApply,returnDiff`;
    `--schema-profile=lean --hide-params=dryRun,validateOnAply`. Host flavors: Basic (`AddRoslynSentinelEnginesBasic` +
    `AddRoslynSentinelToolsBasic`) and Advanced (`AddRoslynSentinelEnginesAdvanced` + `AddRoslynSentinelToolsAdvanced`,
    pattern of `ClaudeLeanSchemaBudgetTests.Measure`), so Advanced-only tools are covered. Mode sets:
    `Workspace,Refactor,WholeFileWrite` and `claude-lean`.
  - Per case: call `SchemaOptions.Configure(args)`, then build host A (default registration) and host B
    (`RemoveAll<IToolSchemaPatch>()` plus the three defaults other than `HiddenParameters`, pattern of
    `ToolSchemaPatchPipelineTests.RemovedDefaultPatch_IsNotApplied`). Tool build is lazy (`ToolSurfaceControllerTests.cs`
    around `:72`), so **resolve each host's `IOptions<McpServerOptions>.Value` and A's registered `ToolSurfaceController.Current`
    immediately after building it**, before any later `SchemaOptions.Configure` call.
  - A private static `LegacyStrip(JsonElement schema, IReadOnlyList<string> hidden, out List<string> removed)` copied from
    today's `ApplyHiddenParameters` body (`McpToolSchemaPatcher.cs:636-679`), including the early returns that leave the
    schema text untouched when nothing is removed; comment that it is the frozen oracle. Its input is
    `SchemaOptions.EffectiveHiddenParameters()` under `#pragma warning disable CS0618` with a comment (obsolete from Step 7).
  - Tests: `ListedSchemas_EqualLegacyStripOfThePipelineWithoutTheHiddenPatch` (per tool, ordinal equality of A's
    `InputSchema.GetRawText()` and `LegacyStrip(B's schema)`), `SnapshotHiddenParameters_EqualLegacyRemovedNames` (A's
    `Current.Find(name).HiddenParameters` equals the oracle's `removed`, ordinal-sorted).
- Done when: dispatcher runs `HiddenParametersParityTests` on the current code: all pass.

### Step 5 - Resolver unit tests
- Done: 7f25720f
- Files: `RoslynSentinel.Tests/ParameterSurfaceTests.cs` (new)
- Depends on: 3b
- Pairs with: Step 6
- Effect: text-neutral
- Lane: test-writer
- Change: no MCP host needed; build `ToolSurfaceTool` rows by hand. Tests: `LaterLayerWins`, `ScopedRuleBeatsUnscopedInSameLayer`,
  `ShowStar_UnhidesLowerLayers`, `ShowStar_ScopedToTool_LeavesOtherToolsHidden`, `ToolNameIgnoresCase_ParameterNameIsOrdinal`,
  `ParseHideList_ToolDotParam_ScopesToTool`, `ParseHideList_BlankOrSeparatorsOnly_GivesEmpty`,
  `ParseHideList_MalformedDots_StayUnscopedLiterals` (`"a.b.c"`, `".x"`, `"x."`),
  `FromStartup_LeanAndExplicit_HidesExactlyEffectiveHiddenParameters` (for every name in the Lean list, the explicit set and
  one unlisted name: `ResolveHidden("AnyTool", name) != null` iff the name is in `EffectiveHiddenParameters()`; CS0618
  suppressed with a comment; `[NonParallelizable]`, statics reset in TearDown), `FromStartup_DottedEntry_IsAnUnscopedLiteral`
  (pins the text-neutral form; Step 12 flips it), `AttributeHidden_GroupsToolsByWinningRule`,
  `AttributeHidden_EqualRulesFromTwoLayers_StaySeparateGroups` (pins the reference grouping),
  `AttributeHidden_OmitsNamesWithNoWinningRule`, `UnmatchedHideRules_ListsRuleThatWonNothing`,
  `HidesAnything_FalseForEmptyOrShowOnly`.
- Done when: dispatcher runs `ParameterSurfaceTests`: all pass.

### Step 6 - Register the surface; the controller takes it; the snapshot carries it
- Done: 056fabaf
- Files: `RoslynSentinel.Server.Advanced/ServiceRegistrationExtensionsBasic.cs`, `RoslynSentinel.Common/ToolSurface/ToolSurfaceController.cs`,
  `RoslynSentinel.Common/ToolSurface/ToolSurfaceSnapshot.cs`
- Depends on: 3b
- Effect: text-neutral (nothing on the wire reads the new snapshot fields; rows still come from `HiddenSchemaParams`)
- Lane: implementer
- Change:
  - `ServiceRegistrationExtensionsBasic.cs`: right after the default-patch loop (`:371-374`, `RegisterRoslynSentinelToolsBasic`)
    `services.TryAddSingleton(_ => ParameterSurface.FromStartup());` with a comment: a host (the eval harness, Step 10)
    registers its own first; built on first resolve, which is when tools are built, so it reads `SchemaOptions` after
    `SchemaOptions.Configure` exactly as the patch does today.
  - Controller constructor, in place: `public ToolSurfaceController(IOptions<McpServerOptions> serverOptions, IEnumerable<IToolSchemaPatch>? patches = null, ParameterSurface? parameters = null)`
    (today `ToolSurfaceController.cs:47`). Blast radius (2026-11-24): 14 construction sites - DI
    `AddSingleton<ToolSurfaceController>()` (`ServiceRegistrationExtensionsBasic.cs:397`), 2 `ActivatorUtilities.CreateInstance`
    (`ToolSchemaPatchPipelineTests.cs:162`, `:166`), 11 `new` in 7 test files - all source-compatible with an optional
    trailing parameter, so none is edited. Comment that the single-constructor rule of stage C still holds.
    New `public ParameterSurface Parameters { get; }` = `parameters ?? ParameterSurface.FromStartup()`.
  - `Refresh`: after the rows are sorted, compute `Parameters.AttributeHidden(rows)` and `Parameters.UnmatchedHideRules(rows)`
    and pass them, with `Parameters`, to the new snapshot. Fingerprint unchanged (rules are startup-only in this stage; a
    rule that changes stripping already changes schema text).
  - `ToolSurfaceSnapshot`: optional trailing `ParameterSurface? Parameters = null, IReadOnlyList<HiddenParameterGroup>? HiddenParameterGroups = null,
    IReadOnlyList<ParameterVisibilityRule>? UnmatchedHideRules = null` with param docs; `Empty` unchanged.
- Done when: `Build` 0 errors (also `scripts/build.ps1 -Flavor Solution`); dispatcher runs `ToolSurfaceControllerTests`,
  `ToolSurfaceControllerApplyTests`, `ToolSurfaceControllerWiringTests`, `ToolSchemaPatchPipelineTests`, `McpServerStatusToolListingTests`: all pass.

### Step 7 - HiddenParametersPatch strips by the surface and records per tool instance
- Done: b775ae9d
- Files: `RoslynSentinel.Common/McpToolSchemaPatcher.cs`, `RoslynSentinel.Common/ToolSurface/DefaultToolSchemaPatches.cs`,
  `RoslynSentinel.Common/SchemaOptions.cs`
- Depends on: 3a, 6 (so DI hosts use their registered surface); acceptance needs Step 4 landed
- Effect: text-neutral (same names stripped for every existing configuration, same bytes, same mirror registrations)
- Lane: implementer
- Change:
  - `CreateSentinelTool` (the `:182` overload): the patch loop calls `patch.Apply(tool, toolMethod, services)`.
  - `HiddenParametersPatch`: implement the three-argument `Apply` as
    `McpToolSchemaPatcher.ApplyHiddenParameters(tool, toolMethod, services?.GetService<ParameterSurface>() ?? ParameterSurface.FromStartup())`;
    the two-argument `Apply` calls the three-argument `Apply` with `services: null`. Comment: reads the standalone singleton,
    never the controller (see Decision and R1); the fallback covers `services: null!` from `ToolSignatureCatalogReflection.cs:77`
    and direct test builds.
  - `ApplyHiddenParameters(McpServerTool tool, MethodInfo toolMethod, ParameterSurface surface)` (signature change in place;
    1 caller, the patch). Body: return when `!surface.HidesAnything` (the old `hidden.Count == 0` fast path); parse as now;
    iterate a copy of the top-level `properties` names and remove each one for which
    `surface.ResolveHidden(tool.ProtocolTool.Name, name)` is non-null; when nothing was removed return without assigning
    `InputSchema` (re-serializing an unchanged schema can change bytes); `required` handling unchanged; assign
    `InputSchema`; `StrippedToolParameters.Record(tool, removed)`; keep `HiddenSchemaParams.Register(...)` with
    `// TODO HiddenSchemaParams is the ToolCallHint mirror; remove with ToolCallHint's move to the snapshot (stage E)`.
    Update the doc comment (`:620-633`): rules come from `ParameterSurface`; strips are recorded per instance.
  - `SchemaOptions.EffectiveHiddenParameters`: `[Obsolete("Use ParameterSurface.ResolveHidden")]` (production caller gone;
    remaining: `SchemaHiddenParametersTests.cs:259` and the suppressed oracle in Step 4).
- Done when: `Build` 0 errors; synchronous pre-commit `RunTest` of `HiddenParametersParityTests`, `ToolSchemaPatchPipelineTests`,
  `SchemaHiddenParametersTests`, `SchemaLeanProfileTests`, `SchemaDataTagOptionTests`, `ReplaceSnippetSchemaTests`,
  `BatchItemSchemaTests`, `McpToolsetControlTests`, `ToolCallHintTests`, `ClaudeLeanSchemaBudgetTests`: all pass.

### Step 8 - Snapshot rows and the validator read the per-instance view
- Done: 1c585b6c
- Files: `RoslynSentinel.Common/ToolSurface/ToolSurfaceController.cs`, `RoslynSentinel.Server.Advanced/ToolArgumentValidator.cs`
- Depends on: 6, 7
- Effect: text-neutral in a single-host process (production). In a multi-host process (tests, eval worlds) the snapshot rows
  and the validator's hidden check now answer for this host only instead of the union of every host built so far.
- Lane: implementer
- Change:
  - `Refresh`: the row's `HiddenParameters` becomes `StrippedToolParameters.For(tool)` (replaces
    `HiddenSchemaParams.HiddenFor(name)` at `:278`; ordinal-sorted both ways).
  - `ToolArgumentValidator`: new
    `private static bool IsHiddenParameter(ModelContextProtocol.Server.McpServer? server, string? toolName, string parameterName)`:
    false for an empty tool name; when `server?.Services?.GetService<RoslynSentinel.Common.ToolSurfaceController>()` is
    present, `Current.Find(toolName)?.HiddenParameters` contains `parameterName` (ordinal; `Find` is ordinal, which is
    correct because `TryGetSchemaParameters` has already matched the tool name ordinally); otherwise
    `HiddenSchemaParams.IsHidden(toolName, parameterName)` with `// TODO remove the HiddenSchemaParams fallback with the registry (stage E)`.
    Replace the three calls at `:1057` (`WrapFlatBatchParameters`), `:1301` and `:1321` (`Validate`); both methods already
    take `server`. Reword the comment at `:1293-1294`: hidden names come from this server's snapshot; `SchemaCache` is still
    process-wide and keyed by tool name (stage E), so the `!declared.Contains(...)` guard stays.
  - Note for the reviewer: on the controller path,
    `SchemaHiddenParametersTests.Refusal_WhenSchemaDeclaresTheParameter_IgnoresAStaleHiddenRegistration` (`:237`) becomes
    trivially true (the registry is no longer read), so it no longer guards the `!declared.Contains` refusal guard. Step 9
    adds the controller-path counterpart; the registry-fallback path keeps no client-level test (R8).
- Done when: `Build` 0 errors; synchronous pre-commit `RunTest` of `SchemaHiddenParametersTests`, `SchemaLeanProfileTests`,
  `ToolSurfaceControllerTests`, `HiddenParametersParityTests`, `ToolSchemaPatchPipelineTests`, `ClaudeLeanSchemaBudgetTests`,
  `ToolParameterAliasTests`, `SurfaceAwareHintsArgumentValidatorTests`, and in ModelEval `EvalToolSchemaVisibilityTests`
  (all 7; eval STOP rule applies): all pass.

### Step 9 - Per-host parameter surface tests
- Done: e3227a76
- Files: `RoslynSentinel.Tests.Server/ToolSurfaceParameterTests.cs` (new)
- Depends on: 8
- Pairs with: Step 10
- Effect: text-neutral
- Lane: test-writer
- Change: hosts built like `ToolSurfaceControllerTests.BuildHarness`; `[NonParallelizable]`, statics reset per test. Tool
  build is lazy: in every multi-host test, resolve host A's `IOptions<McpServerOptions>.Value` and controller `Current`
  before host B's `SchemaOptions.Configure`. Tests:
  - `Snapshot_CarriesTheRegisteredParameterSurface` (same instance as `GetRequiredService<ParameterSurface>()`).
  - `Snapshot_HiddenParameterGroups_NameTheWinningRule` (Lean: a `validateOnApply` group, source `Profile`, origin
    `--schema-profile=lean`, tools include `ReplaceSnippet`).
  - `Snapshot_UnmatchedHideRules_ListsMisspeltHideParam` (`--hide-params=validateOnAply`).
  - `TwoHostsInOneProcess_SnapshotRowsArePerHost` (host A with `--hide-params=validateOnApply`, resolved; then host B without,
    no `HiddenSchemaParams.Clear()` between: B's `ReplaceSnippet` row has no hidden parameters).
  - `HarnessRegisteredSurface_WinsOverStartupDefault` (register
    `new ParameterSurface(new ToolParameterSurfaceOptions([new(null, "returnDiff", ParameterVisibility.Hide, ParameterRuleSource.Harness, "test")]))`
    before `AddRoslynSentinelToolsBasic`; after the host builds and its tools resolve, call `HiddenSchemaParams.Clear()` so
    acceptance can only come from the snapshot: `returnDiff` absent from listed schemas, a call passing it is accepted,
    `Current.Parameters` is that instance).
  - `StaleRegistryEntry_IsIgnored_WhenAControllerIsRegistered` (controller-path counterpart of the `:237` test:
    `HiddenSchemaParams.Register("ReplaceSnippet", ["notAParameter"])`, then a call passing `notAParameter` is rejected as
    "Unknown parameter").
  - `ToolsetEnabledTool_IsStrippedByTheSameSurface` (claude-lean, harness surface hiding a parameter of a toolset tool;
    `SetEnabled` that set: the added tool's schema lacks it and its row lists it).
  - `WrapPath_HiddenKeyIsNotTreatedAsAFlatItemKey` (covers `:1057`, which no existing test exercises - `Search` for
    `FlatBatch|flat edit` found none on 2026-11-24; if no probe tool with a batch item property named like a hidden top-level
    parameter can be built here, report that and leave the gap in R8).
- Done when: dispatcher runs `ToolSurfaceParameterTests`: all pass.

### Step 10 - Eval harness supplies a Harness-layer surface
- Done: d23875b0 (+99a2b7ca comment restore)
- Files: `RoslynSentinel.Tests.ModelEval/ModelEvalMcpEnvironment.cs`, `RoslynSentinel.Common/SchemaOptions.cs`
- Depends on: 7, 8
- Effect: text-neutral by construction; **eval-surface step** (it builds the surface eval models see; eval STOP rule applies)
- Lane: implementer
- Change:
  - Replace `SchemaOptions.ConfigureHiddenParameters(...)` (`:125`) with a registration placed before the
    `configureToolSurface` branch (`:127`), so both branches (`ChainEvalToolSurface` default and `EditArmToolSurface`
    callers) get it:
    ```csharp
    // ConfigureHiddenParameters used to replace the explicit (--hide-params) set, so the EnvOrFlag layer is dropped and
    // the Profile layer kept; fullToolSchema adds no rule. Exactly the old effective list (R6).
    var rules = ToolParameterSurfaceOptions.FromStartup().Visibility
        .Where(r => r.Source != ParameterRuleSource.EnvOrFlag).ToList();
    if (!fullToolSchema)
        rules.Add(new ParameterVisibilityRule(null, "validateOnApply", ParameterVisibility.Hide, ParameterRuleSource.Harness, "ModelEvalMcpEnvironment"));
    services.AddSingleton(new ParameterSurface(new ToolParameterSurfaceOptions(rules)));
    ```
  - Keep `HiddenSchemaParams.Clear()` and `ClearSchemaCacheForTests()`; reword the comment at `:118-122`: hidden-parameter
    rules are per host now; the `ToolCallHint` mirror and the validator schema cache are still process-static (stage E).
  - `SchemaOptions.ConfigureHiddenParameters`: `[Obsolete("Register a ParameterSurface in DI")]` (remaining callers: the
    four test resets listed in Execution rules).
  - Hint text: the reflection catalog (`services: null`) now lists `validateOnApply` as an optional parameter in eval worlds
    (it no longer sees the harness's hiding). `ToolCallHint` renders only supplied or required parameters, and no production
    builder supplies `validateOnApply` (`Search` for `("validateOnApply"` 2026-11-24: only the validator itself), so no hint
    text changes. Note it in the commit message.
  - STOP-rule test set: `EvalToolSchemaVisibilityTests` is the only test that asserts listed parameter schemas in eval worlds;
    `ChainEvalToolSurfaceTests`, `ChainEvalToolSurfaceLeanTests` and `EditArmToolSurfaceTests` assert tool names only. All
    four are in the set; a parameter-schema regression can only show in the first.
- Done when: `Build` 0 errors; synchronous pre-commit `RunTest` of `HiddenParametersParityTests`,
  `ToolSchemaPatchPipelineTests.DefaultPipeline_SchemasMatchUnregisteredFallback`, `ClaudeLeanSchemaBudgetTests` and ModelEval
  `EvalToolSchemaVisibilityTests` (all 7, including `OptIn_AfterADefaultWorld_IsNotRefused` and
  `Default_AfterAnOptInWorld_HidesAgain`), `ChainEvalToolSurfaceTests`, `ChainEvalToolSurfaceLeanTests`, `EditArmToolSurfaceTests`:
  all pass with unchanged outcomes (any change: STOP, do not commit).

### Step 11 - HiddenSchemaParams becomes the ToolCallHint mirror
- Done: 197c56d9
- Files: `RoslynSentinel.Common/HiddenSchemaParams.cs`, `RoslynSentinel.Common/ToolCallHint.cs`, `RoslynSentinel.Common/McpToolSchemaPatcher.cs`
- Depends on: 8, 10
- Pairs with: Step 12 (disjoint files)
- Effect: text-neutral (about 25 new CS0618 warnings, expected; see Execution rules)
- Lane: implementer
- Change: `Search(mode: references)` on `HiddenSchemaParams.HiddenFor` must return 0 (its one caller moved in Step 8);
  delete it. Mark the class
  `[Obsolete("Hidden parameters are per host: read ToolSurfaceSnapshot rows (ToolSurfaceTool.HiddenParameters). Kept only as the ToolCallHint mirror until stage E.")]`
  and rewrite its summary to say so. Decision: `[Obsolete]` lands now rather than in stage E, so the about 25 remaining
  references (test resets and asserts, `ToolCallHint.cs:96`, the validator fallback, `ModelEvalMcpEnvironment.cs:123`)
  show in the build as the stage E work list. `ToolCallHint.cs:96`:
  `// TODO read hidden parameters from the snapshot's hint catalog (stage E), then delete HiddenSchemaParams`.
  The mirror write in `ApplyHiddenParameters` keeps its Step 7 TODO; wrap it in `#pragma warning disable/restore CS0618`
  with a one-line comment (the only intended production write). Leave the other warnings standing.
- Done when: `Build` 0 errors; synchronous pre-commit `RunTest` of `ToolCallHintTests`, `SchemaHiddenParametersTests`,
  `SchemaLeanProfileTests`, `HiddenParametersParityTests`, `ToolSchemaPatchPipelineTests`, `ClaudeLeanSchemaBudgetTests`: all pass.

### Step 12 - Tool-scoped entries in --hide-params
- Done: 290eaa9d
- Files: `RoslynSentinel.Common/ToolSurface/ParameterRules.cs`, `RoslynSentinel.Common/SchemaOptions.cs`, `RoslynSentinel.Tests/ParameterSurfaceTests.cs`
- Depends on: 7 (and 5, same test file)
- Effect: **behaviour-changing**: a `--hide-params` / `ROSLYNSENTINEL_HIDE_PARAMS` entry of the form `Tool.param` now hides
  `param` on that tool only. Today such an entry matches no property and hides nothing. Every other entry, the default
  configuration and the eval harness (Harness rules since Step 10) are unchanged. No script, config or launch file passes
  a dotted entry (Grep of the repo for `hide-params|HIDE_PARAMS`, 2026-11-24: code, tests and docs only). A typo in a
  `Tool.param` entry (wrong tool or parameter) still hides nothing silently: it appears only in the snapshot's
  `UnmatchedHideRules`, which reaches the wire only through the HOLD Steps 14-15.
- Lane: implementer
- Change: `FromStartup` builds the EnvOrFlag rules with
  `ParseHideList(string.Join(",", SchemaOptions.HiddenParameters.OrderBy(n => n, StringComparer.Ordinal)), ParameterRuleSource.EnvOrFlag, "--hide-params / ROSLYNSENTINEL_HIDE_PARAMS")`.
  Update the doc comments of `SchemaOptions.HiddenParameters` and `Configure` (`Tool.param` scopes to one tool). Flip
  `ParameterSurfaceTests.FromStartup_DottedEntry_IsAnUnscopedLiteral` to `FromStartup_DottedEntry_ScopesToTool`.
- Done when: `Build` 0 errors; dispatcher runs `ParameterSurfaceTests`, `HiddenParametersParityTests`, `SchemaHiddenParametersTests`: all pass.

### Step 13 - Tool-scope tests
- Done: 68d0bf23
- Files: `RoslynSentinel.Tests.Server/SchemaHiddenParametersTests.cs`
- Depends on: 12
- Effect: text-neutral
- Lane: test-writer
- Change (paused Step 7, unchanged): `HideParams_ToolScoped_HidesOnlyOnThatTool` (`--hide-params=ReplaceSnippet.validateOnApply`:
  absent from `ReplaceSnippet`, still listed on `ApplyDiff`, which declares `validateOnApply` in the `WholeFileWrite` mode)
  and `HideParams_ToolScoped_HiddenArgumentStillAccepted` (call `ReplaceSnippet` with `validateOnApply: true`: no
  "Unknown parameter").
- Done when: dispatcher runs `SchemaHiddenParametersTests`: all pass.

### Step 14 - HOLD (owner decision R3): McpServerStatus reports parameter provenance
- Done: DEFERRED (HOLD, owner decision R3; INBOX line)
- Files: `RoslynSentinel.Tools.Basic/ServerStatusTools.cs`, `RoslynSentinel.Tests.Server/ClaudeLeanSchemaBudgetTests.cs`
- Depends on: 8; owner approval of R3
- Effect: **behaviour-changing (wire)**: `toolSurface` gains `hiddenParameterGroups` and `unmatchedHideRules`, filled from the
  snapshot only when `toolListing` is not `none` and the list is non-empty (else null). `McpServerStatus`'s output schema
  grows (`McpServerStatusResult` is its `OutputSchemaType`, and the budget test serializes the whole `ProtocolTool`,
  `ClaudeLeanSchemaBudgetTests.cs:93`). Not an eval surface: `McpServerStatus` is hidden on both eval arms
  (`ChainEvalToolSurface.NewFullSurfaceHiddenTools`, and absent from `AgentToolAllowLists.Lean`).
- Lane: implementer
- Change: optional trailing `IReadOnlyList<HiddenParameterGroup>? HiddenParameterGroups = null, IReadOnlyList<ParameterVisibilityRule>? UnmatchedHideRules = null`
  on `McpServerStatusToolSurface` (`:280-291`), filled where the snapshot fields are filled (`:171` area). No `[Description]`
  text change. First confirm the enums serialize as strings under `McpJsonUtilities.DefaultOptions`; if not, stop and
  report (do not add a converter in this step). Budget: as approved in R3.
- Done when: `Build` 0 errors; dispatcher runs `McpServerStatusToolListingTests`, `ClaudeLeanSchemaBudgetTests`: all pass.

### Step 15 - HOLD (with Step 14): status tests
- Done: DEFERRED (HOLD, owner decision R3; INBOX line)
- Files: `RoslynSentinel.Tests.Tools.Basic/McpServerStatusToolListingTests.cs`
- Depends on: 14
- Effect: text-neutral
- Lane: test-writer
- Change (paused Step 10, reshaped onto snapshot fields): `Listing_WithController_LeanProfile_ReportsHiddenParameterGroupWithOrigin`,
  `Listing_ToolListingNone_OmitsParameterProvenance`, `Listing_ReportsUnmatchedRule_ForMisspeltHideParam`.
- Done when: dispatcher runs the three tests: all pass.

### Step 16 - LIVE check
- Done: 2026-11-24 on servedSha 290eaa9d (attempted 99a2b7ca; later commits test/comment only): A ok (v1 startup, four patches), B ok-none (no hiddenParameters on 123 rows; no hide args), C ok-rejected (Unknown parameter validateOnApply for Search), D not-run, E n/a (14-15 HOLD)
- Files: none
- Depends on: Steps 2-13 committed (and 14-15 if they ran); own-server restart (`McpServerControl(StopServer)`, then
  `LoadSolution`); no subagent mid-operation
- Effect: none
- Lane: main session
- Change and outcome classes (record each with the date and `lastLaunch.servedSha` here):
  - **A.** `McpServerStatus(toolListing: all)` after the restart: `surfaceVersion` 1, trigger `startup`, `schemaPatches`
    exactly `ConsumesTags, BatchItemFields, ReplaceSnippetLimits, HiddenParameters` (`ok` | `wrong-version` | `patches-changed`).
  - **B.** Hidden parameters: every declared tool's `hiddenParameters` is null when the own server runs without
    `--schema-profile=lean`/`--hide-params`; if its launch args carry either, record them and check the rows against the
    Lean list / named entries (`ok-none` | `ok-matches-args` | `unexpected-hidden`).
  - **C.** Validator still rejects an undeclared name: `Search(reason: ..., mode: symbol, query: "ParameterSurface", validateOnApply: true)`
    returns "Unknown parameter 'validateOnApply'" (`ok-rejected` | `accepted` - over-acceptance, stop and write a blocker).
  - **D.** A hidden-but-accepted call needs a restart with hide args, which this check does not do (own-server restart
    only): record `not-run`.
  - **E.** Only if Steps 14-15 ran: `hiddenParameterGroups`/`unmatchedHideRules` absent with no hide config
    (`ok` | `present-when-empty`).
- Done when: A-E recorded.

### Step 17 - Docs
- Done: this commit
- Files: `docs/current/references/reference_architecture_map.md`, `docs/current/references/reference_model_eval_procedure.md`
- Depends on: 8, 10, 11 (and 12)
- Effect: none (docs)
- Lane: main session
- Change: architecture map "Tool surface" section (`:81-104`): parameter rules are a controller input (`ParameterSurface`,
  registered per host, harness overrides by registering first); the patch reads it through the services overload of
  `IToolSchemaPatch`; strips are recorded per tool instance (`StrippedToolParameters`); snapshot fields `Parameters`,
  `HiddenParameterGroups`, `UnmatchedHideRules`; the validator reads the snapshot row; `HiddenSchemaParams` is only the
  `ToolCallHint` mirror until stage E. Usage table: add a row "Change which parameters a schema hides" -> a rule in a
  `ParameterSurface` (startup flags, or a harness registration). Eval procedure (`:79`): eval worlds hide `validateOnApply`
  through a Harness-layer `ParameterSurface` registered by `ModelEvalMcpEnvironment`.
- Done when: both docs match the code; links resolve.

### Step 18 - No stubs left, full run, close
- Done: sweep 0 matches in plan files, HiddenFor gone; INBOX lines added; full run green at 99a2b7ca (c:\tmp\rs-testtrees\_results\99a2b7ca-20261124-030715-34992)
- Files: `docs/current/INBOX.md` (two lines), this plan and the paused plan (via `ProjectDoc close`), `docs/current/TODO.md` (the
  stages line)
- Depends on: all above (14-15 excepted while on HOLD)
- Effect: none
- Lane: dispatcher (checks), main session (close)
- Change:
  - `Search(mode: "text", query: "NotImplementedException|TODO implement \\(Step", fileGlob: "RoslynSentinel.Common/ToolSurface/*.cs")`;
    `Search(mode: "text", query: "HiddenFor\\(", includeTests: true)`.
  - Full `RunTest`.
  - Append to `docs/current/INBOX.md` (format in its header): one line for the deferred Steps 14-15 (status provenance,
    owner decision R3, claude-lean budget) and one for the R4 alias follow-on plan (paused Steps 11-14, when a second alias
    source exists).
  - Record Steps 14-15 as deferred in this plan's Status; close it with `ProjectDoc(action: close)`, move the superseded paused
    plan to `plans/closed/` with it, and update the TODO.md "Tool-surface controller stages 2-6" line (D done). The commit
    carries `Closes: plan_tool_surface_controller_stage_d`.
- Done when: zero matches for both searches; full `RunTest` with 0 failures; both INBOX lines present; plan closed.

## Out of scope

- Parameter and value aliases (paused Steps 11-14); see R4.
- `ToolCallHint` reading the snapshot and deleting `HiddenSchemaParams`; the validator's static `SchemaCache`; one captured
  snapshot per call (the validator reads `Current` in `WrapFlatBatchParameters` and again in `Validate`). All stage E.
- Runtime parameter-rule layers (a layer apply that changes listed schemas) and the client re-read probe they need.
- The toolset-file source of hide rules (`ParseHideList(raw, ToolsetFile, <file>)`): belongs to the JSON toolset reshape (R10).
- The ReplaceSnippet/EditSnippets anchor removal in `ReplaceSnippetLimitsPatch`: it strips top-level `lineBefore`/`lineAfter`
  (`McpToolSchemaPatcher.cs:509`; the item-schema call is `:560`, the shared helper `:566-567`) without registering them as
  hidden. It stays as it is (R2).
- Generalising the hard-coded `validateOnApply:false` refusal (`ToolArgumentValidator.cs:1296-1308`), nested batch-item schemas.
- Deleting `SchemaOptions.EffectiveHiddenParameters`/`ConfigureHiddenParameters` (obsolete here; removal when the last test
  caller moves, with stage E's static cleanup).

## Risks and open decisions

- **R1. Where the patch reads the rules (taken: the DI `ParameterSurface` singleton).** Options: (a) singleton with a
  `FromStartup` fallback; (b) through the controller; (c) a static "current surface" - brings back the process static this
  stage removes. (b) is rejected because resolving the controller builds every tool (Decision; SDK
  `McpServerOptionsSetup.cs:12-15`), so a patch resolving the controller would re-enter a singleton under construction.
  What that does is not determined here: depending on the container path it may throw, recurse, or build a second
  controller whose snapshot nobody reads, so a clean test run would not prove its absence. The plan avoids the question
  structurally: no patch, and nothing reachable from `CreateSentinelTool`, ever resolves `ToolSurfaceController`
  (reviewers check this at Steps 7 and 8). The underlying claim rests on standard `Microsoft.Extensions.Options` constructor
  injection (the options factory resolves its `IConfigureOptions` when built) - inferred, not traced in that package.
  Not STOP-worthy.
- **R2. How the snapshot learns what was hidden (taken: per-instance strip table).** Options: (a) `StrippedToolParameters`
  written by the patch; (b) declared-minus-listed, recording the pre-patch names in `CreateSentinelTool`; (c) a marker in
  `ProtocolTool.Meta`. (b) is **not text-neutral**: `ReplaceSnippetLimitsPatch` removes top-level `lineBefore`/`lineAfter`
  from `ReplaceSnippet`/`EditSnippets` unconditionally (top-level call `McpToolSchemaPatcher.cs:509`, item call `:560`,
  shared helper `:566-567`; hidden from the schema 2026-11-09) without registering them, so (b) would list them as hidden in
  status and make the validator accept them - a change in what calls eval models get accepted (STOP-worthy if chosen).
  (c) changes `tools/list` bytes. (a) matches today exactly.
- **R3. Status provenance fields (Steps 14-15) - HOLD, deferred to the owner.** The claude-lean budget is 48500
  (`ClaudeLeanSchemaBudgetTests.cs:43`, `:47`) with essentially no headroom (273e6734 trimmed 10 chars to get back under it on
  2026-11-24), and the new fields grow `McpServerStatus`'s output schema. Options: (a) defer the fields; the snapshot already
  holds the provenance in process; (b) add them and raise the budget by the measured overrun rounded up to 50, as the owner
  allowed for A5 on 2026-11-23 (that decision was for A5 only); (c) encode the rule into the existing per-tool
  `hiddenParameters` strings - changes the meaning of an existing wire field. Recommendation: (a) for the overnight run,
  then (b) if the owner wants status provenance now. Not an eval-surface change (`McpServerStatus` is hidden on both eval
  arms), but a budget raise is the owner's call, so the run skips Steps 14-15 and Step 18 records them in INBOX.
- **R4. Aliases (paused Steps 11-14): deferred (taken).** The alias tables (`ToolArgumentValidator.cs:612`, `:728`,
  `:741`) have one source and their targets are already checked by
  `ToolParameterAliasTests.EveryAliasEntry_ForAnActiveTool_PointsAtADeclaredParameter_AndIsNotItselfDeclared` and
  `GlobalAliasKey_IsNeverDeclaredByAnyActiveTool`. Folding them in now means a 115-line data move (`implementer-senior`)
  plus `[Obsolete]` on two public tables with 9 test references, and buys provenance only. A follow-on plan when the
  toolset-file layer (stage G) brings a second source; `ToolParameterSurfaceOptions` can take an optional trailing
  `Aliases` list then without breaking construction sites. Step 18 records it in INBOX. Not STOP-worthy.
- **R5. `HiddenSchemaParams` removal timing (taken: stage E).** `ToolCallHint.Call` is static, takes a catalog and no
  services, and is reached through many static hint builders (the paused plan counted 452 callers; not re-measured here).
  Moving it belongs with the proposal's stage 4 item "hint catalog from the snapshot". This plan leaves it a write-only
  mirror, `[Obsolete]` from Step 11, with TODOs at both ends. Not STOP-worthy.
- **R6. Eval harness rule shape (taken: exact equivalent).** The proposal's form for the full-schema opt-in is one
  `Show *` Harness rule. It differs from today's `ConfigureHiddenParameters([])` only when `SchemaOptions.Profile` is Lean
  in the eval process (then `Show *` would also un-hide the Lean list, while today it stays hidden). Nothing in ModelEval
  calls `SchemaOptions.Configure` (callers: `ServerStdio.cs:52`, `ServerHttp.cs:40`, tests), so the difference is latent.
  Step 10 uses the exact equivalent; switching to `Show *` would be a deliberate eval-surface decision for the owner
  (STOP-worthy if proposed).
- **R7. `Tool.param` scoping (Step 12): included (taken).** It changes only an input that today does nothing.
  The Grep finds no repo script or config passing a dotted entry. A machine-level `ROSLYNSENTINEL_HIDE_PARAMS` holding a
  dotted value on an eval host cannot be ruled out from the repo (hypothesis: none is set). A typo in a scoped entry stays
  silent until Steps 14-15 surface `UnmatchedHideRules`. Step 12 can be dropped without touching any other step. Not STOP-worthy.
- **R8. Coverage of the validator's hidden checks.** `:1301` and `:1321` are covered through the client by
  `SchemaHiddenParametersTests` and `EvalToolSchemaVisibilityTests`. `:1057` (`WrapFlatBatchParameters`) had no test found
  (Step 9 attempts one). `Refusal_WhenSchemaDeclaresTheParameter_IgnoresAStaleHiddenRegistration` (`SchemaHiddenParametersTests.cs:237`)
  is trivially true on the controller path after Step 8; Step 9's `StaleRegistryEntry_IsIgnored_WhenAControllerIsRegistered`
  covers the controller path, and the registry-fallback path (hosts with no controller) has no client-level test.
  Planner-side gaps: if Step 9 cannot build the `:1057` probe, `:1057` and the fallback stay verified only by review, and
  the reviewer of Step 8 confirms the helper call at `:1057` is the same expression as at `:1321`.
- **R9. Multi-host behaviour moves (text-neutral in production).** After Step 8, tests that build several hosts in one process
  see per-host answers. The tests that exercise this sequencing (`OptIn_AfterADefaultWorld_IsNotRefused`,
  `Default_AfterAnOptInWorld_HidesAgain`) are expected to stay green because the emitted schema stays the authority. A red
  one there is a real finding about the validator, not a test to adjust.
- **R10. Overlap with other plans.** `plan_json_toolset_definitions.md` (DRAFT, not started): the paused plan's Out of scope
  says `hideParams` was "added to plan_json_toolset_definitions.md as a step". It was not: Grep for `hideParams` in that plan
  finds nothing (2026-11-24). Its reshape under the controller should add the ToolsetFile hide-rule source through
  `ToolParameterSurfaceOptions.ParseHideList`. File overlap: its Step 21 (ModelEval surfaces) edits the ModelEval surface
  builders next to Step 10's `ModelEvalMcpEnvironment.cs` (its anchor there is stale: the surface branch is now
  `ModelEvalMcpEnvironment.cs:127-133`), and its Steps 8/13 edit `ServiceRegistrationExtensionsBasic.cs` (Step 6 here).
  `plan_stateless_http_toolset_enablement.md` Step 6 (restored-start filter, effectively landed in b0242718 per stage C R8)
  also sits in `ServiceRegistrationExtensionsBasic.cs`; check `Git(status)` before Step 6 if that plan is running. No
  conflict while the JSON plan has not started; it should rebase on this one. Not STOP-worthy.
- **R11. Default interface member on a public interface.** Hosts outside this repo implementing `IToolSchemaPatch` keep
  compiling (a default body exists); `net10.0` (`RoslynSentinel.Common.csproj:4`) supports it. A host patch that wants
  services overrides the new overload. Not STOP-worthy.
- **R12. Name proximity.** Phase A/B already flagged `ToolsetSurfaceSnapshotTests` (JSON plan) beside `ToolSurfaceSnapshot`.
  This plan adds `ToolSurfaceParameterTests`, `ParameterSurface` and `HiddenParameterGroup` (renamed from the proposal's
  `HiddenParameter`, Decision); no name collides (`Search(mode: symbol, query: "HiddenParameter", exactMatch: false)` finds
  only `HiddenParametersPatch` and `SchemaHiddenParametersTests`, 2026-11-24).
