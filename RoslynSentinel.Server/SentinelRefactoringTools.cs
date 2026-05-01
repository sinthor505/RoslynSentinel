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
        _workspaceManager = workspaceManager;
        _config = config;
        _logger = logger;
    }

    [McpServerTool]
    [Description("Extracts common members from multiple classes into a new shared base class.")]
    public async Task<object> ExtractSuperclass(string[] filePaths, string[] classNames, string newBaseClassName, bool autoStage = true) 
    {
        var changes = await _advancedStructuralEngine.ExtractSuperclassAsync(filePaths, classNames, newBaseClassName);
        if (autoStage) return _workspaceManager.StageChanges(changes, $"Extract superclass '{newBaseClassName}' from {classNames.Length} classes.");
        return changes;
    }

    [McpServerTool]
    [Description("Synchronizes the filename to match the primary type declared in the file.")]
    public async Task<string> SyncTypeAndFilename(string filePath) 
        => await _structuralRefinementEngine.SyncTypeAndFilenameAsync(filePath);

    [McpServerTool]
    [Description("Safe deletes a symbol only if it has zero usages in the entire codebase. Provide contextSnippet: a verbatim substring from the symbol's declaration line. If autoStage is true, returns a ChangeId.")]
    public async Task<object> SafeDeleteSymbol(string filePath, string contextSnippet, bool autoStage = true) 
    {
        var changes = await _refactoringEngine.SafeDeleteSymbolAsync(filePath, contextSnippet);
        if (autoStage) return _workspaceManager.StageChanges(changes, $"Safe delete symbol in '{Path.GetFileName(filePath)}'.");
        return changes;
    }

    [McpServerTool]
    [Description("Inlines a simple single-statement method by replacing call sites with its expression.")]
    public async Task<string> InlineMethod(string filePath, string methodName) 
        => await _refinementEngine.InlineMethodAsync(filePath, methodName);

    [McpServerTool]
    [Description("Reorders parameters in a method signature and updates all call sites globally.")]
    public async Task<object> ChangeSignature(string filePath, string methodName, int[] newParameterOrder, bool autoStage = true) 
    {
        var changes = await _refactoringEngine.ChangeSignatureAsync(filePath, methodName, newParameterOrder);
        if (autoStage) return _workspaceManager.StageChanges(changes, $"Change signature of method '{methodName}' in '{Path.GetFileName(filePath)}'.");
        return changes;
    }

    [McpServerTool]
    [Description("Extracts a block of statements into a new private method. Provide startLine and endLine (1-based, line range only — no column precision needed), the exact physical text of those two lines (used to detect stale files before modifying), and a name for the new method. Data flow analysis automatically determines parameters and return type. Returns BeforeSnippet/CallSiteReplacement/ExtractedMethodText previews plus UpdatedSourceContent for staging.")]
    public async Task<ExtractMethodResult> ExtractMethod(
        string filePath, int startLine, string startLineText, int endLine, string endLineText, string newMethodName)
        => await _refactoringEngine.ExtractMethodAsync(filePath, startLine, startLineText, endLine, endLineText, newMethodName);

    [McpServerTool]
    [Description("Introduces a new private readonly field from an expression. Provide contextSnippet: a verbatim substring from the expression (e.g., the expression text itself or the surrounding assignment). The expression type is inferred from semantics.")]
    public async Task<string> IntroduceField(string filePath, string contextSnippet, string newFieldName) 
        => await _granularRefactoringEngine.IntroduceFieldAsync(filePath, contextSnippet, newFieldName);

    [McpServerTool]
    [Description("Introduces a new method parameter from an expression. Provide contextSnippet: a verbatim substring uniquely identifying the expression. Updates the method signature (single-file only — call sites in other files not updated).")]
    public async Task<string> IntroduceParameter(string filePath, string contextSnippet, string newParamName) 
        => await _granularRefactoringEngine.IntroduceParameterAsync(filePath, contextSnippet, newParamName);

    [McpServerTool]
    [Description("Inlines a private field by replacing all usages with its initializer value.")]
    public async Task<string> InlineField(string filePath, string fieldName) 
        => await _granularRefactoringEngine.InlineFieldAsync(filePath, fieldName);

    [McpServerTool]
    [Description("Inlines a parameter if it has a constant usage across the solution.")]
    public async Task<string> InlineParameter(string filePath, string methodName, string parameterName) 
        => await _granularRefactoringEngine.InlineParameterAsync(filePath, methodName, parameterName);

    [McpServerTool]
    [Description("Makes a method static if it does not access any instance members (fields, properties, methods).")]
    public async Task<string> MakeMethodStatic(string filePath, string methodName) 
        => await _standardRefactoringEngine.MakeMethodStaticAsync(filePath, methodName);

    [McpServerTool]
    [Description("Converts an extension method into a standard static method call.")]
    public async Task<string> ExtensionToStatic(string filePath, string methodName) 
        => await _advancedLogicEngine.ExtensionToStaticAsync(filePath, methodName);

    [McpServerTool]
    [Description("Renames a symbol (class, method, property, field, local, etc.) across the entire solution. " +
                 "Pass 'symbolName' (the exact identifier to rename) and 'contextSnippet' (a verbatim substring from the source file, long enough to appear exactly once — typically the surrounding line or expression). " +
                 "Example: symbolName=\"GetById\", contextSnippet=\"public async Task<Product?> GetById(\". " +
                 "Returns an error if the snippet matches zero or multiple locations. " +
                 "Returns per-file diff hunks (before/after for each changed line with ±2 lines of context) plus a staged ChangeId. Review FileChanges before calling ApplyStagedChanges.")]
    public async Task<object> RenameSymbol(string filePath, string symbolName, string contextSnippet, string newName, bool autoStage = true)
    {
        var result = await _refactoringEngine.RenameSymbolAsync(filePath, symbolName, contextSnippet, newName);
        if (result.Error != null)
            return new { Error = result.Error };
        if (autoStage)
        {
            var id = _workspaceManager.StageChanges(result.PendingChanges, $"Rename '{result.OldName}' to '{result.NewName}'");
            return new { result.OldName, result.NewName, FilesChanged = result.FileChanges.Count, StagingId = id, result.FileChanges };
        }
        return new { result.OldName, result.NewName, FilesChanged = result.FileChanges.Count, result.FileChanges };
    }

    [McpServerTool]
    [Description("Extracts an interface from a class. If autoStage is true, returns a ChangeId.")]
    public async Task<object> ExtractInterface(string filePath, string className, string interfaceName, bool autoStage = true) 
    {
        var changes = await _refactoringEngine.ExtractInterfaceAsync(filePath, className, interfaceName);
        if (autoStage) return _workspaceManager.StageChanges(changes, $"Extract interface '{interfaceName}' from '{className}'.");
        return changes;
    }

    [McpServerTool]
    [Description("Moves a type to its own file. Returns a ChangeId for ApplyStagedChanges plus a ContentPreviews map showing what each affected file will contain after the move. Use autoStage=false to get the full raw file content dictionary instead.")]
    public async Task<object> MoveTypeToFile(string filePath, string typeName, bool autoStage = true) 
    {
        var changes = await _refactoringEngine.MoveTypeToFileAsync(filePath, typeName);
        if (autoStage)
        {
            var id = _workspaceManager.StageChanges(changes, $"Move type '{typeName}' from '{Path.GetFileName(filePath)}'");
            return new
            {
                ChangeId = id,
                Description = $"Moves '{typeName}' to its own file. Call ApplyStagedChanges(\"{id}\") to apply.",
                AffectedFiles = changes.Keys.ToList(),
                ContentPreviews = changes.ToDictionary(
                    kvp => Path.GetFileName(kvp.Key),
                    kvp => kvp.Value.Length > 1500 ? kvp.Value[..1500] + "\n// ... (truncated; use autoStage=false for full content)" : kvp.Value
                )
            };
        }
        return changes;
    }

    [McpServerTool]
    [Description("Converts an abstract class to an interface.")]
    public async Task<string> ConvertAbstractToInterface(string filePath, string className) 
        => await _advancedStructuralEngine.ConvertAbstractClassToInterfaceAsync(filePath, className);

    [McpServerTool]
    [Description("Generates a mapping method between two types.")]
    public async Task<string> GenerateMapping(string filePath, string fromType, string toType) 
        => await _mappingEngine.GenerateMappingAsync(filePath, fromType, toType);

    [McpServerTool]
    [Description("Wraps a range of code in a using statement for an IDisposable object.")]
    public async Task<string> WrapInUsing(string filePath, int startLine, int endLine, string disposalName) 
        => await _semanticRefactoringLibrary.WrapInUsingAsync(filePath, startLine, endLine, disposalName);

    [McpServerTool]
    [Description("Converts an anonymous object creation to a formal named class.")]
    public async Task<Dictionary<string, string>> ConvertAnonymousToNamed(string filePath, string newClassName) 
        => await _advancedTypeEngine.ConvertAnonymousToNamedAsync(filePath, newClassName);

    [McpServerTool]
    [Description("Inlines a class by moving all its members into a target class and removing the original.")]
    public async Task<Dictionary<string, string>> InlineClass(string sourceFilePath, string targetFilePath, string className) 
        => await _advancedStructuralEngine.InlineClassAsync(sourceFilePath, targetFilePath, className);

    [McpServerTool]
    [Description("Introduces a new local variable based on a selected expression and replaces usages. Provide contextSnippet: a verbatim substring uniquely identifying the expression to extract.")]
    public async Task<string> IntroduceVariable(string filePath, string contextSnippet, string newVariableName) 
        => await _granularRefactoringEngine.IntroduceVariableAsync(filePath, contextSnippet, newVariableName);

    [McpServerTool]
    [Description("Inlines a local temporary variable into all its usages within the scope.")]
    public async Task<string> InlineVariable(string filePath, string variableName) 
        => await _semanticRefactoringLibrary.InlineVariableAsync(filePath, variableName);

    [McpServerTool]
    [Description("Converts a property into formal GetX() and SetX() methods.")]
    public async Task<string> ConvertPropertyToMethods(string filePath, string propertyName) 
        => await _codeStyleEngine.ConvertPropertyToMethodsAsync(filePath, propertyName);

    [McpServerTool]
    [Description("Extracts specific members from a class into a new separate class.")]
    public async Task<Dictionary<string, string>> ExtractClass(string filePath, string className, string newClassName, string[] memberNames) 
        => await _advancedStructuralEngine.ExtractClassAsync(filePath, className, newClassName, memberNames);

    [McpServerTool]
    [Description("Extracts specific members from a class into a new partial file.")]
    public async Task<Dictionary<string, string>> ExtractMembersToPartial(string filePath, string className, string[] memberNames) 
        => await _granularRefactoringEngine.ExtractMembersToPartialAsync(filePath, className, memberNames);

    [McpServerTool]
    [Description("Converts a Get(index) method into a formal C# indexer.")]
    public async Task<string> ConvertMethodToIndexer(string filePath, string methodName) 
        => await _granularRefactoringEngine.ConvertMethodToIndexerAsync(filePath, methodName);

    [McpServerTool]
    [Description("Moves a nested type out to the containing namespace scope.")]
    public async Task<string> MoveTypeToOuterScope(string filePath, string typeName)
        => await _granularRefactoringEngine.MoveTypeToOuterScopeAsync(filePath, typeName);

    [McpServerTool]
    [Description("Atomically moves all secondary types in a file to their own files. Returns a ChangeId for ApplyStagedChanges plus first-15-line previews of each affected file's new content.")]
    public async Task<object> MoveAllTypesToFiles(string filePath, bool autoStage = true)
    {
        var changes = await _refactoringEngine.MoveAllTypesToFilesAsync(filePath);
        if (!autoStage) return changes;
        if (changes.Count == 0) return "No secondary types found to move.";
        var id = _workspaceManager.StageChanges(changes, $"Move all types to files in '{Path.GetFileName(filePath)}'");
        return new
        {
            ChangeId = id,
            Description = $"Moves all secondary types in '{Path.GetFileName(filePath)}' to their own files. Call ApplyStagedChanges(\"{id}\") to apply.",
            AffectedFiles = changes.Keys.Select(Path.GetFileName).ToList(),
            ContentPreviews = changes.ToDictionary(
                kvp => Path.GetFileName(kvp.Key)!,
                kvp =>
                {
                    var lines = kvp.Value.Split('\n');
                    return lines.Length > 15
                        ? string.Join("\n", lines.Take(15)) + "\n// ... (truncated)"
                        : kvp.Value;
                }
            )
        };
    }

    [McpServerTool]
    [Description("Atomically moves all secondary types in every file in a project to their own files. If autoStage is true, returns a ChangeId.")]
    public async Task<object> MoveAllTypesToFilesInProject(string projectName, bool autoStage = true)
    {
        var changes = await _refactoringEngine.MoveAllTypesToFilesInProjectAsync(projectName);
        if (!autoStage) return changes;
        if (changes.Count == 0) return $"No secondary types found in project '{projectName}'.";
        var id = _workspaceManager.StageChanges(changes, $"Move all types to files in project '{projectName}'");
        return new PersistentWorkspaceManager.StagedChangeSummary(id, changes.Keys.ToList(), $"Moves all secondary types in project '{projectName}' to their own files.");
    }

    [McpServerTool]
    [Description("Atomically moves all secondary types in every file across the entire solution to their own files. If autoStage is true, returns a ChangeId.")]
    public async Task<object> MoveAllTypesToFilesInSolution(bool autoStage = true)
    {
        var changes = await _refactoringEngine.MoveAllTypesToFilesInSolutionAsync();
        if (!autoStage) return changes;
        if (changes.Count == 0) return "No secondary types found in solution.";
        var id = _workspaceManager.StageChanges(changes, "Move all types to files in solution");
        return new PersistentWorkspaceManager.StagedChangeSummary(id, changes.Keys.ToList(), "Moves all secondary types across the entire solution to their own files.");
    }

    [McpServerTool]
    [Description("Surgically replaces a specific member (method, property, class) in a file by name with new source code.")]
    public async Task<string> ReplaceMember(string filePath, string memberName, string newSource) 
        => await _refactoringEngine.ReplaceMemberAsync(filePath, memberName, newSource);

    [McpServerTool]
    [Description("Adds a new member (method, field, etc.) to an existing class, interface, record, or struct.")]
    public async Task<string> AddMemberToClass(string filePath, string containerName, string newMemberSource) 
        => await _refactoringEngine.AddMemberAsync(filePath, containerName, newMemberSource);

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
        if (!autoStage) return updated;
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
        if (!autoStage) return updated;
        var changes = new Dictionary<string, string> { [filePath] = updated };
        var id = _workspaceManager.StageChanges(changes, $"Add enum value '{valueName}' to '{enumName}'.");
        return new PersistentWorkspaceManager.StagedChangeSummary(id, [filePath], $"Adds '{valueName}' to enum '{enumName}' in {Path.GetFileName(filePath)}.");
    }

    [McpServerTool]
    [Description("""
        Inserts a new member immediately after a named member in a type declaration.
        
        Finds the container type by name, then locates the member named afterMemberName and inserts
        newMemberSource directly after it (e.g. insert a new method after an existing method).
        If afterMemberName is not found, the new member is appended at the end of the type.
        Use autoStage=true (default) to get a ChangeId for ApplyStagedChanges.
        """)]
    public async Task<object> InsertMemberAfter(string filePath, string containerName, string afterMemberName, string newMemberSource, bool autoStage = true)
    {
        var updated = await _refactoringEngine.InsertMemberAfterAsync(filePath, containerName, afterMemberName, newMemberSource);
        if (!autoStage) return updated;
        var changes = new Dictionary<string, string> { [filePath] = updated };
        var id = _workspaceManager.StageChanges(changes, $"Insert member after '{afterMemberName}' in '{containerName}'.");
        return new PersistentWorkspaceManager.StagedChangeSummary(id, [filePath], $"Inserts new member after '{afterMemberName}' in '{containerName}' in {Path.GetFileName(filePath)}.");
    }

    [McpServerTool]
    [Description("""
        Inserts a new member immediately before a named member in a type declaration.
        
        Finds the container type by name, then locates the member named beforeMemberName and inserts
        newMemberSource directly before it (e.g. insert a field before a constructor).
        If beforeMemberName is not found, the new member is appended at the end of the type.
        Use autoStage=true (default) to get a ChangeId for ApplyStagedChanges.
        """)]
    public async Task<object> InsertMemberBefore(string filePath, string containerName, string beforeMemberName, string newMemberSource, bool autoStage = true)
    {
        var updated = await _refactoringEngine.InsertMemberBeforeAsync(filePath, containerName, beforeMemberName, newMemberSource);
        if (!autoStage) return updated;
        var changes = new Dictionary<string, string> { [filePath] = updated };
        var id = _workspaceManager.StageChanges(changes, $"Insert member before '{beforeMemberName}' in '{containerName}'.");
        return new PersistentWorkspaceManager.StagedChangeSummary(id, [filePath], $"Inserts new member before '{beforeMemberName}' in '{containerName}' in {Path.GetFileName(filePath)}.");
    }

    [McpServerTool]
    [Description("""
        Adds an attribute to a named type or member.
        
        targetName is the class name or method/property name to decorate.
        attributeSource is the attribute text with or without brackets (e.g. "[ApiController]" or "Required").
        Complex attributes with arguments are supported (e.g. "[HttpGet(\"/api/items\")]").
        Use autoStage=true (default) to get a ChangeId for ApplyStagedChanges.
        """)]
    public async Task<object> AddAttribute(string filePath, string targetName, string attributeSource, bool autoStage = true)
    {
        var updated = await _refactoringEngine.AddAttributeAsync(filePath, targetName, attributeSource);
        if (!autoStage) return updated;
        var changes = new Dictionary<string, string> { [filePath] = updated };
        var id = _workspaceManager.StageChanges(changes, $"Add attribute '{attributeSource}' to '{targetName}'.");
        return new PersistentWorkspaceManager.StagedChangeSummary(id, [filePath], $"Adds '{attributeSource}' to '{targetName}' in {Path.GetFileName(filePath)}.");
    }

    [McpServerTool]
    [Description("""
        Adds a base type or interface to a type declaration.
        
        typeName is the class/record/struct/interface to modify; baseTypeName is what to add
        (e.g. "IDisposable", "BaseController", "IMyService<string>").
        If the type already inherits or implements baseTypeName, the file is returned unchanged.
        Use autoStage=true (default) to get a ChangeId for ApplyStagedChanges.
        """)]
    public async Task<object> AddBaseType(string filePath, string typeName, string baseTypeName, bool autoStage = true)
    {
        var updated = await _refactoringEngine.AddBaseTypeAsync(filePath, typeName, baseTypeName);
        if (!autoStage) return updated;
        var changes = new Dictionary<string, string> { [filePath] = updated };
        var id = _workspaceManager.StageChanges(changes, $"Add base type '{baseTypeName}' to '{typeName}'.");
        return new PersistentWorkspaceManager.StagedChangeSummary(id, [filePath], $"Adds '{baseTypeName}' to the base list of '{typeName}' in {Path.GetFileName(filePath)}.");
    }

    [McpServerTool]
    [Description("Replaces a class constructor with a private constructor plus a public static Create() factory method.")]
    public async Task<string> ReplaceConstructorWithFactory(string filePath, string className)
        => await _advancedStructuralEngine.ReplaceConstructorWithFactoryAsync(filePath, className);

    [McpServerTool]
    [Description("Swaps left and right sides of all assignment statements within a line range.")]
    public async Task<string> InvertAssignments(string filePath, int startLine, int endLine)
        => await _mappingEngine.InvertAssignmentsAsync(filePath, startLine, endLine);

    [McpServerTool]
    [Description("Inverts a single-branch if statement to an early-return guard clause, reducing nesting depth.")]
    public async Task<string> ReduceBlockDepth(string filePath, string methodName)
        => await _codeFlowEngine.ReduceBlockDepthAsync(filePath, methodName);

    [McpServerTool]
    [Description("Adds .ConfigureAwait(false) to all await expressions in a file for library-safe async code.")]
    public async Task<string> OptimizeTaskWait(string filePath)
        => await _advancedRefactoringEngine.OptimizeTaskWaitAsync(filePath);

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
        if (!autoStage) return changes;
        if (changes.Count == 0) return $"Member '{memberName}' not found or no accessible base class available.";
        var id = _workspaceManager.StageChanges(changes, $"Pull up '{memberName}' from '{className}' to base class.");
        return new PersistentWorkspaceManager.StagedChangeSummary(id, changes.Keys.ToList(), $"Pulls '{memberName}' from '{className}' up to its base class.");
    }

    [McpServerTool]
    [Description("""
        Removes a named attribute from a type or member.
        
        targetName is the class/method/property name to modify.
        attributeName is the attribute to remove, with or without the 'Attribute' suffix and brackets
        (e.g. "Obsolete", "ObsoleteAttribute", or "[Obsolete]" — all match [Obsolete]).
        Use autoStage=true (default) to get a ChangeId for ApplyStagedChanges.
        """)]
    public async Task<object> RemoveAttribute(string filePath, string targetName, string attributeName, bool autoStage = true)
    {
        var updated = await _refactoringEngine.RemoveAttributeAsync(filePath, targetName, attributeName);
        if (!autoStage) return updated;
        var changes = new Dictionary<string, string> { [filePath] = updated };
        var id = _workspaceManager.StageChanges(changes, $"Remove attribute '{attributeName}' from '{targetName}'.");
        return new PersistentWorkspaceManager.StagedChangeSummary(id, [filePath], $"Removes '{attributeName}' from '{targetName}' in {Path.GetFileName(filePath)}.");
    }

    [McpServerTool]
    [Description("""
        Removes a base type or interface from a type declaration.
        
        typeName is the class/record/struct/interface to modify.
        baseTypeName is the base type or interface to remove (e.g. "IDisposable", "BaseController").
        If the type has no base list or does not implement/inherit baseTypeName, the file is returned unchanged.
        Use autoStage=true (default) to get a ChangeId for ApplyStagedChanges.
        """)]
    public async Task<object> RemoveBaseType(string filePath, string typeName, string baseTypeName, bool autoStage = true)
    {
        var updated = await _refactoringEngine.RemoveBaseTypeAsync(filePath, typeName, baseTypeName);
        if (!autoStage) return updated;
        var changes = new Dictionary<string, string> { [filePath] = updated };
        var id = _workspaceManager.StageChanges(changes, $"Remove base type '{baseTypeName}' from '{typeName}'.");
        return new PersistentWorkspaceManager.StagedChangeSummary(id, [filePath], $"Removes '{baseTypeName}' from the base list of '{typeName}' in {Path.GetFileName(filePath)}.");
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
        if (!autoStage) return updated;
        var changes = new Dictionary<string, string> { [filePath] = updated };
        var id = _workspaceManager.StageChanges(changes, $"Change accessibility of '{targetName}' to '{accessibility}'.");
        return new PersistentWorkspaceManager.StagedChangeSummary(id, [filePath], $"Changes accessibility of '{targetName}' to '{accessibility}' in {Path.GetFileName(filePath)}.");
    }

    [McpServerTool]
    [Description("""
        Adds a modifier keyword to a type or member (idempotent).
        
        targetName is the class/method/property name to modify.
        modifier is the keyword to add: virtual, abstract, sealed, static, readonly, override,
        partial, async, new, extern, unsafe, volatile.
        Use autoStage=true (default) to get a ChangeId for ApplyStagedChanges.
        """)]
    public async Task<object> AddModifier(string filePath, string targetName, string modifier, bool autoStage = true)
    {
        var updated = await _refactoringEngine.AddModifierAsync(filePath, targetName, modifier);
        if (!autoStage) return updated;
        var changes = new Dictionary<string, string> { [filePath] = updated };
        var id = _workspaceManager.StageChanges(changes, $"Add '{modifier}' modifier to '{targetName}'.");
        return new PersistentWorkspaceManager.StagedChangeSummary(id, [filePath], $"Adds '{modifier}' to '{targetName}' in {Path.GetFileName(filePath)}.");
    }

    [McpServerTool]
    [Description("""
        Removes a modifier keyword from a type or member (idempotent).
        
        targetName is the class/method/property name to modify.
        modifier is the keyword to remove: virtual, abstract, sealed, static, readonly, override,
        partial, async, new, extern, unsafe, volatile.
        Use autoStage=true (default) to get a ChangeId for ApplyStagedChanges.
        """)]
    public async Task<object> RemoveModifier(string filePath, string targetName, string modifier, bool autoStage = true)
    {
        var updated = await _refactoringEngine.RemoveModifierAsync(filePath, targetName, modifier);
        if (!autoStage) return updated;
        var changes = new Dictionary<string, string> { [filePath] = updated };
        var id = _workspaceManager.StageChanges(changes, $"Remove '{modifier}' modifier from '{targetName}'.");
        return new PersistentWorkspaceManager.StagedChangeSummary(id, [filePath], $"Removes '{modifier}' from '{targetName}' in {Path.GetFileName(filePath)}.");
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
        if (!autoStage) return updated;
        var changes = new Dictionary<string, string> { [filePath] = updated };
        var id = _workspaceManager.StageChanges(changes, $"Add summary comment to '{targetName}'.");
        return new PersistentWorkspaceManager.StagedChangeSummary(id, [filePath], $"Adds XML summary comment to '{targetName}' in {Path.GetFileName(filePath)}.");
    }

    [McpServerTool]
    [Description("""
        Generates an auto-property and adds it to a type.
        
        containerName is the class/record/struct to modify; propertyName and propertyType specify the property.
        accessibility defaults to "public". hasSetter=true generates { get; set; }; false generates { get; }.
        isInit=true generates { get; init; } (requires hasSetter=true).
        Use autoStage=true (default) to get a ChangeId for ApplyStagedChanges.
        """)]
    public async Task<object> AddProperty(string filePath, string containerName, string propertyName, string propertyType, string accessibility = "public", bool hasSetter = true, bool isInit = false, bool autoStage = true)
    {
        var updated = await _refactoringEngine.AddPropertyAsync(filePath, containerName, propertyName, propertyType, accessibility, hasSetter, isInit);
        if (!autoStage) return updated;
        var changes = new Dictionary<string, string> { [filePath] = updated };
        var id = _workspaceManager.StageChanges(changes, $"Add property '{propertyName}' to '{containerName}'.");
        return new PersistentWorkspaceManager.StagedChangeSummary(id, [filePath], $"Adds '{propertyType} {propertyName}' property to '{containerName}' in {Path.GetFileName(filePath)}.");
    }

    [McpServerTool]
    [Description("""
        Generates a field declaration and adds it to a type.
        
        containerName is the class/struct to modify; fieldName and fieldType specify the field.
        accessibility defaults to "private". isReadonly and isStatic add those modifiers.
        initializer is an optional value expression (e.g. "42" or "new List<string>()").
        Use autoStage=true (default) to get a ChangeId for ApplyStagedChanges.
        """)]
    public async Task<object> AddField(string filePath, string containerName, string fieldName, string fieldType, string accessibility = "private", bool isReadonly = false, bool isStatic = false, string? initializer = null, bool autoStage = true)
    {
        var updated = await _refactoringEngine.AddFieldAsync(filePath, containerName, fieldName, fieldType, accessibility, isReadonly, isStatic, initializer);
        if (!autoStage) return updated;
        var changes = new Dictionary<string, string> { [filePath] = updated };
        var id = _workspaceManager.StageChanges(changes, $"Add field '{fieldName}' to '{containerName}'.");
        return new PersistentWorkspaceManager.StagedChangeSummary(id, [filePath], $"Adds '{fieldType} {fieldName}' field to '{containerName}' in {Path.GetFileName(filePath)}.");
    }

    [McpServerTool]
    [Description("""
        Reorders members of a class/struct/record by convention: fields, constructors, destructors,
        properties, indexers, events, methods, operators, nested types.
        
        Within each category: static members come before instance members, then alphabetical by name.
        containerName is the type whose members to sort.
        Use autoStage=true (default) to get a ChangeId for ApplyStagedChanges.
        """)]
    public async Task<object> SortMembers(string filePath, string containerName, bool autoStage = true)
    {
        var updated = await _refactoringEngine.SortMembersAsync(filePath, containerName);
        if (!autoStage) return updated;
        var changes = new Dictionary<string, string> { [filePath] = updated };
        var id = _workspaceManager.StageChanges(changes, $"Sort members of '{containerName}'.");
        return new PersistentWorkspaceManager.StagedChangeSummary(id, [filePath], $"Sorts members of '{containerName}' by convention in {Path.GetFileName(filePath)}.");
    }

    [McpServerTool]
    [Description("""
        Wraps a range of statements in a try/catch block.
        
        startLine and endLine are 1-based line numbers of the statements to wrap.
        exceptionType defaults to "Exception"; catchVariableName defaults to "ex".
        catchBody is an optional statement for the catch block body (e.g. "_logger.LogError(ex, \"msg\");").
        Use autoStage=true (default) to get a ChangeId for ApplyStagedChanges.
        """)]
    public async Task<object> WrapInTryCatch(string filePath, int startLine, int endLine, string exceptionType = "Exception", string catchVariableName = "ex", string? catchBody = null, bool autoStage = true)
    {
        var updated = await _refactoringEngine.WrapInTryCatchAsync(filePath, startLine, endLine, exceptionType, catchVariableName, catchBody);
        if (!autoStage) return updated;
        var changes = new Dictionary<string, string> { [filePath] = updated };
        var id = _workspaceManager.StageChanges(changes, $"Wrap lines {startLine}-{endLine} in try/catch.");
        return new PersistentWorkspaceManager.StagedChangeSummary(id, [filePath], $"Wraps lines {startLine}-{endLine} in a try/{exceptionType} block in {Path.GetFileName(filePath)}.");
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
        if (!autoStage) return updated;
        var changes = new Dictionary<string, string> { [filePath] = updated };
        var id = _workspaceManager.StageChanges(changes, $"Add constructor parameter '{paramName}' to '{className}'.");
        return new PersistentWorkspaceManager.StagedChangeSummary(id, [filePath], $"Adds '{paramType} {paramName}' DI parameter to '{className}' in {Path.GetFileName(filePath)}.");
    }

    [McpServerTool]
    [Description("""
        Wraps a line range with #region / #endregion preprocessor directives.
        
        startLine and endLine are 1-based line numbers. regionName is the label after #region.
        This uses text manipulation rather than AST transformation.
        Use autoStage=true (default) to get a ChangeId for ApplyStagedChanges.
        """)]
    public async Task<object> WrapInRegion(string filePath, int startLine, int endLine, string regionName, bool autoStage = true)
    {
        var updated = await _refactoringEngine.WrapInRegionAsync(filePath, startLine, endLine, regionName);
        if (!autoStage) return updated;
        var changes = new Dictionary<string, string> { [filePath] = updated };
        var id = _workspaceManager.StageChanges(changes, $"Wrap lines {startLine}-{endLine} in #region '{regionName}'.");
        return new PersistentWorkspaceManager.StagedChangeSummary(id, [filePath], $"Wraps lines {startLine}-{endLine} in #region '{regionName}' in {Path.GetFileName(filePath)}.");
    }

    [McpServerTool]
    [Description("Adds to the interface any public methods or properties that exist in the class but are missing from the interface. Finds the interface in the same file or anywhere in the solution. Returns the updated source of the file containing the interface (prefixed with '// Updated file: path' if it differs from the class file).")]
    public async Task<string> SyncInterfaceToImplementation(string filePath, string className, string interfaceName)
        => await _refactoringEngine.SyncInterfaceToImplementationAsync(filePath, className, interfaceName);

    [McpServerTool]
    [Description("Encapsulates method parameters into a new C# 12 record type. Groups all non-CancellationToken parameters (or only those specified in parameterNames) into a 'public record {NewTypeName}(...)'. Rewrites parameter references in the method body to 'request.PropertyName'. Appends the record to the end of the file. Adds a TODO comment in the method body reminding to update call sites.")]
    public async Task<string> IntroduceParameterObject(
        string filePath,
        string methodName,
        string? newTypeName = null,
        string[]? parameterNames = null)
        => await _granularRefactoringEngine.IntroduceParameterObjectAsync(filePath, methodName, newTypeName, parameterNames);

    [McpServerTool]
    [Description("Regenerates XML doc param/returns tags to match the current method signature: adds tags for new parameters, removes tags for deleted parameters, and adds a <returns> tag if missing on a non-void method.")]
    public async Task<string> UpdateXmlDocsFromSignature(string filePath, string methodName)
        => await _refactoringEngine.UpdateXmlDocsFromSignatureAsync(filePath, methodName);

    [McpServerTool]
    [Description("Converts a method or property between expression body (=>) and block body forms. " +
                 "direction: 'ToExpressionBody' or 'ToBlockBody'. " +
                 "memberName: the method/property name. " +
                 "contextSnippet: optional verbatim substring to disambiguate overloads. " +
                 "Returns the updated file content.")]
    public async Task<string> ConvertExpressionBody(string filePath, string memberName, string direction, string? contextSnippet = null)
        => await _refactoringEngine.ConvertExpressionBodyAsync(filePath, memberName, direction, contextSnippet);

    [McpServerTool]
    [Description("Extracts a literal expression to a named constant in the containing type. " +
                 "contextSnippet: a verbatim substring containing or surrounding the literal (e.g., '= 42;' or '\"hello world\"'). " +
                 "constantName: the name for the new constant. " +
                 "visibility: 'private' (default), 'protected', 'internal', or 'public'. " +
                 "Returns the updated file content.")]
    public async Task<string> ExtractConstant(string filePath, string contextSnippet, string constantName, string visibility = "private")
        => await _refactoringEngine.ExtractConstantAsync(filePath, contextSnippet, constantName, visibility);

    [McpServerTool]
    [Description("Analyzes control flow of a method: shows whether it always/sometimes/never returns, lists all return points, and identifies exit paths (throws, breaks, continues). " +
                 "methodName: the method to analyze. " +
                 "contextSnippet: optional snippet to disambiguate overloads. " +
                 "Returns: AlwaysReturns, SometimesReturns, ReturnStatements, ThrowStatements, HasInfiniteLoop.")]
    public async Task<ControlFlowSummary> AnalyzeControlFlow(string filePath, string methodName, string? contextSnippet = null)
        => await _refactoringEngine.AnalyzeControlFlowAsync(filePath, methodName, contextSnippet);

    [McpServerTool]
    [Description("Analyzes data flow of a method body: identifies variables read before assignment, variables that flow out, write-only variables (possible bugs), and captured closure variables. " +
                 "methodName: the method to analyze. " +
                 "contextSnippet: optional snippet to disambiguate overloads. " +
                 "Returns: ReadBeforeAssignment, WrittenInside, ReadInside, CapturedVariables, DataFlowWarnings.")]
    public async Task<DataFlowSummary> AnalyzeDataFlow(string filePath, string methodName, string? contextSnippet = null)
        => await _refactoringEngine.AnalyzeDataFlowAsync(filePath, methodName, contextSnippet);

    [McpServerTool]
    [Description("""
        Returns a preview of what format_document would change without applying the changes.
        Shows each changed line range with ±3 lines of context (similar to a unified diff).
        Returns Changed=false and an empty Hunks list if the file is already formatted correctly.
        Use this to inspect formatting changes before committing to format_document.
        filePath: path to the .cs file to preview.
        """)]
    public async Task<FormatPreviewResult> FormatDocumentPreview(string filePath)
        => await _refactoringEngine.FormatDocumentPreviewAsync(filePath);
}
