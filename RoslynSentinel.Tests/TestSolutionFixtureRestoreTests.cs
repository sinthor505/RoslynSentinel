using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Common;

namespace RoslynSentinel.Tests;

/// <summary>
/// Covers the opt-in <c>restorePackages</c> switch on <see cref="TestSolutionFixture"/>. The default
/// fixture must leave no restore assets in its copy; the opt-in fixture must produce them for
/// ContosoOrders.Tests. The second test runs <c>dotnet restore</c>, so it is categorised as out of process.
/// </summary>
[TestFixture]
[Category("OutOfProcess")]
public class TestSolutionFixtureRestoreTests
{
    [Test]
    public void DefaultFixture_HasNoRestoreAssets()
    {
        using var fixture = new TestSolutionFixture();

        var assets = Directory.GetFiles(fixture.SolutionDirectory, "project.assets.json", SearchOption.AllDirectories);
        Assert.That(assets, Is.Empty, "The default fixture must not run restore, but restore assets were found in its copy.");
    }

    [Test]
    public void RestoreFixture_ProducesAssetsForTheTestProject()
    {
        using var fixture = new TestSolutionFixture(restorePackages: true);

        var assetsPath = Path.Combine(fixture.SolutionDirectory, "ContosoOrders.Tests", "obj", "project.assets.json");
        Assert.That(File.Exists(assetsPath), Is.True, $"Expected restore assets at '{assetsPath}' after restorePackages: true.");
    }

    /// <summary>
    /// Runs the same solution-scope in-memory build that the Build tool uses for level=quickBuild
    /// (BuildEngine.RunQuickBuildAsync) against a freshly loaded copy of the solution.
    /// </summary>
    private static async Task<BuildResult> RunSolutionQuickBuildAsync(string solutionPath)
    {
        var workspaceManager = Battery.Basic.TestEngines.PersistentWorkspaceManager(new NullLogger<IWorkspaceManager>());
        try
        {
            await workspaceManager.LoadSolutionAsync(solutionPath, null, TestSolutionFixture.LoadTimeout, CancellationToken.None);
            var diagnosticEngine = Battery.Basic.TestEngines.DiagnosticEngine(workspaceManager);
            var buildEngine = Battery.Basic.TestEngines.BuildEngine(workspaceManager, diagnosticEngine);
            var result = await buildEngine.RunQuickBuildAsync(ToolScope.solution, null, maxDetails: 200, CancellationToken.None);
            if (!result.TryGetData(out var build))
            {
                Assert.Fail("quickBuild returned no BuildResult data.");
            }

            return build!;
        }
        finally
        {
            workspaceManager.Dispose();
        }
    }

    private static bool IsInTestProject(DiagnosticInfo diagnostic) =>
    diagnostic.FilePath.ToString().Contains("ContosoOrders.Tests", StringComparison.OrdinalIgnoreCase) ||
    (diagnostic.Project?.Contains("ContosoOrders.Tests", StringComparison.OrdinalIgnoreCase) ?? false);

    private static string DescribeErrors(BuildResult build) =>
    build.Errors.Count == 0
        ? "none"
        : string.Join("; ", build.Errors.Take(20).Select(e => $"{e.Id} {Path.GetFileName(e.FilePath.ToString())}: {e.Message}"));

    [Test]
    public async Task QuickBuild_DefaultFixture_ReportsCs0246ForXunit()
    {
        // Documents the cause: without restore, the in-memory solution quickBuild cannot resolve Xunit in ContosoOrders.Tests.
        using var fixture = new TestSolutionFixture();

        var build = await RunSolutionQuickBuildAsync(fixture.SolutionPath);

        var testProjectCs0246 = build.Errors.Where(e => e.Id == "CS0246" && IsInTestProject(e)).ToList();
        Assert.That(testProjectCs0246, Is.Not.Empty,
            $"Expected CS0246 in ContosoOrders.Tests without restore. Errors reported: {DescribeErrors(build)}");
    }

    [Test]
    public async Task QuickBuild_RestoredFixture_HasNoCs0246InTheTestProject()
    {
        using var fixture = new TestSolutionFixture(restorePackages: true);

        var build = await RunSolutionQuickBuildAsync(fixture.SolutionPath);

        var unresolved = build.Errors.Where(e => (e.Id == "CS0246" || e.Id == "CS0234") && IsInTestProject(e)).ToList();
        Assert.That(unresolved, Is.Empty,
            $"Restored fixture still reports unresolved types in ContosoOrders.Tests. Errors reported: {DescribeErrors(build)}");
    }
}
