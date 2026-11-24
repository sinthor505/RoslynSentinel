# Named JSON toolset files replace mode + include + exclude + hide as the one way to choose a tool surface

**Status:** PROPOSED 2026-11-22. Design decisions settled with the owner on 2026-11-22. Prerequisite
[plan_tab_scoped_toolset_persistence.md](../plans/closed/plan_tab_scoped_toolset_persistence.md) closed 2026-11-23. Plan: [plan_json_toolset_definitions.md](../plans/plan_json_toolset_definitions.md) (DRAFT 2026-11-23; adds the owner rulings on group shape, HTTP fold and flag removal; the HTTP fold was dropped later the same day in favour of plan_stateless_http_toolset_enablement.md, and the plan is to be reviewed against proposal_tool_surface_controller.md).

## Motivation

### The trigger

`Launch-RoslynSentinelHttpServer.ps1 -Tools "ReadFile,GetFileOutline,..."` served all 114 registry tools,
a 151 KB `tools/list`, and that swamped a 70k-context Ornith child on host .111. Three things combined:

- The script's `-Mode` defaults to `all` (`scripts/Launch-RoslynSentinelHttpServer.ps1:60`). `-Tools`
  maps to `--include-tools=` (`:90-91`), which is unioned on top of the mode, so it could only widen the
  surface.
- `--include-tools` takes tool-class names. `ReadFile` is a tool name. `NormalizeToolClassNames`
  (`RoslynSentinel.Server.Advanced/ServerStartupHelpers.cs:260-287`) returns unknown names unchanged, and
  `ResolveActiveToolClasses` (`:294-325`) matches no class for them.
- Nothing failed. Since then the server prints a warning and keeps starting: `DescribeUnknownIncludeTools`
  (~`:566-584`, closed in `CLOSED.md`).

The server itself has no `all` default. With neither `--mode` nor `--include-tools` it exits with code 2
(`HandleNoActiveTools`, `ServerStartupHelpers.cs:598-630`). The widening came from the script.

### The gap

- **Three inputs, one of them a different unit.** The surface comes from three inputs combined as
  union-then-subtract: the modes, then `--include-tools`, then `--exclude-tools`. A fourth input,
  `--hide-tools`, works on tool names instead and is applied separately, at registration.
- **The units are not the caller's.** Modes and include/exclude work on tool classes, but callers think
  in tool names.
- **Mistakes are silent or inconsistent.** A wrong name is a warning on server startup and a hard
  failure in `TestInstanceLauncher.ValidateToolSelection` (`RoslynSentinel.Engines.Basic/TestInstanceLauncher.cs:546-597`).
- **Combinations are not predictable.** Example: the owner's `~/.mcp.json` passes
  `"--mode=claude-lean, subagenteval"`. The claude-lean allow list filters every class, so SubAgentEval's
  tool is probably not served. This is a hypothesis: the live status in a claude-lean session shows 28
  of 123 tools active, which equals the claude-lean list.

### The same idea is encoded seven times

Each of these mechanisms separately says what "a lean set of tools" is:

| # | Surface chooser | Where | Unit |
| --- | --- | --- | --- |
| 1 | Modes | `ToolClassRegistry.ModeToToolClasses` (`RoslynSentinel.Server.Advanced/ToolClassRegistry.cs:100-136`), 25 entries, plus the `all` pseudo-mode and the `Toolset1` alias (`ServerStartupHelpers.cs:23-30`) | class |
| 2 | Include, exclude and hide flags | `ParseArgs` (`ServerStartupHelpers.cs:52-99`), `ParseHideTools` (`:113`) | class (include, exclude); tool (hide) |
| 3 | claude-lean allow list and on-demand classes | `ClaudeLeanToolNames` (`ToolClassRegistry.cs:49-58`), `ClaudeLeanOnDemandToolClasses` (`:82-89`), wired at `ServiceRegistrationExtensionsBasic.cs:278-286` | tool |
| 4 | Runtime toolsets | `enum ToolSetName` and `ToolsetCatalog.ToolsBySet` (`RoslynSentinel.Common/ToolsetCatalog.cs:32-72`), `ToolsetService` registered only under claude-lean (`ServiceRegistrationExtensionsBasic.cs:334-355`) | tool |
| 5 | Eval lean lists | `AgentToolAllowLists.Lean` (18 names) and `.PlanStepRunnerSurface` (`RoslynSentinel.AgentLoop/AgentToolAllowLists.cs:10-21`), applied client-side by `FilteringToolHost` | tool |
| 6 | ModelEval | `ChainEvalToolSurface.NewModes()` (Refactor, Workspace, WholeFileWrite) plus `NewHiddenTools`, which emulates the lean list with `--hide-tools` (`RoslynSentinel.Tests.ModelEval/ChainEvalToolSurface.cs:25-26,66-100`) | class plus tool |
| 7 | Per-launcher constants | `SubAgentChildServerLauncher` (`--mode=Claude-Lean --exclude-tools=SubAgentTools,SubAgentEvalTools --hide-tools=WriteFile`), `RunnerOptions.DefaultIncludeTools` (`RoslynSentinel.Utilities.PlanStepRunner/RunnerOptions.cs:72`) | mixed |

