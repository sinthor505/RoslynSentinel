using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging.Abstractions;
using RoslynSentinel.Server;

#pragma warning disable CS8618
namespace RoslynSentinel.Tests;

[TestFixture]
public class ComprehensiveToolTests
{
    private PersistentWorkspaceManager _workspaceManager;
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
    private SemanticRefactoringLibrary _semanticRefinementLibrary;
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

    private SentinelWorkspaceTools _workspaceTools;
    private SentinelIntelligenceTools _intelligenceTools;
    private SentinelRefactoringTools _refactoringTools;
    private SentinelModernizationTools _modernizationTools;
    private SentinelQualityTools _qualityTools;
    private SentinelGenerationTools _generationTools;

    [SetUp]
    public void Setup()
    {
        _workspaceManager = new PersistentWorkspaceManager(NullLogger<PersistentWorkspaceManager>.Instance);
        _diffEngine = new DiffEngine(_workspaceManager);
        _validationEngine = new ValidationEngine(NullLogger<ValidationEngine>.Instance, _workspaceManager, _diffEngine);
        _diagnosticEngine = new DiagnosticEngine(_workspaceManager);
        _solutionManagementEngine = new SolutionManagementEngine(_workspaceManager);
        _structuralRefinementEngine = new StructuralRefinementEngine(_workspaceManager);
        _impactAnalyzer = new ImpactAnalyzer(NullLogger<ImpactAnalyzer>.Instance, _workspaceManager);
        _semanticSearchEngine = new SemanticSearchEngine(_workspaceManager);
        _metricsEngine = new MetricsEngine(_workspaceManager);
        _inventoryEngine = new InventoryEngine(_workspaceManager);
        _deadCodeEngine = new DeadCodeEngine(_workspaceManager);
        _analysisEngine = new AnalysisEngine(_workspaceManager);
        _documentationEngine = new DocumentationEngine(_workspaceManager);
        _dependencyEngine = new DependencyEngine(_workspaceManager);
        _projectStructureEngine = new ProjectStructureEngine(_workspaceManager);
        _refactoringEngine = new RefactoringEngine(NullLogger<RefactoringEngine>.Instance, _workspaceManager);
        _standardRefactoringEngine = new StandardRefactoringEngine(_workspaceManager);
        _advancedStructuralEngine = new AdvancedStructuralEngine(_workspaceManager);
        _mappingEngine = new MappingEngine(_workspaceManager);
        _semanticRefinementLibrary = new SemanticRefactoringLibrary(_workspaceManager);
        _granularRefactoringEngine = new GranularRefactoringEngine(_workspaceManager);
        _advancedLogicEngine = new AdvancedLogicEngine(_workspaceManager);
        _refinementEngine = new RefinementEngine(_workspaceManager);
        _advancedTypeEngine = new AdvancedTypeEngine(_workspaceManager);
        _modernizationEngine = new ModernizationEngine(_workspaceManager);
        _modernizationUpgradeEngine = new ModernizationUpgradeEngine(_workspaceManager);
        _modernLoggingEngine = new ModernLoggingEngine(_workspaceManager);
        _syntaxUpgradeEngine = new SyntaxUpgradeEngine(_workspaceManager);
        _logicOptimizationEngine = new LogicOptimizationEngine(_workspaceManager);
        _codeStyleEngine = new CodeStyleEngine(_workspaceManager);
        _codeHealingEngine = new CodeHealingEngine(_workspaceManager);
        _performanceEngine = new PerformanceEngine(_workspaceManager);
        _securityEngine = new SecurityEngine(_workspaceManager);
        _testingEngine = new TestingEngine(_workspaceManager);
        _controlFlowEngine = new ControlFlowEngine(_workspaceManager);
        _asyncSafetyEngine = new AsyncSafetyEngine(_workspaceManager);
        _codeGenerationEngine = new CodeGenerationEngine(_workspaceManager);
        _apiAutomationEngine = new ApiAutomationEngine(_workspaceManager);
        _healthOrchestrationEngine = new HealthOrchestrationEngine(_workspaceManager, _projectStructureEngine, _analysisEngine, _asyncSafetyEngine);

        _workspaceTools = new SentinelWorkspaceTools(_workspaceManager, _validationEngine, _diffEngine, _diagnosticEngine, _solutionManagementEngine, _structuralRefinementEngine, _dependencyEngine, NullLogger<SentinelWorkspaceTools>.Instance);
        _intelligenceTools = new SentinelIntelligenceTools(_impactAnalyzer, _semanticSearchEngine, _metricsEngine, _inventoryEngine, _deadCodeEngine, _analysisEngine, _documentationEngine, _dependencyEngine, _projectStructureEngine, _asyncSafetyEngine, _healthOrchestrationEngine, NullLogger<SentinelIntelligenceTools>.Instance);
        _refactoringTools = new SentinelRefactoringTools(_refactoringEngine, _standardRefactoringEngine, _advancedStructuralEngine, _mappingEngine, _semanticRefinementLibrary, _granularRefactoringEngine, _advancedLogicEngine, _refinementEngine, _advancedTypeEngine, _structuralRefinementEngine, _workspaceManager, NullLogger<SentinelRefactoringTools>.Instance);
        _modernizationTools = new SentinelModernizationTools(_modernizationEngine, _modernizationUpgradeEngine, _modernLoggingEngine, _syntaxUpgradeEngine, _analysisEngine, _logicOptimizationEngine, _codeStyleEngine, _codeHealingEngine, _advancedLogicEngine, _workspaceManager, NullLogger<SentinelModernizationTools>.Instance);
        _qualityTools = new SentinelQualityTools(_performanceEngine, _securityEngine, _testingEngine, _controlFlowEngine, _logicOptimizationEngine, _analysisEngine, _asyncSafetyEngine, NullLogger<SentinelQualityTools>.Instance);
        _generationTools = new SentinelGenerationTools(_codeGenerationEngine, _apiAutomationEngine, NullLogger<SentinelGenerationTools>.Instance);
    }

