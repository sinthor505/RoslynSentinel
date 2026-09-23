using Microsoft.Extensions.Logging;

using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace RoslynSentinel.Basic;

// Decision 7 step 3 (plan_split_workspace_refactoring_tools_for_di.md): implementation half of the
// RefactoringExtractionDocsTools/Impl pair. Method bodies below are moved verbatim from
// RefactoringTools.cs (GenerateMapping, UsingDirective, SummaryComment, ExtractLocalVariable,
// ExtractMethodSafe). ValidateAndApplyAsync is duplicated per-Impl-class per Decision 1-Amendment.
public class RefactoringExtractionDocsImpl
{
    private readonly RefactoringEngine _refactoringEngine;
    private readonly MsToolAugmentEngine _msToolAugmentEngine;
    private readonly MappingEngine _mappingEngine;
    private readonly SymbolNavigationEngine _symbolNavigationEngine;
    private readonly IWorkspaceManager _workspaceManager;
    private readonly ValidationEngine _validationEngine;
    private readonly ILogger _logger;

    public RefactoringExtractionDocsImpl(
        RefactoringEngine refactoringEngine,
        MsToolAugmentEngine msToolAugmentEngine,
        MappingEngine mappingEngine,
        SymbolNavigationEngine symbolNavigationEngine,
        IWorkspaceManager workspaceManager,
        ValidationEngine validationEngine,
        ILogger logger)
    {
        _refactoringEngine = refactoringEngine;
        _msToolAugmentEngine = msToolAugmentEngine;
        _mappingEngine = mappingEngine;
        _symbolNavigationEngine = symbolNavigationEngine;
        _workspaceManager = workspaceManager;
        _validationEngine = validationEngine;
        _logger = logger;
    }

    /// <summary>
    /// Validates proposed changes against the current in-memory solution and, unless
    /// <paramref name="dryRun"/> is set, writes them straight to disk (write-through -> no
    /// intermediate staging step). Rolls back any already-written files if a multi-file change
    /// partially fails, so a change never lands half-applied.
    /// </summary>
    private Task<ApplyOutcome> ValidateAndApplyAsync(
        Dictionary<FilePathWrapper, string> changes,
        string description,
        string operationName,
        bool dryRun = false,
        bool returnDiff = false,
        IProgress<ProgressNotificationValue>? progress = default,
        IReadOnlyCollection<FilePathWrapper>? removePaths = null,
        CancellationToken cancellationToken = default) =>
        ValidateAndApplyHelper.ValidateAndApplyAsync(
            _validationEngine, _workspaceManager, _logger, changes, operationName,
            dryRun, returnDiff, progress, removePaths, cancellationToken,
            describeValidationFailure: (report, ct) => CompilerErrorLookupHelper.DescribeAsync(report, _symbolNavigationEngine, ct));

    public async Task<SentinelCallToolResult<object>> GenerateMapping(
        FilePathWrapper filepath,
        string fromType,
        string toType,
        bool dryRun = false,
        bool returnDiff = false,
        RequestContext<CallToolRequestParams>? requestParams = null,
        CancellationToken cancellationToken = default)
    {
        FilePathWrapper filePathResolved = FilePathWrapper.FromWire(filepath, _workspaceManager.GetSolutionRoot());
        try
        {
            ProgressToken progressToken = requestParams?.Params?.ProgressToken ?? new ProgressToken();
            IProgress<ProgressNotificationValue> progress = new Progress<ProgressNotificationValue>(msg => requestParams?.Server?.NotifyProgressAsync(progressToken, new ProgressNotificationValue() { Progress = 10.0f }, null, cancellationToken));

            var result = await _mappingEngine.GenerateMappingAsync(filePathResolved, fromType, toType, cancellationToken);
            if (string.IsNullOrEmpty(result.UpdatedText))
                return new SentinelCallToolResult<object> { IsSuccess = false, ErrorData = new ResultError(ToolErrorCode.Exception, $"GenerateMapping produced no output for '{fromType}' -> '{toType}' in '{filePathResolved}'. Ensure both types exist in the solution.") };

            var changes = new Dictionary<FilePathWrapper, string> { [filePathResolved] = result.UpdatedText };
            var apply = await ValidateAndApplyAsync(changes, $"Generate mapping from '{fromType}' to '{toType}'.", "GenerateMapping", dryRun, returnDiff, progress, cancellationToken: cancellationToken);
            if (apply.Error is not null)
                return new SentinelCallToolResult<object> { IsSuccess = false, ErrorData = apply.Error };
            return new SentinelCallToolResult<object> { IsSuccess = true, SuccessData = new AppliedChangeSummary(apply.ChangeId, [filePathResolved], $"Generated mapping from '{fromType}' to '{toType}' in {Path.GetFileName(filePathResolved)}.", apply.DryRun, apply.Diff) };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GenerateMapping failed for '{FromType}' to '{ToType}' in '{FilePathWrapper}'", fromType, toType, filePathResolved);
            return new SentinelCallToolResult<object>() { IsSuccess = false, ErrorData = ToolErrorMapper.ToResultError(ex, _workspaceManager, "GenerateMapping") };
        }
    }

