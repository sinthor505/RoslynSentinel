# Reference: Code File Write Paths

**Status:** CURRENT 2026-10-04. Re-verified against source on this date (chokepoint body read end to end, callers via `FindReferences`). Update when a write-to-disk call site is added anywhere in the solution.

## Purpose

Where workspace `.cs` (and other project source) content is written to disk, and the one shared path
that every such write must use. Scope is only content that belongs to the loaded workspace/solution.
Explicitly excluded: scan results, diagnostic reports, logs, JSON tool-result payloads, forensic
operation blobs, project-documentation output and debug dumps (see "Not source-code writes" below).

## How it works

### The chokepoint

`PersistentWorkspaceManager.ApplyProposedChangesAsync` (`RoslynSentinel.Common/PersistentWorkspaceManager.cs`;
interface `Common/IWorkspaceMutator.cs`; the in-memory test double is `Tests/Fakes/FakeWorkspaceManager`).
`IWorkspaceManager`, `ISolutionProvider` and `IWorkspaceReader` all resolve to the same
`PersistentWorkspaceManager` singleton, so nothing structurally stops a caller from writing a file itself:
the rule is a convention, repeated in the method's remarks.

In order, the method:

1. Refuses everything if the session-halt latch is set (`SessionHaltedException`).
2. Refuses everything if the unrecoverable breaker has tripped (`IUnrecoverableBreaker.StateMessage`).
3. Refuses any target covered by an open scoped operation ledger entry (`_ledger.IsBlocked`), returning a failed `ApplyChangesResult`.
4. Refuses a path given as both a write and a delete target.
5. Checks external drift: if a target is in `GetExternalFileChanges()`, sets `_sessionHalted` and throws `SessionHaltedException`.
   Recovery is `ListExternalDiskChanges` then `AcknowledgeExternalFileChanges` (`ClearSessionHalt`).
6. Optionally compile-validates the whole batch (`validateChanges`, via `ValidationEngine`) before taking the lock.
7. Takes `_solutionLock`, then captures a **pre-image** of every target (null = file did not exist), used for
   `OperationItemRecord.BeforeSource` and `UndoLastApply`.
8. Refuses a change that alters an existing file's line-ending style (`EolChangeGuard`, `ToolErrorCode.EolChangeRefused`).
   `ValidateAndApplyHelper` runs the same check earlier so tools get a structured error; this is the backstop.
9. Processes deletes as their own pass, then writes each file: skips byte-identical content, skips whitespace-only
   changes to `.cs` files (`EnableAstNormalizationNoOpCheck`, compares `NormalizeWhitespace()` forms), optionally logs
   formatter divergence at Debug (observation only), normalizes EOL to the file's dominant style, preserves a UTF-8 BOM,
   marks `_internalChanges` (watcher-loop suppression), then writes through `FileIoHelper.WriteAllTextAsync`
   (per-path lock) with retry on `IOException` (`retryCount`, 500 ms apart). Failed content is cached in `_failedChangesCache`
   for `RetryFailedChanges`.
10. If `rollbackOnPartialFailure` and some files failed after others succeeded, restores the succeeded files to their pre-images.
11. Resyncs the in-memory workspace (`ApplyInMemoryDocumentUpdatesAsync`, bumps `_workspaceVersion`), invalidates the affected
    projects' compilation-cache entries (or all of them on a full reload), and returns `ApplyChangesResult`.

`FileIoHelper.WriteAllTextAsync` is the low-level primitive; in production code only the chokepoint calls it.

### Callers (2026-10-04, non-test)

Most tools do not call the chokepoint raw: they call `Common/ValidateAndApplyHelper.ValidateAndApplyAsync`, which adds
compile validation, the EOL refusal, dry-run and a diff, then calls it.

