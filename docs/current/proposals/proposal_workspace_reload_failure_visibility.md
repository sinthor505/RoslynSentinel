# Make a failed workspace load/reload a sticky, observable STALE state instead of a log line

**Status:** OPEN 2026-10-21. Proposal for review; nothing built. Line numbers cite the working tree of 2026-10-21, which was being edited by the #32 fix at the time, so they may shift.

## Motivation

A real `.csproj` edit reached the per-project reload branch of
`PersistentWorkspaceManager.OnDebounceTimerElapsed`, which called `_workspace.OpenProjectAsync` on a
project that was already open. Roslyn threw `ArgumentException: '<project>' is already part of the
workspace`. The single outer catch logged `"ErrorData refreshing workspace."` and nothing else
(`RoslynSentinel.Common/PersistentWorkspaceManager.cs:1210`). The workspace stayed stale, and neither an
agent nor any status field could tell. It was found only because a test captured the log. The
trigger itself (per-project reload) is being deleted by the #32 fix: in the working tree a changed
`.csproj` now sets `solutionNeedsReload` (`:1119-1126`) and takes the full reload. This proposal is
about the class of defect, not that one trigger: **every load/reload path can fail without the
agent ever learning the workspace no longer matches disk.**

Environment framing: an agent that keeps editing against a stale solution gets plausible-looking
answers (stale compilation, mass CS0234/CS0246 after a bad swap) and cannot diagnose them. The fix
belongs in the server (a state the agent can see), not in agent behaviour.

### Inventory of failure paths (verified against the working tree)

| # | Path | What happens on failure | Visible to agent? |
| --- | --- | --- | --- |
| 1 | `LoadSolutionAsync` `:619` | Disposes the OLD `_workspace` and creates a new one BEFORE opening (`:645`, `:649`); clears `_compilationCache` and `_workspaceLoadErrors` (`:656-657`). A non-cancel `OpenSolutionAsync` throw (`:669`) is logged and the partial solution kept (`:685-687`); `ToolNotFoundException` only when 0 projects (`:724`). Cancel/timeout rethrows with `CurrentSolution` still the OLD snapshot while `_workspace` is the NEW empty one. `finally` (~`:750-757`, `terminal = Loaded` at `:753`) reports `Loaded` whenever any project exists, so a partial load is `Loaded`. | Explicit call: `LoadSolution` wrapper returns `IsError` (`WorkspaceProjectManagementImpl.cs:377-395`). Partial load: only `GetWorkspaceLoadErrors()` (`:1341`, pull). |
| 1b | Startup auto-load | Fire-and-forget; `ContinueWith(OnlyOnFaulted)` only logs (`Server.Basic/ServiceRegistrationExtensionsBasic.cs:417-423`; `Server.Advanced/ServiceRegistrationExtensionsAdvanced.cs:275`). | `LastLoadFailure` is appended to the not-loaded message only when `IsFreshStartup` (`SolutionLoadState.cs:93-106`). `McpServerStatus` has no `LastLoadFailure` field (`ServerStatusTools.cs:84-98`, record `:~190-212`). |
| 2A | `OnDebounceTimerElapsed` full reload (`:1079`; overflow, `.sln`/`.slnx`, and now `.csproj`) | Builds a fresh `MSBuildWorkspace`, swaps only on success (`:1144-1166`), old one kept valid on failure. Good design, but stale and silent: outer `catch (Exception)` logs and swallows (`:1206-1212`). | No. |
| 2A' | Same branch, empty `slnPath` | `if (!string.IsNullOrEmpty(slnPath))` has no `else` (~`:1137`): silent no-op. | No. |
| 2C | Debounce `.cs` fold fallback | If the fold asks for a full reload, reuses the existing `_workspace` for `OpenSolutionAsync` (`:1191`) - the exact pattern the comment at `:1137-1143` says leaves a half-reloaded workspace. In the debounce path it is effectively dead (any `.csproj`/`.sln`/`.slnx` already took 2A), but it is reachable code. | No. |
| 2x | Pending state | `_pendingChanges` and `_watcherOverflowed` are cleared BEFORE the reload (`:1091-1094`), so a failed reload is never retried; the next event may never come. | No. |
| 3 | `ApplyInMemoryDocumentUpdatesAsync` `:2012` | Per-file read failure is warned and skipped (silent partial fold, ~`:2060`). Only `.csproj`/`.sln` set `needsFullReload` (`:2025`); `.slnx` does not. `workspaceInSync = !needsFullReload` (`:1927`) stays `true` after a skipped file. | `WorkspaceInSync` can be wrongly `true`. |
| 4 | `ApplyProposedChangesAsync` post-write sync `:1917-1960` | A throw is logged "Workspace may be stale; call LoadSolution to resync." (`:1958`). | Only `ApplyChangesResult.WorkspaceInSync=false` (`:1972`). |
| 5 | `ApplyProposedChangesAsync` `finally` `:1980-1996` | When `needsFullReload`, `Task.Run(ReloadWorkspaceFromDiskAsync)`; a failure is logged only ("Background workspace reload failed."). | `needsFullReload` is not in `ApplyChangesResult`; `WorkspaceInSync=false` is the only hint, and it never flips back. |
| 6 | `ReloadWorkspaceFromDiskAsync` `:2111` | Builds fresh workspace first, swaps on success; on failure logs and returns normally, old workspace kept, silently stale. Uses `SolutionPath` (not `CurrentSolution.FilePath`) and does NOT call `RecordStructuralFileHashes` (unlike 2A, `:1164`). | No. |
| 7 | `OnWatcherError` `:934` | Sets `_watcherOverflowed` (`:941`) and arms the timer, so it becomes path 2A; a failure there is silent. | No. |

