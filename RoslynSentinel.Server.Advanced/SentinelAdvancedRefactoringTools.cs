using System.ComponentModel;

using Microsoft.CodeAnalysis;
using Microsoft.Extensions.Logging;

using ModelContextProtocol.Server;

namespace RoslynSentinel.Server.Advanced;

[McpServerToolType]
public class SentinelAdvancedRefactoringTools
{
    private readonly RefactoringEngine _refactoringEngine;
    private readonly StandardRefactoringEngine _standardRefactoringEngine;
    private readonly AdvancedStructuralEngine _advancedStructuralEngine;
    private readonly MappingEngine _mappingEngine;
    private readonly SemanticRefactoringLibrary _semanticRefactoringLibrary;
    private readonly GranularRefactoringEngine _granularRefactoringEngine;
    private readonly AdvancedLogicEngine _advancedLogicEngine;
    private readonly RefinementEngine _refinementEngine;
    private readonly AdvancedTypeEngine _advancedTypeEngine;
    private readonly StructuralRefinementEngine _structuralRefinementEngine;
    private readonly CodeStyleEngine _codeStyleEngine;
    private readonly CodeFlowEngine _codeFlowEngine;
    private readonly AdvancedRefactoringEngine _advancedRefactoringEngine;
    private readonly LogicOptimizationEngine _logicOptimizationEngine;
    private readonly OutParamRefactoringEngine _outParamRefactoringEngine;
    private readonly MsToolAugmentEngine _augmentEngine;
    private readonly CodeGenerationEngine _codeGenerationEngine;
    private readonly SymbolNavigationEngine _symbolNavigationEngine;
    private readonly PersistentWorkspaceManager _workspaceManager;
    private readonly ValidationEngine _validationEngine;
    private readonly SentinelConfiguration _config;
    private readonly ILogger<SentinelAdvancedRefactoringTools> _logger;

    public SentinelAdvancedRefactoringTools(
        RefactoringEngine refactoringEngine,
        StandardRefactoringEngine standardRefactoringEngine,
        AdvancedStructuralEngine advancedStructuralEngine,
        MappingEngine mappingEngine,
        SemanticRefactoringLibrary semanticRefactoringLibrary,
        GranularRefactoringEngine granularRefactoringEngine,
        AdvancedLogicEngine advancedLogicEngine,
        RefinementEngine refinementEngine,
        AdvancedTypeEngine advancedTypeEngine,
        StructuralRefinementEngine structuralRefinementEngine,
        CodeStyleEngine codeStyleEngine,
        CodeFlowEngine codeFlowEngine,
        AdvancedRefactoringEngine advancedRefactoringEngine,
        LogicOptimizationEngine logicOptimizationEngine,
        ModernizationEngine modernizationEngine,
        OutParamRefactoringEngine outParamRefactoringEngine,
        MsToolAugmentEngine augmentEngine,
        CodeGenerationEngine codeGenerationEngine,
        SymbolNavigationEngine symbolNavigationEngine,
        PersistentWorkspaceManager workspaceManager,
        ValidationEngine validationEngine,
        SentinelConfiguration config,
        ILogger<SentinelAdvancedRefactoringTools> logger)
    {
        _refactoringEngine = refactoringEngine;
        _standardRefactoringEngine = standardRefactoringEngine;
        _advancedStructuralEngine = advancedStructuralEngine;
        _mappingEngine = mappingEngine;
        _semanticRefactoringLibrary = semanticRefactoringLibrary;
        _granularRefactoringEngine = granularRefactoringEngine;
        _advancedLogicEngine = advancedLogicEngine;
        _refinementEngine = refinementEngine;
        _advancedTypeEngine = advancedTypeEngine;
        _structuralRefinementEngine = structuralRefinementEngine;
        _codeStyleEngine = codeStyleEngine;
        _codeFlowEngine = codeFlowEngine;
        _advancedRefactoringEngine = advancedRefactoringEngine;
        _logicOptimizationEngine = logicOptimizationEngine;
        _outParamRefactoringEngine = outParamRefactoringEngine;
        _augmentEngine = augmentEngine;
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

    private string? GetFileNotInSolutionError(FilePath filePath)
    {
        var ids = _workspaceManager.CurrentSolution?.GetDocumentIdsWithFilePath(filePath);
        if (ids == null || ids.Value.Length == 0)
            return $"File '{Path.GetFileName(filePath)}' not found in the loaded solution. " +
                   $"Verify the path is correct and the solution is loaded. " +
                   $"Loaded projects: {_workspaceManager.ProjectCount}.";
        return null;
    }

    private async Task<(string? ChangeId, ResultError? Error)> ValidateAndStageAsync(
        Dictionary<FilePath, string> changes, string description, string operationName)
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

    [McpServerTool(Name = "ChangeSignature")]
    [Produces(DataTag.ResultOnly)]
    [Description("Reorders method parameters and updates all call sites across the solution. newParameterOrder: zero-based index array specifying the new parameter order. autoStage=true → ChangeId.")]
    public async Task<ToolResult<object>> ChangeSignature(
        [Consumes(DataTag.SourceFilepath, required: true)] string filepath,
        [Consumes(DataTag.SymbolName, required: true)] string methodName,
        [ExternalInputRequired(DataTag.Order, required: true)] int[] newParameterOrder,
        [ToolOption(ToolOptionTag.AutoStage, required: false)] bool autoStage = true,
        Progress<string>? progress = null,
        CancellationToken? cancellationToken = default)
    {
        FilePath filePath = FilePath.FromWire(filepath, _workspaceManager.GetSolutionRoot());

        try
        {
            var changes = await _refactoringEngine.ChangeSignatureAsync(filePath, methodName, newParameterOrder);
            if (!autoStage)
                return new ToolResult<object>() { Success = true, Data = new { Changes = changes } };

            var (id, stageError) = await ValidateAndStageAsync(changes, $"Change signature of method '{methodName}'.", "ChangeSignature");
            if (stageError is not null)
                return new ToolResult<object> { Success = false, Error = stageError };
            return new ToolResult<object>() { Success = true, Data = new PersistentWorkspaceManager.StagedChangeSummary(id!, changes.Keys.ToList(), $"Reorders parameters of '{methodName}' in {Path.GetFileName(filePath)}.") };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ChangeSignature failed for '{MethodName}' in '{FilePath}'", methodName, filePath);
            return new ToolResult<object>() { Success = false, Error = new ResultError(ToolErrorCode.Exception, $"ChangeSignature failed for '{methodName}' in '{filePath}': {ex.GetType().Name}: {ex.Message}") };
        }
    }

    [McpServerTool(Name = "ConvertAnonymousToNamed")]
    [Produces(DataTag.ChangeId)]
    [Description("Converts the first anonymous object creation expression in the file to a formal named class declaration. Validates and stages the result — pass the returned changeId to staged_change(action=\"apply\") to commit.")]
    public async Task<ToolResult<object>> ConvertAnonymousToNamed(
        [ExternalInputRequired(DataTag.SourceFilepath, required: true)] string filepath,
        [ExternalInputRequired(DataTag.ClassName, required: true)] string newClassName,
        Progress<string>? progress = null,
        CancellationToken? cancellationToken = default)
    {
        FilePath filePath = FilePath.FromWire(filepath, _workspaceManager.GetSolutionRoot());
        try
        {
            var changes = await _advancedTypeEngine.ConvertAnonymousToNamedAsync(filePath, newClassName);
            if (changes.Count == 0)
                return new ToolResult<object> { Success = false, Error = new ResultError(ToolErrorCode.Exception, $"ConvertAnonymousToNamed: no anonymous object found in '{filePath}'.") };

            var (id, stageError) = await ValidateAndStageAsync(changes, $"Convert anonymous object to '{newClassName}'.", "ConvertAnonymousToNamed");
            if (stageError is not null)
                return new ToolResult<object> { Success = false, Error = stageError };
            return new ToolResult<object>() { Success = true, Data = new PersistentWorkspaceManager.StagedChangeSummary(id!, changes.Keys.ToList(), $"Converts anonymous object to named class '{newClassName}' in {Path.GetFileName(filePath)}.") };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ConvertAnonymousToNamed failed for '{NewClassName}' in '{FilePath}'", newClassName, filePath);
            return new ToolResult<object>() { Success = false, Error = new ResultError(ToolErrorCode.Exception, $"ConvertAnonymousToNamed failed for '{newClassName}' in '{filePath}': {ex.GetType().Name}: {ex.Message}") };
        }
    }


    [McpServerTool(Name = "InlineClass")]
    [Produces(DataTag.ChangeId)]
    [Description("Merges all members of a source class into a target class and removes the source class declaration. Works within the same file or across files. Updates all type references throughout the solution. Validates and stages the result — pass the returned changeId to staged_change(action=\"apply\") to commit.")]
    public async Task<ToolResult<object>> InlineClass(
        [Consumes(DataTag.SourceFilepath, required: true)] string rawSourceFilePath,
        [Consumes(DataTag.SourceFilepath, required: true)] string rawTargetFilePath,
        [Consumes(DataTag.SymbolName, required: true)] string className,
        Progress<string>? progress = null,
        CancellationToken? cancellationToken = default)
    {
        FilePath sourceFilePath = FilePath.FromWire(rawSourceFilePath, _workspaceManager.GetSolutionRoot());
        FilePath targetFilePath = FilePath.FromWire(rawTargetFilePath, _workspaceManager.GetSolutionRoot());
        try
        {
            var changes = await _advancedStructuralEngine.InlineClassAsync(sourceFilePath, targetFilePath, className);
            if (changes.Count == 0)
                return new ToolResult<object> { Success = false, Error = new ResultError(ToolErrorCode.Exception, $"InlineClass: class '{className}' not found in '{sourceFilePath}'.") };

            var (id, stageError) = await ValidateAndStageAsync(changes, $"Inline class '{className}' into target.", "InlineClass");
            if (stageError is not null)
                return new ToolResult<object> { Success = false, Error = stageError };
            return new ToolResult<object>() { Success = true, Data = new PersistentWorkspaceManager.StagedChangeSummary(id!, changes.Keys.ToList(), $"Inlines '{className}' members into target class across {changes.Count} file(s).") };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "InlineClass failed for '{ClassName}'", className);
            return new ToolResult<object>() { Success = false, Error = new ResultError(ToolErrorCode.Exception, $"InlineClass failed for '{className}' in '{sourceFilePath}' to '{targetFilePath}': {ex.GetType().Name}: {ex.Message}") };
        }
    }

