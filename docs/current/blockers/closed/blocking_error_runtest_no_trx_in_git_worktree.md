# `RunTest` returns TestRunIncomplete "No TRX result file was produced" when the solution is loaded from a git worktree

**Status:** RESOLVED 2026-11-24 in 6b3f492b. The hypotheses H1 (scratch root) and H2 (`.git` file) below were wrong; H3 was closest (dotnet test failed before writing a TRX, silently).

## Resolution
Root cause: the server process carries MSBuildLocator pins in its environment (`MSBUILD_EXE_PATH`, `MSBuildSDKsPath`, `MSBuildExtensionsPath`, all pointing at SDK 10.0.404). `TestRunEngine.RunOneProjectAsync` (`RoslynSentinel.Engines.Basic/TestRunEngine.cs`, the `ProcessStartInfo` block that ends at `BuildIsolation.ApplyArtifactsPath`) passed that environment on to `dotnet test`; `BuildEngine.RunFullBuildAsync` already stripped it. `global.json` is git-ignored here (`.gitignore:10 *.json`), so a worktree has none: `dotnet` then runs the newest installed SDK (11.0.100) while its MSBuild worker nodes start from the pinned 10.0.404 MSBuild. The node handshake fails (`MSBUILDDEBUGCOMM` trace: "Handshake failed. Received 12 from host for FileVersionMinor but expected 9"), the nodes are relaunched in a loop (60+ `/nodemode:1` processes in 5 s), and `dotnet test` exits 1 with empty stdout and stderr. That is why neither `includeOutput` nor `StderrTail` showed anything.

Evidence:
- `RunTest(repoPath: <fresh worktree>)` reproduced the error (same filter on the main tree passed); the same `dotnet test` command line run by hand without the pins passed in the worktree.
- A hand run with the three pins set from the tool shell exited 1 with no output after about 4 minutes.
- Copying `global.json` into the worktree (host SDK 10.0.404 again, matching the pin) made the unfixed server's `RunTest(repoPath)` pass.

Fix: `BuildIsolation.MsBuildPinnedVariables` and `BuildIsolation.RemoveInheritedMsBuildPins` (`RoslynSentinel.Engines.Basic/BuildIsolation.cs`); `TestRunEngine` and `BuildEngine` both call it (before caller `environment`, so a caller can still set a pin on purpose). Tests: `BuildIsolationTests.RemoveInheritedMsBuildPins_StripsEveryPinnedVariableAndKeepsTheRest` and `DotnetSpawningEngines_StripTheInheritedMsBuildPins` (Tests.Tools.Basic, OutOfProcess class, 38/38 passed).

Live check: `RunTest(repoPath)` in a worktree without global.json passed on the restarted server at 6b3f492b.

End-to-end test: `RunTestMsBuildPinStripTests` (Tests.Tools.Basic, OutOfProcess) runs the real `TestRunEngine` with nonexistent `MSBUILD_EXE_PATH`/`MSBuildSDKsPath`/`MSBuildExtensionsPath` in the process environment (green: tests counted) and the same values through the caller `environment` (red control: no-TRX error); with the strip disabled the green test fails.

Follow-ups, both done: the other private copies of the pin list now use `BuildIsolation` (d8ea2319), and `TestRunEngine.NoTrxDetail` puts the exit code and, for a silent exit, the known cause and options into the no-TRX message (809c5c50).

## What was being attempted
Overnight run of the tool-surface controller stage F plan (2026-11-24). Gated slices needed `RunTest` to verify test classes.

Setup:
- `LoadSolution(c:\tmp\rs-stage-f\RoslynSentinel.slnx, forceReload: true)`.
- `c:\tmp\rs-stage-f` is a git worktree of this repo on branch `tool-surface-stage-f`, created with `GitRemote(worktree add)`.
- `RunTest` was called against that loaded solution, including calls with `includeOutput: true`.

