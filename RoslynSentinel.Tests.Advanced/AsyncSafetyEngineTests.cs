using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Engines.Advanced;

#pragma warning disable CS8618
namespace RoslynSentinel.Tests.Advanced;

[TestFixture]
internal class AsyncSafetyEngineTests
{
    private IWorkspaceManager _workspaceManager;
    private AsyncAnalysisEngine _asyncSafetyEngine;

    [SetUp]
    public void Setup()
    {
        var config = new SentinelConfiguration();
        _workspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        _asyncSafetyEngine = new AsyncAnalysisEngine(_workspaceManager);
    }

    [TearDown]
    public void TearDown() => _workspaceManager?.Dispose();

    private void SetSource(string source, string fileName = "Test.cs")
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", new[] { (fileName, source) });
        _workspaceManager.SetTestSolution(solution);
    }

    [Test]
    [TestCase(1)]
    [TestCase(2)]
    [TestCase(3)]
    [TestCase(4)]
    [TestCase(5)]
    public async Task DetectAsyncVoid_ShouldFlagMethods(int id)
    {
        SetSource($"public class C{id} {{ public async void M{id}() {{}} }}", $"C{id}.cs");
        var results = await _asyncSafetyEngine.DetectAsyncVoidMethodsAsync($"C{id}.cs");
        Assert.That(results.Count, Is.EqualTo(1));
    }
}
