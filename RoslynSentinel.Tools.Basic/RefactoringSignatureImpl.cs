using Microsoft.Extensions.Logging;

using ModelContextProtocol;

using RoslynSentinel.Engines.Basic;

namespace RoslynSentinel.Tools.Basic;

// Decision 7 step 3 (plan_split_workspace_refactoring_tools_for_di.md): implementation half of the
// RefactoringSignatureTools/Impl pair. Method bodies below are moved verbatim from
// RefactoringStructuralTools.cs (RenameSymbolTool, MethodSignatureTool, ChangeAccessibilityTool,
// ConstructorParameterTool) except for the ValidateAndApplyAsync private helper, which is duplicated
// per-Impl-class (each new class owns its own thin instance wrapper around the shared static
// ValidateAndApplyHelper, per Decision 1-Amendment).
public class RefactoringSignatureImpl
{
    private readonly BasicRefactoringEngine _refactoringEngine;
    private readonly MemberRefactoringEngine _memberRefactoringEngine;
    private readonly IWorkspaceManager _workspaceManager;
    private readonly ValidationEngine _validationEngine;
    private readonly SymbolNavigationEngine _symbolNavigationEngine;
    private readonly ILogger _logger;

    // Which tools this server can serve, for the edit-impact next step; null (unit tests, hosts without a surface) = every tool callable.
    private readonly ActiveToolSurface? _activeToolSurface;

    public RefactoringSignatureImpl(
        BasicRefactoringEngine refactoringEngine,
        MemberRefactoringEngine memberRefactoringEngine,
        IWorkspaceManager workspaceManager,
        ValidationEngine validationEngine,
        SymbolNavigationEngine symbolNavigationEngine,
        ILogger logger,
        ActiveToolSurface? activeToolSurface = null)
    {
        _activeToolSurface = activeToolSurface;
        _refactoringEngine = refactoringEngine;
        _memberRefactoringEngine = memberRefactoringEngine;
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
        IProgress<EngineProgress>? progress = default,
        IReadOnlyCollection<FilePathWrapper>? removePaths = null,
        CancellationToken cancellationToken = default,
        IReadOnlyCollection<FilePathWrapper>? deletePaths = null) =>
        ValidateAndApplyHelper.ValidateAndApplyAsync(
            _validationEngine, _workspaceManager, _logger, changes, operationName,
            dryRun, returnDiff, progress, removePaths, cancellationToken, deletePaths,
            describeValidationFailure: (report, ct) => CompilerErrorLookupHelper.DescribeAsync(report, _symbolNavigationEngine, ct),
            describeEditImpact: EditImpactGate.Describer(_workspaceManager, changes, _activeToolSurface, removePaths, deletePaths));

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
        IProgress<ProgressNotificationValue> progress = new Progress<ProgressNotificationValue>(msg => requestParams?.Server?.NotifyProgressAsync(progressToken, new ProgressNotificationValue() { Progress = msg.Progress, Total = msg.Total, Message = msg.Message }, null, cancellationToken));

        SymbolResolution resolution = await _workspaceManager.ResolveFromWireAsync(
            projectName, docCommentId, cancellationToken);
        string? docCommentIdNote = null;
        if (!resolution.Resolved)
        {
            // A model often drops the parameter list from a method id (eval run 20261109-233953-684 T8/T10). One method of that
            // name on the type: use it and say so. Several: list the full ids. Anything else keeps the original error.
            // READCHOKEPOINT-CAST: read the committed solution through the reader view of the workspace manager.
            var committed = await ((IWorkspaceReader)_workspaceManager).GetSolutionAsync(ReadSource.Committed, cancellationToken);
            var (unique, overloadIds) = await MissingParameterListResolver.FindAsync(committed, projectName, docCommentId, cancellationToken);
            if (unique?.GetDocumentationCommentId() is { } fullId)
            {
                var byFullId = await _workspaceManager.ResolveFromWireAsync(projectName, fullId, cancellationToken);
                if (byFullId.Resolved)
                {
                    resolution = byFullId;
                    docCommentIdNote = $"docCommentId '{docCommentId}' had no parameter list; it was resolved to the only method of that name, whose full id is '{fullId}'. Pass the full id next time.";
                }
            }
            else if (overloadIds.Count > 1)
            {
                return new SentinelCallToolResult<object>
                {
                    IsError = true,
                    ErrorData = new ResultError(ToolErrorCode.InvalidArgument,
                        $"docCommentId '{docCommentId}' is missing its parameter list, and '{docCommentId[(docCommentId.LastIndexOf('.') + 1)..]}' has {overloadIds.Count} overloads. Pass the full docCommentId of the one to rename:\n"
                        + string.Join("\n", overloadIds.Select(id => "  " + id)))
                };
            }
        }