| Caller | Project | Notes |
| --- | --- | --- |
| `ValidateAndApplyHelper` | Common | Shared validate-then-apply wrapper. Reached by `GenerationTools` (via its private `ValidateAndApplyAsync`) and, by name and call-count, by the refactoring `*Impl` classes (`RefactoringStructuralImpl`, `RefactoringSignatureImpl`, `RefactoringExtractionDocsImpl`) and `AdvancedRefactoringTools` (not individually traced) |
| `WholeFileWriteTools` | Tools.Basic | `WriteFile`, `DeleteFile`, `ApplyDiff`, `ApplyUnifiedDiff` |
| `WorkspaceFileEditImpl` | Tools.Basic | Direct calls from `UndoLastApply`, `ReplaceSnippet`, `ReplaceSnippetBatch`, `CreateFile` |
| `WorkspaceProjectManagementImpl` | Tools.Basic | Direct call from `SafeDeleteUnusedSymbol` |
| `CommentingTools` | Tools.Advanced | Two direct calls in `RunAsync` |
| `MsToolAugmentEngine` | Engines.Basic | Format/using-sort helpers; see the sanctioned fallback below |
| `AsyncBatchEngine`, `AsyncifyTools` | Engines.Advanced, Tools.Advanced | Batch async-migration operations |

The current set changes as tools are added: `FindReferences(symbolName: ApplyProposedChangesAsync, kind: callers)` is the source of truth.
Engines that only compute an in-memory `Dictionary<FilePathWrapper,string>` and hand it up to a tool never write themselves.

### Sanctioned direct write

`MsToolAugmentEngine.FormatDocumentSafeAsync(preview: false)` routes through the chokepoint when a solution is loaded and the
file is a tracked document. When no solution is loaded the chokepoint cannot run (it needs `CurrentSolution`), so it falls back to
`File.WriteAllTextAsync` directly. This is the only known workspace-source write outside the chokepoint.

### History

Three bypasses were found and fixed on 2026-08-22 (a raw write in `SortAndDeduplicateUsingsAsync`, a write-then-resync in
`FormatDocumentSafeAsync`, and a hand-rolled `UndoLastApply` revert loop). Each lacked drift refusal, pre-image capture, rollback
and watcher-loop suppression; the undo path in particular produced spurious external-drift warnings because `_internalChanges`
was never marked. The shared `ValidateAndApplyHelper` was extracted in commit `cb70952`.

## Usage

Adding a tool or engine that persists a source edit: build the `Dictionary<FilePathWrapper,string>` of new contents and call
`ValidateAndApplyHelper.ValidateAndApplyAsync` (preferred) or `ApplyProposedChangesAsync` directly. Never call `File.WriteAllText*`
on workspace source. `Tests.Basic/WriteChokepointGuardrailTests.cs` covers the EOL refusal and line-count reporting of the shared path;
no test or analyzer asserts "no bypass", so review new `File.Write*` calls by hand.

## Gotchas

- Drift on a target halts the whole session, not just that call. Check `git status` before treating it as real
  (the false-positive cross-check is in the project memory index).
- A "successful" apply can be a no-op: identical or whitespace-only content is skipped and reported in `NoOpFiles`/the summary.
- The file's existing EOL and BOM are preserved on write, so the on-disk bytes can differ from the string a tool passed in.

## Not source-code writes

These write files but are outside the chokepoint by design:

- `Tools.Basic/DocumentationTools.cs` (`ProjectDoc` and its helpers): project documentation and state files under `docs/`.
- `Common/OperationBlobWriter.cs`: forensic operation JSON used by `UndoLastApply` for pre-image lookup.
- `Common/MigrationLedger.cs` (`SaveAsync`): ledger JSON persistence.
- `Common/LargeResultHelper.cs`: offloaded oversized tool results under `.roslynsentinel/largeresults/`.
- `Server.Basic/ConsoleMode.cs`: `tool_list_*.json` and method-inventory dumps in the server bin folder.
- `Tools.Advanced/AsyncifyTools.cs` (`AsyncifyLoop`): debug-dump JSON.
- `Common/AgentLoop/ModelAgentRunner.cs`: agent transcripts and log sidecars.
- `Engines.Advanced/AntiPatternEngine.cs`: string literals containing `File.WriteAllText` as suggestion text, not calls.
