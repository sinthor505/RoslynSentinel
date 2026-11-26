using System.Text.Json;

using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging;

using RoslynSentinel.Common.SnippetMatching;
using RoslynSentinel.Engines.Basic;

namespace RoslynSentinel.Tools.Basic;

/// <summary>
/// Plain DI-constructed implementation backing the file-edit tools on WorkspaceTools. Method bodies moved
/// verbatim from WorkspaceTools (Decision 7 step 2).
///
/// CORRECTION vs. the plan doc's Decision 1 (flagged per Decision 7 step 1.5's audit mandate):
/// Decision 1 listed this class as 7 tools (ApplyDiffTool, ApplyUnifiedDiffTool, WriteFileTool, DeleteFileTool,
/// RetryFailedChangesTool, UndoChangeTool, ReadFileTool) with DiffEngine as a dependency. As of 2026-09-17,
/// WorkspaceTools.cs only actually still has 3 of those 7 live as [McpServerTool] methods
/// (RetryFailedChangesTool, UndoChangeTool, ReadFileTool) - the other 4 (ApplyDiffTool/ApplyUnifiedDiffTool/
/// WriteFileTool/DeleteFileTool) are live, separately-registered [McpServerTool] methods on a wholly
/// different, pre-existing class (WholeFileWriteTools.cs) that this plan does not name and
/// is out of scope to touch. DiffEngine is confirmed dead on WorkspaceTools (FindReferencesTool:
/// only the constructor assignment, zero live-method uses - its only "use" is inside a large
/// block-commented dead ApplyDiffWithConfirmationCode method) so it is dropped from this class
/// entirely rather than carried forward unused.
/// </summary>
public class WorkspaceFileEditImpl
{
    private readonly WriteToolAdviceHelper _writeAdvice;
    private readonly SymbolNavigationEngine _symbolNavigationEngine;
    private readonly ValidationEngine _validationEngine;
    private readonly IWorkspaceManager _workspaceManager;
    private readonly ILogger _logger;
    private readonly WorkspaceReadNavigationImpl _readNav;

    // Which tools this server can serve; null (unit tests, hosts without a surface) = treat every tool as callable.
    private readonly ActiveToolSurface? _activeToolSurface;

    // One memory per process, shared by every instance. The MCP tool pipeline builds a fresh tool-class instance per call
    // (McpToolSchemaPatcher.CreateTarget uses ActivatorUtilities, not the registered singleton), and the legacy
    // WorkspaceTools facade does `new WorkspaceFileEditImpl(...)` in its constructor, so an instance field was empty at the
    // next call (eval run 20261109-215824-979 T11 -> T12: the note never fired). A static, rather than a DI singleton passed
    // through, because it also covers the facade, tests and any host that wires the Impl differently. Entries are keyed by
    // full file path (unique per workspace, so tests do not leak into each other), bounded per file, and dropped on a
    // successful write.
    private static readonly RejectedEditMemory SharedRejectedEdits = new();

    private readonly RejectedEditMemory _rejectedEdits = SharedRejectedEdits;

    // Same reasoning as SharedRejectedEdits: the successful single-edit calls must be visible to the next per-call instance.
    private static readonly AppliedEditMemory SharedAppliedEdits = new();

    private readonly AppliedEditMemory _appliedEdits = SharedAppliedEdits;

    // Same reasoning as SharedRejectedEdits: tool-class instances are per call, so the registry of running calls has to be process-wide.
    private static readonly InFlightEditMemory SharedInFlightEdits = new();

    private readonly InFlightEditMemory _inFlightEdits = SharedInFlightEdits;

    // 10-chain run 301 (host 113, 2026-11-10) sent the same two rejected edits for 12 turns while its reasoning said to use
    // RenameSymbol; the identical message gave no sign that it was repeating itself.
    // LoopAssistTracker.RepeatNote (AgentLoop, which cannot see this class) matches this text; keep the two in step.
    private const string RepeatedRejectionNote = "You already sent this exact edit and it was rejected the same way.";
    private const string RepeatedRejectionFollowNextStep = " The next step below lists the options.";
    private const string RepeatedRejectionChangeApproach = " Change the edit to fix the errors below, or take a different approach.";

    private const string RejectedOriginNote = "This oldContent comes from your rejected edit's newContent. That edit was not applied, so the file still has the old text: read it again before editing.";

    /// <summary>
    /// The edit tool a "split into smaller calls" or "use X instead" sentence may name: ReplaceSnippet when callable, else
    /// EditSnippets (the eval arm that hides ReplaceSnippet but runs the same code), else ReplaceSnippet.
    /// </summary>
    private string CallableSnippetTool()
        => _activeToolSurface is null || _activeToolSurface.IsCallable("ReplaceSnippet") || !_activeToolSurface.IsCallable("EditSnippets")
            ? "ReplaceSnippet"
            : "EditSnippets";

    /// <summary>Call hints for the tools that edit an existing file, limited to the ones this server can serve (see <see cref="CallableSnippetTool"/>).</summary>
    private List<string> ExistingFileEditToolHints()
    {
        var hints = new List<string>();
        if (_activeToolSurface is null || _activeToolSurface.IsCallable("Member"))
            hints.Add(ToolCallHint.Call("Member", ("operation", "replace")));
        hints.Add(ToolCallHint.Call(CallableSnippetTool()));
        return hints;
    }

    public WorkspaceFileEditImpl(IWorkspaceManager workspaceManager, WorkspaceReadNavigationImpl readNav, ILogger logger, ValidationEngine validationEngine, SymbolNavigationEngine symbolNavigationEngine, WriteToolAdviceHelper writeAdvice, ActiveToolSurface? activeToolSurface = null)
    {
        _activeToolSurface = activeToolSurface;
        _workspaceManager = workspaceManager;
        _readNav = readNav;
        _logger = logger;
        _validationEngine = validationEngine;
        _symbolNavigationEngine = symbolNavigationEngine;
        _writeAdvice = writeAdvice;
    }

    // Per-file memory of the newContent of recent ReplaceSnippet edits the compile gate REJECTED. After a rejection a model
    // often builds its next oldContent from its own rejected newContent, believing the edit applied (eval run
    // 20261109-202110-210: four "oldContent was not found" errors in a row); a later not-found whose oldContent comes from
    // here gets a note saying so. Bounded per file, and a file's entries are dropped when it is successfully written.
    // NOTE: shared process-wide through SharedRejectedEdits, so it spans calls and every WorkspaceFileEditImpl instance.
    private sealed class RejectedEditMemory
    {
        private const int MaxEntriesPerFile = 3;
        private readonly Dictionary<string, List<(string? OldContent, string NewContent)>> _byFile = new(StringComparer.OrdinalIgnoreCase);
        private readonly object _gate = new();

        private static string Key(string path)
        {
            try
            {
                return Path.GetFullPath(path);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return path;
            }
        }

        private static string NormalizeEol(string text) => text.Replace("\r\n", "\n").Replace('\r', '\n');

        private static IEnumerable<string> TrimmedLines(string text)
            => NormalizeEol(text).Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0);

        public void Remember(string path, string? oldContent, string? newContent)
        {
            // A pure deletion leaves nothing the model could mistake for file text.
            if (string.IsNullOrWhiteSpace(newContent))
                return;
            lock (_gate)
            {
                var key = Key(path);
                if (!_byFile.TryGetValue(key, out var entries))
                    _byFile[key] = entries = [];
                entries.Add((oldContent, newContent));
                while (entries.Count > MaxEntriesPerFile)
                    entries.RemoveAt(0);
            }
        }

        public void Forget(string path)
        {
            lock (_gate)
                _byFile.Remove(Key(path));
        }