Other facts that shape the design:

- **Reads and writes serve a stale-but-non-null `CurrentSolution` as normal.** `GetCurrentSolutionAsync`
  (`:1369`) throws only when `CurrentSolution` is null. Nothing consults `Failed` or a failed-reload flag.
- **`SolutionLoadStatus` is set only in `LoadSolutionAsync`'s `finally`** (`EndLoadTracking`, `:222-239`).
  No debounce or background path touches `_terminalLoadStatus` / `_lastLoadFailure`.
- **`McpServerStatus` reports** `SessionHalted`, `IsFreshStartup`, `SolutionLoadStatus`, `ProjectCount`,
  `WorkspaceVersion`, breakers (`ServerStatusTools.cs:89-98`); not `LastLoadFailure`, not staleness.
- **`GetWorkspaceStatus`** (`:~1480`) computes staleness as file mtime > `_lastLoadedAt` (`:1509-1510`), pull
  only, and `_lastLoadedAt` is bumped by in-memory folds (`ApplyInMemoryDocumentUpdatesAsync` end) so it can
  mask staleness. The 2A full reload bumps neither `_lastLoadedAt` nor `_workspaceVersion` (verified by
  reading `:1156-1176`), so a successful 2A can leave `RequiresReload: true` - a false positive, the mirror image.
- **"call LoadSolution" is not actionable as written.** The `LoadSolution` wrapper returns "already loaded -
  no changes made. Pass forceReload:true ..." when the path matches the loaded one and `forceReload` is false
  (`WorkspaceProjectManagementImpl.cs:343-351`). The log text at `:1958` that tells a caller to "call
  LoadSolution" would, if it ever reached an agent, send it to a no-op. Any recovery message must say
  `forceReload: true`.
- **Precedent for central result stamping exists.** `HaltStamp` (`Server.Basic/HaltStamp.cs`) stamps
  `isSessionHalted` / kind / reason / recovery onto the first text block of every tool response, driven by
  `HaltInfo.From` (`Common/HaltInfo.cs:~62`) in a call-tool filter (`ServiceRegistrationExtensionsBasic.cs:889`,
  registered `:103`). A workspace-staleness stamp can reuse that mechanism. Whether `Server.Advanced` gets
  the same filter is unverified.
- **Latent ordering hazard in 2A (hypothesis, not reproduced).** `_workspace = newWorkspace` is assigned
  (`:1161`) before `RecordStructuralFileHashes` and `CurrentSolution = newSolution` (`:1164-1165`), while the
  `catch` disposes `newWorkspace`. If anything between throws, `_workspace` is a disposed instance and
  `CurrentSolution` is the old one. `RecordStructuralFileHashes` catches only IO/Unauthorized exceptions
  (`:524-545`); whether another type can escape is unverified. The helper below removes the ordering.
