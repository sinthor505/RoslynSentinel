using System.ComponentModel;

using Microsoft.Extensions.Logging;

using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace RoslynSentinel.Server.Basic;

[McpServerToolType]
public class SentinelRefactoringTools
{
    private readonly RefactoringEngine _refactoringEngine;
    private readonly StandardRefactoringEngine _standardRefactoringEngine;
    // private readonly AdvancedStructuralEngine _advancedStructuralEngine;
    private readonly StructuralRefinementEngine _structuralRefinementEngine;
    private readonly MappingEngine _mappingEngine;
    private readonly SemanticRefactoringLibrary _semanticRefactoringLibrary;
    private readonly GranularRefactoringEngine _granularRefactoringEngine;
    // private readonly AdvancedLogicEngine _advancedLogicEngine;
    // private readonly RefinementEngine _refinementEngine;
    // private readonly AdvancedTypeEngine _advancedTypeEngine;
    private readonly CodeStyleEngine _codeStyleEngine;
    private readonly CodeFlowEngine _codeFlowEngine;
    // private readonly AdvancedRefactoringEngine _advancedRefactoringEngine;
    // private readonly LogicOptimizationEngine _logicOptimizationEngine;
    // private readonly OutParamRefactoringEngine _outParamRefactoringEngine;
    private readonly MsToolAugmentEngine _msToolAugmentEngine;
    private readonly CodeGenerationEngine _codeGenerationEngine;
    private readonly SymbolNavigationEngine _symbolNavigationEngine;
    private readonly IWorkspaceManager _workspaceManager;
    private readonly ValidationEngine _validationEngine;
    private readonly SentinelConfiguration _config;
    private readonly ILogger<SentinelRefactoringTools> _logger;

    public SentinelRefactoringTools(
        RefactoringEngine refactoringEngine,
        StandardRefactoringEngine standardRefactoringEngine,
        MappingEngine mappingEngine,
        SemanticRefactoringLibrary semanticRefactoringLibrary,
        GranularRefactoringEngine granularRefactoringEngine,
    // AdvancedLogicEngine advancedLogicEngine,
    // RefinementEngine refinementEngine,
    // AdvancedTypeEngine advancedTypeEngine,
    StructuralRefinementEngine structuralRefinementEngine,
    CodeStyleEngine codeStyleEngine,
        CodeFlowEngine codeFlowEngine,
        // AdvancedRefactoringEngine advancedRefactoringEngine,
        // LogicOptimizationEngine logicOptimizationEngine,
        // ModernizationEngine modernizationEngine,
        // OutParamRefactoringEngine outParamRefactoringEngine,
        MsToolAugmentEngine augmentEngine,
        CodeGenerationEngine codeGenerationEngine,
        SymbolNavigationEngine symbolNavigationEngine,
        IWorkspaceManager workspaceManager,
        ValidationEngine validationEngine,
        SentinelConfiguration config,
        ILogger<SentinelRefactoringTools> logger)
    {
        _refactoringEngine = refactoringEngine;
        _standardRefactoringEngine = standardRefactoringEngine;
        _mappingEngine = mappingEngine;
        _semanticRefactoringLibrary = semanticRefactoringLibrary;
        _granularRefactoringEngine = granularRefactoringEngine;
        // _advancedLogicEngine = advancedLogicEngine;
        // _refinementEngine = refinementEngine;
        // _advancedTypeEngine = advancedTypeEngine;
        _structuralRefinementEngine = structuralRefinementEngine;
        _codeStyleEngine = codeStyleEngine;
        _codeFlowEngine = codeFlowEngine;
        //_advancedRefactoringEngine = advancedRefactoringEngine;
        // _logicOptimizationEngine = logicOptimizationEngine;
        // _outParamRefactoringEngine = outParamRefactoringEngine;
        _msToolAugmentEngine = augmentEngine;
        _codeGenerationEngine = codeGenerationEngine;
        _symbolNavigationEngine = symbolNavigationEngine;
        _workspaceManager = workspaceManager;
        _validationEngine = validationEngine;
        _config = config;
        _logger = logger;
    }

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

    /// <summary>
    /// Guards against staging an unintended empty-file overwrite: when a document-edit engine
    /// method can't locate its target (wrong name, wrong attribute/modifier, etc.), it returns
    /// Outcome != Modified and leaves UpdatedText at its string.Empty default rather than null —
    /// so skipping this check would silently propose replacing the whole file with nothing.
    /// Returns null when updated.UpdatedText is safe to use as the new file content.
    /// </summary>
    private static ToolResult<object>? RequireUpdatedText(DocumentEditResult updated, string operationName, FilePath filePath)
    {
        if (!string.IsNullOrEmpty(updated.UpdatedText))
        {
            return null;
        }

        return new ToolResult<object>
        {
            Success = false,
            Error = new ResultError(ToolErrorCode.Exception,
                $"{operationName}: no change produced for '{filePath}' ({updated.Outcome}). {updated.Message}")
        };
    }

    /// <summary>
    /// Validates proposed changes against the current in-memory solution and, unless
    /// <paramref name="dryRun"/> is set, writes them straight to disk (write-through — no
    /// intermediate staging step). Rolls back any already-written files if a multi-file change
    /// partially fails, so a change never lands half-applied.
    /// </summary>
    private Task<ApplyOutcome> ValidateAndApplyAsync(
        Dictionary<FilePath, string> changes,
        string description,
        string operationName,
        bool dryRun = false,
        bool returnDiff = false,
        IProgress<ProgressNotificationValue>? progress = default,
        CancellationToken cancellationToken = default) =>
        ValidateAndApplyHelper.ValidateAndApplyAsync(
            _validationEngine, _workspaceManager, _logger, changes, operationName,
            dryRun, returnDiff, progress, cancellationToken);

    private Task<string> BuildDiffAsync(Dictionary<FilePath, string> changes, CancellationToken cancellationToken) =>
        ValidateAndApplyHelper.BuildDiffAsync(_workspaceManager, changes, cancellationToken);

    internal static string BuildDiffFromPreImages(
        Dictionary<FilePath, string> changes,
        IReadOnlyDictionary<string, string?>? preImages) =>
        ValidateAndApplyHelper.BuildDiffFromPreImages(changes, preImages);

