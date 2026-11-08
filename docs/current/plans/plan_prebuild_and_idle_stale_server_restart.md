# Plan: reuse server builds across non-server commits, prebuild after each commit, and restart idle stale servers on their own

**Status:** APPROVED 2026-11-07. Not started; owner decisions settled (see the end).

## Problem

Several Claude sessions share this working tree, and each runs its own stdio server instance. After a
commit to server source, every other instance is stale until its session restarts it by hand
(`McpServerControl(StopServer)` + `LoadSolution`), and the first launch after a commit waits for a full
build.

Evidence:
- The launcher keys builds on the exact HEAD sha: fast path only when `builds\<sha8>\complete` exists
  (`scripts/roslynsentinel-mcp-launch.ps1:659-668`), otherwise `Invoke-ProdBuild` (`:532-598`). A docs-only
  commit therefore costs a full build: on 2026-11-07, `builds\` gained 16e80ce8 (a CLAUDE.md + skill commit),
  73590a61, 61990d8b and 0ecef449 within about 40 minutes.
- The build only happens at launch. There is no build-only switch (param block `:149-172`) and no git hook
  (`core.hooksPath` unset, no active hooks in `.git/hooks`).
- On 2026-11-07, two peer sessions ran servers built at 6db1808a, stale by 00822e3d, until another session
  asked them by `SendMessage` to restart.
- No idle exit exists. The design in memory `project_idle_shutdown_timer_design` (plain 60-minute idle exit)
  was never built.

## Decision

Three independent slices, each useful on its own, ordered by value per effort:

1. **Build reuse.** At launch, if the newest complete build's commit is an ancestor of HEAD and no commit
   since then touches server source, serve that build instead of building HEAD. "Server source" is one shared
   rule, used by the launcher and by `ServerBinaryStaleness`.
2. **Prebuild.** A `-BuildOnly` mode of the launcher (sync source, select or build, record the result, no
   server), started detached from git `post-commit`/`post-merge`/`post-rewrite` hooks. Each launch or prebuild
   records its choice in `builds\latest.json` (`headSha`, `buildSha8`, `utc`).
3. **Idle stale restart.** A stdio server exits on its own when all of these hold: idle for N minutes, no tool
   call in flight, not halted by ExternalFileDrift, no scoped operation ledger open, the build gate not busy,
   and `latest.json` names a different build for the dev repo's current HEAD. VS Code respawns it on the next
   tool call, which takes the launcher fast path, so no session waits on a build.

The server never decides which build is next; it only reads what the launcher recorded. That keeps the
selection rule in one place (the launcher), and a failed prebuild leaves `latest.json` unchanged, so servers
simply stay up.

## Execution rules

- `.ps1` changes and hooks go to `implementer` with Edit/Write (non-C#); C# goes through the MCP edit tools
  per CLAUDE.md. Load `dispatch-implementer` first. Step 6 changes a DTO: load `dto-conventions`.
- One `Build`/`RunTest` at a time. The baseline is 0 test failures.
- Commit each step on its own with a `Plan: plan_prebuild_and_idle_stale_server_restart` trailer; the last
  one carries `Closes:`.
- A step that changes server source ends with `Build(level: fullBuild)` and a restart of this session's own
  instance before any live check.

## Steps

### Step 1 - One shared "server source" rule
- Files: new `scripts/server-source-paths.txt` (one repo-relative prefix per line);
  `RoslynSentinel.Common/ServerBinaryStaleness.cs:43-49` (`SourcePathPrefixes`); a new NUnit test in the
  existing server test project.
- Change: the text file holds the same prefixes as `SourcePathPrefixes` (`RoslynSentinel.Common/`,
  `RoslynSentinel.Engines.`, `RoslynSentinel.Tools.`, `RoslynSentinel.Server.`), plus the root build inputs
  `Directory.Build.props` and `global.json` (decision 4), added to `SourcePathPrefixes` too. A test asserts the file and `SourcePathPrefixes` hold the same set, so they
  cannot drift. C# keeps its compiled list; it does not read the file at runtime.
- Done when: Build 0 errors; the new test passes; changing either list alone makes it fail.

### Step 2 - Launcher reuses a build across non-server commits
- Files: `scripts/roslynsentinel-instances-common.ps1` (new `Find-ReusableBuild`, `Write-LatestBuildRecord`);
  `scripts/roslynsentinel-mcp-launch.ps1` (build selection `:659-685`).
- Change: between the exact-sha fast path and `Invoke-ProdBuild`, take the newest complete build. If its sha is
  an ancestor of HEAD in the source clone (`git merge-base --is-ancestor`) and
  `git diff --name-only <buildSha> <HEAD>` has no path matching `server-source-paths.txt`, select it and log
  `Reuse path: builds\<x> serves HEAD <y> (no server-source changes since)`. Every launch that selects a build
  (fast, reuse, built or fallback) writes `builds\latest.json` atomically (temp file + rename); the fallback
  case also writes `buildFailed: true`.
- Done when: after a docs-only commit, a server restart shows the reuse line in `launch.log`, no new
  `builds\<sha8>` folder appears, and `McpServerStatus` reports the old `buildSha` with
  `binaryStaleness.isStale: false`.

### Step 3 - Build-only mode
- Files: `scripts/roslynsentinel-mcp-launch.ps1` (param block `:149-172`, new `-BuildOnly` switch).
- Change: `-BuildOnly` runs source sync (`:493-526`) and build selection (Step 2), writes `latest.json`, and
  exits 0 (non-zero on failure) without launching or sweeping. Single-flight: take a lock file
  `builds\prebuild.lock` (stale after 30 minutes). If HEAD moved during the build, loop once more, so a burst of
  commits ends with a build for the last one.
- Done when: running `-BuildOnly` after a server-source commit produces `builds\<sha8>\complete` and an updated
  `latest.json` with no server process started; a second concurrent run exits early, logging that the lock is
  held.

### Step 4 - Git hooks start the prebuild
- Files: new `.githooks/post-commit`, `.githooks/post-merge`, `.githooks/post-rewrite` (sh, one line each);
  new `scripts/Start-ServerPrebuild.ps1`; new `scripts/Install-GitHooks.ps1` (sets `core.hooksPath .githooks`,
  an owner action, run once).
- Change: each hook calls `Start-ServerPrebuild.ps1`, which starts `roslynsentinel-mcp-launch.ps1 -BuildOnly`
  fully detached (no inherited stdout/stderr handles) and returns at once. It skips the prebuild when the new
  commit touches no server-source path, and when the branch is not the launcher's `-Branch`.
- Done when: a server-source commit through the MCP `Git` tool (which shells out to `git.exe`,
  `RoslynSentinel.Tools.Basic/GitImpl.cs:1650-1735`, with no `--no-verify`) returns within about 2 s of its
  usual time; the build appears in the background; a docs-only commit starts no build.

### Step 5 - Activity tracking in the server
- Files: new `RoslynSentinel.Common/ServerActivityTracker.cs` (DI singleton: `LastActivityUtc`, `InFlightCount`,
  `Enter()` returning `IDisposable`, `Touch()`); a new request filter added to the ordered array at
  `RoslynSentinel.Server.Advanced/ServiceRegistrationExtensionsBasic.cs:91-103`;
  `RoslynSentinel.Common/PersistentWorkspaceManager.cs:1815` (`ApplyProposedChangesAsync`: `Touch()` at the top).
- Change: the filter wraps every tool call in `Enter()` (try/finally). Nothing counts in-flight calls today
  (the only related concept is `BuildIsolation.cs:300-315`, build/test only). The write-chokepoint touch keeps
  a long single call (BulkComment, Asyncify batches) from looking idle.
- Done when: Build 0 errors; unit tests cover the counter, including a throwing tool call, and touch.

### Step 6 - Idle stale restart monitor
- Files: new `RoslynSentinel.Common/IdleRestartOptions.cs` (pattern of `LlmOptions.Configure`,
  `RoslynSentinel.Common/LlmOptions.cs:78-79`; `--idle-restart-minutes` /
  `ROSLYNSENTINEL_IDLE_RESTART_MINUTES`, `<= 0` disables); new `IdleRestartMonitor` (hosted service, ticks every
  60 s); `RoslynSentinel.Server.Advanced/ServerStdio.cs:43-49` (`Configure` + registration, stdio only); the
  launcher passes `--builds-root` (the server does not know it today); `RoslynSentinel.Tools.Basic/AdminTools.cs:569-586`
  (`ScheduleProcessExit` moves to a shared Common helper that both `StopServer` and the monitor call);
  `RoslynSentinel.Tools.Basic/ServerStatusTools.cs` (new `idleRestart` field).
- Change: on each tick, exit only if every guard holds: `LastActivityUtc` older than N minutes;
  `InFlightCount == 0`; `!IsSessionHalted()` (`PersistentWorkspaceManager.cs:495`); no open ledger
  (`IScopedOperationLedger.OpenOperationName == null`, `RoslynSentinel.Common/IScopedOperationLedger.cs:13`); the
  build gate is idle; `latest.json` exists, is not `buildFailed`, its `headSha` equals the dev repo HEAD, and its
  `buildSha8` differs from this server's `ServerBuildInfo.BuildSha`. Before exiting, write a reason marker that
  the next start reports (same consume-on-start pattern as `ServerStartupHelpers.ReadAndConsumeStoppedByScriptMarker`,
  `ServerStartupHelpers.cs:603`). `McpServerStatus.idleRestart` reports enabled, minutes, `lastActivityUtc` and
  the first unmet guard. Keep the evaluation a pure function of its inputs so it can be unit-tested.
- Done when: unit tests cover each guard blocking an exit and the all-clear case; a live check with
  `ROSLYNSENTINEL_IDLE_RESTART_MINUTES=1` after a server-source commit plus prebuild shows the instance exiting,
  the next tool call respawning on the new build (fast path in `launch.log`), and `McpServerStatus` reporting the
  restart reason.

### Step 7 - Docs
- Files: `docs/current/references/reference_live_server_freshness.md`; `.claude/skills/peer-coordination/SKILL.md`
  (`RESTART` row: normally unnecessary once Step 6 ships); memory `project_idle_shutdown_timer_design`
  (superseded by this plan).
- Done when: the reference describes reuse, prebuild, `latest.json` and idle restart, and how to disable each.

## Out of scope

- The HTTP server variant (shared, long-lived; idle exit there is a different question).
- Restarting Claude sessions themselves, which only the user can do.
- The legacy launch layout (always rebuilds, `roslynsentinel-mcp-launch.ps1:473-477`): the monitor stays disabled
  when `--builds-root` is absent.
- Changing the build sweep: it already protects the selected, newest-complete, running and under-1-hour builds
  (`:784-804`).

## Risks and open decisions

Owner decisions, settled 2026-11-07:
1. **Idle threshold:** default 15 minutes. A ready newer build is required anyway, so a short threshold is
   cheap; the cost of a restart is lost in-memory state (below).
2. **HTTP variant:** stdio only.
3. **Trigger:** git hooks (Step 4), which fire for MCP `Git` commits, shell commits and pulls. Rejected: a
   scheduled task polling HEAD every minute.
4. **Root build inputs:** `SourcePathPrefixes` ignored root files, so a change to them altered the server
   binary but would be reused (Step 2) and not reported stale. Of the candidates, `Directory.Build.props` and
   `global.json` exist at the root (no `Directory.Packages.props`, `nuget.config` or `Directory.Build.targets`);
   both join the rule in Step 1. `.editorconfig` stays out: it changes diagnostics, not the binary's behaviour.

Risks and unverified points:
5. **State lost on restart (hypothesis to verify).** Undo history (`UndoLastApply`), toolsets enabled by
   `McpToolsetControl` and other in-memory state probably do not survive. Step 6 should list what is lost in
   the restart-reason marker so the agent re-enables toolsets instead of concluding a tool is missing.
6. **Hook hang (risk).** `GitImpl.RunGitAsync` reads git's output to EOF; a prebuild child that inherits the
   hook's stdout would keep the pipe open and hang every commit. Step 4's done-when timing check guards this.
7. **CPU contention (risk).** A prebuild competes with sessions' own `Build`/`RunTest`, but not for `bin/`/`obj/`
   locks (it builds in a throwaway worktree under the instances root).
8. **Not verified:** that VS Code respawns after a self-initiated `Environment.Exit` exactly as after
   `StopServer`. Both use the same exit path, and the respawn after `StopServer` worked on 2026-11-07.
