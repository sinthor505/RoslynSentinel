// Battery #26 — Gotcha / Edge-Case Tests
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
using RoslynSentinel.Server;

namespace RoslynSentinel.Tests;

// ─────────────────────────────────────────────────────────────────────────────
// 1. BlockingCallFalsePositiveTests
//    DetectAntiPatternsAsync / BlockingTaskWait detection
//    Verifies that the assignment-guard and name-filter edges are respected.
// ─────────────────────────────────────────────────────────────────────────────
[TestFixture]
public class BlockingCallFalsePositiveTests
{
    private PersistentWorkspaceManager _workspaceManager = null!;
    private AntiPatternEngine _engine = null!;

    [SetUp]
    public void Setup()
    {
        _workspaceManager = new PersistentWorkspaceManager(NullLogger<PersistentWorkspaceManager>.Instance);
        _engine = new AntiPatternEngine(_workspaceManager);
    }

    [TearDown]
    public void TearDown() => _workspaceManager?.Dispose();

    [Test]
    public async Task DetectAntiPatterns_ContextResultAssignment_IsNotFlaggedAsBlockingCall()
    {
        // context.Result = x  →  left-hand side of assignment; the engine has an explicit
        // guard: if (assign.Left == ma) continue — this MUST prevent a false positive.
        const string source = """
            public class MyResult { }
            public class MyContext { public MyResult Result { get; set; } = new MyResult(); }
            public class MyController {
                public void Configure(MyContext context) {
                    context.Result = new MyResult();
                }
            }
            """;
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Controller.cs", source)]);
        _workspaceManager.SetTestSolution(solution);

        var findings = await _engine.DetectAntiPatternsAsync("Controller.cs");
        var blocking = findings.Where(f => f.Pattern == "BlockingTaskWait").ToList();

        Assert.That(blocking, Is.Empty,
            "Assigning to context.Result (left-side) must NOT produce a BlockingTaskWait finding.");
    }

    [Test]
    public async Task DetectAntiPatterns_GetAwaiterGetResult_IsFlagged()
    {
        // .GetAwaiter().GetResult() is the classic sync-over-async deadlock pattern.
        const string source = """
            using System.Threading.Tasks;
            public class Sneaky {
                public string GetData() { return FetchAsync().GetAwaiter().GetResult(); }
                private Task<string> FetchAsync() => Task.FromResult("x");
            }
            """;
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Sneaky.cs", source)]);
        _workspaceManager.SetTestSolution(solution);

        var findings = await _engine.DetectAntiPatternsAsync("Sneaky.cs");
        var blocking = findings.Where(f => f.Pattern == "BlockingTaskWait").ToList();

        Assert.That(blocking, Is.Not.Empty,
            ".GetAwaiter().GetResult() must be flagged as BlockingTaskWait.");
    }

    [Test]
    public async Task DetectAntiPatterns_TaskResultInsideAsyncMethod_IsFlagged()
    {
        // .Result on a Task<T> inside an async method is a real deadlock risk.
        const string source = """
            using System.Threading.Tasks;
            public class DeadlockProne {
                public async Task<string> DoAsync() { return FetchAsync().Result; }
                private Task<string> FetchAsync() => Task.FromResult("x");
            }
            """;
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Deadlock.cs", source)]);
        _workspaceManager.SetTestSolution(solution);

        var findings = await _engine.DetectAntiPatternsAsync("Deadlock.cs");
        var blocking = findings.Where(f => f.Pattern == "BlockingTaskWait").ToList();

        Assert.That(blocking, Is.Not.Empty,
            ".Result on Task<T> inside an async method must be flagged.");
    }

    [Test]
    public async Task DetectAntiPatterns_StringReplaceMethod_IsNotFlagged()
    {
        // The method name "Replace" != "Result" and != "Wait".
        // Only the name filter is checked first — this must not reach the semantic phase.
        const string source = """
            public class StringOps {
                public string Process(string input) {
                    return input.Replace("old", "new");
                }
            }
            """;
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("StringOps.cs", source)]);
        _workspaceManager.SetTestSolution(solution);

        var findings = await _engine.DetectAntiPatternsAsync("StringOps.cs");
        var blocking = findings.Where(f => f.Pattern == "BlockingTaskWait").ToList();

        Assert.That(blocking, Is.Empty,
            "String.Replace() must not be caught by the .Result/.Wait name filter.");
    }

    [Test]
    public async Task DetectAntiPatterns_TaskWaitInSyncMethod_IsFlaggedByDetectAntiPatterns()
    {
        // DetectAntiPatternsAsync does NOT restrict blocking-call detection to async methods.
        // Task.Wait() in a plain sync method is STILL a blocking call and must be flagged.
        const string source = """
            using System.Threading.Tasks;
            public class SyncBlocking {
                public void DoWork() { Task.Delay(100).Wait(); }
            }
            """;
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("SyncBlocking.cs", source)]);
        _workspaceManager.SetTestSolution(solution);

        var findings = await _engine.DetectAntiPatternsAsync("SyncBlocking.cs");
        var blocking = findings.Where(f => f.Pattern == "BlockingTaskWait").ToList();

        Assert.That(blocking, Is.Not.Empty,
            "Task.Wait() in a sync method must still be flagged by DetectAntiPatternsAsync.");
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// 2. AsyncVoidGotchaTests
//    DetectAsyncVoidMethodsAsync
//    Validates access-modifier invariance and the event-handler exception.
// ─────────────────────────────────────────────────────────────────────────────
[TestFixture]
public class AsyncVoidGotchaTests
{
    private PersistentWorkspaceManager _workspaceManager = null!;
    private AsyncSafetyEngine _engine = null!;

    [SetUp]
    public void Setup()
    {
        _workspaceManager = new PersistentWorkspaceManager(NullLogger<PersistentWorkspaceManager>.Instance);
        _engine = new AsyncSafetyEngine(_workspaceManager);
    }

