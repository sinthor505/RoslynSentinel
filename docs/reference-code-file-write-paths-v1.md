# Reference: Code File Write Paths

**Status:** Living reference — update when a new write-to-disk call site is added anywhere in
the solution.
**Scope:** Only call sites that write modified `.cs` (or other project source) file content that
belongs to the loaded workspace/solution. Explicitly excludes: scan results, diagnostic reports,
log files, JSON tool-result payloads, forensic operation blobs, `docs/` project documentation
output, and debug dumps — none of those go through or need to go through the path described here.

## The chokepoint

**`PersistentWorkspaceManager.ApplyProposedChangesAsync`**
(`RoslynSentinel.Common/PersistentWorkspaceManager.cs`) is the shared, safe path for writing
source file changes to disk. It is the only write path that provides:

- **External drift refusal** — refuses to write if the target file changed on disk since the
  last sync and the drift hasn't been acknowledged via `ClearExternalDrift`.
- **Pre-image capture** — reads every file's content immediately before writing, so callers can
  populate `OperationItemRecord.BeforeSource` for `UndoLastApply`.
- **No-op skip** — skips the write entirely if proposed content is byte-identical to current
  content.
- **Whitespace-only-diff skip** (`.cs` files only) — parses both old and new content, compares
  `NormalizeWhitespace()`'d forms, and skips the write if they're semantically identical. Catches
  engines that accidentally reformat without changing meaning.
- **`FileSystemWatcher` loop suppression** — marks the path in `_internalChanges` before writing
  so the manager's own watcher doesn't mistake its own write for an external edit.
- **IOException retry** — retries transient file-lock failures with backoff.
- **Rollback on partial failure** (opt-in via `rollbackOnPartialFailure`) — if a multi-file change
  partially fails, restores already-written files to their pre-images so the change doesn't land
  half-applied.
- **Workspace resync** — updates `CurrentSolution` in-memory after a successful write, so
  subsequent semantic queries see the new content without a full reload.

**Any new tool or engine method that needs to persist a modified source file to disk must call
this method (directly or via a caller that does), rather than calling `File.WriteAllText*`
itself.** There is no `IWorkspaceManager` interface — `PersistentWorkspaceManager` is injected
as a concrete class — so nothing structurally enforces this; it is a convention documented here
and in the code comment on the method itself.

## Callers that funnel through the chokepoint (confirmed 2026-08-22)

| Caller | Location | Notes |
|---|---|---|
| `ValidateAndApplyAsync` (Basic) | `RoslynSentinel.Server.Basic/SentinelRefactoringTools.cs:157` | Shared local helper for RenameSymbol, GenerateMapping, Member replace/remove/add, UsingDirective, ModifyEnum, ChangeAccessibility, SummaryComment, ConstructorParameter, ExtractLocalVariable, ExtractMethodSafe, ModifyAttribute, ModifyModifier, ModifyBaseType, SyncTypeAndFilename |
| `ValidateAndApplyAsync` (Advanced) | `RoslynSentinel.Server.Advanced/SentinelAdvancedRefactoringTools.cs:146` | **Duplicate implementation** of the Basic helper — copy-pasted, not shared via `RoslynSentinel.Common`. Same behavior, but a fix to one requires manually mirroring it in the other. |
| `SafeDeleteUnusedSymbol` | `RoslynSentinel.Server.Basic/SentinelWorkspaceTools.cs:760` | Inline call after computing `DocumentEditResult.UpdatedText` |
| `ApplyDiff` (files format) | `RoslynSentinel.Server.Basic/SentinelWorkspaceTools.cs:380` | Direct inline call |
| `ApplyDiff` (unified diff format) | `RoslynSentinel.Server.Basic/SentinelWorkspaceTools.cs:482` | Direct inline call, after `_diffEngine.ApplyDiff(oldText, unifiedDiff)` |
| `UndoLastApply` | `RoslynSentinel.Server.Basic/SentinelWorkspaceTools.cs` | **Fixed 2026-08-22** — previously bypassed the chokepoint entirely (see below); now routes through it. |
| Asyncify batch tools | `RoslynSentinel.Server.Advanced/SentinelAsyncifyTools.cs` (13 call sites) | Each after computing changes from an `AsyncOptimizationEngine` call |
| `AsyncBatchEngine` | `RoslynSentinel.Advanced/AsyncBatchEngine.cs` (12 call sites) | Holds its own `PersistentWorkspaceManager` reference; batch operations like `RunUpliftBatchAsync`, `PropagateCancellationTokenBatchAsync` |

