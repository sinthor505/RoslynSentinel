# `Git` (MCP) unreachable: root_roslyn_sentinel_advanced_stdio reports CONNECTION_CLOSED, session commit blocked

**Status:** OPEN 2026-10-01. Not traced to source; the server dropped mid-session (it was healthy earlier: `Git(operation: "log")` succeeded) and the cause is unknown.

## What was being attempted

Committing this session's files through the MCP `Git` tool (`stage` with `scope: listed`, then `commit`), per CLAUDE.md "Commits". Session files:

- `scripts/Get-JournalDigest.ps1` (new)
- `.claude/agents/journal-improvement-planner.md` (new)
- `.claude/skills/journal-improve/SKILL.md` (new)

## The exact symptom

Harness environment update at the start of the commit turn:

```
65 deferred tools are no longer available (MCP server disconnected): mcp__root_roslyn_sentinel_advanced_stdio__* (65).
root_roslyn_sentinel_advanced_stdio (CONNECTION_CLOSED): "Connection closed"
```

No `Git` tool call was attempted after the disconnect; the tool is not in the callable set.

## Source trace

Not traced. Ruled out / not checked:

- Not a missing tool: `Git` worked earlier this session (log returned 5 commits, server pid 37104).
- Not checked: whether the process exited, was restarted by another concurrent session, or hit a VS Code spawn problem (per memory, triage as a launch-script issue first: `roslynsentinel-mcp-launch.ps1`, `%VSCODE_PID%`-keyed per-window instance). Server logs under `bin-vscode\...\logs\` have not been read.

## What is and is not confirmed

- Confirmed: the MCP server is disconnected for this session; shell git add/commit is forbidden by CLAUDE.md and blocked by `enforce-dogfood.ps1`, so there is no sanctioned route to commit.
- Confirmed (relevant to the commit itself): `.gitignore` lines 50-53 exclude `.claude/*` except `settings.json` and `hooks/**`. The new agent and skill files are therefore untracked-and-ignored; a plain listed `stage` will refuse or skip them. Only `scripts/Get-JournalDigest.ps1` is trackable as-is.
- Suspected: transient disconnect, recoverable by reconnecting or `McpServerControl` restart from a fresh connection.

## Why this matters

A server drop between the last edit and the commit leaves finished work uncommitted with no in-band recovery for the agent. Separately, the `.claude/agents/` and `.claude/skills/` files this workflow relies on are not versioned, so they cannot be shared or restored from git.

## Suggested direction (not implemented)

1. Reconnect the MCP server (VS Code MCP restart, or `roslynsentinel-vscode-control.ps1 status/restart`), then re-run the commit.
2. Decide whether `.claude/agents/**` and `.claude/skills/**` should be tracked. If yes, add `!.claude/agents/`, `!.claude/agents/**`, `!.claude/skills/`, `!.claude/skills/**` to `.gitignore`; otherwise commit only `scripts/Get-JournalDigest.ps1`.
