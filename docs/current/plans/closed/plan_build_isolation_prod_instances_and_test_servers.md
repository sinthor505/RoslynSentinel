# Plan: isolate concurrent builds per server instance, move prod instances out of the repo with git-based staleness, and launch HTTP test instances from McpServerControl

**Status:** DONE 2026-10-23 (commits 3b073b4e, 60595073, 9844baf0, 4616131c, 9b22cf2a, ecdfb0fd, a71673ac, c8dacae8). All four parts landed in one session. Deviations: the Part 3.3 launcher is OPT-IN (set ROSLYNSENTINEL_INSTANCES_ROOT or flip $UseDefaultInstancesRootWhenUnset in scripts/roslynsentinel-instances-common.ps1) so live windows were not switched; WriteMethodInventory was deleted (dead output) instead of relocated; LaunchTestInstance refuses solutions without a RoslynSentinel.Server.Advanced project; the stateless HTTP endpoint needs both Accept values and always replies as SSE; the gate is tested directly (no fake process runner exists).

## Problem

Several Claude Code windows run their own RoslynSentinel server against the same checkout. Their
builds, test runs and launches all write the same `bin/` and `obj/` folders, so they block or fail
each other, and the only remedy today is an opt-in flag a model must already know about.

Evidence (verified 2026-10-23 against source and by running a build):

- `Build(level: fullBuild)` and `RunTest` default to the projects' own `bin/`/`obj/`. The only
  guard is a point-in-time probe that polls for a competing `dotnet`/`msbuild` process for up to
  30 s, then proceeds (`RoslynSentinel.Engines.Basic/BuildEngine.cs:201-233`,
  `RoslynSentinel.Tools.Basic/BuildIsolation.cs:15-16`); two servers can both see "clear" and
  start together, and the probe fails open if WMI throws (`BuildIsolation.cs:85-89`). A losing
  build only recognises MSB3027/MSB3021 and blames "a running process" (`BuildEngine.cs:292-296`).
- `useScratchDir: true` adds `-p:ArtifactsPath=<solutionDir>/.scratch/roslynsentinel-builds/<pid>/`
  (`BuildIsolation.cs:30-57`). A run on 2026-10-23 confirmed it redirects `bin/`, `obj/` and all 21
  `project.assets.json` files; no project `bin/obj` DLL changed. It is off by default and the
  schema text says `isServerBinaryStale` will not react, which is the reason a model avoids it.
- The scratch output is 685 MB per instance: `bin/` is 651 MB of which 54 MB is unique content
  (12 test projects at about 44 MB each; `Microsoft.CodeAnalysis.CSharp.dll` 15 copies / 101 MB;
  `Microsoft.CodeAnalysis.CSharp.resources.dll` 195 copies / 90 MB). No `.csproj`/`.props` sets
  any output or copy-local property; `Directory.Build.props` holds only `NuGetAudit=false`.
- `scripts/roslynsentinel-mcp-launch.ps1` builds on every launch, unconditionally (lines 33-36,
  301-308), into `bin-vscode\<repoHash>-<random>\Advanced` via `-o` (lines 177-179) but shares
  `obj/`; a per-instance `BaseIntermediateOutputPath` was tried and reverted for CS0579 (lines
  284-297). Concurrent launches have no lock. The sweep keeps a folder when a
  `RoslynSentinel.Server.Advanced.exe` runs from it (lines 219-241); its minimum age is 3600 s in
  code (line 208) but 15 s in the header comment (lines 29-30).
- `ServerBinaryStaleness` walks up from the running binary to the first `.slnx` and scans every
  `<child>/bin/**/RoslynSentinel.*.dll`, flagging an assembly stale when a candidate has the same
  name, a newer write time, a matching `Debug`/`Release` segment and a different MVID
  (`RoslynSentinel.Common/ServerBinaryStaleness.cs:45-58, 65-83, 116-150`). A scratch build is
  invisible to it; any session's normal build flags every other session, even with no source change
  surviving the MVID check only because builds are deterministic.