The exact `RunTest` arguments (project and filter values) were not preserved in the orchestrator report. Run/step ids were not recorded either.

## The exact symptom
`RunTest` returned `errorCode` `TestRunIncomplete` with this message (as reported by the orchestrator):

```
No TRX result file was produced
```

No stderr was returned, even with `includeOutput: true`.

## Source trace
Not traced. The source was not read when this doc was written. The following are hypotheses, not findings.

- H1: `TestRunEngine` writes or looks for the TRX in a results directory derived from the scratch root. The default `ROSLYNSENTINEL_SCRATCH_ROOT` is `..\RoslynSentinel-ScratchDir`, relative to the solution. For `c:\tmp\rs-stage-f` that resolves to `c:\tmp\RoslynSentinel-ScratchDir`. The directory may be missing or unwritable, or the writer and reader may resolve it differently (solution root versus worktree root).
- H2: A worktree has a `.git` file rather than a `.git` directory. Some path or repo-root discovery step may resolve to the wrong root, or fail, and send the TRX somewhere the reader does not look.
- H3: `dotnet test` exits before writing a TRX (for example a build or restore failure in the worktree). The message "No TRX result file was produced" then masks the real cause. This fits the missing stderr: it may be dropped or not captured on this path.
- Separate question: why stderr is empty even with `includeOutput: true`. If the process failed, its output should be surfaced.

Related existing note: `docs/current/INBOX.md` line 16 records a different `TestRunIncomplete` hint problem. Its error hint is misleading when the adapter rejects a filter, and it points at `WorkspaceBuildTestImpl.cs`. That is likely the same error-mapping area, but not the same defect.

Start with a text search for `No TRX result file` (via `Search`, mode `text`) and trace back to where the TRX path is chosen.

## What is and is not confirmed
Confirmed (from the orchestrator report, not independently reproduced):
- `RunTest` failed with `TestRunIncomplete` and "No TRX result file was produced" on the worktree-loaded solution.
- `scripts/Invoke-WorktreeTests.ps1 -RepoPath c:\tmp\rs-stage-f` (no `-Ref`) produced normal `summary.json` results.
- The same script with `-Ref <branch commit>` also produced normal results. The same test classes passed.
- The code under test is therefore buildable and testable in the worktree. The failure is in the `RunTest` path, not the tests.

Not confirmed:
- Whether `RunTest` works on this same solution from the main repo (not tried in this report).
- Whether the cause is the worktree itself, the `c:\tmp` location, the scratch-root resolution, or something else.
- Where the TRX was expected, and whether it was written anywhere.
- Why stderr was empty.

## Why this matters
- Slices gated on `RunTest` cannot be verified through the MCP tool when the server is loaded on a git worktree. The tool gives no actionable cause: no stderr, and a message that names a symptom (no TRX) instead of the reason.
- The failing message and the empty output leave the caller unable to tell a missing results directory, a build failure and a worktree path issue apart. An agent under test would have no route to recover.
- The run was not halted. A staged route exists: `scripts/Invoke-WorktreeTests.ps1`. Worktree-based runs are a normal workflow (PlanStepRunner and overnight stage runs), so this affects the dog-fooding rule that tests go through `RunTest`.

## Suggested direction (not implemented)
1. Reproduce: load a fresh worktree outside the repo (for example `c:\tmp\<name>`), call `RunTest` on one small test class, and note whether `c:\tmp\RoslynSentinel-ScratchDir` is created and where any TRX lands.
2. Trace in `TestRunEngine`: where the TRX or results directory is chosen, and what each of the solution root, worktree root and scratch root contributes to it.
3. If the cause is path resolution, resolve the results directory from a location that exists and is writable regardless of worktree placement, or create it on demand.
4. If the cause is `dotnet test` failing before a TRX is written, surface its stderr and exit code in the result so the error names the actual failure. Report why stderr was dropped on this path even with `includeOutput: true`.
5. Add a test that runs `RunTest` against a solution located in a git worktree.
