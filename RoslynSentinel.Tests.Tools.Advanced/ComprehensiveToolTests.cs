using Microsoft.CodeAnalysis;
using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Engines.Advanced;
using RoslynSentinel.Engines.Basic;
using RoslynSentinel.Tools.Advanced;
using RoslynSentinel.Tools.Basic;

#pragma warning disable CS8618
namespace RoslynSentinel.Tests.Tools.Advanced;

[TestFixture]
[Category("WorkspaceTools")] // sentinel:auto-category
public class ComprehensiveToolTests
{
    private LogicSimplificationEngine _advancedLogicEngine;
    private AdvancedRefactoringEngine _advancedRefactoringEngine;
    private StructuralRefactoringEngine _advancedStructuralEngine;
    private ApiGenerationEngine _apiGenerationEngine;
    private AsyncBatchEngine _asyncBatchEngine;
    private AsyncOptimizationEngine _asyncOptimizationEngine;
    private AsyncAnalysisEngine _asyncSafetyEngine;
    private LogicSimplificationEngine _codeFlowEngine;
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
    private HealthOrchestrationEngine _healthOrchestrationEngine;
    private IDEStyleEngine _ideStyleEngine;
    private ImpactAnalyzer _impactAnalyzer;
    private IntelligenceTools _intelligenceTools;
    private InventoryEngine _inventoryEngine;
    private IWorkspaceManager _workspaceManager;
    private LogicSimplificationEngine _logicOptimizationEngine;
    private MappingEngine _mappingEngine;
    private MetricsEngine _metricsEngine;
    private SyntaxModernizationEngine _modernizationEngine;
    private ModernizationTools _modernizationTools;
    private PerformanceEngine _performanceEngine;
    private SolutionStructureEngine _solutionStructureEngine;
    private QualityTools _qualityTools;
    private BasicRefactoringEngine _refactoringEngine;
    private AdvancedRefactoringTools _advancedRefactoringTools;
    private SecurityEngine _securityEngine;
    private SemanticRefactoringEngine _semanticRefactoringEngine;
    private DiscoveryEngine _semanticSearchEngine;
    private SentinelConfiguration _config;
    private SolutionManagementEngine _solutionManagementEngine;
    private StructuralRefinementEngine _structuralRefinementEngine;
    private SymbolNavigationEngine _symbolNavigationEngine;
    private SyntaxUpgradeEngine _syntaxUpgradeEngine;
    private TestingEngine _testingEngine;
    private ValidationEngine _validationEngine;
    private WorkspaceTools _workspaceTools;

