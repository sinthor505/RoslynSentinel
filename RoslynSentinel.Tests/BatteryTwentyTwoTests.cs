// Battery #22 — SentinelIntelligenceTools
// Tests all 45 public methods of SentinelIntelligenceTools in-memory via TestSolutionBuilder.

using System.Collections.Generic;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using RoslynSentinel.Server;

#pragma warning disable CS8618
namespace RoslynSentinel.Tests;

[TestFixture]
public class BatteryTwentyTwoTests
{
    private PersistentWorkspaceManager _workspaceManager;
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
        _workspaceManager = new PersistentWorkspaceManager(NullLogger<PersistentWorkspaceManager>.Instance);
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
            _workspaceManager, _projectStructureEngine, _analysisEngine, _asyncSafetyEngine, _config);
        _architecturalEngine = new ArchitecturalEngine(_workspaceManager);
        _symbolNavigationEngine = new SymbolNavigationEngine(_workspaceManager, NullLogger<SymbolNavigationEngine>.Instance);
        _dependencyInjectionEngine = new DependencyInjectionEngine(_workspaceManager);
        _discoveryEngine = new DiscoveryEngine(_workspaceManager);
        _tools = new SentinelIntelligenceTools(
            _impactAnalyzer, _semanticSearchEngine, _metricsEngine, _inventoryEngine,
            _deadCodeEngine, _analysisEngine, _documentationEngine, _dependencyEngine,
            _projectStructureEngine, _asyncSafetyEngine, _healthOrchestrationEngine,
            _architecturalEngine, _symbolNavigationEngine, _dependencyInjectionEngine,
            _discoveryEngine, new ProjectConsistencyEngine(_workspaceManager), new BreakingChangeEngine(_workspaceManager),
            new CloneDetectionEngine(_workspaceManager),
            _config, NullLogger<SentinelIntelligenceTools>.Instance);
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
        var result = await _tools.GetComprehensiveHealthReport(timeoutSeconds: 5);
        Assert.That(result, Is.Not.Null);
    }

    // --- GetBlastRadius ---

    [Test]
    public async Task GetBlastRadius_ValidMethod_ReturnsReport()
    {
        SetSource(RichSource, "Test.cs");
        var result = await _tools.GetBlastRadius("Test.cs", "ProcessAsync");
        Assert.That(result, Is.Not.Null);
    }

    // --- FindMethodsByReturnType ---

    [Test]
    public async Task FindMethodsByReturnType_ValidType_ReturnsList()
    {
        SetSource(RichSource, "Test.cs");
        var result = await _tools.FindMethodsByReturnType("string");
        Assert.That(result, Is.Not.Null);
    }

    // --- GetSolutionMetrics ---

    [Test]
    public async Task GetSolutionMetrics_LoadedSolution_ReturnsMetrics()
    {
        SetSource(RichSource, "Test.cs");
        var result = await _tools.GetSolutionMetrics();
        Assert.That(result, Is.Not.Null);
    }

    [Test]
    public async Task GetSolutionMetrics_WithProjectName_ReturnsMetrics()
    {
        SetSource(RichSource, "Test.cs");
        var result = await _tools.GetSolutionMetrics("TestProj");
        Assert.That(result, Is.Not.Null);
    }

    // --- GetCodeInventory ---

    [Test]
    public async Task GetCodeInventory_ValidFile_ReturnsInventory()
    {
        SetSource(RichSource, "Test.cs");
        var result = await _tools.GetCodeInventory("Test.cs");
        Assert.That(result, Is.Not.Null);
    }

    // --- FindUnusedPrivateMembers ---

    [Test]
    public async Task FindUnusedPrivateMembers_ValidClass_ReturnsList()
    {
        SetSource(RichSource, "Test.cs");
        var result = await _tools.FindUnusedPrivateMembers("Test.cs", "Order");
        Assert.That(result, Is.Not.Null);
    }

    // --- DetectUnusedPrivateFields ---

    [Test]
    public async Task DetectUnusedPrivateFields_ValidFile_ReturnsList()
    {
        SetSource(RichSource, "Test.cs");
        var result = await _tools.DetectUnusedPrivateFields("Test.cs");
        Assert.That(result, Is.Not.Null);
    }

    // --- DetectUnusedLocalVariables ---

    [Test]
    public async Task DetectUnusedLocalVariables_ValidFile_ReturnsList()
    {
        SetSource(RichSource, "Test.cs");
        var result = await _tools.DetectUnusedLocalVariables("Test.cs");
        Assert.That(result, Is.Not.Null);
    }

    // --- DetectLongParameterLists ---

    [Test]
    public async Task DetectLongParameterLists_ValidSolution_ReturnsList()
    {
        SetSource(RichSource, "Test.cs");
        var result = await _tools.DetectLongParameterLists();
        Assert.That(result, Is.Not.Null);
    }

    // --- FindUninstantiatedTypes ---

    [Test]
    public async Task FindUninstantiatedTypes_ValidSolution_ReturnsList()
    {
        SetSource(RichSource, "Test.cs");
        var result = await _tools.FindUninstantiatedTypes();
        Assert.That(result, Is.Not.Null);
    }

    // --- FindCircularDependencies (no params, on _analysisEngine) ---

    [Test]
    public async Task FindCircularDependencies_NoParams_ReturnsList()
    {
        SetSource(RichSource, "Test.cs");
        var result = await _tools.FindCircularDependencies();
        Assert.That(result, Is.Not.Null);
    }

    // --- GenerateCallTree ---

    [Test]
    public async Task GenerateCallTree_ValidMethod_ReturnsString()
    {
        SetSource(RichSource, "Test.cs");
        var result = await _tools.GenerateCallTree("Test.cs", "ProcessAsync");
        Assert.That(result, Is.Not.Null.And.Not.Empty);
    }

    // --- DocumentPocoFields ---

    [Test]
    public async Task DocumentPocoFields_ValidClass_ReturnsString()
    {
        SetSource(RichSource, "Test.cs");
        var result = await _tools.DocumentPocoFields("Test.cs", "Order");
        Assert.That(result, Is.Not.Null.And.Not.Empty);
    }

    // --- GenerateEqualityOverrides ---

    [Test]
    public async Task GenerateEqualityOverrides_ValidClass_ReturnsString()
    {
        SetSource(RichSource, "Test.cs");
        var result = await _tools.GenerateEqualityOverrides("Test.cs", "Order");
        Assert.That(result, Is.Not.Null.And.Not.Empty);
    }

    // --- FindUnusedReferences ---

    [Test]
    public async Task FindUnusedReferences_ValidProject_ReturnsList()
    {
        SetSource(RichSource, "Test.cs");
        var result = await _tools.FindUnusedReferences("TestProj");
        Assert.That(result, Is.Not.Null);
    }

    // --- CheckPackageInconsistency ---

    [Test]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Style", "IDE0058:Expression value is never used", Justification = "Test is only verifying exception throwing")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("AsyncUsage", "AsyncFixer06:Task<T> to Task conversion silently discards result", Justification = "Test is only verifying exception throwing")]
    public void CheckPackageInconsistency_ValidSolution_Throws()
    {
        SetSource(RichSource, "Test.cs");
        Assert.CatchAsync<Exception>(() => _tools.CheckPackageInconsistency());
    }

    // --- FindUnusedInterfaces ---

    [Test]
    public async Task FindUnusedInterfaces_ValidSolution_ReturnsList()
    {
        SetSource(RichSource, "Test.cs");
        var result = await _tools.FindUnusedInterfaces();
        Assert.That(result, Is.Not.Null);
    }

    // --- FindInternalClassesThatCouldBePrivate ---

    [Test]
    public async Task FindInternalClassesThatCouldBePrivate_ValidSolution_ReturnsList()
    {
        SetSource(RichSource, "Test.cs");
        var result = await _tools.FindInternalClassesThatCouldBePrivate();
        Assert.That(result, Is.Not.Null);
    }

    // --- FindLargeSwitchStatements ---

    [Test]
    public async Task FindLargeSwitchStatements_ValidSolution_ReturnsList()
    {
        SetSource(RichSource, "Test.cs");
        var result = await _tools.FindLargeSwitchStatements();
        Assert.That(result, Is.Not.Null);
    }

    // --- FindStructuralSmells ---

    [Test]
    public async Task FindStructuralSmells_ValidSolution_ReturnsList()
    {
        SetSource(RichSource, "Test.cs");
        var result = await _tools.FindStructuralSmells();
        Assert.That(result, Is.Not.Null);
    }

    // --- FindUnusedConstructors ---

    [Test]
    public async Task FindUnusedConstructors_ValidFile_ReturnsList()
    {
        SetSource(RichSource, "Test.cs");
        var result = await _tools.FindUnusedConstructors("Test.cs");
        Assert.That(result, Is.Not.Null);
    }

    // --- CheckForUnusedEventSubscriptions ---

    [Test]
    public async Task CheckForUnusedEventSubscriptions_ValidFile_ReturnsList()
    {
        SetSource(RichSource, "Test.cs");
        var result = await _tools.CheckForUnusedEventSubscriptions("Test.cs");
        Assert.That(result, Is.Not.Null);
    }

    // --- GetSymbolInfo ---

    [Test]
    public async Task GetSymbolInfo_ValidSymbolSnippet_ReturnsInfo()
    {
        SetSource(RichSource, "Test.cs");
        var result = await _tools.GetSymbolInfo("Test.cs", "ProcessAsync");
        Assert.That(result, Is.Not.Null);
    }

    // --- FindAllImplementations ---

    [Test]
    public async Task FindAllImplementations_ValidInterface_ReturnsList()
    {
        SetSource(RichSource, "Test.cs");
        var result = await _tools.FindAllImplementations("IOrderService");
        Assert.That(result, Is.Not.Null);
    }

    // --- FindReadonlyFieldCandidates ---

    [Test]
    public async Task FindReadonlyFieldCandidates_ValidFile_ReturnsList()
    {
        SetSource(RichSource, "Test.cs");
        var result = await _tools.FindReadonlyFieldCandidates("Test.cs");
        Assert.That(result, Is.Not.Null);
    }

    // --- FindDiRegistrations ---

    [Test]
    public async Task FindDiRegistrations_ValidSolution_ReturnsList()
    {
        SetSource(RichSource, "Test.cs");
        var result = await _tools.FindDiRegistrations();
        Assert.That(result, Is.Not.Null);
    }

    // --- GetTypeMembersDetail ---

    [Test]
    public async Task GetTypeMembersDetail_ValidType_ReturnsList()
    {
        SetSource(RichSource, "Test.cs");
        var result = await _tools.GetTypeMembersDetail("Order");
        Assert.That(result, Is.Not.Null);
    }

    // --- VerifyInterfaceCompleteness ---

    [Test]
    public async Task VerifyInterfaceCompleteness_ValidInterface_ReturnsList()
    {
        SetSource(RichSource, "Test.cs");
        var result = await _tools.VerifyInterfaceCompleteness("IOrderService");
        Assert.That(result, Is.Not.Null);
    }

    // --- FindExtensionMethods ---

    [Test]
    public async Task FindExtensionMethods_ValidType_ReturnsList()
    {
        SetSource(RichSource, "Test.cs");
        var result = await _tools.FindExtensionMethods("string");
        Assert.That(result, Is.Not.Null);
    }

    // --- AnalyzeTypeCohesion ---

    [Test]
    public async Task AnalyzeTypeCohesion_ValidFile_ReturnsList()
    {
        SetSource(RichSource, "Test.cs");
        var result = await _tools.AnalyzeTypeCohesion("Test.cs");
        Assert.That(result, Is.Not.Null);
    }

    // --- FindCircularDependencies (with projectName, on _architecturalEngine) ---

    [Test]
    public async Task FindCircularDependencies_WithProjectName_ReturnsList()
    {
        SetSource(RichSource, "Test.cs");
        var result = await _tools.FindCircularDependencies("TestProj");
        Assert.That(result, Is.Not.Null);
    }

    // --- GetCallGraph ---

    [Test]
    public async Task GetCallGraph_ValidMethod_ReturnsCallGraph()
    {
        SetSource(RichSource, "Test.cs");
        var result = await _tools.GetCallGraph("Test.cs", "ProcessAsync");
        Assert.That(result, Is.Not.Null);
    }

    [Test]
    public void GetCallGraph_NonExistentMethod_Throws()
    {
        SetSource(RichSource, "Test.cs");
        Assert.ThrowsAsync<InvalidOperationException>(
            () => _tools.GetCallGraph("Test.cs", "NoSuchMethod99"));
    }

    // --- GetReverseCallGraph ---

    [Test]
    public async Task GetReverseCallGraph_ValidMethod_ReturnsCallGraph()
    {
        SetSource(RichSource, "Test.cs");
        var result = await _tools.GetReverseCallGraph("Test.cs", "GetStatus");
        Assert.That(result, Is.Not.Null);
    }

    [Test]
    public void GetReverseCallGraph_NonExistentMethod_Throws()
    {
        SetSource(RichSource, "Test.cs");
        Assert.ThrowsAsync<InvalidOperationException>(
            () => _tools.GetReverseCallGraph("Test.cs", "NoSuchMethod99"));
    }

    // --- ConvertToBackgroundService ---

    [Test]
    public async Task ConvertToBackgroundService_ValidClass_ReturnsString()
    {
        SetSource(RichSource, "Test.cs");
        var result = await _tools.ConvertToBackgroundService("Test.cs", "OrderService");
        Assert.That(result, Is.Not.Null);
    }

    // --- FixMismatchedNamespaces ---

    [Test]
    public async Task FixMismatchedNamespaces_ValidFile_ReturnsString()
    {
        SetSource(RichSource, "Test.cs");
        var result = await _tools.FixMismatchedNamespaces("Test.cs");
        Assert.That(result, Is.Not.Null);
    }

    // --- MoveFileToNamespaceFolder ---

    [Test]
    public async Task MoveFileToNamespaceFolder_ValidFile_ReturnsString()
    {
        SetSource(RichSource, "Test.cs");
        var result = await _tools.MoveFileToNamespaceFolder("Test.cs");
        Assert.That(result, Is.Not.Null);
    }

    // --- FindAllThrowSites ---

    [Test]
    public async Task FindAllThrowSites_ValidSolution_ReturnsList()
    {
        SetSource(RichSource, "Test.cs");
        var result = await _tools.FindAllThrowSites();
        Assert.That(result, Is.Not.Null);
    }

    // --- FindObjectCreationSites ---

    [Test]
    public async Task FindObjectCreationSites_ValidType_ReturnsList()
    {
        SetSource(RichSource, "Test.cs");
        var result = await _tools.FindObjectCreationSites("Order");
        Assert.That(result, Is.Not.Null);
    }

    // --- GetPublicApiSurface ---

    [Test]
    public async Task GetPublicApiSurface_ValidProject_ReturnsList()
    {
        SetSource(RichSource, "Test.cs");
        var result = await _tools.GetPublicApiSurface("TestProj");
        Assert.That(result, Is.Not.Null);
    }

    // --- FindServicesNotRegistered ---

    [Test]
    public async Task FindServicesNotRegistered_ValidSolution_ReturnsList()
    {
        SetSource(RichSource, "Test.cs");
        var result = await _tools.FindServicesNotRegistered();
        Assert.That(result, Is.Not.Null);
    }

    // --- FindBestInsertionPoint ---

    [Test]
    public async Task FindBestInsertionPoint_ValidClass_ReturnsResult()
    {
        SetSource(RichSource, "Test.cs");
        var result = await _tools.FindBestInsertionPoint("Test.cs", "Order", "method");
        Assert.That(result, Is.Not.Null);
    }

    // --- FindTodoFixmeComments ---

    [Test]
    public async Task FindTodoFixmeComments_ValidSolution_ReturnsList()
    {
        SetSource(RichSource, "Test.cs");
        var result = await _tools.FindTodoFixmeComments();
        Assert.That(result, Is.Not.Null);
    }

    // --- PreviewRenameImpact ---

    [Test]
    public async Task PreviewRenameImpact_ValidSymbol_ReturnsPreview()
    {
        SetSource(RichSource, "Test.cs");
        var result = await _tools.PreviewRenameImpact("Test.cs", "ProcessAsync");
        Assert.That(result, Is.Not.Null);
    }

    // --- FindCallersSafe ---

    [Test]
    public async Task FindCallersSafe_ValidSymbol_ReturnsList()
    {
        SetSource(RichSource, "Test.cs");
        var result = await _tools.FindCallersSafe("Test.cs", "ProcessAsync");
        Assert.That(result, Is.Not.Null);
    }

    // --- FindImplementationsSafe ---

    [Test]
    public async Task FindImplementationsSafe_ValidInterface_ReturnsList()
    {
        SetSource(RichSource, "Test.cs");
        var result = await _tools.FindImplementationsSafe("Test.cs", "IOrderService");
        Assert.That(result, Is.Not.Null);
    }
}
