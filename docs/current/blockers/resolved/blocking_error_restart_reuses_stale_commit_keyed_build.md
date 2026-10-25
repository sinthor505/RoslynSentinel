# `McpServerControl(StopServer)` respawn silently reuses an old commit-keyed build when the new HEAD fails to build

**Status:** RESOLVED 2026-10-24 for the compile error (fixed in cd080eb7; respawn then served 36e51f0c, contextLines accepted live). Silent-fallback remainder fixed in e592a4e2 (plan_surface_failed_launch_build: `McpServerStatus.lastLaunch`, one-shot `launchWarning`, StopServer dirty-source warning). Original: OPEN 2026-10-24. Root cause traced to the launcher's per-instance `launch.log`: the build of the new HEAD (36e51f0c) failed with CS0103 and the launcher fell back to the newest complete build (2c93d97b); the fallback is only visible in log files, not to the agent. Why committed HEAD does not compile is not yet traced (see "What is and is not confirmed").

## What was being attempted
Verify the new `Search` parameter `contextLines` (commit 36e51f0c, "Add Search contextLines (text mode)...") on the live server, per the CLAUDE.md "Verification" procedure and `docs/current/references/reference_live_server_freshness.md` (lines 36-54, opt-in `RoslynSentinelInstances` layout):

1. `Build(level: fullBuild)` -> succeeded, 0 errors (`bin/` updated 20:39:26 local).
2. `McpServerControl(operation: StopServer, confirmServerStop: ConfirmServerStop)` -> returned "VS Code will rebuild the server binary and spawn a fresh instance on your next tool call".
3. Next call `LoadSolution` -> "already loaded".
4. `Search(..., contextLines: ...)` to exercise the new parameter.

Session date 2026-10-24; repo HEAD 36e51f0c on `master`.

## The exact symptom
`Search` rejected the new parameter, i.e. the live server is the old binary:

```
Unknown parameter 'contextLines' for tool 'Search'. This tool accepts only: containingNamespace, ... symbolKind.
```

`McpServerStatus` after the restart:

- `serverBinaryPath`: `C:\Users\Administrator\RoslynSentinelInstances\builds\2c93d97b\Advanced\RoslynSentinel.Server.Advanced.dll`
- `buildSha`: `2c93d97b59cae3ee3b342a2e38ab4948e35ebf6d` (not one of the listed stale commits)
- `workingDir`: `...\instances\d87a019a-73e211ea`, respawn `"auto"`
- `binaryStaleness.isStale: true`, 33 `staleCommits`, newest 36e51f0c, oldest e06631cc
- process PID 5256, started 2026-10-24 20:39:52 local (`serverStartedUtc` 2026-10-25T03:39:52Z), so a restart DID happen
- `RoslynSentinel.Tools.Basic.dll` under `builds\2c93d97b\Advanced` has LastWriteTime 3:55 PM local

The only output the agent saw was the success message of `StopServer` and the later "unknown parameter" error. Nothing told the agent that the respawn used an older build.

## Source trace
Traced from log files (not yet from launcher source line numbers).

`C:\Users\Administrator\RoslynSentinelInstances\instances\d87a019a-73e211ea\launch.log` (the respawn that produced PID 5256):

```
2026-10-24 20:39:43 Source: HEAD is 36e51f0ccbc805189bb430c69638d14b1071ec4f (pullOk=True, cloned=False).
2026-10-24 20:39:43 Build starting for 36e51f0c... (worktree ...\wt\73e211ea -> ...\builds\36e51f0c.73e211ea\Advanced)...
2026-10-24 20:39:51 Build of 36e51f0c... FAILED: dotnet build FAILED (exit 1).
  ...\wt\73e211ea\RoslynSentinel.Engines.Basic\SyntaxTargetResolver.cs(612,13): error CS0103: The name 'contextSnippet' does not exist in the current context
  ...SyntaxTargetResolver.cs(614,51): error CS0103: The name 'hintBuilder' does not exist in the current context
  ...SyntaxTargetResolver.cs(614,93): error CS0103: The name 'filePath' does not exist in the current context
  ...SyntaxTargetResolver.cs(614,158): error CS0103: The name 'sourceText' does not exist in the current context
  ...SyntaxTargetResolver.cs(614,170): error CS0103: The name 'filePath' does not exist in the current context
2026-10-24 20:39:51 WARNING: could not produce a build for the current source HEAD (sha=36e51f0c...); falling back to the newest complete build 2c93d97b. This server is OLDER than the source; see the lines above for the reason.
2026-10-24 20:39:52 Launching: ...\builds\2c93d97b\Advanced\RoslynSentinel.Server.Advanced.exe --transport=stdio --working-dir=...\instances\d87a019a-73e211ea ...
```

