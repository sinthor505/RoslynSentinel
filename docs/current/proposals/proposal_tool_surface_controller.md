# One front door for the server's tool surface: layered inputs, one composed spec, one atomic apply

**Status:** PROPOSED 2026-11-23. Owner endorsed the direction and the shape below on 2026-11-23; recommended decisions given. Plan: [plan_tool_surface_controller.md](../plans/closed/plan_tool_surface_controller.md) (stage 1 plus the schema-patch seam), IMPLEMENTED 2026-11-24. Stage 2: [plan_tool_surface_controller_stage_c.md](../plans/closed/plan_tool_surface_controller_stage_c.md), IMPLEMENTED 2026-11-24. Stage 3: [plan_tool_surface_controller_stage_d.md](../plans/closed/plan_tool_surface_controller_stage_d.md), IMPLEMENTED 2026-11-24. Stage 4: [plan_tool_surface_controller_stage_e.md](../plans/closed/plan_tool_surface_controller_stage_e.md), IMPLEMENTED 2026-11-24. Stage 5: [plan_tool_surface_controller_stage_f.md](../plans/closed/plan_tool_surface_controller_stage_f.md), IMPLEMENTED 2026-11-24 (merged eafe6bf3). Stage 6 open: one plan per stage (`plans/plan_tool_surface_controller_stage_<c|d|e|f>.md`), TODO.md "Tool-surface controller stages 2-6".

## Motivation

The question "what tool surface does this server expose right now, and why?" has no single answer. The
answer is spread over about a dozen components with different lifetimes (process static, DI singleton,
per-call), different triggers (startup args, DI registration, first resolve, runtime toolset calls, test
harness resets) and no shared version. Troubleshooting a missing or wrong tool therefore starts with
"which of the many places could the problem be?", and a client-side problem cannot be cleanly separated from
a server-side one.

The goal (owner, 2026-11-23): **the server can state definitively what it exposes, regardless of what the
client shows**, so troubleshooting starts from one authoritative snapshot instead of a hunt.

### Where tool-surface state lives today

Traced read-only on 2026-11-23 (option-1 investigation; file:line as of that date):

| State | Location | Lifetime / trigger | Invalidation today |
| --- | --- | --- | --- |
| Active modes and classes | `ServerStartupHelpers.ResolveActiveToolClasses` (`ServerStartupHelpers.cs:294`), `ToolClassRegistry.ModeToToolClasses` | read once at DI registration | none; a mode change needs a restart |
| Per-class tool registration | 20 `if (activeToolClasses.Contains(...))` blocks in `ServiceRegistrationExtensionsBasic.cs`, 11 in `...Advanced.cs`; `WithSentinelTools<T>` lazy singletons (`McpToolSchemaPatcher.cs:154-155`) | built on first resolve | none |
| claude-lean allow-list | `ToolAllowList` (`ToolAllowList.cs`), read inside `WithSentinelTools` (`McpToolSchemaPatcher.cs:136-139`) | registration time | not applicable after startup |
| `ActiveToolSurface` | DI singleton, built at `ServiceRegistrationExtensionsBasic.cs:378-386`; captured by reference in `PersistentWorkspaceManager`, engine `isCallable` closures and tool constructors | startup | none; `IsCallable` already ignores runtime toolsets (`ActiveToolSurface.cs:60-64`) |
| `WriteToolAdviceHelper` | DI singleton (`ServiceRegistrationExtensionsBasic.cs:372`), copies class set and allow-list | startup | none |
| Runtime toolsets | `ToolsetService` (`ToolsetService.cs`), only in claude-lean (`ServiceRegistrationExtensionsBasic.cs:397-452`); `_enabled` holds tool instances | `McpToolsetControl` calls, `--toolsets`, tab state | its own adds/removes only |
| Parameter hiding | `SchemaOptions` statics, `HiddenSchemaParams` static registry (`HiddenSchemaParams.cs`) | args/env at startup; harness resets | `Clear()` for tests |
| Parameter and value aliases | static dictionaries in `ToolArgumentValidator` (`:612`, `:728`, `:741`) | compile time | none |
| Schema options | `SchemaOptions.EmitDataTags`, `ReplaceSnippetOptions.Max*`, `ResultMarshallerOptions.Mode` (env read per access) | startup | none |
| Validator schema cache | static `SchemaCache` (`ToolArgumentValidator.cs:15`), per tool name, first call | first call | `ClearSchemaCacheForTests` only |
| Hint catalog | `ToolCallHint.Current`, fallback built once by reflection (`ToolSignatureCatalogReflection.cs:50-88`) | first use | none |
| `McpToolsetControl` enum and description | `ToolsetCatalog` statics | compile time | none |

