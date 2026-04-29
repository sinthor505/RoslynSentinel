using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging.Abstractions;
using RoslynSentinel.Server;

#pragma warning disable CS8618
namespace RoslynSentinel.Tests;

[TestFixture]
public class MassiveQualityTests
{
    private PersistentWorkspaceManager _workspaceManager;
    private AnalysisEngine _analysisEngine;
    private SecurityEngine _securityEngine;
    private AsyncSafetyEngine _asyncSafetyEngine;

    [SetUp]
    public void Setup()
    {
        var config = new SentinelConfiguration();
        _workspaceManager = new PersistentWorkspaceManager(NullLogger<PersistentWorkspaceManager>.Instance);
        _analysisEngine = new AnalysisEngine(_workspaceManager, config);
        _asyncSafetyEngine = new AsyncSafetyEngine(_workspaceManager);
        _securityEngine = new SecurityEngine(_workspaceManager);
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
    public async Task FindBoxingAllocations_ShouldIdentifyBoxing(int id)
    {
        SetSource($"public class C{id} {{ void M() {{ object o = {id}; }} }}", $"C{id}.cs");
        var results = await _analysisEngine.FindBoxingAllocationsAsync(filePath: $"C{id}.cs");
        Assert.That(results.Count, Is.EqualTo(1));
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

    [Test]
    [TestCase(1)]
    [TestCase(2)]
    [TestCase(3)]
    [TestCase(4)]
    [TestCase(5)]
    public async Task FindHardcodedPaths_ShouldFlagPotentialIssues(int id)
    {
        SetSource($@"public class C{id} {{ string path = @""C:\Temp\File{id}.txt""; }}", $"C{id}.cs");
        var results = await _securityEngine.FindHardcodedPathsAsync($"C{id}.cs");
        Assert.That(results.Count, Is.GreaterThan(0));
    }
}
