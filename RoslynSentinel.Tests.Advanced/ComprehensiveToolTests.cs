using Microsoft.CodeAnalysis;
using Microsoft.Extensions.Logging.Abstractions;

using ModernizationTools = RoslynSentinel.Server.Advanced.ModernizationTools;

#pragma warning disable CS8618
namespace RoslynSentinel.Tests.Advanced;

[TestFixture]
public class ComprehensiveToolTests
{
    private AdvancedLogicEngine _advancedLogicEngine;
    private AdvancedRefactoringEngine _advancedRefactoringEngine;
    private AdvancedStructuralEngine _advancedStructuralEngine;
    private AdvancedTypeEngine _advancedTypeEngine;
    private AnalysisEngine _analysisEngine;
    private ApiAutomationEngine _apiAutomationEngine;
    private ApiIntegrationEngine _apiIntegrationEngine;
    private ArchitecturalEngine _architecturalEngine;
    private AsyncBatchEngine _asyncBatchEngine;
    private AsyncOptimizationEngine _asyncOptimizationEngine;
    private AsyncSafetyEngine _asyncSafetyEngine;
    private CodeFlowEngine _codeFlowEngine;
    private CodeGenerationEngine _codeGenerationEngine;
    private CodeHealingEngine _codeHealingEngine;
    private CodeStyleEngine _codeStyleEngine;
    private ControlFlowEngine _controlFlowEngine;
    private DeadCodeEngine _deadCodeEngine;
    private DependencyEngine _dependencyEngine;
    private DependencyInjectionEngine _dependencyInjectionEngine;
    private DiagnosticEngine _diagnosticEngine;
    private DiffEngine _diffEngine;
    private DiscoveryEngine _discoveryEngine;
    private DocumentationEngine _documentationEngine;
    private GenerationTools _generationTools;
    private GranularRefactoringEngine _granularRefactoringEngine;
    private HealthOrchestrationEngine _healthOrchestrationEngine;
    private IDEStyleEngine _ideStyleEngine;
    private ImmutabilityEngine _immutabilityEngine;
    private ImpactAnalyzer _impactAnalyzer;
    private IntelligenceTools _intelligenceTools;
    private InventoryEngine _inventoryEngine;
    private IWorkspaceManager _workspaceManager;
    private LogicOptimizationEngine _logicOptimizationEngine;
    private MappingEngine _mappingEngine;
    private MetricsEngine _metricsEngine;
    private ModernizationEngine _modernizationEngine;
    private ModernizationTools _modernizationTools;
    private ModernizationUpgradeEngine _modernizationUpgradeEngine;
    private ModernLoggingEngine _modernLoggingEngine;
    private PerformanceEngine _performanceEngine;
    private ProjectStructureEngine _projectStructureEngine;
    private QualityTools _qualityTools;
    private RefactoringEngine _refactoringEngine;
    private AdvancedRefactoringTools _advancedRefactoringTools;
    private RefinementEngine _refinementEngine;
    private SecurityEngine _securityEngine;
    private SemanticRefactoringLibrary _semanticRefactoringLibrary;
    private SemanticSearchEngine _semanticSearchEngine;
    private SentinelConfiguration _config;
    private SolutionManagementEngine _solutionManagementEngine;
    private StandardRefactoringEngine _standardRefactoringEngine;
    private StructuralRefinementEngine _structuralRefinementEngine;
    private SymbolNavigationEngine _symbolNavigationEngine;
    private SyntaxUpgradeEngine _syntaxUpgradeEngine;
    private TestingEngine _testingEngine;
    private ValidationEngine _validationEngine;
    private WorkspaceTools _workspaceTools;


