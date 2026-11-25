# Plan: Declare every startup flag and env var once, generate the configuration reference from it, and report effective values at runtime

**Status:** IN PROGRESS 2026-11-25. Steps 1-9, 11, 13-15, 22a done (see per-step Done lines). Declare every startup flag and env var once as a `StartupSetting<T>` and drive parsing, the generated reference, `McpServerStatus` and a configuration halt latch from that registry (33 slices, staged report-only before the halt).

## Problem

The server and its harnesses read 67 distinct CLI flags and about 85 distinct `ROSLYNSENTINEL_*` env vars
(about 51 in product code, 34 test-only) through 21 hand-written options classes and 16 private parsing helpers
(`GetArgValue` copies in 10 `Common` options classes, `ServerInstancePaths`, `ServerStartupHelpers`;
`LoopLatchOptions.GetFlagValue`, `NearMissOptions.GetValueFlag`, `RunnerOptions.GetArg`/`HasFlag`).
No document lists them; `docs/current/DOCUMENTATION_INDEX.md` has no configuration row, and partial
lists drift across `reference_model_eval_procedure.md`, `reference_working_conventions.md` and
`reference_architecture_map.md`. `McpServerStatus` reports only 4 effective values
(`Tools.Basic/ServerStatusTools.cs:201-217`).

Evidence of cost (grounding 2026-11-25):
- Names are inline literals, written twice per setting in each `Configure` body; tests repeat them
  (`ModelEvalAssemblySetUp.cs:51-52`, `ToolSurfaceParityTests.cs:51`); `ROSLYNSENTINEL_INSTANCE_ID`
  is defined twice (`Common/ToolTelemetry.cs:18`, `Engines.Basic/BuildIsolation.cs:23`).
- The parsing helpers disagree: a bare flag yields `"true"` in 4 classes and null (so the default) in
  8, e.g. a bare `--llm-minimal-tools` is ignored. Unparseable values fall back to a default silently
  (`INBOX.md:25`: `--hide-params` typos hide nothing; `NotFoundInventory=bogus` means off,
  `WirePath=bogus` means on).
- Entry-point drift: `IdleRestartOptions.Configure` is absent from `ServerHttp.Startup`
  (`ServerHttp.cs:31-44`, apparently deliberate but undocumented); `ModelEvalAssemblySetUp.cs:36-52`
  configures 6 of 13 classes (precedent:
  `blockers/closed/blocking_error_modeleval_harness_never_configures_near_miss_options.md`).
- Raw args reach the host builders (`ServerStdio.cs:106`, `ServerHttp.cs:73`), so any `--key=value`
  is silently absorbed as host configuration (`launchSettings.json:46,51` `--solution-Path=` does nothing).
- `plan_json_toolset_definitions.md` (DRAFT) will add `--toolset`, `--toolset-file`, `--tools` and
  shipped toolset JSON files, growing the surface again.

Existing generated-doc mechanism to mirror: `Tests.Server/ArchitectureDocGenerator.cs` (reflection),
freshness asserted by `ArchitectureDocFreshnessTests.cs:139-163`, regenerated with
`ROSLYNSENTINEL_UPDATE_GENERATED_DOCS=1` via `scripts/Generate-ArchitectureMap.ps1`; same shape in
`Tests.ModelEval/ScenarioCatalogFreshnessTests.cs`.

## Decision

Summary: each setting is one `StartupSetting<T>` field (flag, env, default, description, scope, kind)
in a class marked `[StartupSettingGroup("title")]`. One registry drives parsing, the generated
`docs/generated/configuration.md`, `McpServerStatus` (effective value plus source), `--list-options`,
and a configuration halt latch that refuses every tool except `McpServerStatus` while any setting is
invalid or any flag unknown.

Decision log:
- 2026-11-25 Q1 Audience: generated markdown reference AND runtime exposure of effective values
  (value plus source) to models. Owner choice.
- 2026-11-25 Q2 Scope: C# only - server, PlanStepRunner, AgentLoop and the test harnesses
  (Tests.Harness, Tests.ModelEval). `scripts/*.ps1` and `.claude/hooks/*` env vars are out of scope.
  Owner choice.
- 2026-11-25 Q3 Drift guard: a test fails when C# reads a flag or `ROSLYNSENTINEL_*` env var that is
  not declared. Owner choice.
- 2026-11-25 Q4 Mechanism: option B, a typed `StartupSetting<T>` descriptor per setting is the single
  definition. One shared parser replaces the 16 helpers; each read records value and source
  (Flag/Env/Default/Invalid); `ConfigureAll` replaces the per-class `Configure` calls at entry points;
  generator, status and guard enumerate the same registry. Why: only option meeting checklist item 2
  and Q1's runtime source without extra plumbing. (Amended by Q19: no `[Obsolete]`.)
- 2026-11-25 Q5 Invalid config in the server (owner proposal): a configuration halt latch instead of
  fail-fast (a failed spawn shows only as "MCP connection failed" in VS Code). `ConfigureAll` sets
  `LastReport.Halted` once when any setting is Invalid (unparseable value, value flag with no value)
  or any `--flag` is unknown; a call-tool filter returns a structured `ConfigurationInvalid` error with
  the typed problem list to every call outside the allow list; one stderr line per problem. Parsing:
  a bare bool flag means true; flag beats env; repeated flag, last wins.
- 2026-11-25 Q5a Allow list during a config halt: `McpServerStatus` only (not the unrecoverable list,
  which also admits reads and would let an agent carry on unaware). Owner choice.
- 2026-11-25 Q6 Status surface: an always-on `Configuration` section (problems, halted, undeclared-env
  count) plus an opt-in `settings: none | nonDefault | all` parameter (default none, mirrors
  `toolListing`); rows `{name, flag, env, value, source, scope}` without descriptions. Existing
  `EolMode`/`OperatingMode`/`IdleRestart` fields stay. Owner accepted. (Refined by Q24.)
- 2026-11-25 Q7 Toolset JSON: this plan lands before `plan_json_toolset_definitions.md`; that plan gets
  a Risks note to declare `--toolset`, `--toolset-file`, `--tools` as `StartupSetting` (unknown toolset
  name = Invalid); shipped JSON files are documented by its own `docs/generated/toolsets/` catalog,
  linked from the configuration reference. Owner accepted.
