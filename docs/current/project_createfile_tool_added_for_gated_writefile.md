---
name: project_createfile_tool_added_for_gated_writefile
description: "WriteFile gated off for agent tasks; CreateFile added as scoped stub-then-populate replacement, incl. staticClass typeKind"
metadata: 
  node_type: memory
  type: project
  originSessionId: ed9672c6-7cb1-472d-afcf-6da9436d8838
  modified: 2026-09-09T16:13:04.052Z
---

`WriteFile` (whole-file write, `operation: CreateFile|ReplaceFile`, free-form `content` param) is
gated off / not exposed to the agent for these tasks — disabled due to a high rate of model
misuse/failure in testing.

`CreateFile` (`RoslynSentinel.Server.Basic/SentinelWorkspaceTools.cs:689`) is a separate, newer
MCP tool added to fill the resulting gap: create a new file with a skeleton, then populate it with
already-scoped tools. It takes **no content parameter** — for a `.cs` file it requires
`namespaceName`, `typeKind`, `typeName` and always stubs `namespace {ns};\n\npublic {kind}
{typeName}\n{\n}\n`. It fails if the file already exists.

`typeKind` (`NewTypeKind` enum, `RoslynSentinel.Common/ToolEnums.cs:70`) values: `class`, `record`,
`interface`, `enum`, `struct`, and **`staticClass`** (added 2026-09-09 specifically to avoid a
`ModifyModifier` follow-up call for static utility classes — `public static class {typeName}`
directly).

**How to apply:** any plan/step doc telling an agent to "create a new file" with real content must
decompose it as: `CreateFile` (stub) → `Member(add, containerName: "...")` once per
method/property/field (or `Member(add, containerName: null, ...)` for a second top-level type in
the same file) → `UsingDirective(add)` for imports the stub doesn't carry. See
[[feedback_dont_name_gated_tools_in_agent_docs]] for how to phrase this in agent-facing docs
without naming the gated tool.