- Right after the scratch build above finished, 79 files in the projects' own `obj/` and `bin/`
  (`*.AssemblyInfo.cs`, `*.AssemblyReference.cache`, coverage mappings) were rewritten at
  06:29:41-06:29:48 UTC, after `dotnet build` had exited at 06:29:37: the workspace reload
  (`workspaceVersion` 131 -> 132) ran MSBuildWorkspace design-time builds. Those also write the
  shared `obj/`, from every server, on every reload. No Build-side lock covers them.
- The server writes beside its binary on every startup: `tool_list_<mode>.json` and
  `tool_list_simple_<mode>.json` (`RoslynSentinel.Server.Basic/ConsoleMode.cs:567-631`), Serilog
  `server-<yyyyMMdd-HHmmss>.log` under `<binDir>\logs` (`ServerStartupHelpers.cs:372-408`,
  `--log-dir` override at `:414`), `crash.log` (`:469-492`), and it reads and deletes
  `<binDir>\..\stopped-by-script.marker` (`:510-529`). `WriteMethodInventory` writes
  `all_methods_<mode>.csv` / `engine_methods_<mode>.json` into the **repo root**, found by walking
  up to `Directory.Build.props` (`ConsoleMode.cs:642-728`). Same-mode instances overwrite each other.
- Testing an uncommitted server change live means finding and reading
  `scripts/Launch-RoslynSentinelHttpServer.ps1` (builds into `bin-vscode\http-<port>-<ts>`, fixed
  port 5100, no stop, PID printed only; lines 54, 95, 104-108, 148). Sessions regularly reach the
  wrong script. The HTTP host binds all interfaces with no auth (`RoslynSentinel.Server.Advanced/ServerHttp.cs:71`)
  and has no `--port=0` (`ServerStartupHelpers.cs:210-214`). `McpServerControl` offers only
  `GetServerStatus` and `StopServer` (`RoslynSentinel.Tools.Basic/AdminTools.cs:144-208`), and the
  `StopServer` success text assumes a VS Code relaunch.
- Claude Code cannot add or reconnect an MCP server on the model's request; only the user can
  (`/mcp` > Reconnect). A session therefore keeps its stdio instance and drives any test instance
  out of band (raw JSON-RPC, which models handle well). Documented at
  https://code.claude.com/docs/en/mcp.md.