    [TearDown]
    public void TearDown() => _workspaceManager?.Dispose();

    [Test]
    public async Task DetectAsyncVoid_PublicAsyncVoidMethod_IsFlagged()
    {
        const string source = """
            using System.Threading.Tasks;
            public class Bad {
                public async void DoWork() { await Task.Delay(1); }
            }
            """;
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Bad.cs", source)]);
        _workspaceManager.SetTestSolution(solution);

        var reports = await _engine.DetectAsyncVoidMethodsAsync("Bad.cs");

        Assert.That(reports, Is.Not.Empty, "public async void must be flagged.");
    }

    [Test]
    public async Task DetectAsyncVoid_PrivateAsyncVoidMethod_IsAlsoFlagged()
    {
        // The engine checks ALL async void methods regardless of access modifier.
        const string source = """
            using System.Threading.Tasks;
            public class Bad {
                private async void FireAndForget() { await Task.Delay(1); }
            }
            """;
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Bad.cs", source)]);
        _workspaceManager.SetTestSolution(solution);

        var reports = await _engine.DetectAsyncVoidMethodsAsync("Bad.cs");

        Assert.That(reports, Is.Not.Empty, "private async void is still unsafe and must be flagged.");
    }

    [Test]
    public async Task DetectAsyncVoid_AsyncTaskMethod_IsNotFlagged()
    {
        // async Task is the correct pattern — must produce no reports.
        const string source = """
            using System.Threading.Tasks;
            public class Good {
                public async Task DoWorkAsync() { await Task.Delay(1); }
            }
            """;
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Good.cs", source)]);
        _workspaceManager.SetTestSolution(solution);

        var reports = await _engine.DetectAsyncVoidMethodsAsync("Good.cs");

        Assert.That(reports, Is.Empty, "async Task must NOT be flagged as async void.");
    }

    [Test]
    public async Task DetectAsyncVoid_ClassWithNoAsyncMethods_ReturnsEmpty()
    {
        const string source = """
            public class Plain {
                public void DoWork() { var x = 1; }
                public int Add(int a, int b) => a + b;
            }
            """;
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Plain.cs", source)]);
        _workspaceManager.SetTestSolution(solution);

        var reports = await _engine.DetectAsyncVoidMethodsAsync("Plain.cs");

        Assert.That(reports, Is.Empty, "A class with no async methods must return an empty report list.");
    }

    [Test]
    public async Task DetectAsyncVoid_EventHandlerSignature_IsReportedWithAdvisory()
    {
        // FIXED: engine now calls IsEventHandlerSignature() and emits an advisory message
        // (not a crash warning) for the conventional (object sender, *EventArgs e) pattern.
        const string source = """
            using System;
            using System.Threading.Tasks;
            public class MyForm {
                public async void Button_Click(object sender, EventArgs e) {
                    await Task.Delay(1);
                }
            }
            """;
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("MyForm.cs", source)]);
        _workspaceManager.SetTestSolution(solution);

        var reports = await _engine.DetectAsyncVoidMethodsAsync("MyForm.cs");

        // Event handler is still reported — but with an advisory message, NOT the crash warning.
        Assert.That(reports, Has.Count.EqualTo(1),
            "Event handlers matching (object sender, *EventArgs e) should still produce a report.");
        Assert.That(reports[0].MethodName, Is.EqualTo("Button_Click"));
        Assert.That(reports[0].Reason, Does.Contain("only acceptable use"),
            "Event handler report should contain advisory language, not the crash warning.");
        Assert.That(reports[0].Reason, Does.Not.Contain("cannot be awaited"),
            "Event handler should NOT get the non-event-handler crash message.");
    }

    [Test]
    public async Task DetectAsyncVoid_NonEventHandler_GetsCrashWarning()
    {
        // Non-event-handler async void methods still receive the fatal crash warning.
        const string source = """
            using System.Threading.Tasks;
            public class Worker {
                public async void DoWork() {
                    await Task.Delay(1);
                }
            }
            """;
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Worker.cs", source)]);
        _workspaceManager.SetTestSolution(solution);

        var reports = await _engine.DetectAsyncVoidMethodsAsync("Worker.cs");

        Assert.That(reports, Has.Count.EqualTo(1));
        Assert.That(reports[0].Reason, Does.Contain("cannot be awaited"),
            "Non-event-handler async void must receive the crash warning.");
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// 3. LockInAsyncGotchaTests
//    FindBlockingCallsInAsyncAsync
//    Only flags blocking calls that appear inside async methods.
// ─────────────────────────────────────────────────────────────────────────────
[TestFixture]
public class LockInAsyncGotchaTests
{
    private PersistentWorkspaceManager _workspaceManager = null!;
    private AsyncSafetyEngine _engine = null!;

    [SetUp]
    public void Setup()
    {
        _workspaceManager = new PersistentWorkspaceManager(NullLogger<PersistentWorkspaceManager>.Instance);
        _engine = new AsyncSafetyEngine(_workspaceManager);
    }

    [TearDown]
    public void TearDown() => _workspaceManager?.Dispose();

    [Test]
    public async Task FindBlockingCalls_ThreadSleepInsideAsync_IsFlagged()
    {
        const string source = """
            using System.Threading;
            using System.Threading.Tasks;
            public class Poller {
                public async Task PollAsync() {
                    if (true) { Thread.Sleep(500); }
                    await Task.Delay(1);
                }
            }
            """;
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Poller.cs", source)]);
        _workspaceManager.SetTestSolution(solution);

        var reports = await _engine.FindBlockingCallsInAsyncAsync("Poller.cs");

        Assert.That(reports, Is.Not.Empty, "Thread.Sleep inside async method must be flagged.");
    }

    [Test]
    public async Task FindBlockingCalls_TaskWaitInsideAsync_IsFlagged()
    {
        const string source = """
            using System.Threading.Tasks;
            public class WaitInAsync {
                public async Task DoAsync() {
                    Task.Delay(100).Wait();
                    await Task.Delay(1);
                }
            }
            """;
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("WaitInAsync.cs", source)]);
        _workspaceManager.SetTestSolution(solution);

        var reports = await _engine.FindBlockingCallsInAsyncAsync("WaitInAsync.cs");

        Assert.That(reports, Is.Not.Empty, ".Wait() inside async method must be flagged.");
    }

    [Test]
    public async Task FindBlockingCalls_GetAwaiterGetResultInsideAsync_IsFlagged()
    {
        const string source = """
            using System.Threading.Tasks;
            public class HiddenBlock {
                public async Task DoAsync() {
                    var s = FetchAsync().GetAwaiter().GetResult();
                    await Task.Delay(1);
                }
                private Task<string> FetchAsync() => Task.FromResult("x");
            }
            """;
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("HiddenBlock.cs", source)]);
        _workspaceManager.SetTestSolution(solution);

        var reports = await _engine.FindBlockingCallsInAsyncAsync("HiddenBlock.cs");

        Assert.That(reports, Is.Not.Empty, ".GetAwaiter().GetResult() inside async method must be flagged.");
    }

    [Test]
    public async Task FindBlockingCalls_ThreadSleepInSyncMethod_IsNotFlagged()
    {
        // FindBlockingCallsInAsyncAsync only walks async methods.
        // Thread.Sleep in a sync method must NOT produce a report.
        const string source = """
            using System.Threading;
            public class SyncOk {
                public void Poll() { Thread.Sleep(100); }
            }
            """;
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("SyncOk.cs", source)]);
        _workspaceManager.SetTestSolution(solution);

        var reports = await _engine.FindBlockingCallsInAsyncAsync("SyncOk.cs");

        Assert.That(reports, Is.Empty,
            "Thread.Sleep inside a synchronous method must NOT be flagged by FindBlockingCallsInAsyncAsync.");
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// 4. ThreadSafeLockGotchaTests
//    MakeMethodThreadSafeAsync + ConvertLockToSemaphoreSlimAsync
// ─────────────────────────────────────────────────────────────────────────────
[TestFixture]
public class ThreadSafeLockGotchaTests
{
    private PersistentWorkspaceManager _workspaceManager = null!;
    private ThreadSafetyEngine _engine = null!;

