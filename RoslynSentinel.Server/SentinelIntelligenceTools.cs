using System.ComponentModel;
using System.IO;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace RoslynSentinel.Server;

[McpServerToolType]
public class SentinelIntelligenceTools
{
    private readonly ImpactAnalyzer _impactAnalyzer;
    private readonly SemanticSearchEngine _semanticSearchEngine;
    private readonly MetricsEngine _metricsEngine;
    private readonly InventoryEngine _inventoryEngine;
    private readonly DeadCodeEngine _deadCodeEngine;
    private readonly AnalysisEngine _analysisEngine;
    private readonly DocumentationEngine _documentationEngine;
    private readonly DependencyEngine _dependencyEngine;
    private readonly ProjectStructureEngine _projectStructureEngine;
    private readonly AsyncSafetyEngine _asyncSafetyEngine;
    private readonly HealthOrchestrationEngine _healthOrchestrationEngine;
    private readonly ArchitecturalEngine _architecturalEngine;
    private readonly SymbolNavigationEngine _symbolNavigationEngine;
    private readonly DependencyInjectionEngine _dependencyInjectionEngine;
    private readonly DiscoveryEngine _discoveryEngine;
    private readonly ILogger<SentinelIntelligenceTools> _logger;

    public SentinelIntelligenceTools(
        ImpactAnalyzer impactAnalyzer,
        SemanticSearchEngine semanticSearchEngine,
        MetricsEngine metricsEngine,
        InventoryEngine inventoryEngine,
        DeadCodeEngine deadCodeEngine,
        AnalysisEngine analysisEngine,
        DocumentationEngine documentationEngine,
        DependencyEngine dependencyEngine,
        ProjectStructureEngine projectStructureEngine,
        AsyncSafetyEngine asyncSafetyEngine,
        HealthOrchestrationEngine healthOrchestrationEngine,
        ArchitecturalEngine architecturalEngine,
        SymbolNavigationEngine symbolNavigationEngine,
        DependencyInjectionEngine dependencyInjectionEngine,
        DiscoveryEngine discoveryEngine,
        SentinelConfiguration config,
        ILogger<SentinelIntelligenceTools> logger)
    {
        _impactAnalyzer = impactAnalyzer;
        _semanticSearchEngine = semanticSearchEngine;
        _metricsEngine = metricsEngine;
        _inventoryEngine = inventoryEngine;
        _deadCodeEngine = deadCodeEngine;
        _analysisEngine = analysisEngine;
        _documentationEngine = documentationEngine;
        _dependencyEngine = dependencyEngine;
        _projectStructureEngine = projectStructureEngine;
        _asyncSafetyEngine = asyncSafetyEngine;
        _healthOrchestrationEngine = healthOrchestrationEngine;
        _architecturalEngine = architecturalEngine;
        _symbolNavigationEngine = symbolNavigationEngine;
        _dependencyInjectionEngine = dependencyInjectionEngine;
        _discoveryEngine = discoveryEngine;
        _logger = logger;
    }

    [McpServerTool]
    [Description("Generates a paged comprehensive health report. engines: Structure, Modernization, Performance, Safety, Architecture. offset/limit for project paging.")]
    public async Task<ComprehensiveHealthReport> GetComprehensiveHealthReport(
        List<HealthEngineType>? engines = null,
        string? projectName = null,
        string? filePath = null,
        int offset = 0,
        int limit = 10,
        int timeoutSeconds = 25) 
        => await _healthOrchestrationEngine.GenerateComprehensiveHealthReportAsync(engines, projectName, filePath, offset, limit, timeoutSeconds);

    [McpServerTool]
    [Description("Gets the blast radius (impact analysis) of changing a symbol. Provide contextSnippet: a verbatim substring from the symbol's declaration or reference (e.g., 'public async Task<T> GetById('). Provide lineBefore and/or lineAfter when the snippet could match multiple locations. Returns all call sites and affected projects.")]
    public async Task<ImpactReport> GetBlastRadius(string filePath, string contextSnippet, string? lineBefore = null, string? lineAfter = null) 
        => await _impactAnalyzer.AnalyzeImpactAsync(filePath, contextSnippet, lineBefore, lineAfter);

