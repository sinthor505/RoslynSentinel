# `Build` / `RunTest` fail with CS2012 and MSB4018 while a `LaunchTestInstance` test instance is running

**Status:** FIXED IN CODE 2026-10-23 (fix options a and b: the child gets its own instance id `test-<parentPid>-<n>` and runs from a real copy of the output under `<workingDir>\server`; helper unit tests green). Live re-test (Build on the parent while a test instance runs) is pending a server restart; move to `resolved/` once that passes. Leftover: the copy is not deleted on `StopTestInstance` (TODO in `TestInstanceLauncher.LaunchFromOutputAsync`). Original finding: mechanism traced to source for the shared artifacts directory (the child inherits the parent's `ROSLYNSENTINEL_INSTANCE_ID` and runs its exe from the parent's `bin/`); the exact file-lock chain on `obj/` files (hard links) is a hypothesis, not verified.

Reference plan: `docs/current/plans/closed/plan_build_isolation_prod_instances_and_test_servers.md` (Part 1 throwaway builds, Part 2 hard links, Step 4.2 `LaunchTestInstance`).

## What was being attempted

Observed 2026-10-23. A stdio server (pid 27876, running from `RoslynSentinelInstances\builds\8421d95d...\Advanced`, instance id `d87a019a-7649a7c2`) was used to test uncommitted server changes.

1. `McpServerControl(operation: LaunchTestInstance, build: Auto)` started an HTTP test instance, pid 21108, at `http://127.0.0.1:59863/mcp`.
   - workingDir: `...\instances\d87a019a-7649a7c2\test-instances\1`
   - exePath: `.roslynsentinel/build/d87a019a-7649a7c2/bin/RoslynSentinel.Server.Advanced/debug/RoslynSentinel.Server.Advanced.exe`
   - `rebuilt: false`
2. `Build` (default level `fullBuildThrowaway`) was then called on the HTTP instance (raw JSON-RPC `tools/call`) and on the stdio instance at the same time.
3. `McpServerControl(operation: StopTestInstance)` for pid 21108 was called afterwards.

## The exact symptom

stdio `Build`, failed after 3 s:

```
CS2012: Cannot open '.roslynsentinel\build\d87a019a-7649a7c2\obj\RoslynSentinel.Common\debug\RoslynSentinel.Common.dll' for writing -- process cannot access the file
```

HTTP-instance `Build`, failed after 8 s with 32 errors, all of the form (path as reported):

```
MSB4018: The "CreateAppHost" task failed unexpectedly ... UnauthorizedAccessException: Access to the path '.roslynsentinel\build\d87a019a-7649a7c2\obj\RoslynSentinel.Server.Advanced\debug\apphost.exe' is denied  (at File.Delete)
```

The HTTP instance's own result detail said its output went to `.roslynsentinel/build/d87a019a-7649a7c2/` -- the PARENT's instance id.

After `StopTestInstance` (pid 21108), the stdio `Build` succeeded (9.6 s, 0 errors). Earlier the same day, with no test instance alive, `Build` took 28 s cold / 14 s warm and a full-solution `RunTest` took 6m49s with no blocking and no writes to project `bin/` or `obj/`.

## Source trace

Confirmed by reading source (line numbers as of commit 8421d95d plus the dirty tree at the time of writing):

1. Artifacts directory is keyed by `ResolveInstanceId()`: `RoslynSentinel.Engines.Basic/BuildIsolation.cs:74-78` returns the sanitized `ROSLYNSENTINEL_INSTANCE_ID` env var if set, else the process id. `GetArtifactsPath` (`BuildIsolation.cs:52-69`) builds `<solutionDir>/.roslynsentinel/build/<instanceId>`. A `// TODO Part 3` at `BuildIsolation.cs:72` says the `--working-dir` leaf name is not yet used for the id (the plan text at line 148 describes it as if it were).
2. Every throwaway `Build` applies it via `BuildIsolation.ApplyArtifactsPath` (`RoslynSentinel.Engines.Basic/BuildEngine.cs:267`); `RunTest` does too (`RoslynSentinel.Engines.Basic/TestRunEngine.cs:426`).
3. The test instance is a child process created with `ProcessStartInfo` in `RoslynSentinel.Engines.Basic/TestInstanceLauncher.cs` (`LaunchFromOutputAsync`, about lines 88-175). It sets `WorkingDirectory`, redirects streams and removes only the MSBuild-pinned variables (`MsBuildPinnedVariables`, lines 34-38). It does not set or clear `ROSLYNSENTINEL_INSTANCE_ID`, so the child inherits the parent's value. `scripts/roslynsentinel-mcp-launch.ps1:749` is what exports that variable to the parent. The child's `ResolveInstanceId()` therefore returns the parent's id, and its own `Build` / `RunTest` target the same `obj/` and `bin/` as the parent.
4. The child's executable is taken from the parent's throwaway output: `GetServerOutputDirectory` (`TestInstanceLauncher.cs:508-509`) is `<buildRoot>/<ResolveInstanceId()>/bin/RoslynSentinel.Server.Advanced/debug`, and `ResolveExecutable` (`TestInstanceLauncher.cs:290-309`) launches the apphost `.exe` from there. So a live process holds the exe and loaded dlls open inside the directory every later `Build` / `RunTest` of the parent (and of the child itself) rewrites.
5. Nothing in the build path guards against this. `BuildEngine.cs:215` takes `BuildGate.Shared`, which is in-process only (`BuildIsolation.cs:337-395`). The cross-process wait `WaitForNoCompetingBuildAsync` is skipped for throwaway builds (`BuildEngine.cs:223-224`: "A throwaway build is isolated"), and it only matches `dotnet`/`msbuild` processes anyway (`BuildIsolation.cs:145-169`), not a running server exe. That "isolated" premise no longer holds once a test instance shares the id.
6. Two servers building at once into the one directory (steps 2 and 4 above) explains the HTTP-side failure; the stdio-side CS2012 on a `Common.dll` under `obj/` and the HTTP-side `apphost.exe` delete denial fit either a lock held by the running exe or the two concurrent builds colliding. Both candidates share the same cause: one artifacts directory for two servers.

