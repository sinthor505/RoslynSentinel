# Reference: Code File Write Paths

**Status:** CURRENT 2026-11-09. Steps 5 (drift check removed), 7 and 7a (cross-process write lock, plan step 9), 7b (stale-read check, now opted in by anchor, engine and direct callers) and 7c (`FoldPending`) updated for plan_stale_read_guard_followups steps 1-9h; "Writers outside the chokepoint" below lists what the lock does not cover. The chokepoint step list and callers table were verified 2026-10-04; the EOL policy sections and steps 8-9 were re-verified against source on 2026-10-26 (plan_eol_policy_and_write_chokepoint.md Steps 4-8). Update when a write-to-disk call site is added anywhere in the solution.

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

1. Checks the session-halt latch (`_sessionHalted`, `SessionHaltedException`). Nothing sets the latch any more (the drift halt was
   removed, see step 5), so this check is dead code awaiting a cleanup slice (`// TODO` in the method).
2. Refuses everything if the unrecoverable breaker has tripped (`IUnrecoverableBreaker.StateMessage`); this is the only session halt left.
3. Refuses any target covered by an open scoped operation ledger entry (`_ledger.IsBlocked`), returning a failed `ApplyChangesResult`.
4. Refuses a path given as both a write and a delete target.
5. (Removed 2026-11-09.) There is no external-drift check here any more: a target in `GetExternalFileChanges()` is not refused and
   does not latch the session. Drift on a file the writer read or edited is caught per agent by step 7b, and in-memory text that lags
   disk by step 7c. `ExternalFileDrift` lists the drift for visibility only; `Acknowledge` clears that list and never gates writes.
6. Optionally compile-validates the whole batch (`validateChanges`, via `ValidationEngine`) before taking either lock.
7. Takes `_solutionLock`, then (under it, in this order) `CheckConcurrentEditConflict` (refuses with
   `ToolErrorCode.ConcurrentEditRefused` when another known agent wrote a target after this call started) and
   `RevalidateAfterForeignWriteAsync` (a second compile gate when another agent wrote elsewhere), then the cross-process write
   lock (7a), then captures a **pre-image** of every target (null = file did not exist), used for
   `OperationItemRecord.BeforeSource` and `UndoLastApply`.
7a. Cross-process write lock (`CrossProcessWriteLock`, `Common/CrossProcessWriteLock.cs`): an OS-held `FileShare.None` lock on
   `<solution root>/.roslynsentinel/write.lock`, taken after the checks above and before pre-image capture, so the stale-read and
   `FoldPending` checks and the writes see a disk no other RoslynSentinel process can change. Wait 10 s
   (`CrossProcessWriteLockOptions.ChokepointWait`); if still busy the whole batch is refused with the non-latching
   `ToolErrorCode.WriteLockBusy` (every target in `FailedFiles`, summary naming the holder from the sidecar
   `write.lock.holder.json`, nothing written; `WriteRefusal.TryMap` maps it). Released in the method's `finally`, after the
   in-memory resync (step 11) and before `_solutionLock.Release()`, on success, refusal and exception alike. Root =
   `GetSolutionRoot()` or the `SetTestSolutionRoot` test seam; a null root (a `SetTestSolution` manager with no on-disk solution)
   takes no OS lock. Never held across compile validation (step 6 and `RevalidateAfterForeignWriteAsync`) or a full reload (the
   background reload for `needsFullReload` starts after release). Rules for changing this method (lock order, no re-entrancy):
   `reference_architecture_map.md`, "Write chokepoint". Detail and the other two acquisition points (debounce fold, fold-on-read):
   `reference_file_watcher_and_drift_detection.md` section 7.
