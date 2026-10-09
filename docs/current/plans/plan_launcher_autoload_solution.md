# Plan: Auto-load RoslynSentinel.slnx at server start and say "load in progress" while it runs

**Status:** IMPLEMENTED 2026-10-09 (launcher a48bf633, Step 2 in the commit after it). Build 0 errors; full suite 3473 tests, 3447 passed, 0 failed, 26 skipped (baseline 3470 + 3 new tests). Live restart check (Step 3, last bullet) is still to be done by the session that restarts the server.

## Implementation notes (2026-10-09)

- Step 1 deviations: `$launchArgs` is built with a `foreach` (not `@($ServerArgs)`, which yields a 1-element array holding `$null` when no args are passed); `$cwdInRepo` compares against `$repoRoot + '\'` so a sibling folder such as `...\RoslynSentinel2` does not match; the not-adding log line also prints the cwd. The extracted block was run in isolation for 6 cases (no args, extra args, `--solution=x`, `--solution x`, cwd outside, sibling prefix) and the file parses with 0 errors under both pwsh 7 and Windows PowerShell 5.1. The script was not executed end to end.
- Step 2 as planned, plus a third test (`ForFilePath_LoadInProgress_ComposesRetryMessage`).
- Premise correction: `GetCurrentSolutionAsync` takes `_solutionLock` (about line 1086), and `LoadSolutionAsync` takes it synchronously at startup, so tools that go through `GetCurrentSolutionAsync` do not fail mid-load; they queue behind the load (and may hit a client timeout) and only throw the message if the load failed. The "load in progress" wording therefore helps the lock-free paths that read `LoadState` while `CurrentSolution` is null (filePath resolution in ReplaceSnippet/CreateFile/ApplyDiff, `BuildEngine`, `McpServerStatus`, `GetLargeResult`). `WorkspaceFileEditImpl.ReadFileWithoutSolutionAsync` still says "freshly (re)started" while a load is running (it tests `IsFreshStartup` directly); not changed here.
- Unverified: whether the MCP client launches the script with cwd inside the repo (see Risks). The `Auto-load:` line in `bin-vscode/<instance-id>/launch.log` shows the cwd and the decision.

## Problem

Every server restart (rebuild, crash, update) drops the loaded solution. The chat that was using
the server keeps going and its next call fails with "No solution is loaded". Evidence:

- `docs/current/findings/finding_solution_path_lost_across_server_restart.md` (the original finding; its
  recommendation 3 is superseded by the decision below).
- The state is per process: `RoslynSentinel.Common/PersistentWorkspaceManager.cs:151`
  `public SolutionLoadState LoadState => new(SolutionLoadState.ProcessStartedUtc, _lastSuccessfulLoadUtc);`
  and `GetCurrentSolutionAsync` throws `SolutionNotLoadedException(SolutionNotLoadedMessage.Build(LoadState))`
  (about line 1089). Nothing persists a last-used path.
- The wording problem is already fixed: commit `d03bc24` added the "freshly (re)started ... a solution loaded
  earlier in this chat was lost" text (`RoslynSentinel.Common/SolutionLoadState.cs`, `SolutionNotLoadedMessage.Build`).
  What remains is that the agent still has to spend a turn on `LoadSolution`.
- The server already supports start-time loading. `--solution` is accepted as `--solution=<path>` or
  `--solution <path>` (`GetArgValue` in `RoslynSentinel.Server.Basic/ServerStartupHelpers.cs`) and
  `WarmupAndAutoLoadBasic` / `WarmupAndAutoLoadAdvanced` fire-and-forget `LoadSolutionAsync` (serialised on
  `_solutionLock`, `PersistentWorkspaceManager.cs:421-424`). The launcher forwards arbitrary args verbatim
  (`scripts/roslynsentinel-mcp-launch.ps1:328`: `& $exePath --transport=stdio @ServerArgs`) but nobody passes `--solution`.
  The user-global `C:\Users\Administrator\.mcp.json` supplies the arg list, so it is outside the repo.

Because the load is fire-and-forget, the first tool call after a restart usually arrives while the load is still
running. At that moment `CurrentSolution` is null, so the caller gets the "freshly (re)started ... Call LoadSolution"
message, tries `LoadSolution`, and queues behind the auto-load on `_solutionLock`. The message should say "a load is
in progress, retry" instead (Step 2).

## Decision

