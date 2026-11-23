#pragma warning disable CS8618
using Microsoft.Extensions.Logging.Abstractions;
using RoslynSentinel.Engines.Basic;

namespace RoslynSentinel.Tests.Battery.Advanced;

[TestFixture]
[Category("SolutionManagementEngine")] // sentinel:auto-category
// Runs exclusively: SetUp/TearDown wipe every top-level *.cs in the shared working directory (WorkingDirectoryLeakGuard).
[NonParallelizable]
public class SolutionManagementEngineTests
{
    private IWorkspaceManager _workspaceManager;

    [SetUp]
    public void Setup()
    {
        WorkingDirectoryLeakGuard.DeleteLeakedSourceFiles();
        _workspaceManager = Basic.TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        var solution = TestSolutionBuilder.CreateSolutionWithProject("Source", [("Foo.cs", "public class Foo { }")]);
        _workspaceManager.SetTestSolution(solution);
    }

    [TearDown]
    public void TearDown()
    {
        _workspaceManager?.Dispose();
        WorkingDirectoryLeakGuard.DeleteLeakedSourceFiles();
    }

    [Test]
    public async Task CreateProject_NullSolutionPath_ThrowsMissingSolutionPath()
    {
        // AdhocWorkspace has no FilePathWrapper; SolutionPath is also null -> "Solution path not found."
        var ex = await Assert.ThrowsAsync<ToolPreconditionException>(async () =>
            await Basic.TestEngines.SolutionStructureEngine(_workspaceManager, Basic.TestEngines.SentinelConfiguration()).CreateProjectAsync("NewProject", "classlib"));

        Assert.That(ex!.Message, Does.Contain("Solution path not found"),
            "Should fail when no solution file path is available.");
    }

    [Test]
    public async Task SplitProjectByFolder_NullSolutionPath_ThrowsMissingSolutionPath()
    {
        // SplitProject internally calls CreateProjectAsync first, which propagates the null-path error
        var ex = await Assert.ThrowsAsync<ToolPreconditionException>(async () =>
            await Basic.TestEngines.SolutionStructureEngine(_workspaceManager, Basic.TestEngines.SentinelConfiguration()).SplitProjectByFolderAsync("Source", "Services", "Source.Services"));

        Assert.That(ex!.Message, Does.Contain("Solution path not found"),
            "SplitProject should propagate the missing solution path from CreateProject.");
    }
}
