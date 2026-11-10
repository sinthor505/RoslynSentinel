# Reference: file watcher, external-drift flagging and per-file write protection

**Status:** CURRENT 2026-11-09. Verified against `RoslynSentinel.Common/PersistentWorkspaceManager.cs`, `Common/CrossProcessWriteLock.cs`, `Common/NotFoundEvidence.cs` and the `Tools.Basic` read tools after plan_stale_read_guard_followups steps 1-9h (anchor and engine tools opted in, outline/InspectSymbol recording, create-on-write, `FoldPending` guard, drift halt removed, cross-process write lock, debounce fold under the lock, fold-on-read, zero-hit Search hint). Sections 3-9 describe the model AS IMPLEMENTED; sections 7-9 are new (lock, fold-on-read, zero-hit hint). Symbols cited by name because line numbers shift.

## Purpose
How the server notices that a source file changed outside its own write path, what protects a write
from overwriting such a change (per-file guards plus a cross-process write lock), how the read tools
catch up with disk before answering, and what `ExternalFileDrift` still does. Consult it before touching the
watcher, the drift flagging, the write guards, the write lock, the read-tool fold or `ExternalFileDrift`, or when a `StaleRead` /
`FoldPending` / `WriteLockBusy` / "files changed externally" report looks wrong. History: until 2026-11-09 a drift hit on
a write target latched the whole session (`SessionHalted`); that latch is gone (section 4).

## How it works
All symbols below are in `PersistentWorkspaceManager` (PWM) unless another file is named.

**1. Watchers**
- `SetupWatcher` watches the solution directory (recursive). `SetupOutOfTreeWatchers` adds one
  recursive watcher per project directory outside the solution directory (nested ones deduped).
- Both use `Filters` built from `WatchedFilePatterns` (`*.cs`, `*.csproj`, `*.sln`, `*.slnx`) and
  `InternalBufferSize = 64 KB`. `NotifyFilter` is `LastWrite | FileName | DirectoryName`.
- A `FileSystemWatcher` cannot exclude subfolders, and on Windows the name filter is applied in managed
  code after the kernel buffer is filled, so a narrower `Filters` list does not shrink buffer pressure.
  The buffer size and cheap early rejection in the handler are what keep overflow down.
- `OnWatcherError` sets `_watcherOverflowed` and forces a full reload; it adds no drift entries.

**2. Handler `OnFileSystemChanged` (order matters)**
1. Extension gate: `.cs`, `.csproj`, `.sln`, `.slnx`.
2. `IsIgnoredWatcherPath`: any DIRECTORY segment equal (case-insensitive) to an entry of
   `IgnoredWatcherDirectoryNames` (`bin`, `obj`, `.git`, `.vs`, `node_modules`, `Worktree`) returns
   immediately. It runs before any file I/O on purpose: slow callbacks delay draining the watcher buffer
   and cause overflow. A file merely NAMED like an ignored folder (`obj.cs`) is not ignored.
3. Content-hash gate (Changed/Created only): if `_knownFileHashes` has a baseline for the path and the
   on-disk SHA-256 matches it, the event is our own echo or a no-op and is dropped. A path locked by
   `FilePathLock` or an `IOException` is treated as our own write in progress.
4. `_internalChanges` gate: an entry written by the chokepoint within the last 5 s whose content still
   matches disk suppresses the event; a tracked delete that is still gone is suppressed. Renamed and
   other Deleted events fall through as real drift.
5. Otherwise the path is added to `_pendingChanges` and `_externalChanges` and the 500 ms
   `_debounceTimer` is re-armed.