    [McpServerTool(Name = "RenameSymbol")]
    [Produces(DataTag.ChangeId)]
    [Description("Renames a symbol and all its references across the solution. Returns changeId and updatedHandle for the renamed symbol. Does NOT simplify call sites or add/remove using directives — if the rename target's new name needs a namespace not already in scope at a call site, or you want to shorten a fully-qualified reference, use the UsingDirective tool separately.")]
    public async Task<ToolResult<object>> RenameSymbol(
        [Description(ToolParams.ProjectName)] string projectName,
        [Description(ToolParams.DocCommentId)] string docCommentId,
        [Description("New name for the symbol. Must be a valid C# identifier.")] string newName,
        [Description(ToolParams.SessionId)] string sessionId = "",
        [Description(ToolParams.DryRun)][ToolOption(ToolOptionTag.DryRun)] bool dryRun = false,
        [Description(ToolParams.ReturnDiff)][ToolOption(ToolOptionTag.ReturnDiff)] bool returnDiff = false,
        RequestContext<CallToolRequestParams>? requestParams = null,
        CancellationToken cancellationToken = default)
    {
        ProgressToken progressToken = requestParams?.Params?.ProgressToken ?? new ProgressToken();
        IProgress<ProgressNotificationValue> progress = new Progress<ProgressNotificationValue>(msg => requestParams?.Server?.NotifyProgressAsync(progressToken, new ProgressNotificationValue() { Progress = 10.0f }, null, cancellationToken));

        SymbolResolution resolution = await _workspaceManager.ResolveFromWireAsync(
            sessionId, projectName, docCommentId, cancellationToken);
        if (!resolution.Resolved)
        {
            return new ToolResult<object>
            {
                Success = false,
                Error = new ResultError(ToolErrorCode.Exception, resolution.Error!.Message)
            };
        }

        RenameSymbolResult result = await _refactoringEngine.RenameSymbolAsync(
            resolution.Handle, resolution.Symbol!, newName, cancellationToken);

        if (result.Error is not null)
        {
            return new ToolResult<object>
            {
                Success = false,
                Error = new ResultError(ToolErrorCode.Exception, result.Error)
            };
        }

        if (result.PendingChanges.Count == 0)
        {
            return new ToolResult<object>
            {
                Success = false,
                Error = new ResultError(ToolErrorCode.Exception,
                    $"RenameSymbol produced no file changes for '{result.OldName}' → '{result.NewName}'.")
            };
        }

        var apply = await ValidateAndApplyAsync(
            result.PendingChanges,
            $"Rename '{result.OldName}' to '{result.NewName}'.",
            "RenameSymbol", dryRun, returnDiff, cancellationToken: cancellationToken);

        if (apply.Error is not null)
            return new ToolResult<object> { Success = false, Error = apply.Error };

        return new ToolResult<object>
        {
            Success = true,
            Data = new
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
                        h.SessionId,
                        h.ProjectName,
                        h.DocCommentId
                    }
                    : null
            }
        };
    }

    [McpServerTool(Name = "GenerateMapping")]
    [Produces(DataTag.ChangeId)]
    [Description("Generates a mapping method between fromType and toType. Returns changeId.")]
    public async Task<ToolResult<object>> GenerateMapping(
        [Consumes(DataTag.SourceFilepath, required: true)] string filepath,
        [ExternalInputRequired(DataTag.DataType, required: true)] string fromType,
        [ExternalInputRequired(DataTag.DataType)] string toType,
        [Description(ToolParams.DryRun)][ToolOption(ToolOptionTag.DryRun)] bool dryRun = false,
        [Description(ToolParams.ReturnDiff)][ToolOption(ToolOptionTag.ReturnDiff)] bool returnDiff = false,
        RequestContext<CallToolRequestParams>? requestParams = null,
        CancellationToken cancellationToken = default)
    {
        FilePath filePath = FilePath.FromWire(filepath, _workspaceManager.GetSolutionRoot());
        try
        {
            ProgressToken progressToken = requestParams?.Params?.ProgressToken ?? new ProgressToken();
            IProgress<ProgressNotificationValue> progress = new Progress<ProgressNotificationValue>(msg => requestParams?.Server?.NotifyProgressAsync(progressToken, new ProgressNotificationValue() { Progress = 10.0f }, null, cancellationToken));

            var result = await _mappingEngine.GenerateMappingAsync(filePath, fromType, toType, cancellationToken);
            if (string.IsNullOrEmpty(result.UpdatedText))
                return new ToolResult<object> { Success = false, Error = new ResultError(ToolErrorCode.Exception, $"GenerateMapping produced no output for '{fromType}' → '{toType}' in '{filePath}'. Ensure both types exist in the solution.") };

            var changes = new Dictionary<FilePath, string> { [filePath] = result.UpdatedText };
            var apply = await ValidateAndApplyAsync(changes, $"Generate mapping from '{fromType}' to '{toType}'.", "GenerateMapping", dryRun, returnDiff, progress, cancellationToken);
            if (apply.Error is not null)
                return new ToolResult<object> { Success = false, Error = apply.Error };
            return new ToolResult<object> { Success = true, Data = new AppliedChangeSummary(apply.ChangeId, [filePath], $"Generates mapping from '{fromType}' to '{toType}' in {Path.GetFileName(filePath)}.", apply.DryRun, apply.Diff) };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GenerateMapping failed for '{FromType}' to '{ToType}' in '{FilePath}'", fromType, toType, filePath);
            return new ToolResult<object>() { Success = false, Error = ToolErrorMapper.ToResultError(ex, _workspaceManager, "GenerateMapping") };
        }
    }

    [McpServerTool(Name = "Member")]
    [Produces(DataTag.ChangeId)]
    [Description("Add, remove, replace, or view a type member (method, property, field, constructor). " +
        "OPERATION add: containerName required. Two modes — pass newMemberSource (raw source, e.g. 'private decimal Foo() { ... }') with " +
        "optional position (null/\"end\" to append, \"after:MemberName\", or \"before:MemberName\"); OR pass typedKind (\"property\"/\"field\") " +
        "with typedName+typedType to generate a typed member (property: hasSetter/isInit/accessibility default public; field: isReadonly/isStatic/initializer/accessibility default private). " +
        "OPERATION remove: memberName required. By default checks for callers and implementations (via FindReferences(kind: all)) and refuses if found; pass skipPrecheck: true to remove unconditionally. For zero-usages-only contract, use SafeDeleteUnusedSymbol instead. " +
        "OPERATION replace: memberName + newMemberSource required. This is the right choice even for a one-line change inside a member — don't avoid it just because the edit is small. " +
        "Prefer this over a unified diff/patch (e.g. via ApplyDiff) to edit part of a member: even though ApplyDiff tolerates modest line-number drift, a whole-member replacement can't drift out of sync the way a hand-built diff hunk can. " +
        "Read the member's current source first (e.g. via GetMethodSource/ReadFile), copy it verbatim, make your small edit in that copy, and pass the WHOLE resulting member as newSource — modifiers, signature, and body, not a fragment. " +
        "OPERATION view: containerName required. Lists the container's direct members (name, kind, signature, line range) — use this to find the exact memberName/contextSnippet to pass to remove or replace. " +
        "containerName only applies to add/view; remove/replace resolve memberName directly (optionally disambiguated via contextSnippet/lineBefore/lineAfter) regardless of container. " +
        "For overloaded targets, provide contextSnippet (distinctive substring) and optionally lineBefore/lineAfter to disambiguate. Returns changeId for add/remove/replace, member list for view.")]
    public async Task<ToolResult<object>> Member(
        [Consumes(DataTag.SourceFilepath, required: true)] string filepath,
        [Consumes(DataTag.Action, required: true)] MemberAction operation,
        [Consumes(DataTag.SymbolName, required: false)] string? containerName = null,
        [Consumes(DataTag.SymbolName, required: false)] string? memberName = null,
        [Consumes(DataTag.SourceCode, required: false)] string? newMemberSource = null,
        [ExternalInputRequired(DataTag.Position)] string? position = null,
        [ExternalInputRequired(DataTag.SymbolKind, required: false)] TypedMemberKind? typedKind = null,
        [ExternalInputRequired(DataTag.SymbolName, required: false)] string? typedName = null,
        [ExternalInputRequired(DataTag.DataType, required: false)] string? typedType = null,
        [Description(ToolParams.AccessibilityValues)][ExternalInputRequired(DataTag.Accessibility)] string accessibility = "public",
        [ExternalInputRequired(DataTag.HasSetter)] bool hasSetter = true,
        [ExternalInputRequired(DataTag.IsInit)] bool isInit = false,
        [ExternalInputRequired(DataTag.IsReadonly)] bool isReadonly = false,
        [ExternalInputRequired(DataTag.IsStatic)] bool isStatic = false,
        [ExternalInputRequired(DataTag.Initializer)] string? initializer = null,
        [Description("When false (default), refuses removal if the member has any callers or implementations (checked the same way as FindReferences(kind: all)). Set true to skip this check and remove unconditionally. remove only.")] bool skipPrecheck = false,
        [Description(ToolParams.ContextSnippet)][ExternalInputRequired(DataTag.ContextSnippet, required: false)] string? contextSnippet = null,
        [Description(ToolParams.LineBefore)][ExternalInputRequired(DataTag.LineBefore, required: false)] string? lineBefore = null,
        [Description(ToolParams.LineAfter)][ExternalInputRequired(DataTag.LineAfter, required: false)] string? lineAfter = null,
        [Description(ToolParams.AutoStage)][ToolOption(ToolOptionTag.AutoStage, required: false)] bool autoStage = true,
        [Description(ToolParams.DryRun)][ToolOption(ToolOptionTag.DryRun)] bool dryRun = false,
        [Description(ToolParams.ReturnDiff)][ToolOption(ToolOptionTag.ReturnDiff)] bool returnDiff = false,
        RequestContext<CallToolRequestParams>? requestParams = null,
        CancellationToken cancellationToken = default)
    {
        FilePath filePath = FilePath.FromWire(filepath, _workspaceManager.GetSolutionRoot());
        try
        {
            if (operation == MemberAction.view)
            {
                if (string.IsNullOrEmpty(containerName))
                    return new ToolResult<object>() { Success = false, Error = new ResultError(ToolErrorCode.InvalidArgument, "Member: containerName is required for operation 'view'.") };

                var (outcome, message, members) = await _refactoringEngine.GetContainerMembersAsync(filePath, containerName, contextSnippet, lineBefore, lineAfter, cancellationToken);
                if (outcome is EditOutcome.DocumentNotFound or EditOutcome.CannotEdit)
                    return new ToolResult<object>() { Success = false, Error = new ResultError(ToolErrorCode.Exception, $"Member: {message}") };
                return new ToolResult<object>() { Success = true, Data = new { Members = members } };
            }

            if (operation == MemberAction.replace)
            {
                if (string.IsNullOrEmpty(memberName) || string.IsNullOrEmpty(newMemberSource))
                    return new ToolResult<object>() { Success = false, Error = new ResultError(ToolErrorCode.InvalidArgument, "Member: memberName and newMemberSource are required for operation 'replace'.") };

                ProgressToken progressToken = requestParams?.Params?.ProgressToken ?? new ProgressToken();
                IProgress<ProgressNotificationValue> progress = new Progress<ProgressNotificationValue>(msg => requestParams?.Server?.NotifyProgressAsync(progressToken, new ProgressNotificationValue() { Progress = 10.0f }, null, cancellationToken));

                var result = await _refactoringEngine.ReplaceMemberAsync(filePath, memberName, newMemberSource, contextSnippet, lineBefore, lineAfter, cancellationToken);
                if (string.IsNullOrEmpty(result.UpdatedText))
                {
                    string reason = result.Outcome switch
                    {
                        EditOutcome.DocumentNotFound => $"Member: document '{filePath}' not found in the workspace.",
                        EditOutcome.SourceInvalid => $"Member: newMemberSource for '{memberName}' is not a valid member declaration. " +
                            "Provide the full member (signature + body, e.g. 'private decimal Foo() { ... }'), not just a statement or method body fragment.",
                        EditOutcome.TargetNotFound => $"Member: member '{memberName}' not found in '{filePath}'.",
                        _ => $"Member: no changes produced for '{memberName}' in '{filePath}' ({result.Outcome}). {result.Message}"
                    };
                    return new ToolResult<object> { Success = false, Error = new ResultError(ToolErrorCode.Exception, reason) };
                }

                var changes = new Dictionary<FilePath, string> { [filePath] = result.UpdatedText };
                var apply = await ValidateAndApplyAsync(changes, $"Replace member '{memberName}'.", "Member", dryRun, returnDiff, progress, cancellationToken);
                if (apply.Error is not null)
                    return new ToolResult<object> { Success = false, Error = apply.Error };
                return await ToolResult<object>.ForPossiblyLargeDataAsync(
                    new MemberChangedContentResult
                    {
                        Summary = new AppliedChangeSummary(apply.ChangeId, [filePath], $"Replaces '{memberName}' in {Path.GetFileName(filePath)}.", apply.DryRun, apply.Diff),
                        ChangedContent = newMemberSource
                    },
                    _workspaceManager.GetSolutionRoot(), "MemberChangedContent", ScanWrapperType.MemberChangedContent,
                    workspaceVersion: _workspaceManager.WorkspaceVersion);
            }

            if (operation == MemberAction.remove)
            {
                if (string.IsNullOrEmpty(memberName))
                    return new ToolResult<object>() { Success = false, Error = new ResultError(ToolErrorCode.InvalidArgument, "Member: memberName is required for operation 'remove'.") };

                if (!skipPrecheck)
                {
                    var callers = await _symbolNavigationEngine.FindCallersAsync(filePath, memberName, contextSnippet: contextSnippet, lineBefore: lineBefore, lineAfter: lineAfter, cancellationToken: cancellationToken);
                    var implementations = await _symbolNavigationEngine.FindImplementationsForMemberAsync(filePath, memberName, contextSnippet: contextSnippet, lineBefore: lineBefore, lineAfter: lineAfter, cancellationToken: cancellationToken);
                    if (callers.Count > 0 || implementations.Count > 0)
                    {
                        var parts = new List<string>();
                        if (callers.Count > 0) parts.Add($"{callers.Count} caller(s)");
                        if (implementations.Count > 0) parts.Add($"{implementations.Count} implementation(s)");
                        return new ToolResult<object>
                        {
                            Success = false,
                            Error = new ResultError(ToolErrorCode.InvalidArgument,
                                $"Member: '{memberName}' has {string.Join(" and ", parts)} — refusing to remove. " +
                                "Pass skipPrecheck: true to remove anyway, or resolve the callers/implementations first. " +
                                $"Callers: {System.Text.Json.JsonSerializer.Serialize(callers)}. " +
                                $"Implementations: {System.Text.Json.JsonSerializer.Serialize(implementations)}.")
                        };
                    }
                }

                var result = await _refactoringEngine.RemoveMemberAsync(filePath, memberName, contextSnippet, lineBefore, lineAfter);
                if (string.IsNullOrEmpty(result.UpdatedText))
                    return new ToolResult<object> { Success = false, Error = new ResultError(ToolErrorCode.Exception, $"Member: member '{memberName}' not found in '{filePath}'.") };

                var changes = new Dictionary<FilePath, string> { [filePath] = result.UpdatedText };
                var apply = await ValidateAndApplyAsync(changes, $"Remove member '{memberName}'.", "Member", dryRun, returnDiff, cancellationToken: cancellationToken);
                if (apply.Error is not null)
                    return new ToolResult<object> { Success = false, Error = apply.Error };
                return new ToolResult<object> { Success = true, Data = new AppliedChangeSummary(apply.ChangeId, [filePath], $"Removes '{memberName}' from {Path.GetFileName(filePath)}.", apply.DryRun, apply.Diff, _workspaceManager.WorkspaceVersion) };
            }

            // operation == MemberAction.add
            if (string.IsNullOrEmpty(containerName))
                return new ToolResult<object>() { Success = false, Error = new ResultError(ToolErrorCode.InvalidArgument, "Member: containerName is required for operation 'add'.") };

            var hasRawSource = !string.IsNullOrEmpty(newMemberSource);
            var hasTypedSpec = typedKind != null;
            if (hasRawSource == hasTypedSpec)
            {
                return new ToolResult<object>() { Success = false, Error = new ResultError(ToolErrorCode.InvalidArgument, "Member: for operation 'add', pass exactly one of newMemberSource (raw source) or typedKind+typedName+typedType (generated property/field).") };
            }

            DocumentEditResult updated;
            string description;
            // Only the raw-source path has the added member's exact text available verbatim
            // (the caller already supplied it). The typed-generation path (AddPropertyAsync/
            // AddFieldAsync) builds its source string inside the engine and doesn't return it
            // separately from the whole-file UpdatedText, so ChangedContent stays null there
            // rather than duplicating the engine's formatting logic at the tool layer.
            string? addedMemberSource = hasTypedSpec ? null : newMemberSource;
            if (hasTypedSpec)
            {
                if (string.IsNullOrEmpty(typedName) || string.IsNullOrEmpty(typedType))
                    return new ToolResult<object>() { Success = false, Error = new ResultError(ToolErrorCode.InvalidArgument, "Member: typedName and typedType are required when typedKind is set.") };

                if (typedKind == TypedMemberKind.property)
                {
                    updated = await _refactoringEngine.AddPropertyAsync(filePath, containerName, typedName, typedType, accessibility, hasSetter, isInit, contextSnippet, lineBefore, lineAfter);
                    description = $"Added '{typedType} {typedName}' property to '{containerName}' in {Path.GetFileName(filePath)}.";
                }
                else
                {
                    updated = await _refactoringEngine.AddFieldAsync(filePath, containerName, typedName, typedType, accessibility, isReadonly, isStatic, initializer, contextSnippet, lineBefore, lineAfter);
                    description = $"Added '{typedType} {typedName}' field to '{containerName}' in {Path.GetFileName(filePath)}.";
                }
            }
            else if (string.IsNullOrEmpty(position) || position == "end")
            {
                updated = await _refactoringEngine.AddMemberAsync(filePath, containerName, newMemberSource!, contextSnippet, lineBefore, lineAfter);
                description = $"Added new member to '{containerName}' in {Path.GetFileName(filePath)}.";
            }
            else if (position.StartsWith("after:", StringComparison.OrdinalIgnoreCase))
            {
                var afterMemberName = position.Substring("after:".Length);
                updated = await _refactoringEngine.InsertMemberAfterAsync(filePath, containerName, afterMemberName, newMemberSource!, contextSnippet, lineBefore, lineAfter);
                description = $"Inserted new member after '{afterMemberName}' in '{containerName}' in {Path.GetFileName(filePath)}.";
            }
            else if (position.StartsWith("before:", StringComparison.OrdinalIgnoreCase))
            {
                var beforeMemberName = position.Substring("before:".Length);
                updated = await _refactoringEngine.InsertMemberBeforeAsync(filePath, containerName, beforeMemberName, newMemberSource!, contextSnippet, lineBefore, lineAfter);
                description = $"Inserted new member before '{beforeMemberName}' in '{containerName}' in {Path.GetFileName(filePath)}.";
            }
            else
            {
                return new ToolResult<object>() { Success = false, Error = new ResultError(ToolErrorCode.InvalidArgument, $"Unknown position '{position}'. Valid values: null, 'end', 'after:MemberName', 'before:MemberName'.") };
            }

            if (!autoStage)
            {
                return new ToolResult<object>() { Success = true, Data = updated.ToJsonSummary() };
            }
            if (RequireUpdatedText(updated, "Member", filePath) is { } guardResult)
                return guardResult;

            var addChanges = new Dictionary<FilePath, string> { [filePath] = updated.UpdatedText! };
            var addApply = await ValidateAndApplyAsync(addChanges, description, "Member", dryRun, returnDiff, cancellationToken: cancellationToken);
            if (addApply.Error is not null)
                return new ToolResult<object> { Success = false, Error = addApply.Error };
            return await ToolResult<object>.ForPossiblyLargeDataAsync(
                new MemberChangedContentResult
                {
                    Summary = new AppliedChangeSummary(addApply.ChangeId, [filePath], description, addApply.DryRun, addApply.Diff),
                    ChangedContent = addedMemberSource ?? ""
                },
                _workspaceManager.GetSolutionRoot(), "MemberChangedContent", ScanWrapperType.MemberChangedContent,
                workspaceVersion: _workspaceManager.WorkspaceVersion);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Member ({Operation}) failed for '{ContainerOrMemberName}' in '{FilePath}'", operation, containerName ?? memberName, filePath);
            return new ToolResult<object>() { Success = false, Error = ToolErrorMapper.ToResultError(ex, _workspaceManager, "Member") };
        }
    }

    [McpServerTool(Name = "UsingDirective")]
    [Produces(DataTag.ChangeId)]
    [Description("Add, remove, or view using directives in a file. OPERATION add: inserts a using (namespaceName required; for static usings prefix with \"static \", e.g. \"static System.Math\"); no-op if already present. simplifyExisting (add only): after inserting, runs Roslyn's Simplifier (semantic-model-based, not text find/replace) over the file to shorten now-redundant fully-qualified references — it only reduces a name when doing so introduces no ambiguity. OPERATION remove: deletes the matching using directive (namespaceName required; same \"static \" prefix convention). OPERATION view: lists current using directives (name, isStatic, alias); no changes made, namespaceName not required. Returns changeId for add/remove, using list for view.")]
    public async Task<ToolResult<object>> UsingDirective(
        [Consumes(DataTag.SourceFilepath, required: true)] string filepath,
        [Consumes(DataTag.Action, required: true)] AddRemoveViewAction operation,
        [Consumes(DataTag.SymbolName, required: false)] string? namespaceName = null,
        [Description("Simplify existing fully-qualified references in the file after adding this using (add only). Uses Roslyn's Simplifier against the semantic model, not text find/replace, so it never introduces a naming collision.")] bool simplifyExisting = false,
        [Description(ToolParams.AutoStage)][ToolOption(ToolOptionTag.AutoStage, required: false)] bool autoStage = true,
        [Description(ToolParams.DryRun)][ToolOption(ToolOptionTag.DryRun)] bool dryRun = false,
        [Description(ToolParams.ReturnDiff)][ToolOption(ToolOptionTag.ReturnDiff)] bool returnDiff = false,
        // RequestContext<CallToolRequestParams> requestParams = null,
        CancellationToken cancellationToken = default)
    {
        FilePath filePath = FilePath.FromWire(filepath, _workspaceManager.GetSolutionRoot());
        try
        {
            if (operation == AddRemoveViewAction.view)
            {
                var usings = await _refactoringEngine.GetUsingDirectivesAsync(filePath, cancellationToken);
                return new ToolResult<object>() { Success = true, Data = new { Usings = usings } };
            }

            if (string.IsNullOrEmpty(namespaceName))
            {
                return new ToolResult<object>() { Success = false, Error = new ResultError(ToolErrorCode.InvalidArgument, $"UsingDirective: namespaceName is required for operation '{operation}'.") };
            }

            DocumentEditResult updated;
            string opName;
            if (operation == AddRemoveViewAction.add)
            {
                updated = await _refactoringEngine.AddUsingDirectiveAsync(filePath, namespaceName, simplifyExisting, cancellationToken);
                opName = "Add";
            }
            else
            {
                updated = await _refactoringEngine.RemoveUsingDirectiveAsync(filePath, namespaceName, cancellationToken);
                opName = "Remove";
            }

            if (!autoStage)
            {
                return new ToolResult<object>() { Success = true, Data = updated.ToJsonSummary() };
            }

            if (RequireUpdatedText(updated, "UsingDirective", filePath) is { } guardResult)
                return guardResult;

            var changes = new Dictionary<FilePath, string> { [filePath] = updated.UpdatedText! };
            var apply = await ValidateAndApplyAsync(changes, $"{opName} using {namespaceName}.", "UsingDirective", dryRun, returnDiff, cancellationToken: cancellationToken);
            if (apply.Error is not null)
                return new ToolResult<object> { Success = false, Error = apply.Error };
            var description = operation == AddRemoveViewAction.add
                ? $"Adds 'using {namespaceName};' to {Path.GetFileName(filePath)}."
                : $"Removes 'using {namespaceName};' from {Path.GetFileName(filePath)}.";
            if (operation != AddRemoveViewAction.add)
            {
                return new ToolResult<object>() { Success = true, Data = new AppliedChangeSummary(apply.ChangeId, [filePath], description, apply.DryRun, apply.Diff, _workspaceManager.WorkspaceVersion) };
            }

            // namespaceName is caller-supplied verbatim (including any "static " prefix); the
            // emitted directive text is trivially reconstructed from it rather than re-derived
            // from the engine's inserted syntax node.
            var addedUsing = $"using {namespaceName};";
            return await ToolResult<object>.ForPossiblyLargeDataAsync(
                new MemberChangedContentResult
                {
                    Summary = new AppliedChangeSummary(apply.ChangeId, [filePath], description, apply.DryRun, apply.Diff, _workspaceManager.WorkspaceVersion),
                    ChangedContent = addedUsing
                },
                _workspaceManager.GetSolutionRoot(), "MemberChangedContent", ScanWrapperType.MemberChangedContent,
                workspaceVersion: _workspaceManager.WorkspaceVersion);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "UsingDirective failed for '{Namespace}' in '{FilePath}'", namespaceName, filePath);
            return new ToolResult<object>() { Success = false, Error = ToolErrorMapper.ToResultError(ex, _workspaceManager, "UsingDirective") };
        }
    }

    [McpServerTool(Name = "ModifyEnum")]
    [Produces(DataTag.ChangeId)]
    [Description("Replaces an enum's complete member list in one operation. Values is a comma-separated list of member names in desired order (e.g. \"Pending,Shipped,Cancelled\"); append \"=N\" to set explicit value (e.g. \"Archived=99\"). Omitted names are removed; new names are added. Explicit values (\"=N\") are preserved; implicit members take next ordinal from predecessor (as if hand-typed). Pass complete list every time, not delta. For enums with same name, provide contextSnippet (distinctive substring) and optionally lineBefore/lineAfter to disambiguate. Use GetTypeInfo(typeName, include:\"members\") to see current values first. Returns changeId.")]
    public async Task<ToolResult<object>> ModifyEnum(
        [Consumes(DataTag.SourceFilepath, required: true)] string filepath,
        [Consumes(DataTag.SymbolName, required: true)] string enumName,
        [ExternalInputRequired(DataTag.SymbolName, required: true)] string values,
        [Description(ToolParams.ContextSnippet)][ExternalInputRequired(DataTag.ContextSnippet, required: false)] string? contextSnippet = null,
        [Description(ToolParams.LineBefore)][ExternalInputRequired(DataTag.LineBefore, required: false)] string? lineBefore = null,
        [Description(ToolParams.LineAfter)][ExternalInputRequired(DataTag.LineAfter, required: false)] string? lineAfter = null,
        [Description(ToolParams.AutoStage)][ToolOption(ToolOptionTag.AutoStage, required: false)] bool autoStage = true,
        [Description(ToolParams.DryRun)][ToolOption(ToolOptionTag.DryRun)] bool dryRun = false,
        [Description(ToolParams.ReturnDiff)][ToolOption(ToolOptionTag.ReturnDiff)] bool returnDiff = false,
        // RequestContext<CallToolRequestParams> requestParams = null,
        CancellationToken cancellationToken = default)
    {
        FilePath filePath = FilePath.FromWire(filepath, _workspaceManager.GetSolutionRoot());
        try
        {
            var updated = await _refactoringEngine.ModifyEnumAsync(filePath, enumName, values, contextSnippet, lineBefore, lineAfter);
            if (!autoStage)
            {
                return new ToolResult<object>() { Success = true, Data = updated.ToJsonSummary() };
            }

            if (RequireUpdatedText(updated, "ModifyEnum", filePath) is { } guardResult)
                return guardResult;

            var description = string.IsNullOrEmpty(updated.Message)
                ? $"Sets '{enumName}' members in {Path.GetFileName(filePath)} to match the requested list."
                : $"'{enumName}' in {Path.GetFileName(filePath)}: {updated.Message}.";

            var changes = new Dictionary<FilePath, string> { [filePath] = updated.UpdatedText! };
            var apply = await ValidateAndApplyAsync(changes, description, "ModifyEnum", dryRun, returnDiff, cancellationToken: cancellationToken);
            if (apply.Error is not null)
                return new ToolResult<object> { Success = false, Error = apply.Error };
            return new ToolResult<object>() { Success = true, Data = new AppliedChangeSummary(apply.ChangeId, [filePath], description, apply.DryRun, apply.Diff, _workspaceManager.WorkspaceVersion) };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ModifyEnum failed for '{EnumName}' in '{FilePath}'", enumName, filePath);
            return new ToolResult<object>() { Success = false, Error = ToolErrorMapper.ToResultError(ex, _workspaceManager, "ModifyEnum") };
        }
    }

    [McpServerTool(Name = "ChangeAccessibility")]
    [Produces(DataTag.ChangeId)]
    [Description("Changes the accessibility modifier (private, public, internal, protected, protected internal, private protected) of a type or member. For overloaded members, provide contextSnippet (distinctive substring) and optionally lineBefore/lineAfter to disambiguate. This tool covers accessibility only — use ChangeAccessibility for modifiers, ModifyAttribute for [Attribute] syntax, and ModifyModifier for non-accessibility keywords. Returns changeId.")]
    public async Task<ToolResult<object>> ChangeAccessibility(
        [Consumes(DataTag.SourceFilepath, required: true)] string filepath,
        [Consumes(DataTag.SymbolName, required: true)] string targetName,
        [Description(ToolParams.AccessibilityValues)][ExternalInputRequired(DataTag.Accessibility, required: true)] string accessibility,
        [Description(ToolParams.ContextSnippet)][ExternalInputRequired(DataTag.ContextSnippet, required: false)] string? contextSnippet = null,
        [Description(ToolParams.LineBefore)][ExternalInputRequired(DataTag.LineBefore, required: false)] string? lineBefore = null,
        [Description(ToolParams.LineAfter)][ExternalInputRequired(DataTag.LineAfter, required: false)] string? lineAfter = null,
        [Description(ToolParams.AutoStage)][ToolOption(ToolOptionTag.AutoStage, required: false)] bool autoStage = true,
        [Description(ToolParams.DryRun)][ToolOption(ToolOptionTag.DryRun)] bool dryRun = false,
        [Description(ToolParams.ReturnDiff)][ToolOption(ToolOptionTag.ReturnDiff)] bool returnDiff = false,
        // RequestContext<CallToolRequestParams> requestParams = null,
        CancellationToken cancellationToken = default)
    {
        FilePath filePath = FilePath.FromWire(filepath, _workspaceManager.GetSolutionRoot());
        try
        {
            var updated = await _refactoringEngine.ChangeAccessibilityAsync(filePath, targetName, accessibility, contextSnippet, lineBefore, lineAfter);
            if (!autoStage)
            {
                return new ToolResult<object>() { Success = true, Data = updated.ToJsonSummary() };
            }

            if (RequireUpdatedText(updated, "ChangeAccessibility", filePath) is { } guardResult)
                return guardResult;

            var changes = new Dictionary<FilePath, string> { [filePath] = updated.UpdatedText! };
            var apply = await ValidateAndApplyAsync(changes, $"Change accessibility of '{targetName}' to '{accessibility}'.", "ChangeAccessibility", dryRun, returnDiff, cancellationToken: cancellationToken);
            if (apply.Error is not null)
                return new ToolResult<object> { Success = false, Error = apply.Error };
            // No ChangedContent here: the only "new" text is the accessibility keyword itself,
            // which the caller already passed in verbatim — echoing it back adds nothing the
            // caller doesn't already have, unlike a reconstructed multi-part snippet.
            return new ToolResult<object>() { Success = true, Data = new AppliedChangeSummary(apply.ChangeId, [filePath], $"Changes accessibility of '{targetName}' to '{accessibility}' in {Path.GetFileName(filePath)}.", apply.DryRun, apply.Diff, _workspaceManager.WorkspaceVersion) };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ChangeAccessibility failed for '{TargetName}' in '{FilePath}'", targetName, filePath);
            return new ToolResult<object>() { Success = false, Error = ToolErrorMapper.ToResultError(ex, _workspaceManager, "ChangeAccessibility") };
        }
    }

    [McpServerTool(Name = "SummaryComment")]
    [Produces(DataTag.ChangeId)]
    [Description("Add, remove, or view a /// <summary> XML doc comment on a type or member. OPERATION add: adds or replaces the summary (summaryText required); replaces any existing summary. OPERATION remove: deletes the summary comment if present; no-op if none exists. OPERATION view: returns the current summary text (or null if none); no changes made. For overloaded targets, provide contextSnippet (distinctive substring) and optionally lineBefore/lineAfter to disambiguate. Returns changeId for add/remove, summary text for view.")]
    public async Task<ToolResult<object>> SummaryComment(
        [Consumes(DataTag.SourceFilepath, required: true)] string filepath,
        [Consumes(DataTag.Action, required: true)] AddRemoveViewAction operation,
        [Consumes(DataTag.SymbolName, required: true)] string targetName,
        string? summaryText = null,
        [Description(ToolParams.ContextSnippet)][ExternalInputRequired(DataTag.ContextSnippet, required: false)] string? contextSnippet = null,
        [Description(ToolParams.LineBefore)][ExternalInputRequired(DataTag.LineBefore, required: false)] string? lineBefore = null,
        [Description(ToolParams.LineAfter)][ExternalInputRequired(DataTag.LineAfter, required: false)] string? lineAfter = null,
        [Description(ToolParams.ContainingTypeName)] string? containingTypeName = null,
        [Description(ToolParams.AutoStage)] bool autoStage = true,
        [Description(ToolParams.DryRun)][ToolOption(ToolOptionTag.DryRun)] bool dryRun = false,
        [Description(ToolParams.ReturnDiff)][ToolOption(ToolOptionTag.ReturnDiff)] bool returnDiff = false,
        // RequestContext<CallToolRequestParams> requestParams = null,
        CancellationToken cancellationToken = default)
    {
        FilePath filePath = FilePath.FromWire(filepath, _workspaceManager.GetSolutionRoot());
        try
        {
            if (operation == AddRemoveViewAction.view)
            {
                var (outcome, message, text) = await _refactoringEngine.GetSummaryCommentAsync(filePath, targetName, contextSnippet, lineBefore, lineAfter, containingTypeName, cancellationToken);
                if (outcome is EditOutcome.DocumentNotFound or EditOutcome.CannotEdit)
                    return new ToolResult<object>() { Success = false, Error = new ResultError(ToolErrorCode.Exception, $"SummaryComment: {message}") };
                return new ToolResult<object>() { Success = true, Data = new { SummaryText = text } };
            }

            if (operation == AddRemoveViewAction.add && string.IsNullOrEmpty(summaryText))
            {
                return new ToolResult<object>() { Success = false, Error = new ResultError(ToolErrorCode.InvalidArgument, "SummaryComment: summaryText is required for operation 'add'.") };
            }

            var updated = operation == AddRemoveViewAction.add
                ? await _refactoringEngine.AddSummaryCommentAsync(filePath, targetName, summaryText!, contextSnippet, lineBefore, lineAfter, containingTypeName)
                : await _refactoringEngine.RemoveSummaryCommentAsync(filePath, targetName, contextSnippet, lineBefore, lineAfter, containingTypeName, cancellationToken);

            if (!autoStage)
            {
                return new ToolResult<object>() { Success = true, Data = updated.ToJsonSummary() };
            }

            if (RequireUpdatedText(updated, "SummaryComment", filePath) is { } guardResult)
                return guardResult;

            var description = operation == AddRemoveViewAction.add
                ? $"Added XML summary comment to '{targetName}' in {Path.GetFileName(filePath)}."
                : $"Removed XML summary comment from '{targetName}' in {Path.GetFileName(filePath)}.";

            var changes = new Dictionary<FilePath, string> { [filePath] = updated.UpdatedText! };
            var apply = await ValidateAndApplyAsync(changes, description, "SummaryComment", dryRun, returnDiff, cancellationToken: cancellationToken);
            if (apply.Error is not null)
                return new ToolResult<object> { Success = false, Error = apply.Error };
            var summary = new AppliedChangeSummary(apply.ChangeId, [filePath], description, apply.DryRun, apply.Diff);

            // add: summaryText is caller-supplied verbatim, echoed back as the added content
            // (same reasoning as Member(add)'s raw-source path). remove has no new content to show.
            if (operation != AddRemoveViewAction.add)
            {
                return new ToolResult<object>() { Success = true, Data = summary };
            }

            return await ToolResult<object>.ForPossiblyLargeDataAsync(
                new MemberChangedContentResult { Summary = summary, ChangedContent = summaryText! },
                _workspaceManager.GetSolutionRoot(), "MemberChangedContent", ScanWrapperType.MemberChangedContent,
                workspaceVersion: _workspaceManager.WorkspaceVersion);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SummaryComment failed for '{TargetName}' in '{FilePath}'", targetName, filePath);
            return new ToolResult<object>() { Success = false, Error = ToolErrorMapper.ToResultError(ex, _workspaceManager, "SummaryComment") };
        }
    }

    [McpServerTool(Name = "ConstructorParameter")]
    [Produces(DataTag.ChangeId)]
    [Description("Add, remove, or view DI constructor parameters on a class. OPERATION add: creates private readonly field, parameter, and body assignment in one step (paramName, paramType required). fieldName overrides the default derived field name (_camelCase); passing fieldName equal to paramName or its underscore-prefixed form both resolve to '_paramName', never a bare name that would collide with the parameter. Creates a constructor if none exists. OPERATION remove: deletes the parameter and its assignment statement (paramName required); the backing field is only deleted if a solution-wide reference check confirms nothing else in the class still uses it — otherwise it's left in place. OPERATION view: lists current constructor parameters and their inferred backing fields; no changes made, paramName/paramType not required. For classes with the same name in the same file, provide contextSnippet (distinctive substring) and optionally lineBefore/lineAfter to disambiguate. Returns changeId for add/remove, parameter list for view.")]
    public async Task<ToolResult<object>> ConstructorParameter([Consumes(DataTag.SourceFilepath, required: true)] string filepath,
        [Consumes(DataTag.Action, required: true)] AddRemoveViewAction operation,
        [Consumes(DataTag.ClassName, required: true)] string className,
        [Consumes(DataTag.SymbolName, required: false)] string? paramName = null,
        [Consumes(DataTag.DataType, required: false)] string? paramType = null,
        [Consumes(DataTag.SymbolName, required: false)] string? fieldName = null,
        [Description(ToolParams.ContextSnippet)][ExternalInputRequired(DataTag.ContextSnippet, required: false)] string? contextSnippet = null,
        [Description(ToolParams.LineBefore)][ExternalInputRequired(DataTag.LineBefore, required: false)] string? lineBefore = null,
        [Description(ToolParams.LineAfter)][ExternalInputRequired(DataTag.LineAfter, required: false)] string? lineAfter = null,
        [Description(ToolParams.AutoStage)][ToolOption(ToolOptionTag.AutoStage, required: false)] bool autoStage = true,
        [Description(ToolParams.DryRun)][ToolOption(ToolOptionTag.DryRun)] bool dryRun = false,
        [Description(ToolParams.ReturnDiff)][ToolOption(ToolOptionTag.ReturnDiff)] bool returnDiff = false,
        // RequestContext<CallToolRequestParams> requestParams = null,
        CancellationToken cancellationToken = default)
    {
        FilePath filePath = FilePath.FromWire(filepath, _workspaceManager.GetSolutionRoot());
        try
        {
            if (operation == AddRemoveViewAction.view)
            {
                var (outcome, message, parameters) = await _refactoringEngine.GetConstructorParametersAsync(filePath, className, contextSnippet, lineBefore, lineAfter, cancellationToken);
                if (outcome is EditOutcome.DocumentNotFound or EditOutcome.CannotEdit)
                    return new ToolResult<object>() { Success = false, Error = new ResultError(ToolErrorCode.Exception, $"ConstructorParameter: {message}") };
                return new ToolResult<object>() { Success = true, Data = new { Parameters = parameters } };
            }

            if (string.IsNullOrEmpty(paramName))
            {
                return new ToolResult<object>() { Success = false, Error = new ResultError(ToolErrorCode.InvalidArgument, $"ConstructorParameter: paramName is required for operation '{operation}'.") };
            }

            if (operation == AddRemoveViewAction.add && string.IsNullOrEmpty(paramType))
            {
                return new ToolResult<object>() { Success = false, Error = new ResultError(ToolErrorCode.InvalidArgument, "ConstructorParameter: paramType is required for operation 'add'.") };
            }

            DocumentEditResult updated;
            string resolvedFieldName;
            if (operation == AddRemoveViewAction.add)
            {
                updated = await _refactoringEngine.AddConstructorParameterAsync(filePath, className, paramName, paramType!, fieldName, contextSnippet, lineBefore, lineAfter);
                // updated.Message carries "// paramName='x', fieldName='_x'" on success — surface the
                // resolved field name explicitly since it may differ from what the caller passed
                // (see fieldName/paramName collision disambiguation in AddConstructorParameterAsync).
                resolvedFieldName = updated.Message is { Length: > 0 } msg
                    && System.Text.RegularExpressions.Regex.Match(msg, "fieldName='([^']*)'") is { Success: true } m
                    ? m.Groups[1].Value
                    : $"_{char.ToLower(paramName[0])}{paramName[1..]}";
            }
            else
            {
                updated = await _refactoringEngine.RemoveConstructorParameterAsync(filePath, className, paramName, contextSnippet, lineBefore, lineAfter, cancellationToken);
                resolvedFieldName = updated.Message is { Length: > 0 } msg
                    && System.Text.RegularExpressions.Regex.Match(msg, "fieldName='([^']*)'") is { Success: true } m
                    ? m.Groups[1].Value
                    : "";
            }

            if (!autoStage)
            {
                return new ToolResult<object>() { Success = true, Data = updated.ToJsonSummary() };
            }

            if (RequireUpdatedText(updated, "ConstructorParameter", filePath) is { } guardResult)
                return guardResult;

            var description = operation == AddRemoveViewAction.add
                ? $"Added '{paramType} {paramName}' DI parameter to '{className}' in {Path.GetFileName(filePath)}, backed by field '{resolvedFieldName}'."
                : $"Removed '{paramName}' DI parameter from '{className}' in {Path.GetFileName(filePath)}."
                    + (updated.Message?.Contains("fieldRemoved='True'") == true ? $" Also removed unused backing field '{resolvedFieldName}'." : "");

            var changes = new Dictionary<FilePath, string> { [filePath] = updated.UpdatedText! };
            var apply = await ValidateAndApplyAsync(changes, description, "ConstructorParameter", dryRun, returnDiff, cancellationToken: cancellationToken);
            if (apply.Error is not null)
                return new ToolResult<object> { Success = false, Error = apply.Error };

            // Added-parameter text is reconstructed here (paramType/paramName are already known)
            // rather than extracted from AddConstructorParameterAsync's internal formatting — same
            // reasoning as Member(add)'s typed-generation path. Remove has no new content to show.
            var changedContent = operation == AddRemoveViewAction.add ? $"{paramType} {paramName}" : "";
            return await ToolResult<object>.ForPossiblyLargeDataAsync(
                new MemberChangedContentResult
                {
                    Summary = new AppliedChangeSummary(apply.ChangeId, [filePath], description, apply.DryRun, apply.Diff),
                    ChangedContent = changedContent
                },
                _workspaceManager.GetSolutionRoot(), "MemberChangedContent", ScanWrapperType.MemberChangedContent,
                workspaceVersion: _workspaceManager.WorkspaceVersion);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ConstructorParameter failed for '{ClassName}' in '{FilePath}'", className, filePath);
            return new ToolResult<object>() { Success = false, Error = ToolErrorMapper.ToResultError(ex, _workspaceManager, "ConstructorParameter") };
        }
    }

    [McpServerTool(Name = "ExtractLocalVariable")]
    [Produces(DataTag.ChangeId)]
    [Description("Extracts an inline expression into a named local variable declaration. exactExpressionText is NOT a search fragment (unlike contextSnippet on other tools) — it must be the WHOLE expression to extract, copied verbatim.")]
    public async Task<ToolResult<object>> ExtractLocalVariable(
        [Consumes(DataTag.SourceFilepath, required: true)] string filepath,
        [Description("The exact expression to extract, copied VERBATIM character-for-character from a prior ReadFile/GetMethodSource result — the whole expression, not a shortened/unique fragment. This is NOT a search anchor like contextSnippet on other tools: it must match the target expression's full text exactly (whitespace differences are tolerated, but the expression itself must be complete). A partial expression may still resolve to the nearest enclosing expression rather than the one you intended, silently extracting the wrong span — if in doubt, include the whole expression, not less.")]
        [Consumes(DataTag.ContextSnippet, required: true)] string exactExpressionText,
        [Consumes(DataTag.SymbolName)] string variableName,
        [Description(ToolParams.LineBefore)][ExternalInputRequired(DataTag.LineBefore)] string? lineBefore = null,
        [Description(ToolParams.LineAfter)][ExternalInputRequired(DataTag.LineAfter)] string? lineAfter = null,
        [Description(ToolParams.DryRun)][ToolOption(ToolOptionTag.DryRun)] bool dryRun = false,
        [Description(ToolParams.ReturnDiff)][ToolOption(ToolOptionTag.ReturnDiff)] bool returnDiff = false,
        // RequestContext<CallToolRequestParams> requestParams = null,
        CancellationToken cancellationToken = default)
    {
        FilePath filePath = FilePath.FromWire(filepath, _workspaceManager.GetSolutionRoot());
        try
        {
            var result = await _refactoringEngine.ExtractLocalVariableAsync(filePath, exactExpressionText, variableName, lineBefore, lineAfter);
            if (string.IsNullOrEmpty(result.UpdatedText))
            {
                string reason = result.Outcome switch
                {
                    EditOutcome.DocumentNotFound => $"ExtractLocalVariable: document '{filePath}' not found in the workspace.",
                    EditOutcome.SourceInvalid => $"ExtractLocalVariable: exactExpressionText not found in '{filePath}'. {result.Message}",
                    EditOutcome.CannotConvert => $"ExtractLocalVariable: could not extract '{variableName}' in '{filePath}'. {result.Message}",
                    _ => $"ExtractLocalVariable: no change produced for '{variableName}' in '{filePath}' ({result.Outcome}). {result.Message}"
                };
                return new ToolResult<object>() { Success = false, Error = new ResultError(ToolErrorCode.Exception, reason) };
            }

            var changes = new Dictionary<FilePath, string> { [filePath] = result.UpdatedText };
            var apply = await ValidateAndApplyAsync(changes, $"Extract local variable '{variableName}'.", "ExtractLocalVariable", dryRun, returnDiff, cancellationToken: cancellationToken);
            if (apply.Error is not null)
                return new ToolResult<object> { Success = false, Error = apply.Error };
            return new ToolResult<object> { Success = true, Data = new AppliedChangeSummary(apply.ChangeId, [filePath], $"Extracts '{variableName}' as a local variable in {Path.GetFileName(filePath)}.", apply.DryRun, apply.Diff) };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ExtractLocalVariable failed for '{VariableName}' in '{FilePath}'", variableName, filePath);
            return new ToolResult<object>() { Success = false, Error = ToolErrorMapper.ToResultError(ex, _workspaceManager, "ExtractLocalVariable") };
        }
    }

    [McpServerTool(Name = "ExtractMethodSafe")]
    [Produces(DataTag.ChangeId)]
    [Description("Extracts selected statements into a new method with the correct return type inferred from the selection. newMethodName must be a valid C# identifier. exactSourceBlock is NOT a search fragment (unlike contextSnippet on other tools) — the entire range you want extracted must appear in it verbatim, since its matched span IS the extraction boundary; a too-short excerpt silently extracts only that narrower range, not the whole intended block. Written to disk (or staged, per autoStage) like other refactoring tools — not preview-only. Returns changeId.")]
    // Fixes MS BUG: where selections ending with "return <expression>" are extracted into a method declared "private void MethodName(...)", causing a compile error. This tool uses Roslyn's SemanticModel to determine the actual type of the returned expression, and DataFlowAnalysis to find the correct parameter list. Requires a loaded solution (via set_solution_path or equivalent).
    public async Task<ToolResult<object>> ExtractMethodSafe(
        [Consumes(DataTag.SourceFilepath, required: true)] string filepath,
        [ExternalInputRequired(DataTag.MethodName, required: true)] string newMethodName,
        [Description("The exact statements to extract, copied VERBATIM character-for-character from a prior ReadFile/GetMethodSource result — not retyped from memory, not a shortened/unique fragment. This is NOT a search anchor like contextSnippet on other tools: the whole extracted range (every statement, including blank lines/comments within it, exactly as they appear in the file) must be present here, because the matched span directly becomes the extraction boundary. Passing only part of the intended range (e.g. just the first statement) will silently extract only that part, stranding the rest — some ambiguous narrow selections are refused with an error, but do not rely on that guard catching every case; when in doubt, include more of the surrounding block, not less.")]
        [Consumes(DataTag.ContextSnippet, required: true)] string exactSourceBlock,
        [Description(ToolParams.LineBefore)][ExternalInputRequired(DataTag.LineBefore)] string? lineBefore = null,
        [Description(ToolParams.LineAfter)][ExternalInputRequired(DataTag.LineAfter)] string? lineAfter = null,
        [Description(ToolParams.AutoStage)][ToolOption(ToolOptionTag.AutoStage, required: false)] bool autoStage = true,
        [Description(ToolParams.DryRun)][ToolOption(ToolOptionTag.DryRun)] bool dryRun = false,
        [Description(ToolParams.ReturnDiff)][ToolOption(ToolOptionTag.ReturnDiff)] bool returnDiff = false,
        // RequestContext<CallToolRequestParams> requestParams = null,
        CancellationToken cancellationToken = default)
    {
        FilePath filePath = FilePath.FromWire(filepath, _workspaceManager.GetSolutionRoot());
        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation("ExtractMethodSafe: {File} method={Name}", filePath, newMethodName);
        }
        try
        {
            var result = await _msToolAugmentEngine.ExtractMethodSafeAsync(
                filePath, newMethodName, exactSourceBlock, lineBefore, lineAfter, cancellationToken: cancellationToken);

            if (!result.Success)
            {
                return new ToolResult<object>
                {
                    Success = false,
                    Error = new ResultError(ToolErrorCode.Exception, $"ExtractMethodSafe: {result.Error}")
                };
            }

            if (!autoStage)
            {
                return new ToolResult<object> { Success = true, Data = result };
            }

            if (string.IsNullOrEmpty(result.UpdatedContent))
            {
                return new ToolResult<object>
                {
                    Success = false,
                    Error = new ResultError(ToolErrorCode.Exception, $"ExtractMethodSafe: no change produced for '{filePath}'.")
                };
            }

            var changes = new Dictionary<FilePath, string> { [filePath] = result.UpdatedContent };
            var apply = await ValidateAndApplyAsync(changes, $"Extract '{newMethodName}' from '{filePath}'.", "ExtractMethodSafe", dryRun, returnDiff, cancellationToken: cancellationToken);
            if (apply.Error is not null)
                return new ToolResult<object> { Success = false, Error = apply.Error };
            return new ToolResult<object>
            {
                Success = true,
                Data = new AppliedChangeSummary(apply.ChangeId, [filePath], $"Extracts '{newMethodName}' into a new method in {Path.GetFileName(filePath)}.", apply.DryRun, apply.Diff, _workspaceManager.WorkspaceVersion)
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ExtractMethodSafe failed for '{NewMethodName}' in '{FilePath}'", newMethodName, filePath);
            return new ToolResult<object>
            {
                Success = false,
                Error = ToolErrorMapper.ToResultError(ex, _workspaceManager, "ExtractMethodSafe")
            };
        }
    }

    [McpServerTool(Name = "ModifyAttribute")]
    [Produces(DataTag.ChangeId)]
    [Description("Adds, replaces, or removes an attribute (with [Attribute] syntax) on a type or member. Action: add/replace/remove. existingAttribute name can include or omit brackets. newAttribute required for replace. For overloaded targets, provide contextSnippet (distinctive substring) and optionally lineBefore/lineAfter to disambiguate. This tool is for [Attribute] syntax only — use ChangeAccessibility for accessibility and ModifyModifier for non-accessibility keywords. Returns changeId.")]
    public async Task<ToolResult<object>> ModifyAttribute(
        [Consumes(DataTag.SourceFilepath, required: true)] string filepath,
        [Consumes(DataTag.SymbolName, required: true)] string targetName,
        [ExternalInputRequired(DataTag.AttributeName, required: true)] string existingAttribute,
        [ExternalInputRequired(DataTag.AttributeName, required: false)] string newAttribute,
        [ExternalInputRequired(DataTag.Action, required: true)] AttributeModifyAction action,
        [Description(ToolParams.ContextSnippet)][ExternalInputRequired(DataTag.ContextSnippet, required: false)] string? contextSnippet = null,
        [Description(ToolParams.LineBefore)][ExternalInputRequired(DataTag.LineBefore, required: false)] string? lineBefore = null,
        [Description(ToolParams.LineAfter)][ExternalInputRequired(DataTag.LineAfter, required: false)] string? lineAfter = null,
        [Description(ToolParams.AutoStage)][ToolOption(ToolOptionTag.AutoStage, required: false)] bool autoStage = true,
        [Description(ToolParams.DryRun)][ToolOption(ToolOptionTag.DryRun)] bool dryRun = false,
        [Description(ToolParams.ReturnDiff)][ToolOption(ToolOptionTag.ReturnDiff)] bool returnDiff = false,
        // RequestContext<CallToolRequestParams> requestParams = null,
        CancellationToken cancellationToken = default)
    {
        FilePath filePath = FilePath.FromWire(filepath, _workspaceManager.GetSolutionRoot());
        try
        {
            DocumentEditResult updated;
            if (action == AttributeModifyAction.add)
            {
                updated = await _refactoringEngine.AddAttributeAsync(filePath, targetName, existingAttribute, contextSnippet, lineBefore, lineAfter);
            }
            else if (action == AttributeModifyAction.replace)
            {
                updated = await _refactoringEngine.ReplaceAttributeAsync(filePath, targetName, existingAttribute, newAttribute, contextSnippet, lineBefore, lineAfter);
            }
            else if (action == AttributeModifyAction.remove)
            {
                updated = await _refactoringEngine.RemoveAttributeAsync(filePath, targetName, existingAttribute, contextSnippet, lineBefore, lineAfter);
            }
            else
            {
                return new ToolResult<object>() { Success = false, Error = new ResultError(ToolErrorCode.InvalidArgument, $"Unhandled action '{action}'.") };
            }
            if (!autoStage)
            {
                return new ToolResult<object>() { Success = true, Data = updated.ToJsonSummary() };
            }
            if (RequireUpdatedText(updated, "ModifyAttribute", filePath) is { } guardResult)
                return guardResult;

            var changes = new Dictionary<FilePath, string> { [filePath] = updated.UpdatedText! };
            var apply = await ValidateAndApplyAsync(changes, $"{action} attribute '{existingAttribute}' on '{targetName}'.", "ModifyAttribute", dryRun, returnDiff, cancellationToken: cancellationToken);
            if (apply.Error is not null)
                return new ToolResult<object> { Success = false, Error = apply.Error };
            var summary = new AppliedChangeSummary(apply.ChangeId, [filePath], $"{(action == AttributeModifyAction.add ? "Adds" : action == AttributeModifyAction.replace ? "Replaces" : "Removes")} '{existingAttribute}' attribute on '{targetName}' in {Path.GetFileName(filePath)}.", apply.DryRun, apply.Diff);

            // add/replace: existingAttribute (add) or newAttribute (replace) already holds the
            // exact attribute source the caller composed — echoed back verbatim, same reasoning
            // as Member(add)'s raw-source path. remove has no new content to show.
            if (action == AttributeModifyAction.remove)
            {
                return new ToolResult<object>() { Success = true, Data = summary };
            }

            var changedAttribute = action == AttributeModifyAction.add ? existingAttribute : newAttribute;
            return await ToolResult<object>.ForPossiblyLargeDataAsync(
                new MemberChangedContentResult { Summary = summary, ChangedContent = changedAttribute ?? "" },
                _workspaceManager.GetSolutionRoot(), "MemberChangedContent", ScanWrapperType.MemberChangedContent,
                workspaceVersion: _workspaceManager.WorkspaceVersion);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ModifyAttribute failed for '{TargetName}' in '{FilePath}'", targetName, filePath);
            return new ToolResult<object>() { Success = false, Error = ToolErrorMapper.ToResultError(ex, _workspaceManager, "ModifyAttribute") };
        }
    }

    [McpServerTool(Name = "ModifyModifier")]
    [Produces(DataTag.ChangeId)]
    [Description("Adds or removes non-accessibility modifier keywords: virtual, abstract, sealed, static, readonly, override, partial, async, new, extern, unsafe, volatile. Action: add or remove. For overloaded targets, provide contextSnippet (distinctive substring) and optionally lineBefore/lineAfter to disambiguate. Does NOT cover accessibility (private/public/etc.) — use ChangeAccessibility for those, or ModifyAttribute for [Attribute] syntax. Returns changeId.")]
    public async Task<ToolResult<object>> ModifyModifier(
        [Consumes(DataTag.SourceFilepath, required: true)] string filepath,
        [Consumes(DataTag.SymbolName, required: true)] string targetName,
        [ExternalInputRequired(DataTag.Modifier, required: true)] string modifier,
        [Consumes(DataTag.Action, required: true)] AddRemoveAction action,
        [Description(ToolParams.ContextSnippet)][ExternalInputRequired(DataTag.ContextSnippet, required: false)] string? contextSnippet = null,
        [Description(ToolParams.LineBefore)][ExternalInputRequired(DataTag.LineBefore, required: false)] string? lineBefore = null,
        [Description(ToolParams.LineAfter)][ExternalInputRequired(DataTag.LineAfter, required: false)] string? lineAfter = null,
        [Description(ToolParams.AutoStage)][ToolOption(ToolOptionTag.AutoStage, required: false)] bool autoStage = true,
        [Description(ToolParams.DryRun)][ToolOption(ToolOptionTag.DryRun)] bool dryRun = false,
        [Description(ToolParams.ReturnDiff)][ToolOption(ToolOptionTag.ReturnDiff)] bool returnDiff = false,
        // RequestContext<CallToolRequestParams> requestParams = null,
        CancellationToken cancellationToken = default)
    {
        FilePath filePath = FilePath.FromWire(filepath, _workspaceManager.GetSolutionRoot());
        try
        {
            DocumentEditResult updated;
            if (action == AddRemoveAction.add)
            {
                updated = await _refactoringEngine.AddModifierAsync(filePath, targetName, modifier, contextSnippet, lineBefore, lineAfter);
            }
            else if (action == AddRemoveAction.remove)
            {
                updated = await _refactoringEngine.RemoveModifierAsync(filePath, targetName, modifier, contextSnippet, lineBefore, lineAfter);
            }
            else
            {
                return new ToolResult<object>() { Success = false, Error = new ResultError(ToolErrorCode.InvalidArgument, $"Unhandled action '{action}'.") };
            }
            if (!autoStage)
            {
                return new ToolResult<object>() { Success = true, Data = updated.ToJsonSummary() };
            }
            if (RequireUpdatedText(updated, "ModifyModifier", filePath) is { } guardResult)
                return guardResult;

            var changes = new Dictionary<FilePath, string> { [filePath] = updated.UpdatedText! };
            var apply = await ValidateAndApplyAsync(changes, $"{action} '{modifier}' modifier on '{targetName}'.", "ModifyModifier", dryRun, returnDiff, cancellationToken: cancellationToken);
            if (apply.Error is not null)
                return new ToolResult<object> { Success = false, Error = apply.Error };
            // No ChangedContent: the only "new" text is the single modifier keyword the caller
            // already passed in — same reasoning as ChangeAccessibility.
            var summary = new AppliedChangeSummary(apply.ChangeId, [filePath], $"{(action == AddRemoveAction.add ? "Adds" : "Removes")} '{modifier}' modifier on '{targetName}' in {Path.GetFileName(filePath)}.", apply.DryRun, apply.Diff);
            return new ToolResult<object>() { Success = true, Data = summary };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ModifyModifier failed for '{TargetName}' in '{FilePath}'", targetName, filePath);
            return new ToolResult<object>() { Success = false, Error = ToolErrorMapper.ToResultError(ex, _workspaceManager, "ModifyModifier") };
        }
    }

    [McpServerTool(Name = "ModifyBaseType")]
    [Produces(DataTag.ChangeId)]
    [Description("Adds or removes a base type or interface from a type declaration. Action: add or remove. For types with the same name in the same file, provide contextSnippet (distinctive substring) and optionally lineBefore/lineAfter to disambiguate. Returns changeId.")]
    public async Task<ToolResult<object>> ModifyBaseType(
        [Consumes(DataTag.SourceFilepath, required: true)] string filepath,
        [Consumes(DataTag.SymbolName, required: true)] string typeName,
        string baseTypeName,
        AddRemoveAction action,
        [Description(ToolParams.ContextSnippet)][ExternalInputRequired(DataTag.ContextSnippet, required: false)] string? contextSnippet = null,
        [Description(ToolParams.LineBefore)][ExternalInputRequired(DataTag.LineBefore, required: false)] string? lineBefore = null,
        [Description(ToolParams.LineAfter)][ExternalInputRequired(DataTag.LineAfter, required: false)] string? lineAfter = null,
        [Description(ToolParams.AutoStage)] bool autoStage = true,
        [Description(ToolParams.DryRun)][ToolOption(ToolOptionTag.DryRun)] bool dryRun = false,
        [Description(ToolParams.ReturnDiff)][ToolOption(ToolOptionTag.ReturnDiff)] bool returnDiff = false,
        // RequestContext<CallToolRequestParams> requestParams = null,
        CancellationToken cancellationToken = default)
    {
        FilePath filePath = FilePath.FromWire(filepath, _workspaceManager.GetSolutionRoot());
        try
        {
            DocumentEditResult updated;
            if (action == AddRemoveAction.add)
            {
                updated = await _refactoringEngine.AddBaseTypeAsync(filePath, typeName, baseTypeName, contextSnippet, lineBefore, lineAfter);
            }
            else if (action == AddRemoveAction.remove)
            {
                updated = await _refactoringEngine.RemoveBaseTypeAsync(filePath, typeName, baseTypeName, contextSnippet, lineBefore, lineAfter);
            }
            else
            {
                return new ToolResult<object>() { Success = false, Error = new ResultError(ToolErrorCode.InvalidArgument, $"Unhandled action '{action}'.") };
            }
            if (!autoStage)
            {
                return new ToolResult<object>() { Success = true, Data = updated.ToJsonSummary() };
            }
            if (RequireUpdatedText(updated, "ModifyBaseType", filePath) is { } guardResult)
                return guardResult;

            var changes = new Dictionary<FilePath, string> { [filePath] = updated.UpdatedText! };
            var apply = await ValidateAndApplyAsync(changes, $"{action} base type '{baseTypeName}' on '{typeName}'.", "ModifyBaseType", dryRun, returnDiff, cancellationToken: cancellationToken);
            if (apply.Error is not null)
                return new ToolResult<object> { Success = false, Error = apply.Error };
            // No ChangedContent: the only "new" text is the base type name the caller already
            // passed in — same reasoning as ChangeAccessibility/ModifyModifier.
            var summary = new AppliedChangeSummary(apply.ChangeId, [filePath], $"{(action == AddRemoveAction.add ? "Adds" : "Removes")} '{baseTypeName}' on '{typeName}' in {Path.GetFileName(filePath)}.", apply.DryRun, apply.Diff);
            return new ToolResult<object>() { Success = true, Data = summary };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ModifyBaseType failed for '{TypeName}' in '{FilePath}'", typeName, filePath);
            return new ToolResult<object>() { Success = false, Error = ToolErrorMapper.ToResultError(ex, _workspaceManager, "ModifyBaseType") };
        }
    }

    [McpServerTool(Name = "SyncTypeAndFilename")]
    [Produces(DataTag.ResultOnly)]
    [Description("Synchronizes the filename to match the primary type declared in the file.")]
    public async Task<ToolResult<object>> SyncTypeAndFilename(
        [Consumes(DataTag.SourceFilepath, required: true)] string filepath,
        [Description(ToolParams.DryRun)][ToolOption(ToolOptionTag.DryRun)] bool dryRun = false,
        [Description(ToolParams.ReturnDiff)][ToolOption(ToolOptionTag.ReturnDiff)] bool returnDiff = false,
        // RequestContext<CallToolRequestParams> requestParams = null,
        CancellationToken cancellationToken = default)
    {
        FilePath filePath = FilePath.FromWire(filepath, _workspaceManager.GetSolutionRoot());

        try
        {
            var result = await _structuralRefinementEngine.SyncTypeAndFilenameAsync(filePath, cancellationToken);
            if (result.Outcome != EditOutcome.Modified || result.Changes.Count == 0)
            {
                return new ToolResult<object> { Success = false, Error = new ResultError(ToolErrorCode.Exception, $"SyncTypeAndFilename: no change produced for '{filePath}' ({result.Outcome}). {result.Message}") };
            }

            var (newPath, content) = result.Changes.First();
            if (File.Exists(newPath))
            {
                return new ToolResult<object> { Success = false, Error = new ResultError(ToolErrorCode.Exception, $"SyncTypeAndFilename: target file '{newPath}' already exists — refusing to overwrite.") };
            }

            var changes = new Dictionary<FilePath, string> { [newPath] = content };
            var apply = await ValidateAndApplyAsync(changes, result.Message ?? $"Rename '{Path.GetFileName(filePath)}' to '{Path.GetFileName(newPath)}'.", "SyncTypeAndFilename", dryRun, returnDiff, cancellationToken: cancellationToken);
            if (apply.Error is not null)
                return new ToolResult<object> { Success = false, Error = apply.Error };

            // dryRun: ValidateAndApplyAsync never wrote newPath, so deleting filePath here would
            // destroy the original with nothing on disk to replace it. Report the preview as-is.
            if (apply.DryRun)
            {
                return new ToolResult<object> { Success = true, Data = new AppliedChangeSummary(apply.ChangeId, [filePath, newPath], $"[DryRun] Would rename '{Path.GetFileName(filePath)}' to '{Path.GetFileName(newPath)}'.", apply.DryRun, apply.Diff) };
            }

            // Only remove the old file after the new one is validated and written, so the
            // two never coexist as a validated on-disk duplicate of the same type.
            try
            {
                File.Delete(filePath);
            }
            catch (Exception ex)
            {
                // Deliberately not routed through ToolErrorMapper (unlike other catches in this
                // file): this is a partial-success condition (new file written and validated, only
                // the old-file delete failed), not a plain failure, and the mapper's generic
                // "failed unexpectedly" wording would drop the actionable remediation advice below.
                _logger.LogError(ex, "SyncTypeAndFilename wrote '{NewPath}' but failed to delete old file '{OldPath}'", newPath, filePath);
                return new ToolResult<object> { Success = false, Error = new ResultError(ToolErrorCode.Exception, $"SyncTypeAndFilename wrote '{Path.GetFileName(newPath)}' but failed to delete the old file '{filePath}': {ex.Message}. Delete it manually to avoid a duplicate-type compile error.") };
            }

            // The old file is gone from disk, but ApplyProposedChangesAsync only ever added the
            // new Document — it has no reason to know the old one should be dropped too. Without
            // this, the old Document stays tracked and the type it declares now exists twice in
            // the compilation, corrupting symbol resolution for every subsequent call.
            await _workspaceManager.RemoveDocumentByPathAsync(filePath, cancellationToken);

            return new ToolResult<object> { Success = true, Data = new AppliedChangeSummary(apply.ChangeId, [filePath, newPath], $"Renamed '{Path.GetFileName(filePath)}' to '{Path.GetFileName(newPath)}'.", apply.DryRun, apply.Diff) };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SyncTypeAndFilename unexpected exception for '{FilePath}'", filePath);
            return new ToolResult<object> { Success = false, Error = ToolErrorMapper.ToResultError(ex, _workspaceManager, $"SyncTypeAndFilename for '{filePath}'") };
        }
    }
}