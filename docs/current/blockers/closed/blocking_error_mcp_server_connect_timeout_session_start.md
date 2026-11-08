# `root_roslyn_sentinel_advanced_stdio` MCP server failed to connect at session start (CONNECT_TIMEOUT)

**Status:** RESOLVED 2026-10-04. The server connected on retry after the user restarted it; the cause was never traced (no code change, no commit).

## What was being attempted
Session task: add a `useScratchDir: true` option to the `Build` and `RunTest` tools so each call builds into a unique scratch
directory and parallel sessions stop blocking each other (default stays the main repo output). The task needs C# reads and
edits of `BuildEngine`, `TestRunEngine` and their tool classes, which CLAUDE.md requires to go through the RoslynSentinel MCP tools.

## The exact symptom
```
root_roslyn_sentinel_advanced_stdio (CONNECT_TIMEOUT): "MCP server root_roslyn_sentinel_advanced_stdio connection timed out after 30000ms"
```
A `ToolSearch` for the RoslynSentinel tools (query "roslyn_sentinel Build RunTest") returned no `mcp__root_roslyn_sentinel_advanced_stdio__*` tools.

## Source trace
Not traced. Ruled out: nothing yet. Not checked: `roslynsentinel-mcp-launch.ps1` output, whether another VS Code window's server holds
the per-window build folder (keyed by `%VSCODE_PID%`), or whether a build in progress delayed the 30s startup handshake.

## What is and is not confirmed
- Confirmed: the server was reported failed at session start and no RoslynSentinel MCP tools are available in this session.
- Suspected (unverified): launch script spends >30s building a fresh server, or a stale/locked bin folder from a parallel session
  blocks the build step - which is the same class of problem the requested feature targets.

## Why this matters
Dog-fooding policy forbids routing around the tools with `Read`/`Edit` on `.cs`, so all C# work in the session is blocked.
The timeout message gives no hint whether the launch script, the build, or the server process was the slow part.

## Suggested direction (not implemented)
Check the launch script's log for this window, restart the MCP server from VS Code, and start a new session (new tool classes need
a fresh session anyway). Consider having the launch script log elapsed time per phase so a CONNECT_TIMEOUT is attributable.