- 2026-11-25 Q8 Foreign env vars: git-layer `PATH`, `PATHEXT`, `HOME`, `GIT_*`, `XDG_CONFIG_HOME`
  exempt (git's own environment); `NUGET_PACKAGES` and `CLAUDE_CODE_SESSION_ID` declared with kind
  External. Owner choice.
- 2026-11-25 Q9 `--list-options`: yes, a CLI switch beside `--list-tools` that prints the registry,
  reusing the generator's renderer. Owner choice.
- 2026-11-25 Q10 Drift guard mechanism: flags are covered by the runtime unknown-flag check; env vars
  by an in-process source-scan test (T10). BannedApiAnalyzers rejected (extra package, pragmas,
  cannot police literals). Owner accepted. (Rule amended by Q20 and readiness corrections.)
- 2026-11-25 Q11 (investigation) Generator home: `RoslynSentinel.Tests.ModelEval` is the only test
  project reaching all declaring assemblies (Common, AgentLoop, Server.Advanced, Tests.Harness, and
  PlanStepRunner via Tests.Harness); it already hosts `ScenarioCatalogFreshnessTests.cs`.
- 2026-11-25 Q12 (investigation) `McpServerStatus` (`Tools.Basic/ServerStatusTools.cs`, 372 lines):
  params `reason`, `toolListing` (`McpServerStatusToolListing` :268, default none), `toolNameFilter`,
  then `cancellationToken`, `requestContext`; returns `McpServerStatusResult` (:320-364); carries
  `[UnrecoverableBreaker(Allowed)]`.
- 2026-11-25 Q13 An undeclared `ROSLYNSENTINEL_*` var present in the process environment is reported,
  never a halt (scripts, hooks and shell profiles may leave out-of-scope names). Owner choice.
- 2026-11-25 Q14 Non-server processes fail fast: PlanStepRunner exits non-zero naming the bad setting
  (it validates its own and the shared `Server | PlanStepRunner` settings; other `--flags` are
  server pass-through and the child server reports them); the ModelEval harness `[OneTimeSetUp]`
  fails through `ThrowIfInvalid`. Owner choice.
- 2026-11-25 Q16 Rollout: staged. Problems are reported without halting (`HaltOnErrors = false`); a
  test proves the known arg sets produce zero problems; the halt is enabled as the last behaviour
  step. Why: a tolerated-today arg becoming "unknown" would halt every session after restart. Owner choice.
- 2026-11-25 Q17 Host builders: `Host.CreateApplicationBuilder(args)` (`ServerStdio.cs:106`) and
  `WebApplication.CreateBuilder(args)` (`ServerHttp.cs:73`) receive `[]`. HTTP binds through
  `ConfigureKestrel` `Listen` (`ServerHttp.cs:83`); no product code reads command-line values through
  `IConfiguration` (readiness round 2). Owner choice.
- 2026-11-25 Q18 `ServiceRegistrationExtensionsBasic.cs` is 1497 lines: an early text-neutral step
  moves `AddUnrecoverableBreakerFilter` (:1111-1162) into new `Server.Advanced/BreakerFilters.cs`;
  the configuration halt filter is added there. Owner choice.
- 2026-11-25 Q19 (amends Q4) Every options class keeps its public properties, its existing public
  helpers and its `Configure(string[] args)`; internally each property reads a public descriptor
  field (`<Prop>Setting`) and `Configure` delegates to `StartupSettings.ConfigureGroup` (plus any
  existing post-processing through the group's `PostConfigure` hook). `Configure` is the supported
  per-group test reset, not `[Obsolete]`. Only the entry points switch to `ConfigureAll`. Why: about
  280 test `Configure` calls and hundreds of property reads stay untouched. Owner choice.
- 2026-11-25 Q20 (amends Q10) Test code may keep `ROSLYNSENTINEL_*` literals (about 128 whole-literal
  hits), but each must be a declared name. The 34 test-only names are declared in Tests.Harness
  (scope `TestHarness`, type `String`). Owner-approved exception to checklist item 2 for test
  projects that cannot reference the descriptor.
- 2026-11-25 Q21 Schema budget: `ClaudeLeanSchemaBudgetTests` allows 50000 chars against 49677
  measured. Keep the `settings` parameter with a description of at most 80 chars; if the measured
  total exceeds 50000, raise `AdvancedTotalBudget` and `BasicTotalBudget` (:46, :53) by the measured
  delta rounded up to the next 100, with a comment citing this plan. Pre-approved. Owner choice.
- 2026-11-25 Q22 Strictness: strict parsing with softened forms. Bool literals true/false/1/0/on/off/
  yes/no (case-insensitive); enum names match case-insensitively ignoring `-` and `_`
  (`single-agent` = `SingleAgent`); an empty or whitespace value is unset (default, no problem).
  Anything else is Invalid, so today's junk-accepting parsers (`NotFoundInventory=bogus`, `Schema`
  "anything but false", unknown `ResultMarshaller`/`Eol`/`NearMiss` values) become loud. Owner choice.
- 2026-11-25 Q23 ModelEval applies every `Server`-scope setting from env through `ConfigureAll` (about
  9 classes it never configured before), with `ThrowIfInvalid`. Why: this is the entry-point drift
  the plan exists to fix. Owner choice.
- 2026-11-25 Q24 Undeclared env names are not problems. `ConfigurationReport.UndeclaredEnv` holds them;
  status always carries `UndeclaredEnvCount`, lists the names only when `settings != none`, and when
  the list is omitted and the count is above 0 carries a hint (one shared constant, facts and
  options only) naming the `settings` values that include them; stderr at startup lists them. Owner
  choice, including the hint.
- 2026-11-25 Simplification (item 7): no separate breaker class, no `HaltInfo` change, no problem
  severity (every problem is an error; undeclared env is a separate list, Q24). The halt is
  `StartupSettings.LastReport.Halted`.
- 2026-11-25 Design review (checklist 1-7): pass. New wire DTOs carry `NEW-DTO:` justifications in
  their steps. Item 7: the simplest version (attribute + generated doc) cannot give the runtime
  source or the drift guard the owner required; fail-fast is simpler than the halt latch but
  rejected for the VS Code spawn failure mode.
- 2026-11-25 Readiness round 1 corrections (owner: no objection): `[Flags] SettingScope` with
  duplicates allowed in non-overlapping scopes (`--include-tools` for server and runner);
  `LlmOptions`/`ErrorPolicyOptions` scope `Server | PlanStepRunner`; `LoopLatchOptions` helpers deleted
  only after their last caller migrates; a stateless public `Read` so `Parse*` methods and injected-
  reader APIs need no registry state; full contract with factories on `StartupSetting`, registry on
  `StartupSettings`; `BreakerFilters` public (no InternalsVisibleTo); filter matches
  `ServerStatusTools.ToolName`; existing `const string` env names stay `const`; T10 product rule "a
  whole `ROSLYNSENTINEL_*` literal occurs once and is declared"; renderer in `Server.Advanced`.
- 2026-11-25 Readiness round 2 corrections: the remaining direct env reads
  (`LmStudioAgentClient.cs:826`, `BuildIsolation.cs:104`, `ToolTelemetry.cs:92`, `LoopAssist.cs:47`)
  migrate; a `PostConfigure` hook on the group attribute carries existing post-processing (LlmOptions
  host-sampler fold, ErrorPolicy cross-field check, AgentOperatingMode latch reset); `Int` takes a
  `minValue`; `ConfigurationInvalidException` has a facts-only message naming each setting;
  `ConfigureAll` list overload takes `haltOnErrors` (testable before Step 26); External values render
  as `set`/`unset`; a shared `BreakerFilters.BuildErrorResult` replaces the `LoopLatchFilter.Refusal`
  envelope copy; ModelEval passes `ErrorPolicyFile.ResolveConfigureArgs(...)` as `ConfigureAll` args.
- 2026-11-25 Readiness round 3 corrections: Q22's "blank = unset" also applies to
  `ROSLYNSENTINEL_MODELEVAL_THINKING_EFFORT` (blank now means the default `low` instead of a throw;
  the peer session is told); `Parse*` methods apply their caller default when `Source` is `Default`
  or `Invalid` (the registry still reports the problem, so it stays loud); an existing range check
  becomes `minValue` or a `Custom` parse that rejects out-of-range values (Invalid); a group whose
  `PostConfigure` reports a problem resets its settings to defaults, so no half-valid state survives
  (ErrorPolicy: Inline with no file falls back to Off); flags match case-sensitively, as today;
  the halt filter's per-call cost is one static read and a string compare.
- 2026-11-25 Readiness round 4-5 corrections: values are trimmed before parsing; existing public
  raw-string parse helpers delegate to their descriptor; Q22 test rewrites listed in Steps 10 and 11
  (OrientationBreaker and WirePath moved to Steps 10b/11b for the file cap); T15 replays the ModelEval
  setup call in-process (`ModelEvalAssemblySetUp` is in the `[Explicit]` LiveModel namespace);
  `DescribeProblems` always carries flag and env names (AgentLoop `FormatException` tests);
  `RunnerOptions.Parse` keeps all its `ArgumentException` paths and derived defaults; `--interactive`
  reads statelessly. Question numbers are Q1-Q14 and Q16-Q24 (Q15 unused).
- 2026-11-25 Readiness (plan-design): independent review clean at 8e02735a (round 6, after five
  rounds of fixes).
- 2026-11-25 Peer request (session of `plan_error_gated_thinking_and_client_reasoning_budget.md`):
  `AgentLoop/ThinkingOptions.cs` (now present) joins the migration (Step 15);
  `InvalidThinkingSettingException` is replaced by `ConfigurationInvalidException`.
- 2026-11-25 Run setup (implement-plan): staleness check since 8e02735a clean (only drift: the
  launcher's server-argument strings for T11 now sit at `roslynsentinel-mcp-launch.ps1:1017-1039`;
  `plan_error_gated_thinking_and_client_reasoning_budget.md` is closed, so its Risks note is moot).
  Mode Batched, one orchestrator per slice-map wave; `implementer-senior` wherever it is faster than
  splitting for Haiku. Owner away during the run: mid-run decisions are deferred. Owner choice.

## Tests

| # | Test (class.method) | Behaviour | Fixture | Asserts | Pass |
| --- | --- | --- | --- | --- | --- |
| T1 | StartupSettingParserTests.ValidForms_* | bare bool = true; `--f=v` and `--f v`; flag beats env; repeat = last wins; bool literals incl. yes/no; enum match ignores case, `-`, `_`; empty value = unset; bool next token consumed only if a bool literal; `minValue` | in-process `Read` with a fake env delegate | `Value`, `Source` | all cases |
| T2 | StartupSettingParserTests.InvalidValue_* | missing value / unparseable value / below `minValue` keeps the default, records Invalid with raw text | same | `Source == Invalid`, `Problem.Kind`, `RawValue` | all cases |
| T3 | StartupSettingRegistryTests.UnknownFlag_IsProblem | undeclared `--x` is a problem; a consumed value token is not a flag | `ConfigureAll` over a fixture setting list | `Problems[].Kind == UnknownFlag`, count | exact |
| T4 | StartupSettingRegistryTests.UndeclaredEnv_IsListedNotProblem | an undeclared `ROSLYNSENTINEL_*` name in the env lands in `UndeclaredEnv`, not `Problems` (Q13, Q24) | fake `EnvironmentNames` (name built by concatenation, see T10) | list contents, `Problems` empty | exact |
| T5 | StartupSettingRegistryTests.DuplicateName_OverlappingScopes_Throws / _DisjointScopes_Allowed | duplicate flag or env in overlapping scopes fails discovery; disjoint scopes allowed | fixture group types | typed exception / no throw | exact |
| T6 | StartupSettingRegistryTests.LiveSetting_RereadsEnv / PostConfigure_RunsAndReportsProblems | `Live` kind re-reads env per access; a group's `PostConfigure` runs after apply and its problems are reported | fake `EnvironmentReader`, fixture group | `Value` changes; problem present | exact |
| T6b | StartupSettingRegistryTests.Halted_FollowsHaltOnErrors / ThrowIfInvalid_NamesSettings | `Halted == haltOnErrors && any problem` for both values; `ThrowIfInvalid` throws `ConfigurationInvalidException` whose `Problems` name the setting | list overload of `ConfigureAll` | `Halted`, exception `Problems[0].SettingName` | exact |
| T7 | ConfigurationHaltFilterTests.Halted_RefusesNonAllowed / Halted_AllowsMcpServerStatus / NotHalted_PassesThrough | halted report: non-allowed call returns `ConfigurationInvalid` with problems in `StructuredDetail`; `ResultStatusExtensions.FromErrorCode(ConfigurationInvalid) == FromErrorCode(PreconditionNotMet)` (`Category` is `[JsonIgnore]`); `McpServerStatus` passes | injected `Func<ConfigurationReport>`, fixture shape of `Tests.Server/OrientationBreakerFilterTests.cs` | `ErrorCode`, mapped status, `StructuredDetail` count | exact |
| T8 | ServerStatusSettingsTests.* | `settings` none/nonDefault/all row sets; `UndeclaredEnvCount` always; names only when `settings != none`; hint present only when names omitted and count > 0; External value is `set`/`unset` | in-process status call after `ConfigureAll` over a fixture list | row counts, `Source`, count, hint == shared constant | exact |
| T9 | ConfigurationReferenceFreshnessTests.* | `docs/generated/configuration.md` matches the registry | in-process reflection (Tests.ModelEval) | file equality | equal |
| T10 | EnvVarDeclarationGuardTests.* | product: each whole literal matching `^ROSLYNSENTINEL_[A-Z0-9_]*[A-Z0-9]$` is declared and occurs once; `Environment.GetEnvironmentVariable` only under `RoslynSentinel.Common/Configuration/` and `RoslynSentinel.Git/`; tests: each such literal is a declared name (prefix literals ending in `_` are not names) | in-process file scan from the repo root | offending file:line list | empty |
| T11 | KnownArgSetsTests.* | launcher flags, every `launchSettings.json` profile, `SubAgentChildServerLauncher.BuildServerArguments` and `ErrorPolicyWiring.ChildServerArgs` output produce zero problems (Q16) | in-process | problem count | 0 |
| T15 | KnownArgSetsTests.ModelEvalScopes_DiscoverWithoutDuplicates_CleanEnvHasNoProblems | the Step 19a call (`Discover(ModelEval assembly)`, scopes `Server | AgentLoop | TestHarness`, empty args, fake env with no values) throws no `StartupSettingDeclarationException` and reports zero problems (Q23) | in-process, fake `EnvironmentReader`/`EnvironmentNames` | exception absence, problem count | 0 |
| T12 | ServerSettingsDiscoveryTests.Discover_FindsServerGroups | server discovery finds the Common, AgentLoop, Server.Advanced and External groups (Q8) | in-process | named groups present | all present |
| T13 | ServerSettingsDiscoveryTests.ListOptions_RowsMatchRegistry | `RenderConsole` rows equal the server-scope registry | in-process renderer | row names set-equal | equal |
| T14 | PlanStepRunnerSurfaceTests.InvalidSetting_* / MissingRequiredFlag_Throws / InvalidBranchMode_Throws | runner: an invalid runner or LLM value yields a problem naming the setting; `RunnerOptions.Parse` throws `ArgumentException` on a missing required flag (no existing test pins this) and on a bad `--branch-mode` (Q14) | in-process | problem setting name, exception type | exact |

Not tested in-process: Q17 (host builders get `[]`) and the `--list-options` early exit (both
covered by the dispatcher verification after Step 26); the ModelEval `[OneTimeSetUp]` wiring (Q23;
`ModelEvalAssemblySetUp` is in namespace `RoslynSentinel.Tests.ModelEval.LiveModel`, whose fixtures
are all `[Explicit]`, so it runs only in live evals; T15 replays its exact `ConfigureAll` call
in-process and T6b covers the throw path); the runner's exit code 2 (a `Program.Main` process exit; the problem detection
is T14). T11 covers the arg sets visible to tests; `TestInstanceLauncher.BuildServerArguments`
(`internal`, flags `--transport`, `--working-dir`, both declared) and users' own `mcp.json` args are
not covered. Descriptor wiring of each migrated class
(Q19) is covered by the existing suite staying green (about 280 `Configure` test calls).