PlanStepRunner narrows twice: on the server with `--include-tools` (`Program.cs:205`), and on the client
with `FilteringToolHost(PlanStepRunnerSurface)` (`Program.cs:223`).

### What already exists to build on

Per-tool gating already works in any mode:

- `ToolAllowList` (`RoslynSentinel.Common/ToolAllowList.cs`) is read by
  `McpToolSchemaPatcher.WithSentinelTools<T>` (`McpToolSchemaPatcher.cs:131-159`), which skips every
  tool method that is not in the list.
- A filtered tool is never registered, so it is absent from `tools/list`, and `tools/call` fails as
  "Unknown tool" (`CallToolFailureHandler.cs:68`).
- `--hide-tools` already uses this outside claude-lean (`ClaudeLeanModeTests.HideTools_OutsideClaudeLean_RemovesOnlyTheNamedTool`).

Only the source of the names is hard-wired.

## Proposal

All points below were decided with the owner on 2026-11-22.

### One surface source per launch

A launch takes exactly one of three flags:

| Flag | Meaning |
| --- | --- |
| `--toolset=<name>` | A shipped toolset file, by name. Exactly one name. |
| `--toolset-file=<path>` | An ad-hoc toolset file with the full schema, for example under the scratch root. |
| `--tools=<a,b,...>` | An inline, unnamed toolset meaning "exactly these". It accepts tool names, and class names as shorthand. It has no `extends`, `exclude` or `onDemand`. |

- More than one of these fails startup.
- `--toolset=<name>` resolves only to shipped files. There is no search path.
- `--toolsets=<groups>` stays. It enables `onDemand` groups at startup, and now works with any toolset
  that defines groups, not only claude-lean.

### File schema (sketch)

```json
{
  "name": "planstep-runner",
  "description": "Lean eval surface plus GetDiagnostics.",
  "extends": ["eval-lean"],
  "classes": [],
  "tools": ["GetDiagnostics"],
  "exclude": [],
  "onDemand": {}
}
```

The fields:

- **`tools`:** the primary unit. A list of tool names.
- **`classes`:** shorthand that expands to every tool of each named class. `"*"` means every registry
  class that is not marked opt-in.
- **`extends`:** other toolsets, resolved depth first. A child inherits each parent's `tools` and
  `onDemand` groups. A cycle fails the load.
- **`exclude`:** removes a name from both the core tools and every `onDemand` group.
- **`onDemand`:** named groups of tool names that `McpToolsetControl` switches on and off at runtime.
  This replaces `enum ToolSetName` and `ToolsetCatalog`. A tool listed both in the core and in a group
  counts as core.

The built-in `all` is `{"name": "all", "classes": ["*"]}`. `ToolClassRegistry` gains a per-class opt-in
flag for the classes that `all` excludes today, `CodeTransformTools` and `EditSnippetsTools`
(`ExclusiveModes` and `OptInModes`, `ToolClassRegistry.cs:32-40`). Adding a tool therefore stays the
existing three-place change, with no fourth file to forget.

### Resolution

1. Load the source: a shipped file, a file from a path, or the inline `--tools` list.
2. Resolve `extends`. Expand `classes` to tool names by reflecting the `[McpServerTool]` methods of each
   class.