1. The launcher appends `--solution <repoRoot>\RoslynSentinel.slnx` when (a) the file exists, (b) the caller did not
   already pass a `--solution` arg, and (c) the launch cwd is inside `$repoRoot`. Rule (c) matters because the
   user-global `.mcp.json` launches this same script for every VS Code window, including windows opened on other repos.
2. `SolutionLoadState` gains a `LoadInProgress` flag so `SolutionNotLoadedMessage.Build` can say "load in progress".
3. No Claude Code hook is added (see Hook evaluation).

## Execution rules

- C# edits through MCP tools only; the `.ps1` edit may use Edit/Write (non-C# file).
- Step 1 touches no C#, so it needs no Build; Step 2 does.
- Do not edit `C:\Users\Administrator\.mcp.json` (outside the repo, user-owned).
- The launcher runs under Windows PowerShell 5.1 (see the comment at `roslynsentinel-mcp-launch.ps1:139-144`): no
  `??`, no ternary, no `-Raw`-style pwsh-only features. Use plain `if`.
- Launcher stdout/stderr must stay clean (stdio JSON-RPC): log only via `Write-LaunchLog`.

## Steps

### Step 1 - Launcher appends --solution
- Files: `scripts/roslynsentinel-mcp-launch.ps1`
- Change: in the `#region Launch` block, immediately before `Write-LaunchLog "Launching: ..."` (currently line 313),
  insert:
  ```powershell
  $hasSolutionArg = $false
  foreach ($serverArg in $ServerArgs) {
      if ($serverArg -match '^--solution(=|$)') { $hasSolutionArg = $true }
  }
  $defaultSolution = Join-Path $repoRoot 'RoslynSentinel.slnx'
  $cwd = (Get-Location).Path
  $cwdInRepo = $cwd.StartsWith($repoRoot, [StringComparison]::OrdinalIgnoreCase)
  $launchArgs = @($ServerArgs)
  if (-not $hasSolutionArg -and $cwdInRepo -and (Test-Path -LiteralPath $defaultSolution)) {
      $launchArgs += "--solution=$defaultSolution"
      Write-LaunchLog "Auto-load: no --solution passed and cwd '$cwd' is inside the repo; adding --solution=$defaultSolution"
  }
  else {
      Write-LaunchLog "Auto-load: not adding --solution (passed=$hasSolutionArg, cwdInRepo=$cwdInRepo, slnxExists=$(Test-Path -LiteralPath $defaultSolution))."
  }
  ```
  Then change the `Launching:` log line to print `$launchArgs` and the call at line 328 to
  `& $exePath --transport=stdio @launchArgs`. Use the `--solution=<path>` form so a path with spaces stays one token.
  Update the `.DESCRIPTION` step 4 text (lines 37-39) with one sentence describing the auto-load rule.
- Done when: after `McpServerControl stop` and reconnect, the newest `bin-vscode/<instance-id>/launch.log` contains an
  `Auto-load: ... adding --solution=...RoslynSentinel.slnx` line, and `McpServerStatus` shows a loaded workspace
  without any `LoadSolution` call. There is no automated test for the launcher; this live check is the regression check.

### Step 2 - "Load in progress" wording
- Files: `RoslynSentinel.Common/SolutionLoadState.cs`, `RoslynSentinel.Common/PersistentWorkspaceManager.cs`,
  `RoslynSentinel.Tests/SolutionNotLoadedMessageTests.cs`