    [SetUp]
    public void Setup()
    {
        _workspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        _advancedLogicEngine = new AdvancedLogicEngine(_workspaceManager);
        _advancedRefactoringEngine = new AdvancedRefactoringEngine(_workspaceManager);
        _advancedStructuralEngine = new AdvancedStructuralEngine(_workspaceManager);
        _advancedTypeEngine = new AdvancedTypeEngine(_workspaceManager);
        _analysisEngine = new AnalysisEngine(_workspaceManager, _config);
        _apiAutomationEngine = new ApiAutomationEngine(_workspaceManager);
        _apiIntegrationEngine = new ApiIntegrationEngine(_workspaceManager);
        _architecturalEngine = new ArchitecturalEngine(_workspaceManager);
        _asyncBatchEngine = new AsyncBatchEngine(_workspaceManager, _asyncOptimizationEngine, new ValidationEngine(_workspaceManager, new DiffEngine(), NullLogger<ValidationEngine>.Instance), new AntiPatternEngine(_workspaceManager), new MigrationLedger(), NullLogger<AsyncBatchEngine>.Instance);
        _asyncOptimizationEngine = new AsyncOptimizationEngine(_workspaceManager);
        _asyncSafetyEngine = new AsyncSafetyEngine(_workspaceManager);
        _codeFlowEngine = new CodeFlowEngine(_workspaceManager);
        _codeGenerationEngine = new CodeGenerationEngine(_workspaceManager);
        _codeHealingEngine = new CodeHealingEngine(_workspaceManager, _config);
        _codeStyleEngine = new CodeStyleEngine(_workspaceManager, _config);
        _config = new SentinelConfiguration();
        _controlFlowEngine = new ControlFlowEngine(_workspaceManager);
        _deadCodeEngine = new DeadCodeEngine(_workspaceManager);
        _dependencyEngine = new DependencyEngine(_workspaceManager);
        _dependencyInjectionEngine = new DependencyInjectionEngine(_workspaceManager);
        _diagnosticEngine = new DiagnosticEngine(_workspaceManager);
        _diffEngine = new DiffEngine();
        _discoveryEngine = new DiscoveryEngine(_workspaceManager, _symbolNavigationEngine);
        _documentationEngine = new DocumentationEngine(_workspaceManager);
        _granularRefactoringEngine = new GranularRefactoringEngine(_workspaceManager);
        _healthOrchestrationEngine = new HealthOrchestrationEngine(_workspaceManager, _projectStructureEngine, _analysisEngine, _config);
        _ideStyleEngine = new IDEStyleEngine(_workspaceManager);
        _immutabilityEngine = new ImmutabilityEngine(_workspaceManager);
        _impactAnalyzer = new ImpactAnalyzer(_workspaceManager, NullLogger<ImpactAnalyzer>.Instance);
        _inventoryEngine = new InventoryEngine(_workspaceManager);
        _logicOptimizationEngine = new LogicOptimizationEngine(_workspaceManager);
        _mappingEngine = new MappingEngine(_workspaceManager);
        _metricsEngine = new MetricsEngine(_workspaceManager);
        _modernizationEngine = new ModernizationEngine(_workspaceManager, _config);
        _modernizationUpgradeEngine = new ModernizationUpgradeEngine(_workspaceManager);
        _modernLoggingEngine = new ModernLoggingEngine(_workspaceManager);
        _performanceEngine = new PerformanceEngine(_workspaceManager);
        _projectStructureEngine = new ProjectStructureEngine(_workspaceManager, _config);
        _refactoringEngine = new RefactoringEngine(_workspaceManager, NullLogger<RefactoringEngine>.Instance, _config);
        _refinementEngine = new RefinementEngine(_workspaceManager);
        _securityEngine = new SecurityEngine(_workspaceManager);
        _semanticRefactoringLibrary = new SemanticRefactoringLibrary(_workspaceManager);
        _semanticSearchEngine = new SemanticSearchEngine(_workspaceManager);
        _solutionManagementEngine = new SolutionManagementEngine(_workspaceManager);
        _standardRefactoringEngine = new StandardRefactoringEngine(_workspaceManager);
        _structuralRefinementEngine = new StructuralRefinementEngine(_workspaceManager, _config);
        _symbolNavigationEngine = new SymbolNavigationEngine(_workspaceManager, NullLogger<SymbolNavigationEngine>.Instance);
        _syntaxUpgradeEngine = new SyntaxUpgradeEngine(_workspaceManager, _config);
        _testingEngine = new TestingEngine(_workspaceManager);
        _validationEngine = new ValidationEngine(_workspaceManager, _diffEngine, NullLogger<ValidationEngine>.Instance);

        _advancedRefactoringTools = new AdvancedRefactoringTools(_workspaceManager);


        _workspaceTools = new WorkspaceTools(_workspaceManager,
            _validationEngine,
            _diffEngine,
            _diagnosticEngine,
            _solutionManagementEngine,
            _structuralRefinementEngine,
            _dependencyEngine,
            new ProjectConsistencyEngine(_workspaceManager),
            _config,
            NullLogger<WorkspaceTools>.Instance,
            new BuildEngine(_workspaceManager, _diagnosticEngine),
            _symbolNavigationEngine,
            new TestRunEngine(_workspaceManager),
            new WorkspaceReadNavigationImpl(_workspaceManager, NullLogger<WorkspaceReadNavigationImpl>.Instance),
            WriteToolAdviceHelper.WithAllToolsExposed());

        _intelligenceTools = new IntelligenceTools(_impactAnalyzer,
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
            NullLogger<IntelligenceTools>.Instance);

        _modernizationTools = new ModernizationTools(_modernizationEngine, _modernizationUpgradeEngine, _modernLoggingEngine, _syntaxUpgradeEngine, _analysisEngine, _logicOptimizationEngine, _codeStyleEngine, _codeHealingEngine, _advancedLogicEngine, _ideStyleEngine, _immutabilityEngine, _asyncOptimizationEngine, _workspaceManager, _config, NullLogger<ModernizationTools>.Instance);

        _qualityTools = new QualityTools(_testingEngine,
            _controlFlowEngine,
            _analysisEngine,
            new AntiPatternEngine(_workspaceManager),
            new ThreadSafetyEngine(_workspaceManager),
            _diagnosticEngine,
            new CodeStyleAnalysisEngine(_workspaceManager),
            new StackOverflowEngine(_workspaceManager),
            new MsToolAugmentEngine(_workspaceManager),
            _workspaceManager,
            NullLogger<QualityTools>.Instance);

        _generationTools = new GenerationTools(_workspaceManager, NullLogger<GenerationTools>.Instance);
    }

