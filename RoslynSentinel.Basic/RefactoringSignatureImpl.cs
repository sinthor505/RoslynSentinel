using Microsoft.Extensions.Logging;

using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace RoslynSentinel.Basic;

// Decision 7 step 3 (plan_split_workspace_refactoring_tools_for_di.md): implementation half of the
// RefactoringSignatureTools/Impl pair. Method bodies below are moved verbatim from
// RefactoringTools.cs (RenameSymbol, MethodSignature, ChangeAccessibility,
// ConstructorParameter) except for the ValidateAndApplyAsync private helper, which is duplicated
// per-Impl-class (each new class owns its own thin instance wrapper around the shared static
// ValidateAndApplyHelper, per Decision 1-Amendment).
public class RefactoringSignatureImpl
{
    private readonly RefactoringEngine _refactoringEngine;
    private readonly IWorkspaceManager _workspaceManager;
    private readonly ValidationEngine _validationEngine;
    private readonly SymbolNavigationEngine _symbolNavigationEngine;
    private readonly ILogger _logger;

    public RefactoringSignatureImpl(
        RefactoringEngine refactoringEngine,
        IWorkspaceManager workspaceManager,
        ValidationEngine validationEngine,
        SymbolNavigationEngine symbolNavigationEngine,
        ILogger logger)
    {
        _refactoringEngine = refactoringEngine;
        _workspaceManager = workspaceManager;
        _validationEngine = validationEngine;
        _symbolNavigationEngine = symbolNavigationEngine;
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

    public async Task<SentinelCallToolResult<object>> RenameSymbol(
        ToolCallReason reason,
        string projectName,
        string docCommentId,
        string newName,
        bool dryRun = false,
        bool returnDiff = false,
        RequestContext<CallToolRequestParams>? requestParams = null,
        CancellationToken cancellationToken = default)
    {
        ProgressToken progressToken = requestParams?.Params?.ProgressToken ?? new ProgressToken();
        IProgress<ProgressNotificationValue> progress = new Progress<ProgressNotificationValue>(msg => requestParams?.Server?.NotifyProgressAsync(progressToken, new ProgressNotificationValue() { Progress = 10.0f }, null, cancellationToken));

        SymbolResolution resolution = await _workspaceManager.ResolveFromWireAsync(
            projectName, docCommentId, cancellationToken);
        if (!resolution.Resolved)
        {
            return new SentinelCallToolResult<object>
            {
                IsSuccess = false,
                ErrorData = new ResultError(ToolErrorCode.Exception, resolution.Error!.Message)
            };
        }

        RenameSymbolResult result = await _refactoringEngine.RenameSymbolAsync(
            resolution.Handle, resolution.Symbol!, newName, cancellationToken);

        if (result.Error is not null)
        {
            return new SentinelCallToolResult<object>
            {
                IsSuccess = false,
                ErrorData = new ResultError(ToolErrorCode.Exception, result.Error)
            };
        }

        if (result.PendingChanges.Count == 0)
        {
            return new SentinelCallToolResult<object>
            {
                IsSuccess = false,
                ErrorData = new ResultError(ToolErrorCode.Exception,
                    $"RenameSymbol produced no file changes for '{result.OldName}' -> '{result.NewName}'.")
            };
        }

        var apply = await ValidateAndApplyAsync(
            result.PendingChanges,
            $"Rename '{result.OldName}' to '{result.NewName}'.",
            "RenameSymbol", dryRun, returnDiff, cancellationToken: cancellationToken);

        if (apply.Error is not null)
            return new SentinelCallToolResult<object> { IsSuccess = false, ErrorData = apply.Error };

        // Deliberately not wired into ForPossiblyLargeDataAsync/MemberChangedContentResult: this
        // already returns rich custom SuccessData (oldName/newName/residualMentions/updatedHandle) instead
        // of a bare AppliedChangeSummary, and newName is caller-supplied verbatim -> there is no
        // separate "new content" fragment the offload mechanism would add value for.
        return new SentinelCallToolResult<object>
        {
            IsSuccess = true,
            SuccessData = new
            {
                changeId = apply.ChangeId,
                dryRun = apply.DryRun,
                diff = apply.Diff,
                oldName = result.OldName,
                newName = result.NewName,
                filesChanged = result.PendingChanges.Count,
                updatedHandle = result.UpdatedHandle is SymbolHandle h
                    ? new
                    {
                        h.ProjectName,
                        h.DocCommentId
                    }
                    : null,
                residualMentions = result.ResidualMentions is { Count: > 0 } rm ? rm : null,
                residualMentionsNote = result.ResidualMentions is { Count: > 0 }
                    ? $"'{result.OldName}' still appears in {result.ResidualMentions.Count} place(s) Rename couldn't reach (e.g. embedded in another identifier, or in a non-source file). Review residualMentions."
                    : null
            }
        };
    }

    public async Task<SentinelCallToolResult<object>> MethodSignature(
        ToolCallReason reason,
        FilePathWrapper filepath,
        AddRemoveViewAction operation,
        string methodName,
        string? paramName = null,
        string? paramType = null,
        string? defaultValue = null,
        string? contextSnippet = null,
        string? lineBefore = null,
        string? lineAfter = null,
        bool autoStage = true,
        bool dryRun = false,
        bool returnDiff = false,
        CancellationToken cancellationToken = default,
        bool nullDefault = false)
    {
        FilePathWrapper filePathResolved = FilePathWrapper.FromWire(filepath, _workspaceManager.GetSolutionRoot());
        try
        {
            if (operation == AddRemoveViewAction.view)
            {
                var (outcome, message, parameters) = await _refactoringEngine.GetMethodParametersAsync(filePathResolved, methodName, contextSnippet, lineBefore, lineAfter, cancellationToken);
                if (outcome is EditOutcome.DocumentNotFound or EditOutcome.CannotEdit or EditOutcome.TargetNotFound)
                    return new SentinelCallToolResult<object>() { IsSuccess = false, ErrorData = new ResultError(ToolErrorCode.Exception, $"MethodSignature: {message}") };
                return new SentinelCallToolResult<object>() { IsSuccess = true, SuccessData = new MethodSignatureViewResult(parameters) };
            }

            if (string.IsNullOrEmpty(paramName))
            {
                return new SentinelCallToolResult<object>() { IsSuccess = false, ErrorData = new ResultError(ToolErrorCode.InvalidArgument, $"MethodSignature: paramName is required for operation '{operation}'.") };
            }

            if (operation == AddRemoveViewAction.add && string.IsNullOrEmpty(paramType))
            {
                return new SentinelCallToolResult<object>() { IsSuccess = false, ErrorData = new ResultError(ToolErrorCode.InvalidArgument, "MethodSignature: paramType is required for operation 'add'.") };
            }

            if (nullDefault && defaultValue != null)
            {
                return new SentinelCallToolResult<object>() { IsSuccess = false, ErrorData = new ResultError(ToolErrorCode.InvalidArgument, "MethodSignature: nullDefault and defaultValue are mutually exclusive - pass only one.") };
            }

            if (nullDefault && operation != AddRemoveViewAction.add)
            {
                return new SentinelCallToolResult<object>() { IsSuccess = false, ErrorData = new ResultError(ToolErrorCode.InvalidArgument, $"MethodSignature: nullDefault is only valid for operation 'add', not '{operation}'.") };
            }

            DocumentEditResult updated;
            Dictionary<FilePathWrapper, string> changes;
            if (operation == AddRemoveViewAction.add)
            {
                updated = await _refactoringEngine.AddMethodParameterAsync(filePathResolved, methodName, paramName, paramType!, defaultValue, contextSnippet, lineBefore, lineAfter, cancellationToken, nullDefault);
                if (RefactoringToolHelpers.RequireUpdatedText(updated, "MethodSignature", filePathResolved) is { } addGuardResult)
                    return addGuardResult;
                changes = new Dictionary<FilePathWrapper, string> { [filePathResolved] = updated.UpdatedText! };
            }
            else
            {
                updated = await _refactoringEngine.RemoveMethodParameterAsync(filePathResolved, methodName, paramName, contextSnippet, lineBefore, lineAfter, cancellationToken);
                if (updated.Outcome == EditOutcome.CannotRemove)
                {
                    return new SentinelCallToolResult<object>() { IsSuccess = false, ErrorData = new ResultError(ToolErrorCode.InvalidArgument, $"MethodSignature: {updated.Message}") };
                }

                if (RefactoringToolHelpers.RequireUpdatedText(updated, "MethodSignature", filePathResolved) is { } removeGuardResult)
                    return removeGuardResult;
                changes = updated.Changes;
            }

            if (!autoStage)
            {
                var noStageDescription = operation == AddRemoveViewAction.add
                    ? $"Added parameter '{paramType} {paramName}{(nullDefault ? " = null" : defaultValue != null ? $" = {defaultValue}" : "")}' to '{methodName}' in {Path.GetFileName(filePathResolved)}."
                    : $"Removed parameter '{paramName}' from '{methodName}' in {Path.GetFileName(filePathResolved)}, updating {changes.Count - 1} call site(s).";
                return new SentinelCallToolResult<object>()
                {
                    IsSuccess = true,
                    SuccessData = new AppliedChangeSummary(
                        ChangeId: null,
                        AffectedFiles: changes.Keys.ToList(),
                        Description: noStageDescription,
                        DryRun: false,
                        Diff: null,
                        ChangedContent: changes,
                        Validated: false)
                };
            }

            var description = operation == AddRemoveViewAction.add
                ? $"Added parameter '{paramType} {paramName}{(nullDefault ? " = null" : defaultValue != null ? $" = {defaultValue}" : "")}' to '{methodName}' in {Path.GetFileName(filePathResolved)}."
                : $"Removed parameter '{paramName}' from '{methodName}' in {Path.GetFileName(filePathResolved)}, updating {changes.Count - 1} call site(s).";
            var apply = await ValidateAndApplyAsync(changes, description, "MethodSignature", dryRun, returnDiff, cancellationToken: cancellationToken);
            if (apply.Error is not null)
                return new SentinelCallToolResult<object> { IsSuccess = false, ErrorData = apply.Error };

            return await SentinelCallToolResult<object>.ForPossiblyLargeDataAsync(
                new AppliedChangeSummary(apply.ChangeId, changes.Keys.ToList(), description, apply.DryRun, apply.Diff, ChangedContent: changes, Validated: true),
                _workspaceManager.GetSolutionRoot(), "AppliedChangeSummary", ResultWrapperType.AppliedChangeSummaryResult,
                workspaceVersion: _workspaceManager.WorkspaceVersion, statusMessage: description);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "MethodSignature failed for '{MethodName}' in '{FilePathWrapper}'", methodName, filePathResolved);
            return new SentinelCallToolResult<object>() { IsSuccess = false, ErrorData = ToolErrorMapper.ToResultError(ex, _workspaceManager, "MethodSignature") };
        }
    }

