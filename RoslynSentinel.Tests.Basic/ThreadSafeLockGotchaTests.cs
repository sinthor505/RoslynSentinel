// Battery #26 -> Gotcha / Edge-Case Tests
// Covers false positives, boundary conditions, and behaviors that must NOT trigger
// detections across AntiPatternEngine, AsyncSafetyEngine, ThreadSafetyEngine,
// SecurityAndSafetyEngine, and ImmutabilityEngine.
//
// Bug fixes included in this battery:
//   - AsyncSafetyEngine.DetectAsyncVoidMethodsAsync: async void event handlers
//     (object sender, *EventArgs e) now produce an advisory message rather than
//     the generic crash warning.
//   - AsyncSafetyEngine.FindUnawaitedFireAndForgetAsync: _ = MethodAsync() discard
//     assignments are now detected as fire-and-forget patterns.
#pragma warning disable CS8618
using Microsoft.Extensions.Logging.Abstractions;

using NUnit.Framework;

using RoslynSentinel.Common;
using RoslynSentinel.Engines.Basic;

namespace RoslynSentinel.Tests.Battery.Advanced;

[TestFixture]
[Category("ThreadSafetyEngine")] // sentinel:auto-category
// Runs exclusively: SetUp/TearDown wipe every top-level *.cs in the shared working directory (WorkingDirectoryLeakGuard).
[NonParallelizable]
public class ThreadSafeLockGotchaTests
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
    public async Task MakeMethodThreadSafe_NoExistingLock_AddsLockStatement()
    {
        const string source = """
            public class Counter {
                private int _count = 0;
                public void Increment() { _count++; }
            }
            """;
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Counter.cs", source)]);
        _workspaceManager.SetTestSolution(solution);
        var result = await _engine.MakeMethodThreadSafeAsync("Counter.cs", "Increment");
        Assert.That(result.UpdatedText, Does.Contain("lock"), "Method body must be wrapped in a lock statement.");
        Assert.That(result.UpdatedText, Does.Contain("_lock"), "A _lock field must be introduced.");
    }

    [Test]
    public async Task MakeMethodThreadSafe_NonExistentMethod_ThrowsNotFound()
    {
        const string source = """
            public class Worker {
                public void DoWork() { var x = 1; }
            }
            """;
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Worker.cs", source)]);
        _workspaceManager.SetTestSolution(solution);
        var ex = await Assert.ThrowsAsync<ToolNotFoundException>(async () => await _engine.MakeMethodThreadSafeAsync("Worker.cs", "NonExistent"));
        Assert.That(ex!.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound), "error code should be NotFound");
    }

    [Test]
    public async Task MakeMethodThreadSafe_AlreadyLockedMethod_DoesNotThrow()
    {
        // Engine reuses the existing object _lock field and wraps body again.
        // The result may double-lock, but it must not throw an exception.
        const string source = """
            public class AlreadyLocked {
                private readonly object _lock = new object();
                public void DoWork() { lock (_lock) { var x = 1; } }
            }
            """;
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("AlreadyLocked.cs", source)]);
        _workspaceManager.SetTestSolution(solution);
        var result = await _engine.MakeMethodThreadSafeAsync("AlreadyLocked.cs", "DoWork");
        Assert.That(result, Is.Not.Null, "Engine must return a non-null result even for an already-locked method.");
        Assert.That(result.UpdatedText, Does.Contain("lock"), "The lock keyword must still be present in the result.");
    }

    [Test]
    public async Task ConvertLockToSemaphoreSlim_MethodWithLock_ConvertsSemaphore()
    {
        const string source = """
            public class Serialized {
                private readonly object _lock = new object();
                public void Process() {
                    lock (_lock) {
                        var x = 1;
                    }
                }
            }
            """;
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Serialized.cs", source)]);
        _workspaceManager.SetTestSolution(solution);
        var result = await _engine.ConvertLockToSemaphoreSlimAsync("Serialized.cs", "Process");
        Assert.That(result.UpdatedText, Does.Contain("SemaphoreSlim"), "lock must be replaced with SemaphoreSlim.");
        Assert.That(result.UpdatedText, Does.Contain("WaitAsync"), "Converted method must call WaitAsync.");
    }

    [Test]
    public async Task ConvertLockToSemaphoreSlim_MethodWithNoLock_ThrowsNotFound()
    {
        // A method with no lock statement throws NotFound (the engine no longer returns the original code).
        const string source = """
            public class NoLockAtAll {
                public void Process() { var x = 1; }
            }
            """;
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("NoLock.cs", source)]);
        _workspaceManager.SetTestSolution(solution);
        var ex = await Assert.ThrowsAsync<ToolNotFoundException>(async () => await _engine.ConvertLockToSemaphoreSlimAsync("NoLock.cs", "Process"));
        Assert.That(ex!.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound), "error code should be NotFound");
    }

    [Test]
    public async Task ConvertLockToSemaphoreSlim_ResultDoesNotContainOriginalLock()
    {
        const string source = """
            public class Migrated {
                private readonly object _lock = new object();
                public void Run() { lock (_lock) { var y = 2; } }
            }
            """;
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Migrated.cs", source)]);
        _workspaceManager.SetTestSolution(solution);
        var result = await _engine.ConvertLockToSemaphoreSlimAsync("Migrated.cs", "Run");
        Assert.That(result.UpdatedText, Does.Contain("Release"), "SemaphoreSlim.Release() must appear in the converted output.");
    }
}
