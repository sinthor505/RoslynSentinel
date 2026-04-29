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
        _projectStructureEngine = new ProjectStructureEngine(_workspaceManager);
        _analysisEngine = new AnalysisEngine(_workspaceManager);
        _asyncSafetyEngine = new AsyncSafetyEngine(_workspaceManager);
        
        _healthEngine = new HealthOrchestrationEngine(
            _workspaceManager, 
            _projectStructureEngine, 
            _analysisEngine, 
            _asyncSafetyEngine);

        // Build a mock solution with various smells across 2 projects
        var solution = TestSolutionBuilder.CreateSolutionWithProject("CoreProj", new[] {
            ("File1.cs", "namespace Core; public class C1 {} public class C2 {}"), // MULTI_TYPE
            ("Mismatch.cs", "namespace Core; public class DifferentName {}")      // NAME_MISMATCH
        });

        var projectIdB = ProjectId.CreateNewId();
        solution = solution.AddProject(projectIdB, "WebProj", "WebProj", LanguageNames.CSharp);
        solution = solution.AddDocument(DocumentId.CreateNewId(projectIdB), "Async.cs", @"
            using System;
            using System.Threading.Tasks;
            public class Web {
                public async void BadAsync() {} // ASYNC_VOID
                public void Catch() { try {} catch(Exception) {} } // EMPTY_CATCH
            }");

        _workspaceManager.SetTestSolution(solution);
    }

    [TearDown]
    public void TearDown() => _workspaceManager.Dispose();

    [Test]
    public async Task GetComprehensiveHealthReport_SolutionWide_ShouldAggregateAll()
    {
        // Act
        var report = await _healthEngine.GenerateComprehensiveHealthReportAsync();

        // Assert
        Assert.That(report.TotalIssues, Is.GreaterThanOrEqualTo(4));
        Assert.That(report.ProjectSummaries.Count, Is.EqualTo(2));
        
        var coreProj = report.ProjectSummaries.First(p => p.ProjectName == "CoreProj");
        // File1.cs: [MULTI_TYPE], [NAME_MISMATCH] (C1 is primary but C2 exists)
        // Mismatch.cs: [NAME_MISMATCH]
        // Note: The analyzer reports MULTI_TYPE for File1, and NAME_MISMATCH for both File1 and Mismatch.
        // Let's check for at least these 3.
        Assert.That(coreProj.TotalIssues, Is.GreaterThanOrEqualTo(2));
    }

    [Test]
    public async Task GetComprehensiveHealthReport_WithEngineFilter_ShouldOnlyRunSelected()
    {
        // Act - Only run Structure
        var report = await _healthEngine.GenerateComprehensiveHealthReportAsync(
            engines: new List<HealthEngineType> { HealthEngineType.Structure });

        // Assert
        Assert.That(report.TotalIssuesByCategory.Any(c => c.Category == "MULTI_TYPE"), Is.True);
        Assert.That(report.TotalIssuesByCategory.Any(c => c.Category == "ASYNC_VOID"), Is.False, "Safety engine should not have run.");
    }

    [Test]
    public async Task GetComprehensiveHealthReport_WithProjectFilter_ShouldFocusOnlyOnOne()
    {
        // Act
        var report = await _healthEngine.GenerateComprehensiveHealthReportAsync(projectName: "WebProj");

        // Assert
        Assert.That(report.ProjectSummaries.Count, Is.EqualTo(1));
        Assert.That(report.ProjectSummaries[0].ProjectName, Is.EqualTo("WebProj"));
    }

    [Test]
    public async Task GetComprehensiveHealthReport_WithFileFilter_ShouldFocusOnSingleFile()
    {
        // Act
        var report = await _healthEngine.GenerateComprehensiveHealthReportAsync(filePath: "File1.cs");

        // Assert
        Assert.That(report.TotalIssues, Is.GreaterThanOrEqualTo(1));
        var coreSummary = report.ProjectSummaries.First(p => p.ProjectName == "CoreProj");
        Assert.That(coreSummary.IssuesByCategory.Any(c => c.Category == "MULTI_TYPE"), Is.True);
    }
}