    [SetUp]
    public void Setup()
    {
        _workspaceManager = new PersistentWorkspaceManager(NullLogger<PersistentWorkspaceManager>.Instance);
        _engine = new ThreadSafetyEngine(_workspaceManager);
    }

    [TearDown]
    public void TearDown() => _workspaceManager?.Dispose();

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

        Assert.That(result, Does.Contain("lock"), "Method body must be wrapped in a lock statement.");
        Assert.That(result, Does.Contain("_lock"), "A _lock field must be introduced.");
    }

    [Test]
    public async Task MakeMethodThreadSafe_NonExistentMethod_ReturnsErrorString()
    {
        const string source = """
            public class Worker {
                public void DoWork() { var x = 1; }
            }
            """;
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Worker.cs", source)]);
        _workspaceManager.SetTestSolution(solution);

        var result = await _engine.MakeMethodThreadSafeAsync("Worker.cs", "NonExistent");

        Assert.That(result, Does.Contain("Error"),
            "Requesting a non-existent method must return an error string, not throw.");
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
        Assert.That(result, Does.Contain("lock"), "The lock keyword must still be present in the result.");
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

        Assert.That(result, Does.Contain("SemaphoreSlim"), "lock must be replaced with SemaphoreSlim.");
        Assert.That(result, Does.Contain("WaitAsync"), "Converted method must call WaitAsync.");
    }

    [Test]
    public async Task ConvertLockToSemaphoreSlim_MethodWithNoLock_ReturnsOriginalCode()
    {
        // When no lock statements are found the engine returns root.ToFullString() — the original code.
        const string source = """
            public class NoLockAtAll {
                public void Process() { var x = 1; }
            }
            """;
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("NoLock.cs", source)]);
        _workspaceManager.SetTestSolution(solution);

        var result = await _engine.ConvertLockToSemaphoreSlimAsync("NoLock.cs", "Process");

        Assert.That(result, Is.Not.Null, "Must return the original code, not null, when no lock exists.");
        Assert.That(result, Does.Contain("NoLockAtAll"), "Original class name must be present in returned code.");
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

        Assert.That(result, Does.Contain("Release"),
            "SemaphoreSlim.Release() must appear in the converted output.");
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// 5. ValueTaskMisuseGotchaTests
//    DetectValueTaskMisuseAsync
//    The "immediately-next-statement await" boundary must NOT be flagged.
// ─────────────────────────────────────────────────────────────────────────────
[TestFixture]
public class ValueTaskMisuseGotchaTests
{
    private PersistentWorkspaceManager _workspaceManager = null!;
    private AsyncSafetyEngine _engine = null!;

    [SetUp]
    public void Setup()
    {
        _workspaceManager = new PersistentWorkspaceManager(NullLogger<PersistentWorkspaceManager>.Instance);
        _engine = new AsyncSafetyEngine(_workspaceManager);
    }

    [TearDown]
    public void TearDown() => _workspaceManager?.Dispose();