    [SetUp]
    public void Setup()
    {
        _workspaceManager = Battery.Basic.TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        _advancedLogicEngine = Battery.Advanced.TestEnginesAdvanced.LogicSimplificationEngine(_workspaceManager);
        _advancedRefactoringEngine = Battery.Advanced.TestEnginesAdvanced.AdvancedRefactoringEngine(_workspaceManager);
        _advancedStructuralEngine = Battery.Advanced.TestEnginesAdvanced.StructuralRefactoringEngine(_workspaceManager);
        _apiGenerationEngine = Battery.Advanced.TestEnginesAdvanced.ApiGenerationEngine(_workspaceManager);
        _asyncOptimizationEngine = Battery.Advanced.TestEnginesAdvanced.AsyncOptimizationEngine(_workspaceManager);
        _asyncBatchEngine = Battery.Advanced.TestEnginesAdvanced.AsyncBatchEngine(_workspaceManager, _asyncOptimizationEngine, Battery.Basic.TestEngines.ValidationEngine(_workspaceManager, Battery.Basic.TestEngines.DiffEngine(), NullLogger<ValidationEngine>.Instance), Battery.Advanced.TestEnginesAdvanced.AntiPatternEngine(_workspaceManager), new MigrationLedger(), NullLogger<AsyncBatchEngine>.Instance);
        _asyncSafetyEngine = Battery.Advanced.TestEnginesAdvanced.AsyncAnalysisEngine(_workspaceManager);
        _codeFlowEngine = Battery.Advanced.TestEnginesAdvanced.LogicSimplificationEngine(_workspaceManager);
        _codeGenerationEngine = Battery.Advanced.TestEnginesAdvanced.CodeGenerationEngine(_workspaceManager);
        _config = Battery.Basic.TestEngines.SentinelConfiguration();
        _codeHealingEngine = Battery.Advanced.TestEnginesAdvanced.CodeHealingEngine(_workspaceManager, _config);
        _codeStyleEngine = Battery.Advanced.TestEnginesAdvanced.CodeStyleEngine(_workspaceManager, _config);
        _controlFlowEngine = Battery.Advanced.TestEnginesAdvanced.ControlFlowEngine(_workspaceManager);
        _deadCodeEngine = Battery.Advanced.TestEnginesAdvanced.DeadCodeEngine(_workspaceManager);
        _dependencyEngine = Battery.Basic.TestEngines.DependencyEngine(_workspaceManager);
        _dependencyInjectionEngine = Battery.Advanced.TestEnginesAdvanced.DependencyInjectionEngine(_workspaceManager);
        _diagnosticEngine = Battery.Basic.TestEngines.DiagnosticEngine(_workspaceManager);
        _diffEngine = Battery.Basic.TestEngines.DiffEngine();
        _symbolNavigationEngine = Battery.Basic.TestEngines.SymbolNavigationEngine(_workspaceManager, NullLogger<SymbolNavigationEngine>.Instance);
        _documentationEngine = Battery.Advanced.TestEnginesAdvanced.DocumentationEngine(_workspaceManager);
        _solutionStructureEngine = Battery.Basic.TestEngines.SolutionStructureEngine(_workspaceManager, _config);
        _ideStyleEngine = Battery.Advanced.TestEnginesAdvanced.IDEStyleEngine(_workspaceManager);
        _impactAnalyzer = Battery.Basic.TestEngines.ImpactAnalyzer(_workspaceManager, NullLogger<ImpactAnalyzer>.Instance);
        _inventoryEngine = Battery.Basic.TestEngines.InventoryEngine(_workspaceManager);
        _logicOptimizationEngine = Battery.Advanced.TestEnginesAdvanced.LogicSimplificationEngine(_workspaceManager);
        _mappingEngine = Battery.Advanced.TestEnginesAdvanced.MappingEngine(_workspaceManager);
        _metricsEngine = Battery.Advanced.TestEnginesAdvanced.MetricsEngine(_workspaceManager);
        _modernizationEngine = Battery.Advanced.TestEnginesAdvanced.SyntaxModernizationEngine(_workspaceManager, _config);
        _performanceEngine = Battery.Advanced.TestEnginesAdvanced.PerformanceEngine(_workspaceManager);
        _healthOrchestrationEngine = Battery.Advanced.TestEnginesAdvanced.HealthOrchestrationEngine(_workspaceManager, _solutionStructureEngine, _config, Battery.Advanced.TestEnginesAdvanced.PerformanceEngine(_workspaceManager), Battery.Advanced.TestEnginesAdvanced.AntiPatternEngine(_workspaceManager, _config));
        _refactoringEngine = Battery.Basic.TestEngines.BasicRefactoringEngine(_workspaceManager, NullLogger<BasicRefactoringEngine>.Instance, _config);
        _securityEngine = Battery.Advanced.TestEnginesAdvanced.SecurityEngine(_workspaceManager);
        _semanticRefactoringEngine = Battery.Basic.TestEngines.SemanticRefactoringEngine(_workspaceManager);
        _semanticSearchEngine = Battery.Basic.TestEngines.DiscoveryEngine(_workspaceManager);
        _solutionManagementEngine = Battery.Basic.TestEngines.SolutionManagementEngine(_workspaceManager);
        _structuralRefinementEngine = Battery.Basic.TestEngines.StructuralRefinementEngine(_workspaceManager, _config);
        _discoveryEngine = Battery.Basic.TestEngines.DiscoveryEngine(_workspaceManager, _symbolNavigationEngine);
        _syntaxUpgradeEngine = Battery.Basic.TestEngines.SyntaxUpgradeEngine(_workspaceManager, _config);
        _testingEngine = Battery.Advanced.TestEnginesAdvanced.TestingEngine(_workspaceManager);
        _validationEngine = Battery.Basic.TestEngines.ValidationEngine(_workspaceManager, _diffEngine, NullLogger<ValidationEngine>.Instance);

        _advancedRefactoringTools = Battery.Advanced.TestToolsAdvanced.AdvancedRefactoringTools(_workspaceManager);

        _workspaceTools = Battery.Basic.TestTools.WorkspaceTools(_workspaceManager,
            _validationEngine,
            _diffEngine,
            _diagnosticEngine,
            _solutionManagementEngine,
            _structuralRefinementEngine,
            _dependencyEngine,
            Battery.Basic.TestEngines.ProjectConsistencyEngine(_workspaceManager),
            _config,
            NullLogger<WorkspaceTools>.Instance,
            Battery.Basic.TestEngines.BuildEngine(_workspaceManager, _diagnosticEngine),
            _symbolNavigationEngine,
            Battery.Basic.TestEngines.TestRunEngine(_workspaceManager),
            Battery.Basic.TestTools.WorkspaceReadNavigationImpl(_workspaceManager, NullLogger<WorkspaceReadNavigationImpl>.Instance),
            WriteToolAdviceHelper.WithAllToolsExposed());

        // The tool classes resolve their own engines from the factory container (same wiring, fresh instances, shared manager).
        _intelligenceTools = Battery.Advanced.TestToolsAdvanced.IntelligenceTools(_workspaceManager);

        _modernizationTools = Battery.Advanced.TestToolsAdvanced.ModernizationTools(_workspaceManager);

        _qualityTools = Battery.Advanced.TestToolsAdvanced.QualityTools(_workspaceManager);

        _generationTools = Battery.Advanced.TestToolsAdvanced.GenerationTools(_workspaceManager, NullLogger<GenerationTools>.Instance);
    }

