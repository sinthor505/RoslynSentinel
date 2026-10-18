using System.ComponentModel;

using Microsoft.Extensions.Logging;

using RoslynSentinel.Engines.Basic;

namespace RoslynSentinel.Tools.Basic;
/// <summary>
/// God-class MCP surface for workspace tools. Six of its tool methods -> GetMethodSourceTool,
/// GetFileOutlineTool, ListAllTool, SearchSolutionTextTool, GetOperationDetailTool, GetLargeResultTool -> are thin
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

    private readonly WorkspaceReadNavigationTools _readNavTools;

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
        _readNavTools = new WorkspaceReadNavigationTools(readNav, _symbolNavigation, _symbolRelationship);
    }
    [McpServerTool(Name = "Features")]
    [Produces(DataTag.Report)]
    [Description("Queries or updates feature flags.")]
    public Task<SentinelCallToolResult<object>> McpServerFeaturesTool(
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
        => _healthMisc.McpServerFeaturesControlTool(reason, action, names, enabled, delaySeconds, cancellationToken);
    [UnrecoverableBreaker(UnrecoverableBreakerAccess.Allowed)]
    [McpServerTool(Name = "ListSolutionItems")]
    [Produces(DataTag.FileList)]
    [Produces(DataTag.ProjectList)]
    [Produces(DataTag.DependencyList)]
    [Description("Lists projects, files, dependencies, or solution-folder items in the loaded solution.")]
    public Task<SentinelCallToolResult<object>> ListSolutionItemsTool(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [Description("files/dependencies: requires projectName. projects/solutionItems: list every project or solution-folder item (solutionItems aren't searchable via SearchSolutionText - use ProjectDoc). all: everything in one call.")]
        [ExternalInputRequired(DataTag.Scope)] SolutionItemsKind kind,
        [Description("Required for kind=files/dependencies.")]
        [Consumes(DataTag.ProjectName)] string? projectName = null,
        CancellationToken cancellationToken = default)
        => _projectManagement.ListSolutionItemsTool(reason, kind, projectName, cancellationToken);

    /// <summary>
    /// Hard cap on how many files ListWorkspaceSolutionsTool will walk before giving up -> protects
    /// against a caller passing an overly broad root (see the drive-root guard below) that isn't
    /// caught by that check but still turns out to contain far more than any real workspace would
    /// (e.g. a node_modules-style tree, or a root one level above the intended one).
    /// </summary>
    private const int ListWorkspaceSolutionsMaxFilesWalked = 200_000;
    [McpServerTool(Name = "ListWorkspaceSolutions")]
    [Produces(DataTag.FileList)]
    [Produces(DataTag.SolutionList)]
    [Description("Lists .sln/.slnx files under a directory, for use with LoadSolution.")]
    public SentinelCallToolResult<List<SolutionFileInfo>, ResultError> ListWorkspaceSolutionsTool(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [Description("A real project/repo directory, not a drive root.")] string workspacePath,
        CancellationToken cancellationToken = default)
        => _projectManagement.ListWorkspaceSolutionsTool(reason, workspacePath, cancellationToken);
    // current directory, --base-repo-dir (if set), or the server's install directory.
    [McpServerTool(Name = "LoadSolution")]
    [Produces(DataTag.ResultOnly)]
    [Description("Loads a .NET solution into memory. Required before any operation needing a loaded solution. If a load is already in progress (for example the start-up auto-load) this call waits for it instead of reloading; timeoutSeconds bounds that wait and any load this call starts, and on timeout that load is cancelled and the error names timeoutSeconds.")]
    public Task<SentinelCallToolResult<object>> LoadSolutionTool(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [Consumes(DataTag.SolutionFilepath, required: true)] string solutionPath,
        [ToolOption(ToolOptionTag.RepoDirectory)][Description("Base directory for resolving a relative solutionPath. Must exist on this host - omit rather than guess.")] string? baseRepoDir = null,
        [Description("true forces a full reload from disk, discarding in-memory state, when this solution is already loaded.")] bool forceReload = false,
        [Description("Seconds to wait for a solution load (1-3600, default 30). Covers waiting for a load already running (for example the start-up auto-load) and a load this call starts; a load this call starts is cancelled when it expires.")] int timeoutSeconds = 30,
        CancellationToken cancellationToken = default)
        => _projectManagement.LoadSolutionTool(reason, solutionPath, baseRepoDir, forceReload, timeoutSeconds, cancellationToken);

    // ListExternalDiskChanges/AcknowledgeExternalFileChanges moved to AdminTools.cs,
    // gated behind the "Admin" mode -> see docs/current/ideas/external-drift-hard-blocker.md.
    // Not model-visible by default anymore; reconciliation is an out-of-band operator action now.

    // ApplyUnifiedDiffTool moved to WholeFileWriteTools.cs (gated off the default MCP surface,
    // alongside ApplyDiffTool) -> see docs/current/design_applyunifieddiff_replace_snippet_v1.md.
    // ReplaceSnippetTool (below, on this default surface) replaces it for small, exact-text batchEdits.

    // ReplaceSnippetTool/ReplaceSnippetBatch/CreateFileTool moved to WorkspaceFileEditTools/
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
    [Description("Replaces one exact text block with another in a file, for small localized edits. Prefer a structural Roslyn tool for structural changes. By default the edited project(s) and their dependents are delta-compiled BEFORE writing, and the change is rejected if it introduces a new compiler error. For edits larger than the size limit or whole-member rewrites, use Member(operation: replace) instead.")]
    public Task<SentinelCallToolResult<ReplaceSnippetResult>> ReplaceSnippetTool(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [Description("apply: writes the change. validate: checks it would apply cleanly without writing.")]
        [ExternalInputRequired(DataTag.Action)] ProposedChangeAction action,
        // CONDITIONAL-PARAM-REVIEW-REQUIRED: required only when 'batchEdits' is omitted -> see the either/or check below.
        [Consumes(DataTag.SourceFilepath, required: false)][Description("File to edit. Do not combine with batchEdits; each batch edit carries its own filePath.")] string? filePath = null,
        [ToolOption(ToolOptionTag.OldContent, required: false)][Description(ToolParams.OldContent)] string? oldContent = null,
        [ToolOption(ToolOptionTag.NewContent, required: false)][Description(ToolParams.NewContent)] string? newContent = null,
        [Description(ToolParams.LineBefore)][ExternalInputRequired(DataTag.LineBefore, required: false)] string? lineBefore = null,
        [Description(ToolParams.LineAfter)][ExternalInputRequired(DataTag.LineAfter, required: false)] string? lineAfter = null,
        [Description(ToolParams.ReplaceSnippetWithinMember)] string? withinMember = null,
        [Description(ToolParams.ReplaceSnippetMatchLine)] int? matchLine = null,
        [Description(ToolParams.SnippetEdits)] List<SnippetEdit>? batchEdits = null,
        [ToolOption(ToolOptionTag.ValidateOnApply)][Description(ToolParams.ValidateOnApply)] bool validateOnApply = true,
        [Description(ToolParams.ReturnDiff)][ToolOption(ToolOptionTag.ReturnDiff)] bool returnDiff = false,
        CancellationToken cancellationToken = default)
        => _fileEdit.ReplaceSnippetTool(reason, action, filePath, oldContent, newContent, lineBefore, lineAfter, withinMember, matchLine, batchEdits, validateOnApply, returnDiff, cancellationToken);

    [McpServerTool(Name = "CreateFile")]
    [Produces(DataTag.ChangeId)]
    [Description("Creates a new file; fails if it already exists. For a new file with its full content in one call, use WriteFile(operation: CreateFile, content: ...).")]
    public Task<SentinelCallToolResult<object>> CreateFileTool(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [Consumes(DataTag.SourceFilepath, required: true)] string filePath,
        [Description("Required for .cs files: namespace to seed the file with.")] string? namespaceName = null,
        [Description("Required for .cs files: kind of top-level type to seed (class/interface/etc). Use staticClass for a static utility class.")] NewTypeKind? typeKind = null,
        [Description("Required for .cs files: name of the seeded top-level type.")] string? typeName = null,
        CancellationToken cancellationToken = default)
        => _fileEdit.CreateFileTool(reason, filePath, namespaceName, typeKind, typeName, cancellationToken);

    // The confirmationCode paramater was causing hallucinations and invalid tool calls. Reverted back to the original ApplyDiffTool tool but keeping this here (block-commented, since it depends
    // on ProposedChangeAction.confirmationCode, which is also commented out in ToolEnums.cs) in case we want to reintroduce ApplyDiffTool with a confirmationCode in the future.
    /*
    //[McpServerTool(Name = "ApplyDiffWithConfirmationCode")]
    [Produces(DataTag.ChangeId)]
    [Description("Applies or validates a change set. changesetFormat=files -> changes dict filePath->newContent (filePath not used). changesetFormat=diff -> filePath and unifiedDiff are BOTH REQUIRED (filePath names the single file the diff applies to; omitting it is a common mistake and fails immediately). For changesetFormat=diff, hunk line numbers are treated as a starting guess: if a hunk's declared position doesn't match, this searches nearby lines and re-anchors automatically, so modest line-number drift from an earlier edit to the same file is tolerated. Returns ApplyChangesResult with UndoChangeId on successful apply. The full pre-edit file content is NOT included by default (it's already captured for undo via UndoLastApplyTool/GetOperationDetailTool) - pass returnDiff=true to get a unified-diff-style preview of what changed instead. IMPORTANT: for changesetFormat=files with action=apply, any file whose content would shrink by more than 50% is rejected with errorCode=ConfirmationRequired - this is a strong signal you submitted only a changed fragment as if it were the whole file, rather than a genuine whole-file rewrite. If the rewrite is really intended, call ApplyDiffTool again with action=confirmationCode and confirmationCode set to the code from the rejection - do not resend changes/filePath/unifiedDiff on that call, the original changeset is already cached server-side.")]
    public async Task<SentinelCallToolResult<object>> ApplyDiffWithConfirmationCodeTool([ExternalInputRequired(DataTag.ChangeseFormat)] ChangesetFormat changesetFormat, [ExternalInputRequired(DataTag.Action)] ProposedChangeAction action, [ExternalInputRequired(DataTag.OperationId)] Dictionary<FilePathWrapper, string>? changes = null, [Consumes(DataTag.SourceFilepath, required: false)] string? filePath = null, [ToolOption(ToolOptionTag.UnifiedDiff)] string? unifiedDiff = null, [ToolOption(ToolOptionTag.RetryCount)] int retryCount = 3, [ToolOption(ToolOptionTag.ValidateOnApply)][Description(ToolParams.ValidateOnApply)] bool validateOnApply = true, [Description(ToolParams.ReturnDiff)][ToolOption(ToolOptionTag.ReturnDiff)] bool returnDiff = false, [ToolOption(ToolOptionTag.ConfirmationCode)][Description("Required when action=confirmationCode. The code returned by a prior apply call that was rejected for exceeding the whole-file-rewrite size threshold. Replays that exact cached changeset - do not also pass changes/filePath/unifiedDiff.")] string? confirmationCode = null, // RequestContext<CallToolRequestParams> requestParams = null,
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
                        ErrorData =  new ResultError(ToolErrorCode.InvalidArgument, $"confirmationCode '{confirmationCode}' is unrecognized or has expired (codes are single-use and expire after 10 minutes). Resubmit the original ApplyDiffTool(changesetFormat: files, action: apply, ...) call to get a fresh code.")
                    };
                }

                var confirmedResult = await _workspaceManager.ApplyProposedChangesAsync(pending.Value.Changes, pending.Value.RetryCount, validateChanges: pending.Value.ValidateOnApply);
                if (!confirmedResult.IsSuccess && confirmedResult.ValidationResult != null)
                    return new SentinelCallToolResult<object>()
                    {
                        IsSuccess = false,
                        ErrorData =  new ResultError(ToolErrorCode.Exception, $"ApplyDiffTool pre-apply validate failed: {confirmedResult.ValidationResult.Diagnostics.ToJson()}")
                    };
                await OperationBlobHelper.WriteBlobForApplyAsync(_logger, _workspaceManager, "apply_diff", confirmedResult);
                var strippedConfirmedResult = confirmedResult with { PreImages = null };
                object confirmedResponseData = returnDiff
                    ? new
                    {
                        result = strippedConfirmedResult,
                        diff = RefactoringStructuralTools.BuildDiffFromPreImages(pending.Value.Changes, confirmedResult.PreImages)
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
                                $"If a whole-file rewrite to this size is genuinely intended, call ApplyDiffTool again with action=confirmationCode and confirmationCode=\"{code}\" to apply the exact changeset just submitted (no need to resend changes). This code expires in 10 minutes.")
                        };
                    }

                    var result = await _workspaceManager.ApplyProposedChangesAsync(changes, retryCount, validateChanges: validateOnApply);
                    if (!result.IsSuccess && result.ValidationResult != null)
                        return new SentinelCallToolResult<object>()
                        {
                            IsSuccess = false,
                            ErrorData =  new ResultError(ToolErrorCode.Exception,
                                "ApplyDiffTool: the diff was valid and matched the target file, but the resulting code introduces new compiler errors - change not applied. Fix the issue(s) below and retry:\n[COMPILER ERROR]\n" +
                                await CompilerErrorLookupHelper.DescribeAsync(result.ValidationResult, _symbolNavigationEngine, cancellationToken))
                        };
                    await OperationBlobHelper.WriteBlobForApplyAsync(_logger, _workspaceManager, "apply_diff", result);
                    // PreImages (full pre-edit file content) is dropped from the default response -
                    // it's already captured in the undo blob written above (GetOperationDetailTool/
                    // UndoLastApplyTool can retrieve it) and was the single largest contributor to
                    // ApplyDiffTool responses exceeding the calling harness's token limit on large files.
                    var strippedResult = result with { PreImages = null };
                    object responseData = returnDiff
                        ? new
                        {
                            result = strippedResult,
                            diff = RefactoringStructuralTools.BuildDiffFromPreImages(changes, result.PreImages)
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
                            ErrorData =  new ResultError(ToolErrorCode.Exception, $"ApplyDiffTool validate failed: {validationResult.Diagnostics}")
                        };
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "ApplyDiffTool validate unexpected exception");
                        return new SentinelCallToolResult<object>()
                        {
                            IsSuccess = false,
                            Data =  ToolErrorMapper.ToResultError(ex, _workspaceManager, "ApplyDiffTool validate")
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
                        ErrorData =  new ResultError(ToolErrorCode.InvalidArgument, "ApplyDiffTool: both 'filePath' and 'unifiedDiff' are required when changesetFormat=diff.")
                    };
                }

                if (!filePathResolved.Validated)
                {
                    return new SentinelCallToolResult<object>()
                    {
                        IsSuccess = false,
                        ErrorData =  new ResultError(ToolErrorCode.InvalidArgument, "ApplyDiffTool: 'filePath' is required when changesetFormat=diff (it names the single file the unifiedDiff applies to). Only changesetFormat=files takes multiple files via 'changes'.")
                    };
                }

                if (string.IsNullOrEmpty(unifiedDiff))
                {
                    return new SentinelCallToolResult<object>()
                    {
                        IsSuccess = false,
                        ErrorData =  new ResultError(ToolErrorCode.InvalidArgument, "ApplyDiffTool: 'unifiedDiff' is required when changesetFormat=diff.")
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
                        var newContent = _diffEngine.ApplyDiffTool(oldText, unifiedDiff).Text.ToString();
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
                                    "ApplyDiffTool: the diff was valid and matched the target file, but the resulting code introduces new compiler errors - change not applied. Fix the issue(s) below and retry:\n[COMPILER ERROR]\n" +
                                    await CompilerErrorLookupHelper.DescribeAsync(result.ValidationResult, _symbolNavigationEngine, cancellationToken))
                            };
                        await OperationBlobHelper.WriteBlobForApplyAsync(_logger, _workspaceManager, "apply_diff", result);
                        var strippedDiffResult = result with { PreImages = null };
                        object diffResponseData = returnDiff
                            ? new
                            {
                                result = strippedDiffResult,
                                diff = RefactoringStructuralTools.BuildDiffFromPreImages(diffChanges, result.PreImages)
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
                        _logger.LogError(ex, "ApplyDiffTool diff apply unexpected exception for '{FilePathWrapper}'", filePathResolved);
                        return new SentinelCallToolResult<object>()
                        {
                            IsSuccess = false,
                            Data =  ToolErrorMapper.ToResultError(ex, _workspaceManager, $"ApplyDiffTool diff apply for '{filePathResolved}'")
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
                        ErrorData =  new ResultError(ToolErrorCode.Exception, $"ApplyDiffTool diff validate failed: {validationResult.Diagnostics.ToInfo()}")
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
            _logger.LogError(ex, "ApplyDiffTool ({ChangesetFormat}/{Action}) failed", changesetFormat, action);
            return new SentinelCallToolResult<object>()
            {
                IsSuccess = false,
                Data =  ToolErrorMapper.ToResultError(ex, _workspaceManager, "ApplyDiffTool")
            };
        }
    }
    */

    [McpServerTool(Name = "RetryFailedChanges")]
    [Produces(DataTag.ResultOnly)]
    [Description("Retries failed file writes using server-cached content.")]
    public Task<SentinelCallToolResult<object>> RetryFailedChangesTool(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [Consumes(DataTag.SourceFilepath, required: false)] List<string>? specificFiles = null,
        [ToolOption(ToolOptionTag.RetryCount)] int retryCount = 3,
        CancellationToken cancellationToken = default)
        => _fileEdit.RetryFailedChangesTool(reason, specificFiles, retryCount, cancellationToken);

    // WriteBlobForApplyAsync moved to OperationBlobHelper.WriteBlobForApplyAsync (Decision 7 step 1).
    [McpServerTool(Name = "GetDiagnostics")]
    [Produces(DataTag.Report)]
    [Description("Gets compiler diagnostics for a file, project, or the whole solution.")]
    public Task<SentinelCallToolResult<object>> GetDiagnosticsTool(
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
        => _buildTest.GetDiagnosticsTool(reason, scope, scopeName, summarize, maxDetails, topN, verify, cancellationToken);
    [McpServerTool(Name = "Build")]
    [Produces(DataTag.Report)]
    [Description("Compiles the loaded solution and reports the result. level=quickBuild uses in-memory Roslyn diagnostics (fast, same check GetDiagnostics does). level=fullBuild shells out to `dotnet build` (slower, catches MSBuild-only failures - NuGet restore, resource copy, post-build events - that quickBuild can't see). A green build returns the outcome, the projects compiled and the error/warning counts only. A failed build returns the error counts by project, by file and by diagnostic code, the first maxDetails (default 20) errors with the root-cause project's errors first, and SuppressedDownstreamErrorCount (errors in projects that failed only because a project they depend on failed). OmittedErrorCount says how many errors are not listed; FullDiagnosticsResultId holds every error and warning - read it with GetLargeResult. The stdout/stderr tails and the warnings list are left out unless you pass includeOutput / includeWarnings.")]
    public Task<SentinelCallToolResult<object>> BuildTool(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        BuildVerifyLevel level = BuildVerifyLevel.fullBuild,
        ToolScope scope = ToolScope.solution,
        string? scopeName = null,
        [ToolOptionAttribute(ToolOptionTag.ResultLimit)] int maxDetails = 20,
        [Description("Only used by level=fullBuild. " + ToolParams.UseScratchDir)] bool useScratchDir = false,
        [Description("true = also return the stdout/stderr tail of the build, even when it succeeded or its errors were parsed. Default false: counts and errors only.")] bool includeOutput = false,
        [Description("true = also return the warnings list and warning summary, capped by maxDetails. Default false: only the warning count.")] bool includeWarnings = false,
        CancellationToken cancellationToken = default)
        => _buildTest.BuildTool(reason, level, scope, scopeName, maxDetails, useScratchDir, includeOutput, includeWarnings, cancellationToken);

    [McpServerTool(Name = "RunTest")]
    [Produces(DataTag.Report)]
    [Description("Runs `dotnet test` against the loaded solution (or a single project) and reports structured results. Returns TotalCount/PassedCount/FailedCount/SkippedCount, a FailureSummary grouping failures by message signature (e.g. \"45 of 50 failures share one cause\") so an agent doesn't have to paginate to notice a pattern, and a capped Results list (filtered by resultsType, then capped by maxDetails). Each failed test entry also carries Output: the test's captured output (for example NUnit TestContext.Out / Console.Out or xUnit ITestOutputHelper), the last 2000 characters, present only for failed tests. resultsType defaults to \"failed\" so a clean run stays a short summary with no per-test list; pass \"all\" to see every test's outcome. Set summary=true to omit the Results list entirely (just counts + FailureSummary), regardless of resultsType. filter is passed through to `dotnet test --filter` - an unresolvable filter expression is a distinct error from a filter that resolves but matches zero tests. With scope=solution a filter still builds and probes every test project (about 1.5 minutes for 13 projects); pass scope=project and scopeName to run one project in seconds.")]
    public Task<SentinelCallToolResult<object>> RunTestTool(
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
        => _buildTest.RunTestTool(reason, scope, scopeName, filter, resultsType, maxDetails, timeoutSeconds, summary, useScratchDir, cancellationToken);
    // CONDITIONAL-PARAM-REVIEW-REQUIRED: none of projectName/docCommentId/symbolName/line/column is
    // individually required -> the tool needs exactly one full resolution strategy: (projectName +
    // docCommentId), or symbolName (optionally with contextSnippet/lineBefore/lineAfter), or
    // (line + column). Supplying an incomplete subset of any one strategy (e.g. line without column)
    // is accepted by the schema but rejected at runtime with InvalidArgument.
    [McpServerTool(Name = "DeleteUnusedSymbol")]
    [Produces(DataTag.ResultOnly)]
    [Description("Deletes a symbol only if it has zero usages anywhere in the codebase.")]
    public Task<SentinelCallToolResult<object>> DeleteUnusedSymbolTool(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [Consumes(DataTag.SourceFilepath, required: true)] string filePath,
        [Description("Preferred, with docCommentId.")] string projectName = "",
        [Description("Preferred, with projectName (from Search, mode: symbol or references).")] string docCommentId = "",
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
        => _projectManagement.DeleteUnusedSymbolTool(reason, filePath, projectName, docCommentId, symbolName, contextSnippet, lineBefore, lineAfter, line, column, cancellationToken);

    [McpServerTool(Name = "CreateProject")]
    [Produces(DataTag.ResultOnly)]
    [Description("Creates a new project and adds it to the loaded solution.")]
    public Task<SentinelCallToolResult<object>> CreateProjectTool(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [ExternalInputRequired(DataTag.ProjectName, required: true)] string projectName,
        [ExternalInputRequired(DataTag.ProjectType)] string projectType = "console",
        CancellationToken cancellationToken = default)
        => _projectManagement.CreateProjectTool(reason, projectName, projectType, cancellationToken);

    [McpServerTool(Name = "SplitProjectByFolder")]
    [Produces(DataTag.ResultOnly)]
    [Description("Moves all files under a folder from one project to a new target project, preserving structure.")]
    public Task<SentinelCallToolResult<object>> SplitProjectByFolderTool(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [Consumes(DataTag.ProjectName, required: true)] string sourceProjectName,
        [ExternalInputRequired(DataTag.ClassName, required: true)] string folderName,
        [ExternalInputRequired(DataTag.ProjectName, required: true)] string targetProjectName,
        CancellationToken cancellationToken = default)
        => _projectManagement.SplitProjectByFolderTool(reason, sourceProjectName, folderName, targetProjectName, cancellationToken);

    // ── Phase 1 -> Low-level fallback tools ──────────────────────────────────
    // Non-tool delegator: the GetMethodSource [McpServerTool] lives on WorkspaceReadNavigationTools.
    public Task<SentinelCallToolResult<MethodSourceResult, ResultError>> GetMethodSourceTool(
        ToolCallReason reason, string filePath, string methodName, CancellationToken cancellationToken = default)
        => _readNavTools.GetMethodSourceTool(reason, filePath, methodName, cancellationToken);

    [UnrecoverableBreaker(UnrecoverableBreakerAccess.Allowed)]
    [McpServerTool(Name = "ReadFile")]
    [Produces(DataTag.SourceCode)]
    [Description("Returns a file's raw text verbatim, or a 1-based line-range slice via startLine/endLine. To read a single member, pass memberName (optionally containerName).")]
    public Task<SentinelCallToolResult<object>> ReadFileTool(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [Consumes(DataTag.SourceFilepath, required: true)] string filePath,
        [Description("Omit to start from the first line.")] int? startLine = null,
        [Description("Omit to read through the last line.")] int? endLine = null,
        [Consumes(DataTag.SymbolName, required: false)][Description("Read just this member's source (method, property, field, constructor) instead of the whole file. Cannot be combined with startLine/endLine.")] string? memberName = null,
        [Consumes(DataTag.SymbolName, required: false)][Description("Only with memberName: the type that holds the member, to disambiguate same-named members.")] string? containerName = null,
        CancellationToken cancellationToken = default)
        => _fileEdit.ReadFileTool(reason, filePath, startLine, endLine, memberName, containerName, cancellationToken);

    // Non-tool delegator: the GetFileOutline [McpServerTool] lives on WorkspaceReadNavigationTools.
    public Task<SentinelCallToolResult<FileOutlineResult, ResultError>> GetFileOutlineTool(
        ToolCallReason reason, string filePath, CancellationToken cancellationToken = default)
        => _readNavTools.GetFileOutlineTool(reason, filePath, cancellationToken);

    // Non-tool delegator: the ListAll [McpServerTool] lives on WorkspaceReadNavigationTools.
    public Task<SentinelCallToolResult<object>> ListAllTool(
        ToolCallReason reason, ListAllKind kind = ListAllKind.all, string? projectName = null, bool includeTests = false,
        CancellationToken cancellationToken = default)
        => _readNavTools.ListAllTool(reason, kind, projectName, includeTests, cancellationToken);

    /// <summary>
    /// Non-tool delegator kept so existing callers of <c>WorkspaceTools.SearchSolutionTool</c> keep compiling.
    /// The Search tool itself (the [McpServerTool] attribute) lives on <see cref="WorkspaceReadNavigationTools"/>.
    /// </summary>
    public Task<SentinelCallToolResult<object>> SearchSolutionTool(
        ToolCallReason reason, SearchMode mode, string? query = null, string? fileGlob = null, int maxResults = 200,
        SymbolKindFilter symbolKind = SymbolKindFilter.any, string? containingType = null, string? containingNamespace = null,
        string? projectName = null, bool exactMatch = true, string? filePath = null, string? contextSnippet = null,
        string? lineBefore = null, string? lineAfter = null, bool includeTests = false, string? hasAttribute = null,
        CancellationToken cancellationToken = default)
        => _readNavTools.SearchSolutionTool(reason, mode, query, fileGlob, maxResults, symbolKind, containingType, containingNamespace, projectName, exactMatch, filePath, contextSnippet, lineBefore, lineAfter, includeTests, hasAttribute, cancellationToken);

    // Non-tool delegator: the GetOperationDetail [McpServerTool] lives on WorkspaceReadNavigationTools.
    public Task<SentinelCallToolResult<object>> GetOperationDetailTool(
        ToolCallReason reason, string changeId, string? filter = null, int maxItems = 50, int offset = 0,
        CancellationToken cancellationToken = default)
        => _readNavTools.GetOperationDetailTool(reason, changeId, filter, maxItems, offset, cancellationToken);

    [McpServerTool(Name = "UndoLastApply")]
    [Produces(DataTag.ResultOnly)]
    [Description("Reverts files from a previous apply back to their pre-apply state.")]
    public Task<SentinelCallToolResult<object>> UndoLastApplyTool(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [Consumes(DataTag.OperationId, required: true)] string changeId,
        CancellationToken cancellationToken = default)
        => _fileEdit.UndoLastApplyTool(reason, changeId, cancellationToken);

    // ── 8. GetWorkspaceHealthTool ─────────────────────────────────────────────────
    [UnrecoverableBreaker(UnrecoverableBreakerAccess.Allowed)]
    [McpServerTool(Name = "GetWorkspaceHealth")]
    [Produces(DataTag.ResultOnly)]
    [Description("Reports live workspace/solution health: loaded state, project/document counts, and staleness.")]
    public Task<SentinelCallToolResult<WorkspaceHealthReport, ResultError>> GetWorkspaceHealthTool(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        BuildVerifyLevel verify = BuildVerifyLevel.noBuild,
        CancellationToken cancellationToken = default)
        => _healthMisc.GetWorkspaceHealthTool(reason, verify, cancellationToken);

    [McpServerTool(Name = "ListProjectFrameworkTargets")]
    [Produces(DataTag.Report)]
    [Description("Returns each project's TargetFramework value.")]
    public Task<SentinelCallToolResult<List<ProjectFrameworkSummary>, ResultError>> ListProjectFrameworkTargetsTool(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        CancellationToken cancellationToken = default)
        => _projectManagement.ListProjectFrameworkTargetsTool(reason, cancellationToken);

    // ── get_large_result ────────────────────────────────────────────────────────

    // Non-tool delegator: the GetLargeResult [McpServerTool] lives on WorkspaceReadNavigationTools.
    public Task<SentinelCallToolResult<object>> GetLargeResultTool(
        ToolCallReason reason, string? resultId = null, string? filePath = null, int limit = 50, int offset = 0,
        int? charLimit = null, CancellationToken cancellationToken = default)
        => _readNavTools.GetLargeResultTool(reason, resultId, filePath, limit, offset, charLimit, cancellationToken);

    private readonly SymbolNavigationImpl _symbolNavigation;

    private readonly SymbolRelationshipImpl _symbolRelationship;
}
