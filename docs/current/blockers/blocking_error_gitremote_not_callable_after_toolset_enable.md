# `GitRemote` not callable after `McpToolsetControl(toolSet: "git", enabled: true)` reported success

**Status:** OPEN 2026-11-06 (client-side, workaround known 2026-11-07). Not traced to source; the client-side cause is a hypothesis, and the only server-side fact confirmed is where the "can still be called by name" text comes from. Update 2026-11-07: after the user ran `/mcp` reconnect, `McpToolsetControl(toolSet: modelEval, enabled: true)` made `SubAgent` loadable by `ToolSearch` and callable in the same session, while the same enable before the reconnect had not. This fits the Claude Code reports that the deferred-tool index is built at connect and not refreshed on `tools/list_changed` (anthropics/claude-code reports 66084, 60626, 50515, 13646 on claudeissues.com). Workaround: `/mcp` reconnect, then enable the toolset.

## What was being attempted

A `modeleval-runner` subagent session, ~18:30 local on 2026-11-06, server build at HEAD `853e545f`. It
needed `GitRemote` `worktree add` to create the BEFORE-arm worktree for step 6 of
`plan_claude_lean_core_write_tools`. `GitRemote` is gated off, so the session called:

```
McpToolsetControl(toolSet: "git", enabled: true, reason: "need GitRemote worktree add for the BEFORE arm")
```

then tried to use the tool:

```
mcp__root_roslyn_sentinel_advanced_stdio__GitRemote(operation: worktree, action: add,
  worktreePath: C:\tmp\rs_before_563f5ead, branchName: <branch>, createBranch: true,
  ref: 563f5ead, reason: <text>)
```

## The exact symptom

1. `McpToolsetControl` returned `isError: false`, `changed: true`, `addedTools: ["GitRemote"]`, an
   `activeTools` list that included `"GitRemote"`, and this statusMessage text:

```
If your client has not refreshed its tool list yet, the tools can still be called by name.
```

2. Client tool discovery did not see the tool:

```
ToolSearch(query: "select:mcp__root_roslyn_sentinel_advanced_stdio__GitRemote")
-> No matching deferred tools found
ToolSearch(query: "GitRemote")
-> (only Git and McpToolsetControl returned)
```

3. Calling it by name:

```
Error: No such tool available: mcp__root_roslyn_sentinel_advanced_stdio__GitRemote
```

## Source trace

Not traced end to end. The client half (Claude Code harness) is outside this repo.

- The "can still be called by name" sentence is built at `RoslynSentinel.Tools.Basic/ToolsetControlTools.cs:79`
  (`summary += " If your client has not refreshed its tool list yet, the tools can still be called by name.";`,
  in `McpToolsetControlTool`). It is appended unconditionally to the summary; nothing in that path checks
  whether the client can actually reach an unlisted tool.
- Related, same mechanism, earlier occurrence on another toolset: see
  `blocking_error_toolset_enable_not_visible_in_client_tool_list.md` (2026-10-25, `moveExtract`; later marked
  NOT REPRODUCED 2026-11-05 for `declarations`). It records the server-side source facts
  (`ToolsetService` mutates the SDK `ToolCollection`; the SDK emits `notifications/tools/list_changed`;
  `McpToolsetControlTests` prove the in-process path). Those were not re-verified for this report.

Ruled out: nothing yet. No server log was inspected, and the main session was not asked whether it sees
`GitRemote`.

## What is and is not confirmed

Confirmed (observed in this session):
- The server reported the toolset flip as successful and listed `GitRemote` as active.
- The client's `ToolSearch` could not find `GitRemote`, and a by-name call was rejected with
  "No such tool available" (the rejection text is the client harness's, not the server's).
- The "can still be called by name" claim is false for this client session.

Unknown:
- Whether this is specific to subagent sessions (including `modeleval-runner`), or also happens in the main session after the same call.
- Whether the main session sees `GitRemote` after the same `McpToolsetControl` call.
- Whether the server emitted `notifications/tools/list_changed` for this flip, and which protocol revision the client negotiated.
- Whether a subagent started after the toolset was enabled (by the dispatcher) would see the tool.

## Why this matters

The environment advertises a path it cannot deliver for this client: the enable call succeeds, the response
promises by-name callability, and the next call fails with a client error that points back at nothing
actionable. A supervisor following the documented gated-tools workflow ends with an "enabled" toolset it
cannot use. CLAUDE.md forbids routing around a failed tool, and shell `git worktree add` is not blocked by
the `enforce-dogfood` hook but is not an approved substitute, so the BEFORE arm of step 6 of
`plan_claude_lean_core_write_tools` is paused. This is the second recorded occurrence of the same symptom
(different toolset, different session type), which suggests it is not a one-off.

## Suggested direction (not implemented)

Hypotheses to test, in order:
1. Confirm from the server log whether `tools/list_changed` was sent for the `git` flip and what protocol
   revision was negotiated.
2. Run the same `McpToolsetControl(toolSet: "git", enabled: true)` from the main session and check whether
   `ToolSearch` finds `GitRemote`, to separate subagent-specific from general behaviour.
3. Make the `McpToolsetControl` status message stop claiming by-name callability unless it is verified for
   the connected client (`ToolsetControlTools.cs:79`), and say what works instead.
4. If subagents only see the tool list from their spawn time, document that the dispatcher must enable the
   `git` toolset before spawning the subagent (or the user must start the session with `GitRemote` active).
