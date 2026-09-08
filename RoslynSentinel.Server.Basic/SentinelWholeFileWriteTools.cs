using System.ComponentModel;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.Logging;

using ModelContextProtocol.Server;

namespace RoslynSentinel.Server.Basic;

public class SentinelWholeFileWriteTools
{
    private readonly SymbolNavigationEngine _symbolNavigationEngine;    // Added by AddConstructorParameter
    private readonly IWorkspaceManager _workspaceManager;
    private readonly SentinelWorkspaceTools _workspaceTools;
    private readonly ValidationEngine _validationEngine;
    private readonly DiffEngine _diffEngine;
    private readonly ILogger<SentinelWholeFileWriteTools> _logger;

    public SentinelWholeFileWriteTools(IWorkspaceManager workspaceManager, SentinelWorkspaceTools workspaceTools, ValidationEngine validationEngine, DiffEngine diffEngine, ILogger<SentinelWholeFileWriteTools> logger, SymbolNavigationEngine symbolNavigationEngine)
    {
        _workspaceManager = workspaceManager;
        _workspaceTools = workspaceTools;
        _validationEngine = validationEngine;
        _diffEngine = diffEngine;
        _logger = logger;
        _symbolNavigationEngine = symbolNavigationEngine;
    }

    [McpServerTool(Name = "WriteFile")]
    [Produces(DataTag.ChangeId)]
    [Description("Writes a whole file to disk. operation=CreateFile requires the file NOT to already exist (fails otherwise). operation=ReplaceFile requires the file to already exist (fails otherwise) and overwrites its full content — for a partial edit use ApplyUnifiedDiff instead. Routes through the same write-path chokepoint as every other mutating tool (drift-checked, undo-tracked via UndoLastApply). By default this delta-compiles the edited file plus every project that transitively references it BEFORE writing, and REJECTS the write if that introduces any new compiler error — so renaming or changing the signature of a member here will fail unless every call site is already consistent with the new form. For a change that necessarily spans multiple files (e.g. renaming a method used elsewhere), either use RenameSymbol/ChangeSignature to update all call sites atomically in one operation, or pass validateOnApply=false on the intermediate writes and validate once at the end — do not repeatedly retry the same whole-file write expecting a different file's state to change. Parent directories are created automatically if missing.")]
    public async Task<ToolResult<object>> WriteFile(
        [Description(ToolParams.Reason)] string reason,
        [ExternalInputRequired(DataTag.Action)] WriteFileOperation operation,
        [Consumes(DataTag.SourceFilepath, required: true)] string filepath, [Description("Full content of the file.")] string content, [ToolOption(ToolOptionTag.ValidateOnApply)][Description(ToolParams.ValidateOnApply)] bool validateOnApply = true, CancellationToken cancellationToken = default)
    {
        FilePath filePath = FilePath.FromWire(filepath, _workspaceManager.GetSolutionRoot());
        try
        {
            bool exists = File.Exists(filePath);
            if (operation == WriteFileOperation.CreateFile && exists)
            {
                return new ToolResult<object>()
                {
                    Success = false,
                    Error = new ResultError(ToolErrorCode.InvalidArgument, $"WriteFile: '{filePath}' already exists. Use operation=ReplaceFile to overwrite an existing file.")
                };
            }

            if (operation == WriteFileOperation.ReplaceFile && !exists)
            {
                return new ToolResult<object>()
                {
                    Success = false,
                    Error = new ResultError(ToolErrorCode.InvalidArgument, $"WriteFile: '{filePath}' does not exist. Use operation=CreateFile to create a new file.")
                };
            }

            var directory = Path.GetDirectoryName((string)filePath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var changes = new Dictionary<FilePath, string> { [filePath] = content };
            var result = await _workspaceManager.ApplyProposedChangesAsync(changes, validateChanges: validateOnApply, cancellationToken: cancellationToken);
            if (!result.Success && result.ValidationResult != null)
            {
                return new ToolResult<object>()
                {
                    Success = false,
                    Error = new ResultError(ToolErrorCode.Exception,
                        "WriteFile: this content would introduce new compiler errors — not written to disk. Fix the issue(s) below and retry:\n" +
                        await CompilerErrorLookupHelper.DescribeAsync(result.ValidationResult, _symbolNavigationEngine, cancellationToken))
                };
            }

            if (!result.Success)
            {
                return new ToolResult<object>()
                {
                    Success = false,
                    Error = new ResultError(ToolErrorCode.Exception, $"WriteFile failed to write '{filePath}': {result.Summary}")
                };
            }

            await _workspaceTools.WriteBlobForApplyAsync(operation == WriteFileOperation.CreateFile ? "create_file" : "replace_file", result);
            var strippedResult = result with { PreImages = null };
            return new ToolResult<object>()
            {
                Success = true,
                Data = strippedResult
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "WriteFile failed for '{FilePath}'", filePath);
            return new ToolResult<object>()
            {
                Success = false,
                Error = ToolErrorMapper.ToResultError(ex, _workspaceManager, $"WriteFile for '{filePath}'")
            };
        }
    }

    [McpServerTool(Name = "DeleteFile")]
    [Produces(DataTag.ChangeId)]
    [Description("Deletes a file from disk. Fails if the file does not exist. Routes through the same write-path chokepoint as every other mutating tool: refused if the file was modified externally since the last sync (see ListExternalDiskChanges/AcknowledgeExternalFileChanges), and undoable via UndoLastApply (the pre-delete content is captured). If the file is a tracked Roslyn Document, it's removed from the in-memory solution as part of the same operation.")]
    public async Task<ToolResult<object>> DeleteFile(
        [Description(ToolParams.Reason)] string reason,
        [Consumes(DataTag.SourceFilepath, required: true)] string filepath, CancellationToken cancellationToken = default)
    {
        FilePath filePath = FilePath.FromWire(filepath, _workspaceManager.GetSolutionRoot());
        try
        {
            if (!File.Exists(filePath))
            {
                return new ToolResult<object>()
                {
                    Success = false,
                    Error = new ResultError(ToolErrorCode.InvalidArgument, $"DeleteFile: '{filePath}' does not exist.")
                };
            }

            var result = await _workspaceManager.ApplyProposedChangesAsync(
                changes: [],
                cancellationToken: cancellationToken,
                deletePaths: [filePath]);
            if (!result.Success)
            {
                return new ToolResult<object>()
                {
                    Success = false,
                    Error = new ResultError(ToolErrorCode.Exception, $"DeleteFile failed to delete '{filePath}': {result.Summary}")
                };
            }

            await _workspaceTools.WriteBlobForApplyAsync("delete_file", result);
            var strippedResult = result with { PreImages = null };
            return new ToolResult<object>()
            {
                Success = true,
                Data = strippedResult
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DeleteFile failed for '{FilePath}'", filePath);
            return new ToolResult<object>()
            {
                Success = false,
                Error = ToolErrorMapper.ToResultError(ex, _workspaceManager, $"DeleteFile for '{filePath}'")
            };
        }
    }

    /// <summary>
    /// Fraction of <paramref name="oldContent"/>'s line count that <paramref name="newContent"/>
    /// would remove. Only shrinkage counts — a large *increase* (codegen, genuine expansion) is
    /// not the "submitted a fragment as if it were the whole file" failure mode this guards
    /// against, so it's exempt. Returns 0 for a new file (no oldContent to shrink from) or a
    /// same-size-or-larger replacement.
    /// </summary>
    private static double PercentLinesRemoved(string? oldContent, string newContent)
    {
        if (string.IsNullOrEmpty(oldContent))
        {
            return 0;
        }

        int oldLines = oldContent.Split('\n').Length;
        int newLines = newContent.Split('\n').Length;
        if (newLines >= oldLines || oldLines == 0)
        {
            return 0;
        }

        return (oldLines - newLines) / (double)oldLines;
    }

    /// <summary>
    /// Count of non-blank source lines in <paramref name="content"/> that contain real C# syntax
    /// (tokens), as opposed to lines that are blank or entirely comment trivia. Parsed rather than
    /// regex-matched so that comment markers appearing inside string literals don't skew the count.
    /// Returns 0 (guard exempt — see <see cref="PercentActiveCodeLinesRemoved"/>) if the content
    /// doesn't parse as C#, since a non-.cs file has no meaningful "active code line" notion here.
    /// </summary>
    private static int CountActiveCodeLines(string content)
    {
        var tree = CSharpSyntaxTree.ParseText(content);
        var text = tree.GetText();
        var activeLines = new HashSet<int>();
        foreach (var token in tree.GetRoot().DescendantTokens())
        {
            if (token.IsKind(SyntaxKind.None) || token.IsKind(SyntaxKind.EndOfFileToken))
            {
                continue;
            }

            int startLine = text.Lines.GetLineFromPosition(token.SpanStart).LineNumber;
            int endLine = text.Lines.GetLineFromPosition(token.Span.End).LineNumber;
            for (int line = startLine; line <= endLine; line++)
            {
                activeLines.Add(line);
            }
        }

        return activeLines.Count;
    }

    /// <summary>
    /// Fraction of <paramref name="oldContent"/>'s active (non-comment, non-blank) C# code lines
    /// that <paramref name="newContent"/> would remove. This catches the case the raw
    /// <see cref="PercentLinesRemoved"/> line-count guard misses entirely: an agent that comments
    /// out every existing line one-for-one (e.g. prefixing each with "// ") produces a file with
    /// the SAME line count as before, so the shrink guard sees 0% change, even though the file now
    /// contains no working code. Only files that still look like C# after the edit are checked
    /// (both old and new content must parse to at least one active line) — a genuine full-file
    /// deletion-to-near-empty is already caught by the line-count guard, and non-.cs content has no
    /// "active code line" concept to compare. Returns 0 (exempt) for a new file or malformed content.
    /// </summary>
    private static double PercentActiveCodeLinesRemoved(string filePath, string? oldContent, string newContent)
    {
        if (string.IsNullOrEmpty(oldContent) || !filePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        int oldActive = CountActiveCodeLines(oldContent);
        if (oldActive == 0)
        {
            return 0;
        }

        int newActive = CountActiveCodeLines(newContent);
        if (newActive >= oldActive)
        {
            return 0;
        }

        return (oldActive - newActive) / (double)oldActive;
    }

    /// <summary>
    /// A files-format apply where any file would lose more than this fraction of its line count
    /// (see <see cref="PercentLinesRemoved"/>), or of its active C# code lines (see
    /// <see cref="PercentActiveCodeLinesRemoved"/>), is rejected (see
    /// <see cref="ToolErrorCode.ConfirmationRequired"/>) rather than applied — the first check
    /// catches a caller submitting only a changed fragment as if it were the entire file; the
    /// second catches the same intent expressed by commenting out the whole file instead of
    /// shortening it, which leaves the raw line count unchanged. A large increase in either
    /// dimension is exempt from both.
    /// </summary>
    private const double LargeShrinkRejectionThreshold = 0.5;



    [McpServerTool(Name = "ApplyDiff")]
    [Produces(DataTag.ChangeId)]
    [Description("Applies or validates a change set. changesetFormat=files → changes dict filePath→newContent (filepath not used). changesetFormat=diff → filepath and unifiedDiff are BOTH REQUIRED (filepath names the single file the diff applies to; omitting it is a common mistake and fails immediately). For changesetFormat=diff, hunk line numbers are treated as a starting guess: if a hunk's declared position doesn't match, this searches nearby lines and re-anchors automatically, so modest line-number drift from an earlier edit to the same file is tolerated. Returns ApplyChangesResult with UndoChangeId on successful apply. The full pre-edit file content is NOT included by default (it's already captured for undo via UndoLastApply/GetOperationDetail) — pass returnDiff=true to get a unified-diff-style preview of what changed instead. IMPORTANT: for changesetFormat=files with action=apply, any file whose content would shrink by more than 50% (by line count, OR by active/non-comment C# code lines — so commenting out the whole file instead of shortening it is caught too) is rejected with errorCode=ConfirmationRequired — this is a strong signal you submitted only a changed fragment as if it were the whole file, or commented out code instead of actually editing/removing it, rather than a genuine whole-file rewrite. If that happens, re-submit the complete, unabridged file content in a fresh ApplyDiff call (or switch to changesetFormat=diff for a partial edit) — do not retry with a different action. By default this also delta-compiles the edited project(s) plus every project that transitively references them BEFORE writing, and REJECTS the change if it introduces any new compiler error — so renaming or changing the signature of a member will fail unless every call site (possibly in a different file) is updated to match in the SAME changeset. For a rename or signature change, prefer RenameSymbol/ChangeSignature, which update all call sites atomically in one operation; if you must do it manually across multiple ApplyDiff calls, either include every affected file's changes in one changesetFormat=files call, or pass validateOnApply=false on the intermediate calls and validate once at the end — do not repeatedly retry one file's edit expecting a different, not-yet-edited file to already match.")]
    public async Task<ToolResult<object>> ApplyDiff([Description(ToolParams.Reason)] string reason, [ExternalInputRequired(DataTag.ChangeseFormat)] ChangesetFormat changesetFormat, [ExternalInputRequired(DataTag.Action)] ProposedChangeAction action, [ExternalInputRequired(DataTag.OperationId)] Dictionary<FilePath, string>? changes = null, [Consumes(DataTag.SourceFilepath, required: false)] string? filepath = null, [ToolOption(ToolOptionTag.UnifiedDiff)] string? unifiedDiff = null, [ToolOption(ToolOptionTag.RetryCount)] int retryCount = 3, [ToolOption(ToolOptionTag.ValidateOnApply)][Description(ToolParams.ValidateOnApply)] bool validateOnApply = true, [Description(ToolParams.ReturnDiff)][ToolOption(ToolOptionTag.ReturnDiff)] bool returnDiff = false, // RequestContext<CallToolRequestParams> requestParams = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            FilePath filePath = _workspaceManager.SetFilePath(filepath);
            if (changesetFormat == ChangesetFormat.files)
            {
                if (changes == null)
                {
                    return new ToolResult<object>()
                    {
                        Success = false,
                        Error = new ResultError(ToolErrorCode.InvalidArgument, "changes is required when changesetFormat=files.")
                    };
                }

                if (action == ProposedChangeAction.apply)
                {
                    string? oversizedFile = null;
                    double oversizedPercent = 0;
                    bool oversizedIsCommentCollapse = false;
                    foreach (var (changedPath, newContent) in changes)
                    {
                        var oldContent = await FileIoHelper.ReadAllTextIfExistsAsync(changedPath, cancellationToken);
                        var percentRemoved = PercentLinesRemoved(oldContent, newContent);
                        var percentActiveRemoved = PercentActiveCodeLinesRemoved(changedPath, oldContent, newContent);
                        if (percentRemoved > LargeShrinkRejectionThreshold || percentActiveRemoved > LargeShrinkRejectionThreshold)
                        {
                            oversizedFile = changedPath;
                            oversizedPercent = Math.Max(percentRemoved, percentActiveRemoved);
                            oversizedIsCommentCollapse = percentActiveRemoved > LargeShrinkRejectionThreshold && percentRemoved <= LargeShrinkRejectionThreshold;
                            break;
                        }
                    }

                    if (oversizedFile != null)
                    {
                        return new ToolResult<object>()
                        {
                            Success = false,
                            Error = new ResultError(ToolErrorCode.ConfirmationRequired,
                                oversizedIsCommentCollapse
                                    ? $"File '{oversizedFile}' would have {oversizedPercent:P0} of its active code lines turned into comments, exceeding the {LargeShrinkRejectionThreshold:P0} threshold for a files-format apply. " +
                                      "This usually means the intended change (or a partial rewrite) was commented out instead of actually being edited or removed, rather than a genuine intentional deletion. If this is a genuine intentional removal of this code, re-submit ApplyDiff with the complete file content included in 'changes'. For a partial edit, use changesetFormat=diff instead."
                                    : $"File '{oversizedFile}' would shrink by {oversizedPercent:P0}, exceeding the {LargeShrinkRejectionThreshold:P0} threshold for a files-format apply. " +
                                      "This usually means only a changed fragment was submitted instead of the complete file content. If this is a genuine whole-file rewrite, re-submit ApplyDiff with the complete file content included in 'changes'. For a partial edit, use changesetFormat=diff instead.")
                        };
                    }

                    var result = await _workspaceManager.ApplyProposedChangesAsync(changes, retryCount, validateChanges: validateOnApply);
                    if (!result.Success && result.ValidationResult != null)
                        return new ToolResult<object>()
                        {
                            Success = false,
                            Error = new ResultError(ToolErrorCode.Exception,
                                "ApplyDiff: the diff was valid and matched the target file, but the resulting code introduces new compiler errors — change not applied. Fix the issue(s) below and retry:\n" +
                                await CompilerErrorLookupHelper.DescribeAsync(result.ValidationResult, _symbolNavigationEngine, cancellationToken))
                        };
                    await _workspaceTools.WriteBlobForApplyAsync("apply_diff", result);
                    // PreImages (full pre-edit file content) is dropped from the default response -
                    // it's already captured in the undo blob written above (GetOperationDetail/
                    // UndoLastApply can retrieve it) and was the single largest contributor to
                    // ApplyDiff responses exceeding the calling harness's token limit on large files.
                    var strippedResult = result with { PreImages = null };
                    object responseData = returnDiff
                        ? new
                        {
                            result = strippedResult,
                            diff = SentinelRefactoringTools.BuildDiffFromPreImages(changes, result.PreImages)
                        }
                        : strippedResult;
                    return new ToolResult<object>()
                    {
                        Success = true,
                        Data = responseData
                    };
                }

                if (action == ProposedChangeAction.validate)
                {
                    try
                    {
                        var validationResult = await _validationEngine.ValidateChangesAsync(changes);
                        return validationResult.Success ? new ToolResult<object>()
                        {
                            Success = true,
                            Data = validationResult
                        }

                        : new ToolResult<object>()
                        {
                            Success = false,
                            Error = new ResultError(ToolErrorCode.Exception, $"ApplyDiff validate failed: {validationResult.Diagnostics}")
                        };
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "ApplyDiff validate unexpected exception");
                        return new ToolResult<object>()
                        {
                            Success = false,
                            Error = ToolErrorMapper.ToResultError(ex, _workspaceManager, "ApplyDiff validate")
                        };
                    }
                }
            }
            else if (changesetFormat == ChangesetFormat.diff)
            {
                if (!filePath.Validated && string.IsNullOrEmpty(unifiedDiff))
                {
                    return new ToolResult<object>()
                    {
                        Success = false,
                        Error = new ResultError(ToolErrorCode.InvalidArgument, "ApplyDiff: both 'filepath' and 'unifiedDiff' are required when changesetFormat=diff.")
                    };
                }

                if (!filePath.Validated)
                {
                    return new ToolResult<object>()
                    {
                        Success = false,
                        Error = new ResultError(ToolErrorCode.InvalidArgument, "ApplyDiff: 'filepath' is required when changesetFormat=diff (it names the single file the unifiedDiff applies to). Only changesetFormat=files takes multiple files via 'changes'.")
                    };
                }

                if (string.IsNullOrEmpty(unifiedDiff))
                {
                    return new ToolResult<object>()
                    {
                        Success = false,
                        Error = new ResultError(ToolErrorCode.InvalidArgument, "ApplyDiff: 'unifiedDiff' is required when changesetFormat=diff.")
                    };
                }

                if (action == ProposedChangeAction.apply)
                {
                    try
                    {
                        var solution = await _workspaceManager.GetCurrentSolutionAsync(cancellationToken);
                        var document = solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => d.Name == filePath.Absolute || d.FilePath == filePath.Absolute);
                        if (document == null)
                        {
                            return new ToolResult<object>()
                            {
                                Success = false,
                                Error = new ResultError(ToolErrorCode.InvalidArgument, "File not found.")
                            };
                        }

                        var oldText = await document.GetTextAsync();
                        var newContent = _diffEngine.ApplyDiff(oldText, unifiedDiff).ToString();
                        var targetPath = document.FilePath ?? filePath;
                        var diffChanges = new Dictionary<FilePath, string>
                        {
                            [targetPath] = newContent
                        };
                        var result = await _workspaceManager.ApplyProposedChangesAsync(diffChanges, validateChanges: validateOnApply);
                        if (!result.Success && result.ValidationResult != null)
                            return new ToolResult<object>()
                            {
                                Success = false,
                                Error = new ResultError(ToolErrorCode.Exception,
                                    "ApplyDiff: the diff was valid and matched the target file, but the resulting code introduces new compiler errors — change not applied. Fix the issue(s) below and retry:\n" +
                                    await CompilerErrorLookupHelper.DescribeAsync(result.ValidationResult, _symbolNavigationEngine, cancellationToken))
                            };
                        await _workspaceTools.WriteBlobForApplyAsync("apply_diff", result);
                        var strippedDiffResult = result with { PreImages = null };
                        object diffResponseData = returnDiff
                            ? new
                            {
                                result = strippedDiffResult,
                                diff = SentinelRefactoringTools.BuildDiffFromPreImages(diffChanges, result.PreImages)
                            }
                            : strippedDiffResult;
                        return new ToolResult<object>()
                        {
                            Success = true,
                            Data = diffResponseData
                        };
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "ApplyDiff diff apply unexpected exception for '{FilePath}'", filePath);
                        return new ToolResult<object>()
                        {
                            Success = false,
                            Error = ToolErrorMapper.ToResultError(ex, _workspaceManager, $"ApplyDiff diff apply for '{filePath}'")
                        };
                    }
                }

                if (action == ProposedChangeAction.validate)
                {
                    var validationResult = await _validationEngine.ValidateDiffAsync(filePath.Absolute, unifiedDiff);
                    return validationResult.Success ? new ToolResult<object>()
                    {
                        Success = true,
                        Data = validationResult
                    }

                    : new ToolResult<object>()
                    {
                        Success = false,
                        Error = new ResultError(ToolErrorCode.Exception, $"ApplyDiff diff validate failed: {validationResult.Diagnostics.ToInfo()}")
                    };
                }
            }

            return new ToolResult<object>()
            {
                Success = false,
                Error = new ResultError(ToolErrorCode.Exception, $"Unhandled changesetFormat '{changesetFormat}' / action '{action}'.")
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ApplyDiff ({ChangesetFormat}/{Action}) failed", changesetFormat, action);
            return new ToolResult<object>()
            {
                Success = false,
                Error = ToolErrorMapper.ToResultError(ex, _workspaceManager, "ApplyDiff")
            };
        }
    }
}