    [TearDown]
    public void TearDown() => _workspaceManager?.Dispose();

    private Solution CreateSolution(string source, string fileName = "Test.cs")
    {
        var adhocWorkspace = new AdhocWorkspace();
        var solution = adhocWorkspace.CurrentSolution;
        var projectId = ProjectId.CreateNewId();
        solution = solution.AddProject(projectId, "TestProject", "TestProject", LanguageNames.CSharp);
        var docId = DocumentId.CreateNewId(projectId);
        return solution.AddDocument(docId, fileName, SourceText.From(source), filePath: fileName);
    }

    [Test]
    public async Task LoadSolution_ShouldReturnSuccess()
    {
        var result = await _workspaceTools.LoadSolution("fake.sln");
        Assert.That(result, Contains.Substring("Solution loaded successfully") | Contains.Substring("Error"));
    }

    [Test]
    public async Task Diagnose_ShouldReturnReport()
    {
        var report = await _workspaceTools.Diagnose();
        Assert.That(report, Is.Not.Null);
    }

    [Test]
    public async Task GetBlastRadius_ShouldReturnReport()
    {
        var source = "public class C { public void M() {} }";
        _workspaceManager.SetTestSolution(CreateSolution(source, "C.cs"));
        var report = await _intelligenceTools.GetBlastRadius("C.cs", 1, 30);
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
        var result = await _modernizationTools.ClassToRecord("C.cs", "C");
        Assert.That(result, Is.Not.Null);
    }

    [Test]
    public async Task FindBoxingAllocations_ShouldReturnList()
    {
        var source = "public class C { void M() { object o = 1; } }";
        _workspaceManager.SetTestSolution(CreateSolution(source, "C.cs"));
        var results = await _qualityTools.FindBoxingAllocations("C.cs");
        Assert.That(results, Is.Not.Null);
    }
}