Baseline: test baseline is 0 failures; any failure is new. No eval or live model run.

## Execution rules

- `Build` 0 errors after every slice; tests are run by the dispatcher, one `RunTest` at a time
  (pairs edit concurrently; builds and test runs are serialized).
- Migration slices (Steps 6-17) keep every public member, `Configure` signature and existing
  `const string` env-name field (names, types, const-ness). Intended behaviour changes are only Q5
  and Q22 parsing; any other change stops the slice. An existing range check (e.g. `> 0`) becomes
  `minValue` or a `Custom` parse that returns false out of range, so an out-of-range value is
  Invalid instead of silently the default; an existing test that pins the old silent fallback is
  updated in the same slice to assert the default value plus the reported problem.
- Existing public raw-string parse helpers (`SchemaOptions.ParseProfile`/`ParseEnabled`,
  `EolPolicyOptions.ParseMode`, `AgentOperatingModeOptions.ParseMode`, `ResultMarshallerOptions.Parse`,
  `ToolCallEchoOptions.ParseEnabled`, `NotFoundInventoryOptions.ParseEnabled`, and any like them) keep
  their signatures and delegate to the descriptor: `<Setting>.Read(StartupArgs.Empty, _ => raw).Value`
  (an Invalid value yields the default, as these helpers do today), so the parse rule has one
  definition (item 2). Pinning tests: `EolResolverTests.cs:69`, `AgentOperatingModeOptionsTests`
  `ParseMode_MapsRawValues`, `SentinelToolFactoryParityTests.cs:136-137`.
- Defaults, flag names and env names are copied verbatim.
- Each migrated class: `[StartupSettingGroup("<title>")]`; one `public static readonly`
  `<Prop>Setting` field per setting created with a `StartupSetting.*` factory, **declared above every
  static field or property initializer that reads it** (C# initializes static fields in textual
  order); a class with a `const string` env name passes that const as `env:`; the class's private
  parsing helpers go; existing post-processing in `Configure` moves into a `PostConfigure` method
  named by the attribute.
- Every test fixture touching `StartupSettings.EnvironmentReader`, `EnvironmentNames` or `LastReport`
  is `[NonParallelizable]` with a comment (static state).
- The halt stays disabled (`StartupSettings.HaltOnErrors = false`) until Step 26 (Q16).

### Contract (Step 2 lands it; signatures are binding)

Common types: namespace `RoslynSentinel.Common`, folder `RoslynSentinel.Common/Configuration/`.

```csharp
// StartupSetting.cs
[Flags] public enum SettingScope { None = 0, Server = 1, ServerStdio = 2, PlanStepRunner = 4, AgentLoop = 8, TestHarness = 16 }
public enum SettingSource { Default, Flag, Env, Invalid }
public enum SettingKind { Startup, Live, External }   // Live and External: env re-read on every Value access
public enum ConfigurationProblemKind { InvalidValue, MissingValue, UnknownFlag, PostConfigure }

[AttributeUsage(AttributeTargets.Class)]
public sealed class StartupSettingGroupAttribute(string title) : Attribute
{
    public string Title { get; } = title;
    public string? PostConfigure { get; set; }   // name of a `static IReadOnlyList<ConfigurationProblem> X()` on the group class
}

public delegate bool TryParseSetting<T>(string raw, out T value);

public sealed class StartupArgs
{
    public static StartupArgs Empty { get; }
    public static StartupArgs Parse(IReadOnlyList<string> args);
    public IReadOnlyList<string> Tokens { get; }
}

public sealed record SettingRead<T>(T Value, SettingSource Source, ConfigurationProblem? Problem);

public abstract class StartupSetting
{
    private protected StartupSetting(string? flag, string? env, string description, SettingScope scope, SettingKind kind);
    public string Name { get; internal set; }          // "<Class>.<Field>" with a trailing "Setting" removed; set by Discover
    public string? Flag { get; }                       // "--kebab"; null = env-only
    public string? EnvName { get; }                    // null = flag-only
    public string Description { get; }
    public SettingScope Scope { get; }
    public SettingKind Kind { get; }
    public abstract Type ValueType { get; }
    public abstract bool IsBool { get; }
    public abstract string DefaultText { get; }
    public abstract string EffectiveText { get; }      // External kind: "set" or "unset", never the value
    public abstract SettingSource Source { get; }
    internal abstract ConfigurationProblem? Apply(StartupArgs args, Func<string, string?> getEnv);   // stores value + source
    internal abstract void Reset();

    // factories (flag and env may each be null, not both; violation throws StartupSettingDeclarationException)
    public static StartupSetting<bool> Bool(string? flag, string? env, bool defaultValue, string description, SettingScope scope, SettingKind kind = SettingKind.Startup);
    public static StartupSetting<int> Int(string? flag, string? env, int defaultValue, string description, SettingScope scope, SettingKind kind = SettingKind.Startup, int? minValue = null);
    public static StartupSetting<int?> NullableInt(string? flag, string? env, string description, SettingScope scope, SettingKind kind = SettingKind.Startup, int? minValue = null);
    public static StartupSetting<double> Double(string? flag, string? env, double defaultValue, string description, SettingScope scope, SettingKind kind = SettingKind.Startup);
    public static StartupSetting<double?> NullableDouble(string? flag, string? env, string description, SettingScope scope, SettingKind kind = SettingKind.Startup);
    public static StartupSetting<string?> String(string? flag, string? env, string? defaultValue, string description, SettingScope scope, SettingKind kind = SettingKind.Startup);
    public static StartupSetting<TEnum> Enum<TEnum>(string? flag, string? env, TEnum defaultValue, string description, SettingScope scope, SettingKind kind = SettingKind.Startup) where TEnum : struct, System.Enum;
    public static StartupSetting<T> Custom<T>(string? flag, string? env, T defaultValue, string defaultText, TryParseSetting<T> parse, string expected, string description, SettingScope scope, SettingKind kind = SettingKind.Startup);
}

public sealed class StartupSetting<T> : StartupSetting
{
    internal StartupSetting(string? flag, string? env, T defaultValue, string defaultText, TryParseSetting<T> parse, string expected, string description, SettingScope scope, SettingKind kind);
    public T DefaultValue { get; }
    public T Value { get; }                            // Startup: last applied value (default before Apply); Live/External: Read(StartupArgs.Empty, StartupSettings.EnvironmentReader).Value unless a flag was applied
    public SettingRead<T> Read(StartupArgs args, Func<string, string?> getEnv);   // stateless
}

// StartupSettings.cs
public sealed record ConfigurationProblem(ConfigurationProblemKind Kind, string? SettingName,
    string? Flag, string? EnvName, string? RawValue, string? Expected);
public sealed record ConfigurationReport(IReadOnlyList<StartupSetting> Settings,
    IReadOnlyList<ConfigurationProblem> Problems, IReadOnlyList<string> UndeclaredEnv, bool Halted);
public sealed class ConfigurationInvalidException(IReadOnlyList<ConfigurationProblem> problems)
    : Exception(StartupSettings.DescribeProblems(problems))
{ public IReadOnlyList<ConfigurationProblem> Problems { get; } = problems; }
public sealed class StartupSettingDeclarationException(string message) : Exception(message);

public static class StartupSettings
{
    public static readonly bool HaltOnErrors = false;  // Step 26 sets true (Q16); readonly, not const, to avoid CS0162
    public const string UndeclaredEnvHint = "...";     // Q24 hint text, facts and options only; Step 4 writes it
    public static Func<string, string?> EnvironmentReader { get; set; }   // default Environment.GetEnvironmentVariable
    public static Func<IEnumerable<string>> EnvironmentNames { get; set; } // default: keys of Environment.GetEnvironmentVariables()
    public static ConfigurationReport LastReport { get; private set; }     // empty, Halted false, before ConfigureAll
    public static IReadOnlyList<StartupSetting> Discover(Assembly root);   // root + transitive RoslynSentinel.* references
    public static IReadOnlyList<StartupSetting> Discover(IEnumerable<Type> groupTypes);
    public static ConfigurationReport ConfigureAll(IReadOnlyList<string> args, IReadOnlyList<StartupSetting> settings, SettingScope scopes, bool haltOnErrors); // sets LastReport
    public static ConfigurationReport ConfigureAll(IReadOnlyList<string> args, Assembly root, SettingScope scopes); // = (args, Discover(root), scopes, HaltOnErrors)
    public static IReadOnlyList<ConfigurationProblem> ConfigureGroup(Type groupType, IReadOnlyList<string> args); // does not touch LastReport
    public static void ThrowIfInvalid(ConfigurationReport report);         // throws ConfigurationInvalidException when Problems is non-empty
    public static string DescribeProblems(IReadOnlyList<ConfigurationProblem> problems); // one "<SettingName> (<Flag>, <EnvName>): <Kind>, raw '<RawValue>', expected <Expected>" line per problem, null parts omitted; always contains the flag and env names
}
```

