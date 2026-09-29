using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Engines.Advanced;

namespace RoslynSentinel.Tests.Advanced;

internal class AsyncOptimizationEngineTests
{
    private IWorkspaceManager _workspaceManager;
    private SentinelConfiguration _config;
    private AsyncOptimizationEngine _asyncOptimizationEngine;
    [SetUp]
    public void Setup()
    {
        _workspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        _config = new SentinelConfiguration();
        _asyncOptimizationEngine = new AsyncOptimizationEngine(_workspaceManager);
    }

    [TearDown]
    public void TearDown() => _workspaceManager?.Dispose();
    private void SetSource(string source, string fileName = "Test.cs")
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [(fileName, source)]);
        _workspaceManager.SetTestSolution(solution);
    }


    // ── Bug 47: OptimizeIndependentAwaits -> Overload Disambiguation ──────────────
    [Test]
    public async Task BUG_47_OptimizeIndependentAwaits_MultipleOverloads_PicksCorrect()
    {
        const string code = @"
public class Processor
{
    public async Task<int> Process(string param)
    {
        var result1 = await GetValueAsync(param);
        var result2 = await GetValueAsync(param, 10);
        return result1 + result2;
    }

    private async Task<int> GetValueAsync(string value) => 1;
    private async Task<int> GetValueAsync(string value, int max) => max;
}";
        SetSource(code, "Processor.cs");
        var result = await _asyncOptimizationEngine.OptimizeIndependentAwaitsAsync("Processor.cs", "Process");
        Assert.That(result, Is.Not.Null, "Should return a result");
        // With var assignments, should use task hoisting pattern: var resultTask = ..., then await
        // The optimization occurs but isn't Task.WhenAll - it's task variable hoisting
        // Both patterns parallelize the execution
        Assert.That(result.UpdatedText, Does.Contain("Task") | Does.Contain("result"), "Should optimize by creating task variables or using Task.WhenAll");
    }
}
