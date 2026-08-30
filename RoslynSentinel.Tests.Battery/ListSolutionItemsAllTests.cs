// Coverage for ListSolutionItems(kind: all) — see docs/current/plan-orientation-breaker.md
// section 3. This is the aggregation option the orientation breaker's tripped-state message
// points agents at as a "browse everything" alternative to guessing SearchSolutionText
// patterns: it returns projects + solutionItems + every project's files and dependencies in
// one call, without requiring projectName (unlike kind=files/dependencies, which need it).

using Microsoft.Extensions.Logging.Abstractions;

namespace RoslynSentinel.Tests.Battery;

[TestFixture]
public class ListSolutionItemsAllTests
{
    private static SentinelWorkspaceTools BuildTools(IWorkspaceManager workspaceManager)
    {
        var config = new SentinelConfiguration();
        var diffEngine = new DiffEngine();
        var validationEngine = new ValidationEngine(NullLogger<ValidationEngine>.Instance, workspaceManager, diffEngine);
        var diagnosticEngine = new DiagnosticEngine(workspaceManager);
        var solutionManagementEngine = new SolutionManagementEngine(workspaceManager);
        var structuralRefinementEngine = new StructuralRefinementEngine(workspaceManager, config);
        var dependencyEngine = new DependencyEngine(workspaceManager);
        var projectConsistencyEngine = new ProjectConsistencyEngine(workspaceManager);
        return new SentinelWorkspaceTools(
            workspaceManager, validationEngine, diffEngine, diagnosticEngine,
            solutionManagementEngine, structuralRefinementEngine, dependencyEngine,
            projectConsistencyEngine, config, NullLogger<SentinelWorkspaceTools>.Instance,
            new BuildEngine(workspaceManager, diagnosticEngine));
    }

    [Test]
    public async Task ListSolutionItems_KindAll_DoesNotRequireProjectNameAsync()
    {
        using var fixture = new TestSolutionFixture();
        using var workspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        await workspaceManager.LoadSolutionAsync(fixture.SolutionPath);
        var tools = BuildTools(workspaceManager);

        var result = await tools.ListSolutionItems(SolutionItemsKind.all);

        Assert.That(result.Success, Is.True, result.Error?.Message);
    }

    [Test]
    public async Task ListSolutionItems_KindAll_ReturnsEveryProjectWithFilesAndDependenciesAsync()
    {
        using var fixture = new TestSolutionFixture();
        using var workspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        await workspaceManager.LoadSolutionAsync(fixture.SolutionPath);
        var tools = BuildTools(workspaceManager);

        var result = await tools.ListSolutionItems(SolutionItemsKind.all);
        Assert.That(result.Success, Is.True, result.Error?.Message);

        var combined = (SolutionItemsAllResult)result.Data!;

        // ContosoOrders sample solution has exactly 2 projects (ContosoOrders.Core, ContosoOrders.Tests).
        Assert.That(combined.Projects, Has.Count.EqualTo(2));
        Assert.That(combined.Projects.Select(p => p.Name), Is.EquivalentTo(new[] { "ContosoOrders.Core", "ContosoOrders.Tests" }));

        Assert.That(combined.ProjectDetails, Has.Count.EqualTo(2));
        foreach (var detail in combined.ProjectDetails)
        {
            Assert.That(detail.Files, Is.Not.Empty, $"expected at least one file for project '{detail.ProjectName}'");
            Assert.That(detail.Files, Is.Unique, $"files for project '{detail.ProjectName}' must be deduped");
            Assert.That(detail.Dependencies, Is.Not.Null);
        }
    }

    [Test]
    public async Task ListSolutionItems_KindAll_MatchesUnionOfIndividualKindsAsync()
    {
        using var fixture = new TestSolutionFixture();
        using var workspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        await workspaceManager.LoadSolutionAsync(fixture.SolutionPath);
        var tools = BuildTools(workspaceManager);

        var allResult = await tools.ListSolutionItems(SolutionItemsKind.all);
        var combined = (SolutionItemsAllResult)allResult.Data!;

        var projectsResult = await tools.ListSolutionItems(SolutionItemsKind.projects);
        var projectsOnly = (List<ProjectInfoEntry>)projectsResult.Data!;
        Assert.That(combined.Projects.Select(p => p.Name), Is.EquivalentTo(projectsOnly.Select(p => p.Name)));

        foreach (var project in projectsOnly)
        {
            var filesResult = await tools.ListSolutionItems(SolutionItemsKind.files, projectName: project.Name);
            var filesOnly = (List<string>)filesResult.Data!;
            var detail = combined.ProjectDetails.Single(d => d.ProjectName == project.Name);
            Assert.That(detail.Files, Is.EquivalentTo(filesOnly));
        }
    }
}
