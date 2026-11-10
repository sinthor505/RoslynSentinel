# Reference: file watcher, external-drift flagging and per-file write protection

**Status:** CURRENT 2026-11-09. Verified against `RoslynSentinel.Common/PersistentWorkspaceManager.cs` after plan_stale_read_guard_followups steps 1-8d (anchor and engine tools opted in, outline/InspectSymbol recording, create-on-write, `FoldPending` guard, drift halt removed). Sections 4-6 describe the model AS IMPLEMENTED; the cross-process write lock, fold-on-read and the zero-hit Search hint (steps 9a-9h) are NOT built and are not described here. Symbols cited by name because line numbers shift.

## Purpose
How the server notices that a source file changed outside its own write path, what protects a write
from overwriting such a change, and what `ExternalFileDrift` still does. Consult it before touching the
watcher, the drift flagging, the write guards or `ExternalFileDrift`, or when a `StaleRead` /
`FoldPending` / "files changed externally" report looks wrong. History: until 2026-11-09 a drift hit on
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
Takes `_solutionLock`. `.sln`/`.slnx` change or watcher overflow -> full reload. `.csproj` -> project
reload. Changed `.cs` -> `ApplyInMemoryDocumentUpdatesAsync` folds disk text into `CurrentSolution`
(workspace version increments) and, via `RecordFoldedDocumentBaseline`, refreshes that file's
`_knownFileHashes` baseline to the folded text. It does NOT touch `_externalChanges`.

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
Three layers inside `ApplyProposedChangesAsync`, in this order, after pre-image capture and before the EOL
pre-pass: the stale-read check, then the `FoldPending` guard. Both refuse the WHOLE batch before anything is
written or deleted and never latch.

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
  (`Common/WriteRefusal.cs`) maps `StaleRead` and `FoldPending` (and returns false for any other code). The
  message names at most 10 files then "and N more" (full list in `StructuredDetail`), tolerates an empty
  `FailedFiles`, and for `StaleRead` says to re-read with `ReadFile` and, if refused again, to wait a moment.
  Callers of `TryMap`: `WriteFile`, `DeleteFile`, `ApplyDiff` (both formats), `ApplyUnifiedDiff`,
  `ReplaceSnippet` (single and batch), `DeleteUnusedSymbol`, and `ValidateAndApplyHelper` (so every engine
  tool). The direct callers `AsyncifyTools`, `AsyncBatchEngine`, `CommentingTools` do not use `TryMap`: they
  carry the refusal in their summary or per-item text (the chokepoint `Summary` begins `StaleRead:` or
  `FoldPending:`), and `MsToolAugmentEngine` sort-usings reports it in `UsingsCleanupResult.Error`.
  A `// TODO per-file attribution` in `WriteRefusal.StaleReadError` is the hook where
  `proposal_cross_session_write_journal.md` will name the writer that changed each file.
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
  INLINE (`ApplyInMemoryDocumentUpdatesAsync` under the `_solutionLock` the chokepoint already holds, then
  bump `_workspaceVersion`) and refuse the whole batch with `ToolErrorCode.FoldPending` ("`<file>` is
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

## Usage
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
- Window left open: the stale-read check and `FoldPending` compare against disk at the moment of the call; there
  is no cross-process write lock yet (plan steps 9a-9h are not built), so another PROCESS can still write between
  the pre-image read and our write, and `FileIoHelper.WriteAllTextAsync` truncates before it writes (a reader in
  another process can see a partial file). `FoldPending` catches in-memory-differs-from-disk, not a half-written
  file that happens to equal the in-memory text.
- Read tools do not fold pending changes first (fold-on-read is not built): a `ReadFile` inside the 500 ms
  debounce window can record in-memory text that is older than disk, and the next write is then refused with
  `FoldPending` or `StaleRead` until a retry or a re-read. `GetFileOutline`, `Search` and `InspectSymbol` have
  the same exposure.
- A new read tool that returns file content must call `RecordObserved`, or its files stay unprotected
  (convention, not enforced). New WRITERS are covered by `StaleReadPolicyRatchetTests`.
- `UndoLastApply` (`exactRestore`) and `CreateFile` skip both the stale-read check and `FoldPending` by design.
- Tests on one process: the reading agent's `StartSeq` must be past a competing write, or
  `ConcurrentEditRefused` pre-empts `StaleRead`. `PersistentWorkspaceManager.AddExternalChangeForTest`
  sets a drift entry without the watcher (it now only affects `ExternalFileDrift` output).
- Fixtures that make the in-memory text differ from disk (`File.WriteAllText` after `SetTestSolution`) now
  get one `FoldPending` before the write succeeds; assert the first refusal, then retry.