- **`.props`/`.targets`/`Directory.Build.props` are not watched at all** (`WatchedFilePatterns` `:788`;
  extension gate `:948`), so a change to them is a different silent-stale class: no event, no reload, no
  failure to record. This corrects the earlier assumption that they were routed into the `.cs` fold path.

## Proposal

### Principle (recommended)

A failed refresh must become a **sticky, observable STALE state**, never a log line. The user's literal
ask was "unload the solution/project on exception". That is not the default (see Alternatives): the full-reload
code deliberately keeps the old workspace on failure because a half-reloaded workspace caused mass
CS0234/CS0246 (comment `:1137-1143`), and an unload discards the loaded solution, caches and hash
baselines. Unload is reserved for the case where the manager is internally inconsistent (below).

### 1. A reload-failure record on the manager

One field, e.g. `WorkspaceReloadFailure? _reloadFailure`, set by every catch in the inventory, cleared by any
successful full load or reload (explicit, debounce, post-write background). Shape (names open):

- `Trigger` kind: `ExplicitLoad`, `StartupLoad`, `SolutionFileChange`, `ProjectFileChange`, `WatcherOverflow`,
  `WatcherError`, `PostWriteSync`, `PostWriteReload`.
- `TriggerFile` (file name only), `FailedAtUtc`, `Attempts`, `OldWorkspaceKept` (bool), and a **sanitized
  one-line cause** (exception type plus a short mapped reason). Full exception text stays in the log only.
  Today `failure = ex.Message` (~`:743-745`) is stored raw and later echoed into the not-loaded message
  (`SolutionLoadState.cs:104-106`); MSBuild messages routinely carry absolute paths, which CLAUDE.md forbids in
  a tool result. Fixing that is part of slice 1.

### 2. Surface it

- **`McpServerStatus`**: add `WorkspaceStale` (bool), `ReloadFailure` (the record), and `LastLoadFailure`
  (already stored, never reported). Cheap, pull-based, needs no policy decision.
- **Reads**: stamp a `workspaceStale` warning (reason + recovery) on tool results, via a filter modelled on
  `HaltStamp`. Reads still succeed.
- **Mutations**: recommended refusal at the write chokepoint (`ApplyProposedChangesAsync`, next to the
  `_sessionHalted` check at `:1550`), with an actionable message: what failed (sanitized), that the workspace
  is stale, and `LoadSolution(forceReload: true)`. Target exemption: writes whose targets are
  `.csproj`/`.sln`/`.slnx` must still go through, otherwise an agent cannot repair the broken project file that
  caused the failure (deadlock). Options considered: warn-only; refuse everything; refuse except structural
  targets (recommended). Decision is open (see Open questions).
- **Recovery**: a successful `LoadSolution(forceReload: true)` clears it. Whether a later successful *debounce*
  reload also clears it is open.
- **Startup auto-load failure**: report through the same record/`McpServerStatus` fields so it is visible after
  a fresh start AND not only in the fresh-start-gated message.

### 3. One build-new-then-swap helper

Today four places hand-roll the same sequence with different omissions. Introduce one private helper that
builds a fresh `MSBuildWorkspace`, opens the solution, and on success performs, in one place and in a safe
order: assign `CurrentSolution`, `RecordStructuralFileHashes`, clear `_compilationCache`, bump
`_workspaceVersion` and `_lastLoadedAt`, prune `_internalChanges`, swap `_workspace`, dispose the old one. On
failure it disposes only the new instance, records the failure, and leaves the old workspace untouched.
Callers: debounce 2A, `ReloadWorkspaceFromDiskAsync`, the 2C fallback (deleted in favour of a re-queue), and
`LoadSolutionAsync` (which today disposes the OLD workspace first, `:645`, so a failed explicit `LoadSolution`
can lose a good solution; with the helper a failed reload keeps the old one and returns the failure).
`LoadSolutionAsync` keeps its extra duties (`SetupWatcher`, `ClearExternalFileChanges`,
`PopulateKnownFileHashes`, `SolutionPath`) around the helper.

### 4. Re-queue and one automatic retry

