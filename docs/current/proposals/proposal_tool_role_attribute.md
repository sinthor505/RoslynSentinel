# Tool role attribute: derive "which tool can I name in advice" from the tool classes

**Status:** PROPOSED 2026-11-05. Design agreed in discussion, nothing built; attribute shape and the `ToolsetCatalog` question are open.

## Motivation

Advice and error texts name specific tools unconditionally, while the tool surface is gated (claude-lean
allow-list, modes, on-demand toolsets). A model told to call a tool it cannot see is stuck, which is an
environment defect. Evidence: a model-eval (ReadFileOnly, 2026-11-05) was told to "call ListSolutionItems",
which was not exposed in that run.

Unconditional tool-name sites (verified 2026-11-05):

- `RoslynSentinel.Tools.Basic/WorkspaceFileEditImpl.cs:190` (`BuildFileNotFoundError`, names `ListSolutionItems`)
- `RoslynSentinel.Tools.Basic/WorkspaceReadNavigationImpl.cs:93` (second `BuildFileNotFoundError`, same text)
- `RoslynSentinel.Common/DocumentLookup.cs:123` (`DocumentLookup.Describe`)
- `RoslynSentinel.Common/OrientationCircuitBreaker.cs:70` (`StateMessage`, names `ListAll` and `ListSolutionItems`)
- `RoslynSentinel.Server.Advanced/ToolArgumentValidator.cs:472-473` (`ParameterHints`, `filePath` hint text)
- `AgentSystemPrompts` (not re-verified for this doc; check when slice 4 starts)

Today's mitigations are hand-kept parallel tables that can drift from the code:

- `Common/WriteToolAdviceHelper.cs:82` `ToolToDeclaringClass` and `IsExposed(tool)` (line ~124: class active AND allow-listed; unknown names are not vouched for).
- `Common/ToolsetCatalog.cs:32` `ToolsBySet`, a second hand-written tool grouping (the file header lists the manual steps to add a toolset).
- `Common/ActiveToolSurface.cs` (singleton): `ActiveToolClasses` and `IsToolAllowed` (line 46).

The immediate fix (`ActiveToolSurface.IsCallable` plus an optional `Func<string,bool> isCallable` parameter on the
not-found builders and `OrientationCircuitBreaker`) is being done separately and is NOT part of this proposal.
This proposal removes the hand-kept tables behind it.

## Proposal

Put the data on the tools so it cannot drift from the code.

- **A bare category is not enough.** `[ToolCategory("Read")]` tells you a tool reads, but advice needs a specific
  call it can print, e.g. `ListSolutionItems(kind: all)`, with its arguments. So the primary attribute is a
  capability role: `[ToolRole(ToolRole.EnumerateFiles)]` on `ListSolutionItems` and `ListAll`;
  `WholeFileWrite` on `WriteFile`/`ApplyDiff`/`ApplyUnifiedDiff`/`DeleteFile`; `StructuredEdit` on
  `Member`/`RenameSymbol`/... (role list is illustrative, not final).
- **Advice asks "which callable tool has role X".** A per-role hint template renders the call with its arguments
  (reusing `ToolCallHint.Call`). If no callable tool has the role, the advice sentence is omitted, not printed.
- **Optional coarser category** (Read, Refactoring, ...) can ride on the same attribute and give a shared
  `ActiveToolSurface.GetActiveToolsInCategory(...)`.
- **Read by reflection once** when `ActiveToolSurface` is built. `IsCallable` must combine attribute data with the
  live class/allow-list/toolset state (see open questions).
- **Unattributed tools are not vouched for** (same as `IsExposed` for unknown names today), so rollout is incremental.
- **Ratchet test** (after rollout): every registered tool must carry the attribute.

### Slices (migration-first)

1. Attribute + reflection lookup on `ActiveToolSurface` + tests.
2. Attribute the tools the advice helpers already name.
3. Migrate `WriteToolAdviceHelper`'s table to the lookup; keep the old table until tests pass, mark it `[Obsolete("Use ActiveToolSurface role lookup")]`.
4. Move the unconditional hints listed above to role-based hints.
5. Ratchet test: a registered tool without the attribute fails.
6. Decide `ToolsetCatalog.ToolsBySet` (see below).

## Alternatives considered, not pursued

- **Keep extending hand-written tables** (`ToolToDeclaringClass`, `ToolsBySet`): works, but each new tool needs
  edits in 2-3 places and a missed one silently yields "not vouched for". This is the drift the proposal removes.
- **Category-only attribute:** cannot yield a printable call with arguments; advice would still need a hard-coded name.
- **Only the `isCallable` filter (the immediate fix):** stops wrong advice but still needs a name table to say what
  to call instead. Complementary, not a competitor.

## Open questions

- Does the attribute replace `ToolsetCatalog.ToolsBySet` (lean yes, with a consistency test during transition) or sit beside it?
- Attribute name; enum vs string for the role.
- Granularity: roles only, or roles plus categories. How many roles before it becomes a second tool catalog?
- Interaction with on-demand toolsets enabled at runtime (`McpToolsetControl`): `IsCallable` must read live state, not a startup snapshot.

## Cost / risk

- Touches every `[McpServerTool]` class eventually (attribute per tool); mechanical, suited to `implementer-sweep`.
- New attribute type must live in `Common` (Tools.* siblings cannot see each other; `Common` is visible to all).
- Startup reflection over tool classes: small cost; must tolerate classes not registered in the active flavor.
- Role vocabulary is a new thing to maintain; a wrong role is worse than none (wrong advice again), hence the ratchet is on presence only and roles need review.

## Related

- `docs/current/proposals/proposal_reduce_tool_schema_token_cost.md` (origin of the toolset gating this has to respect).
- `docs/current/proposals/proposal_sequence_pager.md` (unrelated trigger; its "Later" list has an `IPageableResult` marker for a ratchet test, the same pattern).