**3. Debounce `OnDebounceTimerElapsed`**
Takes `_solutionLock` and drains `_pendingChanges`. A change set for which `RequiresFullReload` is true (a
`.sln`/`.slnx`, a `.csproj`, or a build config file such as `.props`/`.targets`) or a watcher overflow ->
full reload into a fresh workspace, swapped in only on success. The full reload takes NO OS write lock, on purpose: it
reads many files for seconds and must not hold other processes' writers off (rule c, section 7). (An older
version of this section said a `.csproj` change was a project reload; it is a full reload.)
Otherwise (changed `.cs` only): take the cross-process write lock (section 7) with the debounce wait
(2 s) BEFORE reading disk, then `ApplyInMemoryDocumentUpdatesAsync` folds disk text into `CurrentSolution`
and, via `RecordFoldedDocumentBaseline`, refreshes that file's `_knownFileHashes` baseline to the folded text;
the lock is released right after the fold, before the needs-reload handling, and the workspace version
increments. Lock busy: the changes are put back into `_pendingChanges` (and a pending watcher-error flag
restored), the timer is re-armed at `DebounceRetryDelayMs` (3000 ms, not the 500 ms debounce), and the handler
returns without reading disk, without latching and without recording a reload failure. `_debounceRetryCount` is
not touched on that path, so busy retries are not capped (a `// TODO` at the acquire marks the up-to-2-s wait
inside `_solutionLock`). A manager with no lock root (section 7) skips the lock and folds as before. The handler
does NOT touch `_externalChanges`.

**4. The drift list (and the retired latch)**
- `_externalChanges` is filled by step 5 of the handler and read only for visibility: `ExternalFileDrift`
  (`List`, `Status`) and the hint text in `DriftMessages`. The write chokepoint no longer consults it. Until
  2026-11-09 a write or delete target found in it set `_sessionHalted` and threw `SessionHaltedException`,
  which refused every later write by every agent in the session; that block is deleted (a comment marks
  its place in `ApplyProposedChangesAsync`). Drift on a file an agent read or edited is now caught per
  agent by `StaleRead` (section 5); drift on a file the writer never touched is written without a refusal.
- The session halt that remains is the unrecoverable breaker (`IUnrecoverableBreaker`): a separate throw at
  the top of the chokepoint (`throw new SessionHaltedException(unrecoverableHalt)`), no reset, pinned by
  `UnrecoverableBreakerTests`. It does not go through `_sessionHalted`.
- `_sessionHalted` is dead: nothing sets it (the only writer left is `ClearSessionHalt`, which
  `Acknowledge` still calls as a no-op), but its entry check at the top of the chokepoint and its readers
  (`HaltInfo`, which feeds `HaltStamp`; `IdleRestartMonitor`, which feeds `IdleRestartPolicy`;
  `ServerStatusTools`; the `Status` operation of `ExternalFileDrift`) remain and always see false. A `// TODO` at the removed block tracks the
  cleanup slice. Consequence: `ExternalFileDrift(Status)` prints `SessionHalted=False` even during a
  breaker halt (it reads `IsSessionHalted()`, which is `_sessionHalted` only); the breaker is visible through
  the response halt stamp, not through that operation.
- `LoadSolutionAsync` (forced reload) calls `ClearExternalFileChanges()` and `PopulateKnownFileHashes()`, so a
  reload empties the list. (A stale code comment in `LoadSolutionAsync` still says only the clear call
  drains the list; the call right below it is what happens.)
- `GetContentExternalFileChangesAsync` (in-memory vs disk comparison) feeds only the post-call log line
  in `AddDriftFilter` (`Server.Advanced/ServiceRegistrationExtensionsBasic.cs`).

**5. Per-file write protection (non-latching)**
Two checks inside `ApplyProposedChangesAsync`, in this order, after the cross-process write lock (section 7) and
pre-image capture and before the EOL pre-pass: the stale-read check, then the `FoldPending` guard. Both refuse
the WHOLE batch before anything is written or deleted and never latch. (Write path in full, as built: `_solutionLock` ->
`CheckConcurrentEditConflict` -> `RevalidateAfterForeignWriteAsync` -> OS write lock -> pre-image capture -> these two
checks -> EOL pre-pass -> deletes and writes -> rollback decision -> create-on-write -> in-memory resync -> release the OS
lock, then `_solutionLock`. Compile validation runs before `_solutionLock`, so it is outside both locks.)

*5a. Observed-content ledger (what an agent has seen)*
- `_observedHashes` holds, per `(agent key, path)`, the `ContentFingerprint.Compute` hash of the whole
  file text this server last returned to that agent. Agent key = `ToolCallContext.Current?.AgentId ?? ""`.
  Interface: `Common/IObservedContentLedger.cs` (`RecordObserved`), composed into `IWorkspaceManager`.