Capture the failed trigger (changes list plus overflow flag) instead of dropping it at `:1091-1094`, and retry
it once automatically after a short delay before latching the STALE state. Reason: a transient failure (a
`.sln` mid-write during a `git checkout`, the case the `:1137-1143` comment names) should not block writes.
Retry count is open; it must stay bounded because every attempt holds `_solutionLock` for a full MSBuild load.

### 5. When to unload (narrow)

Unload (null `CurrentSolution`, dispose `_workspace`, report not-loaded with the failure) only when the manager
is **inconsistent**, i.e. `_workspace` and `CurrentSolution` no longer describe the same load: the
`LoadSolutionAsync` cancel/timeout case (row 1) and the 2C fallback. With the unified helper the first
largely disappears (the old workspace is kept until the new one succeeds). Everywhere else keep the old valid
workspace and mark it stale. On the "discards in-memory edits" concern: the comment at `:1180-1186` says
folded text lives only in the manager's `CurrentSolution`, but the write chokepoint writes disk first and then
folds (`:1917-1927`), so unwritten in-memory-only text should be rare; whether any exists outside test
scaffolding (`SetTestSolution`) is unverified.

### 6. Smaller defects to track as follow-ups (separate slices or TODO lines)

- `.slnx` does not set `needsFullReload` in `ApplyInMemoryDocumentUpdatesAsync` (`:2025`), although the
  debounce path treats `.slnx` as a solution reload (`:1113`).
- Per-file fold read failures are silent partial folds, and `workspaceInSync` stays `true`.
- `needsFullReload` is not exposed in `ApplyChangesResult` (it is only inferable from `WorkspaceInSync`).
- `ReloadWorkspaceFromDiskAsync` uses `SolutionPath` and skips `RecordStructuralFileHashes` (covered by the helper).
- 2A empty-`slnPath` silent no-op (`~:1137`).
- `_lastLoadedAt` masks staleness; 2A does not bump it or `_workspaceVersion`.
- `.props`/`.targets`/`Directory.Build.props` changes are not watched; decide whether to watch them and route them to 2A.
- A concurrent `ReloadWorkspaceFromDiskAsync` opens outside the lock and swaps later (`:2111`+), so edits folded
  into `CurrentSolution` in between are replaced by the disk reload. Disk should hold them (writes go to disk
  first); unverified for edge cases.

### 7. Slices (migration-first, each compile-green)

1. **Failure record plus `McpServerStatus` fields**, set from the existing catches (2A, 4, 5, 6, 7, startup
   auto-load, `LoadSolutionAsync`). Also sanitize the stored cause. No behaviour change besides state.
2. **Unified reload helper**; move paths onto it one slice per path (2A, then `ReloadWorkspaceFromDiskAsync`,
   then `LoadSolutionAsync`), old code kept beside until each caller moves; delete the 2C fallback last.
3. **Result-level stale stamp and mutation refusal** (structural-target exemption), with the recovery text
   naming `forceReload: true`.
4. **Re-queue plus bounded automatic retry.**

### 8. Tests and the fault-injection seam

No fault seam exists today. The existing debounce tests (`RoslynSentinel.Tests.Tools.Basic/CreateFileDeleteFileTests.cs:436-473`
project reload; `:491` fold) build a real `PersistentWorkspaceManager` with `NullLogger` over
`TestSolutionFixture`, poll private fields by reflection (`_knownFileHashes`) and `Task.Delay`/poll for the
500 ms debounce. They cover the success path of 2A (hash re-baseline after a `.csproj` change) and the 2C fold;
they say nothing about any failure path, so a green run of them is not evidence about this proposal. The
constructor takes `ILogger` (`:259`), so a capturing logger can assert the log text, but that asserts the
symptom this proposal wants to stop depending on. Needed:

- Slice 1-2: a way to make the open throw deterministically. Preferred: an internal `Func<string, CancellationToken, Task<...>>`
  open delegate on the manager (default = real MSBuild), settable by tests, so tests can throw, throw-then-succeed
  (retry), and cancel. A seam-free alternative worth trying first: delete or lock the fixture `.sln` before the
  debounce fires (whether `OpenSolutionAsync` then throws, rather than loading nothing, is unverified).
- Assert on state, not logs: record populated, `McpServerStatus.WorkspaceStale`, mutation refused with the
  documented code, structural-target write allowed, cleared after a successful reload.
