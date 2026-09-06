---
name: project_di_engine_audit_open_question
description: Open question in DI split plan — per-method engine usage may not match planned class groupings
metadata: 
  node_type: memory
  type: project
  originSessionId: d912bb80-d14d-41b4-b453-e7a77fd07e20
  modified: 2026-09-06T02:56:35.714Z
---

Added to [[project_di_tool_split_plan_2026_09_05]] as Decision 1-Amendment-2 (open, unresolved).
First-pass grep of planned `WorkspaceProjectManagementTools` (7 methods, 6 ctor deps) showed each
non-`IWorkspaceManager` engine is used by only 1-2 methods, never shared within the class —
`DependencyEngine` only by ListSolutionItems/ListWorkspaceSolutions, `StructuralRefinementEngine`
only by SafeDeleteUnusedSymbol, `SolutionManagementEngine` only by CreateProject/SplitProjectByFolder,
`ProjectConsistencyEngine` only by ListProjectFrameworkTargets.

**Why it matters:** the planned classes are grouped by naming-intuition, not by which engines their
methods actually share — same root problem the original god-class split was fixing, just smaller
scale. This will carry into the new `*Impl` classes ([[project_tools_impl_split_amendment]]) unless
addressed before the split lands.

**Not yet resolved:** needs the same per-method engine-call-site audit applied to all 8 planned
classes (not just WorkspaceProjectManagement), ideally via RoslynSentinel's own MCP tools
(QuerySymbolRelationships / GetCallGraph / FindReferences against each engine field) rather than
manual grep, before deciding whether narrow multi-engine constructors are acceptable (impl classes
aren't LLM-schema-visible so it's a testability-only concern) or groupings should be reshuffled by
engine-sharing instead. Do as its own step, before or alongside the *Impl extraction.

**How to apply:** before finalizing Decision 2's constructor table, run the engine-usage audit via
MCP tools per class. Don't let classes get split twice.