    public async Task<SentinelCallToolResult<object>> UsingDirective(
        ToolCallReason reason,
        FilePathWrapper filepath,
        AddRemoveViewAction operation,
        string? namespaceName = null,
        bool simplifyExisting = false,
        bool autoStage = true,
        bool dryRun = false,
        bool returnDiff = false,
        CancellationToken cancellationToken = default)
    {
        FilePathWrapper filePathResolved = FilePathWrapper.FromWire(filepath, _workspaceManager.GetSolutionRoot());
        try
        {
            if (operation == AddRemoveViewAction.view)
            {
                var usings = await _refactoringEngine.GetUsingDirectivesAsync(filePathResolved, cancellationToken);
                return new SentinelCallToolResult<object>() { IsSuccess = true, SuccessData = new UsingDirectiveViewResult(usings) };
            }

            if (string.IsNullOrEmpty(namespaceName))
            {
                return new SentinelCallToolResult<object>() { IsSuccess = false, ErrorData = new ResultError(ToolErrorCode.InvalidArgument, $"UsingDirective: namespaceName is required for operation '{operation}'.") };
            }

            DocumentEditResult updated;
            string opName;
            if (operation == AddRemoveViewAction.add)
            {
                updated = await _refactoringEngine.AddUsingDirectiveAsync(filePathResolved, namespaceName, simplifyExisting, cancellationToken);
                opName = "Add";
            }
            else
            {
                updated = await _refactoringEngine.RemoveUsingDirectiveAsync(filePathResolved, namespaceName, cancellationToken);
                opName = "Remove";
            }

            if (!autoStage)
            {
                var noStageDescription = operation == AddRemoveViewAction.add
                    ? $"Adds 'using {namespaceName};' to {Path.GetFileName(filePathResolved)}."
                    : $"Removes 'using {namespaceName};' from {Path.GetFileName(filePathResolved)}.";
                var noStageChanges = string.IsNullOrEmpty(updated.UpdatedText)
                    ? new Dictionary<FilePathWrapper, string>()
                    : new Dictionary<FilePathWrapper, string> { [filePathResolved] = updated.UpdatedText! };
                return new SentinelCallToolResult<object>()
                {
                    IsSuccess = true,
                    SuccessData = new AppliedChangeSummary(
                        ChangeId: null,
                        AffectedFiles: noStageChanges.Keys.ToList(),
                        Description: noStageDescription,
                        DryRun: false,
                        Diff: null,
                        ChangedContent: noStageChanges.Count == 0 ? null : noStageChanges,
                        Validated: false)
                };
            }

            if (RefactoringToolHelpers.RequireUpdatedText(updated, "UsingDirective", filePathResolved) is { } guardResult)
                return guardResult;

            var changes = new Dictionary<FilePathWrapper, string> { [filePathResolved] = updated.UpdatedText! };
            // returnDiff is forced to true here regardless of the caller's own returnDiff flag:
            // the add path below needs the real before/after diff to populate ChangedContent, not
            // just to decide whether to include a preview Diff in the summary.
            var needsDiff = returnDiff || operation == AddRemoveViewAction.add;
            var apply = await ValidateAndApplyAsync(changes, $"{opName} using {namespaceName}.", "UsingDirective", dryRun, needsDiff, cancellationToken: cancellationToken);
            if (apply.Error is not null)
                return new SentinelCallToolResult<object> { IsSuccess = false, ErrorData = apply.Error };
            var description = operation == AddRemoveViewAction.add
                ? $"Adds 'using {namespaceName};' to {Path.GetFileName(filePathResolved)}."
                : $"Removes 'using {namespaceName};' from {Path.GetFileName(filePathResolved)}.";
            if (operation != AddRemoveViewAction.add)
            {
                return new SentinelCallToolResult<object>() { IsSuccess = true, StatusMessage = description, SuccessData = new AppliedChangeSummary(apply.ChangeId, [filePathResolved], description, apply.DryRun, returnDiff ? apply.Diff : null, _workspaceManager.WorkspaceVersion, ChangedContent: changes, Validated: true) };
            }

            // Diff is populated from the actual before/after document diff (apply.Diff, built by
            // ValidateAndApplyHelper.BuildDiffFromPreImages) unconditionally here - NOT gated on
            // the caller's own returnDiff flag, unlike the non-add branch above. needsDiff already
            // forced ValidateAndApplyAsync to compute apply.Diff for the add path regardless of
            // returnDiff (see the comment above needsDiff); surfacing it unconditionally is what
            // used to be guaranteed by the now-retired MemberChangedContentResult wrapper's own
            // ChangedContent field. A hardcoded "using {namespaceName};" could never reveal any
            // other change bundled into the same write (formatting drift, accessibility changes,
            // whitespace normalization, etc.) -> the caller had no way to know from this tool's own
            // result whether something unexpected also changed.
            return await SentinelCallToolResult<object>.ForPossiblyLargeDataAsync(
                new AppliedChangeSummary(apply.ChangeId, [filePathResolved], description, apply.DryRun, apply.Diff, _workspaceManager.WorkspaceVersion, ChangedContent: changes, Validated: true),
                _workspaceManager.GetSolutionRoot(), "AppliedChangeSummary", ResultWrapperType.AppliedChangeSummaryResult,
                workspaceVersion: _workspaceManager.WorkspaceVersion, statusMessage: description);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "UsingDirective failed for '{Namespace}' in '{FilePathWrapper}'", namespaceName, filePathResolved);
            return new SentinelCallToolResult<object>() { IsSuccess = false, ErrorData = ToolErrorMapper.ToResultError(ex, _workspaceManager, "UsingDirective") };
        }
    }

    public async Task<SentinelCallToolResult<object>> SummaryComment(
        ToolCallReason reason,
        FilePathWrapper filepath,
        AddRemoveViewAction operation,
        string targetName,
        string? summaryText = null,
        string? contextSnippet = null,
        string? lineBefore = null,
        string? lineAfter = null,
        string? containingTypeName = null,
        bool autoStage = true,
        bool dryRun = false,
        bool returnDiff = false,
        CancellationToken cancellationToken = default)
    {
        FilePathWrapper filePathResolved = FilePathWrapper.FromWire(filepath, _workspaceManager.GetSolutionRoot());
        try
        {
            if (operation == AddRemoveViewAction.view)
            {
                var (outcome, message, text) = await _refactoringEngine.GetSummaryCommentAsync(filePathResolved, targetName, contextSnippet, lineBefore, lineAfter, containingTypeName, cancellationToken);
                if (outcome is EditOutcome.DocumentNotFound or EditOutcome.CannotEdit)
                    return new SentinelCallToolResult<object>() { IsSuccess = false, ErrorData = new ResultError(ToolErrorCode.Exception, $"SummaryComment: {message}") };
                return new SentinelCallToolResult<object>() { IsSuccess = true, SuccessData = new SummaryCommentViewResult(text) };
            }

            if (operation == AddRemoveViewAction.add && string.IsNullOrEmpty(summaryText))
            {
                return new SentinelCallToolResult<object>() { IsSuccess = false, ErrorData = new ResultError(ToolErrorCode.InvalidArgument, "SummaryComment: summaryText is required for operation 'add'.") };
            }

            var updated = operation == AddRemoveViewAction.add
                ? await _refactoringEngine.AddSummaryCommentAsync(filePathResolved, targetName, summaryText!, contextSnippet, lineBefore, lineAfter, containingTypeName)
                : await _refactoringEngine.RemoveSummaryCommentAsync(filePathResolved, targetName, contextSnippet, lineBefore, lineAfter, containingTypeName, cancellationToken);

            if (!autoStage)
            {
                var noStageDescription = operation == AddRemoveViewAction.add
                    ? $"Added XML summary comment to '{targetName}' in {Path.GetFileName(filePathResolved)}."
                    : $"Removed XML summary comment from '{targetName}' in {Path.GetFileName(filePathResolved)}.";
                var noStageChanges = string.IsNullOrEmpty(updated.UpdatedText)
                    ? new Dictionary<FilePathWrapper, string>()
                    : new Dictionary<FilePathWrapper, string> { [filePathResolved] = updated.UpdatedText! };
                return new SentinelCallToolResult<object>()
                {
                    IsSuccess = true,
                    SuccessData = new AppliedChangeSummary(
                        ChangeId: null,
                        AffectedFiles: noStageChanges.Keys.ToList(),
                        Description: noStageDescription,
                        DryRun: false,
                        Diff: null,
                        ChangedContent: noStageChanges.Count == 0 ? null : noStageChanges,
                        Validated: false)
                };
            }

            if (RefactoringToolHelpers.RequireUpdatedText(updated, "SummaryComment", filePathResolved) is { } guardResult)
                return guardResult;

            var description = operation == AddRemoveViewAction.add
                ? $"Added XML summary comment to '{targetName}' in {Path.GetFileName(filePathResolved)}."
                : $"Removed XML summary comment from '{targetName}' in {Path.GetFileName(filePathResolved)}.";

            var changes = new Dictionary<FilePathWrapper, string> { [filePathResolved] = updated.UpdatedText! };
            var apply = await ValidateAndApplyAsync(changes, description, "SummaryComment", dryRun, returnDiff, cancellationToken: cancellationToken);
            if (apply.Error is not null)
                return new SentinelCallToolResult<object> { IsSuccess = false, ErrorData = apply.Error };
            var summary = new AppliedChangeSummary(apply.ChangeId, [filePathResolved], description, apply.DryRun, apply.Diff, ChangedContent: changes, Validated: true);

            // add: summaryText is caller-supplied verbatim, echoed back as the added content
            // (same reasoning as Member(add)'s raw-source path). remove has no new content to show.
            if (operation != AddRemoveViewAction.add)
            {
                return new SentinelCallToolResult<object>() { IsSuccess = true, StatusMessage = description, SuccessData = summary };
            }

            return await SentinelCallToolResult<object>.ForPossiblyLargeDataAsync(
                summary,
                _workspaceManager.GetSolutionRoot(), "AppliedChangeSummary", ResultWrapperType.AppliedChangeSummaryResult,
                workspaceVersion: _workspaceManager.WorkspaceVersion, statusMessage: description);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SummaryComment failed for '{TargetName}' in '{FilePathWrapper}'", targetName, filePathResolved);
            return new SentinelCallToolResult<object>() { IsSuccess = false, ErrorData = ToolErrorMapper.ToResultError(ex, _workspaceManager, "SummaryComment") };
        }
    }

    public async Task<SentinelCallToolResult<object>> ExtractLocalVariable(
        ToolCallReason reason,
        FilePathWrapper filepath,
        string exactExpressionText,
        string variableName,
        string? lineBefore = null,
        string? lineAfter = null,
        bool dryRun = false,
        bool returnDiff = false,
        CancellationToken cancellationToken = default)
    {
        FilePathWrapper filePathResolved = FilePathWrapper.FromWire(filepath, _workspaceManager.GetSolutionRoot());
        try
        {
            var result = await _refactoringEngine.ExtractLocalVariableAsync(filePathResolved, exactExpressionText, variableName, lineBefore, lineAfter);
            if (string.IsNullOrEmpty(result.UpdatedText))
            {
                string errorReason = result.Outcome switch
                {
                    EditOutcome.DocumentNotFound => $"ExtractLocalVariable: document '{filePathResolved}' not found in the workspace.",
                    EditOutcome.SourceInvalid => $"ExtractLocalVariable: exactExpressionText not found in '{filePathResolved}'. {result.Message}",
                    EditOutcome.CannotConvert => $"ExtractLocalVariable: could not extract '{variableName}' in '{filePathResolved}'. {result.Message}",
                    _ => $"ExtractLocalVariable: no change produced for '{variableName}' in '{filePathResolved}' ({result.Outcome}). {result.Message}"
                };
                return new SentinelCallToolResult<object>() { IsSuccess = false, ErrorData = new ResultError(ToolErrorCode.Exception, errorReason) };
            }

            var changes = new Dictionary<FilePathWrapper, string> { [filePathResolved] = result.UpdatedText };
            var apply = await ValidateAndApplyAsync(changes, $"Extract local variable '{variableName}'.", "ExtractLocalVariable", dryRun, returnDiff, cancellationToken: cancellationToken);
            if (apply.Error is not null)
                return new SentinelCallToolResult<object> { IsSuccess = false, ErrorData = apply.Error };
            // Not wired into MemberChangedContentResult: unlike Member/ConstructorParameter, the new
            // declaration text isn't caller-supplied or separately exposed -> DocumentEditResult only
            // returns the whole-file UpdatedText, so reconstructing just the new "var x = ..." line
            // here would mean duplicating ExtractLocalVariableAsync's formatting logic. Revisit only
            // if that engine method is changed to return the new declaration text alongside UpdatedText.
            return new SentinelCallToolResult<object> { IsSuccess = true, SuccessData = new AppliedChangeSummary(apply.ChangeId, [filePathResolved], $"Extracted '{variableName}' as a local variable in {Path.GetFileName(filePathResolved)}.", apply.DryRun, apply.Diff) };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ExtractLocalVariable failed for '{VariableName}' in '{FilePathWrapper}'", variableName, filePathResolved);
            return new SentinelCallToolResult<object>() { IsSuccess = false, ErrorData = ToolErrorMapper.ToResultError(ex, _workspaceManager, "ExtractLocalVariable") };
        }
    }

    public async Task<SentinelCallToolResult<AppliedChangeSummary>> ExtractMethodSafe(
        ToolCallReason reason,
        FilePathWrapper filepath,
        string newMethodName,
        string exactSourceBlock,
        string? lineBefore = null,
        string? lineAfter = null,
        bool autoStage = true,
        bool dryRun = false,
        bool returnDiff = false,
        CancellationToken cancellationToken = default)
    {
        FilePathWrapper filePathResolved = FilePathWrapper.FromWire(filepath, _workspaceManager.GetSolutionRoot());
        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation("ExtractMethodSafe: {File} method={Name}", filePathResolved, newMethodName);
        }
        try
        {
            var result = await _msToolAugmentEngine.ExtractMethodSafeAsync(
                filePathResolved, newMethodName, exactSourceBlock, lineBefore, lineAfter, cancellationToken: cancellationToken);

            if (!result.Success)
            {
                return new SentinelCallToolResult<AppliedChangeSummary>
                {
                    IsSuccess = false,
                    ErrorData = new ResultError(ToolErrorCode.Exception, $"ExtractMethodSafe: {result.Error}")
                };
            }

            if (!autoStage)
            {
                var noStageChanges = string.IsNullOrEmpty(result.UpdatedContent)
                    ? new Dictionary<FilePathWrapper, string>()
                    : new Dictionary<FilePathWrapper, string> { [filePathResolved] = result.UpdatedContent };
                return new SentinelCallToolResult<AppliedChangeSummary>
                {
                    IsSuccess = true,
                    SuccessData = new AppliedChangeSummary(
                        ChangeId: null,
                        AffectedFiles: noStageChanges.Keys.ToList(),
                        Description: $"Extract '{newMethodName}' from '{filePathResolved}'.",
                        DryRun: false,
                        Diff: null,
                        ChangedContent: noStageChanges.Count == 0 ? null : noStageChanges,
                        Validated: false)
                };
            }

            if (string.IsNullOrEmpty(result.UpdatedContent))
            {
                return new SentinelCallToolResult<AppliedChangeSummary>
                {
                    IsSuccess = false,
                    ErrorData = new ResultError(ToolErrorCode.Exception, $"ExtractMethodSafe: no change produced for '{filePathResolved}'.")
                };
            }

            // Not wired into MemberChangedContentResult: the extracted method's body isn't caller-
            // supplied or separately exposed -> ExtractMethodSafeAsync's result only carries the
            // whole-file UpdatedContent, so showing just the new method here would mean duplicating
            // its formatting/signature-inference logic. Revisit only if that engine method starts
            // returning the extracted method's text alongside UpdatedContent.
            var changes = new Dictionary<FilePathWrapper, string> { [filePathResolved] = result.UpdatedContent };
            var apply = await ValidateAndApplyAsync(changes, $"Extract '{newMethodName}' from '{filePathResolved}'.", "ExtractMethodSafe", dryRun, returnDiff, cancellationToken: cancellationToken);
            if (apply.Error is not null)
                return new SentinelCallToolResult<AppliedChangeSummary> { IsSuccess = false, ErrorData = apply.Error };
            return new SentinelCallToolResult<AppliedChangeSummary>
            {
                IsSuccess = true,
                SuccessData = new AppliedChangeSummary(apply.ChangeId, [filePathResolved], $"Extracted '{newMethodName}' into a new method in {Path.GetFileName(filePathResolved)}.", apply.DryRun, apply.Diff, _workspaceManager.WorkspaceVersion, ChangedContent: changes, Validated: true)
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ExtractMethodSafe failed for '{NewMethodName}' in '{FilePathWrapper}'", newMethodName, filePathResolved);
            return new SentinelCallToolResult<AppliedChangeSummary>
            {
                IsSuccess = false,
                ErrorData = ToolErrorMapper.ToResultError(ex, _workspaceManager, "ExtractMethodSafe")
            };
        }
    }
}