        if (!resolution.Resolved)
        {
            return new SentinelCallToolResult<object>
            {
                IsError = true,
                ErrorData = new ResultError(resolution.Error!.Code == EngineErrorCode.SymbolNotResolved ? ToolErrorCode.NotFound : ToolErrorCode.Exception, resolution.Error!.Message)
            };
        }

        // A namespace rename replaces only the LAST segment of the namespace (IDE semantics); Roslyn's Renamer
        // rewrites the identifier token of every declaration, using directive and qualified name, so a dotted
        // newName would nest a second namespace under the parent instead of renaming. Refuse it up front.
        if (resolution.Symbol is Microsoft.CodeAnalysis.INamespaceSymbol nsSymbol)
        {
            if (nsSymbol.IsGlobalNamespace)
            {
                return new SentinelCallToolResult<object>
                {
                    IsError = true,
                    ErrorData = new ResultError(ToolErrorCode.InvalidArgument, "The global namespace cannot be renamed. Pass docCommentId 'N:<Namespace.Name>' of a named namespace.")
                };
            }

            if (newName.Contains('.'))
            {
                return new SentinelCallToolResult<object>
                {
                    IsError = true,
                    ErrorData = new ResultError(ToolErrorCode.InvalidArgument,
                        $"newName '{newName}' contains '.'. A namespace rename replaces only the last segment: pass the new last segment only, e.g. 'Advanced' to turn A.B.Basic into A.B.Advanced. To move a namespace elsewhere, rename it segment by segment.")
                };
            }

            if (!Microsoft.CodeAnalysis.CSharp.SyntaxFacts.IsValidIdentifier(newName))
            {
                return new SentinelCallToolResult<object>
                {
                    IsError = true,
                    ErrorData = new ResultError(ToolErrorCode.InvalidArgument, $"newName '{newName}' is not a valid C# identifier; pass the new last segment of the namespace.")
                };
            }
        }

        RenameSymbolResult result = await _refactoringEngine.RenameSymbolAsync(
            resolution.Handle, resolution.Symbol!, newName, cancellationToken);

        if (result.Error is not null)
        {
            return new SentinelCallToolResult<object>
            {
                IsError = true,
                ErrorData = new ResultError(ToolErrorCode.Exception, result.Error)
            };
        }

        if (result.PendingChanges.Count == 0)
        {
            return new SentinelCallToolResult<object>
            {
                IsError = true,
                ErrorData = new ResultError(ToolErrorCode.Exception,
                    $"RenameSymbol produced no file changes for '{result.OldName}' -> '{result.NewName}'.")
            };
        }

        // KNOWN, INTENTIONALLY CORRECT REFUSAL: renaming a member to a name the type already has with the same signature
        // makes the compile gate below reject the change with CS0111 (duplicate member) or CS0121 (ambiguous call). There
        // is no explicit collision check here or in BasicRefactoringEngine.RenameSymbolAsync; the gate produces the
        // refusal and CompilerErrorLookupHelper adds the CS0101/CS0111 hint. RenameSymbolTool is a rename, not a merge, so no
        // body-reconciliation policy exists to apply. Decision 13 in
        // docs/current/proposals/proposal_journal_digest_followup_decisions.md: do not re-investigate or "fix" this.
        var apply = await ValidateAndApplyAsync(
            result.PendingChanges,
            $"Rename '{result.OldName}' to '{result.NewName}'.",
            "RenameSymbol", dryRun, returnDiff, cancellationToken: cancellationToken);

