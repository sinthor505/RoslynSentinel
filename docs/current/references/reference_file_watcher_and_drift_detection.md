# Reference: file watcher, external-drift flagging and the SessionHalted latch

**Status:** CURRENT 2026-10-17. Verified against `RoslynSentinel.Common/PersistentWorkspaceManager.cs` after the watcher filter/ignore change (symbols cited by name because line numbers shift).

## Purpose
How the server notices that a source file changed outside its own write path, what that does to the
session, and how to recover. Consult it before touching the watcher, the drift flagging, the write
guard or `ExternalFileDrift`, or when a `SessionHalted` / "files changed externally" report looks wrong.

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
(workspace version increments). It does NOT touch `_externalChanges` and does NOT refresh
`_knownFileHashes`, so a later event for the same file mismatches the stale baseline and is flagged
again (open small bug).

**4. The latch**
- The only trip point is `ApplyProposedChangesAsync`: if any write or delete target is in
  `_externalChanges`, it sets `_sessionHalted` and throws `SessionHaltedException`
  (`ToolErrorCode.SessionHalted`). Reads never trip it.
- Once set, every chokepoint write is refused, whatever file it targets. Reads and `ExternalFileDrift`
  keep working; responses are stamped `isSessionHalted`.
- `LoadSolutionAsync` (forced reload) calls `ClearExternalFileChanges()` and `PopulateKnownFileHashes()`
  but NOT `ClearSessionHalt`, so a reload empties the list and the latch stays set.
- `GetContentExternalFileChangesAsync` (in-memory vs disk comparison) feeds only the post-call log line
  in `AddDriftFilter` (`Server.Basic/ServiceRegistrationExtensionsBasic.cs`), not the latch.

**5. Recovery** (`Tools.Basic/AdminTools.cs`, tool `ExternalFileDrift`)
`Status`, `List`, `Acknowledge`. `Acknowledge` with `ConfirmAll` drains the list and clears the halt;
with `ConfirmWithListedFiles` it removes only the named entries but still clears the halt, so unnamed
entries re-trip on the next write that targets them.

## Usage
- A halted session: `ExternalFileDrift(operation: List)`, compare with `Git(operation: status)`
  (git-clean does not prove no drift happened; the watcher may have seen an intermediate write), then
  `Acknowledge`.
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
- Do not delete tracked files with a shell command; it trips the latch (use `DeleteFile`).
- Nothing records which files a caller has READ, so the latch cannot tell a stale-read edit from a
  harmless unrelated change. Read-tracking is under design.
