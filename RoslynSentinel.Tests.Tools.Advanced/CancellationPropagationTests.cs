using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Engines.Advanced;

#pragma warning disable CS8618
namespace RoslynSentinel.Tests.Tools.Advanced;

/// <summary>
/// S1a part 2: the Advanced engines' generic catches must let OperationCanceledException through
/// (see docs/current/proposals/proposal_throw_typed_exceptions_not_failure_results.md), and the
/// whole-method catches must keep the ToolException's ErrorCode on the edit result.
/// </summary>
[TestFixture]
public class CancellationPropagationTests
{
    private IWorkspaceManager _workspaceManager;
    private AsyncOptimizationEngine _asyncOptimizationEngine;
    private LogicSimplificationEngine _logicOptimizationEngine;

    [SetUp]
    public void Setup()
    {
        _workspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        _asyncOptimizationEngine = new AsyncOptimizationEngine(_workspaceManager);
        _logicOptimizationEngine = new LogicSimplificationEngine(_workspaceManager);
    }

    [TearDown]
    public void TearDown() => _workspaceManager?.Dispose();

    private void SetSource(string source, string fileName)
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [(fileName, source)]);
        _workspaceManager.SetTestSolution(solution);
    }

    private static async Task<CancellationToken> CancelledTokenAsync()
    {
        var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        return cts.Token;
    }

    // The one-liner "try { GetSemanticModelAsync } catch { }" sites (AsyncSafetyEngine, AsyncOptimizationEngine)
    // have no propagation test: GetSolutionAsync/GetSyntaxRootAsync throw OperationCanceledException for a
    // pre-cancelled token before the try is reached, so such a test passes without the filter.

    // The whole-method catch used to turn a cancelled token into a CannotEdit "// ErrorDetails:" result.
    [Test]
    [Category("AsyncOptimizationEngine")] // sentinel:auto-category
    public async Task ConvertToAsyncEnumerable_CancelledToken_PropagatesOperationCanceledException()
    {
        SetSource(
            "using System.Collections.Generic; public class C { public List<string> GetNames() { var r = new List<string>(); r.Add(\"a\"); return r; } }",
            "C.cs");
        var token = await CancelledTokenAsync();

        await Assert.CatchAsync<OperationCanceledException>(
            async () => await _asyncOptimizationEngine.ConvertToAsyncEnumerableAsync("C.cs", "GetNames", cancellationToken: token));
    }

    // The catch used to set only Message; a ToolException inside the try must keep its code.
    // Deliberately no SetSource: GetSolutionAsync throws SolutionNotLoadedException inside the try.
    [Test]
    [Category("AsyncOptimizationEngine")] // sentinel:auto-category
    public async Task ConvertToAsyncEnumerable_SolutionNotLoaded_Throws()
    {
        var ex = await Assert.ThrowsAsync<SolutionNotLoadedException>(async () => await _asyncOptimizationEngine.ConvertToAsyncEnumerableAsync("C.cs", "GetNames"));

        Assert.That(ex.ErrorCode, Is.EqualTo(ToolErrorCode.SolutionNotLoaded));
    }

    [Test]
    [Category("AsyncOptimizationEngine")] // sentinel:auto-category
    public async Task PropagateCancellationTokenInMethod_ForwardsToken_ReturnsUpdatedText()
    {
        SetSource(
            "using System.Threading; using System.Threading.Tasks; public class C { public async Task FooAsync(CancellationToken ct) { await BarAsync(); } public Task BarAsync() => Task.CompletedTask; public Task BarAsync(CancellationToken ct) => Task.CompletedTask; }",
            "C.cs");

        var result = await _asyncOptimizationEngine.PropagateCancellationTokenInMethodAsync("C.cs", "FooAsync");

        Assert.That(result.Outcome, Is.EqualTo(EditOutcome.Modified));
        Assert.That(result.UpdatedText, Does.Contain("BarAsync(ct)"));
    }

    [Test]
    [Category("LogicOptimizationEngine")] // sentinel:auto-category
    public async Task ReduceBlockDepth_CancelledToken_PropagatesOperationCanceledException()
    {
        SetSource(
            "public class C { public void M(bool b) { if (b) { System.Console.WriteLine(1); } } }",
            "C.cs");
        var token = await CancelledTokenAsync();

        await Assert.CatchAsync<OperationCanceledException>(
            async () => await _logicOptimizationEngine.ReduceBlockDepthAsync("C.cs", "M", token));
    }

    [Test]
    [Category("LogicOptimizationEngine")] // sentinel:auto-category
    public async Task ReduceBlockDepth_SolutionNotLoaded_Throws()
    {
        var ex = await Assert.ThrowsAsync<SolutionNotLoadedException>(async () => await _logicOptimizationEngine.ReduceBlockDepthAsync("C.cs", "M"));

        Assert.That(ex.ErrorCode, Is.EqualTo(ToolErrorCode.SolutionNotLoaded));
    }
}
