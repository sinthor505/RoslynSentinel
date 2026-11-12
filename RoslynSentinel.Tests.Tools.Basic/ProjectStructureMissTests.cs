using RoslynSentinel.Tests.Fakes;

namespace RoslynSentinel.Tests.Tools.Basic;

// Not-found text of the SolutionStructureEngine (ProjectStructureEngine.cs) project lookups that moved onto ProjectNotFoundDetail through
// SolutionStructureEngine.ProjectNotFound (plan_not_found_error_orientation, Step 5e slice 1). Each producer is driven through the tool that
// hosts it. The in-memory workspace names its only project "InMemoryProject", so the candidate case misspells that name.
// Producers covered: GetProjectDependenciesAsync (ListSolutionItems kind: dependencies), SplitProjectByFolderAsync (source project, retry names
// sourceProjectName; also asserts that no target project was created, which the reorder of that method guarantees).
// Covered in Tests.Tools.Advanced (ProjectStructureMissAdvancedTests) because their hosts are Advanced tools: GetPublicApiSurfaceAsync,
// DetectLayerViolationsAsync, FindUnusedReferencesAsync. Covered in Tests.Server (CodeTransformMissFileClassTests): the file and class lookups of
// FixMismatchedNamespacesAsync and ConvertToBackgroundServiceAsync, whose host (CodeTransformTools) lives in Tools.Experimental.
[TestFixture]
[Parallelizable(ParallelScope.All)]
public sealed class ProjectStructureMissTests
{
    private const string FileName = "Types.cs";

    private const string Source = "public class Alpha\r\n{\r\n}\r\n";

    private static void AssertFactsAndOptionsOnly(string message)
    {
        Assert.That(message, Does.Not.Contain("tell the user"));
        Assert.That(message, Does.Not.Contain("You MUST"));
        Assert.That(message, Does.Not.Contain("not found."), "No bare 'not found.' sentence.");
    }

    private static void AssertCandidate(string message, string paramName)
    {
        Assert.That(message, Does.Contain("Did you mean 'InMemoryProject'? Retry with " + paramName + ": 'InMemoryProject'."));
        Assert.That(message, Does.Contain("Loaded projects: InMemoryProject."));
        AssertFactsAndOptionsOnly(message);
    }

    private static void AssertNoCandidate(string message, string wanted)
    {
        Assert.That(message, Does.Contain($"No loaded project has a similar name to '{wanted}'."));
        Assert.That(message, Does.Not.Contain("Did you mean"));
        Assert.That(message, Does.Contain("Loaded projects: InMemoryProject."));
        AssertFactsAndOptionsOnly(message);
    }

    // Runs one refused call: a NotFound error, the file untouched and no project added or removed.
    private static async Task<string> RunMissAsync(Func<InMemoryWorkspace, Task<(bool IsError, string? Code, string? Message)>> call)
    {
        using var workspace = InMemoryWorkspace.Create((FileName, Source));
        var before = workspace.ReadText(FileName);
        var projectsBefore = workspace.Manager.CurrentSolution!.Projects.Count();
        var (isError, code, message) = await call(workspace);
        Assert.That(isError, Is.True, message);
        Assert.That(code, Is.EqualTo(nameof(ToolErrorCode.NotFound)), message);
        Assert.That(workspace.ReadText(FileName), Is.EqualTo(before), "A refused call must not write the file.");
        Assert.That(workspace.Manager.CurrentSolution!.Projects.Count(), Is.EqualTo(projectsBefore), "A refused call must not add or remove a project.");
        return message ?? string.Empty;
    }

    // ---- ListSolutionItems(kind: dependencies, projectName): GetProjectDependenciesAsync ----

    [Test]
    [Category("SolutionStructureEngine")]
    public async Task ListSolutionItemsDependencies_MisspeltProject_OffersTheNearProjectAsync()
    {
        var message = await RunMissAsync(async w =>
        {
            var r = await Battery.Basic.TestTools.WorkspaceTools(w.Manager).ListSolutionItemsTool(reason: "test message", SolutionItemsKind.dependencies, "InMemoryProjct");
            return (r.IsError, r.ErrorData?.ErrorCode.ToString(), r.ErrorData?.Message);
        });
        AssertCandidate(message, "projectName");
    }

    [Test]
    [Category("SolutionStructureEngine")]
    public async Task ListSolutionItemsDependencies_NoSimilarProject_StatesTheNoCandidateFactAndListsProjectsAsync()
    {
        var message = await RunMissAsync(async w =>
        {
            var r = await Battery.Basic.TestTools.WorkspaceTools(w.Manager).ListSolutionItemsTool(reason: "test message", SolutionItemsKind.dependencies, "Zzz.Missing");
            return (r.IsError, r.ErrorData?.ErrorCode.ToString(), r.ErrorData?.Message);
        });
        AssertNoCandidate(message, "Zzz.Missing");
    }

    // ---- SplitProjectByFolder: SplitProjectByFolderAsync (source project; the target project must not be created) ----

    [Test]
    [Category("SolutionStructureEngine")]
    public async Task SplitProjectByFolder_MisspeltSourceProject_OffersTheNearProjectNamesSourceProjectNameAndCreatesNoTargetAsync()
    {
        var message = await RunMissAsync(async w =>
        {
            var r = await Battery.Basic.TestTools.WorkspaceTools(w.Manager).SplitProjectByFolderTool(reason: "test message", "InMemoryProjct", "Models", "InMemoryProject.Models");
            return (r.IsError, r.ErrorData?.ErrorCode.ToString(), r.ErrorData?.Message);
        });
        AssertCandidate(message, "sourceProjectName");
        Assert.That(message, Does.Contain("Project 'InMemoryProjct' was not found"), "The wanted source name is echoed.");
    }

    [Test]
    [Category("SolutionStructureEngine")]
    public async Task SplitProjectByFolder_NoSimilarSourceProject_StatesTheNoCandidateFactAndCreatesNoTargetAsync()
    {
        var message = await RunMissAsync(async w =>
        {
            var r = await Battery.Basic.TestTools.WorkspaceTools(w.Manager).SplitProjectByFolderTool(reason: "test message", "Zzz.Missing", "Models", "Zzz.Models");
            return (r.IsError, r.ErrorData?.ErrorCode.ToString(), r.ErrorData?.Message);
        });
        AssertNoCandidate(message, "Zzz.Missing");
    }
}