7b. Stale-read check, only when the caller passes a `staleReadPolicy` other than `None` (the chokepoint's default is `None`;
   `exactRestore` is exempt): for each target with an observed-content entry under the writer's agent key, refuses
   the whole batch with `ToolErrorCode.StaleRead` if that hash differs from the disk pre-image or the in-memory document
   text. Non-latching. Targets with no entry are allowed. Opted in, by policy value:
   - `RefuseIfObservedStale` (an unobserved existing target also gets a `WriteChokepoint` Info finding "read them first"):
     `WriteFile` (ReplaceFile), `DeleteFile`, `ApplyDiff` (files and diff formats), `ApplyUnifiedDiff`, `ReplaceSnippet`, `ReplaceSnippetBatch`.
   - `RefuseIfObservedStaleQuiet` (same refusal, no unobserved finding): `ValidateAndApplyHelper` by default, so every engine tool
     that writes through it (`Member`, `RenameSymbol`, `MoveMember`, ...) is opted in with no edit at the tool; and the direct callers
     `AsyncifyTools`, `AsyncBatchEngine`, `CommentingTools`, `MsToolAugmentEngine` and `DeleteUnusedSymbol`.
   - Exempt (`None`): `UndoLastApply` (`exactRestore`), `CreateFile`, and `RetryFailedChangesAsync` (a cache replay in `Common`).
   `StaleReadPolicyRatchetTests` fails any production `ApplyProposedChangesAsync(` call outside `Common` that does not name
   `staleReadPolicy:` unless allowlisted (presence only, not the value). Tools turn the refusal into a `ResultError` with
   `WriteRefusal.TryMap` (`Common/WriteRefusal.cs`: `StaleRead`, `FoldPending`; at most 10 files named, rest in `StructuredDetail`);
   the Asyncify, batch-engine and commenting callers carry it in summary text instead.
   Details: `reference_file_watcher_and_drift_detection.md` section 5.
7c. `FoldPending` guard (`CheckFoldPendingAsync`), directly after 7b, for every policy including `None` (deletes included; not
   `exactRestore`): if a target's in-memory document text differs from the disk pre-image just captured, it is not written over.
   Ladder: exact equal -> proceed; equal after BOM, EOL and per-line trailing-whitespace normalization -> proceed with an Info
   finding; otherwise fold the refused files from disk inline (under the held `_solutionLock`) and refuse the whole batch once with
   `ToolErrorCode.FoldPending` ("is changing on disk; retry in a moment"). A path that still mismatches after the fold is marked
   in `_foldRefusedFiles`; its next write proceeds with a Warning finding that names `LoadSolution(forceReload: true)`. A target with
   no document or no disk pre-image is unchecked. Non-latching; `StaleRead` takes precedence.
8. EOL pre-pass (`ComputeEolPrePass`, see "EOL policy" below): for every target it computes the bytes to write (the whole file
   converted to the resolved EOL) and refuses with `ToolErrorCode.EolChangeRefused` only if the converted text is not a
   single line-ending style. Nothing is deleted or written before this pass finishes.
   In `ValidateAndApplyHelper` (not in the chokepoint), before the compile gate: when a
   caller passes `expectedEdits` (MoveMember does), `CollateralChangeGuard` refuses with
   `ToolErrorCode.CollateralChangeRefused` if a file's proposed text differs from its pre-image plus those `TextChange`s.
   Files with no pre-image (new files) or no entry are unchecked.
9. Processes deletes as their own pass, then writes each file: skips content byte-identical to the pre-image (compared to the
   EOL-converted bytes, so an EOL-only change is written, not dropped), optionally logs
   formatter divergence at Debug (observation only), preserves a UTF-8 BOM,
   marks `_internalChanges` (watcher-loop suppression), then writes through `FileIoHelper.WriteAllTextAsync`
   (per-path lock) with retry on `IOException` (`retryCount`, 500 ms apart). Failed content is cached in `_failedChangesCache`
   for `RetryFailedChanges`.
10. If `rollbackOnPartialFailure` and some files failed after others succeeded, restores the succeeded files to their pre-images
    (and the writer's EXISTING observed-content entries to the pre-image hashes; the restore never creates one). Inside the write
    loop each successful write refreshes only an existing entry; after the rollback decision, a batch that is still successful
    creates the writer's entry for every file it wrote (not for no-op writes), so engine and anchor writers are protected against a
    later external change to a file they wrote. A delete creates no entry and drops the path's entries for every agent.
11. Resyncs the in-memory workspace (`ApplyInMemoryDocumentUpdatesAsync`, bumps `_workspaceVersion`; this call never takes the OS
    lock, it runs under the one step 7a holds), invalidates the affected projects' compilation-cache entries (or all of them on a
    full reload), and returns `ApplyChangesResult`. The `finally` then releases the OS write lock (7a) and `_solutionLock`, in that
    order, and starts the background full reload if one is needed.

