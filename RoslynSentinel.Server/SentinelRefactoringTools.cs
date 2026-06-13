using System.ComponentModel;

using Microsoft.CodeAnalysis;
using Microsoft.Extensions.Logging;

using ModelContextProtocol.Server;

namespace RoslynSentinel.Server;

[McpServerToolType]
public class SentinelRefactoringTools
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
    private readonly ModernizationEngine _modernizationEngine;
    private readonly OutParamRefactoringEngine _outParamRefactoringEngine;
    private readonly MsToolAugmentEngine _augmentEngine;
    private readonly CodeGenerationEngine _codeGenerationEngine;
    private readonly SymbolNavigationEngine _symbolNavigationEngine;
    private readonly PersistentWorkspaceManager _workspaceManager;
    private readonly SentinelConfiguration _config;
    private readonly ILogger<SentinelRefactoringTools> _logger;

    public SentinelRefactoringTools(
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
        SentinelConfiguration config,
        ILogger<SentinelRefactoringTools> logger)
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
        _modernizationEngine = modernizationEngine;
        _outParamRefactoringEngine = outParamRefactoringEngine;
        _augmentEngine = augmentEngine;
        _codeGenerationEngine = codeGenerationEngine;
        _symbolNavigationEngine = symbolNavigationEngine;
        _workspaceManager = workspaceManager;
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

    [McpServerTool(Name = "SyncTypeAndFilename")]
    [Produces(DataTag.ResultOnly)]
    [Description("Synchronizes the filename to match the primary type declared in the file.")]
    public async Task<string> SyncTypeAndFilename(
        [Consumes(DataTag.SourceFilepath, required: true)] string filepath)
    {
        FilePath filePath = FilePath.FromWire(filepath, _workspaceManager.GetSolutionRoot());

        try
        {
            var result = await _structuralRefinementEngine.SyncTypeAndFilenameAsync(filePath);
            return result.ToJsonSummary();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SyncTypeAndFilename unexpected exception for '{FilePath}'", filePath);
            return $"SyncTypeAndFilename for '{filePath}' failed: {ex.GetType().Name}: {ex.Message}";
        }
    }

    [McpServerTool(Name = "ChangeSignature")]
    [Produces(DataTag.ResultOnly)]
    [Description("Reorders method parameters and updates all call sites across the solution. newParameterOrder: zero-based index array specifying the new parameter order. autoStage=true → ChangeId.")]
    public async Task<ToolResult<object>> ChangeSignature(
        [Consumes(DataTag.SourceFilepath, required: true)] string filepath,
        [Consumes(DataTag.SymbolName, required: true)] string methodName,
        [ExternalInputRequired(DataTag.Order, required: true)] int[] newParameterOrder,
        [ToolOption(ToolOptionTag.AutoStage, required: false)] bool autoStage = true)
    {
        FilePath filePath = FilePath.FromWire(filepath, _workspaceManager.GetSolutionRoot());

        try
        {
            var changes = await _refactoringEngine.ChangeSignatureAsync(filePath, methodName, newParameterOrder);
            if (autoStage)
            {
                var id = _workspaceManager.StageChanges(changes, $"Change signature of method '{methodName}' in '{Path.GetFileName(filePath)}'.");
                return new ToolResult<object>() { Success = true, Data = new { Changes = changes, StagingId = id } };
            }

            return new ToolResult<object>() { Success = true, Data = new { Changes = changes } };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ChangeSignature failed for '{MethodName}' in '{FilePath}'", methodName, filePath);
            return new ToolResult<object>() { Success = false, Error = new ResultError("", $"ChangeSignature failed for '{methodName}' in '{filePath}': {ex.GetType().Name}: {ex.Message}") };
        }
    }

    [McpServerTool(Name = "RenameSymbol")]
    [Description("Renames a symbol and all its references across the solution. " +
                 "Requires a symbol handle from locate_symbol (sessionId, projectName, docCommentId).")]
    public async Task<string> RenameSymbol(
        [Description(ToolParams.SessionId)] string sessionId,
        [Description(ToolParams.ProjectName)] string projectName,
        [Description(ToolParams.DocCommentId)] string docCommentId,
        [Description("New name for the symbol. Must be a valid C# identifier.")] string newName,
        CancellationToken ct = default)
    {
        SymbolResolution resolution = await _workspaceManager.ResolveFromWireAsync(
            sessionId, projectName, docCommentId, ct);
        if (!resolution.Resolved)
        {
            return resolution.Error!.ToToolResponse();
        }

        RenameSymbolResult result = await _refactoringEngine.RenameSymbolAsync(
            resolution.Handle, resolution.Symbol!, newName, ct);
        return result.ToToolResponse();
    }

    [McpServerTool(Name = "GenerateMapping")]
    [Produces(DataTag.ResultOnly)]
    [Description("Generates a mapping method between fromType and toType.")]
    public async Task<ToolResult<object>> GenerateMapping(
        [Consumes(DataTag.SourceFilepath, required: true)] string filepath,
        [ExternalInputRequired(DataTag.DataType, required: true)] string fromType,
        [ExternalInputRequired(DataTag.DataType)] string toType)
    {
        FilePath filePath = FilePath.FromWire(filepath, _workspaceManager.GetSolutionRoot());
        try
        {
            var result = await _mappingEngine.GenerateMappingAsync(filePath, fromType, toType);
            return new ToolResult<object>() { Success = true, Data = result };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GenerateMapping failed for '{FromType}' to '{ToType}' in '{FilePath}'", fromType, toType, filePath);
            return new ToolResult<object>() { Success = false, Error = new ResultError("", $"GenerateMapping failed for '{fromType}' to '{toType}' in '{filePath}': {ex.GetType().Name}: {ex.Message}") };
        }
    }

    [McpServerTool(Name = "ConvertAnonymousToNamed")]
    [Produces(DataTag.ResultOnly)]
    [Description("Converts the first anonymous object creation expression in the file to a formal named class declaration.")]
    public async Task<ToolResult<object>> ConvertAnonymousToNamed(
        [ExternalInputRequired(DataTag.SourceFilepath, required: true)] string filepath,
        [ExternalInputRequired(DataTag.ClassName, required: true)] string newClassName)
    {
        FilePath filePath = FilePath.FromWire(filepath, _workspaceManager.GetSolutionRoot());
        try
        {
            var result = await _advancedTypeEngine.ConvertAnonymousToNamedAsync(filePath, newClassName);
            return new ToolResult<object>() { Success = true, Data = result };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ConvertAnonymousToNamed failed for '{NewClassName}' in '{FilePath}'", newClassName, filePath);
            return new ToolResult<object>() { Success = false, Error = new ResultError("", $"ConvertAnonymousToNamed failed for '{newClassName}' in '{filePath}': {ex.GetType().Name}: {ex.Message}") };
        }
    }

    [McpServerTool(Name = "InlineClass")]
    [Produces(DataTag.Report)]
    [Description("Merges all members of a source class into a target class and removes the source class declaration. Works within the same file or across files. Updates all type references (variable declarations, constructor calls, casts, typeof, etc.) to the inlined class name throughout the solution. Returns a filePath → updatedContent dictionary for every affected file.")]
    public async Task<ToolResult<object>> InlineClass(
        [Consumes(DataTag.SourceFilepath, required: true)] string rawSourceFilePath,
        [Consumes(DataTag.SourceFilepath, required: true)] string rawTargetFilePath,
        [Consumes(DataTag.SymbolName, required: true)] string className)
    {
        FilePath sourceFilePath = FilePath.FromWire(rawSourceFilePath, _workspaceManager.GetSolutionRoot());
        FilePath targetFilePath = FilePath.FromWire(rawTargetFilePath, _workspaceManager.GetSolutionRoot());
        try
        {
            var result = await _advancedStructuralEngine.InlineClassAsync(sourceFilePath, targetFilePath, className);
            return new ToolResult<object>() { Success = true, Data = result };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "InlineClass failed for '{ClassName}'", className);
            return new ToolResult<object>() { Success = false, Error = new ResultError("", $"InlineClass failed for '{className}' in '{sourceFilePath}' to '{targetFilePath}': {ex.GetType().Name}: {ex.Message}") };
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
        bool autoStage = true)
    {
        try
        {
            if (scope == "file")
            {
                if (scopeName is null)
                {
                    return new ToolResult<object>() { Success = false, Error = new ResultError("", "scopeName (file path) is required for scope=file.") };
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
                    return new ToolResult<object>() { Success = false, Error = new ResultError("", "scopeName (project name) is required for scope=project.") };
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
            return new ToolResult<object>() { Success = false, Error = new ResultError("", $"Unknown scope '{scope}'. Valid: file, project, solution.") };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "MoveAllTypesToFiles ({Scope}) failed", scope);
            return new ToolResult<object>() { Success = false, Error = new ResultError("", $"MoveAllTypesToFiles failed: {ex.GetType().Name}: {ex.Message}") };
        }
    }

    private Task<ToolResult<object>> MoveAllTypesToFilesCore(
        Dictionary<FilePath, string> changes,
        bool autoStage,
        string description,
        bool previewFiles)
    {
        if (!autoStage)
        {
            return Task.FromResult(new ToolResult<object>() { Success = true, Data = changes });
        }

        if (changes.Count == 0)
        {
            return Task.FromResult(new ToolResult<object>() { Success = true, Data = "No secondary types found to move." });
        }

        var id = _workspaceManager.StageChanges(changes, description);

        if (previewFiles)
        {
            return Task.FromResult(new ToolResult<object>()
            {
                Success = true,
                Data = new
                {
                    ChangeId = id,
                    Description = $"{description}. Call ApplyStagedChanges(\"{id}\") to apply.",
                    AffectedFiles = changes.Keys.Select(kvp => Path.GetFileName(kvp)).ToList(),
                    ContentPreviews = changes.ToDictionary(
                        kvp => Path.GetFileName(kvp.Key)!,

                    kvp => PreviewFileContent(kvp.Value))
                }
            });
        }

        return Task.FromResult(new ToolResult<object>()
        {
            Success = true,
            Data = new PersistentWorkspaceManager.StagedChangeSummary(id, changes.Keys.ToList(), description)
        });
    }

    [McpServerTool(Name = "ReplaceMember")]
    [Produces(DataTag.ResultOnly)]
    [Description("Surgically replaces a specific member (method, property, class) in a file by name with new source code.")]
    public async Task<string> ReplaceMember(
        [Consumes(DataTag.SourceFilepath, required: true)] string filepath,
        [Consumes(DataTag.SymbolName, required: true)] string memberName,
        [Consumes(DataTag.SourceCode, required: true)] string newSource)
    {
        FilePath filePath = FilePath.FromWire(filepath, _workspaceManager.GetSolutionRoot());
        try
        {
            var result = await _refactoringEngine.ReplaceMemberAsync(filePath, memberName, newSource);
            return result.ToJsonSummary();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ReplaceMember unexpected exception for '{MemberName}' in '{FilePath}'", memberName, filePath);
            return $"ReplaceMember for '{memberName}' in '{filePath}' failed: {ex.GetType().Name}: {ex.Message}";
        }
    }

    [McpServerTool(Name = "RemoveMember")]
    [Produces(DataTag.ResultOnly)]
    [Description("Removes a specific member from a class or interface by name.")]
    public async Task<string> RemoveMember(
        [Consumes(DataTag.SourceFilepath, required: true)] string filepath,
        [Consumes(DataTag.SymbolName, required: true)] string memberName)
    {
        FilePath filePath = FilePath.FromWire(filepath, _workspaceManager.GetSolutionRoot());
        try
        {
            var result = await _refactoringEngine.RemoveMemberAsync(filePath, memberName);
            return result.ToJsonSummary();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "RemoveMember failed for '{MemberName}' in '{FilePath}'", memberName, filePath);
            return $"RemoveMember failed: {ex.GetType().Name}: {ex.Message}";
        }
    }

    [McpServerTool(Name = "AddUsingDirective")]
    [Produces(DataTag.ResultOnly)]
    [Description("""
        Adds a using directive to a file if not already present.

        Pass just the namespace name (e.g. "System.Linq", "Microsoft.Extensions.DependencyInjection").
        For static usings, prefix with "static " (e.g. "static System.Math").
        If the directive already exists, the file is returned unchanged.
        Use autoStage=true (default) to get a ChangeId for ApplyStagedChanges.
        """)]
    public async Task<ToolResult<object>> AddUsingDirective(
        [Consumes(DataTag.SourceFilepath, required: true)] string filepath,
        [Consumes(DataTag.SymbolName, required: true)] string namespaceName,
        [ToolOption(ToolOptionTag.AutoStage, required: false)] bool autoStage = true)
    {
        FilePath filePath = FilePath.FromWire(filepath, _workspaceManager.GetSolutionRoot());
        try
        {
            var updated = await _refactoringEngine.AddUsingDirectiveAsync(filePath, namespaceName);
            if (!autoStage)
            {
                return new ToolResult<object>() { Success = true, Data = updated.ToJsonSummary() };
            }

            var changes = new Dictionary<FilePath, string> { [filePath] = updated.FilePath };
            var id = _workspaceManager.StageChanges(changes, $"Add using {namespaceName}.");
            return new ToolResult<object>() { Success = true, Data = new PersistentWorkspaceManager.StagedChangeSummary(id, [filePath], $"Adds 'using {namespaceName};' to {Path.GetFileName(filePath)}.") };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AddUsingDirective failed for '{Namespace}' in '{FilePath}'", namespaceName, filePath);
            return new ToolResult<object>() { Success = false, Error = new ResultError("", $"AddUsingDirective failed: {ex.GetType().Name}: {ex.Message}") };
        }
    }

    [McpServerTool(Name = "AddEnumValue")]
    [Produces(DataTag.ResultOnly)]
    [Description("""
        Adds a new value to an existing enum. `explicitValue=99` → `Archived = 99`. If the enum is not found, the file is returned unchanged. `autoStage=true` → ChangeId for `staged_change`.
        """)]
    public async Task<ToolResult<object>> AddEnumValue(
        [Consumes(DataTag.SourceFilepath, required: true)] string filepath,
        [Consumes(DataTag.SymbolName, required: true)] string enumName,
        [Consumes(DataTag.SymbolName, required: true)] string valueName,
        [Consumes(DataTag.DataType, required: false)] int? explicitValue = null,
        [ToolOption(ToolOptionTag.AutoStage, required: false)] bool autoStage = true)
    {
        FilePath filePath = FilePath.FromWire(filepath, _workspaceManager.GetSolutionRoot());
        try
        {
            var updated = await _refactoringEngine.AddEnumValueAsync(filePath, enumName, valueName, explicitValue);
            if (!autoStage)
            {
                return new ToolResult<object>() { Success = true, Data = updated.ToJsonSummary() };
            }

            var changes = new Dictionary<FilePath, string> { [filePath] = updated.FilePath };
            var id = _workspaceManager.StageChanges(changes, $"Add enum value '{valueName}' to '{enumName}'.");
            return new ToolResult<object>() { Success = true, Data = new PersistentWorkspaceManager.StagedChangeSummary(id, [filePath], $"Adds '{valueName}' to enum '{enumName}' in {Path.GetFileName(filePath)}.") };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AddEnumValue failed for '{EnumName}' in '{FilePath}'", enumName, filePath);
            return new ToolResult<object>() { Success = false, Error = new ResultError("", $"AddEnumValue failed: {ex.GetType().Name}: {ex.Message}") };
        }
    }

    [McpServerTool(Name = "InvertAssignments")]
    [Produces(DataTag.ResultOnly)]
    [Description("Swaps left and right sides of all assignment statements within a 1-based line range.")]
    public async Task<ToolResult<object>> InvertAssignments(
        [Consumes(DataTag.SourceFilepath, required: true)] string filepath,
        [Consumes(DataTag.StartLine)] int startLine,
        [Consumes(DataTag.EndLine)] int endLine)
    {
        FilePath filePath = FilePath.FromWire(filepath, _workspaceManager.GetSolutionRoot());
        try
        {
            var result = await _mappingEngine.InvertAssignmentsAsync(filePath, startLine, endLine);
            return new ToolResult<object>() { Success = true, Data = result };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "InvertAssignments failed in '{FilePath}'", filePath);
            return new ToolResult<object>() { Success = false, Error = new ResultError("", $"InvertAssignments failed: {ex.GetType().Name}: {ex.Message}") };
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
        [ToolOption(ToolOptionTag.AutoStage, required: false)] bool autoStage = true)
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
                return new ToolResult<object>() { Success = false, Error = new ResultError("", $"Member '{memberName}' not found or no accessible base class available.") };
            }

            var id = _workspaceManager.StageChanges(changes, $"Pull up '{memberName}' from '{className}' to base class.");
            return new ToolResult<object>() { Success = true, Data = new PersistentWorkspaceManager.StagedChangeSummary(id, changes.Keys.ToList(), $"Pulls '{memberName}' from '{className}' up to its base class.") };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "PullUpMember failed for '{MemberName}' in '{ClassName}'", memberName, className);
            return new ToolResult<object>() { Success = false, Error = new ResultError("", $"PullUpMember failed: {ex.GetType().Name}: {ex.Message}") };
        }
    }

    [McpServerTool(Name = "ChangeAccessibility")]
    [Produces(DataTag.ResultOnly)]
    [Description("""
        Changes the accessibility modifier of a type or member.

        targetName is the class/method/property/field name to modify.
        accessibility must be one of: "public", "private", "internal", "protected",
        "protected internal", or "private protected".
        Use autoStage=true (default) to get a ChangeId for ApplyStagedChanges.
        """)]
    public async Task<ToolResult<object>> ChangeAccessibility(
        [Consumes(DataTag.SourceFilepath, required: true)] string filepath,
        [Consumes(DataTag.SymbolName, required: true)] string targetName,
        [ExternalInputRequired(DataTag.Accessibility, required: true)] string accessibility,
        [ToolOption(ToolOptionTag.AutoStage, required: false)] bool autoStage = true)
    {
        FilePath filePath = FilePath.FromWire(filepath, _workspaceManager.GetSolutionRoot());
        try
        {
            var updated = await _refactoringEngine.ChangeAccessibilityAsync(filePath, targetName, accessibility);
            if (!autoStage)
            {
                return new ToolResult<object>() { Success = true, Data = updated.ToJsonSummary() };
            }

            var changes = new Dictionary<FilePath, string> { [filePath] = updated.FilePath };
            var id = _workspaceManager.StageChanges(changes, $"Change accessibility of '{targetName}' to '{accessibility}'.");
            return new ToolResult<object>() { Success = true, Data = new PersistentWorkspaceManager.StagedChangeSummary(id, [filePath], $"Changes accessibility of '{targetName}' to '{accessibility}' in {Path.GetFileName(filePath)}.") };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ChangeAccessibility failed for '{TargetName}' in '{FilePath}'", targetName, filePath);
            return new ToolResult<object>() { Success = false, Error = new ResultError("", $"ChangeAccessibility failed: {ex.GetType().Name}: {ex.Message}") };
        }
    }

    [McpServerTool(Name = "AddSummaryComment")]
    [Description("""
        Adds or replaces a /// <summary>...</summary> XML doc comment on a type or member.

        targetName is the class/method/property name to document.
        summaryText is the text content of the summary (single line).
        If a summary already exists it will be replaced.
        Use autoStage=true (default) to get a ChangeId for ApplyStagedChanges.
        """)]
    public async Task<ToolResult<object>> AddSummaryComment(
        [Consumes(DataTag.SourceFilepath, required: true)] string filepath,
        [Consumes(DataTag.SymbolName, required: true)] string targetName,
        string summaryText,
        bool autoStage = true)
    {
        FilePath filePath = FilePath.FromWire(filepath, _workspaceManager.GetSolutionRoot());
        try
        {
            var updated = await _refactoringEngine.AddSummaryCommentAsync(filePath, targetName, summaryText);
            if (!autoStage)
            {
                return new ToolResult<object>() { Success = true, Data = updated.ToJsonSummary() };
            }

            var changes = new Dictionary<FilePath, string> { [filePath] = updated.FilePath };
            var id = _workspaceManager.StageChanges(changes, $"Add summary comment to '{targetName}'.");
            return new ToolResult<object>() { Success = true, Data = new PersistentWorkspaceManager.StagedChangeSummary(id, [filePath], $"Added XML summary comment to '{targetName}' in {Path.GetFileName(filePath)}.") };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AddSummaryComment failed for '{TargetName}' in '{FilePath}'", targetName, filePath);
            return new ToolResult<object>() { Success = false, Error = new ResultError("", $"AddSummaryComment failed: {ex.GetType().Name}: {ex.Message}") };
        }
    }

    [McpServerTool(Name = "AddConstructorParameter")]
    [Description("""
        Adds a DI constructor parameter in one step: private readonly field + parameter + body assignment. `fieldName` overrides the derived field name (defaults to `_camelCase` of `paramName`). Creates a constructor if none exists; converts expression-bodied constructors to block bodies. `autoStage=true` → ChangeId for `staged_change`
        """)]
    public async Task<ToolResult<object>> AddConstructorParameter([Consumes(DataTag.SourceFilepath, required: true)] string filepath,
        [Consumes(DataTag.ClassName, required: true)] string className,
        [Consumes(DataTag.SymbolName, required: true)] string paramName,
        [Consumes(DataTag.DataType, required: true)] string paramType,
        [Consumes(DataTag.SymbolName, required: false)] string? fieldName = null,
        [ToolOption(ToolOptionTag.AutoStage, required: false)] bool autoStage = true)
    {
        FilePath filePath = FilePath.FromWire(filepath, _workspaceManager.GetSolutionRoot());
        try
        {
            var updated = await _refactoringEngine.AddConstructorParameterAsync(filePath, className, paramName, paramType, fieldName);
            if (!autoStage)
            {
                return new ToolResult<object>() { Success = true, Data = updated.ToJsonSummary() };
            }

            var changes = new Dictionary<FilePath, string> { [filePath] = updated.FilePath };
            var id = _workspaceManager.StageChanges(changes, $"Add constructor parameter '{paramName}' to '{className}'.");
            return new ToolResult<object>() { Success = true, Data = new PersistentWorkspaceManager.StagedChangeSummary(id, [filePath], $"Added '{paramType} {paramName}' DI parameter to '{className}' in {Path.GetFileName(filePath)}.") };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AddConstructorParameter failed for '{ClassName}' in '{FilePath}'", className, filePath);
            return new ToolResult<object>() { Success = false, Error = new ResultError("", $"AddConstructorParameter failed: {ex.GetType().Name}: {ex.Message}") };
        }
    }

    [McpServerTool(Name = "IntroduceParameterObject")]
    [Produces(DataTag.ResultOnly)]
    [Description("Encapsulates method parameters into a new C# 12 record type. Groups all non-CancellationToken parameters (or only parameterNames if specified) into public record {NewTypeName}(...). Rewrites parameter references in the method body to request.PropertyName. Appends the record to end of file. Adds a TODO comment to update call sites — call sites must be updated manually.")]
    public async Task<ToolResult<object>> IntroduceParameterObject(
        [Consumes(DataTag.SourceFilepath, required: true)] string filepath,
        [Consumes(DataTag.SymbolName, required: true)] string methodName,
        string? newTypeName = null,
        string[]? parameterNames = null)
    {
        FilePath filePath = FilePath.FromWire(filepath, _workspaceManager.GetSolutionRoot());
        try
        {
            var result = await _granularRefactoringEngine.IntroduceParameterObjectAsync(filePath, methodName, newTypeName, parameterNames);
            if (string.IsNullOrEmpty(result.UpdatedText))
            {
                return new ToolResult<object>() { Success = false, Error = new ResultError("", $"IntroduceParameterObject failed for '{methodName}' in '{filePath}': file not found in workspace or method not found. Ensure the solution is loaded.") };
            }

            return new ToolResult<object>() { Success = true, Data = result.ToJsonSummary() };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "IntroduceParameterObject failed for '{MethodName}' in '{FilePath}'", methodName, filePath);
            return new ToolResult<object>() { Success = false, Error = new ResultError("", $"IntroduceParameterObject failed: {ex.GetType().Name}: {ex.Message}") };
        }
    }

    [McpServerTool(Name = "ExtractLocalVariable")]
    [Produces(DataTag.ResultOnly)]
    [Description("""
        Extracts an inline expression into a local variable declaration.
        Converts patterns like:
        - 'return x + y;' → 'var sum = x + y; return sum;'
        - 'var result = getValue();' → Extracts getValue() to a variable

        contextSnippet: a verbatim substring containing or surrounding the expression to extract.
        variableName: the name for the new variable.
        Provide lineBefore and/or lineAfter when the snippet could match multiple locations.
        Returns the updated file content with the expression extracted to a variable.
        """)]
    public async Task<ToolResult<object>> ExtractLocalVariable(
        [Consumes(DataTag.SourceFilepath, required: true)] string filepath,
        [Consumes(DataTag.ContextSnippet, required: true)] string contextSnippet,
        [Consumes(DataTag.SymbolName)] string variableName,
        [Consumes(DataTag.LineBefore)] string? lineBefore = null,
        [Consumes(DataTag.LineAfter)] string? lineAfter = null)
    {
        FilePath filePath = FilePath.FromWire(filepath, _workspaceManager.GetSolutionRoot());
        try
        {
            var result = await _refactoringEngine.ExtractLocalVariableAsync(filePath, contextSnippet, variableName, lineBefore, lineAfter);
            if (string.IsNullOrEmpty(result.UpdatedText))
            {
                return new ToolResult<object>() { Success = false, Error = new ResultError("", $"ExtractLocalVariable failed for variable '{variableName}' in '{filePath}': file not found in workspace or context snippet did not match any expression. Ensure the solution is loaded.") };
            }

            return new ToolResult<object>() { Success = true, Data = result.ToJsonSummary() };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ExtractLocalVariable failed for '{VariableName}' in '{FilePath}'", variableName, filePath);
            return new ToolResult<object>() { Success = false, Error = new ResultError("", $"ExtractLocalVariable failed: {ex.GetType().Name}: {ex.Message}") };
        }
    }

    [McpServerTool(Name = "ModifyAttribute")]
    [Produces(DataTag.ResultOnly)]
    [Description("Adds or removes an attribute on a type or member. action: add or remove. attribute/attributeSource/attributeName accept the attribute with or without brackets or Attribute suffix (e.g. \"[ApiController]\", \"Required\", \"Obsolete\"). autoStage=true → ChangeId.")]
    public async Task<ToolResult<object>> ModifyAttribute(
        [Consumes(DataTag.SourceFilepath, required: true)] string filepath,
        [Consumes(DataTag.SymbolName, required: true)] string targetName,
        [ExternalInputRequired(DataTag.AttributeName, required: true)] string attribute,
        [ExternalInputRequired(DataTag.Action, required: true)] string action,
        [ToolOption(ToolOptionTag.AutoStage, required: false)] bool autoStage = true)
    {
        FilePath filePath = FilePath.FromWire(filepath, _workspaceManager.GetSolutionRoot());
        try
        {
            DocumentEditResult updated;
            if (action == "add")
            {
                updated = await _refactoringEngine.AddAttributeAsync(filePath, targetName, attribute);
            }
            else if (action == "remove")
            {
                updated = await _refactoringEngine.RemoveAttributeAsync(filePath, targetName, attribute);
            }
            else
            {
                return new ToolResult<object>() { Success = false, Error = new ResultError("", $"Unknown action '{action}'. Valid values: add, remove.") };
            }
            if (!autoStage)
            {
                return new ToolResult<object>() { Success = true, Data = updated.ToJsonSummary() };
            }
            var changes = new Dictionary<FilePath, string> { [filePath] = updated.FilePath };
            var id = _workspaceManager.StageChanges(changes, $"{action} attribute '{attribute}' on '{targetName}'.");
            var summary = new PersistentWorkspaceManager.StagedChangeSummary(id, [filePath], $"{(action == "add" ? "Adds" : "Removes")} '{attribute}' attribute on '{targetName}' in {Path.GetFileName(filePath)}.");
            return new ToolResult<object>() { Success = true, Data = summary };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ModifyAttribute failed for '{TargetName}' in '{FilePath}'", targetName, filePath);
            return new ToolResult<object>() { Success = false, Error = new ResultError("", $"ModifyAttribute failed: {ex.GetType().Name}: {ex.Message}") };
        }
    }

    [McpServerTool(Name = "ModifyModifier")]
    [Produces(DataTag.ResultOnly)]
    [Description("Adds or removes a modifier keyword on a type or member. modifier: virtual, abstract, sealed, static, readonly, override, partial, async, new, extern, unsafe, volatile. action: add or remove. autoStage=true → ChangeId.")]
    public async Task<ToolResult<object>> ModifyModifier(
        [Consumes(DataTag.SourceFilepath, required: true)] string filepath,
        [Consumes(DataTag.SymbolName, required: true)] string targetName,
        [ExternalInputRequired(DataTag.Modifier, required: true)] string modifier,
        [Consumes(DataTag.Action, required: true)] string action,
        [ToolOption(ToolOptionTag.AutoStage, required: false)] bool autoStage = true)
    {
        FilePath filePath = FilePath.FromWire(filepath, _workspaceManager.GetSolutionRoot());
        try
        {
            DocumentEditResult updated;
            if (action == "add")
            {
                updated = await _refactoringEngine.AddModifierAsync(filePath, targetName, modifier);
            }
            else if (action == "remove")
            {
                updated = await _refactoringEngine.RemoveModifierAsync(filePath, targetName, modifier);
            }
            else
            {
                return new ToolResult<object>() { Success = false, Error = new ResultError("", $"Unknown action '{action}'. Valid values: add, remove.") };
            }
            if (!autoStage)
            {
                return new ToolResult<object>() { Success = true, Data = updated.ToJsonSummary() };
            }
            var changes = new Dictionary<FilePath, string> { [filePath] = updated.FilePath };
            var id = _workspaceManager.StageChanges(changes, $"{action} '{modifier}' modifier on '{targetName}'.");
            var summary = new PersistentWorkspaceManager.StagedChangeSummary(id, [filePath], $"{(action == "add" ? "Adds" : "Removes")} '{modifier}' modifier on '{targetName}' in {Path.GetFileName(filePath)}.");
            return new ToolResult<object>() { Success = true, Data = summary };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ModifyModifier failed for '{TargetName}' in '{FilePath}'", targetName, filePath);
            return new ToolResult<object>() { Success = false, Error = new ResultError("", $"ModifyModifier failed: {ex.GetType().Name}: {ex.Message}") };
        }
    }

    [McpServerTool(Name = "ModifyBaseType")]
    [Produces(DataTag.ChangeId)]
    [Description("Adds or removes a base type or interface from a type declaration. action: add or remove. autoStage=true → ChangeId.")]
    public async Task<ToolResult<object>> ModifyBaseType(
        [Consumes(DataTag.SourceFilepath, required: true)] string filepath,
        [Consumes(DataTag.SymbolName, required: true)] string typeName,
        string baseTypeName,
        string action,
        bool autoStage = true)
    {
        FilePath filePath = FilePath.FromWire(filepath, _workspaceManager.GetSolutionRoot());
        try
        {
            DocumentEditResult updated;
            if (action == "add")
            {
                updated = await _refactoringEngine.AddBaseTypeAsync(filePath, typeName, baseTypeName);
            }
            else if (action == "remove")
            {
                updated = await _refactoringEngine.RemoveBaseTypeAsync(filePath, typeName, baseTypeName);
            }
            else
            {
                return new ToolResult<object>() { Success = false, Error = new ResultError("", $"Unknown action '{action}'. Valid values: add, remove.") };
            }
            if (!autoStage)
            {
                return new ToolResult<object>() { Success = true, Data = updated.ToJsonSummary() };
            }
            var changes = new Dictionary<FilePath, string> { [filePath] = updated.FilePath };
            var id = _workspaceManager.StageChanges(changes, $"{action} base type '{baseTypeName}' on '{typeName}'.");
            var summary = new PersistentWorkspaceManager.StagedChangeSummary(id, [filePath], $"{(action == "add" ? "Adds" : "Removes")} '{baseTypeName}' on '{typeName}' in {Path.GetFileName(filePath)}.");
            return new ToolResult<object>() { Success = true, Data = summary };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ModifyBaseType failed for '{TypeName}' in '{FilePath}'", typeName, filePath);
            return new ToolResult<object>() { Success = false, Error = new ResultError("", $"ModifyBaseType failed: {ex.GetType().Name}: {ex.Message}") };
        }
    }

    [McpServerTool(Name = "Introduce")]
    [Produces(DataTag.ResultOnly)]
    [Description("Introduces a named symbol from an expression. as values: localVariable, field (private readonly), parameter (single-file), constant (→ MsAugmentResult). contextSnippet: verbatim substring identifying the expression. lineBefore/lineAfter disambiguate.")]
    public async Task<ToolResult<object>> Introduce(
        [Consumes(DataTag.SourceFilepath, required: true)] string filepath,
        [Consumes(DataTag.ContextSnippet, required: true)] string contextSnippet,
        [ExternalInputRequired(DataTag.SymbolName)] string newName,
        [ExternalInputRequired(DataTag.SymbolKind)] string @as,
        [Consumes(DataTag.LineBefore)] string? lineBefore = null,
        [Consumes(DataTag.LineAfter)] string? lineAfter = null)
    {
        FilePath filePath = FilePath.FromWire(filepath, _workspaceManager.GetSolutionRoot());
        try
        {
            if (@as == "localVariable")
            {
                return new ToolResult<object>() { Success = true, Data = await _granularRefactoringEngine.IntroduceVariableAsync(filePath, contextSnippet, newName, lineBefore, lineAfter) };
            }
            if (@as == "field")
            {
                return new ToolResult<object>() { Success = true, Data = await _granularRefactoringEngine.IntroduceFieldAsync(filePath, contextSnippet, newName, lineBefore, lineAfter) };
            }
            if (@as == "parameter")
            {
                return new ToolResult<object>() { Success = true, Data = await _granularRefactoringEngine.IntroduceParameterAsync(filePath, contextSnippet, newName, lineBefore, lineAfter) };
            }
            if (@as == "constant")
            {
                return new ToolResult<object>() { Success = true, Data = await _augmentEngine.ExtractConstantSafeAsync(filePath, contextSnippet, newName, lineBefore, lineAfter) };
            }
            return new ToolResult<object>() { Success = false, Error = new ResultError("", $"Unknown as '{@as}'. Valid values: localVariable, field, parameter, constant.") };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Introduce ({As}) failed for '{NewName}' in '{FilePath}'", @as, newName, filePath);
            return new ToolResult<object>() { Success = false, Error = new ResultError("", $"Introduce failed: {ex.GetType().Name}: {ex.Message}") };
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
        [ToolOption(ToolOptionTag.AutoStage, required: false)] bool autoStage = true)
    {
        FilePath filePath = FilePath.FromWire(filepath, _workspaceManager.GetSolutionRoot());
        try
        {
            if (@as == "interface")
            {
                if (string.IsNullOrEmpty(newTypeName))
                {
                    return new ToolResult<object>() { Success = false, Error = new ResultError("", "newTypeName (interface name) is required when as=interface.") };
                }
                try
                {
                    var changes = await _refactoringEngine.ExtractInterfaceAsync(filePath, className, newTypeName);
                    if (autoStage)
                    {
                        var id = _workspaceManager.StageChanges(changes, $"Extract interface '{newTypeName}' from '{className}'.");
                        return new ToolResult<object>() { Success = true, Data = new { Changes = changes, StagingId = id } };
                    }
                    return new ToolResult<object>() { Success = true, Data = new { Changes = changes } };
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "ExtractMembers/interface unexpected exception for '{NewTypeName}'", newTypeName);
                    return new ToolResult<object>() { Success = false, Error = new ResultError("", $"ExtractMembers as=interface for '{newTypeName}' failed: {ex.GetType().Name}: {ex.Message}") };
                }
            }
            if (@as == "class")
            {
                if (memberNames == null || memberNames.Length == 0)
                {
                    return new ToolResult<object>() { Success = false, Error = new ResultError("", "memberNames is required when as=class.") };
                }
                if (string.IsNullOrEmpty(newTypeName))
                {
                    return new ToolResult<object>() { Success = false, Error = new ResultError("", "newTypeName (new class name) is required when as=class.") };
                }
                var result = await _advancedStructuralEngine.ExtractClassAsync(filePath, className, newTypeName, memberNames);
                return new ToolResult<object>() { Success = true, Data = result };
            }
            if (@as == "partial")
            {
                if (memberNames == null || memberNames.Length == 0)
                {
                    return new ToolResult<object>() { Success = false, Error = new ResultError("", "memberNames is required when as=partial.") };
                }
                var result = await _granularRefactoringEngine.ExtractMembersToPartialAsync(filePath, className, memberNames);
                return new ToolResult<object>() { Success = true, Data = result };
            }
            if (@as == "superclass")
            {
                if (string.IsNullOrEmpty(newTypeName))
                {
                    return new ToolResult<object>() { Success = false, Error = new ResultError("", "newTypeName (new base class name) is required when as=superclass.") };
                }
                var actualFilePaths = filePaths ?? new[] { filePath };
                var actualClassNames = classNames ?? new[] { className };
                try
                {
                    var changes = await _advancedStructuralEngine.ExtractSuperclassAsync(actualFilePaths, actualClassNames, newTypeName);
                    if (autoStage)
                    {
                        var id = _workspaceManager.StageChanges(changes, $"Extract superclass '{newTypeName}' from {actualClassNames.Length} classes.");
                        return new ToolResult<object>() { Success = true, Data = new { Changes = changes, StagingId = id } };
                    }
                    return new ToolResult<object>() { Success = true, Data = new { Changes = changes } };
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "ExtractMembers/superclass unexpected exception for '{NewTypeName}'", newTypeName);
                    return new ToolResult<object>() { Success = false, Error = new ResultError("", $"ExtractMembers as=superclass for '{newTypeName}' failed: {ex.GetType().Name}: {ex.Message}") };
                }
            }
            return new ToolResult<object>() { Success = false, Error = new ResultError("", $"Unknown as '{@as}'. Valid values: interface, class, partial, superclass.") };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ExtractMembers ({As}) failed for '{ClassName}' in '{FilePath}'", @as, className, filePath);
            return new ToolResult<object>() { Success = false, Error = new ResultError("", $"ExtractMembers failed: {ex.GetType().Name}: {ex.Message}") };
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
        [Consumes(DataTag.ProjectName)] string? projectName = null)
    {
        try
        {
            FilePath filePath = FilePath.FromWire(filepath, _workspaceManager.GetSolutionRoot());
            if (action == "implement")
            {
                if (string.IsNullOrEmpty(className))
                {
                    return new ToolResult<object>() { Success = false, Error = new ResultError("", "className is required when action=implement.") };
                }
                var result = await _codeGenerationEngine.ImplementInterfaceAsync(filePath, className, interfaceName);
                if (string.IsNullOrEmpty(result.UpdatedText))
                {
                    return new ToolResult<object>() { Success = false, Error = new ResultError("", $"SyncInterface implement failed for '{className}' implementing '{interfaceName}' in '{filePath}': file not found in workspace, class not found, or interface not found. Ensure the solution is loaded.") };
                }
                return new ToolResult<object>() { Success = true, Data = result.ToJsonSummary() };
            }
            if (action == "sync")
            {
                if (string.IsNullOrEmpty(className))
                {
                    return new ToolResult<object>() { Success = false, Error = new ResultError("", "className is required when action=sync.") };
                }
                var result = await _refactoringEngine.SyncInterfaceToImplementationAsync(filePath, className, interfaceName);
                if (string.IsNullOrEmpty(result.UpdatedText))
                {
                    return new ToolResult<object>() { Success = false, Error = new ResultError("", $"SyncInterface sync failed for '{className}' implementing '{interfaceName}' in '{filePath}': file not found in workspace, class not found, or interface not found. Ensure the solution is loaded.") };
                }
                return new ToolResult<object>() { Success = true, Data = result.ToJsonSummary() };
            }
            if (action == "verify")
            {
                var result = await _symbolNavigationEngine.VerifyInterfaceCompletenessAsync(interfaceName, projectName);
                return new ToolResult<object>() { Success = true, Data = result };
            }
            return new ToolResult<object>() { Success = false, Error = new ResultError("", $"Unknown action '{action}'. Valid values: implement, sync, verify.") };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SyncInterface ({Action}) failed for '{InterfaceName}'", action, interfaceName);
            return new ToolResult<object>() { Success = false, Error = new ResultError("", $"SyncInterface failed: {ex.GetType().Name}: {ex.Message}") };
        }
    }

    [McpServerTool(Name = "Inline")]
    [Produces(DataTag.ResultOnly)]
    [Description("Inlines a symbol by replacing all usages with its definition. kind: method (inline body at all call sites solution-wide — expression-body or single-return methods only), variable (inline local variable into usages), field (inline field value into usages), parameter (inline a constant parameter into method body — also supply methodName). targetName is the symbol name (parameterName when kind=parameter).")]
    public async Task<ToolResult<object>> Inline(
        [Consumes(DataTag.SourceFilepath, required: true)] string filepath,
        [Consumes(DataTag.SymbolName, required: true)] string targetName,
        [Consumes(DataTag.SymbolKind, required: true)] string kind,
        [Consumes(DataTag.SymbolName)] string? methodName = null)
    {
        FilePath filePath = FilePath.FromWire(filepath, _workspaceManager.GetSolutionRoot());
        try
        {
            if (kind == "method")
            {
                try
                {
                    var result = await _refinementEngine.InlineMethodAsync(filePath, targetName);
                    return new ToolResult<object>() { Success = true, Data = result };
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Inline/method unexpected exception for '{TargetName}' in '{FilePath}'", targetName, filePath);
                    return new ToolResult<object>() { Success = false, Error = new ResultError("", $"Inline method '{targetName}' in '{filePath}' failed: {ex.GetType().Name}: {ex.Message}") };
                }
            }
            if (kind == "variable")
            {
                var result = await _semanticRefactoringLibrary.InlineVariableAsync(filePath, targetName);
                return new ToolResult<object>() { Success = true, Data = result };
            }
            if (kind == "field")
            {
                var result = await _granularRefactoringEngine.InlineFieldAsync(filePath, targetName);
                return new ToolResult<object>() { Success = true, Data = result };
            }
            if (kind == "parameter")
            {
                if (string.IsNullOrEmpty(methodName))
                {
                    return new ToolResult<object>() { Success = false, Error = new ResultError("", "methodName is required when kind=parameter.") };
                }
                var result = await _granularRefactoringEngine.InlineParameterAsync(filePath, methodName, targetName);
                return new ToolResult<object>() { Success = true, Data = result };
            }
            return new ToolResult<object>() { Success = false, Error = new ResultError("", $"Unknown kind '{kind}'. Valid values: method, variable, field, parameter.") };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Inline ({Kind}) failed for '{TargetName}' in '{FilePath}'", kind, targetName, filePath);
            return new ToolResult<object>() { Success = false, Error = new ResultError("", $"Inline failed: {ex.GetType().Name}: {ex.Message}") };
        }
    }

    [McpServerTool(Name = "AddMember")]
    [Produces(DataTag.ResultOnly)]
    [Description("Adds a new member to a type. position: null/end (append), after:MemberName, or before:MemberName. autoStage=true → ChangeId.")]
    public async Task<ToolResult<object>> AddMember(
        [Consumes(DataTag.SourceFilepath, required: true)] string filepath,
        [Consumes(DataTag.SymbolName, required: true)] string containerName,
        [ExternalInputRequired(DataTag.ClassName)] string newMemberSource,
        [ExternalInputRequired(DataTag.Position)] string? position = null,
        [ToolOption(ToolOptionTag.AutoStage, required: false)] bool autoStage = true)
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
                return new ToolResult<object>() { Success = false, Error = new ResultError("", $"Unknown position '{position}'. Valid values: null, 'end', 'after:MemberName', 'before:MemberName'.") };
            }
            if (!autoStage)
            {
                return new ToolResult<object>() { Success = true, Data = updated.ToJsonSummary() };
            }
            var changes = new Dictionary<FilePath, string> { [filePath] = updated.UpdatedText };
            var id = _workspaceManager.StageChanges(changes, description);
            var summary = new PersistentWorkspaceManager.StagedChangeSummary(id, [filePath], description);
            return new ToolResult<object>() { Success = true, Data = summary };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AddMember failed for '{ContainerName}' in '{FilePath}'", containerName, filePath);
            return new ToolResult<object>() { Success = false, Error = new ResultError("", $"AddMember failed: {ex.GetType().Name}: {ex.Message}") };
        }
    }

    [McpServerTool(Name = "AddMemberTyped")]
    [Produces(DataTag.ChangeId)]
    [Description("Generates a typed member and adds it to a type. kind: property (auto-property) or field. Property defaults: hasSetter=true, accessibility=public. Field defaults: isReadonly=false, isStatic=false, accessibility=private; initializer sets optional field initializer expression. autoStage=true → ChangeId.")]
    public async Task<ToolResult<object>> AddMemberTyped(
        [Consumes(DataTag.SourceFilepath, required: true)] string filepath,
        [Consumes(DataTag.ClassName, required: true)] string containerName,
        [ExternalInputRequired(DataTag.SymbolName)] string name,
        [ExternalInputRequired(DataTag.DataType)] string type,
        [ExternalInputRequired(DataTag.SymbolKind)] string kind,
        [ExternalInputRequired(DataTag.Accessibility)] string accessibility = "public",
        [ExternalInputRequired(DataTag.HasSetter)] bool hasSetter = true,
        [ExternalInputRequired(DataTag.IsInit)] bool isInit = false,
        [ExternalInputRequired(DataTag.IsReadonly)] bool isReadonly = false,
        [ExternalInputRequired(DataTag.IsStatic)] bool isStatic = false,
        [ExternalInputRequired(DataTag.Initializer)] string? initializer = null,
        [ToolOptionAttribute(ToolOptionTag.AutoStage)] bool autoStage = true)
    {
        FilePath filePath = FilePath.FromWire(filepath, _workspaceManager.GetSolutionRoot());
        try
        {
            DocumentEditResult updated;
            string description;
            if (kind == "property")
            {
                updated = await _refactoringEngine.AddPropertyAsync(filePath, containerName, name, type, accessibility, hasSetter, isInit);
                description = $"Added '{type} {name}' property to '{containerName}' in {Path.GetFileName(filePath)}.";
            }
            else if (kind == "field")
            {
                updated = await _refactoringEngine.AddFieldAsync(filePath, containerName, name, type, accessibility, isReadonly, isStatic, initializer);
                description = $"Added '{type} {name}' field to '{containerName}' in {Path.GetFileName(filePath)}.";
            }
            else
            {
                return new ToolResult<object>() { Success = false, Error = new ResultError("", $"Unknown kind '{kind}'. Valid values: property, field.") };
            }
            if (!autoStage)
            {
                return new ToolResult<object>() { Success = true, Data = updated.ToJsonSummary() };
            }
            var changes = new Dictionary<FilePath, string> { [filePath] = updated.FilePath };
            var id = _workspaceManager.StageChanges(changes, description);
            var summary = new PersistentWorkspaceManager.StagedChangeSummary(id, [filePath], description);
            return new ToolResult<object>() { Success = true, Data = summary };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AddMemberTyped ({Kind}) failed for '{ContainerName}' in '{FilePath}'", kind, containerName, filePath);
            return new ToolResult<object>() { Success = false, Error = new ResultError("", $"AddMemberTyped failed: {ex.GetType().Name}: {ex.Message}") };
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
        [ToolOptionAttribute(ToolOptionTag.AutoStage)] bool autoStage = true)
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
                var changes = new Dictionary<FilePath, string> { [filePath] = updated.FilePath };
                var id = _workspaceManager.StageChanges(changes, $"Wrapped lines {startLine}-{endLine} in try/catch.");
                var summary = new PersistentWorkspaceManager.StagedChangeSummary(id, [filePath], $"Wrapped lines {startLine}-{endLine} in a try/{exceptionType} block in {Path.GetFileName(filePath)}.");
                return new ToolResult<object>() { Success = true, Data = summary };
            }
            if (wrapper == "using")
            {
                if (string.IsNullOrEmpty(name))
                {
                    return new ToolResult<object>() { Success = false, Error = new ResultError("", "name (disposalName) is required when wrapper=using.") };
                }
                var updated = await _semanticRefactoringLibrary.WrapInUsingAsync(filePath, startLine, endLine, name);
                if (!autoStage)
                {
                    return new ToolResult<object>() { Success = true, Data = updated.ToJsonSummary() };
                }
            }
            if (wrapper == "region")
            {
                if (string.IsNullOrEmpty(name))
                {
                    return new ToolResult<object>() { Success = false, Error = new ResultError("", "name (regionName) is required when wrapper=region.") };
                }
                var updated = await _refactoringEngine.WrapInRegionAsync(filePath, startLine, endLine, name);
                if (!autoStage)
                {
                    return new ToolResult<object>() { Success = true, Data = updated.ToJsonSummary() };
                }
                var changes = new Dictionary<FilePath, string> { [filePath] = updated.FilePath };
                var id = _workspaceManager.StageChanges(changes, $"Wrap lines {startLine}-{endLine} in #region '{name}'.");
                var summary = new PersistentWorkspaceManager.StagedChangeSummary(id, [filePath], $"Wraps lines {startLine}-{endLine} in #region '{name}' in {Path.GetFileName(filePath)}.");
                return new ToolResult<object>() { Success = true, Data = summary };
            }
            return new ToolResult<object>() { Success = false, Error = new ResultError("", $"Unknown wrapper '{wrapper}'. Valid values: tryCatch, using, region.") };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "WrapRange ({Wrapper}) failed in '{FilePath}'", wrapper, filePath);
            return new ToolResult<object>() { Success = false, Error = new ResultError("", $"WrapRange failed: {ex.GetType().Name}: {ex.Message}") };
        }
    }

    [McpServerTool(Name = "MoveType")]
    [Produces(DataTag.ChangeId)]
    [Description("Moves a type to a new location. destination: ownFile (move to its own .cs file → ChangeId + content previews; autoStage=false → raw file dict) or outerScope (move nested type to containing namespace scope → updated file content).")]
    public async Task<ToolResult<object>> MoveType(
        [Consumes(DataTag.SourceFilepath, required: true)] string filepath,
        [Consumes(DataTag.SymbolName, required: true)] string typeName,
        string destination,
        bool autoStage = true)
    {
        FilePath filePath = FilePath.FromWire(filepath, _workspaceManager.GetSolutionRoot());
        try
        {
            if (destination == "ownFile")
            {
                var changes = await _refactoringEngine.MoveTypeToFileAsync(filePath, typeName);
                if (!autoStage)
                {
                    return new ToolResult<object>
                    {
                        Success = true,
                        Data = changes
                    };
                }
                var id = _workspaceManager.StageChanges(changes, $"Move type '{typeName}' from '{Path.GetFileName(filePath)}'");
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
                        kvp => PreviewFileContent(kvp.Value)
                    )
                    }
                };
            }
            if (destination == "outerScope")
            {
                var changes = await _granularRefactoringEngine.MoveTypeToOuterScopeAsync(filePath, typeName);
                if (!autoStage)
                {
                    return new ToolResult<object>
                    {
                        Success = true,
                        Data = changes
                    };
                }
            }
            return new ToolResult<object>
            {
                Success = false,
                Error = new ResultError("", $"Unknown destination '{destination}'. Valid values: ownFile, outerScope.")
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "MoveType ({Destination}) failed for '{TypeName}' in '{FilePath}'", destination, typeName, filePath);
            return new ToolResult<object>
            {
                Success = false,
                Error = new ResultError("", $"MoveType failed: {ex.GetType().Name}: {ex.Message}")
            };
        }
    }
}