    [TearDown]
    public void TearDown() => _workspaceManager?.Dispose();

    private Solution CreateSolution(string source, string fileName = "Test.cs") =>
        TestSolutionBuilder.CreateSolutionWithProject("TestProject", [(fileName, source)]);

    [Test]
    public async Task LoadSolution_NonExistentFile_ReturnsErrorResult()
    {
        var result = await _workspaceTools.LoadSolutionTool(reason: "test message", "fake.sln");
        Assert.That(result.IsError, Is.True, "fake.sln does not exist");
        Assert.That(result.ErrorData?.Message, Is.Not.Null.And.Not.Empty);
    }

    [Test]
    [Category("ImpactAnalyzer")] // sentinel:auto-category
    public async Task GetBlastRadius_ShouldReturnReport()
    {
        var source = "public class C { public void M() {} }";
        _workspaceManager.SetTestSolution(CreateSolution(source, "C.cs"));
        var report = await _impactAnalyzer.AnalyzeImpactAsync("C.cs", "public void M()");
        Assert.That(report.SymbolName, Is.EqualTo("C.M()")); Assert.That(report.TotalCallSites, Is.EqualTo(0));
    }

    [Test]
    [Category("IntelligenceTools")] // sentinel:auto-category
    public async Task GetComprehensiveHealthReport_ShouldReturnReport()
    {
        _workspaceManager.SetTestSolution(CreateSolution("public class C {}"));
        var report = await _intelligenceTools.GetComprehensiveHealthReportTool(reason: "test message");
        Assert.That(report.IsError, Is.False, report.ErrorData?.Message); Assert.That(report.SuccessData, Is.Not.Null);
    }

    [Test]
    [Category("SyntaxModernizationEngine")] // sentinel:auto-category
    public async Task ClassToRecord_ShouldReturnString()
    {
        var source = "public class C { public int Id { get; init; } }";
        _workspaceManager.SetTestSolution(CreateSolution(source, "C.cs"));
        var result = await _modernizationEngine.ClassToRecordAsync("C.cs", "C");
        Assert.That(result.Outcome, Is.EqualTo(EditOutcome.Modified)); Assert.That(result.UpdatedText, Does.Contain("record C"));
    }

    [Test]
    [Category("PerformanceEngine")] // sentinel:auto-category
    public async Task FindBoxingAllocations_ShouldReturnList()
    {
        var source = "public class C { void M() { object o = 1; } }";
        _workspaceManager.SetTestSolution(CreateSolution(source, "C.cs"));
        var results = await _performanceEngine.FindBoxingAllocationsAsync("C.cs");
        Assert.That(results, Has.Count.EqualTo(1)); Assert.That(results[0].Line, Is.EqualTo(1));
    }

    [Test]
    [Category("DeadCodeEngine")] // sentinel:auto-category
    public async Task Comprehensive_DeadCode_Analysis()
    {
        SetSource("public class C { private int _unused; }", "C.cs");
        var deadCode = await _deadCodeEngine.DetectUnusedPrivateFieldsAsync("C.cs");
        Assert.That(deadCode.Any(r => r.SymbolName == "_unused"), Is.True);
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
        var result = await _workspaceTools.GetFileOutlineTool(reason: "test message", "Status.cs");

        Assert.That(!result.IsError, Is.True);
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

        var result = await _workspaceTools.ListAllTool(reason: "test message");

        Assert.That(!result.IsError, Is.True);
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

        var result = await _workspaceTools.ListAllTool(reason: "test message", kind: ListAllKind.method);

        Assert.That(!result.IsError, Is.True);
        var entries = (List<SolutionSymbolEntry>)result.SuccessData!;
        Assert.That(entries, Has.All.Matches<SolutionSymbolEntry>(e => e?.Kind == "method"));
        Assert.That(entries.Select(e => e.Name), Contains.Item("Ship"));
    }

    [Test]
    public async Task ListAll_EnumMemberKindFilter_MatchesTwoWordKind()
    {
        SetSource("namespace N;\npublic enum Status { Pending, Shipped }", "Status.cs");

        var result = await _workspaceTools.ListAllTool(reason: "test message", kind: ListAllKind.enumMember);

        Assert.That(!result.IsError, Is.True);
        var entries = (List<SolutionSymbolEntry>)result.SuccessData!;
        Assert.That(entries, Has.Count.EqualTo(2));
        Assert.That(entries.Select(e => e.Name), Is.EquivalentTo(new[] { "Pending", "Shipped" }));
    }
}
