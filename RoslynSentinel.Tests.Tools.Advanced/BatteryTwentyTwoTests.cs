// Battery #22 -> IntelligenceTools
// Tests all 45 public methods of IntelligenceTools in-memory via TestSolutionBuilder.
using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Engines.Advanced;
using RoslynSentinel.Engines.Basic;
using RoslynSentinel.Tools.Advanced;
using RoslynSentinel.Tools.Basic;

#pragma warning disable CS8618
namespace RoslynSentinel.Tests.Tools.Advanced;

[TestFixture]
public class BatteryTwentyTwoTests
{
    private IWorkspaceManager _workspaceManager;
    private SentinelConfiguration _config;
    private ImpactAnalyzer _impactAnalyzer;
    private DiscoveryEngine _semanticSearchEngine;
    private MetricsEngine _metricsEngine;
    private InventoryEngine _inventoryEngine;
    private DeadCodeEngine _deadCodeEngine;
    private DocumentationEngine _documentationEngine;
    private DependencyEngine _dependencyEngine;
    private SolutionStructureEngine _solutionStructureEngine;
    private AsyncAnalysisEngine _asyncSafetyEngine;
    private HealthOrchestrationEngine _healthOrchestrationEngine;
    private SymbolNavigationEngine _symbolNavigationEngine;
    private DependencyInjectionEngine _dependencyInjectionEngine;
    private DiscoveryEngine _discoveryEngine;
    private IntelligenceTools _tools;
    private SymbolRelationshipTools _symbolRelationshipTools;
    private SymbolNavigationTools _symbolNavigationTools;
    private ScanTools _scanTools;
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
    private const string GenericSource = """
        namespace TestProj;

        public class EngineResultWrapper<T>
        {
            public T? Value { get; set; }
        }

        public class PairWrapper<TKey, TValue>
        {
            public TKey? Key { get; set; }
        }

        public class PlainType
        {
            public int Count { get; set; }
        }
        """;

    [SetUp]
    public void Setup()
    {
        _workspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        _config = new SentinelConfiguration();
        _impactAnalyzer = new ImpactAnalyzer(_workspaceManager, NullLogger<ImpactAnalyzer>.Instance);
        _semanticSearchEngine = new DiscoveryEngine(_workspaceManager);
        _metricsEngine = new MetricsEngine(_workspaceManager);
        _inventoryEngine = new InventoryEngine(_workspaceManager);
        _deadCodeEngine = new DeadCodeEngine(_workspaceManager, _config);
        _documentationEngine = new DocumentationEngine(_workspaceManager);
        _dependencyEngine = new DependencyEngine(_workspaceManager);
        _solutionStructureEngine = new SolutionStructureEngine(_workspaceManager, _config);
        _asyncSafetyEngine = new AsyncAnalysisEngine(_workspaceManager);
        _healthOrchestrationEngine = new HealthOrchestrationEngine(_workspaceManager, _solutionStructureEngine, _config, new PerformanceEngine(_workspaceManager), new AntiPatternEngine(_workspaceManager, _config));
        _antiPatternEngine = new AntiPatternEngine(_workspaceManager, _config);
        _symbolNavigationEngine = new SymbolNavigationEngine(_workspaceManager, NullLogger<SymbolNavigationEngine>.Instance);
        _dependencyInjectionEngine = new DependencyInjectionEngine(_workspaceManager);
        _discoveryEngine = new DiscoveryEngine(_workspaceManager, _symbolNavigationEngine);
        _tools = new IntelligenceTools(_impactAnalyzer, _metricsEngine, _inventoryEngine, _deadCodeEngine, _documentationEngine, _dependencyEngine, _solutionStructureEngine, _asyncSafetyEngine, _healthOrchestrationEngine, _symbolNavigationEngine, _dependencyInjectionEngine, _discoveryEngine, new ProjectConsistencyEngine(_workspaceManager), _workspaceManager, new AntiPatternEngine(_workspaceManager), config: _config, logger: NullLogger<IntelligenceTools>.Instance);
        // Symbol-level tools moved to SymbolNavigationTools (Basic) in the server split.
        _symbolRelationshipTools = new SymbolRelationshipTools(new SymbolRelationshipImpl(_discoveryEngine, _symbolNavigationEngine, _workspaceManager, NullLogger<SymbolRelationshipImpl>.Instance));
        _symbolNavigationTools = new SymbolNavigationTools(new SymbolNavigationImpl(_symbolNavigationEngine, _impactAnalyzer, _workspaceManager, NullLogger<SymbolNavigationImpl>.Instance));
        // GetPublicApiSurface moved to ScanTools (Advanced).
        _scanTools = new ScanTools(new SecurityEngine(_workspaceManager), new AntiPatternEngine(_workspaceManager), _asyncSafetyEngine, new ThreadSafetyEngine(_workspaceManager), new ControlFlowEngine(_workspaceManager), new PerformanceEngine(_workspaceManager), _deadCodeEngine, _dependencyEngine, _solutionStructureEngine, _dependencyInjectionEngine, new ProjectConsistencyEngine(_workspaceManager), _metricsEngine, new CloneDetectionEngine(_workspaceManager), _discoveryEngine, new StackOverflowEngine(_workspaceManager), new CodeStyleEngine(_workspaceManager, _config), new CodeStyleAnalysisEngine(_workspaceManager), new BasicRefactoringEngine(_workspaceManager, NullLogger<BasicRefactoringEngine>.Instance, _config), _symbolNavigationEngine, new BreakingChangeEngine(_workspaceManager), _workspaceManager, new ResourceSafetyEngine(_workspaceManager), NullLogger<ScanTools>.Instance);

        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Wrappers.cs", RichSource)]);
        _workspaceManager.SetTestSolution(solution);
    }

