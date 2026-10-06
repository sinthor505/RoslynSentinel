using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Engines.Advanced;

#pragma warning disable CS8618
namespace RoslynSentinel.Tests.Advanced;

[TestFixture]
[Category("SecurityEngine")] // sentinel:auto-category
public class MassiveQualityTests
{
    private IWorkspaceManager _workspaceManager;
    private SecurityEngine _securityEngine;

    [SetUp]
    public void Setup()
    {
        var config = new SentinelConfiguration();
        _workspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
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
    public async Task FindHardcodedPaths_ShouldFlagPotentialIssues(int id)
    {
        SetSource($@"public class C{id} {{ string path = @""C:\Temp\File{id}.txt""; }}", $"C{id}.cs");
        var results = await _securityEngine.FindHardcodedPathsAsync($"C{id}.cs");
        Assert.That(results.Count, Is.GreaterThan(0));
    }
}
