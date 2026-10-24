# Fold RoslynSentinel.Server.Basic into RoslynSentinel.Server.Advanced

**Status:** APPROVED 2026-10-24. Direction decided by the user; implementation in a fresh session (user moves the files by hand, an agent fixes up namespaces, duplicates, scripts and docs).

## Motivation

The Basic/Advanced split was made to keep any one project small. It achieved that in `Engines.*` and
`Tools.*`, which stay split. At the server level the split produces a near-duplicate project that
nobody launches and that is already drifting. Facts (read 2026-10-24 from source, `.csproj` and scripts):

- **Server.Advanced is a shim over Server.Basic.** 4 `.cs` files, about 600 lines: `Program.cs` (25,
  identical to Basic's), `ServerStdio.cs` (183 vs 171), `ServerHttp.cs` (121 vs 112),
  `ServiceRegistrationExtensionsAdvanced.cs` (279). The stdio and http hosts are copies of Basic's with
  three deltas: `LlmOptions.Configure(args)` (`ServerStdio.cs:44`), `mcpBuilder.WithTasks(...)`
  (`ServerStdio.cs:119-121`, `ServerHttp.cs:82-84`), and the Advanced registration calls.
  `WarmupAndAutoLoadAdvanced` (`ServiceRegistrationExtensionsAdvanced.cs:249-278`) is a byte-for-byte
  copy of `WarmupAndAutoLoadBasic` (`ServiceRegistrationExtensionsBasic.cs:408-429`). `ConsoleMode`,
  the 11 request filters, `ToolArgumentValidator`, `ServerStartupHelpers` and `ToolClassRegistry` live in
  Basic and are used by both.
- **No runtime flavor distinction.** No flavor enum, option or branch exists in `Server.*`, `Common`,
  `Tools.*` or `Engines.*`. `SentinelHostOptions` (`Common/SentinelHostOptions.cs:38-47`) carries
  `OperatingMode`, `Transport`, `Respawn` only. Assembly-name checks are prefix matches on
  `"RoslynSentinel.Server."` (`Common/ServerBinaryStaleness.cs:75,326`, `Server.Basic/ConsoleMode.cs:66`).
- **Basic is never launched.** Every live path hardcodes Advanced: `C:\Users\Administrator\.mcp.json`
  -> `scripts/roslynsentinel-mcp-launch.ps1:183,445-446,601`, `Launch-RoslynSentinelHttpServer.ps1:77-78`,
  `roslynsentinel-vscode-control.ps1:108,201`, `install.ps1:14,21`, `roslynsentinel-planstep.ps1:11`,
  `Utilities.PlanStepRunner/Program.cs:180-196`, `Tools.Advanced/SubAgentChildServerLauncher.cs:105-121`,
  and `Engines.Basic/TestInstanceLauncher.cs:24` (`ServerProjectName = "RoslynSentinel.Server.Advanced"`).
  `scripts/build.ps1 -Flavor Basic` (`:56,78-83,233`) only builds the Basic csproj and runs
  `Tests.Battery.Basic`. `Server.Basic/Properties/launchSettings.json` has 5 debug profiles with stale
  argument names (`--modes=All`, `--llm-base-url`, which Basic ignores). No test project references
  `Server.Basic` without also referencing `Server.Advanced`.
- **The duplication is drifting.** Two mode maps in `Server.Basic/ToolClassRegistry.cs`:
  `AdvancedModeToToolClasses` (`:134`) omits `SymbolRelationshipTools` from `Workspace` where
  `BasicModeToToolClasses` (`:90`) has it (`:149` vs `:105`); Basic-only modes (`WorkspaceFileIO`,
  `WorkspaceReadNav`, ...) are absent from the Advanced map. `CodeGenerationEngine` is registered twice
  in `AddRoslynSentinelEnginesAdvanced` (`:45-46`). `docs/known-failing-tests.Basic.txt` (23 lines,
  2026-08-23) and `.Advanced.txt` (12 lines, 2026-09-05) contradict the current 0-failure baseline.
- **What Basic buys:** a binary with 5 fewer DLLs (22.1 MB vs 24.3 MB). Not shipped, not used.

## Proposal

One server project, `RoslynSentinel.Server.Advanced`, keeping its name, folder, assembly name and exe
so that no launcher, script, `.mcp.json` entry, memory or external reference changes. "Advanced" becomes
a plain name; the Basic/Advanced distinction remains real one layer down, in `Engines.*` and `Tools.*`.

### Decided

1. **Files.** The 11 `Server.Basic/*.cs` files move into `Server.Advanced/`. The four duplicate pairs
   are merged by keeping Basic's body and folding in Advanced's deltas:
   - `Program.cs`: keep Basic's (it has `isFreshStartup`, read by `ToolCallEcho.cs:110,123`).
   - `ServerStdio.cs`, `ServerHttp.cs`: Basic's body plus `LlmOptions.Configure(args)`,
     `mcpBuilder.WithTasks(new InMemoryMcpTaskStore(), o => o.ExecutionModeSelector =
     TaskEnabledToolsHelper.SelectExecutionMode)`, the Advanced registration calls, and Advanced's
     smoke-resolve type list (4 types). MCP Tasks therefore become available to Basic tools too.
   - `ServiceRegistrationExtensionsBasic.cs` + `ServiceRegistrationExtensionsAdvanced.cs`: both stay as
     files for now (the registration-move proposal below empties them); delete
     `WarmupAndAutoLoadAdvanced`, point callers at `WarmupAndAutoLoadBasic`; dedupe the
     `CodeGenerationEngine` registration.
2. **Namespace.** `RoslynSentinel.Server.Basic` -> `RoslynSentinel.Server.Advanced` for the moved types.
   `using RoslynSentinel.Server.Basic;` lines in `Tests.Server`, `Tests.Advanced`, `Tests.ModelEval`,
   `Tests.Integration`, `Tests.SubAgent`, `Tests.Asyncify` are removed or merged. Class and method
   names keep their `Basic`/`Advanced` suffixes (`AddRoslynSentinelEnginesBasic`,
   `RoslynSentinelServiceExtensionsBasic`, `BasicModeToToolClasses`): there they name an engine or tool
   set, not a server.
3. **Mode map.** `BasicModeToToolClasses` and `AdvancedModeToToolClasses` merge into one
   `ModeToToolClasses`. The union is taken: Basic-only modes survive, `Workspace` regains
   `SymbolRelationshipTools`. `ClaudeLeanToolClasses` / `AdvancedClaudeLeanToolClasses` merge the same
   way (the Advanced list is the Basic list plus `SubAgentTools`). `BuildClassToModes` reads one map.
   `ServerStartupHelpers.ResolveActiveToolClasses` is unchanged.
4. **Project removal.** `RoslynSentinel.Server.Basic.csproj` and its folder are deleted; the entry
   leaves `RoslynSentinel.slnx`; the `<ProjectReference>` to it leaves `Server.Advanced.csproj`,
   `Tests.Server.csproj` and every other test csproj that lists it. `Server.Advanced.csproj` gains
   Basic's package references that it does not already have (`ModelContextProtocol.AspNetCore`,
   `Serilog`, `Serilog.AspNetCore`) and a direct `<ProjectReference>` to `Engines.Basic` if any moved
   file needs it directly.