```csharp
// RoslynSentinel.Server.Advanced/ConfigurationReferenceRenderer.cs (namespace of Server.Advanced)
public static class ConfigurationReferenceRenderer
{
    public static string RenderMarkdown(IReadOnlyList<StartupSetting> settings);
    public static string RenderConsole(IReadOnlyList<StartupSetting> settings);
}
```

Rules the bodies implement:
- Every raw value (flag or env) is trimmed before any rule below (today's parsers all trim;
  `SchemaLeanProfileTests.cs:422` passes `--schema-profile= lean `).
- Parsing (Q5, Q22): `--f=v`; `--f v` consumes the next token unless it starts with `--`, and for a
  bool setting only when it is a bool literal; bool literals true/false/1/0/on/off/yes/no
  (case-insensitive); a bare bool flag is true; a bare value flag is `MissingValue`; an empty or
  whitespace value (flag or env) is unset; flag beats env; repeated flag, last wins; enum names match
  case-insensitively ignoring `-` and `_`; a value below `minValue` or unparseable keeps the default,
  `Source = Invalid`, problem `InvalidValue` with `RawValue` and `Expected`.
- Inside `StartupSetting`, the factory names `String`, `Double`, `Enum` shadow BCL types: use the
  `string`/`double` keywords and `System.Enum` in bodies.
- `Discover`: in each `[StartupSettingGroup]` class, every `public static readonly` field assignable
  to `StartupSetting`; reading the field runs the class's static initializer. A flag or env name
  declared twice with overlapping `Scope` throws `StartupSettingDeclarationException`.
- `ConfigureAll`: applies every setting whose `Scope` intersects `scopes`, then each touched group's
  `PostConfigure` (its problems get kind `PostConfigure`); every `--token` not declared by any setting
  in the list (any scope) is `UnknownFlag`; every name from `EnvironmentNames` starting
  `ROSLYNSENTINEL_` not declared is added to `UndeclaredEnv`; `Halted = haltOnErrors && Problems.Count > 0`;
  one stderr line per problem and per undeclared name; sets `LastReport`.
- `ConfigureGroup`: discovers the group (`Discover(new[] { groupType })`, cached per type, so `Name`
  is set on this path too), applies its settings only (scope ignored), runs its `PostConfigure`,
  returns the problems.
- `PostConfigure`: a `static` method (public or private; bind with
  `BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic`) returning
  `IReadOnlyList<ConfigurationProblem>`. When it returns any problem, the registry calls `Reset()` on
  every setting of that group, so the group runs on defaults.