`FileIoHelper.WriteAllTextAsync` is the low-level primitive. Production callers (checked 2026-11-09): the chokepoint (write and
rollback restore), `EolFileWriter`, and `ToolTelemetryFlusher` (telemetry JSON, not workspace source).
`BareFileWriteRatchetTests` enforces the wider rule, see below.

### EOL policy

Every workspace write ends up with one line-ending style, chosen by the server, not by whichever tool built the text.

- **Startup arg `--eol auto|crlf|lf`** (default `auto`; fallback env var `ROSLYNSENTINEL_EOL`), parsed by
  `EolPolicyOptions.Configure(args)` into `EolPolicyOptions.Mode` (`EolMode.Auto/Crlf/Lf`, `Common/EolPolicyOptions.cs`). It is a static
  options class, set at each server entry point. This repo's launch script passes `--eol=crlf`.
- **`EolResolver.Resolve(mode, preImageText, editorConfigEndOfLine)`** (`Common/EolResolver.cs`) is pure and returns `"\r\n"` or `"\n"`.
  `crlf`/`lf` return that value and ignore everything else. `auto` uses a single-style pre-image (CRLF-only or LF-only), then the
  `.editorconfig` `end_of_line` for the path, then `Environment.NewLine`. A mixed, CR-only or newline-free pre-image and a new file
  therefore get the `.editorconfig`/platform value.
