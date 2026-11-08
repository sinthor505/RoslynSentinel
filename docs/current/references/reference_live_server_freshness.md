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
- A restart picks up new commits **only if they build**: `StopServer`, then the launcher builds the new SHA
  once for all windows. If that build fails it falls back to the newest complete build (an OLDER server) and
  records `instances\<id>\launch-outcome.json`. `McpServerStatus.lastLaunch` shows it (`buildFailed`,
  `attemptedSha`, `servedSha`, `firstErrors`, `hint`, and `pullOk`/`pullError` - `pullOk: false` means the launcher's
  `git pull` of `<root>\source` failed and the server may be OLDER than the dev repo even though `buildFailed` is false;
  a missing `pullOk` is unknown; the launcher self-heals a stale `.git\index.lock` + half-applied files and retries once) and the first response after start carries a one-shot
  `launchWarning`. Full detail: `instances\<id>\launch.log`. `StopServer` also warns when server source is
  uncommitted, since a restart builds committed code only.

## Build reuse, background prebuild and idle restart (prod layout)

Built by `docs/current/plans/plan_prebuild_and_idle_stale_server_restart.md`.

- **Reuse.** When HEAD has no build, the launcher serves the newest complete build whose commit is an ancestor of
  HEAD and whose diff to HEAD touches no server source (`scripts/server-source-paths.txt`, kept identical to
  `ServerBinaryStaleness.SourcePathPrefixes` by a test; includes `Directory.Build.props` and `global.json`).
  `launch.log` says `Reuse path: ...`. A docs- or scripts-only commit therefore costs no build.
- **`builds\latest.json`** (`headSha`, `buildSha8`, `selection` fast|reuse|built|fallback, `buildFailed`, `utc`)
  records every launch's or prebuild's choice.
- **Prebuild.** `scripts/Install-GitHooks.ps1 -InstancesRoot <root>` (once per clone; `-Uninstall` reverses it)
  sets `core.hooksPath .githooks` and `roslynsentinel.instancesRoot`. The `post-commit`/`post-merge`/`post-rewrite`
  hooks start `scripts/Start-ServerPrebuild.ps1`, which returns at once and runs the launcher's `-BuildOnly` mode
  in a detached, single-flight worker (`builds\prebuild.lock`; a commit during a run sets `builds\prebuild.pending`
  and gets one more run). It strips the `GIT_*` variables git exports to hooks. Log: `builds\prebuild.log`.
  Only commits on `master` (git config `roslynsentinel.prebuildBranch`) trigger it.
- **Idle restart.** A stdio server started with `--builds-root` (the prod launcher passes it) exits on its own when
  it has been idle for `--idle-restart-minutes` / `ROSLYNSENTINEL_IDLE_RESTART_MINUTES` (default 15; `<= 0`
  disables), no tool call is in flight, it is not halted by ExternalFileDrift, no operation ledger is open, no
  build/test holds the gate, and `latest.json` names a different, non-failed build for the dev repo's current HEAD.
  The next tool call respawns it on the fast path. A restart loses in-memory state (undo history, toolsets enabled
  with `McpToolsetControl`); `isFreshStartup: true` shows it happened. `McpServerStatus.idleRestart` shows whether
  it is enabled and `lastUnmetGuard`, the check currently keeping the server up (`IdleRestartPolicy.Guard*`).

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