    public async Task<SentinelCallToolResult<AppliedChangeSummary>> ChangeAccessibility(
        ToolCallReason reason,
        FilePathWrapper filepath,
        string targetName,
        AccessibilityLevel accessibility,
        string? contextSnippet = null,
        string? lineBefore = null,
        string? lineAfter = null,
        bool autoStage = true,
        bool dryRun = false,
        bool returnDiff = false,
        CancellationToken cancellationToken = default)
    {
        FilePathWrapper filePathResolved = FilePathWrapper.FromWire(filepath, _workspaceManager.GetSolutionRoot());
        try
        {
            var updated = await _refactoringEngine.ChangeAccessibilityAsync(filePathResolved, targetName, accessibility, contextSnippet, lineBefore, lineAfter);
            if (!autoStage)
            {
                var noStageAccessibilityKeyword = accessibility switch
                {
                    AccessibilityLevel.protectedInternal => "protected internal",
                    AccessibilityLevel.privateProtected => "private protected",
                    _ => accessibility.ToString()
                };
                var noStageChanges = string.IsNullOrEmpty(updated.UpdatedText)
                    ? new Dictionary<FilePathWrapper, string>()
                    : new Dictionary<FilePathWrapper, string> { [filePathResolved] = updated.UpdatedText! };
                return new SentinelCallToolResult<AppliedChangeSummary>()
                {
                    IsSuccess = true,
                    SuccessData = new AppliedChangeSummary(
                        ChangeId: null,
                        AffectedFiles: noStageChanges.Keys.ToList(),
                        Description: $"Change accessibility of '{targetName}' to '{noStageAccessibilityKeyword}'.",
                        DryRun: false,
                        Diff: null,
                        ChangedContent: noStageChanges.Count == 0 ? null : noStageChanges,
                        Validated: false)
                };
            }

            if (RefactoringToolHelpers.RequireUpdatedText(updated, "ChangeAccessibility", filePathResolved) is { } guardResult)
                return new SentinelCallToolResult<AppliedChangeSummary> { IsSuccess = false, ErrorData = guardResult.ErrorData };

            var accessibilityKeyword = accessibility switch
            {
                AccessibilityLevel.protectedInternal => "protected internal",
                AccessibilityLevel.privateProtected => "private protected",
                _ => accessibility.ToString()
            };
            var changes = new Dictionary<FilePathWrapper, string> { [filePathResolved] = updated.UpdatedText! };
            var apply = await ValidateAndApplyAsync(changes, $"Change accessibility of '{targetName}' to '{accessibilityKeyword}'.", "ChangeAccessibility", dryRun, returnDiff, cancellationToken: cancellationToken);
            if (apply.Error is not null)
                return new SentinelCallToolResult<AppliedChangeSummary> { IsSuccess = false, ErrorData = apply.Error };
            // No ChangedContent here: the only "new" text is the accessibility keyword itself,
            // which the caller already passed in verbatim -> echoing it back adds nothing the
            // caller doesn't already have, unlike a reconstructed multi-part snippet.
            return new SentinelCallToolResult<AppliedChangeSummary>() { IsSuccess = true, SuccessData = new AppliedChangeSummary(apply.ChangeId, [filePathResolved], $"Changed accessibility of '{targetName}' to '{accessibilityKeyword}' in {Path.GetFileName(filePathResolved)}.", apply.DryRun, apply.Diff, _workspaceManager.WorkspaceVersion, ChangedContent: changes, Validated: true) };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ChangeAccessibility failed for '{TargetName}' in '{FilePathWrapper}'", targetName, filePathResolved);
            return new SentinelCallToolResult<AppliedChangeSummary>() { IsSuccess = false, ErrorData = ToolErrorMapper.ToResultError(ex, _workspaceManager, "ChangeAccessibility") };
        }
    }