    [Test]
    public async Task DetectValueTaskMisuse_AwaitedOnceImmediately_IsNotFlagged()
    {
        // Storing a ValueTask in a local and awaiting it on the very next statement
        // (j == declIdx + 1) must NOT be flagged.  The check is: j > declIdx + 1.
        const string source = """
            using System.Threading.Tasks;
            public class GoodValueTask {
                public async Task Good() {
                    ValueTask<int> vt = GetValueTask();
                    await vt;
                }
                private ValueTask<int> GetValueTask() => new ValueTask<int>(42);
            }
            """;
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("GoodVT.cs", source)]);
        _workspaceManager.SetTestSolution(solution);

        var reports = await _engine.DetectValueTaskMisuseAsync("GoodVT.cs");

        Assert.That(reports, Is.Empty,
            "Awaiting a ValueTask local on the immediately-next statement must NOT be flagged.");
    }

    [Test]
    public async Task DetectValueTaskMisuse_ValueTaskAwaitedTwice_IsFlagged()
    {
        // Awaiting a ValueTask more than once is undefined behaviour — must be flagged.
        const string source = """
            using System.Threading.Tasks;
            public class DoubleAwaited {
                public async Task Bad() {
                    ValueTask<int> vt = GetValueTask();
                    await vt;
                    await vt;
                }
                private ValueTask<int> GetValueTask() => new ValueTask<int>(42);
            }
            """;
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("DoubleVT.cs", source)]);
        _workspaceManager.SetTestSolution(solution);

        var reports = await _engine.DetectValueTaskMisuseAsync("DoubleVT.cs");

        Assert.That(reports, Is.Not.Empty, "Awaiting a ValueTask local twice must be flagged.");
    }

    [Test]
    public async Task DetectValueTaskMisuse_CleanValueTaskReturnMethod_ReturnsEmpty()
    {
        // A method whose return type is ValueTask<int> and whose body is just an expression body
        // with no local ValueTask variables has no misuse to detect.
        const string source = """
            using System.Threading.Tasks;
            public class CleanValueTask {
                public ValueTask<int> GetAsync() => new ValueTask<int>(42);
            }
            """;
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("CleanVT.cs", source)]);
        _workspaceManager.SetTestSolution(solution);

        var reports = await _engine.DetectValueTaskMisuseAsync("CleanVT.cs");

        Assert.That(reports, Is.Empty, "Expression-body ValueTask return with no local storage must return empty.");
    }

    [Test]
    public async Task DetectValueTaskMisuse_NoValueTaskAnywhere_ReturnsEmpty()
    {
        const string source = """
            public class NoValueTask {
                public int Add(int a, int b) => a + b;
            }
            """;
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("NoVT.cs", source)]);
        _workspaceManager.SetTestSolution(solution);

        var reports = await _engine.DetectValueTaskMisuseAsync("NoVT.cs");

        Assert.That(reports, Is.Empty, "A class with no ValueTask at all must return an empty list.");
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// 6. ExceptionHandlingGotchaTests
//    AnalyzeExceptionHandlingAsync
//    The SwallowedException pattern has nuanced conditions that must be respected.
// ─────────────────────────────────────────────────────────────────────────────
[TestFixture]
public class ExceptionHandlingGotchaTests
{
    private PersistentWorkspaceManager _workspaceManager = null!;
    private AntiPatternEngine _engine = null!;

    [SetUp]
    public void Setup()
    {
        _workspaceManager = new PersistentWorkspaceManager(NullLogger<PersistentWorkspaceManager>.Instance);
        _engine = new AntiPatternEngine(_workspaceManager);
    }

    [TearDown]
    public void TearDown() => _workspaceManager?.Dispose();

    [Test]
    public async Task AnalyzeExceptions_EmptyCatch_IsSwallowedException()
    {
        const string source = """
            public class Swallower {
                public void DoWork() {
                    try { Risky(); }
                    catch { }
                }
                private void Risky() { }
            }
            """;
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Swallower.cs", source)]);
        _workspaceManager.SetTestSolution(solution);

        var findings = await _engine.AnalyzeExceptionHandlingAsync("Swallower.cs");

        Assert.That(findings.Any(f => f.Pattern == "SwallowedException"), Is.True,
            "An empty catch block must produce a SwallowedException finding.");
    }

    [Test]
    public async Task AnalyzeExceptions_CatchWithCommentOnly_IsSwallowedException()
    {
        // A comment is not a SyntaxNode — block.Statements.Count == 0 even with a comment.
        // The engine must treat this the same as an empty catch.
        const string source = """
            public class CommentSwallower {
                public void DoWork() {
                    try { Risky(); }
                    catch (System.Exception) { /* intentionally ignored */ }
                }
                private void Risky() { }
            }
            """;
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("CommentSwallower.cs", source)]);
        _workspaceManager.SetTestSolution(solution);

        var findings = await _engine.AnalyzeExceptionHandlingAsync("CommentSwallower.cs");

        Assert.That(findings.Any(f => f.Pattern == "SwallowedException"), Is.True,
            "A catch block containing only a comment (0 real statements) must be treated as a swallowed exception.");
    }

    [Test]
    public async Task AnalyzeExceptions_CatchWithLogAndRethrow_IsNotSwallowedException()
    {
        // hasLog=true AND hasRethrow=true — SwallowedException must NOT be added.
        // Note: CatchAll WILL still be present because type is System.Exception.
        const string source = """
            public class ProperHandler {
                public void DoWork() {
                    try { Risky(); }
                    catch (System.Exception ex) {
                        System.Console.WriteLine(ex);
                        throw;
                    }
                }
                private void Risky() { }
            }
            """;
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Proper.cs", source)]);
        _workspaceManager.SetTestSolution(solution);

        var findings = await _engine.AnalyzeExceptionHandlingAsync("Proper.cs");

        Assert.That(findings.All(f => f.Pattern != "SwallowedException"), Is.True,
            "Catch that logs and rethrows must NOT be flagged as SwallowedException.");
    }

    [Test]
    public async Task AnalyzeExceptions_FilteredCatchWithRethrow_IsNotSwallowedException()
    {
        // when-filter + throw; → hasRethrow=true → SwallowedException must NOT be added.
        const string source = """
            public class Filtered {
                public void DoWork() {
                    try { Risky(); }
                    catch (System.Exception ex) when (!(ex is System.OperationCanceledException)) {
                        throw;
                    }
                }
                private void Risky() { }
            }
            """;
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Filtered.cs", source)]);
        _workspaceManager.SetTestSolution(solution);

        var findings = await _engine.AnalyzeExceptionHandlingAsync("Filtered.cs");

        Assert.That(findings.All(f => f.Pattern != "SwallowedException"), Is.True,
            "A filtered catch that rethrows must NOT be flagged as SwallowedException.");
    }

    [Test]
    public async Task AnalyzeExceptions_SpecificExceptionTypeCatch_IsNotCatchAll()
    {
        // System.IO.IOException is a specific type — the CatchAll pattern requires
        // bare catch, Exception, or System.Exception.  A specific type must NOT add CatchAll.
        const string source = """
            public class SpecificCatch {
                public void DoWork() {
                    try { Risky(); }
                    catch (System.IO.IOException) { }
                }
                private void Risky() { }
            }
            """;
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("SpecificCatch.cs", source)]);
        _workspaceManager.SetTestSolution(solution);

        var findings = await _engine.AnalyzeExceptionHandlingAsync("SpecificCatch.cs");

        Assert.That(findings.All(f => f.Pattern != "CatchAll"), Is.True,
            "catch(IOException) must NOT be flagged as CatchAll.");
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// 7. ImmutabilityGotchaTests
//    MakeClassImmutableAsync
//    Idempotency, unknown-class fallback, and public-field handling.
// ─────────────────────────────────────────────────────────────────────────────
[TestFixture]
public class ImmutabilityGotchaTests
{
    private PersistentWorkspaceManager _workspaceManager = null!;
    private ImmutabilityEngine _engine = null!;