3. Union the core tools. Subtract `exclude` from the core and from every group.
4. Validate every name: tool, class, toolset and group. **An unknown name fails startup** with a
   closest-match hint, matching what `TestInstanceLauncher.ValidateToolSelection` already does.
5. Produce three outputs:
   - the core tool names, which feed `ToolAllowList`;
   - the active classes (every class owning a core or on-demand tool), which feed the existing
     `activeToolClasses.Contains(...)` DI blocks;
   - the `onDemand` groups, which go to `ToolsetService`. It is registered whenever groups exist, and
     on-demand classes are registered but held back by the allow list, as claude-lean does today.
6. `ActiveToolSurface` also records the toolset name and its source: shipped, file, inline or mode alias.

Where the parts live:

- **File parsing and the schema:** `RoslynSentinel.Common`.
- **Shipped files:** embedded resources under `RoslynSentinel.Common/ToolSets/`. Every consumer can read
  them, and the running binary always matches its commit.
- **Class expansion:** the server layer, because it needs the tools assemblies.

### Runtime groups and status

- **`McpToolsetControl`:** the `toolSet` parameter's enum is generated at startup from the active
  toolset's groups. Its description is generated too, so the test that pins the hand-written description
  against the catalog is replaced.
- **Persisted state:** persisted tab state (`toolsets.json`) stores group names. A name the active
  toolset does not define is ignored, with a warning.
- **`McpServerStatus`:** reports the active toolset name and its source. `enabledBy` hints
  (`DescribeHowToEnable`, `ServerStatusTools.cs:196-222`) name a toolset or `--tools` instead of a mode
  or `--include-tools`.

### Modes and the old flags

- **Which modes become files:** only modes with a live caller (scripts, launchers, tests, configs) get a
  shipped file. The survey is the plan's first step. Known so far: `all`, `claude-lean`, the ModelEval
  trio, `SubAgentEval` and `Experimental`. Single-class modes and the six sub-modes are reachable as
  `--tools=<ClassName>`.
- **`--mode`:** a deprecated alias. A name with a file resolves to it. Any other mode, or a comma list
  such as `claude-lean, subagenteval`, becomes an unnamed union built from the old `ModeToToolClasses`
  until the alias is removed.
- **`--include-tools`, `--exclude-tools`, `--hide-tools`:** deprecated, with a startup warning. They keep
  today's warn-only handling of unknown names until every caller has moved. Only the new flags fail on
  an unknown name.

### Eval and subagent surfaces

- **The server surface is the only gate.** Evals launch their server with an eval toolset, so
  `tools/list` is already the filtered list. `AgentToolAllowLists` is deleted and `FilteringToolHost`
  loses its list.
- **PlanStepRunner is the only production user of `FilteringToolHost`** (`Program.cs:223`; verified by
  search on 2026-11-22). The filter also guards against loops, and that guard is kept: repeated calls to
  unavailable tools end the run with `UnknownToolRequested`, and a listed call resets the count
  (`RoslynSentinel.Tests.ErrorVariants/RunnerToolHostTests.cs:156-224`). The guard moves so it also
  counts the server's "Unknown tool" errors.
- **Subagents already get a dedicated child server each**, through `SubAgentChildServerLauncher`, so the
  server toolset is the right gate. Changing a subagent's tools means editing one file.

### Pinning

Each shipped toolset's resolved tool names are snapshotted under `docs/generated/toolsets/<name>.txt` and
checked by `ArchitectureDocFreshnessTests`. Changing a surface therefore shows up as a snapshot diff that
someone updates deliberately. Before migrating, the current mode surfaces are snapshotted so the
migration can prove it changes nothing.

### Consumer migration

