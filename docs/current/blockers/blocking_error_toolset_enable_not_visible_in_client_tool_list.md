# `McpToolsetControl` enabled a toolset on the server but the Claude Code client never loaded the new tools

**Status:** OPEN 2026-10-25 (worked around the same day: commit `73d89af1` registers SemanticFindReplace in `--mode all`; a `LaunchTestInstance(mode: all)` HTTP instance emits and runs it, which is how step 6 was applied). The claude-lean client-refresh question below is still untraced. Server side confirmed working; the client-side cause (notification not sent, not honoured, or not delivered) is NOT traced, only hypotheses below.

## What was being attempted

Step 6 of `docs/current/plans/plan_semantic_find_replace_remove_null_forgiving.md`: run the new
`SemanticFindReplace` operation `removeNullForgiving` (committed 1934c0f3) to strip 235 `UpdatedText!`
occurrences in test files. The tool lives in toolset `moveExtract`, which is gated off in mode
`claude-lean,subagenttools`, so the session called:

```
McpToolsetControl(toolSet: moveExtract, enabled: true)
```

Environment: one Claude Code VS Code session, 2026-10-25, server flavor Advanced, mode
`claude-lean,subagenttools`, server build 1934c0f3.

## The exact symptom

Server side, the call succeeded: `changed: true`, `addedTools` includes SemanticFindReplace, MoveMember,
ApplyDiff and others; the response's `activeTools` list contains them; `McpServerStatus` lists
`SemanticFindReplaceTools` in `activeToolClasses`. The response text says tools/list_changed refreshes the
client's tool list and "If your client has not refreshed its tool list yet, the tools can still be called
by name."

Client side, the tools never appeared. In the main session, both of these returned:

```
ToolSearch select:mcp__root_roslyn_sentinel_advanced_stdio__SemanticFindReplace
ToolSearch select:mcp__root_roslyn_sentinel_advanced_stdio__MoveMember
-> No matching deferred tools found
```

This was repeated after the enable, after a disable+enable toggle, and across several turns. A subagent
started afterwards reported that calling the tool by name gives:

```
No such tool available
```

The tool's schema is therefore never loaded in the client, so the tool cannot be invoked from the main
session or from any subagent, although the server exposes it. The "can still be called by name" advice in
the server response is not usable in practice: the Claude Code harness refuses a name it has not listed.

## Source trace

Not traced end to end. The client half is outside this repo. What the server source shows:

- `RoslynSentinel.Common/ToolsetService.cs:45-46` (class doc): the service only mutates the SDK's
  `ToolCollection` inside one `DeferChangedEvents` scope; the SDK raises `notifications/tools/list_changed`
  itself "on a stateful transport (McpServerImpl subscribes ToolCollection.Changed)". This service does
  not send the notification directly, so a failure to emit would be in the SDK path, not in a call here.
- `RoslynSentinel.Tools.Basic/ToolsetControlTools.cs:9,24`: the tool description promises that
  "tools/list_changed refreshes your tool list".
- `RoslynSentinel.Tests.Server/McpToolsetControlTests.cs` (in-process host with the SDK `McpClient`, stdio
  pipe transport):
  - `Lean_AdvertisesListChanged_AndEnableThenDisableEachSendOneNotification` (~line 423/433) shows the
    notification does reach an SDK client on protocol revision `2025-11-25` (`LiveServer.LegacyProtocol`).
  - `Lean_ClientOn20260728_GetsListChangedOnlyOverASubscription` (line 442) shows a client negotiating
    `2026-07-28` (the SDK client default) gets NO broadcast unless it opened a `subscriptions/listen`
    stream; the server deliberately stays silent otherwise. The test's comment says a re-list still shows
    the new tools.
  - `EnabledTool_IsCallable_WithoutRelisting` (~line 481) shows the server accepts a call by name without a
    re-list. This is server-side only; it does not cover the Claude Code harness gate.

So the in-process tests prove the server can notify and can serve an unlisted-but-enabled tool. They do not
prove what Claude Code negotiated or did in the failing session.

## What is and is not confirmed

Confirmed (observed this session):
- The server enabled the toolset and reports the tools active (`changed: true`, `activeTools`,
  `activeToolClasses`).
- Claude Code's `ToolSearch` could not find the tools, across enable, disable+enable and several turns.
- A subagent started after the enable got "No such tool available" when calling by name.
- No files were changed by the attempt; work is paused after step 5 of the plan.

Not verified:
- Whether a new chat session or a VS Code MCP reconnect (`/mcp`) picks up the enabled toolset.
- Whether the server actually emitted `notifications/tools/list_changed` in the live session (the
  description says it does; only the in-process tests show it can).
- Which protocol revision Claude Code negotiated with the live server; if it was `2026-07-28` and Claude
  Code does not open `subscriptions/listen`, the server correctly sends nothing (hypothesis).
- Whether Claude Code honours `tools/list_changed` at all for deferred tools (it may re-read the list only
  on reconnect) (hypothesis).
- Whether a stdio transport flush problem swallowed the notification (hypothesis; no evidence).

## Why this matters

The environment advertised a path it cannot deliver on this client. `McpToolsetControl` returns success and
tells the caller to expect a refresh or to call by name, but neither works in Claude Code, and nothing in
the response says so or offers a recovery. A turn-one agent (or a weak model) following the documented
workflow ends up with a toolset that is "enabled" yet unreachable, with no actionable error. The new
`removeNullForgiving` operation, and any other tool in an on-demand toolset, cannot be used from such a
session or from subagents spawned in it.

## Suggested direction (not implemented)

Questions to answer first (hypotheses to test, in order):
1. Log, on the live server, the negotiated protocol version at initialize and whether
   `tools/list_changed` was written to stdout after `McpToolsetControl`. If the version is `2026-07-28`
   with no `subscriptions/listen`, the server stays silent by design and the fix is on the
   description/response side (and perhaps a client-capability-aware hint).
2. Check whether Claude Code re-reads the tool list only on reconnect, e.g. try a fresh chat session and
   `/mcp` reconnect after the enable.
3. If the client cannot hot-load tools, make `McpToolsetControl`'s response state that honestly for
   clients known not to refresh (and drop "can still be called by name" where it is false), and name the
   working route.

Workaround needing a user configuration decision (not a blocker for the tool surface itself): start the
server with a mode or `includeTools` that has `SemanticFindReplace` (and the rest of `moveExtract`) active
from session start, so the client lists it in the initial `tools/list`. Per CLAUDE.md "Gated tools", a mode
or flag change is the user's call.