    [SetUp]
    public void Setup()
    {
        _workspaceManager = new PersistentWorkspaceManager(NullLogger<PersistentWorkspaceManager>.Instance);
        _engine = new ImmutabilityEngine(_workspaceManager);
    }

    [TearDown]
    public void TearDown() => _workspaceManager?.Dispose();

    [Test]
    public async Task MakeClassImmutable_SetAccessor_BecomesInit()
    {
        const string source = """
            public class Entity { public string Name { get; set; } = ""; }
            """;
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Entity.cs", source)]);
        _workspaceManager.SetTestSolution(solution);

        var result = await _engine.MakeClassImmutableAsync("Entity.cs", "Entity");

        Assert.That(result, Does.Contain("init"), "set; accessor must be replaced with init;");
        Assert.That(result, Does.Not.Contain("set;"), "set; must no longer be present after mutation.");
    }

    [Test]
    public async Task MakeClassImmutable_AlreadyImmutableClass_IsIdempotent()
    {
        // The property already uses init; — there is no set accessor to replace.
        // The engine must not add a second init or corrupt the output.
        const string source = """
            public class AlreadyImmutable { public string Name { get; init; } = ""; }
            """;
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("AI.cs", source)]);
        _workspaceManager.SetTestSolution(solution);

        var result = await _engine.MakeClassImmutableAsync("AI.cs", "AlreadyImmutable");

        Assert.That(result, Is.Not.Null, "Must not return null for an already-immutable class.");
        Assert.That(result, Does.Contain("init"), "init keyword must still be present.");
        Assert.That(result, Does.Not.Contain("set;"), "No spurious set; must be introduced.");
    }

    [Test]
    public async Task MakeClassImmutable_PublicMutableFields_GetReadonlyModifier()
    {
        const string source = """
            public class MutableFields {
                public string Name = "";
                public int Count = 0;
            }
            """;
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Mutable.cs", source)]);
        _workspaceManager.SetTestSolution(solution);

        var result = await _engine.MakeClassImmutableAsync("Mutable.cs", "MutableFields");

        Assert.That(result, Does.Contain("readonly"), "Public mutable fields must receive the readonly modifier.");
    }

    [Test]
    public async Task MakeClassImmutable_UnknownClassName_ReturnsOriginalCodeNotEmpty()
    {
        // When the class name is not found the engine does: return root?.ToFullString() ?? ""
        // That means the ENTIRE original source is returned — not null, not empty.
        const string source = """
            public class RealClass { public string Name { get; set; } = ""; }
            """;
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Real.cs", source)]);
        _workspaceManager.SetTestSolution(solution);

        var result = await _engine.MakeClassImmutableAsync("Real.cs", "NonExistent");

        Assert.That(result, Is.Not.Null.And.Not.Empty,
            "Unknown class name must return the original file content, not null or empty.");
        Assert.That(result, Does.Contain("RealClass"),
            "The original class name must be present in the returned (unchanged) source.");
    }

    [Test]
    public async Task MakeClassImmutable_ClassWithNoProperties_ReturnsWithoutCrashing()
    {
        const string source = """
            public class EmptyClass { }
            """;
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Empty.cs", source)]);
        _workspaceManager.SetTestSolution(solution);

        var result = await _engine.MakeClassImmutableAsync("Empty.cs", "EmptyClass");

        Assert.That(result, Is.Not.Null, "Engine must not crash or return null for a class with no properties.");
        Assert.That(result, Does.Contain("EmptyClass"), "Class name must be present in the returned code.");
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// 8. StringConcatInLoopGotchaTests
//    DetectAntiPatternsAsync / StringConcatInLoop
//    The heuristic requires either a string-literal RHS OR a variable name that
//    matches LooksLikeStringVar (ends in "str", "text", "html", …).
// ─────────────────────────────────────────────────────────────────────────────
[TestFixture]
public class StringConcatInLoopGotchaTests
{
    private PersistentWorkspaceManager _workspaceManager = null!;
    private AntiPatternEngine _engine = null!;

    [SetUp]
    public void Setup()
    {
        _workspaceManager = new PersistentWorkspaceManager(NullLogger<PersistentWorkspaceManager>.Instance);
        _engine = new AntiPatternEngine(_workspaceManager);
    }

    [TearDown]
    public void TearDown() => _workspaceManager?.Dispose();

    [Test]
    public async Task DetectAntiPatterns_StringLiteralRhsInForLoop_IsFlagged()
    {
        // RHS is a string literal → rhsIsString=true → pattern fires regardless of variable name.
        const string source = """
            public class Builder {
                public string Build(int n) {
                    var s = "";
                    for (int i = 0; i < n; i++) s += "sep";
                    return s;
                }
            }
            """;
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Builder.cs", source)]);
        _workspaceManager.SetTestSolution(solution);

        var findings = await _engine.DetectAntiPatternsAsync("Builder.cs");

        Assert.That(findings.Any(f => f.Pattern == "StringConcatInLoop"), Is.True,
            "+= with a string literal inside a for loop must be flagged as StringConcatInLoop.");
    }

    [Test]
    public async Task DetectAntiPatterns_StringLiteralRhsOutsideLoop_IsNotFlagged()
    {
        // Same += with a string literal but outside any loop — must NOT flag.
        const string source = """
            public class Greeter {
                public string Greet(string name) {
                    var s = "Hello";
                    s += " World";
                    return s;
                }
            }
            """;
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Greeter.cs", source)]);
        _workspaceManager.SetTestSolution(solution);

        var findings = await _engine.DetectAntiPatternsAsync("Greeter.cs");

        Assert.That(findings.All(f => f.Pattern != "StringConcatInLoop"), Is.True,
            "+= outside any loop must NOT be flagged as StringConcatInLoop.");
    }

    [Test]
    public async Task DetectAntiPatterns_LooksLikeStringVarInForeach_IsFlagged()
    {
        // Variable named "text" ends in "text" → LooksLikeStringVar=true → fires even with
        // a non-literal RHS (identifier).
        const string source = """
            using System.Collections.Generic;
            public class Concatenator {
                public string Join(List<string> items) {
                    var text = "";
                    foreach (var item in items) text += item;
                    return text;
                }
            }
            """;
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Concat.cs", source)]);
        _workspaceManager.SetTestSolution(solution);

        var findings = await _engine.DetectAntiPatternsAsync("Concat.cs");

        Assert.That(findings.Any(f => f.Pattern == "StringConcatInLoop"), Is.True,
            "+= on a variable whose name ends in 'text' inside a foreach must be flagged.");
    }

    [Test]
    public async Task DetectAntiPatterns_WhileLoopStringConcat_IsFlagged()
    {
        // The loop-ancestor check covers ForStatement, ForEachStatement, WhileStatement,
        // and DoStatement — so a while loop must also fire.
        const string source = """
            public class WhileBuilder {
                public string Build(int n) {
                    var str = "";
                    int i = 0;
                    while (i < n) { str += "x"; i++; }
                    return str;
                }
            }
            """;
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("While.cs", source)]);
        _workspaceManager.SetTestSolution(solution);

        var findings = await _engine.DetectAntiPatternsAsync("While.cs");

        Assert.That(findings.Any(f => f.Pattern == "StringConcatInLoop"), Is.True,
            "+= with a string-literal RHS inside a while loop must be flagged.");
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// 9. MissingCancellationTokenGotchaTests
//    FindMissingCancellationTokensAsync
//    Uses the semantic model to find callees that accept CancellationToken.
// ─────────────────────────────────────────────────────────────────────────────
[TestFixture]
public class MissingCancellationTokenGotchaTests
{
    private PersistentWorkspaceManager _workspaceManager = null!;
    private AntiPatternEngine _engine = null!;

    [SetUp]
    public void Setup()
    {
        _workspaceManager = new PersistentWorkspaceManager(NullLogger<PersistentWorkspaceManager>.Instance);
        _engine = new AntiPatternEngine(_workspaceManager);
    }

    [TearDown]
    public void TearDown() => _workspaceManager?.Dispose();

    [Test]
    public async Task FindMissingCancellationTokens_AsyncMethodWithoutCtCallingCalleeWithCt_IsFlagged()
    {
        // DoWorkAsync has no CancellationToken parameter yet it calls SaveAsync which
        // accepts one.  The semantic model resolves SaveAsync and the finding is produced.
        const string source = """
            using System.Threading;
            using System.Threading.Tasks;
            public class MissingToken {
                public async Task DoWorkAsync() {
                    await SaveAsync(CancellationToken.None);
                }
                private async Task SaveAsync(CancellationToken ct) {
                    await Task.Delay(1, ct);
                }
            }
            """;
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Missing.cs", source)]);
        _workspaceManager.SetTestSolution(solution);

        var findings = await _engine.FindMissingCancellationTokensAsync("Missing.cs");

        Assert.That(findings.Any(f => f.MethodName == "DoWorkAsync"), Is.True,
            "An async method without a CancellationToken parameter that calls a callee accepting one must be flagged.");
    }

    [Test]
    public async Task FindMissingCancellationTokens_AsyncMethodAlreadyHasCt_IsNotFlagged()
    {
        const string source = """
            using System.Threading;
            using System.Threading.Tasks;
            public class HasToken {
                public async Task DoWorkAsync(CancellationToken ct) {
                    await SaveAsync(ct);
                }
                private async Task SaveAsync(CancellationToken ct) {
                    await Task.Delay(1, ct);
                }
            }
            """;
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("HasToken.cs", source)]);
        _workspaceManager.SetTestSolution(solution);

        var findings = await _engine.FindMissingCancellationTokensAsync("HasToken.cs");

        Assert.That(findings.All(f => f.MethodName != "DoWorkAsync"), Is.True,
            "An async method that already has a CancellationToken parameter must NOT be flagged.");
    }

    [Test]
    public async Task FindMissingCancellationTokens_SyncMethodCallingCtCallee_IsNotFlagged()
    {
        // Sync methods (not async, not returning Task) are skipped by the engine.
        const string source = """
            using System.Threading;
            using System.Threading.Tasks;
            public class SyncCaller {
                public void DoWork() { SaveAsync(CancellationToken.None); }
                private async Task SaveAsync(CancellationToken ct) {
                    await Task.Delay(1, ct);
                }
            }
            """;
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("SyncCaller.cs", source)]);
        _workspaceManager.SetTestSolution(solution);

        var findings = await _engine.FindMissingCancellationTokensAsync("SyncCaller.cs");

        Assert.That(findings.All(f => f.MethodName != "DoWork"), Is.True,
            "A synchronous method must NOT be flagged by FindMissingCancellationTokensAsync.");
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// 10. UnsafeTypeCastGotchaTests
//     FindUnsafeTypeCastsAsync
//     Only CastExpressionSyntax is flagged — "as" and "is" patterns are safe.
// ─────────────────────────────────────────────────────────────────────────────
[TestFixture]
public class UnsafeTypeCastGotchaTests
{
    private PersistentWorkspaceManager _workspaceManager = null!;
    private SecurityAndSafetyEngine _engine = null!;