5. **Scripts and baselines.** `scripts/build.ps1`: drop `-Flavor Basic`; keep `Advanced` and `Solution`
   (the `Tests.Battery.Basic` run moves under `Solution`, or stays reachable through a test filter).
   Delete `docs/known-failing-tests.Basic.txt` and `docs/known-build-warnings.Basic.txt`; refresh the
   Advanced and Solution baselines by running `build.ps1`. `scripts/Measure-ToolSchemaCost.ps1`: drop the
   `-Flavor` switch. Delete `Server.Basic/Properties/launchSettings.json`; Advanced's profiles are kept.
6. **Tests.** `Tests.Server/ClaudeLeanModeTests.cs`: `ListToolNamesAsync` (`:64-111`) loses its
   flavor branch; `ClaudeLean_ExposesExactlyTheCoreToolsForItsFlavor(bool advanced)` becomes a single
   case; `Claude_BasicServer_IsTheAdvancedBaselineMinusAdvancedOnlyClasses` (`:136-148`) is deleted or
   rewritten as a mode-set assertion ("mode X exposes exactly these names"). `Tests.Advanced/
   DependencyInjectionTests.cs:45-48` reads the single map. `Tests.Server/ArchitectureDocGenerator.cs:253`
   reads the single map.
7. **Docs.** CLAUDE.md Architecture bullets that name `Server.Basic/ToolClassRegistry.cs` and
   `Server.Basic/ServiceRegistrationExtensionsBasic.cs` (and the `Tools.Experimental is hosted by
   Server.Advanced only` aside) are updated; `docs/current/references/reference_architecture_map.md`
   and `reference_changing_claude_lean_tools.md` likewise; `docs/generated/` regenerated with
   `scripts/Generate-ArchitectureMap.ps1`. Memory `project_server_flavors_and_build_configs` gets a
   superseding note.

### Implementation approach (agreed with the user)