- Flag names match case-sensitively and exactly (as today's `GetArgValue`); `--solution-Path` is an
  `UnknownFlag`.

## Steps

### Step 1 - Extract the unrecoverable breaker filter
- Files: `RoslynSentinel.Server.Advanced/ServiceRegistrationExtensionsBasic.cs`, `RoslynSentinel.Server.Advanced/BreakerFilters.cs` (new)
- Depends on: none. Pairs with: Step 2.
- Effect: text-neutral.
- Change: move `private static void AddUnrecoverableBreakerFilter(IMcpRequestFilterBuilder filters)` (:1111-1162) into a new `public static class BreakerFilters` as `public static` (comment: public because Server.Advanced has no InternalsVisibleTo for its tests, the repo's existing pattern); the entry `("UnrecoverableBreaker", AddUnrecoverableBreakerFilter)` at :259 in `BuildCallToolFilterRegistrations` (:239) points at `BreakerFilters.AddUnrecoverableBreakerFilter`. `MoveMember` (toolset `moveExtract`).
- Done when: `Build` 0 errors.
- Lane: implementer.
- Done: c284da15

### Step 2 - Contract skeleton
- Files: `RoslynSentinel.Common/Configuration/StartupSetting.cs` (new), `RoslynSentinel.Common/Configuration/StartupSettings.cs` (new), `RoslynSentinel.Server.Advanced/ConfigurationReferenceRenderer.cs` (new)
- Depends on: none. Pairs with: Step 1.
- Effect: text-neutral (no callers).
- Change: the Contract verbatim, one `CreateFile` per file; method and property bodies `throw new NotImplementedException(); // TODO implement (Step 3|4|20)`; `HaltOnErrors`, `EnvironmentReader`, `EnvironmentNames` initialised to their real defaults; `UndeclaredEnvHint` a placeholder string. Placement in `Common`: 20 of the 21 options classes live there, and AgentLoop and PlanStepRunner see only `Common` (item 4).
  `NEW-DTO: ConfigurationProblem (Item, Common.Configuration)` / `Evaluated: ResultError - one error per call, no setting/flag/env fields; EngineError - engine failure shape` / `Wire impact: new tool output field(s) (Steps 21, 22b)`.
  `NEW-DTO: ConfigurationReport (Result, Common.Configuration)` / `Evaluated: HaltInfo - kind/reason/recovery strings, no problem list` / `Wire impact: none`.
  `NEW-DTO: SettingRead<T> (Result, Common.Configuration)` / `Evaluated: tuple (forbidden, item 1); ConfigurationProblem - no value` / `Wire impact: none`.
- Done when: `Build` 0 errors.
- Lane: implementer.
- Done: 0655a422

### Step 3 - Setting types and parsing
- Files: `RoslynSentinel.Common/Configuration/StartupSetting.cs`
- Depends on: Step 2. Pairs with: Step 4.
- Effect: text-neutral (no callers yet).
- Change: implement `StartupArgs`, the factories, `StartupSetting<T>` (`Read`, `Apply`, `Reset`, `Value`, texts, `Source`) per "Rules".
- Done when: `Build` 0 errors.
- Lane: implementer-senior (token lookahead and generic parsing; every later step depends on it).
- Done: 6aeae685

### Step 4 - Registry
- Files: `RoslynSentinel.Common/Configuration/StartupSettings.cs`
- Depends on: Step 2. Pairs with: Step 3.
- Effect: text-neutral.
- Change: implement `Discover` (both; the `root` overload walks `GetReferencedAssemblies()` transitively, names starting `RoslynSentinel.`), `ConfigureAll` (both), `ConfigureGroup` (incl. `PostConfigure` invocation by reflection on the named static method), `ThrowIfInvalid`, `DescribeProblems`, `LastReport`, and the `UndeclaredEnvHint` text, e.g. "Undeclared ROSLYNSENTINEL_* names are listed with settings: nonDefault or settings: all." per "Rules".
- Done when: `Build` 0 errors.
- Lane: implementer-senior (reflection discovery and scope rules).
- Done: 51418125

### Step 5 - Parser and registry tests (T1-T6b)
- Files: `RoslynSentinel.Tests.Basic/StartupSettingParserTests.cs` (new), `RoslynSentinel.Tests.Basic/StartupSettingRegistryTests.cs` (new)
- Depends on: Steps 3, 4. Pairs with: Step 6.
- Effect: tests only. Fixture group classes declared inside the test files, flags prefixed `--test-fixture-`, env names prefixed `TESTFIXTURE_` (never `ROSLYNSENTINEL_`); T4's undeclared `ROSLYNSENTINEL_*` name is built by concatenation (`"ROSLYNSENTINEL" + "_TEST_UNDECLARED"`, comment: keeps T10 from treating it as a name). Tests use `Discover(IEnumerable<Type>)` and the list overload of `ConfigureAll`, never `Discover(Assembly)`.
- Done when: T1-T6b pass.
- Lane: test-writer.
- Done: 0a41fc63

### Step 6 - Migrate LlmOptions
- Files: `RoslynSentinel.Common/LlmOptions.cs`
- Depends on: Steps 3, 4. Pairs with: Step 5.
- Effect: Q5/Q22 parsing only (e.g. a bare `--llm-minimal-tools` now means true; `--llm-parallelism=0` is Invalid instead of silently the default).
- Change: per Execution rules; 13 settings, scope `Server | PlanStepRunner`; `Parallelism` and `StreamIdleTimeoutSeconds` use `Int(..., minValue: 1)`; `--llm-host-sampler` is `HostSamplerSetting` via `Custom` whose parse keeps today's behaviour (malformed pairs skipped, always returns true); `HostSampler` stays a `{ get; private set; }` property, assigned in `PostConfigure` from `HostSamplerSetting.Value` plus today's fold of `TopK`/`MinP`/`RepeatPenalty` (:171-178); `Configure` becomes `StartupSettings.ConfigureGroup(typeof(LlmOptions), args);`; delete the private `GetArgValue` (:189). One `WriteFile`.
- Done when: `Build` 0 errors.
- Lane: implementer.
- Done: cc04aa92

### Step 7 - Migrate ReplaceSnippetOptions, SchemaOptions, ErrorPolicyOptions
- Files: `RoslynSentinel.Common/ReplaceSnippetOptions.cs`, `RoslynSentinel.Common/SchemaOptions.cs`, `RoslynSentinel.Common/ErrorPolicyOptions.cs`
- Depends on: Steps 3, 4. Pairs with: Step 8.
- Effect: as Step 6. `SchemaOptions.ConfigureHiddenParameters` and `EffectiveHiddenParameters()` keep their signatures; `--hide-params` is a `Custom` string set. `ErrorPolicyOptions` scope `Server | PlanStepRunner`, others `Server`; its descriptor fields are `ModeSetting` (`--error-policy-mode` / `ROSLYNSENTINEL_ERROR_POLICY_MODE`) and `FileSetting` (`--error-policy-file` / `ROSLYNSENTINEL_ERROR_POLICY_FILE`). `ErrorPolicyOptions.Configure` keeps its contract: it first evaluates both settings with `Read(StartupArgs.Parse(args), StartupSettings.EnvironmentReader)` and the cross-field rule (Inline needs a readable file, `ErrorPolicyOptions.cs:21-37`), throws `ArgumentException` before assigning anything if either fails (old values survive, as today), else calls `ConfigureGroup`; the same cross-field rule is its `PostConfigure` for the `ConfigureAll` path (on a problem the group resets, so Mode falls back to Off rather than Inline with no file).
- Done when: `Build` 0 errors.
- Lane: implementer.
- Done: f16f7295, 1bcefe84

### Step 8 - Migrate EolPolicyOptions, AgentOperatingModeOptions, FullSuiteStalenessOptions
- Files: `RoslynSentinel.Common/EolPolicyOptions.cs`, `RoslynSentinel.Common/AgentOperatingModeOptions.cs`, `RoslynSentinel.Common/FullSuiteStalenessOptions.cs`
- Depends on: Steps 3, 4. Pairs with: Step 7.
- Effect: as Step 6. `AgentOperatingModeOptions`: `Configured` becomes an `Enum` descriptor (kebab names such as `single-agent` match per Q22); `Effective`/`Source`/`ResolveOnce` unchanged; the `_latched` reset that `Configure` does today moves to `PostConfigure`. `FullSuiteStalenessOptions`: max-age days via `Custom` (double, must be > 0, today's check at :28), max fast runs `Int(minValue: 1)` if today's code rejects values below 1 (keep whatever bound the code has). Remove the `// TODO share GetArgValue` at `FullSuiteStalenessOptions.cs:38`. Scope `Server`.
- Done when: `Build` 0 errors.
- Lane: implementer.
- Done: 888ac302

### Step 9 - Migrate LoopLatchOptions, LiftRenamesOptions, NearMissOptions
- Files: `RoslynSentinel.Common/LoopLatchOptions.cs`, `RoslynSentinel.Common/LiftRenamesOptions.cs`, `RoslynSentinel.Common/NearMissOptions.cs`
- Depends on: Steps 3, 4. Pairs with: Step 11.
- Effect: as Step 6. `LoopLatchOptions.GetFlagValue` and `ParseEnabled` STAY (still called by `NotFoundInventoryOptions.cs:27` until Step 10); `LiftRenames` and `NearMiss` stop calling them; `OverrideForCurrentContext` unchanged. Scope `Server`.
- Done when: `Build` 0 errors.
- Lane: implementer.
- Done: 47e6f24f

### Step 10 - Migrate IdleRestartOptions, NotFoundInventoryOptions
- Files: `RoslynSentinel.Common/IdleRestartOptions.cs`, `RoslynSentinel.Common/NotFoundInventoryOptions.cs`, `RoslynSentinel.Tests.Basic/NotFoundInventoryOptionsTests.cs`
- Depends on: Step 9. Pairs with: Step 12.
- Effect: as Step 6, plus Q22 for NotFoundInventory: `on`/`yes` now mean true, and `2`/`bogus` are Invalid so the default (true) applies. `NotFoundInventoryOptionsTests.Configure_EnvVarOtherValue_LeavesItOff` is rewritten: `on`/`yes` move to the enabled cases; `2`/`bogus` assert `Enabled == true` (default) and an `InvalidValue` problem from `ConfigureGroup`; rename the test to match. IdleRestart settings (`--idle-restart-minutes`, `--builds-root`) scope `ServerStdio` (documents the deliberate HTTP omission); `IdleRestartOptions.ResetForTests` resets its descriptors; remove the `// TODO share GetArgValue` at `IdleRestartOptions.cs:37`. `NotFoundInventoryOptions.EnvironmentVariable` stays a `const` passed as `env:` and stops calling `LoopLatchOptions.GetFlagValue`.
- Done when: `Build` 0 errors.
- Lane: implementer.

### Step 10b - OrientationBreakerOptions; remove the LoopLatchOptions parsing helpers
- Files: `RoslynSentinel.Common/OrientationBreakerOptions.cs`, `RoslynSentinel.Common/LoopLatchOptions.cs`
- Depends on: Step 10.
- Effect: as Step 6.
- Change: migrate `OrientationBreakerOptions` (scope `Server`) per Execution rules. In `LoopLatchOptions`, delete `GetFlagValue` and `ParseEnabled` if `Search(mode: references, includeTests: true)` shows no caller; otherwise mark them `[Obsolete("Use StartupSettings")]`.
- Done when: `Build` 0 errors.
- Lane: implementer.

### Step 11 - Migrate ToolCallEchoOptions, ResultMarshallerOptions
- Files: `RoslynSentinel.Common/ToolCallEchoOptions.cs`, `RoslynSentinel.Tests.Server/ToolCallEchoFilterTests.cs`, `RoslynSentinel.Common/ResultMarshallerOptions.cs`
- Depends on: Steps 3, 4. Pairs with: Step 9.
- Effect: Q22 only: `--echo-tool-args=yes` now enables echo (`ToolCallEchoFilterTests.cs:499` `Configure_Banana_StaysDisabled("--echo-tool-args=yes")` moves to the enabled cases; the other banana values assert disabled plus an `InvalidValue` problem); an unknown `ResultMarshaller` value becomes Invalid. ResultMarshaller is an env-only `Live` setting, preserving the per-access read that `LargeResultOffloadFilterTests`, `ToolCallEchoFilterTests` and `WirePathRelativizeFilterTests` rely on; `ResultMarshallerOptions.EnvVarName` stays `const`, `Parse` stays public (delegating per Execution rules). Scope `Server`.
- Done when: `Build` 0 errors.
- Lane: implementer.
- Done: b5848b71

### Step 11b - Migrate WirePathOptions
- Files: `RoslynSentinel.Common/WirePathOptions.cs`
- Depends on: Steps 3, 4. Pairs with: Step 10b.
- Effect: Q22 only (`ROSLYNSENTINEL_RELATIVE_PATHS=bogus` becomes Invalid; default on applies). Env-only `Live` setting, scope `Server`, preserving the per-access read that `WirePathRelativizeFilterTests` relies on.
- Done when: `Build` 0 errors.
- Lane: implementer.

### Step 12 - Migrate ChangedContentOptions, ServerInstancePaths, ScratchRoot
- Files: `RoslynSentinel.Common/ChangedContentOptions.cs`, `RoslynSentinel.Common/ServerInstancePaths.cs`, `RoslynSentinel.Common/ScratchRoot.cs`
- Depends on: Steps 3, 4. Pairs with: Step 10.
- Effect: text-neutral.
- Change: `ChangedContentOptions`: env-only `Startup` descriptor declared first; `InlineOnApply` stays a settable static initialised from `<Setting>.Read(StartupArgs.Empty, StartupSettings.EnvironmentReader).Value`. `ServerInstancePaths`: flag-only descriptors (env null) for `--working-dir`, `--tab-state-dir` (scope `Server`) declared above `CurrentLazy`; `Resolve(args, binDir)` reads them with `Read(StartupArgs.Parse(args), StartupSettings.EnvironmentReader)`; its private `GetArgValue` (:77) goes. `ScratchRoot`: `ROSLYNSENTINEL_SCRATCH_ROOT` and `_RESULTS_ROOT` (env-only, `Live`, scope `Server | PlanStepRunner | AgentLoop | TestHarness`).
- Done when: `Build` 0 errors.
- Lane: implementer.

### Step 13 - Single-source REPO_ROOT and INSTANCE_ID
- Files: `RoslynSentinel.Common/RepoRootLocator.cs`, `RoslynSentinel.Common/ToolTelemetry.cs`, `RoslynSentinel.Engines.Basic/BuildIsolation.cs`
- Depends on: Steps 3, 4. Pairs with: Step 14.
- Effect: text-neutral.
- Change: `RepoRootLocator` gets `[StartupSettingGroup("Repository roots")]` and `RepoRootSetting` for `ROSLYNSENTINEL_REPO_ROOT` (const at :6, env-only `Live`, scope `Server | PlanStepRunner | AgentLoop | TestHarness`); its read at :47 uses `RepoRootSetting.Value`. `ToolTelemetry` gets `[StartupSettingGroup("Instance identity")]` and `InstanceIdSetting` for `ROSLYNSENTINEL_INSTANCE_ID` (const at :18, env-only `Live`, scope `Server`); its read at :92 uses `InstanceIdSetting.Value`. `BuildIsolation.cs:23` drops its literal and references the `ToolTelemetry` const; its read at :104 uses `ToolTelemetry.InstanceIdSetting.Value`.
- Done when: `Build` 0 errors.
- Lane: implementer.
- Done: f4fe4bbc

### Step 14 - AgentLoop env reads
- Files: `RoslynSentinel.AgentLoop/LoopAssist.cs`, `RoslynSentinel.AgentLoop/ReasoningReplayPolicy.cs`, `RoslynSentinel.AgentLoop/LmStudioAgentClient.cs`
- Depends on: Steps 3, 4. Pairs with: Step 13.
- Effect: Q22 only.
- Change: env-only `Live` descriptors, scope `AgentLoop`, each taking the existing `const` (`LoopAssistOptions.EnabledVariable`/`RoleVariable` :39-40; `ReasoningReplayPolicy` pressure :21, window :48). `LoopAssistOptions.FromEnvironment(Func<string,string?>)` reads via `Read(StartupArgs.Empty, reader)`; its default reader (:47) becomes `StartupSettings.EnvironmentReader`; an invalid value keeps the default, as `IsTruthy` does today (no new throw). `ReasoningReplayPolicy`: pressure via `Custom` (double in (0,1], today's check), window as `NullableInt(minValue: 0)` (or the bound today's parser enforces). The internal raw-text helpers `ParseWindowOverride(string?)` and `ParsePressureFractionOverride(string?)` keep their signatures and delegate via `<Setting>.Read(StartupArgs.Empty, _ => text)`; on a `Problem` they throw their existing `FormatException` with message `StartupSettings.DescribeProblems([problem])`, which contains the env name, so `ReasoningReplayPolicyTests.ParsePressureFractionOverride_Throws_WhenNotAFractionAboveZero` (`Tests.ErrorVariants/ReasoningReplayPolicyTests.cs:197-200`) and `LmStudioAgentClientInputItemsTests.ParseReasoningWindowOverride_Throws_WhenNotANonNegativeInteger` (:136-140) stay green (`LmStudioAgentClient.ParseReasoningWindowOverride` forwards to `ParseWindowOverride`). `LmStudioAgentClient.cs:817` const stays; its read at :826 uses the `ReasoningReplayPolicy` window descriptor.
- Done when: `Build` 0 errors.
- Lane: implementer.
- Done: e47ef3d7

### Step 14b - EvalQueueSetupMarker and External env vars
- Files: `RoslynSentinel.AgentLoop/EvalQueueSetupMarker.cs`, `RoslynSentinel.Common/Configuration/ExternalEnvironmentSettings.cs` (new), `RoslynSentinel.Engines.Advanced/CommentingEngine.cs`
- Depends on: Steps 3, 4. Pairs with: Step 15.
- Effect: text-neutral.
- Change: `EvalQueueSetupMarker` (:15) env-only `Live` descriptor, scope `AgentLoop`. New `[StartupSettingGroup("External environment")] public static class ExternalEnvironmentSettings` declaring `NUGET_PACKAGES` and `CLAUDE_CODE_SESSION_ID` (kind `External`, scope `Server`, Q8); `CommentingEngine.cs:478` reads its descriptor. (`ServiceRegistrationExtensionsBasic.cs:467` moves in Step 20.)
- Done when: `Build` 0 errors.
- Lane: implementer.
- Done: 5aa50c88

### Step 15 - ThinkingOptions
- Files: `RoslynSentinel.AgentLoop/ThinkingOptions.cs`, `RoslynSentinel.Tests.Harness/ThinkingOptionsTests.cs`
- Depends on: Steps 3, 4. Pairs with: Step 14b.
- Effect: an invalid value throws `ConfigurationInvalidException` instead of `InvalidThinkingSettingException`.
- Change: `[StartupSettingGroup("Thinking (ModelEval)")]`; its four `const string` names (`ROSLYNSENTINEL_MODELEVAL_THINKING` off|triggered|always, `_THINKING_TRIGGERS` comma list error,latchRefusal,firstTurn, `_THINKING_EFFORT` non-blank string default `low`, `_REASONING_BUDGET` positive int, unset = none) become env-only `Live` descriptors, scope `AgentLoop` (`Enum`, `Custom`, `Custom`, `NullableInt(minValue: 1)`). `FromEnvironment(Func<string,string?>? readVariable)` keeps its signature, reads via `Read(StartupArgs.Empty, readVariable ?? StartupSettings.EnvironmentReader)` and throws `ConfigurationInvalidException` on any problem; remove `InvalidThinkingSettingException`. Per Q22 a blank effort is unset (default `low`), no longer a throw. `ThinkingOptionsTests.cs`: the `FromEnvironment_InvalidValue_Throws` cases assert `ConfigurationInvalidException` with `Problems[0].EnvName` equal to the variable (replacing `ex.Variable`); the two blank-effort cases (`"   "`, `""`) move to the unset test and expect `low`; `FromEnvironment_EmptyStringIsUnsetExceptEffort` becomes "empty string is unset" for all four.
- Done when: `Build` 0 errors.
- Lane: implementer.
- Done: 3fc5b101

### Step 16 - Server host flags
- Files: `RoslynSentinel.Server.Advanced/ServerHostSettings.cs` (new), `RoslynSentinel.Server.Advanced/ServerStartupHelpers.cs`, `RoslynSentinel.Server.Advanced/ServerStdio.cs`
- Depends on: Steps 3, 4. Pairs with: Step 17.
- Effect: Q5/Q22 parsing only. Note: today's `GetArgValue` (:331) takes any next token as the value, even one starting `--`; the new rule does not.
- Change: `[StartupSettingGroup("Server host")] public static class ServerHostSettings` declaring 21 flags, scope `Server` unless noted: `--testing`, `--mode`, `--modes`, `--solution`, `--base-repo-dir`, `--include-tools`, `--exclude-tools`, `--hide-tools`, `--toolsets`, `--port` (`NullableInt`), `--transport` (`String`), `--bind` (`Enum<HttpBindScope>`, default `Loopback`), `--respawn` (`Custom<RespawnPolicy?>`, enum names per Q22, null default), `--list-tools`, `--full`, `--output`, `--log-dir`, `--run-id`, `--step-id`, `--interactive` (scope `ServerStdio`), `--list-options` (new). Each `Parse*` method keeps its public signature and return type and reads its descriptor with `Read(StartupArgs.Parse(args), StartupSettings.EnvironmentReader)`: `ParseArgs` :52-99, `ParseHideTools` :113, `ParseToolsets` :120, `ParsePort(args, defaultPort)` :345 (caller default when `Source` is `Default` or `Invalid`; `--port=abc` still returns 5100, `HttpHostStartupArgsTests.cs:15-19`), `ParseTransport` :352 (returns the raw value; the unknown-transport `ArgumentException` stays in `Program.cs:19`), `ParseBind` :359 (`--bind=everything` is Invalid and returns `Loopback`, `:30`), `ParseRespawn(args, transport)` :369 (transport-derived default when `Source` is `Default` or `Invalid`; `--respawn=sometimes`, `:70`), `HandleListTools` :460, `ParseLogDirectory` :716, `ParseRunId` :724, `ParseStepId` :730; delete private `GetArgValue` (:331). `ServerStdio.cs:80` `args.Contains("--interactive")` becomes `ServerHostSettings.InteractiveSetting.Read(StartupArgs.Parse(args), StartupSettings.EnvironmentReader).Value` (not `.Value` on the descriptor: `ConfigureAll` only arrives in Step 18).
- Done when: `Build` 0 errors.
- Lane: implementer-senior (11 methods in a 941-line file). Dispatcher afterwards runs `HttpHostStartupArgsTests`, `ToolClassSelectionTests`, `SubAgentLaunchTests`.

### Step 17 - Test-harness declarations
- Files: `RoslynSentinel.Tests.Harness/TestHarnessSettings.cs` (new)
- Depends on: Steps 3, 4. Pairs with: Step 16.
- Effect: text-neutral (declarations only; consumers keep parsing their own values).
- Change: `[StartupSettingGroup("Test harness")] public static class TestHarnessSettings`, all 34 as `String` env-only `Live` descriptors, scope `TestHarness`, default null, so no value can fail the harness. Where a `const string` already exists in Tests.Harness, pass it as `env:`; otherwise a literal.
  - Tests.Harness consts: `ROSLYNSENTINEL_MODELEVAL_CONTRACT`, `_CONTRACT_ENDINGS` (`EvalContractOptions`), `_TURN_CAP` (`ScenarioDefinition`), `_RESCORE_RUN` (`ScenarioRescoreRunTests`), `_SCORE_TRANSCRIPT`, `_SCORE_CSV`, `_SCORE_TOOL` (`SavedTranscriptScoring`), `_RESUME_TRANSCRIPT`, `_RESUME_KEEP_TURNS`, `_RESUME_SAMPLES`, `_RESUME_CONTINUE_TURNS` (`TranscriptResume`), `ROSLYNSENTINEL_UPDATE_TOOL_SURFACE_GOLDEN` (`ToolSurfaceGolden`).
  - Literals: `ROSLYNSENTINEL_MODELEVAL_REPLAY_TRANSCRIPT`, `_REPLAY_SIZE`, `_TOOLSET`, `_EDIT_ARM`, `_STEP_WALL_CLOCK_MINUTES`, `_SCENARIO`, `_SCENARIO_SAMPLES`, `_PROMPT_VARIANT`, `_SIZES`, `_REPEATS`, `_REPLAY_EARLY_STOP`, `_RESUME_LITERAL_CASE`, `_RESUME_STEP`; `ROSLYNSENTINEL_UPDATE_GENERATED_DOCS`, `_UPDATE_LONG_MESSAGE_BASELINE`, `_UPDATE_PLACEMENT_BASELINE`, `_UPDATE_HINT_BASELINE`, `_UPDATE_TOOL_RETURN_BASELINE`; `ROSLYNSENTINEL_TEST_KEEP`, `_TEST_ENV_PASSTHROUGH`, `_TEST_SERVER_OUTPUT`; `ROSLYNSENTINEL_FAKE_WRITE_AUDIT`.
- Done when: `Build` 0 errors.
- Lane: implementer.

### Step 18 - Server entry points (report-only)
- Files: `RoslynSentinel.Server.Advanced/ServerStdio.cs`, `RoslynSentinel.Server.Advanced/ServerHttp.cs`
- Depends on: Steps 6-16.
- Effect: behaviour-changing: host builders get `[]` (Q17); problems and undeclared env names written to stderr; nothing halts (`HaltOnErrors` false).
- Change: at the top of `ServerStdio.Startup`, before `ParseArgs` (:41), `StartupSettings.ConfigureAll(args, typeof(ServerStdio).Assembly, SettingScope.Server | SettingScope.ServerStdio);`, deleting the 14 `Configure` calls (:43-57); the `// TODO` at :49 is re-created above the `ConfigureAll` call. `ServerHttp.Startup` likewise with `SettingScope.Server`, deleting its 13 calls (:31-44). `Host.CreateApplicationBuilder(args)` (:106) and `WebApplication.CreateBuilder(args)` (:73) receive `[]`.
- Done when: `Build` 0 errors.
- Lane: implementer.

### Step 19a - ModelEval harness entry point
- Files: `RoslynSentinel.Tests.ModelEval/ModelEvalAssemblySetUp.cs`
- Depends on: Steps 6-17. Pairs with: Step 18.
- Effect: behaviour-changing: the harness applies every `Server` setting from env and fails fast on invalid settings (Q14, Q23).
- Change: keep the comment and `TestHostAmbientDefaults.Apply()` (:36-37). Replace the 5 `Configure([])` calls and the `ErrorPolicyOptions.Configure(...)` line (:38-52) with `var report = StartupSettings.ConfigureAll(ErrorPolicyFile.ResolveConfigureArgs(StartupSettings.EnvironmentReader(ErrorPolicyOptions.ModeSetting.EnvName!), StartupSettings.EnvironmentReader(ErrorPolicyOptions.FileSetting.EnvName!)), typeof(ModelEvalAssemblySetUp).Assembly, SettingScope.Server | SettingScope.AgentLoop | SettingScope.TestHarness); StartupSettings.ThrowIfInvalid(report);` (keep the deviation comment, reworded: the resolved args become flags, which beat env). `ModelAgentRunner.ThinkingDefault = ThinkingOptions.FromEnvironment();` stays after it.
- Done when: `Build` 0 errors.
- Lane: implementer.

### Step 19b - PlanStepRunner entry point
- Files: `RoslynSentinel.Utilities.PlanStepRunner/RunnerOptions.cs`, `RoslynSentinel.Utilities.PlanStepRunner/Program.cs`
- Depends on: Steps 6-17. Pairs with: none (runs after the 18 + 19a pair).
- Effect: behaviour-changing: the runner fails fast on invalid settings (Q14).
- Change:
  - `RunnerOptions`: `[StartupSettingGroup("PlanStepRunner")]` descriptors for its 11 flags, scope `PlanStepRunner` (`--branch-mode` an `Enum<BranchMode>`; `--run-dir` and `--branch` `String` with null defaults). `Parse` keeps its signature and every current `ArgumentException` path: a missing required flag; an Invalid value of any of its settings (e.g. a bad `--branch-mode`, built with `DescribeProblems`); `--start-step` > `--end-step`; and the `DefaultRunDir` mapping of `ScratchRoot`'s `InvalidOperationException` to `ArgumentException` naming the variable (`PlanStepRunnerSurfaceTests.cs:55` `Options_RelativeScratchRoot_ThrowsArgumentExceptionNamingTheVariable`; `ScratchRootTests.cs:63,107`). The derived defaults stay in `Parse`: `--run-dir` from `--repo` (`DefaultRunDir`), `--branch` from the run-dir leaf. Delete `GetArg` (:150) and `HasFlag` (:163); fix the stale "--plan-runner-*" summary (:18).
  - `Program.cs:27-28`: replace the two `Configure` calls with `ConfigureAll(args, typeof(Program).Assembly, SettingScope.PlanStepRunner | SettingScope.AgentLoop)`; print each problem that is not `UnknownFlag` with `DescribeProblems` and return exit code 2; `UnknownFlag` entries are server pass-through flags (:205).
- Done when: `Build` 0 errors.
- Lane: implementer.

### Step 20 - Renderer and `--list-options`
- Files: `RoslynSentinel.Server.Advanced/ConfigurationReferenceRenderer.cs`, `RoslynSentinel.Server.Advanced/Program.cs`, `RoslynSentinel.Server.Advanced/ServiceRegistrationExtensionsBasic.cs`
- Depends on: Steps 14b, 16, 18. Pairs with: Step 21.
- Effect: behaviour-changing: new CLI switch.
- Change: renderer bodies. Markdown: header `<!-- Generated by ConfigurationReferenceRenderer; DO NOT EDIT. Regenerate with: $env:ROSLYNSENTINEL_UPDATE_GENERATED_DOCS=1; scripts/Generate-ArchitectureMap.ps1 -->`, a short paragraph on declaring a new setting (a `StartupSetting.*` field in a `[StartupSettingGroup]` class), a link to `docs/generated/toolsets/` (Q7), then one table per group (ordered by scope, then title): Setting, Flag, Env, Type, Default, Scope, Kind, Description (never effective values). Console: one line per setting (flag or env, type, default, description). `Program.cs`: when `--list-options` is present, print `RenderConsole` of `Discover(typeof(Program).Assembly)` filtered to scopes Server/ServerStdio and return 0 before the transport switch. `ServiceRegistrationExtensionsBasic.cs:467` reads `ExternalEnvironmentSettings` for `CLAUDE_CODE_SESSION_ID`.
- Done when: `Build` 0 errors.
- Lane: implementer.

### Step 21 - McpServerStatus configuration section
- Files: `RoslynSentinel.Tools.Basic/ServerStatusTools.cs`
- Depends on: Step 18. Pairs with: Step 20.
- Effect: behaviour-changing: new wire field and parameter.
- Change: `public const string ToolName = "McpServerStatus";` on `ServerStatusTools`, used by its `[McpServerTool(Name = ToolName, ...)]` (:49). New parameter after `toolNameFilter` and before `cancellationToken`: `McpServerStatusSettingsListing settings = McpServerStatusSettingsListing.none` (enum `none, nonDefault, all` beside `McpServerStatusToolListing` :268), `[Description]` at most 80 chars (Q21); positional test callers that break get a named argument. New optional trailing `McpServerStatusConfiguration? Configuration` on `McpServerStatusResult` (:320-364), from `StartupSettings.LastReport`: `McpServerStatusConfiguration(IReadOnlyList<ConfigurationProblem> Problems, bool Halted, int UndeclaredEnvCount, IReadOnlyList<string>? UndeclaredEnv, string? UndeclaredEnvHint, IReadOnlyList<McpServerStatusSetting>? Settings)`; `UndeclaredEnv` and `Settings` only when `settings != none`; `UndeclaredEnvHint = StartupSettings.UndeclaredEnvHint` only when the list is omitted and the count is above 0 (Q24). `McpServerStatusSetting(string Name, string? Flag, string? Env, string Value, SettingSource Source, IReadOnlyList<SettingScope> Scopes)` (each single flag of `Scope`, so the wire never carries a combined flags string); `Value` is `EffectiveText`; `nonDefault` = `Source != Default`.
  `NEW-DTO: McpServerStatusConfiguration (View, Tools.Basic)` / `Evaluated: McpServerStatusIdleRestart - one feature; ConfigurationReport - holds live StartupSetting objects, not wire-safe` / `Wire impact: new tool output field(s)`.
  `NEW-DTO: McpServerStatusSetting (Item, Tools.Basic)` / `Evaluated: McpServerStatusDeclaredTool - tool-shaped` / `Wire impact: new tool output field(s)`.
- Done when: `Build` 0 errors. Dispatcher then runs `ClaudeLeanSchemaBudgetTests` (over budget: apply Q21) and regenerates the tool-surface golden (`ROSLYNSENTINEL_UPDATE_TOOL_SURFACE_GOLDEN=1`, `Tests.Harness/ToolSurfaceGolden.cs`), committing the updated golden with the step.
- Lane: implementer.

### Step 22a - ConfigurationInvalid error code
- Files: `RoslynSentinel.Common/SentinelCallToolResult.cs`, `RoslynSentinel.Common/IResultStatus.cs`
- Depends on: none. Pairs with: none (wave 1, after the 1 + 2 pair).
- Effect: text-neutral.
- Change: `ToolErrorCode.ConfigurationInvalid` const (class at `SentinelCallToolResult.cs:75`); map it in `ResultStatusExtensions.FromErrorCode` (`IResultStatus.cs:55`) to the status `PreconditionNotMet` maps to. If a test enumerates the codes, it is updated in this step.
- Done when: `Build` 0 errors.
- Lane: implementer.
- Done: 2714c690

### Step 22b - Halt filter and shared refusal envelope (disabled)
- Files: `RoslynSentinel.Server.Advanced/BreakerFilters.cs`, `RoslynSentinel.Server.Advanced/LoopLatchFilter.cs`
- Depends on: Steps 1, 21, 22a.
- Effect: text-neutral while `HaltOnErrors` is false; `LoopLatchFilter` output unchanged.
- Change: `public static CallToolResult BuildErrorResult(ResultError error)` in `BreakerFilters`, the envelope `LoopLatchFilter.Refusal` builds today (`LoopLatchFilter.cs:67-83`: `SentinelCallToolResult<object> { IsError = true, ErrorData = error }` serialized with `McpJsonUtilities.DefaultOptions` into a `TextContentBlock`, `IsError = true`); `LoopLatchFilter.Refusal(string message)` keeps its signature and returns `BreakerFilters.BuildErrorResult(new ResultError(ToolErrorCode.TargetIneligible, message))`. `public static void AddConfigurationHaltFilter(IMcpRequestFilterBuilder filters, Func<ConfigurationReport> report)`: when `report().Halted` and the tool name is not `ServerStatusTools.ToolName`, return `BuildErrorResult(new ResultError(ToolErrorCode.ConfigurationInvalid, <facts-only message: problem count and that McpServerStatus lists them>, StructuredDetail: report().Problems))`.
- Done when: `Build` 0 errors.
- Lane: implementer.

### Step 23 - Register the halt filter
- Files: `RoslynSentinel.Server.Advanced/ServiceRegistrationExtensionsBasic.cs`, `RoslynSentinel.Tests.Server/ToolTelemetryFilterOrderTests.cs`
- Depends on: Steps 20, 22b.
- Effect: text-neutral while `HaltOnErrors` is false.
- Change: in `BuildCallToolFilterRegistrations` (:239) add `("ConfigurationHalt", f => BreakerFilters.AddConfigurationHaltFilter(f, () => StartupSettings.LastReport))` unconditionally, directly after the `UnrecoverableBreaker` entry (:259), i.e. last in the fixed list; in `ToolTelemetryFilterOrderTests` the pinned filter counts at :80 and :101 go from 15 to 16 and 16 to 17 (with their comments and messages). `LoopLatchTests.cs:351` (`names[4]`) and `NearMissRecordTests.cs:141` (position relative to `ToolTelemetry`) are unaffected by an entry after :259.
- Done when: `Build` 0 errors; dispatcher runs `ToolTelemetryFilterOrderTests`, `LoopLatchTests`, `NearMissRecordTests`.
- Lane: implementer.

### Step 24 - Generated reference
- Files: `RoslynSentinel.Tests.ModelEval/ConfigurationReferenceFreshnessTests.cs` (new), `docs/generated/configuration.md` (generated), `scripts/Generate-ArchitectureMap.ps1`, `docs/current/DOCUMENTATION_INDEX.md`
- Depends on: Steps 6-20. Pairs with: Step 23.
- Effect: tests and docs only.
- Change: freshness test mirroring `ScenarioCatalogFreshnessTests` (regenerate when `ROSLYNSENTINEL_UPDATE_GENERATED_DOCS` is set, else compare) over `ConfigurationReferenceRenderer.RenderMarkdown(StartupSettings.Discover(typeof(ConfigurationReferenceFreshnessTests).Assembly))`; regenerate the doc; add the test filter to the script; add a DOCUMENTATION_INDEX row.
- Done when: T9 passes.
- Lane: test-writer.

### Step 25 - Tests T7, T8, T10-T15
- Sub-slices:
  - 25a `RoslynSentinel.Tests.Server/ConfigurationHaltFilterTests.cs` (new, T7), `RoslynSentinel.Tests.Server/ServerStatusSettingsTests.cs` (new, T8), `RoslynSentinel.Tests.Server/ServerSettingsDiscoveryTests.cs` (new, T12, T13). Depends on Steps 20-23.
  - 25b `RoslynSentinel.Tests.ModelEval/EnvVarDeclarationGuardTests.cs` (new, T10), `RoslynSentinel.Tests.ModelEval/KnownArgSetsTests.cs` (new, T11, T15; both in the parent namespace `RoslynSentinel.Tests.ModelEval`, not `LiveModel`), `RoslynSentinel.Server.Advanced/Properties/launchSettings.json`. Depends on Steps 6-20.
  - 25c `RoslynSentinel.Tests.Harness/PlanStepRunnerSurfaceTests.cs` (T14). Depends on Step 19b.
- Change notes: T10 scans `*.cs` under the repo root (`RepoRootLocator`), excluding `*/Worktree/`, `bin/`, `obj/`; a literal is a whole C# string literal matching `^ROSLYNSENTINEL_[A-Z0-9_]*[A-Z0-9]$`; declared names come from `Discover(typeof(EnvVarDeclarationGuardTests).Assembly)`. T11 arg sets: the flags in the server-argument string literals of `scripts/roslynsentinel-mcp-launch.ps1` (:992-1020; match `--[a-z][a-z0-9-]*` only inside the quoted argument strings, ignoring log text and regex literals); every `launchSettings.json` profile's `commandLineArgs`, split into tokens quote-aware (Windows command-line rules, `\"` quotes, trimmed, so a trailing `\r\n` is dropped); `SubAgentChildServerLauncher.BuildServerArguments` and `ErrorPolicyWiring.ChildServerArgs` output; asserts zero problems against `Discover(typeof(ServerStdio).Assembly)`. Fix `launchSettings.json`: `--solution-Path=` to `--solution=` (:46, :51), `admintools` to `admin` (:51).
- Done when: T7, T8, T10-T15 pass.
- Lane: test-writer.

### Step 26 - Enable the halt
- Files: `RoslynSentinel.Common/Configuration/StartupSettings.cs`, `docs/current/plans/plan_json_toolset_definitions.md`
- Depends on: Steps 23, 25 (T11 green: the arg sets visible to tests are clean).
- Effect: behaviour-changing: an invalid setting or unknown flag halts every tool except `McpServerStatus`.
- Change: `HaltOnErrors = true`. Add a Risks note to `plan_json_toolset_definitions.md`: its new flags are `StartupSetting` declarations in `ServerHostSettings`, and an unknown toolset name is `InvalidValue` (Q7).
- Done when: `Build` 0 errors.
- Lane: implementer.
- Dispatcher verification after this step: `Search(mode: text, query: "NotImplementedException", fileGlob: "**/Configuration/**")` returns 0 and `ConfigurationReferenceRenderer.cs` has none; full `RunTest` baseline 0 failures; commit, restart the server, `McpServerStatus` shows `Configuration.Problems` empty and `Halted == false`; one manual `--list-options` run prints the server settings.

## Slice map

| Wave | Steps (pairs touch disjoint files) |
| --- | --- |
| 1 | 1 + 2; 22a |
| 2 | 3 + 4 (both senior) |
| 3 | 5 + 6; 7 + 8; 9 + 11; 13 + 14; 14b + 15 |
| 4 | 10 + 12; 10b + 11b; 16 + 17 |
| 5 | 18 + 19a; 19b |
| 6 | 20 + 21 |
| 7 | 22b; 23 + 24 |
| 8 | 25a + 25c; 25b |
| 9 | 26 |

## Out of scope

- Env vars read or set only by `scripts/*.ps1` (9 script-only names) and `.claude/hooks/*` (15 names) (Q2).
- Git's own environment (`PATH`, `PATHEXT`, `HOME`, `GIT_*`, `XDG_CONFIG_HOME`) (Q8).
- Converting the unrecoverable breaker filter's plain-text refusal to a structured error.
- Validating `--hide-params` entries against known parameter groups (`INBOX.md:25`); follow-up once the registry exists.
- Modelling the toolset JSON files themselves (owned by `plan_json_toolset_definitions.md`, Q7).
- Sweeping test literals to descriptor references (Q20).

## Risks and open decisions

- `plan_prebuild_and_idle_stale_server_restart.md` is IN PROGRESS and owns `IdleRestartOptions`
  (Step 10). Before Step 10, check its status and `Git(status)` for that file; if still in flight,
  coordinate with its session (`peer-coordination`, `HOLD`) or run Step 10 last in its wave.
- `plan_error_gated_thinking_and_client_reasoning_budget.md` is IN PROGRESS (`ThinkingOptions` landed
  in its Steps 1-10, through 8e02735a; its session confirmed no objection to Step 15 and Q22 on
  2026-11-25). Steps 15 and 19a touch `ThinkingOptions.cs`, `ThinkingOptionsTests.cs` and
  `ModelEvalAssemblySetUp.cs`; check `Git(status)` for them first.
- `plan_stateless_http_toolset_enablement.md` (APPROVED) edits `ServerHttp.cs` (Step 18). Whichever
  lands second rebases on the other's `Startup` body.
- Q22/Q23 can make a box's existing env loud: a junk value that "worked" now halts the server (after
  Step 26) or fails the ModelEval namespace. Intended; the stderr line and `DescribeProblems` name it.
- `Discover(Assembly)` uses `GetReferencedAssemblies()`, which omits a project reference whose types
  are never used (Server.Advanced reaches AgentLoop only through Tools.Advanced). T12 asserts the
  server finds its groups; if it fails, the server passes an explicit assembly list (implementer default).
- The halt filter sits last in the fixed filter list, beside the unrecoverable breaker (its
  precedent), so an earlier filter's refusal (e.g. workspace not loaded) can answer first; the next
  call still meets `ConfigurationInvalid`, and `McpServerStatus` shows the halt. Accepted as is.
- Users' own `mcp.json` args are not visible to T11; an unknown flag there halts that user's server
  after Step 26, and `McpServerStatus` names it.
