using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Engines.Basic;

namespace RoslynSentinel.Tests.Advanced;

[TestFixture]
[Category("ThreadSafetyEngine")] // sentinel:auto-category
public class Bug53MakeMethodThreadSafeRegressionTests
{
    private IWorkspaceManager _workspaceManager;
    private ThreadSafetyEngine _threadSafetyEngine;
    [SetUp]
    public void Setup()
    {
        _workspaceManager = Battery.Basic.TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        _threadSafetyEngine = Battery.Basic.TestEngines.ThreadSafetyEngine(_workspaceManager);
    }

    [TearDown]
    public void TearDown() => _workspaceManager?.Dispose();
    private void SetSource(string source, string fileName = "Test.cs")
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [(fileName, source)]);
        _workspaceManager.SetTestSolution(solution);
    }

    [Test]
    public async Task BUG_53_MakeMethodThreadSafe_SimpleMethod_NoServerCrash()
    {
        const string code = @"
public class Counter
{
    private int _count = 0;

    public void Increment()
    {
        _count++;
    }
}";
        SetSource(code, "Counter.cs");
        var document = (await _workspaceManager.GetSolutionAsync(ReadSource.Committed, CancellationToken.None))?.Projects.First()?.Documents.First();
        if (document == null)
        {
            Assert.Fail("Document not found");
        }

        var result = await _threadSafetyEngine.MakeMethodThreadSafeAsync(document!.FilePath!, "Increment");
        Assert.That(result, Is.Not.Null, "Should return non-null result");
        Assert.That(result.UpdatedText, Is.Not.Empty, "Should return non-empty result");
        Assert.That(result.UpdatedText, Does.Contain("lock"), "Should contain lock statement");
        Assert.That(result.UpdatedText, Does.Contain("_lock"), "Should contain lock field");
    }
}
