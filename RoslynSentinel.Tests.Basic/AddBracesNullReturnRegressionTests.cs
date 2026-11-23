using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Engines.Basic;

namespace RoslynSentinel.Tests.Advanced;

/// <summary>
/// Regression tests for AddBraces and UpgradePatternMatching null-return bugs:
/// SyntaxUpgradeEngine returns "" when the file is not found in the workspace,
/// which the tool layer now converts to an InvalidOperationException.
/// </summary>
[TestFixture]
[Category("SyntaxUpgradeEngine")] // sentinel:auto-category
public class AddBracesNullReturnRegressionTests
{
    private IWorkspaceManager _workspaceManager;
    private SyntaxUpgradeEngine _engine;
    private SentinelConfiguration _config;
    [SetUp]
    public void Setup()
    {
        _workspaceManager = Battery.Basic.TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        _config = Battery.Basic.TestEngines.SentinelConfiguration();
        _engine = Battery.Basic.TestEngines.SyntaxUpgradeEngine(_workspaceManager, _config);
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Stub.cs", "public class Stub { public void Foo() { } }")]);
        _workspaceManager.SetTestSolution(solution);
    }

    [TearDown]
    public void TearDown() => _workspaceManager?.Dispose();
    [Test]
    public async Task AddBracesAsync_FileNotInWorkspace_ThrowsNotFound()
    {
        var ex = await Assert.ThrowsAsync<ToolNotFoundException>(async () => await _engine.AddBracesAsync("nonexistent.cs"));
        Assert.That(ex!.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound));
    }

    [Test]
    public async Task AddBraces_Tool_FileNotInWorkspace_ThrowsToolNotFoundException()
    {
        var ex = await Assert.ThrowsAsync<ToolNotFoundException>(async () => await _engine.AddBracesAsync("nonexistent.cs"));
        Assert.That(ex!.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound));
    }

    [Test]
    public async Task UpgradePatternMatchingAsync_FileNotInWorkspace_ThrowsNotFound()
    {
        var ex = await Assert.ThrowsAsync<ToolNotFoundException>(async () => await _engine.UpgradePatternMatchingAsync("nonexistent.cs"));
        Assert.That(ex!.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound));
    }

    [Test]
    public async Task UpgradePatternMatching_Tool_FileNotInWorkspace_ThrowsToolNotFoundException()
    {
        var ex = await Assert.ThrowsAsync<ToolNotFoundException>(async () => await _engine.UpgradePatternMatchingAsync("nonexistent.cs"));
        Assert.That(ex!.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound));
    }
}
