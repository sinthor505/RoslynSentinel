# Full build waits for pipe EOF that a reused MSBuild node holds open - OPEN

**Status:** RESOLVED 2026-11-10 (ef4301b6): ChildProcessCapture waits on exit with a 4 s drain grace; tests set MSBUILDDISABLENODEREUSE=1.

## Summary
`FullBuild_SolutionScope_FailsBecauseOfBadProject` (`BuildScopeFullBuildTests.cs` ~103) times out after
5 minutes on loaded runs. `BuildEngine.RunFullBuildAsync` (`BuildEngine.cs` ~198) spawns `dotnet build`
with redirected stdout/stderr and waits on
`Task.WhenAll(ReadToEndAsync(stdout), ReadToEndAsync(stderr), WaitForExitAsync)`. `ReadToEndAsync`
completes only at pipe EOF, so an MSBuild worker node (`/nodeReuse:true`) that inherited the pipe keeps
the wait alive after `dotnet` itself has exited.

## Why it matters
A user `Build` can hang until the 5-minute timeout for the same reason, and the full test suite flakes.

## What is known
- The spawn passes no `-nodeReuse` argument and sets no node-reuse env var; only the MSBuild/DOTNET pins
  are stripped (~327-360).
- The timeout path calls `TryKillProcessTree`, which returns early when `process.HasExited`
  (~422-432), so the node holding the pipe is never killed.
- The timeout message does not say whether the root process had exited.
- 2026-11-09: 20 orphaned `dotnet MSBuild.dll /nodemode:1 /nodeReuse:true` processes with a dead parent
  were on the box (attribution to this test unverified).
- Precedent: `scripts/Invoke-WorktreeTests.ps1` ~171-180 sets `MSBUILDDISABLENODEREUSE=1`.
- Ruled out as the fix: `-nodeReuse:false` or `--disable-build-servers` on every spawn (slows repeated
  user builds). `DOTNET_CLI_USE_MSBUILD_SERVER=0` is probably irrelevant: the survivors are classic
  worker nodes, not the MSBuild server (hypothesis).
- Unchecked: whether `RunTest`'s `dotnet test` spawn has the same pattern.

## Decisions (todo review 2026-11-09)
- Wait on process exit, not pipe EOF (READY, Medium):
  - After `WaitForExitAsync`, give the stream reads a 3-5 s drain grace, then cancel them and keep the
    partial text (chunked read loops rather than `ReadToEndAsync`).
  - The timeout message reports whether the root had exited, plus its exit code.
  - The test engine factory sets `MSBUILDDISABLENODEREUSE=1` on the child, so tests stop leaving orphan
    nodes.
  - Apply the same pattern to the `RunTest` spawn if it has it.
  - Why: fixes the hang for users and tests without losing node reuse.

## What would resolve it
A test with a fake child that exits while a grandchild keeps the pipe open returns within the grace window
with the exit code, and `FullBuild_SolutionScope_FailsBecauseOfBadProject` passes under a parallel full run.
