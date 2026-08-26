---
name: project_filepathlock_watcher_race
description: FilePathLock ported into RoslynSentinel.Common and wired into PersistentWorkspaceManager to fix a watcher-vs-write race in OnFileSystemChanged
metadata: 
  node_type: memory
  type: project
  originSessionId: 94678fbd-7cc6-4fa2-b771-56bd6fd6f4ed
  modified: 2026-08-26T21:28:05.971Z
---

`RoslynSentinel.Common/FilePathLock.cs` (commit 9f70b24) is a per-path async lock (keyed by normalized full path, `SemaphoreSlim`-backed) ported from the user's other project (originally used `TwonkyDomain.Common.DefinedPath`; adapted here to `RoslynSentinel.Common.FilePath`, which has an implicit string conversion so it dropped in cleanly).

**Why:** `PersistentWorkspaceManager.OnFileSystemChanged` (line ~418) already had self-write suppression via `_internalChanges` (path+content+timestamp recorded before each write) plus a `catch (IOException)` around its verification `File.ReadAllText`. But Windows can raise a `Changed` event *while* `File.WriteAllTextAsync` in `ApplyProposedChangesAsync` still holds the write handle open, so the verification read would throw and get caught — functionally fine, but noisy (shows up as a first-chance `IOException` break in the VS debugger even though nothing was actually broken). A second, unrelated first-chance exception (`OperationCanceledException` from `CSharpSyntaxTree.ParseText` in the diagnostic format-divergence block, line ~1078) is also already caught by a bare `catch {}` and is likewise just first-chance noise.

**How to apply:** `OnFileSystemChanged` now checks `FilePathLock.IsLocked(e.FullPath)` first and returns early (skipping the read+throw+catch entirely) when a write to that exact path is in flight. `ApplyProposedChangesAsync` now holds `FilePathLock.AcquireAsync(filePath)` for the duration of both the main write and the rollback-on-partial-failure write. This is defense-in-depth/debugger-noise cleanup, not a correctness fix — `_solutionLock` already serializes all `ApplyProposedChangesAsync` calls solution-wide, so there was never real concurrent-writer corruption here.

Also confirmed: `SentinelCommentingTools.RunAsync` (BulkComment) legitimately calls `ApplyProposedChangesAsync` **twice** for the same file when it has un-seeded stale members — once for the seed phase ([ContentHash] attribute injection) and once for the comment phase (actual doc comments + reformatting). Two attribute additions + a formatting diff in git for one file after a BulkComment run is expected, not a sign of dual writers.

See [Write-path chokepoint unified](project_write_path_chokepoint_unified.md) for the broader write-path context, and [Watcher reload corruption fixed](project_watcher_reload_corruption_fixed.md) for a different, earlier watcher-race fix (mid-reload corruption, not this write-verification race).

**Follow-up (commit 0661c25):** added `RoslynSentinel.Common/FileIoHelper.cs` — a static wrapper (`ReadAllTextAsync`, `ReadAllTextIfExistsAsync`, `WriteAllTextAsync`, `DeleteAsync`, `IsLocked`) that bakes `FilePathLock` acquisition into every write/delete and mutation-adjacent read. Migrated all of `PersistentWorkspaceManager`'s write-path call sites onto it (pre-image capture, the main write, rollback write/delete, post-write resync read), replacing the ad-hoc `FilePathLock.AcquireAsync` usages from the first pass. Deliberately scoped to `PersistentWorkspaceManager` only — the user asked about widening this to a solution-wide `File.*` replacement (36 files touch `File.*`, ~15 of them tests) and agreed to defer that; existence/metadata-only checks (`File.Exists`, `Directory.Exists`, `GetLastWriteTimeUtc`, `.sln` parsing via `ReadLines`) were deliberately left as direct `System.IO` calls since they don't participate in the write race. If asked to widen scope later, the remaining candidates are `ScanResultHelper.cs`, `OperationBlobWriter.cs`, `MigrationLedger.cs`, `ValidateAndApplyHelper.cs` in `.Common` first, then Basic/Advanced/Server.* engines.