Friction this causes (journal grep over `.claude/journal/*.md`, 2026-11-23, counts are approximate):
about 12 sessions hit configuration-only changes that needed a restart or reconnect (7 of them "tool is
gated behind a mode restart"), and about 19 hit "enable reported success but the client could not see or
call the tool". In the second group nobody could say from the server side alone whether the server or the
client was at fault.

## Proposal

### Properties (decided in principle, owner 2026-11-23)

1. **One source of truth.** An immutable, versioned `ToolSurfaceSnapshot` is the only answer to "what is
   exposed and why". Every consumer reads it; none keeps its own copy.
2. **One front door.** A single `ToolSurfaceController` accepts every change. Nothing else mutates the live
   tool collection.
3. **One code path.** Startup is the first apply; a runtime change is the same apply with a different layer.
4. **Atomic.** The next snapshot is built and validated off to the side. Only a valid snapshot is
   committed; a failed apply leaves the live surface untouched and is reported.
5. **Serialized.** Applies run one at a time.
6. **Provenance.** Every tool and parameter in the snapshot records which layer put it there.

### Inputs: layers

Each source owns exactly one layer and replaces it wholesale when it changes:

| Layer source | Today's equivalent |
| --- | --- |
| Mode defaults | `ModeToToolClasses`, `ClaudeLeanToolNames` |
| Toolset file | planned JSON toolsets (`plan_json_toolset_definitions.md`) |
| Startup args and env | `--mode`, `--include/exclude/hide-tools`, `--toolsets`, `--hide-params`, `--schema-profile`, snippet limits |
| Tab state | `TabToolsetStateStore` |
| Runtime toolset calls | `McpToolsetControl` |
| Harness | eval worlds and test hosts (`ModelEvalMcpEnvironment`, `ChainEvalToolSurface`) |

A layer may carry: tool classes and tool names to include, exclude or hide; enabled toolsets; parameter hide
and alias rules (the `ParameterVisibilityRule` / `ParameterAliasRule` model of
`proposal_tool_parameter_surface_options.md`); schema options (data tags, snippet limits); description
overrides. Layers have a fixed precedence; within the parameter rules, the ordered-layer, last-match-wins
rule of that proposal applies to the whole surface.

### Compose

A pure function merges the layers into an immutable `ToolSurfaceSpec`. It needs no server and is unit
testable on its own; the same inputs always give the same spec.

### The front door

`ToolSurfaceController` (DI singleton) exposes `Current` (the committed snapshot) and one operation,
`ApplyAsync(layer)`, which runs these stages in order under one serialization lock:

1. **Compose** the layers, with the new layer replacing its predecessor, into the next spec.
2. **Build** the next snapshot from the spec: every tool through `CreateSentinelTool` with its patched
   schema, plus the derived views (callable set, allow-list, hidden parameters with their winning rule,
   alias map, declared parameters per tool, hint catalog).
3. **Validate:** unmatched hide rules, alias targets no tool declares, unknown tool or class names,
   optionally the schema budget. A blocking failure ends the apply here.
4. **Commit:** swap `Current`, then reconcile the live tool collection inside one `DeferChangedEvents` scope:
   in-place schema change for tools that stay, add and remove for membership changes. This raises one
   `Changed` event.
5. **Deliver:** send `tools/list_changed` by the route the protocol and transport allow (the delivery logic
   of `plan_stateless_http_toolset_enablement.md` Step 4) and record which route was taken.

The controller mutates the one shared `McpServerOptions.ToolCollection`; it never installs a new collection
object, because existing stateful sessions hold the old reference (`McpServerImpl.cs:1504`, SDK v2.4.0).

### Consumers

| Today | Becomes |
| --- | --- |
| `ActiveToolSurface`, `WriteToolAdviceHelper` | views of `Current` (or thin wrappers that read it per call) |
| `HiddenSchemaParams`, validator `SchemaCache` | snapshot views, or caches keyed by snapshot version |
| `ToolCallHint.Current` | built from the snapshot's hint catalog |
| `ToolsetService` | the runtime-toolset layer's source; its tool list comes from the snapshot |
| per-class DI blocks, `WithSentinelTools` | removed once the controller fills the collection at startup |
| `McpServerStatus` tool listing | reads the snapshot |

The call filter captures `Current` once per call and hands it to the validator and anything else that needs
it, so a call is checked against the surface it started under even if an apply commits meanwhile. An
in-flight call already finishes on the tool instance it matched (SDK `MatchTool` resolves per call,
`McpServerImpl.cs:1751-1760`).

### Diagnostics: the troubleshooting win

`McpServerStatus` gains a surface section read from the snapshot: version and commit time; the layers with
their source and origin; per tool, whether it is exposed and which layer decided it; per parameter, whether
it is hidden and by which rule; the last apply's outcome (including a validation failure that was not
committed); and the delivery route of the last change. With that, "the server exposes X at version N and
sent `list_changed` by route R at time T" is a fact, and a client that still lacks X is a client-side
problem by elimination.

