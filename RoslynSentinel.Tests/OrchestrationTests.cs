using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

using RoslynSentinel.Tests.Fakes;

#pragma warning disable CS8618
namespace RoslynSentinel.Tests;

[TestFixture]
public class OrchestrationTests
{
    private IWorkspaceManager _workspaceManager;
    private HealthOrchestrationEngine _healthEngine;
    private ProjectStructureEngine _structureEngine;
    private AnalysisEngine _analysisEngine;

    [SetUp]
    public void Setup()
    {
        _workspaceManager = new FakeWorkspaceManager();
        var config = new SentinelConfiguration();
        _structureEngine = new ProjectStructureEngine(_workspaceManager, config);
        _analysisEngine = new AnalysisEngine(_workspaceManager, config);
        _healthEngine = new HealthOrchestrationEngine(_workspaceManager, _structureEngine, _analysisEngine, config);
    }

    [TearDown]
    public void TearDown() => _workspaceManager?.Dispose();

    private Solution CreateLargeSolution(int projectCount)
    {
        var workspace = new AdhocWorkspace();
        var solution = workspace.CurrentSolution;
        for (int i = 0; i < projectCount; i++)
        {
            var pid = ProjectId.CreateNewId();
            solution = solution.AddProject(pid, $"Proj{i:D3}", $"Proj{i:D3}", LanguageNames.CSharp);
            var did = DocumentId.CreateNewId(pid);
            solution = solution.AddDocument(did, "File.cs", SourceText.From("public class C {}"));
        }
        return solution;
    }

    [Test]
    public async Task HealthReport_Paging_ShouldWork()
    {
        _workspaceManager.SetTestSolution(CreateLargeSolution(20));
        var report = await _healthEngine.GenerateComprehensiveHealthReportAsync(offset: 0, limit: 5);
        Assert.That(report.ProjectSummaries.Count, Is.LessThanOrEqualTo(5));
        Assert.That(report.HasMorePages, Is.True);
        Assert.That(report.NextProjectOffset, Is.EqualTo(5));
    }

    [Test]
    public async Task HealthReport_Offset_ShouldSkipProjects()
    {
        _workspaceManager.SetTestSolution(CreateLargeSolution(10));
        var report = await _healthEngine.GenerateComprehensiveHealthReportAsync(offset: 5, limit: 5);
        Assert.That(report.HasMorePages, Is.False);
    }

    [Test]
    public async Task HealthReport_Filtering_ShouldLimitResults()
    {
        _workspaceManager.SetTestSolution(CreateLargeSolution(10));
        var report = await _healthEngine.GenerateComprehensiveHealthReportAsync(projectName: "Proj001");
        Assert.That(report.ProjectSummaries.All(p => p.ProjectName.Contains("Proj001")), Is.True);
    }

    [Test]
    public async Task HealthReport_Timeout_ShouldReturnPartialResults()
    {
        _workspaceManager.SetTestSolution(CreateLargeSolution(50));
        // Use a 0-second timeout to force immediate partial return
        var report = await _healthEngine.GenerateComprehensiveHealthReportAsync(timeoutSeconds: 0);
        Assert.That(report.StatusMessage, Contains.Substring("timed out"));
    }
}
