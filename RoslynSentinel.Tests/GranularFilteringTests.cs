#pragma warning disable CS8618
using Microsoft.CodeAnalysis;
using Microsoft.Extensions.Logging.Abstractions;
using RoslynSentinel.Server;

namespace RoslynSentinel.Tests;

[TestFixture]
public class GranularFilteringTests
{
    private PersistentWorkspaceManager _workspaceManager;
    private ProjectStructureEngine _projectStructureEngine;
    private MetricsEngine _metricsEngine;

    [SetUp]
    public void Setup()
    {
        _workspaceManager = new PersistentWorkspaceManager(NullLogger<PersistentWorkspaceManager>.Instance);
        _projectStructureEngine = new ProjectStructureEngine(_workspaceManager, new SentinelConfiguration());
        _metricsEngine = new MetricsEngine(_workspaceManager);

        var solution = TestSolutionBuilder.CreateSolutionWithProject("ProjectA", new[] {
            ("File1.cs", "namespace App; public class C1 {} public class C2 {}"),
            ("File2.cs", "namespace App; public class Mismatch {}")
        });
        
        var projectIdB = ProjectId.CreateNewId();
        solution = solution.AddProject(projectIdB, "ProjectB", "ProjectB", LanguageNames.CSharp);
        var documentIdB = DocumentId.CreateNewId(projectIdB);
        solution = solution.AddDocument(documentIdB, "File3.cs", "namespace App; public class C3 {}");

        _workspaceManager.SetTestSolution(solution);
    }

    [TearDown]
    public void TearDown()
    {
        _workspaceManager.Dispose();
    }

    [Test]
    public async Task FindStructuralSmells_WithProjectFilter_ShouldOnlyReturnMatches()
    {
        // Act
        var allSmells = await _projectStructureEngine.FindStructuralSmellsAsync();
        var projectASmells = await _projectStructureEngine.FindStructuralSmellsAsync(projectName: "ProjectA");
        var projectBSmells = await _projectStructureEngine.FindStructuralSmellsAsync(projectName: "ProjectB");

        // Assert
        Assert.That(allSmells.Count, Is.EqualTo(4), "Total smells should be 4 (1 multi, 3 mismatch)");
        Assert.That(projectASmells.Count, Is.EqualTo(3), "ProjectA should have 3 smells (1 multi, 2 mismatch)");
        Assert.That(projectBSmells.Count, Is.EqualTo(1), "ProjectB should have 1 smell (1 mismatch)");
    }

    [Test]
    public async Task FindStructuralSmells_WithTypeFilter_ShouldOnlyReturnSpecificType()
    {
        // Act
        var multiTypeOnly = await _projectStructureEngine.FindStructuralSmellsAsync(typeFilter: ProjectStructureEngine.StructuralSmellType.MultiType);
        var mismatchOnly = await _projectStructureEngine.FindStructuralSmellsAsync(typeFilter: ProjectStructureEngine.StructuralSmellType.NameMismatch);

        // Assert
        Assert.That(multiTypeOnly.Count, Is.EqualTo(1));
        Assert.That(multiTypeOnly[0], Does.Contain("[MULTI_TYPE]"));
        
        Assert.That(mismatchOnly.Count, Is.EqualTo(3));
        Assert.That(mismatchOnly[0], Does.Contain("[NAME_MISMATCH]"));
    }

    [Test]
    public async Task GetSolutionMetrics_WithProjectFilter_ShouldOnlyReturnOneProject()
    {
        // Act
        var allMetrics = await _metricsEngine.GetSolutionMetricsAsync();
        var projectAMetrics = await _metricsEngine.GetSolutionMetricsAsync(projectName: "ProjectA");

        // Assert
        Assert.That(allMetrics.Projects.Count, Is.EqualTo(2));
        Assert.That(projectAMetrics.Projects.Count, Is.EqualTo(1));
        Assert.That(projectAMetrics.Projects[0].Name, Is.EqualTo("ProjectA"));
    }
}
