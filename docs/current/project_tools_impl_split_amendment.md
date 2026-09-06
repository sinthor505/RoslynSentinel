---
name: project_tools_impl_split_amendment
description: Plan amendment splitting MCP-attribute surface from implementation into *Tools/*Impl class pairs
metadata: 
  node_type: memory
  type: project
  originSessionId: d912bb80-d14d-41b4-b453-e7a77fd07e20
  modified: 2026-09-06T02:56:24.668Z
---

Added to [[project_di_tool_split_plan_2026_09_05]] as Decision 1-Amendment: every new `*Tools` class
from the Workspace/Refactoring split becomes a pair — `*Tools` (MCP surface, holds all
`[McpServerTool]`/`[Description]`/`[Consumes]`/`[Produces]` attributes, 1-line delegating methods)
and `*Impl` (plain DI-constructed class, today's method bodies moved verbatim, still returns
`ToolResult<object>`/`ResultError` — not yet MCP-agnostic in return type).

**Why:** user wants MCP surface fully separated from implementation for flexibility and cleaner
layering; doing it now piggybacks on the split plan's already-planned single touch of all 39 method
bodies, instead of a second pass later that re-touches everything again.

**Scope decision (explicit):** mechanical shim only for this pass — `*Impl` keeps `ToolResult`
return types verbatim. A full domain-type boundary (impl returns plain types/throws, wrapper builds
ToolResult) is deferred, to be done per-tool opportunistically later, NOT as part of this plan.

**How to apply:** when implementing the split plan, step 2/3 create `*Tools`+`*Impl` file pairs, not
single files. Don't attempt the domain-type boundary rewrite unless separately requested.
