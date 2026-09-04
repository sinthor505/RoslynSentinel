---
name: feedback-attach-debugger-when-mcp-tools-cant-show-internal-state
description: When MCP tools alone can't reveal what's actually happening inside a live tool call (e.g. an argument's real runtime value, or generated text a tool doesn't surface), ask the user to attach VS's debugger to the MCP stdio server process rather than continuing to guess from black-box symptoms
metadata:
  node_type: memory
  type: feedback
  originSessionId: b36b7e80-314b-4a64-a7a9-10327c472699
  modified: 2026-09-04T07:49:36.600Z
---

While root-causing [[project_methodsignature_null_default_bug]], I spent a long stretch trying to
infer what `AddMethodParameterAsync` actually generated purely through MCP tool black-box testing
(isolated unit tests, `ApplyDiff(action:"validate")` on hand-typed candidate text, diagnostic-blob
column-counting). Two independent isolated tests gave **false negatives** — they showed correct
behavior because they didn't exercise the same code path as a real MCP call (they called the engine
in-process, bypassing the actual MCP argument-binding/transport layer entirely). This nearly led to
a wrong conclusion (that the engine's text-generation logic itself was fine, full stop).

The user asked whether attaching the VS debugger with break-on-exception would help; break-on-
exception wouldn't have (the failure path is a normal diagnostic return, not a thrown exception), but
a **regular breakpoint** at the exact point of interest was decisive: one breakpoint in
`ValidationEngine.ValidateChangesAsync` at the `WithDocumentText` call revealed the real generated
text (proving the engine's output really was broken in the live path, contradicting the isolated
tests), and a second breakpoint at the very top of the `[McpServerTool]` method body proved the
argument was already corrupted (string `"null"` had become C# `null`) before RoslynSentinel's own
code ran at all — conclusively placing the bug outside this repo, in the MCP SDK/transport layer.

**Why this matters:** MCP tools like `RunTest`/`ApplyDiff(validate)`/`autoStage:false` are good for
testing behavior but have real, confirmed gaps in surfacing *why* something happens (e.g.
`autoStage:false`'s `ToJsonSummary()` never includes `UpdatedText` — see the blocker doc). When
black-box MCP evidence starts contradicting itself (isolated test says X is fine, live tool call
says X is broken), that's the signal to stop guessing and ask the user to attach a debugger to the
live server process (the stdio copy actually serving the session, not the HTTP sibling — confirm
which one first) rather than continuing to construct more indirect black-box probes.

**How to apply:** when stuck on a live-vs-isolated discrepancy in RoslynSentinel's own behavior,
proactively offer/ask for a debugger attach + specific breakpoint location + specific variable to
watch, rather than continuing indefinitely down the MCP-tools-only path once it's demonstrably not
converging. Note `RunTest` runs `dotnet test` as a **separate process** — a breakpoint in the
debugged MCP server won't hit during a `RunTest` call; only direct MCP tool calls (e.g.
`MethodSignature`, `ApplyDiff`) execute inside the attached process.