    [McpServerTool]
    [Description("Finds all methods in the solution that return a specific type.")]
    public async Task<List<SearchResult>> FindMethodsByReturnType(string returnType) 
        => await _semanticSearchEngine.FindMethodsByReturnTypeAsync(returnType);

    [McpServerTool]
    [Description("Gets deep metrics for the entire solution or a specific project.")]
    public async Task<SolutionMetrics> GetSolutionMetrics(string? projectName = null) => await _metricsEngine.GetSolutionMetricsAsync(projectName);

    [McpServerTool]
    [Description("Generates a structured report of all namespaces, classes, methods, and properties in a file.")]
    public async Task<CodeInventoryReport> GetCodeInventory(string filePath) => await _inventoryEngine.GetCodeInventoryAsync(filePath);

    [McpServerTool]
    [Description("Finds all unused private members in a class.")]
    public async Task<List<DeadCodeReport>> FindUnusedPrivateMembers(string filePath, string className) 
        => await _deadCodeEngine.FindUnusedPrivateMembersAsync(filePath, className);

    [McpServerTool]
    [Description("Detects private fields that are never read or written in the file.")]
    public async Task<List<DeadCodeReport>> DetectUnusedPrivateFields(string filePath) 
        => await _deadCodeEngine.DetectUnusedPrivateFieldsAsync(filePath);

    [McpServerTool]
    [Description("Identifies local variables that are declared but never used within their scope.")]
    public async Task<List<DeadCodeReport>> DetectUnusedLocalVariables(string filePath) 
        => await _deadCodeEngine.DetectUnusedLocalVariablesAsync(filePath);

    [McpServerTool]
    [Description("Detects methods with too many parameters and suggests a Parameter Object, optionally filtered by project.")]
    public async Task<List<string>> DetectLongParameterLists(int threshold = 5, string? projectName = null) 
        => await _analysisEngine.DetectLongParameterListsAsync(threshold, projectName);

    [McpServerTool]
    [Description("Identifies classes that are never instantiated across the entire solution or a specific project.")]
    public async Task<List<string>> FindUninstantiatedTypes(string? projectName = null) 
        => await _analysisEngine.FindUninstantiatedTypesAsync(projectName);


    [McpServerTool]
    [Description("Identifies circular project references (A -> B -> A).")]
    public async Task<List<string>> FindCircularDependencies() 
        => await _analysisEngine.FindCircularDependenciesAsync();

    [McpServerTool]
    [Description("Generates a markdown call tree for a specific method.")]
    public async Task<string> GenerateCallTree(string filePath, string methodName, int depth = 3) 
        => await _analysisEngine.GenerateCallTreeAsync(filePath, methodName, depth);

    [McpServerTool]
    [Description("Adds comprehensive [Description] comments to all fields in a POCO class.")]
    public async Task<string> DocumentPocoFields(string filePath, string className) 
        => await _documentationEngine.DocumentPocoFieldsAsync(filePath, className);

    [McpServerTool]
    [Description("Generates Equals and GetHashCode overrides for a class.")]
    public async Task<string> GenerateEqualityOverrides(string filePath, string className) 
        => await _analysisEngine.GenerateEqualityOverridesAsync(filePath, className);

    [McpServerTool]
    [Description("Identifies NuGet package references in a project that are not being used.")]
    public async Task<List<string>> FindUnusedReferences(string projectName) 
        => await _dependencyEngine.FindUnusedReferencesAsync(projectName);

    [McpServerTool]
    [Description("Checks for NuGet package version inconsistencies across multiple projects.")]
    public async Task<List<string>> CheckPackageInconsistency() 
        => await _dependencyEngine.CheckPackageInconsistencyAsync();

    [McpServerTool]
    [Description("Identifies interfaces that are declared but never implemented in the solution or a specific project.")]
    public async Task<List<string>> FindUnusedInterfaces(string? projectName = null) 
        => await _analysisEngine.FindUnusedInterfacesAsync(projectName);

    [McpServerTool]
    [Description("Identifies internal classes that are only used in a single file and could be made private, optionally filtered by project.")]
    public async Task<List<string>> FindInternalClassesThatCouldBePrivate(string? projectName = null) 
        => await _analysisEngine.FindInternalClassesThatCouldBePrivateAsync(projectName);

