// Coverage for ListSolutionItemsTool(kind: all) -> see docs/current/plan-orientation-breaker.md
// section 3. This is the aggregation option the orientation breaker's tripped-state message
// points agents at as a "browse everything" alternative to guessing SearchSolutionTextTool
// patterns: it returns projects + solutionItems + every project's files and dependencies in
// one call, without requiring projectName. kind=files/dependencies without projectName return every
// project's files or dependencies, grouped by project (the same ProjectFilesAndDependencies group).

using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Engines.Basic;
using RoslynSentinel.Tests.Fakes;
using RoslynSentinel.Tools.Basic;

namespace RoslynSentinel.Tests.Battery.Basic;

[TestFixture]
[Category("WorkspaceTools")] // sentinel:auto-category
public class ListSolutionItemsAllTests
{
    private static WorkspaceTools BuildTools(IWorkspaceManager workspaceManager) =>
        TestTools.WorkspaceTools(workspaceManager);

    [Test]
    public async Task ListSolutionItems_KindAll_DoesNotRequireProjectNameAsync()
    {
        using var workspace = InMemoryWorkspace.Create(("ContosoOrders.Core/Helper.cs", "namespace TestProj;\n\npublic static class Helper\n{\n}\n"));
        var workspaceTools = BuildTools(workspace.Manager);

        var result = await workspaceTools.ListSolutionItemsTool(reason: "test message", SolutionItemsKind.all);

        Assert.That(!result.IsError, Is.True, result.ErrorData?.Message);
    }

    // Stays on disk: InMemoryWorkspace holds one project, and this test asserts the two-project ContosoOrders layout.
    [Test]
    public async Task ListSolutionItems_KindAll_ReturnsEveryProjectWithFilesAndDependenciesAsync()
    {
        using var fixture = new TestSolutionFixture();
        using var workspaceManager = TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        await workspaceManager.LoadSolutionAsync(fixture.SolutionPath);
        var workspaceTools = BuildTools(workspaceManager);

        var result = await workspaceTools.ListSolutionItemsTool(reason: "test message", SolutionItemsKind.all);
        Assert.That(!result.IsError, Is.True, result.ErrorData?.Message);

        var combined = (SolutionItemsAllResult)result.SuccessData!;

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

    // Eval run 20261109-233953-684 T2: kind=files without projectName was refused with "projectName is required".
    [Test]
    public async Task ListSolutionItems_KindFilesWithoutProjectName_ReturnsEveryProjectsFilesGroupedByProjectAsync()
    {
        using var fixture = new TestSolutionFixture();
        using var workspaceManager = TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        await workspaceManager.LoadSolutionAsync(fixture.SolutionPath);
        var workspaceTools = BuildTools(workspaceManager);

        var result = await workspaceTools.ListSolutionItemsTool(reason: "test message", SolutionItemsKind.files);

        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        var groups = (List<ProjectFilesAndDependencies>)result.SuccessData!;
        Assert.That(groups.Select(g => g.ProjectName), Is.EquivalentTo(new[] { "ContosoOrders.Core", "ContosoOrders.Tests" }));
        foreach (var group in groups)
        {
            Assert.That(group.Files, Is.Not.Null.And.Not.Empty, $"files of '{group.ProjectName}'");
            Assert.That(group.Dependencies, Is.Null, "kind=files does not ask for dependencies");
            var single = await workspaceTools.ListSolutionItemsTool(reason: "test message", SolutionItemsKind.files, projectName: group.ProjectName);
            Assert.That(group.Files, Is.EquivalentTo((List<string>)single.SuccessData!), "the grouped files equal the single-project list");
        }

        Assert.That(result.TotalRecords, Is.EqualTo(groups.Sum(g => g.Files!.Count)));
    }

    [Test]
    public async Task ListSolutionItems_KindDependenciesWithoutProjectName_ReturnsEveryProjectsDependenciesGroupedByProjectAsync()
    {
        using var fixture = new TestSolutionFixture();
        using var workspaceManager = TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        await workspaceManager.LoadSolutionAsync(fixture.SolutionPath);
        var workspaceTools = BuildTools(workspaceManager);

        var result = await workspaceTools.ListSolutionItemsTool(reason: "test message", SolutionItemsKind.dependencies);

        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        var groups = (List<ProjectFilesAndDependencies>)result.SuccessData!;
        Assert.That(groups.Select(g => g.ProjectName), Is.EquivalentTo(new[] { "ContosoOrders.Core", "ContosoOrders.Tests" }));
        Assert.That(groups.All(g => g.Dependencies is not null && g.Files is null), Is.True);
    }

    [Test]
    public async Task ListSolutionItems_KindAll_MatchesUnionOfIndividualKindsAsync()
    {
        using var workspace = InMemoryWorkspace.Create(
            ("ContosoOrders.Core/Helper.cs", "namespace TestProj;\n\npublic static class Helper\n{\n}\n"),
            ("ContosoOrders.Core/Caller.cs", "namespace TestProj;\n\npublic class Caller\n{\n}\n"));
        var workspaceTools = BuildTools(workspace.Manager);

        var allResult = await workspaceTools.ListSolutionItemsTool(reason: "test message", SolutionItemsKind.all);
        var combined = (SolutionItemsAllResult)allResult.SuccessData!;

        var projectsResult = await workspaceTools.ListSolutionItemsTool(reason: "test message", SolutionItemsKind.projects);
        var projectsOnly = (List<ProjectInfoEntry>)projectsResult.SuccessData!;
        Assert.That(combined.Projects.Select(p => p.Name), Is.EquivalentTo(projectsOnly.Select(p => p.Name)));

        foreach (var project in projectsOnly)
        {
            var filesResult = await workspaceTools.ListSolutionItemsTool(reason: "test message", SolutionItemsKind.files, projectName: project.Name);
            var filesOnly = (List<string>)filesResult.SuccessData!;
            var detail = combined.ProjectDetails.Single(d => d.ProjectName == project.Name);
            Assert.That(detail.Files, Is.EquivalentTo(filesOnly));
        }
    }
}
