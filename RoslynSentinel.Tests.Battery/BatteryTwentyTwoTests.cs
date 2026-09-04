// Battery #22 — SentinelIntelligenceTools
// Tests all 45 public methods of SentinelIntelligenceTools in-memory via TestSolutionBuilder.

using Microsoft.Extensions.Logging.Abstractions;

#pragma warning disable CS8618
namespace RoslynSentinel.Tests.Battery;

[TestFixture]
public class BatteryTwentyTwoTests
{
    private IWorkspaceManager _workspaceManager;
    private SentinelConfiguration _config;
    private ImpactAnalyzer _impactAnalyzer;
    private SemanticSearchEngine _semanticSearchEngine;
    private MetricsEngine _metricsEngine;
    private InventoryEngine _inventoryEngine;
    private DeadCodeEngine _deadCodeEngine;
    private AnalysisEngine _analysisEngine;
    private DocumentationEngine _documentationEngine;
    private DependencyEngine _dependencyEngine;
    private ProjectStructureEngine _projectStructureEngine;
    private AsyncSafetyEngine _asyncSafetyEngine;
    private HealthOrchestrationEngine _healthOrchestrationEngine;
    private ArchitecturalEngine _architecturalEngine;
    private SymbolNavigationEngine _symbolNavigationEngine;
    private DependencyInjectionEngine _dependencyInjectionEngine;
    private DiscoveryEngine _discoveryEngine;
    private SentinelIntelligenceTools _tools;
    private SentinelSymbolTools _symbolTools;
    private SentinelScanTools _scanTools;

    private const string RichSource = @"
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace TestProj;

public class Order
{
    public int OrderId;
    public string CustomerName;
    private readonly ILogger _logger;

    public Order(int orderId, string customerName, ILogger logger)
    {
        OrderId = orderId;
        CustomerName = customerName;
        _logger = logger;
    }

    public async Task<string> ProcessAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(""Processing order {Id}"", OrderId);
        var result = string.Format(""{0}: {1}"", OrderId, CustomerName);
        return await Task.FromResult(result);
    }

    public string GetStatus()
    {
        if (OrderId == 1) return ""Active"";
        if (OrderId == 2) return ""Pending"";
        return ""Unknown"";
    }

    public void UpdateStatus(int status)
    {
        switch (status)
        {
            case 1: Console.WriteLine(""active""); break;
            case 2: Console.WriteLine(""pending""); break;
            default: Console.WriteLine(""unknown""); break;
        }
    }

    public List<string> GetItems()
    {
        var items = new List<string>();
        foreach (var i in new[] { ""a"", ""b"" })
        {
            items.Add(i);
        }
        return items;
    }

    public async Task WaitAsync() => await Task.Delay(1000);
}

public interface IOrderService
{
    Task<Order> GetOrderAsync(int id);
    Task SaveAsync(Order order);
}

