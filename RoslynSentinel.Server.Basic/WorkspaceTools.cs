using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;

using Microsoft.Extensions.Logging;

using ModelContextProtocol.Server;

namespace RoslynSentinel.Server.Basic;
/// <summary>
/// God-class MCP surface for workspace tools. Six of its tool methods -> GetMethodSource,
/// GetFileOutline, ListAll, SearchSolutionText, GetOperationDetail, GetLargeResult -> are thin
/// delegates to <see cref="WorkspaceReadNavigationTools"/> (field <c>_readNav</c>), which itself
/// delegates to <see cref="WorkspaceReadNavigationImpl"/> for the actual logic. That pair is a
/// trial slice of a larger planned split of this class -> see
/// docs/current/plans/plan_split_workspace_refactoring_tools_for_di.md. This class remains the one
/// actually registered/reachable over MCP for those six tool names until that plan's Decision 4/
/// Decision 7 step 4 (fine-grained mode-string wiring) lands.
/// </summary>
[McpServerToolType]
public class WorkspaceTools
{
    private readonly SymbolNavigationEngine _symbolNavigationEngine;    // Added by AddConstructorParameter
    private readonly BuildEngine _buildEngine;
    private readonly TestRunEngine _testRunEngine;
    private readonly IWorkspaceManager _workspaceManager;
    private readonly ValidationEngine _validationEngine;
    private readonly DiagnosticEngine _diagnosticEngine;
    private readonly SolutionManagementEngine _solutionManagementEngine;
    private readonly StructuralRefinementEngine _structuralRefinementEngine;
    private readonly DependencyEngine _dependencyEngine;
    private readonly ProjectConsistencyEngine _projectConsistencyEngine;
    private readonly SentinelConfiguration _config;
    private readonly ILogger<WorkspaceTools> _logger;
    private readonly WorkspaceReadNavigationImpl _readNav;
    private readonly WriteToolAdviceHelper _writeAdvice;


    // Added by InsertMemberAfter (expected - used for diagnostics)
    private readonly WorkspaceProjectManagementTools _projectManagement;


    // Added by InsertMemberAfter (expected - used for diagnostics)
    private readonly WorkspaceBuildTestTools _buildTest;


    // Added by InsertMemberAfter (expected - used for diagnostics)
    private readonly WorkspaceFileEditTools _fileEdit;


    // Added by InsertMemberAfter (expected - used for diagnostics)
    private readonly WorkspaceHealthMiscTools _healthMisc;


