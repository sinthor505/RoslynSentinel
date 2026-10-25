# Plan: Surface a failed launcher build to the agent (no silent fallback to an older build)

**Status:** IN PROGRESS 2026-10-24. All four steps built (Build 0 errors, new tests pass); live verification (commit, StopServer, read `lastLaunch` on the respawned server) pending, then close.

## Problem
When `McpServerControl(StopServer)` respawns, the launcher builds the new HEAD in a clean worktree. If that
build fails it silently falls back to the newest complete build
(`scripts/roslynsentinel-mcp-launch.ps1:588-596`; WARNING goes to `launch.log`, `launch-startup.log` and
stderr only). The agent sees a successful `StopServer`, "already loaded" from `LoadSolution`, and later an
"Unknown parameter" error from the old binary. `McpServerStatus` shows `binaryStaleness.isStale` but not that
a newer build was attempted and failed, nor why. The working-tree `Build(fullBuild)` can be green while the
committed HEAD is red (uncommitted fix), which is exactly how the original incident arose. Evidence:
[blocking_error_restart_reuses_stale_commit_keyed_build.md](../blockers/resolved/blocking_error_restart_reuses_stale_commit_keyed_build.md);
TODO.md open item "Surface a failed respawn build".

## Decision
- The launcher writes `<instance dir>\launch-outcome.json` on every launch (success, fast path, fallback).
  It overwrites the file each launch, because a respawn reuses the instance folder.
- The server reads it from `ServerInstancePaths.Current.WorkingDir` through a new static
  `LaunchOutcome` class in `RoslynSentinel.Common` (no DI change, unlike `StoppedByScriptMarker`). It exposes
  it as `lastLaunch` in `McpServerStatus` and, when `buildFailed` is true, as a one-shot `launchWarning` on
  the first response envelope after start. The existing bool `isServerBinaryStale` stays; a per-response
  string would cost tokens on every call.
- `StopServer` appends a warning when `git status --porcelain` shows uncommitted changes under the server
  source prefixes (`ServerBinaryStaleness.SourcePathPrefixes`): a restart builds committed code only.
