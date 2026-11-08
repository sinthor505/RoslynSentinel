# Reference: Code File Write Paths

**Status:** CURRENT 2026-10-26. The chokepoint step list and callers table were verified 2026-10-04; the EOL policy sections and steps 8-9 were re-verified against source on 2026-10-26 (plan_eol_policy_and_write_chokepoint.md Steps 4-8). Update when a write-to-disk call site is added anywhere in the solution.

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
   Recovery is `ExternalFileDrift(operation: List)` then `ExternalFileDrift(operation: Acknowledge)` (`ClearSessionHalt`).
6. Optionally compile-validates the whole batch (`validateChanges`, via `ValidationEngine`) before taking the lock.
7. Takes `_solutionLock`, then captures a **pre-image** of every target (null = file did not exist), used for
   `OperationItemRecord.BeforeSource` and `UndoLastApply`.
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
10. If `rollbackOnPartialFailure` and some files failed after others succeeded, restores the succeeded files to their pre-images.
11. Resyncs the in-memory workspace (`ApplyInMemoryDocumentUpdatesAsync`, bumps `_workspaceVersion`), invalidates the affected
    projects' compilation-cache entries (or all of them on a full reload), and returns `ApplyChangesResult`.

`FileIoHelper.WriteAllTextAsync` is the low-level primitive; in production code only the chokepoint and `EolFileWriter` call it
(`BareFileWriteRatchetTests` enforces the wider rule, see below).

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

- Drift on a target halts the whole session, not just that call. Check `git status` before treating it as real
  (the false-positive cross-check is in the project memory index).
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