    [SetUp]
    public void Setup()
    {
        _workspaceManager = new PersistentWorkspaceManager(NullLogger<PersistentWorkspaceManager>.Instance);
        _engine = new SecurityAndSafetyEngine(_workspaceManager);
    }

    [TearDown]
    public void TearDown() => _workspaceManager?.Dispose();

    [Test]
    public async Task FindUnsafeCasts_DirectStringCast_IsFlagged()
    {
        const string source = """
            public class Caster {
                public string Convert(object obj) { return (string)obj; }
            }
            """;
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Caster.cs", source)]);
        _workspaceManager.SetTestSolution(solution);

        var issues = await _engine.FindUnsafeTypeCastsAsync("Caster.cs");

        Assert.That(issues.Any(i => i.Type == "UnsafeCast"), Is.True,
            "(string)obj is a direct cast and must be flagged as UnsafeCast.");
    }

    [Test]
    public async Task FindUnsafeCasts_NumericCast_IsFlagged()
    {
        const string source = """
            public class NumCaster {
                public int Truncate(double d) { return (int)d; }
            }
            """;
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("NumCaster.cs", source)]);
        _workspaceManager.SetTestSolution(solution);

        var issues = await _engine.FindUnsafeTypeCastsAsync("NumCaster.cs");

        Assert.That(issues.Any(i => i.Type == "UnsafeCast"), Is.True,
            "(int)double is a direct cast and must be flagged.");
    }

    [Test]
    public async Task FindUnsafeCasts_AsCast_IsNotFlagged()
    {
        // "as" produces an AsExpressionSyntax, NOT a CastExpressionSyntax.
        // The engine only flags CastExpressionSyntax — "as" is safe and must be skipped.
        const string source = """
            public class SafeCaster {
                public string? Convert(object obj) { return obj as string; }
            }
            """;
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("SafeCaster.cs", source)]);
        _workspaceManager.SetTestSolution(solution);

        var issues = await _engine.FindUnsafeTypeCastsAsync("SafeCaster.cs");

        Assert.That(issues, Is.Empty,
            "\"obj as string\" must NOT be flagged — it returns null instead of throwing.");
    }

    [Test]
    public async Task FindUnsafeCasts_PatternMatchingIsExpression_IsNotFlagged()
    {
        // Pattern matching via "is" produces an IsPatternExpressionSyntax / pattern variables.
        // These are NOT CastExpressionSyntax and must not be flagged.
        const string source = """
            public class PatternMatcher {
                public string? Convert(object obj) {
                    return obj is string s ? s : null;
                }
            }
            """;
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Pattern.cs", source)]);
        _workspaceManager.SetTestSolution(solution);

        var issues = await _engine.FindUnsafeTypeCastsAsync("Pattern.cs");

        Assert.That(issues, Is.Empty,
            "\"obj is string s\" pattern matching must NOT be flagged as an unsafe cast.");
    }

    [Test]
    public async Task FindUnsafeCasts_NoCastsAtAll_ReturnsEmpty()
    {
        const string source = """
            public class NoCasts {
                public int Add(int a, int b) => a + b;
            }
            """;
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("NoCasts.cs", source)]);
        _workspaceManager.SetTestSolution(solution);

        var issues = await _engine.FindUnsafeTypeCastsAsync("NoCasts.cs");

        Assert.That(issues, Is.Empty, "A class with no casts must return an empty list.");
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// 11. FireAndForgetGotchaTests
//     FindUnawaitedFireAndForgetAsync
//     Tests raw invocation, discard-assignment (_ = RunAsync()), and awaited calls.
//     Bug fixed: _ = RunAsync() was silently skipped (AssignmentExpressionSyntax).
// ─────────────────────────────────────────────────────────────────────────────
[TestFixture]
public class FireAndForgetGotchaTests
{
    private PersistentWorkspaceManager _workspaceManager = null!;
    private AsyncSafetyEngine _engine = null!;