The same "could not produce a build ... falling back to the newest complete build 2c93d97b" WARNING appears in `C:\Users\Administrator\RoslynSentinelInstances\launch-startup.log` for every launch that day (HEAD shas 9b7f55b1, a6ad2f54, 892f67cb, 36e51f0c; last at 20:39:51). `builds\` contains only `2c93d97b` (complete marker 15:55); no build was published since. The prod clone `RoslynSentinelInstances\source` is at 36e51f0c, so the pull succeeded.

Relevant script behaviour: `scripts/roslynsentinel-instances-common.ps1` (the source-sync function, around lines 159-231) treats a failed pull as non-fatal and keeps the current HEAD; the launcher (`scripts/roslynsentinel-mcp-launch.ps1`) logs the build-failure fallback as a WARNING and hands off to the newest complete build. The exact fallback code line was not located in this session.

## What is and is not confirmed
Confirmed (log evidence above):
- StopServer's respawn did run the launcher, did pull 36e51f0c, did attempt a worktree build of 36e51f0c, and that build failed with 5 x CS0103 in `RoslynSentinel.Engines.Basic/SyntaxTargetResolver.cs` (lines 612/614 of the committed file).
- The launcher then fell back to the only complete build, 2c93d97b, which has no `contextLines`.
- The documented claim in `reference_live_server_freshness.md` line 54 ("A restart picks up new commits") holds only when the new SHA builds.

Confirmed about the working tree: `Build(level: fullBuild)` of the working tree succeeded with 0 errors, and `git status` at session start shows `RoslynSentinel.Engines.Basic/SyntaxTargetResolver.cs` as modified (uncommitted).

Not confirmed (hypothesis): the committed 36e51f0c is missing part of the `SyntaxTargetResolver.cs` edit (the missing names `contextSnippet`, `hintBuilder`, `filePath`, `sourceText` exist as parameters/locals in the working-tree helper `SnippetMatchesInside`, whose line numbers differ from the committed file), i.e. the commit omitted or only partly included that file's changes. This was not verified because the committed blob could not be read with the available tools this session (shell `git show | sed` is hook-blocked and `Git` was not available to the blocker-writer agent). Next step: `Git(operation: show/diff)` of `36e51f0c:RoslynSentinel.Engines.Basic/SyntaxTargetResolver.cs` around lines 600-620.

Earlier hypotheses in the session brief (launcher builds only at window start; build from a different clone/branch) are ruled out by the logs: the respawn did try to build, from the correct clone and branch.

## Why this matters
Environment defects, none of which is the agent's fault:
- **Silent fallback.** `StopServer` reports success and "VS Code will rebuild the server binary", `LoadSolution` says "already loaded", and the new process starts normally, yet it is the old binary. The failure is visible only in `launch.log` / `launch-startup.log`. `McpServerStatus` shows `isStale: true` and the old `buildSha`, but not that a newer build was attempted and failed, nor the compile errors.
- **Local build is green but prod build is red.** `Build(fullBuild)` of the working tree passes, so the agent has no signal that committed HEAD does not compile. The prod layout builds only committed code (reference doc line 42), so uncommitted/partially committed edits cause exactly this divergence.
- The server-freshness doc promises that a restart picks up new commits and does not mention the fallback.
- Same class of issue repeated at least 6 times on 2026-10-24 (launch-startup.log), with different HEAD shas, without any agent-visible report.

## Suggested direction (not implemented)
1. Immediate unblock: inspect and fix the committed `SyntaxTargetResolver.cs` so 36e51f0c (or the next commit) builds in a clean worktree, then `StopServer` again and confirm `buildSha` changes. Until then, verify uncommitted/new-parameter behaviour with `Build` + `McpServerControl(LaunchTestInstance)`.
2. Surface the fallback: have `McpServerStatus` report the launcher's last build outcome (attempted SHA, failed, first errors, fallback SHA), e.g. by having the launcher write a small marker in `instances\<id>\` that the server reads; and/or return a warning from `StopServer`/`LoadSolution` when `buildSha` differs from the source clone HEAD.
3. Pre-flight check: before relying on a restart, a tool (or the `Build` fullBuild result) could warn when the working tree has uncommitted changes to server projects ("prod layout builds committed code only").
4. Update `docs/current/references/reference_live_server_freshness.md` line 54 to state the fallback and where to find the log (`instances\<id>\launch.log`).

## Root cause (verified 2026-10-24)

The committed `SyntaxTargetResolver.cs` does not compile at HEAD. Commit 9b7f55b1 (null contextSnippet with several candidates throws `ToolAmbiguousMatchException`) landed the new `throw` block and a doc-comment line in the wrong method (a block that uses `hintBuilder`, `sourceText` and `filePath` sat inside the hint-assembling method around lines 605-617, where those names are out of scope), which gives the 5 x CS0103. The working tree holds an uncommitted fix (`Git diff` on the file: the throw block moves into `ResolveBySnippetWithWarning`, the stray doc line moves to that method's summary). So committing that file's working-tree change is what makes HEAD buildable, and the launcher then picks up the new SHA.

Remaining design gap: the launcher falls back to the previous complete build silently. `StopServer` says it will rebuild, but a failed build only shows in `launch.log` / `launch-startup.log`; nothing surfaces in `McpServerStatus` or the next tool response. Suggest `McpServerStatus` report `lastLaunchBuildFailed` plus the first error.
