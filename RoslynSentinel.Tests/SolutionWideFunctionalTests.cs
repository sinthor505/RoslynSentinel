#pragma warning disable CS8618
using Microsoft.CodeAnalysis;
using Microsoft.Extensions.Logging.Abstractions;
using RoslynSentinel.Server;

namespace RoslynSentinel.Tests;

[TestFixture]
public class SolutionWideFunctionalTests
{
    private PersistentWorkspaceManager _workspaceManager;
    private HealthOrchestrationEngine _healthEngine;
    private ProjectStructureEngine _projectStructureEngine;
    private AnalysisEngine _analysisEngine;
    private AsyncSafetyEngine _asyncSafetyEngine;

    [SetUp]
    public void Setup()
    {
        _workspaceManager = new PersistentWorkspaceManager(new NullLogger<PersistentWorkspaceManager>());
        var config = new SentinelConfiguration();
        _projectStructureEngine = new ProjectStructureEngine(_workspaceManager, config);
        _analysisEngine = new AnalysisEngine(_workspaceManager, config);
        _asyncSafetyEngine = new AsyncSafetyEngine(_workspaceManager);
        
        _healthEngine = new HealthOrchestrationEngine(
            _workspaceManager, 
            _projectStructureEngine, 
            _analysisEngine, 
            _asyncSafetyEngine,
            config);

        // Build a mock solution with 3 projects to test paging
        var solution = TestSolutionBuilder.CreateSolutionWithProject("ProjA", new[] { ("F1.cs", "class A {}") });
        
        var idB = ProjectId.CreateNewId();
        solution = solution.AddProject(idB, "ProjB", "ProjB", LanguageNames.CSharp);
        solution = solution.AddDocument(DocumentId.CreateNewId(idB), "F2.cs", "class B {}");

        var idC = ProjectId.CreateNewId();
        solution = solution.AddProject(idC, "ProjC", "ProjC", LanguageNames.CSharp);
        solution = solution.AddDocument(DocumentId.CreateNewId(idC), "F3.cs", "class C {}");

        _workspaceManager.SetTestSolution(solution);
    }

    [TearDown]
    public void TearDown() => _workspaceManager.Dispose();

    [Test]
    public async Task GetComprehensiveHealthReport_Paging_ShouldReturnChunks()
    {
        // Act - Page 1
        var report1 = await _healthEngine.GenerateComprehensiveHealthReportAsync(limit: 2, offset: 0);

        // Assert
        Assert.That(report1.ProjectSummaries.Count, Is.EqualTo(2));
        Assert.That(report1.HasMore, Is.True);
        Assert.That(report1.NextProjectOffset, Is.EqualTo(2));

        // Act - Page 2
        var report2 = await _healthEngine.GenerateComprehensiveHealthReportAsync(limit: 2, offset: 2);

        // Assert
        Assert.That(report2.ProjectSummaries.Count, Is.EqualTo(1));
        Assert.That(report2.HasMore, Is.False);
        Assert.That(report2.NextProjectOffset, Is.Null);
    }

    [Test]
    public async Task GetComprehensiveHealthReport_Timeout_ShouldReturnPartial()
    {
        // Act - Set a very aggressive timeout (0ms) to force immediate partial return
        var report = await _healthEngine.GenerateComprehensiveHealthReportAsync(timeoutSeconds: 0);

        // Assert
        Assert.That(report.StatusMessage, Does.Contain("timed out"));
    }
}