- Planner owns stating that these must cover each path separately (2A, 4/5/6, 1, startup); reviewer confirms
  the tests hit the changed path and not only the shared helper.

## Alternatives considered, not pursued

- **Literal unload on any exception (the user's ask).** Rejected as the default: it turns a transient `.sln`
  mid-write into total loss of the solution, caches and baselines (agents then see "No solution is loaded" with
  no cause), and it contradicts the deliberate keep-old-on-failure design at `:1137-1143`. Kept for the
  inconsistent-manager cases only.
- **Better logging.** Rejected: the log is exactly what failed here; agents do not read it.
- **Rethrow from the `async void` handler.** Rejected: an unhandled exception in `OnDebounceTimerElapsed` is
  process-fatal (the `Dispose` comment at `:2228-2237` describes the same hazard).
- **Rely on the existing pull status (`GetWorkspaceStatus`/`GetWorkspaceHealth`).** Insufficient: pull only, mtime
  based, masked by `_lastLoadedAt`, and a failed reload with no later edits shows nothing.
- **Reuse `SessionHalted`/`HaltInfo` as the mechanism.** Attractive (stamp, recovery text, `ExternalFileDrift`-style
  acknowledge already exist) but it halts all mutations unconditionally at `:1550` and its recovery is an
  acknowledge of drift, not a reload; staleness should clear itself on a successful reload. Open whether to add a
  new `HaltInfo` kind that reuses only the stamping (see Open questions).
- **Unbounded or periodic auto-retry.** Rejected: each attempt is a full MSBuild load under `_solutionLock`; a
  permanently broken `.csproj` would starve every other caller.

## Open questions

1. Refuse vs warn on mutate (recommended: refuse, except `.csproj`/`.sln`/`.slnx` targets). Should reads also be refused for a latched failure of kind `ExplicitLoad`/`StartupLoad` with no usable solution (that case already returns not-loaded)?
2. Automatic retry count and delay (recommended: 1 retry, a few seconds).
3. Should a later successful debounce/background reload clear the latch? (Recommended yes: it is the same operation as `LoadSolution(forceReload)`.)
4. Separate latch, or reuse `SessionHalted` machinery (`HaltInfo` kind, stamp) with its own recovery? Relation to `plan_session_halt_recovery_and_git_gaps.md` to be settled with the owner.
5. Do `.props`/`.targets`/`Directory.Build.props` belong in the watcher? Separate decision, bigger blast radius (more reloads).
6. Does `Server.Advanced` register the same halt-stamp filter chain as Basic (`AddHaltStampFilter`)? Unverified.

## Cost / risk

- Touches `PersistentWorkspaceManager.cs` (about five reload paths, the largest hot file; concurrent sessions edit it),
  `McpServerStatus` DTO/schema (`ServerStatusTools.cs`), a new filter in `Server.Basic`, `ApplyProposedChangesAsync`
  (the write chokepoint) and `ApplyChangesResult` if `needsFullReload` is exposed.
- Refuse-on-mutate makes a flaky reload block writes until a successful reload; mitigated by the structural-target
  exemption, the bounded retry and self-clearing on a later success. A wrong latch is itself a new silent-failure mode,
  so the stamp must always carry the recovery text.
- The unified helper changes `LoadSolutionAsync` ordering (old workspace disposed last, not first): peak memory holds two
  workspaces during a load; a failed reload now leaves the old solution loaded where it used to be replaced by a
  partial one. Both are intended, but any test or tool that relies on "failed load -> empty/partial solution" needs a look.
- Stamping every result adds bytes only while stale; no cost in the healthy state.

## Related

- `docs/current/plans/plan_session_halt_recovery_and_git_gaps.md` (halt recovery, `ExternalFileDrift` acknowledge).
- `docs/current/issues/issue_tool_hygiene_open_items.md` (adjacent small tool-hygiene defects).
- `docs/current/plans/plan_external_file_drift_tool_and_halt_stamping.md` (source of the `HaltStamp` pattern).
- #32 (per-project reload removal, routing `.csproj` to the full reload, `RecordStructuralFileHashes`), in flight.
- Memory: `reference_central_mcp_request_filter_pipeline.md` (where result transforms live),
  `project_externaldrift_false_positive_cross_check_gitstatus.md`, `project_concurrent_sessions.md`.