    [McpServerTool(Name = "MoveAllTypesToFiles")]
    [Produces(DataTag.Report)]
    [Description("""
        Moves all secondary types to their own files. scope=file → requires scopeName (file path), returns ChangeId + first-15-line content previews. scope=project → requires scopeName (project name), returns ChangeId + affected file list. scope=solution → scopeName ignored. autoStage=false → returns raw changes dictionary without staging.
        """)]
    public async Task<ToolResult<object>> MoveAllTypesToFiles(
        string scope,
        string? scopeName = null,
        bool autoStage = true,
        Progress<string>? progress = null,
        CancellationToken? cancellationToken = default)
    {
        try
        {
            if (scope == "file")
            {
                if (scopeName is null)
                {
                    return new ToolResult<object>() { Success = false, Error = new ResultError(ToolErrorCode.InvalidArgument, "scopeName (file path) is required for scope=file.") };
                }

                return await MoveAllTypesToFilesCore(
                    await _refactoringEngine.MoveAllTypesToFilesAsync(scopeName),
                    autoStage,
                    $"Move all types to files in '{Path.GetFileName(scopeName)}'",
                    previewFiles: true);
            }
            if (scope == "project")
            {
                if (scopeName is null)
                {
                    return new ToolResult<object>() { Success = false, Error = new ResultError(ToolErrorCode.InvalidArgument, "scopeName (project name) is required for scope=project.") };
                }

                return await MoveAllTypesToFilesCore(
                    await _refactoringEngine.MoveAllTypesToFilesInProjectAsync(scopeName),
                    autoStage,
                    $"Move all types to files in project '{scopeName}'",
                    previewFiles: false);
            }
            if (scope == "solution")
            {
                return await MoveAllTypesToFilesCore(
                    await _refactoringEngine.MoveAllTypesToFilesInSolutionAsync(),
                    autoStage,
                    "Move all types to files in solution",
                    previewFiles: false);
            }
            return new ToolResult<object>() { Success = false, Error = new ResultError(ToolErrorCode.Exception, $"Unknown scope '{scope}'. Valid: file, project, solution.") };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "MoveAllTypesToFiles ({Scope}) failed", scope);
            return new ToolResult<object>() { Success = false, Error = new ResultError(ToolErrorCode.Exception, $"MoveAllTypesToFiles failed unexpectedly ({ex.GetType().Name}). Check that the solution is loaded and the file path is valid. Details: {ex.Message}") };
        }
    }

    private async Task<ToolResult<object>> MoveAllTypesToFilesCore(
    Dictionary<FilePath, string> changes,
    bool autoStage,
    string description,
    bool previewFiles)
    {
        if (!autoStage)
            return new ToolResult<object>() { Success = true, Data = changes };

        if (changes.Count == 0)
            return new ToolResult<object>() { Success = true, Data = "No secondary types found to move." };

        var (id, stageError) = await ValidateAndStageAsync(changes, description, "MoveAllTypesToFiles");
        if (stageError is not null)
            return new ToolResult<object> { Success = false, Error = stageError };

        if (previewFiles)
        {
            return new ToolResult<object>()
            {
                Success = true,
                Data = new
                {
                    ChangeId = id,
                    Description = $"{description}. Call staged_change(action=\"apply\", changeId=\"{id}\") to apply.",
                    AffectedFiles = changes.Keys.Select(kvp => Path.GetFileName(kvp)).ToList(),
                    ContentPreviews = changes.ToDictionary(
                        kvp => Path.GetFileName(kvp.Key)!,
                    kvp => PreviewFileContent(kvp.Value))
                }
            };
        }

        return new ToolResult<object>()
        {
            Success = true,
            Data = new PersistentWorkspaceManager.StagedChangeSummary(id!, changes.Keys.ToList(), description)
        };
    }

    [McpServerTool(Name = "InvertAssignments")]
    [Produces(DataTag.ChangeId)]
    [Description("Swaps left and right sides of all assignment statements within a 1-based line range. Validates and stages the result — pass the returned changeId to staged_change(action=\"apply\") to commit.")]
    public async Task<ToolResult<object>> InvertAssignments(
        [Consumes(DataTag.SourceFilepath, required: true)] string filepath,
        [Consumes(DataTag.StartLine)] int startLine,
        [Consumes(DataTag.EndLine)] int endLine,
        Progress<string>? progress = null,
        CancellationToken? cancellationToken = default)
    {
        FilePath filePath = FilePath.FromWire(filepath, _workspaceManager.GetSolutionRoot());
        try
        {
            var result = await _mappingEngine.InvertAssignmentsAsync(filePath, startLine, endLine);
            if (string.IsNullOrEmpty(result.UpdatedText))
                return new ToolResult<object> { Success = false, Error = new ResultError(ToolErrorCode.Exception, $"InvertAssignments: no assignments found in lines {startLine}-{endLine} of '{filePath}'.") };

            var changes = new Dictionary<FilePath, string> { [filePath] = result.UpdatedText };
            var (id, stageError) = await ValidateAndStageAsync(changes, $"Invert assignments in lines {startLine}-{endLine}.", "InvertAssignments");
            if (stageError is not null)
                return new ToolResult<object> { Success = false, Error = stageError };
            return new ToolResult<object>() { Success = true, Data = new PersistentWorkspaceManager.StagedChangeSummary(id!, [filePath], $"Inverts assignments in lines {startLine}-{endLine} of {Path.GetFileName(filePath)}.") };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "InvertAssignments failed in '{FilePath}'", filePath);
            return new ToolResult<object>() { Success = false, Error = new ResultError(ToolErrorCode.Exception, $"InvertAssignments failed unexpectedly ({ex.GetType().Name}). Check that the solution is loaded and the file path is valid. Details: {ex.Message}") };
        }
    }

    [McpServerTool(Name = "PullUpMember")]
    [Produces(DataTag.ResultOnly)]
    [Description("""
        Pulls a method or property from a derived class into its base class. Removes override, adds virtual (if not already abstract/virtual), and moves the declaration. Returns a two-file change dict (derived + base class). Requires the base class to have accessible source in the solution. autoStage=true → ChangeId.
        """)]
    public async Task<ToolResult<object>> PullUpMember(
        [Consumes(DataTag.SourceFilepath, required: true)] string filepath,
        [Consumes(DataTag.SymbolName, required: true)] string className,
        [Consumes(DataTag.SymbolName, required: true)] string memberName,
        [ToolOption(ToolOptionTag.AutoStage, required: false)] bool autoStage = true,
        Progress<string>? progress = null,
        CancellationToken? cancellationToken = default)
    {
        try
        {
            FilePath filePath = FilePath.FromWire(filepath, _workspaceManager.GetSolutionRoot());
            var changes = await _refinementEngine.PullUpMemberAsync(filePath, className, memberName);
            if (!autoStage)
            {
                return new ToolResult<object>() { Success = true, Data = changes };
            }

            if (changes.Count == 0)
            {
                return new ToolResult<object>() { Success = false, Error = new ResultError(ToolErrorCode.Exception, $"Member '{memberName}' not found or no accessible base class available.") };
            }

            var (id, stageError) = await ValidateAndStageAsync(changes, $"Pull up '{memberName}' from '{className}' to base class.", "PullUpMember");
            if (stageError is not null)
                return new ToolResult<object> { Success = false, Error = stageError };
            return new ToolResult<object>() { Success = true, Data = new PersistentWorkspaceManager.StagedChangeSummary(id!, changes.Keys.ToList(), $"Pulls '{memberName}' from '{className}' up to its base class.") };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "PullUpMember failed for '{MemberName}' in '{ClassName}'", memberName, className);
            return new ToolResult<object>() { Success = false, Error = new ResultError(ToolErrorCode.Exception, $"PullUpMember failed unexpectedly ({ex.GetType().Name}). Check that the solution is loaded and the file path is valid. Details: {ex.Message}") };
        }
    }

    [McpServerTool(Name = "IntroduceParameterObject")]
    [Produces(DataTag.ChangeId)]
    [Description("Encapsulates method parameters into a new C# 12 record type. Groups all non-CancellationToken parameters (or only parameterNames if specified) into public record {NewTypeName}(...). Rewrites parameter references in the method body to request.PropertyName. Appends the record to end of file. Adds a TODO comment to update call sites — call sites must be updated manually. Validates and stages the result — pass the returned changeId to staged_change(action=\"apply\") to commit.")]
    public async Task<ToolResult<object>> IntroduceParameterObject(
        [Consumes(DataTag.SourceFilepath, required: true)] string filepath,
        [Consumes(DataTag.SymbolName, required: true)] string methodName,
        string? newTypeName = null,
        string[]? parameterNames = null,
        Progress<string>? progress = null,
        CancellationToken? cancellationToken = default)
    {
        FilePath filePath = FilePath.FromWire(filepath, _workspaceManager.GetSolutionRoot());
        try
        {
            var fileErr = GetFileNotInSolutionError(filePath);
            if (fileErr != null) return new ToolResult<object>() { Success = false, Error = new ResultError(ToolErrorCode.Exception, fileErr) };

            var result = await _granularRefactoringEngine.IntroduceParameterObjectAsync(filePath, methodName, newTypeName, parameterNames);
            if (string.IsNullOrEmpty(result.UpdatedText))
                return new ToolResult<object>() { Success = false, Error = new ResultError(ToolErrorCode.Exception,
                    $"IntroduceParameterObject: method '{methodName}' not found in '{Path.GetFileName(filePath)}'. " +
                    "Verify the method name (case-sensitive) or use get_file_outline to list available methods.") };

            var changes = new Dictionary<FilePath, string> { [filePath] = result.UpdatedText };
            var (id, stageError) = await ValidateAndStageAsync(changes, $"Introduce parameter object for '{methodName}'.", "IntroduceParameterObject");
            if (stageError is not null)
                return new ToolResult<object> { Success = false, Error = stageError };
            return new ToolResult<object>() { Success = true, Data = new PersistentWorkspaceManager.StagedChangeSummary(id!, [filePath], $"Introduces parameter object for '{methodName}' in {Path.GetFileName(filePath)}.") };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "IntroduceParameterObject failed for '{MethodName}' in '{FilePath}'", methodName, filePath);
            return new ToolResult<object>() { Success = false, Error = new ResultError(ToolErrorCode.Exception, $"IntroduceParameterObject failed unexpectedly ({ex.GetType().Name}). Check that the solution is loaded and the file path is valid. Details: {ex.Message}") };
        }
    }

    [McpServerTool(Name = "Introduce")]
    [Produces(DataTag.ChangeId)]
    [Description("Introduces a named symbol from an expression. as values: localVariable, field (private readonly), parameter (single-file), constant (→ MsAugmentResult). contextSnippet: verbatim substring identifying the expression. lineBefore/lineAfter disambiguate. Validates and stages localVariable/field/parameter — pass the returned changeId to staged_change(action=\"apply\") to commit.")]
    public async Task<ToolResult<object>> Introduce(
        [Consumes(DataTag.SourceFilepath, required: true)] string filepath,
        [Consumes(DataTag.ContextSnippet, required: true)] string contextSnippet,
        [ExternalInputRequired(DataTag.SymbolName)] string newName,
        [ExternalInputRequired(DataTag.SymbolKind)] string @as,
        [Consumes(DataTag.LineBefore)] string? lineBefore = null,
        [Consumes(DataTag.LineAfter)] string? lineAfter = null,
        Progress<string>? progress = null,
        CancellationToken? cancellationToken = default)
    {
        FilePath filePath = FilePath.FromWire(filepath, _workspaceManager.GetSolutionRoot());
        try
        {
            DocumentEditResult result;
            string stageDesc;
            if (@as == "localVariable")
            {
                result = await _granularRefactoringEngine.IntroduceVariableAsync(filePath, contextSnippet, newName, lineBefore, lineAfter);
                stageDesc = $"Introduce local variable '{newName}'.";
            }
            else if (@as == "field")
            {
                result = await _granularRefactoringEngine.IntroduceFieldAsync(filePath, contextSnippet, newName, lineBefore, lineAfter);
                stageDesc = $"Introduce field '{newName}'.";
            }
            else if (@as == "parameter")
            {
                result = await _granularRefactoringEngine.IntroduceParameterAsync(filePath, contextSnippet, newName, lineBefore, lineAfter);
                stageDesc = $"Introduce parameter '{newName}'.";
            }
            else if (@as == "constant")
            {
                return new ToolResult<object>() { Success = true, Data = await _augmentEngine.ExtractConstantSafeAsync(filePath, contextSnippet, newName, lineBefore, lineAfter) };
            }
            else
            {
                return new ToolResult<object>() { Success = false, Error = new ResultError(ToolErrorCode.Exception, $"Unknown as '{@as}'. Valid values: localVariable, field, parameter, constant.") };
            }

            if (string.IsNullOrEmpty(result.UpdatedText))
                return new ToolResult<object> { Success = false, Error = new ResultError(ToolErrorCode.Exception, $"Introduce({@as}): context snippet '{contextSnippet}' not matched in '{filePath}'.") };

            var changes = new Dictionary<FilePath, string> { [filePath] = result.UpdatedText };
            var (id, stageError) = await ValidateAndStageAsync(changes, stageDesc, $"Introduce({@as})");
            if (stageError is not null)
                return new ToolResult<object> { Success = false, Error = stageError };
            return new ToolResult<object>() { Success = true, Data = new PersistentWorkspaceManager.StagedChangeSummary(id!, [filePath], $"Introduces '{newName}' as {(@as == "localVariable" ? "a local variable" : @as)} in {Path.GetFileName(filePath)}.") };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Introduce ({As}) failed for '{NewName}' in '{FilePath}'", @as, newName, filePath);
            return new ToolResult<object>() { Success = false, Error = new ResultError(ToolErrorCode.Exception, $"Introduce failed unexpectedly ({ex.GetType().Name}). Check that the solution is loaded and the file path is valid. Details: {ex.Message}") };
        }
    }

    [McpServerTool(Name = "ExtractMembers")]
    [Produces(DataTag.ChangeId)]
    [Description("Extracts members from a class into a new type. as values: interface (public API → new interface file, requires newTypeName), class (named members → new class, requires memberNames + newTypeName), partial (named members → new partial file, requires memberNames), superclass (common members → new base class, requires newTypeName; for multiple classes supply filePaths[] + classNames[]). autoStage=true → ChangeId where applicable.")]
    public async Task<ToolResult<object>> ExtractMembers(
        [Consumes(DataTag.SourceFilepath, required: true)] string filepath,
        [Consumes(DataTag.SymbolName, required: true)] string className,
        [ExternalInputRequired(DataTag.SymbolKind)] string @as,
        [ExternalInputRequired(DataTag.SymbolName)] string? newTypeName = null,
        [ExternalInputRequired(DataTag.SymbolName)] string[]? memberNames = null,
        [ExternalInputRequired(DataTag.SourceFilepath)] FilePath[]? filePaths = null,
        [ExternalInputRequired(DataTag.ClassName)] string[]? classNames = null,
        [ToolOption(ToolOptionTag.AutoStage, required: false)] bool autoStage = true,
        Progress<string>? progress = null,
        CancellationToken? cancellationToken = default)
    {
        FilePath filePath = FilePath.FromWire(filepath, _workspaceManager.GetSolutionRoot());
        try
        {
            if (@as == "interface")
            {
                if (string.IsNullOrEmpty(newTypeName))
                {
                    return new ToolResult<object>() { Success = false, Error = new ResultError(ToolErrorCode.InvalidArgument, "newTypeName (interface name) is required when as=interface.") };
                }
                try
                {
                    var changes = await _refactoringEngine.ExtractInterfaceAsync(filePath, className, newTypeName);
                    if (!autoStage)
                        return new ToolResult<object>() { Success = true, Data = new { Changes = changes } };

                    var (id, stageError) = await ValidateAndStageAsync(changes, $"Extract interface '{newTypeName}' from '{className}'.", "ExtractMembers/interface");
                    if (stageError is not null)
                        return new ToolResult<object> { Success = false, Error = stageError };
                    return new ToolResult<object>() { Success = true, Data = new PersistentWorkspaceManager.StagedChangeSummary(id!, changes.Keys.ToList(), $"Extracts interface '{newTypeName}' from '{className}'.") };
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "ExtractMembers/interface unexpected exception for '{NewTypeName}'", newTypeName);
                    return new ToolResult<object>() { Success = false, Error = new ResultError(ToolErrorCode.Exception, $"ExtractMembers as=interface for '{newTypeName}' failed unexpectedly ({ex.GetType().Name}). Check that the solution is loaded and the file path is valid. Details: {ex.Message}") };
                }
            }
            if (@as == "class")
            {
                if (memberNames == null || memberNames.Length == 0)
                {
                    return new ToolResult<object>() { Success = false, Error = new ResultError(ToolErrorCode.InvalidArgument, "memberNames is required when as=class.") };
                }
                if (string.IsNullOrEmpty(newTypeName))
                {
                    return new ToolResult<object>() { Success = false, Error = new ResultError(ToolErrorCode.InvalidArgument, "newTypeName (new class name) is required when as=class.") };
                }
                var classChanges = await _advancedStructuralEngine.ExtractClassAsync(filePath, className, newTypeName, memberNames);
                if (!autoStage)
                    return new ToolResult<object>() { Success = true, Data = classChanges };

                var (classId, classError) = await ValidateAndStageAsync(classChanges, $"Extract class '{newTypeName}' from '{className}'.", "ExtractMembers/class");
                if (classError is not null)
                    return new ToolResult<object> { Success = false, Error = classError };
                return new ToolResult<object>() { Success = true, Data = new PersistentWorkspaceManager.StagedChangeSummary(classId!, classChanges.Keys.ToList(), $"Extracts '{newTypeName}' class from '{className}'.") };
            }
            if (@as == "partial")
            {
                if (memberNames == null || memberNames.Length == 0)
                {
                    return new ToolResult<object>() { Success = false, Error = new ResultError(ToolErrorCode.InvalidArgument, "memberNames is required when as=partial.") };
                }
                var partialChanges = await _granularRefactoringEngine.ExtractMembersToPartialAsync(filePath, className, memberNames);
                if (!autoStage)
                    return new ToolResult<object>() { Success = true, Data = partialChanges };

                var (partialId, partialError) = await ValidateAndStageAsync(partialChanges, $"Extract members to partial for '{className}'.", "ExtractMembers/partial");
                if (partialError is not null)
                    return new ToolResult<object> { Success = false, Error = partialError };
                return new ToolResult<object>() { Success = true, Data = new PersistentWorkspaceManager.StagedChangeSummary(partialId!, partialChanges.Keys.ToList(), $"Extracts members of '{className}' to a new partial file.") };
            }
            if (@as == "superclass")
            {
                if (string.IsNullOrEmpty(newTypeName))
                {
                    return new ToolResult<object>() { Success = false, Error = new ResultError(ToolErrorCode.InvalidArgument, "newTypeName (new base class name) is required when as=superclass.") };
                }
                var actualFilePaths = filePaths ?? new[] { filePath };
                var actualClassNames = classNames ?? new[] { className };
                try
                {
                    var changes = await _advancedStructuralEngine.ExtractSuperclassAsync(actualFilePaths, actualClassNames, newTypeName);
                    if (!autoStage)
                        return new ToolResult<object>() { Success = true, Data = new { Changes = changes } };

                    var (id, stageError) = await ValidateAndStageAsync(changes, $"Extract superclass '{newTypeName}' from {actualClassNames.Length} class(es).", "ExtractMembers/superclass");
                    if (stageError is not null)
                        return new ToolResult<object> { Success = false, Error = stageError };
                    return new ToolResult<object>() { Success = true, Data = new PersistentWorkspaceManager.StagedChangeSummary(id!, changes.Keys.ToList(), $"Extracts superclass '{newTypeName}' from {actualClassNames.Length} class(es).") };
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "ExtractMembers/superclass unexpected exception for '{NewTypeName}'", newTypeName);
                    return new ToolResult<object>() { Success = false, Error = new ResultError(ToolErrorCode.Exception, $"ExtractMembers as=superclass for '{newTypeName}' failed unexpectedly ({ex.GetType().Name}). Check that the solution is loaded and the file path is valid. Details: {ex.Message}") };
                }
            }
            return new ToolResult<object>() { Success = false, Error = new ResultError(ToolErrorCode.Exception, $"Unknown as '{@as}'. Valid values: interface, class, partial, superclass.") };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ExtractMembers ({As}) failed for '{ClassName}' in '{FilePath}'", @as, className, filePath);
            return new ToolResult<object>() { Success = false, Error = new ResultError(ToolErrorCode.Exception, $"ExtractMembers failed unexpectedly ({ex.GetType().Name}). Check that the solution is loaded and the file path is valid. Details: {ex.Message}") };
        }
    }

    [McpServerTool(Name = "SyncInterface")]
    [Produces(DataTag.ResultOnly)]
    [Description("Manages interface/class synchronization. action values: implement (generate stub implementations for all unimplemented interface members on className → returns updated file content), sync (add to interface any public members in className missing from interfaceName → returns updated interface file), verify (report coverage of all implementing classes → requires only interfaceName; use projectName to scope). filePath is the class file for implement/sync.")]
    public async Task<ToolResult<object>> SyncInterface(
        [Consumes(DataTag.SourceFilepath, required: true)] string filepath,
        [Consumes(DataTag.SymbolName, required: true)] string interfaceName,
        [Consumes(DataTag.Action, required: true)] string action,
        [Consumes(DataTag.SymbolName)] string? className = null,
        [Consumes(DataTag.ProjectName)] string? projectName = null,
        Progress<string>? progress = null,
        CancellationToken? cancellationToken = default)
    {
        try
        {
            FilePath filePath = FilePath.FromWire(filepath, _workspaceManager.GetSolutionRoot());
            if (action == "implement")
            {
                if (string.IsNullOrEmpty(className))
                    return new ToolResult<object>() { Success = false, Error = new ResultError(ToolErrorCode.InvalidArgument, "className is required when action=implement.") };

                var implFileErr = GetFileNotInSolutionError(filePath);
                if (implFileErr != null) return new ToolResult<object>() { Success = false, Error = new ResultError(ToolErrorCode.Exception, implFileErr) };

                var implResult = await _codeGenerationEngine.ImplementInterfaceAsync(filePath, className, interfaceName);
                if (string.IsNullOrEmpty(implResult.UpdatedText))
                    return new ToolResult<object>() { Success = false, Error = new ResultError(ToolErrorCode.Exception,
                        $"SyncInterface implement: class '{className}' or interface '{interfaceName}' not found in '{Path.GetFileName(filePath)}'. " +
                        "Verify both names are spelled correctly (case-sensitive). Use locate_symbol to confirm the interface exists in the solution.") };

                var implChanges = new Dictionary<FilePath, string> { [filePath] = implResult.UpdatedText };
                var (implId, implError) = await ValidateAndStageAsync(implChanges, $"Implement '{interfaceName}' on '{className}'.", "SyncInterface/implement");
                if (implError is not null)
                    return new ToolResult<object> { Success = false, Error = implError };
                return new ToolResult<object>() { Success = true, Data = new PersistentWorkspaceManager.StagedChangeSummary(implId!, [filePath], $"Implements '{interfaceName}' on '{className}' in {Path.GetFileName(filePath)}.") };
            }
            if (action == "sync")
            {
                if (string.IsNullOrEmpty(className))
                    return new ToolResult<object>() { Success = false, Error = new ResultError(ToolErrorCode.InvalidArgument, "className is required when action=sync.") };

                var syncFileErr = GetFileNotInSolutionError(filePath);
                if (syncFileErr != null) return new ToolResult<object>() { Success = false, Error = new ResultError(ToolErrorCode.Exception, syncFileErr) };

                var syncResult = await _refactoringEngine.SyncInterfaceToImplementationAsync(filePath, className, interfaceName);
                if (string.IsNullOrEmpty(syncResult.UpdatedText))
                    return new ToolResult<object>() { Success = false, Error = new ResultError(ToolErrorCode.Exception,
                        $"SyncInterface sync: class '{className}' or interface '{interfaceName}' not found in '{Path.GetFileName(filePath)}'. " +
                        "Verify both names are spelled correctly (case-sensitive). Use locate_symbol to confirm the interface exists in the solution.") };

                var syncChanges = new Dictionary<FilePath, string> { [filePath] = syncResult.UpdatedText };
                var (syncId, syncError) = await ValidateAndStageAsync(syncChanges, $"Sync '{interfaceName}' to '{className}' implementation.", "SyncInterface/sync");
                if (syncError is not null)
                    return new ToolResult<object> { Success = false, Error = syncError };
                return new ToolResult<object>() { Success = true, Data = new PersistentWorkspaceManager.StagedChangeSummary(syncId!, [filePath], $"Syncs '{interfaceName}' to '{className}' implementation in {Path.GetFileName(filePath)}.") };
            }
            if (action == "verify")
            {
                var result = await _symbolNavigationEngine.VerifyInterfaceCompletenessAsync(interfaceName, projectName);
                return new ToolResult<object>() { Success = true, Data = result };
            }
            return new ToolResult<object>() { Success = false, Error = new ResultError(ToolErrorCode.Exception, $"Unknown action '{action}'. Valid values: implement, sync, verify.") };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SyncInterface ({Action}) failed for '{InterfaceName}'", action, interfaceName);
            return new ToolResult<object>() { Success = false, Error = new ResultError(ToolErrorCode.Exception, $"SyncInterface failed unexpectedly ({ex.GetType().Name}). Check that the solution is loaded and the file path is valid. Details: {ex.Message}") };
        }
    }

    [McpServerTool(Name = "Inline")]
    [Produces(DataTag.ChangeId)]
    [Description("Inlines a symbol by replacing all usages with its definition. kind: method (inline body at all call sites solution-wide — expression-body or single-return methods only), variable (inline local variable into usages), field (inline field value into usages), parameter (inline a constant parameter into method body — also supply methodName). targetName is the symbol name (parameterName when kind=parameter). Validates and stages the result — pass the returned changeId to staged_change(action=\"apply\") to commit.")]
    public async Task<ToolResult<object>> Inline(
        [Consumes(DataTag.SourceFilepath, required: true)] string filepath,
        [Consumes(DataTag.SymbolName, required: true)] string targetName,
        [Consumes(DataTag.SymbolKind, required: true)] string kind,
        [Consumes(DataTag.SymbolName)] string? methodName = null,
        Progress<string>? progress = null,
        CancellationToken? cancellationToken = default)
    {
        FilePath filePath = FilePath.FromWire(filepath, _workspaceManager.GetSolutionRoot());
        try
        {
            if (kind == "method")
            {
                try
                {
                    var methodChanges = await _refinementEngine.InlineMethodAsync(filePath, targetName);
                    if (methodChanges.Count == 0)
                        return new ToolResult<object> { Success = false, Error = new ResultError(ToolErrorCode.Exception, $"Inline/method: method '{targetName}' not found or has no inlineable call sites in '{filePath}'.") };

                    var (methodId, methodError) = await ValidateAndStageAsync(methodChanges, $"Inline method '{targetName}'.", "Inline/method");
                    if (methodError is not null)
                        return new ToolResult<object> { Success = false, Error = methodError };
                    return new ToolResult<object>() { Success = true, Data = new PersistentWorkspaceManager.StagedChangeSummary(methodId!, methodChanges.Keys.ToList(), $"Inlines '{targetName}' at all call sites across {methodChanges.Count} file(s).") };
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Inline/method unexpected exception for '{TargetName}' in '{FilePath}'", targetName, filePath);
                    return new ToolResult<object>() { Success = false, Error = new ResultError(ToolErrorCode.Exception, $"Inline method '{targetName}' in '{filePath}' failed unexpectedly ({ex.GetType().Name}). Check that the solution is loaded and the file path is valid. Details: {ex.Message}") };
                }
            }
            if (kind == "variable")
            {
                var updated = await _semanticRefactoringLibrary.InlineVariableAsync(filePath, targetName);
                if (string.IsNullOrEmpty(updated))
                    return new ToolResult<object> { Success = false, Error = new ResultError(ToolErrorCode.Exception, $"Inline/variable: variable '{targetName}' not found in '{filePath}'.") };

                var varChanges = new Dictionary<FilePath, string> { [filePath] = updated };
                var (varId, varError) = await ValidateAndStageAsync(varChanges, $"Inline variable '{targetName}'.", "Inline/variable");
                if (varError is not null)
                    return new ToolResult<object> { Success = false, Error = varError };
                return new ToolResult<object>() { Success = true, Data = new PersistentWorkspaceManager.StagedChangeSummary(varId!, [filePath], $"Inlines variable '{targetName}' into its usages in {Path.GetFileName(filePath)}.") };
            }
            if (kind == "field")
            {
                var fieldResult = await _granularRefactoringEngine.InlineFieldAsync(filePath, targetName);
                if (string.IsNullOrEmpty(fieldResult.UpdatedText))
                    return new ToolResult<object> { Success = false, Error = new ResultError(ToolErrorCode.Exception, $"Inline/field: field '{targetName}' not found in '{filePath}'.") };

                var fieldChanges = new Dictionary<FilePath, string> { [filePath] = fieldResult.UpdatedText };
                var (fieldId, fieldError) = await ValidateAndStageAsync(fieldChanges, $"Inline field '{targetName}'.", "Inline/field");
                if (fieldError is not null)
                    return new ToolResult<object> { Success = false, Error = fieldError };
                return new ToolResult<object>() { Success = true, Data = new PersistentWorkspaceManager.StagedChangeSummary(fieldId!, [filePath], $"Inlines field '{targetName}' into its usages in {Path.GetFileName(filePath)}.") };
            }
            if (kind == "parameter")
            {
                if (string.IsNullOrEmpty(methodName))
                    return new ToolResult<object>() { Success = false, Error = new ResultError(ToolErrorCode.InvalidArgument, "methodName is required when kind=parameter.") };

                var paramResult = await _granularRefactoringEngine.InlineParameterAsync(filePath, methodName, targetName);
                if (string.IsNullOrEmpty(paramResult.UpdatedText))
                    return new ToolResult<object> { Success = false, Error = new ResultError(ToolErrorCode.Exception, $"Inline/parameter: parameter '{targetName}' not found in method '{methodName}' in '{filePath}'.") };

                var paramChanges = new Dictionary<FilePath, string> { [filePath] = paramResult.UpdatedText };
                var (paramId, paramError) = await ValidateAndStageAsync(paramChanges, $"Inline parameter '{targetName}' in '{methodName}'.", "Inline/parameter");
                if (paramError is not null)
                    return new ToolResult<object> { Success = false, Error = paramError };
                return new ToolResult<object>() { Success = true, Data = new PersistentWorkspaceManager.StagedChangeSummary(paramId!, [filePath], $"Inlines parameter '{targetName}' into '{methodName}' body in {Path.GetFileName(filePath)}.") };
            }
            return new ToolResult<object>() { Success = false, Error = new ResultError(ToolErrorCode.Exception, $"Unknown kind '{kind}'. Valid values: method, variable, field, parameter.") };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Inline ({Kind}) failed for '{TargetName}' in '{FilePath}'", kind, targetName, filePath);
            return new ToolResult<object>() { Success = false, Error = new ResultError(ToolErrorCode.Exception, $"Inline failed unexpectedly ({ex.GetType().Name}). Check that the solution is loaded and the file path is valid. Details: {ex.Message}") };
        }
    }

    [McpServerTool(Name = "WrapRange")]
    [Produces(DataTag.ChangeId)]
    [Description("Wraps a 1-based line range. wrapper values: tryCatch (wrap in try/catch; name = exceptionType, default Exception; catchVariableName defaults to ex; catchBody optional), using (wrap in using statement; name = disposal variable name, required), region (wrap in #region; name = region label, required). autoStage=true → ChangeId for tryCatch/region; using returns content string directly.")]
    public async Task<ToolResult<object>> WrapRange(
        [Consumes(DataTag.SourceFilepath, required: true)] string filepath,
        [Consumes(DataTag.StartLine)] int startLine,
        [Consumes(DataTag.EndLine)] int endLine,
        [ExternalInputRequired(DataTag.Wrapper)] string wrapper,
        [ExternalInputRequired(DataTag.SymbolName)] string? name = null,
        [ExternalInputRequired(DataTag.SymbolName)] string catchVariableName = "ex",
        [ExternalInputRequired(DataTag.SourceCode)] string? catchBody = null,
        [ToolOptionAttribute(ToolOptionTag.AutoStage)] bool autoStage = true,
        Progress<string>? progress = null,
        CancellationToken? cancellationToken = default)
    {
        FilePath filePath = FilePath.FromWire(filepath, _workspaceManager.GetSolutionRoot());
        try
        {
            if (wrapper == "tryCatch")
            {
                var exceptionType = name ?? "Exception";
                var updated = await _refactoringEngine.WrapInTryCatchAsync(filePath, startLine, endLine, exceptionType, catchVariableName, catchBody);
                if (!autoStage)
                {
                    return new ToolResult<object>() { Success = true, Data = updated.ToJsonSummary() };
                }
                var changes = new Dictionary<FilePath, string> { [filePath] = updated.UpdatedText! };
                var (id, stageError) = await ValidateAndStageAsync(changes, $"Wrap lines {startLine}-{endLine} in try/catch.", "WrapRange/tryCatch");
                if (stageError is not null)
                    return new ToolResult<object> { Success = false, Error = stageError };
                var summary = new PersistentWorkspaceManager.StagedChangeSummary(id!, [filePath], $"Wrapped lines {startLine}-{endLine} in a try/{exceptionType} block in {Path.GetFileName(filePath)}.");
                return new ToolResult<object>() { Success = true, Data = summary };
            }
            if (wrapper == "using")
            {
                if (string.IsNullOrEmpty(name))
                {
                    return new ToolResult<object>() { Success = false, Error = new ResultError(ToolErrorCode.InvalidArgument, "name (disposalName) is required when wrapper=using.") };
                }
                var updated = await _semanticRefactoringLibrary.WrapInUsingAsync(filePath, startLine, endLine, name);
                if (!autoStage)
                {
                    return new ToolResult<object>() { Success = true, Data = updated.ToJsonSummary() };
                }
                var usingChanges = new Dictionary<FilePath, string> { [filePath] = updated.UpdatedText! };
                var (usingId, usingStageError) = await ValidateAndStageAsync(usingChanges, $"Wrap lines {startLine}-{endLine} in using ({name}).", "WrapRange/using");
                if (usingStageError is not null)
                    return new ToolResult<object> { Success = false, Error = usingStageError };
                var usingSummary = new PersistentWorkspaceManager.StagedChangeSummary(usingId!, [filePath], $"Wraps lines {startLine}-{endLine} in a using ({name}) block in {Path.GetFileName(filePath)}.");
                return new ToolResult<object>() { Success = true, Data = usingSummary };
            }
            if (wrapper == "region")
            {
                if (string.IsNullOrEmpty(name))
                {
                    return new ToolResult<object>() { Success = false, Error = new ResultError(ToolErrorCode.InvalidArgument, "name (regionName) is required when wrapper=region.") };
                }
                var updated = await _refactoringEngine.WrapInRegionAsync(filePath, startLine, endLine, name);
                if (!autoStage)
                {
                    return new ToolResult<object>() { Success = true, Data = updated.ToJsonSummary() };
                }
                var changes = new Dictionary<FilePath, string> { [filePath] = updated.UpdatedText! };
                var (id, stageError) = await ValidateAndStageAsync(changes, $"Wrap lines {startLine}-{endLine} in #region '{name}'.", "WrapRange/region");
                if (stageError is not null)
                    return new ToolResult<object> { Success = false, Error = stageError };
                var summary = new PersistentWorkspaceManager.StagedChangeSummary(id!, [filePath], $"Wraps lines {startLine}-{endLine} in #region '{name}' in {Path.GetFileName(filePath)}.");
                return new ToolResult<object>() { Success = true, Data = summary };
            }
            return new ToolResult<object>() { Success = false, Error = new ResultError(ToolErrorCode.Exception, $"Unknown wrapper '{wrapper}'. Valid values: tryCatch, using, region.") };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "WrapRange ({Wrapper}) failed in '{FilePath}'", wrapper, filePath);
            return new ToolResult<object>() { Success = false, Error = new ResultError(ToolErrorCode.Exception, $"WrapRange failed unexpectedly ({ex.GetType().Name}). Check that the solution is loaded and the file path is valid. Details: {ex.Message}") };
        }
    }

    [McpServerTool(Name = "MoveType")]
    [Produces(DataTag.ChangeId)]
    [Description("Moves a type to a new location. destination: ownFile (move to its own .cs file → ChangeId + content previews; autoStage=false → raw file dict) or outerScope (move nested type to containing namespace scope → updated file content).")]
    public async Task<ToolResult<object>> MoveType(
        [Consumes(DataTag.SourceFilepath, required: true)] string filepath,
        [Consumes(DataTag.SymbolName, required: true)] string typeName,
        string destination,
        bool autoStage = true,
        Progress<string>? progress = null,
        CancellationToken? cancellationToken = default)
    {
        FilePath filePath = FilePath.FromWire(filepath, _workspaceManager.GetSolutionRoot());
        try
        {
            if (destination == "ownFile")
            {
                var changes = await _refactoringEngine.MoveTypeToFileAsync(filePath, typeName);
                if (!autoStage)
                    return new ToolResult<object> { Success = true, Data = changes };

                var (id, stageError) = await ValidateAndStageAsync(changes, $"Move type '{typeName}' from '{Path.GetFileName(filePath)}'.", "MoveType/ownFile");
                if (stageError is not null)
                    return new ToolResult<object> { Success = false, Error = stageError };
                return new ToolResult<object>
                {
                    Success = true,
                    Data = new
                    {
                        ChangeId = id,
                        Description = $"Moves '{typeName}' to its own file. Call staged_change(action=\"apply\", changeId=\"{id}\") to apply.",
                        AffectedFiles = changes.Keys.ToList(),
                        ContentPreviews = changes.ToDictionary(
                            kvp => Path.GetFileName(kvp.Key),
                            kvp => PreviewFileContent(kvp.Value))
                    }
                };
            }
            if (destination == "outerScope")
            {
                var outerResult = await _granularRefactoringEngine.MoveTypeToOuterScopeAsync(filePath, typeName);
                if (!autoStage)
                    return new ToolResult<object> { Success = true, Data = outerResult };

                if (string.IsNullOrEmpty(outerResult.UpdatedText))
                    return new ToolResult<object> { Success = false, Error = new ResultError(ToolErrorCode.Exception, $"MoveType/outerScope: nested type '{typeName}' not found in '{filePath}'.") };

                var outerChanges = new Dictionary<FilePath, string> { [filePath] = outerResult.UpdatedText };
                var (outerId, outerError) = await ValidateAndStageAsync(outerChanges, $"Move type '{typeName}' to outer scope.", "MoveType/outerScope");
                if (outerError is not null)
                    return new ToolResult<object> { Success = false, Error = outerError };
                return new ToolResult<object> { Success = true, Data = new PersistentWorkspaceManager.StagedChangeSummary(outerId!, [filePath], $"Moves '{typeName}' to outer namespace scope in {Path.GetFileName(filePath)}.") };
            }
            return new ToolResult<object>
            {
                Success = false,
                Error = new ResultError(ToolErrorCode.Exception, $"Unknown destination '{destination}'. Valid values: ownFile, outerScope.")
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "MoveType ({Destination}) failed for '{TypeName}' in '{FilePath}'", destination, typeName, filePath);
            return new ToolResult<object>
            {
                Success = false,
                Error = new ResultError(ToolErrorCode.Exception, $"MoveType failed unexpectedly ({ex.GetType().Name}). Check that the solution is loaded and the file path is valid. Details: {ex.Message}")
            };
        }
    }
}