    [McpServerTool]
    [Description("Finds switch statements with a large number of cases that may need refactoring, optionally filtered by project.")]
    public async Task<List<string>> FindLargeSwitchStatements(int threshold = 10, string? projectName = null) 
        => await _analysisEngine.FindLargeSwitchStatementsAsync(threshold, projectName);
[McpServerTool]
[Description("Scans the solution for structural issues with granular filtering. typeFilter options: All, MultiType, NameMismatch.")]
public async Task<List<string>> FindStructuralSmells(
    ProjectStructureEngine.StructuralSmellType typeFilter = ProjectStructureEngine.StructuralSmellType.All,
    string? projectName = null,
    string? filePath = null)
{
    return await _projectStructureEngine.FindStructuralSmellsAsync(typeFilter, projectName, filePath);
}

    [McpServerTool]
    [Description("Identifies constructors that are never called in the entire solution.")]
    public async Task<List<DeadCodeReport>> FindUnusedConstructors(string filePath)
    {
        try
        {
            return await _deadCodeEngine.FindUnusedConstructorsAsync(filePath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "FindUnusedConstructors unexpected exception for '{FilePath}'", filePath);
            throw new InvalidOperationException($"FindUnusedConstructors for '{filePath}' failed: {ex.GetType().Name}: {ex.Message}", ex);
        }
    }

    [McpServerTool]
    [Description("Scans a file for event subscriptions that are never unsubscribed, potential memory leaks.")]
    public async Task<List<DeadCodeReport>> CheckForUnusedEventSubscriptions(string filePath)
        => await _deadCodeEngine.CheckForUnusedEventSubscriptionsAsync(filePath);

    [McpServerTool]
    [Description("Gets deep metadata for a symbol: type, kind, accessibility, attributes, documentation. Provide contextSnippet: a verbatim substring identifying the symbol usage or declaration. Provide lineBefore and/or lineAfter when the snippet could match multiple locations.")]
    public async Task<SymbolHoverInfo> GetSymbolInfo(string filePath, string contextSnippet, string? lineBefore = null, string? lineAfter = null)
        => await _symbolNavigationEngine.GetSymbolInfoAsync(filePath, contextSnippet, lineBefore, lineAfter);

    [McpServerTool]
    [Description("Finds all types that implement an interface or derive from a class, returning file path and line for each. Optionally scoped to a single project.")]
    public async Task<List<ImplementationInfo>> FindAllImplementations(string typeName, string? projectName = null)
        => await _symbolNavigationEngine.FindAllImplementationsAsync(typeName, projectName);

    [McpServerTool]
    [Description("Finds private, non-readonly fields that are only ever assigned inside constructors and could safely be marked readonly.")]
    public async Task<List<ReadonlyFieldCandidate>> FindReadonlyFieldCandidates(string filePath)
        => await _symbolNavigationEngine.FindReadonlyFieldCandidatesAsync(filePath);

    [McpServerTool]
    [Description("Scans for all DI registrations (AddSingleton/AddScoped/AddTransient) across the solution or in a specific project/file. Returns service type, implementation type, lifetime, and source location for each registration. Use lifetimeFilter to narrow results ('Singleton', 'Scoped', 'Transient').")]
    public async Task<List<DiRegistration>> FindDiRegistrations(
        string? projectName = null,
        string? filePath = null,
        string? lifetimeFilter = null)
        => await _dependencyInjectionEngine.FindDiRegistrationsAsync(projectName, filePath, lifetimeFilter);

    [McpServerTool]
    [Description("Returns all members of a type (methods, properties, fields, events) with full metadata: signature, accessibility, kind, IsInherited, IsOverride, IsAbstract, IsStatic, file path, and line. Set includeInherited=false to show only directly declared members.")]
    public async Task<List<TypeMemberDetail>> GetTypeMembersDetail(
        string typeName, string? projectName = null, bool includeInherited = true)
        => await _symbolNavigationEngine.GetTypeMembersDetailAsync(typeName, projectName, includeInherited);

