using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging.Abstractions;

using SentinelModernizationTools = RoslynSentinel.Server.Advanced.SentinelModernizationTools;

#pragma warning disable CS8618
namespace RoslynSentinel.Tests.Advanced;

[TestFixture]
public class ComprehensiveToolTests
{
    private IWorkspaceManager _workspaceManager;
    private SentinelConfiguration _config;
    private ValidationEngine _validationEngine;
    private DiffEngine _diffEngine;
    private DiagnosticEngine _diagnosticEngine;
    private SolutionManagementEngine _solutionManagementEngine;
    private StructuralRefinementEngine _structuralRefinementEngine;
    private ImpactAnalyzer _impactAnalyzer;
    private SemanticSearchEngine _semanticSearchEngine;
    private MetricsEngine _metricsEngine;
    private InventoryEngine _inventoryEngine;
    private DeadCodeEngine _deadCodeEngine;
    private AnalysisEngine _analysisEngine;
    private DocumentationEngine _documentationEngine;
    private DependencyEngine _dependencyEngine;
    private ProjectStructureEngine _projectStructureEngine;
    private RefactoringEngine _refactoringEngine;
    private StandardRefactoringEngine _standardRefactoringEngine;
    private AdvancedStructuralEngine _advancedStructuralEngine;
    private MappingEngine _mappingEngine;
    private SemanticRefactoringLibrary _semanticRefactoringLibrary;
    private GranularRefactoringEngine _granularRefactoringEngine;
    private AdvancedLogicEngine _advancedLogicEngine;
    private RefinementEngine _refinementEngine;
    private AdvancedTypeEngine _advancedTypeEngine;
    private ModernizationEngine _modernizationEngine;
    private ModernizationUpgradeEngine _modernizationUpgradeEngine;
    private ModernLoggingEngine _modernLoggingEngine;
    private SyntaxUpgradeEngine _syntaxUpgradeEngine;
    private LogicOptimizationEngine _logicOptimizationEngine;
    private CodeStyleEngine _codeStyleEngine;
    private CodeHealingEngine _codeHealingEngine;
    private PerformanceEngine _performanceEngine;
    private SecurityEngine _securityEngine;
    private TestingEngine _testingEngine;
    private ControlFlowEngine _controlFlowEngine;
    private AsyncSafetyEngine _asyncSafetyEngine;
    private CodeGenerationEngine _codeGenerationEngine;
    private ApiAutomationEngine _apiAutomationEngine;
    private HealthOrchestrationEngine _healthOrchestrationEngine;
    private ArchitecturalEngine _architecturalEngine;
    private SymbolNavigationEngine _symbolNavigationEngine;
    private DependencyInjectionEngine _dependencyInjectionEngine;
    private DiscoveryEngine _discoveryEngine;
    private IDEStyleEngine _ideStyleEngine;
    private ImmutabilityEngine _immutabilityEngine;
    private AsyncOptimizationEngine _asyncOptimizationEngine;
    private CodeFlowEngine _codeFlowEngine;
    private AdvancedRefactoringEngine _advancedRefactoringEngine;
    private ApiIntegrationEngine _apiIntegrationEngine;
    private AsyncBatchEngine _asyncBatchEngine;

    private SentinelWorkspaceTools _workspaceTools;
    private SentinelIntelligenceTools _intelligenceTools;
    private SentinelRefactoringTools _refactoringTools;
    private SentinelModernizationTools _modernizationTools;
    private SentinelQualityTools _qualityTools;
    private SentinelGenerationTools _generationTools;