The bulk of `RoslynSentinel.Advanced`'s ~30 other engine classes (`AdvancedRefactoringEngine`,
`AdvancedTypeEngine`, `DocumentationEngine`, `SecurityEngine`, `PerformanceEngine`, etc.) never
write to disk directly — they compute an in-memory `Dictionary<FilePath,string>` or a preview
`UpdatedText` and hand it back up to a `Server.*` tool method, which then calls the chokepoint.

## Divergent paths found and fixed (2026-08-22)

Three call sites bypassed the chokepoint via raw `File.WriteAllTextAsync`, meaning none of the
guards above applied to them. All three were fixed to route through
`ApplyProposedChangesAsync` — see commit history for the fix.

1. **`MsToolAugmentEngine.SortAndDeduplicateUsingsAsync`**
   (`RoslynSentinel.Basic/MsToolAugmentEngine.cs`) — wrote `updatedContent` straight to disk when
   `writeToFile=true`. No drift check, no pre-image capture, no rollback, no watcher-loop
   suppression, no workspace resync.
2. **`MsToolAugmentEngine.FormatDocumentSafeAsync`**
   (`RoslynSentinel.Basic/MsToolAugmentEngine.cs`) — wrote `formatted` directly when
   `preview=false`, then redundantly called `ApplyProposedChangesAsync` afterward purely for
   workspace resync. Since the file was already overwritten by the time the "official" call ran,
   the drift check happened too late to matter and the no-op skip made the second call a pure
   resync no-write.
3. **`UndoLastApply`** (`RoslynSentinel.Server.Basic/SentinelWorkspaceTools.cs`) — the entire
   undo/revert mechanism was a hand-rolled `File.WriteAllTextAsync` loop over
   `OperationItemRecord.BeforeSource` values, completely independent of the chokepoint. Reverts
   didn't get rollback-on-partial-failure protection, and — because `_internalChanges` was never
   marked — a revert would be picked up by the `FileSystemWatcher` as an *external* change,
   potentially triggering spurious drift warnings on the next apply.

## Format-and-log diagnostic

As of 2026-08-22, `ApplyProposedChangesAsync` runs every `.cs` write's new content through
`Formatter.Format` (via a per-call `AdhocWorkspace`, the same lightweight pattern
`MsToolAugmentEngine.FormatDocumentSafeAsync` already used) immediately before the write, and —
only when `LogLevel.Debug` is enabled and the formatted output differs from what's about to be
written — logs the line-count delta between the two (`CountLines(formatted) - CountLines(written)`)
at `LogLevel.Debug`. If content matches, nothing is logged. A parse/format failure is swallowed
silently; the diagnostic must never block or alter the real write. This is purely observational —
it runs before the write and never touches `newContent` — and exists to build a picture of which
tools/callers produce output that diverges from Roslyn's own formatting rules, ahead of any
decision to wire real auto-formatting into the write path.

## Explicitly out of scope / not source-code writes

- `RoslynSentinel.Server.Basic/DocumentationTools.cs` (`ProjectDoc`/`WriteFile`) — writes to
  `docs/plans`, `docs/handoffs`, `docs/migration-state.yaml`.
- `RoslynSentinel.Common/OperationBlobWriter.cs` — forensic JSON audit blobs under
  `.roslynsentinel/operations/`, used by `UndoLastApply` for pre-image lookup.
- `RoslynSentinel.Common/MigrationLedger.cs`, `RoslynSentinel.Common/ScanResultHelper.cs` —
  ledger/scan-result JSON persistence.
- `RoslynSentinel.Server.Advanced/SentinelAsyncifyTools.cs` debug-dump JSON payloads.
- String-literal lookup tables of anti-pattern suggestion text (e.g. in `AntiPatternEngine.cs`)
  that happen to contain the substring `File.WriteAllText` as example/suggestion text — not
  actual write calls.