- **`.editorconfig` is read only in `auto` mode**, by `EditorConfigEolReader.Read(filePath)` (`Common/EditorConfigEolReader.cs`; walks up
  from the file's directory, cached per directory and extension, invalidated by `.editorconfig` timestamps). An explicit
  `--eol crlf|lf` wins over `.editorconfig`.
- **Whole-file conversion and a final-bytes check.** `EolWritePolicy.TryComputeContentToWrite` (`Common/EolWritePolicy.cs`, called by
  `ComputeEolPrePass`) converts the entire proposed text to the resolved EOL with `EolUtilities.NormalizeEol`, then counts the result
  (`EolChangeGuard.Count`) and refuses (`EolChangeRefused`) if the bytes it is about to write are not uniform. So text arriving in
  the other style (an agent's LF snippet in a CRLF file) is converted, not refused, and a mixed file converges on its first touch. There
  is no longer a "style flip" refusal, and `NormalizeEolOfChangedLines` is not on the write path.
- **Exemptions.** `UndoLastApply` passes `exactRestore: true`, which writes the pre-image back unchanged. The partial-failure rollback
  restore also writes the pre-image as it was. Consequence, accepted: in a foreign `auto` repo an undo or rollback can restore a file
  that was mixed before the change.
- **Attribution finding.** When the pre-image read from disk is mixed, or uniform in a style other than the resolved target, `ComputeEolPrePass`
  adds a `Finding("WriteChokepoint", "pre-image of '<file>' was written outside the chokepoint ... caller: <operationLabel>", Caution)` and
  logs a warning, then converts anyway. It is a record, not a refusal. The finding is not yet surfaced in tool results
  (`ApplyChangesResult.Findings`, open in TODO.md).
- **Test override.** `PersistentWorkspaceManager.EolModeOverride` (`EolMode?`) replaces `EolPolicyOptions.Mode` for one manager instance, so
  tests never touch the static.

**Engines get the EOL from the resolver, not from the source file.** `IEolResolver.ResolveEol(filePath, preImageText)` (`Common/IEolResolver.cs`) is
implemented by `PersistentWorkspaceManager` (and `FakeWorkspaceManager`); `IWorkspaceMutator` and `IWorkspaceReader` both extend it, so any
engine holding either can call it. `ResolveEolAsync(this IEolResolver, Document, ct)` (`Common/WorkspaceMutatorEolExtensions.cs`) resolves with the
document's path and current text as the pre-image. Engines pass the result as the `eol` argument of the `RoslynFormattingHelper` format
methods and of hand-built trivia; the required `eol` parameter replaced the old defaulted overloads. This only affects text returned to the
caller (previews, `UpdatedText`); the chokepoint converts whatever is finally written.

**Writes that cannot use the chokepoint** use `EolFileWriter` (`Common/EolFileWriter.cs`): `WriteAllTextResolvedAsync(path, content, preImage)`
applies the same `EolWritePolicy` and keeps an on-disk UTF-8 BOM, and `NormalizeNewFileAsync(path)` rewrites a file a CLI tool just created. Callers:
`MsToolAugmentEngine.FormatDocumentSafeAsync` (no-solution fallback), `SolutionManagementEngine.CreateProjectAsync` (`dotnet new` template files)
and the `DocumentationTools` `.md`/`.json`/`.yaml` writes.

**Ratchets** (all in `Tests.Basic`, counts may only shrink):

- `BareFileWriteRatchetTests`: fails on `File.WriteAll*/AppendAll*/Create/CreateText/Copy` in any production project outside `Common/FileIoHelper.cs`,
  with an allowlist of non-workspace writes keyed `<file>::<member>` plus a reason. Route a new workspace write through the chokepoint or `EolFileWriter`.
- `NewlineLiteralRatchetTests`: fails on `SyntaxFactory.LineFeed`, `CarriageReturnLineFeed`, `EndOfLine(...)` or an argument-less `NormalizeWhitespace()` in
  `Common` and `Engines.*` (syntax scan, so comments and strings never match), except `RoslynFormattingHelper.cs` and `EolUtilities.cs` and a
  baseline of 26 keys / 41 calls measured 2026-10-26. New code takes `eol` from `ResolveEol` instead.
- `EolAuditRatchetTests`: the `scripts/Get-EolAudit.ps1 -FailOn lf,mixed` check (a `.cs` with a lone LF or lone CR fails) as a test, so `RunTest` sees it;
  `scripts/build.ps1` runs the script itself.

### Callers (2026-10-04, non-test)

Most tools do not call the chokepoint raw: they call `Common/ValidateAndApplyHelper.ValidateAndApplyAsync`, which adds
compile validation, the collateral-change check, dry-run and a diff, then calls it.

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

### Sanctioned writes outside the chokepoint

`MsToolAugmentEngine.FormatDocumentSafeAsync(preview: false)` routes through the chokepoint when a solution is loaded and the
file is a tracked document. When no solution is loaded the chokepoint cannot run (it needs `CurrentSolution`), so it falls back to
`EolFileWriter.WriteAllTextResolvedAsync`. `SolutionManagementEngine.CreateProjectAsync` does the same for the template `.cs`
files `dotnet new` creates (`EolFileWriter.NormalizeNewFileAsync`). These two are the only workspace-source writes outside the chokepoint;
both apply the resolved EOL but get no drift tracking, pre-image capture or undo.

### Writers outside the chokepoint and the cross-process write lock

The OS write lock (step 7a) is taken only inside the chokepoint (and by the debounce fold and fold-on-read, which read, not write
source). A writer that never reaches the chokepoint has no solution root and keeps in-process exclusion only (`FilePathLock` inside
`FileIoHelper.WriteAllTextAsync`). Excluded, as of 2026-11-09:

| Writer | Why it is outside | `// TODO` marker |
| --- | --- | --- |
| `EolFileWriter` (`Common/EolFileWriter.cs`: `WriteAllTextResolvedAsync`, `NormalizeNewFileAsync`; callers `SolutionManagementEngine.CreateProjectAsync` and the `DocumentationTools` `.md`/`.json`/`.yaml` writes) | no solution root, never reaches the chokepoint | on the class (`EolFileWriter.cs`, above `public static class EolFileWriter`) |
| `MsToolAugmentEngine.FormatDocumentSafeAsync` no-solution-loaded fallback | the chokepoint needs `CurrentSolution`; it calls `EolFileWriter` | at the write call in `MsToolAugmentEngine.cs` |
| `FileIoHelper.WriteAllTextAsync` callers outside the chokepoint (today `EolFileWriter` and `ToolTelemetryFlusher`; the latter is non-workspace telemetry JSON) | the helper itself takes only the per-path in-process lock | NONE: `FileIoHelper.cs` carries no marker. The plan (Step 9b) called for one; it was not written. The two callers are covered by the `EolFileWriter` marker and by the fact that `ToolTelemetryFlusher` writes no source |

Also not covered: the operation blob write (`OperationBlobWriter`) happens after the chokepoint returns and releases the lock (accepted;
`proposal_cross_session_write_journal.md` closes it), and `Git` tool operations and shell or editor writes never take the lock.
A new writer of workspace source should go through the chokepoint, not add to this table.

### History

Three bypasses were found and fixed on 2026-08-22 (a raw write in `SortAndDeduplicateUsingsAsync`, a write-then-resync in
`FormatDocumentSafeAsync`, and a hand-rolled `UndoLastApply` revert loop). Each lacked drift refusal, pre-image capture, rollback
and watcher-loop suppression; the undo path in particular produced spurious external-drift warnings because `_internalChanges`
was never marked. The shared `ValidateAndApplyHelper` was extracted in commit `cb70952`.

## Usage

Adding a tool or engine that persists a source edit: build the `Dictionary<FilePathWrapper,string>` of new contents and call
`ValidateAndApplyHelper.ValidateAndApplyAsync` (preferred) or `ApplyProposedChangesAsync` directly. Never call `File.WriteAllText*`
on workspace source. `Tests.Basic/WriteChokepointGuardrailTests.cs` and `EolChokepointConversionTests.cs` cover EOL conversion and line-count
reporting of the shared path; `BareFileWriteRatchetTests` fails a new bare `File.Write*` in production code (see "EOL policy" above).

## Gotchas

- Drift no longer halts the session. A write can now be refused per call with `StaleRead` (the writer read or wrote a file that
  changed since) or `FoldPending` (the workspace text lags disk; retry once); both are non-latching and the message names the
  files. Only the unrecoverable breaker still halts every write.
- `WriteLockBusy` is a third non-latching refusal: another RoslynSentinel process holds `.roslynsentinel/write.lock`; nothing was
  written; retry in a moment. The wait (10 s) happens inside `_solutionLock`, so it also stalls this process's other write and read-fold
  operations. A write loop that throws still releases the lock through the `finally`, but no test proves that path (a `// TODO` marks it).
- A shell delete or edit of a tracked file bypasses the chokepoint and is reconciled later by the watcher; prefer `DeleteFile` and
  the edit tools so the workspace and observed-content entries stay consistent.
- A "successful" apply can be a no-op: identical or whitespace-only content is skipped and reported in `NoOpFiles`/the summary.
- The whole file is rewritten to the resolved EOL and an existing UTF-8 BOM is preserved, so the on-disk bytes can differ from the string a tool passed in.
  Under `--eol crlf|lf` the first touch of a mixed file therefore produces a whole-file EOL diff; `UpdatedText` returned by a tool may still carry the
  engine's EOL (the resolver value, or the source file's where an engine has not moved to `ResolveEol` yet).

## Not source-code writes

These write files but are outside the chokepoint by design:

- `Tools.Basic/DocumentationTools.cs` (`ProjectDoc` and its helpers): project documentation and state files under `docs/`
  (written through `EolFileWriter`, so they follow the EOL policy).
- `Common/OperationBlobWriter.cs`: forensic operation JSON used by `UndoLastApply` for pre-image lookup.
- `Common/MigrationLedger.cs` (`SaveAsync`): ledger JSON persistence.
- `Common/LargeResultHelper.cs`: offloaded oversized tool results under `.roslynsentinel/largeresults/`.
- `Server.Basic/ConsoleMode.cs`: `tool_list_*.json` and method-inventory dumps in the server bin folder.
- `Tools.Advanced/AsyncifyTools.cs` (`AsyncifyLoop`): debug-dump JSON.
- `Common/AgentLoop/ModelAgentRunner.cs`: agent transcripts and log sidecars.
- `Engines.Advanced/AntiPatternEngine.cs`: string literals containing `File.WriteAllText` as suggestion text, not calls.
