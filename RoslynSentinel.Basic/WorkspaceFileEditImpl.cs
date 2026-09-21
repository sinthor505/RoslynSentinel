using System.Text.Json;

using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging;

namespace RoslynSentinel.Basic;

/// <summary>
/// Plain DI-constructed implementation backing WorkspaceFileEditTools. Method bodies moved
/// verbatim from SentinelWorkspaceTools (Decision 7 step 2).
///
/// CORRECTION vs. the plan doc's Decision 1 (flagged per Decision 7 step 1.5's audit mandate):
/// Decision 1 listed this class as 7 tools (ApplyDiff, ApplyUnifiedDiff, WriteFile, DeleteFile,
/// RetryFailedChanges, UndoLastApply, ReadFile) with DiffEngine as a dependency. As of 2026-09-17,
/// SentinelWorkspaceTools.cs only actually still has 3 of those 7 live as [McpServerTool] methods
/// (RetryFailedChanges, UndoLastApply, ReadFile) - the other 4 (ApplyDiff/ApplyUnifiedDiff/
/// WriteFile/DeleteFile) are live, separately-registered [McpServerTool] methods on a wholly
/// different, pre-existing class (SentinelWholeFileWriteTools.cs) that this plan does not name and
/// is out of scope to touch. DiffEngine is confirmed dead on SentinelWorkspaceTools (FindReferences:
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
                IsSuccess = true,
                SuccessDetails = await _workspaceManager.RetryFailedChangesAsync(specificFiles, retryCount, cancellationToken)
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "RetryFailedChanges failed");
            return new SentinelCallToolResult<object>()
            {
                IsSuccess = false,
                ErrorDetails = ToolErrorMapper.ToResultError(ex, _workspaceManager, "RetryFailedChanges")
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
                    IsSuccess = false,
                    ErrorDetails = new ResultError("NoOperationBlobFound",
                        $"No operation blob found for changeId '{changeId}' under .roslynsentinel/operations/. " +
                        "This does not mean the change failed - it may well be on disk. It means no undo record " +
                        "exists for that id here. Check the id against the value the applying tool returned, and " +
                        "that this is the same server session and solution; a changeId from an earlier session or " +
                        "a different solution root will not resolve. If the applying tool reported the change as " +
                        "'not reversible', there is no undo record to find and the change must be reverted manually.")
                };
            }

            var json = await File.ReadAllTextAsync(blobPath);
            var doc = JsonSerializer.Deserialize<JsonElement>(json);
            var revertable = doc.GetProperty("items").EnumerateArray().Select(e => JsonSerializer.Deserialize<OperationItemRecord>(e.GetRawText())!).Where(r => r.Outcome == ItemRecordOutcome.Succeeded && r.BeforeSource != null).ToList();
            if (revertable.Count == 0)
            {
                return new SentinelCallToolResult<object>()
                {
                    IsSuccess = false,
                    ErrorDetails = new ResultError("NoReversibleItems",
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
                var revertResult = await _workspaceManager.ApplyProposedChangesAsync(
                    revertChanges, rollbackOnPartialFailure: true, cancellationToken: cancellationToken);
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
                IsSuccess = true,
                SuccessDetails = $"Reverted {reverted.Count} files{noOpPart}. Files: {string.Join(", ", reverted)}{failedPart}"
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "UndoLastApply failed for '{ChangeId}'", changeId);
            return new SentinelCallToolResult<object>()
            {
                IsSuccess = false,
                ErrorDetails = ToolErrorMapper.ToResultError(ex, _workspaceManager, "UndoLastApply")
            };
        }
    }

    private static ResultError BuildFileNotFoundError(Microsoft.CodeAnalysis.Solution solution, string normalizedPath)
    {
        var requestedFileName = Path.GetFileName(normalizedPath);
        var candidates = solution.Projects
            .SelectMany(p => p.Documents)
            .Where(d => !string.IsNullOrEmpty(d.FilePath) && string.Equals(Path.GetFileName(d.FilePath), requestedFileName, StringComparison.OrdinalIgnoreCase))
            .Select(d => d.FilePath!)
            .Distinct()
            .Take(5)
            .ToList();

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

    public async Task<SentinelCallToolResult<object>> ReadFile(ToolCallReason reason, FilePathWrapper filepath, int? startLine = null,
        int? endLine = null, CancellationToken cancellationToken = default)
    {
        FilePathWrapper filePathResolved = FilePathWrapper.FromWire(filepath, _workspaceManager.GetSolutionRoot());
        try
        {
            var solution = await _workspaceManager.GetCurrentSolutionAsync(cancellationToken);
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
                        IsSuccess = false,
                        ErrorDetails = BuildFileNotFoundError(solution, normalizedPath)
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
                        IsSuccess = false,
                        ErrorDetails = new ResultError(ToolErrorCode.InvalidArgument, $"ReadFile: requested range {from}-{to} is out of bounds for a {totalLines}-line file.")
                    };
                }

                var start = sourceText.Lines[from - 1].Start;
                var end = sourceText.Lines[to - 1].EndIncludingLineBreak;
                var slice = sourceText.ToString(TextSpan.FromBounds(start, end));
                return new SentinelCallToolResult<object>()
                {
                    IsSuccess = true,
                    SuccessDetails = new
                    {
                        filePath = (string)filePathResolved,
                        startLine = from,
                        endLine = to,
                        totalLines,
                        source = slice
                    },
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
                    outlineData = fileOutline.SuccessDetails;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "GetFileOutline failed for '{FilePathWrapper}'", filePathResolved);
                }

                return new SentinelCallToolResult<object>
                {
                    IsSuccess = true,
                    LargeResult = new LargeResultInfo(resultType: "FileSource", writtenToFile: stored.offloaded, filePath: stored.filePath, resultId: stored.resultId!, sizeBytes: textBytes, totalRecords: 1, message: $"Result is {totalLines} lines, {textBytes} bytes (threshold: {thresholdBytes}). " + $"Use GetLargeResult(resultId: \"{stored.resultId}\") to page through results, or retry ReadFile with startLine/endLine for just the slice you need, or use GetFileOutline to get the constructors, methods, helpers, members, enums, fields, properties, etc of a file without reading the entire file."),
                    SuccessDetails = new
                    {
                        totalLines,
                        fileOutline = outlineData
                    },
                    WorkspaceVersion = _workspaceManager.WorkspaceVersion,
                };
            }

            return new SentinelCallToolResult<object>()
            {
                IsSuccess = true,
                SuccessDetails = new
                {
                    filePath = (string)filePathResolved,
                    startLine = 1,
                    endLine = totalLines,
                    totalLines,
                    source = fullText
                },
                WorkspaceVersion = _workspaceManager.WorkspaceVersion,
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ReadFile failed for '{FilePathWrapper}'", filePathResolved);
            return new SentinelCallToolResult<object>()
            {
                IsSuccess = false,
                ErrorDetails = ToolErrorMapper.ToResultError(ex, _workspaceManager, "ReadFile")
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
        FilePathWrapper? filepath = null,
        string? oldContent = null,
        string? newContent = null,
        string? lineBefore = null,
        string? lineAfter = null,
        List<SnippetEdit>? edits = null,
        bool validateOnApply = true,
        bool returnDiff = false,
        CancellationToken cancellationToken = default)
    {
        try
        {
            bool hasSingularEdit = filepath.HasValue || !string.IsNullOrEmpty(oldContent) || newContent != null;
            bool hasBatchEdit = edits != null;

            if (hasSingularEdit && hasBatchEdit)
            {
                return new SentinelCallToolResult<ReplaceSnippetResult>()
                {
                    IsSuccess = false,
                    ErrorDetails = new ResultError(ToolErrorCode.InvalidArgument,
                        "ReplaceSnippet: supply either filepath/oldContent/newContent or 'edits', not both.")
                };
            }

            if (hasBatchEdit)
            {
                if (edits!.Count == 0)
                {
                    return new SentinelCallToolResult<ReplaceSnippetResult>()
                    {
                        IsSuccess = false,
                        ErrorDetails = new ResultError(ToolErrorCode.InvalidArgument, "ReplaceSnippet: 'edits' was supplied but is empty.")
                    };
                }

                return await ReplaceSnippetBatch(edits, action, validateOnApply, returnDiff, cancellationToken);
            }

            if (!filepath.HasValue)
            {
                return new SentinelCallToolResult<ReplaceSnippetResult>()
                {
                    IsSuccess = false,
                    ErrorDetails = new ResultError(ToolErrorCode.InvalidArgument,
                        "ReplaceSnippet: 'filepath' is required (it names the single file oldContent/newContent applies to), unless 'edits' is supplied instead.")
                };
            }

            FilePathWrapper filePathResolved = _workspaceManager.SetFilePath(filepath.Value);
            if (!filePathResolved.Validated)
            {
                return new SentinelCallToolResult<ReplaceSnippetResult>()
                {
                    IsSuccess = false,
                    ErrorDetails = filePathResolved.FailureReason == FilePathFailureReason.NoSolutionLoaded
                        ? new ResultError(ToolErrorCode.SolutionNotLoaded, "ReplaceSnippet: no solution is loaded, so 'filepath' could not be resolved. Call LoadSolution first, then retry with the same filepath.")
                        : new ResultError(ToolErrorCode.InvalidArgument, "ReplaceSnippet: 'filepath' could not be resolved.")
                };
            }

            if (string.IsNullOrEmpty(oldContent))
            {
                return new SentinelCallToolResult<ReplaceSnippetResult>()
                {
                    IsSuccess = false,
                    ErrorDetails = new ResultError(ToolErrorCode.InvalidArgument, "ReplaceSnippet: 'oldContent' is required.")
                };
            }

            if (newContent == null)
            {
                return new SentinelCallToolResult<ReplaceSnippetResult>()
                {
                    IsSuccess = false,
                    ErrorDetails = new ResultError(ToolErrorCode.InvalidArgument, "ReplaceSnippet: 'newContent' is required (pass an empty string for a pure deletion).")
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
                    IsSuccess = false,
                    ErrorDetails = new ResultError(ToolErrorCode.InvalidArgument,
                        $"ReplaceSnippet: {string.Join("; ", exceeded)}. " + advice.Sentence)
                };
            }

            if (action == ProposedChangeAction.apply || action == ProposedChangeAction.validate)
            {
                try
                {
                    var solution = await _workspaceManager.GetCurrentSolutionAsync(cancellationToken);
                    var document = solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => d.Name == filePathResolved.Absolute || d.FilePath == filePathResolved.Absolute);
                    if (document == null)
                    {
                        return new SentinelCallToolResult<ReplaceSnippetResult>()
                        {
                            IsSuccess = false,
                            ErrorDetails = new ResultError(ToolErrorCode.InvalidArgument, "File not found.")
                        };
                    }

                    var oldText = await document.GetTextAsync();
                    // FindExactSnippetPosition (not FindSnippetPosition) is required here: it never
                    // uses ContextHelper's whitespace-collapsing fallback, so match.Length is always
                    // the real removable span. Using oldContent.Length instead of match.Length used
                    // to corrupt adjacent lines when a fallback match fired (see
                    // project_replacesnippet_silent_splice_corruption_adjacent_lines memory).
                    var match = ContextHelper.FindExactSnippetPosition(oldText, oldContent, lineBefore, lineAfter);
                    var newFileContent = oldText.ToString().Remove(match.Start, match.Length).Insert(match.Start, newContent);
                    var targetPath = document.FilePath ?? filePathResolved;
                    var snippetChanges = new Dictionary<FilePathWrapper, string>
                    {
                        [targetPath] = newFileContent
                    };

                    if (action == ProposedChangeAction.validate)
                    {
                        var validationResult = await _validationEngine.ValidateChangesAsync(snippetChanges);
                        return validationResult.Success ? new SentinelCallToolResult<ReplaceSnippetResult>()
                        {
                            IsSuccess = true,
                            SuccessDetails = new ReplaceSnippetResult(null, validationResult, null)
                        }
                        : new SentinelCallToolResult<ReplaceSnippetResult>()
                        {
                            IsSuccess = false,
                            ErrorDetails = new ResultError(ToolErrorCode.Exception, $"ReplaceSnippet validate failed: {validationResult.Diagnostics.ToInfo()}")
                        };
                    }

                    var result = await _workspaceManager.ApplyProposedChangesAsync(snippetChanges, validateChanges: validateOnApply);
                    if (!result.Success && result.ValidationResult != null)
                        return new SentinelCallToolResult<ReplaceSnippetResult>()
                        {
                            IsSuccess = false,
                            ErrorDetails = new ResultError(ToolErrorCode.Exception,
                                "ReplaceSnippet: the edit matched the target file, but the resulting code introduces new compiler errors - change not applied. Fix the issue(s) below and retry:\n" +
                                "[COMPILER ERROR]\n" +
                                await CompilerErrorLookupHelper.DescribeAsync(result.ValidationResult, _symbolNavigationEngine, cancellationToken))
                        };
                    await OperationBlobHelper.WriteBlobForApplyAsync(_logger, _workspaceManager, "replace_snippet", result);
                    var strippedResult = result with { PreImages = null };
                    string? diffContent = returnDiff
                        ? ValidateAndApplyHelper.BuildDiffFromPreImages(snippetChanges, result.PreImages)
                        : null;
                    return new SentinelCallToolResult<ReplaceSnippetResult>()
                    {
                        IsSuccess = true,
                        SuccessDetails = new ReplaceSnippetResult(strippedResult, null, diffContent)
                    };
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "ReplaceSnippet {Action} unexpected exception for '{FilePathWrapper}'", action, filePathResolved);
                    return new SentinelCallToolResult<ReplaceSnippetResult>()
                    {
                        IsSuccess = false,
                        ErrorDetails = ToolErrorMapper.ToResultError(ex, _workspaceManager, $"ReplaceSnippet {action} for '{filePathResolved}'")
                    };
                }
            }

            return new SentinelCallToolResult<ReplaceSnippetResult>()
            {
                IsSuccess = false,
                ErrorDetails = new ResultError(ToolErrorCode.Exception, $"Unhandled action '{action}'.")
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ReplaceSnippet ({Action}) failed", action);
            return new SentinelCallToolResult<ReplaceSnippetResult>()
            {
                IsSuccess = false,
                ErrorDetails = ToolErrorMapper.ToResultError(ex, _workspaceManager, "ReplaceSnippet")
            };
        }
    }

    private async Task<SentinelCallToolResult<ReplaceSnippetResult>> ReplaceSnippetBatch(
        List<SnippetEdit> edits,
        ProposedChangeAction action,
        bool validateOnApply,
        bool returnDiff,
        CancellationToken cancellationToken)
    {
        if (edits.Count > MaxSnippetEditsPerBatch)
        {
            return new SentinelCallToolResult<ReplaceSnippetResult>()
            {
                IsSuccess = false,
                ErrorDetails = new ResultError(ToolErrorCode.InvalidArgument,
                    $"ReplaceSnippet: edits has {edits.Count} entries (limit {MaxSnippetEditsPerBatch}). Split into multiple calls.")
            };
        }

        var perEditErrors = new List<string>();
        for (int i = 0; i < edits.Count; i++)
        {
            var edit = edits[i];
            if (string.IsNullOrEmpty(edit.FilePath))
            {
                perEditErrors.Add($"edits[{i}]: filePath is required.");
                continue;
            }
            if (string.IsNullOrEmpty(edit.OldContent))
            {
                perEditErrors.Add($"edits[{i}] ({edit.FilePath}): oldContent is required.");
                continue;
            }
            if (edit.NewContent == null)
            {
                perEditErrors.Add($"edits[{i}] ({edit.FilePath}): newContent is required (pass an empty string for a pure deletion).");
                continue;
            }
            var exceeded = DescribeExceededSnippetSizeBounds(edit.OldContent, edit.NewContent);
            if (exceeded.Count > 0)
            {
                perEditErrors.Add($"edits[{i}] ({edit.FilePath}): {string.Join("; ", exceeded)}.");
            }
        }

        if (perEditErrors.Count > 0)
        {
            return new SentinelCallToolResult<ReplaceSnippetResult>()
            {
                IsSuccess = false,
                ErrorDetails = new ResultError(ToolErrorCode.InvalidArgument, "ReplaceSnippet batch rejected before anchoring:\n" + string.Join("\n", perEditErrors))
            };
        }

        var solution = await _workspaceManager.GetCurrentSolutionAsync(cancellationToken);
        var editsByFile = edits
            .Select((edit, index) => (edit, index))
            .GroupBy(pair => _workspaceManager.SetFilePath(pair.edit.FilePath));

        var finalContents = new Dictionary<FilePathWrapper, string>();
        foreach (var fileGroup in editsByFile)
        {
            var filePathResolved = fileGroup.Key;
            if (!filePathResolved.Validated)
            {
                perEditErrors.Add($"'{fileGroup.Key}': {(filePathResolved.FailureReason == FilePathFailureReason.NoSolutionLoaded ? "no solution is loaded." : "path could not be resolved.")}");
                continue;
            }

            var document = solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => d.Name == filePathResolved.Absolute || d.FilePath == filePathResolved.Absolute);
            if (document == null)
            {
                perEditErrors.Add($"'{filePathResolved}': file not found.");
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
                    perEditErrors.Add($"edits[{index}] ({filePathResolved}): {toolEx.Message}");
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
                        $"edits[{prev.index}] and edits[{curr.index}] ({filePathResolved}) have overlapping matches " +
                        $"(edits[{prev.index}]: [{prev.match.Start}, {prev.match.Start + prev.match.Length}), " +
                        $"edits[{curr.index}]: [{curr.match.Start}, {curr.match.Start + curr.match.Length})). " +
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

            finalContents[filePathResolved] = spliced;
        }

        if (perEditErrors.Count > 0)
        {
            return new SentinelCallToolResult<ReplaceSnippetResult>()
            {
                IsSuccess = false,
                ErrorDetails = new ResultError(ToolErrorCode.InvalidArgument, "ReplaceSnippet batch rejected - no changes were written:\n" + string.Join("\n", perEditErrors))
            };
        }

        if (action == ProposedChangeAction.validate)
        {
            var validationResult = await _validationEngine.ValidateChangesAsync(finalContents);
            return validationResult.Success
                ? new SentinelCallToolResult<ReplaceSnippetResult>() { IsSuccess = true, SuccessDetails = new ReplaceSnippetResult(null, validationResult, null) }
                : new SentinelCallToolResult<ReplaceSnippetResult>()
                {
                    IsSuccess = false,
                    ErrorDetails = new ResultError(ToolErrorCode.Exception, $"ReplaceSnippet batch validate failed: {validationResult.Diagnostics.ToInfo()}")
                };
        }

        try
        {
            var result = await _workspaceManager.ApplyProposedChangesAsync(finalContents, validateChanges: validateOnApply);
            if (!result.Success && result.ValidationResult != null)
                return new SentinelCallToolResult<ReplaceSnippetResult>()
                {
                    IsSuccess = false,
                    ErrorDetails = new ResultError(ToolErrorCode.Exception,
                        "ReplaceSnippet batch: every edit matched, but the resulting code introduces new compiler errors - no changes were written. Fix the issue(s) below and retry:\n" +
                        "[COMPILER ERROR]\n" +
                        await CompilerErrorLookupHelper.DescribeAsync(result.ValidationResult, _symbolNavigationEngine, cancellationToken))
                };
            await OperationBlobHelper.WriteBlobForApplyAsync(_logger, _workspaceManager, "replace_snippet_batch", result);
            var strippedResult = result with { PreImages = null };
            string? diffContent = returnDiff
                ? ValidateAndApplyHelper.BuildDiffFromPreImages(finalContents, result.PreImages)
                : null;
            return new SentinelCallToolResult<ReplaceSnippetResult>()
            {
                IsSuccess = true,
                SuccessDetails = new ReplaceSnippetResult(strippedResult, null, diffContent)
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ReplaceSnippet batch ({Action}) unexpected exception for {Count} file(s)", action, finalContents.Count);
            return new SentinelCallToolResult<ReplaceSnippetResult>()
            {
                IsSuccess = false,
                ErrorDetails = ToolErrorMapper.ToResultError(ex, _workspaceManager, $"ReplaceSnippet batch {action} for {finalContents.Count} file(s)")
            };
        }
    }

    public async Task<SentinelCallToolResult<object>> CreateFile(
        ToolCallReason reason,
        FilePathWrapper filepath,
        string? namespaceName = null,
        NewTypeKind? typeKind = null,
        string? typeName = null,
        CancellationToken cancellationToken = default)
    {
        FilePathWrapper filePathResolved = _workspaceManager.SetFilePath(filepath);
        try
        {
            if (!filePathResolved.Validated)
            {
                return new SentinelCallToolResult<object>()
                {
                    IsSuccess = false,
                    ErrorDetails = filePathResolved.FailureReason == FilePathFailureReason.NoSolutionLoaded
                        ? new ResultError(ToolErrorCode.SolutionNotLoaded, "CreateFile: no solution is loaded, so 'filepath' could not be resolved. Call LoadSolution first, then retry with the same filepath.")
                        : new ResultError(ToolErrorCode.InvalidArgument, "CreateFile: 'filepath' is required.")
                };
            }

            if (File.Exists(filePathResolved.Absolute))
            {
                return new SentinelCallToolResult<object>()
                {
                    IsSuccess = false,
                    ErrorDetails = new ResultError(ToolErrorCode.InvalidArgument, $"CreateFile: '{filePathResolved}' already exists. CreateFile never overwrites - use Member/ReplaceSnippet to edit an existing file.")
                };
            }

            bool isCSharpFile = filePathResolved.Absolute.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);
            if (isCSharpFile && string.IsNullOrWhiteSpace(namespaceName))
            {
                return new SentinelCallToolResult<object>()
                {
                    IsSuccess = false,
                    ErrorDetails = new ResultError(ToolErrorCode.InvalidArgument, "CreateFile: 'namespaceName' is required for a .cs file, so the new file starts as a valid compilation unit that Member(add) can populate.")
                };
            }

            if (isCSharpFile && (typeKind == null || string.IsNullOrWhiteSpace(typeName)))
            {
                return new SentinelCallToolResult<object>()
                {
                    IsSuccess = false,
                    ErrorDetails = new ResultError(ToolErrorCode.InvalidArgument, "CreateFile: 'typeKind' and 'typeName' are both required for a .cs file, so the new file starts with an empty top-level type that Member(add) can populate members into.")
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
            if (!result.Success && result.ValidationResult != null)
            {
                string writeFileHint = _writeAdvice.IsExposed("WriteFile")
                    ? "\nAlternatively, WriteFile(operation=CreateFile) can create this file with its full, already-correct body in one call, avoiding the empty-scaffold-then-populate sequence entirely."
                    : "";
                return new SentinelCallToolResult<object>()
                {
                    IsSuccess = false,
                    ErrorDetails = new ResultError(ToolErrorCode.Exception,
                        "CreateFile: this content would introduce new compiler errors - not written to disk. Fix the issue(s) below and retry:\n" +
                        "[COMPILER ERROR]\n" +
                        await CompilerErrorLookupHelper.DescribeAsync(result.ValidationResult, _symbolNavigationEngine, cancellationToken) +
                        writeFileHint)
                };
            }

            if (!result.Success)
            {
                return new SentinelCallToolResult<object>()
                {
                    IsSuccess = false,
                    ErrorDetails = new ResultError(ToolErrorCode.Exception, $"CreateFile failed to write '{filePathResolved}': {result.Summary}")
                };
            }

            await OperationBlobHelper.WriteBlobForApplyAsync(_logger, _workspaceManager, "create_file", result);
            var strippedResult = result with { PreImages = null };
            return new SentinelCallToolResult<object>()
            {
                IsSuccess = true,
                SuccessDetails = strippedResult,
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
                IsSuccess = false,
                ErrorDetails = ToolErrorMapper.ToResultError(ex, _workspaceManager, "CreateFile")
            };
        }
    }
}