    public async Task<SentinelCallToolResult<object>> ConstructorParameter(
        ToolCallReason reason,
        FilePathWrapper filepath,
        AddRemoveViewAction operation,
        string className,
        string? paramName = null,
        string? paramType = null,
        string? fieldName = null,
        string? contextSnippet = null,
        string? lineBefore = null,
        string? lineAfter = null,
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
                var (outcome, message, parameters) = await _refactoringEngine.GetConstructorParametersAsync(filePath: filePathResolved, className, contextSnippet, lineBefore, lineAfter, cancellationToken);
                if (outcome is EditOutcome.DocumentNotFound or EditOutcome.CannotEdit)
                    return new SentinelCallToolResult<object>() { IsSuccess = false, ErrorData = new ResultError(ToolErrorCode.Exception, $"ConstructorParameter: {message}") };
                return new SentinelCallToolResult<object>() { IsSuccess = true, SuccessData = new ConstructorParameterViewResult(parameters) };
            }

            if (string.IsNullOrEmpty(paramName))
            {
                return new SentinelCallToolResult<object>() { IsSuccess = false, ErrorData = new ResultError(ToolErrorCode.InvalidArgument, $"ConstructorParameter: paramName is required for operation '{operation}'.") };
            }

            if (operation == AddRemoveViewAction.add && string.IsNullOrEmpty(paramType))
            {
                return new SentinelCallToolResult<object>() { IsSuccess = false, ErrorData = new ResultError(ToolErrorCode.InvalidArgument, "ConstructorParameter: paramType is required for operation 'add'.") };
            }

