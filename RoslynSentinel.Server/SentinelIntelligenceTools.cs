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
    [Description("Gets the blast radius (impact analysis) of a change to a symbol at a specific location.")]
    public async Task<ImpactReport> GetBlastRadius(string filePath, int line, int column) 
        => await _impactAnalyzer.AnalyzeImpactAsync(filePath, line, column);

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
        => await _deadCodeEngine.FindUnusedConstructorsAsync(filePath);

    [McpServerTool]
    [Description("Scans a file for event subscriptions that are never unsubscribed, potential memory leaks.")]
    public async Task<List<DeadCodeReport>> CheckForUnusedEventSubscriptions(string filePath) 
        => await _deadCodeEngine.CheckForUnusedEventSubscriptionsAsync(filePath);
}