    private static readonly JsonSerializerOptions _jsonOptions = new JsonSerializerOptions
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters =
            {
                new JsonStringEnumConverter()
            }
    };

    public WorkspaceTools(IWorkspaceManager workspaceManager, ValidationEngine validationEngine, DiffEngine diffEngine, DiagnosticEngine diagnosticEngine, SolutionManagementEngine solutionManagementEngine, StructuralRefinementEngine structuralRefinementEngine, DependencyEngine dependencyEngine, ProjectConsistencyEngine projectConsistencyEngine, SentinelConfiguration config, ILogger<WorkspaceTools> logger, BuildEngine buildEngine, SymbolNavigationEngine symbolNavigationEngine, TestRunEngine testRunEngine, WorkspaceReadNavigationImpl readNav, WriteToolAdviceHelper writeAdvice)
    {
        _workspaceManager = workspaceManager;
        _validationEngine = validationEngine;
        _diagnosticEngine = diagnosticEngine;
        _solutionManagementEngine = solutionManagementEngine;
        _structuralRefinementEngine = structuralRefinementEngine;
        _dependencyEngine = dependencyEngine;
        _projectConsistencyEngine = projectConsistencyEngine;
        _config = config;
        _logger = logger;
        _buildEngine = buildEngine;
        _symbolNavigationEngine = symbolNavigationEngine;
        _testRunEngine = testRunEngine;
        _readNav = readNav;
        _writeAdvice = writeAdvice;

        // Decision 7 step 2 (plan_split_workspace_refactoring_tools_for_di.md): WorkspaceTools
        // is now a legacy facade preserving its original constructor/tool signatures, delegating
        // internally to the newly-split *Tools classes.
        _projectManagement = new WorkspaceProjectManagementTools(workspaceManager, solutionManagementEngine, dependencyEngine, projectConsistencyEngine, structuralRefinementEngine, logger);
        _buildTest = new WorkspaceBuildTestTools(workspaceManager, diagnosticEngine, buildEngine, testRunEngine, logger);
        _fileEdit = new WorkspaceFileEditTools(workspaceManager, readNav, logger, validationEngine, symbolNavigationEngine, writeAdvice);
        _healthMisc = new WorkspaceHealthMiscTools(workspaceManager, config, buildEngine, logger);
    }
    [McpServerTool(Name = "Features")]
    [Produces(DataTag.Report)]
    [Description("Queries or updates feature flags.")]
    public Task<SentinelCallToolResult<object>> Features(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [Description("list: all flags. get: flags named in names. update: batch-updates flags in enabled.")]
        FeaturesAction action,
        [Description("Required for action=get: flag names to look up.")]
        List<string>? names = null,
        [Description("Required for action=update: [{Key, Value}] pairs to apply.")]
        List<KeyValuePair<string, bool>>? enabled = null,
        [Description("Test-only: delay before acting.")]
        int delaySeconds = 0,
        CancellationToken cancellationToken = default)
        => _healthMisc.Features(reason, action, names, enabled, delaySeconds, cancellationToken);
    [McpServerTool(Name = "ListSolutionItems")]
    [Produces(DataTag.FileList)]
    [Produces(DataTag.ProjectList)]
    [Produces(DataTag.DependencyList)]
    [Description("Lists projects, files, dependencies, or solution-folder items in the loaded solution.")]
    public Task<SentinelCallToolResult<object>> ListSolutionItems(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [Description("files/dependencies: requires projectName. projects/solutionItems: list every project or solution-folder item (solutionItems aren't searchable via SearchSolutionText - use ProjectDoc). all: everything in one call.")]
        [ExternalInputRequired(DataTag.Scope)] SolutionItemsKind kind,
        [Description("Required for kind=files/dependencies.")]
        [Consumes(DataTag.ProjectName)] string? projectName = null,
        CancellationToken cancellationToken = default)
        => _projectManagement.ListSolutionItems(reason, kind, projectName, cancellationToken);

    /// <summary>
    /// Hard cap on how many files ListWorkspaceSolutions will walk before giving up -> protects
    /// against a caller passing an overly broad root (see the drive-root guard below) that isn't
    /// caught by that check but still turns out to contain far more than any real workspace would
    /// (e.g. a node_modules-style tree, or a root one level above the intended one).
    /// </summary>
    private const int ListWorkspaceSolutionsMaxFilesWalked = 200_000;
    [McpServerTool(Name = "ListWorkspaceSolutions")]
    [Produces(DataTag.FileList)]
    [Produces(DataTag.SolutionList)]
    [Description("Lists .sln/.slnx files under a directory, for use with LoadSolution.")]
    public SentinelCallToolResult<List<SolutionFileInfo>, ResultError> ListWorkspaceSolutions(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [Description("A real project/repo directory, not a drive root.")] string workspacePath,
        CancellationToken cancellationToken = default)
        => _projectManagement.ListWorkspaceSolutions(reason, workspacePath, cancellationToken);
    // current directory, --base-repo-dir (if set), or the server's install directory.
    [McpServerTool(Name = "LoadSolution")]
    [Produces(DataTag.ResultOnly)]
    [Description("Loads a .NET solution into memory. Required before any operation needing a loaded solution.")]
    public Task<SentinelCallToolResult<object>> LoadSolution(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [Consumes(DataTag.SolutionFilepath, required: true)] string solutionPath,
        [ToolOption(ToolOptionTag.RepoDirectory)][Description("Base directory for resolving a relative solutionPath. Must exist on this host - omit rather than guess.")] string? baseRepoDir = null,
        [Description("true forces a full reload from disk, discarding in-memory state, when this solution is already loaded.")] bool forceReload = false,
        CancellationToken cancellationToken = default)
        => _projectManagement.LoadSolution(reason, solutionPath, baseRepoDir, forceReload, cancellationToken);

    // ListExternalDiskChanges/AcknowledgeExternalFileChanges moved to AdminTools.cs,
    // gated behind the "Admin" mode -> see docs/current/ideas/external-drift-hard-blocker.md.
    // Not model-visible by default anymore; reconciliation is an out-of-band operator action now.

    private static string PreviewFileContent(string content)
    {
        var lines = content.Split('\n');
        if (lines.Length <= 20)
        {
            return content;
        }

        var head = lines.Take(10);
        var tail = lines.TakeLast(10);
        return string.Join("\n", head) + "\n// ... (truncated)\n" + string.Join("\n", tail);
    }

    // ApplyUnifiedDiff moved to WholeFileWriteTools.cs (gated off the default MCP surface,
    // alongside ApplyDiff) -> see docs/current/design_applyunifieddiff_replace_snippet_v1.md.
    // ReplaceSnippet (below, on this default surface) replaces it for small, exact-text edits.

    // ReplaceSnippet/ReplaceSnippetBatch/CreateFile moved to WorkspaceFileEditTools/
    // WorkspaceFileEditImpl (Decision 7 step 2 facade split). This class keeps its original
    // constructor/tool signatures, delegating internally.
    [McpServerTool(Name = "ReplaceSnippet")]
    [Produces(DataTag.ChangeId)]
    // Deliberately names no whole-file-write tool. Attribute arguments must be compile-time
    // constants, so this text can't be generated per-session from the tool registry the way the
    // over-cap error can (see WriteToolAdviceHelper) -> and a hardcoded name here would be shown to
    // the model on every single call even when that tool is gated off, which is the run-398 failure
    // in its most persistent form. The error path is where the redirect is actually needed.
    [Description("Replaces one exact text block with another in a file, for small localized edits. Prefer a structural Roslyn tool for structural changes.")]
    public Task<SentinelCallToolResult<ReplaceSnippetResult>> ReplaceSnippet(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [Description("apply: writes the change. validate: checks it would apply cleanly without writing.")]
        [ExternalInputRequired(DataTag.Action)] ProposedChangeAction action,
        // CONDITIONAL-PARAM-REVIEW-REQUIRED: required only when 'edits' is omitted -> see the either/or check below.
        [Consumes(DataTag.SourceFilepath, required: false)] FilePathWrapper? filePath = null,
        [ToolOption(ToolOptionTag.OldContent, required: false)][Description(ToolParams.OldContent)] string? oldContent = null,
        [ToolOption(ToolOptionTag.NewContent, required: false)][Description(ToolParams.NewContent)] string? newContent = null,
        [Description(ToolParams.LineBefore)][ExternalInputRequired(DataTag.LineBefore, required: false)] string? lineBefore = null,
        [Description(ToolParams.LineAfter)][ExternalInputRequired(DataTag.LineAfter, required: false)] string? lineAfter = null,
        [Description(ToolParams.SnippetEdits)] List<SnippetEdit>? edits = null,
        [ToolOption(ToolOptionTag.ValidateOnApply)][Description(ToolParams.ValidateOnApply)] bool validateOnApply = true,
        [Description(ToolParams.ReturnDiff)][ToolOption(ToolOptionTag.ReturnDiff)] bool returnDiff = false,
        CancellationToken cancellationToken = default)
        => _fileEdit.ReplaceSnippet(reason, action, filePath, oldContent, newContent, lineBefore, lineAfter, edits, validateOnApply, returnDiff, cancellationToken);

    // CONDITIONAL-PARAM-REVIEW-REQUIRED: namespaceName/typeKind/typeName are required for a .cs
    // file (to seed a valid compilation unit) and ignored for every other file extension -> a model
    // creating a non-.cs file can omit all three, but a model creating a .cs file must supply all
    // three or the call fails, and nothing besides the description text signals that split.
    [McpServerTool(Name = "CreateFile")]
    [Produces(DataTag.ChangeId)]
    [Description("Creates a new file; fails if it already exists.")]
    public Task<SentinelCallToolResult<object>> CreateFile(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [Consumes(DataTag.SourceFilepath, required: true)] FilePathWrapper filePath,
        [Description("Required for .cs files: namespace to seed the file with.")] string? namespaceName = null,
        [Description("Required for .cs files: kind of top-level type to seed (class/interface/etc). Use staticClass for a static utility class.")] NewTypeKind? typeKind = null,
        [Description("Required for .cs files: name of the seeded top-level type.")] string? typeName = null,
        CancellationToken cancellationToken = default)
        => _fileEdit.CreateFile(reason, filePath, namespaceName, typeKind, typeName, cancellationToken);

    // The confirmationCode paramater was causing hallucinations and invalid tool calls. Reverted back to the original ApplyDiff tool but keeping this here (block-commented, since it depends
    // on ProposedChangeAction.confirmationCode, which is also commented out in ToolEnums.cs) in case we want to reintroduce ApplyDiff with a confirmationCode in the future.
    /*
    //[McpServerTool(Name = "ApplyDiffWithConfirmationCode")]
    [Produces(DataTag.ChangeId)]
    [Description("Applies or validates a change set. changesetFormat=files -> changes dict filePath->newContent (filePath not used). changesetFormat=diff -> filePath and unifiedDiff are BOTH REQUIRED (filePath names the single file the diff applies to; omitting it is a common mistake and fails immediately). For changesetFormat=diff, hunk line numbers are treated as a starting guess: if a hunk's declared position doesn't match, this searches nearby lines and re-anchors automatically, so modest line-number drift from an earlier edit to the same file is tolerated. Returns ApplyChangesResult with UndoChangeId on successful apply. The full pre-edit file content is NOT included by default (it's already captured for undo via UndoLastApply/GetOperationDetail) - pass returnDiff=true to get a unified-diff-style preview of what changed instead. IMPORTANT: for changesetFormat=files with action=apply, any file whose content would shrink by more than 50% is rejected with errorCode=ConfirmationRequired - this is a strong signal you submitted only a changed fragment as if it were the whole file, rather than a genuine whole-file rewrite. If the rewrite is really intended, call ApplyDiff again with action=confirmationCode and confirmationCode set to the code from the rejection - do not resend changes/filePath/unifiedDiff on that call, the original changeset is already cached server-side.")]
    public async Task<SentinelCallToolResult<object>> ApplyDiffWithConfirmationCode([ExternalInputRequired(DataTag.ChangeseFormat)] ChangesetFormat changesetFormat, [ExternalInputRequired(DataTag.Action)] ProposedChangeAction action, [ExternalInputRequired(DataTag.OperationId)] Dictionary<FilePathWrapper, string>? changes = null, [Consumes(DataTag.SourceFilepath, required: false)] string? filePath = null, [ToolOption(ToolOptionTag.UnifiedDiff)] string? unifiedDiff = null, [ToolOption(ToolOptionTag.RetryCount)] int retryCount = 3, [ToolOption(ToolOptionTag.ValidateOnApply)][Description(ToolParams.ValidateOnApply)] bool validateOnApply = true, [Description(ToolParams.ReturnDiff)][ToolOption(ToolOptionTag.ReturnDiff)] bool returnDiff = false, [ToolOption(ToolOptionTag.ConfirmationCode)][Description("Required when action=confirmationCode. The code returned by a prior apply call that was rejected for exceeding the whole-file-rewrite size threshold. Replays that exact cached changeset - do not also pass changes/filePath/unifiedDiff.")] string? confirmationCode = null, // RequestContext<CallToolRequestParams> requestParams = null,
    CancellationToken cancellationToken = default)
    {
        try
        {
            if (action == ProposedChangeAction.confirmationCode)
            {
                if (string.IsNullOrEmpty(confirmationCode))
                {
                    return new SentinelCallToolResult<object>()
                    {
                        IsSuccess = false,
                        ErrorData =  new ResultError(ToolErrorCode.InvalidArgument, "confirmationCode is required when action=confirmationCode.")
                    };
                }

                var pending = _workspaceManager.TakePendingChangeset(confirmationCode);
                if (pending == null)
                {
                    return new SentinelCallToolResult<object>()
                    {
                        IsSuccess = false,
                        ErrorData =  new ResultError(ToolErrorCode.InvalidArgument, $"confirmationCode '{confirmationCode}' is unrecognized or has expired (codes are single-use and expire after 10 minutes). Resubmit the original ApplyDiff(changesetFormat: files, action: apply, ...) call to get a fresh code.")
                    };
                }

                var confirmedResult = await _workspaceManager.ApplyProposedChangesAsync(pending.Value.Changes, pending.Value.RetryCount, validateChanges: pending.Value.ValidateOnApply);
                if (!confirmedResult.IsSuccess && confirmedResult.ValidationResult != null)
                    return new SentinelCallToolResult<object>()
                    {
                        IsSuccess = false,
                        ErrorData =  new ResultError(ToolErrorCode.Exception, $"ApplyDiff pre-apply validate failed: {confirmedResult.ValidationResult.Diagnostics.ToJson()}")
                    };
                await OperationBlobHelper.WriteBlobForApplyAsync(_logger, _workspaceManager, "apply_diff", confirmedResult);
                var strippedConfirmedResult = confirmedResult with { PreImages = null };
                object confirmedResponseData = returnDiff
                    ? new
                    {
                        result = strippedConfirmedResult,
                        diff = RefactoringTools.BuildDiffFromPreImages(pending.Value.Changes, confirmedResult.PreImages)
                    }
                    : strippedConfirmedResult;
                return new SentinelCallToolResult<object>()
                {
                    IsSuccess = true,
                    Data = confirmedResponseData
                };
            }

            FilePathWrapper filePathResolved = _workspaceManager.SetFilePath(filePath);
            if (changesetFormat == ChangesetFormat.files)
            {
                if (changes == null)
                {
                    return new SentinelCallToolResult<object>()
                    {
                        IsSuccess = false,
                        ErrorData =  new ResultError(ToolErrorCode.InvalidArgument, "changes is required when changesetFormat=files.")
                    };
                }

                if (action == ProposedChangeAction.apply)
                {
                    string? oversizedFile = null;
                    double oversizedPercent = 0;
                    foreach (var (changedPath, newContent) in changes)
                    {
                        var oldContent = await FileIoHelper.ReadAllTextIfExistsAsync(changedPath, cancellationToken);
                        var percentRemoved = PercentLinesRemoved(oldContent, newContent);
                        if (percentRemoved > LargeShrinkRejectionThreshold)
                        {
                            oversizedFile = changedPath;
                            oversizedPercent = percentRemoved;
                            break;
                        }
                    }

                    if (oversizedFile != null)
                    {
                        var code = _workspaceManager.CachePendingChangeset(changes, retryCount, validateOnApply);
                        return new SentinelCallToolResult<object>()
                        {
                            IsSuccess = false,
                            ErrorData =  new ResultError(ToolErrorCode.ConfirmationRequired,
                                $"File '{oversizedFile}' would shrink by {oversizedPercent:P0}, exceeding the {LargeShrinkRejectionThreshold:P0} threshold for a files-format apply. " +
                                "This usually means only a changed fragment was submitted instead of the complete file content - use changesetFormat=diff for a partial edit instead. " +
                                $"If a whole-file rewrite to this size is genuinely intended, call ApplyDiff again with action=confirmationCode and confirmationCode=\"{code}\" to apply the exact changeset just submitted (no need to resend changes). This code expires in 10 minutes.")
                        };
                    }

                    var result = await _workspaceManager.ApplyProposedChangesAsync(changes, retryCount, validateChanges: validateOnApply);
                    if (!result.IsSuccess && result.ValidationResult != null)
                        return new SentinelCallToolResult<object>()
                        {
                            IsSuccess = false,
                            ErrorData =  new ResultError(ToolErrorCode.Exception,
                                "ApplyDiff: the diff was valid and matched the target file, but the resulting code introduces new compiler errors - change not applied. Fix the issue(s) below and retry:\n[COMPILER ERROR]\n" +
                                await CompilerErrorLookupHelper.DescribeAsync(result.ValidationResult, _symbolNavigationEngine, cancellationToken))
                        };
                    await OperationBlobHelper.WriteBlobForApplyAsync(_logger, _workspaceManager, "apply_diff", result);
                    // PreImages (full pre-edit file content) is dropped from the default response -
                    // it's already captured in the undo blob written above (GetOperationDetail/
                    // UndoLastApply can retrieve it) and was the single largest contributor to
                    // ApplyDiff responses exceeding the calling harness's token limit on large files.
                    var strippedResult = result with { PreImages = null };
                    object responseData = returnDiff
                        ? new
                        {
                            result = strippedResult,
                            diff = RefactoringTools.BuildDiffFromPreImages(changes, result.PreImages)
                        }
                        : strippedResult;
                    return new SentinelCallToolResult<object>()
                    {
                        IsSuccess = true,
                        Data = responseData
                    };
                }

                if (action == ProposedChangeAction.validate)
                {
                    try
                    {
                        var validationResult = await _validationEngine.ValidateChangesAsync(changes);
                        return validationResult.IsSuccess ? new SentinelCallToolResult<object>()
                        {
                            IsSuccess = true,
                            Data = validationResult
                        }

                        : new SentinelCallToolResult<object>()
                        {
                            IsSuccess = false,
                            ErrorData =  new ResultError(ToolErrorCode.Exception, $"ApplyDiff validate failed: {validationResult.Diagnostics}")
                        };
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "ApplyDiff validate unexpected exception");
                        return new SentinelCallToolResult<object>()
                        {
                            IsSuccess = false,
                            Data =  ToolErrorMapper.ToResultError(ex, _workspaceManager, "ApplyDiff validate")
                        };
                    }
                }
            }
            else if (changesetFormat == ChangesetFormat.diff)
            {
                if (!filePathResolved.Validated && string.IsNullOrEmpty(unifiedDiff))
                {
                    return new SentinelCallToolResult<object>()
                    {
                        IsSuccess = false,
                        ErrorData =  new ResultError(ToolErrorCode.InvalidArgument, "ApplyDiff: both 'filePath' and 'unifiedDiff' are required when changesetFormat=diff.")
                    };
                }

                if (!filePathResolved.Validated)
                {
                    return new SentinelCallToolResult<object>()
                    {
                        IsSuccess = false,
                        ErrorData =  new ResultError(ToolErrorCode.InvalidArgument, "ApplyDiff: 'filePath' is required when changesetFormat=diff (it names the single file the unifiedDiff applies to). Only changesetFormat=files takes multiple files via 'changes'.")
                    };
                }

                if (string.IsNullOrEmpty(unifiedDiff))
                {
                    return new SentinelCallToolResult<object>()
                    {
                        IsSuccess = false,
                        ErrorData =  new ResultError(ToolErrorCode.InvalidArgument, "ApplyDiff: 'unifiedDiff' is required when changesetFormat=diff.")
                    };
                }

                if (action == ProposedChangeAction.apply)
                {
                    try
                    {
                        var solution = await _workspaceManager.GetCurrentSolutionAsync(cancellationToken);
                        var document = solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => d.Name == filePathResolved.Absolute || d.FilePathWrapper == filePathResolved.Absolute);
                        if (document == null)
                        {
                            return new SentinelCallToolResult<object>()
                            {
                                IsSuccess = false,
                                ErrorData =  new ResultError(ToolErrorCode.InvalidArgument, "File not found.")
                            };
                        }

                        var oldText = await document.GetTextAsync();
                        var newContent = _diffEngine.ApplyDiff(oldText, unifiedDiff).Text.ToString();
                        var targetPath = document.FilePathWrapper ?? filePath;
                        var diffChanges = new Dictionary<FilePathWrapper, string>
                        {
                            [targetPath] = newContent
                        };
                        var result = await _workspaceManager.ApplyProposedChangesAsync(diffChanges, validateChanges: validateOnApply);
                        if (!result.IsSuccess && result.ValidationResult != null)
                            return new SentinelCallToolResult<object>()
                            {
                                IsSuccess = false,
                                ErrorData =  new ResultError(ToolErrorCode.Exception,
                                    "ApplyDiff: the diff was valid and matched the target file, but the resulting code introduces new compiler errors - change not applied. Fix the issue(s) below and retry:\n[COMPILER ERROR]\n" +
                                    await CompilerErrorLookupHelper.DescribeAsync(result.ValidationResult, _symbolNavigationEngine, cancellationToken))
                            };
                        await OperationBlobHelper.WriteBlobForApplyAsync(_logger, _workspaceManager, "apply_diff", result);
                        var strippedDiffResult = result with { PreImages = null };
                        object diffResponseData = returnDiff
                            ? new
                            {
                                result = strippedDiffResult,
                                diff = RefactoringTools.BuildDiffFromPreImages(diffChanges, result.PreImages)
                            }
                            : strippedDiffResult;
                        return new SentinelCallToolResult<object>()
                        {
                            IsSuccess = true,
                            Data = diffResponseData
                        };
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "ApplyDiff diff apply unexpected exception for '{FilePathWrapper}'", filePathResolved);
                        return new SentinelCallToolResult<object>()
                        {
                            IsSuccess = false,
                            Data =  ToolErrorMapper.ToResultError(ex, _workspaceManager, $"ApplyDiff diff apply for '{filePathResolved}'")
                        };
                    }
                }

                if (action == ProposedChangeAction.validate)
                {
                    var validationResult = await _validationEngine.ValidateDiffAsync(filePathResolved.Absolute, unifiedDiff);
                    return validationResult.IsSuccess ? new SentinelCallToolResult<object>()
                    {
                        IsSuccess = true,
                        Data = validationResult
                    }

                    : new SentinelCallToolResult<object>()
                    {
                        IsSuccess = false,
                        ErrorData =  new ResultError(ToolErrorCode.Exception, $"ApplyDiff diff validate failed: {validationResult.Diagnostics.ToInfo()}")
                    };
                }
            }

            return new SentinelCallToolResult<object>()
            {
                IsSuccess = false,
                ErrorData =  new ResultError(ToolErrorCode.Exception, $"Unhandled changesetFormat '{changesetFormat}' / action '{action}'.")
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ApplyDiff ({ChangesetFormat}/{Action}) failed", changesetFormat, action);
            return new SentinelCallToolResult<object>()
            {
                IsSuccess = false,
                Data =  ToolErrorMapper.ToResultError(ex, _workspaceManager, "ApplyDiff")
            };
        }
    }
    */

    [McpServerTool(Name = "RetryFailedChanges")]
    [Produces(DataTag.ResultOnly)]
    [Description("Retries failed file writes using server-cached content.")]
    public Task<SentinelCallToolResult<object>> RetryFailedChanges(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [Consumes(DataTag.SourceFilepath, required: false)] List<string>? specificFiles = null,
        [ToolOption(ToolOptionTag.RetryCount)] int retryCount = 3,
        CancellationToken cancellationToken = default)
        => _fileEdit.RetryFailedChanges(reason, specificFiles, retryCount, cancellationToken);

    // WriteBlobForApplyAsync moved to OperationBlobHelper.WriteBlobForApplyAsync (Decision 7 step 1).
    [McpServerTool(Name = "GetDiagnostics")]
    [Produces(DataTag.Report)]
    [Description("Gets compiler diagnostics for a file, project, or the whole solution.")]
    public Task<SentinelCallToolResult<object>> GetDiagnostics(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [Description("file/project: also pass scopeName. solution: scopeName ignored.")]
        [Consumes(DataTag.ProjectName, required: true)][Consumes(DataTag.SourceFilepath, required: false)] ToolScope scope = ToolScope.solution,
        [Description("Required for scope=file/project (filePath or projectName). Ignored for solution.")]
        string? scopeName = null,
        [Description("Groups results by diagnostic ID with counts instead of a raw list.")]
        bool summarize = false,
        [Description("Caps the raw diagnostics list. Ignored when summarize=true.")]
        [ToolOptionAttribute(ToolOptionTag.ResultLimit)] int maxDetails = 50,
        [Description("Caps groups returned. Only used when summarize=true.")]
        [ToolOptionAttribute(ToolOptionTag.TopN)] int topN = 20,
        [Description("noBuild (default): diagnostics only. quickBuild/fullBuild: also runs a build check.")]
        BuildVerifyLevel verify = BuildVerifyLevel.noBuild,
        CancellationToken cancellationToken = default)
        => _buildTest.GetDiagnostics(reason, scope, scopeName, summarize, maxDetails, topN, verify, cancellationToken);
    [McpServerTool(Name = "Build")]
    [Produces(DataTag.Report)]
    [Description("Compiles the loaded solution and reports errors/warnings.")]
    public Task<SentinelCallToolResult<object>> Build(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        BuildVerifyLevel level = BuildVerifyLevel.fullBuild,
        ToolScope scope = ToolScope.solution,
        string? scopeName = null,
        [ToolOptionAttribute(ToolOptionTag.ResultLimit)] int maxDetails = 50,
        CancellationToken cancellationToken = default)
        => _buildTest.Build(reason, level, scope, scopeName, maxDetails, cancellationToken);

    [McpServerTool(Name = "RunTest")]
    [Produces(DataTag.Report)]
    [Description("Runs dotnet test and reports structured pass/fail results with failure grouping.")]
    public Task<SentinelCallToolResult<object>> RunTest(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        ToolScope scope = ToolScope.solution,
        string? scopeName = null,
        string? filter = null,
        TestResultsFilter resultsType = TestResultsFilter.failed,
        [ToolOptionAttribute(ToolOptionTag.ResultLimit)] int maxDetails = 50,
        int timeoutSeconds = 600,
        [Description("If true, omit the per-test Results list entirely (just counts + FailureSummary).")] bool summary = false,
        CancellationToken cancellationToken = default)
        => _buildTest.RunTest(reason, scope, scopeName, filter, resultsType, maxDetails, timeoutSeconds, summary, cancellationToken);
    // CONDITIONAL-PARAM-REVIEW-REQUIRED: none of projectName/docCommentId/symbolName/line/column is
    // individually required -> the tool needs exactly one full resolution strategy: (projectName +
    // docCommentId), or symbolName (optionally with contextSnippet/lineBefore/lineAfter), or
    // (line + column). Supplying an incomplete subset of any one strategy (e.g. line without column)
    // is accepted by the schema but rejected at runtime with InvalidArgument.
    [McpServerTool(Name = "SafeDeleteUnusedSymbol")]
    [Produces(DataTag.ResultOnly)]
    [Description("Deletes a symbol only if it has zero usages anywhere in the codebase.")]
    public Task<SentinelCallToolResult<object>> SafeDeleteUnusedSymbol(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [Consumes(DataTag.SourceFilepath, required: true)] FilePathWrapper filePath,
        [Description("Preferred, with docCommentId - the most reliable resolution path.")] string projectName = "",
        [Description("Preferred, with projectName - from LocateSymbol/FindReferences.")] string docCommentId = "",
        [Description("Fallback if projectName/docCommentId aren't available; combine with contextSnippet to disambiguate.")]
        [Consumes(DataTag.SymbolName, required: false)] string? symbolName = null,
        [Description(ToolParams.ContextSnippet)][ExternalInputRequired(DataTag.ContextSnippet, required: false)] string? contextSnippet = null,
        [Description(ToolParams.LineBefore)][ExternalInputRequired(DataTag.LineBefore, required: false)] string? lineBefore = null,
        [Description(ToolParams.LineAfter)][ExternalInputRequired(DataTag.LineAfter, required: false)] string? lineAfter = null,
        [Description("Legacy fallback: 1-based line of the declaration site. Requires column too.")]
        [Consumes(DataTag.StartLine, required: false)] int line = 0,
        [Description("Legacy fallback: 1-based column of the declaration site. Requires line too.")]
        [Consumes(DataTag.Offset, required: false)] int column = 0,
        CancellationToken cancellationToken = default)
        => _projectManagement.SafeDeleteUnusedSymbol(reason, filePath, projectName, docCommentId, symbolName, contextSnippet, lineBefore, lineAfter, line, column, cancellationToken);

    [McpServerTool(Name = "CreateProject")]
    [Produces(DataTag.ResultOnly)]
    [Description("Creates a new project and adds it to the loaded solution.")]
    public Task<SentinelCallToolResult<object>> CreateProject(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [ExternalInputRequired(DataTag.ProjectName, required: true)] string projectName,
        [ExternalInputRequired(DataTag.ProjectType)] string projectType = "console",
        CancellationToken cancellationToken = default)
        => _projectManagement.CreateProject(reason, projectName, projectType, cancellationToken);

    [McpServerTool(Name = "SplitProjectByFolder")]
    [Produces(DataTag.ResultOnly)]
    [Description("Moves all files under a folder from one project to a new target project, preserving structure.")]
    public Task<SentinelCallToolResult<object>> SplitProjectByFolder(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [Consumes(DataTag.ProjectName, required: true)] string sourceProjectName,
        [ExternalInputRequired(DataTag.ClassName, required: true)] string folderName,
        [ExternalInputRequired(DataTag.ProjectName, required: true)] string targetProjectName,
        CancellationToken cancellationToken = default)
        => _projectManagement.SplitProjectByFolder(reason, sourceProjectName, folderName, targetProjectName, cancellationToken);

    // ── Phase 1 -> Low-level fallback tools ──────────────────────────────────
    [McpServerTool(Name = "GetMethodSource")]
    [Produces(DataTag.SourceCode)]
    [Description("Returns a method's or constructor's full source text and attributes. For a constructor, pass the class name.")]
    public Task<SentinelCallToolResult<MethodSourceResult, ResultError>> GetMethodSource(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [Consumes(DataTag.SourceFilepath, required: true)] FilePathWrapper filePath, [Consumes(DataTag.MethodName, required: true)] string methodName, // RequestContext<CallToolRequestParams> requestParams = null,
        CancellationToken cancellationToken = default)
    {
        FilePathWrapper filePathResolved = FilePathWrapper.FromWire(filePath, _workspaceManager.GetSolutionRoot());
        return _readNav.GetMethodSource(reason, filePathResolved, methodName, cancellationToken);
    }

    [McpServerTool(Name = "ReadFile")]
    [Produces(DataTag.SourceCode)]
    [Description("Returns a file's raw text verbatim, or a 1-based line-range slice via startLine/endLine.")]
    public Task<SentinelCallToolResult<object>> ReadFile(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [Consumes(DataTag.SourceFilepath, required: true)] FilePathWrapper filePath,
        [Description("Omit to start from the first line.")] int? startLine = null,
        [Description("Omit to read through the last line.")] int? endLine = null,
        CancellationToken cancellationToken = default)
        => _fileEdit.ReadFile(reason, filePath, startLine, endLine, cancellationToken);

    [McpServerTool(Name = "GetFileOutline")]
    [Produces(DataTag.Report)]
    [Description("Returns a structural outline of a file's types and members with 1-based line ranges (no bodies).")]
    public Task<SentinelCallToolResult<FileOutlineResult, ResultError>> GetFileOutline(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [Consumes(DataTag.SourceFilepath, required: true)] FilePathWrapper filePath, // RequestContext<CallToolRequestParams> requestParams = null,
        CancellationToken cancellationToken = default)
    {
        FilePathWrapper filePathResolved = FilePathWrapper.FromWire(filePath, _workspaceManager.GetSolutionRoot());
        return _readNav.GetFileOutline(reason, filePathResolved, cancellationToken);
    }

    [McpServerTool(Name = "ListAll")]
    [Produces(DataTag.Report)]
    [Description("Lists every declared symbol in the loaded solution with file, kind, name, container, and line range. Call this first if you don't know an exact symbol name.")]
    public Task<SentinelCallToolResult<object>> ListAll(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [Description(ToolParams.ListAllKindValues)][ExternalInputRequired(DataTag.SymbolKind, required: false)] ListAllKind kind = ListAllKind.all,
        [Description("Restricts to one project. Omit for the whole solution.")]
        [Consumes(DataTag.ProjectName, required: false)] string? projectName = null,
        CancellationToken cancellationToken = default)
        => _readNav.ListAll(reason, kind, projectName, cancellationToken);
    [McpServerTool(Name = "SearchSolutionText")]
    [Produces(DataTag.Report)]
    [Produces(DataTag.FileList)]
    [Description("Searches solution source files for pattern, matched both as a literal substring and as a regex in one pass. Use LocateSymbol for known symbol names instead.")]
    public Task<SentinelCallToolResult<object>> SearchSolutionText(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [Description("Text to search for, matched as both literal substring and regex.")]
        [ToolOption(ToolOptionTag.Pattern, required: true)] string pattern,
        [Description("Restricts to matching file paths (glob). Omit for all files.")]
        [ExternalInputRequired(DataTag.SourceFilepath)] string? fileGlob = null,
        [Description("Caps total matches scanned.")]
        [ToolOptionAttribute(ToolOptionTag.ResultLimit)] int maxResults = 200, // RequestContext<CallToolRequestParams> requestParams = null,
        CancellationToken cancellationToken = default)
        => _readNav.SearchSolutionText(reason, pattern, fileGlob, maxResults, cancellationToken);
    [McpServerTool(Name = "GetOperationDetail")]
    [Produces(DataTag.ResultOnly)]
    [Description("Returns a filtered, paged slice of an operation result blob by changeId.")]
    public Task<SentinelCallToolResult<object>> GetOperationDetail(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [Consumes(DataTag.ChangeId, required: true)] string changeId,
        [Description("Filters items by outcome (failures/skipped/succeeded/rolledback/NeedsManualReview, with common synonyms) or by file:<path>. Omit for all.")]
        [ToolOptionAttribute(ToolOptionTag.Filter)] string? filter = null,
        [Description("Caps how many filtered items this page returns.")]
        [ToolOptionAttribute(ToolOptionTag.ResultLimit)] int maxItems = 50,
        [Description("Filtered items to skip. Pass NextOffset from the previous response to continue paging.")]
        [ToolOptionAttribute(ToolOptionTag.Offset)] int offset = 0, // RequestContext<CallToolRequestParams> requestParams = null,
        CancellationToken cancellationToken = default)
        => _readNav.GetOperationDetail(reason, changeId, filter, maxItems, offset, cancellationToken);
    [McpServerTool(Name = "UndoLastApply")]
    [Produces(DataTag.ResultOnly)]
    [Description("Reverts files from a previous apply back to their pre-apply state.")]
    public Task<SentinelCallToolResult<object>> UndoLastApply(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [Consumes(DataTag.OperationId, required: true)] string changeId,
        CancellationToken cancellationToken = default)
        => _fileEdit.UndoLastApply(reason, changeId, cancellationToken);

    // ── 8. GetWorkspaceHealth ─────────────────────────────────────────────────
    [McpServerTool(Name = "GetWorkspaceHealth")]
    [Produces(DataTag.ResultOnly)]
    [Description("Reports live workspace/solution health: loaded state, project/document counts, and staleness.")]
    public Task<SentinelCallToolResult<WorkspaceHealthReport, ResultError>> GetWorkspaceHealth(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        BuildVerifyLevel verify = BuildVerifyLevel.noBuild,
        CancellationToken cancellationToken = default)
        => _healthMisc.GetWorkspaceHealth(reason, verify, cancellationToken);

    [McpServerTool(Name = "ListProjectFrameworkTargets")]
    [Produces(DataTag.Report)]
    [Description("Returns each project's TargetFramework value.")]
    public Task<SentinelCallToolResult<List<ProjectFrameworkSummary>, ResultError>> ListProjectFrameworkTargets(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        CancellationToken cancellationToken = default)
        => _projectManagement.ListProjectFrameworkTargets(reason, cancellationToken);

    // ── get_large_result ────────────────────────────────────────────────────────

    [McpServerTool(Name = "GetLargeResult")]
    [Produces(DataTag.Report)]
    [Description("Pages through a large result written to disk after exceeding the inline size threshold.")]
    public Task<SentinelCallToolResult<object>> GetLargeResult(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        // CONDITIONAL-PARAM-REVIEW-REQUIRED: exactly one of resultId/filePath must be supplied;
        // neither is individually required but the tool fails if both are omitted.
        [Description("The resultId from the original truncated result. Required if filePath is omitted.")]
        [Consumes(DataTag.ResultId)] string? resultId = null,
        [Description("Path to a largeresult_*.json file. Required if resultId is omitted.")]
        [Consumes(DataTag.SourceFilepath, required: false)] string? filePath = null,
        [Description("Max records to return. Ignored for Raw/text results - use charLimit.")]
        [ToolOption(ToolOptionTag.ResultLimit)] int limit = 50,
        [Description("Records (or characters, for text results) to skip before the next page.")]
        [ToolOption(ToolOptionTag.Offset)] int offset = 0,
        [Description("Max characters to return for a Raw/text result. Other result types use limit instead.")]
        [ToolOption(ToolOptionTag.CharLimit)] int? charLimit = null,
        CancellationToken cancellationToken = default)
    {
        FilePathWrapper filePathResolved = FilePathWrapper.FromWire(filePath, _workspaceManager.GetSolutionRoot());
        return _readNav.GetLargeResult(reason, resultId, filePathResolved, limit, offset, charLimit: charLimit, cancellationToken: cancellationToken);
    }
}
