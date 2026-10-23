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
- `Build` defaults to `level: fullBuildThrowaway`, which builds into
  `.roslynsentinel/build/<instance>/` (one folder per server instance, git-ignored) and leaves the
  projects' own `bin/`/`obj/` untouched, so concurrent sessions do not block each other. It does not set
  the stale flag; use `fullBuild` only when you need the shared `bin/` updated. `RunTest` always builds
  into the same instance folder. Within one server, Build/RunTest calls queue behind one gate.
- `McpServerStatus` gives `binaryStaleness.staleAssemblies`, `serverBinaryPath`,
  `serverBuildTimeUtc` (DLL mtime on disk) and `serverPid`. If you edited server source but have not
  run a `fullBuild`, assume the live server is old.
- If `Build` reports a suspicious warning/error count, force a full rebuild; incremental builds can
  skip recompiles and under-report.

## Prod instances layout and git-based staleness (opt-in)

Set `ROSLYNSENTINEL_INSTANCES_ROOT` (or flip `$UseDefaultInstancesRootWhenUnset` in
`scripts/roslynsentinel-instances-common.ps1`) and the launcher uses
`%USERPROFILE%\RoslynSentinelInstances` instead of `bin-vscode\`:

- `source\` is a prod clone of this repo (`git pull --ff-only` on every launch, branch `master`), so only
  **committed** code reaches a window's server.
- `builds\<sha>\Advanced\` is an immutable build keyed by commit (a `complete` marker file; built in a
  throwaway `git worktree`, published by rename, shared read-only by every window on that commit, last good
  build used if a build fails).
- `instances\<id>\` is the server's `--working-dir`: `logs\` (file names carry the pid), `crash.log`,
  `tool_list*.json`, `stopped-by-script.marker`. Swept when its process is gone. Without `--working-dir` the
  server writes beside its binary as before.
- The build is stamped with `-p:SourceRevisionId=<sha>`; `McpServerStatus` shows `buildSha` and `workingDir`.
  `isServerBinaryStale` then means the loaded solution's repo has commits after that SHA touching
  `RoslynSentinel.Common/`, `Engines.*`, `Tools.*` or `Server.*`; they are listed in
  `binaryStaleness.staleCommits`. A binary without a SHA (a plain local build) falls back to the `bin/**` scan.
- A restart picks up new commits: `StopServer`, then the launcher builds the new SHA once for all windows.

## Test instances for uncommitted server changes

`McpServerControl(operation: LaunchTestInstance)` runs the throwaway build output on a free loopback port
(`--port=0 --bind=loopback --respawn=none`) and returns the URL plus paste-ready PowerShell requests.
Your session keeps its own stdio server (a session cannot add or reconnect an MCP server); drive the test
instance with raw JSON-RPC. The stateless Streamable HTTP endpoint needs `Content-Type: application/json`
and `Accept: application/json, text/event-stream` (both, else HTTP 406) and always answers as SSE (`data:`
line). `ListTestInstances` / `StopTestInstance` manage them; the parent's shutdown kills them.

## LoadSolution does not rebind the executing binary

`LoadSolution` changes which files are analyzed, not which binary executes tool logic. Pointing the
server at another worktree's `.slnx` loads that worktree's files, but tool calls still run the
already-running process's code, with no error. Before trusting a live result as evidence about a
specific worktree, confirm the server was built from it (`serverBinaryPath` and `serverBuildTimeUtc`
vs. `git log`). If not, prefer `dotnet test` against that worktree over live MCP calls.
