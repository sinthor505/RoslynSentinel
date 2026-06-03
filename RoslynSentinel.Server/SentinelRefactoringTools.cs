using System.ComponentModel;

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

    [McpServerTool]
    [Description("Synchronizes the filename to match the primary type declared in the file.")]
    public async Task<string> SyncTypeAndFilename(string filePath)
    {
        try
        {
            return await _structuralRefinementEngine.SyncTypeAndFilenameAsync(filePath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SyncTypeAndFilename unexpected exception for '{FilePath}'", filePath);
            return $"SyncTypeAndFilename for '{filePath}' failed: {ex.GetType().Name}: {ex.Message}";
        }
    }

    [McpServerTool]
    [Description("Reorders parameters in a method signature and updates all call sites globally.")]
    public async Task<ToolResult<object>> ChangeSignature(string filePath, string methodName, int[] newParameterOrder, bool autoStage = true)
    {
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

    [McpServerTool]
    [Description("Renames a symbol (class, method, property, field, local, etc.) across the entire solution. " +
                 "Pass 'symbolName' (the exact identifier to rename) and 'contextSnippet' (a verbatim substring from the source file, long enough to appear exactly once — typically the surrounding line or expression). " +
                 "Example: symbolName=\"GetById\", contextSnippet=\"public async Task<Product?> GetById(\". " +
                 "Provide lineBefore and/or lineAfter (verbatim text from the line above/below the target) when the snippet could match multiple locations. " +
                 "Returns an error if the snippet matches zero or multiple locations. " +
                 "Returns per-file diff hunks (before/after for each changed line with ±2 lines of context) plus a staged ChangeId. Review FileChanges before calling ApplyStagedChanges.")]
    public async Task<ToolResult<object>> RenameSymbol(string filePath, string symbolName, string contextSnippet, string newName, bool autoStage = true, string? lineBefore = null, string? lineAfter = null)
    {
        try
        {
            var result = await _refactoringEngine.RenameSymbolAsync(filePath, symbolName, contextSnippet, newName, lineBefore, lineAfter);
            if (result.Error != null)
            {
                return new ToolResult<object>() { Success = false, Error = new ResultError("", result.Error) };
            }

            if (autoStage)
            {
                var id = _workspaceManager.StageChanges(result.PendingChanges, $"Rename '{result.OldName}' to '{result.NewName}'");
                return new ToolResult<object>() { Success = true, Data = new { result.OldName, result.NewName, FilesChanged = result.FileChanges.Count, StagingId = id, result.FileChanges } };
            }
            return new ToolResult<object>() { Success = true, Data = new { result.OldName, result.NewName, FilesChanged = result.FileChanges.Count, result.FileChanges } };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "RenameSymbol failed for '{SymbolName}' in '{FilePath}'", symbolName, filePath);
            return new ToolResult<object>() { Success = false, Error = new ResultError("", $"RenameSymbol failed for '{symbolName}' in '{filePath}': {ex.GetType().Name}: {ex.Message}") };
        }
    }

    [McpServerTool]
    [Description("Generates a mapping method between two types.")]
    public async Task<ToolResult<object>> GenerateMapping(string filePath, string fromType, string toType)
    {
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

    [McpServerTool]
    [Description("Converts an anonymous object creation to a formal named class.")]
    public async Task<ToolResult<object>> ConvertAnonymousToNamed(string filePath, string newClassName)
    {
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

    [McpServerTool]
    [Description("Moves all members of a class into a target class and removes the source class declaration. Works within the same file (sourceFilePath == targetFilePath) or across files. Automatically updates ALL type references (variable declarations, constructor calls, casts, typeof, etc.) to the inlined class name throughout the solution, renaming them to the target class. Returns a dictionary of filePath→updatedContent for every affected file.")]
    public async Task<ToolResult<object>> InlineClass(string sourceFilePath, string targetFilePath, string className)
    {
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

    [McpServerTool]
    [Description("""
        Atomically moves all secondary types to their own files.
        scope="file"     — moves types in a single file. Requires scopeName (the file path).
                           Returns a ChangeId plus first-15-line content previews.
        scope="project"  — moves types in every file in a project. Requires scopeName (project name).
                           Returns a ChangeId and the list of affected files.
        scope="solution" — moves types in every file across the entire solution. scopeName is ignored.
                           Returns a ChangeId and the list of affected files.
        autoStage=false  — returns the raw changes dictionary without staging.
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
        Dictionary<string, string> changes,
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
                    AffectedFiles = changes.Keys.Select(Path.GetFileName).ToList(),
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

    [McpServerTool]
    [Description("Surgically replaces a specific member (method, property, class) in a file by name with new source code.")]
    public async Task<string> ReplaceMember(string filePath, string memberName, string newSource)
    {
        try
        {
            return await _refactoringEngine.ReplaceMemberAsync(filePath, memberName, newSource);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ReplaceMember unexpected exception for '{MemberName}' in '{FilePath}'", memberName, filePath);
            return $"ReplaceMember for '{memberName}' in '{filePath}' failed: {ex.GetType().Name}: {ex.Message}";
        }
    }

    [McpServerTool]
    [Description("Removes a specific member from a class or interface by name.")]
    public async Task<string> RemoveMember(string filePath, string memberName)
    {
        try
        {
            return await _refactoringEngine.RemoveMemberAsync(filePath, memberName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "RemoveMember failed for '{MemberName}' in '{FilePath}'", memberName, filePath);
            return $"RemoveMember failed: {ex.GetType().Name}: {ex.Message}";
        }
    }

    [McpServerTool]
    [Description("""
        Adds a using directive to a file if not already present.

        Pass just the namespace name (e.g. "System.Linq", "Microsoft.Extensions.DependencyInjection").
        For static usings, prefix with "static " (e.g. "static System.Math").
        If the directive already exists, the file is returned unchanged.
        Use autoStage=true (default) to get a ChangeId for ApplyStagedChanges.
        """)]
    public async Task<ToolResult<object>> AddUsingDirective(string filePath, string namespaceName, bool autoStage = true)
    {
        try
        {
            var updated = await _refactoringEngine.AddUsingDirectiveAsync(filePath, namespaceName);
            if (!autoStage)
            {
                return new ToolResult<object>() { Success = true, Data = updated };
            }

            var changes = new Dictionary<string, string> { [filePath] = updated };
            var id = _workspaceManager.StageChanges(changes, $"Add using {namespaceName}.");
            return new ToolResult<object>() { Success = true, Data = new PersistentWorkspaceManager.StagedChangeSummary(id, [filePath], $"Adds 'using {namespaceName};' to {Path.GetFileName(filePath)}.") };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AddUsingDirective failed for '{Namespace}' in '{FilePath}'", namespaceName, filePath);
            return new ToolResult<object>() { Success = false, Error = new ResultError("", $"AddUsingDirective failed: {ex.GetType().Name}: {ex.Message}") };
        }
    }

    [McpServerTool]
    [Description("""
        Adds a new value to an existing enum. `explicitValue=99` → `Archived = 99`. If the enum is not found, the file is returned unchanged. `autoStage=true` → ChangeId for `staged_change`.
        """)]
    public async Task<ToolResult<object>> AddEnumValue(string filePath, string enumName, string valueName, int? explicitValue = null, bool autoStage = true)
    {
        try
        {
            var updated = await _refactoringEngine.AddEnumValueAsync(filePath, enumName, valueName, explicitValue);
            if (!autoStage)
            {
                return new ToolResult<object>() { Success = true, Data = updated };
            }

            var changes = new Dictionary<string, string> { [filePath] = updated };
            var id = _workspaceManager.StageChanges(changes, $"Add enum value '{valueName}' to '{enumName}'.");
            return new ToolResult<object>() { Success = true, Data = new PersistentWorkspaceManager.StagedChangeSummary(id, [filePath], $"Adds '{valueName}' to enum '{enumName}' in {Path.GetFileName(filePath)}.") };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AddEnumValue failed for '{EnumName}' in '{FilePath}'", enumName, filePath);
            return new ToolResult<object>() { Success = false, Error = new ResultError("", $"AddEnumValue failed: {ex.GetType().Name}: {ex.Message}") };
        }
    }

    [McpServerTool]
    [Description("Swaps left and right sides of all assignment statements within a line range.")]
    public async Task<ToolResult<object>> InvertAssignments(string filePath, int startLine, int endLine)
    {
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

    [McpServerTool]
    [Description("""
        Pulls a member (method or property) up from a derived class to its base class.

        Removes the 'override' modifier and adds 'virtual' (if not already abstract/virtual),
        then moves the declaration from the derived class into the base class. Returns a two-file
        change dict: derived class file (with member removed) + base class file (with member added).

        Requires: the source class has a base class with accessible source code in the solution.
        If autoStage is true (default), returns a ChangeId for use with ApplyStagedChanges.
        """)]
    public async Task<ToolResult<object>> PullUpMember(string filePath, string className, string memberName, bool autoStage = true)
    {
        try
        {
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

    [McpServerTool]
    [Description("""
        Changes the accessibility modifier of a type or member.

        targetName is the class/method/property/field name to modify.
        accessibility must be one of: "public", "private", "internal", "protected",
        "protected internal", or "private protected".
        Use autoStage=true (default) to get a ChangeId for ApplyStagedChanges.
        """)]
    public async Task<ToolResult<object>> ChangeAccessibility(string filePath, string targetName, string accessibility, bool autoStage = true)
    {
        try
        {
            var updated = await _refactoringEngine.ChangeAccessibilityAsync(filePath, targetName, accessibility);
            if (!autoStage)
            {
                return new ToolResult<object>() { Success = true, Data = updated };
            }

            var changes = new Dictionary<string, string> { [filePath] = updated };
            var id = _workspaceManager.StageChanges(changes, $"Change accessibility of '{targetName}' to '{accessibility}'.");
            return new ToolResult<object>() { Success = true, Data = new PersistentWorkspaceManager.StagedChangeSummary(id, [filePath], $"Changes accessibility of '{targetName}' to '{accessibility}' in {Path.GetFileName(filePath)}.") };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ChangeAccessibility failed for '{TargetName}' in '{FilePath}'", targetName, filePath);
            return new ToolResult<object>() { Success = false, Error = new ResultError("", $"ChangeAccessibility failed: {ex.GetType().Name}: {ex.Message}") };
        }
    }

    [McpServerTool]
    [Description("""
        Adds or replaces a /// <summary>...</summary> XML doc comment on a type or member.

        targetName is the class/method/property name to document.
        summaryText is the text content of the summary (single line).
        If a summary already exists it will be replaced.
        Use autoStage=true (default) to get a ChangeId for ApplyStagedChanges.
        """)]
    public async Task<ToolResult<object>> AddSummaryComment(string filePath, string targetName, string summaryText, bool autoStage = true)
    {
        try
        {
            var updated = await _refactoringEngine.AddSummaryCommentAsync(filePath, targetName, summaryText);
            if (!autoStage)
            {
                return new ToolResult<object>() { Success = true, Data = updated };
            }

            var changes = new Dictionary<string, string> { [filePath] = updated };
            var id = _workspaceManager.StageChanges(changes, $"Add summary comment to '{targetName}'.");
            return new ToolResult<object>() { Success = true, Data = new PersistentWorkspaceManager.StagedChangeSummary(id, [filePath], $"Added XML summary comment to '{targetName}' in {Path.GetFileName(filePath)}.") };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AddSummaryComment failed for '{TargetName}' in '{FilePath}'", targetName, filePath);
            return new ToolResult<object>() { Success = false, Error = new ResultError("", $"AddSummaryComment failed: {ex.GetType().Name}: {ex.Message}") };
        }
    }

    [McpServerTool]
    [Description("""
        Adds a DI constructor parameter in one step: private readonly field + parameter + body assignment. `fieldName` overrides the derived field name (defaults to `_camelCase` of `paramName`). Creates a constructor if none exists; converts expression-bodied constructors to block bodies. `autoStage=true` → ChangeId for `staged_change`
        """)]
    public async Task<ToolResult<object>> AddConstructorParameter(string filePath, string className, string paramName, string paramType, string? fieldName = null, bool autoStage = true)
    {
        try
        {
            var updated = await _refactoringEngine.AddConstructorParameterAsync(filePath, className, paramName, paramType, fieldName);
            if (!autoStage)
            {
                return new ToolResult<object>() { Success = true, Data = updated };
            }

            var changes = new Dictionary<string, string> { [filePath] = updated };
            var id = _workspaceManager.StageChanges(changes, $"Add constructor parameter '{paramName}' to '{className}'.");
            return new ToolResult<object>() { Success = true, Data = new PersistentWorkspaceManager.StagedChangeSummary(id, [filePath], $"Added '{paramType} {paramName}' DI parameter to '{className}' in {Path.GetFileName(filePath)}.") };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AddConstructorParameter failed for '{ClassName}' in '{FilePath}'", className, filePath);
            return new ToolResult<object>() { Success = false, Error = new ResultError("", $"AddConstructorParameter failed: {ex.GetType().Name}: {ex.Message}") };
        }
    }

    [McpServerTool]
    [Description("Encapsulates method parameters into a new C# 12 record type. Groups all non-CancellationToken parameters (or only those specified in parameterNames) into a 'public record {NewTypeName}(...)'. Rewrites parameter references in the method body to 'request.PropertyName'. Appends the record to the end of the file. Adds a TODO comment in the method body reminding to update call sites.")]
    public async Task<ToolResult<object>> IntroduceParameterObject(
        string filePath,
        string methodName,
        string? newTypeName = null,
        string[]? parameterNames = null)
    {
        try
        {
            var result = await _granularRefactoringEngine.IntroduceParameterObjectAsync(filePath, methodName, newTypeName, parameterNames);
            if (string.IsNullOrEmpty(result))
            {
                return new ToolResult<object>() { Success = false, Error = new ResultError("", $"IntroduceParameterObject failed for '{methodName}' in '{filePath}': file not found in workspace or method not found. Ensure the solution is loaded.") };
            }

            return new ToolResult<object>() { Success = true, Data = result };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "IntroduceParameterObject failed for '{MethodName}' in '{FilePath}'", methodName, filePath);
            return new ToolResult<object>() { Success = false, Error = new ResultError("", $"IntroduceParameterObject failed: {ex.GetType().Name}: {ex.Message}") };
        }
    }

    [McpServerTool]
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
    public async Task<ToolResult<object>> ExtractLocalVariable(string filePath, string contextSnippet, string variableName, string? lineBefore = null, string? lineAfter = null)
    {
        try
        {
            var result = await _refactoringEngine.ExtractLocalVariableAsync(filePath, contextSnippet, variableName, lineBefore, lineAfter);
            if (string.IsNullOrEmpty(result))
            {
                return new ToolResult<object>() { Success = false, Error = new ResultError("", $"ExtractLocalVariable failed for variable '{variableName}' in '{filePath}': file not found in workspace or context snippet did not match any expression. Ensure the solution is loaded.") };
            }

            return new ToolResult<object>() { Success = true, Data = result };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ExtractLocalVariable failed for '{VariableName}' in '{FilePath}'", variableName, filePath);
            return new ToolResult<object>() { Success = false, Error = new ResultError("", $"ExtractLocalVariable failed: {ex.GetType().Name}: {ex.Message}") };
        }
    }

    [McpServerTool]
    [Description("Modifies an attribute on a named type or member. action: add (add attributeSource to targetName), remove (remove attributeName from targetName). attributeSource/attributeName: attribute text with or without brackets or 'Attribute' suffix (e.g. \"[ApiController]\", \"Required\", \"Obsolete\"). Use autoStage=true (default) for ChangeId.")]
    public async Task<ToolResult<object>> ModifyAttribute(string filePath, string targetName, string attribute, string action, bool autoStage = true)
    {
        try
        {
            string updated;
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
                return new ToolResult<object>() { Success = true, Data = updated };
            }
            var changes = new Dictionary<string, string> { [filePath] = updated };
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

    [McpServerTool]
    [Description("Modifies a modifier keyword on a type or member. action: add or remove. modifier: virtual, abstract, sealed, static, readonly, override, partial, async, new, extern, unsafe, volatile. Use autoStage=true (default) for ChangeId.")]
    public async Task<ToolResult<object>> ModifyModifier(string filePath, string targetName, string modifier, string action, bool autoStage = true)
    {
        try
        {
            string updated;
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
                return new ToolResult<object>() { Success = true, Data = updated };
            }
            var changes = new Dictionary<string, string> { [filePath] = updated };
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

    [McpServerTool]
    [Description("Modifies the base type/interface list of a type. action: add or remove. baseTypeName: the type to add or remove (e.g. \"IDisposable\", \"BaseController\"). Use autoStage=true (default) for ChangeId.")]
    public async Task<ToolResult<object>> ModifyBaseType(string filePath, string typeName, string baseTypeName, string action, bool autoStage = true)
    {
        try
        {
            string updated;
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
                return new ToolResult<object>() { Success = true, Data = updated };
            }
            var changes = new Dictionary<string, string> { [filePath] = updated };
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

    [McpServerTool]
    [Description("Introduces a new named symbol from an expression. as: localVariable (local variable), field (private readonly field), parameter (method parameter, single-file), constant (extract to const — returns MsAugmentResult). contextSnippet: verbatim substring identifying the expression. newName: name for the new symbol. lineBefore/lineAfter for disambiguation.")]
    public async Task<ToolResult<object>> Introduce(string filePath, string contextSnippet, string newName, string @as, string? lineBefore = null, string? lineAfter = null)
    {
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

    [McpServerTool]
    [Description("Extracts members from a class into a new type. as: interface (public API → new interface file, newTypeName required), class (named members → new class, memberNames + newTypeName required), partial (named members → new partial file, memberNames required), superclass (common members → new base class, newTypeName required; for multiple classes supply filePaths[] + classNames[]). Use autoStage=true (default) for ChangeId where applicable.")]
    public async Task<ToolResult<object>> ExtractMembers(
        string filePath,
        string className,
        string @as,
        string? newTypeName = null,
        string[]? memberNames = null,
        string[]? filePaths = null,
        string[]? classNames = null,
        bool autoStage = true)
    {
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

    [McpServerTool]
    [Description("Manages interface/class synchronization. action: implement (generate stub implementations for all unimplemented interface members on className — returns updated file content), sync (add to interface any public members in className missing from interfaceName — returns updated interface file), verify (report coverage of all implementing classes — requires only interfaceName; use projectName to scope). filePath is the class file for implement/sync; interfaceName is always required.")]
    public async Task<ToolResult<object>> SyncInterface(string filePath, string interfaceName, string action, string? className = null, string? projectName = null)
    {
        try
        {
            if (action == "implement")
            {
                if (string.IsNullOrEmpty(className))
                {
                    return new ToolResult<object>() { Success = false, Error = new ResultError("", "className is required when action=implement.") };
                }
                var result = await _codeGenerationEngine.ImplementInterfaceAsync(filePath, className, interfaceName);
                if (string.IsNullOrEmpty(result))
                {
                    return new ToolResult<object>() { Success = false, Error = new ResultError("", $"SyncInterface implement failed for '{className}' implementing '{interfaceName}' in '{filePath}': file not found in workspace, class not found, or interface not found. Ensure the solution is loaded.") };
                }
                return new ToolResult<object>() { Success = true, Data = result };
            }
            if (action == "sync")
            {
                if (string.IsNullOrEmpty(className))
                {
                    return new ToolResult<object>() { Success = false, Error = new ResultError("", "className is required when action=sync.") };
                }
                var result = await _refactoringEngine.SyncInterfaceToImplementationAsync(filePath, className, interfaceName);
                if (string.IsNullOrEmpty(result))
                {
                    return new ToolResult<object>() { Success = false, Error = new ResultError("", $"SyncInterface sync failed for '{className}' implementing '{interfaceName}' in '{filePath}': file not found in workspace, class not found, or interface not found. Ensure the solution is loaded.") };
                }
                return new ToolResult<object>() { Success = true, Data = result };
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

    [McpServerTool]
    [Description("Inlines a symbol by replacing all usages with its definition. kind: method (inline method body at all call sites solution-wide — expression-body or single-return methods only), variable (inline local variable into usages), field (inline field value into usages), parameter (inline a constant parameter into method body — also supply methodName). targetName is the symbol name (parameterName when kind=parameter).")]
    public async Task<ToolResult<object>> Inline(string filePath, string targetName, string kind, string? methodName = null)
    {
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

    [McpServerTool]
    [Description("Adds a new member to a type. containerName: target class/struct/interface. newMemberSource: full member declaration text. position: null or 'end' (append at end), 'after:MemberName' (insert after named member), 'before:MemberName' (insert before named member). Use autoStage=true (default) for ChangeId.")]
    public async Task<ToolResult<object>> AddMember(string filePath, string containerName, string newMemberSource, string? position = null, bool autoStage = true)
    {
        try
        {
            string updated;
            string description;
            if (string.IsNullOrEmpty(position) || position == "end")
            {
                updated = await _refactoringEngine.AddMemberAsync(filePath, containerName, newMemberSource);
                description = $"Adds new member to '{containerName}' in {Path.GetFileName(filePath)}.";
            }
            else if (position.StartsWith("after:", StringComparison.OrdinalIgnoreCase))
            {
                var afterMemberName = position.Substring("after:".Length);
                updated = await _refactoringEngine.InsertMemberAfterAsync(filePath, containerName, afterMemberName, newMemberSource);
                description = $"Inserts new member after '{afterMemberName}' in '{containerName}' in {Path.GetFileName(filePath)}.";
            }
            else if (position.StartsWith("before:", StringComparison.OrdinalIgnoreCase))
            {
                var beforeMemberName = position.Substring("before:".Length);
                updated = await _refactoringEngine.InsertMemberBeforeAsync(filePath, containerName, beforeMemberName, newMemberSource);
                description = $"Inserts new member before '{beforeMemberName}' in '{containerName}' in {Path.GetFileName(filePath)}.";
            }
            else
            {
                return new ToolResult<object>() { Success = false, Error = new ResultError("", $"Unknown position '{position}'. Valid values: null, 'end', 'after:MemberName', 'before:MemberName'.") };
            }
            if (!autoStage)
            {
                return new ToolResult<object>() { Success = true, Data = updated };
            }
            var changes = new Dictionary<string, string> { [filePath] = updated };
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

    [McpServerTool]
    [Description("Generates a typed member and adds it to a type. kind: property (auto-property) or field. containerName: target class/struct. name: member name. type: C# type. For property: hasSetter (default true), isInit, accessibility (default 'public'). For field: isReadonly, isStatic, initializer (optional expression), accessibility (default 'private'). Use autoStage=true (default) for ChangeId.")]
    public async Task<ToolResult<object>> AddMemberTyped(
        string filePath,
        string containerName,
        string name,
        string type,
        string kind,
        string accessibility = "public",
        bool hasSetter = true,
        bool isInit = false,
        bool isReadonly = false,
        bool isStatic = false,
        string? initializer = null,
        bool autoStage = true)
    {
        try
        {
            string updated;
            string description;
            if (kind == "property")
            {
                updated = await _refactoringEngine.AddPropertyAsync(filePath, containerName, name, type, accessibility, hasSetter, isInit);
                description = $"Adds '{type} {name}' property to '{containerName}' in {Path.GetFileName(filePath)}.";
            }
            else if (kind == "field")
            {
                updated = await _refactoringEngine.AddFieldAsync(filePath, containerName, name, type, accessibility, isReadonly, isStatic, initializer);
                description = $"Adds '{type} {name}' field to '{containerName}' in {Path.GetFileName(filePath)}.";
            }
            else
            {
                return new ToolResult<object>() { Success = false, Error = new ResultError("", $"Unknown kind '{kind}'. Valid values: property, field.") };
            }
            if (!autoStage)
            {
                return new ToolResult<object>() { Success = true, Data = updated };
            }
            var changes = new Dictionary<string, string> { [filePath] = updated };
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

    [McpServerTool]
    [Description("Wraps a line range. wrapper: tryCatch (wrap in try/catch — name is exceptionType default 'Exception'; also use catchVariableName default 'ex', catchBody optional), using (wrap in using statement — name is disposalName, required), region (wrap in #region — name is region label, required). startLine and endLine are 1-based. Use autoStage=true (default) for ChangeId (tryCatch/region only; using returns content string directly).")]
    public async Task<ToolResult<object>> WrapRange(
        string filePath,
        int startLine,
        int endLine,
        string wrapper,
        string? name = null,
        string catchVariableName = "ex",
        string? catchBody = null,
        bool autoStage = true)
    {
        try
        {
            if (wrapper == "tryCatch")
            {
                var exceptionType = name ?? "Exception";
                var updated = await _refactoringEngine.WrapInTryCatchAsync(filePath, startLine, endLine, exceptionType, catchVariableName, catchBody);
                if (!autoStage)
                {
                    return new ToolResult<object>() { Success = true, Data = updated };
                }
                var changes = new Dictionary<string, string> { [filePath] = updated };
                var id = _workspaceManager.StageChanges(changes, $"Wrap lines {startLine}-{endLine} in try/catch.");
                var summary = new PersistentWorkspaceManager.StagedChangeSummary(id, [filePath], $"Wraps lines {startLine}-{endLine} in a try/{exceptionType} block in {Path.GetFileName(filePath)}.");
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
                    return new ToolResult<object>() { Success = true, Data = updated };
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
                    return new ToolResult<object>() { Success = true, Data = updated };
                }
                var changes = new Dictionary<string, string> { [filePath] = updated };
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

    [McpServerTool]
    [Description("Moves a type to a new location. destination: ownFile (move to its own '.cs' file — returns ChangeId + content previews; use autoStage=false for raw file dict), outerScope (move nested type to containing namespace scope — returns updated file content).")]
    public async Task<ToolResult<object>> MoveType(string filePath, string typeName, string destination, bool autoStage = true)
    {
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