    [McpServerTool]
    [Description("Checks every class that implements an interface and reports which interface members each implementor has covered. Useful for finding partially-implemented interfaces across the solution.")]
    public async Task<List<InterfaceImplementorCoverage>> VerifyInterfaceCompleteness(
        string interfaceName, string? projectName = null)
        => await _symbolNavigationEngine.VerifyInterfaceCompletenessAsync(interfaceName, projectName);

    [McpServerTool]
    [Description("Finds all extension methods whose receiver type matches the given type (or its base types / interfaces). Returns method name, full signature, defining class, namespace, file path, and line.")]
    public async Task<List<ExtensionMethodInfo>> FindExtensionMethods(
        string targetTypeName, string? projectName = null)
        => await _symbolNavigationEngine.FindExtensionMethodsAsync(targetTypeName, projectName);

    [McpServerTool]
    [Description("Analyzes class cohesion using an LCOM-based metric. Returns per-class analysis including field/method counts, LCOM score (0=cohesive, 1=disconnected), a rating (Excellent/Good/Poor/Very Poor), and informal suggestions for classes that could be split. Useful for identifying god classes and extraction opportunities.")]
    public async Task<List<CohesionAnalysis>> AnalyzeTypeCohesion(string filePath, string? className = null)
        => await _metricsEngine.AnalyzeTypeCohesionAsync(filePath, className);

    [McpServerTool]
    [Description("Detects circular type dependencies within a project. Returns each cycle as an ordered list of type names (last == first) plus file paths. CycleType is 'Direct' for A→B→A cycles or 'Transitive' for longer chains. Scoped to projectName if provided.")]
    public async Task<List<CircularDependencyChain>> FindCircularDependencies(string? projectName = null)
        => await _architecturalEngine.FindCircularDependenciesAsync(projectName);

    [McpServerTool]
    [Description("Builds a forward call graph from a method: shows what that method calls, what those callees call, and so on up to maxDepth levels (default 3). Only follows calls into methods with source locations in the solution (not BCL/NuGet). Returns a CallGraphNode tree: MethodName, ContainingType, FilePath, Line, Callees. Already-visited methods appear as leaf nodes to prevent cycles.")]
    public async Task<CallGraphNode> GetCallGraph(
        string filePath, string methodName, int maxDepth = 3)
    {
        var result = await _symbolNavigationEngine.GetCallGraphAsync(filePath, methodName, maxDepth);
        if (result == null)
            throw new InvalidOperationException(
                $"Method '{methodName}' not found in '{Path.GetFileName(filePath)}'. " +
                "Ensure the file is part of the loaded solution and the method name exactly matches (case-sensitive). " +
                "Use get_document_outline to list available methods in the file.");
        return result;
    }

    [McpServerTool]
    [Description("Builds a reverse call graph (who calls this method): shows all methods that call the given method, what calls those, etc., up to maxDepth levels. Uses Roslyn SymbolFinder for accurate semantic reference resolution — not text search. Returns a ReverseCallGraphNode tree: MethodName, ContainingType, FilePath, Line, Callers.")]
    public async Task<ReverseCallGraphNode> GetReverseCallGraph(string filePath, string methodName, int maxDepth = 3)
    {
        var result = await _symbolNavigationEngine.GetReverseCallGraphAsync(filePath, methodName, maxDepth);
        if (result == null)
            throw new InvalidOperationException(
                $"Method '{methodName}' not found in '{Path.GetFileName(filePath)}'. " +
                "Ensure the file is part of the loaded solution and the method name exactly matches (case-sensitive). " +
                "Use get_document_outline to list available methods in the file.");
        return result;
    }

    [McpServerTool]
    [Description("Converts a class to a BackgroundService: adds BackgroundService base class, generates ExecuteAsync override, and adds Microsoft.Extensions.Hosting using directive.")]
    public async Task<string> ConvertToBackgroundService(string filePath, string className)
        => await _architecturalEngine.ConvertToBackgroundServiceAsync(filePath, className);

    [McpServerTool]
    [Description("Corrects the namespace declaration in a file to match its actual folder structure relative to the project root.")]
    public async Task<string> FixMismatchedNamespaces(string filePath)
        => await _projectStructureEngine.FixMismatchedNamespacesAsync(filePath);