| Consumer | Today | After |
| --- | --- | --- |
| `scripts/Launch-RoslynSentinelHttpServer.ps1` | `-Mode` default `all` (`:60`); `-Tools` becomes `--include-tools` (`:90-91`); `-ExcludeTools` (`:62`) | `-ToolSet` (default `all`) or `-Tools`, which maps to `--tools` and suppresses the default; `-ExcludeTools` removed |
| `scripts/roslynsentinel-planstep.ps1` | `-IncludeTools` (`:172`, `:259`) | `-ToolSet planstep-runner` / `-Tools` |
| PlanStepRunner | `RunnerOptions.DefaultIncludeTools` (`:72`, `:110`), `Program.cs:205`, `:223` | `--toolset=planstep-runner`; no client list; loop guard kept |
| `AgentToolAllowLists` | `Lean`, `PlanStepRunnerSurface` | `eval-lean.json`, `planstep-runner.json` (extends `eval-lean`); class deleted |
| ModelEval `ChainEvalToolSurface` | `NewModes` plus `NewHiddenTools`; env `ROSLYNSENTINEL_MODELEVAL_TOOLSET` (`:29`) | in-process server gets a toolset; the env var names it |
| `SubAgentChildServerLauncher` | mode, exclude and hide flags | `--toolset=subagent-child` (extends `claude-lean`, excludes the subagent tools and `WriteFile`) |
| `TestInstanceLauncher`, `AdminTools.LaunchTestInstance`, `McpServerControl` | `--mode`, `--include-tools` (`includeTools` param, `AdminTools.cs:557`), `--toolsets` | `toolset` / `tools` params; validation moves to the shared resolver; `toolsets` and `keepToolsets` stay |
| claude-lean | `ClaudeLeanToolNames`, `ClaudeLeanOnDemandToolClasses`, `ToolsetCatalog` | `claude-lean.json` with `tools` and `onDemand` |
| `roslynsentinel-vscode-control.ps1:339`, `install.ps1:37` | `--mode=all` | `--toolset=all` |
| `Measure-ToolSchemaCost.ps1:28`, `Invoke-ToolComparison.ps1:29` | default mode `claude-lean` | default toolset `claude-lean` |
| `RoslynSentinel.Server.Advanced/Properties/launchSettings.json:31,36` | `--include-tools=RefactoringTools`, which is not a registry class but still triggers a dead DI block (`ServiceRegistrationExtensionsBasic.cs` ~395) | a real toolset; the dead block goes |
| `~/.mcp.json` (outside the repo, owner's config) | `"--mode=claude-lean, subagenteval"` | works through the alias; the owner moves it to a toolset |

### Sequencing (input for the plan, not the plan)

0. Finish the tab-scoped persistence plan (steps 9-10).
1. Snapshot the current surfaces of every mode that has a caller, and survey those callers.
2. Build the schema, loader and resolver. Add `--toolset`, `--toolset-file` and `--tools` beside the old
   flags.
3. Ship the built-in files, route `--mode` through the resolver, and prove zero snapshot diff.
4. Generalise `onDemand`: retire `ToolSetName` and `ToolsetCatalog`, generate the `McpToolsetControl`
   enum.
5. Migrate consumers, one per slice.
6. Deprecate the old flags, then remove them and `ModeToToolClasses`.

## Alternatives considered, not pursued

- **Files list classes only.** Rejected: that cannot express "only ReadFile", so claude-lean would stay
  a special case.
- **Files list tool names only.** Rejected: large sets such as `all` would become long lists that go
  stale when a tool is added.
- **Toolsets beside modes, both kept.** Rejected: that leaves two systems and keeps the composition
  problem alive.
- **Composition on the command line (several names, or include and exclude flags).** Rejected: the point
  is that one file states the surface. Composition lives inside files, through `extends` and `exclude`.
- **An explicit class list in `all.json`.** Rejected: a new tool class would silently miss `all` unless a
  test caught it.
- **All 25 modes migrated one to one.** Rejected: unused sub-modes would linger. The class shorthand
  covers them.
- **Keeping `FilteringToolHost` reading the same file.** Rejected: AgentLoop references only Common, so
  it cannot expand class shorthand. It is also a second gate that only works in clients we write.
- **Only one ad-hoc route (`--toolset-file` or `--tools`).** Rejected: the owner wanted both. `--tools`
  serves the one-line script case, and a file serves anything that needs `extends`, `exclude` or groups.
- **A search path for named toolsets (scratch root, instance layout).** Rejected: `--toolset-file` covers
  it without making name resolution depend on the environment.

## Open questions

- **Per-session surfaces on a shared server.** Agents with different surfaces sharing one server process
  (an external client such as an Ornith child on a shared HTTP server) can only be narrowed by the
  server, per MCP session. The tab-scoped toolset machinery is the precedent. Deferred, not designed.
- **Details of the `onDemand` schema.** Group descriptions for the generated `McpToolsetControl`
  description, naming rules, and whether startup-enabled groups belong in the file (an `enabledAtStart`
  field) or stay on `--toolsets`. Settle after the persistence plan closes.
- **Which modes have live callers.** The partial list above is from the 2026-11-22 trace, and the
  complete survey is plan step 1. Not checked: `ModelEvalMcpEnvironment.cs`,
  `OrderPricingRefactorChainAgentTests.cs`, the consumer of `ROSLYNSENTINEL_LLM_MINIMAL_TOOLS`
  (`scripts/roslynsentinel-modeleval.ps1:180`), and the default mode in `roslynsentinel-mcp-launch.ps1`.
- **Assembly loading.** The resolver must reflect over every tools assembly. `--hide-tools` discovery
  uses `AppDomain.GetAssemblies()` at registration time and may miss an assembly that has not loaded yet
  (hypothesis, untested).
- **Stateless HTTP transport.** How it interacts with `onDemand` groups was not traced.
- **The `subagenteval` hypothesis.** Whether `"--mode=claude-lean, subagenteval"` serves SubAgentEval's
  tool today is not verified. If it does not, the owner's config already relies on a surface it does not
  get.
- **Exit codes.** A distinct exit code for toolset validation failure, or the existing code 2.
- **When the deprecated flags and the `--mode` alias are removed.**

## Cost / risk

**What it touches:**

- **Server startup and registration:** `ServerStartupHelpers` (parsing and resolution), `ToolClassRegistry`
  (modes, the claude-lean lists, the new opt-in flag), `ServiceRegistrationExtensions{Basic,Advanced}`
  (the allow-list source, `ToolsetService` registration) and `McpToolSchemaPatcher`. The allow list must
  still be in the service collection before the first `WithSentinelTools` call.
- **Runtime toolsets and status:** `ToolsetCatalog`, `ToolsetService`, `ToolSetName`,
  `ToolsetControlTools`, `TabToolsetStateStore` and `ServerStatusTools`.
- **Launchers:** `TestInstanceLauncher`, `AdminTools`, `SubAgentChildServerLauncher`.
- **Eval and agent code:** PlanStepRunner, AgentLoop, ModelEval.
- **Scripts:** about six.
- **Tests:** at least `ClaudeLeanModeTests`, `McpToolsetControlTests`, `NoActiveToolsStartupTests`,
  `ToolClassNameResolutionTests`, `ExperimentalModeTests`, `ArchitectureDocFreshnessTests`,
  `SubAgentRegistrationTests`, `SubAgentLaunchTests`, `DependencyInjectionTests`,
  `TestInstanceLauncherTests`, `McpServerStatusToolListingTests`, `FilteringToolHostTests`,
  `PlanStepRunnerSurfaceTests` and `RunnerToolHostTests`.

**Risks:**

- **Silent surface regressions during migration.** Mitigated by snapshotting the current surfaces
  before step 2 and requiring zero diff in step 3.
- **The `McpToolsetControl` schema becomes dynamic.** Its enum and description change per toolset, which
  shifts schema token cost and what weak models see. Measure with `Measure-ToolSchemaCost.ps1` before
  and after.
- **Failing startup on unknown names is stricter than today's warning.** Configs with typos stop
  starting, which is intended. Hence the old flags keep warning until they are removed.
- **More can be changed without touching code.** Editing a JSON file changes a shipped surface, which is
  why the snapshot test is part of the design, not an option.

## Related

- [plan_tab_scoped_toolset_persistence.md](../plans/closed/plan_tab_scoped_toolset_persistence.md): prerequisite.
- [reference_changing_claude_lean_tools.md](../references/reference_changing_claude_lean_tools.md): replaced by editing `claude-lean.json` once built.
- [plan_modeleval_lean_tool_surface.md](../plans/closed/plan_modeleval_lean_tool_surface.md) and
  [proposal_modeleval_lean_tool_surface.md](proposal_modeleval_lean_tool_surface.md): origin of the eval lean list.
- [plan_stateless_http_toolset_enablement.md](../plans/plan_stateless_http_toolset_enablement.md)
- [blocking_error_gitremote_not_callable_after_toolset_enable.md](../blockers/blocking_error_gitremote_not_callable_after_toolset_enable.md)
- Memory: Tab-scoped toolset persistence run; Idle restart drops toolsets.
