# Blocking error — `PersistentWorkspaceManager.Dispose()` races `OnDebounceTimerElapsed`, crashes the process

**Status:** OPEN — reported per docs/current/feedback_dogfood_mcp_blocking_errors.md, waiting for
fix/confirmation. Found while writing Battery tests for the new `RunTest` tool
(docs/current/plan-runtest-tool-v1.md Task 3) — not a bug in the tool being added, but hit directly
by that work.

## Symptom

`dotnet test` against `RoslynSentinel.Tests.Battery` aborted the entire test run with an unhandled
`ObjectDisposedException` thrown from a background thread pool thread, crashing the testhost
process. This is not a per-test failure — it kills every remaining test in the run.

```
Unhandled exception. System.ObjectDisposedException: Cannot access a disposed object.
Object name: 'System.Threading.SemaphoreSlim'.
   at System.Threading.SemaphoreSlim.Release()
   at RoslynSentinel.Common.PersistentWorkspaceManager.OnDebounceTimerElapsed(Object state) in
     C:\Users\Administrator\source\repos\RoslynSentinel\RoslynSentinel.Common\PersistentWorkspaceManager.cs:line 877
The active test run was aborted. Reason: Test host process crashed
```

## Root cause (read, not yet fixed)

`RoslynSentinel.Common/PersistentWorkspaceManager.cs`:

- `Dispose()` (line 1755) sets `_disposed = true`, then disposes `_workspace`, `_watcher`,
  `_debounceTimer`, and finally `_solutionLock` (line 1766) — with **no lock held during `Dispose()`
  itself**, and disposing a `System.Threading.Timer` does not block on or cancel an
  already-in-flight callback.
- `OnDebounceTimerElapsed` (line 725, `async void`) checks `if (_disposed) return;` at entry (line
  727) — a plain check-then-act race, not synchronized with `Dispose()`.
- If the timer callback's entry check passes and it successfully completes
  `await _solutionLock.WaitAsync()` (`acquired = true`), but `Dispose()` runs concurrently and
  reaches `_solutionLock.Dispose()` before the callback's `finally` block runs, the `finally`'s
  unconditional `_solutionLock.Release()` (line 877) throws `ObjectDisposedException` **from inside
  the `finally` block** — which is not caught by the method's own `catch (ObjectDisposedException)`
  (line 862) or `catch (Exception ex)` (line 866), since those only guard the `try` body, not
  `finally`. Because the method is `async void`, this exception has no caller to propagate to and
  crashes the process instead.

Trigger conditions: a `PersistentWorkspaceManager` with an active file-system watcher (i.e. one that
has had `LoadSolutionAsync` reload/watch a real on-disk solution) is `Dispose()`d while a debounced
file-change timer callback is at any point past its `_disposed` check but not yet past its
`_solutionLock.Release()`. In my repro this happened because `TestSolutionFixture.Dispose()` deletes
the temp solution directory, which itself races the still-open `dotnet test` subprocess file handles
(a second, separate `IOException` symptom logged alongside this one — see below) — real on-disk
churn right around the same window `using var workspaceManager = new PersistentWorkspaceManager(...)`
goes out of scope and disposes it, which is exactly the kind of timing a real filesystem watcher
plus real subprocess activity produces. Not reproduced from a synthetic/forced race — occurred
directly from normal fixture teardown timing under `RunTest`'s new real-`dotnet test`-subprocess
Battery tests.

## Impact

Any test (or real MCP session) that disposes a `PersistentWorkspaceManager` shortly after a
filesystem change it's watching can crash the whole process, not just fail one operation. For the
Battery test suite specifically, this aborts the entire `dotnet test` run for the assembly, not just
the one test — every test after the crashing one is reported as not-run rather than failed.

## Secondary symptom noticed alongside this (same repro, likely related but distinct)

`TestSolutionFixture.Dispose()` (RoslynSentinel.Tests/TestSolutionFixture.cs:47,
`Directory.Delete(SolutionDirectory, recursive: true)`) intermittently throws
`System.IO.IOException: The process cannot access the file 'ContosoOrders.Core.csproj' because it is
being used by another process.` This looks like a `dotnet test` subprocess (spawned by `RunTest`'s
`TestRunEngine` against files inside the fixture's temp directory) not having fully released its
file handles by the time the fixture's `using` block disposes and deletes the directory immediately
after `await`ing `RunTest`'s completion. Filed here rather than as a separate blocker since both
symptoms come from the same repro and may share a root timing cause (subprocess/watcher activity not
fully quiesced before teardown) — but they are two distinct code paths (`TestSolutionFixture.Dispose`
vs `PersistentWorkspaceManager.OnDebounceTimerElapsed`) and may need two separate fixes.

## What I did NOT do

Did not patch `PersistentWorkspaceManager.cs`'s disposal race myself to unblock — this is exactly the
kind of underlying-tool defect the dogfood policy asks to be paused on and reported rather than
silently worked around, since a wrong fix here (e.g. blindly wrapping `Release()` in a try/catch)
could mask a real double-release or lock-imbalance bug rather than fix the disposal ordering. Did not
delete or rewrite the failing `RunTestTests.cs` test to avoid hitting this — the test scenario
(FailureSummary grouping across two injected test files) is a legitimate, plan-specified scenario;
removing it would just hide the crash instead of fixing it.

## Next step

Waiting for confirmation/fix before resuming Task 3's remaining test runs. Once resolved, move this
file to `docs/obsolete/blockers/`.