### Migration path (each stage shippable)

1. **Snapshot and controller wrap the current startup, read-only.** The controller builds a snapshot from
   the already-registered tools; `ActiveToolSurface` and `McpServerStatus` read it. One source of truth
   for reads; no behaviour change.
2. **Runtime toolsets go through the controller.** `McpToolsetControl` and tab-state restores become layer
   applies; `ToolsetService` stops mutating the collection itself.
3. **Parameter rules move into the spec** (the reshaped parameter-surface plan).
4. **Caches keyed by snapshot version:** validator `SchemaCache`, `HiddenSchemaParams`, `ToolCallHint`.
5. **The controller owns startup population.** Per-class DI blocks and `WithSentinelTools` registration go;
   adding a tool shrinks to the class plus its `ToolClassRegistry` entry. Tools are already created per call
   by `ActivatorUtilities` (`McpToolSchemaPatcher.CreateTarget`, `:385-386`) and their dependencies are
   registered unconditionally (`AddRoslynSentinelToolImplsBasic`, `ServiceRegistrationExtensionsBasic.cs:370`),
   so the blocks' `AddSingleton<X>` lines are not needed.
6. **New sources:** the JSON toolset layer, and a runtime mode switch (an operation on the existing
   `McpServerControl`), which addresses the "gated behind a mode restart" sessions.

### Recommended decisions

1. **Whole layers, not imperative deltas.** A source replaces its layer; compose is pure. This gives replay,
   provenance and testability; deltas would need ordering and undo rules.
2. **Wrap first, own later.** Stage 1 wraps the existing startup; stage 5 replaces it. Nothing lands at once.
3. **Per-call snapshot.** A call is validated against the snapshot it started under.
4. **Mutate the shared collection.** Never swap the collection object (see Commit).
5. **Interfaces only where behaviour varies** (owner question, 2026-11-23). Schema patches (`IToolSchemaPatch`,
   an ordered DI collection), layer sources, validators and delivery are seams a test or eval host can add to or
   replace in its own service collection without production edits. The composer, controller and snapshot stay
   concrete: they are the single source of truth. Most A/B variants should be data in a harness layer, not code; the
   snapshot records the ordered patch names and layer origins so every eval result names the surface it ran against.

## Alternatives considered, not pursued

- **Keep fixing per component** (the parameter-surface plan as drafted, more `Clear()` hooks). Each fix adds
  another lifetime and trigger; it deepens the problem this proposal names.