    [TearDown]
    public void TearDown() => _workspaceManager?.Dispose();
    private void SetSource(string source, string fileName = "Test.cs")
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [(fileName, source)]);
        _workspaceManager.SetTestSolution(solution);
    }

    // --- RunScanDetector ---
    [Test]
    public async Task RunScanDetector_UnusedReferencesWithoutProjectScope_ReturnsInvalidArgument()
    {
        SetSource(RichSource, "Test.cs");
        var result = await _scanTools.RunScanDetector(reason: "test message", detector: ScanTools.DetectorId.unused_references, scope: ToolScope.file, filePath: "Test.cs");
        Assert.That(result.IsSuccess, Is.False);
        Assert.That(result.ErrorData, Is.Not.Null);
        Assert.That(result.ErrorData!.ErrorCode, Is.EqualTo(ToolErrorCode.InvalidArgument));
    }

    // --- GetComprehensiveHealthReport ---
    [Test]
    public async Task GetComprehensiveHealthReport_ValidSolution_ReturnsReport()
    {
        SetSource(RichSource, "Test.cs");
        var result = await _tools.GetComprehensiveHealthReport(reason: "test message", timeoutSeconds: 5);
        Assert.That(result, Is.Not.Null);
        Assert.That(result.IsSuccess, Is.True);
    }

    // --- GetBlastRadius (via InspectSymbol) ---
    [Test]
    public async Task GetBlastRadius_ValidMethod_ReturnsReport()
    {
        SetSource(RichSource, "Test.cs");
        var result = await _symbolNavigationTools.InspectSymbol(reason: "test message", "Test.cs", "ProcessAsync", InspectSymbolAspect.blastRadius);
        Assert.That(result, Is.Not.Null);
        Assert.That(result.IsSuccess, Is.True);
    }

    [Test]
    public async Task GetBlastRadius_ValidMethod_ReportHasNoErrorAndNamesSymbol()
    {
        SetSource(RichSource, "Test.cs");
        var result = await _symbolNavigationTools.InspectSymbol(reason: "test message", "Test.cs", "ProcessAsync", InspectSymbolAspect.blastRadius);
        Assert.That(result, Is.Not.Null);
        Assert.That(result.IsSuccess, Is.True);
        var report = (ImpactReport)result.SuccessData!;
        Assert.That(report.Error, Is.Null);
        Assert.That(report.SymbolName, Does.Contain("ProcessAsync"));
    }

    [Test]
    public async Task GetBlastRadius_UnresolvableSnippet_ReturnsNotFoundError()
    {
        SetSource(RichSource, "Test.cs");
        var result = await _symbolNavigationTools.InspectSymbol(reason: "test message", "Test.cs", "ThisTextDoesNotExistAnywhere", InspectSymbolAspect.blastRadius);
        Assert.That(result, Is.Not.Null);
        Assert.That(result.IsSuccess, Is.False);
        Assert.That(result.ErrorData, Is.Not.Null);
        Assert.That(result.ErrorData!.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound.ToString()));
        Assert.That(result.ErrorData!.Message, Is.Not.Null.And.Not.Empty);
    }

    [Test]
    public async Task InspectSymbol_UnresolvableSnippet_InfoAndBlastRadiusAgree()
    {
        SetSource(RichSource, "Test.cs");
        var resultInfo = await _symbolNavigationTools.InspectSymbol(reason: "test message", "Test.cs", "ThisTextDoesNotExistAnywhere", InspectSymbolAspect.info);
        var resultBlastRadius = await _symbolNavigationTools.InspectSymbol(reason: "test message", "Test.cs", "ThisTextDoesNotExistAnywhere", InspectSymbolAspect.blastRadius);
        Assert.That(resultInfo.IsSuccess, Is.False);
        Assert.That(resultBlastRadius.IsSuccess, Is.False);
        Assert.That(resultInfo.ErrorData, Is.Not.Null);
        Assert.That(resultBlastRadius.ErrorData, Is.Not.Null);
        Assert.That(resultInfo.ErrorData!.Message, Is.Not.Null.And.Not.Empty);
        Assert.That(resultBlastRadius.ErrorData!.Message, Is.Not.Null.And.Not.Empty);
    }

    // --- FindMethodsByReturnType (via QuerySymbolRelationships) ---
    [Test]
    public async Task FindMethodsByReturnType_ValidType_ReturnsList()
    {
        var result = await _symbolRelationshipTools.QuerySymbolRelationships(reason: "test message", "List<string>", FindUsagesSearchKind.methodsByReturnType);
        Assert.That(result, Is.Not.Null);
        Assert.That(result.TotalRecords > 0);
    }

    // --- GetSolutionMetrics ---
    [Test]
    public async Task GetSolutionMetrics_LoadedSolution_ReturnsMetrics()
    {
        SetSource(RichSource, "Test.cs");
        var result = await _tools.GetSolutionMetrics(reason: "test message");
        Assert.That(result, Is.Not.Null);
        Assert.That(result.IsSuccess, Is.True);
    }

    [Test]
    public async Task GetSolutionMetrics_WithProjectName_ReturnsMetrics()
    {
        SetSource(RichSource, "Test.cs");
        var result = await _tools.GetSolutionMetrics(reason: "test message", "TestProj");
        Assert.That(result, Is.Not.Null);
        Assert.That(result.IsSuccess, Is.True);
    }

    // --- GetCodeInventory ---
    [Test]
    public async Task GetCodeInventory_ValidFile_ReturnsInventory()
    {
        SetSource(RichSource, "Test.cs");
        var result = await _tools.GetCodeInventory(reason: "test message", "Test.cs");
        Assert.That(result, Is.Not.Null);
        //Assert.That(result.TotalRecords > 0);
    }

    // --- FindUnusedPrivateMembers (via DeadCodeEngine) ---
    [Test]
    public async Task FindUnusedPrivateMembers_ValidClass_ReturnsList()
    {
        SetSource(RichSource, "Test.cs");
        var result = await _deadCodeEngine.FindUnusedPrivateMembersAsync("Test.cs", "Order");
        Assert.That(result, Is.Not.Null);
        //Assert.That(result.Count > 0);
    }

    // --- DetectUnusedPrivateFields (via DeadCodeEngine) ---
    [Test]
    public async Task DetectUnusedPrivateFields_ValidFile_ReturnsList()
    {
        SetSource(RichSource, "Test.cs");
        var result = await _deadCodeEngine.DetectUnusedPrivateFieldsAsync("Test.cs");
        Assert.That(result, Is.Not.Null);
        //Assert.That(result.Count > 0);
    }

    // --- DetectUnusedLocalVariables (via DeadCodeEngine) ---
    [Test]
    public async Task DetectUnusedLocalVariables_ValidFile_ReturnsList()
    {
        SetSource(RichSource, "Test.cs");
        var result = await _deadCodeEngine.DetectUnusedLocalVariablesAsync("Test.cs");
        Assert.That(result, Is.Not.Null);
        //Assert.That(result.Count > 0);
    }

    // --- DetectLongParameterLists (via AnalysisEngine) ---
    [Test]
    public async Task DetectLongParameterLists_ValidSolution_ReturnsList()
    {
        SetSource(RichSource, "Test.cs");
        var result = await _antiPatternEngine.DetectLongParameterListsAsync();
        Assert.That(result, Is.Not.Null);
        //Assert.That(result.Count > 0);
    }

    // --- FindUninstantiatedTypes (via AnalysisEngine) ---
    [Test]
    public async Task FindUninstantiatedTypes_ValidSolution_ReturnsList()
    {
        SetSource(RichSource, "Test.cs");
        var result = await _deadCodeEngine.FindUninstantiatedTypesAsync();
        Assert.That(result, Is.Not.Null);
        //Assert.That(result.Count > 0);
    }

    // --- FindCircularDependencies (no params, via AnalysisEngine) ---
    [Test]
    public async Task FindCircularDependencies_NoParams_ReturnsList()
    {
        SetSource(RichSource, "Test.cs");
        var result = await _solutionStructureEngine.FindCircularDependenciesAsync();
        Assert.That(result, Is.Not.Null);
        //Assert.That(result.Count > 0);
    }

    // --- GenerateCallTree (via GetCallGraph "tree") ---
    [Test]
    public async Task GenerateCallTree_ValidMethod_ReturnsString()
    {
        SetSource(RichSource, "Test.cs");
        var result = await _tools.GetCallGraph(reason: "test message", "Test.cs", "ProcessAsync", "tree");
        Assert.That(result.SuccessData, Is.Not.Null);
        //Assert.That(result.TotalRecords > 0);
    }

    // --- DocumentPocoFields (via DocumentationEngine) ---
    [Test]
    public async Task DocumentPocoFields_ValidClass_ReturnsString()
    {
        SetSource(RichSource, "Test.cs");
        var result = await _documentationEngine.DocumentPocoFieldsAsync("Test.cs", "Order");
        Assert.That(result, Is.Not.Null);
        Assert.That(result.Outcome == EditOutcome.Modified);
    }

    // --- GenerateEqualityOverrides (via AnalysisEngine) ---
    [Test]
    public async Task GenerateEqualityOverrides_ValidClass_ReturnsString()
    {
        SetSource(RichSource, "Test.cs");
        var result = await _antiPatternEngine.GenerateEqualityOverridesAsync("Test.cs", "Order");
        Assert.That(result, Is.Not.Null);
        Assert.That(result.Outcome == EditOutcome.Modified);
    }

    // --- FindUnusedReferences (via DependencyEngine) ---
    [Test]
    public async Task FindUnusedReferences_ValidProject_ReturnsList()
    {
        SetSource(RichSource, "Test.cs");
        var result = await _dependencyEngine.FindUnusedReferencesAsync("TestProj");
        Assert.That(result, Is.Not.Null);
        //Assert.That(result.Count > 0);
    }

    // --- CheckPackageInconsistency (via DependencyEngine) ---
    // Reads project.FilePathWrapper directly off disk (regexes <PackageReference> out of the raw
    // .csproj XML -> Roslyn's in-memory Project model has no NuGet-version API), so unlike the
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
        //Assert.That(result.Count > 0);
    }

    // --- FindUnusedInterfaces (via AnalysisEngine) ---
    [Test]
    public async Task FindUnusedInterfaces_ValidSolution_ReturnsList()
    {
        SetSource(RichSource, "Test.cs");
        var result = await _deadCodeEngine.FindUnusedInterfacesAsync();
        Assert.That(result, Is.Not.Null);
        //Assert.That(result.Count > 0);
    }

    // --- FindInternalClassesThatCouldBePrivate (via AnalysisEngine) ---
    [Test]
    public async Task FindInternalClassesThatCouldBePrivate_ValidSolution_ReturnsList()
    {
        SetSource(RichSource, "Test.cs");
        var result = await _deadCodeEngine.FindInternalClassesThatCouldBePrivateAsync();
        Assert.That(result, Is.Not.Null);
        //Assert.That(result.Count > 0);
    }

    // --- FindLargeSwitchStatements (via AnalysisEngine) ---
    [Test]
    public async Task FindLargeSwitchStatements_ValidSolution_ReturnsList()
    {
        SetSource(RichSource, "Test.cs");
        var result = await _antiPatternEngine.FindLargeSwitchStatementsAsync();
        Assert.That(result, Is.Not.Null);
        //Assert.That(result.Count > 0);
    }

    // --- FindStructuralSmells (via SolutionStructureEngine) ---
    [Test]
    public async Task FindStructuralSmells_ValidSolution_ReturnsList()
    {
        SetSource(RichSource, "Test.cs");
        var result = await _solutionStructureEngine.FindStructuralSmellsAsync();
        Assert.That(result, Is.Not.Null);
        Assert.That(result.Count > 0);
    }

    // --- FindUnusedConstructors (via DeadCodeEngine) ---
    [Test]
    public async Task FindUnusedConstructors_ValidFile_ReturnsList()
    {
        SetSource(RichSource, "Test.cs");
        var result = await _deadCodeEngine.FindUnusedConstructorsAsync("Test.cs");
        Assert.That(result, Is.Not.Null);
        //Assert.That(result.Count > 0);
    }

    // --- CheckForUnusedEventSubscriptions (via DeadCodeEngine) ---
    [Test]
    public async Task CheckForUnusedEventSubscriptions_ValidFile_ReturnsList()
    {
        SetSource(RichSource, "Test.cs");
        var result = await _deadCodeEngine.CheckForUnusedEventSubscriptionsAsync("Test.cs");
        Assert.That(result, Is.Not.Null);
        //Assert.That(result, Is.Not.Null);
    }

    // --- GetSymbolInfo (via InspectSymbol) ---
    [Test]
    public async Task GetSymbolInfo_ValidSymbolSnippet_ReturnsInfo()
    {
        SetSource(RichSource, "Test.cs");
        var result = await _symbolNavigationTools.InspectSymbol(reason: "test message", "Test.cs", "ProcessAsync", InspectSymbolAspect.info);
        Assert.That(result, Is.Not.Null);
        //Assert.That(result.TotalRecords > 0);
    }

    // --- FindAllImplementations (via QuerySymbolRelationships) ---
    [Test]
    public async Task FindAllImplementations_ValidInterface_ReturnsList()
    {
        var result = await _symbolRelationshipTools.QuerySymbolRelationships(reason: "test message", "IOrderService", FindUsagesSearchKind.implementorsOf);
        Assert.That(result, Is.Not.Null);
        Assert.That(result.TotalRecords > 0);
    }

    // --- FindReadonlyFieldCandidates (via SymbolNavigationEngine) ---
    [Test]
    public async Task FindReadonlyFieldCandidates_ValidFile_ReturnsList()
    {
        SetSource(RichSource, "Test.cs");
        var result = await _symbolNavigationEngine.FindReadonlyFieldCandidatesAsync("Test.cs");
        Assert.That(result, Is.Not.Null);
        //Assert.That(result.Count > 0);
    }

    // --- FindDiRegistrations (now GetDiRegistrations) ---
    [Test]
    public async Task FindDiRegistrations_ValidSolution_ReturnsList()
    {
        SetSource(RichSource, "Test.cs");
        var result = await _tools.GetDiRegistrations(reason: "test message");
        Assert.That(result, Is.Not.Null);
        //Assert.That(result.TotalRecords > 0);
    }

    // --- GetTypeMembersDetail (via GetTypeInfo) ---
    [Test]
    public async Task GetTypeMembersDetail_ValidType_ReturnsList()
    {
        SetSource(RichSource, "Test.cs");
        var result = await _symbolNavigationTools.GetTypeInfo(reason: "test message", "Order", include: TypeInfoInclude.members);
        Assert.That(result, Is.Not.Null);
        //Assert.That(result.TotalRecords > 0);
    }

    // --- FindExtensionMethods (via QuerySymbolRelationships) ---
    [Test]
    public async Task FindExtensionMethods_ValidType_ReturnsList()
    {
        var result = await _symbolRelationshipTools.QuerySymbolRelationships(reason: "test message", "Order", FindUsagesSearchKind.extensionsFor);
        Assert.That(result, Is.Not.Null);
        Assert.That(result.TotalRecords > 0);
    }

    // --- AnalyzeTypeCohesion (via MetricsEngine) ---
    [Test]
    public async Task AnalyzeTypeCohesion_ValidFile_ReturnsList()
    {
        SetSource(RichSource, "Test.cs");
        var result = await _metricsEngine.AnalyzeTypeCohesionAsync("Test.cs");
        Assert.That(result, Is.Not.Null);
        Assert.That(result.Count > 0);
    }

    // --- FindCircularDependencies (with projectName, via SolutionStructureEngine) ---
    [Test]
    public async Task FindCircularDependencies_WithProjectName_ReturnsList()
    {
        SetSource(RichSource, "Test.cs");
        var result = await _solutionStructureEngine.FindCircularDependenciesAsync("TestProj");
        Assert.That(result, Is.Not.Null);
        //Assert.That(result.Count > 0);
    }

    // --- GetCallGraph ---
    [Test]
    public async Task GetCallGraph_ValidMethod_ReturnsCallGraph()
    {
        SetSource(RichSource, "Test.cs");
        var result = await _tools.GetCallGraph(reason: "test message", "Test.cs", "ProcessAsync");
        Assert.That(result, Is.Not.Null);
        //Assert.That(result.TotalRecords > 0);
    }

    [Test]
    public async Task GetCallGraph_NonExistentMethod_ReturnsStructuredError()
    {
        SetSource(RichSource, "Test.cs");
        var result = await _tools.GetCallGraph(reason: "test message", "Test.cs", "NoSuchMethod99");
        Assert.That(result.IsSuccess, Is.False);
        Assert.That(result.ErrorData, Is.Not.Null);
    }

    // --- GetReverseCallGraph (via GetCallGraph "reverse") ---
    [Test]
    public async Task GetReverseCallGraph_ValidMethod_ReturnsCallGraph()
    {
        SetSource(RichSource, "Test.cs");
        var result = await _tools.GetCallGraph(reason: "test message", "Test.cs", "GetStatus", "reverse");
        Assert.That(result, Is.Not.Null);
        Assert.That(result.IsSuccess, Is.True);
    }

    [Test]
    public async Task GetReverseCallGraph_NonExistentMethod_ReturnsStructuredError()
    {
        SetSource(RichSource, "Test.cs");
        var result = await _tools.GetCallGraph(reason: "test message", "Test.cs", "NoSuchMethod99", "reverse");
        Assert.That(result.IsSuccess, Is.False);
        Assert.That(result.ErrorData, Is.Not.Null);
    }

    // --- MoveFileToNamespaceFolder ---
    [Test]
    public async Task MoveFileToNamespaceFolder_ValidFile_ReturnsString()
    {
        SetSource(RichSource, "Test.cs");
        var result = await _tools.PreviewMoveFileToNamespaceFolder(reason: "test message", "Test.cs");
        Assert.That(result, Is.Not.Null);
        Assert.That(result.SuccessData, Is.Not.Null);
        Assert.That(result.IsSuccess, Is.True);
    }

    // --- FindAllThrowSites (via DiscoveryEngine) ---
    [Test]
    public async Task FindAllThrowSites_ValidSolution_ReturnsList()
    {
        SetSource(RichSource, "Test.cs");
        var result = await _discoveryEngine.FindAllThrowSitesAsync();
        Assert.That(result, Is.Not.Null);
        //Assert.That(result.Count > 0);
    }

    // --- FindObjectCreationSites (via FindByName) ---
    [Test]
    public async Task FindObjectCreationSites_ValidType_ReturnsList()
    {
        var result = await _symbolRelationshipTools.QuerySymbolRelationships(reason: "test message", "", FindUsagesSearchKind.objectCreations);
        Assert.That(result, Is.Not.Null);
        Assert.That(result.TotalRecords > 0);
    }

    // --- GetPublicApiSurface ---
    [Test]
    public async Task GetPublicApiSurface_ValidProject_ReturnsList()
    {
        SetSource(RichSource, "Test.cs");
        var result = await _scanTools.GetPublicApiSurface("TestProj");
        Assert.That(result, Is.Not.Null);
        //Assert.That(result.TotalRecords > 0);
    }

    // --- FindServicesNotRegistered (via DependencyInjectionEngine) ---
    [Test]
    public async Task FindServicesNotRegistered_ValidSolution_ReturnsList()
    {
        SetSource(RichSource, "Test.cs");
        var result = await _dependencyInjectionEngine.FindServicesNotRegisteredAsync();
        Assert.That(result, Is.Not.Null);
        //Assert.That(result.Count > 0);
    }

    // --- FindBestInsertionPoint (now GetBestInsertionPoint) ---
    [Test]
    public async Task FindBestInsertionPoint_ValidClass_ReturnsResult()
    {
        SetSource(RichSource, "Test.cs");
        var result = await _symbolRelationshipTools.GetBestInsertionPoint(reason: "test message", "Test.cs", "Order", InsertionMemberKind.method);
        Assert.That(result, Is.Not.Null);
        Assert.That(result.IsSuccess, Is.True);
    }

    // --- FindTodoFixmeComments (via DiscoveryEngine) ---
    [Test]
    public async Task FindTodoFixmeComments_ValidSolution_ReturnsList()
    {
        SetSource(RichSource, "Test.cs");
        var result = await _discoveryEngine.FindTodoFixmeCommentsAsync();
        Assert.That(result, Is.Not.Null);
        //Assert.That(result.Count > 0);
    }

    // --- PreviewRenameImpact ---
    [Test]
    public async Task PreviewRenameImpact_ValidSymbol_ReturnsPreview()
    {
        SetSource(RichSource, "Test.cs");
        var result = await _symbolRelationshipTools.PreviewRenameImpact(reason: "test message", filePath: "Test.cs", symbolName: "ProcessAsync");
        Assert.That(result, Is.Not.Null);
        //Assert.That(result.TotalRecords > 0);
    }

    // --- FindCallersSafe (via FindReferences) ---
    [Test]
    public async Task FindCallersSafe_ValidSymbol_ReturnsList()
    {
        SetSource(RichSource, "Test.cs");
        var result = await _symbolRelationshipTools.FindReferences(reason: "test message", "ProcessAsync", FindReferencesKind.callers, filePath: "Test.cs");
        Assert.That(result, Is.Not.Null);
        //Assert.That(result.TotalRecords > 0);
    }

    // --- FindImplementationsSafe (via FindReferences) ---
    [Test]
    public async Task FindImplementationsSafe_ValidInterface_ReturnsList()
    {
        SetSource(RichSource, "Test.cs");
        var result = await _symbolRelationshipTools.FindReferences(reason: "test message", "IOrderService", FindReferencesKind.implementations, filePath: "Test.cs");
        Assert.That(result, Is.Not.Null);
        //Assert.That(result.TotalRecords > 0);
    }

    // --- FindReferences(kind: all) ---
    [Test]
    public async Task FindReferences_KindAll_ReturnsBothCallersAndImplementations()
    {
        SetSource(RichSource, "Test.cs");
        var result = await _symbolRelationshipTools.FindReferences(reason: "test message", "ProcessAsync", FindReferencesKind.all, filePath: "Test.cs");
        Assert.That(result.IsSuccess, Is.True);
        Assert.That(result.SuccessData, Is.Not.Null);
        var json = System.Text.Json.JsonSerializer.Serialize(result.SuccessData);
        Assert.That(json, Does.Contain("callers"));
        Assert.That(json, Does.Contain("implementations"));
    }

    // --- QuerySymbolRelationships (renamed from FindUsages) ---
    [Test]
    public async Task QuerySymbolRelationships_ObjectCreationsForRealType_ReturnsResult()
    {
        SetSource(RichSource, "Test.cs");
        var result = await _symbolRelationshipTools.QuerySymbolRelationships(reason: "test message", "Order", FindUsagesSearchKind.objectCreations);
        Assert.That(result, Is.Not.Null);
        Assert.That(result.IsSuccess, Is.True);
    }

    [Test]
    public async Task QuerySymbolRelationships_ObjectCreationsForMethodName_ReturnsSemanticGuardError()
    {
        SetSource(RichSource, "Test.cs");
        var result = await _symbolRelationshipTools.QuerySymbolRelationships(reason: "test message", "ProcessAsync", FindUsagesSearchKind.objectCreations);
        Assert.That(result.IsSuccess, Is.False, "objectCreations against a method name must be rejected, not silently return [].");
        Assert.That(result.ErrorData, Is.Not.Null);
        Assert.That(result.ErrorData!.Message, Does.Contain("FindReferences"), "The guard should point the caller at FindReferences instead of objectCreations for a member name.");
    }

    [Test]
    public async Task QuerySymbolRelationships_EmptyTargetedKind_BroadensAndFindsUnderAnotherKind()
    {
        SetSource(RichSource, "Test.cs");
        // "IOrderService" has zero attribute usages, but a real implementor exists (OrderService) ->
        // broaden-on-empty should surface that under 'implementorsOf' instead of just returning [].
        var result = await _symbolRelationshipTools.QuerySymbolRelationships(reason: "test message", "IOrderService", FindUsagesSearchKind.attributeUsages);
        Assert.That(result.IsSuccess, Is.True);
        Assert.That(result.WarningDetails, Is.Not.Null.And.Contains("Broadened"));
        Assert.That(result.WarningDetails, Does.Contain("implementorsOf"));
    }

    [Test]
    public async Task QuerySymbolRelationships_EmptyUnderAllKinds_ReturnsPlainNotFoundSignal()
    {
        SetSource(RichSource, "Test.cs");
        var result = await _symbolRelationshipTools.QuerySymbolRelationships(reason: "test message", "ThisNameAppearsNowhereInTheSolution", FindUsagesSearchKind.attributeUsages);
        Assert.That(result.IsSuccess, Is.True);
        Assert.That(result.WarningDetails, Does.Contain("nothing found under any kind"));
    }


    [Test]
    public async Task QuerySymbolRelationships_AttributeUsages_TopLevelClassTarget_ReturnsMatchNotCrash()
    {
        // Regression test for blocking_error_findattributeusages_first_throws_on_unresolved_target.md:
        // FindAttributeUsagesAsync used to pass containingType: "" (empty string) for every top-level
        // type declaration, but LocateSymbolAsync's containingType filter only activates when the
        // argument is non-null - and a top-level type's own symbol.ContainingType is null, so "" != null
        // always excluded it, leaving LocateSymbolAsync's result empty and an unguarded .First() call
        // throwing InvalidOperationException for every single top-level attribute usage.
        const string source = @"
using System;
namespace TestProj;

public class ProbeAttribute : Attribute { }

[Probe]
public class AttributedTarget { }
";
        SetSource(source, "AttributeProbe.cs");
        var result = await _symbolRelationshipTools.QuerySymbolRelationships(reason: "test message", "Probe", FindUsagesSearchKind.attributeUsages);
        Assert.That(result.IsSuccess, Is.True, result.ErrorData?.Message);
        var sites = (result.SuccessData as System.Collections.IEnumerable)?.Cast<object>().ToList();
        Assert.That(sites, Is.Not.Null.And.Count.EqualTo(1));
    }

    public AntiPatternEngine _antiPatternEngine;
}