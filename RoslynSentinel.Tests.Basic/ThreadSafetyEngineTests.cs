// Battery #11 -> ImmutabilityEngine / ThreadSafetyEngine / AsyncSafetyEngine / DeadCodeEngine
// Adds dedicated XxxEngineTests fixture classes for 4 more engine classes.
// All tests run in-memory via AdhocWorkspace (no MSBuild/project-file loading).
using Microsoft.Extensions.Logging.Abstractions;
using RoslynSentinel.Engines.Basic;

namespace RoslynSentinel.Tests.Battery.Advanced;

[TestFixture]
[Category("ThreadSafetyEngine")] // sentinel:auto-category
// Runs exclusively: SetUp/TearDown wipe every top-level *.cs in the shared working directory (WorkingDirectoryLeakGuard).
[NonParallelizable]
public class ThreadSafetyEngineTests
{
    private IWorkspaceManager _workspaceManager = null!;
    private ThreadSafetyEngine _engine = null!;
    [SetUp]
    public void Setup()
    {
        WorkingDirectoryLeakGuard.DeleteLeakedSourceFiles();
        _workspaceManager = Basic.TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        _engine = Basic.TestEngines.ThreadSafetyEngine(_workspaceManager);
    }

    [TearDown]
    public void TearDown()
    {
        _workspaceManager?.Dispose();
        WorkingDirectoryLeakGuard.DeleteLeakedSourceFiles();
    }
    [Test]
    public async Task MakeMethodThreadSafe_SimpleMethod_AddsLockStatement()
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Counter.cs", "public class Counter { private int _count; public void Increment() { _count++; } }")]);
        _workspaceManager.SetTestSolution(solution);
        var result = await _engine.MakeMethodThreadSafeAsync("Counter.cs", "Increment");
        Assert.That(result.UpdatedText, Does.Contain("lock"), "Method body should be wrapped in a lock statement");
        Assert.That(result.UpdatedText, Does.Contain("_lock"), "A lock object field should be added");
        Assert.That(result.UpdatedText, Does.Not.StartWith("// ErrorDetails:"), "Should not return an error comment");
    }

    [Test]
    public async Task MakeMethodThreadSafe_UnknownFile_ThrowsNotFound()
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Counter.cs", "public class Counter { public void Inc() { } }")]);
        _workspaceManager.SetTestSolution(solution);
        var ex = await Assert.ThrowsAsync<ToolNotFoundException>(async () => await _engine.MakeMethodThreadSafeAsync("DoesNotExist.cs", "Inc"));
        Assert.That(ex!.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound), "error code should be NotFound");
    }

    [Test]
    public async Task MakeMethodThreadSafe_UnknownMethod_ThrowsNotFound()
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Counter.cs", "public class Counter { public void Inc() { } }")]);
        _workspaceManager.SetTestSolution(solution);
        var ex = await Assert.ThrowsAsync<ToolNotFoundException>(async () => await _engine.MakeMethodThreadSafeAsync("Counter.cs", "NonExistentMethod"));
        Assert.That(ex!.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound), "error code should be NotFound");
        Assert.That(ex.Message, Does.Contain("NonExistentMethod"), "the exception should name the missing method");
    }
}