- Change (land all three in one `ReplaceSnippet` batch, definitions first):
  1. `SolutionLoadState`: add a third positional parameter with a default,
     `public sealed record SolutionLoadState(DateTime ServerStartedUtc, DateTime? LastLoadedUtc, bool LoadInProgress = false)`
     and document it in the `<param>` list. Existing constructor call sites keep compiling because of the default:
     `PersistentWorkspaceManager.cs:151`, `RoslynSentinel.Tests/Fakes/FakeWorkspaceManager.cs:38`, and
     `RoslynSentinel.Tests/SolutionNotLoadedMessageTests.cs` lines 17, 28, 47, 55, 65, 66, 95 (none edited except the new tests).
  2. `SolutionNotLoadedMessage.Build`: after the `state is null` check and before the `IsFreshStartup` check, add a branch for
     `state.LoadInProgress && !state.HasLoadedSolutionSinceStart` that returns
     `"No solution is loaded yet: a solution load started when this server (re)started and is still in progress. Retry this call in a few seconds; calling LoadSolution is not needed."`
     Keep the existing wording for every other state. Check `SolutionNotLoadedMessage.ForFilePath` still composes (it wraps
     `Build`; confirm by reading it).
  3. `PersistentWorkspaceManager`: add `private int _loadsInProgress;`. In `LoadSolutionAsync`, as the first statement inside the
     `try {` that follows `await _solutionLock.WaitAsync(cancellationToken);` (line 424-425) add
     `Interlocked.Increment(ref _loadsInProgress);`, and as the first statement of the existing
     `finally { _solutionLock.Release(); }` (lines 510-513) add `Interlocked.Decrement(ref _loadsInProgress);`.
     Do the increment after the lock is acquired so a cancelled wait cannot leak the counter. Change line 151 to
     `new(SolutionLoadState.ProcessStartedUtc, _lastSuccessfulLoadUtc, Volatile.Read(ref _loadsInProgress) > 0)`.
     Note that this makes the message state-dependent only on a lock holder, so a caller queued behind the lock is not
     counted; that is acceptable (it is already waiting, not erroring).
  4. Tests in `SolutionNotLoadedMessageTests.cs`: `Build_LoadInProgressOnFreshStart_SaysRetryNotLoadSolution`
     (state `new SolutionLoadState(Started, null, LoadInProgress: true)` -> message contains "still in progress" and does not
     contain "Call LoadSolution"), and `Build_LoadInProgressAfterEarlierLoad_IsStillPlain` (a reload in progress with
     `LastLoadedUtc` set -> `Plain`, since the solution is not null then and this is a defensive case).
- Done when: `Build` has 0 errors and `SolutionNotLoadedMessageTests` passes (existing tests unchanged and green).

### Step 3 - Verification
- Files: none.
- Change: `Build` (0 errors); `RunTest` at solution scope and compare against the known-failure baseline
  (`reference_known_failing_tests`), reporting new failures only; `McpServerControl stop` (with the confirm parameter), let VS Code
  relaunch, `LoadSolution` is NOT needed any more: confirm with `McpServerStatus` that the workspace is loaded, then make a call
  immediately after restart to see which message appears if the race is lost.
- Done when: Build 0 errors, no new test failures, and the live restart check above shows the solution loaded or the
  "load in progress" message.

## Out of scope

- Persisting a last-used solution path to disk (rejected: stale-path risk, and the launcher rule covers this repo, which is
  where restarts actually happen).
- Making `GetCurrentSolutionAsync` wait for the in-flight load instead of failing (a behaviour change touching every tool; see
  Risks).
- Editing `C:\Users\Administrator\.mcp.json`, or auto-loading for other repos' windows.
- Any Claude Code hook.

## Hook evaluation (decision C asked for it)

- SessionStart: a hook runs shell and cannot call MCP tools, so it cannot call `LoadSolution`. It could only print text,
  which the server's own message already provides.
- PostToolUse / PostToolUseFailure on `mcp__*`: a hook could detect the "No solution is loaded" text and inject
  `additionalContext` telling the agent to call `LoadSolution`. This duplicates what `SolutionNotLoadedMessage` now says, adds a
  per-call process spawn on every MCP call, and whether `IsError=true` MCP results fire PostToolUse or PostToolUseFailure is
  unverified (hypothesis). The server-side fix (Steps 1-2) removes the cause instead of narrating it.
- PreToolUse: could block calls until a solution is loaded, but would need the server's state, which a hook cannot read cheaply.
- Recommendation: no hook. Revisit only if auto-load proves unreliable.

## Risks and open decisions

- Race remains: even with auto-load, a call that lands before the load completes (MSBuild load takes several seconds) fails once.
  Step 2 turns that failure into a self-explaining retry message. Making the call wait for the load is *needs design* (every tool
  goes through `GetCurrentSolutionAsync`; a bounded wait changes timeout behaviour). Human decision: accept the retry message, or
  commission the bounded wait.
- cwd guard is a hypothesis: whether VS Code or Claude Code starts the MCP server with cwd inside the repo is unverified. If
  the cwd is elsewhere, Step 1 silently does nothing; the `Auto-load: not adding --solution (... cwdInRepo=False ...)` log line
  shows this. If so, the guard should switch to a different signal (for example an env var set in `.mcp.json`); that is the
  user's file, so flag it rather than work around it.
- The auto-loaded solution is the Advanced flavour's own repo. If the user later wants a different default solution per window,
  `--solution` in `.mcp.json` args wins (rule b), so there is an override path.
- A load failure at start (bad MSBuild state) is only logged, not surfaced; `McpServerStatus` is where to look. Out of scope here.
- Proposed TODO.md change (not applied): none for this plan beyond closing the finding doc once implemented.