- Recorded by `ReadFile` (every content branch, partial reads count as the whole file),
  `GetMethodSource`, Search text mode (documents among the returned hits only), `GetFileOutline`
  (whole-file text of the document, success path only) and `InspectSymbol(aspect: info)`, which records the
  DECLARING file (`SymbolHoverInfo.DefinedInFile`, through `RecordWholeFileObservedAsync`), not the file
  holding the snippet; a metadata symbol has no source file and records nothing, and a failed lookup records
  nothing. NOT recorded: `ListAll`, `InspectSymbol(aspect: blastRadius)` (its `ImpactReport` carries no
  declaring file), engine reads, Git output.
- Recording is wider than the content shown: an outline or one symbol's metadata marks the whole file
  observed, so a write after an outline passes the check even if the body changed between outline and write
  (same class as the accepted partial-read hole).
- A write creates the writer's entry (below), so an agent that has only written a file is also protected.

*5b. Stale-read check: who opts in*
The check runs only when the caller passes a `StaleReadPolicy` other than `None`, and never for
`exactRestore`. For each target with an entry under the writer's key, the entry must match both the disk
pre-image and the in-memory document text, or the batch is refused with `ToolErrorCode.StaleRead`
(`staleFiles` maps each file to "changed on disk since you last read it" or "changed in the workspace since
you last read it"). A target with no entry is allowed.

| Policy | Who passes it | Unobserved existing target |
| --- | --- | --- |
| `RefuseIfObservedStale` (anchor and whole-file tools) | `WriteFile` (ReplaceFile), `DeleteFile`, `ApplyDiff` (files format and diff format), `ApplyUnifiedDiff`, `ReplaceSnippet`, `ReplaceSnippetBatch` | allowed, plus a `WriteChokepoint` Info finding "changed without this agent having read them first" |
| `RefuseIfObservedStaleQuiet` (engine and batch tools) | `ValidateAndApplyHelper.ValidateAndApplyAsync` DEFAULT (so `Member`, `RenameSymbol`, `MoveMember`, `ModifyModifier`, `ChangeSignature`, `ExtractMethod`, `SemanticFindReplace` and every other tool that writes through the helper, with no edit at the tool); direct callers `AsyncifyTools` (18 sites), `AsyncBatchEngine` (11), `CommentingTools` (2), `MsToolAugmentEngine` (2: sort-usings and format), `DeleteUnusedSymbol` (1) | allowed, NO finding (engine tools edit many files; the finding would be noise) |
| `None` (exempt) | `UndoLastApply` (`exactRestore: true`), `CreateFile` (a new file has nothing observed); `RetryFailedChangesAsync` (a cache replay inside `Common`, default policy) | not checked |

- The helper's default deviates on purpose from the chokepoint's default-`None` pattern (a code comment says
  so). A `dryRun` returns before the chokepoint and is never stale-checked.
- Ratchet: `StaleReadPolicyRatchetTests` (`Tests.Basic`) is a syntax scan of the production projects outside
  `Common`: every `ApplyProposedChangesAsync(` invocation must name a `staleReadPolicy:` argument or be
  allowlisted as `<file>::<member>` with a reason (today only `WorkspaceFileEditImpl::UndoLastApply` and
  `::CreateFile`); a second test fails when an allowlist key no longer matches, so the list only shrinks. It
  checks that the argument is PRESENT, not which value it carries, and it does not see the helper's default.
- Mapping to the tool result: `WriteRefusal.TryMap(toolName, result, out ResultError)`
  (`Common/WriteRefusal.cs`) maps `StaleRead`, `FoldPending` and `WriteLockBusy` (and returns false for any other
  code; `WriteLockBusy` becomes `<tool>: WriteLockBusy: another session (...) is writing; retry in a moment`, see section 7). The
  `StaleRead`/`FoldPending` message names at most 10 files then "and N more" (full list in `StructuredDetail`), tolerates an empty
  `FailedFiles`, and for `StaleRead` says to re-read with `ReadFile` and, if refused again, to wait a moment.
  Callers of `TryMap`: `WriteFile`, `DeleteFile`, `ApplyDiff` (both formats), `ApplyUnifiedDiff`,
  `ReplaceSnippet` (single and batch), `DeleteUnusedSymbol`, and `ValidateAndApplyHelper` (so every engine
  tool). The direct callers `AsyncifyTools`, `AsyncBatchEngine`, `CommentingTools` do not use `TryMap`: they
  carry the refusal in their summary or per-item text (the chokepoint `Summary` begins `StaleRead:`,
  `FoldPending:` or `WriteLockBusy:`), and `MsToolAugmentEngine` sort-usings reports it in `UsingsCleanupResult.Error`.
  A `// TODO per-file attribution` in `WriteRefusal.StaleReadError` is the hook where
  `proposal_cross_session_write_journal.md` will name the writer that changed each file. The holder sidecar of
  section 7 names the CURRENT lock holder only (for `WriteLockBusy`); it is not a record of who changed a file.
- Known gap: the four anchor/diff sites still fall through to `IsError = false` for a non-validation
  refusal that `TryMap` does not map (for example `ConcurrentEditRefused`); `// TODO` marked, not fixed.

*5c. Entry maintenance*
- Inside the write loop a successful write only UPDATES an existing entry (update-only
  `RefreshObservedForWriter`). After the rollback decision, a batch that is still successful CREATES the
  writer's entry for every file it actually wrote (`createIfMissing: true`; no-op writes are skipped). So a
  rolled-back batch creates nothing, and the rollback restore itself is update-only (it must not claim the
  agent saw the restored pre-image). This reversed the closed plan's "a write only refreshes an existing entry".
- A delete creates no entry and removes the path under EVERY agent key; so does a rolled-back new file.
  Entries survive reloads (content comparison makes stale entries harmless).

*5d. `FoldPending` guard (in-memory text lags disk)*
- Runs for every policy including `None`, for deletes too, except `exactRestore`, in `CheckFoldPendingAsync`
  right after the stale check, so `StaleRead` wins for an agent that is really stale. It compares each
  target's disk pre-image with its in-memory document text (`FoldComparison.Compare`). A target with no
  document (a new file) or no disk pre-image is not checked.
- Ladder: (1) exact text equal -> proceed. (2) Equal after stripping a BOM, normalizing EOLs and trimming
  trailing whitespace per line (`Converged`) -> proceed with a `WriteChokepoint` Info finding. (3) Otherwise
  (`Mismatch`): if the path was not already refused since its last fold, fold the refused files from disk
  INLINE (`ApplyInMemoryDocumentUpdatesAsync` under the `_solutionLock` and the OS write lock the chokepoint already holds
  (the fold never takes the OS lock itself, rule b of section 7), then bump `_workspaceVersion`) and refuse the whole batch with `ToolErrorCode.FoldPending` ("`<file>` is
  changing on disk; retry in a moment"); the retry then sees the fresh text. Not Roslyn syntax equivalence
  (`IsEquivalentTo`): it ignores comments, so an external comment edit would count as equal and be
  overwritten.
- Refuse once per fold: `_foldRefusedFiles` marks a path only if it STILL mismatches after the inline fold;
  `NoteFileFolded` clears the mark (and `ClearReloadFailure` clears all). A marked path that mismatches again
  does not refuse a second time: the write proceeds against the in-memory text and the result carries a
  Warning finding "still differs from disk after a fold" whose recovery text is `WorkspaceStaleText.Recovery`
  (`LoadSolution(forceReload: true)`; the sentence is reused from the stale-workspace refusal, so its
  ".csproj/.sln" wording is a little off for this case). Test gap: that persisting-mismatch Warning path has no
  test.
- Inline fold instead of enqueue plus timer because a `SetTestSolution` manager's debounce handler returns
  early (an enqueue would never complete there) and refuse-once timing would depend on the timer.

**6. `ExternalFileDrift`** (`Tools.Basic/AdminTools.cs`)
`Status` (tracked count; the `SessionHalted=` part is always False, see section 4), `List` (the tracked
paths) and `Acknowledge`. `Acknowledge` only removes entries from the visibility list: `ConfirmAll` drains it,
`ConfirmWithListedFiles` removes the named entries and leaves the rest listed. It does not change whether a
write is allowed, and it is never needed to resume writing. `DriftMessages` and the tool description say so.
The only way to make a refused file writable is to re-read it (`StaleRead`) or retry after the fold
(`FoldPending`).

**7. Cross-process write lock** (`CrossProcessWriteLock`, `Common/CrossProcessWriteLock.cs`)
Purpose: two server processes on the same solution root must not interleave writes, and a process must not
read disk mid-write by another server process (the write truncates, then writes). It serializes RoslynSentinel
processes only; an editor, `git` or a shell does not take it.
- Mechanism: `<solution root>/.roslynsentinel/write.lock` opened `FileMode.OpenOrCreate`, `FileAccess.ReadWrite`,
  `FileShare.None`. A contended open throws `IOException`, so `AcquireAsync` polls every 50 ms until its wait
  elapses (`ct` cancels). The OS drops the handle if the process dies. `.roslynsentinel/` is not watched (the lock
  file and sidecar have non-watched extensions).
- Holder metadata is a SIDECAR, `write.lock.holder.json` (`WriteLockHolder(SessionId, AgentId, ToolName, Reason,
  SinceUtc)` as JSON), because a waiter cannot read the exclusive holder's lock file. Written best-effort right after
  acquire; on release the sidecar is deleted first, then the stream disposed (so a waiter that acquires next never
  has its fresh sidecar deleted). A waiter reads it best-effort; missing or unparsable reads as "holder unknown".
  A crash leaves a stale sidecar; it is only seen in the short window between a new holder's acquire and its
  sidecar write, so a busy message can briefly name the previous holder.
- Root: `WriteLockRoot()` = the test seam `SetTestSolutionRoot` if set, else `GetSolutionRoot()`. Null root = NO OS
  lock, in-process `_solutionLock` only. That is what a `SetTestSolution` manager (no on-disk solution path) gets, and a
  test that wants the lock sets a temp root (never the repo root).
- Three acquisition points; waits come from `CrossProcessWriteLockOptions` (defaults only; `// TODO wire startup arg per
  the LlmOptions pattern`):

| Entry point | Wait | On busy |
| --- | --- | --- |
| Write chokepoint (`ApplyProposedChangesAsync`) | 10 s | Refuse the batch: `ApplyChangesResult` with `RefusalCode = WriteLockBusy`, every target in `FailedFiles`, summary `WriteLockBusy: another session (<agent>, <tool>, since HH:mm:ss UTC) is writing; retry in a moment`. Nothing written, non-latching |
| Debounce fold (`OnDebounceTimerElapsed`) | 2 s | Re-queue the changes, re-arm the timer at 3 s, no disk read (section 3) |
| Fold-on-read (`SyncFileFromDiskIfStaleAsync`, `FlushPendingFoldsAsync`) | 1 s | Fail open: fold nothing, the read proceeds on in-memory text (section 8) |

- Chokepoint placement: taken after `_solutionLock`, `CheckConcurrentEditConflict` and `RevalidateAfterForeignWriteAsync`, before
  pre-image capture, so the stale-read and `FoldPending` checks compare against a disk no other server process can change
  until release. Released in the method's `finally` after the in-memory resync and before `_solutionLock.Release()`. A
  refusal that returns before the write (stale, `FoldPending`, EOL, validation) releases through the same `finally`. A
  background full reload queued by `needsFullReload` starts after both locks are released.
- Never held across compile validation: `ValidationEngine.ValidateChangesAsync` runs before `_solutionLock`, and
  `RevalidateAfterForeignWriteAsync` (a second compile under `_solutionLock`) runs before the OS lock is taken.
- Holder identity comes from `ToolCallContext.Current` (`SessionId`, `AgentId`, `ToolName`, else the chokepoint's operation
  label, and `Reason`); any part may be null and prints as "unknown". The debounce handler uses a synthetic holder (agent
  `server-debounce`, tool `DebounceFold`); fold-on-read uses the call's context with tool name `FoldOnRead` as fallback.
- Lock-order and re-entrancy rules (the file is `FileShare.None`, so a second open from the SAME process fails like another
  process's): see `reference_architecture_map.md`, "Write chokepoint", rules (a)-(c).
- Cost: the wait happens INSIDE `_solutionLock`, so a busy lock can stall every in-process operation that needs
  `_solutionLock` for up to 10 s (chokepoint) or 2 s per retry (debounce, uncapped). Accepted for v1.
- The operation blob (`OperationBlobWriter`) is written after the chokepoint returns, so it is outside the lock (accepted gap;
  `proposal_cross_session_write_journal.md` closes it by appending inside the lock).
- Excluded writers (in-process exclusion only, each with a `// TODO`): `EolFileWriter` (class-level TODO), the format
  engine's no-solution fallback (`MsToolAugmentEngine.FormatDocumentSafeAsync`, TODO at the write). See
  `reference_code_file_write_paths.md`.
- Tests (what each covers): `CrossProcessWriteLockTests` exercises the lock TYPE with two leases in ONE process (exclusivity,
  timeout naming the holder, release on dispose, release when a holder throws, sidecar lifecycle, `WriteRefusal.TryMap` of
  `WriteLockBusy`); that proves the OS `FileShare.None` behavior, not a second process. `CrossProcessWriteLockChokepointTests`
  exercises the chokepoint with `SetTestSolutionRoot`: success releases the lock, a busy lock refuses `WriteLockBusy` naming the holder
  with nothing written, a refusal before the write releases the lock, a null root takes no lock, a validation failure is reported even
  while another lease holds the lock (validation is outside it). NOT covered: release when the write loop itself throws (a `// TODO` in
  the `finally` says it needs a fault-injection seam), and the lock being held across the write and resync. `DebounceFoldWriteLockTests`
  covers the debounce fold. Watcher/debounce tests that use `TestSolutionFixture` (real solution root) do take the lock.

**8. Fold-on-read: read tools catch up with disk before answering**
Problem it fixes: a read inside the 500 ms debounce window (or before the watcher event arrives) recorded in-memory text
older than disk, so the agent's next write was refused. Two primitives on `PersistentWorkspaceManager`, declared on
`IWorkspaceMutator` (the fake is a no-op):
- `FlushPendingFoldsAsync(ct)`: returns at once when `_pendingChanges` is empty (usual case). Otherwise, under `_solutionLock`:
  nothing is folded if there is no solution, the watcher overflowed, or the queued set `RequiresFullReload`; else take the OS lock
  (1 s; null root = none) and fold every queued path in one `ApplyInMemoryDocumentUpdatesAsync` call, then bump the workspace
  version. A busy lock, a fold that says it needs a full reload, or an exception re-queues/leaves the paths for the debounce
  handler. Cost scales with the number of pending changes.
- `SyncFileFromDiskIfStaleAsync(path, ct)`: under `_solutionLock`, find the document for the path (none -> return), read disk
  (missing -> return), compare with the in-memory text ordinally; equal -> return (cost: one disk read and a string compare).
  Differing -> take the OS lock (1 s), fold that one path, bump the version, remove it from `_pendingChanges`. Catches a change
  the watcher has not delivered yet. A fold that needs a full reload leaves the path queued.
- Both FAIL OPEN and never throw on I/O errors (only cancellation propagates): a read tool must not fail because the lock was
  busy or the file vanished. The cost of failing open is that the read returns and records in-memory text that may still lag disk; a
  later write is then refused `FoldPending`, which self-heals through section 5d.

| Tool | Flush | Per-file sync | Then |
| --- | --- | --- | --- |
| `ReadFile` (member path, line-range path, whole-file path) | yes | yes | read, then record the whole-file hash. The no-solution path (`ReadFileWithoutSolutionAsync`) does neither |
| `GetMethodSource` | yes | yes | read, then record |
| `Search` mode `text` (`SearchSolutionText`) | yes | no | search the folded solution |
| `Search` modes `symbol`, `references`, `callers`, `implementations`, `attribute`, the declaration listings; `ListAll`; `GetFileOutline`; `InspectSymbol` | NO | NO | unchanged: they read in-memory text and can still lag disk by up to the debounce window |

`GetFileOutline` was left out on purpose (the decision named three tools); do not add it by analogy without a new decision.
Order inside the tool is flush, then per-file sync, then the read.

**9. Zero-hit `Search` hint (foreign-write evidence)**
Problem it fixes: a `Search` that finds nothing because another agent or session just removed (or just added) the text reads as
"does not exist". The server attaches an Info `Finding` (source `EditAttribution`) that states evidence, never a cause.
- Where it is called: text mode, inside `SearchSolutionText` on its one zero-result return
  (`NotFoundEvidence.ForZeroHitSearch(solutionRoot, pattern, isRegex: true)`, merged after the `OrientationBreaker` finding when
  that tripped). Every other mode: a post-process in `DispatchSearch` (`WorkspaceReadNavigationTools`), after `DispatchSearchCore`,
  `WorkspaceReadNavigationImpl.AddZeroHitEvidence(mode, query, result)`, which acts only when `!IsError`, `TotalRecords == 0`, the
  mode is not text and the query is not blank (it passes `isRegex: false`). No change when there are hits.
- `ForZeroHitSearch` returns nothing unless the call's `ToolCallContext` has an `AgentId` and the solution root is non-blank. It calls
  `NotFoundEvidence.FindAcrossFiles`, which skips a blank term, an identifier-shaped term shorter than 3 characters and a regex
  shorter than 3 characters, and never throws (an unreadable or corrupt blob is skipped).
- What it reads: operation blobs under `<solution root>/.roslynsentinel/operations`, newest first by the timestamp in the blob name,
  at most 200 blobs inside a 60-minute window, at most 3 notes, one per file. It skips items with no path or outcome
  `Skipped`/`Failed`/`RolledBack`, and keeps only FOREIGN writers (`Classify`: with both session ids known, a different session or
  agent; otherwise an unknown writer, or a bare `main` without a session id, is NOT foreign; the caller's own writes never
  produce a note).
  - Removed: the item's `BeforeSource` holds the term -> "`term` was in `File` before agent `x` (type ..., in another session)
    changed it <age> ago via <tool>: <reason, clipped to 160 chars>". It does not check `AfterSource`: chokepoint blobs never store
    it, and the zero-hit search already shows the in-memory text lacks the term.
  - Added, not loaded yet: the blob is under 15 s old, `BeforeSource` does not hold the term, and the named file READ FROM DISK
    (skipped over 2 MiB) holds it -> "`term` appears in `File` on disk as written by ... <age> ago; the server has not loaded it
    yet - retry".
  - Term matching: identifier-shaped literal -> case-sensitive whole word; other literal -> ordinal substring with CRLF normalized;
    regex -> .NET regex, ignore-case, 250 ms timeout (an invalid or slow pattern holds nothing).
- Limits, named:
  - No hint does not mean nothing changed: editors, `git` and shell writes leave no blob; 5 chokepoint paths write none (see the blob
    coverage note in `proposal_cross_session_write_journal.md`); the window is 60 minutes (15 s for "added"). The write journal stores
    hashes only and cannot replace this search.
  - Text mode always passes `isRegex: true`, so a literal query containing regex metacharacters (for example `Foo(`) is an invalid or
    different regex here and yields no note, even though the text search itself falls back to literal matching.
  - The non-text hint depends on `TotalRecords == 0`. `LocateSymbol`, `ListAll` (declaration listings) and `QuerySymbolRelationships`
    (attribute) set it. `FindReferences` (modes `references`, `callers`, `implementations`) does not set `TotalRecords` (it leaves
    it null), so by reading the code those three modes never get the hint on a real zero-result call. Not confirmed at runtime, and
    no `// TODO` marks it in the code as of 2026-11-09. Fix direction: derive the zero test from `ListSummary` or set `TotalRecords`
    in `FindReferences`.
  - Test coverage: `NotFoundEvidenceTests` (`AcrossFiles_*`) covers `FindAcrossFiles`; `ZeroHitSearchHintToolTests` drives text mode
    through the real `SearchSolutionText`, but for the other modes it calls `AddZeroHitEvidence` directly with a hand-built result
    that has `TotalRecords = 0`. No test goes through `DispatchSearch` or through a real zero-result `FindReferences`, so the gap
    above survived an all-green run. That is a planner gap (the plan's T9h-2 said "Search with zero hits" without naming the mode
    list) and a reviewer gap (the delivered tests do not cover the dispatch seam), not an implementer failure.

## Usage
- A `WriteLockBusy` refusal: another server process (the message names its agent and tool when the sidecar is readable) is
  writing; nothing was written; retry in a moment. If it persists, a crashed holder is not the cause (the OS releases the lock),
  so look for a long-running write or a second window stuck inside the chokepoint.
- A `StaleRead` or `FoldPending` refusal: re-read the named file with `ReadFile` (or retry once for
  `FoldPending`), then repeat the edit. `ExternalFileDrift(operation: List)` plus `Git(operation: status)`
  show what changed externally (git-clean does not prove no drift happened; the watcher may have seen an
  intermediate write); acknowledging is optional housekeeping.
- Adding another ignored folder: add the name to `IgnoredWatcherDirectoryNames` and a case to
  `PersistentWorkspaceManagerWatcherPathTests`.
- Watching another file type: add it to `WatchedFilePatterns` AND the extension gate in
  `OnFileSystemChanged` (and the debounce, if it needs a reload).

## Gotchas
- The main-watcher filter used to be `*.cs` only, so in-tree `.csproj`/`.sln` events never arrived
  (only out-of-tree watchers delivered them). That is fixed by `Filters`; do not reintroduce a single
  `Filter`.
- Watching the solution root also sees every `Worktree/` harness clone; that is why `Worktree` is in the
  ignore list. Never treat drift reports for paths under it as authoritative.
- `Git` tool operations (checkout, pull, stash apply) change `.cs` files on disk without telling the
  workspace manager, so they surface as external drift (no reconciliation call exists).
- Do not delete tracked files with a shell command; use `DeleteFile`. A shell delete no longer halts the
  session (the latch is gone), but it bypasses the chokepoint: the workspace document is only reconciled later
  by the watcher fold, the observed-content entries for the path are not removed (only a chokepoint delete
  does that), and an agent that had read the file is refused `StaleRead` on its next write to that path
  (a missing file counts as changed).
- Drift no longer halts anything. A drifted file the writer never read or wrote is overwritten without a
  refusal (the in-memory text is folded from disk first when it lags, see `FoldPending`); only a file the
  writer read or edited is protected, by `StaleRead`. Do not read "files changed externally" in
  `ExternalFileDrift(List)` as a write block.
- There IS a cross-process write lock now (section 7), and `ReadFile`, `GetMethodSource` and text `Search` fold
  first (section 8). Do not look for the old "no lock, no fold-on-read" gap. What is still open:
  - Only RoslynSentinel processes take the lock. An editor, `git` or a shell can still write between our pre-image read and
    our write, and `FileIoHelper.WriteAllTextAsync` still truncates before it writes, so a non-cooperating reader (or the
    watcher's content-hash read, which takes no lock) can see a partial file. `FoldPending` catches in-memory-differs-from-disk,
    not a half-written file that happens to equal the in-memory text.
  - Writers that do not reach the chokepoint take no OS lock: `EolFileWriter` callers and the format engine's no-solution fallback
    (each has a `// TODO`); the operation blob write happens after the chokepoint releases the lock.
  - A manager with no lock root (null `GetSolutionRoot()` and no test root) takes no OS lock at all.
  - Fold-on-read fails open: a busy lock, or a change that needs a full reload (`.sln`/`.csproj`/`.props`/`.targets`, overflow), leaves the
    read on in-memory text, so a read can still record text that lags disk; the next write is then refused `FoldPending` or `StaleRead`
    until a retry or a re-read.
  - Not folded at all: `GetFileOutline`, `InspectSymbol`, `ListAll` and every `Search` mode except text; text `Search` flushes the
    queue but has no per-file disk compare, so the milliseconds before the OS delivers a watcher event remain open for it (and for
    every tool in the "NO" row of section 8), while `ReadFile`/`GetMethodSource` close that gap for their one file.
  - The lock wait runs inside `_solutionLock`: a long-held foreign lock stalls this process's other operations (up to 10 s per
    write, 2 s per uncapped debounce retry).
- A new read tool that returns file content must call `RecordObserved`, or its files stay unprotected
  (convention, not enforced). It should also call `FlushPendingFoldsAsync` (and `SyncFileFromDiskIfStaleAsync` when it
  reads one named file) before reading, or it inherits the debounce-window exposure (section 8; also convention, not enforced). New WRITERS are covered by `StaleReadPolicyRatchetTests`.
- `UndoLastApply` (`exactRestore`) and `CreateFile` skip both the stale-read check and `FoldPending` by design.
- Tests on one process: the reading agent's `StartSeq` must be past a competing write, or
  `ConcurrentEditRefused` pre-empts `StaleRead`. `PersistentWorkspaceManager.AddExternalChangeForTest`
  sets a drift entry without the watcher (it now only affects `ExternalFileDrift` output).
- Fixtures that make the in-memory text differ from disk (`File.WriteAllText` after `SetTestSolution`) now
  get one `FoldPending` before the write succeeds; assert the first refusal, then retry.