    [TearDown]
    public void TearDown() => _workspaceManager?.Dispose();

    private Solution CreateSolution(string source, string fileName = "Test.cs") =>
        TestSolutionBuilder.CreateSolutionWithProject("TestProject", [(fileName, source)]);

    [Test]
    public async Task LoadSolution_NonExistentFile_ReturnsErrorResult()
    {
        var result = await _workspaceTools.LoadSolution(reason: "test message", "fake.sln");
        Assert.That(result.IsSuccess, Is.False, "fake.sln does not exist");
        Assert.That(result.ErrorData?.Message, Is.Not.Null.And.Not.Empty);
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
        var report = await _intelligenceTools.GetComprehensiveHealthReport(reason: "test message");
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
        // "namespace" entry -> enum/struct/record/constructor/field were never covered by the
        // switch, silently implying the file had no commentable/editable members at all. This
        // was the root cause behind a live agent skipping OrderStatus.cs entirely while adding
        // summary comments to every other file in a solution.
        SetSource("namespace N;\npublic enum Status\n{\n    Pending,\n    Shipped\n}", "Status.cs");
        var result = await _workspaceTools.GetFileOutline(reason: "test message", "Status.cs");

        Assert.That(result.IsSuccess, Is.True);
        var items = ((FileOutlineResult)result.SuccessData!).Symbols;
        Assert.That(items.Select(i => (i.Kind, i.Name)), Contains.Item(("enum", "Status")));
        Assert.That(items.Select(i => (i.Kind, i.Name)), Contains.Item(("enum member", "Pending")));
        Assert.That(items.Select(i => (i.Kind, i.Name)), Contains.Item(("enum member", "Shipped")));
    }

    private void SetSource(string source, string fileName)
    {
        _workspaceManager.SetTestSolution(CreateSolution(source, fileName));
    }

    [Test]
    public async Task ListAll_MultiFileSolution_ListsSymbolsFromEveryFile()
    {
        _workspaceManager.SetTestSolution(TestSolutionBuilder.CreateSolutionWithProject("TestProject",
        [
            ("Order.cs", "namespace N;\npublic class Order { public int Id { get; set; } public void Ship() {} }"),
            ("Status.cs", "namespace N;\npublic enum Status { Pending, Shipped }")
        ]));

        var result = await _workspaceTools.ListAll(reason: "test message");

        Assert.That(result.IsSuccess, Is.True);
        var entries = (List<SolutionSymbolEntry>)result.SuccessData!;
        Assert.That(entries.Select(e => (e.Kind, e.Name)), Contains.Item(("class", "Order")));
        Assert.That(entries.Select(e => (e.Kind, e.Name)), Contains.Item(("method", "Ship")));
        Assert.That(entries.Select(e => (e.Kind, e.Name)), Contains.Item(("enum", "Status")));
        Assert.That(entries.Select(e => (e.Kind, e.Name)), Contains.Item(("enum member", "Pending")));
        Assert.That(entries.Select(e => e.FilePath.ToString()), Has.Some.Contains("Order.cs"));
        Assert.That(entries.Select(e => e.FilePath.ToString()), Has.Some.Contains("Status.cs"));
    }

    [Test]
    public async Task ListAll_KindFilter_ReturnsOnlyThatKind()
    {
        _workspaceManager.SetTestSolution(TestSolutionBuilder.CreateSolutionWithProject("TestProject",
        [
            ("Order.cs", "namespace N;\npublic class Order { public int Id { get; set; } public void Ship() {} }"),
            ("Status.cs", "namespace N;\npublic enum Status { Pending, Shipped }")
        ]));

        var result = await _workspaceTools.ListAll(reason: "test message", kind: ListAllKind.method);

        Assert.That(result.IsSuccess, Is.True);
        var entries = (List<SolutionSymbolEntry>)result.SuccessData!;
        Assert.That(entries, Has.All.Matches<SolutionSymbolEntry>(e => e?.Kind == "method"));
        Assert.That(entries.Select(e => e.Name), Contains.Item("Ship"));
    }

    [Test]
    public async Task ListAll_EnumMemberKindFilter_MatchesTwoWordKind()
    {
        SetSource("namespace N;\npublic enum Status { Pending, Shipped }", "Status.cs");

        var result = await _workspaceTools.ListAll(reason: "test message", kind: ListAllKind.enumMember);

        Assert.That(result.IsSuccess, Is.True);
        var entries = (List<SolutionSymbolEntry>)result.SuccessData!;
        Assert.That(entries, Has.Count.EqualTo(2));
        Assert.That(entries.Select(e => e.Name), Is.EquivalentTo(new[] { "Pending", "Shipped" }));
    }
}