    [McpServerTool]
    [Description("Returns the folder path where a file should reside based on its declared namespace. Use to plan file moves.")]
    public async Task<string> MoveFileToNamespaceFolder(string filePath)
        => await _projectStructureEngine.MoveFileToNamespaceFolderAsync(filePath);

    [McpServerTool]
    [Description("Finds all throw sites (throw statements and throw expressions) across the solution, optionally filtered by exception type, file, or project. Returns file path, line, column, the exception type being thrown, the containing method name, whether the throw is inside a catch block (rethrow patterns), and any extracted message literal from the first argument. Useful for auditing error handling patterns, finding where specific exceptions are raised, and reviewing exception message quality. Use exceptionType to narrow to e.g. 'ArgumentNullException' or 'InvalidOperation'.")]
    public async Task<List<ThrowSiteInfo>> FindAllThrowSites(
        string? exceptionType = null,
        string? filePath = null,
        string? projectName = null)
        => await _discoveryEngine.FindAllThrowSitesAsync(exceptionType, filePath, projectName);

    [McpServerTool]
    [Description("Finds all object creation sites (new T(...) and implicit new(...)) for a given type name substring match, optionally scoped to a file or project. Returns file path, line, column, resolved type name, containing method, and argument count. Useful for finding all places a class is instantiated, auditing factory usage, detecting missing factory patterns, or mapping the lifetime of objects. Supports both explicit 'new Foo()' and implicit 'new()' syntax with type inference from context.")]
    public async Task<List<ObjectCreationSite>> FindObjectCreationSites(
        string typeName,
        string? filePath = null,
        string? projectName = null)
        => await _discoveryEngine.FindObjectCreationSitesAsync(typeName, filePath, projectName);

    [McpServerTool]
    [Description("Returns the complete public API surface of a named project: all public types (classes, interfaces, records, structs), their public and protected methods, properties, and constructors. Each entry includes the type name, member name, full signature, kind (Class/Interface/Method/Property/Constructor/Record/Struct), virtuality/abstractness/sealed flags, and any XML documentation summary. Use includeMethods/includeProperties/includeTypes flags to filter output. Ideal for generating SDK documentation, comparing API surfaces across versions, or producing API review reports.")]
    public async Task<List<ApiSurfaceEntry>> GetPublicApiSurface(
        string projectName,
        bool includeMethods = true,
        bool includeProperties = true,
        bool includeTypes = true)
        => await _discoveryEngine.GetPublicApiSurfaceAsync(projectName, includeMethods, includeProperties, includeTypes);

    [McpServerTool]
    [Description("Scans all constructor parameters in the solution (or a specific project) and identifies injected service types that appear to be missing from the DI container registrations. Detects interfaces (IFoo pattern) and common service-like types (ending in Service, Repository, Manager, Factory, Provider, Handler, Validator, Dispatcher). Framework-provided types (ILogger, IOptions, IConfiguration, etc.) are excluded. Returns consumer class name, file, line, the missing type, and the parameter name. Use to catch unregistered dependencies before runtime failures.")]
    public async Task<List<UnregisteredServiceFinding>> FindServicesNotRegistered(
        string? projectName = null)
        => await _dependencyInjectionEngine.FindServicesNotRegisteredAsync(projectName);

    [McpServerTool]
    [Description("Returns the best 1-based line number where a new member of the given kind should be inserted in a type, following standard C# ordering (fields → constructors → destructors → properties → events → methods → nested types).")]
    public async Task<BestInsertionResult> FindBestInsertionPoint(string filePath, string containerName, string memberKind)
        => await _discoveryEngine.FindBestInsertionPointAsync(filePath, containerName, memberKind);

    [McpServerTool]
    [Description("Scans for TODO, FIXME, HACK, REVIEW, NOTE, BUG comments (case-insensitive) in a file, project, or entire solution. Sorted by severity: BUG/FIXME > HACK > TODO > REVIEW > NOTE.")]
    public async Task<List<TodoCommentFinding>> FindTodoFixmeComments(string? filePath = null, string? projectName = null)
        => await _discoveryEngine.FindTodoFixmeCommentsAsync(filePath, projectName);

