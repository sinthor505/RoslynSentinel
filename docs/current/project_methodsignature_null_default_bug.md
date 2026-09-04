---
name: project-methodsignature-null-default-bug
description: "MethodSignature(add) CS1737 CLOSED: root cause is Claude Code's own MCP client serializing the string \"null\" as JSON null before it reaches any MCP server — matches upstream anthropics/claude-code#81911. Not a RoslynSentinel bug; no fix needed here."
metadata: 
  node_type: memory
  type: project
  originSessionId: b36b7e80-314b-4a64-a7a9-10327c472699
  modified: 2026-09-04T09:05:41.147Z
---

[[plan-reason-parameter-rollout-v1]] Task 3 blocked on `MethodSignature(add)` against
`GitTools.Git` with CS1737 ("optional parameters must appear after all required parameters").
**CLOSED 2026-09-04 as an upstream Claude Code defect, not a RoslynSentinel bug.**

**Investigation arc:** two isolated unit tests (calling `AddMethodParameterAsync`/`MethodSignature`
in-process) produced correct output and couldn't reproduce the bug — only real MCP tool calls
failed. A live VS debugger attached to the MCP stdio server ([[feedback_attach_debugger_when_mcp_tools_cant_show_internal_state]])
showed `defaultValue` arrives as C# `null` (not the string `"null"`) before RoslynSentinel's own
code runs, for a call made with `defaultValue: "null"`. Generalization testing showed this isn't
`defaultValue`-specific: any nullable-string parameter on any tool (confirmed also on
`contextSnippet`) collapses the same way. Live JSON-schema inspection showed both affected params
have identical `{"type":["string","null"]}` schema shape — ruling out any RoslynSentinel-side
attribute/schema explanation.

**Decisive cross-client test:** the same exact call, made via MCP Inspector (an independent MCP
client) against the same stdio server binary, **succeeded** — `string? reasonTest4 = null` was
generated correctly, no CS1737. This proved the defect is specific to whichever MCP client was
serving the failing calls (Claude Code's own tool-calling layer), not RoslynSentinel's server,
schema, or the `ModelContextProtocol`/`Microsoft.Extensions.AI` SDK.

**Matches known upstream bug:** [anthropics/claude-code#81911](https://github.com/anthropics/claude-code/issues/81911)
("MCP Tool Null Parameter Serialization Bug") documents the same string/null conflation from the
opposite direction — passing JSON `null` gets sent as the string `"null"`. Their own root-cause
notes confirm raw JSON-RPC works correctly, matching the Inspector cross-check here. Related but
less precise matches: [#82652](https://github.com/anthropics/claude-code/issues/82652) (empty-schema
params stringified), [#90123](https://github.com/anthropics/claude-code/issues/90123) (`anyOf`
schemas flattened to `{}`), [#56263](https://github.com/anthropics/claude-code/issues/56263)
(`anyOf: [X, null]` stripped client-side in Claude Desktop).

**Disposition:** no RoslynSentinel code fix implemented or needed — the defect is entirely in
Claude Code's MCP argument serialization. Workaround for any caller (human or agent) hitting this:
never pass the literal string `"null"` as an MCP tool argument value; use `"default"`, omit the
argument, or (if this recurs enough to justify it) a dedicated non-string escape-hatch parameter
(e.g. a `nullDefault: bool`) — proposed but not implemented, since the upstream fix is the correct
long-term resolution. Full investigation trail archived at
`docs/obsolete/blockers/blocking_error_methodsignature_add_rejects_required_trailing_cancellationtoken.md`.

**Notable self-correction during investigation:** partway through, a stretch of `MethodSignature`/
`GetMethodSource`/`ReadFile` calls failed with `"missing a value for the required parameter
'filepath'"` and was briefly misattributed to a new "server-wide outage" theorized to be caused by
the user experimenting with an unrelated `RequestContext<CallToolRequestParams>` debug parameter in
`GitTools.cs`. This was wrong — it was a plain casing mistake (calling with `filePath` when the
actual parameter is lowercase `filepath`); the `requestParams` line was never actually uncommented
in the running server. Caught and corrected in the same session once questioned. See
[[feedback_verify_before_theorizing_on_tool_errors]] for the general lesson.
