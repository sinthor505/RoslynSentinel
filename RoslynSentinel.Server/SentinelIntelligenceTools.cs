using System.ComponentModel;

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
    private readonly ProjectConsistencyEngine _projectConsistencyEngine;
    private readonly BreakingChangeEngine _breakingChangeEngine;
    private readonly CloneDetectionEngine _cloneDetectionEngine;
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
        ProjectConsistencyEngine projectConsistencyEngine,
        BreakingChangeEngine breakingChangeEngine,
        CloneDetectionEngine cloneDetectionEngine,
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
        _projectConsistencyEngine = projectConsistencyEngine;
        _breakingChangeEngine = breakingChangeEngine;
        _cloneDetectionEngine = cloneDetectionEngine;
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
    public async Task<List<SearchResult>> ScanMethodsByReturnType(string returnType)
        => await _semanticSearchEngine.FindMethodsByReturnTypeAsync(returnType);

    [McpServerTool]
    [Description("Gets deep metrics for the entire solution or a specific project.")]
    public async Task<SolutionMetrics> GetSolutionMetrics(string? projectName = null) => await _metricsEngine.GetSolutionMetricsAsync(projectName);

    [McpServerTool]
    [Description("Generates a structured report of all namespaces, classes, methods, and properties in a file.")]
    public async Task<CodeInventoryReport> GetCodeInventory(string filePath) => await _inventoryEngine.GetCodeInventoryAsync(filePath);

    [McpServerTool]
    [Description("Finds all unused private members in a class.")]
    public async Task<List<DeadCodeReport>> ScanUnusedPrivateMembers(string filePath, string className)
        => await _deadCodeEngine.FindUnusedPrivateMembersAsync(filePath, className);

    [McpServerTool]
    [Description("Detects private fields that are never read or written in the file.")]
    public async Task<List<DeadCodeReport>> ScanUnusedPrivateFields(string filePath)
        => await _deadCodeEngine.DetectUnusedPrivateFieldsAsync(filePath);

    [McpServerTool]
    [Description("Identifies local variables that are declared but never used within their scope.")]
    public async Task<List<DeadCodeReport>> ScanUnusedLocalVariables(string filePath)
        => await _deadCodeEngine.DetectUnusedLocalVariablesAsync(filePath);

    [McpServerTool]
    [Description("Detects methods with too many parameters and suggests a Parameter Object, optionally filtered by project.")]
    public async Task<List<string>> ScanLongParameterLists(int threshold = 5, string? projectName = null)
        => await _analysisEngine.DetectLongParameterListsAsync(threshold, projectName);

    [McpServerTool]
    [Description("Identifies classes that are never instantiated across the entire solution or a specific project.")]
    public async Task<List<string>> ScanUninstantiatedTypes(string? projectName = null)
        => await _analysisEngine.FindUninstantiatedTypesAsync(projectName);

    [McpServerTool]
    [Description("Identifies circular project references (A -> B -> A).")]
    public async Task<List<string>> ScanCircularDependencies()
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
    public async Task<List<string>> ScanUnusedReferences(string projectName)
        => await _dependencyEngine.FindUnusedReferencesAsync(projectName);

    [McpServerTool]
    [Description("Checks for NuGet package version inconsistencies across multiple projects.")]
    public async Task<List<string>> CheckPackageInconsistency()
        => await _dependencyEngine.CheckPackageInconsistencyAsync();

    [McpServerTool]
    [Description("Identifies interfaces that are declared but never implemented in the solution or a specific project.")]
    public async Task<List<string>> ScanUnusedInterfaces(string? projectName = null)
        => await _analysisEngine.FindUnusedInterfacesAsync(projectName);

    [McpServerTool]
    [Description("Identifies internal classes that are only used in a single file and could be made private, optionally filtered by project.")]
    public async Task<List<string>> ScanInternalClassesThatCouldBePrivate(string? projectName = null)
        => await _analysisEngine.FindInternalClassesThatCouldBePrivateAsync(projectName);

    [McpServerTool]
    [Description("Finds switch statements with a large number of cases that may need refactoring, optionally filtered by project.")]
    public async Task<List<string>> ScanLargeSwitchStatements(int threshold = 10, string? projectName = null)
        => await _analysisEngine.FindLargeSwitchStatementsAsync(threshold, projectName);
    [McpServerTool]
    [Description("Scans the solution for structural issues with granular filtering. typeFilter options: All, MultiType, NameMismatch.")]
    public async Task<List<string>> ScanStructuralSmells(
        ProjectStructureEngine.StructuralSmellType typeFilter = ProjectStructureEngine.StructuralSmellType.All,
        string? projectName = null,
        string? filePath = null)
    {
        return await _projectStructureEngine.FindStructuralSmellsAsync(typeFilter, projectName, filePath);
    }

    [McpServerTool]
    [Description("Identifies constructors that are never called in the entire solution.")]
    public async Task<List<DeadCodeReport>> ScanUnusedConstructors(string filePath)
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
        => await _deadCodeEngine.CheckForUnusedEventSubscriptionsAsync(filePath) ?? throw new InvalidOperationException("Unused event subscriptions not found.");

    [McpServerTool]
    [Description("Gets deep metadata for a symbol: type, kind, accessibility, attributes, documentation. Provide contextSnippet: a verbatim substring identifying the symbol usage or declaration. Provide lineBefore and/or lineAfter when the snippet could match multiple locations.")]
    public async Task<SymbolHoverInfo> GetSymbolInfo(string filePath, string contextSnippet, string? lineBefore = null, string? lineAfter = null)
        => await _symbolNavigationEngine.GetSymbolInfoAsync(filePath, contextSnippet, lineBefore, lineAfter) ?? throw new InvalidOperationException("Symbol info not found.");

    [McpServerTool]
    [Description("Finds all types that implement an interface or derive from a class, returning file path and line for each. Optionally scoped to a single project.")]
    public async Task<List<ImplementationInfo>> GetAllImplementations(string typeName, string? projectName = null)
        => await _symbolNavigationEngine.FindAllImplementationsAsync(typeName, projectName);

    [McpServerTool]
    [Description("Finds private, non-readonly fields that are only ever assigned inside constructors and could safely be marked readonly.")]
    public async Task<List<ReadonlyFieldCandidate>> ScanReadonlyFieldCandidates(string filePath)
        => await _symbolNavigationEngine.FindReadonlyFieldCandidatesAsync(filePath);

    [McpServerTool]
    [Description("Scans for all DI registrations (AddSingleton/AddScoped/AddTransient) across the solution or in a specific project/file. Returns service type, implementation type, lifetime, and source location for each registration. Use lifetimeFilter to narrow results ('Singleton', 'Scoped', 'Transient').")]
    public async Task<List<DiRegistration>> GetDiRegistrations(
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
    public async Task<List<ExtensionMethodInfo>> GetExtensionMethods(
        string targetTypeName, string? projectName = null)
        => await _symbolNavigationEngine.FindExtensionMethodsAsync(targetTypeName, projectName);

    [McpServerTool]
    [Description("Analyzes class cohesion using an LCOM-based metric. Returns per-class analysis including field/method counts, LCOM score (0=cohesive, 1=disconnected), a rating (Excellent/Good/Poor/Very Poor), and informal suggestions for classes that could be split. Useful for identifying god classes and extraction opportunities.")]
    public async Task<List<CohesionAnalysis>> AnalyzeTypeCohesion(string filePath, string? className = null)
        => await _metricsEngine.AnalyzeTypeCohesionAsync(filePath, className);

    [McpServerTool]
    [Description("Detects circular type dependencies within a project. Returns each cycle as an ordered list of type names (last == first) plus file paths. CycleType is 'Direct' for A→B→A cycles or 'Transitive' for longer chains. Scoped to projectName if provided.")]
    public async Task<List<CircularDependencyChain>> ScanCircularDependencies(string? projectName = null)
        => await _architecturalEngine.FindCircularDependenciesAsync(projectName);

    [McpServerTool]
    [Description("Builds a forward call graph from a method: shows what that method calls, what those callees call, and so on up to maxDepth levels (default 3). Only follows calls into methods with source locations in the solution (not BCL/NuGet). Returns a CallGraphNode tree: MethodName, ContainingType, FilePath, Line, Callees. Already-visited methods appear as leaf nodes to prevent cycles.")]
    public async Task<CallGraphNode> GetCallGraph(
        string filePath, string methodName, int maxDepth = 3)
    {
        var result = await _symbolNavigationEngine.GetCallGraphAsync(filePath, methodName, maxDepth);
        if (result == null)
        {
            throw new InvalidOperationException(
                $"Method '{methodName}' not found in '{Path.GetFileName(filePath)}'. " +
                "Ensure the file is part of the loaded solution and the method name exactly matches (case-sensitive). " +
                "Use get_document_outline to list available methods in the file.");
        }

        return result;
    }

    [McpServerTool]
    [Description("Builds a reverse call graph (who calls this method): shows all methods that call the given method, what calls those, etc., up to maxDepth levels. Uses Roslyn SymbolFinder for accurate semantic reference resolution — not text search. Returns a ReverseCallGraphNode tree: MethodName, ContainingType, FilePath, Line, Callers.")]
    public async Task<ReverseCallGraphNode> GetReverseCallGraph(string filePath, string methodName, int maxDepth = 3)
    {
        var result = await _symbolNavigationEngine.GetReverseCallGraphAsync(filePath, methodName, maxDepth);
        if (result == null)
        {
            throw new InvalidOperationException(
                $"Method '{methodName}' not found in '{Path.GetFileName(filePath)}'. " +
                "Ensure the file is part of the loaded solution and the method name exactly matches (case-sensitive). " +
                "Use get_document_outline to list available methods in the file.");
        }

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
    [Description("Finds all throw sites (throw statements and throw expressions) across the solution, optionally filtered by exception type, file, or project. Returns file path, line, column, the exception type being thrown, the containing method name, whether the throw is inside a catch block (rethrow patterns), and any extracted message literal from the first argument. Set sortByFrequency=true to rank results by how often each exception type is thrown (most frequent first) — useful for auditing dominant error patterns.")]
    public async Task<List<ThrowSiteInfo>> ScanAllThrowSites(
        string? exceptionType = null,
        string? filePath = null,
        string? projectName = null,
        bool sortByFrequency = false)
        => await _discoveryEngine.FindAllThrowSitesAsync(exceptionType, filePath, projectName, sortByFrequency);

    [McpServerTool]
    [Description("Finds all object creation sites (new T(...) and implicit new(...)) for a given type name substring match, optionally scoped to a file or project. Returns file path, line, column, resolved type name, containing method, and argument count. Set sortByFrequency=true to rank results by how often each resolved type is instantiated — useful when the typeName is broad (e.g. 'Exception') and you want the most-created types first. Supports both explicit 'new Foo()' and implicit 'new()' syntax with type inference from context.")]
    public async Task<List<ObjectCreationSite>> GetObjectCreationSites(
        string typeName,
        string? filePath = null,
        string? projectName = null,
        bool sortByFrequency = false)
        => await _discoveryEngine.FindObjectCreationSitesAsync(typeName, filePath, projectName, sortByFrequency);

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
    public async Task<List<UnregisteredServiceFinding>> ScanServicesNotRegistered(
        string? projectName = null)
        => await _dependencyInjectionEngine.FindServicesNotRegisteredAsync(projectName);

    [McpServerTool]
    [Description("Returns the best 1-based line number where a new member of the given kind should be inserted in a type, following standard C# ordering (fields → constructors → destructors → properties → events → methods → nested types).")]
    public async Task<BestInsertionResult> GetBestInsertionPoint(string filePath, string containerName, string memberKind)
        => await _discoveryEngine.FindBestInsertionPointAsync(filePath, containerName, memberKind);

    [McpServerTool]
    [Description("Scans for TODO, FIXME, HACK, REVIEW, NOTE, BUG comments (case-insensitive) in a file, project, or entire solution. Sorted by severity: BUG/FIXME > HACK > TODO > REVIEW > NOTE.")]
    public async Task<List<TodoCommentFinding>> ScanTodoFixmeComments(string? filePath = null, string? projectName = null)
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
    public async Task<List<CallerInfo>> GetCallers(string filePath, string symbolName, string? contextSnippet = null, string? lineBefore = null, string? lineAfter = null)
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
    public async Task<List<ImplementationInfo>> GetImplementations(string filePath, string symbolName, string? contextSnippet = null, string? lineBefore = null, string? lineAfter = null)
        => await _symbolNavigationEngine.FindImplementationsForMemberAsync(filePath, symbolName, contextSnippet, lineBefore, lineAfter);

    [McpServerTool]
    [Description("Checks solution-wide consistency: TargetFramework alignment across projects and project naming convention adherence. Returns a list of issues with IssueType (TargetFrameworkMismatch, NamingConventionViolation), description, project name, and file path. NuGet package version consistency is covered by check_package_inconsistency.")]
    public async Task<List<ProjectConsistencyIssue>> CheckProjectConsistency()
        => await _projectConsistencyEngine.CheckConsistencyAsync();

    [McpServerTool]
    [Description("Returns a summary of every project's TargetFramework value. Use this before check_project_consistency to see the full framework landscape across the solution.")]
    public async Task<List<ProjectFrameworkSummary>> GetProjectFrameworkSummary()
        => await _projectConsistencyEngine.GetProjectFrameworkSummaryAsync();

    [McpServerTool]
    [Description("Extracts a snapshot of the public API surface (all public/protected types, methods, constructors, properties, events) from a project or file. Save the returned list as a baseline, make code changes, then call detect_breaking_changes with the baseline to find removed or renamed members. Provide projectName to scope to one project, filePath to scope to one file, or omit both for the entire solution.")]
    public async Task<List<PublicApiMember>> GetPublicApiSurfaceSnapshot(
        string? projectName = null,
        string? filePath = null)
        => await _breakingChangeEngine.GetPublicApiSurfaceAsync(projectName, filePath);

    [McpServerTool]
    [Description("Compares a previously captured API surface (baseline) against the current code and reports breaking changes: removed types, removed/renamed members, and signature changes. Workflow: (1) call get_public_api_surface_snapshot to capture the baseline, (2) make code changes, (3) call this tool with the baseline list. Scope with projectName or filePath as in step 1.")]
    public async Task<List<BreakingChange>> ScanBreakingChanges(
        List<PublicApiMember> baseline,
        string? projectName = null,
        string? filePath = null)
        => await _breakingChangeEngine.DetectBreakingChangesAsync(baseline, projectName, filePath);

    [McpServerTool]
    [Description("Detects namespace-level layer architecture violations (e.g. Controllers importing from Data/Repositories directly, Domain models depending on infrastructure layers). Operates on using directives — fast, no compilation required. Returns violation type, description, source layer, forbidden dependency namespace, file path, and line. Scope with projectName or filePath, or omit for the entire solution.")]
    public async Task<List<ArchitecturalEngine.LayerViolation>> ScanLayerViolations(
        string? projectName = null,
        string? filePath = null)
        => await _architecturalEngine.DetectLayerViolationsAsync(projectName, filePath);

    [McpServerTool]
    [Description("Finds types (classes, structs, records, interfaces) exceeding a line count threshold across the solution or a specific project. Returns TypeName, FilePath, and LineCount. Use before modifying a large class — anything over 500 lines is a prime extract-class candidate. Default threshold is 500 lines.")]
    public async Task<List<LargeTypeReport>> ScanLargeTypes(int maxLines = 500, string? projectName = null)
        => await _analysisEngine.FindLargeTypesAsync(maxLines, projectName);

    [McpServerTool]
    [Description("Finds methods exceeding a line count threshold across the solution or a specific project. Returns MethodName, TypeName, FilePath, and LineCount. Methods over 50 lines are too large to modify safely without reading in full — they are also prime extract-method candidates. Default threshold is 50 lines.")]
    public async Task<List<LargeMethodReport>> ScanLargeMethods(int maxLines = 50, string? projectName = null)
        => await _analysisEngine.FindLargeMethodsAsync(maxLines, projectName);

    [McpServerTool]
    [Description("Finds structurally duplicate method implementations across the solution: methods that share the same statement structure and control flow even if identifiers differ (hash-based). Returns groups of duplicate methods with their file paths and type names. Use to find copy-paste code that should be consolidated into a shared helper. Increase minStatements (default 5) to reduce false positives on short utility methods.")]
    public async Task<List<DuplicateMethodGroup>> ScanDuplicateMethods(int minStatements = 5, string? projectName = null)
        => await _analysisEngine.FindDuplicateMethodsAsync(minStatements, projectName);

    [McpServerTool]
    [Description("Finds public classes with 3+ public methods but no corresponding interface. Returns ClassName, FilePath, and the list of public method names. These are prime candidates for interface extraction — a prerequisite for testability (Moq/NSubstitute) and adding a second implementation. Increase minPublicMethods (default 3) for stricter filtering.")]
    public async Task<List<InterfaceCandidateReport>> ScanInterfaceExtractionCandidates(int minPublicMethods = 3, string? projectName = null)
        => await _analysisEngine.FindInterfaceExtractionCandidatesAsync(minPublicMethods, projectName);

    [McpServerTool]
    [Description("Finds circular constructor-injection dependencies among user-defined types: type A's constructor depends on type B, and B's constructor (transitively) depends on A. This is the exact cycle that causes .NET's DI container to throw at startup. Complements find_services_not_registered — use both before adding new service registrations. Optionally scoped to a project.")]
    public async Task<List<string>> ScanCircularTypeReferences(string? projectName = null)
        => await _analysisEngine.FindCircularTypeReferencesAsync(projectName);

    [McpServerTool]
    [Description("Finds generic methods with type parameters used in ways that require constraints but have none declared: (1) 'new T()' instantiation without 'where T : new()', (2) 'param == null' comparison without 'where T : class' (always false for value types). Returns file path, line number, method name, and a description of the specific violation. Scope by file or project, or scan the entire solution.")]
    public async Task<List<string>> ScanMissingGenericConstraints(string? projectName = null, string? filePath = null)
        => await _analysisEngine.FindMissingGenericConstraintsAsync(projectName, filePath);

    [McpServerTool]
    [Description("Finds all TYPES (classes, interfaces, records, structs, enums) decorated with a specific attribute using the semantic model for accuracy. Unlike find_attribute_usages (which returns all targets including methods and properties), this returns only type-level decoration. Useful for: 'find all [ApiController] classes', 'find all [TestClass] types', 'find all [Serializable] types'. Returns TypeName, FilePath, and Line.")]
    public async Task<List<SearchResult>> ScanTypesByAttribute(string attributeName)
        => await _semanticSearchEngine.FindTypesByAttributeAsync(attributeName);

    [McpServerTool]
    [Description("Generates XML documentation stubs (<summary>, <param>, <returns>) for ALL undocumented public methods in a file. Unlike document_poco_fields (which targets a specific class's fields), this covers every public method in the file that lacks XML docs. Returns the updated file content. Apply with apply_proposed_changes to write to disk.")]
    public async Task<string> GenerateXmlDocumentationStubs(string filePath)
    {
        var result = await _documentationEngine.GenerateXmlDocumentationStubsAsync(filePath);
        if (string.IsNullOrEmpty(result))
        {
            throw new InvalidOperationException(
                $"GenerateXmlDocumentationStubs failed for '{filePath}': " +
                "file not found in workspace. Ensure the solution is loaded.");
        }

        return result;
    }

    [McpServerTool]
    [Description("""
        Finds all usages of a named attribute across the solution, optionally scoped to a
        project or file. Accepts both "Authorize" and "AuthorizeAttribute" (or "[Authorize]")
        spelling — all three resolve identically.

        Returns: AttributeName, TargetKind (Class/Method/Property/Field/Parameter/Constructor/
        Interface/Record/Struct/Enum), TargetName, ContainingType (empty for type-level attrs),
        FilePath, and Line.

        Common use cases:
          • Security audit: find every endpoint missing [Authorize] by listing controllers and
            diffing against this result.
          • API review: "find_attribute_usages [ProducesResponseType]" to see which actions
            document their return types.
          • Validation audit: "find_attribute_usages [Required]" to map all required properties.
          • Obsolete API detection: "find_attribute_usages [Obsolete]" to find every member
            still marked deprecated.
        """)]
    public async Task<List<AttributeUsageSite>> GetAttributeUsages(
        string attributeName,
        string? projectName = null,
        string? filePath = null)
        => await _discoveryEngine.FindAttributeUsagesAsync(attributeName, projectName, filePath);

    [McpServerTool]
    [Description("""
        Finds duplicate statement sequences within the methods of a single class.
        Uses structural hashing (SyntaxKind-based) so blocks match regardless of
        variable names or literal values — the same control-flow shape is a clone.

        Returns groups of matching block locations including:
        • StatementCount — how many statements the clone spans
        • HasControlFlowExit — true if any statement is a return/break/continue/throw
          (flag only; does not block the finding)
        • SnippetPreview — first 120 chars of the first statement
        • CapturedVariables — variables that would need to become parameters if extracted
        • ProducedVariables — variables that would need to be returned if extracted
        • Occurrences — method name, start line, end line, file path for each copy

        Use this to find Extract-Method candidates within a single class before the
        duplication spreads. Set minStatements lower (e.g. 3) for aggressive detection,
        higher (e.g. 6) for only substantial clones.
        """)]
    public async Task<List<DuplicateBlockGroup>> ScanDuplicateBlocksInClass(
        string filePath,
        string className,
        int minStatements = 4)
        => await _cloneDetectionEngine.FindDuplicateBlocksInClassAsync(filePath, className, minStatements);

    [McpServerTool]
    [Description("""
        Finds duplicate statement sequences across all types in an inheritance or
        interface hierarchy — base class, derived classes, and implementing types.

        Scoped to avoid noise: only types that share a common ancestor or interface
        with the named type are included. Cross-hierarchy clones (unrelated types)
        are excluded. This makes findings directly actionable: duplicates in a hierarchy
        are candidates for extraction to the base class or a shared abstract method.

        Returns the same DuplicateBlockGroup shape as FindDuplicateBlocksInClass,
        with ContainingType and MethodName populated per occurrence so you can see
        exactly which derived class has the copy.

        typeName: the root type to anchor the hierarchy search (class or interface name).
        projectName: optional filter to limit the search scope.
        minStatements: minimum window size (default 4).
        """)]
    public async Task<List<DuplicateBlockGroup>> ScanDuplicateBlocksInHierarchy(
        string typeName,
        string? projectName = null,
        int minStatements = 4)
        => await _cloneDetectionEngine.FindDuplicateBlocksInHierarchyAsync(typeName, projectName, minStatements);

    // ── Legacy aliases (deprecated — use scan_*/get_* names) ─────────────────────

    [McpServerTool]
    [Description("Deprecated: use scan_methods_by_return_type instead. This alias will be removed in a future release.")]
    public Task<List<SearchResult>> FindMethodsByReturnType(string returnType)
        => ScanMethodsByReturnType(returnType);

    [McpServerTool]
    [Description("Deprecated: use scan_unused_private_members instead. This alias will be removed in a future release.")]
    public Task<List<DeadCodeReport>> FindUnusedPrivateMembers(string filePath, string className)
        => ScanUnusedPrivateMembers(filePath, className);

    [McpServerTool]
    [Description("Deprecated: use scan_unused_private_fields instead. This alias will be removed in a future release.")]
    public Task<List<DeadCodeReport>> DetectUnusedPrivateFields(string filePath)
        => ScanUnusedPrivateFields(filePath);

    [McpServerTool]
    [Description("Deprecated: use scan_unused_local_variables instead. This alias will be removed in a future release.")]
    public Task<List<DeadCodeReport>> DetectUnusedLocalVariables(string filePath)
        => ScanUnusedLocalVariables(filePath);

    [McpServerTool]
    [Description("Deprecated: use scan_long_parameter_lists instead. This alias will be removed in a future release.")]
    public Task<List<string>> DetectLongParameterLists(int threshold = 5, string? projectName = null)
        => ScanLongParameterLists(threshold, projectName);

    [McpServerTool]
    [Description("Deprecated: use scan_uninstantiated_types instead. This alias will be removed in a future release.")]
    public Task<List<string>> FindUninstantiatedTypes(string? projectName = null)
        => ScanUninstantiatedTypes(projectName);

    [McpServerTool]
    [Description("Deprecated: use scan_circular_dependencies instead. This alias will be removed in a future release.")]
    public Task<List<string>> FindCircularDependencies()
        => ScanCircularDependencies();

    [McpServerTool]
    [Description("Deprecated: use scan_circular_dependencies instead. This alias will be removed in a future release.")]
    public Task<List<CircularDependencyChain>> FindCircularDependencies(string? projectName = null)
        => ScanCircularDependencies(projectName);

    [McpServerTool]
    [Description("Deprecated: use scan_unused_references instead. This alias will be removed in a future release.")]
    public Task<List<string>> FindUnusedReferences(string projectName)
        => ScanUnusedReferences(projectName);

    [McpServerTool]
    [Description("Deprecated: use scan_unused_interfaces instead. This alias will be removed in a future release.")]
    public Task<List<string>> FindUnusedInterfaces(string? projectName = null)
        => ScanUnusedInterfaces(projectName);

    [McpServerTool]
    [Description("Deprecated: use scan_internal_classes_that_could_be_private instead. This alias will be removed in a future release.")]
    public Task<List<string>> FindInternalClassesThatCouldBePrivate(string? projectName = null)
        => ScanInternalClassesThatCouldBePrivate(projectName);

    [McpServerTool]
    [Description("Deprecated: use scan_large_switch_statements instead. This alias will be removed in a future release.")]
    public Task<List<string>> FindLargeSwitchStatements(int threshold = 10, string? projectName = null)
        => ScanLargeSwitchStatements(threshold, projectName);

    [McpServerTool]
    [Description("Deprecated: use scan_structural_smells instead. This alias will be removed in a future release.")]
    public Task<List<string>> FindStructuralSmells(ProjectStructureEngine.StructuralSmellType typeFilter = ProjectStructureEngine.StructuralSmellType.All, string? projectName = null, string? filePath = null)
        => ScanStructuralSmells(typeFilter, projectName, filePath);

    [McpServerTool]
    [Description("Deprecated: use scan_unused_constructors instead. This alias will be removed in a future release.")]
    public Task<List<DeadCodeReport>> FindUnusedConstructors(string filePath)
        => ScanUnusedConstructors(filePath);

    [McpServerTool]
    [Description("Deprecated: use get_all_implementations instead. This alias will be removed in a future release.")]
    public Task<List<ImplementationInfo>> FindAllImplementations(string typeName, string? projectName = null)
        => GetAllImplementations(typeName, projectName);

    [McpServerTool]
    [Description("Deprecated: use scan_readonly_field_candidates instead. This alias will be removed in a future release.")]
    public Task<List<ReadonlyFieldCandidate>> FindReadonlyFieldCandidates(string filePath)
        => ScanReadonlyFieldCandidates(filePath);

    [McpServerTool]
    [Description("Deprecated: use get_di_registrations instead. This alias will be removed in a future release.")]
    public Task<List<DiRegistration>> FindDiRegistrations(string? projectName = null, string? filePath = null, string? lifetimeFilter = null)
        => GetDiRegistrations(projectName, filePath, lifetimeFilter);

    [McpServerTool]
    [Description("Deprecated: use get_extension_methods instead. This alias will be removed in a future release.")]
    public Task<List<ExtensionMethodInfo>> FindExtensionMethods(string targetTypeName, string? projectName = null)
        => GetExtensionMethods(targetTypeName, projectName);

    [McpServerTool]
    [Description("Deprecated: use scan_all_throw_sites instead. This alias will be removed in a future release.")]
    public Task<List<ThrowSiteInfo>> FindAllThrowSites(string? exceptionType = null, string? filePath = null, string? projectName = null, bool sortByFrequency = false)
        => ScanAllThrowSites(exceptionType, filePath, projectName, sortByFrequency);

    [McpServerTool]
    [Description("Deprecated: use get_object_creation_sites instead. This alias will be removed in a future release.")]
    public Task<List<ObjectCreationSite>> FindObjectCreationSites(string typeName, string? filePath = null, string? projectName = null, bool sortByFrequency = false)
        => GetObjectCreationSites(typeName, filePath, projectName, sortByFrequency);

    [McpServerTool]
    [Description("Deprecated: use scan_services_not_registered instead. This alias will be removed in a future release.")]
    public Task<List<UnregisteredServiceFinding>> FindServicesNotRegistered(string? projectName = null)
        => ScanServicesNotRegistered(projectName);

    [McpServerTool]
    [Description("Deprecated: use get_best_insertion_point instead. This alias will be removed in a future release.")]
    public Task<BestInsertionResult> FindBestInsertionPoint(string filePath, string containerName, string memberKind)
        => GetBestInsertionPoint(filePath, containerName, memberKind);

    [McpServerTool]
    [Description("Deprecated: use scan_todo_fixme_comments instead. This alias will be removed in a future release.")]
    public Task<List<TodoCommentFinding>> FindTodoFixmeComments(string? filePath = null, string? projectName = null)
        => ScanTodoFixmeComments(filePath, projectName);

    [McpServerTool]
    [Description("Deprecated: use get_callers instead. This alias will be removed in a future release.")]
    public Task<List<CallerInfo>> FindCallersSafe(string filePath, string symbolName, string? contextSnippet = null, string? lineBefore = null, string? lineAfter = null)
        => GetCallers(filePath, symbolName, contextSnippet, lineBefore, lineAfter);

    [McpServerTool]
    [Description("Deprecated: use get_implementations instead. This alias will be removed in a future release.")]
    public Task<List<ImplementationInfo>> FindImplementationsSafe(string filePath, string symbolName, string? contextSnippet = null, string? lineBefore = null, string? lineAfter = null)
        => GetImplementations(filePath, symbolName, contextSnippet, lineBefore, lineAfter);

    [McpServerTool]
    [Description("Deprecated: use scan_breaking_changes instead. This alias will be removed in a future release.")]
    public Task<List<BreakingChange>> DetectBreakingChanges(List<PublicApiMember> baseline, string? projectName = null, string? filePath = null)
        => ScanBreakingChanges(baseline, projectName, filePath);

    [McpServerTool]
    [Description("Deprecated: use scan_layer_violations instead. This alias will be removed in a future release.")]
    public Task<List<ArchitecturalEngine.LayerViolation>> DetectLayerViolations(string? projectName = null, string? filePath = null)
        => ScanLayerViolations(projectName, filePath);

    [McpServerTool]
    [Description("Deprecated: use scan_large_types instead. This alias will be removed in a future release.")]
    public Task<List<LargeTypeReport>> FindLargeTypes(int maxLines = 500, string? projectName = null)
        => ScanLargeTypes(maxLines, projectName);

    [McpServerTool]
    [Description("Deprecated: use scan_large_methods instead. This alias will be removed in a future release.")]
    public Task<List<LargeMethodReport>> FindLargeMethods(int maxLines = 50, string? projectName = null)
        => ScanLargeMethods(maxLines, projectName);

    [McpServerTool]
    [Description("Deprecated: use scan_duplicate_methods instead. This alias will be removed in a future release.")]
    public Task<List<DuplicateMethodGroup>> FindDuplicateMethods(int minStatements = 5, string? projectName = null)
        => ScanDuplicateMethods(minStatements, projectName);

    [McpServerTool]
    [Description("Deprecated: use scan_interface_extraction_candidates instead. This alias will be removed in a future release.")]
    public Task<List<InterfaceCandidateReport>> FindInterfaceExtractionCandidates(int minPublicMethods = 3, string? projectName = null)
        => ScanInterfaceExtractionCandidates(minPublicMethods, projectName);

    [McpServerTool]
    [Description("Deprecated: use scan_circular_type_references instead. This alias will be removed in a future release.")]
    public Task<List<string>> FindCircularTypeReferences(string? projectName = null)
        => ScanCircularTypeReferences(projectName);

    [McpServerTool]
    [Description("Deprecated: use scan_missing_generic_constraints instead. This alias will be removed in a future release.")]
    public Task<List<string>> FindMissingGenericConstraints(string? projectName = null, string? filePath = null)
        => ScanMissingGenericConstraints(projectName, filePath);

    [McpServerTool]
    [Description("Deprecated: use scan_types_by_attribute instead. This alias will be removed in a future release.")]
    public Task<List<SearchResult>> FindTypesByAttribute(string attributeName)
        => ScanTypesByAttribute(attributeName);

    [McpServerTool]
    [Description("Deprecated: use get_attribute_usages instead. This alias will be removed in a future release.")]
    public Task<List<AttributeUsageSite>> FindAttributeUsages(string attributeName, string? projectName = null, string? filePath = null)
        => GetAttributeUsages(attributeName, projectName, filePath);

    [McpServerTool]
    [Description("Deprecated: use scan_duplicate_blocks_in_class instead. This alias will be removed in a future release.")]
    public Task<List<DuplicateBlockGroup>> FindDuplicateBlocksInClass(string filePath, string className, int minStatements = 4)
        => ScanDuplicateBlocksInClass(filePath, className, minStatements);

    [McpServerTool]
    [Description("Deprecated: use scan_duplicate_blocks_in_hierarchy instead. This alias will be removed in a future release.")]
    public Task<List<DuplicateBlockGroup>> FindDuplicateBlocksInHierarchy(string typeName, string? projectName = null, int minStatements = 4)
        => ScanDuplicateBlocksInHierarchy(typeName, projectName, minStatements);

}