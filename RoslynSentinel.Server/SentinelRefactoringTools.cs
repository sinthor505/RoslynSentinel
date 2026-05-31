using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
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
            throw new InvalidOperationException($"SyncTypeAndFilename for '{filePath}' failed: {ex.GetType().Name}: {ex.Message}", ex);
        }
    }

    [McpServerTool]
    [Description("Reorders parameters in a method signature and updates all call sites globally.")]
    public async Task<object> ChangeSignature(string filePath, string methodName, int[] newParameterOrder, bool autoStage = true) 
    {
        var changes = await _refactoringEngine.ChangeSignatureAsync(filePath, methodName, newParameterOrder);
        if (autoStage)
        {
            return _workspaceManager.StageChanges(changes, $"Change signature of method '{methodName}' in '{Path.GetFileName(filePath)}'.");
        }

        return changes;
    }



    [McpServerTool]
    [Description("Renames a symbol (class, method, property, field, local, etc.) across the entire solution. " +
                 "Pass 'symbolName' (the exact identifier to rename) and 'contextSnippet' (a verbatim substring from the source file, long enough to appear exactly once — typically the surrounding line or expression). " +
                 "Example: symbolName=\"GetById\", contextSnippet=\"public async Task<Product?> GetById(\". " +
                 "Provide lineBefore and/or lineAfter (verbatim text from the line above/below the target) when the snippet could match multiple locations. " +
                 "Returns an error if the snippet matches zero or multiple locations. " +
                 "Returns per-file diff hunks (before/after for each changed line with ±2 lines of context) plus a staged ChangeId. Review FileChanges before calling ApplyStagedChanges.")]
    public async Task<object> RenameSymbol(string filePath, string symbolName, string contextSnippet, string newName, bool autoStage = true, string? lineBefore = null, string? lineAfter = null)
    {
        var result = await _refactoringEngine.RenameSymbolAsync(filePath, symbolName, contextSnippet, newName, lineBefore, lineAfter);
        if (result.Error != null)
        {
            return new { Error = result.Error };
        }

        if (autoStage)
        {
            var id = _workspaceManager.StageChanges(result.PendingChanges, $"Rename '{result.OldName}' to '{result.NewName}'");
            return new { result.OldName, result.NewName, FilesChanged = result.FileChanges.Count, StagingId = id, result.FileChanges };
        }
        return new { result.OldName, result.NewName, FilesChanged = result.FileChanges.Count, result.FileChanges };
    }


    [McpServerTool]
    [Description("Generates a mapping method between two types.")]
    public async Task<string> GenerateMapping(string filePath, string fromType, string toType) 
        => await _mappingEngine.GenerateMappingAsync(filePath, fromType, toType);

    [McpServerTool]
    [Description("Converts an anonymous object creation to a formal named class.")]
    public async Task<Dictionary<string, string>> ConvertAnonymousToNamed(string filePath, string newClassName) 
        => await _advancedTypeEngine.ConvertAnonymousToNamedAsync(filePath, newClassName);

    [McpServerTool]
    [Description("Moves all members of a class into a target class and removes the source class declaration. Works within the same file (sourceFilePath == targetFilePath) or across files. Automatically updates ALL type references (variable declarations, constructor calls, casts, typeof, etc.) to the inlined class name throughout the solution, renaming them to the target class. Returns a dictionary of filePath→updatedContent for every affected file.")]
    public async Task<Dictionary<string, string>> InlineClass(string sourceFilePath, string targetFilePath, string className) 
        => await _advancedStructuralEngine.InlineClassAsync(sourceFilePath, targetFilePath, className);



    [McpServerTool]
    [Description("Atomically moves all secondary types in a file to their own files. Returns a ChangeId for ApplyStagedChanges plus first-15-line previews of each affected file's new content.")]
    public async Task<object> MoveAllTypesToFiles(string filePath, bool autoStage = true)
    {
        var changes = await _refactoringEngine.MoveAllTypesToFilesAsync(filePath);
        if (!autoStage)
        {
            return changes;
        }

        if (changes.Count == 0)
        {
            return "No secondary types found to move.";
        }

        var id = _workspaceManager.StageChanges(changes, $"Move all types to files in '{Path.GetFileName(filePath)}'");
        return new
        {
            ChangeId = id,
            Description = $"Moves all secondary types in '{Path.GetFileName(filePath)}' to their own files. Call ApplyStagedChanges(\"{id}\") to apply.",
            AffectedFiles = changes.Keys.Select(Path.GetFileName).ToList(),
            ContentPreviews = changes.ToDictionary(
                kvp => Path.GetFileName(kvp.Key)!,
                kvp => PreviewFileContent(kvp.Value)
            )
        };
    }

    [McpServerTool]
    [Description("Atomically moves all secondary types in every file in a project to their own files. If autoStage is true, returns a ChangeId.")]
    public async Task<object> MoveAllTypesToFilesInProject(string projectName, bool autoStage = true)
    {
        var changes = await _refactoringEngine.MoveAllTypesToFilesInProjectAsync(projectName);
        if (!autoStage)
        {
            return changes;
        }

        if (changes.Count == 0)
        {
            return $"No secondary types found in project '{projectName}'.";
        }

        var id = _workspaceManager.StageChanges(changes, $"Move all types to files in project '{projectName}'");
        return new PersistentWorkspaceManager.StagedChangeSummary(id, changes.Keys.ToList(), $"Moves all secondary types in project '{projectName}' to their own files.");
    }

    [McpServerTool]
    [Description("Atomically moves all secondary types in every file across the entire solution to their own files. If autoStage is true, returns a ChangeId.")]
    public async Task<object> MoveAllTypesToFilesInSolution(bool autoStage = true)
    {
        var changes = await _refactoringEngine.MoveAllTypesToFilesInSolutionAsync();
        if (!autoStage)
        {
            return changes;
        }

        if (changes.Count == 0)
        {
            return "No secondary types found in solution.";
        }

        var id = _workspaceManager.StageChanges(changes, "Move all types to files in solution");
        return new PersistentWorkspaceManager.StagedChangeSummary(id, changes.Keys.ToList(), "Moves all secondary types across the entire solution to their own files.");
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
            throw new InvalidOperationException($"ReplaceMember for '{memberName}' in '{filePath}' failed: {ex.GetType().Name}: {ex.Message}", ex);
        }
    }

    [McpServerTool]
    [Description("Removes a specific member from a class or interface by name.")]
    public async Task<string> RemoveMember(string filePath, string memberName) 
        => await _refactoringEngine.RemoveMemberAsync(filePath, memberName);

    [McpServerTool]
    [Description("""
        Adds a using directive to a file if not already present.
        
        Pass just the namespace name (e.g. "System.Linq", "Microsoft.Extensions.DependencyInjection").
        For static usings, prefix with "static " (e.g. "static System.Math").
        If the directive already exists, the file is returned unchanged.
        Use autoStage=true (default) to get a ChangeId for ApplyStagedChanges.
        """)]
    public async Task<object> AddUsingDirective(string filePath, string namespaceName, bool autoStage = true)
    {
        var updated = await _refactoringEngine.AddUsingDirectiveAsync(filePath, namespaceName);
        if (!autoStage)
        {
            return updated;
        }

        var changes = new Dictionary<string, string> { [filePath] = updated };
        var id = _workspaceManager.StageChanges(changes, $"Add using {namespaceName}.");
        return new PersistentWorkspaceManager.StagedChangeSummary(id, [filePath], $"Adds 'using {namespaceName};' to {Path.GetFileName(filePath)}.");
    }

    [McpServerTool]
    [Description("""
        Adds a new value to an existing enum declaration.
        
        Specify the enum name and the new value name. Optionally provide an explicit integer value
        (e.g. enumName="Status", valueName="Archived", explicitValue=99 produces 'Archived = 99').
        If the enum is not found in the file, the file is returned unchanged.
        Use autoStage=true (default) to get a ChangeId for ApplyStagedChanges.
        """)]
    public async Task<object> AddEnumValue(string filePath, string enumName, string valueName, int? explicitValue = null, bool autoStage = true)
    {
        var updated = await _refactoringEngine.AddEnumValueAsync(filePath, enumName, valueName, explicitValue);
        if (!autoStage)
        {
            return updated;
        }

        var changes = new Dictionary<string, string> { [filePath] = updated };
        var id = _workspaceManager.StageChanges(changes, $"Add enum value '{valueName}' to '{enumName}'.");
        return new PersistentWorkspaceManager.StagedChangeSummary(id, [filePath], $"Adds '{valueName}' to enum '{enumName}' in {Path.GetFileName(filePath)}.");
    }


    [McpServerTool]
    [Description("Swaps left and right sides of all assignment statements within a line range.")]
    public async Task<string> InvertAssignments(string filePath, int startLine, int endLine)
        => await _mappingEngine.InvertAssignmentsAsync(filePath, startLine, endLine);



    [McpServerTool]
    [Description("""
        Pulls a member (method or property) up from a derived class to its base class.
        
        Removes the 'override' modifier and adds 'virtual' (if not already abstract/virtual),
        then moves the declaration from the derived class into the base class. Returns a two-file
        change dict: derived class file (with member removed) + base class file (with member added).
        
        Requires: the source class has a base class with accessible source code in the solution.
        If autoStage is true (default), returns a ChangeId for use with ApplyStagedChanges.
        """)]
    public async Task<object> PullUpMember(string filePath, string className, string memberName, bool autoStage = true)
    {
        var changes = await _refinementEngine.PullUpMemberAsync(filePath, className, memberName);
        if (!autoStage)
        {
            return changes;
        }

        if (changes.Count == 0)
        {
            return $"Member '{memberName}' not found or no accessible base class available.";
        }

        var id = _workspaceManager.StageChanges(changes, $"Pull up '{memberName}' from '{className}' to base class.");
        return new PersistentWorkspaceManager.StagedChangeSummary(id, changes.Keys.ToList(), $"Pulls '{memberName}' from '{className}' up to its base class.");
    }

    [McpServerTool]
    [Description("""
        Changes the accessibility modifier of a type or member.
        
        targetName is the class/method/property/field name to modify.
        accessibility must be one of: "public", "private", "internal", "protected",
        "protected internal", or "private protected".
        Use autoStage=true (default) to get a ChangeId for ApplyStagedChanges.
        """)]
    public async Task<object> ChangeAccessibility(string filePath, string targetName, string accessibility, bool autoStage = true)
    {
        var updated = await _refactoringEngine.ChangeAccessibilityAsync(filePath, targetName, accessibility);
        if (!autoStage)
        {
            return updated;
        }

        var changes = new Dictionary<string, string> { [filePath] = updated };
        var id = _workspaceManager.StageChanges(changes, $"Change accessibility of '{targetName}' to '{accessibility}'.");
        return new PersistentWorkspaceManager.StagedChangeSummary(id, [filePath], $"Changes accessibility of '{targetName}' to '{accessibility}' in {Path.GetFileName(filePath)}.");
    }

    [McpServerTool]
    [Description("""
        Adds or replaces a /// <summary>...</summary> XML doc comment on a type or member.
        
        targetName is the class/method/property name to document.
        summaryText is the text content of the summary (single line).
        If a summary already exists it will be replaced.
        Use autoStage=true (default) to get a ChangeId for ApplyStagedChanges.
        """)]
    public async Task<object> AddSummaryComment(string filePath, string targetName, string summaryText, bool autoStage = true)
    {
        var updated = await _refactoringEngine.AddSummaryCommentAsync(filePath, targetName, summaryText);
        if (!autoStage)
        {
            return updated;
        }

        var changes = new Dictionary<string, string> { [filePath] = updated };
        var id = _workspaceManager.StageChanges(changes, $"Add summary comment to '{targetName}'.");
        return new PersistentWorkspaceManager.StagedChangeSummary(id, [filePath], $"Adds XML summary comment to '{targetName}' in {Path.GetFileName(filePath)}.");
    }


    [McpServerTool]
    [Description("""
        Adds a DI constructor parameter in one step: private readonly field + parameter + body assignment.
        
        className is the target class; paramName and paramType define the parameter.
        fieldName overrides the derived field name (defaults to _camelCase of paramName).
        If the class has no constructor one is created. Expression-bodied constructors are converted to block bodies.
        Use autoStage=true (default) to get a ChangeId for ApplyStagedChanges.
        """)]
    public async Task<object> AddConstructorParameter(string filePath, string className, string paramName, string paramType, string? fieldName = null, bool autoStage = true)
    {
        var updated = await _refactoringEngine.AddConstructorParameterAsync(filePath, className, paramName, paramType, fieldName);
        if (!autoStage)
        {
            return updated;
        }

        var changes = new Dictionary<string, string> { [filePath] = updated };
        var id = _workspaceManager.StageChanges(changes, $"Add constructor parameter '{paramName}' to '{className}'.");
        return new PersistentWorkspaceManager.StagedChangeSummary(id, [filePath], $"Adds '{paramType} {paramName}' DI parameter to '{className}' in {Path.GetFileName(filePath)}.");
    }

    [McpServerTool]
    [Description("Encapsulates method parameters into a new C# 12 record type. Groups all non-CancellationToken parameters (or only those specified in parameterNames) into a 'public record {NewTypeName}(...)'. Rewrites parameter references in the method body to 'request.PropertyName'. Appends the record to the end of the file. Adds a TODO comment in the method body reminding to update call sites.")]
    public async Task<string> IntroduceParameterObject(
        string filePath,
        string methodName,
        string? newTypeName = null,
        string[]? parameterNames = null)
    {
        var result = await _granularRefactoringEngine.IntroduceParameterObjectAsync(filePath, methodName, newTypeName, parameterNames);
        if (string.IsNullOrEmpty(result))
        {
            throw new InvalidOperationException(
                $"IntroduceParameterObject failed for '{methodName}' in '{filePath}': " +
                "file not found in workspace or method not found. Ensure the solution is loaded.");
        }

        return result;
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
    public async Task<string> ExtractLocalVariable(string filePath, string contextSnippet, string variableName, string? lineBefore = null, string? lineAfter = null)
    {
        var result = await _refactoringEngine.ExtractLocalVariableAsync(filePath, contextSnippet, variableName, lineBefore, lineAfter);
        if (string.IsNullOrEmpty(result))
        {
            throw new InvalidOperationException(
                $"ExtractLocalVariable failed for variable '{variableName}' in '{filePath}': " +
                "file not found in workspace or context snippet did not match any expression. Ensure the solution is loaded.");
        }

        return result;
    }




    [McpServerTool]
    [Description("Modifies an attribute on a named type or member. action: add (add attributeSource to targetName), remove (remove attributeName from targetName). attributeSource/attributeName: attribute text with or without brackets or 'Attribute' suffix (e.g. \"[ApiController]\", \"Required\", \"Obsolete\"). Use autoStage=true (default) for ChangeId.")]
    public async Task<object> ModifyAttribute(string filePath, string targetName, string attribute, string action, bool autoStage = true)
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
            throw new ArgumentException($"Unknown action '{action}'. Valid values: add, remove.");
        }
        if (!autoStage)
        {
            return updated;
        }
        var changes = new Dictionary<string, string> { [filePath] = updated };
        var id = _workspaceManager.StageChanges(changes, $"{action} attribute '{attribute}' on '{targetName}'.");
        return new PersistentWorkspaceManager.StagedChangeSummary(id, [filePath], $"{(action == "add" ? "Adds" : "Removes")} '{attribute}' attribute on '{targetName}' in {Path.GetFileName(filePath)}.");
    }

    [McpServerTool]
    [Description("Modifies a modifier keyword on a type or member. action: add or remove. modifier: virtual, abstract, sealed, static, readonly, override, partial, async, new, extern, unsafe, volatile. Use autoStage=true (default) for ChangeId.")]
    public async Task<object> ModifyModifier(string filePath, string targetName, string modifier, string action, bool autoStage = true)
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
            throw new ArgumentException($"Unknown action '{action}'. Valid values: add, remove.");
        }
        if (!autoStage)
        {
            return updated;
        }
        var changes = new Dictionary<string, string> { [filePath] = updated };
        var id = _workspaceManager.StageChanges(changes, $"{action} '{modifier}' modifier on '{targetName}'.");
        return new PersistentWorkspaceManager.StagedChangeSummary(id, [filePath], $"{(action == "add" ? "Adds" : "Removes")} '{modifier}' modifier on '{targetName}' in {Path.GetFileName(filePath)}.");
    }

    [McpServerTool]
    [Description("Modifies the base type/interface list of a type. action: add or remove. baseTypeName: the type to add or remove (e.g. \"IDisposable\", \"BaseController\"). Use autoStage=true (default) for ChangeId.")]
    public async Task<object> ModifyBaseType(string filePath, string typeName, string baseTypeName, string action, bool autoStage = true)
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
            throw new ArgumentException($"Unknown action '{action}'. Valid values: add, remove.");
        }
        if (!autoStage)
        {
            return updated;
        }
        var changes = new Dictionary<string, string> { [filePath] = updated };
        var id = _workspaceManager.StageChanges(changes, $"{action} base type '{baseTypeName}' on '{typeName}'.");
        return new PersistentWorkspaceManager.StagedChangeSummary(id, [filePath], $"{(action == "add" ? "Adds" : "Removes")} '{baseTypeName}' on '{typeName}' in {Path.GetFileName(filePath)}.");
    }

    [McpServerTool]
    [Description("Introduces a new named symbol from an expression. as: localVariable (local variable), field (private readonly field), parameter (method parameter, single-file), constant (extract to const — returns MsAugmentResult). contextSnippet: verbatim substring identifying the expression. newName: name for the new symbol. lineBefore/lineAfter for disambiguation.")]
    public async Task<object> Introduce(string filePath, string contextSnippet, string newName, string @as, string? lineBefore = null, string? lineAfter = null)
    {
        if (@as == "localVariable")
        {
            return await _granularRefactoringEngine.IntroduceVariableAsync(filePath, contextSnippet, newName, lineBefore, lineAfter);
        }
        if (@as == "field")
        {
            return await _granularRefactoringEngine.IntroduceFieldAsync(filePath, contextSnippet, newName, lineBefore, lineAfter);
        }
        if (@as == "parameter")
        {
            return await _granularRefactoringEngine.IntroduceParameterAsync(filePath, contextSnippet, newName, lineBefore, lineAfter);
        }
        if (@as == "constant")
        {
            return await _augmentEngine.ExtractConstantSafeAsync(filePath, contextSnippet, newName, lineBefore, lineAfter);
        }
        throw new ArgumentException($"Unknown as '{@as}'. Valid values: localVariable, field, parameter, constant.");
    }

    [McpServerTool]
    [Description("Extracts members from a class into a new type. as: interface (public API → new interface file, newTypeName required), class (named members → new class, memberNames + newTypeName required), partial (named members → new partial file, memberNames required), superclass (common members → new base class, newTypeName required; for multiple classes supply filePaths[] + classNames[]). Use autoStage=true (default) for ChangeId where applicable.")]
    public async Task<object> ExtractMembers(
        string filePath,
        string className,
        string @as,
        string? newTypeName = null,
        string[]? memberNames = null,
        string[]? filePaths = null,
        string[]? classNames = null,
        bool autoStage = true)
    {
        if (@as == "interface")
        {
            if (string.IsNullOrEmpty(newTypeName))
            {
                throw new ArgumentException("newTypeName (interface name) is required when as=interface.");
            }
            try
            {
                var changes = await _refactoringEngine.ExtractInterfaceAsync(filePath, className, newTypeName);
                if (autoStage)
                {
                    return _workspaceManager.StageChanges(changes, $"Extract interface '{newTypeName}' from '{className}'.");
                }
                return changes;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ExtractMembers/interface unexpected exception for '{NewTypeName}'", newTypeName);
                throw new InvalidOperationException($"ExtractMembers as=interface for '{newTypeName}' failed: {ex.GetType().Name}: {ex.Message}", ex);
            }
        }
        if (@as == "class")
        {
            if (memberNames == null || memberNames.Length == 0)
            {
                throw new ArgumentException("memberNames is required when as=class.");
            }
            if (string.IsNullOrEmpty(newTypeName))
            {
                throw new ArgumentException("newTypeName (new class name) is required when as=class.");
            }
            return await _advancedStructuralEngine.ExtractClassAsync(filePath, className, newTypeName, memberNames);
        }
        if (@as == "partial")
        {
            if (memberNames == null || memberNames.Length == 0)
            {
                throw new ArgumentException("memberNames is required when as=partial.");
            }
            return await _granularRefactoringEngine.ExtractMembersToPartialAsync(filePath, className, memberNames);
        }
        if (@as == "superclass")
        {
            if (string.IsNullOrEmpty(newTypeName))
            {
                throw new ArgumentException("newTypeName (new base class name) is required when as=superclass.");
            }
            var actualFilePaths = filePaths ?? [filePath];
            var actualClassNames = classNames ?? [className];
            try
            {
                var changes = await _advancedStructuralEngine.ExtractSuperclassAsync(actualFilePaths, actualClassNames, newTypeName);
                if (autoStage)
                {
                    return _workspaceManager.StageChanges(changes, $"Extract superclass '{newTypeName}' from {actualClassNames.Length} classes.");
                }
                return changes;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ExtractMembers/superclass unexpected exception for '{NewTypeName}'", newTypeName);
                throw new InvalidOperationException($"ExtractMembers as=superclass for '{newTypeName}' failed: {ex.GetType().Name}: {ex.Message}", ex);
            }
        }
        throw new ArgumentException($"Unknown as '{@as}'. Valid values: interface, class, partial, superclass.");
    }

    [McpServerTool]
    [Description("Manages interface/class synchronization. action: implement (generate stub implementations for all unimplemented interface members on className — returns updated file content), sync (add to interface any public members in className missing from interfaceName — returns updated interface file), verify (report coverage of all implementing classes — requires only interfaceName; use projectName to scope). filePath is the class file for implement/sync; interfaceName is always required.")]
    public async Task<object> SyncInterface(string filePath, string interfaceName, string action, string? className = null, string? projectName = null)
    {
        if (action == "implement")
        {
            if (string.IsNullOrEmpty(className))
            {
                throw new ArgumentException("className is required when action=implement.");
            }
            var result = await _codeGenerationEngine.ImplementInterfaceAsync(filePath, className, interfaceName);
            if (string.IsNullOrEmpty(result))
            {
                throw new InvalidOperationException(
                    $"SyncInterface implement failed for '{className}' implementing '{interfaceName}' in '{filePath}': " +
                    "file not found in workspace, class not found, or interface not found. Ensure the solution is loaded.");
            }
            return result;
        }
        if (action == "sync")
        {
            if (string.IsNullOrEmpty(className))
            {
                throw new ArgumentException("className is required when action=sync.");
            }
            var result = await _refactoringEngine.SyncInterfaceToImplementationAsync(filePath, className, interfaceName);
            if (string.IsNullOrEmpty(result))
            {
                throw new InvalidOperationException(
                    $"SyncInterface sync failed for '{className}' implementing '{interfaceName}' in '{filePath}': " +
                    "file not found in workspace, class not found, or interface not found. Ensure the solution is loaded.");
            }
            return result;
        }
        if (action == "verify")
        {
            return await _symbolNavigationEngine.VerifyInterfaceCompletenessAsync(interfaceName, projectName);
        }
        throw new ArgumentException($"Unknown action '{action}'. Valid values: implement, sync, verify.");
    }

    [McpServerTool]
    [Description("Inlines a symbol by replacing all usages with its definition. kind: method (inline method body at all call sites solution-wide — expression-body or single-return methods only), variable (inline local variable into usages), field (inline field value into usages), parameter (inline a constant parameter into method body — also supply methodName). targetName is the symbol name (parameterName when kind=parameter).")]
    public async Task<object> Inline(string filePath, string targetName, string kind, string? methodName = null)
    {
        if (kind == "method")
        {
            try
            {
                return await _refinementEngine.InlineMethodAsync(filePath, targetName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Inline/method unexpected exception for '{TargetName}' in '{FilePath}'", targetName, filePath);
                throw new InvalidOperationException($"Inline method '{targetName}' in '{filePath}' failed: {ex.GetType().Name}: {ex.Message}", ex);
            }
        }
        if (kind == "variable")
        {
            return await _semanticRefactoringLibrary.InlineVariableAsync(filePath, targetName);
        }
        if (kind == "field")
        {
            return await _granularRefactoringEngine.InlineFieldAsync(filePath, targetName);
        }
        if (kind == "parameter")
        {
            if (string.IsNullOrEmpty(methodName))
            {
                throw new ArgumentException("methodName is required when kind=parameter.");
            }
            return await _granularRefactoringEngine.InlineParameterAsync(filePath, methodName, targetName);
        }
        throw new ArgumentException($"Unknown kind '{kind}'. Valid values: method, variable, field, parameter.");
    }

    [McpServerTool]
    [Description("Adds a new member to a type. containerName: target class/struct/interface. newMemberSource: full member declaration text. position: null or 'end' (append at end), 'after:MemberName' (insert after named member), 'before:MemberName' (insert before named member). Use autoStage=true (default) for ChangeId.")]
    public async Task<object> AddMember(string filePath, string containerName, string newMemberSource, string? position = null, bool autoStage = true)
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
            throw new ArgumentException($"Unknown position '{position}'. Valid values: null, 'end', 'after:MemberName', 'before:MemberName'.");
        }
        if (!autoStage)
        {
            return updated;
        }
        var changes = new Dictionary<string, string> { [filePath] = updated };
        var id = _workspaceManager.StageChanges(changes, description);
        return new PersistentWorkspaceManager.StagedChangeSummary(id, [filePath], description);
    }

    [McpServerTool]
    [Description("Generates a typed member and adds it to a type. kind: property (auto-property) or field. containerName: target class/struct. name: member name. type: C# type. For property: hasSetter (default true), isInit, accessibility (default 'public'). For field: isReadonly, isStatic, initializer (optional expression), accessibility (default 'private'). Use autoStage=true (default) for ChangeId.")]
    public async Task<object> AddMemberTyped(
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
            throw new ArgumentException($"Unknown kind '{kind}'. Valid values: property, field.");
        }
        if (!autoStage)
        {
            return updated;
        }
        var changes = new Dictionary<string, string> { [filePath] = updated };
        var id = _workspaceManager.StageChanges(changes, description);
        return new PersistentWorkspaceManager.StagedChangeSummary(id, [filePath], description);
    }

    [McpServerTool]
    [Description("Wraps a line range. wrapper: tryCatch (wrap in try/catch — name is exceptionType default 'Exception'; also use catchVariableName default 'ex', catchBody optional), using (wrap in using statement — name is disposalName, required), region (wrap in #region — name is region label, required). startLine and endLine are 1-based. Use autoStage=true (default) for ChangeId (tryCatch/region only; using returns content string directly).")]
    public async Task<object> WrapRange(
        string filePath,
        int startLine,
        int endLine,
        string wrapper,
        string? name = null,
        string catchVariableName = "ex",
        string? catchBody = null,
        bool autoStage = true)
    {
        if (wrapper == "tryCatch")
        {
            var exceptionType = name ?? "Exception";
            var updated = await _refactoringEngine.WrapInTryCatchAsync(filePath, startLine, endLine, exceptionType, catchVariableName, catchBody);
            if (!autoStage)
            {
                return updated;
            }
            var changes = new Dictionary<string, string> { [filePath] = updated };
            var id = _workspaceManager.StageChanges(changes, $"Wrap lines {startLine}-{endLine} in try/catch.");
            return new PersistentWorkspaceManager.StagedChangeSummary(id, [filePath], $"Wraps lines {startLine}-{endLine} in a try/{exceptionType} block in {Path.GetFileName(filePath)}.");
        }
        if (wrapper == "using")
        {
            if (string.IsNullOrEmpty(name))
            {
                throw new ArgumentException("name (disposalName) is required when wrapper=using.");
            }
            return await _semanticRefactoringLibrary.WrapInUsingAsync(filePath, startLine, endLine, name);
        }
        if (wrapper == "region")
        {
            if (string.IsNullOrEmpty(name))
            {
                throw new ArgumentException("name (regionName) is required when wrapper=region.");
            }
            var updated = await _refactoringEngine.WrapInRegionAsync(filePath, startLine, endLine, name);
            if (!autoStage)
            {
                return updated;
            }
            var changes = new Dictionary<string, string> { [filePath] = updated };
            var id = _workspaceManager.StageChanges(changes, $"Wrap lines {startLine}-{endLine} in #region '{name}'.");
            return new PersistentWorkspaceManager.StagedChangeSummary(id, [filePath], $"Wraps lines {startLine}-{endLine} in #region '{name}' in {Path.GetFileName(filePath)}.");
        }
        throw new ArgumentException($"Unknown wrapper '{wrapper}'. Valid values: tryCatch, using, region.");
    }

    [McpServerTool]
    [Description("Moves a type to a new location. destination: ownFile (move to its own '.cs' file — returns ChangeId + content previews; use autoStage=false for raw file dict), outerScope (move nested type to containing namespace scope — returns updated file content).")]
    public async Task<object> MoveType(string filePath, string typeName, string destination, bool autoStage = true)
    {
        if (destination == "ownFile")
        {
            var changes = await _refactoringEngine.MoveTypeToFileAsync(filePath, typeName);
            if (!autoStage)
            {
                return changes;
            }
            var id = _workspaceManager.StageChanges(changes, $"Move type '{typeName}' from '{Path.GetFileName(filePath)}'");
            return new
            {
                ChangeId = id,
                Description = $"Moves '{typeName}' to its own file. Call staged_change(action=\"apply\", changeId=\"{id}\") to apply.",
                AffectedFiles = changes.Keys.ToList(),
                ContentPreviews = changes.ToDictionary(
                    kvp => Path.GetFileName(kvp.Key),
                    kvp => PreviewFileContent(kvp.Value)
                )
            };
        }
        if (destination == "outerScope")
        {
            return await _granularRefactoringEngine.MoveTypeToOuterScopeAsync(filePath, typeName);
        }
        throw new ArgumentException($"Unknown destination '{destination}'. Valid values: ownFile, outerScope.");
    }
}
