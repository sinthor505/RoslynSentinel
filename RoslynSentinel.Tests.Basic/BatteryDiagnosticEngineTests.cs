// Battery #10 -> ValidationEngine / MetricsEngine / DiagnosticEngine / SecurityEngine
// Adds dedicated XxxEngineTests fixture classes for 4 more engine classes.
// All tests run in-memory via AdhocWorkspace (no MSBuild/project-file loading).

using Microsoft.Extensions.Logging.Abstractions;
using RoslynSentinel.Engines.Basic;

namespace RoslynSentinel.Tests.Battery.Advanced;

[TestFixture]
[Category("DiagnosticEngine")] // sentinel:auto-category
[Category("DiagnosticSummary")] // sentinel:auto-category
// Runs exclusively: SetUp/TearDown wipe every top-level *.cs in the shared working directory (WorkingDirectoryLeakGuard).
[NonParallelizable]
public class BatteryDiagnosticEngineTests
{
    private IWorkspaceManager _workspaceManager = null!;
    private DiagnosticEngine _engine = null!;

    [SetUp]
    public void Setup()
    {
        WorkingDirectoryLeakGuard.DeleteLeakedSourceFiles();
        _workspaceManager = Basic.TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        _engine = Basic.TestEngines.DiagnosticEngine(_workspaceManager);

        var solution = TestSolutionBuilder.CreateSolutionWithProject("Source",
            [("Clean.cs", "public class Clean { public int Add(int a, int b) => a + b; }")]);
        _workspaceManager.SetTestSolution(solution);
    }

    [TearDown]
    public void TearDown()
    {
        _workspaceManager?.Dispose();
        WorkingDirectoryLeakGuard.DeleteLeakedSourceFiles();
    }

    [Test]
    public async Task GetFileDiagnostics_CleanFile_ReturnsZeroErrors()
    {
        var summary = await _engine.GetFileDiagnosticsAsync("Clean.cs");

        Assert.That(summary.Errors, Is.Zero, "Clean file should report no errors");
    }

    [Test]
    public async Task GetFileDiagnostics_UnknownFile_ThrowsFileNotFound()
    {
        await Assert.ThrowsAsync<ToolNotFoundException>(
            () => _engine.GetFileDiagnosticsAsync("DoesNotExist.cs"));
    }

    [Test]
    public async Task GetProjectDiagnostics_KnownProject_ReturnsSummaryWithNonNegativeCounts()
    {
        var summary = await _engine.GetProjectDiagnosticsAsync("Source");

        Assert.That(summary, Is.Not.Null, "Known project should return a summary");
        Assert.That(summary, Is.Not.Null, "Known project should succeed");
        Assert.That(summary.Errors, Is.Zero, "Known project should report no errors");
        Assert.That(summary.Warnings, Is.Zero, "Known project should report no warnings");
    }
}