    [SetUp]
    public void Setup()
    {
        _workspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        _config = new SentinelConfiguration();
        _diffEngine = new DiffEngine();
        _validationEngine = new ValidationEngine(NullLogger<ValidationEngine>.Instance, _workspaceManager, _diffEngine);
        _diagnosticEngine = new DiagnosticEngine(_workspaceManager);
        _solutionManagementEngine = new SolutionManagementEngine(_workspaceManager);
        _structuralRefinementEngine = new StructuralRefinementEngine(_workspaceManager, _config);
        _impactAnalyzer = new ImpactAnalyzer(NullLogger<ImpactAnalyzer>.Instance, _workspaceManager);
        _semanticSearchEngine = new SemanticSearchEngine(_workspaceManager);
        _metricsEngine = new MetricsEngine(_workspaceManager);
        _inventoryEngine = new InventoryEngine(_workspaceManager);
        _deadCodeEngine = new DeadCodeEngine(_workspaceManager);
        _analysisEngine = new AnalysisEngine(_workspaceManager, _config);
        _documentationEngine = new DocumentationEngine(_workspaceManager);
        _dependencyEngine = new DependencyEngine(_workspaceManager);
        _projectStructureEngine = new ProjectStructureEngine(_workspaceManager, _config);
        _refactoringEngine = new RefactoringEngine(NullLogger<RefactoringEngine>.Instance, _workspaceManager, _config);
        _standardRefactoringEngine = new StandardRefactoringEngine(_workspaceManager);
        _advancedStructuralEngine = new AdvancedStructuralEngine(_workspaceManager);
        _mappingEngine = new MappingEngine(_workspaceManager);
        _semanticRefactoringLibrary = new SemanticRefactoringLibrary(_workspaceManager);
        _granularRefactoringEngine = new GranularRefactoringEngine(_workspaceManager);
        _advancedLogicEngine = new AdvancedLogicEngine(_workspaceManager);
        _refinementEngine = new RefinementEngine(_workspaceManager);
        _advancedTypeEngine = new AdvancedTypeEngine(_workspaceManager);
        _modernizationEngine = new ModernizationEngine(_workspaceManager, _config);
        _modernizationUpgradeEngine = new ModernizationUpgradeEngine(_workspaceManager);
        _modernLoggingEngine = new ModernLoggingEngine(_workspaceManager);
        _syntaxUpgradeEngine = new SyntaxUpgradeEngine(_workspaceManager, _config);
        _logicOptimizationEngine = new LogicOptimizationEngine(_workspaceManager);
        _codeStyleEngine = new CodeStyleEngine(_workspaceManager, _config);
        _codeHealingEngine = new CodeHealingEngine(_workspaceManager, _config);
        _performanceEngine = new PerformanceEngine(_workspaceManager);
        _securityEngine = new SecurityEngine(_workspaceManager);
        _testingEngine = new TestingEngine(_workspaceManager);
        _controlFlowEngine = new ControlFlowEngine(_workspaceManager);
        _asyncSafetyEngine = new AsyncSafetyEngine(_workspaceManager);
        _codeGenerationEngine = new CodeGenerationEngine(_workspaceManager);
        _apiAutomationEngine = new ApiAutomationEngine(_workspaceManager);
        _healthOrchestrationEngine = new HealthOrchestrationEngine(_workspaceManager, _projectStructureEngine, _analysisEngine, _config);
        _architecturalEngine = new ArchitecturalEngine(_workspaceManager);
        _symbolNavigationEngine = new SymbolNavigationEngine(_workspaceManager, NullLogger<SymbolNavigationEngine>.Instance);
        _dependencyInjectionEngine = new DependencyInjectionEngine(_workspaceManager);
        _discoveryEngine = new DiscoveryEngine(_workspaceManager, _symbolNavigationEngine);
        _ideStyleEngine = new IDEStyleEngine(_workspaceManager);
        _immutabilityEngine = new ImmutabilityEngine(_workspaceManager);
        _asyncOptimizationEngine = new AsyncOptimizationEngine(_workspaceManager);
        _codeFlowEngine = new CodeFlowEngine(_workspaceManager);
        _advancedRefactoringEngine = new AdvancedRefactoringEngine(_workspaceManager);
        _apiIntegrationEngine = new ApiIntegrationEngine(_workspaceManager);
        _asyncBatchEngine = new AsyncBatchEngine(_workspaceManager, _asyncOptimizationEngine, new ValidationEngine(NullLogger<ValidationEngine>.Instance, _workspaceManager, new DiffEngine()), new AntiPatternEngine(_workspaceManager), new MigrationLedger(), NullLogger<AsyncBatchEngine>.Instance);

        _workspaceTools = new SentinelWorkspaceTools(_workspaceManager,
            _validationEngine,
            _diffEngine,
            _diagnosticEngine,
            _solutionManagementEngine,
            _structuralRefinementEngine,
            _dependencyEngine,
            new ProjectConsistencyEngine(_workspaceManager),
            _config,
            NullLogger<SentinelWorkspaceTools>.Instance,
            new BuildEngine(_workspaceManager, _diagnosticEngine));
        _intelligenceTools = new SentinelIntelligenceTools(_impactAnalyzer,
            _semanticSearchEngine,
            _metricsEngine,
            _inventoryEngine,
            _deadCodeEngine,
            _analysisEngine,
            _documentationEngine,
            _dependencyEngine,
            _projectStructureEngine,
            _asyncSafetyEngine,
            _healthOrchestrationEngine,
            _architecturalEngine,
            _symbolNavigationEngine,
            _dependencyInjectionEngine,
            _discoveryEngine,
            new ProjectConsistencyEngine(_workspaceManager),
            _workspaceManager,
            _config,
            NullLogger<SentinelIntelligenceTools>.Instance);
        _refactoringTools = new SentinelRefactoringTools(_refactoringEngine,
            _standardRefactoringEngine,
            _mappingEngine,
            _semanticRefactoringLibrary,
            _granularRefactoringEngine,
            _structuralRefinementEngine,
            _codeStyleEngine,
            _codeFlowEngine,
            new MsToolAugmentEngine(_workspaceManager),
            new CodeGenerationEngine(_workspaceManager),
            new SymbolNavigationEngine(_workspaceManager, NullLogger<SymbolNavigationEngine>.Instance),
            _workspaceManager,
            new ValidationEngine(NullLogger<ValidationEngine>.Instance, _workspaceManager, new DiffEngine()),
            _config,
            NullLogger<SentinelRefactoringTools>.Instance);

        _modernizationTools = new SentinelModernizationTools(_modernizationEngine, _modernizationUpgradeEngine, _modernLoggingEngine, _syntaxUpgradeEngine, _analysisEngine, _logicOptimizationEngine, _codeStyleEngine, _codeHealingEngine, _advancedLogicEngine, _ideStyleEngine, _immutabilityEngine, _asyncOptimizationEngine, _workspaceManager, _config, NullLogger<SentinelModernizationTools>.Instance);
        _qualityTools = new SentinelQualityTools(_testingEngine,
            _controlFlowEngine,
            _analysisEngine,
            new AntiPatternEngine(_workspaceManager),
            new ThreadSafetyEngine(_workspaceManager),
            _diagnosticEngine,
            new CodeStyleAnalysisEngine(_workspaceManager),
            new StackOverflowEngine(_workspaceManager),
            new MsToolAugmentEngine(_workspaceManager),
            _workspaceManager,
            NullLogger<SentinelQualityTools>.Instance);
        _generationTools = new SentinelGenerationTools(_codeGenerationEngine,
            _apiAutomationEngine,
            _workspaceManager,
            NullLogger<SentinelGenerationTools>.Instance);
    }

