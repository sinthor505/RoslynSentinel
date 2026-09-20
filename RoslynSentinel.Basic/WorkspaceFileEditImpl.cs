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
    private readonly IWorkspaceManager _workspaceManager;
    private readonly ILogger _logger;
    private readonly WorkspaceReadNavigationImpl _readNav;

    public WorkspaceFileEditImpl(IWorkspaceManager workspaceManager, WorkspaceReadNavigationImpl readNav, ILogger logger)
    {
        _workspaceManager = workspaceManager;
        _readNav = readNav;
        _logger = logger;
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
}