        /// <summary>
        /// File name to the remembered rejected newContent (joined) of every file except <paramref name="excludedPaths"/>. For the edit-impact analyzer: a name the current
        /// edit uses that only one of these declares comes from a sibling call that was rejected and so never applied (parallel calls cannot see each other).
        /// </summary>
        public Dictionary<string, string> OtherFilesNewContent(IEnumerable<string> excludedPaths)
        {
            var excluded = new HashSet<string>(excludedPaths.Select(Key), StringComparer.OrdinalIgnoreCase);
            lock (_gate)
                return _byFile
                    .Where(kv => !excluded.Contains(kv.Key))
                    .ToDictionary(kv => kv.Key, kv => string.Join("\n", kv.Value.Select(e => e.NewContent)), StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// True when the edit equals a remembered rejected edit for the file: same oldContent AND same newContent, ignoring
        /// line endings. The same newContent under a different oldContent is a different edit, not a repeat.
        /// </summary>
        public bool IsRepeat(string path, string? oldContent, string? newContent)
        {
            if (string.IsNullOrWhiteSpace(newContent))
                return false;
            var normalizedOld = NormalizeEol(oldContent ?? "");
            var normalizedNew = NormalizeEol(newContent);
            lock (_gate)
                return _byFile.TryGetValue(Key(path), out var entries)
                    && entries.Any(e => NormalizeEol(e.OldContent ?? "") == normalizedOld && NormalizeEol(e.NewContent) == normalizedNew);
        }

        /// <summary>
        /// True when the not-found <paramref name="oldContent"/> looks copied from a remembered rejected newContent: it holds
        /// at least one line the file lacks, and every such line sits in one remembered newContent. An ordinary typo
        /// produces lines no rejected newContent holds, so it gets no note.
        /// </summary>
        public bool Explains(string path, string oldContent, string fileText)
        {
            List<string> remembered;
            lock (_gate)
            {
                if (!_byFile.TryGetValue(Key(path), out var entries) || entries.Count == 0)
                    return false;
                remembered = entries.Select(e => e.NewContent).ToList();
            }

            var fileLines = new HashSet<string>(TrimmedLines(fileText), StringComparer.Ordinal);
            var missing = TrimmedLines(oldContent).Where(l => !fileLines.Contains(l)).Distinct().ToList();
            if (missing.Count == 0)
                return false;
            return remembered.Any(rejected =>
            {
                var rejectedLines = new HashSet<string>(TrimmedLines(rejected), StringComparer.Ordinal);
                return missing.All(rejectedLines.Contains);
            });
        }
    }

    // The last few SUCCESSFUL single-edit ReplaceSnippet calls per file. A model that sends the same edit twice in one turn
    // (action replace, then apply) reaches the second call after the first has landed; without this it fails "oldContent was
    // not found" and burns its failure budget (eval batch H4 run 1). Bounded per file. Not dropped on a write: the write
    // itself is what makes a repeat a no-op. FindDuplicate also checks that the file still holds what was written for that edit (the re-indented newContent).
    // NOTE: shared process-wide through SharedAppliedEdits, like RejectedEditMemory.
    private sealed class AppliedEditMemory
    {
        private const int MaxEntriesPerFile = 3;
        private readonly Dictionary<string, List<(string OldContent, string NewContent, string WrittenContent, string ChangeId)>> _byFile = new(StringComparer.OrdinalIgnoreCase);
        private readonly object _gate = new();

        private static string Key(string path)
        {
            try
            {
                return Path.GetFullPath(path);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return path;
            }
        }

        private static string NormalizeEol(string text) => text.Replace("\r\n", "\n").Replace('\r', '\n');

        public void Remember(string path, string? oldContent, string? newContent, string changeId, string? writtenContent = null)
        {
            // A pure deletion leaves nothing a repeat could be matched against.
            if (string.IsNullOrWhiteSpace(newContent))
                return;
            lock (_gate)
            {
                var key = Key(path);
                if (!_byFile.TryGetValue(key, out var entries))
                    _byFile[key] = entries = [];
                entries.Add((oldContent ?? string.Empty, newContent, writtenContent ?? newContent, changeId));
                while (entries.Count > MaxEntriesPerFile)
                    entries.RemoveAt(0);
            }
        }

        /// <summary>
        /// The changeId of a remembered edit with the same oldContent AND newContent (ignoring line endings), when the file
        /// still holds what was written for that edit (the re-indented newContent); null otherwise. Newest entry wins.
        /// </summary>
        public string? FindDuplicate(string path, string? oldContent, string? newContent, string fileText)
        {
            if (string.IsNullOrWhiteSpace(newContent))
                return null;
            var normalizedOld = NormalizeEol(oldContent ?? string.Empty);
            var normalizedNew = NormalizeEol(newContent);
            var normalizedFile = NormalizeEol(fileText);
            lock (_gate)
            {
                if (!_byFile.TryGetValue(Key(path), out var entries))
                    return null;
                for (var i = entries.Count - 1; i >= 0; i--)
                {
                    var entry = entries[i];
                    if (NormalizeEol(entry.OldContent) == normalizedOld && NormalizeEol(entry.NewContent) == normalizedNew
                        && normalizedFile.Contains(NormalizeEol(entry.WrittenContent), StringComparison.Ordinal))
                        return entry.ChangeId;
                }
                return null;
            }
        }
    }

    // The newContent of the ReplaceSnippet calls that are running right now, per file. A parallel call that is validated while another is still in flight cannot see
    // that call's edit (each is checked on its own against the committed solution), so a name only the other call declares looks missing; the edit-impact analyzer
    // consults this beside RejectedEditMemory (which only knows calls that already finished and were rejected) to attribute that error to the other edit instead of leaving
    // it bare. An entry lives from the point the call has resolved its file(s) until the call ends, success or not. Process-wide like SharedRejectedEdits; the analyzer keeps
    // only the paths that are documents of its own solution.
    private sealed class InFlightEditMemory
    {
        private readonly Dictionary<Guid, List<(string Path, string NewContent)>> _byCall = new();
        private readonly object _gate = new();

        private static string Key(string path)
        {
            try
            {
                return Path.GetFullPath(path);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return path;
            }
        }

        /// <summary>Registers this call's edits (file path and newContent each); dispose the result when the call ends.</summary>
        public IDisposable Register(IEnumerable<(string Path, string NewContent)> edits)
        {
            var id = Guid.NewGuid();
            lock (_gate)
                _byCall[id] = edits.Where(e => !string.IsNullOrWhiteSpace(e.NewContent)).Select(e => (Key(e.Path), e.NewContent)).ToList();
            return new Registration(this, id);
        }

        /// <summary>Full file path to the newContent (joined) of every in-flight edit of a file other than <paramref name="excludedPaths"/>.</summary>
        public Dictionary<string, string> OtherFilesNewContent(IEnumerable<string> excludedPaths)
        {
            var excluded = new HashSet<string>(excludedPaths.Select(Key), StringComparer.OrdinalIgnoreCase);
            lock (_gate)
                return _byCall.Values
                    .SelectMany(edits => edits)
                    .Where(e => !excluded.Contains(e.Path))
                    .GroupBy(e => e.Path, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => string.Join("\n", g.Select(e => e.NewContent)), StringComparer.OrdinalIgnoreCase);
        }

        private void Remove(Guid id)
        {
            lock (_gate)
                _byCall.Remove(id);
        }

        private sealed class Registration(InFlightEditMemory owner, Guid id) : IDisposable
        {
            public void Dispose() => owner.Remove(id);
        }
    }

    public async Task<SentinelCallToolResult<object>> RetryFailedChanges(ToolCallReason reason, List<string>? specificFiles = null,
        int retryCount = 3, CancellationToken cancellationToken = default)
    {
        try
        {
            return new SentinelCallToolResult<object>()
            {
                IsError = false,
                SuccessData = await _workspaceManager.RetryFailedChangesAsync(specificFiles, retryCount, cancellationToken)
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "RetryFailedChanges failed");
            return new SentinelCallToolResult<object>()
            {
                IsError = true,
                ErrorData = ToolErrorMapper.ToResultError(ex, _workspaceManager, "RetryFailedChanges")
            };
        }
    }

    public async Task<SentinelCallToolResult<object>> UndoChange(ToolCallReason reason, string changeId, CancellationToken cancellationToken = default)
    {
        try
        {
            var solutionRoot = _workspaceManager.GetSolutionRoot();
            var blobPath = OperationBlobWriter.FindBlobPath(changeId, solutionRoot);
            if (blobPath == null)
            {
                return new SentinelCallToolResult<object>()
                {
                    IsError = true,
                    ErrorData = new ResultError("NoOperationBlobFound",
                        $"No operation blob found for changeId '{changeId}' under .roslynsentinel/operations/. " +
                        "This does not mean the change failed - it may well be on disk. It means no undo record " +
                        "exists for that id here. Check the id against the value the applying tool returned, and " +
                        "that this is the same server session and solution; a changeId from an earlier session or " +
                        "a different solution root will not resolve. If the applying tool reported the change as " +
                        "'not reversible', there is no undo record to find and the change must be reverted manually. " +
                        $"Operation blobs older than {RuntimeStateRetention.OperationsMaxAge.TotalDays:0} days, and all but the newest {RuntimeStateRetention.OperationsMaxCount}, are deleted when a solution loads (the newest 24 hours are always kept).")
                };
            }

            var json = await File.ReadAllTextAsync(blobPath, cancellationToken);
            var doc = JsonSerializer.Deserialize<JsonElement>(json);
            var allItems = doc.GetProperty("items").EnumerateArray().Select(e => JsonSerializer.Deserialize<OperationItemRecord>(e.GetRawText())!).ToList();
            OperationItemRecord.ResolveAfterSources(allItems);
            // Last-only rule: refuse when a newer, non-undone operation blob touched any of these files; the drift guard below still covers writes that left no blob.
            var targetPaths = allItems.Where(r => r.Outcome == ItemRecordOutcome.Succeeded).Select(r => r.FilePath).ToList();
            if (solutionRoot != null && targetPaths.Count > 0)
            {
                var newerWriters = UndoLastOnlyGuard.FindNewerWriters(solutionRoot, blobPath, changeId, targetPaths);
                if (newerWriters is not null)
                {
                    return new SentinelCallToolResult<object>()
                    {
                        IsError = true,
                        ErrorData = new ResultError(ToolErrorCode.PreconditionNotMet, newerWriters)
                    };
                }
            }
            // An item is revertable with its pre-image (restore) or when the blob recorded it as created (delete); a null pre-image alone is not enough, it can also mean the pre-image read failed.
            var revertable = allItems.Where(r => r.Outcome == ItemRecordOutcome.Succeeded && (r.BeforeSource != null || r.Created)).ToList();
            if (revertable.Count == 0)
            {
                return new SentinelCallToolResult<object>()
                {
                    IsError = true,
                    ErrorData = new ResultError("NoReversibleItems",
                        $"The operation blob for changeId '{changeId}' was found, but none of its items carry the " +
                        "original file contents needed to revert. The change itself completed - this is a gap in " +
                        "what was recorded, not a failed apply, and it is most common for operations that renamed " +
                        "or created files rather than editing them in place. Revert manually (e.g. via version " +
                        $"control); {ToolCallHint.Call("GetOperationDetail", ("changeId", changeId))} shows exactly which files were touched.")
                };
            }

            var undoConflict = UndoConcurrentEditConflict(revertable);
            if (undoConflict is not null)
            {
                return new SentinelCallToolResult<object>()
                {
                    IsError = true,
                    ErrorData = new ResultError(ToolErrorCode.ConcurrentEditRefused, undoConflict)
                };
            }

            var diskDrift = UndoDiskDriftConflict(revertable, solutionRoot, changeId);
            if (diskDrift is not null)
            {
                return new SentinelCallToolResult<object>()
                {
                    IsError = true,
                    ErrorData = new ResultError(ToolErrorCode.ConcurrentEditRefused, diskDrift)
                };
            }

            var failed = new List<string>();
            var revertChanges = new DocumentChangeSet();
            // Created files are deleted by the undo; one already gone from disk (the drift guard let it through) has nothing left to delete.
            var deletePaths = new List<FilePathWrapper>();
            var alreadyAbsent = new List<string>();
            foreach (var item in revertable)
            {
                if (solutionRoot != null && !item.FilePath.StartsWith(solutionRoot, StringComparison.OrdinalIgnoreCase))
                {
                    failed.Add($"{item.FilePath}: outside solution root, skipped");
                    continue;
                }

                if (item.Created)
                {
                    var createdPath = new FilePathWrapper(item.FilePath);
                    if (File.Exists(createdPath.Absolute))
                    {
                        deletePaths.Add(createdPath);
                    }
                    else
                    {
                        alreadyAbsent.Add(item.FilePath);
                    }

                    continue;
                }

                // TODO UndoChange reverts multi-item files last-write-wins (the last item's BeforeSource is restored); AsyncifyTools makes every item of a file carry the first BeforeSource to compensate
                revertChanges[item.FilePath] = item.BeforeSource!;
            }

            ApplyChangesResult? revertResult = null;
            var reverted = new List<string>();
            var deleted = new List<string>();
            var noOpFiles = new List<string>();
            // Line delta of each revert (keyed by absolute path), measured against the file's text on disk before the write.
            var revertDeltas = new Dictionary<string, FileLineChange>(StringComparer.OrdinalIgnoreCase);
            if (revertChanges.Count > 0 || deletePaths.Count > 0)
            {
                foreach (var (revertPath, restoredText) in revertChanges)
                {
                    if (!File.Exists(revertPath.Absolute))
                    {
                        continue;
                    }
                    revertDeltas[revertPath.Absolute] = FileLineChange.Compute(revertPath.Absolute, await File.ReadAllTextAsync(revertPath.Absolute, cancellationToken), restoredText);
                }

                // exactRestore: this writes back a pre-image the server captured itself, so it bypasses the
                // EOL-change guard and EOL normalization (a mixed-EOL pre-image over a since-normalized file would
                // otherwise be refused as an EOL change) and writes the captured bytes verbatim.
                // validateChanges: the restored tree (restores plus the removal of created files) is compiled before anything is written, like every other edit tool; deletePaths join the same atomic call.
                revertResult = await _workspaceManager.ApplyProposedChangesAsync(
                    revertChanges, validateChanges: true, rollbackOnPartialFailure: true, cancellationToken: cancellationToken, deletePaths: deletePaths, exactRestore: true);
                if (revertResult.IsError && revertResult.ValidationResult != null)
                {
                    // Compile gate refusal: nothing was written; return before RecordUndo/MarkReverted/MarkUndone.
                    return new SentinelCallToolResult<object>()
                    {
                        IsError = true,
                        ErrorData = await ComposeUndoValidationRefusalAsync(changeId, revertChanges, revertResult.ValidationResult, deletePaths, cancellationToken)
                    };
                }

                var noOpSet = new HashSet<string>(revertResult.NoOpFiles ?? [], StringComparer.OrdinalIgnoreCase);
                var deleteSet = new HashSet<string>(deletePaths.Select(p => (string)p), StringComparer.OrdinalIgnoreCase);
                foreach (var path in revertResult.SucceededFiles)
                {
                    if (noOpSet.Contains(path))
                    {
                        noOpFiles.Add(path);
                    }
                    else if (deleteSet.Contains(path))
                    {
                        deleted.Add(path);
                    }
                    else
                    {
                        reverted.Add(path);
                    }
                }
                foreach (var (path, error) in revertResult.FailedFiles)
                {
                    failed.Add($"{path}: {error}");
                }
                if (revertResult.IsError)
                {
                    // Refused before any write (e.g. ScopedLedgerBlocked): return before RecordUndo so the ledger keeps these fixes marked as applied.
                    var refusedFailures = failed.Count > 0 ? $" Failures: {string.Join("; ", failed)}" : "";
                    return new SentinelCallToolResult<object>()
                    {
                        IsError = true,
                        ErrorData = new ResultError(revertResult.Code!, $"{revertResult.Summary} Nothing was reverted.{refusedFailures}")
                    };
                }
            }

            // Unconditional: the ledger itself sorts out whether changeId matches an individual
            // fix's entry or the changeId that opened the ledger (cascades to every entry in that
            // case) - see ScopedOperationLedgerEngine.RecordUndo. A no-op when no ledger is open
            // or changeId matches nothing tracked.
            ((IScopedOperationLedger)_workspaceManager).RecordUndo(changeId);

            // The undone change's symbol effects ("call X renamed Y") no longer hold. Marked only when files were actually put back (or
            // already matched), and only effects stamped with this changeId at blob write; an apply that wrote no blob has none.
            if (reverted.Count + noOpFiles.Count + deleted.Count + alreadyAbsent.Count > 0)
            {
                (_workspaceManager as IEditEffectIndexProvider)?.EditEffects.MarkReverted(changeId);
                // On disk too: the next solution load seeds the index from blobs, and a blob does not otherwise say it was undone.
                OperationBlobWriter.MarkUndone(blobPath, _logger);
            }

            var lineChangeEntries = reverted
                .Where(p => revertDeltas.ContainsKey(p))
                .Select(p => $"{Path.GetFileName(p)} +{revertDeltas[p].LinesAdded}/-{revertDeltas[p].LinesRemoved}")
                .ToList();
            var lineChangePart = lineChangeEntries.Count > 0
                ? $" Lines changed by the revert: {string.Join(", ", lineChangeEntries)}."
                : "";
            var failedPart = failed.Count > 0 ? $" Failures: {string.Join("; ", failed)}" : "";
            var noOpPart = noOpFiles.Count > 0
                ? $" ({noOpFiles.Count} already matched pre-apply state - no change needed: {string.Join(", ", noOpFiles)})"
                : "";
            var absentPart = alreadyAbsent.Count > 0
                ? $" ({alreadyAbsent.Count} created file(s) already absent - nothing to delete: {string.Join(", ", alreadyAbsent)})"
                : "";
            // The text shape for restored files only is kept when no created file was deleted.
            var deletedPart = deleted.Count > 0 ? $"; deleted {deleted.Count} created files" : "";
            return new SentinelCallToolResult<object>()
            {
                IsError = false,
                SuccessData = $"Reverted {reverted.Count} files{deletedPart}{noOpPart}{absentPart}. Files: {string.Join(", ", reverted.Concat(deleted))}{lineChangePart}{failedPart}",
                Findings = revertResult?.Findings ?? []
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "UndoChange failed for '{ChangeId}'", changeId);
            return new SentinelCallToolResult<object>()
            {
                IsError = true,
                ErrorData = ToolErrorMapper.ToResultError(ex, _workspaceManager, "UndoChange")
            };
        }
    }

    /// <summary>
    /// The <see cref="ToolErrorCode.ValidationFailed"/> error of an undo whose restored tree (restored pre-images plus the removal of created files) fails the compile gate.
    /// Nothing was written. Facts and options only: the new compiler errors (with the edit-impact attribution when one applies), then what can be done about them.
    /// </summary>
    private async Task<ResultError> ComposeUndoValidationRefusalAsync(
        string changeId, IReadOnlyDictionary<FilePathWrapper, string> restored, DiagnosticReport validation, IReadOnlyCollection<FilePathWrapper> deletePaths, CancellationToken cancellationToken)
    {
        var errorCount = validation.Diagnostics.Count(d => d.Severity == "Error");
        var errors = await CompilerErrorLookupHelper.DescribeAsync(validation, _symbolNavigationEngine, cancellationToken, isToolActive: _activeToolSurface is null ? null : _activeToolSurface.IsCallable);
        return await EditImpactGate.RejectionAsync(_workspaceManager, restored, validation, _activeToolSurface,
            $"Undo refused: restoring changeId '{changeId}' would introduce {errorCount} new compiler error(s); nothing was written. ",
            "Compiler errors the undo would introduce:\n[COMPILER ERROR]\n" + errors +
            "\nOptions: the dependent code can be edited by hand first, or the dependent change undone first (newest first).",
            cancellationToken, deletePaths: deletePaths);
    }

    /// <summary>
    /// Step 7 undo guard. Refuses the whole undo when a file it would revert was changed after this change was applied,
    /// by an agent other than the caller, per the edit attribution ledger (EditAttributionLedger, not the scoped
    /// operation ledger). The refusal covers the whole undo, never a single file, because the revert result reports
    /// failures as IsError=false and RecordUndo runs unconditionally. Calls with no agent id are never refused.
    /// </summary>
    private string? UndoConcurrentEditConflict(IReadOnlyList<OperationItemRecord> items)
    {
        try
        {
            var callerId = ToolCallContext.Current?.AgentId;
            if (callerId is null)
            {
                return null;
            }

            var ledger = (_workspaceManager as IEditAttributionLedgerProvider)?.EditAttributions;
            if (ledger is null)
            {
                return null;
            }

            var clauses = new List<string>();
            string? firstPath = null;
            foreach (var item in items)
            {
                var applierId = item.AppliedByAgentId;
                if (applierId is null)
                {
                    continue;
                }

                var latest = ledger.GetLatest(item.FilePath);
                if (latest is null
                    || latest.AgentId is null
                    || string.Equals(latest.AgentId, applierId, StringComparison.Ordinal))
                {
                    continue;
                }

                clauses.Add($"{item.FilePath} was changed after agent '{applierId}' applied this change; last changed by {EditAttributionWording.Describe(latest, DateTime.UtcNow, ToolCallContext.Current?.SessionId)}");
                firstPath ??= item.FilePath;
            }

            if (clauses.Count == 0 || firstPath is null)
            {
                return null;
            }

            return $"Undo refused, nothing was reverted: {string.Join("; ", clauses)}. Reverting would overwrite that work. Next: "
                + ToolCallHint.Call("ReadFile", ("filePath", firstPath))
                + " to see the current content, then edit by hand. MULTI-AGENT: if you did not expect another agent to touch this file, report it.";
        }
        catch (Exception ex)
        {
            if (_logger.IsEnabled(LogLevel.Warning))
            {
                _logger.LogWarning(ex, "Undo concurrent edit check failed; the undo proceeds.");
            }

            return null;
        }
    }

    /// <summary>
    /// Undo guard on the file text itself. Refuses the whole undo when, for a revertable item whose blob recorded the text the
    /// operation wrote (AfterSource), the file on disk now differs from that text (EOL style and a leading BOM are ignored) or is
    /// gone (a created item that is gone is not drift: nothing is left to delete). This catches every later change whatever its source: another agent, the same agent, a raw disk edit, or a change
    /// made before a server restart. An item with no AfterSource (an older blob, or a tool that builds its own items) is not
    /// checked. Items outside the solution root are not checked because the undo skips them. The writer is named when the edit
    /// attribution ledger or the operation blobs know one.
    /// </summary>
    private string? UndoDiskDriftConflict(IReadOnlyList<OperationItemRecord> items, string? solutionRoot, string changeId)
    {
        try
        {
            var drifted = new List<(string Path, bool Missing)>();
            foreach (var item in items)
            {
                if (item.AfterSource is null
                    || (solutionRoot != null && !item.FilePath.StartsWith(solutionRoot, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                var absolute = new FilePathWrapper(item.FilePath).Absolute;
                if (!File.Exists(absolute))
                {
                    // A created file that is already gone has nothing left to delete: the undo reports it as already absent instead of refusing.
                    if (!item.Created)
                    {
                        drifted.Add((item.FilePath, true));
                    }

                    continue;
                }

                if (!string.Equals(
                        NormalizeForUndoCompare(File.ReadAllText(absolute)),
                        NormalizeForUndoCompare(item.AfterSource),
                        StringComparison.Ordinal))
                {
                    drifted.Add((item.FilePath, false));
                }
            }

            if (drifted.Count == 0)
            {
                return null;
            }

            var driftedPaths = drifted.Select(d => d.Path).ToList();
            var ledger = (_workspaceManager as IEditAttributionLedgerProvider)?.EditAttributions;
            var writers = EditAttributionWording.LastWriters(ledger, solutionRoot, driftedPaths, DateTime.UtcNow);
            var writerPart = writers.Count == 0
                ? string.Empty
                : " Last write the server recorded: "
                  + string.Join("; ", writers.Select(w => $"{w.Key} by {EditAttributionWording.Describe(w.Value, DateTime.UtcNow, ToolCallContext.Current?.SessionId)}"))
                  + ".";
            var firstPath = driftedPaths[0];
            return $"Undo refused, nothing was reverted: the text on disk now differs from what change '{changeId}' wrote for {drifted.Count} file(s): {string.Join("; ", drifted.Select(d => d.Missing ? $"{d.Path} (missing on disk)" : d.Path))}. "
                + "Reverting would overwrite whatever was written there since." + writerPart
                + " Options: " + ToolCallHint.Call("ReadFile", ("filePath", firstPath))
                + " to see the current content and edit it back by hand, or "
                + ToolCallHint.Call("GetOperationDetail", ("changeId", changeId))
                + " for the operation's recorded before text.";
        }
        catch (Exception ex)
        {
            if (_logger.IsEnabled(LogLevel.Warning))
            {
                _logger.LogWarning(ex, "Undo disk drift check failed; the undo proceeds.");
            }

            return null;
        }
    }

    /// <summary>EOL-insensitive, BOM-insensitive form of a file text, for comparing disk text with a blob's AfterSource.</summary>
    private static string NormalizeForUndoCompare(string text)
    {
        var withoutBom = text.Length > 0 && text[0] == '﻿' ? text.Substring(1) : text;
        return EolUtilities.NormalizeEol(withoutBom, "\n");
    }

    /// <summary>FileNotFound for ReadFile. <paramref name="isCallable"/> says whether a tool name can be called on this
    /// server (null = every tool can); ListSolutionItems is named only when it can.</summary>
    internal static ResultError BuildFileNotFoundError(Microsoft.CodeAnalysis.Solution solution, string normalizedPath, Func<string, bool>? isCallable = null)
    {
        // A file the server itself moved (RenameSymbol, SyncTypeAndFilename) is named at its new path instead of hedged.
        var moved = DocumentLookup.FindMovedTo(solution, normalizedPath);
        return new("FileNotFound", FileNotFoundMessage.Build(normalizedPath, DocumentLookup.FindFileCandidates(solution, normalizedPath), solution.Projects.Count(), isCallable, DocumentLookup.FindDeclaringTypeFiles(solution, normalizedPath), moved?.NewPath, moved?.ToolName, DocumentLookup.FindFolderFileNames(solution, normalizedPath)));
    }

    private async Task<SentinelCallToolResult<object>> ReadFileWithoutSolutionAsync(string absolutePath, int? startLine, int? endLine, CancellationToken cancellationToken, LineEndingDisplay showLineEndings = LineEndingDisplay.None)
    {
        try
        {
            var diskContent = await FileIoHelper.ReadAllTextIfExistsAsync(absolutePath, cancellationToken);
            if (diskContent == null)
            {
                return new SentinelCallToolResult<object>()
                {
                    IsError = true,
                    ErrorData = new ResultError("FileNotFound", $"'{Path.GetFileName(absolutePath)}' does not exist at '{absolutePath}' (only that exact path was checked: no solution is loaded, so no search by file name or similar name ran). {SolutionNotLoadedMessage.Build(_workspaceManager.LoadState)}")
                };
            }

            var sourceText = SourceText.From(diskContent);
            var totalLines = sourceText.Lines.Count;
            var explanation = "No solution is loaded; read directly from disk (no outline or offload available). "
                + (_workspaceManager.LoadState is { IsFreshStartup: true } ? $"This server was freshly (re)started and has not loaded a solution yet; call {ToolCallHint.Call("LoadSolution")} to restore full functionality. " : string.Empty);
            var statusMessage = showLineEndings == LineEndingDisplay.All
                ? explanation.TrimEnd() + " source is annotated with line-ending markers and is not verbatim."
                : explanation.TrimEnd();

            if (startLine.HasValue || endLine.HasValue)
            {
                int from = Math.Max(1, startLine ?? 1);
                int to = Math.Min(totalLines, endLine ?? totalLines);
                if (from <= totalLines && from > to)
                {
                    return new SentinelCallToolResult<object>()
                    {
                        IsError = true,
                        ErrorData = InvertedRangeError(from, to)
                    };
                }
                if (from > totalLines || from > to)
                {
                    return new SentinelCallToolResult<object>()
                    {
                        IsError = true,
                        ErrorData = new ResultError(ToolErrorCode.InvalidArgument, $"ReadFile: requested range {from}-{to} is out of bounds for a {totalLines}-line file.")
                    };
                }

                // The requested range is returned whole; an over-budget range is paged by lines by the result marshaller.
                // instance ResolveFromWire yields an empty path while no solution is loaded
                _workspaceManager.RecordObserved(FilePathWrapper.ResolveFromWire(absolutePath, null), diskContent);
                var slice = sourceText.ToString(TextSpan.FromBounds(sourceText.Lines[from - 1].Start, sourceText.Lines[to - 1].EndIncludingLineBreak));
                var rangeLineEndings = showLineEndings == LineEndingDisplay.None ? null : EolUtilities.DescribeLineEndings(diskContent);
                if (showLineEndings == LineEndingDisplay.All)
                {
                    slice = EolUtilities.AnnotateLineEndings(slice);
                }

                object rangeData = showLineEndings == LineEndingDisplay.None
                    ? new
                    {
                        filePath = absolutePath,
                        startLine = from,
                        endLine = to,
                        totalLines,
                        source = slice
                    }
                    : new
                    {
                        filePath = absolutePath,
                        startLine = from,
                        endLine = to,
                        totalLines,
                        lineEndings = rangeLineEndings,
                        source = slice
                    };
                return new SentinelCallToolResult<object>()
                {
                    IsError = false,
                    SuccessData = rangeData,
                    HasMoreData = to < totalLines,
                    StatusMessage = statusMessage,
                    WorkspaceVersion = _workspaceManager.WorkspaceVersion,
                };
            }

            var wholeLineEndings = showLineEndings == LineEndingDisplay.None ? null : EolUtilities.DescribeLineEndings(diskContent);
            var wholeSource = showLineEndings == LineEndingDisplay.All ? EolUtilities.AnnotateLineEndings(diskContent) : diskContent;
            var textBytes = System.Text.Encoding.UTF8.GetByteCount(wholeSource);
            object wholeData = showLineEndings == LineEndingDisplay.None
                ? new
                {
                    filePath = absolutePath,
                    startLine = 1,
                    endLine = totalLines,
                    totalLines,
                    source = diskContent
                }
                : new
                {
                    filePath = absolutePath,
                    startLine = 1,
                    endLine = totalLines,
                    totalLines,
                    lineEndings = wholeLineEndings,
                    source = wholeSource
                };
            if (textBytes > LargeResultHelper.InlineBudgetChars)
            {
                return new SentinelCallToolResult<object>()
                {
                    IsError = true,
                    ErrorData = new ResultError(ToolErrorCode.InvalidArgument, $"File is {totalLines} lines; it is over the inline limit and results cannot be offloaded while no solution is loaded. Pass a small startLine/endLine range (a range over the limit cannot be paged while no solution is loaded) or call {ToolCallHint.Call("LoadSolution")} first.")
                };
            }

            _workspaceManager.RecordObserved(FilePathWrapper.ResolveFromWire(absolutePath, null), diskContent);
            return new SentinelCallToolResult<object>()
            {
                IsError = false,
                SuccessData = wholeData,
                HasMoreData = false,
                StatusMessage = statusMessage,
                WorkspaceVersion = _workspaceManager.WorkspaceVersion,
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ReadFile (no solution) failed for '{Path}'", absolutePath);
            return new SentinelCallToolResult<object>()
            {
                IsError = true,
                ErrorData = ToolErrorMapper.ToResultError(ex, _workspaceManager, "ReadFile")
            };
        }
    }

    private static ResultError InvertedRangeError(int from, int to)
    {
        return new ResultError(ToolErrorCode.InvalidArgument, $"ReadFile: the range is inverted: startLine {from} is greater than endLine {to}. Options: startLine {to}, endLine {from} reads lines {to}-{from}.");
    }

    private Task RecordWholeFileObservedAsync(FilePathWrapper path, CancellationToken cancellationToken) =>
        _workspaceManager.RecordWholeFileObservedAsync(path, cancellationToken);

    public async Task<SentinelCallToolResult<object>> ReadFile(ToolCallReason reason, string filePath, int? startLine = null,
        int? endLine = null, string? memberName = null, string? containerName = null, LineEndingDisplay showLineEndings = LineEndingDisplay.None, PreferredTarget preferredTarget = PreferredTarget.constructor, CancellationToken cancellationToken = default)
    {
        FilePathWrapper filePathResolved = _workspaceManager.ResolveFromWire(filePath);
        if (!string.IsNullOrEmpty(memberName))
        {
            if (startLine.HasValue || endLine.HasValue)
            {
                return new SentinelCallToolResult<object>()
                {
                    IsError = true,
                    ErrorData = new ResultError(ToolErrorCode.InvalidArgument, "ReadFile: memberName cannot be combined with startLine/endLine. Pass either memberName (optionally containerName) or a line range.")
                };
            }
            if (showLineEndings != LineEndingDisplay.None)
            {
                return new SentinelCallToolResult<object>()
                {
                    IsError = true,
                    ErrorData = new ResultError(ToolErrorCode.InvalidArgument, $"ReadFile: showLineEndings cannot be combined with memberName. Read the file by startLine/endLine instead (use {ToolCallHint.Call("GetFileOutline")} or {ToolCallHint.Call("Search")} for the line range).")
                };
            }
            try
            {
                // fold queued/differing disk text first so the read and its recorded hash match disk (plan_stale_read_guard_followups 9e/9f-2)
                await _workspaceManager.FlushPendingFoldsAsync(cancellationToken);
                await _workspaceManager.SyncFileFromDiskIfStaleAsync(filePathResolved, cancellationToken);
                // GetMemberSourceAsync throws on failure and returns Modified with a non-null source.
                var (_, message, _, source) = await _symbolNavigationEngine.GetMemberSourceAsync(filePathResolved, memberName, containerName, null, null, null, true, preferredTarget: preferredTarget, callingTool: "ReadFile", cancellationToken: cancellationToken);
                // Extra whole-file fetch the member read costs (the member slice alone cannot be recorded).
                await RecordWholeFileObservedAsync(filePathResolved, cancellationToken);
                return new SentinelCallToolResult<object>() { IsError = false, SuccessData = new MemberSourceViewResult(source!), Findings = message is null ? [] : [new Finding("MemberSource", message)] };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ReadFile member read failed for '{FilePathWrapper}'", filePathResolved);
                return new SentinelCallToolResult<object>() { IsError = true, ErrorData = ToolErrorMapper.ToResultError(ex, _workspaceManager, "ReadFile") };
            }
        }
        if (filePathResolved.FailureReason == FilePathFailureReason.NoSolutionLoaded
            && !string.IsNullOrWhiteSpace(filePath)
            && Path.IsPathRooted(filePath))
        {
            return await ReadFileWithoutSolutionAsync(filePath, startLine, endLine, cancellationToken, showLineEndings);
        }
        try
        {
            // fold queued/differing disk text first so the read and its recorded hash match disk (plan_stale_read_guard_followups 9e/9f-2)
            await _workspaceManager.FlushPendingFoldsAsync(cancellationToken);
            await _workspaceManager.SyncFileFromDiskIfStaleAsync(filePathResolved, cancellationToken);
            var solution = await _workspaceManager.GetSolutionAsync(ReadSource.Committed, cancellationToken);
            var normalizedPath = Path.GetFullPath(filePathResolved);
            var document = solution.GetDocumentIdsWithFilePath(normalizedPath).Select(solution.GetDocument).FirstOrDefault() ?? solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => !string.IsNullOrEmpty(d.FilePath) && string.Equals(Path.GetFullPath(d.FilePath), normalizedPath, StringComparison.OrdinalIgnoreCase));

            SourceText sourceText;
            // Findings that describe how this read resolved its file; the success paths below append to them.
            var readFindings = new List<Finding>();
            if (document != null)
            {
                sourceText = await document.GetTextAsync(cancellationToken);
            }
            else
            {
                var diskContent = await FileIoHelper.ReadAllTextIfExistsAsync(filePathResolved, cancellationToken);
                if (diskContent == null)
                {
                    // A bare file name that is the only loaded document with that name continues with that document.
                    var bare = DocumentLookup.TryResolveUniqueBareName(solution, filePath);
                    if (bare is null)
                    {
                        return new SentinelCallToolResult<object>()
                        {
                            IsError = true,
                            ErrorData = BuildFileNotFoundError(solution, normalizedPath, _activeToolSurface is null ? null : _activeToolSurface.IsCallable)
                        };
                    }

                    filePathResolved = _workspaceManager.ResolveFromWire(bare.Path);
                    document = DocumentLookup.RequireDocument(solution, filePathResolved);
                    readFindings.Add(new Finding("FileResolved", $"'{filePath}' resolved to {bare.Path} (the only loaded file with that name)", FindingSeverity.Info));
                    sourceText = await document.GetTextAsync(cancellationToken);
                }
                else
                {
                    sourceText = SourceText.From(diskContent);
                }
            }

            var totalLines = sourceText.Lines.Count;
            if (startLine.HasValue || endLine.HasValue)
            {
                int from = Math.Max(1, startLine ?? 1);
                int to = Math.Min(totalLines, endLine ?? totalLines);
                if (from <= totalLines && from > to)
                {
                    return new SentinelCallToolResult<object>()
                    {
                        IsError = true,
                        ErrorData = InvertedRangeError(from, to)
                    };
                }
                if (from > totalLines || from > to)
                {
                    return new SentinelCallToolResult<object>()
                    {
                        IsError = true,
                        ErrorData = new ResultError(ToolErrorCode.InvalidArgument, $"ReadFile: requested range {from}-{to} is out of bounds for a {totalLines}-line file.")
                    };
                }

                _workspaceManager.RecordObserved(filePathResolved, sourceText.ToString());
                // The requested range is returned whole; an over-budget range is paged by lines by the result marshaller.
                var start = sourceText.Lines[from - 1].Start;
                var end = sourceText.Lines[to - 1].EndIncludingLineBreak;
                var slice = sourceText.ToString(TextSpan.FromBounds(start, end));
                var sliceMixedEol = EolUtilities.DescribeMixedLineEndings(sourceText.ToString());
                var rangeLineEndings = showLineEndings == LineEndingDisplay.None ? null : EolUtilities.DescribeLineEndings(sourceText.ToString());
                if (showLineEndings == LineEndingDisplay.All)
                {
                    slice = EolUtilities.AnnotateLineEndings(slice);
                }

                object rangeData = showLineEndings == LineEndingDisplay.None
                    ? new
                    {
                        filePath = (string)filePathResolved,
                        startLine = from,
                        endLine = to,
                        totalLines,
                        source = slice
                    }
                    : new
                    {
                        filePath = (string)filePathResolved,
                        startLine = from,
                        endLine = to,
                        totalLines,
                        lineEndings = rangeLineEndings,
                        source = slice
                    };
                return new SentinelCallToolResult<object>()
                {
                    IsError = false,
                    SuccessData = rangeData,
                    Findings = sliceMixedEol is null ? readFindings : [.. readFindings, new Finding("ReadFile", sliceMixedEol)],
                    HasMoreData = to < totalLines,
                    StatusMessage = showLineEndings == LineEndingDisplay.All ? "source is annotated with line-ending markers and is not verbatim." : null,
                    WorkspaceVersion = _workspaceManager.WorkspaceVersion,
                };
            }

            var fullText = sourceText.ToString();
            _workspaceManager.RecordObserved(filePathResolved, fullText);
            var mixedEol = EolUtilities.DescribeMixedLineEndings(fullText);
            var textBytes = System.Text.Encoding.UTF8.GetByteCount(fullText);
            const int thresholdBytes = LargeResultHelper.InlineBudgetChars;
            var wholeLineEndings = showLineEndings == LineEndingDisplay.None ? null : EolUtilities.DescribeLineEndings(fullText);
            var wholeSource = showLineEndings == LineEndingDisplay.All ? EolUtilities.AnnotateLineEndings(fullText) : fullText;
            var statusNote = showLineEndings == LineEndingDisplay.All ? "source is annotated with line-ending markers and is not verbatim." : null;
            object wholeData = showLineEndings == LineEndingDisplay.None
                ? new
                {
                    filePath = (string)filePathResolved,
                    startLine = 1,
                    endLine = totalLines,
                    totalLines,
                    source = fullText
                }
                : new
                {
                    filePath = (string)filePathResolved,
                    startLine = 1,
                    endLine = totalLines,
                    totalLines,
                    lineEndings = wholeLineEndings,
                    source = wholeSource
                };
            if (textBytes > thresholdBytes)
            {
                // Over the threshold the marshaller cuts the payload and stores the rest; the source gets the
                // whole inline budget, so no outline is attached here (GetFileOutline is a separate call).
                return new SentinelCallToolResult<object>
                {
                    IsError = false,
                    SuccessData = wholeData,
                    Findings = mixedEol is null ? readFindings : [.. readFindings, new Finding("ReadFile", mixedEol)],
                    HasMoreData = false,
                    StatusMessage = $"File is {totalLines} lines ({textBytes} bytes), over the inline limit: the first part is returned and the rest is stored (see largeResult). For a structure overview call {ToolCallHint.Call("GetFileOutline")}; for a slice call {ToolCallHint.Call("ReadFile", ("startLine", "1"), ("endLine", "200"))}." + (statusNote is null ? string.Empty : " " + statusNote),
                    WorkspaceVersion = _workspaceManager.WorkspaceVersion,
                };
            }

            return new SentinelCallToolResult<object>()
            {
                IsError = false,
                SuccessData = wholeData,
                Findings = mixedEol is null ? readFindings : [.. readFindings, new Finding("ReadFile", mixedEol)],
                HasMoreData = false,
                StatusMessage = statusNote,
                WorkspaceVersion = _workspaceManager.WorkspaceVersion,
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ReadFile failed for '{FilePathWrapper}'", filePathResolved);
            return new SentinelCallToolResult<object>()
            {
                IsError = true,
                ErrorData = ToolErrorMapper.ToResultError(ex, _workspaceManager, "ReadFile")
            };
        }
    }

    private static int MaxOldContentLines => ReplaceSnippetOptions.MaxOldContentLines;

    private static int MaxOldContentChars => ReplaceSnippetOptions.MaxOldContentChars;

    private static int MaxNewContentLines => ReplaceSnippetOptions.MaxNewContentLines;

    private static int MaxNewContentChars => ReplaceSnippetOptions.MaxNewContentChars;

    private const int MaxSnippetEditsPerBatch = ReplaceSnippetOptions.MaxEditsPerBatch;

    private static int CountCharsIgnoringLeadingIndentation(string content)
    {
        var total = 0;
        foreach (var line in content.Split('\n'))
        {
            total += line.TrimStart(' ', '\t').Length;
        }
        return total;
    }

    // newContent holding tool-call markup (for example a stray '</parameter>') would be written into the code. The file's
    // current text is consulted only when newContent holds a marker, and a marker the file already has is legitimate.
    // The marker list is shared with the NoMatch message for oldContent (ToolCallMarkup).
    private static string? DescribeLeakedNewContentMarkup(string newContent, Func<string> currentText)
    {
        var current = new Lazy<string>(currentText);
        var marker = ToolCallMarkup.FindLeaked(newContent, m => current.Value.Contains(m, StringComparison.Ordinal));
        return marker is null ? null : $"newContent contains tool-call markup '{marker}' that would be written into the code; remove it.";
    }

    private List<string> DescribeExceededSnippetSizeBounds(string oldContent, string newContent)
    {
        var exceeded = new List<string>();
        var oldContentLineCount = oldContent.Split('\n').Length;
        var newContentLineCount = newContent.Split('\n').Length;
        var oldContentCharCount = CountCharsIgnoringLeadingIndentation(oldContent);
        var newContentCharCount = CountCharsIgnoringLeadingIndentation(newContent);
        if (oldContentLineCount > MaxOldContentLines)
        {
            exceeded.Add($"oldContent is {oldContentLineCount} lines (limit {MaxOldContentLines})");
        }
        if (oldContentCharCount > MaxOldContentChars)
        {
            exceeded.Add($"oldContent is {oldContentCharCount} chars excluding leading indentation (limit {MaxOldContentChars})");
        }
        if (newContentLineCount > MaxNewContentLines)
        {
            exceeded.Add($"newContent is {newContentLineCount} lines (limit {MaxNewContentLines})");
        }
        if (newContentCharCount > MaxNewContentChars)
        {
            exceeded.Add($"newContent is {newContentCharCount} chars excluding leading indentation (limit {MaxNewContentChars})");
        }
        return exceeded;
    }

    // 10-chain batch E run R2 (2026-11-10): a whole-file oldContent with empty newContent emptied an interface file and no tool could recover it.
    /// <summary>
    // TODO ReplaceSnippet silently applies an edit that strips only the file-scoped namespace line (its types move to the global namespace); flag it like this check does
    // TODO pre-check whether an edit's new text declares a type that already exists in another file, ahead of compile validation
    /// Refuses an edit that leaves a .cs file with no type declaration when it had one before (empty, whitespace or only
    /// usings/namespace/comments). Returns the refusal message, or null when the edit may proceed.
    /// </summary>
    private string? DescribeNoTypeLeft(string path, string before, string after, bool batch, string toolName = "ReplaceSnippet")
    {
        static bool HasTypeContent(string text) =>
            Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(text).GetRoot().DescendantNodes().Any(node =>
                node is Microsoft.CodeAnalysis.CSharp.Syntax.BaseTypeDeclarationSyntax
                    or Microsoft.CodeAnalysis.CSharp.Syntax.DelegateDeclarationSyntax
                    or Microsoft.CodeAnalysis.CSharp.Syntax.GlobalStatementSyntax);

        if (!path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) || !HasTypeContent(before) || HasTypeContent(after))
        {
            return null;
        }

        // Top-level declared type names in source order; nested types are skipped (their parent is a type declaration).
        static List<string> TopLevelTypeNames(string text) =>
            Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(text).GetRoot().DescendantNodes()
                .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.BaseTypeDeclarationSyntax>()
                .Where(type => type.Parent is not Microsoft.CodeAnalysis.CSharp.Syntax.BaseTypeDeclarationSyntax)
                .Select(type => type.Identifier.Text)
                .Distinct(StringComparer.Ordinal)
                .ToList();

        bool Callable(string tool) => _activeToolSurface is null || _activeToolSurface.IsCallable(tool);
        var names = TopLevelTypeNames(before);
        var steps = new List<string>();
        var including = names.Count == 1 ? "that type" : "those types";
        if (Callable("DeleteFile")) steps.Add($"To delete the file (this removes the whole file including {including}), use {ToolCallHint.Call("DeleteFile")}.");
        // memberName is optional in Member's schema, so Call adds no placeholder for it: name it explicitly (eval run
        // 20261111-061005-246 followed this hint without memberName twice).
        if (Callable("Member"))
        {
            foreach (var name in names)
            {
                steps.Add($"To remove {name} only, use {ToolCallHint.Call("Member", ("operation", "remove"), ("memberName", name))}.");
            }
        }
        if (Callable("WriteFile")) steps.Add($"To replace the whole file, use {ToolCallHint.Call("WriteFile", ("operation", "ReplaceFile"))}.");

        var prefix = batch ? $"{toolName} batch: " : $"{toolName}: ";
        var suffix = batch ? "; nothing in the batch was written" : "";
        var fact = names.Count == 0
            ? string.Empty
            : names.Count == 1
                ? $" This file declares {names[0]}; the replaced text contains it."
                : $" This file declares {string.Join(", ", names)}; the replaced text contains all of them.";
        var message = $"{prefix}this edit would leave '{path}' with no type declaration (the result is empty or only usings/comments) - not applied{suffix}.{fact}";
        return steps.Count == 0 ? message : message + " " + string.Join(" ", steps);
    }

    // 10-chain batch E run R2 (2026-11-10): refusals must name the file without an absolute path; an in-memory file has no Relative, so fall back to its file name (keeps the .cs extension for the guard).
    private static string DisplayPath(FilePathWrapper filePath) =>
        string.IsNullOrEmpty(filePath.Relative) ? Path.GetFileName(filePath.Absolute) : filePath.Relative;

    // 10-chain batch E run R2 (2026-11-10): after a file was emptied, ReplaceSnippet and CreateFile both refused and the model never recovered.
    /// <summary>
    /// Refusal text for ReplaceSnippet on an empty file. Names only the recovery tools this server can call.
    /// </summary>
    private string DescribeEmptyFile(string relativePath, string toolName = "ReplaceSnippet")
    {
        bool Callable(string tool) => _activeToolSurface is null || _activeToolSurface.IsCallable(tool);
        var steps = new List<string>();
        if (Callable("UndoChange"))
        {
            steps.Add($"If an earlier edit emptied it, UndoChange restores the previous content: {ToolCallHint.Call("UndoChange")}, with the changeId from the response of that edit.");
        }

        if (Callable("CreateFile"))
        {
            steps.Add($"To write new content, use {ToolCallHint.Call("CreateFile")} with the file's full content" + (Callable("WriteFile") ? $", or {ToolCallHint.Call("WriteFile", ("operation", "ReplaceFile"))}." : "."));
        }
        else if (Callable("WriteFile"))
        {
            steps.Add($"To write new content, use {ToolCallHint.Call("WriteFile", ("operation", "ReplaceFile"))}.");
        }

        // ReplaceRange anchors on two lines, not oldContent, so the fact names them instead.
        var head = $"{toolName}: '{relativePath}' is empty, so there is no text for {(toolName == "ReplaceSnippet" ? "oldContent" : "firstLineText and lastLineText")} to match.";
        return steps.Count == 0 ? head : head + " " + string.Join(" ", steps);
    }

    /// <summary>
    /// Builds the delegate that lets an ambiguity error name the member enclosing each match.
    /// Returns null for a document with no syntax tree (e.g. a non-C# file), in which case the
    /// error simply omits the member names.
    /// </summary>
    private static Func<int, string?>? MemberNameResolverFor(Microsoft.CodeAnalysis.SyntaxNode? root)
        => root is null ? null : position => EnclosingMemberResolver.GetEnclosingMemberName(root, position);

    /// <summary>
    /// The one lookup ReplaceSnippet (single and batch) and ReplaceRange share. A project document comes back as <c>Document</c>. When the
    /// lookup missed with NotFound and the path is an existing plain-text file under the solution root that no project owns
    /// (<see cref="NonProjectFile.Classify"/>, the rule DeleteFile shares), its disk text comes back as <c>NonProjectText</c> and
    /// the caller edits it as plain text: no syntax tree, no member names, no re-indentation, and no compile gate (the write chokepoint's
    /// validation skips a non-.cs path no project contains). Every other miss (missing file, outside the root, ambiguous bare name, .cs outside
    /// every project, binary) leaves both null, so <c>Lookup</c> carries the unchanged NotFound or Ambiguous text.
    /// </summary>
    private async Task<(Microsoft.CodeAnalysis.Document? Document, SourceText? NonProjectText, DocumentLookupResult Lookup)> ResolveEditTargetAsync(
        FilePathWrapper path, CancellationToken cancellationToken)
    {
        // One lookup, owned by the read chokepoint: case-insensitive exact path, then a unique bare name; a miss names the closest real paths (or the ambiguous candidates).
        var lookup = await _workspaceManager.GetDocumentAsync(path, ReadSource.Committed, cancellationToken);
        if (lookup.TryGetDocument(out var document))
        {
            return (document, null, lookup);
        }

        if (lookup.Status != DocumentLookupStatus.NotFound)
        {
            return (null, null, lookup);
        }

        var solution = await _workspaceManager.GetSolutionAsync(ReadSource.Committed, cancellationToken);
        if (NonProjectFile.Classify(solution, _workspaceManager.GetSolutionRoot(), path) != NonProjectFileStatus.EligibleText)
        {
            return (null, null, lookup);
        }

        var diskText = await FileIoHelper.ReadAllTextIfExistsAsync(path, cancellationToken);
        return diskText is null ? (null, null, lookup) : (null, SourceText.From(diskText), lookup);
    }

    /// <summary>
    /// Resolves oldContent to the single span a ReplaceSnippetTool edit may splice, shared by the single and batch
    /// paths. The request tier is IgnoreLineEndings with case-sensitive comparison: the ordinal stage, then the
    /// CR/LF/CRLF-interchangeable stage, exactly what the removed legacy ReplaceSnippetTool matcher did, so the returned
    /// span is always the real removable span (see project_replacesnippet_silent_splice_corruption_adjacent_lines).
    /// Only <see cref="SnippetMatchOutcome.Matched"/> yields a match. Every other outcome, including a snippet that
    /// matches only at a looser tier (its near-matches are never edit candidates, except a looser-tier candidate
    /// whose C# tokens equal oldContent's: only whitespace and end-of-line trivia differ; it is accepted and its
    /// token note is reported), is thrown through
    /// <see cref="SnippetErrorFormatter.Build"/> so the message lists the file's own candidate lines.
    /// </summary>
    private static (SnippetMatch Match, string? Note) ResolveEditMatch(
        SourceText sourceText, Microsoft.CodeAnalysis.SyntaxNode? fileRoot, string oldContent, string? lineBefore, string? lineAfter,
        Func<int, string?>? memberNameAt, string? withinMember, int? matchLine)
    {
        SnippetMatchResult ResolveWithTokenStep(SnippetSelectors selectors)
        {
            var resolved = SnippetResolver.Resolve(
                sourceText, oldContent, WhitespaceTolerance.IgnoreLineEndings, SnippetCaseMode.CaseSensitive,
                selectors, memberNameAt);
            if (resolved.Outcome == SnippetMatchOutcome.MatchedLooserThanRequested && fileRoot is not null)
            {
                var tokenResult = SnippetTokenEquivalence.Resolve(sourceText, fileRoot, oldContent, selectors, memberNameAt);
                if (tokenResult is not null)
                {
                    return tokenResult;
                }
            }

            return resolved;
        }

        string TokenNote(SnippetMatch found)
        {
            int first = sourceText.Lines.GetLinePosition(found.Start).Line + 1;
            int last = sourceText.Lines.GetLinePosition(Math.Max(found.Start, found.Start + found.Length - 1)).Line + 1;
            return $"oldContent matched line {first} by its C# tokens; its whitespace differs from the file's, and the file's lines {first}-{last} were replaced.";
        }

        var result = ResolveWithTokenStep(new SnippetSelectors(withinMember, matchLine, lineBefore, lineAfter));
        if (result.Outcome == SnippetMatchOutcome.Matched)
        {
            var single = result.Matches.Single();
            return (single, single.MatcherName == SnippetTokenEquivalence.MatcherName ? TokenNote(single) : null);
        }

        // Batch V R8: oldContent that occurs once has a single candidate, so a wrong guessed matchLine only costs a
        // turn. Retry without matchLine and use the match only if that retry matches; withinMember and lineBefore/lineAfter
        // still apply. Any other retry outcome, or two or more matches, falls through to the original error below.
        if (result.Outcome == SnippetMatchOutcome.SelectorMismatch && matchLine > 0 && result.PreFilterMatches.Count == 1)
        {
            var retry = ResolveWithTokenStep(new SnippetSelectors(withinMember, null, lineBefore, lineAfter));
            if (retry.Outcome == SnippetMatchOutcome.Matched)
            {
                var only = retry.Matches.Single();
                int pos = only.Start;
                while (char.IsWhiteSpace(sourceText[pos]) && pos < only.Start + only.Length - 1)
                {
                    pos++;
                }

                int actualLine = sourceText.Lines.GetLinePosition(pos).Line + 1;
                var lineNote = $"matchLine {matchLine} ignored: oldContent occurs once, at line {actualLine}.";
                var tokenNote = only.MatcherName == SnippetTokenEquivalence.MatcherName ? TokenNote(only) : null;
                return (only, tokenNote is null ? lineNote : $"{lineNote} {tokenNote}");
            }
        }

        throw SnippetErrorFormatter.Build(result, sourceText, oldContent, "oldContent", memberNameAt, selectorsSupported: true, adjacentLinesSupported: false);
    }

    /// <summary>
    /// Rename lifting (experimental, <c>--lift-renames</c>; proposal_semantic_edit_impact_diagnostics.md "Second slice", step 1), shared by
    /// the single and batch ReplaceSnippet paths. Called only after the compile gate rejected <paramref name="candidateChanges"/>. When
    /// the flag is on and the edit is one unambiguous rename (<see cref="RenameLifter"/>), re-validates and applies the merged set:
    /// the model's text for the files it edited plus the RenameSymbol output for every other file. Null means "not lifted": the caller
    /// keeps the original rejection (the first-slice redirect). Otherwise <c>Result</c> is the merged set's apply result (success, or
    /// the remaining compiler errors, in which case nothing was written), <c>Changes</c> the merged set and <c>Note</c> the sentence the
    /// response carries ("Applied as RenameSymbol: ..." on success, an explanation of the remaining errors otherwise).
    /// <paramref name="editedSpans"/> are the model's replacements per file as spans of the baseline text, so the rename also reaches old-name
    /// uses the model's snippets did not cover (a constructor or self-call elsewhere in an edited file).
    /// </summary>
    private async Task<(ApplyChangesResult? Result, DocumentChangeSet? Changes, string Note, List<ImpactVerdict>? Verdicts, IReadOnlyDictionary<string, IReadOnlyList<(int Line, string OldText, string NewText)>>? Rewrites)?> TryLiftRenameAsync(
        Dictionary<FilePathWrapper, string> candidateChanges, IReadOnlyDictionary<FilePathWrapper, IReadOnlyList<TextChange>> editedSpans, CancellationToken cancellationToken)
    {
        if (!LiftRenamesOptions.Enabled)
        {
            return null;
        }

        var (plan, refusal) = await RenameLifter.PlanAsync(_workspaceManager, candidateChanges, editedSpans, cancellationToken);
        if (plan is null)
        {
            // H2 R1: a refusal used to be silent, so the model could not tell "the server did not try" from "the server could not". Result == null marks it: the
            // caller keeps the ordinary rejection and prefixes this note. No note when the diff held nothing rename-shaped (refusal is null then).
            return refusal is null ? null : (null, null, $"Not auto-applied as RenameSymbol: {refusal}.", null, null);
        }

        // H3: the old and new text of the lines the rename rewrote in files the model did not edit, for RewrittenLineMemory. Read from the committed solution
        // BEFORE the apply replaces it; kept only when the lift applied (the caller remembers them after its own forget of the written files).
        // READCHOKEPOINT-CAST: read the committed solution through the reader view of the workspace manager.
        var baselineSolution = await ((IWorkspaceReader)_workspaceManager).GetSolutionAsync(ReadSource.Committed, cancellationToken);
        var rewrites = await RenameLifter.RewrittenLinePairsAsync(
            baselineSolution, plan.Changes.Where(kv => !candidateChanges.ContainsKey(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value), cancellationToken);
        var lifted = await _workspaceManager.ApplyProposedChangesAsync(plan.Changes, validateChanges: true, staleReadPolicy: StaleReadPolicy.RefuseIfObservedStale);
        var rewrittenLines = plan.DescribeRewrittenLines();
        // Batch G R3 T2: the model blamed the rename for errors that came from the other text of its edit. When no remaining error sits in a file the
        // rename rewrote, say plainly that the trial rename was clean; otherwise stay hedged (the rename may share the blame).
        var errorFiles = lifted.ValidationResult?.Diagnostics.Where(d => d.Severity == "Error").Select(d => Path.GetFileName(d.FilePath.ToString())).ToList() ?? [];
        var errorsInRenamedFiles = errorFiles.Any(f => plan.RenamedFiles.Contains(f, StringComparer.OrdinalIgnoreCase));
        // Batch H4 R1 T7, R2 T7, R3 T6: "tried as a RenameSymbol ... Nothing was changed" was read as "the rename may have persisted". Lead with the
        // rollback and say the rename comes back with the resent edit.
        const string trialRolledBack = "The trial was rolled back: no file was changed, the rename included, so every file still has its old text.";
        var note = lifted.IsError
            ? errorsInRenamedFiles
                ? $"{trialRolledBack} Your edit renames {plan.DescribeRenames()}. It was tried as a RenameSymbol ({plan.CallSiteCount} call site(s) in {plan.RenamedFiles.Count} other file(s)) with your own text kept for the file(s) you edited, and errors remain in files the rename rewrote."
                : $"{trialRolledBack} Your edit renames {plan.DescribeRenames()}. In the trial the rename itself was clean ({plan.CallSiteCount} call site(s) in {plan.RenamedFiles.Count} other file(s)); the errors below come from the other text in your edit, not from the rename. Fix them and resend the whole edit: the rename is applied together with it."
            : $"Applied as RenameSymbol: {plan.Describe()} Files changed: {string.Join(", ", plan.Changes.Keys.Select(k => Path.GetFileName((string)k)))}."
                + (rewrittenLines.Length == 0 ? "" : "\n" + rewrittenLines + "\n" + RenameLifter.AlreadyUpdatedSentence);
        _logger.LogInformation("ReplaceSnippet rename lift of {Renames}: {Outcome}", plan.DescribeRenames(), lifted.IsError ? "still rejected" : "applied");
        return (lifted, plan.Changes, note, plan.ToVerdicts(lifted.IsError), lifted.IsError ? null : rewrites);
    }

    /// <summary>
    /// The <c>errorData.structuredDetail</c> of a rejected ReplaceSnippet, single and batch alike: one <see cref="EditImpactDetail"/> holding the verdicts
    /// (the lift attempt first, then the symbol-diff verdicts) the compiler diagnostics as named lists, and the near-miss names (null when there are none). The lists may be empty; the shape does not vary.
    /// </summary>
    private static List<object> RejectionDetail(EditImpactResult? impact, IEnumerable<ImpactVerdict>? liftVerdicts, IReadOnlyList<DiagnosticInfo> diagnostics, IReadOnlyList<NearMissName>? nearMisses = null)
        => EditImpactGate.BuildDetail(impact, diagnostics, liftVerdicts, nearMisses);

    /// <summary>
    /// What other ReplaceSnippet edits say, for the impact analyzer's unapplied-name rule: full file path to newContent for every file except <paramref name="ownPaths"/>,
    /// from edits that were already rejected (<see cref="RejectedEditMemory"/>) and edits that are running right now (<see cref="InFlightEditMemory"/>). Order-independent:
    /// whichever of two parallel calls is checked first, the other is in one of the two.
    /// </summary>
    private Dictionary<string, string> SiblingEditTexts(IReadOnlyCollection<string> ownPaths)
    {
        var siblings = _rejectedEdits.OtherFilesNewContent(ownPaths);
        foreach (var (path, text) in _inFlightEdits.OtherFilesNewContent(ownPaths))
            siblings[path] = siblings.TryGetValue(path, out var rejected) ? rejected + "\n" + text : text;
        return siblings;
    }

    /// <summary>
    /// The findings of a successful ReplaceSnippet: those of the apply, plus one advisory line when the write was a lifted rename (step 3: advisory entries on a
    /// successful write go to <c>findings</c>). The line is <c>edit-impact verdict=lifted kind=renamed ...</c> so a harness can match on it.
    /// </summary>
    private static List<Finding> WithLiftFinding(IEnumerable<Finding>? findings, IEnumerable<ImpactVerdict>? liftVerdicts)
    {
        var all = findings is null ? [] : findings.ToList();
        foreach (var v in (liftVerdicts ?? []).Where(v => v.Verdict == "lifted"))
            all.Add(new Finding("EditImpact", $"edit-impact verdict={v.Verdict} kind={v.Kind} declaration={v.DeclarationKind} old={v.OldName} new={v.NewName} usesRewritten={v.BrokenUseCount} suggestedTool={v.SuggestedTool}"));
        return all;
    }

    /// <summary>
    /// One batchEdits item's inserted text as it sits in the NEW (edited, EOL-normalised) text of <paramref name="FilePath"/>: the span a compile
    /// diagnostic is tested against to name the item that introduced it. <paramref name="ItemIndex"/> is the item's ORIGINAL batchEdits index.
    /// </summary>
    private sealed record BatchItemSpan(int ItemIndex, string FilePath, LinePosition Start, LinePosition End);

    /// <summary>
    /// The new-text spans of one file's batch items. <paramref name="items"/> hold each item's baseline match start/length and its final inserted text;
    /// the edits are applied in position order, so an item's new start is its baseline start plus the length change of every earlier item in the file.
    /// Positions are converted through <paramref name="normalized"/> (the text that is written) because the compile gate reports against it.
    /// </summary>
    private static List<BatchItemSpan> BatchItemSpansInNewText(
        string filePath, string spliced, string normalized, string targetEol, IReadOnlyList<(int ItemIndex, int Start, int Length, string Inserted)> items)
    {
        var lines = SourceText.From(normalized).Lines;
        var result = new List<BatchItemSpan>();
        var shift = 0;
        foreach (var item in items.OrderBy(i => i.Start))
        {
            var start = item.Start + shift;
            var end = start + item.Inserted.Length;
            shift += item.Inserted.Length - item.Length;
            result.Add(new BatchItemSpan(item.ItemIndex, filePath, ToPosition(start), ToPosition(end)));
        }

        return result;

        LinePosition ToPosition(int position)
        {
            var inSpliced = Math.Min(position, spliced.Length);
            var inNormalized = Math.Min(EolUtilities.NormalizeEol(spliced[..inSpliced], targetEol).Length, normalized.Length);
            return lines.GetLinePosition(inNormalized);
        }
    }

    /// <summary>
    /// The facts line of a rejected batch: which batchEdits item(s) each new compiler error falls in (its start position inside the item's new-text span),
    /// and the errors that fall outside every edited span (a callee or caller elsewhere). Null when there is no error diagnostic.
    /// </summary>
    private static string? DescribeBatchItemErrors(IReadOnlyList<DiagnosticInfo> diagnostics, IReadOnlyList<BatchItemSpan> itemSpans)
    {
        var errors = diagnostics.Where(d => d.Severity == "Error").ToList();
        if (errors.Count == 0)
            return null;

        var multiFile = itemSpans.Select(s => s.FilePath).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1;
        var byItem = new SortedDictionary<int, (string File, List<string> Ids)>();
        var outside = new List<(string File, string Id)>();
        foreach (var diagnostic in errors)
        {
            var diagnosticPath = diagnostic.FilePath.ToString();
            var at = new LinePosition(Math.Max(diagnostic.StartLine - 1, 0), Math.Max(diagnostic.StartColumn - 1, 0));
            var owner = itemSpans.FirstOrDefault(s => SamePath(s.FilePath, diagnosticPath) && s.Start <= at && (at < s.End || at == s.Start));
            if (owner is null)
            {
                outside.Add((Path.GetFileName(diagnosticPath), diagnostic.Id));
                continue;
            }

            if (!byItem.TryGetValue(owner.ItemIndex, out var entry))
                byItem[owner.ItemIndex] = entry = (Path.GetFileName(owner.FilePath), []);
            entry.Ids.Add(diagnostic.Id);
        }

        var parts = new List<string>();
        foreach (var (itemIndex, entry) in byItem)
            parts.Add($"batchEdits[{itemIndex}]{(multiFile ? $" ({entry.File})" : "")}: {DescribeIds(entry.Ids)}");
        foreach (var fileGroup in outside.GroupBy(o => o.File, StringComparer.OrdinalIgnoreCase))
            parts.Add($"outside every edited span: {DescribeIds(fileGroup.Select(o => o.Id))} in {fileGroup.Key}");

        var note = "New errors by batch item: " + string.Join("; ", parts) + ".";
        // Dropping items is only an option when some items are clean (otherwise nothing would be left to apply).
        if (byItem.Count > 0 && byItem.Count < itemSpans.Select(s => s.ItemIndex).Distinct().Count())
            note += " When only the named item(s) cause the errors, a batch without them is an option.";
        return note;

        static string DescribeIds(IEnumerable<string> ids) =>
            string.Join(", ", ids.GroupBy(id => id, StringComparer.Ordinal).Select(g => g.Count() == 1 ? g.Key : $"{g.Key} x{g.Count()}"));

        // A workspace-relative path matches an absolute one when the absolute path ends with it.
        static bool SamePath(string a, string b)
        {
            a = a.Trim().Replace('/', '\\');
            b = b.Trim().Replace('/', '\\');
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase)
                || a.EndsWith("\\" + b, StringComparison.OrdinalIgnoreCase)
                || b.EndsWith("\\" + a, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// The <see cref="ToolErrorCode.ValidationFailed"/> error of a ReplaceSnippet whose edit matched but whose resulting code fails the compile gate, shared by
    /// apply and validate, single and batch alike (a validate that said only "validate failed: &lt;diagnostics&gt;" gave no file:line, no impact verdict, no
    /// RenameSymbol redirect and no compiler-error hint; eval 20261112-151531-550 T3, T4/T13, T28). Runs the edit-impact analysis for
    /// <paramref name="changes"/> (the files as they would have been written), then composes <paramref name="headline"/>, the repeat note, the lift note, the
    /// impact or untouched-file sentence and the "[COMPILER ERROR]" block. Callers own the memories: only apply records into <see cref="RejectedEditMemory"/>
    /// (and passes <paramref name="isRepeat"/>); a validate is a dry run and passes false. <paramref name="liftText"/> and <paramref name="liftVerdicts"/> are
    /// null for validate (no rename lifting without a write).
    /// </summary>
    private async Task<ResultError> ComposeCompileRejectionAsync(
        string headline, bool isRepeat, string? liftText, Dictionary<FilePathWrapper, string> changes, DiagnosticReport validation,
        IReadOnlyDictionary<FilePathWrapper, IReadOnlyList<TextChange>> editedSpans, IEnumerable<ImpactVerdict>? liftVerdicts, CancellationToken cancellationToken,
        IReadOnlyList<BatchItemSpan>? batchItemSpans = null)
    {
        var changedPaths = changes.Keys.Select(k => (string)k).ToList();
        var impact = await EditImpactGate.AnalyzeAsync(_workspaceManager, changes, validation, _activeToolSurface, cancellationToken: cancellationToken,
            editedSpans: editedSpans,
            rejectedSiblingTexts: SiblingEditTexts(changedPaths));
        var impactNote = impact?.Text;
        // The edit-impact report, when it can attribute the errors to unedited files, replaces the generic untouched-file sentence (it already covers it).
        var untouchedNote = impactNote
            ?? ValidateAndApplyHelper.DescribeUntouchedFileErrors(changedPaths, validation.Diagnostics.Select(d => d.FilePath.ToString()), "one ReplaceSnippet call with batchEdits, one item per file, each with its own filePath");
        // A batch only: which batchEdits item each new error falls in, so a model can drop just the item that caused it (INBOX 2026-11-23 08:20).
        var batchNote = batchItemSpans is null ? null : DescribeBatchItemErrors(validation.Diagnostics, batchItemSpans);
        var repeatNote = isRepeat ? RepeatedRejectionNote + (impactNote is null ? RepeatedRejectionChangeApproach : RepeatedRejectionFollowNextStep) + " " : "";
        var nearMisses = await EditImpactGate.DetectNearMissesAsync(_workspaceManager, changes, validation.Diagnostics, cancellationToken);
        // A CS0103 for a name the edit renamed away is explained by the impact attribution, so the typo/closest-name text is dropped for it.
        var renamedAwayNames = impact?.Changes.Where(c => c.Kind == SymbolChangeKind.Renamed).Select(c => c.OldName).Distinct(StringComparer.Ordinal).ToList();
        return new ResultError(ToolErrorCode.ValidationFailed,
            headline +
            repeatNote +
            (liftText is null ? "" : liftText + " ") +
            (untouchedNote is null ? "" : untouchedNote + " ") +
            (batchNote is null ? "" : batchNote + " ") +
            (impactNote is null ? "Fix the issue(s) below and retry:\n" : "Compiler errors caused by this edit:\n") +
            "[COMPILER ERROR]\n" +
            await CompilerErrorLookupHelper.DescribeAsync(validation, _symbolNavigationEngine, cancellationToken, changedPaths, isToolActive: _activeToolSurface is null ? null : _activeToolSurface.IsCallable, renamedAwayNames: renamedAwayNames, isBatchEdit: batchItemSpans is not null),
            StructuredDetail: RejectionDetail(impact, liftVerdicts, validation.Diagnostics, nearMisses));
    }

    /// <summary>The single-file compile-rejection headline of <see cref="ApplyEditTailAsync"/>; <paramref name="nextStep"/> is the tool-specific sentence after "The file is unchanged: " (ends with a space).</summary>
    private static string StandardCompileRejectionHeadline(string toolName, string nextStep) =>
        $"{toolName}: the edit matched the target file, but the resulting code introduces new compiler errors - change not applied. The file is unchanged: {nextStep}";

    /// <summary>
    /// The apply tail shared by the single-edit ReplaceSnippet, ReplaceRange and ReplaceSnippetBatch (proposal_replace_range_tool.md, "Apply tail extraction"): applies
    /// <paramref name="initialChanges"/> through the write chokepoint with the stale-read refusal, maps write refusals, optionally near-miss-corrects and
    /// rename-lifts a compile rejection, composes the compile-rejection error, keeps the rejected/applied-edit memories (when <paramref name="editMemory"/>
    /// is given), writes the operation blob and returns the success summary with the UndoChange hint and the optional diff. The caller owns match
    /// resolution, the no-type-left guard, the in-flight registration, the validate-only branch and the exception boundary.
    /// </summary>
    /// <param name="toolName">Names the tool in WriteRefusal and near-miss correction (for example "ReplaceSnippet").</param>
    /// <param name="operationKind">The operation-blob kind label (for example "replace_snippet").</param>
    /// <param name="targetPath">The file the edit targets; its file memories (and those of every <paramref name="editMemory"/> path and written file) are forgotten after a successful write.</param>
    /// <param name="initialChanges">The files as they would be written.</param>
    /// <param name="editedSpans">The edit's replaced span(s) in the baseline text, for rename lifting and impact attribution.</param>
    /// <param name="compileRejectionHeadline">The complete compile-rejection headline, ending with a space; <see cref="StandardCompileRejectionHeadline"/> builds the single-file form.</param>
    /// <param name="summaryPrefixNotes">Notes put before the apply summary, in order; null entries are skipped.</param>
    /// <param name="enableNearMissCorrection">false skips <see cref="NearMissAutoCorrector"/>.</param>
    /// <param name="enableRenameLift">false skips <see cref="TryLiftRenameAsync"/>.</param>
    /// <param name="batchItemSpans">A batch's per-item spans in the new text, to name the item each new compile error falls in; ignored (the written text no longer matches them) after a near-miss correction or a rename lift.</param>
    /// <param name="editMemory">One (path, oldContent, newContent, inserted text) key per edit for <c>_rejectedEdits</c> (a repeat needs every key to repeat) and, for a key whose inserted text is not null, <c>_appliedEdits</c>; null skips both memories.</param>
    private async Task<SentinelCallToolResult<ReplaceSnippetResult>> ApplyEditTailAsync(
        string toolName, string operationKind, string targetPath, DocumentChangeSet initialChanges,
        IReadOnlyDictionary<FilePathWrapper, IReadOnlyList<TextChange>> editedSpans, string compileRejectionHeadline,
        IEnumerable<string?> summaryPrefixNotes, bool validateOnApply, bool returnDiff,
        bool enableNearMissCorrection, bool enableRenameLift, IReadOnlyList<(string Path, string? OldContent, string NewContent, string? InsertedText)>? editMemory,
        CancellationToken cancellationToken, IReadOnlyList<BatchItemSpan>? batchItemSpans = null)
    {
        var snippetChanges = initialChanges;
        var liftSpans = editedSpans;
        var result = await _workspaceManager.ApplyProposedChangesAsync(snippetChanges, validateChanges: validateOnApply, staleReadPolicy: StaleReadPolicy.RefuseIfObservedStale);
        // TODO a non-mapped, non-validation refusal (IsError without ValidationResult) falls through to IsError = false below; map it once decided (plan_stale_read_guard_followups open decision 9)
        if (WriteRefusal.TryMap(toolName, result, out var refusal, WriteRefusal.LedgerOf(_workspaceManager)))
            return new SentinelCallToolResult<ReplaceSnippetResult>() { IsError = true, ErrorData = refusal };
        // Experimental rename lifting (--lift-renames, off by default): a rejected edit that is one unambiguous rename is
        // re-validated as RenameSymbol output merged with this edit's own text; see TryLiftRenameAsync.
        string? liftNote = null;
        string? liftRefusalNote = null;
        IReadOnlyDictionary<string, IReadOnlyList<(int Line, string OldText, string NewText)>>? liftRewrites = null;
        List<ImpactVerdict>? liftVerdicts = null;
        // Batch H5 R5: the model's own text, kept because a lift replaces snippetChanges with the merged set (see RenameLifter.MergedEditedSpansAsync).
        var candidateChanges = snippetChanges;
        IReadOnlyList<Finding>? nearMissFindings = null;
        if (enableNearMissCorrection && result.IsError && result.ValidationResult != null
            && await NearMissAutoCorrector.TryAutoCorrectAsync(_workspaceManager, snippetChanges, result.ValidationResult, toolName, StaleReadPolicy.RefuseIfObservedStale, cancellationToken) is { } nearMissFix)
        {
            result = nearMissFix.ApplyResult;
            snippetChanges = new DocumentChangeSet(nearMissFix.CorrectedChanges);
            nearMissFindings = nearMissFix.Findings;
        }
        if (enableRenameLift && result.IsError && result.ValidationResult != null && await TryLiftRenameAsync(snippetChanges, liftSpans, cancellationToken) is { } lift)
        {
            if (lift.Result is null)
            {
                liftRefusalNote = lift.Note; // refused: the ordinary rejection stands, with the reason in front
            }
            else
            {
                if (WriteRefusal.TryMap(toolName, lift.Result, out var liftRefusal, WriteRefusal.LedgerOf(_workspaceManager)))
                    return new SentinelCallToolResult<ReplaceSnippetResult>() { IsError = true, ErrorData = liftRefusal };
                result = lift.Result;
                snippetChanges = lift.Changes!;
                liftNote = lift.Note;
                liftVerdicts = lift.Verdicts;
                liftRewrites = lift.Rewrites;
            }
        }
        var liftText = liftNote ?? liftRefusalNote;
        if (result.IsError && result.ValidationResult != null)
        {
            var isRepeat = false;
            if (editMemory is { } rejectedKeys)
            {
                isRepeat = rejectedKeys.All(k => _rejectedEdits.IsRepeat(k.Path, k.OldContent, k.NewContent));
                foreach (var rejectedKey in rejectedKeys)
                    _rejectedEdits.Remember(rejectedKey.Path, rejectedKey.OldContent, rejectedKey.NewContent);
            }
            return new SentinelCallToolResult<ReplaceSnippetResult>()
            {
                IsError = true,
                ErrorData = await ComposeCompileRejectionAsync(
                    compileRejectionHeadline,
                    isRepeat, liftText, snippetChanges, result.ValidationResult,
                    // Spans only for the model's own text: a lifted (merged) set was changed outside them.
                    // Batch H5 R5: after a lift the spans are the model's own line ranges mapped onto the merged text, so an error the rename left in the model's file but outside its edit is attributed.
                    liftNote is null ? liftSpans : await RenameLifter.MergedEditedSpansAsync(_workspaceManager, candidateChanges, snippetChanges, liftSpans, cancellationToken),
                    liftVerdicts, cancellationToken,
                    batchItemSpans: liftNote is null && nearMissFindings is null ? batchItemSpans : null)
            };
        }
        if (!result.IsError)
        {
            ForgetFileMemories(targetPath);
            foreach (var keyedEdit in editMemory ?? [])
                ForgetFileMemories(keyedEdit.Path);
            // A lifted rename also wrote other files (snippetChanges is the merged set then): an earlier rejected edit of one of them
            // must not make the "file still has the old text" note fire, because the lift rewrote that file (batch G R3).
            foreach (var writtenFile in snippetChanges.Keys)
                ForgetFileMemories((string)writtenFile);
            // After the forget: the lift's own rewrites of the other files are what a later stale oldContent should be told about.
            if (liftRewrites is not null)
                RewrittenLineMemory.Shared.Remember("the rename lift", liftRewrites);
        }
        var changeId = Guid.NewGuid().ToString("n")[..8];
        // Batch H4 R1: remember the applied edit so an identical second call is a no-op (see AppliedEditMemory). A lifted edit wrote other files too, so it is not remembered.
        if (editMemory is { } appliedKeys && !result.IsError && liftNote is null && nearMissFindings is null)
            foreach (var appliedKey in appliedKeys.Where(k => k.InsertedText is not null))
                _appliedEdits.Remember(appliedKey.Path, appliedKey.OldContent, appliedKey.NewContent, changeId, appliedKey.InsertedText!);
        var blobWrite = await OperationBlobHelper.WriteBlobForApplyAsync(_logger, _workspaceManager, operationKind, result, blobChangeId: changeId, cancellationToken: cancellationToken);
        var summaryBody = liftNote is null ? result.Summary : $"{liftNote} {result.Summary}";
        var appliedSummary = string.Join(" ", summaryPrefixNotes.Append(summaryBody).Where(part => part is not null));
        var strippedResult = result with { PreImages = null, Summary = blobWrite.Written ? $"{appliedSummary} Call {ToolCallHint.Call("UndoChange", ("changeId", changeId))} to revert." : appliedSummary };
        string? diffContent = returnDiff
            ? ValidateAndApplyHelper.BuildDiffFromPreImages(snippetChanges, result.PreImages)
            : null;
        return new SentinelCallToolResult<ReplaceSnippetResult>()
        {
            IsError = false,
            SuccessData = new ReplaceSnippetResult(strippedResult, null, diffContent, blobWrite.Written ? changeId : null),
            Findings = nearMissFindings is null ? WithLiftFinding(result.Findings, liftVerdicts) : WithLiftFinding(result.Findings, liftVerdicts).Concat(nearMissFindings).ToList()
        };
    }

    public async Task<SentinelCallToolResult<ReplaceSnippetResult>> ReplaceSnippet(
        ToolCallReason reason,
        ProposedChangeAction action,
        string? filePath = null,
        string? oldContent = null,
        string? newContent = null,
        string? lineBefore = null,
        string? lineAfter = null,
        string? withinMember = null,
        int? matchLine = null,
        List<SnippetEdit>? batchEdits = null,
        bool validateOnApply = true,
        bool returnDiff = false,
        CancellationToken cancellationToken = default)
    {
        try
        {
            bool hasBatchEdit = batchEdits != null;
            // Beside batchEdits, a top-level filePath alone is the default file for items with a blank filePath (models repeat it).
            // A top-level oldContent/newContent beside batchEdits is appended as one more item (SnippetEdit carries every selector
            // too, so withinMember/matchLine/lineBefore/lineAfter travel with it). The only conflict is a top-level edit that
            // cannot form an item: oldContent without newContent, newContent without oldContent, or a selector with neither.
            // An empty newContent with no oldContent and no selector is tolerated as unset, like the old filePath-only rule.
            bool hasTopLevelItemFields = !string.IsNullOrEmpty(oldContent) || !string.IsNullOrEmpty(newContent)
                || withinMember is not null || matchLine is not null || lineBefore is not null || lineAfter is not null;

            if (hasBatchEdit && hasTopLevelItemFields)
            {
                if (string.IsNullOrEmpty(oldContent) || newContent is null)
                {
                    var given = new[]
                    {
                        (!string.IsNullOrEmpty(oldContent), "oldContent"), (!string.IsNullOrEmpty(newContent), "newContent"),
                        (withinMember is not null, "withinMember"), (matchLine is not null, "matchLine"),
                        (lineBefore is not null, "lineBefore"), (lineAfter is not null, "lineAfter")
                    }.Where(p => p.Item1).Select(p => p.Item2);
                    var missing = string.IsNullOrEmpty(oldContent) ? "oldContent" : "newContent";
                    return new SentinelCallToolResult<ReplaceSnippetResult>()
                    {
                        IsError = true,
                        ErrorData = new ResultError(ToolErrorCode.InvalidArgument,
                            $"ReplaceSnippet: top-level {string.Join("/", given)} beside batchEdits cannot form an edit because {missing} is not set. "
                            + "Options: pass oldContent and newContent together (they count as one more batch item), move the edit into batchEdits, or drop batchEdits.")
                    };
                }

                batchEdits = [.. batchEdits!, new SnippetEdit
                {
                    FilePath = filePath ?? "",
                    OldContent = oldContent,
                    NewContent = newContent,
                    LineBefore = lineBefore,
                    LineAfter = lineAfter,
                    WithinMember = withinMember,
                    MatchLine = matchLine
                }];
            }

            if (hasBatchEdit)
            {
                if (batchEdits!.Count == 0)
                {
                    return new SentinelCallToolResult<ReplaceSnippetResult>()
                    {
                        IsError = true,
                        ErrorData = new ResultError(ToolErrorCode.InvalidArgument, "ReplaceSnippet: 'batchEdits' was supplied but is empty.")
                    };
                }

                if (filePath is not null)
                {
                    foreach (var edit in batchEdits!)
                    {
                        if (string.IsNullOrWhiteSpace(edit.FilePath))
                        {
                            edit.FilePath = filePath;
                        }
                    }
                }

                return await ReplaceSnippetBatch(batchEdits, action, validateOnApply, returnDiff, cancellationToken);
            }

            if (filePath is null)
            {
                return new SentinelCallToolResult<ReplaceSnippetResult>()
                {
                    IsError = true,
                    ErrorData = new ResultError(ToolErrorCode.InvalidArgument,
                        "ReplaceSnippet: 'filePath' is required (it names the single file oldContent/newContent applies to), unless 'batchEdits' is supplied instead.")
                };
            }

            FilePathWrapper filePathResolved = _workspaceManager.ResolveFromWire(filePath);
            // Only a blank path is 'could not be resolved'. A non-blank unvalidated path (misspelt or missing file)
            // falls through to DocumentLookup below so its near-match or no-match text reaches the model.
            if (!filePathResolved.Validated
                && (filePathResolved.FailureReason == FilePathFailureReason.NoSolutionLoaded || string.IsNullOrWhiteSpace(filePathResolved.Absolute)))
            {
                return new SentinelCallToolResult<ReplaceSnippetResult>()
                {
                    IsError = true,
                    ErrorData = filePathResolved.FailureReason == FilePathFailureReason.NoSolutionLoaded
                        ? new ResultError(ToolErrorCode.SolutionNotLoaded, SolutionNotLoadedMessage.ForFilePath("ReplaceSnippet", _workspaceManager.LoadState))
                        : new ResultError(ToolErrorCode.InvalidArgument, "ReplaceSnippet: 'filePath' could not be resolved.")
                };
            }

            // 10-chain batch E run R2 (2026-11-10): after a file was emptied, ReplaceSnippet and CreateFile both refused and the model never recovered.
            var emptyLookup = await _workspaceManager.GetDocumentAsync(filePathResolved, ReadSource.Committed, cancellationToken);
            if (emptyLookup.TryGetDocument(out var emptyDocument) && string.IsNullOrWhiteSpace((await emptyDocument.GetTextAsync(cancellationToken)).ToString()))
            {
                return new SentinelCallToolResult<ReplaceSnippetResult>()
                {
                    IsError = true,
                    ErrorData = new ResultError(string.IsNullOrEmpty(oldContent) ? ToolErrorCode.InvalidArgument : ToolErrorCode.NotFound, DescribeEmptyFile(DisplayPath(filePathResolved)))
                };
            }

            if (string.IsNullOrEmpty(oldContent))
            {
                return new SentinelCallToolResult<ReplaceSnippetResult>()
                {
                    IsError = true,
                    ErrorData = new ResultError(ToolErrorCode.InvalidArgument, "ReplaceSnippet: 'oldContent' is required.")
                };
            }

            if (newContent == null)
            {
                return new SentinelCallToolResult<ReplaceSnippetResult>()
                {
                    IsError = true,
                    ErrorData = new ResultError(ToolErrorCode.InvalidArgument, "ReplaceSnippet: 'newContent' is required (pass an empty string for a pure deletion).")
                };
            }

            // Each bound is reported separately, with the actual value against the limit: run
            // 20260910-013550-398 shows the model repeatedly guessing wrong about which of the four
            // it had hit, because the old message named them all at once.
            var exceeded = DescribeExceededSnippetSizeBounds(oldContent, newContent);
            if (exceeded.Count > 0)
            {
                // Escape-hatch advice comes from WriteToolAdviceHelper, never a hardcoded tool name:
                // the old text here named WriteFileTool unconditionally, and in run 398 WriteFileTool was
                // gated off, so the one instruction the model was given was unfollowable.
                // The shape lets the advice stop recommending Member(replace) for content that holds several
                // members or usings.
                var advice = _writeAdvice.AdviseForOversizedEdit(CallableSnippetTool(), RejectedContentClassifier.Classify(newContent));
                return new SentinelCallToolResult<ReplaceSnippetResult>()
                {
                    IsError = true,
                    ErrorData = new ResultError(ToolErrorCode.InvalidArgument,
                        $"ReplaceSnippet: {string.Join("; ", exceeded)}. " + advice.Sentence)
                };
            }

            if (action == ProposedChangeAction.apply || action == ProposedChangeAction.validate)
            {
                try
                {
                    // One lookup, owned by the read chokepoint: case-insensitive exact path, then a unique
                    // bare name; a miss names the closest real paths (or the ambiguous candidates).
                    var (document, nonProjectText, lookup) = await ResolveEditTargetAsync(filePathResolved, cancellationToken);
                    if (document is null && nonProjectText is null)
                    {
                        return new SentinelCallToolResult<ReplaceSnippetResult>()
                        {
                            IsError = true,
                            ErrorData = lookup.ToResultError()
                        };
                    }

                    // A non-project text file has no Document: its disk text is matched as plain text (no syntax root, no member names, no re-indent).
                    var oldText = nonProjectText ?? await document!.GetTextAsync(cancellationToken: cancellationToken);
                    var leakedMarkup = DescribeLeakedNewContentMarkup(newContent, () => oldText.ToString());
                    if (leakedMarkup is not null)
                    {
                        return new SentinelCallToolResult<ReplaceSnippetResult>()
                        {
                            IsError = true,
                            ErrorData = new ResultError(ToolErrorCode.InvalidArgument, "ReplaceSnippet: " + leakedMarkup)
                        };
                    }

                    // ResolveEditMatch caps the tier at IgnoreLineEndings, so it never uses a
                    // whitespace-collapsing fallback and match.Length is always the real removable
                    // span. Using oldContent.Length instead of match.Length used to corrupt adjacent
                    // lines when a fallback match fired (see
                    // project_replacesnippet_silent_splice_corruption_adjacent_lines memory).
                    var root = document is null ? null : await document.GetSyntaxRootAsync(cancellationToken);
                    SnippetMatch match;
                    // Set when a wrong matchLine was ignored for a unique match; prefixed to the apply summary.
                    string? selectorNote = null;
                    try
                    {
                        (match, selectorNote) = ResolveEditMatch(
                            oldText, root, oldContent, lineBefore, lineAfter, memberNameAt: MemberNameResolverFor(root),
                            withinMember: withinMember, matchLine: matchLine);
                    }
                    // Batch H4 R1: an identical edit sent twice (e.g. action replace + apply in one turn) is a no-op success, not a not-found failure.
                    catch (ToolNotFoundException) when (_appliedEdits.FindDuplicate((string)(document?.FilePath ?? filePathResolved), oldContent, newContent, oldText.ToString()) is not null)
                    {
                        var duplicateOf = _appliedEdits.FindDuplicate((string)(document?.FilePath ?? filePathResolved), oldContent, newContent, oldText.ToString());
                        return new SentinelCallToolResult<ReplaceSnippetResult>()
                        {
                            IsError = false,
                            SuccessData = new ReplaceSnippetResult(
                                new ApplyChangesResult(
                                    IsError: false,
                                    SucceededFiles: new List<string>(),
                                    FailedFiles: new Dictionary<FilePathWrapper, string>(),
                                    Summary: $"No change: this exact edit was already applied by an earlier ReplaceSnippet call (changeId {duplicateOf}), and the file already contains your newContent. Do not send it again; continue with the next step."),
                                null, null, null)
                        };
                    }
                    catch (ToolNotFoundException notFound) when (_rejectedEdits.Explains((string)(document?.FilePath ?? filePathResolved), oldContent, oldText.ToString()))
                    {
                        // Same error and code; only the text grows, so callers branching on NotFound are unaffected.
                        throw new ToolNotFoundException(notFound.Message + " " + RejectedOriginNote, notFound.MissingTargetKind, notFound.MissingTargetName, notFound.Snippet, notFound.Candidates);
                    }
                    catch (ToolNotFoundException notFound) when (RewrittenLineMemory.Shared.Explain((string)(document?.FilePath ?? filePathResolved), oldContent, oldText.ToString()) is { } rewriteNote)
                    {
                        // H3: the oldContent holds the old text of a line the server rewrote (a lifted rename or RenameSymbol) earlier.
                        throw new ToolNotFoundException(notFound.Message + " " + rewriteNote, notFound.MissingTargetKind, notFound.MissingTargetName, notFound.Snippet, notFound.Candidates);
                    }
                    // The indent pass runs on every successful match, exact ones included, apply and validate alike.
                    IReadOnlyList<TextChange> spliceEdits = [new TextChange(new TextSpan(match.Start, match.Length), newContent)];
                    var reindent = document is null
                        ? NewContentIndenter.SplicePlain(oldText, spliceEdits)
                        : await NewContentIndenter.ReindentAsync(document, spliceEdits, cancellationToken);
                    var newFileContent = reindent.Text;
                    var targetPath = document?.FilePath ?? filePathResolved;
                    var targetEol = _workspaceManager.ResolveEol((string)targetPath, oldText.ToString());
                    var normalizedContent = EolUtilities.NormalizeEol(newFileContent, targetEol);
                    var noTypeLeft = DescribeNoTypeLeft(DisplayPath(filePathResolved), oldText.ToString(), normalizedContent, batch: false);
                    if (noTypeLeft is not null)
                    {
                        return new SentinelCallToolResult<ReplaceSnippetResult>()
                        {
                            IsError = true,
                            ErrorData = new ResultError(ToolErrorCode.TargetIneligible, noTypeLeft)
                        };
                    }

                    var snippetChanges = new DocumentChangeSet
                    {
                        [targetPath] = normalizedContent
                    };
                    // The matched span in the baseline text and its replacement, for rename lifting (same key instance as snippetChanges).
                    IReadOnlyDictionary<FilePathWrapper, IReadOnlyList<TextChange>> liftSpans = new Dictionary<FilePathWrapper, IReadOnlyList<TextChange>>
                    {
                        [snippetChanges.Keys.Single()] = [new TextChange(new TextSpan(match.Start, match.Length), reindent.InsertedTexts[0])]
                    };
                    // Visible to parallel calls' impact analysis until this call ends (see InFlightEditMemory).
                    using var inFlight = _inFlightEdits.Register([((string)targetPath, newContent ?? string.Empty)]);

                    if (action == ProposedChangeAction.validate)
                    {
                        // validate returns here, before the apply summary, so no selector note is reported on this path.
                        var validationResult = await _validationEngine.ValidateChangesAsync(snippetChanges, cancellationToken: cancellationToken);
                        return !validationResult.IsError ? new SentinelCallToolResult<ReplaceSnippetResult>()
                        {
                            IsError = false,
                            SuccessData = new ReplaceSnippetResult(null, validationResult, null)
                        }
                        : new SentinelCallToolResult<ReplaceSnippetResult>()
                        {
                            IsError = true,
                            // A dry run: same impact/redirect/hint body as apply, but no _rejectedEdits record or repeat check and no rename lifting (nothing is written).
                            ErrorData = await ComposeCompileRejectionAsync(
                                "ReplaceSnippet validate: the edit matched the target file, but the resulting code introduces new compiler errors (nothing was written; the file is unchanged). ",
                                isRepeat: false, liftText: null, snippetChanges, validationResult, liftSpans, liftVerdicts: null, cancellationToken)
                        };
                    }

                    var indentNote = reindent.LinesAdjusted > 0 ? $"newContent indentation was adjusted to the file's on {reindent.LinesAdjusted} line(s); returnDiff shows the written text." : null;
                    return await ApplyEditTailAsync(
                        "ReplaceSnippet", "replace_snippet", (string)targetPath, snippetChanges, liftSpans,
                        StandardCompileRejectionHeadline("ReplaceSnippet", "take the next oldContent from the file as it is now, not from the rejected newContent. "),
                        [selectorNote, indentNote], validateOnApply, returnDiff,
                        enableNearMissCorrection: true, enableRenameLift: true,
                        [((string)targetPath, oldContent, newContent!, reindent.InsertedTexts[0])], cancellationToken);
                }
                catch (Exception ex)
                {
                    if (ex is ToolException toolEx)
                        _logger.LogInformation("ReplaceSnippet {Action} refused for '{FilePathWrapper}': {Message}", action, filePathResolved, toolEx.Message);
                    else
                        _logger.LogError(ex, "ReplaceSnippet {Action} unexpected exception for '{FilePathWrapper}'", action, filePathResolved);
                    return new SentinelCallToolResult<ReplaceSnippetResult>()
                    {
                        IsError = true,
                        ErrorData = ToolErrorMapper.ToResultError(ex, _workspaceManager, $"ReplaceSnippet {action} for '{filePathResolved}'")
                    };
                }
            }

            return new SentinelCallToolResult<ReplaceSnippetResult>()
            {
                IsError = true,
                ErrorData = new ResultError(ToolErrorCode.Exception, $"Unhandled action '{action}'.")
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ReplaceSnippet ({Action}) failed", action);
            return new SentinelCallToolResult<ReplaceSnippetResult>()
            {
                IsError = true,
                ErrorData = ToolErrorMapper.ToResultError(ex, _workspaceManager, "ReplaceSnippet")
            };
        }
    }

    // Zero-match anchor hints list at most this many nearby lines that contain the anchor text.
    private const int MaxAnchorNearLines = 5;

    // An ambiguous-anchor refusal lists at most this many candidate matches (line number and text).
    private const int MaxAnchorCandidates = 10;

    /// <summary>
    /// Replaces (or, with an empty <paramref name="newContent"/>, deletes) the whole lines from the line (or contiguous block of
    /// lines) matching <paramref name="firstLineText"/> through the line (or block) matching <paramref name="lastLineText"/>, inclusive
    /// (proposal_replace_range_tool.md). An anchor is one or more lines compared line by line with leading/trailing whitespace
    /// ignored; the range starts at the first anchor's first line and ends at the last anchor's last line. firstLineText must match
    /// exactly one line/block; a lastLineText that matches several resolves to the first match starting at or after the end of the first anchor.
    /// An anchor that matches nothing (or firstLineText several times) is refused with the count and every matching line number; nothing is written. The write goes through <see cref="ApplyEditTailAsync"/> (compile gate,
    /// stale-read refusal, undo).
    /// </summary>
    public async Task<SentinelCallToolResult<ReplaceSnippetResult>> ReplaceRange(
        ToolCallReason reason,
        string filePath,
        string firstLineText,
        string lastLineText,
        string newContent,
        CancellationToken cancellationToken = default)
    {
        static SentinelCallToolResult<ReplaceSnippetResult> Refuse(string code, string message) =>
            new()
            {
                IsError = true,
                ErrorData = new ResultError(code, message)
            };

        try
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                return Refuse(ToolErrorCode.InvalidArgument, "ReplaceRange: 'filePath' is required.");
            }

            if (string.IsNullOrWhiteSpace(firstLineText) || string.IsNullOrWhiteSpace(lastLineText))
            {
                return Refuse(ToolErrorCode.InvalidArgument,
                    "ReplaceRange: 'firstLineText' and 'lastLineText' are required and must not be blank (a blank line matches many lines).");
            }

            if (newContent is null)
            {
                return Refuse(ToolErrorCode.InvalidArgument, "ReplaceRange: 'newContent' is required (pass an empty string to delete the range).");
            }

            // Anchor comparison ignores leading/trailing whitespace of each line (so a model that drops indentation still matches) and the line ending.
            // An anchor may be several lines: it matches a contiguous block of file lines.
            var firstAnchorLines = SplitAnchorLines(firstLineText);
            var lastAnchorLines = SplitAnchorLines(lastLineText);

            FilePathWrapper filePathResolved = _workspaceManager.ResolveFromWire(filePath);
            // Same front matter as ReplaceSnippet: only a blank path is 'could not be resolved'; a misspelt path falls through to the lookup's near-match text.
            if (!filePathResolved.Validated
                && (filePathResolved.FailureReason == FilePathFailureReason.NoSolutionLoaded || string.IsNullOrWhiteSpace(filePathResolved.Absolute)))
            {
                return filePathResolved.FailureReason == FilePathFailureReason.NoSolutionLoaded
                    ? Refuse(ToolErrorCode.SolutionNotLoaded, SolutionNotLoadedMessage.ForFilePath("ReplaceRange", _workspaceManager.LoadState))
                    : Refuse(ToolErrorCode.InvalidArgument, "ReplaceRange: 'filePath' could not be resolved.");
            }

            var (document, nonProjectText, lookup) = await ResolveEditTargetAsync(filePathResolved, cancellationToken);
            if (document is null && nonProjectText is null)
            {
                return new SentinelCallToolResult<ReplaceSnippetResult>() { IsError = true, ErrorData = lookup.ToResultError() };
            }

            // A non-project text file has no Document: its disk text is edited as plain text and written through the same tail (no compile gate applies to it).
            var oldText = nonProjectText ?? await document!.GetTextAsync(cancellationToken: cancellationToken);
            if (string.IsNullOrWhiteSpace(oldText.ToString()))
            {
                return Refuse(ToolErrorCode.InvalidArgument, DescribeEmptyFile(DisplayPath(filePathResolved), "ReplaceRange"));
            }

            var leakedMarkup = DescribeLeakedNewContentMarkup(newContent, () => oldText.ToString());
            if (leakedMarkup is not null)
            {
                return Refuse(ToolErrorCode.InvalidArgument, "ReplaceRange: " + leakedMarkup);
            }

            // firstLineText must match exactly one line/block in the whole file (owner rule). A lastLineText that matches several places resolves to the
            // first match that starts at or after the line where the first anchor ends; one that matches once is used as is (a match above the first anchor is refused below).
            // Known remaining limit (owner note): widening does not always help - runs of '}' lines at the end of nested blocks, and several similar blocks whose first
            // and last lines look alike, can resolve to a line the caller did not mean; ReplaceSnippet (whole-block match) is the usual route there.
            var firstHits = FindAnchorBlocks(oldText, firstAnchorLines);
            var lastHits = FindAnchorBlocks(oldText, lastAnchorLines);
            string? lastResolvedNote = null;
            if (firstHits.Count == 1 && lastHits.Count > 1)
            {
                var firstEnd = firstHits[0].End;
                var lastCandidates = lastHits;
                var lastSpans = string.Join(", ", lastCandidates.Select(hit => lastAnchorLines.Length > 1 ? $"{hit.Start + 1}-{hit.End + 1}" : $"{hit.Start + 1}"));
                var firstAfterFirst = lastCandidates.FindIndex(hit => hit.Start >= firstEnd);
                if (firstAfterFirst < 0)
                {
                    return Refuse(ToolErrorCode.InvalidArgument,
                        $"ReplaceRange: lastLineText matches {lastCandidates.Count} places of '{DisplayPath(filePathResolved)}' (lines {lastSpans}); none starts at or after line {firstEnd + 1}, where firstLineText ends. Nothing was written.");
                }

                lastHits = [lastCandidates[firstAfterFirst]];
                lastResolvedNote = $"lastLineText matched {lastCandidates.Count} places (lines {lastSpans}); the first starting at or after line {firstEnd + 1} (line {lastHits[0].Start + 1}) was used.";
            }

            var anchorProblems = new List<(string Code, string Message)>();
            if (DescribeAnchorMatchCount("firstLineText", firstAnchorLines, firstHits, oldText, DisplayPath(filePathResolved)) is { } firstProblem)
            {
                anchorProblems.Add(firstProblem);
            }

            if (DescribeAnchorMatchCount("lastLineText", lastAnchorLines, lastHits, oldText, DisplayPath(filePathResolved)) is { } lastProblem)
            {
                anchorProblems.Add(lastProblem);
            }

            if (anchorProblems.Count > 0)
            {
                return Refuse(anchorProblems[0].Code,
                    "ReplaceRange: " + string.Join(" ", anchorProblems.Select(problem => problem.Message))
                    + " An anchor matches one line or one contiguous block of lines (leading/trailing whitespace of each line ignored). Nothing was written.");
            }

            // The range starts at the first anchor's first line and ends at the last anchor's last line.
            var firstLine = firstHits[0].Start;
            var lastLine = lastHits[0].End;
            if (lastLine < firstLine)
            {
                return Refuse(ToolErrorCode.InvalidArgument,
                    $"ReplaceRange: lastLineText is on line {lastLine + 1} but firstLineText is on line {firstLine + 1}; the range runs from the first line down to the last. Nothing was written.");
            }

            // The range is whole lines, inclusive of both anchors, terminators included.
            var rangeStart = oldText.Lines[firstLine].Start;
            var rangeEnd = oldText.Lines[lastLine].EndIncludingLineBreak;
            var rangeEndedWithTerminator = rangeEnd > oldText.Lines[lastLine].End;
            var targetPath = document?.FilePath ?? filePathResolved;
            var targetEol = _workspaceManager.ResolveEol((string)targetPath, oldText.ToString());

            // Only the replacement is normalized to the file's line ending; the chokepoint keeps the untouched lines as they are.
            var replacement = EolUtilities.NormalizeEol(newContent, targetEol);
            if (replacement.Length > 0)
            {
                var endsWithTerminator = replacement.EndsWith('\n') || replacement.EndsWith('\r');
                if (rangeEndedWithTerminator && !endsWithTerminator)
                {
                    // The range ended with a line terminator, so the replacement does too (the next line stays on its own line).
                    replacement += targetEol;
                }
                else if (!rangeEndedWithTerminator && endsWithTerminator)
                {
                    // The range ended the file without a terminator; keep that (drop one trailing terminator).
                    replacement = replacement.EndsWith("\r\n", StringComparison.Ordinal) ? replacement[..^2] : replacement[..^1];
                }
            }

            // Same indent pass as ReplaceSnippet: the splice is the whole range (terminators included) with the adjusted replacement.
            // A deletion has no inserted lines to indent, so it keeps the plain splice.
            IReadOnlyList<TextChange> spliceEdits = [new TextChange(new TextSpan(rangeStart, rangeEnd - rangeStart), replacement)];
            var reindent = document is null || replacement.Length == 0
                ? NewContentIndenter.SplicePlain(oldText, spliceEdits)
                : await NewContentIndenter.ReindentAsync(document, spliceEdits, cancellationToken);
            var newFileContent = reindent.Text;
            var oldTextString = oldText.ToString();
            var noTypeLeft = DescribeNoTypeLeft(DisplayPath(filePathResolved), oldTextString, newFileContent, batch: false, toolName: "ReplaceRange");
            if (noTypeLeft is not null)
            {
                return Refuse(ToolErrorCode.TargetIneligible, noTypeLeft);
            }

            var rangeChanges = new DocumentChangeSet
            {
                [targetPath] = newFileContent
            };
            // The replaced span in the baseline text and its replacement, for impact attribution (same key instance as rangeChanges).
            IReadOnlyDictionary<FilePathWrapper, IReadOnlyList<TextChange>> editedSpans = new Dictionary<FilePathWrapper, IReadOnlyList<TextChange>>
            {
                [rangeChanges.Keys.Single()] = [new TextChange(new TextSpan(rangeStart, rangeEnd - rangeStart), reindent.InsertedTexts[0])]
            };
            // Visible to parallel calls' impact analysis until this call ends (see InFlightEditMemory).
            using var inFlight = _inFlightEdits.Register([((string)targetPath, reindent.InsertedTexts[0])]);

            var lineCount = lastLine - firstLine + 1;
            var rangeText = firstLine == lastLine ? $"line {firstLine + 1}" : $"lines {firstLine + 1}-{lastLine + 1}";
            var replacementLines = replacement.Length == 0 ? 0 : replacement.Split('\n').Length - (replacement.EndsWith('\n') ? 1 : 0);
            var rangeNote = replacement.Length == 0
                ? $"ReplaceRange deleted {rangeText} ({lineCount} line(s))."
                : $"ReplaceRange replaced {rangeText} ({lineCount} line(s)) with {replacementLines} line(s).";
            // Same wording as ReplaceSnippet's note; ReplaceRange does not honour returnDiff, so that clause is left out.
            var indentNote = reindent.LinesAdjusted > 0 ? $"newContent indentation was adjusted to the file's on {reindent.LinesAdjusted} line(s)." : null;
            // Near-miss correction and rename lift are tied to oldContent text and stay off; no edit memory (nothing to key on without oldContent).
            return await ApplyEditTailAsync(
                "ReplaceRange", "replace_range", (string)targetPath, rangeChanges, editedSpans,
                StandardCompileRejectionHeadline("ReplaceRange", "the anchors still match the file as it is now; fix newContent and send it again. "),
                [rangeNote, lastResolvedNote, indentNote], validateOnApply: true, returnDiff: false,
                enableNearMissCorrection: false, enableRenameLift: false, editMemory: null, cancellationToken);
        }
        catch (Exception ex)
        {
            if (ex is ToolException toolEx)
                _logger.LogInformation("ReplaceRange refused for '{FilePath}': {Message}", filePath, toolEx.Message);
            else
                _logger.LogError(ex, "ReplaceRange unexpected exception for '{FilePath}'", filePath);
            return new SentinelCallToolResult<ReplaceSnippetResult>()
            {
                IsError = true,
                ErrorData = ToolErrorMapper.ToResultError(ex, _workspaceManager, $"ReplaceRange for '{filePath}'")
            };
        }
    }

    // An anchor's lines, each trimmed of leading/trailing whitespace (a multi-line anchor matches a contiguous block of file lines).
    private static string[] SplitAnchorLines(string anchorText) =>
        anchorText.Trim().Split(["\r\n", "\n", "\r"], StringSplitOptions.None).Select(line => line.Trim()).ToArray();

    // How many consecutive anchor lines, from the first, equal the file lines starting at 0-based line 'start' (whitespace at each line's ends ignored).
    private static int CountMatchingAnchorLines(SourceText text, string[] anchorLines, int start)
    {
        var matched = 0;
        while (matched < anchorLines.Length
            && start + matched < text.Lines.Count
            && string.Equals(text.ToString(text.Lines[start + matched].Span).Trim(), anchorLines[matched], StringComparison.Ordinal))
        {
            matched++;
        }

        return matched;
    }

    // 0-based (Start, End) line pairs of every contiguous block of file lines that equals the anchor lines, line by line.
    private static List<(int Start, int End)> FindAnchorBlocks(SourceText text, string[] anchorLines)
    {
        var blocks = new List<(int Start, int End)>();
        for (var start = 0; start + anchorLines.Length <= text.Lines.Count; start++)
        {
            if (CountMatchingAnchorLines(text, anchorLines, start) == anchorLines.Length)
            {
                blocks.Add((start, start + anchorLines.Length - 1));
            }
        }

        return blocks;
    }

    // Up to MaxAnchorNearLines lines (1-based number and text) that contain the text, case-insensitive; empty text when none.
    private static string DescribeLinesContaining(SourceText text, string trimmedText)
    {
        var near = text.Lines
            .Where(line => text.ToString(line.Span).Contains(trimmedText, StringComparison.OrdinalIgnoreCase))
            .Take(MaxAnchorNearLines)
            .Select(line => $"{line.LineNumber + 1}: {text.ToString(line.Span).Trim()}")
            .ToList();
        return near.Count == 0 ? "" : $" Lines containing that text (case-insensitive): {string.Join(" | ", near)}.";
    }

    // Facts for an anchor that does not match exactly one line/block: the count and every matching 1-based line number with its text
    // (several matches); or, with no match, nearby lines containing the anchor (one line) or the closest partial block (several lines).
    private static (string Code, string Message)? DescribeAnchorMatchCount(string parameterName, string[] anchorLines, List<(int Start, int End)> hits, SourceText text, string displayPath)
    {
        if (hits.Count == 1)
        {
            return null;
        }

        var multiLine = anchorLines.Length > 1;
        if (hits.Count > 1)
        {
            var spans = string.Join(", ", hits.Select(hit => multiLine ? $"{hit.Start + 1}-{hit.End + 1}" : $"{hit.Start + 1}"));
            var candidates = string.Join(" | ", hits.Take(MaxAnchorCandidates).Select(hit => $"{hit.Start + 1}: {text.ToString(text.Lines[hit.Start].Span).Trim()}"));
            var more = hits.Count > MaxAnchorCandidates ? $" (+{hits.Count - MaxAnchorCandidates} more)" : "";
            var matchesText = multiLine
                ? $"matches {hits.Count} blocks of {anchorLines.Length} lines of '{displayPath}' (lines {spans})"
                : $"matches {hits.Count} lines of '{displayPath}' (lines {spans})";
            return (ToolErrorCode.Ambiguous,
                $"{parameterName} {matchesText}. Candidates (first line of each): {candidates}{more}."
                + $" A {parameterName} that also includes the neighbouring lines matches one contiguous block, which narrows the match.");
        }

        if (!multiLine)
        {
            return (ToolErrorCode.NotFound, $"{parameterName} matches 0 lines of '{displayPath}'.{DescribeLinesContaining(text, anchorLines[0])}");
        }

        // Several lines: report the start whose block agrees with the most leading anchor lines, and the first line where it differs.
        var best = (Start: -1, Matched: 0);
        for (var start = 0; start < text.Lines.Count; start++)
        {
            var matched = CountMatchingAnchorLines(text, anchorLines, start);
            if (matched > best.Matched)
            {
                best = (start, matched);
            }
        }

        string closest;
        if (best.Matched == 0)
        {
            closest = DescribeLinesContaining(text, anchorLines[0]);
        }
        else
        {
            var fileLineIndex = best.Start + best.Matched;
            var fileSide = fileLineIndex < text.Lines.Count
                ? $"file line {fileLineIndex + 1} is '{text.ToString(text.Lines[fileLineIndex].Span).Trim()}'"
                : "the file ends there";
            closest = $" The closest block starts at line {best.Start + 1} and agrees for {best.Matched} of {anchorLines.Length} lines; anchor line {best.Matched + 1} is '{anchorLines[best.Matched]}' but {fileSide}.";
        }

        return (ToolErrorCode.NotFound, $"{parameterName} matches 0 blocks of {anchorLines.Length} consecutive lines of '{displayPath}'.{closest}");
    }

    private async Task<SentinelCallToolResult<ReplaceSnippetResult>> ReplaceSnippetBatch(
        List<SnippetEdit> batchEdits,
        ProposedChangeAction action,
        bool validateOnApply,
        bool returnDiff,
        CancellationToken cancellationToken)
    {
        if (batchEdits.Count > MaxSnippetEditsPerBatch)
        {
            return new SentinelCallToolResult<ReplaceSnippetResult>()
            {
                IsError = true,
                ErrorData = new ResultError(ToolErrorCode.InvalidArgument,
                    $"ReplaceSnippet: batchEdits has {batchEdits.Count} entries (limit {MaxSnippetEditsPerBatch}). Split into multiple calls.")
            };
        }

        var perEditErrors = new List<string>();
        // Batch V R8: one note per edit whose wrong matchLine was ignored, prefixed to the batch summary.
        var selectorNotes = new List<string>();
        var anySizeBoundExceeded = false;
        // Shape of the oversized NEW content, combined across edits: the most general shape wins
        // (FileLevel > MultipleMembers > Unspecified > SingleMember), so one non-member edit is never hidden.
        var oversizedShape = RejectedContentShape.Unspecified;
        var sawOversized = false;
        static RejectedContentShape CombineShapes(RejectedContentShape a, RejectedContentShape b) => ShapeRank(a) >= ShapeRank(b) ? a : b;
        static int ShapeRank(RejectedContentShape shape) => shape switch
        {
            RejectedContentShape.FileLevel => 3,
            RejectedContentShape.MultipleMembers => 2,
            RejectedContentShape.Unspecified => 1,
            _ => 0
        };
        for (int i = 0; i < batchEdits.Count; i++)
        {
            var edit = batchEdits[i];
            if (string.IsNullOrEmpty(edit.FilePath))
            {
                perEditErrors.Add($"batchEdits[{i}] has no filePath. Options: set filePath on batchEdits[{i}], or pass one top-level filePath that every item without its own filePath uses.");
                continue;
            }
            if (string.IsNullOrEmpty(edit.OldContent))
            {
                perEditErrors.Add($"batchEdits[{i}] ({edit.FilePath}): oldContent is required; widen oldContent or pass matchLine to disambiguate it.");
                continue;
            }
            if (edit.NewContent == null)
            {
                var missingNewContent = $"batchEdits[{i}] has no newContent (newContent is required; an empty string \"\" is a valid pure deletion).";
                if (edit.OldContent.Contains("\"newContent\":") || edit.OldContent.Contains("\\\"newContent\\\":")
                    || edit.OldContent.EndsWith("\", ") || edit.OldContent.EndsWith("\\\", "))
                {
                    missingNewContent += " oldContent appears to contain the start of the next key; a quote in the text may have ended the string early. Option: escape every quote inside oldContent and newContent.";
                }
                perEditErrors.Add(missingNewContent);
                continue;
            }
            var exceeded = DescribeExceededSnippetSizeBounds(edit.OldContent, edit.NewContent);
            if (exceeded.Count > 0)
            {
                perEditErrors.Add($"batchEdits[{i}] ({edit.FilePath}): {string.Join("; ", exceeded)}.");
                anySizeBoundExceeded = true;
                var shape = RejectedContentClassifier.Classify(edit.NewContent);
                oversizedShape = sawOversized ? CombineShapes(oversizedShape, shape) : shape;
                sawOversized = true;
            }
        }

        if (perEditErrors.Count > 0)
        {
            // Same escape-hatch advice as the single-edit path, appended once (not per edit) and only
            // when a size bound tripped - it is noise for e.g. a missing newContent.
            var sizeAdvice = anySizeBoundExceeded
                ? "\n" + _writeAdvice.AdviseForOversizedEdit(CallableSnippetTool(), oversizedShape).Sentence
                : "";
            return new SentinelCallToolResult<ReplaceSnippetResult>()
            {
                IsError = true,
                ErrorData = new ResultError(ToolErrorCode.InvalidArgument, "ReplaceSnippet batch rejected before anchoring:\n" + string.Join("\n", perEditErrors) + sizeAdvice)
            };
        }

        var failureCodes = new List<string>();
        var editsByFile = batchEdits
            .Select((edit, index) => (edit, index))
            .GroupBy(pair => _workspaceManager.ResolveFromWire(pair.edit.FilePath));

        var finalContents = new DocumentChangeSet();
        // Per file, each edit's matched span in the baseline text and its replacement, for rename lifting.
        var liftSpans = new Dictionary<FilePathWrapper, IReadOnlyList<TextChange>>();
        // Per file, each item's span in the NEW text, to name the batchEdits item a compile error falls in.
        var batchItemSpans = new List<BatchItemSpan>();
        var newContentsByFile = new Dictionary<string, List<(string? OldContent, string NewContent)>>(StringComparer.OrdinalIgnoreCase);
        foreach (var fileGroup in editsByFile)
        {
            var filePathResolved = fileGroup.Key;
            // Only a blank path is 'path could not be resolved.'. A non-blank unvalidated path (misspelt or missing file)
            // falls through to DocumentLookup below so its near-match or no-match text reaches the model.
            if (!filePathResolved.Validated
                && (filePathResolved.FailureReason == FilePathFailureReason.NoSolutionLoaded || string.IsNullOrWhiteSpace(filePathResolved.Absolute)))
            {
                perEditErrors.Add($"'{fileGroup.Key}': {(filePathResolved.FailureReason == FilePathFailureReason.NoSolutionLoaded ? SolutionNotLoadedMessage.Build(_workspaceManager.LoadState) : "path could not be resolved.")}");
                continue;
            }

            // One lookup, owned by the read chokepoint: case-insensitive exact path, then a unique
            // bare name; a miss names the closest real paths (or the ambiguous candidates).
            var (document, nonProjectText, lookup) = await ResolveEditTargetAsync(filePathResolved, cancellationToken);
            if (document is null && nonProjectText is null)
            {
                perEditErrors.Add(lookup.Describe());
                failureCodes.Add(lookup.Status == DocumentLookupStatus.Ambiguous ? ToolErrorCode.Ambiguous : ToolErrorCode.NotFound);
                continue;
            }

            // Key the result by the document's own path, not the caller's spelling. Two groups can
            // reach the same file under different spellings (e.g. a bare name and a full path); each
            // would otherwise be spliced against the original text and the later one would silently
            // overwrite the earlier one's batchEdits.
            var canonicalPath = new FilePathWrapper(document?.FilePath ?? filePathResolved.Absolute, _workspaceManager.GetSolutionRoot(), validated: true);
            if (finalContents.ContainsKey(canonicalPath))
            {
                perEditErrors.Add($"'{fileGroup.Key}' resolves to '{canonicalPath}', which another edit entry in this batch already targets under a different spelling. Use the same path string for every edit to one file so they are anchored together.");
                continue;
            }

            // A non-project text file has no Document: its disk text is matched as plain text (no syntax root, no member names, no re-indent).
            var originalText = nonProjectText ?? await document!.GetTextAsync(cancellationToken);
            var originalString = originalText.ToString();
            var fileRoot = document is null ? null : await document.GetSyntaxRootAsync(cancellationToken);
            var memberNameAt = MemberNameResolverFor(fileRoot);

            // Resolve every edit's match against the file's ORIGINAL text -> never against a prior
            // edit's output within this same batch. This is what removes the sequencing hazard a
            // series of single ReplaceSnippetTool calls has: every edit here is anchored against one
            // fixed snapshot, not a chain of intermediate results the model never sees.
            var resolved = new List<(int index, SnippetMatch match, string? oldContent, string newContent)>();
            foreach (var (edit, index) in fileGroup)
            {
                var leakedMarkup = DescribeLeakedNewContentMarkup(edit.NewContent!, () => originalString);
                if (leakedMarkup is not null)
                {
                    perEditErrors.Add($"batchEdits[{index}] ({filePathResolved}): {leakedMarkup}");
                    failureCodes.Add(ToolErrorCode.InvalidArgument);
                    continue;
                }

                try
                {
                    var (match, selectorNote) = ResolveEditMatch(
                        originalText, fileRoot, edit.OldContent, edit.LineBefore, edit.LineAfter, memberNameAt: memberNameAt,
                        withinMember: edit.WithinMember, matchLine: edit.MatchLine);
                    if (selectorNote is not null)
                        selectorNotes.Add($"batchEdits[{index}]: {selectorNote}");
                    resolved.Add((index, match, edit.OldContent, edit.NewContent!));
                }
                catch (ToolException toolEx)
                {
                    // TODO duplicate-apply no-op for batch edits (single-edit path has it: AppliedEditMemory, Batch H4 R1)
                    var rejectedOriginNote = toolEx is ToolNotFoundException && _rejectedEdits.Explains((string)canonicalPath, edit.OldContent, originalString)
                        ? " " + RejectedOriginNote
                        : "";
                    // H3: or the oldContent holds the old text of a line the server rewrote earlier (a lifted rename or RenameSymbol).
                    if (rejectedOriginNote.Length == 0 && toolEx is ToolNotFoundException && RewrittenLineMemory.Shared.Explain((string)canonicalPath, edit.OldContent, originalString) is { } rewriteNote)
                        rejectedOriginNote = " " + rewriteNote;
                    perEditErrors.Add($"batchEdits[{index}] ({filePathResolved}): {toolEx.Message}{rejectedOriginNote}"); failureCodes.Add(toolEx.ErrorCode);
                }
            }

            if (perEditErrors.Count > 0)
            {
                continue;
            }

            // Reject overlapping matches before splicing anything -> silent last-writer-wins here is
            // the same corruption class as the closed ReplaceSnippetTool silent-splice-corruption finding
            // on adjacent lines (wrong match length previously corrupted a neighboring line with no
            // error at all).
            var byStart = resolved.OrderBy(r => r.match.Start).ToList();
            for (int i = 1; i < byStart.Count; i++)
            {
                var prev = byStart[i - 1];
                var curr = byStart[i];
                if (curr.match.Start < prev.match.Start + prev.match.Length)
                {
                    perEditErrors.Add(
                        $"batchEdits[{prev.index}] and batchEdits[{curr.index}] ({filePathResolved}) have overlapping matches " +
                        $"(batchEdits[{prev.index}]: [{prev.match.Start}, {prev.match.Start + prev.match.Length}), " +
                        $"batchEdits[{curr.index}]: [{curr.match.Start}, {curr.match.Start + curr.match.Length})). " +
                        "Split these into separate ReplaceSnippet calls.");
                }
            }

            if (perEditErrors.Count > 0)
            {
                continue;
            }

            // All matches were computed against originalString, so one indent-aware splice applies them
            // together. The indent pass runs on every successful match, exact ones included.
            IReadOnlyList<TextChange> spliceEdits = resolved.Select(r => new TextChange(new TextSpan(r.match.Start, r.match.Length), r.newContent)).ToList();
            var reindent = document is null
                ? NewContentIndenter.SplicePlain(originalText, spliceEdits)
                : await NewContentIndenter.ReindentAsync(document, spliceEdits, cancellationToken);
            var spliced = reindent.Text;
            if (reindent.LinesAdjusted > 0)
                selectorNotes.Add($"{DisplayPath(filePathResolved)}: newContent indentation was adjusted to the file's on {reindent.LinesAdjusted} line(s); returnDiff shows the written text.");

            var targetEol = _workspaceManager.ResolveEol((string)canonicalPath, originalString);
            var normalizedSpliced = EolUtilities.NormalizeEol(spliced, targetEol);
            var noTypeLeft = DescribeNoTypeLeft(DisplayPath(filePathResolved), originalString, normalizedSpliced, batch: true);
            if (noTypeLeft is not null)
            {
                return new SentinelCallToolResult<ReplaceSnippetResult>()
                {
                    IsError = true,
                    ErrorData = new ResultError(ToolErrorCode.TargetIneligible, noTypeLeft)
                };
            }
            finalContents[canonicalPath] = normalizedSpliced;
            liftSpans[canonicalPath] = resolved.Select((r, i) => new TextChange(new TextSpan(r.match.Start, r.match.Length), reindent.InsertedTexts[i])).ToList();
            batchItemSpans.AddRange(BatchItemSpansInNewText(
                (string)canonicalPath, spliced, normalizedSpliced, targetEol,
                resolved.Select((r, i) => (r.index, r.match.Start, r.match.Length, reindent.InsertedTexts[i])).ToList()));
            newContentsByFile[(string)canonicalPath] = resolved.Select(r => (r.oldContent, r.newContent)).ToList();
        }

        if (perEditErrors.Count > 0)
        {
            // When every rejection carries its own code (a failed path lookup, or a snippet match that was
            // ambiguous/missing), report that code so a caller can branch on it. Mixes of NotFound, Ambiguous and MatchedLooserThanRequested report Ambiguous; any other mix or unattributed rejection stays InvalidArgument.
            var errorCode = failureCodes.Count > 0 && failureCodes.Count == perEditErrors.Count
                ? (failureCodes.Distinct().Count() == 1 ? failureCodes[0] : failureCodes.All(c => c == ToolErrorCode.Ambiguous || c == ToolErrorCode.NotFound || c == ToolErrorCode.MatchedLooserThanRequested) ? ToolErrorCode.Ambiguous : ToolErrorCode.InvalidArgument)
                : ToolErrorCode.InvalidArgument;
            return new SentinelCallToolResult<ReplaceSnippetResult>()
            {
                IsError = true,
                ErrorData = new ResultError(errorCode, BatchFailureMessage.Compose("ReplaceSnippet batch rejected - nothing was changed; every file is exactly as you last read it:", perEditErrors))
            };
        }

        // Visible to parallel calls' impact analysis until this call ends (see InFlightEditMemory).
        using var inFlight = _inFlightEdits.Register(newContentsByFile.SelectMany(kv => kv.Value.Select(c => (kv.Key, c.NewContent ?? string.Empty))));

        if (action == ProposedChangeAction.validate)
        {
            // validate returns here, before the apply summary, so no selector note is reported on this path.
            var validationResult = await _validationEngine.ValidateChangesAsync(finalContents, cancellationToken: cancellationToken);
            return !validationResult.IsError
                ? new SentinelCallToolResult<ReplaceSnippetResult>() { IsError = false, SuccessData = new ReplaceSnippetResult(null, validationResult, null) }
                : new SentinelCallToolResult<ReplaceSnippetResult>()
                {
                    IsError = true,
                    // A dry run: same impact/redirect/hint body as apply, but no _rejectedEdits record or repeat check and no rename lifting (nothing is written).
                    ErrorData = await ComposeCompileRejectionAsync(
                        "ReplaceSnippet batch validate: every edit matched, but the resulting code introduces new compiler errors (nothing was written; every file is exactly as you last read it). ",
                        isRepeat: false, liftText: null, finalContents, validationResult, liftSpans, liftVerdicts: null, cancellationToken, batchItemSpans)
                };
        }

        try
        {
            // One key per edit for the rejected-edit memory; a null inserted text keeps the batch out of the applied-edit memory (TODO duplicate-apply no-op above).
            var batchEditKeys = newContentsByFile
                .SelectMany(kv => kv.Value.Select(c => (Path: kv.Key, c.OldContent, c.NewContent, InsertedText: (string?)null)))
                .ToList();
            return await ApplyEditTailAsync(
                "ReplaceSnippet", "replace_snippet_batch", newContentsByFile.Keys.First(), finalContents, liftSpans,
                "ReplaceSnippet batch: every edit matched, but the resulting code introduces new compiler errors, so nothing was changed; every file is exactly as you last read it. ",
                selectorNotes, validateOnApply, returnDiff,
                enableNearMissCorrection: true, enableRenameLift: true, batchEditKeys, cancellationToken, batchItemSpans);
        }
        catch (Exception ex)
        {
            if (ex is ToolException toolEx)
                _logger.LogInformation("ReplaceSnippet batch ({Action}) refused for {Count} file(s): {Message}", action, finalContents.Count, toolEx.Message);
            else
                _logger.LogError(ex, "ReplaceSnippet batch ({Action}) unexpected exception for {Count} file(s)", action, finalContents.Count);
            return new SentinelCallToolResult<ReplaceSnippetResult>()
            {
                IsError = true,
                ErrorData = ToolErrorMapper.ToResultError(ex, _workspaceManager, $"ReplaceSnippet batch {action} for {finalContents.Count} file(s)")
            };
        }
    }

    public async Task<SentinelCallToolResult<object>> CreateFile(
        ToolCallReason reason,
        string filePath,
        string? namespaceName = null,
        NewTypeKind? typeKind = null,
        string? typeName = null,
        string? content = null,
        CancellationToken cancellationToken = default)
    {
        FilePathWrapper filePathResolved = _workspaceManager.ResolveFromWire(filePath);
        try
        {
            if (!filePathResolved.Validated)
            {
                return new SentinelCallToolResult<object>()
                {
                    IsError = true,
                    ErrorData = filePathResolved.FailureReason == FilePathFailureReason.NoSolutionLoaded
                        ? new ResultError(ToolErrorCode.SolutionNotLoaded, SolutionNotLoadedMessage.ForFilePath("CreateFile", _workspaceManager.LoadState))
                        : new ResultError(ToolErrorCode.InvalidArgument, "CreateFile: 'filePath' is required.")
                };
            }

            // 10-chain batch E run R2 (2026-11-10): after a file was emptied, ReplaceSnippet and CreateFile both refused and the model never recovered.
            var existedEmpty = File.Exists(filePathResolved.Absolute) && string.IsNullOrWhiteSpace(await File.ReadAllTextAsync(filePathResolved.Absolute, cancellationToken));
            if (File.Exists(filePathResolved.Absolute) && !existedEmpty)
            {
                return new SentinelCallToolResult<object>()
                {
                    IsError = true,
                    ErrorData = new ResultError(ToolErrorCode.InvalidArgument, $"CreateFile: '{filePathResolved}' already exists. CreateFile never overwrites - use {string.Join("/", ExistingFileEditToolHints())} to edit an existing file.")
                };
            }

            bool hasSeedArguments = typeKind != null || !string.IsNullOrWhiteSpace(typeName) || !string.IsNullOrWhiteSpace(namespaceName);
            if (content != null && hasSeedArguments)
            {
                return new SentinelCallToolResult<object>()
                {
                    IsError = true,
                    ErrorData = new ResultError(ToolErrorCode.InvalidArgument, "CreateFile: pass either 'content' (the full file body) or 'typeKind' and 'typeName' (an empty seeded type), not both.")
                };
            }

            bool isCSharpFile = filePathResolved.Absolute.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);
            if (isCSharpFile && content == null && (typeKind == null || string.IsNullOrWhiteSpace(typeName)))
            {
                return new SentinelCallToolResult<object>()
                {
                    IsError = true,
                    ErrorData = new ResultError(ToolErrorCode.InvalidArgument, $"CreateFile: 'typeKind' and 'typeName' are both required for a .cs file, so the new file starts with an empty top-level type that {ToolCallHint.Call("Member", ("operation", "addMember"))} can populate members into. Or pass 'content' to write the whole file in one call.")
                };
            }

            string body;
            if (content != null)
            {
                body = content;
            }
            else if (isCSharpFile)
            {
                string keyword = typeKind!.Value == NewTypeKind.staticClass ? "static class" : typeKind.Value.ToString().TrimStart('@');
                // A null/whitespace namespaceName means the global namespace, by design (e.g. an NUnit [SetUpFixture]).
                body = string.IsNullOrWhiteSpace(namespaceName)
                    ? $"public {keyword} {typeName}\n{{\n}}\n"
                    : $"namespace {namespaceName};\n\npublic {keyword} {typeName}\n{{\n}}\n";
            }
            else
            {
                body = "";
            }

            var directory = Path.GetDirectoryName((string)filePathResolved.Absolute);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            // TODO a .cs file outside every project folder (e.g. the solution root) is written without compile validation: no project owns it, so the gate compiles nothing; WriteFile has the same gap
            var changes = new DocumentChangeSet { [filePathResolved] = body };
            var result = await _workspaceManager.ApplyProposedChangesAsync(changes, validateChanges: true, cancellationToken: cancellationToken);
            if (result.IsError && result.ValidationResult != null)
            {
                return new SentinelCallToolResult<object>()
                {
                    IsError = true,
                    ErrorData = await EditImpactGate.RejectionAsync(_workspaceManager, changes, result.ValidationResult, _activeToolSurface,
                        "CreateFile: this content would introduce new compiler errors - not written to disk. Fix the issue(s) below and retry:\n" +
                        "[COMPILER ERROR]\n",
                        renamedAway => CompilerErrorLookupHelper.DescribeAsync(result.ValidationResult, _symbolNavigationEngine, cancellationToken, isToolActive: _activeToolSurface is null ? null : _activeToolSurface.IsCallable, renamedAwayNames: renamedAway), cancellationToken)
                };
            }

            if (result.IsError)
            {
                return new SentinelCallToolResult<object>()
                {
                    IsError = true,
                    ErrorData = new ResultError(ToolErrorCode.Exception, $"CreateFile failed to write '{filePathResolved}': {result.Summary}")
                };
            }

            var changeId = Guid.NewGuid().ToString("n")[..8];
            var blobWrite = await OperationBlobHelper.WriteBlobForApplyAsync(_logger, _workspaceManager, "create_file", result, cancellationToken: cancellationToken, blobChangeId: changeId);
            var strippedResult = result with { PreImages = null, ChangeId = blobWrite.Written ? changeId : null };
            var createFileFindings = new List<Finding>();
            createFileFindings.AddRange(result.Findings ?? []);
            return new SentinelCallToolResult<object>()
            {
                IsError = false,
                SuccessData = strippedResult,
                Findings = createFileFindings,
                // 10-chain batch E run R2 (2026-11-10): after a file was emptied, ReplaceSnippet and CreateFile both refused and the model never recovered.
                StatusMessage = existedEmpty ? $"'{DisplayPath(filePathResolved)}' already existed but was empty; its content was written." : null
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "CreateFile failed for '{FilePathWrapper}'", filePathResolved);
            return new SentinelCallToolResult<object>()
            {
                IsError = true,
                ErrorData = ToolErrorMapper.ToResultError(ex, _workspaceManager, "CreateFile")
            };
        }
    }

    /// <summary>A successful write of <paramref name="path"/> makes both per-file memories stale: the rejected-edit rows and the server-side rewrite rows (<see cref="RewrittenLineMemory"/>).</summary>
    private void ForgetFileMemories(string path)
    {
        _rejectedEdits.Forget(path);
        RewrittenLineMemory.Shared.Forget(path);
    }
}