    [TearDown]
    public void TearDown() => _workspaceManager?.Dispose();

    private Solution CreateSolution(string source, string fileName = "Test.cs") =>
        TestSolutionBuilder.CreateSolutionWithProject("TestProject", [(fileName, source)]);

    [Test]
    public async Task LoadSolution_NonExistentFile_ReturnsErrorResult()
    {
        var result = await _workspaceTools.LoadSolution("fake.sln");
        Assert.That(result.Success, Is.False, "fake.sln does not exist");
        Assert.That(result.Error?.Message, Is.Not.Null.And.Not.Empty);
    }

    [Test]
    public async Task GetBlastRadius_ShouldReturnReport()
    {
        var source = "public class C { public void M() {} }";
        _workspaceManager.SetTestSolution(CreateSolution(source, "C.cs"));
        var report = await _impactAnalyzer.AnalyzeImpactAsync("C.cs", "public void M()");
        Assert.That(report, Is.Not.Null);
    }

    [Test]
    public async Task GetComprehensiveHealthReport_ShouldReturnReport()
    {
        _workspaceManager.SetTestSolution(CreateSolution("public class C {}"));
        var report = await _intelligenceTools.GetComprehensiveHealthReport();
        Assert.That(report, Is.Not.Null);
    }

    [Test]
    public async Task ClassToRecord_ShouldReturnString()
    {
        var source = "public class C { public int Id { get; init; } }";
        _workspaceManager.SetTestSolution(CreateSolution(source, "C.cs"));
        var result = await _modernizationEngine.ClassToRecordAsync("C.cs", "C");
        Assert.That(result, Is.Not.Null);
    }

    [Test]
    public async Task FindBoxingAllocations_ShouldReturnList()
    {
        var source = "public class C { void M() { object o = 1; } }";
        _workspaceManager.SetTestSolution(CreateSolution(source, "C.cs"));
        var results = await _performanceEngine.FindBoxingAllocationsAsync("C.cs");
        Assert.That(results, Is.Not.Null);
    }

    [Test]
    public async Task Comprehensive_DeadCode_Analysis()
    {
        SetSource("public class C { private int _unused; }", "C.cs");
        var deadCode = await _deadCodeEngine.DetectUnusedPrivateFieldsAsync("C.cs");
        Assert.That(deadCode, Is.Not.Null);
    }

    [Test]
    public async Task GetFileOutline_EnumOnlyFile_ListsTheEnumAndItsMembers()
    {
        // A file containing only an enum used to produce an outline with nothing but the
        // "namespace" entry — enum/struct/record/constructor/field were never covered by the
        // switch, silently implying the file had no commentable/editable members at all. This
        // was the root cause behind a live agent skipping OrderStatus.cs entirely while adding
        // summary comments to every other file in a solution.
        SetSource("namespace N;\npublic enum Status\n{\n    Pending,\n    Shipped\n}", "Status.cs");
        var result = await _workspaceTools.GetFileOutline("Status.cs");

        Assert.That(result.Success, Is.True);
        var items = ((FileOutlineResult)result.Data!).Symbols;
        Assert.That(items.Select(i => (i.Kind, i.Name)), Contains.Item(("enum", "Status")));
        Assert.That(items.Select(i => (i.Kind, i.Name)), Contains.Item(("enum member", "Pending")));
        Assert.That(items.Select(i => (i.Kind, i.Name)), Contains.Item(("enum member", "Shipped")));
    }

    private void SetSource(string source, string fileName)
    {
        _workspaceManager.SetTestSolution(CreateSolution(source, fileName));
    }
}