public class OrderService : IOrderService
{
    private readonly ILogger<OrderService> _logger;
    public OrderService(ILogger<OrderService> logger) { _logger = logger; }
    public async Task<Order> GetOrderAsync(int id) => await Task.FromResult(new Order(id, ""test"", _logger));
    public async Task SaveAsync(Order order) => await Task.CompletedTask;
}";

    [SetUp]
    public void Setup()
    {
        _workspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        _config = new SentinelConfiguration();
        _impactAnalyzer = new ImpactAnalyzer(NullLogger<ImpactAnalyzer>.Instance, _workspaceManager);
        _semanticSearchEngine = new SemanticSearchEngine(_workspaceManager);
        _metricsEngine = new MetricsEngine(_workspaceManager);
        _inventoryEngine = new InventoryEngine(_workspaceManager);
        _deadCodeEngine = new DeadCodeEngine(_workspaceManager);
        _analysisEngine = new AnalysisEngine(_workspaceManager, _config);
        _documentationEngine = new DocumentationEngine(_workspaceManager);
        _dependencyEngine = new DependencyEngine(_workspaceManager);
        _projectStructureEngine = new ProjectStructureEngine(_workspaceManager, _config);
        _asyncSafetyEngine = new AsyncSafetyEngine(_workspaceManager);
        _healthOrchestrationEngine = new HealthOrchestrationEngine(
            _workspaceManager, _projectStructureEngine, _analysisEngine, _config);
        _architecturalEngine = new ArchitecturalEngine(_workspaceManager);
        _symbolNavigationEngine = new SymbolNavigationEngine(_workspaceManager, NullLogger<SymbolNavigationEngine>.Instance);
        _dependencyInjectionEngine = new DependencyInjectionEngine(_workspaceManager);
        _discoveryEngine = new DiscoveryEngine(_workspaceManager, _symbolNavigationEngine);
        _tools = new SentinelIntelligenceTools(
            _impactAnalyzer, _semanticSearchEngine, _metricsEngine, _inventoryEngine,
            _deadCodeEngine, _analysisEngine, _documentationEngine, _dependencyEngine,
            _projectStructureEngine, _asyncSafetyEngine, _healthOrchestrationEngine,
            _architecturalEngine, _symbolNavigationEngine, _dependencyInjectionEngine,
            _discoveryEngine, new ProjectConsistencyEngine(_workspaceManager),
            _workspaceManager,
            _config, NullLogger<SentinelIntelligenceTools>.Instance);

        // Symbol-level tools moved to SentinelSymbolTools (Basic) in the server split.
        _symbolTools = new SentinelSymbolTools(
            _impactAnalyzer, _semanticSearchEngine, _inventoryEngine, _analysisEngine,
            _dependencyEngine, _projectStructureEngine, _symbolNavigationEngine,
            _discoveryEngine, new ProjectConsistencyEngine(_workspaceManager),
            _workspaceManager, _config, NullLogger<SentinelSymbolTools>.Instance);

        // GetPublicApiSurface moved to SentinelScanTools (Advanced).
        _scanTools = new SentinelScanTools(
            _analysisEngine, new SecurityEngine(_workspaceManager), new AntiPatternEngine(_workspaceManager),
            _asyncSafetyEngine, new ThreadSafetyEngine(_workspaceManager), new ControlFlowEngine(_workspaceManager),
            new PerformanceEngine(_workspaceManager), _deadCodeEngine, _dependencyEngine, _architecturalEngine,
            _projectStructureEngine, _dependencyInjectionEngine, new ProjectConsistencyEngine(_workspaceManager),
            _metricsEngine, new CloneDetectionEngine(_workspaceManager), _discoveryEngine,
            new StackOverflowEngine(_workspaceManager), new CodeStyleEngine(_workspaceManager, _config),
            new CodeStyleAnalysisEngine(_workspaceManager),
            new RefactoringEngine(NullLogger<RefactoringEngine>.Instance, _workspaceManager, _config),
            _symbolNavigationEngine, new BreakingChangeEngine(_workspaceManager),
            _workspaceManager, NullLogger<SentinelScanTools>.Instance);
    }

    [TearDown]
    public void TearDown() => _workspaceManager?.Dispose();

    private void SetSource(string source, string fileName = "Test.cs")
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [(fileName, source)]);
        _workspaceManager.SetTestSolution(solution);
    }

    // --- GetComprehensiveHealthReport ---

    [Test]
    public async Task GetComprehensiveHealthReport_ValidSolution_ReturnsReport()
    {
        SetSource(RichSource, "Test.cs");
        var result = await _tools.GetComprehensiveHealthReport(reason: "test", timeoutSeconds: 5);
        Assert.That(result, Is.Not.Null);
    }

    // --- GetBlastRadius (via InspectSymbol) ---

    [Test]
    public async Task GetBlastRadius_ValidMethod_ReturnsReport()
    {
        SetSource(RichSource, "Test.cs");
        var result = await _symbolTools.InspectSymbol(reason: "test", "Test.cs", "ProcessAsync", InspectSymbolAspect.blastRadius);
        Assert.That(result, Is.Not.Null);
    }

    // --- FindMethodsByReturnType (via FindByName) ---

    [Ignore("API changed: FindByName removed")]
    [Test]
    public async Task FindMethodsByReturnType_ValidType_ReturnsList()
    {
        Assert.Ignore("API changed: FindByName removed");
    }

    // --- GetSolutionMetrics ---

    [Test]
    public async Task GetSolutionMetrics_LoadedSolution_ReturnsMetrics()
    {
        SetSource(RichSource, "Test.cs");
        var result = await _tools.GetSolutionMetrics(reason: "test");
        Assert.That(result, Is.Not.Null);
    }

    [Test]
    public async Task GetSolutionMetrics_WithProjectName_ReturnsMetrics()
    {
        SetSource(RichSource, "Test.cs");
        var result = await _tools.GetSolutionMetrics(reason: "test", "TestProj");
        Assert.That(result, Is.Not.Null);
    }

    // --- GetCodeInventory ---

    [Test]
    public async Task GetCodeInventory_ValidFile_ReturnsInventory()
    {
        SetSource(RichSource, "Test.cs");
        var result = await _tools.GetCodeInventory(reason: "test", "Test.cs");
        Assert.That(result, Is.Not.Null);
    }

    // --- FindUnusedPrivateMembers (via DeadCodeEngine) ---

    [Test]
    public async Task FindUnusedPrivateMembers_ValidClass_ReturnsList()
    {
        SetSource(RichSource, "Test.cs");
        var result = await _deadCodeEngine.FindUnusedPrivateMembersAsync("Test.cs", "Order");
        Assert.That(result, Is.Not.Null);
    }

    // --- DetectUnusedPrivateFields (via DeadCodeEngine) ---

    [Test]
    public async Task DetectUnusedPrivateFields_ValidFile_ReturnsList()
    {
        SetSource(RichSource, "Test.cs");
        var result = await _deadCodeEngine.DetectUnusedPrivateFieldsAsync("Test.cs");
        Assert.That(result, Is.Not.Null);
    }

    // --- DetectUnusedLocalVariables (via DeadCodeEngine) ---

    [Test]
    public async Task DetectUnusedLocalVariables_ValidFile_ReturnsList()
    {
        SetSource(RichSource, "Test.cs");
        var result = await _deadCodeEngine.DetectUnusedLocalVariablesAsync("Test.cs");
        Assert.That(result, Is.Not.Null);
    }

    // --- DetectLongParameterLists (via AnalysisEngine) ---

    [Test]
    public async Task DetectLongParameterLists_ValidSolution_ReturnsList()
    {
        SetSource(RichSource, "Test.cs");
        var result = await _analysisEngine.DetectLongParameterListsAsync();
        Assert.That(result, Is.Not.Null);
    }

    // --- FindUninstantiatedTypes (via AnalysisEngine) ---

    [Test]
    public async Task FindUninstantiatedTypes_ValidSolution_ReturnsList()
    {
        SetSource(RichSource, "Test.cs");
        var result = await _analysisEngine.FindUninstantiatedTypesAsync();
        Assert.That(result, Is.Not.Null);
    }

    // --- FindCircularDependencies (no params, via AnalysisEngine) ---

    [Test]
    public async Task FindCircularDependencies_NoParams_ReturnsList()
    {
        SetSource(RichSource, "Test.cs");
        var result = await _analysisEngine.FindCircularDependenciesAsync();
        Assert.That(result, Is.Not.Null);
    }

    // --- GenerateCallTree (via GetCallGraph "tree") ---

    [Test]
    public async Task GenerateCallTree_ValidMethod_ReturnsString()
    {
        SetSource(RichSource, "Test.cs");
        var result = await _tools.GetCallGraph(reason: "test", "Test.cs", "ProcessAsync", "tree");
        Assert.That(result, Is.Not.Null);
    }

    // --- DocumentPocoFields (via DocumentationEngine) ---

    [Test]
    public async Task DocumentPocoFields_ValidClass_ReturnsString()
    {
        SetSource(RichSource, "Test.cs");
        var result = await _documentationEngine.DocumentPocoFieldsAsync("Test.cs", "Order");
        Assert.That(result, Is.Not.Null);
    }

    // --- GenerateEqualityOverrides (via AnalysisEngine) ---

    [Test]
    public async Task GenerateEqualityOverrides_ValidClass_ReturnsString()
    {
        SetSource(RichSource, "Test.cs");
        var result = await _analysisEngine.GenerateEqualityOverridesAsync("Test.cs", "Order");
        Assert.That(result, Is.Not.Null);
    }

    // --- FindUnusedReferences (via DependencyEngine) ---

    [Test]
    public async Task FindUnusedReferences_ValidProject_ReturnsList()
    {
        SetSource(RichSource, "Test.cs");
        var result = await _dependencyEngine.FindUnusedReferencesAsync("TestProj");
        Assert.That(result, Is.Not.Null);
    }

    // --- CheckPackageInconsistency (via DependencyEngine) ---
    // Reads project.FilePath directly off disk (regexes <PackageReference> out of the raw
    // .csproj XML — Roslyn's in-memory Project model has no NuGet-version API), so unlike the
    // rest of this battery it can't run against TestSolutionBuilder's in-memory fake project
    // path. Uses a real on-disk solution via TestSolutionFixture instead.

    [Test]
    public async Task CheckPackageInconsistency_ValidSolution_ReturnsList()
    {
        using var fixture = new TestSolutionFixture();
        using var workspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        await workspaceManager.LoadSolutionAsync(fixture.SolutionPath);
        var dependencyEngine = new DependencyEngine(workspaceManager);

        var result = await dependencyEngine.CheckPackageInconsistencyAsync();
        Assert.That(result, Is.Not.Null);
    }

    // --- FindUnusedInterfaces (via AnalysisEngine) ---

    [Test]
    public async Task FindUnusedInterfaces_ValidSolution_ReturnsList()
    {
        SetSource(RichSource, "Test.cs");
        var result = await _analysisEngine.FindUnusedInterfacesAsync();
        Assert.That(result, Is.Not.Null);
    }

    // --- FindInternalClassesThatCouldBePrivate (via AnalysisEngine) ---

    [Test]
    public async Task FindInternalClassesThatCouldBePrivate_ValidSolution_ReturnsList()
    {
        SetSource(RichSource, "Test.cs");
        var result = await _analysisEngine.FindInternalClassesThatCouldBePrivateAsync();
        Assert.That(result, Is.Not.Null);
    }

    // --- FindLargeSwitchStatements (via AnalysisEngine) ---

    [Test]
    public async Task FindLargeSwitchStatements_ValidSolution_ReturnsList()
    {
        SetSource(RichSource, "Test.cs");
        var result = await _analysisEngine.FindLargeSwitchStatementsAsync();
        Assert.That(result, Is.Not.Null);
    }

    // --- FindStructuralSmells (via ProjectStructureEngine) ---

    [Test]
    public async Task FindStructuralSmells_ValidSolution_ReturnsList()
    {
        SetSource(RichSource, "Test.cs");
        var result = await _projectStructureEngine.FindStructuralSmellsAsync();
        Assert.That(result, Is.Not.Null);
    }

    // --- FindUnusedConstructors (via DeadCodeEngine) ---

    [Test]
    public async Task FindUnusedConstructors_ValidFile_ReturnsList()
    {
        SetSource(RichSource, "Test.cs");
        var result = await _deadCodeEngine.FindUnusedConstructorsAsync("Test.cs");
        Assert.That(result, Is.Not.Null);
    }

    // --- CheckForUnusedEventSubscriptions (via DeadCodeEngine) ---

    [Test]
    public async Task CheckForUnusedEventSubscriptions_ValidFile_ReturnsList()
    {
        SetSource(RichSource, "Test.cs");
        var result = await _deadCodeEngine.CheckForUnusedEventSubscriptionsAsync("Test.cs");
        Assert.That(result, Is.Not.Null);
    }

    // --- GetSymbolInfo (via InspectSymbol) ---

    [Test]
    public async Task GetSymbolInfo_ValidSymbolSnippet_ReturnsInfo()
    {
        SetSource(RichSource, "Test.cs");
        var result = await _symbolTools.InspectSymbol(reason: "test", "Test.cs", "ProcessAsync", InspectSymbolAspect.info);
        Assert.That(result, Is.Not.Null);
    }

    // --- FindAllImplementations (via FindByName) ---

    [Ignore("API changed: FindByName removed")]
    [Test]
    public async Task FindAllImplementations_ValidInterface_ReturnsList()
    {
        Assert.Ignore("API changed: FindByName removed");
    }

    // --- FindReadonlyFieldCandidates (via SymbolNavigationEngine) ---

    [Test]
    public async Task FindReadonlyFieldCandidates_ValidFile_ReturnsList()
    {
        SetSource(RichSource, "Test.cs");
        var result = await _symbolNavigationEngine.FindReadonlyFieldCandidatesAsync("Test.cs");
        Assert.That(result, Is.Not.Null);
    }

    // --- FindDiRegistrations (now GetDiRegistrations) ---

    [Test]
    public async Task FindDiRegistrations_ValidSolution_ReturnsList()
    {
        SetSource(RichSource, "Test.cs");
        var result = await _tools.GetDiRegistrations(reason: "test");
        Assert.That(result, Is.Not.Null);
    }

    // --- GetTypeMembersDetail (via GetTypeInfo) ---

    [Test]
    public async Task GetTypeMembersDetail_ValidType_ReturnsList()
    {
        SetSource(RichSource, "Test.cs");
        var result = await _symbolTools.GetTypeInfo(reason: "test", "Order", include: TypeInfoInclude.members);
        Assert.That(result, Is.Not.Null);
    }

    // --- FindExtensionMethods (via FindByName) ---

    [Ignore("API changed: FindByName removed")]
    [Test]
    public async Task FindExtensionMethods_ValidType_ReturnsList()
    {
        Assert.Ignore("API changed: FindByName removed");
    }

    // --- AnalyzeTypeCohesion (via MetricsEngine) ---

    [Test]
    public async Task AnalyzeTypeCohesion_ValidFile_ReturnsList()
    {
        SetSource(RichSource, "Test.cs");
        var result = await _metricsEngine.AnalyzeTypeCohesionAsync("Test.cs");
        Assert.That(result, Is.Not.Null);
    }

    // --- FindCircularDependencies (with projectName, via ArchitecturalEngine) ---

    [Test]
    public async Task FindCircularDependencies_WithProjectName_ReturnsList()
    {
        SetSource(RichSource, "Test.cs");
        var result = await _architecturalEngine.FindCircularDependenciesAsync("TestProj");
        Assert.That(result, Is.Not.Null);
    }

    // --- GetCallGraph ---

    [Test]
    public async Task GetCallGraph_ValidMethod_ReturnsCallGraph()
    {
        SetSource(RichSource, "Test.cs");
        var result = await _tools.GetCallGraph(reason: "test", "Test.cs", "ProcessAsync");
        Assert.That(result, Is.Not.Null);
    }

    [Test]
    public async Task GetCallGraph_NonExistentMethod_ReturnsStructuredError()
    {
        SetSource(RichSource, "Test.cs");
        var result = await _tools.GetCallGraph(reason: "test", "Test.cs", "NoSuchMethod99");

        Assert.That(result.Success, Is.False);
        Assert.That(result.Error, Is.Not.Null);
    }

    // --- GetReverseCallGraph (via GetCallGraph "reverse") ---

    [Test]
    public async Task GetReverseCallGraph_ValidMethod_ReturnsCallGraph()
    {
        SetSource(RichSource, "Test.cs");
        var result = await _tools.GetCallGraph(reason: "test", "Test.cs", "GetStatus", "reverse");
        Assert.That(result, Is.Not.Null);
    }

    [Test]
    public async Task GetReverseCallGraph_NonExistentMethod_ReturnsStructuredError()
    {
        SetSource(RichSource, "Test.cs");
        var result = await _tools.GetCallGraph(reason: "test", "Test.cs", "NoSuchMethod99", "reverse");

        Assert.That(result.Success, Is.False);
        Assert.That(result.Error, Is.Not.Null);
    }

    // --- MoveFileToNamespaceFolder ---

    [Test]
    public async Task MoveFileToNamespaceFolder_ValidFile_ReturnsString()
    {
        SetSource(RichSource, "Test.cs");
        var result = await _tools.PreviewMoveFileToNamespaceFolder(reason: "test", "Test.cs");
        Assert.That(result, Is.Not.Null);
    }

    // --- FindAllThrowSites (via DiscoveryEngine) ---

    [Test]
    public async Task FindAllThrowSites_ValidSolution_ReturnsList()
    {
        SetSource(RichSource, "Test.cs");
        var result = await _discoveryEngine.FindAllThrowSitesAsync();
        Assert.That(result, Is.Not.Null);
    }

    // --- FindObjectCreationSites (via FindByName) ---

    [Ignore("API changed: FindByName removed")]
    [Test]
    public async Task FindObjectCreationSites_ValidType_ReturnsList()
    {
        Assert.Ignore("API changed: FindByName removed");
    }

    // --- GetPublicApiSurface ---

    [Test]
    public async Task GetPublicApiSurface_ValidProject_ReturnsList()
    {
        SetSource(RichSource, "Test.cs");
        var result = await _scanTools.GetPublicApiSurface("TestProj");
        Assert.That(result, Is.Not.Null);
    }

    // --- FindServicesNotRegistered (via DependencyInjectionEngine) ---

    [Test]
    public async Task FindServicesNotRegistered_ValidSolution_ReturnsList()
    {
        SetSource(RichSource, "Test.cs");
        var result = await _dependencyInjectionEngine.FindServicesNotRegisteredAsync();
        Assert.That(result, Is.Not.Null);
    }

    // --- FindBestInsertionPoint (now GetBestInsertionPoint) ---

    [Test]
    public async Task FindBestInsertionPoint_ValidClass_ReturnsResult()
    {
        SetSource(RichSource, "Test.cs");
        var result = await _symbolTools.GetBestInsertionPoint(reason: "test", "Test.cs", "Order", "method");
        Assert.That(result, Is.Not.Null);
    }

    // --- FindTodoFixmeComments (via DiscoveryEngine) ---

    [Test]
    public async Task FindTodoFixmeComments_ValidSolution_ReturnsList()
    {
        SetSource(RichSource, "Test.cs");
        var result = await _discoveryEngine.FindTodoFixmeCommentsAsync();
        Assert.That(result, Is.Not.Null);
    }

    // --- PreviewRenameImpact ---

    [Test]
    public async Task PreviewRenameImpact_ValidSymbol_ReturnsPreview()
    {
        SetSource(RichSource, "Test.cs");
        var result = await _symbolTools.PreviewRenameImpact(reason: "test", filepath: "Test.cs", symbolName: "ProcessAsync");
        Assert.That(result, Is.Not.Null);
    }

    // --- FindCallersSafe (via FindReferences) ---

    [Test]
    public async Task FindCallersSafe_ValidSymbol_ReturnsList()
    {
        SetSource(RichSource, "Test.cs");
        var result = await _symbolTools.FindReferences(reason: "test", "ProcessAsync", FindReferencesKind.callers, filepath: "Test.cs");
        Assert.That(result, Is.Not.Null);
    }

    // --- FindImplementationsSafe (via FindReferences) ---

    [Test]
    public async Task FindImplementationsSafe_ValidInterface_ReturnsList()
    {
        SetSource(RichSource, "Test.cs");
        var result = await _symbolTools.FindReferences(reason: "test", "IOrderService", FindReferencesKind.implementations, filepath: "Test.cs");
        Assert.That(result, Is.Not.Null);
    }

    // --- FindReferences(kind: all) ---

    [Test]
    public async Task FindReferences_KindAll_ReturnsBothCallersAndImplementations()
    {
        SetSource(RichSource, "Test.cs");
        var result = await _symbolTools.FindReferences(reason: "test", "ProcessAsync", FindReferencesKind.all, filepath: "Test.cs");

        Assert.That(result.Success, Is.True);
        Assert.That(result.Data, Is.Not.Null);
        var json = System.Text.Json.JsonSerializer.Serialize(result.Data);
        Assert.That(json, Does.Contain("callers"));
        Assert.That(json, Does.Contain("implementations"));
    }

    // --- QuerySymbolRelationships (renamed from FindUsages) ---

    [Test]
    public async Task QuerySymbolRelationships_ObjectCreationsForRealType_ReturnsResult()
    {
        SetSource(RichSource, "Test.cs");
        var result = await _symbolTools.QuerySymbolRelationships(reason: "test", "Order", FindUsagesSearchKind.objectCreations);
        Assert.That(result, Is.Not.Null);
        Assert.That(result.Success, Is.True);
    }

    [Test]
    public async Task QuerySymbolRelationships_ObjectCreationsForMethodName_ReturnsSemanticGuardError()
    {
        SetSource(RichSource, "Test.cs");
        var result = await _symbolTools.QuerySymbolRelationships(reason: "test", "ProcessAsync", FindUsagesSearchKind.objectCreations);

        Assert.That(result.Success, Is.False, "objectCreations against a method name must be rejected, not silently return [].");
        Assert.That(result.Error, Is.Not.Null);
        Assert.That(result.Error!.Message, Does.Contain("FindReferences"),
            "The guard should point the caller at FindReferences instead of objectCreations for a member name.");
    }

    [Test]
    public async Task QuerySymbolRelationships_EmptyTargetedKind_BroadensAndFindsUnderAnotherKind()
    {
        SetSource(RichSource, "Test.cs");
        // "IOrderService" has zero attribute usages, but a real implementor exists (OrderService) —
        // broaden-on-empty should surface that under 'implementorsOf' instead of just returning [].
        var result = await _symbolTools.QuerySymbolRelationships(reason: "test", "IOrderService", FindUsagesSearchKind.attributeUsages);

        Assert.That(result.Success, Is.True);
        Assert.That(result.Warning, Is.Not.Null.And.Contains("Broadened"));
        Assert.That(result.Warning, Does.Contain("implementorsOf"));
    }

    [Test]
    public async Task QuerySymbolRelationships_EmptyUnderAllKinds_ReturnsPlainNotFoundSignal()
    {
        SetSource(RichSource, "Test.cs");
        var result = await _symbolTools.QuerySymbolRelationships(reason: "test", "ThisNameAppearsNowhereInTheSolution", FindUsagesSearchKind.attributeUsages);

        Assert.That(result.Success, Is.True);
        Assert.That(result.Warning, Does.Contain("nothing found under any kind"));
    }
}
