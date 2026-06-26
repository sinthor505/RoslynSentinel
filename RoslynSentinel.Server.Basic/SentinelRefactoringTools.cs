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

    private async Task<(string? ChangeId, ResultError? Error)> ValidateAndStageAsync(
        Dictionary<FilePath, string> changes,
        string description,
        string operationName,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        DiagnosticReport validation;
        try
        {
            validation = await _validationEngine.ValidateChangesAsync(changes);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ValidateAndStage pre-validate failed for {OperationName}", operationName);
            return (null, new ResultError(ToolErrorCode.Exception,
                $"{operationName} pre-validate failed unexpectedly ({ex.GetType().Name}). Check that the solution is loaded and the file path is valid. Details: {ex.Message}"));
        }

        if (!validation.Success)
        {
            return (null, new ResultError(ToolErrorCode.Exception,
                $"{operationName} introduces new compiler errors — change not staged. " +
                $"Fix diagnostics and retry: {validation.Diagnostics.ToJson()}"));
        }

        return (_workspaceManager.StageChanges(changes, description), null);
    }

    [McpServerTool(Name = "RenameSymbol")]
    [Produces(DataTag.ChangeId)]
    [Description("Renames a symbol and all its references across the solution. Returns changeId and updatedHandle for the renamed symbol.")]
    public async Task<ToolResult<object>> RenameSymbol(
        [Description(ToolParams.SessionId)] string sessionId,
        [Description(ToolParams.ProjectName)] string projectName,
        [Description(ToolParams.DocCommentId)] string docCommentId,
        [Description("New name for the symbol. Must be a valid C# identifier.")] string newName,
        RequestContext<CallToolRequestParams> requestParams = null,
        CancellationToken cancellationToken = default)
    {
        ProgressToken progressToken = requestParams.Params?.ProgressToken ?? new ProgressToken();
        IProgress<string> progress = new Progress<string>(msg => requestParams.Server.NotifyProgressAsync(progressToken, new ProgressNotificationValue() { Progress = 10.0f }, null, cancellationToken));

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

        var (changeId, stageError) = await ValidateAndStageAsync(
            result.PendingChanges,
            $"Rename '{result.OldName}' to '{result.NewName}'.",
            "RenameSymbol");

        if (stageError is not null)
            return new ToolResult<object> { Success = false, Error = stageError };

        return new ToolResult<object>
        {
            Success = true,
            Data = new
            {
                changeId,
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
        RequestContext<CallToolRequestParams> requestParams = null,
        CancellationToken cancellationToken = default)
    {
        FilePath filePath = FilePath.FromWire(filepath, _workspaceManager.GetSolutionRoot());
        try
        {
            ProgressToken progressToken = requestParams.Params?.ProgressToken ?? new ProgressToken();
            IProgress<string> progress = new Progress<string>(msg => requestParams.Server.NotifyProgressAsync(progressToken, new ProgressNotificationValue() { Progress = 10.0f }, null, cancellationToken));

            var result = await _mappingEngine.GenerateMappingAsync(filePath, fromType, toType, progress: progress, cancellationToken);
            if (string.IsNullOrEmpty(result.UpdatedText))
                return new ToolResult<object> { Success = false, Error = new ResultError(ToolErrorCode.Exception, $"GenerateMapping produced no output for '{fromType}' → '{toType}' in '{filePath}'. Ensure both types exist in the solution.") };

            var changes = new Dictionary<FilePath, string> { [filePath] = result.UpdatedText };
            var (changeId, error) = await ValidateAndStageAsync(changes, $"Generate mapping from '{fromType}' to '{toType}'.", "GenerateMapping");
            if (error is not null)
                return new ToolResult<object> { Success = false, Error = error };
            return new ToolResult<object> { Success = true, Data = new PersistentWorkspaceManager.StagedChangeSummary(changeId!, [filePath], $"Generates mapping from '{fromType}' to '{toType}' in {Path.GetFileName(filePath)}.") };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GenerateMapping failed for '{FromType}' to '{ToType}' in '{FilePath}'", fromType, toType, filePath);
            return new ToolResult<object>() { Success = false, Error = new ResultError(ToolErrorCode.Exception, $"GenerateMapping failed for '{fromType}' to '{toType}' in '{filePath}': {ex.GetType().Name}: {ex.Message}") };
        }
    }

    [McpServerTool(Name = "ReplaceMember")]
    [Produces(DataTag.ChangeId)]
    [Description("Replaces a member (method, property, or class) in a file by name with new source code. Returns changeId.")]
    public async Task<ToolResult<object>> ReplaceMember(
        [Consumes(DataTag.SourceFilepath, required: true)] string filepath,
        [Consumes(DataTag.SymbolName, required: true)] string memberName,
        [Consumes(DataTag.SourceCode, required: true)] string newSource,
        RequestContext<CallToolRequestParams> requestParams = null,
        CancellationToken cancellationToken = default)
    {
        FilePath filePath = FilePath.FromWire(filepath, _workspaceManager.GetSolutionRoot());
        try
        {
            ProgressToken progressToken = requestParams.Params?.ProgressToken ?? new ProgressToken();
            IProgress<string> progress = new Progress<string>(msg => requestParams.Server.NotifyProgressAsync(progressToken, new ProgressNotificationValue() { Progress = 10.0f }, null, cancellationToken));

            var result = await _refactoringEngine.ReplaceMemberAsync(filePath, memberName, newSource, progress, cancellationToken);
            if (string.IsNullOrEmpty(result.UpdatedText))
                return new ToolResult<object> { Success = false, Error = new ResultError(ToolErrorCode.Exception, $"ReplaceMember: member '{memberName}' not found in '{filePath}'.") };

            var changes = new Dictionary<FilePath, string> { [filePath] = result.UpdatedText };
            var (changeId, error) = await ValidateAndStageAsync(changes, $"Replace member '{memberName}'.", "ReplaceMember", progress, cancellationToken);
            if (error is not null)
                return new ToolResult<object> { Success = false, Error = error };
            return new ToolResult<object> { Success = true, Data = new PersistentWorkspaceManager.StagedChangeSummary(changeId!, [filePath], $"Replaces '{memberName}' in {Path.GetFileName(filePath)}.") };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ReplaceMember unexpected exception for '{MemberName}' in '{FilePath}'", memberName, filePath);
            return new ToolResult<object> { Success = false, Error = new ResultError(ToolErrorCode.Exception, $"ReplaceMember for '{memberName}' in '{filePath}' failed unexpectedly ({ex.GetType().Name}). Check that the solution is loaded and the file path is valid. Details: {ex.Message}") };
        }
    }

    [McpServerTool(Name = "RemoveMember")]
    [Produces(DataTag.ChangeId)]
    [Description("Removes a specific member from a class or interface by name. Returns changeId.")]
    public async Task<ToolResult<object>> RemoveMember(
        [Consumes(DataTag.SourceFilepath, required: true)] string filepath,
        [Consumes(DataTag.SymbolName, required: true)] string memberName,
        RequestContext<CallToolRequestParams> requestParams = null,
        CancellationToken cancellationToken = default)
    {
        FilePath filePath = FilePath.FromWire(filepath, _workspaceManager.GetSolutionRoot());
        try
        {
            var result = await _refactoringEngine.RemoveMemberAsync(filePath, memberName);
            if (string.IsNullOrEmpty(result.UpdatedText))
                return new ToolResult<object> { Success = false, Error = new ResultError(ToolErrorCode.Exception, $"RemoveMember: member '{memberName}' not found in '{filePath}'.") };

            var changes = new Dictionary<FilePath, string> { [filePath] = result.UpdatedText };
            var (changeId, error) = await ValidateAndStageAsync(changes, $"Remove member '{memberName}'.", "RemoveMember");
            if (error is not null)
                return new ToolResult<object> { Success = false, Error = error };
            return new ToolResult<object> { Success = true, Data = new PersistentWorkspaceManager.StagedChangeSummary(changeId!, [filePath], $"Removes '{memberName}' from {Path.GetFileName(filePath)}.") };
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
        RequestContext<CallToolRequestParams> requestParams = null,
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

            var changes = new Dictionary<FilePath, string> { [filePath] = updated.UpdatedText! };
            var (id, stageError) = await ValidateAndStageAsync(changes, $"Add using {namespaceName}.", "AddUsingDirective");
            if (stageError is not null)
                return new ToolResult<object> { Success = false, Error = stageError };
            return new ToolResult<object>() { Success = true, Data = new PersistentWorkspaceManager.StagedChangeSummary(id!, [filePath], $"Adds 'using {namespaceName};' to {Path.GetFileName(filePath)}.") };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AddUsingDirective failed for '{Namespace}' in '{FilePath}'", namespaceName, filePath);
            return new ToolResult<object>() { Success = false, Error = new ResultError(ToolErrorCode.Exception, $"AddUsingDirective failed unexpectedly ({ex.GetType().Name}). Check that the solution is loaded and the file path is valid. Details: {ex.Message}") };
        }
    }

    [McpServerTool(Name = "AddEnumValue")]
    [Produces(DataTag.ChangeId)]
    [Description("Adds a new value to an existing enum. explicitValue sets an explicit integer value (e.g. 99 → Archived = 99). Returns unchanged if enum not found.")]
    public async Task<ToolResult<object>> AddEnumValue(
        [Consumes(DataTag.SourceFilepath, required: true)] string filepath,
        [Consumes(DataTag.SymbolName, required: true)] string enumName,
        [Consumes(DataTag.SymbolName, required: true)] string valueName,
        [Consumes(DataTag.DataType, required: false)] int? explicitValue = null,
        [Description(ToolParams.AutoStage)][ToolOption(ToolOptionTag.AutoStage, required: false)] bool autoStage = true,
        RequestContext<CallToolRequestParams> requestParams = null,
        CancellationToken cancellationToken = default)
    {
        FilePath filePath = FilePath.FromWire(filepath, _workspaceManager.GetSolutionRoot());
        try
        {
            var updated = await _refactoringEngine.AddEnumValueAsync(filePath, enumName, valueName, explicitValue);
            if (!autoStage)
            {
                return new ToolResult<object>() { Success = true, Data = updated.ToJsonSummary() };
            }

            var changes = new Dictionary<FilePath, string> { [filePath] = updated.UpdatedText! };
            var (id, stageError) = await ValidateAndStageAsync(changes, $"Add enum value '{valueName}' to '{enumName}'.", "AddEnumValue");
            if (stageError is not null)
                return new ToolResult<object> { Success = false, Error = stageError };
            return new ToolResult<object>() { Success = true, Data = new PersistentWorkspaceManager.StagedChangeSummary(id!, [filePath], $"Adds '{valueName}' to enum '{enumName}' in {Path.GetFileName(filePath)}.") };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AddEnumValue failed for '{EnumName}' in '{FilePath}'", enumName, filePath);
            return new ToolResult<object>() { Success = false, Error = new ResultError(ToolErrorCode.Exception, $"AddEnumValue failed unexpectedly ({ex.GetType().Name}). Check that the solution is loaded and the file path is valid. Details: {ex.Message}") };
        }
    }

    [McpServerTool(Name = "ChangeAccessibility")]
    [Produces(DataTag.ChangeId)]
    [Description("Changes the accessibility modifier of a type or member.")]
    public async Task<ToolResult<object>> ChangeAccessibility(
        [Consumes(DataTag.SourceFilepath, required: true)] string filepath,
        [Consumes(DataTag.SymbolName, required: true)] string targetName,
        [Description(ToolParams.AccessibilityValues)][ExternalInputRequired(DataTag.Accessibility, required: true)] string accessibility,
        [Description(ToolParams.AutoStage)][ToolOption(ToolOptionTag.AutoStage, required: false)] bool autoStage = true,
        RequestContext<CallToolRequestParams> requestParams = null,
        CancellationToken cancellationToken = default)
    {
        FilePath filePath = FilePath.FromWire(filepath, _workspaceManager.GetSolutionRoot());
        try
        {
            var updated = await _refactoringEngine.ChangeAccessibilityAsync(filePath, targetName, accessibility);
            if (!autoStage)
            {
                return new ToolResult<object>() { Success = true, Data = updated.ToJsonSummary() };
            }

            var changes = new Dictionary<FilePath, string> { [filePath] = updated.UpdatedText! };
            var (id, stageError) = await ValidateAndStageAsync(changes, $"Change accessibility of '{targetName}' to '{accessibility}'.", "ChangeAccessibility");
            if (stageError is not null)
                return new ToolResult<object> { Success = false, Error = stageError };
            return new ToolResult<object>() { Success = true, Data = new PersistentWorkspaceManager.StagedChangeSummary(id!, [filePath], $"Changes accessibility of '{targetName}' to '{accessibility}' in {Path.GetFileName(filePath)}.") };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ChangeAccessibility failed for '{TargetName}' in '{FilePath}'", targetName, filePath);
            return new ToolResult<object>() { Success = false, Error = new ResultError(ToolErrorCode.Exception, $"ChangeAccessibility failed unexpectedly ({ex.GetType().Name}). Check that the solution is loaded and the file path is valid. Details: {ex.Message}") };
        }
    }

    [McpServerTool(Name = "AddSummaryComment")]
    [Produces(DataTag.ChangeId)]
    [Description("Adds or replaces a /// <summary> XML doc comment on a type or member. Replaces existing summary.")]
    public async Task<ToolResult<object>> AddSummaryComment(
        [Consumes(DataTag.SourceFilepath, required: true)] string filepath,
        [Consumes(DataTag.SymbolName, required: true)] string targetName,
        string summaryText,
        [Description(ToolParams.AutoStage)] bool autoStage = true,
        RequestContext<CallToolRequestParams> requestParams = null,
        CancellationToken cancellationToken = default)
    {
        FilePath filePath = FilePath.FromWire(filepath, _workspaceManager.GetSolutionRoot());
        try
        {
            var updated = await _refactoringEngine.AddSummaryCommentAsync(filePath, targetName, summaryText);
            if (!autoStage)
            {
                return new ToolResult<object>() { Success = true, Data = updated.ToJsonSummary() };
            }

            var changes = new Dictionary<FilePath, string> { [filePath] = updated.UpdatedText! };
            var (id, stageError) = await ValidateAndStageAsync(changes, $"Add summary comment to '{targetName}'.", "AddSummaryComment");
            if (stageError is not null)
                return new ToolResult<object> { Success = false, Error = stageError };
            return new ToolResult<object>() { Success = true, Data = new PersistentWorkspaceManager.StagedChangeSummary(id!, [filePath], $"Added XML summary comment to '{targetName}' in {Path.GetFileName(filePath)}.") };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AddSummaryComment failed for '{TargetName}' in '{FilePath}'", targetName, filePath);
            return new ToolResult<object>() { Success = false, Error = new ResultError(ToolErrorCode.Exception, $"AddSummaryComment failed unexpectedly ({ex.GetType().Name}). Check that the solution is loaded and the file path is valid. Details: {ex.Message}") };
        }
    }

    [McpServerTool(Name = "AddConstructorParameter")]
    [Produces(DataTag.ChangeId)]
    [Description("Adds a DI constructor parameter in one step: private readonly field + parameter + body assignment. fieldName overrides the derived field name (defaults to _camelCase of paramName). Creates a constructor if none exists.")]
    public async Task<ToolResult<object>> AddConstructorParameter([Consumes(DataTag.SourceFilepath, required: true)] string filepath,
        [Consumes(DataTag.ClassName, required: true)] string className,
        [Consumes(DataTag.SymbolName, required: true)] string paramName,
        [Consumes(DataTag.DataType, required: true)] string paramType,
        [Consumes(DataTag.SymbolName, required: false)] string? fieldName = null,
        [Description(ToolParams.AutoStage)][ToolOption(ToolOptionTag.AutoStage, required: false)] bool autoStage = true,
        RequestContext<CallToolRequestParams> requestParams = null,
        CancellationToken cancellationToken = default)
    {
        FilePath filePath = FilePath.FromWire(filepath, _workspaceManager.GetSolutionRoot());
        try
        {
            var updated = await _refactoringEngine.AddConstructorParameterAsync(filePath, className, paramName, paramType, fieldName);
            if (!autoStage)
            {
                return new ToolResult<object>() { Success = true, Data = updated.ToJsonSummary() };
            }

            var changes = new Dictionary<FilePath, string> { [filePath] = updated.UpdatedText! };
            var (id, stageError) = await ValidateAndStageAsync(changes, $"Add constructor parameter '{paramName}' to '{className}'.", "AddConstructorParameter");
            if (stageError is not null)
                return new ToolResult<object> { Success = false, Error = stageError };
            return new ToolResult<object>() { Success = true, Data = new PersistentWorkspaceManager.StagedChangeSummary(id!, [filePath], $"Added '{paramType} {paramName}' DI parameter to '{className}' in {Path.GetFileName(filePath)}.") };
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
        RequestContext<CallToolRequestParams> requestParams = null,
        CancellationToken cancellationToken = default)
    {
        FilePath filePath = FilePath.FromWire(filepath, _workspaceManager.GetSolutionRoot());
        try
        {
            var result = await _refactoringEngine.ExtractLocalVariableAsync(filePath, contextSnippet, variableName, lineBefore, lineAfter);
            if (string.IsNullOrEmpty(result.UpdatedText))
            {
                return new ToolResult<object>() { Success = false, Error = new ResultError(ToolErrorCode.Exception, $"ExtractLocalVariable failed for variable '{variableName}' in '{filePath}': file not found in workspace or context snippet did not match any expression. Ensure the solution is loaded.") };
            }

            var changes = new Dictionary<FilePath, string> { [filePath] = result.UpdatedText };
            var (changeId, stageError) = await ValidateAndStageAsync(changes, $"Extract local variable '{variableName}'.", "ExtractLocalVariable");
            if (stageError is not null)
                return new ToolResult<object> { Success = false, Error = stageError };
            return new ToolResult<object> { Success = true, Data = new PersistentWorkspaceManager.StagedChangeSummary(changeId!, [filePath], $"Extracts '{variableName}' as a local variable in {Path.GetFileName(filePath)}.") };
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
        RequestContext<CallToolRequestParams> requestParams = null,
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
                filePath, newMethodName, contextSnippet, lineBefore, lineAfter);
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
    [Description("Adds, replaces, or removes an attribute on a type or member. existingAttribute accepts name with or without brackets (e.g. \"[ApiController]\", \"Required\"). newAttribute required for replace.")]
    public async Task<ToolResult<object>> ModifyAttribute(
        [Consumes(DataTag.SourceFilepath, required: true)] string filepath,
        [Consumes(DataTag.SymbolName, required: true)] string targetName,
        [ExternalInputRequired(DataTag.AttributeName, required: true)] string existingAttribute,
        [ExternalInputRequired(DataTag.AttributeName, required: false)] string newAttribute,
        [ExternalInputRequired(DataTag.Action, required: true)] AttributeModifyAction action,
        [Description(ToolParams.AutoStage)][ToolOption(ToolOptionTag.AutoStage, required: false)] bool autoStage = true,
        RequestContext<CallToolRequestParams> requestParams = null,
        CancellationToken cancellationToken = default)
    {
        FilePath filePath = FilePath.FromWire(filepath, _workspaceManager.GetSolutionRoot());
        try
        {
            DocumentEditResult updated;
            if (action == AttributeModifyAction.add)
            {
                updated = await _refactoringEngine.AddAttributeAsync(filePath, targetName, existingAttribute);
            }
            else if (action == AttributeModifyAction.replace)
            {
                updated = await _refactoringEngine.ReplaceAttributeAsync(filePath, targetName, existingAttribute, newAttribute);
            }
            else if (action == AttributeModifyAction.remove)
            {
                updated = await _refactoringEngine.RemoveAttributeAsync(filePath, targetName, existingAttribute);
            }
            else
            {
                return new ToolResult<object>() { Success = false, Error = new ResultError(ToolErrorCode.InvalidArgument, $"Unhandled action '{action}'.") };
            }
            if (!autoStage)
            {
                return new ToolResult<object>() { Success = true, Data = updated.ToJsonSummary() };
            }
            var changes = new Dictionary<FilePath, string> { [filePath] = updated.UpdatedText! };
            var (id, stageError) = await ValidateAndStageAsync(changes, $"{action} attribute '{existingAttribute}' on '{targetName}'.", "ModifyAttribute");
            if (stageError is not null)
                return new ToolResult<object> { Success = false, Error = stageError };
            var summary = new PersistentWorkspaceManager.StagedChangeSummary(id!, [filePath], $"{(action == AttributeModifyAction.add ? "Adds" : action == AttributeModifyAction.replace ? "Replaces" : "Removes")} '{existingAttribute}' attribute on '{targetName}' in {Path.GetFileName(filePath)}.");
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
    [Description("Adds or removes a modifier keyword on a type or member. modifier: virtual, abstract, sealed, static, readonly, override, partial, async, new, extern, unsafe, volatile.")]
    public async Task<ToolResult<object>> ModifyModifier(
        [Consumes(DataTag.SourceFilepath, required: true)] string filepath,
        [Consumes(DataTag.SymbolName, required: true)] string targetName,
        [ExternalInputRequired(DataTag.Modifier, required: true)] string modifier,
        [Consumes(DataTag.Action, required: true)] AddRemoveAction action,
        [Description(ToolParams.AutoStage)][ToolOption(ToolOptionTag.AutoStage, required: false)] bool autoStage = true,
        RequestContext<CallToolRequestParams> requestParams = null,
        CancellationToken cancellationToken = default)
    {
        FilePath filePath = FilePath.FromWire(filepath, _workspaceManager.GetSolutionRoot());
        try
        {
            DocumentEditResult updated;
            if (action == AddRemoveAction.add)
            {
                updated = await _refactoringEngine.AddModifierAsync(filePath, targetName, modifier);
            }
            else if (action == AddRemoveAction.remove)
            {
                updated = await _refactoringEngine.RemoveModifierAsync(filePath, targetName, modifier);
            }
            else
            {
                return new ToolResult<object>() { Success = false, Error = new ResultError(ToolErrorCode.InvalidArgument, $"Unhandled action '{action}'.") };
            }
            if (!autoStage)
            {
                return new ToolResult<object>() { Success = true, Data = updated.ToJsonSummary() };
            }
            var changes = new Dictionary<FilePath, string> { [filePath] = updated.UpdatedText! };
            var (id, stageError) = await ValidateAndStageAsync(changes, $"{action} '{modifier}' modifier on '{targetName}'.", "ModifyModifier");
            if (stageError is not null)
                return new ToolResult<object> { Success = false, Error = stageError };
            var summary = new PersistentWorkspaceManager.StagedChangeSummary(id!, [filePath], $"{(action == AddRemoveAction.add ? "Adds" : "Removes")} '{modifier}' modifier on '{targetName}' in {Path.GetFileName(filePath)}.");
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
    [Description("Adds or removes a base type or interface from a type declaration.")]
    public async Task<ToolResult<object>> ModifyBaseType(
        [Consumes(DataTag.SourceFilepath, required: true)] string filepath,
        [Consumes(DataTag.SymbolName, required: true)] string typeName,
        string baseTypeName,
        AddRemoveAction action,
        [Description(ToolParams.AutoStage)] bool autoStage = true,
        RequestContext<CallToolRequestParams> requestParams = null,
        CancellationToken cancellationToken = default)
    {
        FilePath filePath = FilePath.FromWire(filepath, _workspaceManager.GetSolutionRoot());
        try
        {
            DocumentEditResult updated;
            if (action == AddRemoveAction.add)
            {
                updated = await _refactoringEngine.AddBaseTypeAsync(filePath, typeName, baseTypeName);
            }
            else if (action == AddRemoveAction.remove)
            {
                updated = await _refactoringEngine.RemoveBaseTypeAsync(filePath, typeName, baseTypeName);
            }
            else
            {
                return new ToolResult<object>() { Success = false, Error = new ResultError(ToolErrorCode.InvalidArgument, $"Unhandled action '{action}'.") };
            }
            if (!autoStage)
            {
                return new ToolResult<object>() { Success = true, Data = updated.ToJsonSummary() };
            }
            var changes = new Dictionary<FilePath, string> { [filePath] = updated.UpdatedText! };
            var (id, stageError) = await ValidateAndStageAsync(changes, $"{action} base type '{baseTypeName}' on '{typeName}'.", "ModifyBaseType");
            if (stageError is not null)
                return new ToolResult<object> { Success = false, Error = stageError };
            var summary = new PersistentWorkspaceManager.StagedChangeSummary(id!, [filePath], $"{(action == AddRemoveAction.add ? "Adds" : "Removes")} '{baseTypeName}' on '{typeName}' in {Path.GetFileName(filePath)}.");
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
    [Description("Adds a new member to a type. position: null/\"end\" (append), \"after:MemberName\", or \"before:MemberName\".")]
    public async Task<ToolResult<object>> AddMember(
        [Consumes(DataTag.SourceFilepath, required: true)] string filepath,
        [Consumes(DataTag.SymbolName, required: true)] string containerName,
        [ExternalInputRequired(DataTag.ClassName)] string newMemberSource,
        [ExternalInputRequired(DataTag.Position)] string? position = null,
        [Description(ToolParams.AutoStage)][ToolOption(ToolOptionTag.AutoStage, required: false)] bool autoStage = true,
        RequestContext<CallToolRequestParams> requestParams = null,
        CancellationToken cancellationToken = default)
    {
        FilePath filePath = FilePath.FromWire(filepath, _workspaceManager.GetSolutionRoot());
        try
        {
            DocumentEditResult updated;
            string description;
            if (string.IsNullOrEmpty(position) || position == "end")
            {
                updated = await _refactoringEngine.AddMemberAsync(filePath, containerName, newMemberSource);
                description = $"Added new member to '{containerName}' in {Path.GetFileName(filePath)}.";
            }
            else if (position.StartsWith("after:", StringComparison.OrdinalIgnoreCase))
            {
                var afterMemberName = position.Substring("after:".Length);
                updated = await _refactoringEngine.InsertMemberAfterAsync(filePath, containerName, afterMemberName, newMemberSource);
                description = $"Inserted new member after '{afterMemberName}' in '{containerName}' in {Path.GetFileName(filePath)}.";
            }
            else if (position.StartsWith("before:", StringComparison.OrdinalIgnoreCase))
            {
                var beforeMemberName = position.Substring("before:".Length);
                updated = await _refactoringEngine.InsertMemberBeforeAsync(filePath, containerName, beforeMemberName, newMemberSource);
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
            var changes = new Dictionary<FilePath, string> { [filePath] = updated.UpdatedText };
            var (id, stageError) = await ValidateAndStageAsync(changes, description, "AddMember");
            if (stageError is not null)
                return new ToolResult<object> { Success = false, Error = stageError };
            var summary = new PersistentWorkspaceManager.StagedChangeSummary(id!, [filePath], description);
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
    [Description("Generates a typed member and adds it to a type. property → auto-property (defaults: hasSetter=true, accessibility=public). field → field (defaults: isReadonly=false, isStatic=false, accessibility=private).")]
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
        [Description(ToolParams.AutoStage)][ToolOptionAttribute(ToolOptionTag.AutoStage)] bool autoStage = true,
        RequestContext<CallToolRequestParams> requestParams = null,
        CancellationToken cancellationToken = default)
    {
        FilePath filePath = FilePath.FromWire(filepath, _workspaceManager.GetSolutionRoot());
        try
        {
            DocumentEditResult updated;
            string description;
            if (kind == TypedMemberKind.property)
            {
                updated = await _refactoringEngine.AddPropertyAsync(filePath, containerName, name, type, accessibility, hasSetter, isInit);
                description = $"Added '{type} {name}' property to '{containerName}' in {Path.GetFileName(filePath)}.";
            }
            else if (kind == TypedMemberKind.field)
            {
                updated = await _refactoringEngine.AddFieldAsync(filePath, containerName, name, type, accessibility, isReadonly, isStatic, initializer);
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
            var changes = new Dictionary<FilePath, string> { [filePath] = updated.UpdatedText! };
            var (id, stageError) = await ValidateAndStageAsync(changes, description, "AddMemberTyped");
            if (stageError is not null)
                return new ToolResult<object> { Success = false, Error = stageError };
            var summary = new PersistentWorkspaceManager.StagedChangeSummary(id!, [filePath], description);
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
        RequestContext<CallToolRequestParams> requestParams = null,
        CancellationToken cancellationToken = default)
    {
        FilePath filePath = FilePath.FromWire(filepath, _workspaceManager.GetSolutionRoot());

        try
        {
            var result = await _structuralRefinementEngine.SyncTypeAndFilenameAsync(filePath);
            return new ToolResult<object> { Success = true, Data = result };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SyncTypeAndFilename unexpected exception for '{FilePath}'", filePath);
            return new ToolResult<object> { Success = false, Error = new ResultError(ToolErrorCode.Exception, $"SyncTypeAndFilename for '{filePath}' failed unexpectedly ({ex.GetType().Name}). Check that the solution is loaded and the file path is valid. Details: {ex.Message}") };
        }
    }
}