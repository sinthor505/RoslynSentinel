using System.Text.Json;

using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging;

using RoslynSentinel.Engines.Basic;

namespace RoslynSentinel.Tools.Basic;

/// <summary>
/// Plain DI-constructed implementation backing WorkspaceFileEditTools. Method bodies moved
/// verbatim from WorkspaceTools (Decision 7 step 2).
///
/// CORRECTION vs. the plan doc's Decision 1 (flagged per Decision 7 step 1.5's audit mandate):
/// Decision 1 listed this class as 7 tools (ApplyDiff, ApplyUnifiedDiff, WriteFile, DeleteFile,
/// RetryFailedChanges, UndoLastApply, ReadFile) with DiffEngine as a dependency. As of 2026-09-17,
/// WorkspaceTools.cs only actually still has 3 of those 7 live as [McpServerTool] methods
/// (RetryFailedChanges, UndoLastApply, ReadFile) - the other 4 (ApplyDiff/ApplyUnifiedDiff/
/// WriteFile/DeleteFile) are live, separately-registered [McpServerTool] methods on a wholly
/// different, pre-existing class (WholeFileWriteTools.cs) that this plan does not name and
/// is out of scope to touch. DiffEngine is confirmed dead on WorkspaceTools (FindReferences:
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

    public WorkspaceFileEditImpl(IWorkspaceManager workspaceManager, WorkspaceReadNavigationImpl readNav, ILogger logger, ValidationEngine validationEngine, SymbolNavigationEngine symbolNavigationEngine, WriteToolAdviceHelper writeAdvice)
    {
        _workspaceManager = workspaceManager;
        _readNav = readNav;
        _logger = logger;
        _validationEngine = validationEngine;
        _symbolNavigationEngine = symbolNavigationEngine;
        _writeAdvice = writeAdvice;
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
                        $"control); GetOperationDetail(changeId: \"{changeId}\") shows exactly which files were touched.")
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

            var reverted = new List<string>();
            var noOpFiles = new List<string>();
            if (revertChanges.Count > 0)
            {
                // exactRestore: this writes back a pre-image the server captured itself, so it bypasses the
                // EOL-change guard and EOL normalization (a mixed-EOL pre-image over a since-normalized file would
                // otherwise be refused as an EOL change) and writes the captured bytes verbatim.
                var revertResult = await _workspaceManager.ApplyProposedChangesAsync(
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
                SuccessData = $"Reverted {reverted.Count} files{noOpPart}. Files: {string.Join(", ", reverted)}{failedPart}"
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

    private static ResultError BuildFileNotFoundError(Microsoft.CodeAnalysis.Solution solution, string normalizedPath)
    {
        var requestedFileName = Path.GetFileName(normalizedPath);
        // The candidate search moved to Common (DocumentLookup.FindClosestPaths) so engines can use it too.
        var candidates = DocumentLookup.FindClosestPaths(solution, normalizedPath);

        if (candidates.Count > 0)
        {
            return new ResultError("FileNotFound",
                $"'{requestedFileName}' does not exist at '{normalizedPath}'. A file with this name exists at a different path. " +
                "You MUST retry with the correct path:\n" +
                string.Join("\n", candidates.Select(c => $"  - {c}")));
        }

        return new ResultError("FileNotFound",
            $"'{requestedFileName}' does not exist anywhere in the solution (searched {solution.Projects.Count()} project(s), no filename match). " +
            "You MUST call ListSolutionItems(kind: all) next to see every file actually in the solution before trying another path.");
    }

    private const int RangedReadInlineBudgetChars = LargeResultHelper.OffloadThresholdBytes - 2048;

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
                + (_workspaceManager.LoadState is { IsFreshStartup: true } ? "This server was freshly (re)started and has not loaded a solution yet; call LoadSolution to restore full functionality. " : string.Empty);

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

                int requestedTo = to;
                to = TrimRangeToInlineBudget(sourceText, from, to);
                var slice = sourceText.ToString(TextSpan.FromBounds(sourceText.Lines[from - 1].Start, sourceText.Lines[to - 1].EndIncludingLineBreak));
                return new SentinelCallToolResult<object>()
                {
                    IsError = false,
                    SuccessData = new { filePath = absolutePath, startLine = from, endLine = to, totalLines, source = slice },
                    HasMoreData = to < totalLines,
                    StatusMessage = explanation + (to < requestedTo ? $"Range shortened to lines {from}-{to} of {totalLines} to stay under the {LargeResultHelper.OffloadThresholdBytes}-byte inline limit. Call ReadFile again with startLine: {to + 1} for the rest." : string.Empty),
                    WorkspaceVersion = _workspaceManager.WorkspaceVersion,
                };
            }

            var textBytes = System.Text.Encoding.UTF8.GetByteCount(diskContent);
            if (textBytes > LargeResultHelper.OffloadThresholdBytes)
            {
                return new SentinelCallToolResult<object>()
                {
                    IsError = true,
                    ErrorData = new ResultError(ToolErrorCode.InvalidArgument, $"File is {totalLines} lines; it is over the inline limit and results cannot be offloaded while no solution is loaded. Pass startLine/endLine (ranges are shortened to fit) or call LoadSolution first.")
                };
            }

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

    private static int TrimRangeToInlineBudget(SourceText sourceText, int from, int to)
    {
        bool Fits(int candidateTo)
        {
            var span = TextSpan.FromBounds(sourceText.Lines[from - 1].Start, sourceText.Lines[candidateTo - 1].EndIncludingLineBreak);
            return System.Text.Json.JsonSerializer.Serialize(sourceText.ToString(span), RoslynSentinel.Common.SharedJsonOptions.Default).Length <= RangedReadInlineBudgetChars;
        }

        if (Fits(to))
        {
            return to;
        }

        // Binary search: lo always acceptable (a single over-long line is returned as-is), hi never fits.
        int lo = from;
        int hi = to;
        while (hi - lo > 1)
        {
            int mid = lo + ((hi - lo) / 2);
            if (Fits(mid))
            {
                lo = mid;
            }
            else
            {
                hi = mid;
            }
        }

        return lo;
    }

    public async Task<SentinelCallToolResult<object>> ReadFile(ToolCallReason reason, string filePath, int? startLine = null,
        int? endLine = null, CancellationToken cancellationToken = default)
    {
        FilePathWrapper filePathResolved = _workspaceManager.ResolveFromWire(filePath);
        if (filePathResolved.FailureReason == FilePathFailureReason.NoSolutionLoaded
            && !string.IsNullOrWhiteSpace(filePath)
            && Path.IsPathRooted(filePath))
        {
            return await ReadFileWithoutSolutionAsync(filePath, startLine, endLine, cancellationToken);
        }
        try
        {
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
                        ErrorData = BuildFileNotFoundError(solution, normalizedPath)
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

                int requestedTo = to;
                to = TrimRangeToInlineBudget(sourceText, from, to);
                bool shortened = to < requestedTo;

                var start = sourceText.Lines[from - 1].Start;
                var end = sourceText.Lines[to - 1].EndIncludingLineBreak;
                var slice = sourceText.ToString(TextSpan.FromBounds(start, end));
                return new SentinelCallToolResult<object>()
                {
                    IsError = false,
                    SuccessData = new
                    {
                        filePath = (string)filePathResolved,
                        startLine = from,
                        endLine = to,
                        totalLines,
                        source = slice
                    },
                    HasMoreData = to < totalLines,
                    StatusMessage = shortened
                        ? $"Range shortened to lines {from}-{to} of {totalLines} to stay under the {LargeResultHelper.OffloadThresholdBytes}-byte inline limit. Call ReadFile again with startLine: {to + 1} for the rest."
                        : null,
                    WorkspaceVersion = _workspaceManager.WorkspaceVersion,
                };
            }

            var fullText = sourceText.ToString();
            var textBytes = System.Text.Encoding.UTF8.GetByteCount(fullText);
            const int thresholdBytes = LargeResultHelper.OffloadThresholdBytes;
            var solutionRoot = _workspaceManager.GetSolutionRoot();
            if (textBytes > thresholdBytes && !string.IsNullOrEmpty(solutionRoot))
            {
                var fullResult = new FileSourceResult { FilePath = (string)filePathResolved, StartLine = 1, EndLine = totalLines, TotalLines = totalLines, Source = fullText };
                var stored = await LargeResultHelper.StoreLargeResultAsync(fullResult, solutionRoot, ResultWrapperType.FileSource, cancellationToken);

                object? outlineData = null;
                try
                {
                    var fileOutline = await _readNav.GetFileOutline(reason: reason, filePathResolved, cancellationToken);
                    outlineData = fileOutline.SuccessData;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "GetFileOutline failed for '{FilePathWrapper}'", filePathResolved);
                }

                return new SentinelCallToolResult<object>
                {
                    IsError = false,
                    // Paging here goes through LargeResult/GetLargeResult, not HasMoreData -> leaving it
                    // false avoids signalling a second, redundant continuation mechanism.
                    HasMoreData = false,
                    LargeResult = new LargeResultInfo(resultType: "FileSource", writtenToFile: stored.offloaded, filePath: stored.filePath, resultId: stored.resultId!, sizeBytes: textBytes, totalRecords: 1, message: $"Result is {totalLines} lines, {textBytes} bytes (threshold: {thresholdBytes}). " + $"Use GetLargeResult(resultId: \"{stored.resultId}\") to page through results, or retry ReadFile with startLine/endLine for just the slice you need, or use GetFileOutline to get the constructors, methods, helpers, members, enums, fields, properties, etc of a file without reading the entire file."),
                    SuccessData = new
                    {
                        totalLines,
                        fileOutline = outlineData
                    },
                    WorkspaceVersion = _workspaceManager.WorkspaceVersion,
                };
            }

            return new SentinelCallToolResult<object>()
            {
                IsError = false,
                SuccessData = new
                {
                    filePath = (string)filePathResolved,
                    startLine = 1,
                    endLine = totalLines,
                    totalLines,
                    source = fullText
                },
                HasMoreData = false,
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

    public async Task<SentinelCallToolResult<ReplaceSnippetResult>> ReplaceSnippet(
        ToolCallReason reason,
        ProposedChangeAction action,
        string? filePath = null,
        string? oldContent = null,
        string? newContent = null,
        string? lineBefore = null,
        string? lineAfter = null,
        List<SnippetEdit>? batchEdits = null,
        bool validateOnApply = true,
        bool returnDiff = false,
        CancellationToken cancellationToken = default)
    {
        try
        {
            bool hasSingularEdit = filePath is not null || !string.IsNullOrEmpty(oldContent) || newContent != null;
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
                // the old text here named WriteFile unconditionally, and in run 398 WriteFile was
                // gated off, so the one instruction the model was given was unfollowable.
                var advice = _writeAdvice.AdviseForOversizedEdit("ReplaceSnippet");
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
                    // FindExactSnippetPosition (not FindSnippetPosition) is required here: it never
                    // uses ContextHelper's whitespace-collapsing fallback, so match.Length is always
                    // the real removable span. Using oldContent.Length instead of match.Length used
                    // to corrupt adjacent lines when a fallback match fired (see
                    // project_replacesnippet_silent_splice_corruption_adjacent_lines memory).
                    var match = ContextHelper.FindExactSnippetPosition(oldText, oldContent, lineBefore, lineAfter);
                    var newFileContent = oldText.ToString().Remove(match.Start, match.Length).Insert(match.Start, newContent);
                    var dominantEol = EolUtilities.DetectDominantEol(oldText);
                    var normalizedContent = EolUtilities.NormalizeEol(newFileContent, dominantEol);
                    var targetPath = document.FilePath ?? filePathResolved;
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

                    var result = await _workspaceManager.ApplyProposedChangesAsync(snippetChanges, validateChanges: validateOnApply);
                    if (result.IsError && result.ValidationResult != null)
                        return new SentinelCallToolResult<ReplaceSnippetResult>()
                        {
                            IsError = true,
                            ErrorData = new ResultError(ToolErrorCode.Exception,
                                "ReplaceSnippet: the edit matched the target file, but the resulting code introduces new compiler errors - change not applied. Fix the issue(s) below and retry:\n" +
                                "[COMPILER ERROR]\n" +
                                await CompilerErrorLookupHelper.DescribeAsync(result.ValidationResult, _symbolNavigationEngine, cancellationToken))
                        };
                    await OperationBlobHelper.WriteBlobForApplyAsync(_logger, _workspaceManager, "replace_snippet", result, cancellationToken: cancellationToken);
                    var strippedResult = result with { PreImages = null };
                    string? diffContent = returnDiff
                        ? ValidateAndApplyHelper.BuildDiffFromPreImages(snippetChanges, result.PreImages)
                        : null;
                    return new SentinelCallToolResult<ReplaceSnippetResult>()
                    {
                        IsError = false,
                        SuccessData = new ReplaceSnippetResult(strippedResult, null, diffContent)
                    };
                }
                catch (Exception ex)
                {
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
                perEditErrors.Add($"batchEdits[{i}] ({edit.FilePath}): oldContent is required.");
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
            }
        }

        if (perEditErrors.Count > 0)
        {
            // Same escape-hatch advice as the single-edit path, appended once (not per edit) and only
            // when a size bound tripped - it is noise for e.g. a missing newContent.
            var sizeAdvice = anySizeBoundExceeded
                ? "\n" + _writeAdvice.AdviseForOversizedEdit("ReplaceSnippet").Sentence
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

            // Resolve every edit's match against the file's ORIGINAL text -> never against a prior
            // edit's output within this same batch. This is what removes the sequencing hazard a
            // series of single ReplaceSnippet calls has: every edit here is anchored against one
            // fixed snapshot, not a chain of intermediate results the model never sees.
            var resolved = new List<(int index, ContextHelper.SnippetMatch match, string newContent)>();
            foreach (var (edit, index) in fileGroup)
            {
                try
                {
                    var match = ContextHelper.FindExactSnippetPosition(originalText, edit.OldContent, edit.LineBefore, edit.LineAfter);
                    resolved.Add((index, match, edit.NewContent));
                }
                catch (ToolException toolEx)
                {
                    perEditErrors.Add($"batchEdits[{index}] ({filePathResolved}): {toolEx.Message}"); failureCodes.Add(toolEx.ErrorCode);
                }
            }

            if (perEditErrors.Count > 0)
            {
                continue;
            }

            // Reject overlapping matches before splicing anything -> silent last-writer-wins here is
            // the same corruption class as the closed ReplaceSnippet silent-splice-corruption finding
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

            var dominantEol = EolUtilities.DetectDominantEol(originalText);
            var normalizedSpliced = EolUtilities.NormalizeEol(spliced, dominantEol);
            finalContents[canonicalPath] = normalizedSpliced;
        }

        if (perEditErrors.Count > 0)
        {
            // When every rejection carries its own code (a failed path lookup, or a snippet match that was
            // ambiguous/missing), report that code so a caller can branch on it. Mixed NotFound/Ambiguous reports Ambiguous; any other mix or unattributed rejection stays InvalidArgument.
            var errorCode = failureCodes.Count > 0 && failureCodes.Count == perEditErrors.Count
                ? (failureCodes.Distinct().Count() == 1 ? failureCodes[0] : failureCodes.All(c => c == ToolErrorCode.Ambiguous || c == ToolErrorCode.NotFound) ? ToolErrorCode.Ambiguous : ToolErrorCode.InvalidArgument)
                : ToolErrorCode.InvalidArgument;
            return new SentinelCallToolResult<ReplaceSnippetResult>()
            {
                IsError = true,
                ErrorData = new ResultError(errorCode, "ReplaceSnippet batch rejected - no changes were written:\n" + string.Join("\n", perEditErrors))
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
            var result = await _workspaceManager.ApplyProposedChangesAsync(finalContents, validateChanges: validateOnApply);
            if (result.IsError && result.ValidationResult != null)
                return new SentinelCallToolResult<ReplaceSnippetResult>()
                {
                    IsError = true,
                    ErrorData = new ResultError(ToolErrorCode.Exception,
                        "ReplaceSnippet batch: every edit matched, but the resulting code introduces new compiler errors - no changes were written. Fix the issue(s) below and retry:\n" +
                        "[COMPILER ERROR]\n" +
                        await CompilerErrorLookupHelper.DescribeAsync(result.ValidationResult, _symbolNavigationEngine, cancellationToken),
                        StructuredDetail: result.ValidationResult.Diagnostics),
                };
            await OperationBlobHelper.WriteBlobForApplyAsync(_logger, _workspaceManager, "replace_snippet_batch", result, cancellationToken: cancellationToken);
            var strippedResult = result with { PreImages = null };
            string? diffContent = returnDiff
                ? ValidateAndApplyHelper.BuildDiffFromPreImages(finalContents, result.PreImages)
                : null;
            return new SentinelCallToolResult<ReplaceSnippetResult>()
            {
                IsError = false,
                SuccessData = new ReplaceSnippetResult(strippedResult, null, diffContent)
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
                    ErrorData = new ResultError(ToolErrorCode.InvalidArgument, $"CreateFile: '{filePathResolved}' already exists. CreateFile never overwrites - use Member/ReplaceSnippet to edit an existing file.")
                };
            }

            bool isCSharpFile = filePathResolved.Absolute.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);
            if (isCSharpFile && string.IsNullOrWhiteSpace(namespaceName))
            {
                return new SentinelCallToolResult<object>()
                {
                    IsError = true,
                    ErrorData = new ResultError(ToolErrorCode.InvalidArgument, "CreateFile: 'namespaceName' is required for a .cs file, so the new file starts as a valid compilation unit that Member(add) can populate.")
                };
            }

            if (isCSharpFile && (typeKind == null || string.IsNullOrWhiteSpace(typeName)))
            {
                return new SentinelCallToolResult<object>()
                {
                    IsError = true,
                    ErrorData = new ResultError(ToolErrorCode.InvalidArgument, "CreateFile: 'typeKind' and 'typeName' are both required for a .cs file, so the new file starts with an empty top-level type that Member(add) can populate members into.")
                };
            }

            string content;
            if (isCSharpFile)
            {
                string keyword = typeKind!.Value == NewTypeKind.staticClass ? "static class" : typeKind.Value.ToString().TrimStart('@');
                content = $"namespace {namespaceName};\n\npublic {keyword} {typeName}\n{{\n}}\n";
            }
            else
            {
                content = "";
            }

            var directory = Path.GetDirectoryName((string)filePathResolved.Absolute);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var changes = new Dictionary<FilePathWrapper, string> { [filePathResolved] = content };
            var result = await _workspaceManager.ApplyProposedChangesAsync(changes, validateChanges: true, cancellationToken: cancellationToken);
            if (result.IsError && result.ValidationResult != null)
            {
                string writeFileHint = _writeAdvice.IsExposed("WriteFile")
                    ? "\nAlternatively, WriteFile(operation=CreateFile) can create this file with its full, already-correct body in one call, avoiding the empty-scaffold-then-populate sequence entirely."
                    : "";
                return new SentinelCallToolResult<object>()
                {
                    IsError = true,
                    ErrorData = new ResultError(ToolErrorCode.Exception,
                        "CreateFile: this content would introduce new compiler errors - not written to disk. Fix the issue(s) below and retry:\n" +
                        "[COMPILER ERROR]\n" +
                        await CompilerErrorLookupHelper.DescribeAsync(result.ValidationResult, _symbolNavigationEngine, cancellationToken) +
                        writeFileHint)
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
            return new SentinelCallToolResult<object>()
            {
                IsError = false,
                SuccessData = strippedResult,
                Findings = _writeAdvice.IsExposed("WriteFile")
                    ? [new Finding("CreateFile",
                        "WriteFile is also available on this server and can create a file with its full body " +
                        "in one call, instead of populating it afterward via Member(add).")]
                    : []
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