            DocumentEditResult updated;
            string resolvedFieldName;
            if (operation == AddRemoveViewAction.add)
            {
                updated = await _refactoringEngine.AddConstructorParameterAsync(filePathResolved, className, paramName, paramType!, fieldName, contextSnippet, lineBefore, lineAfter);
                // updated.Message carries "// paramName='x', fieldName='_x'" on success -> surface the
                // resolved field name explicitly since it may differ from what the caller passed
                // (see fieldName/paramName collision disambiguation in AddConstructorParameterAsync).
                resolvedFieldName = updated.Message is { Length: > 0 } msg
                    && System.Text.RegularExpressions.Regex.Match(msg, "fieldName='([^']*)'") is { Success: true } m
                    ? m.Groups[1].Value
                    : $"_{char.ToLower(paramName[0])}{paramName[1..]}";
            }
            else
            {
                updated = await _refactoringEngine.RemoveConstructorParameterAsync(filePathResolved, className, paramName, contextSnippet, lineBefore, lineAfter, cancellationToken);
                resolvedFieldName = updated.Message is { Length: > 0 } msg
                    && System.Text.RegularExpressions.Regex.Match(msg, "fieldName='([^']*)'") is { Success: true } m
                    ? m.Groups[1].Value
                    : "";
            }

            if (!autoStage)
            {
                var noStageDescription = operation == AddRemoveViewAction.add
                    ? $"Added '{paramType} {paramName}' DI parameter to '{className}' in {Path.GetFileName(filePathResolved)}, backed by field '{resolvedFieldName}'."
                    : $"Removed '{paramName}' DI parameter from '{className}' in {Path.GetFileName(filePathResolved)}."
                        + (updated.Message?.Contains("fieldRemoved='True'") == true ? $" Also removed unused backing field '{resolvedFieldName}'." : "");
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

            if (RefactoringToolHelpers.RequireUpdatedText(updated, "ConstructorParameter", filePathResolved) is { } guardResult)
                return guardResult;

            var description = operation == AddRemoveViewAction.add
                ? $"Added '{paramType} {paramName}' DI parameter to '{className}' in {Path.GetFileName(filePathResolved)}, backed by field '{resolvedFieldName}'."
                : $"Removed '{paramName}' DI parameter from '{className}' in {Path.GetFileName(filePathResolved)}."
                    + (updated.Message?.Contains("fieldRemoved='True'") == true ? $" Also removed unused backing field '{resolvedFieldName}'." : "");

            var changes = new Dictionary<FilePathWrapper, string> { [filePathResolved] = updated.UpdatedText! };
            var apply = await ValidateAndApplyAsync(changes, description, "ConstructorParameter", dryRun, returnDiff, cancellationToken: cancellationToken);
            if (apply.Error is not null)
                return new SentinelCallToolResult<object> { IsSuccess = false, ErrorData = apply.Error };

            return await SentinelCallToolResult<object>.ForPossiblyLargeDataAsync(
                new AppliedChangeSummary(apply.ChangeId, [filePathResolved], description, apply.DryRun, apply.Diff, ChangedContent: changes, Validated: true),
                _workspaceManager.GetSolutionRoot(), "AppliedChangeSummary", ResultWrapperType.AppliedChangeSummaryResult,
                workspaceVersion: _workspaceManager.WorkspaceVersion, statusMessage: description);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ConstructorParameter failed for '{ClassName}' in '{FilePathWrapper}'", className, filePathResolved);
            return new SentinelCallToolResult<object>() { IsSuccess = false, ErrorData = ToolErrorMapper.ToResultError(ex, _workspaceManager, "ConstructorParameter") };
        }
    }
}