The user moves the 11 files and deletes the Basic project folder by hand, outside the MCP tools, then a
fresh session does the fix-ups with targeted tools (`RenameSymbol` on the namespace, `ReplaceSnippet`
for the host deltas and map merge, `Member(operation: remove)` for the duplicates), builds,
and runs `Tests.Server` + `Tests.Advanced`. Reasons: cross-project `MoveType` has an open defect
(memory `project_engine_reorg_final_closeout_bugs_1_2_3`, bug 2), and a folder move is one operation
by hand versus 11 tool calls.

Order of slices for the fresh session:
1. Hand move + csproj/slnx edits + first `Build(fullBuild)`; fix compile errors only (namespaces, usings).
2. Merge the duplicate hosts (`ServerStdio`, `ServerHttp`, `Program`); delete `WarmupAndAutoLoadAdvanced`.
3. Merge the mode maps; fix the tests in item 6; run `Tests.Server`, `Tests.Advanced`, then `Tests.Battery.Basic`.
4. Scripts, baselines, launchSettings, docs, memory; regenerate `docs/generated/`; commit.
5. Restart the server (`McpServerControl(StopServer)`, `LoadSolution`) and verify `McpServerStatus` lists the merged modes.

## Alternatives considered, not pursued

- **Keep Server.Basic, delete Server.Advanced.** Basic holds all the code, so the merge is smaller, but
  every launcher, `TestInstanceLauncher`, `SubAgentChildServerLauncher`, PlanStepRunner, `.mcp.json`,
  memories and docs name `RoslynSentinel.Server.Advanced`. Renaming the exe breaks live sessions'
  launchers until restart and costs a sweep for no functional gain.
- **New neutral project `RoslynSentinel.Server`, delete both.** Cleanest name, same external sweep as
  above plus a slnx/folder churn. Can be done later as a pure rename if the name ever matters.
- **Keep two projects, delete only the duplicated code.** Advanced would shrink to the `WithTasks`
  line and a project reference, which is not worth a project, a build flavor, two baselines and two
  mode maps.
- **Leave as is.** The drift is already visible (mode-map divergence, double registration, stale
  baselines) and the upcoming registration-move work would otherwise be done twice.

## Open questions

- Whether any `Server.Basic` type is referenced by name from a non-test project other than
  `Server.Advanced` (expected: none; `Tools.*` and `Engines.*` do not reference `Server.*`).
- Whether `Tests.Battery.Basic` should stay a separate `build.ps1` target or fold into `Solution`.
- Whether to keep the `Server.Advanced/Properties/launchSettings.json` profiles or rewrite them with
  current argument names while touching the file.
- Whether the merged `Workspace` mode should include `SymbolRelationshipTools` (the Basic map has it,
  the Advanced map does not); the proposal says yes, as the omission looks like drift, but it is a
  claude-lean-adjacent surface change to confirm with `McpServerStatus` after restart.

## Cost / risk

- Touches: 15 `.cs` files in two server projects, 1 slnx, 2-8 csproj, `build.ps1`,
  `Measure-ToolSchemaCost.ps1`, 2 baseline files, 2 launchSettings, CLAUDE.md, 2 reference docs,
  generated docs, 1 memory, roughly 6 test files for `using` lines plus `ClaudeLeanModeTests`.
- A hand move of tracked `.cs` files trips `ExternalFileDrift` and halts mutating tools. Do the move
  before the fresh session's server starts, or run `ExternalFileDrift(operation: List)` then
  `Acknowledge` first; report it as a successful bypass per CLAUDE.md.
- The server binary that the fresh session runs is built from the tree being changed. Use
  `Build(fullBuild)` + restart between slices 1 and 2, and prefer `McpServerControl(LaunchTestInstance)`
  for verifying the merged modes before the final restart.
- Behaviour change for the former Basic surface: MCP Tasks on, `LlmOptions.Configure` runs (it only
  parses args), merged mode contents. No change for anything launched today, which was already Advanced.
- Concurrent sessions sharing the worktree would see the deleted project; do this with no other
  session active.

## Related

- `docs/current/plans/plan_test_construction_factories_pilot.md` (re-sequenced after this fold:
  registration-move-down, then test DI, then construction-site migration).
- Follow-ups discussed 2026-10-24, to be written as their own proposals after this one lands: move
  `AddRoslynSentinelEngines*` into `Engines.*` and a flat `AddRoslynSentinelToolDependencies*` into
  `Tools.*`; a per-tool `ToolSelector` + `AddRoslynSentinelTools(toolNames, assemblies)` in `Common`
  replacing the per-class DI blocks, `ToolAllowList` and `ClaudeLeanOnDemandToolClasses`.
- `docs/current/references/reference_architecture_map.md` (tool registration: three places).
- Memories: `project_server_flavors_and_build_configs`, `project_per_session_mcp_server_isolation`,
  `project_engine_reorg_final_closeout_bugs_1_2_3` (bug 2, cross-project MoveType).
