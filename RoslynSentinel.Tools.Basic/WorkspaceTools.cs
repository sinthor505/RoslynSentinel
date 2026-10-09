using System.ComponentModel;

using Microsoft.Extensions.Logging;

using RoslynSentinel.Engines.Basic;

namespace RoslynSentinel.Tools.Basic;
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

    private readonly WorkspaceProjectManagementTools _projectManagement;

    private readonly WorkspaceBuildTestTools _buildTest;

    private readonly WorkspaceFileEditTools _fileEdit;

    private readonly WorkspaceHealthMiscTools _healthMisc;

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
        _projectManagement = new WorkspaceProjectManagementTools(new WorkspaceProjectManagementImpl(workspaceManager, solutionManagementEngine, dependencyEngine, projectConsistencyEngine, structuralRefinementEngine, logger));
        _buildTest = new WorkspaceBuildTestTools(new WorkspaceBuildTestImpl(workspaceManager, diagnosticEngine, buildEngine, testRunEngine, logger));
        _fileEdit = new WorkspaceFileEditTools(new WorkspaceFileEditImpl(workspaceManager, readNav, logger, validationEngine, symbolNavigationEngine, writeAdvice));
        _healthMisc = new WorkspaceHealthMiscTools(new WorkspaceHealthMiscImpl(workspaceManager, config, buildEngine, logger));

        // Search's mode:symbol/references dispatch targets. Built internally (not taken as ctor
        // params) so the many existing `new WorkspaceTools(...)` call sites across the test suite
        // don't all need updating for a dependency only Search uses.
        _symbolNavigation = new SymbolNavigationImpl(symbolNavigationEngine, new ImpactAnalyzer(workspaceManager), workspaceManager, logger);
        _symbolRelationship = new SymbolRelationshipImpl(new DiscoveryEngine(workspaceManager), symbolNavigationEngine, workspaceManager, logger);
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
    [UnrecoverableBreaker(UnrecoverableBreakerAccess.Allowed)]
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

    // ApplyUnifiedDiff moved to WholeFileWriteTools.cs (gated off the default MCP surface,
    // alongside ApplyDiff) -> see docs/current/design_applyunifieddiff_replace_snippet_v1.md.
    // ReplaceSnippet (below, on this default surface) replaces it for small, exact-text batchEdits.

    // ReplaceSnippet/ReplaceSnippetBatch/CreateFile moved to WorkspaceFileEditTools/
    // WorkspaceFileEditImpl (Decision 7 step 2 facade split). This class keeps its original
    // constructor/tool signatures, delegating internally.
    [McpServerTool(Name = "ReplaceSnippet")]
    [SupportsBatching]
    [Produces(DataTag.ChangeId)]
    // Deliberately names no whole-file-write tool. Attribute arguments must be compile-time
    // constants, so this text can't be generated per-session from the tool registry the way the
    // over-cap error can (see WriteToolAdviceHelper) -> and a hardcoded name here would be shown to
    // the model on every single call even when that tool is gated off, which is the run-398 failure
    // in its most persistent form. The error path is where the redirect is actually needed.
    [Description("Replaces one exact text block with another in a file, for small localized edits. Prefer a structural Roslyn tool for structural changes. By default the edited project(s) and their dependents are delta-compiled BEFORE writing, and the change is rejected if it introduces a new compiler error.")]
    public Task<SentinelCallToolResult<ReplaceSnippetResult>> ReplaceSnippet(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [Description("apply: writes the change. validate: checks it would apply cleanly without writing.")]
        [ExternalInputRequired(DataTag.Action)] ProposedChangeAction action,
        // CONDITIONAL-PARAM-REVIEW-REQUIRED: required only when 'batchEdits' is omitted -> see the either/or check below.
        [Consumes(DataTag.SourceFilepath, required: false)] string? filePath = null,
        [ToolOption(ToolOptionTag.OldContent, required: false)][Description(ToolParams.OldContent)] string? oldContent = null,
        [ToolOption(ToolOptionTag.NewContent, required: false)][Description(ToolParams.NewContent)] string? newContent = null,
        [Description(ToolParams.LineBefore)][ExternalInputRequired(DataTag.LineBefore, required: false)] string? lineBefore = null,
        [Description(ToolParams.LineAfter)][ExternalInputRequired(DataTag.LineAfter, required: false)] string? lineAfter = null,
        [Description(ToolParams.SnippetEdits)] List<SnippetEdit>? batchEdits = null,
        [ToolOption(ToolOptionTag.ValidateOnApply)][Description(ToolParams.ValidateOnApply)] bool validateOnApply = true,
        [Description(ToolParams.ReturnDiff)][ToolOption(ToolOptionTag.ReturnDiff)] bool returnDiff = false,
        CancellationToken cancellationToken = default)
        => _fileEdit.ReplaceSnippet(reason, action, filePath, oldContent, newContent, lineBefore, lineAfter, batchEdits, validateOnApply, returnDiff, cancellationToken);

    [McpServerTool(Name = "CreateFile")]
    [Produces(DataTag.ChangeId)]
    [Description("Creates a new file; fails if it already exists.")]
    public Task<SentinelCallToolResult<object>> CreateFile(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [Consumes(DataTag.SourceFilepath, required: true)] string filePath,
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

            FilePathWrapper filePathResolved = _workspaceManager.ResolveFromWire(filePath);
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
                        var solution = await _workspaceManager.GetSolutionAsync(ReadSource.Committed, cancellationToken);
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
        [Description("quickBuild/fullBuild also run a build check.")]
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
        [Description("Only used by level=fullBuild. " + ToolParams.UseScratchDir)] bool useScratchDir = false,
        CancellationToken cancellationToken = default)
        => _buildTest.Build(reason, level, scope, scopeName, maxDetails, useScratchDir, cancellationToken);

    [McpServerTool(Name = "RunTest")]
    [Produces(DataTag.Report)]
    [Description("Runs `dotnet test` against the loaded solution (or a single project) and reports structured results. Returns TotalCount/PassedCount/FailedCount/SkippedCount, a FailureSummary grouping failures by message signature (e.g. \"45 of 50 failures share one cause\") so an agent doesn't have to paginate to notice a pattern, and a capped Results list (filtered by resultsType, then capped by maxDetails). resultsType defaults to \"failed\" so a clean run stays a short summary with no per-test list; pass \"all\" to see every test's outcome. Set summary=true to omit the Results list entirely (just counts + FailureSummary), regardless of resultsType. filter is passed through to `dotnet test --filter` - an unresolvable filter expression is a distinct error from a filter that resolves but matches zero tests. With scope=solution a filter still builds and probes every test project (about 1.5 minutes for 13 projects); pass scope=project and scopeName to run one project in seconds.")]
    public Task<SentinelCallToolResult<object>> RunTest(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [Description("solution (default) runs every test project one after another; project runs only the project named in scopeName; file is not supported.")] ToolScope scope = ToolScope.solution,
        [Description("Project name (for example RoslynSentinel.Tests.Basic) when scope=project; not a parameter called projectName.")] string? scopeName = null,
        [Description("Passed through to `dotnet test --filter`. With scope=solution a filter still builds and probes every test project (about 1.5 minutes for 13 projects); pass scope=project and scopeName to run one project in seconds.")] string? filter = null,
        TestResultsFilter resultsType = TestResultsFilter.failed,
        [ToolOptionAttribute(ToolOptionTag.ResultLimit)] int maxDetails = 50,
        int timeoutSeconds = 600,
        [Description("If true, omit the per-test Results list entirely (just counts + FailureSummary).")] bool summary = false,
        [Description(ToolParams.UseScratchDir)] bool useScratchDir = false,
        CancellationToken cancellationToken = default)
        => _buildTest.RunTest(reason, scope, scopeName, filter, resultsType, maxDetails, timeoutSeconds, summary, useScratchDir, cancellationToken);
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
        [Consumes(DataTag.SourceFilepath, required: true)] string filePath,
        [Description("Preferred, with docCommentId.")] string projectName = "",
        [Description("Preferred, with projectName (from LocateSymbol/FindReferences).")] string docCommentId = "",
        [Description("Fallback; add contextSnippet to disambiguate.")]
        [Consumes(DataTag.SymbolName, required: false)] string? symbolName = null,
        [Description(ToolParams.ContextSnippet)][ExternalInputRequired(DataTag.ContextSnippet, required: false)] string? contextSnippet = null,
        [Description(ToolParams.LineBefore)][ExternalInputRequired(DataTag.LineBefore, required: false)] string? lineBefore = null,
        [Description(ToolParams.LineAfter)][ExternalInputRequired(DataTag.LineAfter, required: false)] string? lineAfter = null,
        [Description("Fallback: 1-based declaration line; needs column.")]
        [Consumes(DataTag.StartLine, required: false)] int line = 0,
        [Description("Fallback: 1-based declaration column; needs line.")]
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
        [Consumes(DataTag.SourceFilepath, required: true)] string filePath, [Consumes(DataTag.MethodName, required: true)] string methodName, // RequestContext<CallToolRequestParams> requestParams = null,
        CancellationToken cancellationToken = default)
    {
        FilePathWrapper filePathResolved = _workspaceManager.ResolveFromWire(filePath);
        return _readNav.GetMethodSource(reason, filePathResolved, methodName, cancellationToken);
    }

    [UnrecoverableBreaker(UnrecoverableBreakerAccess.Allowed)]
    [McpServerTool(Name = "ReadFile")]
    [Produces(DataTag.SourceCode)]
    [Description("Returns a file's raw text verbatim, or a 1-based line-range slice via startLine/endLine.")]
    public Task<SentinelCallToolResult<object>> ReadFile(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [Consumes(DataTag.SourceFilepath, required: true)] string filePath,
        [Description("Omit to start from the first line.")] int? startLine = null,
        [Description("Omit to read through the last line.")] int? endLine = null,
        CancellationToken cancellationToken = default)
        => _fileEdit.ReadFile(reason, filePath, startLine, endLine, cancellationToken);

    [UnrecoverableBreaker(UnrecoverableBreakerAccess.Allowed)]
    [McpServerTool(Name = "GetFileOutline")]
    [Produces(DataTag.Report)]
    [Description("Returns a structural outline of a file's types and members with 1-based line ranges (no bodies).")]
    public Task<SentinelCallToolResult<FileOutlineResult, ResultError>> GetFileOutline(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [Consumes(DataTag.SourceFilepath, required: true)] string filePath, // RequestContext<CallToolRequestParams> requestParams = null,
        CancellationToken cancellationToken = default)
    {
        FilePathWrapper filePathResolved = _workspaceManager.ResolveFromWire(filePath);
        return _readNav.GetFileOutline(reason, filePathResolved, cancellationToken);
    }

    [UnrecoverableBreaker(UnrecoverableBreakerAccess.Allowed)]
    [McpServerTool(Name = "ListAll")]
    [Produces(DataTag.Report)]
    [Description("Lists every declared symbol in the loaded solution with file, kind, name, container, and line range. Call this first if you don't know an exact symbol name.")]
    public Task<SentinelCallToolResult<object>> ListAll(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [Description(ToolParams.ListAllKindValues)][ExternalInputRequired(DataTag.SymbolKind, required: false)] ListAllKind kind = ListAllKind.all,
        [Description("Restricts to one project. Omit for the whole solution.")]
        [Consumes(DataTag.ProjectName, required: false)] string? projectName = null,
        CancellationToken cancellationToken = default)
        => _readNav.ListAll(reason, kind, projectName, cancellationToken: cancellationToken);

    [McpServerTool(Name = "Search")]
    [Produces(DataTag.Report)]
    [Produces(DataTag.FileList)]
    [Produces(DataTag.DocCommentId)]
    [Produces(DataTag.ProjectName)]
    [Description("Unified search. mode selects what's searched: text (free-text/regex scan), symbol (declaration lookup by name), references (callers/implementations of a symbol), or a declaration-kind listing (all, namespace, class, interface, method, property, struct, record, enum, enum member, constructor, field). query: mode text = pattern matched as both a literal and a regex (use fileGlob, not a path parameter, to restrict files); symbol/references = symbol name; declaration-kind modes = optional case-insensitive name filter (exact name unless exactMatch is false). Declaration-kind listings return at most 30 rows: when more match, totalRecords holds the full match count, hasMoreData is true, listSummary gives per-project (byProject) and top-10 per-file (byFile, truncatedFileCount) match counts over ALL matches, and warningDetails says the list was truncated - narrow with query or projectName, or use mode: symbol. Zero matches is not an error: it returns an empty result (isError false, totalRecords 0) whose statusMessage says what to try next.")]
    public Task<SentinelCallToolResult<object>> SearchSolution(
    [Description(ToolParams.Reason)] ToolCallReason reason,
    SearchMode mode,
    [Description("mode: text = pattern matched as both a literal and a regex; symbol/references = symbol name; declaration-kind modes = optional case-insensitive name filter (see exactMatch). Omit in declaration-kind modes only if you want up to 30 rows of that kind.")]
        string? query = null,
    [Description("mode: text only. Glob restricting file paths (omit for all files). " +
        "Supports '*' (within one path segment), '**' (any depth), '?' (one char), " +
        "'{a,b,...}' alternation, and '[abc]'/'[!abc]' character classes. A glob " +
        "containing '/' matches the solution-relative path (e.g. '**/Foo.cs', 'MyProj/*.cs'); a " +
        "glob with no '/' matches the bare filename only (e.g. '*.cs', 'Foo.cs').")]
        [ExternalInputRequired(DataTag.SourceFilepath)] string? fileGlob = null,
    [Description("mode: text only. Caps total matches scanned.")]
        [ToolOptionAttribute(ToolOptionTag.ResultLimit)] int maxResults = 200,
    [Description("mode: symbol only. Restricts to one kind of symbol.")]
        [ExternalInputRequired(DataTag.SymbolKind)] SymbolKindFilter symbolKind = SymbolKindFilter.any,
    [Description("mode: symbol only. Restricts to symbols declared inside this type.")]
        [ExternalInputRequired(DataTag.ContainingType)] string? containingType = null,
    [Description("mode: symbol only. Restricts to symbols declared inside this namespace.")]
        [ExternalInputRequired(DataTag.ContainingNamespace)] string? containingNamespace = null,
    [Description("Restricts to one project (symbol/all/declaration-kind modes).")]
        [Consumes(DataTag.ProjectName, required: false)] string? projectName = null,
    [Description("mode: symbol and declaration-kind modes. true (default) = exact name match; false = substring (contains) match.")]
        [ToolOption(ToolOptionTag.MatchType)] bool exactMatch = true,
    [Description("mode: references only (required). callers: call sites; implementations: overrides/interface implementations; all: both.")]
        [Consumes(DataTag.SymbolKind)] FindReferencesKind? referencesKind = null,
    [Description("mode: symbol/references only. Pins resolution when the name is ambiguous across files. Ignored in text mode - use fileGlob.")]
        [Consumes(DataTag.SourceFilepath, required: false)] string? filePath = null,
    [Description("Required for mode: references. " + ToolParams.ContextSnippet)]
        [Consumes(DataTag.ContextSnippet, required: false)] string? contextSnippet = null,
    [Description(ToolParams.LineBefore)][ExternalInputRequired(DataTag.LineBefore)] string? lineBefore = null,
    [Description(ToolParams.LineAfter)][ExternalInputRequired(DataTag.LineAfter)] string? lineAfter = null,
    CancellationToken cancellationToken = default)
    => DispatchSearch(reason, mode, query, fileGlob, maxResults, symbolKind, containingType, containingNamespace, projectName, exactMatch, referencesKind, filePath, contextSnippet, lineBefore, lineAfter, cancellationToken);

    /// <summary>
    /// Hard cap on rows returned by Search in declaration-kind modes; extra matches are counted in totalRecords and flagged via hasMoreData/warningDetails.
    /// </summary>
    public const int KindListingMaxItems = 30;

    private static ListAllKind SearchModeToListAllKind(SearchMode mode) => mode switch
    {
        SearchMode.all => ListAllKind.all,
        SearchMode.@namespace => ListAllKind.@namespace,
        SearchMode.@class => ListAllKind.@class,
        SearchMode.@interface => ListAllKind.@interface,
        SearchMode.method => ListAllKind.method,
        SearchMode.property => ListAllKind.property,
        SearchMode.@struct => ListAllKind.@struct,
        SearchMode.record => ListAllKind.record,
        SearchMode.@enum => ListAllKind.@enum,
        SearchMode.enumMember => ListAllKind.enumMember,
        SearchMode.constructor => ListAllKind.constructor,
        SearchMode.field => ListAllKind.field,
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unhandled SearchMode in declaration-listing dispatch.")
    };

    private Task<SentinelCallToolResult<object>> DispatchSearch(
    ToolCallReason reason, SearchMode mode, string? query, string? fileGlob, int maxResults,
    SymbolKindFilter symbolKind, string? containingType, string? containingNamespace, string? projectName,
    bool exactMatch, FindReferencesKind? referencesKind, string? filePath, string? contextSnippet,
    string? lineBefore, string? lineAfter, CancellationToken cancellationToken)
    {
        switch (mode)
        {
            case SearchMode.text:
                return _readNav.SearchSolutionText(reason, query ?? string.Empty, fileGlob, maxResults, cancellationToken);
            case SearchMode.symbol:
                return _symbolNavigation.LocateSymbol(reason, query ?? string.Empty, symbolKind, containingType, containingNamespace, projectName, filePath, exactMatch, cancellationToken);
            case SearchMode.references:
                if (string.IsNullOrEmpty(contextSnippet))
                {
                    return Task.FromResult(new SentinelCallToolResult<object>
                    {
                        IsError = true,
                        ErrorData = new ResultError(ToolErrorCode.InvalidArgument, "contextSnippet is required for mode: references - pass a short unique verbatim fragment identifying the target symbol.")
                    });
                }
                if (referencesKind is null)
                {
                    return Task.FromResult(new SentinelCallToolResult<object>
                    {
                        IsError = true,
                        ErrorData = new ResultError(ToolErrorCode.InvalidArgument, "referencesKind is required for mode: references - pass \"callers\", \"implementations\", or \"all\".")
                    });
                }
                return _symbolRelationship.FindReferences(reason, query ?? string.Empty, referencesKind.Value, filePath, contextSnippet, lineBefore, lineAfter, cancellationToken);
            default:
                return _readNav.ListAll(reason, SearchModeToListAllKind(mode), projectName, query, exactMatch, KindListingMaxItems, cancellationToken);
        }
    }
    [UnrecoverableBreaker(UnrecoverableBreakerAccess.Allowed)]
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
    [UnrecoverableBreaker(UnrecoverableBreakerAccess.Allowed)]
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
        FilePathWrapper filePathResolved = _workspaceManager.ResolveFromWire(filePath);
        return _readNav.GetLargeResult(reason, resultId, filePathResolved, limit, offset, charLimit: charLimit, cancellationToken: cancellationToken);
    }

    private readonly SymbolNavigationImpl _symbolNavigation;

    private readonly SymbolRelationshipImpl _symbolRelationship;
}