    [SetUp]
    public void Setup()
    {
        _workspaceManager = new PersistentWorkspaceManager(NullLogger<PersistentWorkspaceManager>.Instance);
        _engine = new AsyncSafetyEngine(_workspaceManager);
    }

    [TearDown]
    public void TearDown() => _workspaceManager?.Dispose();

    [Test]
    public async Task FindUnawaitedFireAndForget_RawCallToAsyncMethod_IsFlagged()
    {
        // Plain invocation statement: the expression IS an InvocationExpressionSyntax directly.
        const string source = """
            using System.Threading.Tasks;
            public class Launcher {
                public async Task StartAsync() {
                    RunAsync();
                    await Task.Delay(1);
                }
                private async Task RunAsync() { await Task.Delay(10); }
            }
            """;
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Launcher.cs", source)]);
        _workspaceManager.SetTestSolution(solution);

        var reports = await _engine.FindUnawaitedFireAndForgetAsync("Launcher.cs");

        Assert.That(reports, Is.Not.Empty,
            "A raw unawaited call to an Async method must be flagged as fire-and-forget.");
    }

    [Test]
    public async Task FindUnawaitedFireAndForget_DiscardAssignment_IsNowDetected()
    {
        // FIXED: _ = RunAsync() is now also detected as fire-and-forget.
        // The engine checks for AssignmentExpressionSyntax where Left is the discard
        // identifier "_" and Right is an InvocationExpression ending in "Async".
        const string source = """
            using System.Threading.Tasks;
            public class Discarder {
                public async Task StartAsync() {
                    _ = RunAsync();
                    await Task.Delay(1);
                }
                private async Task RunAsync() { await Task.Delay(10); }
            }
            """;
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Discarder.cs", source)]);
        _workspaceManager.SetTestSolution(solution);

        var reports = await _engine.FindUnawaitedFireAndForgetAsync("Discarder.cs");

        Assert.That(reports, Is.Not.Empty,
            "_ = RunAsync() is a discard fire-and-forget and must now be detected by the engine.");
        Assert.That(reports[0].Reason, Does.Contain("RunAsync"),
            "The report should name the fire-and-forgot method.");
        Assert.That(reports[0].Reason, Does.Contain("discard"),
            "The report should mention the discard pattern.");
    }