    [McpServerTool]
    [Description("Previews the impact of renaming a symbol across the solution without applying changes. Returns affected files and location count. symbolName: the current name. contextSnippet: optional verbatim substring to disambiguate. Provide lineBefore and/or lineAfter when the snippet could match multiple locations.")]
    public async Task<RenameImpactPreview> PreviewRenameImpact(string filePath, string symbolName, string? contextSnippet = null, string? lineBefore = null, string? lineAfter = null)
        => await _discoveryEngine.PreviewRenameImpactAsync(filePath, symbolName, contextSnippet, lineBefore, lineAfter);

    [McpServerTool]
    [Description("""
        Finds all call sites (references) to a symbol in the solution without requiring line/column coordinates.
        Unlike the built-in find_callers which needs a line number, this locates the symbol by name with
        an optional contextSnippet to disambiguate overloads.
        symbolName: the method/property/field name to search for.
        contextSnippet: optional verbatim substring of the declaration (e.g. the method signature line).
        Provide lineBefore and/or lineAfter when the snippet could match multiple locations.
        Returns CallerMethod, CallerType, FilePath, Line, and CodeSnippet for each call site.
        """)]
    public async Task<List<CallerInfo>> FindCallersSafe(string filePath, string symbolName, string? contextSnippet = null, string? lineBefore = null, string? lineAfter = null)
        => await _symbolNavigationEngine.FindCallersAsync(filePath, symbolName, contextSnippet, lineBefore, lineAfter);

    [McpServerTool]
    [Description("""
        Traces a variable's complete lifetime from declaration through every read, write, ref/out pass,
        return, and closure capture, across all code paths (loops, conditionals, try/catch) in the
        enclosing scope.
        filePath: file containing the variable declaration.
        variableName: the exact identifier name.
        lineNumber: 1-based line of the declaration (used to disambiguate variables with the same name).
        Returns: TypeName, DeclarationLine, ScopeDescription, IsDefinitelyAssigned, IsAlwaysAssigned,
        IsCapturedInClosure, and an Accesses list with Line, Column, AccessKind (Declaration/Read/Write/
        Ref/Out/Return/Capture), ContextStack (method > if > for ancestry), IsInLoop, IsInConditional.
        """)]
    public async Task<VariableLifetimeReport> TraceVariableLifetime(string filePath, string variableName, int lineNumber)
        => await _symbolNavigationEngine.TraceVariableLifetimeAsync(filePath, variableName, lineNumber);

    [McpServerTool]
    [Description("""
        Returns the full type hierarchy for a named type: direct base class, full base class chain
        (excluding System.Object), all implemented interfaces (including transitive ones), derived
        classes (via SymbolFinder.FindDerivedClassesAsync), and — if the type is an interface —
        all implementing types (via FindImplementationsAsync).
        Each entry includes TypeName, FilePath, Line, and Kind (Class/Interface/Struct).
        IsInterface, IsAbstract, IsSealed flags are included on the root type.
        """)]
    public async Task<TypeHierarchyReport> GetTypeHierarchy(string typeName, string? projectName = null)
        => await _symbolNavigationEngine.GetTypeHierarchyAsync(typeName, projectName);

    [McpServerTool]
    [Description("""
        Finds all implementations of an interface member or virtual/abstract method in the solution
        without requiring line/column coordinates.
        Unlike the built-in find_implementations which needs a line number, this locates the symbol by name
        with an optional contextSnippet.
        symbolName: the interface member or virtual method name.
        contextSnippet: optional verbatim substring of the declaration to disambiguate overloads.
        Provide lineBefore and/or lineAfter when the snippet could match multiple locations.
        Returns TypeName, FilePath, Line, and Kind for each implementing symbol.
        """)]
    public async Task<List<ImplementationInfo>> FindImplementationsSafe(string filePath, string symbolName, string? contextSnippet = null, string? lineBefore = null, string? lineAfter = null)
        => await _symbolNavigationEngine.FindImplementationsForMemberAsync(filePath, symbolName, contextSnippet, lineBefore, lineAfter);
}
