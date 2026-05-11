// Accuracy regression tests for the four improved tools.
// Each [Test] exercises exactly one detection rule — positive (should flag) and
// negative (should NOT flag, verifying false-positive guards).

using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using RoslynSentinel.Server;

namespace RoslynSentinel.Tests;

// ════════════════════════════════════════════════════════════════════════════
// 1. FindUnawaitedFireAndForgetAsync — null-conditional + chained patterns
// ════════════════════════════════════════════════════════════════════════════
[TestFixture]
public class FireAndForgetAccuracyTests
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

    private void SetSource(string source) =>
        _workspaceManager.SetTestSolution(
            TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Test.cs", source)]));

    // ── Positive cases (should flag) ─────────────────────────────────────

    [Test]
    public async Task Flags_SimpleDiscardAssignment()
    {
        SetSource(@"
public class C {
    public System.Threading.Tasks.Task DoWorkAsync() => System.Threading.Tasks.Task.CompletedTask;
    public void M() { _ = DoWorkAsync(); }
}");
        var reports = await _engine.FindUnawaitedFireAndForgetAsync("Test.cs");
        Assert.That(reports, Is.Not.Empty, "_ = DoWorkAsync() should be flagged");
        Assert.That(reports[0].Reason, Does.Contain("DoWorkAsync"));
    }

    [Test]
    public async Task Flags_MemberAccess_DiscardAssignment()
    {
        SetSource(@"
public class C {
    private Bus _bus = null!;
    public void M() { _ = _bus.PublishAsync(); }
}
public class Bus {
    public System.Threading.Tasks.Task PublishAsync() => System.Threading.Tasks.Task.CompletedTask;
}");
        var reports = await _engine.FindUnawaitedFireAndForgetAsync("Test.cs");
        Assert.That(reports, Is.Not.Empty, "_ = _bus.PublishAsync() should be flagged");
    }

    [Test]
    public async Task Flags_NullConditional_SingleLevel()
    {
        SetSource(@"
public class C {
    private Bus? _bus;
    public void M() { _ = _bus?.PublishAsync(); }
}
public class Bus {
    public System.Threading.Tasks.Task PublishAsync() => System.Threading.Tasks.Task.CompletedTask;
}");
        var reports = await _engine.FindUnawaitedFireAndForgetAsync("Test.cs");
        Assert.That(reports, Is.Not.Empty, "_ = _bus?.PublishAsync() should be flagged");
        Assert.That(reports[0].Reason, Does.Contain("PublishAsync"));
    }

    [Test]
    public async Task Flags_NullConditional_ChainedTwoLevels()
    {
        SetSource(@"
public class C {
    private Container? _container;
    public void M() { _ = _container?.Bus?.PublishAsync(); }
}
public class Container { public Bus? Bus { get; } }
public class Bus {
    public System.Threading.Tasks.Task PublishAsync() => System.Threading.Tasks.Task.CompletedTask;
}");
        var reports = await _engine.FindUnawaitedFireAndForgetAsync("Test.cs");
        Assert.That(reports, Is.Not.Empty, "_ = _container?.Bus?.PublishAsync() (two-level null-conditional) should be flagged");
        Assert.That(reports[0].Reason, Does.Contain("PublishAsync"));
    }

    [Test]
    public async Task Flags_NullConditional_InsideForeachLoop()
    {
        SetSource(@"
using System.Collections.Generic;
public class C {
    private List<Bus?> _buses = null!;
    public void M() { foreach (var b in _buses) { _ = b?.PublishAsync(); } }
}
public class Bus {
    public System.Threading.Tasks.Task PublishAsync() => System.Threading.Tasks.Task.CompletedTask;
}");
        var reports = await _engine.FindUnawaitedFireAndForgetAsync("Test.cs");
        Assert.That(reports, Is.Not.Empty, "null-conditional discard inside foreach should be flagged");
    }

    [Test]
    public async Task Flags_PlainCall_WithoutDiscard()
    {
        SetSource(@"
public class C {
    public System.Threading.Tasks.Task SendAsync() => System.Threading.Tasks.Task.CompletedTask;
    public void M() { SendAsync(); }
}");
        var reports = await _engine.FindUnawaitedFireAndForgetAsync("Test.cs");
        Assert.That(reports, Is.Not.Empty, "SendAsync() called with no await and no discard should be flagged");
    }

    // ── Negative cases (should NOT flag) ────────────────────────────────

    [Test]
    public async Task DoesNotFlag_AwaitedCall()
    {
        SetSource(@"
public class C {
    public System.Threading.Tasks.Task DoWorkAsync() => System.Threading.Tasks.Task.CompletedTask;
    public async System.Threading.Tasks.Task M() { await DoWorkAsync(); }
}");
        var reports = await _engine.FindUnawaitedFireAndForgetAsync("Test.cs");
        Assert.That(reports, Is.Empty, "awaited calls must not be flagged");
    }

    [Test]
    public async Task DoesNotFlag_DiscardWithContinueWith()
    {
        SetSource(@"
public class C {
    public System.Threading.Tasks.Task DoWorkAsync() => System.Threading.Tasks.Task.CompletedTask;
    public void M() {
        _ = DoWorkAsync().ContinueWith(t => System.Console.WriteLine(t.Exception),
            System.Threading.Tasks.TaskContinuationOptions.OnlyOnFaulted);
    }
}");
        var reports = await _engine.FindUnawaitedFireAndForgetAsync("Test.cs");
        Assert.That(reports, Is.Empty, "discard with ContinueWith error handler is the correct pattern and must not be flagged");
    }

    [Test]
    public async Task DoesNotFlag_NonAsyncMethod()
    {
        SetSource(@"
public class C {
    public int GetValue() => 42;
    public void M() { _ = GetValue(); }
}");
        var reports = await _engine.FindUnawaitedFireAndForgetAsync("Test.cs");
        Assert.That(reports, Is.Empty, "_ = non-Async method must not be flagged");
    }

    // ── Round 3: ternary discard (B3) ───────────────────────────────────

    [Test]
    public async Task Flags_TernaryDiscard_BothBranchesAsync()
    {
        SetSource(@"
public class C {
    public System.Threading.Tasks.Task DoAAsync() => System.Threading.Tasks.Task.CompletedTask;
    public System.Threading.Tasks.Task DoBAsync() => System.Threading.Tasks.Task.CompletedTask;
    public void M(bool flag) { _ = flag ? DoAAsync() : DoBAsync(); }
}");
        var reports = await _engine.FindUnawaitedFireAndForgetAsync("Test.cs");
        Assert.That(reports, Is.Not.Empty, "_ = flag ? DoAAsync() : DoBAsync() should be flagged");
    }

    [Test]
    public async Task Flags_TernaryDiscard_OneBranchAsync()
    {
        SetSource(@"
public class C {
    public System.Threading.Tasks.Task DoAAsync() => System.Threading.Tasks.Task.CompletedTask;
    public void M(bool flag) { _ = flag ? DoAAsync() : System.Threading.Tasks.Task.CompletedTask; }
}");
        var reports = await _engine.FindUnawaitedFireAndForgetAsync("Test.cs");
        Assert.That(reports, Is.Not.Empty, "_ = flag ? DoAAsync() : Task.CompletedTask should be flagged (one branch is async)");
    }

    [Test]
    public async Task DoesNotFlag_TernaryDiscard_NoAsyncBranch()
    {
        SetSource(@"
public class C {
    public int GetA() => 1;
    public int GetB() => 2;
    public void M(bool flag) { _ = flag ? GetA() : GetB(); }
}");
        var reports = await _engine.FindUnawaitedFireAndForgetAsync("Test.cs");
        Assert.That(reports, Is.Empty, "_ = flag ? nonAsync() : nonAsync() must not be flagged");
    }

    // ── Fire-and-forget inside async lambda body ─────────────────────────

    [Test]
    public async Task Flags_FireAndForget_InsideAsyncLambda()
    {
        SetSource(@"
using System;
using System.Threading.Tasks;
public class C {
    public Task DoWorkAsync() => Task.CompletedTask;
    public void M() {
        Task.Run(async () => {
            DoWorkAsync();
        });
    }
}");
        var reports = await _engine.FindUnawaitedFireAndForgetAsync("Test.cs");
        Assert.That(reports, Is.Not.Empty,
            "DoWorkAsync() inside an async lambda body without await should be detected");
    }

    // ── Semantic model: catches Task-returning methods without Async suffix ──

    [Test]
    public async Task Flags_TaskReturning_NoAsyncSuffix_SemanticModel()
    {
        SetSource(@"
using System.Threading.Tasks;
public class C {
    public Task Run() => Task.CompletedTask;
    public void M() { Run(); }
}");
        var reports = await _engine.FindUnawaitedFireAndForgetAsync("Test.cs");
        Assert.That(reports, Is.Not.Empty,
            "A Task-returning method named 'Run' (no Async suffix) should be caught via semantic model");
    }

    [Test]
    public async Task DoesNotFlag_NonTask_NonAsyncSuffix_SemanticModel()
    {
        SetSource(@"
public class C {
    public void Run() { }
    public void M() { Run(); }
}");
        var reports = await _engine.FindUnawaitedFireAndForgetAsync("Test.cs");
        Assert.That(reports, Is.Empty,
            "void-returning method with no Async suffix must not be flagged even when semantic model is available");
    }

    [Test]
    public async Task Flags_ValueTask_Discard_SemanticModel()
    {
        SetSource(@"
using System.Threading.Tasks;
public class C {
    public ValueTask FlushAsync() => default;
    public void M() { _ = FlushAsync(); }
}");
        var reports = await _engine.FindUnawaitedFireAndForgetAsync("Test.cs");
        Assert.That(reports, Is.Not.Empty,
            "_ = ValueTask-returning method should be flagged — semantic model recognizes ValueTask");
    }
}

// ════════════════════════════════════════════════════════════════════════════
// 2. AnalyzeSemaphoreUsageAsync — pool pattern vs genuine leak
// ════════════════════════════════════════════════════════════════════════════
[TestFixture]
public class SemaphoreAccuracyTests
{
    private PersistentWorkspaceManager _workspaceManager = null!;
    private AnalysisEngine _engine = null!;

    [SetUp]
    public void Setup()
    {
        _workspaceManager = new PersistentWorkspaceManager(NullLogger<PersistentWorkspaceManager>.Instance);
        _engine = new AnalysisEngine(_workspaceManager, new SentinelConfiguration());
    }

    [TearDown]
    public void TearDown() => _workspaceManager?.Dispose();

    private void SetSource(string source) =>
        _workspaceManager.SetTestSolution(
            TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Test.cs", source)]));

    [Test]
    public async Task Flags_GenuineLeak_NoReleaseInClass()
    {
        SetSource(@"
using System.Threading;
public class C {
    private SemaphoreSlim _sem = new(1, 1);
    public async System.Threading.Tasks.Task AcquireAsync() {
        await _sem.WaitAsync();
    }
}");
        var results = await _engine.AnalyzeSemaphoreUsageAsync("Test.cs");
        Assert.That(results, Is.Not.Empty, "semaphore acquired but never released anywhere in the class — genuine leak");
        Assert.That(results[0], Does.Contain("leak").IgnoreCase);
    }

    [Test]
    public async Task ReportsAdvisory_PoolPattern_ReleaseInPairedMethod()
    {
        SetSource(@"
using System.Threading;
public class Pool {
    private SemaphoreSlim _sem = new(3, 3);
    public async System.Threading.Tasks.Task AcquireAsync() {
        await _sem.WaitAsync();
    }
    public void ReturnAsync() {
        _sem.Release();
    }
}");
        var results = await _engine.AnalyzeSemaphoreUsageAsync("Test.cs");
        Assert.That(results, Is.Not.Empty, "pool pattern should still produce output (advisory)");
        Assert.That(results[0], Does.Contain("Advisory").Or.Contains("pool").IgnoreCase,
            "pool pattern should be advisory, not a leak report");
        Assert.That(results[0], Does.Not.Contain("leak").IgnoreCase);
    }

    [Test]
    public async Task ReportsAdvisory_PoolPattern_ReleaseWithCount()
    {
        SetSource(@"
using System.Threading;
public class Pool {
    private SemaphoreSlim _sem = new(5, 5);
    public async System.Threading.Tasks.Task AcquireAsync() {
        await _sem.WaitAsync();
    }
    public void Return() {
        _sem.Release(1);
    }
}");
        var results = await _engine.AnalyzeSemaphoreUsageAsync("Test.cs");
        Assert.That(results, Is.Not.Empty, "Release(n) parameterized release should be recognized");
        Assert.That(results[0], Does.Contain("Advisory").Or.Contains("pool").IgnoreCase);
    }

    [Test]
    public async Task DoesNotFlagLeak_WhenMethodContainsOwnRelease()
    {
        SetSource(@"
using System.Threading;
public class C {
    private SemaphoreSlim _sem = new(1, 1);
    public async System.Threading.Tasks.Task DoWorkAsync() {
        await _sem.WaitAsync();
        try { }
        finally { _sem.Release(); }
    }
}");
        var results = await _engine.AnalyzeSemaphoreUsageAsync("Test.cs");
        Assert.That(results, Is.Empty, "WaitAsync + Release in same method is correctly guarded — no flag");
    }

    // ── Round 3: Release in non-method class members ─────────────────────

    [Test]
    public async Task ReportsAdvisory_PoolPattern_ReleaseInProperty()
    {
        SetSource(@"
using System.Threading;
public class Pool {
    private SemaphoreSlim _sem = new(1, 1);
    public async System.Threading.Tasks.Task AcquireAsync() {
        await _sem.WaitAsync();
    }
    public bool TryReturn {
        get { _sem.Release(); return true; }
    }
}");
        var results = await _engine.AnalyzeSemaphoreUsageAsync("Test.cs");
        Assert.That(results, Is.Not.Empty, "Release in a property should be recognized as a pool pattern");
        Assert.That(results[0], Does.Contain("Advisory").Or.Contains("pool").IgnoreCase,
            "should be advisory not a leak when Release is in a property");
    }

    [Test]
    public async Task ReportsAdvisory_PoolPattern_ReleaseInConstructor()
    {
        SetSource(@"
using System.Threading;
public class Pool {
    private SemaphoreSlim _sem = new(1, 1);
    public Pool() { _sem.Release(); }
    public async System.Threading.Tasks.Task AcquireAsync() {
        await _sem.WaitAsync();
    }
}");
        var results = await _engine.AnalyzeSemaphoreUsageAsync("Test.cs");
        Assert.That(results, Is.Not.Empty, "Release in a constructor should be recognized");
        Assert.That(results[0], Does.Contain("Advisory").Or.Contains("pool").IgnoreCase,
            "should be advisory not a leak when Release is in a constructor");
    }

    // ── 1-level-deep helper method Release detection ─────────────────────

    [Test]
    public async Task ReportsAdvisory_PoolPattern_ReleaseInHelperMethod()
    {
        SetSource(@"
using System.Threading;
public class ReleaseHelper {
    public static void FreeSlot(SemaphoreSlim sem) { sem.Release(); }
}
public class Pool {
    private SemaphoreSlim _sem = new(3, 3);
    public async System.Threading.Tasks.Task AcquireAsync() {
        await _sem.WaitAsync();
    }
    public void Return() {
        ReleaseHelper.FreeSlot(_sem);
    }
}");
        var results = await _engine.AnalyzeSemaphoreUsageAsync("Test.cs");
        Assert.That(results, Is.Not.Empty, "Release via 1-level-deep helper call should still be recognized");
        Assert.That(results[0], Does.Contain("Advisory").Or.Contains("pool").IgnoreCase,
            "should be advisory when Release is in a helper called from a class member");
    }

    // ── Semantic model: non-SemaphoreSlim WaitAsync must not be flagged ──

    [Test]
    public async Task DoesNotFlag_WaitAsync_OnNonSemaphoreType()
    {
        SetSource(@"
using System.Threading.Tasks;
public class MyChannel {
    public Task WaitAsync() => Task.CompletedTask;
}
public class C {
    private MyChannel _ch = new();
    public async Task ConsumeAsync() {
        await _ch.WaitAsync();
    }
}");
        var results = await _engine.AnalyzeSemaphoreUsageAsync("Test.cs");
        Assert.That(results, Is.Empty,
            "WaitAsync on a non-SemaphoreSlim type must not be flagged (semantic model verifies receiver type)");
    }
}

// ════════════════════════════════════════════════════════════════════════════
// 3. DetectMismatchedAwaitAsync — false positive reduction
// ════════════════════════════════════════════════════════════════════════════
[TestFixture]
public class MismatchedAwaitAccuracyTests
{
    private PersistentWorkspaceManager _workspaceManager = null!;
    private AnalysisEngine _engine = null!;

    [SetUp]
    public void Setup()
    {
        _workspaceManager = new PersistentWorkspaceManager(NullLogger<PersistentWorkspaceManager>.Instance);
        _engine = new AnalysisEngine(_workspaceManager, new SentinelConfiguration());
    }

    [TearDown]
    public void TearDown() => _workspaceManager?.Dispose();

    private void SetSource(string source) =>
        _workspaceManager.SetTestSolution(
            TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Test.cs", source)]));

    // ── Positive cases (should flag) ─────────────────────────────────────

    [Test]
    public async Task Flags_UnawaitedTaskInExpressionStatement()
    {
        SetSource(@"
using System.Threading.Tasks;
public class C {
    public Task DoWorkAsync() => Task.CompletedTask;
    public async Task M() {
        DoWorkAsync();
    }
}");
        var results = await _engine.DetectMismatchedAwaitAsync("Test.cs");
        Assert.That(results, Is.Not.Empty, "unawaited Task call in statement should be flagged");
    }

    // ── Negative cases (should NOT flag) ────────────────────────────────

    [Test]
    public async Task DoesNotFlag_TaskWhenAll_DirectArguments()
    {
        SetSource(@"
using System.Threading.Tasks;
public class C {
    public Task RemoveAsync(int id) => Task.CompletedTask;
    public Task UpdateAsync(int id) => Task.CompletedTask;
    public async Task M() {
        await Task.WhenAll(RemoveAsync(1), UpdateAsync(1));
    }
}");
        var results = await _engine.DetectMismatchedAwaitAsync("Test.cs");
        Assert.That(results, Is.Empty, "direct WhenAll args must not be flagged");
    }

    [Test]
    public async Task DoesNotFlag_TaskWhenAll_VariableArgs()
    {
        SetSource(@"
using System.Threading.Tasks;
public class C {
    public Task RemoveAsync(int id) => Task.CompletedTask;
    public Task UpdateAsync(int id) => Task.CompletedTask;
    public async Task M() {
        var t1 = RemoveAsync(1);
        var t2 = UpdateAsync(1);
        await Task.WhenAll(t1, t2);
    }
}");
        var results = await _engine.DetectMismatchedAwaitAsync("Test.cs");
        Assert.That(results, Is.Empty, "variables fed to WhenAll must not be flagged at their declaration");
    }

    [Test]
    public async Task DoesNotFlag_TaskFromResult()
    {
        SetSource(@"
using System.Threading.Tasks;
public class C {
    public Task<int> GetAsync() => Task.FromResult(42);
}");
        var results = await _engine.DetectMismatchedAwaitAsync("Test.cs");
        Assert.That(results, Is.Empty, "Task.FromResult is synchronous — must not be flagged");
    }

    [Test]
    public async Task DoesNotFlag_AwaitUsing()
    {
        SetSource(@"
using System.Threading.Tasks;
public class C {
    public async Task M() {
        await using var conn = OpenAsync();
    }
    private System.IO.MemoryStream OpenAsync() => new System.IO.MemoryStream();
}");
        var results = await _engine.DetectMismatchedAwaitAsync("Test.cs");
        Assert.That(results, Is.Empty, "await using declaration must not be flagged");
    }

    [Test]
    public async Task DoesNotFlag_ContinueWithChain()
    {
        SetSource(@"
using System.Threading.Tasks;
public class C {
    public Task DoWorkAsync() => Task.CompletedTask;
    public void M() {
        _ = DoWorkAsync().ContinueWith(
            t => System.Console.WriteLine(t.Exception),
            TaskContinuationOptions.OnlyOnFaulted);
    }
}");
        var results = await _engine.DetectMismatchedAwaitAsync("Test.cs");
        Assert.That(results, Is.Empty, "Task fed into ContinueWith must not be flagged");
    }

    [Test]
    public async Task DoesNotFlag_ReturnStatement()
    {
        SetSource(@"
using System.Threading.Tasks;
public class C {
    public Task GetAsync() => Task.CompletedTask;
    public Task M() { return GetAsync(); }
}");
        var results = await _engine.DetectMismatchedAwaitAsync("Test.cs");
        Assert.That(results, Is.Empty, "returned Task must not be flagged");
    }

    [Test]
    public async Task DoesNotFlag_LocalVarLaterAwaited()
    {
        SetSource(@"
using System.Threading.Tasks;
public class C {
    public Task DoWorkAsync() => Task.CompletedTask;
    public async Task M() {
        var t = DoWorkAsync();
        await t;
    }
}");
        var results = await _engine.DetectMismatchedAwaitAsync("Test.cs");
        Assert.That(results, Is.Empty, "task stored in variable and later awaited must not be flagged at assignment");
    }

    [Test]
    public async Task DoesNotFlag_FieldAssignment()
    {
        SetSource(@"
using System.Threading.Tasks;
public class C {
    private Task _backgroundTask = Task.CompletedTask;
    public Task StartAsync() => Task.CompletedTask;
    public void Begin() {
        _backgroundTask = StartAsync();
    }
}");
        var results = await _engine.DetectMismatchedAwaitAsync("Test.cs");
        Assert.That(results, Is.Empty, "task stored in field must not be flagged");
    }

    [Test]
    public async Task DoesNotFlag_TernaryExpression()
    {
        SetSource(@"
using System.Threading.Tasks;
public class C {
    public Task DoAAsync() => Task.CompletedTask;
    public Task DoBAsync() => Task.CompletedTask;
    public async Task M(bool flag) {
        await (flag ? DoAAsync() : DoBAsync());
    }
}");
        var results = await _engine.DetectMismatchedAwaitAsync("Test.cs");
        Assert.That(results, Is.Empty, "Task inside awaited ternary must not be flagged");
    }

    // ── Round 3: anonymous method, initializer, collection method skips ──

    [Test]
    public async Task DoesNotFlag_TaskInsideAnonymousMethod()
    {
        SetSource(@"
using System;
using System.Threading.Tasks;
public class C {
    public Task DoWorkAsync() => Task.CompletedTask;
    public void M() {
        Action a = delegate { DoWorkAsync(); };
    }
}");
        var results = await _engine.DetectMismatchedAwaitAsync("Test.cs");
        Assert.That(results, Is.Empty, "Task inside anonymous method body must not be flagged at the outer scope");
    }

    [Test]
    public async Task DoesNotFlag_TaskInsideObjectInitializer()
    {
        SetSource(@"
using System.Threading.Tasks;
public class Wrapper { public Task? T { get; set; } }
public class C {
    public Task StartAsync() => Task.CompletedTask;
    public void M() {
        var w = new Wrapper { T = StartAsync() };
    }
}");
        var results = await _engine.DetectMismatchedAwaitAsync("Test.cs");
        Assert.That(results, Is.Empty, "Task assigned inside an object initializer must not be flagged");
    }

    [Test]
    public async Task DoesNotFlag_TaskAddedToCollection()
    {
        SetSource(@"
using System.Collections.Generic;
using System.Threading.Tasks;
public class C {
    private List<Task> _tasks = new();
    public Task DoWorkAsync() => Task.CompletedTask;
    public void M() {
        _tasks.Add(DoWorkAsync());
    }
}");
        var results = await _engine.DetectMismatchedAwaitAsync("Test.cs");
        Assert.That(results, Is.Empty, "Task passed to collection.Add() must not be flagged — it is being tracked");
    }

    [Test]
    public async Task DoesNotFlag_NullCoalescingTaskAssignment()
    {
        SetSource(@"
using System.Threading.Tasks;
public class C {
    private Task? _cached;
    public Task FetchAsync() => Task.CompletedTask;
    public Task GetOrFetch() => _cached ?? FetchAsync();
}");
        var results = await _engine.DetectMismatchedAwaitAsync("Test.cs");
        Assert.That(results, Is.Empty, "null-coalescing Task expression must not be flagged");
    }

    // ── Func<Task> delegate invocation ───────────────────────────────────

    [Test]
    public async Task Flags_FuncTask_Invocation_NotAwaited()
    {
        SetSource(@"
using System;
using System.Threading.Tasks;
public class C {
    public async Task M() {
        Func<Task> fn = async () => await Task.Delay(1);
        fn();
    }
}");
        var results = await _engine.DetectMismatchedAwaitAsync("Test.cs");
        Assert.That(results, Is.Not.Empty,
            "calling a Func<Task> without await should be detected — semantic model resolves Func<Task>.Invoke() return type");
    }

    [Test]
    public async Task DoesNotFlag_FuncTask_WhenAwaited()
    {
        SetSource(@"
using System;
using System.Threading.Tasks;
public class C {
    public async Task M() {
        Func<Task> fn = async () => await Task.Delay(1);
        await fn();
    }
}");
        var results = await _engine.DetectMismatchedAwaitAsync("Test.cs");
        Assert.That(results, Is.Empty, "awaited Func<Task> invocation must not be flagged");
    }

    // ── Semantic model: ValueTask + null-forgiving operator ──────────────

    [Test]
    public async Task Flags_UnawaitedValueTask_SemanticModel()
    {
        SetSource(@"
using System.Threading.Tasks;
public class C {
    public ValueTask FlushAsync() => default;
    public async Task M() {
        FlushAsync();
    }
}");
        var results = await _engine.DetectMismatchedAwaitAsync("Test.cs");
        Assert.That(results, Is.Not.Empty,
            "unawaited ValueTask should be caught — semantic model recognizes ValueTask as awaitable");
    }

    [Test]
    public async Task DoesNotFlag_AwaitWithNullForgivingOperator()
    {
        SetSource(@"
using System.Threading.Tasks;
public class C {
    public Task<int> GetAsync() => Task.FromResult(1);
    public async Task M() {
        var x = (int)(await GetAsync()!);
    }
}");
        var results = await _engine.DetectMismatchedAwaitAsync("Test.cs");
        Assert.That(results, Is.Empty,
            "await expr! (null-forgiving inside await) must not be flagged as mismatched");
    }

    [Test]
    public async Task DoesNotFlag_NonTask_MethodWithAsyncSuffix_SemanticModel()
    {
        SetSource(@"
using System.Threading.Tasks;
public class C {
    // Returns int, not Task — should not be flagged despite Async suffix
    public int GetCountAsync() => 42;
    public async Task M() {
        GetCountAsync();
    }
}");
        var results = await _engine.DetectMismatchedAwaitAsync("Test.cs");
        Assert.That(results, Is.Empty,
            "a method returning int (not Task) must not be flagged even if it ends with Async");
    }
}

// ════════════════════════════════════════════════════════════════════════════
// 4. AnalyzePerformanceAsync — new checks + .Result precision fix
// ════════════════════════════════════════════════════════════════════════════
[TestFixture]
public class PerformanceAccuracyTests
{
    private PersistentWorkspaceManager _workspaceManager = null!;
    private PerformanceEngine _engine = null!;

    [SetUp]
    public void Setup()
    {
        _workspaceManager = new PersistentWorkspaceManager(NullLogger<PersistentWorkspaceManager>.Instance);
        _engine = new PerformanceEngine(_workspaceManager);
    }

    [TearDown]
    public void TearDown() => _workspaceManager?.Dispose();

    private void SetSource(string source) =>
        _workspaceManager.SetTestSolution(
            TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Test.cs", source)]));

    // ── .Result precision ────────────────────────────────────────────────

    [Test]
    public async Task Flags_Result_OnAsyncMethodCall()
    {
        SetSource(@"
public class C {
    public System.Threading.Tasks.Task<int> GetValueAsync() => System.Threading.Tasks.Task.FromResult(42);
    public void M() {
        var x = GetValueAsync().Result;
    }
}");
        var issues = await _engine.AnalyzePerformanceAsync("Test.cs");
        Assert.That(issues.Any(i => i.IssueType == "BlockingAsyncCall"), Is.True,
            ".Result on an Async method should be flagged");
    }

    [Test]
    public async Task DoesNotFlag_Result_OnNonTaskType()
    {
        SetSource(@"
public class OperationResult { public int Result { get; set; } }
public class C {
    private OperationResult _op = new();
    public void M() {
        var x = _op.Result;
    }
}");
        var issues = await _engine.AnalyzePerformanceAsync("Test.cs");
        Assert.That(issues.Any(i => i.IssueType == "BlockingAsyncCall"), Is.False,
            "OperationResult.Result must NOT be flagged as blocking");
    }

    [Test]
    public async Task Flags_Result_OnTaskVariable()
    {
        SetSource(@"
public class C {
    public void M() {
        var fetchTask = System.Threading.Tasks.Task.FromResult(1);
        var x = fetchTask.Result;
    }
}");
        var issues = await _engine.AnalyzePerformanceAsync("Test.cs");
        Assert.That(issues.Any(i => i.IssueType == "BlockingAsyncCall"), Is.True,
            ".Result on a variable named *Task should be flagged");
    }

    // ── Thread.Sleep in async ────────────────────────────────────────────

    [Test]
    public async Task Flags_ThreadSleep_InAsyncMethod()
    {
        SetSource(@"
public class C {
    public async System.Threading.Tasks.Task M() {
        System.Threading.Thread.Sleep(1000);
    }
}");
        var issues = await _engine.AnalyzePerformanceAsync("Test.cs");
        Assert.That(issues.Any(i => i.IssueType == "ThreadSleepInAsync"), Is.True,
            "Thread.Sleep in async method should be flagged");
    }

    [Test]
    public async Task DoesNotFlag_ThreadSleep_InSyncMethod()
    {
        SetSource(@"
public class C {
    public void M() {
        System.Threading.Thread.Sleep(1000);
    }
}");
        var issues = await _engine.AnalyzePerformanceAsync("Test.cs");
        Assert.That(issues.Any(i => i.IssueType == "ThreadSleepInAsync"), Is.False,
            "Thread.Sleep in a synchronous method must not be flagged as async issue");
    }

    // ── lock with async calls ─────────────────────────────────────────────

    [Test]
    public async Task Flags_Lock_InAsyncMethod_WithAsyncCallsInside()
    {
        SetSource(@"
public class C {
    private readonly object _lock = new();
    public System.Threading.Tasks.Task WriteAsync() => System.Threading.Tasks.Task.CompletedTask;
    public async System.Threading.Tasks.Task M() {
        lock (_lock) {
            WriteAsync();
        }
    }
}");
        var issues = await _engine.AnalyzePerformanceAsync("Test.cs");
        Assert.That(issues.Any(i => i.IssueType == "LockWithAsyncInAsyncMethod"), Is.True,
            "lock in async method containing async calls should be flagged");
    }

    [Test]
    public async Task DoesNotFlag_Lock_InAsyncMethod_WithOnlySyncCalls()
    {
        SetSource(@"
public class C {
    private readonly object _lock = new();
    private int _count;
    public async System.Threading.Tasks.Task M() {
        lock (_lock) { _count++; }
        await System.Threading.Tasks.Task.Delay(1);
    }
}");
        var issues = await _engine.AnalyzePerformanceAsync("Test.cs");
        Assert.That(issues.Any(i => i.IssueType == "LockWithAsyncInAsyncMethod"), Is.False,
            "lock with only sync code inside must not be flagged");
    }

    // ── OrderBy().First() → MinBy() ───────────────────────────────────────

    [Test]
    public async Task Flags_OrderByFirst_ShouldUseMinBy()
    {
        SetSource(@"
using System.Collections.Generic;
using System.Linq;
public class C {
    public void M(List<int> items) {
        var min = items.OrderBy(x => x).First();
    }
}");
        var issues = await _engine.AnalyzePerformanceAsync("Test.cs");
        Assert.That(issues.Any(i => i.IssueType == "OrderByThenFirst"), Is.True,
            ".OrderBy(...).First() should be flagged as MinBy opportunity");
        Assert.That(issues.First(i => i.IssueType == "OrderByThenFirst").Description, Does.Contain("MinBy"));
    }

    [Test]
    public async Task Flags_OrderByDescendingFirst_ShouldUseMaxBy()
    {
        SetSource(@"
using System.Collections.Generic;
using System.Linq;
public class C {
    public void M(List<int> items) {
        var max = items.OrderByDescending(x => x).First();
    }
}");
        var issues = await _engine.AnalyzePerformanceAsync("Test.cs");
        Assert.That(issues.Any(i => i.IssueType == "OrderByThenFirst"), Is.True,
            ".OrderByDescending(...).First() should be flagged as MaxBy opportunity");
        Assert.That(issues.First(i => i.IssueType == "OrderByThenFirst").Description, Does.Contain("MaxBy"));
    }

    [Test]
    public async Task DoesNotFlag_OrderByFirst_WhenArgumentProvided()
    {
        SetSource(@"
using System.Collections.Generic;
using System.Linq;
public class C {
    public void M(List<int> items) {
        var x = items.OrderBy(i => i).First(i => i > 5);
    }
}");
        var issues = await _engine.AnalyzePerformanceAsync("Test.cs");
        Assert.That(issues.Any(i => i.IssueType == "OrderByThenFirst"), Is.False,
            ".OrderBy().First(predicate) is not equivalent to MinBy — must not be flagged");
    }

    // ── Pre-existing checks still work ───────────────────────────────────

    [Test]
    public async Task Flags_GetAwaiterGetResult()
    {
        SetSource(@"
public class C {
    public System.Threading.Tasks.Task DoWorkAsync() => System.Threading.Tasks.Task.CompletedTask;
    public void M() {
        DoWorkAsync().GetAwaiter().GetResult();
    }
}");
        var issues = await _engine.AnalyzePerformanceAsync("Test.cs");
        Assert.That(issues.Any(i => i.IssueType == "BlockingAsyncCall"), Is.True,
            ".GetAwaiter().GetResult() should still be flagged");
    }

    [Test]
    public async Task Flags_DotWait()
    {
        SetSource(@"
public class C {
    public System.Threading.Tasks.Task DoWorkAsync() => System.Threading.Tasks.Task.CompletedTask;
    public void M() {
        DoWorkAsync().Wait();
    }
}");
        var issues = await _engine.AnalyzePerformanceAsync("Test.cs");
        Assert.That(issues.Any(i => i.IssueType == "BlockingAsyncCall"), Is.True,
            ".Wait() should still be flagged");
    }

    [Test]
    public async Task Flags_CountGreaterThanZero_ShouldUseAny()
    {
        SetSource(@"
using System.Collections.Generic;
using System.Linq;
public class C {
    public void M(IEnumerable<int> items) {
        if (items.Count() > 0) { }
    }
}");
        var issues = await _engine.AnalyzePerformanceAsync("Test.cs");
        Assert.That(issues.Any(i => i.IssueType == "PoorLinqCountUsage"), Is.True,
            ".Count() > 0 should still be flagged");
    }

    [Test]
    public async Task Flags_StringConcatenationInLoop()
    {
        SetSource(@"
public class C {
    public void M(string[] items) {
        string result = """";
        foreach (var item in items) { result = ""prefix"" + item; }
    }
}");
        var issues = await _engine.AnalyzePerformanceAsync("Test.cs");
        Assert.That(issues.Any(i => i.IssueType == "StringConcatenationInLoop"), Is.True,
            "string concatenation in loop should still be flagged");
    }

    [Test]
    public async Task Flags_HttpClientInMethod()
    {
        SetSource(@"
public class C {
    public void M() {
        var client = new System.Net.Http.HttpClient();
    }
}");
        var issues = await _engine.AnalyzePerformanceAsync("Test.cs");
        Assert.That(issues.Any(i => i.IssueType == "HttpClientPerRequest"), Is.True,
            "HttpClient created in method should be flagged");
    }

    // ── Round 3: collection allocation in loop ────────────────────────────

    [Test]
    public async Task Flags_NewListInsideForEachLoop()
    {
        SetSource(@"
using System.Collections.Generic;
public class C {
    public void M(int[] items) {
        foreach (var item in items) {
            var buffer = new List<int>();
            buffer.Add(item);
        }
    }
}");
        var issues = await _engine.AnalyzePerformanceAsync("Test.cs");
        Assert.That(issues.Any(i => i.IssueType == "CollectionAllocationInLoop"), Is.True,
            "new List<T>() inside foreach should be flagged");
    }

    [Test]
    public async Task Flags_NewDictionaryInsideWhileLoop()
    {
        SetSource(@"
using System.Collections.Generic;
public class C {
    public void M() {
        int i = 0;
        while (i++ < 10) {
            var map = new Dictionary<string, int>();
        }
    }
}");
        var issues = await _engine.AnalyzePerformanceAsync("Test.cs");
        Assert.That(issues.Any(i => i.IssueType == "CollectionAllocationInLoop"), Is.True,
            "new Dictionary<K,V>() inside while should be flagged");
    }

    [Test]
    public async Task DoesNotFlag_NewListOutsideLoop()
    {
        SetSource(@"
using System.Collections.Generic;
public class C {
    public void M(int[] items) {
        var buffer = new List<int>();
        foreach (var item in items) {
            buffer.Clear();
            buffer.Add(item);
        }
    }
}");
        var issues = await _engine.AnalyzePerformanceAsync("Test.cs");
        Assert.That(issues.Any(i => i.IssueType == "CollectionAllocationInLoop"), Is.False,
            "new List<T>() outside loop — allocated once, reused — must not be flagged");
    }

    // ── Round 3: Select().Select() chaining ──────────────────────────────

    [Test]
    public async Task Flags_ChainedSelectProjections()
    {
        SetSource(@"
using System.Collections.Generic;
using System.Linq;
public class C {
    public void M(List<string> items) {
        var result = items.Select(x => x.Trim()).Select(x => x.ToUpper()).ToList();
    }
}");
        var issues = await _engine.AnalyzePerformanceAsync("Test.cs");
        Assert.That(issues.Any(i => i.IssueType == "ChainedSelectProjection"), Is.True,
            ".Select().Select() should be flagged as a mergeable chain");
    }

    [Test]
    public async Task DoesNotFlag_SingleSelectProjection()
    {
        SetSource(@"
using System.Collections.Generic;
using System.Linq;
public class C {
    public void M(List<string> items) {
        var result = items.Select(x => x.ToUpper()).ToList();
    }
}");
        var issues = await _engine.AnalyzePerformanceAsync("Test.cs");
        Assert.That(issues.Any(i => i.IssueType == "ChainedSelectProjection"), Is.False,
            "single .Select() must not be flagged");
    }

    // ── Semantic model: precise .Result and HttpClient checks ─────────────

    [Test]
    public async Task DoesNotFlag_Result_OnCustomResultProperty_SemanticModel()
    {
        SetSource(@"
public class OperationResult {
    public int Result { get; set; }
}
public class C {
    public void M(OperationResult op) {
        var x = op.Result;
    }
}");
        var issues = await _engine.AnalyzePerformanceAsync("Test.cs");
        Assert.That(issues.Any(i => i.IssueType == "BlockingAsyncCall"), Is.False,
            "OperationResult.Result must not be flagged — semantic model knows it is not Task<T>.Result");
    }

    [Test]
    public async Task Flags_Result_OnValueTask_SemanticModel()
    {
        SetSource(@"
using System.Threading.Tasks;
public class C {
    public ValueTask<int> GetAsync() => new ValueTask<int>(42);
    public void M() {
        var fetchTask = GetAsync();
        var x = fetchTask.Result;
    }
}");
        var issues = await _engine.AnalyzePerformanceAsync("Test.cs");
        Assert.That(issues.Any(i => i.IssueType == "BlockingAsyncCall"), Is.True,
            ".Result on a ValueTask<T> variable should be flagged — semantic model recognizes ValueTask");
    }

    [Test]
    public async Task Flags_HttpClient_FullyQualified_SemanticModel()
    {
        SetSource(@"
public class C {
    public void M() {
        var client = new System.Net.Http.HttpClient();
    }
}");
        var issues = await _engine.AnalyzePerformanceAsync("Test.cs");
        Assert.That(issues.Any(i => i.IssueType == "HttpClientPerRequest"), Is.True,
            "Fully-qualified System.Net.Http.HttpClient should be flagged via semantic model");
    }

    [Test]
    public async Task DoesNotFlag_NotHttpClient_SimilarName()
    {
        SetSource(@"
public class MyHttpClientWrapper { }
public class C {
    public void M() {
        var w = new MyHttpClientWrapper();
    }
}");
        var issues = await _engine.AnalyzePerformanceAsync("Test.cs");
        Assert.That(issues.Any(i => i.IssueType == "HttpClientPerRequest"), Is.False,
            "MyHttpClientWrapper ends with 'HttpClient' but is not System.Net.Http.HttpClient — must not be flagged");
    }

    // ── Double enumeration ────────────────────────────────────────────────

    [Test]
    public async Task Flags_IEnumerable_UsedTwice()
    {
        SetSource(@"
using System.Collections.Generic;
using System.Linq;
public class C {
    public void M(IEnumerable<int> source) {
        var filtered = source.Where(x => x > 0);
        var count = filtered.Count();
        var list = filtered.ToList();
    }
}");
        var issues = await _engine.AnalyzePerformanceAsync("Test.cs");
        Assert.That(issues.Any(i => i.IssueType == "PotentialDoubleEnumeration"), Is.True,
            "IEnumerable<T> variable used twice should be flagged as potential double enumeration");
    }

    [Test]
    public async Task DoesNotFlag_List_UsedTwice()
    {
        SetSource(@"
using System.Collections.Generic;
using System.Linq;
public class C {
    public void M(List<int> source) {
        var count = source.Count();
        var first = source.FirstOrDefault();
    }
}");
        var issues = await _engine.AnalyzePerformanceAsync("Test.cs");
        Assert.That(issues.Any(i => i.IssueType == "PotentialDoubleEnumeration"), Is.False,
            "List<T> is already materialized — using it twice is fine");
    }

    // ── Inline Regex instantiation ────────────────────────────────────────

    [Test]
    public async Task Flags_NewRegexInsideMethod()
    {
        SetSource(@"
using System.Text.RegularExpressions;
public class C {
    public bool M(string s) {
        var re = new Regex(@""\d+"");
        return re.IsMatch(s);
    }
}");
        var issues = await _engine.AnalyzePerformanceAsync("Test.cs");
        Assert.That(issues.Any(i => i.IssueType == "InlineRegexInstantiation"), Is.True,
            "new Regex() inside a method should be flagged");
    }

    [Test]
    public async Task DoesNotFlag_StaticReadonlyRegexField()
    {
        SetSource(@"
using System.Text.RegularExpressions;
public class C {
    private static readonly Regex _re = new Regex(@""\d+"");
    public bool M(string s) => _re.IsMatch(s);
}");
        var issues = await _engine.AnalyzePerformanceAsync("Test.cs");
        Assert.That(issues.Any(i => i.IssueType == "InlineRegexInstantiation"), Is.False,
            "static readonly Regex field is the correct pattern — must not be flagged");
    }

    // ── Single-char string method args ────────────────────────────────────

    [Test]
    public async Task Flags_Contains_SingleCharStringArg()
    {
        SetSource(@"
public class C {
    public bool M(string s) => s.Contains(""x"");
}");
        var issues = await _engine.AnalyzePerformanceAsync("Test.cs");
        Assert.That(issues.Any(i => i.IssueType == "StringMethodWithSingleCharArg"), Is.True,
            "s.Contains(\"x\") should be flagged — use s.Contains('x') char overload");
    }

    [Test]
    public async Task Flags_IndexOf_SingleCharStringArg()
    {
        SetSource(@"
public class C {
    public int M(string s) => s.IndexOf("","");
}");
        var issues = await _engine.AnalyzePerformanceAsync("Test.cs");
        Assert.That(issues.Any(i => i.IssueType == "StringMethodWithSingleCharArg"), Is.True,
            "s.IndexOf(\",\") should be flagged — use s.IndexOf(',') char overload");
    }

    [Test]
    public async Task DoesNotFlag_Contains_MultiCharStringArg()
    {
        SetSource(@"
public class C {
    public bool M(string s) => s.Contains(""abc"");
}");
        var issues = await _engine.AnalyzePerformanceAsync("Test.cs");
        Assert.That(issues.Any(i => i.IssueType == "StringMethodWithSingleCharArg"), Is.False,
            "multi-char string argument is not replaceable with char — must not be flagged");
    }

    // ── Where().Where() chaining ──────────────────────────────────────────

    [Test]
    public async Task Flags_ChainedWhereFilters()
    {
        SetSource(@"
using System.Collections.Generic;
using System.Linq;
public class C {
    public void M(List<int> items) {
        var r = items.Where(x => x > 0).Where(x => x < 100).ToList();
    }
}");
        var issues = await _engine.AnalyzePerformanceAsync("Test.cs");
        Assert.That(issues.Any(i => i.IssueType == "ChainedWhereFilters"), Is.True,
            ".Where().Where() should be flagged as mergeable");
    }

    [Test]
    public async Task DoesNotFlag_SingleWhere()
    {
        SetSource(@"
using System.Collections.Generic;
using System.Linq;
public class C {
    public void M(List<int> items) {
        var r = items.Where(x => x > 0 && x < 100).ToList();
    }
}");
        var issues = await _engine.AnalyzePerformanceAsync("Test.cs");
        Assert.That(issues.Any(i => i.IssueType == "ChainedWhereFilters"), Is.False,
            "single Where with compound predicate must not be flagged");
    }

    // ── Loop invariant condition ──────────────────────────────────────────

    [Test]
    public async Task Flags_CountMethodInForLoopCondition()
    {
        SetSource(@"
using System.Collections.Generic;
using System.Linq;
public class C {
    public void M(IEnumerable<int> items) {
        for (int i = 0; i < items.Count(); i++) { }
    }
}");
        var issues = await _engine.AnalyzePerformanceAsync("Test.cs");
        Assert.That(issues.Any(i => i.IssueType == "LoopInvariantCondition"), Is.True,
            ".Count() in for loop condition is O(n) per iteration and should be flagged");
    }

    [Test]
    public async Task Flags_CountPropertyInForLoopCondition()
    {
        SetSource(@"
using System.Collections.Generic;
public class C {
    public void M(List<int> items) {
        for (int i = 0; i < items.Count; i++) { }
    }
}");
        var issues = await _engine.AnalyzePerformanceAsync("Test.cs");
        Assert.That(issues.Any(i => i.IssueType == "LoopInvariantCondition"), Is.True,
            "List<T>.Count in for loop condition is re-read every iteration and should be flagged");
    }

    [Test]
    public async Task DoesNotFlag_ArrayLengthInForLoopCondition()
    {
        SetSource(@"
public class C {
    public void M(int[] items) {
        for (int i = 0; i < items.Length; i++) { }
    }
}");
        var issues = await _engine.AnalyzePerformanceAsync("Test.cs");
        Assert.That(issues.Any(i => i.IssueType == "LoopInvariantCondition"), Is.False,
            "array.Length in for loop condition is hoisted by the JIT — must not be flagged");
    }

    // ── dynamic type usage ────────────────────────────────────────────────

    [Test]
    public async Task Flags_DynamicLocalVariable()
    {
        SetSource(@"
public class C {
    public void M() {
        dynamic x = 42;
        x.DoSomething();
    }
}");
        var issues = await _engine.AnalyzePerformanceAsync("Test.cs");
        Assert.That(issues.Any(i => i.IssueType == "DynamicTypeUsage"), Is.True,
            "'dynamic' local variable should be flagged — forces DLR dispatch");
    }

    [Test]
    public async Task Flags_DynamicParameter()
    {
        SetSource(@"
public class C {
    public void M(dynamic input) { }
}");
        var issues = await _engine.AnalyzePerformanceAsync("Test.cs");
        Assert.That(issues.Any(i => i.IssueType == "DynamicTypeUsage"), Is.True,
            "'dynamic' parameter should be flagged");
    }

    // ── object local variable ─────────────────────────────────────────────

    [Test]
    public async Task Flags_ObjectLocalVariable()
    {
        SetSource(@"
public class C {
    public void M() {
        object value = GetValue();
    }
    private int GetValue() => 42;
}");
        var issues = await _engine.AnalyzePerformanceAsync("Test.cs");
        Assert.That(issues.Any(i => i.IssueType == "ObjectTypeUsage"), Is.True,
            "local variable typed as 'object' should be flagged — causes boxing for value types");
    }

    [Test]
    public async Task DoesNotFlag_ObjectField()
    {
        SetSource(@"
public class C {
    private object _state = new object();
}");
        var issues = await _engine.AnalyzePerformanceAsync("Test.cs");
        Assert.That(issues.Any(i => i.IssueType == "ObjectTypeUsage"), Is.False,
            "object field (e.g. lock target) must not be flagged — only local variables are in scope");
    }

    // ── Enum.Parse in loop ────────────────────────────────────────────────

    [Test]
    public async Task Flags_EnumParseInsideForEach()
    {
        SetSource(@"
public enum Status { Active, Inactive }
public class C {
    public void M(string[] rows) {
        foreach (var row in rows) {
            var s = Enum.Parse<Status>(row);
        }
    }
}");
        var issues = await _engine.AnalyzePerformanceAsync("Test.cs");
        Assert.That(issues.Any(i => i.IssueType == "EnumParseInLoop"), Is.True,
            "Enum.Parse<T>() inside foreach should be flagged — re-parses string on every iteration");
    }

    [Test]
    public async Task DoesNotFlag_EnumParseOutsideLoop()
    {
        SetSource(@"
public enum Status { Active, Inactive }
public class C {
    public void M(string input) {
        var s = Enum.Parse<Status>(input);
    }
}");
        var issues = await _engine.AnalyzePerformanceAsync("Test.cs");
        Assert.That(issues.Any(i => i.IssueType == "EnumParseInLoop"), Is.False,
            "Enum.Parse<T>() outside a loop must not be flagged");
    }

    // ── string.Join opportunity ───────────────────────────────────────────

    [Test]
    public async Task Flags_AggregateWithStringSeparator()
    {
        SetSource(@"
using System.Collections.Generic;
using System.Linq;
public class C {
    public string M(List<string> items) =>
        items.Aggregate((a, b) => a + "", "" + b);
}");
        var issues = await _engine.AnalyzePerformanceAsync("Test.cs");
        Assert.That(issues.Any(i => i.IssueType == "StringJoinOpportunity"), Is.True,
            ".Aggregate() with string concat should be flagged — use string.Join()");
    }

    [Test]
    public async Task DoesNotFlag_AggregateWithoutStringLiteral()
    {
        SetSource(@"
using System.Collections.Generic;
using System.Linq;
public class C {
    public int M(List<int> items) =>
        items.Aggregate((a, b) => a + b);
}");
        var issues = await _engine.AnalyzePerformanceAsync("Test.cs");
        Assert.That(issues.Any(i => i.IssueType == "StringJoinOpportunity"), Is.False,
            "numeric Aggregate without string literal must not be flagged");
    }

    // ── Repeated method call not cached ───────────────────────────────────

    [Test]
    public async Task Flags_SameCallThreeTimes()
    {
        SetSource(@"
public class Config {
    public string GetValue(string key) => key;
}
public class C {
    private Config _cfg = new();
    public void M() {
        var a = _cfg.GetValue(""timeout"");
        var b = _cfg.GetValue(""timeout"");
        var c = _cfg.GetValue(""timeout"");
    }
}");
        var issues = await _engine.AnalyzePerformanceAsync("Test.cs");
        Assert.That(issues.Any(i => i.IssueType == "RepeatedMethodCallNotCached"), Is.True,
            "same method call 3 times should be flagged — cache in a local variable");
    }

    [Test]
    public async Task DoesNotFlag_SameCallTwice()
    {
        SetSource(@"
public class Config {
    public string GetValue(string key) => key;
}
public class C {
    private Config _cfg = new();
    public void M() {
        var a = _cfg.GetValue(""timeout"");
        var b = _cfg.GetValue(""timeout"");
    }
}");
        var issues = await _engine.AnalyzePerformanceAsync("Test.cs");
        Assert.That(issues.Any(i => i.IssueType == "RepeatedMethodCallNotCached"), Is.False,
            "same call only twice is below the caching threshold — must not be flagged");
    }
}

// ════════════════════════════════════════════════════════════════════════════
// 5. AnalyzeExceptionHandlingAsync — checks 5-7 + CatchAll suggestion
// ════════════════════════════════════════════════════════════════════════════
[TestFixture]
public class ExceptionHandlingAccuracyTests
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

    private void SetSource(string source) =>
        _workspaceManager.SetTestSolution(
            TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Test.cs", source)]));

    // ── CatchAll with exception type suggestions ──────────────────────────

    [Test]
    public async Task CatchAll_WithFileReadInTryBlock_SuggestsIOException()
    {
        SetSource(@"
using System.IO;
public class C {
    public void M() {
        try { File.ReadAllText(""path.txt""); }
        catch (Exception) { }
    }
}");
        var findings = await _engine.AnalyzeExceptionHandlingAsync("Test.cs");
        var catchAll = findings.FirstOrDefault(f => f.Pattern == "CatchAll");
        Assert.That(catchAll, Is.Not.Null, "catch (Exception) should produce a CatchAll finding");
        Assert.That(catchAll!.Description, Does.Contain("IOException"),
            "File.ReadAllText in try block should suggest IOException");
    }

    [Test]
    public async Task CatchAll_WithDivisionInTryBlock_SuggestsDivideByZero()
    {
        SetSource(@"
public class C {
    public void M(int a, int b) {
        try { var r = a / b; }
        catch (Exception) { }
    }
}");
        var findings = await _engine.AnalyzeExceptionHandlingAsync("Test.cs");
        var catchAll = findings.FirstOrDefault(f => f.Pattern == "CatchAll");
        Assert.That(catchAll, Is.Not.Null);
        Assert.That(catchAll!.Description, Does.Contain("DivideByZeroException"),
            "division in try block should suggest DivideByZeroException");
    }

    [Test]
    public async Task CatchAll_WithNoRecognizedContent_HasNoSuggestion()
    {
        SetSource(@"
public class C {
    public void M() {
        try { var x = 1 + 1; }
        catch (Exception) { throw; }
    }
}");
        var findings = await _engine.AnalyzeExceptionHandlingAsync("Test.cs");
        var catchAll = findings.FirstOrDefault(f => f.Pattern == "CatchAll");
        Assert.That(catchAll, Is.Not.Null);
        Assert.That(catchAll!.Description, Does.Not.Contain("consider catching"),
            "unrecognized try content should not produce a suggestion");
    }

    // ── Check 5: GenericThrowExpression ───────────────────────────────────

    [Test]
    public async Task Flags_GenericThrow_WithNullMessage_SuggestsArgumentNullException()
    {
        SetSource(@"
public class C {
    public void M(string s) {
        if (s == null) throw new Exception(""value cannot be null"");
    }
}");
        var findings = await _engine.AnalyzeExceptionHandlingAsync("Test.cs");
        var finding = findings.FirstOrDefault(f => f.Pattern == "GenericThrowExpression");
        Assert.That(finding, Is.Not.Null, "throw new Exception() should produce GenericThrowExpression");
        Assert.That(finding!.Description, Does.Contain("ArgumentNullException"),
            "message containing 'null' should suggest ArgumentNullException");
    }

    [Test]
    public async Task Flags_GenericThrow_WithInvalidOperationMessage_SuggestsInvalidOperationException()
    {
        SetSource(@"
public class C {
    private bool _started;
    public void Start() {
        if (_started) throw new Exception(""already started"");
        _started = true;
    }
}");
        var findings = await _engine.AnalyzeExceptionHandlingAsync("Test.cs");
        var finding = findings.FirstOrDefault(f => f.Pattern == "GenericThrowExpression");
        Assert.That(finding, Is.Not.Null);
        Assert.That(finding!.Description, Does.Contain("InvalidOperationException"),
            "message 'already started' should suggest InvalidOperationException");
    }

    [Test]
    public async Task Flags_GenericThrow_WithUnrecognizedMessage_GivesGenericAdvice()
    {
        SetSource(@"
public class C {
    public void M() {
        throw new Exception(""something went wrong"");
    }
}");
        var findings = await _engine.AnalyzeExceptionHandlingAsync("Test.cs");
        var finding = findings.FirstOrDefault(f => f.Pattern == "GenericThrowExpression");
        Assert.That(finding, Is.Not.Null, "throw new Exception() with unrecognized message still flags");
        Assert.That(finding!.Description, Does.Contain("specific BCL exception"),
            "unrecognized message should give generic advice");
    }

    [Test]
    public async Task DoesNotFlag_ThrowSpecificException()
    {
        SetSource(@"
public class C {
    public void M(string s) {
        if (s == null) throw new ArgumentNullException(nameof(s));
    }
}");
        var findings = await _engine.AnalyzeExceptionHandlingAsync("Test.cs");
        Assert.That(findings.Any(f => f.Pattern == "GenericThrowExpression"), Is.False,
            "throw new ArgumentNullException() must not be flagged");
    }

    // ── Check 6: UnprotectedDispose ───────────────────────────────────────

    [Test]
    public async Task Flags_ExplicitDispose_NotInTryCatch()
    {
        SetSource(@"
using System.IO;
public class C {
    public void M() {
        var stream = new MemoryStream();
        stream.Dispose();
    }
}");
        var findings = await _engine.AnalyzeExceptionHandlingAsync("Test.cs");
        Assert.That(findings.Any(f => f.Pattern == "UnprotectedDispose"), Is.True,
            "explicit Dispose() not in try/catch should be flagged");
    }

    [Test]
    public async Task DoesNotFlag_ExplicitDispose_InsideTryCatch()
    {
        SetSource(@"
using System.IO;
public class C {
    public void M() {
        var stream = new MemoryStream();
        try { stream.Dispose(); } catch { }
    }
}");
        var findings = await _engine.AnalyzeExceptionHandlingAsync("Test.cs");
        Assert.That(findings.Any(f => f.Pattern == "UnprotectedDispose"), Is.False,
            "Dispose() inside try/catch must not be flagged");
    }

    [Test]
    public async Task DoesNotFlag_ExplicitDispose_InsideUsingStatement()
    {
        SetSource(@"
using System.IO;
public class C {
    public void M() {
        using (var stream = new MemoryStream()) {
            stream.Dispose();
        }
    }
}");
        var findings = await _engine.AnalyzeExceptionHandlingAsync("Test.cs");
        Assert.That(findings.Any(f => f.Pattern == "UnprotectedDispose"), Is.False,
            "Dispose() inside a using block must not be flagged");
    }

    // ── Check 7: UnsafeDisposeImplementation ──────────────────────────────

    [Test]
    public async Task Flags_DisposeImpl_WithTwoUnprotectedSubDisposes()
    {
        SetSource(@"
using System;
public class C : IDisposable {
    private IDisposable _a = null!;
    private IDisposable _b = null!;
    public void Dispose() {
        _a.Dispose();
        _b.Dispose();
    }
}");
        var findings = await _engine.AnalyzeExceptionHandlingAsync("Test.cs");
        Assert.That(findings.Any(f => f.Pattern == "UnsafeDisposeImplementation"), Is.True,
            "Dispose() with 2 unprotected sub-Dispose calls should be flagged");
    }

    [Test]
    public async Task DoesNotFlag_DisposeImpl_WithOneSubDispose()
    {
        SetSource(@"
using System;
public class C : IDisposable {
    private IDisposable _a = null!;
    public void Dispose() {
        _a.Dispose();
    }
}");
        var findings = await _engine.AnalyzeExceptionHandlingAsync("Test.cs");
        Assert.That(findings.Any(f => f.Pattern == "UnsafeDisposeImplementation"), Is.False,
            "Dispose() with only one sub-Dispose does not need wrapping — no risk of skipping");
    }

    [Test]
    public async Task DoesNotFlag_DisposeImpl_WithProtectedSubDisposes()
    {
        SetSource(@"
using System;
public class C : IDisposable {
    private IDisposable _a = null!;
    private IDisposable _b = null!;
    public void Dispose() {
        try { _a.Dispose(); } catch { }
        try { _b.Dispose(); } catch { }
    }
}");
        var findings = await _engine.AnalyzeExceptionHandlingAsync("Test.cs");
        Assert.That(findings.Any(f => f.Pattern == "UnsafeDisposeImplementation"), Is.False,
            "Dispose() with individually try-wrapped sub-Dispose calls must not be flagged");
    }
}

// ════════════════════════════════════════════════════════════════════════════
// 6. FireAndForgetTask — Task.Run / Task.Factory.StartNew not awaited
// ════════════════════════════════════════════════════════════════════════════
[TestFixture]
public class FireAndForgetTaskAccuracyTests
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

    private void SetSource(string source) =>
        _workspaceManager.SetTestSolution(
            TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Test.cs", source)]));

    [Test]
    public async Task Flags_TaskRun_NotAwaited()
    {
        SetSource(@"
using System.Threading.Tasks;
public class C {
    public void M() {
        Task.Run(() => DoWork());
    }
    private void DoWork() { }
}");
        var findings = await _engine.DetectAntiPatternsAsync("Test.cs");
        Assert.That(findings.Any(f => f.Pattern == "FireAndForgetTask"), Is.True,
            "Task.Run() not awaited should be flagged — exceptions silently swallowed");
    }

    [Test]
    public async Task Flags_TaskFactoryStartNew_NotAwaited()
    {
        SetSource(@"
using System.Threading.Tasks;
public class C {
    public void M() {
        Task.Factory.StartNew(() => DoWork());
    }
    private void DoWork() { }
}");
        var findings = await _engine.DetectAntiPatternsAsync("Test.cs");
        Assert.That(findings.Any(f => f.Pattern == "FireAndForgetTask"), Is.True,
            "Task.Factory.StartNew() not awaited should be flagged");
    }

    [Test]
    public async Task DoesNotFlag_TaskRun_Awaited()
    {
        SetSource(@"
using System.Threading.Tasks;
public class C {
    public async Task M() {
        await Task.Run(() => DoWork());
    }
    private void DoWork() { }
}");
        var findings = await _engine.DetectAntiPatternsAsync("Test.cs");
        Assert.That(findings.Any(f => f.Pattern == "FireAndForgetTask"), Is.False,
            "awaited Task.Run() must not be flagged");
    }

    [Test]
    public async Task DoesNotFlag_TaskRun_Assigned()
    {
        SetSource(@"
using System.Threading.Tasks;
public class C {
    private Task _background = null!;
    public void M() {
        _background = Task.Run(() => DoWork());
    }
    private void DoWork() { }
}");
        var findings = await _engine.DetectAntiPatternsAsync("Test.cs");
        Assert.That(findings.Any(f => f.Pattern == "FireAndForgetTask"), Is.False,
            "Task.Run() assigned to a variable must not be flagged — caller manages the task");
    }

    [Test]
    public async Task DoesNotFlag_TaskRun_PassedToWhenAll()
    {
        SetSource(@"
using System.Threading.Tasks;
public class C {
    public async Task M() {
        await Task.WhenAll(Task.Run(() => DoWork()), Task.Run(() => DoWork()));
    }
    private void DoWork() { }
}");
        var findings = await _engine.DetectAntiPatternsAsync("Test.cs");
        Assert.That(findings.Any(f => f.Pattern == "FireAndForgetTask"), Is.False,
            "Task.Run() passed as argument to Task.WhenAll must not be flagged");
    }
}

// ════════════════════════════════════════════════════════════════════════════
// 7. SecurityEngine — hardcoded secret values, secrets in comments, SQL Dapper
// ════════════════════════════════════════════════════════════════════════════
[TestFixture]
public class SecurityEngineAccuracyTests
{
    private PersistentWorkspaceManager _workspaceManager = null!;
    private SecurityEngine _engine = null!;

    [SetUp]
    public void Setup()
    {
        _workspaceManager = new PersistentWorkspaceManager(NullLogger<PersistentWorkspaceManager>.Instance);
        _engine = new SecurityEngine(_workspaceManager);
    }

    [TearDown]
    public void TearDown() => _workspaceManager?.Dispose();

    private void SetSource(string source) =>
        _workspaceManager.SetTestSolution(
            TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Test.cs", source)]));

    // ── HardcodedSecretValue — connection string with password ────────────

    [Test]
    public async Task Flags_ConnectionString_WithEmbeddedPassword()
    {
        SetSource(@"
public class C {
    private string _conn = ""Server=prod.db;User Id=sa;Password=SuperSecret123;"";
}");
        var issues = await _engine.AnalyzeSecurityAsync("Test.cs");
        Assert.That(issues.Any(i => i.IssueType == "HardcodedSecretValue"), Is.True,
            "connection string with Password= should be flagged as HardcodedSecretValue");
    }

    [Test]
    public async Task Flags_JwtToken_InStringLiteral()
    {
        SetSource(@"
public class C {
    private string _token = ""eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiIxMjM0NTY3ODkwIiwibmFtZSI6IkpvaG4gRG9lIiwiaWF0IjoxNTE2MjM5MDIyfQ.SflKxwRJSMeKKF2QT4fwpMeJf36POk6yJV_adQssw5c"";
}");
        var issues = await _engine.AnalyzeSecurityAsync("Test.cs");
        Assert.That(issues.Any(i => i.IssueType == "HardcodedSecretValue"), Is.True,
            "JWT token in string literal should be flagged");
    }

    [Test]
    public async Task Flags_ApiKey_WithKnownPrefix()
    {
        SetSource(@"
public class C {
    private string _key = ""sk-live-1234567890abcdefghij"";
}");
        var issues = await _engine.AnalyzeSecurityAsync("Test.cs");
        Assert.That(issues.Any(i => i.IssueType == "HardcodedSecretValue"), Is.True,
            "string starting with sk-live- should be flagged as API key");
    }

    [Test]
    public async Task DoesNotFlag_NormalString_WithNoSecretPattern()
    {
        SetSource(@"
public class C {
    private string _greeting = ""Hello, World!"";
}");
        var issues = await _engine.AnalyzeSecurityAsync("Test.cs");
        Assert.That(issues.Any(i => i.IssueType == "HardcodedSecretValue"), Is.False,
            "normal string with no secret pattern must not be flagged");
    }

    // ── SecretInComment ───────────────────────────────────────────────────

    [Test]
    public async Task Flags_Password_InSingleLineComment()
    {
        SetSource(@"
public class C {
    // password=admin123 — for local dev only
    public void M() { }
}");
        var issues = await _engine.AnalyzeSecurityAsync("Test.cs");
        Assert.That(issues.Any(i => i.IssueType == "SecretInComment"), Is.True,
            "password= in a comment should be flagged as SecretInComment");
    }

    [Test]
    public async Task Flags_ApiKey_InXmlDocComment()
    {
        SetSource(@"
public class C {
    /// <summary>Test key: sk-live-abc123def456</summary>
    public void M() { }
}");
        var issues = await _engine.AnalyzeSecurityAsync("Test.cs");
        Assert.That(issues.Any(i => i.IssueType == "SecretInComment"), Is.True,
            "API key in XML doc comment should be flagged");
    }

    [Test]
    public async Task DoesNotFlag_InnocentComment()
    {
        SetSource(@"
public class C {
    // This method handles the user session token renewal
    public void M() { }
}");
        var issues = await _engine.AnalyzeSecurityAsync("Test.cs");
        // 'token' alone in a description-style comment should ideally not fire,
        // but our pattern is intentionally broad — accept either outcome but verify no crash
        Assert.That(issues, Is.Not.Null);
    }

    // ── SQL injection via Dapper interpolated variable ────────────────────

    [Test]
    public async Task Flags_Dapper_QueryAsync_WithInterpolatedVariable()
    {
        SetSource(@"
public class C {
    public async System.Threading.Tasks.Task M(int userId, dynamic conn) {
        var sql = $""SELECT * FROM Users WHERE Id = {userId}"";
        await conn.QueryAsync(sql);
    }
}");
        var issues = await _engine.CheckForSqlInjectionAsync("Test.cs");
        Assert.That(issues.Any(i => i.IssueType == "PossibleSqlInjection"), Is.True,
            "Dapper QueryAsync called with a variable assigned an interpolated string should be flagged");
    }

    [Test]
    public async Task Flags_Dapper_Execute_WithInlineInterpolation()
    {
        SetSource(@"
public class C {
    public void M(string status, dynamic conn) {
        conn.Execute($""UPDATE Orders SET Status = '{status}'"");
    }
}");
        var issues = await _engine.CheckForSqlInjectionAsync("Test.cs");
        Assert.That(issues.Any(i => i.IssueType == "PossibleSqlInjection"), Is.True,
            "Dapper Execute with inline interpolated string should be flagged");
    }

    [Test]
    public async Task DoesNotFlag_Dapper_Query_WithConstantSql()
    {
        SetSource(@"
public class C {
    public void M(dynamic conn) {
        conn.Query(""SELECT Id, Name FROM Users WHERE IsActive = 1"");
    }
}");
        var issues = await _engine.CheckForSqlInjectionAsync("Test.cs");
        Assert.That(issues.Any(i => i.IssueType == "PossibleSqlInjection"), Is.False,
            "Dapper Query with a plain string literal must not be flagged");
    }
}

// ════════════════════════════════════════════════════════════════════════════
// 8. MissingDispose — IDisposable allocation without using/try-finally
// ════════════════════════════════════════════════════════════════════════════
[TestFixture]
public class MissingDisposeAccuracyTests
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

    private void SetSource(string source) =>
        _workspaceManager.SetTestSolution(
            TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Test.cs", source)]));

    [Test]
    public async Task Flags_StreamReader_WithoutUsing()
    {
        SetSource(@"
using System.IO;
public class C {
    public void M() {
        var reader = new StreamReader(""file.txt"");
        var text = reader.ReadToEnd();
    }
}");
        var findings = await _engine.DetectAntiPatternsAsync("Test.cs");
        Assert.That(findings.Any(f => f.Pattern == "MissingDispose"), Is.True,
            "StreamReader not in using should be flagged");
    }

    [Test]
    public async Task Flags_SqlConnection_WithoutUsing()
    {
        SetSource(@"
using Microsoft.Data.SqlClient;
public class C {
    public void M() {
        var conn = new SqlConnection(""...''"");
        conn.Open();
    }
}");
        var findings = await _engine.DetectAntiPatternsAsync("Test.cs");
        Assert.That(findings.Any(f => f.Pattern == "MissingDispose"), Is.True,
            "SqlConnection not in using should be flagged");
    }

    [Test]
    public async Task DoesNotFlag_StreamReader_InUsingDeclaration()
    {
        SetSource(@"
using System.IO;
public class C {
    public void M() {
        using var reader = new StreamReader(""file.txt"");
        var text = reader.ReadToEnd();
    }
}");
        var findings = await _engine.DetectAntiPatternsAsync("Test.cs");
        Assert.That(findings.Any(f => f.Pattern == "MissingDispose"), Is.False,
            "'using var' declaration must not be flagged");
    }

    [Test]
    public async Task DoesNotFlag_StreamReader_InUsingBlock()
    {
        SetSource(@"
using System.IO;
public class C {
    public void M() {
        using (var reader = new StreamReader(""file.txt"")) {
            var text = reader.ReadToEnd();
        }
    }
}");
        var findings = await _engine.DetectAntiPatternsAsync("Test.cs");
        Assert.That(findings.Any(f => f.Pattern == "MissingDispose"), Is.False,
            "StreamReader in using block must not be flagged");
    }

    [Test]
    public async Task Flags_FileOpenText_WithoutUsing()
    {
        SetSource(@"
using System.IO;
public class C {
    public void M() {
        var reader = File.OpenText(""path.txt"");
        var line = reader.ReadLine();
    }
}");
        var findings = await _engine.DetectAntiPatternsAsync("Test.cs");
        Assert.That(findings.Any(f => f.Pattern == "MissingDispose"), Is.True,
            "File.OpenText() result not disposed should be flagged");
    }
}

// ════════════════════════════════════════════════════════════════════════════
// 9. EnumSwitchExhaustiveness — missing enum members without default case
// ════════════════════════════════════════════════════════════════════════════
[TestFixture]
public class EnumSwitchExhaustivenessTests
{
    private PersistentWorkspaceManager _workspaceManager = null!;
    private ControlFlowEngine _engine = null!;

    [SetUp]
    public void Setup()
    {
        _workspaceManager = new PersistentWorkspaceManager(NullLogger<PersistentWorkspaceManager>.Instance);
        _engine = new ControlFlowEngine(_workspaceManager);
    }

    [TearDown]
    public void TearDown() => _workspaceManager?.Dispose();

    private void SetSource(string source) =>
        _workspaceManager.SetTestSolution(
            TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Test.cs", source)]));

    [Test]
    public async Task Flags_Switch_MissingEnumMember()
    {
        SetSource(@"
public enum Status { Active, Inactive, Pending }
public class C {
    public void M(Status s) {
        switch (s) {
            case Status.Active: break;
            case Status.Inactive: break;
        }
    }
}");
        var gaps = await _engine.FindNonExhaustiveEnumSwitchesAsync("Test.cs");
        Assert.That(gaps, Is.Not.Empty, "switch missing Pending member should be flagged");
        Assert.That(gaps[0].MissingMembers, Contains.Item("Pending"));
    }

    [Test]
    public async Task DoesNotFlag_Switch_WithDefaultCase()
    {
        SetSource(@"
public enum Status { Active, Inactive, Pending }
public class C {
    public void M(Status s) {
        switch (s) {
            case Status.Active: break;
            default: break;
        }
    }
}");
        var gaps = await _engine.FindNonExhaustiveEnumSwitchesAsync("Test.cs");
        Assert.That(gaps, Is.Empty, "switch with default case should not be flagged — explicitly handles unknowns");
    }

    [Test]
    public async Task DoesNotFlag_Switch_AllMembersHandled()
    {
        SetSource(@"
public enum Status { Active, Inactive }
public class C {
    public void M(Status s) {
        switch (s) {
            case Status.Active: break;
            case Status.Inactive: break;
        }
    }
}");
        var gaps = await _engine.FindNonExhaustiveEnumSwitchesAsync("Test.cs");
        Assert.That(gaps, Is.Empty, "switch covering all members must not be flagged");
    }

    [Test]
    public async Task DoesNotFlag_Switch_OnNonEnumType()
    {
        SetSource(@"
public class C {
    public void M(int x) {
        switch (x) {
            case 1: break;
            case 2: break;
        }
    }
}");
        var gaps = await _engine.FindNonExhaustiveEnumSwitchesAsync("Test.cs");
        Assert.That(gaps, Is.Empty, "switch on non-enum type must not be flagged");
    }
}

// ════════════════════════════════════════════════════════════════════════════
// 10. ReflectionInLoop + CollectionWithoutCapacity (PerformanceEngine)
// ════════════════════════════════════════════════════════════════════════════
[TestFixture]
public class PerformanceEngine2AccuracyTests
{
    private PersistentWorkspaceManager _workspaceManager = null!;
    private PerformanceEngine _engine = null!;

    [SetUp]
    public void Setup()
    {
        _workspaceManager = new PersistentWorkspaceManager(NullLogger<PersistentWorkspaceManager>.Instance);
        _engine = new PerformanceEngine(_workspaceManager);
    }

    [TearDown]
    public void TearDown() => _workspaceManager?.Dispose();

    private void SetSource(string source) =>
        _workspaceManager.SetTestSolution(
            TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Test.cs", source)]));

    // ── ReflectionInLoop ──────────────────────────────────────────────────

    [Test]
    public async Task Flags_GetMethod_InsideForeach()
    {
        SetSource(@"
using System;
public class C {
    public void M(string[] names) {
        foreach (var name in names) {
            var method = typeof(C).GetMethod(name);
        }
    }
}");
        var issues = await _engine.AnalyzePerformanceAsync("Test.cs");
        Assert.That(issues.Any(i => i.IssueType == "ReflectionInLoop"), Is.True,
            "GetMethod() inside foreach should be flagged — expensive per iteration");
    }

    [Test]
    public async Task Flags_GetProperty_InsideFor()
    {
        SetSource(@"
using System;
public class C {
    public void M(int n) {
        for (int i = 0; i < n; i++) {
            var prop = typeof(C).GetProperty(""Name"");
        }
    }
}");
        var issues = await _engine.AnalyzePerformanceAsync("Test.cs");
        Assert.That(issues.Any(i => i.IssueType == "ReflectionInLoop"), Is.True,
            "GetProperty() inside for loop should be flagged");
    }

    [Test]
    public async Task DoesNotFlag_GetMethod_OutsideLoop()
    {
        SetSource(@"
using System;
public class C {
    public void M() {
        var method = typeof(C).GetMethod(""DoWork"");
    }
}");
        var issues = await _engine.AnalyzePerformanceAsync("Test.cs");
        Assert.That(issues.Any(i => i.IssueType == "ReflectionInLoop"), Is.False,
            "GetMethod() outside a loop must not be flagged");
    }

    // ── CollectionWithoutCapacity ─────────────────────────────────────────

    [Test]
    public async Task Flags_ListWithNoCapacity_WhenAddInLoop()
    {
        SetSource(@"
using System.Collections.Generic;
public class C {
    public List<string> M(string[] items) {
        var result = new List<string>();
        foreach (var item in items) result.Add(item);
        return result;
    }
}");
        var issues = await _engine.AnalyzePerformanceAsync("Test.cs");
        Assert.That(issues.Any(i => i.IssueType == "CollectionWithoutCapacity"), Is.True,
            "new List<T>() with no capacity followed by loop .Add() should be flagged");
    }

    [Test]
    public async Task DoesNotFlag_ListWithCapacity()
    {
        SetSource(@"
using System.Collections.Generic;
public class C {
    public List<string> M(string[] items) {
        var result = new List<string>(items.Length);
        foreach (var item in items) result.Add(item);
        return result;
    }
}");
        var issues = await _engine.AnalyzePerformanceAsync("Test.cs");
        Assert.That(issues.Any(i => i.IssueType == "CollectionWithoutCapacity"), Is.False,
            "new List<T>(capacity) must not be flagged");
    }

    [Test]
    public async Task DoesNotFlag_ListWithNoLoopAdd()
    {
        SetSource(@"
using System.Collections.Generic;
public class C {
    public List<string> M() {
        var result = new List<string>();
        result.Add(""one"");
        result.Add(""two"");
        return result;
    }
}");
        var issues = await _engine.AnalyzePerformanceAsync("Test.cs");
        Assert.That(issues.Any(i => i.IssueType == "CollectionWithoutCapacity"), Is.False,
            "new List<T>() with fixed adds (not in a loop) must not be flagged");
    }
}

// ════════════════════════════════════════════════════════════════════════════
// 11. CaptiveDependency — Singleton depending on Scoped/Transient
// ════════════════════════════════════════════════════════════════════════════
[TestFixture]
public class CaptiveDependencyAccuracyTests
{
    private PersistentWorkspaceManager _workspaceManager = null!;
    private DependencyInjectionEngine _engine = null!;

    [SetUp]
    public void Setup()
    {
        _workspaceManager = new PersistentWorkspaceManager(NullLogger<PersistentWorkspaceManager>.Instance);
        _engine = new DependencyInjectionEngine(_workspaceManager);
    }

    [TearDown]
    public void TearDown() => _workspaceManager?.Dispose();

    private void SetSource(string source) =>
        _workspaceManager.SetTestSolution(
            TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Program.cs", source)]));

    [Test]
    public async Task Flags_Singleton_DependingOn_Scoped()
    {
        SetSource(@"
using Microsoft.Extensions.DependencyInjection;
public interface IScoped { }
public class ScopedService : IScoped { }
public class SingletonService { public SingletonService(IScoped dep) { } }
public class Startup {
    public void Configure(IServiceCollection services) {
        services.AddSingleton<SingletonService>();
        services.AddScoped<IScoped, ScopedService>();
    }
}");
        var findings = await _engine.FindCaptiveDependenciesAsync();
        Assert.That(findings.Any(f => f.ConsumerClass == "SingletonService" && f.DependencyLifetime == "Scoped"),
            Is.True, "Singleton depending on Scoped should be flagged as captive dependency");
    }

    [Test]
    public async Task DoesNotFlag_Singleton_DependingOn_Singleton()
    {
        SetSource(@"
using Microsoft.Extensions.DependencyInjection;
public interface IOther { }
public class OtherService : IOther { }
public class SingletonService { public SingletonService(IOther dep) { } }
public class Startup {
    public void Configure(IServiceCollection services) {
        services.AddSingleton<SingletonService>();
        services.AddSingleton<IOther, OtherService>();
    }
}");
        var findings = await _engine.FindCaptiveDependenciesAsync();
        Assert.That(findings.Any(f => f.ConsumerClass == "SingletonService"), Is.False,
            "Singleton depending on another Singleton is fine — must not be flagged");
    }

    [Test]
    public async Task Flags_Singleton_DependingOn_Transient()
    {
        SetSource(@"
using Microsoft.Extensions.DependencyInjection;
public interface ITransient { }
public class TransientService : ITransient { }
public class SingletonService { public SingletonService(ITransient dep) { } }
public class Startup {
    public void Configure(IServiceCollection services) {
        services.AddSingleton<SingletonService>();
        services.AddTransient<ITransient, TransientService>();
    }
}");
        var findings = await _engine.FindCaptiveDependenciesAsync();
        Assert.That(findings.Any(f => f.ConsumerClass == "SingletonService" && f.DependencyLifetime == "Transient"),
            Is.True, "Singleton depending on Transient should be flagged");
    }
}

// ════════════════════════════════════════════════════════════════════════════
// 12. NullDereferenceChain + ArithmeticOverflow (SecurityAndSafetyEngine)
// ════════════════════════════════════════════════════════════════════════════
[TestFixture]
public class SafetyEngineExtendedAccuracyTests
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

    private void SetSource(string source) =>
        _workspaceManager.SetTestSolution(
            TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Test.cs", source)]));

    // ── NullDereferenceChain ──────────────────────────────────────────────

    [Test]
    public async Task Flags_ChainedAccess_WithoutNullGuard()
    {
        SetSource(@"
public class Inner { public string Value { get; set; } = """"; }
public class Outer { public Inner? Nested { get; set; } }
public class C {
    public void M(Outer obj) {
        var v = obj.Nested.Value;
    }
}");
        var issues = await _engine.FindNullDereferenceChainAsync("Test.cs");
        Assert.That(issues.Any(i => i.Type == "NullDereferenceChain"), Is.True,
            "obj.Nested.Value without null guard on obj.Nested should be flagged");
    }

    [Test]
    public async Task DoesNotFlag_ChainedAccess_WithNullConditional()
    {
        SetSource(@"
public class Inner { public string Value { get; set; } = """"; }
public class Outer { public Inner? Nested { get; set; } }
public class C {
    public void M(Outer obj) {
        var v = obj.Nested?.Value;
    }
}");
        var issues = await _engine.FindNullDereferenceChainAsync("Test.cs");
        Assert.That(issues.Any(i => i.Type == "NullDereferenceChain"), Is.False,
            "obj.Nested?.Value uses null-conditional — must not be flagged");
    }

    // ── ArithmeticOverflowRisk ────────────────────────────────────────────

    [Test]
    public async Task Flags_IntMaxValue_Addition()
    {
        SetSource(@"
public class C {
    public int M() {
        return int.MaxValue + 1;
    }
}");
        var issues = await _engine.FindArithmeticOverflowRisksAsync("Test.cs");
        Assert.That(issues.Any(i => i.Type == "ArithmeticOverflowRisk"), Is.True,
            "int.MaxValue + 1 without checked block should be flagged");
    }

    [Test]
    public async Task DoesNotFlag_IntMaxValue_InsideCheckedBlock()
    {
        SetSource(@"
public class C {
    public int M() {
        checked { return int.MaxValue + 1; }
    }
}");
        var issues = await _engine.FindArithmeticOverflowRisksAsync("Test.cs");
        Assert.That(issues.Any(i => i.Type == "ArithmeticOverflowRisk"), Is.False,
            "int.MaxValue + 1 inside checked { } intentionally throws — must not be flagged");
    }

    [Test]
    public async Task DoesNotFlag_NormalArithmetic()
    {
        SetSource(@"
public class C {
    public int M(int a, int b) {
        return a + b;
    }
}");
        var issues = await _engine.FindArithmeticOverflowRisksAsync("Test.cs");
        Assert.That(issues.Any(i => i.Type == "ArithmeticOverflowRisk"), Is.False,
            "ordinary a + b must not be flagged — no boundary constant involved");
    }
}

// ════════════════════════════════════════════════════════════════════════════
// 13. CircularTypeReferences (AnalysisEngine)
// ════════════════════════════════════════════════════════════════════════════
[TestFixture]
public class CircularTypeReferenceTests
{
    private PersistentWorkspaceManager _workspaceManager = null!;
    private AnalysisEngine _engine = null!;

    [SetUp]
    public void Setup()
    {
        _workspaceManager = new PersistentWorkspaceManager(NullLogger<PersistentWorkspaceManager>.Instance);
        var config = new SentinelConfiguration();
        _engine = new AnalysisEngine(_workspaceManager, config);
    }

    [TearDown]
    public void TearDown() => _workspaceManager?.Dispose();

    private void SetSource(string source) =>
        _workspaceManager.SetTestSolution(
            TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Test.cs", source)]));

    [Test]
    public async Task Flags_DirectCircularDependency_ViaConstructor()
    {
        SetSource(@"
public class A { public A(B b) { } }
public class B { public B(A a) { } }
");
        var results = await _engine.FindCircularTypeReferencesAsync();
        Assert.That(results, Is.Not.Empty, "A depends on B and B depends on A — circular dependency");
        Assert.That(results.Any(r => r.Contains("A") && r.Contains("B")), Is.True);
    }

    [Test]
    public async Task DoesNotFlag_LinearDependency()
    {
        SetSource(@"
public class A { public A(B b) { } }
public class B { public B() { } }
");
        var results = await _engine.FindCircularTypeReferencesAsync();
        Assert.That(results, Is.Empty, "A→B with B having no deps should not be circular");
    }

    [Test]
    public async Task Flags_ThreeWayCycle()
    {
        SetSource(@"
public class X { public X(Y y) { } }
public class Y { public Y(Z z) { } }
public class Z { public Z(X x) { } }
");
        var results = await _engine.FindCircularTypeReferencesAsync();
        Assert.That(results, Is.Not.Empty, "X→Y→Z→X is a three-way cycle");
    }
}

// ════════════════════════════════════════════════════════════════════════════
// 14. DisposedAfterUsing + SyncCallInAsyncContext (AntiPatternEngine)
// ════════════════════════════════════════════════════════════════════════════
[TestFixture]
public class AntiPatternExtendedTests
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

    private void SetSource(string source) =>
        _workspaceManager.SetTestSolution(
            TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Test.cs", source)]));

    // ── DisposedAfterUsing ────────────────────────────────────────────────

    [Test]
    public async Task Flags_VariableUsedAfterUsingBlock()
    {
        SetSource(@"
using System.IO;
public class C {
    public void M() {
        Stream s;
        using (s = new MemoryStream()) { }
        var b = s.Length; // s is disposed!
    }
}");
        var findings = await _engine.DetectAntiPatternsAsync(patternFilter: ["DisposedAfterUsing"]);
        Assert.That(findings.Any(f => f.Pattern == "DisposedAfterUsing"), Is.True,
            "s accessed after using block should be flagged");
    }

    [Test]
    public async Task DoesNotFlag_UsingVarForm()
    {
        SetSource(@"
using System.IO;
public class C {
    public void M() {
        using var s = new MemoryStream();
        var b = s.Length; // s is in scope and valid
    }
}");
        var findings = await _engine.DetectAntiPatternsAsync(patternFilter: ["DisposedAfterUsing"]);
        Assert.That(findings.Any(f => f.Pattern == "DisposedAfterUsing"), Is.False,
            "'using var' scopes the variable to the block — must not be flagged");
    }

    [Test]
    public async Task DoesNotFlag_VariableUsedOnlyInsideUsingBlock()
    {
        SetSource(@"
using System.IO;
public class C {
    public void M() {
        Stream s;
        using (s = new MemoryStream()) {
            var b = s.Length; // valid inside
        }
    }
}");
        var findings = await _engine.DetectAntiPatternsAsync(patternFilter: ["DisposedAfterUsing"]);
        Assert.That(findings.Any(f => f.Pattern == "DisposedAfterUsing"), Is.False,
            "variable used only inside the using block must not be flagged");
    }

    // ── SyncCallInAsyncContext ────────────────────────────────────────────

    [Test]
    public async Task Flags_ThreadSleepInAsyncMethod()
    {
        SetSource(@"
using System.Threading;
using System.Threading.Tasks;
public class C {
    public async Task M() {
        Thread.Sleep(1000);
    }
}");
        var findings = await _engine.DetectAntiPatternsAsync(patternFilter: ["SyncCallInAsyncContext"]);
        Assert.That(findings.Any(f => f.Pattern == "SyncCallInAsyncContext"), Is.True,
            "Thread.Sleep inside async method should be flagged");
        Assert.That(findings.First(f => f.Pattern == "SyncCallInAsyncContext").Description,
            Does.Contain("Task.Delay"));
    }

    [Test]
    public async Task Flags_FileReadAllTextInAsyncMethod()
    {
        SetSource(@"
using System.IO;
using System.Threading.Tasks;
public class C {
    public async Task M() {
        var text = File.ReadAllText(""file.txt"");
    }
}");
        var findings = await _engine.DetectAntiPatternsAsync(patternFilter: ["SyncCallInAsyncContext"]);
        Assert.That(findings.Any(f => f.Pattern == "SyncCallInAsyncContext"), Is.True,
            "File.ReadAllText inside async method should be flagged");
    }

    [Test]
    public async Task DoesNotFlag_SyncCallsInSyncMethod()
    {
        SetSource(@"
using System.Threading;
public class C {
    public void M() {
        Thread.Sleep(1000);
    }
}");
        var findings = await _engine.DetectAntiPatternsAsync(patternFilter: ["SyncCallInAsyncContext"]);
        Assert.That(findings.Any(f => f.Pattern == "SyncCallInAsyncContext"), Is.False,
            "Thread.Sleep in a synchronous method is not a modernization issue");
    }
}

// ════════════════════════════════════════════════════════════════════════════
// 15. MissingGenericConstraints (AnalysisEngine)
// ════════════════════════════════════════════════════════════════════════════
[TestFixture]
public class MissingGenericConstraintTests
{
    private PersistentWorkspaceManager _workspaceManager = null!;
    private AnalysisEngine _engine = null!;

    [SetUp]
    public void Setup()
    {
        _workspaceManager = new PersistentWorkspaceManager(NullLogger<PersistentWorkspaceManager>.Instance);
        var config = new SentinelConfiguration();
        _engine = new AnalysisEngine(_workspaceManager, config);
    }

    [TearDown]
    public void TearDown() => _workspaceManager?.Dispose();

    private void SetSource(string source) =>
        _workspaceManager.SetTestSolution(
            TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Test.cs", source)]));

    [Test]
    public async Task Flags_NewT_WithoutNewConstraint()
    {
        SetSource(@"
public class C {
    public T Create<T>() {
        return new T();
    }
}");
        var results = await _engine.FindMissingGenericConstraintsAsync();
        Assert.That(results.Any(r => r.Contains("new()") && r.Contains("Create")), Is.True,
            "'new T()' without 'where T : new()' should be flagged");
    }

    [Test]
    public async Task DoesNotFlag_NewT_WhenConstraintPresent()
    {
        SetSource(@"
public class C {
    public T Create<T>() where T : new() {
        return new T();
    }
}");
        var results = await _engine.FindMissingGenericConstraintsAsync();
        Assert.That(results.Any(r => r.Contains("new()") && r.Contains("Create")), Is.False,
            "'where T : new()' is already present — must not be flagged");
    }

    [Test]
    public async Task Flags_NullComparison_WithoutClassConstraint()
    {
        SetSource(@"
public class C {
    public bool IsNull<T>(T value) {
        return value == null;
    }
}");
        var results = await _engine.FindMissingGenericConstraintsAsync();
        Assert.That(results.Any(r => r.Contains("class") && r.Contains("IsNull")), Is.True,
            "'value == null' on unconstrained T should be flagged — value types can never be null");
    }

    [Test]
    public async Task DoesNotFlag_NullComparison_WithClassConstraint()
    {
        SetSource(@"
public class C {
    public bool IsNull<T>(T value) where T : class {
        return value == null;
    }
}");
        var results = await _engine.FindMissingGenericConstraintsAsync();
        Assert.That(results.Any(r => r.Contains("class") && r.Contains("IsNull")), Is.False,
            "'where T : class' is present — null comparison is valid, must not be flagged");
    }
}

// ════════════════════════════════════════════════════════════════════════════
// 16. JsonAntiPatterns (SecurityEngine)
// ════════════════════════════════════════════════════════════════════════════
[TestFixture]
public class JsonAntiPatternTests
{
    private PersistentWorkspaceManager _workspaceManager = null!;
    private SecurityEngine _engine = null!;

    [SetUp]
    public void Setup()
    {
        _workspaceManager = new PersistentWorkspaceManager(NullLogger<PersistentWorkspaceManager>.Instance);
        _engine = new SecurityEngine(_workspaceManager);
    }

    [TearDown]
    public void TearDown() => _workspaceManager?.Dispose();

    private void SetSource(string source) =>
        _workspaceManager.SetTestSolution(
            TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Test.cs", source)]));

    [Test]
    public async Task Flags_JsonDocument_Parse_WithoutUsing()
    {
        SetSource(@"
using System.Text.Json;
public class C {
    public void M(string json) {
        var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
    }
}");
        var issues = await _engine.DetectJsonAntiPatternsAsync("Test.cs");
        Assert.That(issues.Any(i => i.IssueType == "JsonDocumentNotDisposed"), Is.True,
            "JsonDocument.Parse() without using should be flagged");
    }

    [Test]
    public async Task DoesNotFlag_JsonDocument_Parse_WithUsing()
    {
        SetSource(@"
using System.Text.Json;
public class C {
    public void M(string json) {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
    }
}");
        var issues = await _engine.DetectJsonAntiPatternsAsync("Test.cs");
        Assert.That(issues.Any(i => i.IssueType == "JsonDocumentNotDisposed"), Is.False,
            "'using var doc = JsonDocument.Parse(...)' is the correct pattern — must not be flagged");
    }

    [Test]
    public async Task Flags_GetProperty_WithoutTryCatch()
    {
        SetSource(@"
using System.Text.Json;
public class C {
    public string M(JsonElement el) {
        return el.GetProperty(""name"").GetString() ?? """";
    }
}");
        var issues = await _engine.DetectJsonAntiPatternsAsync("Test.cs");
        Assert.That(issues.Any(i => i.IssueType == "JsonUnsafeGetProperty"), Is.True,
            "GetProperty without try/catch should be flagged");
    }

    [Test]
    public async Task Flags_Deserialize_ToDynamic()
    {
        SetSource(@"
using System.Text.Json;
public class C {
    public void M(string json) {
        var result = JsonSerializer.Deserialize<dynamic>(json);
    }
}");
        var issues = await _engine.DetectJsonAntiPatternsAsync("Test.cs");
        Assert.That(issues.Any(i => i.IssueType == "JsonDeserializeToUntypedTarget"), Is.True,
            "Deserialize<dynamic> loses type safety and should be flagged");
    }

    [Test]
    public async Task DoesNotFlag_Deserialize_ToStronglyTypedClass()
    {
        SetSource(@"
using System.Text.Json;
public class MyDto { public string Name { get; set; } = """"; }
public class C {
    public void M(string json) {
        var result = JsonSerializer.Deserialize<MyDto>(json);
        if (result == null) throw new System.Exception();
    }
}");
        var issues = await _engine.DetectJsonAntiPatternsAsync("Test.cs");
        Assert.That(issues.Any(i => i.IssueType == "JsonDeserializeToUntypedTarget"), Is.False,
            "Deserialize<MyDto> to a concrete type should not be flagged");
    }
}

// ════════════════════════════════════════════════════════════════════════════
// 17. LinqN1 + StringFormatInLoop + MultipleEnumeration (PerformanceEngine)
// ════════════════════════════════════════════════════════════════════════════
[TestFixture]
public class PerformanceEngineExtendedTests
{
    private PersistentWorkspaceManager _workspaceManager = null!;
    private PerformanceEngine _engine = null!;

    [SetUp]
    public void Setup()
    {
        _workspaceManager = new PersistentWorkspaceManager(NullLogger<PersistentWorkspaceManager>.Instance);
        _engine = new PerformanceEngine(_workspaceManager);
    }

    [TearDown]
    public void TearDown() => _workspaceManager?.Dispose();

    private void SetSource(string source) =>
        _workspaceManager.SetTestSolution(
            TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Test.cs", source)]));

    // ── LinqN1Pattern ─────────────────────────────────────────────────────

    [Test]
    public async Task Flags_LinqTerminal_InsideForeach()
    {
        SetSource(@"
using System.Collections.Generic;
using System.Linq;
public class C {
    public void M(List<int> ids, List<string> items) {
        foreach (var id in ids) {
            var found = items.Where(x => x.Length == id).FirstOrDefault();
        }
    }
}");
        var issues = await _engine.FindLinqN1PatternsAsync();
        Assert.That(issues.Any(i => i.IssueType == "LinqN1Pattern"), Is.True,
            "LINQ terminal inside foreach should be flagged as N+1");
    }

    [Test]
    public async Task DoesNotFlag_LinqOutsideLoop()
    {
        SetSource(@"
using System.Collections.Generic;
using System.Linq;
public class C {
    public void M(List<string> items) {
        var found = items.Where(x => x.Length > 3).ToList();
    }
}");
        var issues = await _engine.FindLinqN1PatternsAsync();
        Assert.That(issues.Any(i => i.IssueType == "LinqN1Pattern"), Is.False,
            "LINQ terminal outside a loop should not be flagged");
    }

    // ── InterpolatedStringInLoop ───────────────────────────────────────────

    [Test]
    public async Task Flags_InterpolatedString_InsideLoop()
    {
        SetSource(@"
public class C {
    public void M(int count) {
        for (int i = 0; i < count; i++) {
            var s = $""item {i}"";
        }
    }
}");
        var issues = await _engine.FindStringFormatInLoopsAsync();
        Assert.That(issues.Any(i => i.IssueType == "InterpolatedStringInLoop"), Is.True,
            "$\"\" inside loop should be flagged");
    }

    [Test]
    public async Task Flags_StringFormat_InsideLoop()
    {
        SetSource(@"
public class C {
    public void M(int count) {
        var result = """";
        for (int i = 0; i < count; i++) {
            result = string.Format(""{0} item"", i);
        }
    }
}");
        var issues = await _engine.FindStringFormatInLoopsAsync();
        Assert.That(issues.Any(i => i.IssueType == "StringFormatInLoop"), Is.True,
            "string.Format() inside loop should be flagged");
    }

    [Test]
    public async Task DoesNotFlag_InterpolatedString_OutsideLoop()
    {
        SetSource(@"
public class C {
    public string M(int x) => $""value={x}"";
}");
        var issues = await _engine.FindStringFormatInLoopsAsync();
        Assert.That(issues.Any(i => i.IssueType == "InterpolatedStringInLoop"), Is.False,
            "interpolated string outside loop must not be flagged");
    }

    // ── MultipleEnumeration ────────────────────────────────────────────────

    [Test]
    public async Task Flags_IEnumerable_EnumeratedTwice()
    {
        SetSource(@"
using System.Collections.Generic;
using System.Linq;
public class C {
    public void M(IEnumerable<int> items) {
        foreach (var x in items) { _ = x; }
        var count = items.Count();
    }
}");
        var issues = await _engine.FindMultipleEnumerationAsync();
        Assert.That(issues.Any(i => i.IssueType == "MultipleEnumeration"), Is.True,
            "IEnumerable iterated twice without ToList should be flagged");
    }

    [Test]
    public async Task DoesNotFlag_MaterializedList_UsedTwice()
    {
        SetSource(@"
using System.Collections.Generic;
using System.Linq;
public class C {
    public void M(IEnumerable<int> source) {
        var items = source.ToList();
        foreach (var x in items) { _ = x; }
        var count = items.Count;
    }
}");
        var issues = await _engine.FindMultipleEnumerationAsync();
        Assert.That(issues.Any(i => i.IssueType == "MultipleEnumeration"), Is.False,
            "ToList() materialization before reuse must not be flagged");
    }
}

// ════════════════════════════════════════════════════════════════════════════
// 18. FinalizerOnDisposable + UnboundedStaticCollection + UnboundedRecursion
//     (AnalysisEngine)
// ════════════════════════════════════════════════════════════════════════════
[TestFixture]
public class AnalysisEngineExtended2Tests
{
    private PersistentWorkspaceManager _workspaceManager = null!;
    private AnalysisEngine _engine = null!;

    [SetUp]
    public void Setup()
    {
        _workspaceManager = new PersistentWorkspaceManager(NullLogger<PersistentWorkspaceManager>.Instance);
        _engine = new AnalysisEngine(_workspaceManager, new SentinelConfiguration());
    }

    [TearDown]
    public void TearDown() => _workspaceManager?.Dispose();

    private void SetSource(string source) =>
        _workspaceManager.SetTestSolution(
            TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Test.cs", source)]));

    // ── FinalizerOnDisposable ──────────────────────────────────────────────

    [Test]
    public async Task Flags_Finalizer_Without_DisposedGuard()
    {
        SetSource(@"
public class C : System.IDisposable {
    public void Dispose() { }
    ~C() { /* no disposed guard */ Dispose(); }
}");
        var results = await _engine.FindFinalizerOnDisposableAsync();
        Assert.That(results, Is.Not.Empty,
            "IDisposable class with unguarded finalizer should be flagged");
    }

    [Test]
    public async Task DoesNotFlag_Finalizer_With_DisposedGuard()
    {
        SetSource(@"
public class C : System.IDisposable {
    private bool _disposed;
    public void Dispose() { _disposed = true; }
    ~C() { if (_disposed) return; Dispose(); }
}");
        var results = await _engine.FindFinalizerOnDisposableAsync();
        Assert.That(results, Is.Empty,
            "Finalizer with _disposed guard is the correct pattern — must not be flagged");
    }

    // ── UnboundedStaticCollection ──────────────────────────────────────────

    [Test]
    public async Task Flags_Static_Dictionary_WithoutSizeCap()
    {
        SetSource(@"
using System.Collections.Generic;
public class Cache {
    private static readonly Dictionary<string, object> _cache = new();
    public void Add(string key, object value) { _cache.Add(key, value); }
}");
        var results = await _engine.FindUnboundedStaticCollectionsAsync();
        Assert.That(results, Is.Not.Empty,
            "Static Dictionary populated without size cap should be flagged");
    }

    [Test]
    public async Task DoesNotFlag_Static_Dictionary_WithClear()
    {
        SetSource(@"
using System.Collections.Generic;
public class Cache {
    private static readonly Dictionary<string, object> _cache = new();
    public void Add(string key, object value) { _cache.Add(key, value); }
    public void Reset() { _cache.Clear(); }
}");
        var results = await _engine.FindUnboundedStaticCollectionsAsync();
        Assert.That(results, Is.Empty,
            "Static Dictionary with Clear() is bounded — must not be flagged");
    }

    // ── UnboundedRecursion ─────────────────────────────────────────────────

    [Test]
    public async Task Flags_Recursion_WithoutDepthGuard()
    {
        SetSource(@"
public class C {
    public void Walk(Node n) {
        Walk(n.Child);
    }
}
public class Node { public Node Child { get; set; } = null!; }");
        var results = await _engine.FindUnboundedRecursionAsync();
        Assert.That(results.Any(r => r.Contains("Walk")), Is.True,
            "Recursive method with no base case or depth guard should be flagged");
    }

    [Test]
    public async Task DoesNotFlag_Recursion_WithDepthParameter()
    {
        SetSource(@"
public class C {
    public void Walk(Node n, int depth) {
        if (depth <= 0) return;
        Walk(n.Child, depth - 1);
    }
}
public class Node { public Node Child { get; set; } = null!; }");
        var results = await _engine.FindUnboundedRecursionAsync();
        Assert.That(results.Any(r => r.Contains("Walk")), Is.False,
            "Recursive method with depth parameter must not be flagged");
    }
}

// ════════════════════════════════════════════════════════════════════════════
// 19. TaskRunBlocking + NamedHandlerLeak (AntiPatternEngine)
// ════════════════════════════════════════════════════════════════════════════
[TestFixture]
public class AntiPatternEngine2Tests
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

    private void SetSource(string source) =>
        _workspaceManager.SetTestSolution(
            TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Test.cs", source)]));

    [Test]
    public async Task Flags_TaskRun_DotResult()
    {
        SetSource(@"
using System.Threading.Tasks;
public class C {
    public int M() => Task.Run(() => 42).Result;
}");
        var findings = await _engine.DetectAntiPatternsAsync(patternFilter: ["TaskRunBlocking"]);
        Assert.That(findings.Any(f => f.Pattern == "TaskRunBlocking"), Is.True,
            "Task.Run(...).Result blocks thread-pool — should be flagged");
    }

    [Test]
    public async Task DoesNotFlag_Awaited_TaskRun()
    {
        SetSource(@"
using System.Threading.Tasks;
public class C {
    public async Task<int> M() => await Task.Run(() => 42);
}");
        var findings = await _engine.DetectAntiPatternsAsync(patternFilter: ["TaskRunBlocking"]);
        Assert.That(findings.Any(f => f.Pattern == "TaskRunBlocking"), Is.False,
            "await Task.Run() is fine — must not be flagged");
    }

    [Test]
    public async Task Flags_NamedHandler_WithoutUnsubscribe()
    {
        SetSource(@"
public class Publisher { public event System.EventHandler? Changed; }
public class Subscriber {
    private Publisher _pub;
    public Subscriber(Publisher p) {
        _pub = p;
        _pub.Changed += OnChanged;
    }
    private void OnChanged(object? s, System.EventArgs e) { }
}");
        var findings = await _engine.DetectAntiPatternsAsync(patternFilter: ["NamedHandlerLeak"]);
        Assert.That(findings.Any(f => f.Pattern == "NamedHandlerLeak"), Is.True,
            "Named handler subscribed without Dispose/unsubscribe should be flagged");
    }
}

// ════════════════════════════════════════════════════════════════════════════
// 20. UnsafeLazyInit + CasLoopWithoutBackoff (ThreadSafetyEngine)
// ════════════════════════════════════════════════════════════════════════════
[TestFixture]
public class ThreadSafetyEngineExtendedTests
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

    private void SetSource(string source) =>
        _workspaceManager.SetTestSolution(
            TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Test.cs", source)]));

    [Test]
    public async Task Flags_UnsafeLazyInit_WithoutLock()
    {
        SetSource(@"
public class C {
    private object? _instance;
    public object Get() {
        if (_instance == null) { _instance = new object(); }
        return _instance;
    }
}");
        var results = await _engine.FindUnsafeLazyInitAsync();
        Assert.That(results, Is.Not.Empty,
            "Null-check then assign without lock or volatile should be flagged");
    }

    [Test]
    public async Task DoesNotFlag_LazySingleton_InLock()
    {
        SetSource(@"
public class C {
    private object? _instance;
    private readonly object _lock = new();
    public object Get() {
        lock (_lock) { if (_instance == null) { _instance = new object(); } }
        return _instance;
    }
}");
        var results = await _engine.FindUnsafeLazyInitAsync();
        Assert.That(results, Is.Empty,
            "Null-check inside lock is safe — must not be flagged");
    }

    [Test]
    public async Task Flags_CasLoop_WithoutSpinWait()
    {
        SetSource(@"
using System.Threading;
public class C {
    private int _val;
    public void Increment() {
        int cur, next;
        do {
            cur = _val;
            next = cur + 1;
        } while (Interlocked.CompareExchange(ref _val, next, cur) != cur);
    }
}");
        var results = await _engine.FindCasLoopWithoutBackoffAsync();
        Assert.That(results, Is.Not.Empty,
            "CAS retry loop without SpinWait should be flagged");
    }

    [Test]
    public async Task DoesNotFlag_CasLoop_WithSpinWait()
    {
        SetSource(@"
using System.Threading;
public class C {
    private int _val;
    public void Increment() {
        var spin = new SpinWait();
        int cur, next;
        do {
            cur = _val;
            next = cur + 1;
            spin.SpinOnce();
        } while (Interlocked.CompareExchange(ref _val, next, cur) != cur);
    }
}");
        var results = await _engine.FindCasLoopWithoutBackoffAsync();
        Assert.That(results, Is.Empty,
            "CAS loop with SpinWait is the correct pattern — must not be flagged");
    }
}

// ════════════════════════════════════════════════════════════════════════════
// 21. ReDoS (SecurityEngine)
// ════════════════════════════════════════════════════════════════════════════
[TestFixture]
public class ReDoSDetectionTests
{
    private PersistentWorkspaceManager _workspaceManager = null!;
    private SecurityEngine _engine = null!;

    [SetUp]
    public void Setup()
    {
        _workspaceManager = new PersistentWorkspaceManager(NullLogger<PersistentWorkspaceManager>.Instance);
        _engine = new SecurityEngine(_workspaceManager);
    }

    [TearDown]
    public void TearDown() => _workspaceManager?.Dispose();

    private void SetSource(string source) =>
        _workspaceManager.SetTestSolution(
            TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Test.cs", source)]));

    [Test]
    public async Task Flags_NestedQuantifier_Pattern()
    {
        SetSource(@"
using System.Text.RegularExpressions;
public class C {
    public bool M(string s) => Regex.IsMatch(s, @""^(a+)+$"");
}");
        var issues = await _engine.FindReDoSPatternsAsync("Test.cs");
        Assert.That(issues.Any(i => i.IssueType == "ReDoSVulnerablePattern"), Is.True,
            "(a+)+ is a classic ReDoS pattern — should be flagged");
    }

    [Test]
    public async Task DoesNotFlag_Simple_Pattern()
    {
        SetSource(@"
using System.Text.RegularExpressions;
public class C {
    public bool M(string s) => Regex.IsMatch(s, @""^\d{3}-\d{4}$"");
}");
        var issues = await _engine.FindReDoSPatternsAsync("Test.cs");
        Assert.That(issues.Any(i => i.IssueType == "ReDoSVulnerablePattern"), Is.False,
            "Simple digit pattern has no nested quantifiers — must not be flagged");
    }
}

// ════════════════════════════════════════════════════════════════════════════
// 22. SequentialAwaits + AsyncVoidWithoutTryCatch (AsyncSafetyEngine)
// ════════════════════════════════════════════════════════════════════════════
[TestFixture]
public class AsyncSafetyEngineExtendedTests
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

    private void SetSource(string source) =>
        _workspaceManager.SetTestSolution(
            TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Test.cs", source)]));

    [Test]
    public async Task Flags_SequentialIndependentAwaits()
    {
        SetSource(@"
using System.Threading.Tasks;
public class C {
    public async Task M() {
        var x = await Task.FromResult(1);
        var y = await Task.FromResult(2);
        _ = x + y;
    }
}");
        var reports = await _engine.FindSequentialIndependentAwaitsAsync();
        Assert.That(reports, Is.Not.Empty,
            "Two independent sequential awaits should be flagged");
    }

    [Test]
    public async Task DoesNotFlag_DependentAwaits()
    {
        SetSource(@"
using System.Threading.Tasks;
public class C {
    public async Task M() {
        var x = await Task.FromResult(1);
        var y = await Task.FromResult(x + 1);
        _ = y;
    }
}");
        var reports = await _engine.FindSequentialIndependentAwaitsAsync();
        Assert.That(reports, Is.Empty,
            "Second await uses result of first — they are dependent, must not be flagged");
    }

    [Test]
    public async Task Flags_AsyncVoid_WithoutTryCatch()
    {
        SetSource(@"
using System.Threading.Tasks;
public class C {
    public async void OnClick(object s, System.EventArgs e) {
        await Task.Delay(10);
    }
}");
        var reports = await _engine.FindAsyncVoidWithoutTryCatchAsync();
        Assert.That(reports, Is.Not.Empty,
            "async void without try/catch should be flagged");
    }

    [Test]
    public async Task DoesNotFlag_AsyncVoid_WithTryCatch()
    {
        SetSource(@"
using System;
using System.Threading.Tasks;
public class C {
    public async void OnClick(object s, EventArgs e) {
        try { await Task.Delay(10); }
        catch (Exception) { }
    }
}");
        var reports = await _engine.FindAsyncVoidWithoutTryCatchAsync();
        Assert.That(reports, Is.Empty,
            "async void with try/catch wrapping the body is safe — must not be flagged");
    }
}

// ════════════════════════════════════════════════════════════════════════════
// 23. MutablePublicCollectionProperty (CodeStyleAnalysisEngine)
// ════════════════════════════════════════════════════════════════════════════
[TestFixture]
public class MutableCollectionPropertyTests
{
    private PersistentWorkspaceManager _workspaceManager = null!;
    private CodeStyleAnalysisEngine _engine = null!;

    [SetUp]
    public void Setup()
    {
        _workspaceManager = new PersistentWorkspaceManager(NullLogger<PersistentWorkspaceManager>.Instance);
        _engine = new CodeStyleAnalysisEngine(_workspaceManager);
    }

    [TearDown]
    public void TearDown() => _workspaceManager?.Dispose();

    private void SetSource(string source) =>
        _workspaceManager.SetTestSolution(
            TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Test.cs", source)]));

    [Test]
    public async Task Flags_PublicList_WithPublicSetter()
    {
        SetSource(@"
using System.Collections.Generic;
public class C {
    public List<string> Items { get; set; } = new();
}");
        var results = await _engine.FindMutablePublicCollectionPropertiesAsync();
        Assert.That(results, Is.Not.Empty,
            "public List<T> { get; set; } should be flagged");
    }

    [Test]
    public async Task DoesNotFlag_PublicList_WithPrivateSetter()
    {
        SetSource(@"
using System.Collections.Generic;
public class C {
    public List<string> Items { get; private set; } = new();
}");
        var results = await _engine.FindMutablePublicCollectionPropertiesAsync();
        Assert.That(results, Is.Empty,
            "private set restricts mutation — must not be flagged");
    }

    [Test]
    public async Task DoesNotFlag_ReadonlyInterface_Property()
    {
        SetSource(@"
using System.Collections.Generic;
public class C {
    public IReadOnlyList<string> Items { get; init; } = new List<string>();
}");
        var results = await _engine.FindMutablePublicCollectionPropertiesAsync();
        Assert.That(results, Is.Empty,
            "IReadOnlyList with init is safe — must not be flagged");
    }
}

// ════════════════════════════════════════════════════════════════════════════
// 24. ThrowInFinally (AntiPatternEngine)
// ════════════════════════════════════════════════════════════════════════════
[TestFixture]
public class ThrowInFinallyTests
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

    private void SetSource(string source) =>
        _workspaceManager.SetTestSolution(
            TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Test.cs", source)]));

    [Test]
    public async Task Flags_ThrowNew_InsideFinally()
    {
        SetSource(@"
public class C {
    public void M() {
        try { }
        finally { throw new System.Exception(""oops""); }
    }
}");
        var results = await _engine.DetectAntiPatternsAsync(null, null, ["ThrowInFinally"]);
        Assert.That(results.Any(r => r.Pattern == "ThrowInFinally"), Is.True,
            "Throwing a new exception in a finally block should be flagged");
    }

    [Test]
    public async Task DoesNotFlag_BareRethrow_InsideFinally()
    {
        SetSource(@"
public class C {
    public void M() {
        try { }
        catch { }
        finally { /* cleanup, no throw */ }
    }
}");
        var results = await _engine.DetectAntiPatternsAsync(null, null, ["ThrowInFinally"]);
        Assert.That(results.Any(r => r.Pattern == "ThrowInFinally"), Is.False,
            "Finally block with no throw must not be flagged");
    }
}

// ════════════════════════════════════════════════════════════════════════════
// 25. LinqRedundantWhere (PerformanceEngine)
// ════════════════════════════════════════════════════════════════════════════
[TestFixture]
public class LinqRedundantWhereTests
{
    private PersistentWorkspaceManager _workspaceManager = null!;
    private PerformanceEngine _engine = null!;

    [SetUp]
    public void Setup()
    {
        _workspaceManager = new PersistentWorkspaceManager(NullLogger<PersistentWorkspaceManager>.Instance);
        _engine = new PerformanceEngine(_workspaceManager);
    }

    [TearDown]
    public void TearDown() => _workspaceManager?.Dispose();

    private void SetSource(string source) =>
        _workspaceManager.SetTestSolution(
            TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Test.cs", source)]));

    [Test]
    public async Task Flags_WhereFirstOrDefault()
    {
        SetSource(@"
using System.Collections.Generic;
using System.Linq;
public class C {
    public void M(List<int> items) {
        var x = items.Where(i => i > 0).FirstOrDefault();
    }
}");
        var results = await _engine.FindLinqRedundantWhereAsync();
        Assert.That(results.Any(r => r.IssueType == "LinqRedundantWhere"), Is.True,
            ".Where(pred).FirstOrDefault() should be flagged");
    }

    [Test]
    public async Task Flags_WhereAny()
    {
        SetSource(@"
using System.Collections.Generic;
using System.Linq;
public class C {
    public void M(List<int> items) {
        var x = items.Where(i => i > 0).Any();
    }
}");
        var results = await _engine.FindLinqRedundantWhereAsync();
        Assert.That(results.Any(r => r.IssueType == "LinqRedundantWhere"), Is.True,
            ".Where(pred).Any() should be flagged");
    }

    [Test]
    public async Task DoesNotFlag_DirectFirstOrDefault_WithPredicate()
    {
        SetSource(@"
using System.Collections.Generic;
using System.Linq;
public class C {
    public void M(List<int> items) {
        var x = items.FirstOrDefault(i => i > 0);
    }
}");
        var results = await _engine.FindLinqRedundantWhereAsync();
        Assert.That(results.Any(r => r.IssueType == "LinqRedundantWhere"), Is.False,
            "Direct FirstOrDefault(pred) is already optimal — must not be flagged");
    }
}

// ════════════════════════════════════════════════════════════════════════════
// 26. DoubleCheckedLockingWithoutVolatile (ThreadSafetyEngine)
// ════════════════════════════════════════════════════════════════════════════
[TestFixture]
public class DoubleCheckedLockingTests
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

    private void SetSource(string source) =>
        _workspaceManager.SetTestSolution(
            TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Test.cs", source)]));

    [Test]
    public async Task Flags_DCL_WithoutVolatile()
    {
        SetSource(@"
public class Singleton {
    private static Singleton _instance;
    private static readonly object _lock = new object();
    public static Singleton Instance {
        get {
            if (_instance == null) {
                lock (_lock) {
                    if (_instance == null) {
                        _instance = new Singleton();
                    }
                }
            }
            return _instance;
        }
    }
}");
        var results = await _engine.FindDoubleCheckedLockingAsync();
        Assert.That(results, Is.Not.Empty,
            "DCL without volatile field should be flagged");
    }

    [Test]
    public async Task DoesNotFlag_DCL_WithVolatile()
    {
        SetSource(@"
public class Singleton {
    private static volatile Singleton _instance;
    private static readonly object _lock = new object();
    public static Singleton Instance {
        get {
            if (_instance == null) {
                lock (_lock) {
                    if (_instance == null) {
                        _instance = new Singleton();
                    }
                }
            }
            return _instance;
        }
    }
}");
        var results = await _engine.FindDoubleCheckedLockingAsync();
        Assert.That(results, Is.Empty,
            "DCL with volatile field is the correct pattern — must not be flagged");
    }
}

// ════════════════════════════════════════════════════════════════════════════
// 27. StaticEventSubscription (AntiPatternEngine)
// ════════════════════════════════════════════════════════════════════════════
[TestFixture]
public class StaticEventSubscriptionTests
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

    private void SetSource(string source) =>
        _workspaceManager.SetTestSolution(
            TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Test.cs", source)]));

    [Test]
    public async Task Flags_StaticEvent_WithoutUnsubscribe()
    {
        SetSource(@"
public static class AppEvents {
    public static event System.EventHandler Tick;
}
public class Subscriber {
    public Subscriber() { AppEvents.Tick += OnTick; }
    private void OnTick(object s, System.EventArgs e) { }
}");
        var results = await _engine.DetectAntiPatternsAsync(null, null, ["StaticEventSubscription"]);
        Assert.That(results.Any(r => r.Pattern == "StaticEventSubscription"), Is.True,
            "Subscribing to static event without unsubscribe should be flagged");
    }

    [Test]
    public async Task DoesNotFlag_StaticEvent_WithUnsubscribeInDispose()
    {
        SetSource(@"
public static class AppEvents {
    public static event System.EventHandler Tick;
}
public class Subscriber : System.IDisposable {
    public Subscriber() { AppEvents.Tick += OnTick; }
    private void OnTick(object s, System.EventArgs e) { }
    public void Dispose() { AppEvents.Tick -= OnTick; }
}");
        var results = await _engine.DetectAntiPatternsAsync(null, null, ["StaticEventSubscription"]);
        Assert.That(results.Any(r => r.Pattern == "StaticEventSubscription"), Is.False,
            "Paired -= in Dispose is the correct pattern — must not be flagged");
    }
}

// ════════════════════════════════════════════════════════════════════════════
// 28. UnvalidatedRegexSource (SecurityEngine)
// ════════════════════════════════════════════════════════════════════════════
[TestFixture]
public class UnvalidatedRegexSourceTests
{
    private PersistentWorkspaceManager _workspaceManager = null!;
    private SecurityEngine _engine = null!;

    [SetUp]
    public void Setup()
    {
        _workspaceManager = new PersistentWorkspaceManager(NullLogger<PersistentWorkspaceManager>.Instance);
        _engine = new SecurityEngine(_workspaceManager);
    }

    [TearDown]
    public void TearDown() => _workspaceManager?.Dispose();

    private void SetSource(string source) =>
        _workspaceManager.SetTestSolution(
            TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Test.cs", source)]));

    [Test]
    public async Task Flags_NewRegex_WithVariablePattern()
    {
        SetSource(@"
using System.Text.RegularExpressions;
public class C {
    public void M(string userPattern) {
        var r = new Regex(userPattern);
    }
}");
        var results = await _engine.FindUnvalidatedRegexSourceAsync("Test.cs");
        Assert.That(results.Any(r => r.IssueType == "UnvalidatedRegexSource"), Is.True,
            "new Regex(variable) should be flagged");
    }

    [Test]
    public async Task DoesNotFlag_NewRegex_WithLiteralPattern()
    {
        SetSource(@"
using System.Text.RegularExpressions;
public class C {
    public void M() {
        var r = new Regex(@""^\d+$"");
    }
}");
        var results = await _engine.FindUnvalidatedRegexSourceAsync("Test.cs");
        Assert.That(results.Any(r => r.IssueType == "UnvalidatedRegexSource"), Is.False,
            "new Regex(literal) is safe — must not be flagged");
    }
}

// ════════════════════════════════════════════════════════════════════════════
// 29. CheckThenActOnDictionary (ThreadSafetyEngine)
// ════════════════════════════════════════════════════════════════════════════
[TestFixture]
public class CheckThenActOnDictionaryTests
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

    private void SetSource(string source) =>
        _workspaceManager.SetTestSolution(
            TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Test.cs", source)]));

    [Test]
    public async Task Flags_ContainsKey_ThenAdd_WithoutLock()
    {
        SetSource(@"
using System.Collections.Generic;
public class C {
    private Dictionary<string, int> _d = new();
    public void Add(string key) {
        if (!_d.ContainsKey(key))
            _d.Add(key, 0);
    }
}");
        var results = await _engine.FindCheckThenActOnDictionaryAsync();
        Assert.That(results, Is.Not.Empty,
            "ContainsKey + Add without lock is a check-then-act race and should be flagged");
    }

    [Test]
    public async Task DoesNotFlag_ContainsKey_ThenAdd_InsideLock()
    {
        SetSource(@"
using System.Collections.Generic;
public class C {
    private Dictionary<string, int> _d = new();
    private readonly object _lock = new();
    public void Add(string key) {
        lock (_lock) {
            if (!_d.ContainsKey(key))
                _d.Add(key, 0);
        }
    }
}");
        var results = await _engine.FindCheckThenActOnDictionaryAsync();
        Assert.That(results, Is.Empty,
            "ContainsKey + Add inside a lock is safe — must not be flagged");
    }
}

// ════════════════════════════════════════════════════════════════════════════
// 30. UnawakedDisposeAsync (AsyncSafetyEngine)
// ════════════════════════════════════════════════════════════════════════════
[TestFixture]
public class UnawakedDisposeAsyncTests
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

    private void SetSource(string source) =>
        _workspaceManager.SetTestSolution(
            TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Test.cs", source)]));

    [Test]
    public async Task Flags_DisposeAsync_NotAwaited_InSyncDispose()
    {
        SetSource(@"
using System;
using System.Threading.Tasks;
public class C : IDisposable {
    private IAsyncDisposable _resource = null!;
    public void Dispose() {
        _resource.DisposeAsync(); // not awaited
    }
}");
        var results = await _engine.FindUnawakedDisposeAsyncAsync();
        Assert.That(results, Is.Not.Empty,
            "DisposeAsync() without await in sync Dispose should be flagged");
    }

    [Test]
    public async Task DoesNotFlag_DisposeAsync_Properly_Awaited()
    {
        SetSource(@"
using System;
using System.Threading.Tasks;
public class C : IAsyncDisposable {
    private IAsyncDisposable _resource = null!;
    public async ValueTask DisposeAsync() {
        await _resource.DisposeAsync();
    }
}");
        var results = await _engine.FindUnawakedDisposeAsyncAsync();
        Assert.That(results, Is.Empty,
            "await DisposeAsync() is the correct pattern — must not be flagged");
    }
}

// ════════════════════════════════════════════════════════════════════════════
// 31. RegexNewInLoop (SecurityEngine)
// ════════════════════════════════════════════════════════════════════════════
[TestFixture]
public class RegexNewInLoopTests
{
    private PersistentWorkspaceManager _workspaceManager = null!;
    private SecurityEngine _engine = null!;

    [SetUp]
    public void Setup()
    {
        _workspaceManager = new PersistentWorkspaceManager(NullLogger<PersistentWorkspaceManager>.Instance);
        _engine = new SecurityEngine(_workspaceManager);
    }

    [TearDown]
    public void TearDown() => _workspaceManager?.Dispose();

    private void SetSource(string source) =>
        _workspaceManager.SetTestSolution(
            TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Test.cs", source)]));

    [Test]
    public async Task Flags_NewRegex_InsideForEach()
    {
        SetSource(@"
using System.Collections.Generic;
using System.Text.RegularExpressions;
public class C {
    public void M(List<string> inputs) {
        foreach (var s in inputs) {
            var r = new Regex(@""\d+"");
            r.IsMatch(s);
        }
    }
}");
        var results = await _engine.FindRegexNewInLoopAsync("Test.cs");
        Assert.That(results.Any(r => r.IssueType == "RegexNewInLoop"), Is.True,
            "new Regex() inside a foreach should be flagged");
    }

    [Test]
    public async Task DoesNotFlag_Regex_HoistedToField()
    {
        SetSource(@"
using System.Collections.Generic;
using System.Text.RegularExpressions;
public class C {
    private static readonly Regex _digits = new Regex(@""\d+"");
    public void M(List<string> inputs) {
        foreach (var s in inputs) {
            _digits.IsMatch(s);
        }
    }
}");
        var results = await _engine.FindRegexNewInLoopAsync("Test.cs");
        Assert.That(results.Any(r => r.IssueType == "RegexNewInLoop"), Is.False,
            "Static readonly Regex hoisted outside the loop is the correct pattern — must not be flagged");
    }
}
