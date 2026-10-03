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

## Triage 2026-10-03

**Server status:** Git tool now works (PID 38660, build time 2026-10-03T20:22:10.168543Z, `Git(operation: status)` succeeded). No current issue to debug.

**Checked:** The blocker occurred on 2026-10-01, one day after commit `1ac34688` (2026-10-01T00:08:47) which added `--untracked-files=all` to `StatusAsync`. Examined:

- `RunGitAsync` (GitImpl.cs:408-493): process spawning, UTF-8 encoding setup, stdin/stdout/stderr handling, event handlers for data-received callbacks. No unhandled exceptions in the method body; exception from `process.WaitForExitAsync()` is caught.
- `OutputDataReceived` / `ErrorDataReceived` handlers (GitImpl.cs:455-456): simple lambdas calling `StringBuilder.AppendLine()`. These run on background threads managed by Process; `AppendLine()` is unlikely to throw.
- `StatusAsync` exception handling (GitImpl.cs:713-823): wrapped in try-catch; exceptions are logged and returned as error results, not propagated.
- Process data-received handlers: no mechanism by which `stdout`/`stderr` could be disposed while handlers are still running (handlers complete before `WaitForExitAsync()` returns).
- Git tool wrapper exception handling (GitTools.cs:155-175): each operation returns a structured `GitResult` or `SentinelCallToolResult<object>` with error data; no exceptions escape to the caller.

**Logs from 2026-10-01:** Not found in bin-vscode/*/logs/. Pre-existing logs were likely cleaned up.

**Conclusion:** The CONNECTION_CLOSED was a transient server failure, likely recoverable by restart (per the blocker's own suggestion). The current code has no obvious crash vector in the git tool path. Cannot establish a concrete cause without either reproducing the failure or examining server logs from the time of the incident. Marking as non-reproducible; leaving open pending further evidence.
