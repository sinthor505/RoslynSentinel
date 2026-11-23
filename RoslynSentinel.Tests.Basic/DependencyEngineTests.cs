#pragma warning disable CS8618
using Microsoft.Extensions.Logging.Abstractions;
using RoslynSentinel.Engines.Basic;

namespace RoslynSentinel.Tests.Battery.Advanced;

[TestFixture]
[Category("DependencyEngine")] // sentinel:auto-category
// Runs exclusively: SetUp/TearDown wipe every top-level *.cs in the shared working directory (WorkingDirectoryLeakGuard).
[NonParallelizable]
public class DependencyEngineTests
{
    private IWorkspaceManager _workspaceManager;
    [SetUp]
    public void Setup()
    {
        WorkingDirectoryLeakGuard.DeleteLeakedSourceFiles();
        _workspaceManager = Basic.TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
    }

    [TearDown]
    public void TearDown()
    {
        _workspaceManager?.Dispose();
        WorkingDirectoryLeakGuard.DeleteLeakedSourceFiles();
    }
    [Test]
    public async Task GetProjectDependencies_KnownInMemoryProject_ReturnsEmptyLists()
    {
        // AdhocWorkspace projects have no ProjectReferences and no .csproj file
        var solution = TestSolutionBuilder.CreateSolutionWithProject("OrderService", [("Order.cs", "public class Order { }")]);
        _workspaceManager.SetTestSolution(solution);
        var report = await Basic.TestEngines.SolutionStructureEngine(_workspaceManager, Basic.TestEngines.SentinelConfiguration()).GetProjectDependenciesAsync("OrderService", CancellationToken.None);
        Assert.That(report.ProjectReferences, Is.Empty, "In-memory project has no project references");
        Assert.That(report.PackageReferences, Is.Empty, "In-memory project has no .csproj file to parse");
    }

    [Test]
    public async Task GetProjectDependencies_UnknownProject_ThrowsInvalidOperation()
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Test.cs", "public class Foo { }")]);
        _workspaceManager.SetTestSolution(solution);
        await Assert.ThrowsAsync<ToolNotFoundException>(async () => await Basic.TestEngines.SolutionStructureEngine(_workspaceManager, Basic.TestEngines.SentinelConfiguration()).GetProjectDependenciesAsync("NonExistent", CancellationToken.None));
    }

    [Test]
    public async Task FindUnusedReferences_InMemoryProject_ReturnsEmpty()
    {
        // AdhocWorkspace compilation has no CompilationReference typed references (metadata only)
        // -> FindUnusedReferences returns empty because the cast check for CompilationReference fails
        var solution = TestSolutionBuilder.CreateSolutionWithProject("PriceService", [("Price.cs", "public class Price { public decimal Amount { get; set; } }")]);
        _workspaceManager.SetTestSolution(solution);
        var result = await Basic.TestEngines.SolutionStructureEngine(_workspaceManager, Basic.TestEngines.SentinelConfiguration()).FindUnusedReferencesAsync("PriceService");
        Assert.That(result, Is.Empty, "AdhocWorkspace has no CompilationReferences to flag as unused");
    }
}
