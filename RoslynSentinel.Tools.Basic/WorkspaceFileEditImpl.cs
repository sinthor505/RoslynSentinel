using System.Text.Json;

using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging;

using RoslynSentinel.Common;
using RoslynSentinel.Common.SnippetMatching;
using RoslynSentinel.Engines.Basic;

namespace RoslynSentinel.Tools.Basic;

/// <summary>
/// Plain DI-constructed implementation backing WorkspaceFileEditTools. Method bodies moved
/// verbatim from WorkspaceTools (Decision 7 step 2).
///
/// CORRECTION vs. the plan doc's Decision 1 (flagged per Decision 7 step 1.5's audit mandate):
/// Decision 1 listed this class as 7 tools (ApplyDiffTool, ApplyUnifiedDiffTool, WriteFileTool, DeleteFileTool,
/// RetryFailedChangesTool, UndoLastApplyTool, ReadFileTool) with DiffEngine as a dependency. As of 2026-09-17,
/// WorkspaceTools.cs only actually still has 3 of those 7 live as [McpServerTool] methods
/// (RetryFailedChangesTool, UndoLastApplyTool, ReadFileTool) - the other 4 (ApplyDiffTool/ApplyUnifiedDiffTool/
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
        private readonly Dictionary<string, List<string>> _byFile = new(StringComparer.OrdinalIgnoreCase);
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

        public void Remember(string path, string? newContent)
        {
            // A pure deletion leaves nothing the model could mistake for file text.
            if (string.IsNullOrWhiteSpace(newContent))
                return;
            lock (_gate)
            {
                var key = Key(path);
                if (!_byFile.TryGetValue(key, out var entries))
                    _byFile[key] = entries = [];
                entries.Add(newContent);
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
                remembered = [.. entries];
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

    public async Task<SentinelCallToolResult<object>> UndoLastApply(ToolCallReason reason, string changeId, CancellationToken cancellationToken = default)
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
                        "'not reversible', there is no undo record to find and the change must be reverted manually.")
                };
            }

            var json = await File.ReadAllTextAsync(blobPath, cancellationToken);
            var doc = JsonSerializer.Deserialize<JsonElement>(json);
            var revertable = doc.GetProperty("items").EnumerateArray().Select(e => JsonSerializer.Deserialize<OperationItemRecord>(e.GetRawText())!).Where(r => r.Outcome == ItemRecordOutcome.Succeeded && r.BeforeSource != null).ToList();
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

            var failed = new List<string>();
            var revertChanges = new Dictionary<FilePathWrapper, string>();
            foreach (var item in revertable)
            {
                if (solutionRoot != null && !item.FilePath.StartsWith(solutionRoot, StringComparison.OrdinalIgnoreCase))
                {
                    failed.Add($"{item.FilePath}: outside solution root, skipped");
                    continue;
                }

                revertChanges[item.FilePath] = item.BeforeSource!;
            }

            ApplyChangesResult? revertResult = null;
            var reverted = new List<string>();
            var noOpFiles = new List<string>();
            if (revertChanges.Count > 0)
            {
                // exactRestore: this writes back a pre-image the server captured itself, so it bypasses the
                // EOL-change guard and EOL normalization (a mixed-EOL pre-image over a since-normalized file would
                // otherwise be refused as an EOL change) and writes the captured bytes verbatim.
                revertResult = await _workspaceManager.ApplyProposedChangesAsync(
                    revertChanges, rollbackOnPartialFailure: true, cancellationToken: cancellationToken, exactRestore: true);
                var noOpSet = new HashSet<string>(revertResult.NoOpFiles ?? [], StringComparer.OrdinalIgnoreCase);
                foreach (var path in revertResult.SucceededFiles)
                {
                    if (noOpSet.Contains(path))
                    {
                        noOpFiles.Add(path);
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

            var failedPart = failed.Count > 0 ? $" Failures: {string.Join("; ", failed)}" : "";
            var noOpPart = noOpFiles.Count > 0
                ? $" ({noOpFiles.Count} already matched pre-apply state - no change needed: {string.Join(", ", noOpFiles)})"
                : "";
            return new SentinelCallToolResult<object>()
            {
                IsError = false,
                SuccessData = $"Reverted {reverted.Count} files{noOpPart}. Files: {string.Join(", ", reverted)}{failedPart}",
                Findings = revertResult?.Findings ?? []
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "UndoLastApply failed for '{ChangeId}'", changeId);
            return new SentinelCallToolResult<object>()
            {
                IsError = true,
                ErrorData = ToolErrorMapper.ToResultError(ex, _workspaceManager, "UndoLastApply")
            };
        }
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

                var agentType = latest.AgentType ?? "unknown";
                var tool = latest.Tool ?? "unknown";
                clauses.Add($"{item.FilePath} was changed by agent '{latest.AgentId}' (type {agentType}, tool {tool}) after agent '{applierId}' applied this change");
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

    /// <summary>FileNotFound for ReadFile. <paramref name="isCallable"/> says whether a tool name can be called on this
    /// server (null = every tool can); ListSolutionItems is named only when it can.</summary>
    internal static ResultError BuildFileNotFoundError(Microsoft.CodeAnalysis.Solution solution, string normalizedPath, Func<string, bool>? isCallable = null)
        => new("FileNotFound", FileNotFoundMessage.Build(normalizedPath, DocumentLookup.FindClosestPaths(solution, normalizedPath), solution.Projects.Count(), isCallable));

    private async Task<SentinelCallToolResult<object>> ReadFileWithoutSolutionAsync(string absolutePath, int? startLine, int? endLine, CancellationToken cancellationToken)
    {
        try
        {
            var diskContent = await FileIoHelper.ReadAllTextIfExistsAsync(absolutePath, cancellationToken);
            if (diskContent == null)
            {
                return new SentinelCallToolResult<object>()
                {
                    IsError = true,
                    ErrorData = new ResultError("FileNotFound", $"'{Path.GetFileName(absolutePath)}' does not exist at '{absolutePath}'. {SolutionNotLoadedMessage.Build(_workspaceManager.LoadState)}")
                };
            }

            var sourceText = SourceText.From(diskContent);
            var totalLines = sourceText.Lines.Count;
            var explanation = "No solution is loaded; read directly from disk (no outline or offload available). "
                + (_workspaceManager.LoadState is { IsFreshStartup: true } ? $"This server was freshly (re)started and has not loaded a solution yet; call {ToolCallHint.Call("LoadSolution")} to restore full functionality. " : string.Empty);

            if (startLine.HasValue || endLine.HasValue)
            {
                int from = Math.Max(1, startLine ?? 1);
                int to = Math.Min(totalLines, endLine ?? totalLines);
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
                return new SentinelCallToolResult<object>()
                {
                    IsError = false,
                    SuccessData = new { filePath = absolutePath, startLine = from, endLine = to, totalLines, source = slice },
                    HasMoreData = to < totalLines,
                    StatusMessage = explanation.TrimEnd(),
                    WorkspaceVersion = _workspaceManager.WorkspaceVersion,
                };
            }

            var textBytes = System.Text.Encoding.UTF8.GetByteCount(diskContent);
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
                SuccessData = new { filePath = absolutePath, startLine = 1, endLine = totalLines, totalLines, source = diskContent },
                HasMoreData = false,
                StatusMessage = explanation.TrimEnd(),
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

    private Task RecordWholeFileObservedAsync(FilePathWrapper path, CancellationToken cancellationToken) =>
        _workspaceManager.RecordWholeFileObservedAsync(path, cancellationToken);

    public async Task<SentinelCallToolResult<object>> ReadFile(ToolCallReason reason, string filePath, int? startLine = null,
        int? endLine = null, string? memberName = null, string? containerName = null, LineEndingDisplay showLineEndings = LineEndingDisplay.None, CancellationToken cancellationToken = default)
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
                var (_, _, _, source) = await _symbolNavigationEngine.GetMemberSourceAsync(filePathResolved, memberName, containerName, null, null, null, true, cancellationToken);
                // Extra whole-file fetch the member read costs (the member slice alone cannot be recorded).
                await RecordWholeFileObservedAsync(filePathResolved, cancellationToken);
                return new SentinelCallToolResult<object>() { IsError = false, SuccessData = new MemberSourceViewResult(source!) };
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
            // TODO showLineEndings is ignored on the no-solution ReadFile path
            return await ReadFileWithoutSolutionAsync(filePath, startLine, endLine, cancellationToken);
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
            if (document != null)
            {
                sourceText = await document.GetTextAsync(cancellationToken);
            }
            else
            {
                var diskContent = await FileIoHelper.ReadAllTextIfExistsAsync(filePathResolved, cancellationToken);
                if (diskContent == null)
                {
                    return new SentinelCallToolResult<object>()
                    {
                        IsError = true,
                        ErrorData = BuildFileNotFoundError(solution, normalizedPath, _activeToolSurface is null ? null : _activeToolSurface.IsCallable)
                    };
                }

                sourceText = SourceText.From(diskContent);
            }

            var totalLines = sourceText.Lines.Count;
            if (startLine.HasValue || endLine.HasValue)
            {
                int from = Math.Max(1, startLine ?? 1);
                int to = Math.Min(totalLines, endLine ?? totalLines);
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
                    Findings = sliceMixedEol is null ? [] : [new Finding("ReadFile", sliceMixedEol)],
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
                    Findings = mixedEol is null ? [] : [new Finding("ReadFile", mixedEol)],
                    HasMoreData = false,
                    StatusMessage = $"File is {totalLines} lines ({textBytes} bytes), over the inline limit: the first part is returned and the rest is stored (see largeResult). For a structure overview call {ToolCallHint.Call("GetFileOutline")}; for a slice call {ToolCallHint.Call("ReadFile", ("startLine", "1"), ("endLine", "200"))}." + (statusNote is null ? string.Empty : " " + statusNote),
                    WorkspaceVersion = _workspaceManager.WorkspaceVersion,
                };
            }

            return new SentinelCallToolResult<object>()
            {
                IsError = false,
                SuccessData = wholeData,
                Findings = mixedEol is null ? [] : [new Finding("ReadFile", mixedEol)],
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

    private const int MaxSnippetEditsPerBatch = 20;

    private static int CountCharsIgnoringLeadingIndentation(string content)
    {
        var total = 0;
        foreach (var line in content.Split('\n'))
        {
            total += line.TrimStart(' ', '\t').Length;
        }
        return total;
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

    /// <summary>
    /// Builds the delegate that lets an ambiguity error name the member enclosing each match.
    /// Returns null for a document with no syntax tree (e.g. a non-C# file), in which case the
    /// error simply omits the member names.
    /// </summary>
    private static Func<int, string?>? MemberNameResolverFor(Microsoft.CodeAnalysis.SyntaxNode? root)
        => root is null ? null : position => EnclosingMemberResolver.GetEnclosingMemberName(root, position);

    /// <summary>
    /// Resolves oldContent to the single span a ReplaceSnippetTool edit may splice, shared by the single and batch
    /// paths. The request tier is IgnoreLineEndings with case-sensitive comparison: the ordinal stage, then the
    /// CR/LF/CRLF-interchangeable stage, exactly what the removed legacy ReplaceSnippetTool matcher did, so the returned
    /// span is always the real removable span (see project_replacesnippet_silent_splice_corruption_adjacent_lines).
    /// Only <see cref="SnippetMatchOutcome.Matched"/> yields a match. Every other outcome, including a snippet that
    /// matches only at a looser tier (its near-matches are never edit candidates), is thrown through
    /// <see cref="SnippetErrorFormatter.Build"/> so the message lists the file's own candidate lines.
    /// </summary>
    private static SnippetMatch ResolveEditMatch(
        SourceText sourceText, string oldContent, string? lineBefore, string? lineAfter,
        Func<int, string?>? memberNameAt, string? withinMember, int? matchLine)
    {
        var result = SnippetResolver.Resolve(
            sourceText, oldContent, WhitespaceTolerance.IgnoreLineEndings, SnippetCaseMode.CaseSensitive,
            new SnippetSelectors(withinMember, matchLine, lineBefore, lineAfter), memberNameAt);
        if (result.Outcome != SnippetMatchOutcome.Matched)
        {
            throw SnippetErrorFormatter.Build(result, sourceText, oldContent, "oldContent", memberNameAt, selectorsSupported: true, adjacentLinesSupported: false);
        }

        return result.Matches.Single();
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
            bool hasSingularEdit = filePath is not null || !string.IsNullOrEmpty(oldContent) || newContent != null
                || withinMember is not null || matchLine is not null;
            bool hasBatchEdit = batchEdits != null;

            if (hasSingularEdit && hasBatchEdit)
            {
                return new SentinelCallToolResult<ReplaceSnippetResult>()
                {
                    IsError = true,
                    ErrorData = new ResultError(ToolErrorCode.InvalidArgument,
                        "ReplaceSnippet: supply either filePath/oldContent/newContent or 'batchEdits', not both.")
                };
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
            if (!filePathResolved.Validated)
            {
                return new SentinelCallToolResult<ReplaceSnippetResult>()
                {
                    IsError = true,
                    ErrorData = filePathResolved.FailureReason == FilePathFailureReason.NoSolutionLoaded
                        ? new ResultError(ToolErrorCode.SolutionNotLoaded, SolutionNotLoadedMessage.ForFilePath("ReplaceSnippet", _workspaceManager.LoadState))
                        : new ResultError(ToolErrorCode.InvalidArgument, "ReplaceSnippet: 'filePath' could not be resolved.")
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
                    var lookup = await _workspaceManager.GetDocumentAsync(filePathResolved, ReadSource.Committed, cancellationToken);
                    if (!lookup.TryGetDocument(out var document))
                    {
                        return new SentinelCallToolResult<ReplaceSnippetResult>()
                        {
                            IsError = true,
                            ErrorData = lookup.ToResultError()
                        };
                    }

                    var oldText = await document.GetTextAsync(cancellationToken: cancellationToken);
                    // ResolveEditMatch caps the tier at IgnoreLineEndings, so it never uses a
                    // whitespace-collapsing fallback and match.Length is always the real removable
                    // span. Using oldContent.Length instead of match.Length used to corrupt adjacent
                    // lines when a fallback match fired (see
                    // project_replacesnippet_silent_splice_corruption_adjacent_lines memory).
                    var root = await document.GetSyntaxRootAsync(cancellationToken);
                    SnippetMatch match;
                    try
                    {
                        match = ResolveEditMatch(
                            oldText, oldContent, lineBefore, lineAfter, memberNameAt: MemberNameResolverFor(root),
                            withinMember: withinMember, matchLine: matchLine);
                    }
                    catch (ToolNotFoundException notFound) when (_rejectedEdits.Explains((string)(document.FilePath ?? filePathResolved), oldContent, oldText.ToString()))
                    {
                        // Same error and code; only the text grows, so callers branching on NotFound are unaffected.
                        throw new ToolNotFoundException(notFound.Message + " " + RejectedOriginNote, notFound.MissingTargetKind, notFound.MissingTargetName, notFound.Snippet, notFound.Candidates);
                    }
                    var newFileContent = oldText.ToString().Remove(match.Start, match.Length).Insert(match.Start, newContent);
                    var targetPath = document.FilePath ?? filePathResolved;
                    var targetEol = _workspaceManager.ResolveEol((string)targetPath, oldText.ToString());
                    var normalizedContent = EolUtilities.NormalizeEol(newFileContent, targetEol);
                    var snippetChanges = new Dictionary<FilePathWrapper, string>
                    {
                        [targetPath] = normalizedContent
                    };

                    if (action == ProposedChangeAction.validate)
                    {
                        var validationResult = await _validationEngine.ValidateChangesAsync(snippetChanges, cancellationToken: cancellationToken);
                        return !validationResult.IsError ? new SentinelCallToolResult<ReplaceSnippetResult>()
                        {
                            IsError = false,
                            SuccessData = new ReplaceSnippetResult(null, validationResult, null)
                        }
                        : new SentinelCallToolResult<ReplaceSnippetResult>()
                        {
                            IsError = true,
                            ErrorData = new ResultError(ToolErrorCode.Exception, $"ReplaceSnippet validate failed: {validationResult.Diagnostics.ToInfo()}")
                        };
                    }

                    var result = await _workspaceManager.ApplyProposedChangesAsync(snippetChanges, validateChanges: validateOnApply, staleReadPolicy: StaleReadPolicy.RefuseIfObservedStale);
                    // TODO a non-mapped, non-validation refusal (IsError without ValidationResult) falls through to IsError = false below; map it once decided (plan_stale_read_guard_followups open decision 9)
                    if (WriteRefusal.TryMap("ReplaceSnippet", result, out var refusal))
                        return new SentinelCallToolResult<ReplaceSnippetResult>() { IsError = true, ErrorData = refusal };
                    string? untouchedNote = null;
                    if (result.IsError && result.ValidationResult != null)
                    {
                        _rejectedEdits.Remember((string)targetPath, newContent);
                        // The edit-impact report, when it can attribute the errors to unedited files, replaces the generic
                        // untouched-file sentence (it already covers it); null keeps that sentence.
                        untouchedNote = await EditImpactGate.DescribeAsync(_workspaceManager, snippetChanges, result.ValidationResult, _activeToolSurface, cancellationToken: cancellationToken)
                            ?? ValidateAndApplyHelper.DescribeUntouchedFileErrors(snippetChanges.Keys.Select(k => (string)k), result.ValidationResult.Diagnostics.Select(d => d.FilePath.ToString()), "one ReplaceSnippet call with batchEdits, one item per file, each with its own filePath");
                    }
                    if (result.IsError && result.ValidationResult != null)
                        return new SentinelCallToolResult<ReplaceSnippetResult>()
                        {
                            IsError = true,
                            ErrorData = new ResultError(ToolErrorCode.Exception,
                                "ReplaceSnippet: the edit matched the target file, but the resulting code introduces new compiler errors - change not applied. " +
                                (untouchedNote is null ? "" : untouchedNote + " ") +
                                "Fix the issue(s) below and retry:\n" +
                                "[COMPILER ERROR]\n" +
                                await CompilerErrorLookupHelper.DescribeAsync(result.ValidationResult, _symbolNavigationEngine, cancellationToken, snippetChanges.Keys.Select(k => (string)k).ToList()))
                        };
                    if (!result.IsError)
                        _rejectedEdits.Forget((string)targetPath);
                    await OperationBlobHelper.WriteBlobForApplyAsync(_logger, _workspaceManager, "replace_snippet", result, cancellationToken: cancellationToken);
                    var strippedResult = result with { PreImages = null };
                    string? diffContent = returnDiff
                        ? ValidateAndApplyHelper.BuildDiffFromPreImages(snippetChanges, result.PreImages)
                        : null;
                    return new SentinelCallToolResult<ReplaceSnippetResult>()
                    {
                        IsError = false,
                        SuccessData = new ReplaceSnippetResult(strippedResult, null, diffContent),
                        Findings = result.Findings ?? []
                    };
                }
                catch (Exception ex)
                {
                    // TODO an expected ToolException (a plain ToolNotFoundException from ResolveEditMatch) is logged here as "unexpected exception" at Error level; log ToolException at Information/Warning instead
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
                perEditErrors.Add($"batchEdits[{i}]: filePath is required.");
                continue;
            }
            if (string.IsNullOrEmpty(edit.OldContent))
            {
                perEditErrors.Add($"batchEdits[{i}] ({edit.FilePath}): oldContent is required; widen oldContent or pass matchLine to disambiguate it.");
                continue;
            }
            if (edit.NewContent == null)
            {
                perEditErrors.Add($"batchEdits[{i}] ({edit.FilePath}): newContent is required (pass an empty string for a pure deletion).");
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

        var finalContents = new Dictionary<FilePathWrapper, string>();
        var newContentsByFile = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var fileGroup in editsByFile)
        {
            var filePathResolved = fileGroup.Key;
            if (!filePathResolved.Validated)
            {
                perEditErrors.Add($"'{fileGroup.Key}': {(filePathResolved.FailureReason == FilePathFailureReason.NoSolutionLoaded ? SolutionNotLoadedMessage.Build(_workspaceManager.LoadState) : "path could not be resolved.")}");
                continue;
            }

            // One lookup, owned by the read chokepoint: case-insensitive exact path, then a unique
            // bare name; a miss names the closest real paths (or the ambiguous candidates).
            var lookup = await _workspaceManager.GetDocumentAsync(filePathResolved, ReadSource.Committed, cancellationToken);
            if (!lookup.TryGetDocument(out var document))
            {
                perEditErrors.Add(lookup.Describe());
                failureCodes.Add(lookup.Status == DocumentLookupStatus.Ambiguous ? ToolErrorCode.Ambiguous : ToolErrorCode.NotFound);
                continue;
            }

            // Key the result by the document's own path, not the caller's spelling. Two groups can
            // reach the same file under different spellings (e.g. a bare name and a full path); each
            // would otherwise be spliced against the original text and the later one would silently
            // overwrite the earlier one's batchEdits.
            var canonicalPath = new FilePathWrapper(document.FilePath ?? filePathResolved.Absolute, _workspaceManager.GetSolutionRoot(), validated: true);
            if (finalContents.ContainsKey(canonicalPath))
            {
                perEditErrors.Add($"'{fileGroup.Key}' resolves to '{canonicalPath}', which another edit entry in this batch already targets under a different spelling. Use the same path string for every edit to one file so they are anchored together.");
                continue;
            }

            var originalText = await document.GetTextAsync(cancellationToken);
            var originalString = originalText.ToString();
            var memberNameAt = MemberNameResolverFor(await document.GetSyntaxRootAsync(cancellationToken));

            // Resolve every edit's match against the file's ORIGINAL text -> never against a prior
            // edit's output within this same batch. This is what removes the sequencing hazard a
            // series of single ReplaceSnippetTool calls has: every edit here is anchored against one
            // fixed snapshot, not a chain of intermediate results the model never sees.
            var resolved = new List<(int index, SnippetMatch match, string newContent)>();
            foreach (var (edit, index) in fileGroup)
            {
                try
                {
                    var match = ResolveEditMatch(
                        originalText, edit.OldContent, edit.LineBefore, edit.LineAfter, memberNameAt: memberNameAt,
                        withinMember: edit.WithinMember, matchLine: edit.MatchLine);
                    resolved.Add((index, match, edit.NewContent));
                }
                catch (ToolException toolEx)
                {
                    var rejectedOriginNote = toolEx is ToolNotFoundException && _rejectedEdits.Explains((string)canonicalPath, edit.OldContent, originalString)
                        ? " " + RejectedOriginNote
                        : "";
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

            // Apply highest-offset-first so an earlier splice's growth/shrink never invalidates a
            // later match's offset -> every position was already computed against originalString above,
            // so no running correction term is needed the way DiffEngine's forward hunk application uses.
            var spliced = originalString;
            foreach (var (_, match, newContent) in byStart.OrderByDescending(r => r.match.Start))
            {
                spliced = spliced.Remove(match.Start, match.Length).Insert(match.Start, newContent);
            }

            var targetEol = _workspaceManager.ResolveEol((string)canonicalPath, originalString);
            var normalizedSpliced = EolUtilities.NormalizeEol(spliced, targetEol);
            finalContents[canonicalPath] = normalizedSpliced;
            newContentsByFile[(string)canonicalPath] = resolved.Select(r => r.newContent).ToList();
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
                ErrorData = new ResultError(errorCode, "ReplaceSnippet batch rejected - nothing was changed; every file is exactly as you last read it:\n" + string.Join("\n", perEditErrors))
            };
        }

        if (action == ProposedChangeAction.validate)
        {
            var validationResult = await _validationEngine.ValidateChangesAsync(finalContents, cancellationToken: cancellationToken);
            return !validationResult.IsError
                ? new SentinelCallToolResult<ReplaceSnippetResult>() { IsError = false, SuccessData = new ReplaceSnippetResult(null, validationResult, null) }
                : new SentinelCallToolResult<ReplaceSnippetResult>()
                {
                    IsError = true,
                    ErrorData = new ResultError(ToolErrorCode.Exception, $"ReplaceSnippet batch validate failed.", StructuredDetail: validationResult.Diagnostics)
                };
        }

        try
        {
            var result = await _workspaceManager.ApplyProposedChangesAsync(finalContents, validateChanges: validateOnApply, staleReadPolicy: StaleReadPolicy.RefuseIfObservedStale);
            // TODO a non-mapped, non-validation refusal (IsError without ValidationResult) falls through to IsError = false below; map it once decided (plan_stale_read_guard_followups open decision 9)
            if (WriteRefusal.TryMap("ReplaceSnippet", result, out var refusal))
                return new SentinelCallToolResult<ReplaceSnippetResult>() { IsError = true, ErrorData = refusal };
            string? untouchedNote = null;
            if (result.IsError && result.ValidationResult != null)
            {
                foreach (var (rejectedPath, rejectedNewContents) in newContentsByFile)
                    foreach (var rejectedNewContent in rejectedNewContents)
                        _rejectedEdits.Remember(rejectedPath, rejectedNewContent);
                // The edit-impact report, when it can attribute the errors to unedited files, replaces the generic
                // untouched-file sentence (it already covers it); null keeps that sentence.
                untouchedNote = await EditImpactGate.DescribeAsync(_workspaceManager, finalContents, result.ValidationResult, _activeToolSurface, cancellationToken: cancellationToken)
                    ?? ValidateAndApplyHelper.DescribeUntouchedFileErrors(finalContents.Keys.Select(k => (string)k), result.ValidationResult.Diagnostics.Select(d => d.FilePath.ToString()), "one ReplaceSnippet call with batchEdits, one item per file, each with its own filePath");
            }
            if (result.IsError && result.ValidationResult != null)
                return new SentinelCallToolResult<ReplaceSnippetResult>()
                {
                    IsError = true,
                    ErrorData = new ResultError(ToolErrorCode.Exception,
                        "ReplaceSnippet batch: every edit matched, but the resulting code introduces new compiler errors, so nothing was changed; every file is exactly as you last read it. " +
                        (untouchedNote is null ? "" : untouchedNote + " ") +
                        "Fix the issue(s) below and retry:\n" +
                        "[COMPILER ERROR]\n" +
                        await CompilerErrorLookupHelper.DescribeAsync(result.ValidationResult, _symbolNavigationEngine, cancellationToken, finalContents.Keys.Select(k => (string)k).ToList()),
                        StructuredDetail: result.ValidationResult.Diagnostics),
                };
            if (!result.IsError)
            {
                foreach (var writtenPath in newContentsByFile.Keys)
                    _rejectedEdits.Forget(writtenPath);
            }
            await OperationBlobHelper.WriteBlobForApplyAsync(_logger, _workspaceManager, "replace_snippet_batch", result, cancellationToken: cancellationToken);
            var strippedResult = result with { PreImages = null };
            string? diffContent = returnDiff
                ? ValidateAndApplyHelper.BuildDiffFromPreImages(finalContents, result.PreImages)
                : null;
            return new SentinelCallToolResult<ReplaceSnippetResult>()
            {
                IsError = false,
                SuccessData = new ReplaceSnippetResult(strippedResult, null, diffContent),
                Findings = result.Findings ?? []
            };
        }
        catch (Exception ex)
        {
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

            if (File.Exists(filePathResolved.Absolute))
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
            var changes = new Dictionary<FilePathWrapper, string> { [filePathResolved] = body };
            var result = await _workspaceManager.ApplyProposedChangesAsync(changes, validateChanges: true, cancellationToken: cancellationToken);
            if (result.IsError && result.ValidationResult != null)
            {
                return new SentinelCallToolResult<object>()
                {
                    IsError = true,
                    ErrorData = new ResultError(ToolErrorCode.Exception,
                        "CreateFile: this content would introduce new compiler errors - not written to disk. Fix the issue(s) below and retry:\n" +
                        "[COMPILER ERROR]\n" +
                        await EditImpactGate.PrependAsync(_workspaceManager, changes, result.ValidationResult, _activeToolSurface, await CompilerErrorLookupHelper.DescribeAsync(result.ValidationResult, _symbolNavigationEngine, cancellationToken), cancellationToken))
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

            await OperationBlobHelper.WriteBlobForApplyAsync(_logger, _workspaceManager, "create_file", result, cancellationToken: cancellationToken);
            var strippedResult = result with { PreImages = null };
            var createFileFindings = new List<Finding>();
            createFileFindings.AddRange(result.Findings ?? []);
            return new SentinelCallToolResult<object>()
            {
                IsError = false,
                SuccessData = strippedResult,
                Findings = createFileFindings
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
}
