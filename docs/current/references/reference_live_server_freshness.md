# Live MCP server freshness

**Status:** CURRENT 2026-10-17. Detail moved out of CLAUDE.md "Verification"; the rule stays there.

Rebuilding RoslynSentinel does not change the running server: VS Code builds and spawns a server only
when a session starts. After changing server source, the running server is **stale** until stopped and
respawned, so restart it before verifying fixes live.

## Restarting

- Server instances are isolated per session, so you may restart your own at any time:
  `McpServerControl(operation: StopServer, confirmServerStop: ConfirmServerStop)`. Omitting the confirm
  param returns a refusal and stops nothing. Allow a few seconds; a clean `Connection closed` is
  normal. VS Code relaunches it on a fresh build; reconnect and re-run `LoadSolution`.
- Before restarting, make sure no subagent is mid-operation against the same server.
- A **new** `[McpServerToolType]` class additionally needs a fresh chat session: the session's tool
  list is fixed at start and does not refresh after a reconnect.

## Detecting staleness

- Any response carries `"isServerBinaryStale": true` while a newer build of a loaded
  `RoslynSentinel.*.dll` exists under a project's `bin/<Config>/` (omitted when current; can lag a few
  seconds). `Build(level: fullBuild)` writes those DLLs and so sets the flag; `Build(quickBuild)` is
  in-memory and does not.
- `McpServerStatus` gives `binaryStaleness.staleAssemblies`, `serverBinaryPath`,
  `serverBuildTimeUtc` (DLL mtime on disk) and `serverPid`. If you edited server source but have not
  run a `fullBuild`, assume the live server is old.
- If `Build` reports a suspicious warning/error count, force a full rebuild; incremental builds can
  skip recompiles and under-report.

## LoadSolution does not rebind the executing binary

`LoadSolution` changes which files are analyzed, not which binary executes tool logic. Pointing the
server at another worktree's `.slnx` loads that worktree's files, but tool calls still run the
already-running process's code, with no error. Before trusting a live result as evidence about a
specific worktree, confirm the server was built from it (`serverBinaryPath` and `serverBuildTimeUtc`
vs. `git log`). If not, prefer `dotnet test` against that worktree over live MCP calls.
