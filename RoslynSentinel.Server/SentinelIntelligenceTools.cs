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
    [Description("Identifies classes that are never instantiated across the entire solution or a specific project.")]
    public async Task<List<string>> ScanUninstantiatedTypes(string? projectName = null)
        => await _analysisEngine.FindUninstantiatedTypesAsync(projectName);

    [McpServerTool]
    [Description("Identifies circular project references (A -> B -> A).")]
    public async Task<List<string>> ScanCircularProjectDependencies()
        => await _analysisEngine.FindCircularDependenciesAsync();


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
    [Description("Inspects a symbol in depth. aspect: info (type, kind, accessibility, attributes, documentation — returns SymbolHoverInfo) or blastRadius (all call sites and affected projects if the symbol is changed — returns ImpactReport). Provide contextSnippet: a verbatim substring identifying the symbol. Provide lineBefore/lineAfter to disambiguate.")]
    public async Task<object> InspectSymbol(
        string filePath,
        string contextSnippet,
        string aspect,
        string? lineBefore = null,
        string? lineAfter = null)
    {
        if (aspect == "info")
        {
            return await _symbolNavigationEngine.GetSymbolInfoAsync(filePath, contextSnippet, lineBefore, lineAfter) ?? throw new InvalidOperationException("Symbol info not found.");
        }
        if (aspect == "blastRadius")
        {
            return await _impactAnalyzer.AnalyzeImpactAsync(filePath, contextSnippet, lineBefore, lineAfter);
        }
        throw new ArgumentException($"Unknown aspect '{aspect}'. Valid values: info, blastRadius.");
    }


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
    [Description("Checks every class that implements an interface and reports which interface members each implementor has covered. Useful for finding partially-implemented interfaces across the solution.")]
    public async Task<List<InterfaceImplementorCoverage>> VerifyInterfaceCompleteness(
        string interfaceName, string? projectName = null)
        => await _symbolNavigationEngine.VerifyInterfaceCompletenessAsync(interfaceName, projectName);


    [McpServerTool]
    [Description("Analyzes class cohesion using an LCOM-based metric. Returns per-class analysis including field/method counts, LCOM score (0=cohesive, 1=disconnected), a rating (Excellent/Good/Poor/Very Poor), and informal suggestions for classes that could be split. Useful for identifying god classes and extraction opportunities.")]
    public async Task<List<CohesionAnalysis>> AnalyzeTypeCohesion(string filePath, string? className = null)
        => await _metricsEngine.AnalyzeTypeCohesionAsync(filePath, className);

    [McpServerTool]
    [Description("Detects circular type dependencies within a project. Returns each cycle as an ordered list of type names (last == first) plus file paths. CycleType is 'Direct' for A→B→A cycles or 'Transitive' for longer chains. Scoped to projectName if provided.")]
    public async Task<List<CircularDependencyChain>> ScanCircularTypeDependencies(string? projectName = null)
        => await _architecturalEngine.FindCircularDependenciesAsync(projectName);

    [McpServerTool]
    [Description("Builds a call graph for a method. direction: forward (what the method calls, returns CallGraphNode tree), reverse (who calls this method, returns ReverseCallGraphNode tree), tree (markdown call-tree string). maxDepth: traversal depth (default 3).")]
    public async Task<object> GetCallGraph(
        string filePath,
        string methodName,
        string direction = "forward",
        int maxDepth = 3)
    {
        if (direction == "forward")
        {
            var fwd = await _symbolNavigationEngine.GetCallGraphAsync(filePath, methodName, maxDepth);
            if (fwd == null)
            {
                throw new InvalidOperationException(
                    $"Method '{methodName}' not found in '{Path.GetFileName(filePath)}'. " +
                    "Ensure the file is part of the loaded solution and the method name exactly matches (case-sensitive). " +
                    "Use get_document_outline to list available methods in the file.");
            }
            return fwd;
        }
        if (direction == "reverse")
        {
            var rev = await _symbolNavigationEngine.GetReverseCallGraphAsync(filePath, methodName, maxDepth);
            if (rev == null)
            {
                throw new InvalidOperationException(
                    $"Method '{methodName}' not found in '{Path.GetFileName(filePath)}'. " +
                    "Ensure the file is part of the loaded solution and the method name exactly matches (case-sensitive). " +
                    "Use get_document_outline to list available methods in the file.");
            }
            return rev;
        }
        if (direction == "tree")
        {
            return await _analysisEngine.GenerateCallTreeAsync(filePath, methodName, maxDepth);
        }
        throw new ArgumentException($"Unknown direction '{direction}'. Valid values: forward, reverse, tree.");
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
    [Description("Finds symbols by name using various lookup strategies. kind: implementorsOf (all types implementing or deriving from the named type), attributeUsages (all usages of a named attribute across the solution), objectCreations (all new T() sites for a type name), extensionsFor (extension methods for a target type), typesWithAttribute (types decorated with a specific attribute), methodsByReturnType (methods returning a specific type). projectName and filePath narrow the scope where supported. sortByFrequency ranks results by frequency (supported for kind=objectCreations).")]
    public async Task<object> FindByName(
        string name,
        string kind,
        string? projectName = null,
        string? filePath = null,
        bool sortByFrequency = false)
    {
        if (kind == "implementorsOf")
        {
            return await _symbolNavigationEngine.FindAllImplementationsAsync(name, projectName);
        }
        if (kind == "attributeUsages")
        {
            return await _discoveryEngine.FindAttributeUsagesAsync(name, projectName, filePath);
        }
        if (kind == "objectCreations")
        {
            return await _discoveryEngine.FindObjectCreationSitesAsync(name, filePath, projectName, sortByFrequency);
        }
        if (kind == "extensionsFor")
        {
            return await _symbolNavigationEngine.FindExtensionMethodsAsync(name, projectName);
        }
        if (kind == "typesWithAttribute")
        {
            return await _semanticSearchEngine.FindTypesByAttributeAsync(name);
        }
        if (kind == "methodsByReturnType")
        {
            return await _semanticSearchEngine.FindMethodsByReturnTypeAsync(name);
        }
        throw new ArgumentException($"Unknown kind '{kind}'. Valid values: implementorsOf, attributeUsages, objectCreations, extensionsFor, typesWithAttribute, methodsByReturnType.");
    }

    [McpServerTool]
    [Description("Returns the public API surface of a project. persistBaseline=false (default): returns full List<ApiSurfaceEntry> with type/member signatures, virtuality, and XML doc — use for SDK documentation or API review. persistBaseline=true: returns compact List<PublicApiMember> baseline suitable for passing to scan_breaking_changes to detect removed or renamed members. filePath scopes to a single file (only used when persistBaseline=true). includeMethods/includeProperties/includeTypes filter the output (only used when persistBaseline=false).")]
    public async Task<object> GetPublicApiSurface(
        string? projectName = null,
        bool persistBaseline = false,
        string? filePath = null,
        bool includeMethods = true,
        bool includeProperties = true,
        bool includeTypes = true)
    {
        if (persistBaseline)
        {
            return await _breakingChangeEngine.GetPublicApiSurfaceAsync(projectName, filePath);
        }
        if (string.IsNullOrEmpty(projectName))
        {
            throw new ArgumentException("projectName is required when persistBaseline=false.");
        }
        return await _discoveryEngine.GetPublicApiSurfaceAsync(projectName, includeMethods, includeProperties, includeTypes);
    }

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
    [Description("Finds all call sites (kind=callers) or implementing members/overrides (kind=implementations) for a symbol, without requiring line/column coordinates. symbolName: the method/property/field name. contextSnippet: optional verbatim substring of the declaration to disambiguate overloads. Provide lineBefore/lineAfter for further disambiguation. Returns List<CallerInfo> for callers, List<ImplementationInfo> for implementations.")]
    public async Task<object> FindReferences(
        string filePath,
        string symbolName,
        string kind,
        string? contextSnippet = null,
        string? lineBefore = null,
        string? lineAfter = null)
    {
        if (kind == "callers")
        {
            return await _symbolNavigationEngine.FindCallersAsync(filePath, symbolName, contextSnippet, lineBefore, lineAfter);
        }
        if (kind == "implementations")
        {
            return await _symbolNavigationEngine.FindImplementationsForMemberAsync(filePath, symbolName, contextSnippet, lineBefore, lineAfter);
        }
        throw new ArgumentException($"Unknown kind '{kind}'. Valid values: callers, implementations.");
    }

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
    [Description("Returns type information for a named type. include: hierarchy (base class chain, interfaces, derived types — returns TypeHierarchyReport), members (all public/protected members with full metadata — returns List<TypeMemberDetail>), both (returns object with Hierarchy and Members properties). projectName scopes the lookup. includeInherited controls whether inherited members appear (default true; used for include=members and both).")]
    public async Task<object> GetTypeInfo(
        string typeName,
        string include = "both",
        string? projectName = null,
        bool includeInherited = true)
    {
        TypeHierarchyReport? hierarchy = null;
        List<TypeMemberDetail>? members = null;
        if (include == "hierarchy" || include == "both")
        {
            hierarchy = await _symbolNavigationEngine.GetTypeHierarchyAsync(typeName, projectName);
        }
        if (include == "members" || include == "both")
        {
            members = await _symbolNavigationEngine.GetTypeMembersDetailAsync(typeName, projectName, includeInherited);
        }
        if (include == "hierarchy")
        {
            return hierarchy!;
        }
        if (include == "members")
        {
            return members!;
        }
        if (include == "both")
        {
            return new { Hierarchy = hierarchy, Members = members };
        }
        throw new ArgumentException($"Unknown include '{include}'. Valid values: hierarchy, members, both.");
    }


    [McpServerTool]
    [Description("Checks solution-wide consistency: TargetFramework alignment across projects and project naming convention adherence. Returns a list of issues with IssueType (TargetFrameworkMismatch, NamingConventionViolation), description, project name, and file path. NuGet package version consistency is covered by check_package_inconsistency.")]
    public async Task<List<ProjectConsistencyIssue>> CheckProjectConsistency()
        => await _projectConsistencyEngine.CheckConsistencyAsync();

    [McpServerTool]
    [Description("Returns a summary of every project's TargetFramework value. Use this before check_project_consistency to see the full framework landscape across the solution.")]
    public async Task<List<ProjectFrameworkSummary>> GetProjectFrameworkSummary()
        => await _projectConsistencyEngine.GetProjectFrameworkSummaryAsync();


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
}
