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
    private readonly PersistentWorkspaceManager _workspaceManager;
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
        PersistentWorkspaceManager workspaceManager,
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
    private async Task<ApplyOutcome> ValidateAndApplyAsync(
        Dictionary<FilePath, string> changes,
        string description,
        string operationName,
        bool dryRun = false,
        bool returnDiff = false,
        IProgress<ProgressNotificationValue>? progress = default,
        CancellationToken cancellationToken = default)
    {
        DiagnosticReport validation;
        try
        {
            validation = await _validationEngine.ValidateChangesAsync(changes, progress, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ValidateAndApply pre-validate failed for {OperationName}", operationName);
            return new ApplyOutcome(null, new ResultError(ToolErrorCode.Exception,
                $"{operationName} pre-validate failed unexpectedly ({ex.GetType().Name}). Check that the solution is loaded and the file path is valid. Details: {ex.Message}"), dryRun);
        }

        if (!validation.Success)
        {
            return new ApplyOutcome(null, new ResultError(ToolErrorCode.Exception,
                $"{operationName} introduces new compiler errors — change not applied. " +
                $"Fix diagnostics and retry: {validation.Diagnostics.ToJson()}"), dryRun);
        }

        if (dryRun)
        {
            var previewDiff = returnDiff ? await BuildDiffAsync(changes, cancellationToken) : null;
            return new ApplyOutcome(null, null, true, previewDiff);
        }

        var applyResult = await _workspaceManager.ApplyProposedChangesAsync(
            changes, retryCount: 3, validateChanges: false, rollbackOnPartialFailure: true,
            progress: progress, cancellationToken: cancellationToken);

        if (!applyResult.Success)
        {
            return new ApplyOutcome(null, new ResultError(ToolErrorCode.Exception,
                $"{operationName} apply failed: {applyResult.Summary}"), false);
        }

        var changeId = Guid.NewGuid().ToString("n")[..8];
        await OperationBlobWriter.WriteApplyBlobAsync(operationName, changeId, applyResult, _workspaceManager.GetSolutionRoot());

        var appliedDiff = returnDiff ? BuildDiffFromPreImages(changes, applyResult.PreImages) : null;
        return new ApplyOutcome(changeId, null, false, appliedDiff);
    }

    private async Task<string> BuildDiffAsync(Dictionary<FilePath, string> changes, CancellationToken cancellationToken)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync(cancellationToken);
        var parts = new List<string>();
        foreach (var (path, newText) in changes)
        {
            var docId = solution.GetDocumentIdsWithFilePath(path).FirstOrDefault();
            string before = "";
            if (docId != null)
            {
                var doc = solution.GetDocument(docId);
                before = (await doc!.GetTextAsync(cancellationToken)).ToString();
            }
            else if (File.Exists(path))
            {
                before = await File.ReadAllTextAsync(path, cancellationToken);
            }
            parts.Add($"--- {path}\n{DiffEngine.CreateDiff(before, newText)}");
        }
        return string.Join("\n", parts);
    }

    private static string BuildDiffFromPreImages(
        Dictionary<FilePath, string> changes,
        IReadOnlyDictionary<string, string?>? preImages)
    {
        var parts = new List<string>();
        foreach (var (path, newText) in changes)
        {
            string before = preImages != null && preImages.TryGetValue(path, out var pre) && pre != null ? pre : "";
            parts.Add($"--- {path}\n{DiffEngine.CreateDiff(before, newText)}");
        }
        return string.Join("\n", parts);
    }

    [McpServerTool(Name = "RenameSymbol")]
    [Produces(DataTag.ChangeId)]
    [Description("Renames a symbol and all its references across the solution. Returns changeId and updatedHandle for the renamed symbol.")]
    public async Task<ToolResult<object>> RenameSymbol(
        [Description(ToolParams.ProjectName)] string projectName,
        [Description(ToolParams.DocCommentId)] string docCommentId,
        [Description("New name for the symbol. Must be a valid C# identifier.")] string newName,
        [Description(ToolParams.SessionId)] string sessionId = "",
        [Description(ToolParams.DryRun)][ToolOption(ToolOptionTag.DryRun)] bool dryRun = false,
        [Description(ToolParams.ReturnDiff)][ToolOption(ToolOptionTag.ReturnDiff)] bool returnDiff = false,
        RequestContext<CallToolRequestParams> requestParams = null,
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
                fileChanges = result.FileChanges,
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
        RequestContext<CallToolRequestParams> requestParams = null,
        CancellationToken cancellationToken = default)
    {
        FilePath filePath = FilePath.FromWire(filepath, _workspaceManager.GetSolutionRoot());
        try
        {
            ProgressToken progressToken = requestParams?.Params?.ProgressToken ?? new ProgressToken();
            IProgress<ProgressNotificationValue> progress = new Progress<ProgressNotificationValue>(msg => requestParams?.Server?.NotifyProgressAsync(progressToken, new ProgressNotificationValue() { Progress = 10.0f }, null, cancellationToken));

            var result = await _mappingEngine.GenerateMappingAsync(filePath, fromType, toType, progress: progress, cancellationToken);
            if (string.IsNullOrEmpty(result.UpdatedText))
                return new ToolResult<object> { Success = false, Error = new ResultError(ToolErrorCode.Exception, $"GenerateMapping produced no output for '{fromType}' → '{toType}' in '{filePath}'. Ensure both types exist in the solution.") };

            var changes = new Dictionary<FilePath, string> { [filePath] = result.UpdatedText };
            var apply = await ValidateAndApplyAsync(changes, $"Generate mapping from '{fromType}' to '{toType}'.", "GenerateMapping", dryRun, returnDiff, progress, cancellationToken);
            if (apply.Error is not null)
                return new ToolResult<object> { Success = false, Error = apply.Error };
            return new ToolResult<object> { Success = true, Data = new PersistentWorkspaceManager.AppliedChangeSummary(apply.ChangeId, [filePath], $"Generates mapping from '{fromType}' to '{toType}' in {Path.GetFileName(filePath)}.", apply.DryRun, apply.Diff) };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GenerateMapping failed for '{FromType}' to '{ToType}' in '{FilePath}'", fromType, toType, filePath);
            return new ToolResult<object>() { Success = false, Error = new ResultError(ToolErrorCode.Exception, $"GenerateMapping failed for '{fromType}' to '{toType}' in '{filePath}': {ex.GetType().Name}: {ex.Message}") };
        }
    }

    [McpServerTool(Name = "ReplaceMember")]
    [Produces(DataTag.ChangeId)]
    [Description("Replaces an entire member (method, property, or class) in a file by name with new source code. " +
        "This is the tool to use for editing/replacing text within a member's body — there is no separate " +
        "snippet-and-replacement tool; instead, read the member, apply your edit in-place, and pass the full " +
        "result as newSource. newSource must be a complete member declaration including its signature/modifiers " +
        "and body (e.g. 'private decimal Foo() { ... }'), not a bare statement or method-body fragment. " +
        "When multiple members share the same name (overloads), provide contextSnippet (a distinctive substring " +
        "from the target member, e.g. its parameter list) and optionally lineBefore/lineAfter to disambiguate. " +
        "Returns changeId.")]
    public async Task<ToolResult<object>> ReplaceMember(
        [Consumes(DataTag.SourceFilepath, required: true)] string filepath,
        [Consumes(DataTag.SymbolName, required: true)] string memberName,
        [Consumes(DataTag.SourceCode, required: true)] string newSource,
        [Description("A distinctive substring from the target member (e.g. parameter list or first statement), used to disambiguate when multiple members share the same name. Optional.")] string? contextSnippet = null,
        [Description("Optional: text from the line immediately before contextSnippet, for additional disambiguation.")] string? lineBefore = null,
        [Description("Optional: text from the line immediately after contextSnippet, for additional disambiguation.")] string? lineAfter = null,
        [Description(ToolParams.DryRun)][ToolOption(ToolOptionTag.DryRun)] bool dryRun = false,
        [Description(ToolParams.ReturnDiff)][ToolOption(ToolOptionTag.ReturnDiff)] bool returnDiff = false,
        RequestContext<CallToolRequestParams> requestParams = null,
        CancellationToken cancellationToken = default)
    {
        FilePath filePath = FilePath.FromWire(filepath, _workspaceManager.GetSolutionRoot());
        try
        {
            ProgressToken progressToken = requestParams?.Params?.ProgressToken ?? new ProgressToken();
            IProgress<ProgressNotificationValue> progress = new Progress<ProgressNotificationValue>(msg => requestParams?.Server?.NotifyProgressAsync(progressToken, new ProgressNotificationValue() { Progress = 10.0f }, null, cancellationToken));

            var result = await _refactoringEngine.ReplaceMemberAsync(filePath, memberName, newSource, contextSnippet, lineBefore, lineAfter, progress, cancellationToken);
            if (string.IsNullOrEmpty(result.UpdatedText))
            {
                string reason = result.Outcome switch
                {
                    EditOutcome.DocumentNotFound => $"ReplaceMember: document '{filePath}' not found in the workspace.",
                    EditOutcome.SourceInvalid => $"ReplaceMember: newSource for '{memberName}' is not a valid member declaration. " +
                        "Provide the full member (signature + body, e.g. 'private decimal Foo() { ... }'), not just a statement or method body fragment.",
                    EditOutcome.TargetNotFound => $"ReplaceMember: member '{memberName}' not found in '{filePath}'.",
                    _ => $"ReplaceMember: no changes produced for '{memberName}' in '{filePath}' ({result.Outcome}). {result.Message}"
                };
                return new ToolResult<object> { Success = false, Error = new ResultError(ToolErrorCode.Exception, reason) };
            }

            var changes = new Dictionary<FilePath, string> { [filePath] = result.UpdatedText };
            var apply = await ValidateAndApplyAsync(changes, $"Replace member '{memberName}'.", "ReplaceMember", dryRun, returnDiff, progress, cancellationToken);
            if (apply.Error is not null)
                return new ToolResult<object> { Success = false, Error = apply.Error };
            return new ToolResult<object> { Success = true, Data = new PersistentWorkspaceManager.AppliedChangeSummary(apply.ChangeId, [filePath], $"Replaces '{memberName}' in {Path.GetFileName(filePath)}.", apply.DryRun, apply.Diff, _workspaceManager.WorkspaceVersion) };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ReplaceMember unexpected exception for '{MemberName}' in '{FilePath}'", memberName, filePath);
            return new ToolResult<object> { Success = false, Error = new ResultError(ToolErrorCode.Exception, $"ReplaceMember for '{memberName}' in '{filePath}' failed unexpectedly ({ex.GetType().Name}). Check that the solution is loaded and the file path is valid. Details: {ex.Message}") };
        }
    }

    [McpServerTool(Name = "RemoveMember")]
    [Produces(DataTag.ChangeId)]
    [Description("Removes a specific member from a class or interface by name. Returns changeId. " +
        "By default, first checks for callers and implementations (same as FindReferences(kind: all)) and " +
        "refuses if any are found, listing what was found — pass skipPrecheck: true to remove unconditionally " +
        "(the engine still separately refuses on direct caller usage regardless of skipPrecheck). For the " +
        "narrower \"only ever remove if truly zero usages\" contract, see SafeDeleteUnusedSymbol. " +
        "When multiple members share the same name (overloads), provide contextSnippet to disambiguate.")]
    public async Task<ToolResult<object>> RemoveMember(
        [Consumes(DataTag.SourceFilepath, required: true)] string filepath,
        [Consumes(DataTag.SymbolName, required: true)] string memberName,
        [Description("A distinctive substring from the target member (e.g. parameter list), used to disambiguate when multiple members share the same name. Optional.")] string? contextSnippet = null,
        [Description("Optional: text from the line immediately before contextSnippet, for additional disambiguation.")] string? lineBefore = null,
        [Description("Optional: text from the line immediately after contextSnippet, for additional disambiguation.")] string? lineAfter = null,
        [Description("When false (default), refuses removal if the member has any callers or implementations (checked the same way as FindReferences(kind: all)). Set true to skip this check and remove unconditionally.")] bool skipPrecheck = false,
        [Description(ToolParams.DryRun)][ToolOption(ToolOptionTag.DryRun)] bool dryRun = false,
        [Description(ToolParams.ReturnDiff)][ToolOption(ToolOptionTag.ReturnDiff)] bool returnDiff = false,
        // RequestContext<CallToolRequestParams> requestParams = null,
        CancellationToken cancellationToken = default)
    {
        FilePath filePath = FilePath.FromWire(filepath, _workspaceManager.GetSolutionRoot());
        try
        {
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
                            $"RemoveMember: '{memberName}' has {string.Join(" and ", parts)} — refusing to remove. " +
                            "Pass skipPrecheck: true to remove anyway, or resolve the callers/implementations first. " +
                            $"Callers: {System.Text.Json.JsonSerializer.Serialize(callers)}. " +
                            $"Implementations: {System.Text.Json.JsonSerializer.Serialize(implementations)}.")
                    };
                }
            }

            var result = await _refactoringEngine.RemoveMemberAsync(filePath, memberName, contextSnippet, lineBefore, lineAfter);
            if (string.IsNullOrEmpty(result.UpdatedText))
                return new ToolResult<object> { Success = false, Error = new ResultError(ToolErrorCode.Exception, $"RemoveMember: member '{memberName}' not found in '{filePath}'.") };

            var changes = new Dictionary<FilePath, string> { [filePath] = result.UpdatedText };
            var apply = await ValidateAndApplyAsync(changes, $"Remove member '{memberName}'.", "RemoveMember", dryRun, returnDiff, cancellationToken: cancellationToken);
            if (apply.Error is not null)
                return new ToolResult<object> { Success = false, Error = apply.Error };
            return new ToolResult<object> { Success = true, Data = new PersistentWorkspaceManager.AppliedChangeSummary(apply.ChangeId, [filePath], $"Removes '{memberName}' from {Path.GetFileName(filePath)}.", apply.DryRun, apply.Diff, _workspaceManager.WorkspaceVersion) };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "RemoveMember failed for '{MemberName}' in '{FilePath}'", memberName, filePath);
            return new ToolResult<object> { Success = false, Error = new ResultError(ToolErrorCode.Exception, $"RemoveMember failed unexpectedly ({ex.GetType().Name}). Check that the solution is loaded and the file path is valid. Details: {ex.Message}") };
        }
    }

    [McpServerTool(Name = "AddUsingDirective")]
    [Produces(DataTag.ChangeId)]
    [Description("Adds a using directive to a file if not already present. For static usings, prefix with \"static \" (e.g. \"static System.Math\"). Returns unchanged if already present.")]
    public async Task<ToolResult<object>> AddUsingDirective(
        [Consumes(DataTag.SourceFilepath, required: true)] string filepath,
        [Consumes(DataTag.SymbolName, required: true)] string namespaceName,
        [Description(ToolParams.AutoStage)][ToolOption(ToolOptionTag.AutoStage, required: false)] bool autoStage = true,
        [Description(ToolParams.DryRun)][ToolOption(ToolOptionTag.DryRun)] bool dryRun = false,
        [Description(ToolParams.ReturnDiff)][ToolOption(ToolOptionTag.ReturnDiff)] bool returnDiff = false,
        // RequestContext<CallToolRequestParams> requestParams = null,
        CancellationToken cancellationToken = default)
    {
        FilePath filePath = FilePath.FromWire(filepath, _workspaceManager.GetSolutionRoot());
        try
        {
            var updated = await _refactoringEngine.AddUsingDirectiveAsync(filePath, namespaceName);
            if (!autoStage)
            {
                return new ToolResult<object>() { Success = true, Data = updated.ToJsonSummary() };
            }

            if (RequireUpdatedText(updated, "AddUsingDirective", filePath) is { } guardResult)
                return guardResult;

            var changes = new Dictionary<FilePath, string> { [filePath] = updated.UpdatedText };
            var apply = await ValidateAndApplyAsync(changes, $"Add using {namespaceName}.", "AddUsingDirective", dryRun, returnDiff, cancellationToken: cancellationToken);
            if (apply.Error is not null)
                return new ToolResult<object> { Success = false, Error = apply.Error };
            return new ToolResult<object>() { Success = true, Data = new PersistentWorkspaceManager.AppliedChangeSummary(apply.ChangeId, [filePath], $"Adds 'using {namespaceName};' to {Path.GetFileName(filePath)}.", apply.DryRun, apply.Diff, _workspaceManager.WorkspaceVersion) };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AddUsingDirective failed for '{Namespace}' in '{FilePath}'", namespaceName, filePath);
            return new ToolResult<object>() { Success = false, Error = new ResultError(ToolErrorCode.Exception, $"AddUsingDirective failed unexpectedly ({ex.GetType().Name}). Check that the solution is loaded and the file path is valid. Details: {ex.Message}") };
        }
    }

    [McpServerTool(Name = "ModifyEnum")]
    [Produces(DataTag.ChangeId)]
    [Description("Sets an enum's complete member list in one call — covers add, remove, and reorder together. " +
        "values is a comma-separated list of member names in the desired final order (e.g. " +
        "\"Pending,Shipped,Delivered,Cancelled\"); append \"=N\" to set or override an explicit integer value " +
        "(e.g. \"Archived=99\"). Names not currently present are added; names omitted from values are removed. " +
        "Members already explicit in the source (had \"= N\") keep that value regardless of position; members " +
        "that were implicit take the next ordinal from their predecessor in the NEW order — same as retyping " +
        "the enum body by hand — so a mid-list insert or removal can shift a retained implicit member's " +
        "underlying value. Pass \"=N\" explicitly for any member whose value must not move. Pass the FULL " +
        "desired member list every time, not just the delta — an incomplete list will remove members you " +
        "didn't mean to drop. contextSnippet: optional distinctive substring from the target's declaration to disambiguate name collisions. To see the current members and their values first, use GetTypeInfo(typeName, " +
        "include: \"members\").")]
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

            var changes = new Dictionary<FilePath, string> { [filePath] = updated.UpdatedText };
            var apply = await ValidateAndApplyAsync(changes, description, "ModifyEnum", dryRun, returnDiff, cancellationToken: cancellationToken);
            if (apply.Error is not null)
                return new ToolResult<object> { Success = false, Error = apply.Error };
            return new ToolResult<object>() { Success = true, Data = new PersistentWorkspaceManager.AppliedChangeSummary(apply.ChangeId, [filePath], description, apply.DryRun, apply.Diff, _workspaceManager.WorkspaceVersion) };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ModifyEnum failed for '{EnumName}' in '{FilePath}'", enumName, filePath);
            return new ToolResult<object>() { Success = false, Error = new ResultError(ToolErrorCode.Exception, $"ModifyEnum failed unexpectedly ({ex.GetType().Name}). Check that the solution is loaded and the file path is valid. Details: {ex.Message}") };
        }
    }

    [McpServerTool(Name = "ChangeAccessibility")]
    [Produces(DataTag.ChangeId)]
    [Description("Changes the accessibility modifier (private, public, internal, protected, protected internal, private protected) of a type or member. contextSnippet: optional distinctive substring from the target's declaration to disambiguate name collisions. This is the tool for accessibility changes — not ModifyAttribute (which is for [Attribute] syntax) or ModifyModifier (which is for non-accessibility keywords like virtual/static/sealed).")]
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

            var changes = new Dictionary<FilePath, string> { [filePath] = updated.UpdatedText };
            var apply = await ValidateAndApplyAsync(changes, $"Change accessibility of '{targetName}' to '{accessibility}'.", "ChangeAccessibility", dryRun, returnDiff, cancellationToken: cancellationToken);
            if (apply.Error is not null)
                return new ToolResult<object> { Success = false, Error = apply.Error };
            return new ToolResult<object>() { Success = true, Data = new PersistentWorkspaceManager.AppliedChangeSummary(apply.ChangeId, [filePath], $"Changes accessibility of '{targetName}' to '{accessibility}' in {Path.GetFileName(filePath)}.", apply.DryRun, apply.Diff, _workspaceManager.WorkspaceVersion) };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ChangeAccessibility failed for '{TargetName}' in '{FilePath}'", targetName, filePath);
            return new ToolResult<object>() { Success = false, Error = new ResultError(ToolErrorCode.Exception, $"ChangeAccessibility failed unexpectedly ({ex.GetType().Name}). Check that the solution is loaded and the file path is valid. Details: {ex.Message}") };
        }
    }

    [McpServerTool(Name = "AddSummaryComment")]
    [Produces(DataTag.ChangeId)]
    [Description("Adds or replaces a /// <summary> XML doc comment on a type or member. contextSnippet: optional distinctive substring from the target's declaration to disambiguate name collisions. Replaces existing summary.")]
    public async Task<ToolResult<object>> AddSummaryComment(
        [Consumes(DataTag.SourceFilepath, required: true)] string filepath,
        [Consumes(DataTag.SymbolName, required: true)] string targetName,
        string summaryText,
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
            var updated = await _refactoringEngine.AddSummaryCommentAsync(filePath, targetName, summaryText, contextSnippet, lineBefore, lineAfter);
            if (!autoStage)
            {
                return new ToolResult<object>() { Success = true, Data = updated.ToJsonSummary() };
            }

            if (RequireUpdatedText(updated, "AddSummaryComment", filePath) is { } guardResult)
                return guardResult;

            var changes = new Dictionary<FilePath, string> { [filePath] = updated.UpdatedText };
            var apply = await ValidateAndApplyAsync(changes, $"Add summary comment to '{targetName}'.", "AddSummaryComment", dryRun, returnDiff, cancellationToken: cancellationToken);
            if (apply.Error is not null)
                return new ToolResult<object> { Success = false, Error = apply.Error };
            return new ToolResult<object>() { Success = true, Data = new PersistentWorkspaceManager.AppliedChangeSummary(apply.ChangeId, [filePath], $"Added XML summary comment to '{targetName}' in {Path.GetFileName(filePath)}.", apply.DryRun, apply.Diff) };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AddSummaryComment failed for '{TargetName}' in '{FilePath}'", targetName, filePath);
            return new ToolResult<object>() { Success = false, Error = new ResultError(ToolErrorCode.Exception, $"AddSummaryComment failed unexpectedly ({ex.GetType().Name}). Check that the solution is loaded and the file path is valid. Details: {ex.Message}") };
        }
    }

    [McpServerTool(Name = "AddConstructorParameter")]
    [Produces(DataTag.ChangeId)]
    [Description("Adds a DI constructor parameter in one step: private readonly field + parameter + body assignment. fieldName overrides the derived field name (defaults to _camelCase of paramName). contextSnippet: optional distinctive substring from the target's declaration to disambiguate name collisions. Creates a constructor if none exists.")]
    public async Task<ToolResult<object>> AddConstructorParameter([Consumes(DataTag.SourceFilepath, required: true)] string filepath,
        [Consumes(DataTag.ClassName, required: true)] string className,
        [Consumes(DataTag.SymbolName, required: true)] string paramName,
        [Consumes(DataTag.DataType, required: true)] string paramType,
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
            var updated = await _refactoringEngine.AddConstructorParameterAsync(filePath, className, paramName, paramType, fieldName, contextSnippet, lineBefore, lineAfter);
            if (!autoStage)
            {
                return new ToolResult<object>() { Success = true, Data = updated.ToJsonSummary() };
            }

            if (RequireUpdatedText(updated, "AddConstructorParameter", filePath) is { } guardResult)
                return guardResult;

            var changes = new Dictionary<FilePath, string> { [filePath] = updated.UpdatedText };
            var apply = await ValidateAndApplyAsync(changes, $"Add constructor parameter '{paramName}' to '{className}'.", "AddConstructorParameter", dryRun, returnDiff, cancellationToken: cancellationToken);
            if (apply.Error is not null)
                return new ToolResult<object> { Success = false, Error = apply.Error };
            return new ToolResult<object>() { Success = true, Data = new PersistentWorkspaceManager.AppliedChangeSummary(apply.ChangeId, [filePath], $"Added '{paramType} {paramName}' DI parameter to '{className}' in {Path.GetFileName(filePath)}.", apply.DryRun, apply.Diff) };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AddConstructorParameter failed for '{ClassName}' in '{FilePath}'", className, filePath);
            return new ToolResult<object>() { Success = false, Error = new ResultError(ToolErrorCode.Exception, $"AddConstructorParameter failed unexpectedly ({ex.GetType().Name}). Check that the solution is loaded and the file path is valid. Details: {ex.Message}") };
        }
    }

    [McpServerTool(Name = "ExtractLocalVariable")]
    [Produces(DataTag.ChangeId)]
    [Description("Extracts an inline expression into a named local variable declaration.")]
    public async Task<ToolResult<object>> ExtractLocalVariable(
        [Consumes(DataTag.SourceFilepath, required: true)] string filepath,
        [Description(ToolParams.ContextSnippet)][Consumes(DataTag.ContextSnippet, required: true)] string contextSnippet,
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
            var result = await _refactoringEngine.ExtractLocalVariableAsync(filePath, contextSnippet, variableName, lineBefore, lineAfter);
            if (string.IsNullOrEmpty(result.UpdatedText))
            {
                string reason = result.Outcome switch
                {
                    EditOutcome.DocumentNotFound => $"ExtractLocalVariable: document '{filePath}' not found in the workspace.",
                    EditOutcome.SourceInvalid => $"ExtractLocalVariable: contextSnippet not found in '{filePath}'. {result.Message}",
                    EditOutcome.CannotConvert => $"ExtractLocalVariable: could not extract '{variableName}' in '{filePath}'. {result.Message}",
                    _ => $"ExtractLocalVariable: no change produced for '{variableName}' in '{filePath}' ({result.Outcome}). {result.Message}"
                };
                return new ToolResult<object>() { Success = false, Error = new ResultError(ToolErrorCode.Exception, reason) };
            }

            var changes = new Dictionary<FilePath, string> { [filePath] = result.UpdatedText };
            var apply = await ValidateAndApplyAsync(changes, $"Extract local variable '{variableName}'.", "ExtractLocalVariable", dryRun, returnDiff, cancellationToken: cancellationToken);
            if (apply.Error is not null)
                return new ToolResult<object> { Success = false, Error = apply.Error };
            return new ToolResult<object> { Success = true, Data = new PersistentWorkspaceManager.AppliedChangeSummary(apply.ChangeId, [filePath], $"Extracts '{variableName}' as a local variable in {Path.GetFileName(filePath)}.", apply.DryRun, apply.Diff) };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ExtractLocalVariable failed for '{VariableName}' in '{FilePath}'", variableName, filePath);
            return new ToolResult<object>() { Success = false, Error = new ResultError(ToolErrorCode.Exception, $"ExtractLocalVariable failed unexpectedly ({ex.GetType().Name}). Check that the solution is loaded and the file path is valid. Details: {ex.Message}") };
        }
    }

    [McpServerTool(Name = "ExtractMethodSafe")]
    [Produces(DataTag.ResultOnly)]
    [Description("Extracts selected statements into a new method with the correct return type inferred from the selection. newMethodName must be a valid C# identifier. Returns MsAugmentResult.")]
    // Fixes MS BUG: where selections ending with "return <expression>" are extracted into a method declared "private void MethodName(...)", causing a compile error. This tool uses Roslyn's SemanticModel to determine the actual type of the returned expression, and DataFlowAnalysis to find the correct parameter list. Requires a loaded solution (via set_solution_path or equivalent).
    public async Task<ToolResult<object>> ExtractMethodSafe(
        [Consumes(DataTag.SourceFilepath, required: true)] string filepath,
        [ExternalInputRequired(DataTag.MethodName, required: true)] string newMethodName,
        [Description(ToolParams.ContextSnippet)][Consumes(DataTag.ContextSnippet, required: true)] string contextSnippet,
        [Description(ToolParams.LineBefore)][ExternalInputRequired(DataTag.LineBefore)] string? lineBefore = null,
        [Description(ToolParams.LineAfter)][ExternalInputRequired(DataTag.LineAfter)] string? lineAfter = null,
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
                filePath, newMethodName, contextSnippet, lineBefore, lineAfter, cancellationToken: cancellationToken);
            return new ToolResult<object>
            {
                Success = true,
                Data = result
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ExtractMethodSafe failed for '{NewMethodName}' in '{FilePath}'", newMethodName, filePath);
            return new ToolResult<object>
            {
                Success = false,
                Error = new ResultError(ToolErrorCode.Exception, $"ExtractMethodSafe failed unexpectedly ({ex.GetType().Name}). Check that the solution is loaded and the file path is valid. Details: {ex.Message}")
            };
        }
    }

    [McpServerTool(Name = "ModifyAttribute")]
    [Produces(DataTag.ChangeId)]
    [Description("Adds, replaces, or removes an attribute on a type or member. existingAttribute accepts name with or without brackets (e.g. \"[ApiController]\", \"Required\"). newAttribute required for replace. contextSnippet: optional distinctive substring from the target's declaration to disambiguate name collisions. This tool is for [Attribute] syntax only — do NOT use it for accessibility (private/public/etc., use ChangeAccessibility) or modifier keywords (virtual/static/sealed/etc., use ModifyModifier).")]
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

            var changes = new Dictionary<FilePath, string> { [filePath] = updated.UpdatedText };
            var apply = await ValidateAndApplyAsync(changes, $"{action} attribute '{existingAttribute}' on '{targetName}'.", "ModifyAttribute", dryRun, returnDiff, cancellationToken: cancellationToken);
            if (apply.Error is not null)
                return new ToolResult<object> { Success = false, Error = apply.Error };
            var summary = new PersistentWorkspaceManager.AppliedChangeSummary(apply.ChangeId, [filePath], $"{(action == AttributeModifyAction.add ? "Adds" : action == AttributeModifyAction.replace ? "Replaces" : "Removes")} '{existingAttribute}' attribute on '{targetName}' in {Path.GetFileName(filePath)}.", apply.DryRun, apply.Diff);
            return new ToolResult<object>() { Success = true, Data = summary };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ModifyAttribute failed for '{TargetName}' in '{FilePath}'", targetName, filePath);
            return new ToolResult<object>() { Success = false, Error = new ResultError(ToolErrorCode.Exception, $"ModifyAttribute failed unexpectedly ({ex.GetType().Name}). Check that the solution is loaded and the file path is valid. Details: {ex.Message}") };
        }
    }

    [McpServerTool(Name = "ModifyModifier")]
    [Produces(DataTag.ChangeId)]
    [Description("Adds or removes a modifier keyword on a type or member. modifier: virtual, abstract, sealed, static, readonly, override, partial, async, new, extern, unsafe, volatile. contextSnippet: optional distinctive substring from the target's declaration to disambiguate name collisions. Does NOT cover accessibility keywords (private, public, internal, protected) — use ChangeAccessibility for those.")]
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

            var changes = new Dictionary<FilePath, string> { [filePath] = updated.UpdatedText };
            var apply = await ValidateAndApplyAsync(changes, $"{action} '{modifier}' modifier on '{targetName}'.", "ModifyModifier", dryRun, returnDiff, cancellationToken: cancellationToken);
            if (apply.Error is not null)
                return new ToolResult<object> { Success = false, Error = apply.Error };
            var summary = new PersistentWorkspaceManager.AppliedChangeSummary(apply.ChangeId, [filePath], $"{(action == AddRemoveAction.add ? "Adds" : "Removes")} '{modifier}' modifier on '{targetName}' in {Path.GetFileName(filePath)}.", apply.DryRun, apply.Diff);
            return new ToolResult<object>() { Success = true, Data = summary };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ModifyModifier failed for '{TargetName}' in '{FilePath}'", targetName, filePath);
            return new ToolResult<object>() { Success = false, Error = new ResultError(ToolErrorCode.Exception, $"ModifyModifier failed unexpectedly ({ex.GetType().Name}). Check that the solution is loaded and the file path is valid. Details: {ex.Message}") };
        }
    }

    [McpServerTool(Name = "ModifyBaseType")]
    [Produces(DataTag.ChangeId)]
    [Description("Adds or removes a base type or interface from a type declaration. contextSnippet: optional distinctive substring from the target's declaration to disambiguate name collisions.")]
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

            var changes = new Dictionary<FilePath, string> { [filePath] = updated.UpdatedText };
            var apply = await ValidateAndApplyAsync(changes, $"{action} base type '{baseTypeName}' on '{typeName}'.", "ModifyBaseType", dryRun, returnDiff, cancellationToken: cancellationToken);
            if (apply.Error is not null)
                return new ToolResult<object> { Success = false, Error = apply.Error };
            var summary = new PersistentWorkspaceManager.AppliedChangeSummary(apply.ChangeId, [filePath], $"{(action == AddRemoveAction.add ? "Adds" : "Removes")} '{baseTypeName}' on '{typeName}' in {Path.GetFileName(filePath)}.", apply.DryRun, apply.Diff);
            return new ToolResult<object>() { Success = true, Data = summary };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ModifyBaseType failed for '{TypeName}' in '{FilePath}'", typeName, filePath);
            return new ToolResult<object>() { Success = false, Error = new ResultError(ToolErrorCode.Exception, $"ModifyBaseType failed unexpectedly ({ex.GetType().Name}). Check that the solution is loaded and the file path is valid. Details: {ex.Message}") };
        }
    }

    [McpServerTool(Name = "AddMember")]
    [Produces(DataTag.ChangeId)]
    [Description("Adds a new member to a type. position: null/\"end\" (append), \"after:MemberName\", or \"before:MemberName\". contextSnippet: optional distinctive substring from the target's declaration to disambiguate name collisions.")]
    public async Task<ToolResult<object>> AddMember(
        [Consumes(DataTag.SourceFilepath, required: true)] string filepath,
        [Consumes(DataTag.SymbolName, required: true)] string containerName,
        [ExternalInputRequired(DataTag.ClassName)] string newMemberSource,
        [ExternalInputRequired(DataTag.Position)] string? position = null,
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
            string description;
            if (string.IsNullOrEmpty(position) || position == "end")
            {
                updated = await _refactoringEngine.AddMemberAsync(filePath, containerName, newMemberSource, contextSnippet, lineBefore, lineAfter);
                description = $"Added new member to '{containerName}' in {Path.GetFileName(filePath)}.";
            }
            else if (position.StartsWith("after:", StringComparison.OrdinalIgnoreCase))
            {
                var afterMemberName = position.Substring("after:".Length);
                updated = await _refactoringEngine.InsertMemberAfterAsync(filePath, containerName, afterMemberName, newMemberSource, contextSnippet, lineBefore, lineAfter);
                description = $"Inserted new member after '{afterMemberName}' in '{containerName}' in {Path.GetFileName(filePath)}.";
            }
            else if (position.StartsWith("before:", StringComparison.OrdinalIgnoreCase))
            {
                var beforeMemberName = position.Substring("before:".Length);
                updated = await _refactoringEngine.InsertMemberBeforeAsync(filePath, containerName, beforeMemberName, newMemberSource, contextSnippet, lineBefore, lineAfter);
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
            if (RequireUpdatedText(updated, "AddMember", filePath) is { } guardResult)
                return guardResult;

            var changes = new Dictionary<FilePath, string> { [filePath] = updated.UpdatedText };
            var apply = await ValidateAndApplyAsync(changes, description, "AddMember", dryRun, returnDiff, cancellationToken: cancellationToken);
            if (apply.Error is not null)
                return new ToolResult<object> { Success = false, Error = apply.Error };
            var summary = new PersistentWorkspaceManager.AppliedChangeSummary(apply.ChangeId, [filePath], description, apply.DryRun, apply.Diff, _workspaceManager.WorkspaceVersion);
            return new ToolResult<object>() { Success = true, Data = summary };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AddMember failed for '{ContainerName}' in '{FilePath}'", containerName, filePath);
            return new ToolResult<object>() { Success = false, Error = new ResultError(ToolErrorCode.Exception, $"AddMember failed unexpectedly ({ex.GetType().Name}). Check that the solution is loaded and the file path is valid. Details: {ex.Message}") };
        }
    }

    [McpServerTool(Name = "AddMemberTyped")]
    [Produces(DataTag.ChangeId)]
    [Description("Generates a typed member and adds it to a type. property → auto-property (defaults: hasSetter=true, accessibility=public). field → field (defaults: isReadonly=false, isStatic=false, accessibility=private). contextSnippet: optional distinctive substring from the target's declaration to disambiguate name collisions.")]
    public async Task<ToolResult<object>> AddMemberTyped(
        [Consumes(DataTag.SourceFilepath, required: true)] string filepath,
        [Consumes(DataTag.ClassName, required: true)] string containerName,
        [ExternalInputRequired(DataTag.SymbolName)] string name,
        [ExternalInputRequired(DataTag.DataType)] string type,
        [ExternalInputRequired(DataTag.SymbolKind)] TypedMemberKind kind,
        [Description(ToolParams.AccessibilityValues)][ExternalInputRequired(DataTag.Accessibility)] string accessibility = "public",
        [ExternalInputRequired(DataTag.HasSetter)] bool hasSetter = true,
        [ExternalInputRequired(DataTag.IsInit)] bool isInit = false,
        [ExternalInputRequired(DataTag.IsReadonly)] bool isReadonly = false,
        [ExternalInputRequired(DataTag.IsStatic)] bool isStatic = false,
        [ExternalInputRequired(DataTag.Initializer)] string? initializer = null,
        [Description(ToolParams.ContextSnippet)][ExternalInputRequired(DataTag.ContextSnippet, required: false)] string? contextSnippet = null,
        [Description(ToolParams.LineBefore)][ExternalInputRequired(DataTag.LineBefore, required: false)] string? lineBefore = null,
        [Description(ToolParams.LineAfter)][ExternalInputRequired(DataTag.LineAfter, required: false)] string? lineAfter = null,
        [Description(ToolParams.AutoStage)][ToolOptionAttribute(ToolOptionTag.AutoStage)] bool autoStage = true,
        [Description(ToolParams.DryRun)][ToolOption(ToolOptionTag.DryRun)] bool dryRun = false,
        [Description(ToolParams.ReturnDiff)][ToolOption(ToolOptionTag.ReturnDiff)] bool returnDiff = false,
        // RequestContext<CallToolRequestParams> requestParams = null,
        CancellationToken cancellationToken = default)
    {
        FilePath filePath = FilePath.FromWire(filepath, _workspaceManager.GetSolutionRoot());
        try
        {
            DocumentEditResult updated;
            string description;
            if (kind == TypedMemberKind.property)
            {
                updated = await _refactoringEngine.AddPropertyAsync(filePath, containerName, name, type, accessibility, hasSetter, isInit, contextSnippet, lineBefore, lineAfter);
                description = $"Added '{type} {name}' property to '{containerName}' in {Path.GetFileName(filePath)}.";
            }
            else if (kind == TypedMemberKind.field)
            {
                updated = await _refactoringEngine.AddFieldAsync(filePath, containerName, name, type, accessibility, isReadonly, isStatic, initializer, contextSnippet, lineBefore, lineAfter);
                description = $"Added '{type} {name}' field to '{containerName}' in {Path.GetFileName(filePath)}.";
            }
            else
            {
                return new ToolResult<object>() { Success = false, Error = new ResultError(ToolErrorCode.InvalidArgument, $"Unhandled kind '{kind}'.") };
            }
            if (!autoStage)
            {
                return new ToolResult<object>() { Success = true, Data = updated.ToJsonSummary() };
            }
            if (RequireUpdatedText(updated, "AddMemberTyped", filePath) is { } guardResult)
                return guardResult;

            var changes = new Dictionary<FilePath, string> { [filePath] = updated.UpdatedText };
            var apply = await ValidateAndApplyAsync(changes, description, "AddMemberTyped", dryRun, returnDiff, cancellationToken: cancellationToken);
            if (apply.Error is not null)
                return new ToolResult<object> { Success = false, Error = apply.Error };
            var summary = new PersistentWorkspaceManager.AppliedChangeSummary(apply.ChangeId, [filePath], description, apply.DryRun, apply.Diff);
            return new ToolResult<object>() { Success = true, Data = summary };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AddMemberTyped ({Kind}) failed for '{ContainerName}' in '{FilePath}'", kind, containerName, filePath);
            return new ToolResult<object>() { Success = false, Error = new ResultError(ToolErrorCode.Exception, $"AddMemberTyped failed unexpectedly ({ex.GetType().Name}). Check that the solution is loaded and the file path is valid. Details: {ex.Message}") };
        }
    }

    [McpServerTool(Name = "SyncTypeAndFilename")]
    [Produces(DataTag.ResultOnly)]
    [Description("Synchronizes the filename to match the primary type declared in the file.")]
    public async Task<ToolResult<object>> SyncTypeAndFilename(
        [Consumes(DataTag.SourceFilepath, required: true)] string filepath,
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
            var apply = await ValidateAndApplyAsync(changes, result.Message ?? $"Rename '{Path.GetFileName(filePath)}' to '{Path.GetFileName(newPath)}'.", "SyncTypeAndFilename", cancellationToken: cancellationToken);
            if (apply.Error is not null)
                return new ToolResult<object> { Success = false, Error = apply.Error };

            // Only remove the old file after the new one is validated and written, so the
            // two never coexist as a validated on-disk duplicate of the same type.
            try
            {
                File.Delete(filePath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "SyncTypeAndFilename wrote '{NewPath}' but failed to delete old file '{OldPath}'", newPath, filePath);
                return new ToolResult<object> { Success = false, Error = new ResultError(ToolErrorCode.Exception, $"SyncTypeAndFilename wrote '{Path.GetFileName(newPath)}' but failed to delete the old file '{filePath}': {ex.Message}. Delete it manually to avoid a duplicate-type compile error.") };
            }

            // The old file is gone from disk, but ApplyProposedChangesAsync only ever added the
            // new Document — it has no reason to know the old one should be dropped too. Without
            // this, the old Document stays tracked and the type it declares now exists twice in
            // the compilation, corrupting symbol resolution for every subsequent call.
            await _workspaceManager.RemoveDocumentByPathAsync(filePath, cancellationToken);

            return new ToolResult<object> { Success = true, Data = new PersistentWorkspaceManager.AppliedChangeSummary(apply.ChangeId, [filePath, newPath], $"Renamed '{Path.GetFileName(filePath)}' to '{Path.GetFileName(newPath)}'.", apply.DryRun, apply.Diff) };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SyncTypeAndFilename unexpected exception for '{FilePath}'", filePath);
            return new ToolResult<object> { Success = false, Error = new ResultError(ToolErrorCode.Exception, $"SyncTypeAndFilename for '{filePath}' failed unexpectedly ({ex.GetType().Name}). Check that the solution is loaded and the file path is valid. Details: {ex.Message}") };
        }
    }
}