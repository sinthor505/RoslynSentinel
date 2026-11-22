# Idea: JSON toolset definitions (a `ToolSets/` folder) instead of mode + include + exclude

**Status:** NOT DESIGNED 2026-11-22. STUB from the todo review of the same date (item about `--include-tools`); problem, constraints and open questions only, no design decisions. Design in a fresh chat.

## What happened

Origin: todo review 2026-11-22. The owner stated that `--include-tools` is meant to ADD tool classes
to what `--mode` selects. The owner suggested redesigning how the active tool surface is populated,
for example a `ToolSets/` subdirectory of JSON files, each defining a named toolset, so a toolset can
be redefined by editing one file instead of composing `--mode` + `--include-tools` + `--exclude-tools`.

Triggering evidence: `Launch-RoslynSentinelHttpServer.ps1 -Tools "ReadFile,GetFileOutline,..."`
(`scripts/Launch-RoslynSentinelHttpServer.ps1:87` maps `-Tools` to `--include-tools=`) served all 114
tools (a 151 KB `tools/list`) and swamped a 70k-context Ornith child on host .111. Two causes, both
traced in source:

- The values were tool names (`ReadFile`), not tool-class names. `--include-tools` is parsed through
  `NormalizeToolClassNames(ParseNameList(...))` (`RoslynSentinel.Server.Advanced/ServerStartupHelpers.cs:98`);
  names that match no registered class are silently dropped.
- `ResolveActiveToolClasses` (`ServerStartupHelpers.cs:295`) expands the active modes, then unions in
  `includeTools`, then removes `excludeTools`. With no `--mode`, the default is `all`, so the include
  list added nothing to a surface that was already everything.

A separate small READY TODO adds an unknown-name warning and fixes the launch-script help meanwhile
(the code already carries `// TODO` at `ServerStartupHelpers.cs:97`). This idea is the larger
redesign, not that fix.

## The actual gap

The active tool surface is assembled from three independent inputs (mode, include, exclude) with
union-then-subtract semantics, and from a unit (tool class) that is not the unit a caller thinks in
(tool name). A weak caller, or a script author, cannot state "exactly these tools" in one place, and
a wrong name fails silently into the widest surface. There is also no single place that defines a
named set: the same idea ("a lean set of tools") is separately encoded in several mechanisms below.

## Constraints and existing mechanisms the design must reconcile

Names below were verified to exist by search on 2026-11-22; behaviour beyond the cited line is not
re-traced here.

- Server modes plus `--include-tools` / `--exclude-tools` in `ServerStartupHelpers`
  (`ParseArgs`, `ResolveActiveToolClasses`, `NormalizeToolClassNames`). `ActiveToolSurface`
  (`RoslynSentinel.Common/ActiveToolSurface.cs`) snapshots what the three resolved to at startup.
- `ToolClassRegistry` (`RoslynSentinel.Server.Advanced/ToolClassRegistry.cs:9`), with the
  `KnownToolClassNames` lookup used for include/exclude normalisation. Per CLAUDE.md a tool is dead
  unless it is also registered in the DI blocks of `ServiceRegistrationExtensions{Basic,Advanced}.cs`.
- `McpToolsetControl` runtime toolsets: `ToolsetCatalog` and `ToolsetService` (`RoslynSentinel.Common`),
  switched on/off at runtime; the tab-scoped persistence work is tracked in
  [plan_tab_scoped_toolset_persistence.md](../plans/plan_tab_scoped_toolset_persistence.md). Startup
  `--toolsets` is honoured only in `claude-lean` (`ServerStartupHelpers.cs:203` warning).
- The `claude-lean` schema profile: an exclusive mode with a per-tool allow list
  (`ToolAllowList`, `ToolClassRegistry.ClaudeLeanToolNames`, `ActiveToolSurface.AllowedToolNames`),
  plus `--hide-tools` (`ParseHideTools`).
- The eval lean allow list `AgentToolAllowLists` (`RoslynSentinel.AgentLoop/AgentToolAllowLists.cs:7`)
  and `FilteringToolHost` in the same project (filters what the model sees and may call).
- `TestInstanceLauncher` (`RoslynSentinel.Engines.Basic`) passes `--include-tools` through to launched
  test instances and has its own `ValidateToolSelection` against known class names.
- ModelEval / PlanStepRunner per-test tool lists: not traced for this stub; locating them and how
  they choose tools is part of the design work (hypothesis: they use `AgentToolAllowLists` or the
  `FilteringToolHost` path, unverified).
- Granularity mismatch: a tool class carries several tools, so class-level selection cannot express
  "only ReadFile" and tool-level selection needs the allow-list machinery that today exists only in
  `claude-lean`.

## Possible fixes (not decided)

Only the owner's suggestion is recorded; it is a direction, not a design:

- A `ToolSets/` subdirectory of JSON files, each defining one named toolset, selected by name at
  launch. Trade-offs, alternatives and whether this replaces or sits beside the flags are open
  (see questions).

## Open questions

- Granularity: tool-class names, tool names, or both in a toolset file? Tool names need per-tool
  gating beyond `claude-lean`.
- Do JSON toolsets replace `--mode`, or do the existing modes become built-in toolset files?
- Composition: do toolsets support includes / extends of other toolsets, and exclusions?
- Relation to `McpToolsetControl` gating, and to the `claude-lean` and eval (`AgentToolAllowLists`)
  lists: is there one source of truth for all of them, or are the runtime on-demand sets a separate
  concept that only shares the file format?
- Startup validation: do unknown names fail the launch or warn? (The current silent drop is the
  defect that started this.)
- Where the folder lives (repo, scratch root, per-instance layout under `RoslynSentinelInstances`)
  and how tests pin the files so a toolset edit cannot silently change a shipped surface.
- Migration of the existing launch scripts and flags (`Launch-RoslynSentinelHttpServer.ps1 -Tools`,
  `TestInstanceLauncher` arguments, MCP client configs) and whether the flags stay as deprecated
  aliases.

## Related

- [plan_tab_scoped_toolset_persistence.md](../plans/plan_tab_scoped_toolset_persistence.md)
- `docs/current/TODO.md`: the READY item for the unknown-name warning and launch-script help fix
  (the interim change).
- Memory: Tab-scoped toolset persistence run; Idle restart drops toolsets.