- Rejected: failing hard instead of falling back (a window with no server is worse; exit 97 stays for "no
  build at all"), and a launcher retry loop (the build is deterministic).

## Naming and deviations (as built)
- The repo DTO rules forbid new `*Outcome` types, so the plan's `LaunchOutcome` is `LaunchBuildResult`
  (record) plus `LaunchBuildMarker` (static reader) in `RoslynSentinel.Common/LaunchBuildMarker.cs`. The
  marker file is still `launch-outcome.json`.
- `LaunchBuildMarker.TakeEnvelopeWarning()` applies the server-process guard; the internal overload taking
  a result does not, so tests can exercise the once-only logic.
- `DescribeUncommittedServerSource` runs `git status --porcelain` without pathspecs and filters by
  `SourcePathPrefixes` in code: git pathspecs match whole path components, so `RoslynSentinel.Tools.` would
  match nothing.
- The launcher writes no marker on the `exit 97` path (no build at all); a non-compile failure with no
  `error CODE` lines reports the first line of the failure text as the single `firstErrors` entry.

## Execution rules
- Slices are sequential (shared server process and `bin/`); one `Build` at a time.
- Marker JSON shape (camelCase, ASCII only):
  `{"utc":"2026-10-24T20:39:51Z","attemptedSha":"36e51f0c","servedSha":"2c93d97b","buildFailed":true,"firstErrors":["..."]}`.
  `attemptedSha`/`servedSha` are 8-char short shas; `firstErrors` is at most 3 `error <CODE>` lines, each
  cut to 300 chars.
- `LaunchOutcome` never throws: a missing or malformed file reads as "no outcome".
- Server edits are not live until `Build(level: fullBuild)` plus a restart (CLAUDE.md "Verification").

## Steps

### Step 1 - Launcher writes the marker
- Files: `scripts/roslynsentinel-mcp-launch.ps1`
- Change: in `Invoke-ProdBuild`'s catch block keep the failure text in a script-scope variable. After the
  "choose the build" block (line 576-603) write `launch-outcome.json` in `$instanceDir` with the shape above:
  fast path / successful build -> `buildFailed:false`, `servedSha` = selected build; fallback ->
  `buildFailed:true`, `attemptedSha` = HEAD, `servedSha` = fallback build, `firstErrors` parsed from the
  failure text (strip the worktree path prefix). Wrap the write in try/catch so a marker failure never stops
  a launch (log it with `Write-LaunchLog`). Update the script's header comment (line ~92 / ~120 file list).
- Done when: a launch against a deliberately non-compiling worktree HEAD produces the file with
  `buildFailed:true`, and a normal launch produces `buildFailed:false`; `launch.log` shows the write.

### Step 2 - Server reads and reports it
- Files: `RoslynSentinel.Common/LaunchOutcome.cs` (new), `RoslynSentinel.Common/SentinelCallToolResult.cs`,
  `RoslynSentinel.Tools.Basic/ServerStatusTools.cs`
- Change: `LaunchOutcome` record (`Utc`, `AttemptedSha`, `ServedSha`, `BuildFailed`, `FirstErrors`,
  `Hint`) with `Current` (lazy, read once from `Path.Combine(ServerInstancePaths.Current.WorkingDir,
  "launch-outcome.json")`, null if absent/malformed), `Parse(string json)` (internal, pure, for tests) and
  `TakeEnvelopeWarning()` (returns the warning text once per process via `Interlocked`, only when
  `BuildFailed`, and only in a server process - same entry-assembly guard as
  `ServerBinaryStaleness.EnvelopeFlag`). Hint text: "committed HEAD <attemptedSha> does not build; this
  server runs older build <servedSha>. Fix and commit, then McpServerControl StopServer." Add
  `string? LaunchWarning { get; init; } = LaunchOutcome.TakeEnvelopeWarning();` beside
  `IsServerBinaryStale` (null is omitted from JSON). Add `LaunchOutcome? LastLaunch = null` as the last
  optional parameter of `McpServerStatusResult` and pass `LaunchOutcome.Current`; extend the
  `McpServerStatus` description with one sentence on `lastLaunch`.
- Done when: Build 0 errors; Step 4 tests pass; after `fullBuild` + restart with a failing HEAD,
  `McpServerStatus.lastLaunch.buildFailed` is true and the first response carries `launchWarning`.

### Step 3 - StopServer warns about dirty server source
- Files: `RoslynSentinel.Common/ServerBinaryStaleness.cs`, `RoslynSentinel.Tools.Basic/AdminTools.cs`
- Change: add `public static string? DescribeUncommittedServerSource(string solutionDirectory)` beside
  `EvaluateGit`: `GitProcess.TryRun(dir, GitTimeout, "-c", "core.quotepath=false", "status", "--porcelain",
  "--", <SourcePathPrefixes>)`; return null when clean, git fails or the directory is missing, else
  "N uncommitted file(s) under server source (first: <path>); a restart builds committed code only, so
  commit them first or the respawn may run the old build". Never throws. In `ControlServer`'s StopServer
  success path (respawn auto) append the text when `loadedSolutionPath` is set and the helper returns
  non-null.
- Done when: Build 0 errors; Step 4 tests pass.

### Step 4 - Tests and docs
- Files: tests in `RoslynSentinel.Tests.Tools.Basic` (where the existing `ServerBinaryStaleness` tests
  live): `LaunchOutcome.Parse` (valid, failed, malformed, empty), `TakeEnvelopeWarning` once-only
  (via an internal overload taking the outcome), `DescribeUncommittedServerSource` (temp git repo: clean ->
  null, dirty server file -> text, dirty non-server file -> null). Docs: update
  `docs/current/references/reference_live_server_freshness.md` line 54 (a restart picks up new commits only
  if they build; otherwise falls back; check `McpServerStatus.lastLaunch`, `instances\<id>\launch.log`),
  move the TODO.md item to CLOSED.md, set this plan's Status to IMPLEMENTED with hashes.
- Done when: new tests pass with no new failures against the baselines.

## Out of scope
- A `Build(fullBuild)` warning about uncommitted server source (same helper could serve it; `// TODO` at the
  `Build` tool once Step 3 lands).
- Making the launcher run a pre-flight compile of HEAD before `StopServer`.
- HTTP/`--respawn=none` flows (no launcher, so no marker; `lastLaunch` is null).

## Risks and open decisions
- `TakeEnvelopeWarning` fires from the first `SentinelCallToolResult` constructed in the process; if that
  object is never sent (internal use) the warning is lost. `McpServerStatus.lastLaunch` is the durable path.
  Hypothesis: the first constructed envelope is a real tool response (unverified).
- Whether `ServerInstancePaths.Current.WorkingDir` equals the launcher's `$instanceDir` in the legacy
  (non-prod) layout is unverified; the prod layout passes `--working-dir=$instanceDir`
  (`roslynsentinel-mcp-launch.ps1:752`). Legacy launches may report no outcome.
- Step 3's dirty check is a heuristic (uncommitted != broken HEAD); wording must say "may".