        if (apply.Error is not null)
            return new SentinelCallToolResult<object> { IsError = true, ErrorData = apply.Error };

        // Residual mentions: return only a small summary (owner ruling D2); the full rows are stored once as an Envelope
        // large result and reached through the per-bucket GetLargeResult calls in residualMentions.details.
        ResidualMentionsView? residualView = null;
        string? residualNote = null;
        if (result.ResidualMentions is { Count: > 0 } residualMentions)
        {
            string? detailResultId = await ResidualMentionDetailStore.StoreAsync(residualMentions, _workspaceManager.GetSolutionRoot(), cancellationToken);
            residualView = ResidualMentionSummarizer.Summarize(residualMentions, result.OldName, detailResultId);
            residualNote = ResidualMentionSummarizer.Note(result.OldName, residualMentions.Count, detailResultId is not null);
        }

        // Deliberately not wired into ForPossiblyLargeDataAsync: this
        // already returns rich custom SuccessData (oldName/newName/residualMentions/updatedHandle) instead
        // of a bare AppliedChangeSummary, and newName is caller-supplied verbatim -> there is no
        // separate "new content" fragment the offload mechanism would add value for.
        return new SentinelCallToolResult<object>
        {
            IsError = false,
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
                residualMentions = residualView,
                residualMentionsNote = residualNote,
                docCommentIdNote
            }
        };
    }

    public async Task<SentinelCallToolResult<object>> MethodSignature(
        ToolCallReason reason,
        string filePath,
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
        FilePathWrapper filePathResolved = _workspaceManager.ResolveFromWire(filePath);
        try
        {
            if (operation == AddRemoveViewAction.view)
            {
                var (_, _, parameters) = await _memberRefactoringEngine.GetMethodParametersAsync(filePathResolved, methodName, contextSnippet, lineBefore, lineAfter, cancellationToken);
                return new SentinelCallToolResult<object>() { IsError = false, SuccessData = new MethodSignatureViewResult(parameters) };
            }

            if (string.IsNullOrEmpty(paramName))
            {
                return new SentinelCallToolResult<object>() { IsError = true, ErrorData = new ResultError(ToolErrorCode.InvalidArgument, $"MethodSignature: paramName is required for operation '{operation}'.") };
            }

            if (operation == AddRemoveViewAction.add && string.IsNullOrEmpty(paramType))
            {
                return new SentinelCallToolResult<object>() { IsError = true, ErrorData = new ResultError(ToolErrorCode.InvalidArgument, "MethodSignature: paramType is required for operation 'add'.") };
            }

            if (nullDefault && defaultValue != null)
            {
                return new SentinelCallToolResult<object>() { IsError = true, ErrorData = new ResultError(ToolErrorCode.InvalidArgument, "MethodSignature: nullDefault and defaultValue are mutually exclusive - pass only one.") };
            }

            if (nullDefault && operation != AddRemoveViewAction.add)
            {
                return new SentinelCallToolResult<object>() { IsError = true, ErrorData = new ResultError(ToolErrorCode.InvalidArgument, $"MethodSignature: nullDefault is only valid for operation 'add', not '{operation}'.") };
            }

            DocumentEditResult updated;
            Dictionary<FilePathWrapper, string> changes;
            if (operation == AddRemoveViewAction.add)
            {
                updated = await _memberRefactoringEngine.AddMethodParameterAsync(filePathResolved, methodName, paramName, paramType!, defaultValue, contextSnippet, lineBefore, lineAfter, cancellationToken, nullDefault);
                changes = new Dictionary<FilePathWrapper, string> { [filePathResolved] = updated.UpdatedText };
            }
            else
            {
                updated = await _memberRefactoringEngine.RemoveMethodParameterAsync(filePathResolved, methodName, paramName, contextSnippet, lineBefore, lineAfter, cancellationToken);
                changes = updated.Changes;
            }

            if (!autoStage)
            {
                var noStageDescription = operation == AddRemoveViewAction.add
                    ? $"Added parameter '{paramType} {paramName}{(nullDefault ? " = null" : defaultValue != null ? $" = {defaultValue}" : "")}' to '{methodName}' in {Path.GetFileName(filePathResolved)}."
                    : $"Removed parameter '{paramName}' from '{methodName}' in {Path.GetFileName(filePathResolved)}, updating {changes.Count - 1} call site(s).";
                return new SentinelCallToolResult<object>()
                {
                    IsError = false,
                    SuccessData = AppliedChangeSummary.From(await ValidateAndApplyHelper.ComputeLineChangesAsync(_workspaceManager, changes, cancellationToken), null, changes.Keys.ToList(), noStageDescription, dryRun: false, diff: null, changedContent: changes, validated: false)
                };
            }

            var description = operation == AddRemoveViewAction.add
                ? $"Added parameter '{paramType} {paramName}{(nullDefault ? " = null" : defaultValue != null ? $" = {defaultValue}" : "")}' to '{methodName}' in {Path.GetFileName(filePathResolved)}."
                : $"Removed parameter '{paramName}' from '{methodName}' in {Path.GetFileName(filePathResolved)}, updating {changes.Count - 1} call site(s).";
            var apply = await ValidateAndApplyAsync(changes, description, "MethodSignature", dryRun, returnDiff, cancellationToken: cancellationToken);
            if (apply.Error is not null)
                return new SentinelCallToolResult<object> { IsError = true, ErrorData = apply.Error };

            return new SentinelCallToolResult<object> { IsError = false, SuccessData = AppliedChangeSummary.From(apply, changes.Keys.ToList(), description, changedContent: changes, validated: true), WorkspaceVersion = _workspaceManager.WorkspaceVersion, StatusMessage = description };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "MethodSignature failed for '{MethodName}' in '{FilePathWrapper}'", methodName, filePathResolved);
            return new SentinelCallToolResult<object>() { IsError = true, ErrorData = ToolErrorMapper.ToResultError(ex, _workspaceManager, "MethodSignature") };
        }
    }

    public async Task<SentinelCallToolResult<AppliedChangeSummary>> ChangeAccessibility(
        ToolCallReason reason,
        string filePath,
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
        FilePathWrapper filePathResolved = _workspaceManager.ResolveFromWire(filePath);
        try
        {
            var updated = await _memberRefactoringEngine.ChangeAccessibilityAsync(filePathResolved, targetName, accessibility, contextSnippet, lineBefore, lineAfter, cancellationToken: cancellationToken);
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
                    : new Dictionary<FilePathWrapper, string> { [filePathResolved] = updated.UpdatedText };
                return new SentinelCallToolResult<AppliedChangeSummary>()
                {
                    IsError = false,
                    SuccessData = AppliedChangeSummary.From(await ValidateAndApplyHelper.ComputeLineChangesAsync(_workspaceManager, noStageChanges, cancellationToken), null, noStageChanges.Keys.ToList(), $"Change accessibility of '{targetName}' to '{noStageAccessibilityKeyword}'.", dryRun: false, diff: null, changedContent: noStageChanges.Count == 0 ? null : noStageChanges, validated: false)
                };
            }

            var accessibilityKeyword = accessibility switch
            {
                AccessibilityLevel.protectedInternal => "protected internal",
                AccessibilityLevel.privateProtected => "private protected",
                _ => accessibility.ToString()
            };
            var changes = new Dictionary<FilePathWrapper, string> { [filePathResolved] = updated.UpdatedText };
            var apply = await ValidateAndApplyAsync(changes, $"Change accessibility of '{targetName}' to '{accessibilityKeyword}'.", "ChangeAccessibility", dryRun, returnDiff, cancellationToken: cancellationToken);
            if (apply.Error is not null)
                return new SentinelCallToolResult<AppliedChangeSummary> { IsError = true, ErrorData = apply.Error };
            // ChangedContent is dropped centrally by AppliedChangeSummary for a real apply; the new text is just the accessibility keyword the caller passed in.
            return new SentinelCallToolResult<AppliedChangeSummary>() { IsError = false, SuccessData = AppliedChangeSummary.From(apply, [filePathResolved], $"Changed accessibility of '{targetName}' to '{accessibilityKeyword}' in {Path.GetFileName(filePathResolved)}.", workspaceVersion: _workspaceManager.WorkspaceVersion, changedContent: changes, validated: true) };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ChangeAccessibility failed for '{TargetName}' in '{FilePathWrapper}'", targetName, filePathResolved);
            return new SentinelCallToolResult<AppliedChangeSummary>() { IsError = true, ErrorData = ToolErrorMapper.ToResultError(ex, _workspaceManager, "ChangeAccessibility") };
        }
    }

    public async Task<SentinelCallToolResult<object>> ConstructorParameter(
        ToolCallReason reason,
        string filePath,
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
        CancellationToken cancellationToken = default, string? defaultValue = null, bool nullDefault = false, Dictionary<string, string>? callSiteFixups = null)
    {
        if (callSiteFixups is { Count: > 0 } && operation != AddRemoveViewAction.add)
        {
            return new SentinelCallToolResult<object>() { IsError = true, ErrorData = new ResultError(ToolErrorCode.InvalidArgument, $"ConstructorParameter: callSiteFixups is only valid for operation 'add', not '{operation}'.") };
        }

        FilePathWrapper filePathResolved = _workspaceManager.ResolveFromWire(filePath);
        try
        {
            if (operation == AddRemoveViewAction.view)
            {
                var (_, _, parameters) = await _memberRefactoringEngine.GetConstructorParametersAsync(filePath: filePathResolved, className, contextSnippet, lineBefore, lineAfter, cancellationToken);
                return new SentinelCallToolResult<object>() { IsError = false, SuccessData = new ConstructorParameterViewResult(parameters) };
            }

            if (string.IsNullOrEmpty(paramName))
            {
                return new SentinelCallToolResult<object>() { IsError = true, ErrorData = new ResultError(ToolErrorCode.InvalidArgument, $"ConstructorParameter: paramName is required for operation '{operation}'.") };
            }

            if (operation == AddRemoveViewAction.add && string.IsNullOrEmpty(paramType))
            {
                return new SentinelCallToolResult<object>() { IsError = true, ErrorData = new ResultError(ToolErrorCode.InvalidArgument, "ConstructorParameter: paramType is required for operation 'add'.") };
            }

            if (nullDefault && defaultValue != null)
            {
                return new SentinelCallToolResult<object>() { IsError = true, ErrorData = new ResultError(ToolErrorCode.InvalidArgument, "ConstructorParameter: nullDefault and defaultValue are mutually exclusive - pass only one.") };
            }

            if (nullDefault && operation != AddRemoveViewAction.add)
            {
                return new SentinelCallToolResult<object>() { IsError = true, ErrorData = new ResultError(ToolErrorCode.InvalidArgument, $"ConstructorParameter: nullDefault is only valid for operation 'add', not '{operation}'.") };
            }

            DocumentEditResult updated;
            string resolvedFieldName;
            AddConstructorParameterCascadeResult? cascade = null;
            if (operation == AddRemoveViewAction.add)
            {
                if (callSiteFixups is { Count: > 0 })
                {
                    try
                    {
                        cascade = await _memberRefactoringEngine.AddConstructorParameterWithCallSitesAsync(filePathResolved, className, paramName, paramType!, callSiteFixups, fieldName, contextSnippet, lineBefore, lineAfter, defaultValue, nullDefault, cancellationToken);
                    }
                    catch (UnresolvedCallSitesException ex)
                    {
                        // Detail echoes the full path verbatim: "{FilePath}:{Line}" is the literal callSiteFixups key.
                        var unresolvedDetail = string.Join("; ", ex.Sites.Select(s => $"{s.FilePath}:{s.Line} [{s.CallText}]" + (s.Note != null ? $" (cannot be fixed with callSiteFixups: {s.Note})" : string.Empty)));
                        return new SentinelCallToolResult<object>()
                        {
                            IsError = true,
                            ErrorData = new ResultError(ToolErrorCode.UnresolvedCallSites, ex.Message, unresolvedDetail, ex.Sites.Cast<object>().ToList())
                        };
                    }

                    updated = cascade.ClassEdit;
                }
                else
                {
                    updated = await _memberRefactoringEngine.AddConstructorParameterAsync(filePathResolved, className, paramName, paramType!, fieldName, contextSnippet, lineBefore, lineAfter, cancellationToken: cancellationToken, defaultValue: defaultValue, nullDefault: nullDefault);
                }

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
                updated = await _memberRefactoringEngine.RemoveConstructorParameterAsync(filePathResolved, className, paramName, contextSnippet, lineBefore, lineAfter, cancellationToken);
                resolvedFieldName = updated.Message is { Length: > 0 } msg
                    && System.Text.RegularExpressions.Regex.Match(msg, "fieldName='([^']*)'") is { Success: true } m
                    ? m.Groups[1].Value
                    : "";
            }

            if (!autoStage)
            {
                var noStageDescription = operation == AddRemoveViewAction.add
                    ? $"Added '{paramType} {paramName}{(nullDefault ? " = null" : defaultValue != null ? $" = {defaultValue}" : "")}' DI parameter to '{className}' in {Path.GetFileName(filePathResolved)}, backed by field '{resolvedFieldName}'."
                    : $"Removed '{paramName}' DI parameter from '{className}' in {Path.GetFileName(filePathResolved)}."
                        + (updated.Message?.Contains("fieldRemoved='True'") == true ? $" Also removed unused backing field '{resolvedFieldName}'." : "");
                var noStageChanges = cascade?.Changes ?? (string.IsNullOrEmpty(updated.UpdatedText)
                    ? new Dictionary<FilePathWrapper, string>()
                    : new Dictionary<FilePathWrapper, string> { [filePathResolved] = updated.UpdatedText });
                return new SentinelCallToolResult<object>()
                {
                    IsError = false,
                    SuccessData = AppliedChangeSummary.From(await ValidateAndApplyHelper.ComputeLineChangesAsync(_workspaceManager, noStageChanges, cancellationToken), null, noStageChanges.Keys.ToList(), noStageDescription, dryRun: false, diff: null, changedContent: noStageChanges.Count == 0 ? null : noStageChanges, validated: false)
                };
            }

            var description = operation == AddRemoveViewAction.add
                ? $"Added '{paramType} {paramName}{(nullDefault ? " = null" : defaultValue != null ? $" = {defaultValue}" : "")}' DI parameter to '{className}' in {Path.GetFileName(filePathResolved)}, backed by field '{resolvedFieldName}'."
                : $"Removed '{paramName}' DI parameter from '{className}' in {Path.GetFileName(filePathResolved)}."
                    + (updated.Message?.Contains("fieldRemoved='True'") == true ? $" Also removed unused backing field '{resolvedFieldName}'." : "");

            if (cascade is { CallSitesUpdated: > 0 } || cascade is { CallSitesLeftToDefault: > 0 })
            {
                description += $" Added the new argument at {cascade.CallSitesUpdated} call site(s) across {cascade.Changes.Count} file(s)" + (cascade.CallSitesLeftToDefault > 0 ? $"; {cascade.CallSitesLeftToDefault} site(s) left on the default value." : ".");
            }

            var changes = cascade?.Changes ?? new Dictionary<FilePathWrapper, string> { [filePathResolved] = updated.UpdatedText };
            var apply = await ValidateAndApplyAsync(changes, description, "ConstructorParameter", dryRun, returnDiff, cancellationToken: cancellationToken);
            if (apply.Error is not null)
                return new SentinelCallToolResult<object> { IsError = true, ErrorData = apply.Error };

            return new SentinelCallToolResult<object> { IsError = false, SuccessData = AppliedChangeSummary.From(apply, changes.Keys.ToList(), description, changedContent: changes, validated: true), WorkspaceVersion = _workspaceManager.WorkspaceVersion, StatusMessage = description };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ConstructorParameter failed for '{ClassName}' in '{FilePathWrapper}'", className, filePathResolved);
            return new SentinelCallToolResult<object>() { IsError = true, ErrorData = ToolErrorMapper.ToResultError(ex, _workspaceManager, "ConstructorParameter") };
        }
    }
}