## What is and is not confirmed

Confirmed:
- Child and parent resolve the same instance id and artifacts directory (items 1-4; the HTTP result detail also reported the parent's id).
- The child runs its exe out of that directory's `bin/` (item 4; `exePath` in the launch response).
- Build succeeded for the parent once the test instance was stopped, and succeeded repeatedly before any test instance existed.
- `LaunchTestInstance` gives no warning that `Build` / `RunTest` will fail while the instance is alive. This is from the observed launch response and the code read above (no such warning in `LaunchAsync` / `LaunchFromOutputAsync`); I did not read `AdminTools.cs` response text line by line.

Not confirmed (hypotheses):
- That Part 2 hard links (`Directory.Build.props:16-18`: `CreateHardLinksForCopyLocalIfPossible`, `CreateHardLinksForCopyFilesToOutputDirectoryIfPossible`, `CreateHardLinksForAdditionalFilesIfPossible`) tie the running `bin/` file to the `obj/` file, so `obj\...\RoslynSentinel.Common.dll` and `obj\...\apphost.exe` are locked as well. `fsutil hardlink list` on `obj\RoslynSentinel.Server.Advanced\debug\apphost.exe` showed a single path; on the `bin/` exe it errored (Error 2). Inconclusive.
- Which of the two failures was caused by the running exe lock versus the two concurrent builds. The two `Build` calls were made at the same time, so the run does not separate them. Not re-run with a single `Build` against a live test instance.

## Why this matters

The advertised workflow for live-testing uncommitted server changes is `Build` then `LaunchTestInstance`; the natural next step is to edit, `Build` and `RunTest` again while the instance is up. That fails with raw compiler / MSBuild errors (CS2012, MSB4018) that name a file path but not the cause (a live test instance sharing the artifacts directory). A model or human sees a build failure in code that did not change and has no pointer to `StopTestInstance`. The throwaway-build design promises that parallel work does not collide; `LaunchTestInstance` silently breaks that promise.

## Suggested direction (not implemented)

Options, not mutually exclusive:

- (a) Give the test instance its own instance id and artifacts directory. For example, set `ROSLYNSENTINEL_INSTANCE_ID` explicitly in `LaunchFromOutputAsync` to something derived from the working dir (`<parentId>-t<n>`), or finish the `// TODO Part 3` so the id comes from the `--working-dir` leaf. The child's own `Build` / `RunTest` then go to a separate directory.
- (b) Launch the test instance from a copy of the output (a snapshot directory under the test instance's working dir) rather than from the parent's `bin/`, so no running process holds files in the directory the parent rebuilds. Hard-linking the copy would reintroduce the sharing, so it must be a real copy.
- (c) Until then, make `LaunchTestInstance` say so in its response ("Build and RunTest on this server will fail until StopTestInstance"), and make `Build` / `RunTest` detect a live test instance (`TestInstanceLauncher.List`) and return a structured error that names `StopTestInstance` instead of surfacing CS2012 / MSB4018.
- (d) Add a Done-when test: with a test instance running (launched from the parent's output), call `Build` on the parent and assert it succeeds (or fails with the structured error above), and call `Build` on the child as well.

Workaround: call `McpServerControl(operation: StopTestInstance)` before any `Build` or `RunTest`, then relaunch with `build: Skip` or `Auto` afterwards.

Next step to unblock the open hypothesis: reproduce with one `Build` on the parent only (no concurrent child build), with the test instance alive, then check `fsutil hardlink list` on the `obj/` and `bin/` files while the instance runs (the `bin/` exe query failed with Error 2 and needs the correct path).