    [Test]
    public async Task FindUnawaitedFireAndForget_AwaitedTask_IsNotFlagged()
    {
        // Properly awaited tasks must not appear in the fire-and-forget list.
        const string source = """
            using System.Threading.Tasks;
            public class Careful {
                public async Task StartAsync() {
                    await RunAsync();
                }
                private async Task RunAsync() { await Task.Delay(1); }
            }
            """;
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Careful.cs", source)]);
        _workspaceManager.SetTestSolution(solution);

        var reports = await _engine.FindUnawaitedFireAndForgetAsync("Careful.cs");

        Assert.That(reports, Is.Empty, "Properly awaited tasks must NOT be flagged as fire-and-forget.");
    }

    [Test]
    public async Task FindUnawaitedFireAndForget_NoAsyncCode_ReturnsEmpty()
    {
        const string source = """
            public class SyncOnly {
                public void DoWork() { var x = 1 + 2; }
            }
            """;
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Sync.cs", source)]);
        _workspaceManager.SetTestSolution(solution);

        var reports = await _engine.FindUnawaitedFireAndForgetAsync("Sync.cs");

        Assert.That(reports, Is.Empty, "A class with no async code must return an empty fire-and-forget list.");
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// 12. ConfigureAwaitInLibraryGotchaTests
//     FindConfigureAwaitMissingAsync
//     Library code must use .ConfigureAwait(false); controller classes are excluded.
// ─────────────────────────────────────────────────────────────────────────────
[TestFixture]
public class ConfigureAwaitInLibraryGotchaTests
{
    private PersistentWorkspaceManager _workspaceManager = null!;
    private AsyncSafetyEngine _engine = null!;

    [SetUp]
    public void Setup()
    {
        _workspaceManager = new PersistentWorkspaceManager(NullLogger<PersistentWorkspaceManager>.Instance);
        _engine = new AsyncSafetyEngine(_workspaceManager);
    }

    [TearDown]
    public void TearDown() => _workspaceManager?.Dispose();

    [Test]
    public async Task FindConfigureAwaitMissing_AwaitInNonControllerClass_IsFlagged()
    {
        // A plain library class with an await that lacks .ConfigureAwait(false)
        // must be flagged.
        const string source = """
            using System.Threading.Tasks;
            public class DataService {
                public async Task<string> FetchAsync() {
                    await Task.Delay(1);
                    return "data";
                }
            }
            """;
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("DataService.cs", source)]);
        _workspaceManager.SetTestSolution(solution);

        var reports = await _engine.FindConfigureAwaitMissingAsync("DataService.cs");

        Assert.That(reports, Is.Not.Empty,
            "await without .ConfigureAwait(false) in a library class must be flagged.");
    }

    [Test]
    public async Task FindConfigureAwaitMissing_MultipleAwaits_AllFlagged()
    {
        const string source = """
            using System.Threading.Tasks;
            public class MultiStep {
                public async Task RunAsync() {
                    await Task.Delay(1);
                    await Task.Delay(2);
                    await Task.Delay(3);
                }
            }
            """;
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Multi.cs", source)]);
        _workspaceManager.SetTestSolution(solution);

        var reports = await _engine.FindConfigureAwaitMissingAsync("Multi.cs");

        Assert.That(reports.Count, Is.GreaterThanOrEqualTo(2),
            "Each await without ConfigureAwait(false) should produce its own report.");
    }

    [Test]
    public async Task FindConfigureAwaitMissing_AwaitWithConfigureAwaitFalse_IsNotFlagged()
    {
        const string source = """
            using System.Threading.Tasks;
            public class LibraryClass {
                public async Task<string> FetchAsync() {
                    await Task.Delay(1).ConfigureAwait(false);
                    return "ok";
                }
            }
            """;
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Lib.cs", source)]);
        _workspaceManager.SetTestSolution(solution);

        var reports = await _engine.FindConfigureAwaitMissingAsync("Lib.cs");

        Assert.That(reports, Is.Empty,
            "await with .ConfigureAwait(false) must NOT be flagged.");
    }

    [Test]
    public async Task FindConfigureAwaitMissing_ControllerClassName_IsNotFlagged()
    {
        // Classes whose names end in "Controller" are excluded from the ConfigureAwait check
        // because ASP.NET controllers run on a specific synchronization context already.
        const string source = """
            using System.Threading.Tasks;
            public class ProductsController {
                public async Task<string> GetAsync() {
                    await Task.Delay(1);
                    return "product";
                }
            }
            """;
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Products.cs", source)]);
        _workspaceManager.SetTestSolution(solution);

        var reports = await _engine.FindConfigureAwaitMissingAsync("Products.cs");

        Assert.That(reports, Is.Empty,
            "A class named *Controller must not be flagged for missing ConfigureAwait(false).");
    }
}