Related: `docs/current/blockers/resolved/blocking_error_loadsolution_missing_msbuild_workspaces_assembly.md`
(the recorded launch-vs-launch `obj/` race), `plans/plan_read_block_http_launcher_retention_fileinfo.md`
(added the HTTP launcher script and the control script's shared `bin-vscode\Advanced.Http` copy),
`plans/plan_stateless_http_toolset_enablement.md` (toolset enablement over stateless HTTP, relevant to
test instances), memory `project_per_session_mcp_server_isolation.md` (says instances are keyed by
`VSCODE_PID`; they are keyed by a random token, lines 141-177 of the launch script).

## Decision

Isolate by **output**, not by source. Sessions deliberately share one dirty working tree; what must
differ per session is where a build writes and where a server writes.

1. **Dev repo (Part 1):** `Build` defaults to a new `fullBuildThrowaway` level and `RunTest` is
   always throwaway, both via `-p:ArtifactsPath=.roslynsentinel/build/<instanceId>/`, one stable
   dir per server instance so restore and copy-local stay warm. `fullBuild` keeps its bin-writing
   meaning. One server-wide semaphore serialises Build/RunTest within a server.
2. **Output size (Part 2):** `Directory.Build.props` gets `SatelliteResourceLanguages=en` and the
   MSBuild hard-link copy properties, so each instance dir costs disk close to its 54 MB of unique
   content and copies are fast.
3. **Prod instances (Part 3):** servers launched by VS Code run from
   `%USERPROFILE%\RoslynSentinelInstances\` (override `ROSLYNSENTINEL_INSTANCES_ROOT`): one prod
   clone tracking `master` with the dev repo as origin, immutable `builds/<sha>/` outputs shared
   read-only by every window on that commit, and `instances/<id>/` passed to the server as
   `--working-dir` for all per-process output. The launcher pulls, reuses `builds/<sha>` when
   complete, otherwise builds it in a throwaway `git worktree` of the prod clone and publishes it by
   rename (first wins), with a last-good fallback. No copy per instance, no lock, no debounce.
   Staleness becomes git-based: commits after the build SHA that touch server code, named in the
   response.
4. **Test instances (Part 4):** `McpServerControl` gains `LaunchTestInstance`,
   `StopTestInstance` and `ListTestInstances`. Launch runs the throwaway build output on a free
   loopback port and returns the URL plus paste-ready `tools/list` / `tools/call` requests. The
   session keeps its stdio instance; the test instance is driven with raw JSON-RPC.

Rejected alternatives, and why:

- *Flip `useScratchDir` default to true, nothing else.* Stops most Build collisions but blinds
  `isServerBinaryStale` and keeps the "agent must know the flag" shape for the bin-writing case.
- *Cross-process lock (`FileShare.None` lock file) around every build.* Correct and cheap, and was
  the agreed design for a while, but once Build/RunTest are throwaway and launches build immutable
  per-SHA outputs by rename, nothing left needs it. A content-based lock with a 1 s wait-and-reread
  was also rejected: the OS exclusive open is already atomic; the real hazard is deleting the lock
  file on release.
- *Per-call random scratch dir.* Cold restore and a full 650 MB copy-local on every call; the
  in-process semaphore makes per-call uniqueness unnecessary.
- *`latest-build.json` marker with mtimes.* Builds are deterministic, so today a no-change rebuild
  does not flag anyone; an mtime marker would. Carrying MVIDs fixed that but git-based staleness is
  simpler and more informative, and it ignores uncommitted work by design.
- *120 s "fresh build" debounce in the launcher.* Serves a stale server when a `.cs` changes 30 s
  after a build; a SHA-keyed output is exact.
- *Worktree per launch as the runtime home.* Lock-free and fully isolated, but a cold build on every
  window and no reuse between windows on one commit. Kept only as the isolated build step.
- *Worktrees for Build/RunTest.* Wrong isolation unit: a worktree checks out a commit, Build must
  verify the uncommitted tree, so the dirty state would have to be replicated per call.
- *Proxy tools (`TestInstanceCallTool`) through the active server.* Not needed; the pain is the
  launch, not the calls.
- *`--per-instance-logs` suffixing logs with the pid.* Logs are one of five per-process outputs;
  `--working-dir` covers all of them with one concept.
- *Default `--working-dir` to the process CWD.* Under VS Code the stdio server's CWD is the
  workspace, which would move prod output into the dev repo root, the method-inventory bug today.

## Execution rules

- CLAUDE.md applies: C# edits via the MCP tools by dispatched implementers; `Build` 0 errors before
  each commit; one `Build`/`RunTest` at a time; test baseline is 0 failures.
- Every commit in a part carries `Plan: plan_build_isolation_prod_instances_and_test_servers`;
  the final commit of Part 4 carries `Closes:`.
- Parts 1-2 touch only the dev repo and are safe to ship first. Part 3 changes how every window
  launches; land it in one session with the user present to relaunch windows. Part 4 depends on
  Part 1 (throwaway output location) and benefits from Part 3 (`--working-dir`) but can ship on
  Part 1 alone.
- After changing server source, restart the own instance per CLAUDE.md "Verification". Parts 3
  and 4 add a `[McpServerTool]` operation or startup arg, so a fresh chat is needed to see them.
- ASCII-only punctuation in code comments, docs and messages.

## Steps

### Part 1 - Dev-repo build isolation

#### Step 1.1 - `fullBuildThrowaway` level and instance-keyed artifacts dir
- Files: `RoslynSentinel.Common/ToolEnums.cs` (`BuildVerifyLevel`, line 289),
  `RoslynSentinel.Tools.Basic/BuildIsolation.cs`, `RoslynSentinel.Engines.Basic/BuildEngine.cs`,
  `RoslynSentinel.Engines.Basic/TestRunEngine.cs`, `RoslynSentinel.Tools.Basic/WorkspaceBuildTestTools.cs`
  (and the mirrored signature in `WorkspaceTools.cs:491-501`), `RoslynSentinel.Common/ToolParams.cs`.
- Change: add `fullBuildThrowaway` and make it the `Build` default; it runs the existing full build
  with `-p:ArtifactsPath=<solutionDir>/.roslynsentinel/build/<instanceId>/`, where `instanceId` is
  the `--working-dir` leaf name (Part 3) or `ROSLYNSENTINEL_INSTANCE_ID` when set, else the pid.
  `RunTest` always uses the same dir. `fullBuild` keeps the bin-writing behaviour and keeps the
  30 s competing-process probe; `fullBuildThrowaway` skips the probe. `useScratchDir` stays accepted
  and `[Obsolete]`-documented in the schema text: `true` maps to `fullBuildThrowaway`. Update the
  `useScratchDir`/level descriptions: say where output goes and that staleness is git-based (after
  Part 3) or unaffected (before it). Keep `--no-incremental`. Replace `.scratch/roslynsentinel-builds/`
  with the new path everywhere, including the `.gitignore` write and the sibling-pid prune
  (`BuildIsolation.cs:195-215`), which becomes "prune instance dirs whose process is gone" (pid
  fallback only; named instances are swept by the launcher in Part 3).
- Done when: `Build` with no args reports the throwaway path in `detail`; a before/after timestamp
  scan of every `<project>/bin|obj` shows no file written by the build itself (the design-time
  rewrites after it are expected and must be called out in the test comment); `BuildIsolationTests`
  updated and green.

#### Step 1.2 - Server-wide build semaphore
- Files: `RoslynSentinel.Engines.Basic/BuildEngine.cs`, `RoslynSentinel.Engines.Basic/TestRunEngine.cs`
  (or a small shared holder in `Engines.Basic`).
- Change: one static `SemaphoreSlim(1,1)` acquired for the duration of any `dotnet build`/`dotnet test`
  subprocess, with the request's `CancellationToken` and a bounded wait (default 10 min, the
  longest `RunTest`); on timeout return `BuildNotRun`/`TestsNotRun` naming the in-flight operation
  and its start time, not a generic failure. `quickBuild` does not take it.
- Done when: a unit test with two concurrent `RunFullBuildAsync` calls against a fake process
  runner shows the second starts after the first completes; existing Build/RunTest tests green.

#### Step 1.3 - `.gitignore` and docs
- Files: `.gitignore`, `docs/current/references/reference_live_server_freshness.md`, CLAUDE.md
  "Verification" (one line), `docs/generated/` tool tables if they embed the schema text.
- Change: add `/.roslynsentinel/build/`; remove `/.scratch` once nothing writes it; document that
  the default build is throwaway and `fullBuild` is the bin-writing level.
- Done when: `git status` clean after a throwaway build; `scripts/Test-GuidanceHygiene.ps1` passes.

### Part 2 - Smaller, faster outputs

#### Step 2.1 - `Directory.Build.props`
- Files: `Directory.Build.props`.
- Change: add `<SatelliteResourceLanguages>en</SatelliteResourceLanguages>`,
  `<CreateHardLinksForCopyLocalIfPossible>true</...>`,
  `<CreateHardLinksForCopyFilesToOutputDirectoryIfPossible>true</...>`,
  `<CreateHardLinksForAdditionalFilesIfPossible>true</...>` with a comment stating the measured
  numbers above and the one hazard: a tool that rewrites files in `bin/` in place (coverlet in-process
  instrumentation) would corrupt the hard-linked source. `TestRunEngine` never passes `--collect`
  (verified 2026-10-23); say so in the comment.
- Done when: `Build(fullBuildThrowaway)` then measure the instance dir: `bin/` well under 200 MB
  of allocated size (`fsutil hardlink list` on one `Microsoft.CodeAnalysis.CSharp.dll` shows > 1
  path), no `*.resources.dll` outside `en`; `RunTest` on one small project green; `scripts/build.ps1`
  baselines unchanged.

### Part 3 - Prod instances outside the repo, git-based staleness

#### Step 3.1 - `--working-dir` startup arg
- Files: `RoslynSentinel.Server.Basic/ServerStartupHelpers.cs` (arg parsing near `:210-217`, log dir
  `:414-444`, crash log `:469-492`, stop marker `:510-529`), `RoslynSentinel.Server.Basic/ConsoleMode.cs`
  (`:567-631` tool dumps, `:642-728` method inventory), `RoslynSentinel.Server.Basic/ServerStdio.cs`,
  `RoslynSentinel.Server.Advanced/ServerHttp.cs`, `RoslynSentinel.Server.Basic/ServerHttp.cs`,
  `RoslynSentinel.Common/SentinelCallToolResult.cs` (expose the path beside `ServerBuildInfo`).
- Change: one `ServerInstancePaths` (in `Common`) resolved once at startup from `--working-dir=<path>`;
  default `AppContext.BaseDirectory` (today's behaviour, never the CWD). All per-process output goes
  under it: `logs/` (`--log-dir` still overrides), `tool_list*.json`, `all_methods_*.csv` /
  `engine_methods_*.json` (no longer the repo root), `crash.log`, and `stopped-by-script.marker` is
  read from it (not `<binDir>\..`). Log file names gain the pid. `McpServerStatus` reports
  `workingDir`.
- Done when: a server started with `--working-dir=<temp>` writes nothing new under its binary
  folder or the repo root (scan before/after); `McpServerStatus` shows the path; tests for the path
  resolver (default, explicit, relative) green.

#### Step 3.2 - Build SHA stamp and git-based `ServerBinaryStaleness`
- Files: `RoslynSentinel.Common/ServerBinaryStaleness.cs`, `RoslynSentinel.Common/SentinelCallToolResult.cs`,
  `RoslynSentinel.Tools.Basic/ServerStatusTools.cs`, `scripts/roslynsentinel-mcp-launch.ps1`
  (passes `-p:SourceRevisionId=<sha>`), `Directory.Build.props` (`IncludeSourceRevisionInInformationalVersion`
  stays default true).
- Change: read the SHA from `AssemblyInformationalVersion` (`+<sha>` suffix). Staleness = the loaded
  solution's repo has commits after that SHA whose paths start with `RoslynSentinel.Common/`,
  `RoslynSentinel.Engines.`, `RoslynSentinel.Tools.` or `RoslynSentinel.Server.`; computed with
  `git log <sha>..HEAD --name-only --format=%h%x09%s` through the existing `GitWorktreeManager.RunGit`
  pattern, cached 5 s as today, repo root = the loaded solution's directory (not the binary path).
  `binaryStaleness` lists the commits (hash, subject) instead of assemblies. When the binary has no
  SHA (local `dotnet build` without the stamp), fall back to the existing `bin/**` MVID scan so
  out-of-tool builds still register. Keep `EnvelopeFlag` semantics (null when not stale).
- Done when: unit tests with a fixture repo: same SHA -> not stale; later commit touching
  `docs/` only -> not stale; later commit touching `RoslynSentinel.Common/x.cs` -> stale with that
  commit listed; no stamp -> legacy scan path exercised. `McpServerStatus` on a stamped build shows
  `buildSha`.

#### Step 3.3 - Launcher: prod clone, per-SHA immutable builds, instances dir
- Files: `scripts/roslynsentinel-mcp-launch.ps1` (kept in the repo), new thin shim
  `scripts/roslynsentinel-mcp-launch-shim.ps1` (what the user-level MCP config points at),
  `scripts/roslynsentinel-vscode-control.ps1`, `scripts/Launch-RoslynSentinelHttpServer.ps1`,
  `scripts/Get-UnknownParameterReport.ps1`, `.gitignore`.
- Change:
  1. Root `$env:ROSLYNSENTINEL_INSTANCES_ROOT`, default `%USERPROFILE%\RoslynSentinelInstances`.
     Layout: `source\` (prod clone, `git clone <devRepo>` on first use, `git pull --ff-only` on
     every launch, branch `master`), `builds\<sha>\Advanced\` (+ `complete` marker file),
     `instances\<id>\` (working dir per server), `launch-startup.log`.
  2. Shim: pulls `source`, then invokes `source\scripts\roslynsentinel-mcp-launch.ps1`, so the
     launcher version always matches the commit it builds. If the pull fails (dev repo mid-rebase),
     continue with the current `source` HEAD and log it.
  3. Launcher fast path: `builds\<sha>\complete` exists -> skip build. Slow path: `git worktree add
     --detach <root>\wt\<token> <sha>` from `source`, `dotnet build RoslynSentinel.Server.Advanced
     -c Debug -o builds\<sha>.<token>\Advanced -p:SourceRevisionId=<sha>`, write `complete`, then
     `Rename-Item` to `builds\<sha>` (if the target appeared meanwhile, delete own copy), `git
     worktree remove --force`, `git worktree prune`. On build failure: use the newest `complete`
     build, log a loud warning into `instances\<id>\launch.log` and the startup log.
  4. Start the server from `builds\<sha>\Advanced\RoslynSentinel.Server.Advanced.exe` with
     `--working-dir=<root>\instances\<id>` plus the existing args. The instance id stays
     `<repoHash>-<random>`; export `ROSLYNSENTINEL_INSTANCE_ID` for Part 1.
  5. Sweep: `instances\<id>` whose process is gone (match `--working-dir` on the `Win32_Process`
     command line, not the exe path, since the exe is shared); `builds\<sha>` with no process
     whose `ExecutablePath` starts there and older than the minimum age; `wt\*` leftovers; and the
     dev repo's `.roslynsentinel\build\<id>` for dead instance ids. Make the minimum age one value
     with one comment (the 3600 s / 15 s drift).
  6. `roslynsentinel-vscode-control.ps1`: find instances by `--working-dir`, write
     `stopped-by-script.marker` there, build the shared HTTP copy into `builds\` the same way (or
     drop the shared copy in favour of Part 4; decide in the session). `Get-UnknownParameterReport.ps1`
     reads `tool_list_all.json` from `instances\*`. Remove `bin-vscode/` and the dead
     `RoslynSentinel.HttpHost` lines from `.gitignore` after the last reference is gone.
- Done when: two windows opened within seconds on the same commit produce one `builds\<sha>` and
  two `instances\<id>` folders, both servers connect, and `McpServerStatus` on each shows the SHA
  and its own `workingDir`; a commit touching `RoslynSentinel.Common` makes both report
  `isServerBinaryStale: true` naming the commit while a docs-only commit does not; killing one server
  and relaunching sweeps its instance dir; `bin-vscode\` is not recreated.

#### Step 3.4 - Docs and memory
- Files: `docs/current/references/reference_live_server_freshness.md`, CLAUDE.md "Verification",
  `docs/current/references/reference_architecture_map.md` (startup args), memory
  `project_per_session_mcp_server_isolation.md` (keyed by random token, not `VSCODE_PID`; instances
  under `RoslynSentinelInstances`).
- Change: describe the layout, the SHA-based staleness and that only committed code reaches a
  window's server; uncommitted server changes are tested per Part 4.
- Done when: hygiene script passes; memory index line updated.

### Part 4 - HTTP test instances from `McpServerControl`

#### Step 4.1 - Server: `--port=0`, loopback bind, stop wording
- Files: `RoslynSentinel.Server.Basic/ServerStartupHelpers.cs` (`ParsePort` `:210-214`),
  `RoslynSentinel.Server.Advanced/ServerHttp.cs` (`:28, 71, 85`), `RoslynSentinel.Server.Basic/ServerHttp.cs`,
  `RoslynSentinel.Tools.Basic/AdminTools.cs` (`:199-201`).
- Change: `--port=0` binds an OS-assigned port and prints the bound URL to stdout as the first
  line (`listening: http://127.0.0.1:<port>/mcp`) and to the log; `--bind=loopback|any`, default
  `loopback` (the current all-interfaces, no-auth default is a finding in its own right; keep `any`
  available for the control script's shared copy). `StopServer` success text states the respawn
  behaviour affirmatively, chosen by a new `--respawn=auto|none` startup arg whose default follows
  the transport: stdio -> `auto` (the text stays as today: VS Code respawns the server on the next
  tool call and the launcher builds it fresh, nothing for the agent to do), http -> `none` (the text
  says this instance stays down and, for a test instance, names `LaunchTestInstance` as the way to
  start another). Never a neutral "the client decides": that sends a model looking for scripts or
  launching the binary by hand. The test-instance launcher (Step 4.2) passes `--respawn=none`
  explicitly; `McpServerStatus` reports the effective value.
- Done when: tests for `ParsePort("--port=0")` and the bind option; a manual start with `--port=0`
  prints a usable URL.

#### Step 4.2 - `LaunchTestInstance` / `StopTestInstance` / `ListTestInstances`
- Files: `RoslynSentinel.Tools.Basic/AdminTools.cs` (`McpServerControlOperation` `:144`, tool `:160-208`),
  new `RoslynSentinel.Engines.Basic/TestInstanceLauncher.cs` (process tracking; reuse the
  `Process.Start` pattern of `BuildEngine.RunFullBuildAsync` `:217-254` and the readiness poll of
  `SubAgentChildServerLauncher.cs:231-244`), `RoslynSentinel.Common/ToolParams.cs`, DI registration in
  `Server.Basic/ServiceRegistrationExtensionsBasic.cs`.
- Change: `LaunchTestInstance(build: auto|always|skip = auto, mode?, includeTools?)`:
  1. `auto` = reuse the throwaway output under `.roslynsentinel/build/<instanceId>/bin/RoslynSentinel.Server.Advanced/debug/`
     when its `RoslynSentinel.Server.Advanced.dll` is newer than every `*.cs`/`*.csproj` under
     `RoslynSentinel.Common`, `Engines.*`, `Tools.*`, `Server.*`; else run `fullBuildThrowaway`
     through the same engine (and the Part 1 semaphore) first. `always`/`skip` as named; `skip`
     with no output returns a structured error naming `Build`.
  2. Start the exe with `--transport=http --port=0 --bind=loopback --mode=<parent or arg>
     --solution=<loaded> --working-dir=<parentWorkingDir>\test-instances\<n>` (or
     `.roslynsentinel/test-instances/<pid>` when the parent has no working dir), read the bound
     URL from stdout, poll `initialize` for up to 60 s.
  3. Track children in a static registry; `StopTestInstance(pid | all)` kills the process tree;
     the parent's shutdown path (`scheduleExit`) and `AppDomain.ProcessExit` kill all children.
  4. Result: `url`, `pid`, `mode`, `solutionPath`, `workingDir`, `buildSource { outputPath,
     buildTimeUtc, gitSha, isDirty, rebuilt }`, `logPaths`, and `howToCall` with two paste-ready
     PowerShell `Invoke-RestMethod` calls (`tools/list`, `tools/call` with a `reason` example)
     including the `Content-Type` and `Accept` headers the stateless Streamable HTTP endpoint
     requires. Verify the exact `Accept` the SDK honours for a JSON (not SSE) reply while building
     this; record it in the result text, not only in a doc.
  5. `ListTestInstances` returns the registry with uptime and URL.
- Done when: `Build` then `LaunchTestInstance` (auto) does not rebuild; `LaunchTestInstance` after
  editing a `.cs` under `Tools.Basic` rebuilds; the returned `tools/list` request works verbatim
  from PowerShell; `StopTestInstance(all)` leaves no `RoslynSentinel.Server.Advanced.exe` from the
  test path; stopping the parent kills the children (manual check); tool tests for the result shape
  and the `skip`-without-output error.

#### Step 4.3 - Script header, CLAUDE.md line, tool tables
- Files: `scripts/Launch-RoslynSentinelHttpServer.ps1` (header only), CLAUDE.md, `docs/generated/`.
- Change: header says the script is for humans and that sessions use
  `McpServerControl(LaunchTestInstance)`; CLAUDE.md "Verification" gets one line: live-test server
  changes with `Build` then `McpServerControl(operation: LaunchTestInstance)`, stop with
  `StopTestInstance`. Regenerate tool tables.
- Done when: hygiene script passes; a fresh session sees the new operations in the schema.

## Out of scope

- Per-session source worktrees (each session on its own branch/worktree, merging back): solves
  cross-session interference from in-flight edits but changes the collaboration model; separate
  decision.
- Env-var-driven `ArtifactsPath` in `Directory.Build.props` so design-time builds also leave the
  shared `obj/`: optional later polish; only matters if design-time writes are observed colliding.
- Running several servers from one `builds/<sha>` is in scope; relocating anything else than the
  files listed in Step 3.1 is not (if another beside-binary write turns up, add it to 3.1).
- Dropping `--no-incremental` for throwaway builds (kept on purpose: mtime skips hid CS0618).
- `RunTest` coverage collection, auth on the HTTP host, `--bind=any` hardening.
- Harness support for pointing `roslynsentinel-modeleval.ps1` / PlanStepRunner at a test instance
  URL (both are stdio-only today); a follow-up once Part 4 exists.

## Risks and open decisions

- **Unverified:** the exact `Accept` header behaviour of the SDK's stateless Streamable HTTP
  endpoint for a plain JSON reply (Step 4.2); whether `--port=0` is honoured by the SDK's Kestrel
  wiring without extra code (Step 4.1); the measured launch time of a cold per-SHA build in a
  worktree (expected 45-90 s vs about 30 s today, paid once per commit, not per window).
- **Hard links** (Step 2.1): safe only while nothing rewrites `bin/` in place. If coverage collection
  is ever added to `RunTest`, pass `-p:CreateHardLinksForCopyLocalIfPossible=false` for that run.
- **Prod clone pull while the dev repo is mid-rebase/merge:** `--ff-only` fails, launcher continues
  on the previous HEAD and logs; acceptable, but the user will not see the warning unless they open
  the startup log. Consider surfacing "built from <sha>, pull failed" in `McpServerStatus`.
- **Branch:** prod tracks `master`; a session on a feature branch does not see its server changes
  in a window until merged. Test instances (Part 4) cover that case; make the branch a launcher
  parameter anyway.
- **Design-time builds** still write the dev repo's `obj/` from every server; the plan does not
  change that. If `AssemblyInfo.cs`/`AssemblyReference.cache` sharing violations are observed,
  revisit the out-of-scope env-var `ArtifactsPath`.
- **Open:** keep or drop the control script's shared `bin-vscode\Advanced.Http` copy once Part 4
  exists (Step 3.3 item 6). Decide in the Part 3 session.
- **Open:** whether `LaunchTestInstance` should refuse when the parent's own solution is not
  `RoslynSentinel.slnx` (the operation is for developing this server; on another solution the
  "server projects" heuristic in `auto` is meaningless).
- Incidental defects found and not fixed here: `scripts/build.ps1:99-100` sets `$allRunning = $null`,
  making its kill-everything step dead code while its header still claims it; the launch script's
  sweep-age comment drift (fixed in Step 3.3 item 5).
