// Battery #25 -> Gap Coverage: 40 untested engine methods across 14 engines
// Each group covers the specific methods that had zero test references.
// All tests use in-memory AdhocWorkspace via TestSolutionBuilder (no MSBuild/project loading).
using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Common;
using RoslynSentinel.Engines.Basic;

namespace RoslynSentinel.Tests.Battery.Advanced;

[TestFixture]
[Category("SyntaxUpgradeEngine")] // sentinel:auto-category
// Runs exclusively: SetUp/TearDown wipe every top-level *.cs in the shared working directory (WorkingDirectoryLeakGuard).
[NonParallelizable]
public class SyntaxUpgradeEngineGapTests
{
    private IWorkspaceManager _workspaceManager = null!;
    private SyntaxUpgradeEngine _engine = null!;
    [SetUp]
    public void Setup()
    {
        WorkingDirectoryLeakGuard.DeleteLeakedSourceFiles();
        _workspaceManager = Basic.TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        _engine = Basic.TestEngines.SyntaxUpgradeEngine(_workspaceManager, Basic.TestEngines.SentinelConfiguration());
    }

    [TearDown]
    public void TearDown()
    {
        _workspaceManager?.Dispose();
        WorkingDirectoryLeakGuard.DeleteLeakedSourceFiles();
    }
    [Test]
    public async Task ConvertSwitchExpressionToStatementAsync_FileWithSwitchExpr_ThrowsNotImplemented()
    {
        var source = @"
public class Router {
    public string Route(int x) => x switch {
        1 => ""a"",
        2 => ""b"",
        _ => ""c""
    };
}";
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Router.cs", source)]);
        _workspaceManager.SetTestSolution(solution);
        var ex = await Assert.ThrowsAsync<ToolNotImplementedException>(async () => await _engine.ConvertSwitchExpressionToStatementAsync("Router.cs"));
        Assert.That(ex!.ErrorCode, Is.EqualTo(ToolErrorCode.NotImplemented));
    }

    [Test]
    public async Task ConvertSwitchExpressionToStatementAsync_UnknownFile_ThrowsNotFound()
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Other.cs", "public class X {}")]);
        _workspaceManager.SetTestSolution(solution);
        var ex = await Assert.ThrowsAsync<ToolNotFoundException>(async () => await _engine.ConvertSwitchExpressionToStatementAsync("NoFile.cs"));
        Assert.That(ex!.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound));
    }

    [Test]
    public async Task UseFieldBackedPropertiesAsync_AutoProperty_ThrowsNotFound()
    {
        var source = "public class Entity { public string Name { get; set; } = \"\"; }";
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Entity.cs", source)]);
        _workspaceManager.SetTestSolution(solution);
        var ex = await Assert.ThrowsAsync<ToolNotFoundException>(async () => await _engine.UseFieldBackedPropertiesAsync("Entity.cs"));
        Assert.That(ex!.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound));
    }

    [Test]
    public async Task UseFieldBackedProperties_MissingFile_ThrowsNotFound()
    {
        // File not in workspace must surface as a typed NotFound, not a result string.
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Other.cs", "public class X {}")]);
        _workspaceManager.SetTestSolution(solution);
        var ex = await Assert.ThrowsAsync<ToolNotFoundException>(async () => await _engine.UseFieldBackedPropertiesAsync("NoFile.cs"));
        Assert.That(ex.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound));
    }
}