- **A relay process in front of a restartable backend.** Covers new compiled code, which this does not, but
  gives no single source of truth inside the server. Compatible as a later addition.
- **Rebuild by recreating `McpServerOptions` or the collection object.** Existing stateful sessions keep the
  old reference and are never notified (`McpServerImpl.cs:119`, `:1504`).
- **Remove-then-add to replace a tool.** Leaves a window in which a call finds no tool and gets
  "Unknown tool" (`McpServerImpl.cs:1603`); the SDK collection has no replace-by-name.

## Open questions

- **Replace mechanism for same-name tools:** in-place mutation of `tool.ProtocolTool` (the patcher already
  assigns `InputSchema` this way; `tools/list` reads `t.ProtocolTool` per request, legacy wire shaping included,
  `McpServerImpl.cs:1552-1565`) versus a derived `McpServerPrimitiveCollection` with an atomically swapped snapshot
  (its `Count`, `IsEmpty` and indexer read base-private state; installing it needs `options.ToolCollection` set
  before `McpServerOptionsSetup`, which keeps an existing collection's identity, confirmed at
  `McpServerOptionsSetup.cs:26-39`). Recommendation: in-place for schema changes, add/remove for membership.
- **Does Claude Code re-read the schema of an already-listed tool after `list_changed`?** Adds and removes
  are verified live (2026-11-23); schema changes are not, and one journal entry shows a client keeping an old
  schema across a restart (`2026-11-12_1acedb4e`). A live probe is needed before stage 3 relies on it.
- **Startup-only options become spec fields:** `SchemaOptions`, `ReplaceSnippetOptions` (both `private set`
  statics). `ResultMarshallerOptions.Mode` reads the environment on every access. `ErrorPolicyOptions.Mode` is
  read when filters are registered, so a change would not reach built filters.
- **Runtime mode switch limits:** call filters registered per mode do not rebuild; `ToolsetService` exists
  only in claude-lean today; excludes such as the child server's `--exclude-tools=SubAgentTools` must
  survive a switch.
- **Constructor dependencies of the 10 classes outside claude-lean's on-demand set** are not traced
  (hypothesis: all registered, since `ToolClassRegistrationTests` covers every class).
- **HTTP: settled (owner, 2026-11-23).** `plan_stateless_http_toolset_enablement.md` lands first (one shared
  live collection, runtime toolsets over HTTP); the JSON toolsets plan dropped its HTTP fold. The controller
  mutates that shared collection on both transports.
- **Schema budget at apply time:** validate as a warning or a blocking failure.

## Cost / risk

- Touches tool registration, the request pipeline (per-call snapshot capture), `ToolsetService`,
  `McpServerStatus` and every harness that builds a tool surface (`ModelEvalMcpEnvironment`,
  `ChainEvalToolSurface`, about 50 test hosts that call `AddRoslynSentinelTools*`).
- Stage 5 changes how every tool is registered; the architecture map's "three places" section and the
  CLAUDE.md rule change with it.
- New DTOs (`ToolSurfaceSpec`, `ToolSurfaceSnapshot`, the layer record) need NEW-DTO justifications in the
  plan; candidates to evaluate first: `ActiveToolSurface`, `ToolAllowList`, `ToolsetDefinition` (planned).
- Risk of a long-lived half-migrated state; mitigated by making each stage end with one owner for its
  piece of state.

## Related

- [proposal_tool_parameter_surface_options.md](proposal_tool_parameter_surface_options.md) and
  [plan_tool_parameter_surface_options.md](../plans/closed/plan_tool_parameter_surface_options.md) - parameter rules;
  the plan is paused after Step 3 pending this proposal.
- [plan_stateless_http_toolset_enablement.md](../plans/plan_stateless_http_toolset_enablement.md) - shared
  collection and delivery routes; independent and can land first.
- [proposal_json_toolset_definitions.md](proposal_json_toolset_definitions.md) and
  [plan_json_toolset_definitions.md](../plans/plan_json_toolset_definitions.md) - becomes a layer source.
- [reference_architecture_map.md](../references/reference_architecture_map.md) - "Tool registration: three
  places" changes at stage 5.
