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
    [Description("Safe deletes a symbol only if it has zero usages in the entire codebase. If autoStage is true, returns a ChangeId.")]
    public async Task<object> SafeDeleteSymbol(string filePath, int line, int column, bool autoStage = true) 
    {
        var changes = await _refactoringEngine.SafeDeleteSymbolAsync(filePath, line, column);
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
    [Description("Introduces a new private field based on a local expression.")]
    public async Task<string> IntroduceField(string filePath, int line, int column, string newFieldName) 
        => await _granularRefactoringEngine.IntroduceFieldAsync(filePath, line, column, newFieldName);

    [McpServerTool]
    [Description("Introduces a new parameter to a method based on an internal expression and updates call sites.")]
    public async Task<string> IntroduceParameter(string filePath, int line, int column, string newParamName) 
        => await _granularRefactoringEngine.IntroduceParameterAsync(filePath, line, column, newParamName);

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
    [Description("Moves a type to its own file. If autoStage is true, returns a ChangeId instead of full content.")]
    public async Task<object> MoveTypeToFile(string filePath, string typeName, bool autoStage = true) 
    {
        var changes = await _refactoringEngine.MoveTypeToFileAsync(filePath, typeName);
        if (autoStage)
        {
            var id = _workspaceManager.StageChanges(changes, $"Move type '{typeName}' from '{Path.GetFileName(filePath)}'");
            return new PersistentWorkspaceManager.StagedChangeSummary(id, changes.Keys.ToList(), $"Moves '{typeName}' to its own file.");
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
    [Description("Introduces a new local variable based on a selected expression and replaces usages.")]
    public async Task<string> IntroduceVariable(string filePath, int line, int column, string newVariableName) 
        => await _granularRefactoringEngine.IntroduceVariableAsync(filePath, line, column, newVariableName);

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
    [Description("Atomically moves all secondary types in a file to their own files. If autoStage is true, returns a ChangeId.")]
    public async Task<object> MoveAllTypesToFiles(string filePath, bool autoStage = true)
    {
        var changes = await _refactoringEngine.MoveAllTypesToFilesAsync(filePath);
        if (!autoStage) return changes;
        if (changes.Count == 0) return "No secondary types found to move.";
        var id = _workspaceManager.StageChanges(changes, $"Move all types to files in '{Path.GetFileName(filePath)}'");
        return new PersistentWorkspaceManager.StagedChangeSummary(id, changes.Keys.ToList(), $"Moves all secondary types in '{Path.GetFileName(filePath)}' to their own files.");
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
    [Description("Adds a new member (method, field, etc.) to an existing class or interface.")]
    public async Task<string> AddMemberToClass(string filePath, string containerName, string newMemberSource) 
        => await _refactoringEngine.AddMemberAsync(filePath, containerName, newMemberSource);

    [McpServerTool]
    [Description("Removes a specific member from a class or interface by name.")]
    public async Task<string> RemoveMember(string filePath, string memberName) 
        => await _refactoringEngine.RemoveMemberAsync(filePath, memberName);

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
}
