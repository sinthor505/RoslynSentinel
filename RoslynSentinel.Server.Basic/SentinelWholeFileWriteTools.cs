using System.ComponentModel;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.Logging;

using ModelContextProtocol.Server;

namespace RoslynSentinel.Server.Basic;

[McpServerToolType]
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
    [Description("Writes a whole file to disk, creating or fully overwriting it. By default this delta-compiles the edited file plus every project that transitively references it before writing, and rejects the write if that introduces any new compiler error. For a partial edit, use ApplyUnifiedDiff instead. For a change that necessarily spans multiple files (e.g. renaming a method used elsewhere), use RenameSymbol/ChangeSignature to update all call sites atomically, or pass validateOnApply=false on intermediate writes and validate once at the end.")]
    public async Task<ToolResult<object>> WriteFile(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [Description("CreateFile requires the file NOT to already exist (fails otherwise). ReplaceFile requires the file to already exist (fails otherwise).")]
        [ExternalInputRequired(DataTag.Action)] WriteFileOperation operation,
        [Consumes(DataTag.SourceFilepath, required: true)] FilePathWrapper filepath,
        [Description("Full content of the file. Parent directories are created automatically if missing.")] string content,
        [ToolOption(ToolOptionTag.ValidateOnApply)][Description(ToolParams.ValidateOnApply)] bool validateOnApply = true,
        CancellationToken cancellationToken = default)
    {
        FilePathWrapper filePathResolved = FilePathWrapper.FromWire(filepath, _workspaceManager.GetSolutionRoot());
        try
        {
            bool exists = File.Exists(filePathResolved);
            if (operation == WriteFileOperation.CreateFile && exists)
            {
                return new ToolResult<object>()
                {
                    Success = false,
                    Error = new ResultError(ToolErrorCode.InvalidArgument, $"WriteFile: '{filePathResolved}' already exists. Use operation=ReplaceFile to overwrite an existing file.")
                };
            }

            if (operation == WriteFileOperation.ReplaceFile && !exists)
            {
                return new ToolResult<object>()
                {
                    Success = false,
                    Error = new ResultError(ToolErrorCode.InvalidArgument, $"WriteFile: '{filePathResolved}' does not exist. Use operation=CreateFile to create a new file.")
                };
            }

            var directory = Path.GetDirectoryName((string)filePathResolved);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var changes = new Dictionary<FilePathWrapper, string> { [filePathResolved] = content };
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
                    Error = new ResultError(ToolErrorCode.Exception, $"WriteFile failed to write '{filePathResolved}': {result.Summary}")
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
            _logger.LogError(ex, "WriteFile failed for '{FilePathWrapper}'", filePathResolved);
            return new ToolResult<object>()
            {
                Success = false,
                Error = ToolErrorMapper.ToResultError(ex, _workspaceManager, $"WriteFile for '{filePathResolved}'")
            };
        }
    }

    [McpServerTool(Name = "DeleteFile")]
    [Produces(DataTag.ChangeId)]
    [Description("Deletes a file from disk. Fails if the file does not exist. Routes through the same write-path chokepoint as every other mutating tool: refused if the file was modified externally since the last sync (see ListExternalDiskChanges/AcknowledgeExternalFileChanges), and undoable via UndoLastApply (the pre-delete content is captured). If the file is a tracked Roslyn Document, it's removed from the in-memory solution as part of the same operation.")]
    public async Task<ToolResult<object>> DeleteFile(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [Consumes(DataTag.SourceFilepath, required: true)] FilePathWrapper filepath, CancellationToken cancellationToken = default)
    {
        FilePathWrapper filePathResolved = FilePathWrapper.FromWire(filepath, _workspaceManager.GetSolutionRoot());
        try
        {
            if (!File.Exists(filePathResolved))
            {
                return new ToolResult<object>()
                {
                    Success = false,
                    Error = new ResultError(ToolErrorCode.InvalidArgument, $"DeleteFile: '{filePathResolved}' does not exist.")
                };
            }

            var result = await _workspaceManager.ApplyProposedChangesAsync(
                changes: [],
                cancellationToken: cancellationToken,
                deletePaths: [filePathResolved]);
            if (!result.Success)
            {
                return new ToolResult<object>()
                {
                    Success = false,
                    Error = new ResultError(ToolErrorCode.Exception, $"DeleteFile failed to delete '{filePathResolved}': {result.Summary}")
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
            _logger.LogError(ex, "DeleteFile failed for '{FilePathWrapper}'", filePathResolved);
            return new ToolResult<object>()
            {
                Success = false,
                Error = ToolErrorMapper.ToResultError(ex, _workspaceManager, $"DeleteFile for '{filePathResolved}'")
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
    /// Builds the success-response payload for a diff-hunk apply (ApplyDiff's changesetFormat=diff
    /// branch, and ApplyUnifiedDiff), including <paramref name="diffReport"/>'s findings when it has
    /// any. Previously a hunk whose header line counts didn't match its own body (e.g. declaring 2
    /// removed/10 added lines when the body actually had 2 removed/159 added) was only logged
    /// server-side on success — the caller had no way to learn its hunk was malformed until the file
    /// came out wrong. Surfacing it here lets the calling model catch its own mistake immediately.
    /// </summary>
    private static object BuildDiffApplyResponseData(
        ApplyChangesResult strippedResult,
        DiffHunkAnalyzer.DiffReport diffReport,
        bool returnDiff,
        Dictionary<FilePathWrapper, string> diffChanges,
        IReadOnlyDictionary<string, string?>? preImages)
    {
        if (!returnDiff && !diffReport.HasFindings)
        {
            return strippedResult;
        }

        return new
        {
            result = strippedResult,
            diff = returnDiff ? SentinelRefactoringTools.BuildDiffFromPreImages(diffChanges, preImages) : null,
            diffHunkFindings = diffReport.HasFindings ? diffReport.Describe() : null
        };
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
    // CONDITIONAL-PARAM-REVIEW-REQUIRED: changesetFormat=files requires 'changes' (filepath/unifiedDiff
    // unused); changesetFormat=diff requires 'filepath' and 'unifiedDiff' (changes unused). No single
    // param is universally required beyond changesetFormat/action, so a model can supply the wrong
    // subset for its chosen format and only find out at runtime.
    [McpServerTool(Name = "ApplyDiff")]
    [Produces(DataTag.ChangeId)]
    [Description("Applies or validates a change set, either as full file contents (changesetFormat=files) or as a unified diff against one file (changesetFormat=diff). For changesetFormat=diff, hunk line numbers are a starting guess — a mismatched position is re-anchored by searching nearby lines, so modest drift from an earlier edit is tolerated. For changesetFormat=files with action=apply, any file that would shrink by more than 50% (by line count or by active/non-comment code lines) is rejected with errorCode=ConfirmationRequired, since that usually signals a partial fragment or a comment-collapse was submitted instead of the full file. By default this also delta-compiles the edited project(s) plus every transitively-referencing project before writing, and rejects the change if it introduces a new compiler error — for a rename or signature change spanning files, prefer RenameSymbol/ChangeSignature, or pass validateOnApply=false on intermediate calls and validate once at the end.")]
    public async Task<ToolResult<object>> ApplyDiff(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [Description("files: changes is a filePath→newContent dict (filepath/unifiedDiff unused). diff: filepath and unifiedDiff apply to a single file (changes unused).")]
        [ExternalInputRequired(DataTag.ChangeseFormat)] ChangesetFormat changesetFormat,
        [ExternalInputRequired(DataTag.Action)] ProposedChangeAction action,
        // CONDITIONAL-PARAM-REVIEW-REQUIRED: required when changesetFormat=files, unused otherwise.
        [Description("Required when changesetFormat=files: filePath→newContent for every file to write.")]
        [ExternalInputRequired(DataTag.OperationId)] Dictionary<string, string>? changes = null,
        // CONDITIONAL-PARAM-REVIEW-REQUIRED: required when changesetFormat=diff, unused otherwise.
        [Description("Required when changesetFormat=diff: the single file unifiedDiff applies to.")]
        [Consumes(DataTag.SourceFilepath, required: false)] string? filepath = null,
        // CONDITIONAL-PARAM-REVIEW-REQUIRED: required when changesetFormat=diff, unused otherwise.
        [Description("Required when changesetFormat=diff: the unified diff to apply to filepath.")]
        [ToolOption(ToolOptionTag.UnifiedDiff)] string? unifiedDiff = null,
        [ToolOption(ToolOptionTag.RetryCount)] int retryCount = 3,
        [ToolOption(ToolOptionTag.ValidateOnApply)][Description(ToolParams.ValidateOnApply)] bool validateOnApply = true,
        [Description(ToolParams.ReturnDiff)][ToolOption(ToolOptionTag.ReturnDiff)] bool returnDiff = false,
        // RequestContext<CallToolRequestParams> requestParams = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            FilePathWrapper filePathResolved = _workspaceManager.SetFilePath(filepath);
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

                // MCP wire type is Dictionary<string,string>: a Dictionary<FilePathWrapper,...> tool
                // parameter makes System.Text.Json.Schema.JsonSchemaExporter fall back to an
                // unrepresentable `true` schema node for the key type, which LM Studio's grammar
                // converter rejects outright ("Unrecognized schema: true"). Resolve keys to
                // FilePathWrapper here instead, after the schema boundary.
                Dictionary<FilePathWrapper, string> resolvedChanges = changes.ToDictionary(
                    kvp => _workspaceManager.SetFilePath(kvp.Key),
                    kvp => kvp.Value);

                if (action == ProposedChangeAction.apply)
                {
                    string? oversizedFile = null;
                    double oversizedPercent = 0;
                    bool oversizedIsCommentCollapse = false;
                    foreach (var (changedPath, newContent) in resolvedChanges)
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

                    var result = await _workspaceManager.ApplyProposedChangesAsync(resolvedChanges, retryCount, validateChanges: validateOnApply);
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
                            diff = SentinelRefactoringTools.BuildDiffFromPreImages(resolvedChanges, result.PreImages)
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
                        var validationResult = await _validationEngine.ValidateChangesAsync(resolvedChanges);
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
                if (!filePathResolved.Validated && string.IsNullOrEmpty(unifiedDiff))
                {
                    return new ToolResult<object>()
                    {
                        Success = false,
                        Error = new ResultError(ToolErrorCode.InvalidArgument, "ApplyDiff: both 'filepath' and 'unifiedDiff' are required when changesetFormat=diff.")
                    };
                }

                if (!filePathResolved.Validated)
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
                        var document = solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => d.Name == filePathResolved.Absolute || d.FilePath == filePathResolved.Absolute);
                        if (document == null)
                        {
                            return new ToolResult<object>()
                            {
                                Success = false,
                                Error = new ResultError(ToolErrorCode.InvalidArgument, "File not found.")
                            };
                        }

                        var oldText = await document.GetTextAsync();
                        var newContent = _diffEngine.ApplyDiff(oldText, unifiedDiff, out var diffReport).ToString();
                        var targetPath = document.FilePath ?? filePathResolved;
                        var diffChanges = new Dictionary<FilePathWrapper, string>
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
                        object diffResponseData = BuildDiffApplyResponseData(strippedDiffResult, diffReport, returnDiff, diffChanges, result.PreImages);
                        return new ToolResult<object>()
                        {
                            Success = true,
                            Data = diffResponseData
                        };
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "ApplyDiff diff apply unexpected exception for '{FilePathWrapper}'", filePathResolved);
                        return new ToolResult<object>()
                        {
                            Success = false,
                            Error = ToolErrorMapper.ToResultError(ex, _workspaceManager, $"ApplyDiff diff apply for '{filePathResolved}'")
                        };
                    }
                }

                if (action == ProposedChangeAction.validate)
                {
                    var validationResult = await _validationEngine.ValidateDiffAsync(filePathResolved.Absolute, unifiedDiff);
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
    // Simplified, diff-only sibling of ApplyDiff — collapses ApplyDiff's two required-param-sets
    // (files: 'changes' dict / diff: 'filepath'+'unifiedDiff', with 'filepath' silently ignored in
    // files mode) into a single always-required (filepath, unifiedDiff) pair, closing the common
    // agent footgun of supplying unifiedDiff without filepath. Whole-file rewrites now go through
    // WriteFile(operation=ReplaceFile) instead of a 'files' mode here. ApplyDiff itself is kept
    // unchanged (not deleted) so its multi-file 'files' mode can be reactivated later if needed.
    // Moved here (off the default MCP surface, this class carries no [McpServerToolType]) from
    // SentinelWorkspaceTools.cs — ReplaceSnippet there now covers small exact-text edits on the
    // default surface without diff-hunk syntax; this tool is kept for reactivation if a genuine
    // need for multi-line diff-hunk edits resurfaces. See
    // docs/current/design_applyunifieddiff_replace_snippet_v1.md.
    [McpServerTool(Name = "ApplyUnifiedDiff")]
    [Produces(DataTag.ChangeId)]
    [Description("Applies or validates a unified diff against a single file. Hunk line numbers are a starting guess — a mismatched position is re-anchored by searching nearby lines, so modest drift from an earlier edit is tolerated. For a whole-file rewrite, use WriteFile(operation=ReplaceFile) instead. By default this also delta-compiles the edited project(s) plus every transitively-referencing project before writing, and rejects the change if it introduces a new compiler error — since this tool only touches one file per call, prefer RenameSymbol/ChangeSignature for a rename or signature change spanning files, or pass validateOnApply=false here and on the other file's edit, then validate once after both are applied.")]
    public async Task<ToolResult<object>> ApplyUnifiedDiff(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [Description("apply: applies the diff. validate: checks it would apply cleanly without writing.")]
        [ExternalInputRequired(DataTag.Action)] ProposedChangeAction action,
        [Description("The single file unifiedDiff applies to.")]
        [Consumes(DataTag.SourceFilepath, required: true)] FilePathWrapper filepath,
        [Description("The unified diff to apply to filepath.")]
        [ToolOption(ToolOptionTag.UnifiedDiff, required: true)] string unifiedDiff,
        [ToolOption(ToolOptionTag.ValidateOnApply)][Description(ToolParams.ValidateOnApply)] bool validateOnApply = true,
        [Description(ToolParams.ReturnDiff)][ToolOption(ToolOptionTag.ReturnDiff)] bool returnDiff = false,
        CancellationToken cancellationToken = default)
    {
        try
        {
            FilePathWrapper filePathResolved = _workspaceManager.SetFilePath(filepath);
            if (!filePathResolved.Validated)
            {
                return new ToolResult<object>()
                {
                    Success = false,
                    Error = new ResultError(ToolErrorCode.InvalidArgument, "ApplyUnifiedDiff: 'filepath' is required (it names the single file the unifiedDiff applies to).")
                };
            }

            if (string.IsNullOrEmpty(unifiedDiff))
            {
                return new ToolResult<object>()
                {
                    Success = false,
                    Error = new ResultError(ToolErrorCode.InvalidArgument, "ApplyUnifiedDiff: 'unifiedDiff' is required.")
                };
            }

            if (action == ProposedChangeAction.apply)
            {
                try
                {
                    var solution = await _workspaceManager.GetCurrentSolutionAsync(cancellationToken);
                    var document = solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => d.Name == filePathResolved.Absolute || d.FilePath == filePathResolved.Absolute);
                    if (document == null)
                    {
                        return new ToolResult<object>()
                        {
                            Success = false,
                            Error = new ResultError(ToolErrorCode.InvalidArgument, "File not found.")
                        };
                    }

                    var oldText = await document.GetTextAsync();
                    var newContent = _diffEngine.ApplyDiff(oldText, unifiedDiff, out var diffReport).ToString();
                    var targetPath = document.FilePath ?? filePathResolved;
                    var diffChanges = new Dictionary<FilePathWrapper, string>
                    {
                        [targetPath] = newContent
                    };
                    var result = await _workspaceManager.ApplyProposedChangesAsync(diffChanges, validateChanges: validateOnApply);
                    if (!result.Success && result.ValidationResult != null)
                        return new ToolResult<object>()
                        {
                            Success = false,
                            Error = new ResultError(ToolErrorCode.Exception,
                                "ApplyUnifiedDiff: the diff was valid and matched the target file, but the resulting code introduces new compiler errors — change not applied. Fix the issue(s) below and retry:\n" +
                                await CompilerErrorLookupHelper.DescribeAsync(result.ValidationResult, _symbolNavigationEngine, cancellationToken))
                        };
                    await _workspaceTools.WriteBlobForApplyAsync("apply_unified_diff", result);
                    var strippedDiffResult = result with { PreImages = null };
                    object diffResponseData = BuildDiffApplyResponseData(strippedDiffResult, diffReport, returnDiff, diffChanges, result.PreImages);
                    return new ToolResult<object>()
                    {
                        Success = true,
                        Data = diffResponseData
                    };
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "ApplyUnifiedDiff apply unexpected exception for '{FilePathWrapper}'", filePathResolved);
                    return new ToolResult<object>()
                    {
                        Success = false,
                        Error = ToolErrorMapper.ToResultError(ex, _workspaceManager, $"ApplyUnifiedDiff apply for '{filePathResolved}'")
                    };
                }
            }

            if (action == ProposedChangeAction.validate)
            {
                var validationResult = await _validationEngine.ValidateDiffAsync(filePathResolved.Absolute, unifiedDiff);
                return validationResult.Success ? new ToolResult<object>()
                {
                    Success = true,
                    Data = validationResult
                }

                : new ToolResult<object>()
                {
                    Success = false,
                    Error = new ResultError(ToolErrorCode.Exception, $"ApplyUnifiedDiff validate failed: {validationResult.Diagnostics.ToInfo()}")
                };
            }

            return new ToolResult<object>()
            {
                Success = false,
                Error = new ResultError(ToolErrorCode.Exception, $"Unhandled action '{action}'.")
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ApplyUnifiedDiff ({Action}) failed", action);
            return new ToolResult<object>()
            {
                Success = false,
                Error = ToolErrorMapper.ToResultError(ex, _workspaceManager, "ApplyUnifiedDiff")
            };
        }
    }
}
