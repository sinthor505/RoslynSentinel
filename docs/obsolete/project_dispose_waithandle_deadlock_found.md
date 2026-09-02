---
name: project_dispose_waithandle_deadlock_found
description: "Today's PersistentWorkspaceManager.Dispose() crash fix (commit 14f8229) added a blocking Timer.Dispose(WaitHandle) wait that can itself deadlock indefinitely against _solutionLock contention, converting a fast crash into a silent multi-hour hang affecting both Battery and ModelEval tests; fixed by removing the blocking wait (it was redundant with an already-present guard)"
metadata:
  node_type: memory
  type: project
  originSessionId: baae58f2-ea41-48a8-b6da-6d65bc32d78d
---

## What happened

The 20-run `PlanImplementVerify` batch against `.113` hung twice in the same batch (run 3, then
run 5) — each time a `testhost.exe` process sat alive for 70-100+ minutes making zero progress,
while `curl` confirmed the LM Studio host and model were fully responsive throughout (sub-second
round trips). This is the same symptom as [[project_modeleval_testhost_hang_gotcha]], but the user
noted it started **recently** and **also affects `RoslynSentinel.Tests.Battery`** — a correlation
across two otherwise-unrelated test projects pointed at something shared between them, not a
test-specific issue.

A debugger screenshot the user attached while a testhost was stuck showed the concrete mechanism:
thread 4 `Active` in `SocketPal.Poll` under `PlanImplementVerifyAgentTests.RunPhaseAsync`, with
other threads `Blocked`/`Awaiting` on it — consistent with, but not sufficient on its own to prove,
a deadlock rather than a genuinely slow remote call.

## Root cause: commit `14f8229`'s crash fix traded a fast crash for a silent hang

`git log` on `PersistentWorkspaceManager.cs` (the one class every test — Battery's `RunTest` and
ModelEval's agent loop alike — constructs and disposes) showed `14f8229` ("Fix
PersistentWorkspaceManager.Dispose() race that crashed the process, add RunTest Battery tests"),
committed **today** (2026-09-01 03:15), as the most recent change to shared disposal logic — timing
that matches "this only started recently."

The original bug (see `docs/obsolete/blockers/blocking_error_persistentworkspacemanager_dispose_race_crashes_process.md`):
`OnDebounceTimerElapsed`'s `finally` block called `_solutionLock.Release()` unconditionally; if
`Dispose()` ran concurrently and reached `_solutionLock.Dispose()` first, `Release()` threw
`ObjectDisposedException` **from inside a `finally` block on an `async void` method** — unobservable,
fatal, crashed the whole test process.

The fix commit did two things:
1. Wrapped that specific `_solutionLock.Release()` call in `try/catch (ObjectDisposedException)` —
   this alone closes the crash, regardless of timing, since it guards the exact throwing call.
2. **Also** added a blocking wait in `Dispose()`: `_debounceTimer.Dispose(waitHandle);
   waitHandle.WaitOne();` — intended as extra insurance, waiting for any in-flight
   `OnDebounceTimerElapsed` callback to fully finish before tearing down `_workspace`/`_solutionLock`.

Item 2 is the new deadlock. `OnDebounceTimerElapsed` starts with `await _solutionLock.WaitAsync()`
— if `_solutionLock` is already held by unrelated, slow, in-progress work (a `LoadSolutionAsync`,
`ApplyProposedChangesAsync`, or any other of the lock's 4 other acquisition sites in this file) when
the file watcher's debounce timer fires, the callback blocks waiting for that lock. The watcher's
timer (`_debounceTimer.Change(500, Timeout.Infinite)`) re-arms on **every file system write** —
meaning any file the agent creates/edits mid-LLM-turn schedules a callback that then queues up
behind the same lock the in-progress operation is holding. If `Dispose()` (test `[TearDown]`, or a
`using` scope ending) runs while that lock is still held by slow work, `Dispose()` now blocks
unconditionally on `waitHandle.WaitOne()` waiting for a callback that can't proceed until the lock
is released — turning what used to be an instant (if fatal) crash into a wait with no timeout at
all, silent for as long as the lock's holder takes (in the observed case, well over an hour, since
the LLM call itself was also part of the critical section timing in these tests).

**This is not a synthetic race** — it's the natural consequence of instrumenting file-write-heavy
agent loops (both Battery's `RunTest`, which invokes real `dotnet test` subprocesses that touch
disk, and ModelEval's `CreateFile`/`ApplyDiff`-heavy `RunPhaseAsync`) against a workspace manager
whose file watcher debounces on exactly that write traffic.

## The fix

Removed the blocking `_debounceTimer.Dispose(waitHandle); waitHandle.WaitOne();` from `Dispose()`
(`RoslynSentinel.Common/PersistentWorkspaceManager.cs`), reverting to a plain `_debounceTimer.Dispose()`.
The already-present `try/catch (ObjectDisposedException)` around `_solutionLock.Release()` in
`OnDebounceTimerElapsed`'s `finally` block is sufficient on its own to prevent the original crash —
it catches the exact exception that used to be fatal, independent of timing relative to
`Dispose()`. No other change needed; item 1 above was always the real fix, item 2 was unnecessary
defense-in-depth that introduced a worse failure mode than the one it additionally guarded against.

## Verification

- `dotnet build RoslynSentinel.slnx -c Debug` → 0 errors.
- `dotnet test RoslynSentinel.Tests.Battery --filter "FullyQualifiedName~RunTestTests"` → 10/10
  passed (the exact tests that originally reproduced the crash in `14f8229`) — confirms the crash
  fix still holds after removing the blocking wait.
- Full `RoslynSentinel.Tests.Battery` run in progress to confirm no other regression.

## How to apply

If a `System.Threading.Timer`/`SemaphoreSlim` disposal-ordering fix is ever proposed again for this
or a similar shared-lock class, check whether the *crash-causing* exception can be guarded directly
at its throw site (a narrow `try/catch` around just that call) before reaching for a blocking wait
on the callback's completion — a blocking wait's safety is only as good as the *worst-case* duration
of whatever that callback is contending on, which for a lock shared with slow external calls (LLM
round-trips, subprocess builds) can be unbounded. Prefer the narrowest guard that closes the actual
crash over an unconditional wait that trades a visible failure for an invisible one.
