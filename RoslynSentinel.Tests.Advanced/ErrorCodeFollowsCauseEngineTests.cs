using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Common;
using RoslynSentinel.Engines.Advanced;

#pragma warning disable CS8618

namespace RoslynSentinel.Tests.Advanced;

[TestFixture]
[Category("ErrorCodeFollowsCause")]
public class ErrorCodeFollowsCauseEngineTests
{
    private IWorkspaceManager _workspaceManager;
    private AsyncOptimizationEngine _asyncEngine;
    private LogicSimplificationEngine _logicEngine;

    [SetUp]
    public void Setup()
    {
        _workspaceManager = Battery.Basic.TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        _asyncEngine = Battery.Advanced.TestEnginesAdvanced.AsyncOptimizationEngine(_workspaceManager);
        _logicEngine = Battery.Advanced.TestEnginesAdvanced.LogicSimplificationEngine(_workspaceManager);
    }

    [TearDown]
    public void TearDown() => _workspaceManager.Dispose();

    private void SetSource(string source, string fileName = "Test.cs")
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [(fileName, source)]);
        _workspaceManager.SetTestSolution(solution);
    }

    [Test]
    public async Task OptimizeIndependentAwaits_UnknownMethod_ThrowsNotFound()
    {
        SetSource("using System.Threading.Tasks; public class S { public async Task M() { await Task.Delay(1); } }");

        var ex = await Assert.ThrowsAsync<ToolNotFoundException>(
            async () => await _asyncEngine.OptimizeIndependentAwaitsAsync("Test.cs", "Missing"));

        Assert.That(ex.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound));
        Assert.That(ex.Message, Does.Not.Contain("not found or"));
    }

    [Test]
    public async Task OptimizeIndependentAwaits_ExpressionBodiedMethod_ThrowsTargetIneligible()
    {
        SetSource("using System.Threading.Tasks; public class S { public Task<int> M() => Task.FromResult(1); }");

        var ex = await Assert.ThrowsAsync<ToolTargetIneligibleException>(
            async () => await _asyncEngine.OptimizeIndependentAwaitsAsync("Test.cs", "M"));

        Assert.That(ex.ErrorCode, Is.EqualTo(ToolErrorCode.TargetIneligible));
    }

    [Test]
    public async Task ReduceBlockDepth_UnknownMethod_ThrowsNotFound()
    {
        SetSource("public class S { public void M() { } }");

        var ex = await Assert.ThrowsAsync<ToolNotFoundException>(
            async () => await _logicEngine.ReduceBlockDepthAsync("Test.cs", "Missing"));

        Assert.That(ex.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound));
        Assert.That(ex.Message, Does.Not.Contain("not found or"));
    }

    [Test]
    public async Task ReduceBlockDepth_AbstractMethod_ThrowsTargetIneligible()
    {
        SetSource("public abstract class S { public abstract void M(); }");

        var ex = await Assert.ThrowsAsync<ToolTargetIneligibleException>(
            async () => await _logicEngine.ReduceBlockDepthAsync("Test.cs", "M"));

        Assert.That(ex.ErrorCode, Is.EqualTo(ToolErrorCode.TargetIneligible));
    }
}
