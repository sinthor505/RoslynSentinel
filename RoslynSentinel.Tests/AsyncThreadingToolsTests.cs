using Microsoft.Extensions.Logging.Abstractions;
using RoslynSentinel.Server;

#pragma warning disable CS8618
namespace RoslynSentinel.Tests;

[TestFixture]
public class AsyncThreadingToolsTests
{
    private PersistentWorkspaceManager _workspaceManager;
    private AsyncSafetyEngine _asyncSafetyEngine;
    private AsyncOptimizationEngine _asyncOptimizationEngine;
    private ThreadSafetyEngine _threadSafetyEngine;

    [SetUp]
    public void Setup()
    {
        _workspaceManager = new PersistentWorkspaceManager(NullLogger<PersistentWorkspaceManager>.Instance);
        _asyncSafetyEngine = new AsyncSafetyEngine(_workspaceManager);
        _asyncOptimizationEngine = new AsyncOptimizationEngine(_workspaceManager);
        _threadSafetyEngine = new ThreadSafetyEngine(_workspaceManager);
    }

    [TearDown]
    public void TearDown() => _workspaceManager?.Dispose();

    private void SetSource(string source, string fileName = "Test.cs")
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [(fileName, source)]);
        _workspaceManager.SetTestSolution(solution);
    }

    // --- FindConfigureAwaitMissing ---

    [Test]
    public async Task FindConfigureAwaitMissing_Flags_AwaitWithoutConfigureAwait()
    {
        SetSource("public class MyLib { public async System.Threading.Tasks.Task DoWork() { await System.Threading.Tasks.Task.Delay(100); } }", "MyLib.cs");
        var reports = await _asyncSafetyEngine.FindConfigureAwaitMissingAsync("MyLib.cs");
        Assert.That(reports, Is.Not.Empty);
        Assert.That(reports[0].Reason, Does.Contain("ConfigureAwait"));
    }

    [Test]
    public async Task FindConfigureAwaitMissing_DoesNotFlag_AlreadyHasConfigureAwait()
    {
        SetSource("public class MyLib { public async System.Threading.Tasks.Task DoWork() { await System.Threading.Tasks.Task.Delay(100).ConfigureAwait(false); } }", "MyLib.cs");
        var reports = await _asyncSafetyEngine.FindConfigureAwaitMissingAsync("MyLib.cs");
        Assert.That(reports, Is.Empty);
    }

    [Test]
    public async Task FindConfigureAwaitMissing_DoesNotFlag_ControllerClass()
    {
        SetSource("public class HomeController { public async System.Threading.Tasks.Task DoWork() { await System.Threading.Tasks.Task.Delay(100); } }", "HomeController.cs");
        var reports = await _asyncSafetyEngine.FindConfigureAwaitMissingAsync("HomeController.cs");
        Assert.That(reports, Is.Empty);
    }

    // --- FindBlockingCallsInAsync ---

    [Test]
    public async Task FindBlockingCalls_Flags_ThreadSleep_InAsync()
    {
        SetSource("public class C { public async System.Threading.Tasks.Task M() { System.Threading.Thread.Sleep(1000); } }", "C.cs");
        var reports = await _asyncSafetyEngine.FindBlockingCallsInAsyncAsync("C.cs");
        Assert.That(reports, Is.Not.Empty);
        Assert.That(reports[0].Reason, Does.Contain("Thread.Sleep"));
    }

    [Test]
    public async Task FindBlockingCalls_Flags_Result_InAsync()
    {
        SetSource("public class C { public async System.Threading.Tasks.Task M() { var x = System.Threading.Tasks.Task.FromResult(1).Result; } }", "C.cs");
        var reports = await _asyncSafetyEngine.FindBlockingCallsInAsyncAsync("C.cs");
        Assert.That(reports, Is.Not.Empty);
        Assert.That(reports.Any(r => r.Reason.Contains(".Result")), Is.True);
    }

    [Test]
    public async Task FindBlockingCalls_Flags_Wait_InAsync()
    {
        SetSource("public class C { public async System.Threading.Tasks.Task M() { System.Threading.Tasks.Task.FromResult(1).Wait(); } }", "C.cs");
        var reports = await _asyncSafetyEngine.FindBlockingCallsInAsyncAsync("C.cs");
        Assert.That(reports, Is.Not.Empty);
        Assert.That(reports.Any(r => r.Reason.Contains(".Wait()")), Is.True);
    }

    [Test]
    public async Task FindBlockingCalls_DoesNotFlag_NonAsyncMethod()
    {
        SetSource("public class C { public void M() { System.Threading.Thread.Sleep(1000); } }", "C.cs");
        var reports = await _asyncSafetyEngine.FindBlockingCallsInAsyncAsync("C.cs");
        Assert.That(reports, Is.Empty);
    }

    // --- FindAsyncInConstructor ---

    [Test]
    public async Task FindAsyncInConstructor_Flags_AsyncMethodCallInCtor()
    {
        SetSource("public class C { public C() { SomeMethodAsync().Wait(); } private System.Threading.Tasks.Task SomeMethodAsync() => System.Threading.Tasks.Task.CompletedTask; }", "C.cs");
        var reports = await _asyncSafetyEngine.FindAsyncInConstructorAsync("C.cs");
        Assert.That(reports, Is.Not.Empty);
        Assert.That(reports[0].Reason, Does.Contain("factory method"));
    }

    [Test]
    public async Task FindAsyncInConstructor_DoesNotFlag_CleanConstructor()
    {
        SetSource("public class C { private int _x; public C() { _x = 5; } }", "C.cs");
        var reports = await _asyncSafetyEngine.FindAsyncInConstructorAsync("C.cs");
        Assert.That(reports, Is.Empty);
    }

    // --- FindTaskRunInAsync ---

    [Test]
    public async Task FindTaskRunInAsync_Flags_AwaitTaskRun()
    {
        SetSource("public class C { public async System.Threading.Tasks.Task M() { await System.Threading.Tasks.Task.Run(() => { }); } }", "C.cs");
        var reports = await _asyncSafetyEngine.FindTaskRunInAsyncAsync("C.cs");
        Assert.That(reports, Is.Not.Empty);
        Assert.That(reports[0].Reason, Does.Contain("Task.Run"));
    }

    [Test]
    public async Task FindTaskRunInAsync_DoesNotFlag_RegularAwait()
    {
        SetSource("public class C { public async System.Threading.Tasks.Task M() { await System.Threading.Tasks.Task.Delay(1); } }", "C.cs");
        var reports = await _asyncSafetyEngine.FindTaskRunInAsyncAsync("C.cs");
        Assert.That(reports, Is.Empty);
    }

    // --- FindConcurrentCollectionOpportunities ---

    [Test]
    public async Task FindConcurrentCollection_Flags_ListWithLock()
    {
        var src = @"
public class C {
    private System.Collections.Generic.List<int> _items = new();
    private object _lock = new();
    public void Add(int x) { lock(_lock) { _items.Add(x); } }
}";
        SetSource(src, "C.cs");
        var reports = await _asyncSafetyEngine.FindConcurrentCollectionOpportunitiesAsync("C.cs");
        Assert.That(reports, Is.Not.Empty);
        Assert.That(reports[0].Reason, Does.Contain("ConcurrentDictionary"));
    }

    [Test]
    public async Task FindConcurrentCollection_DoesNotFlag_NoLock()
    {
        var src = @"
public class C {
    private System.Collections.Generic.List<int> _items = new();
    public void Add(int x) { _items.Add(x); }
}";
        SetSource(src, "C.cs");
        var reports = await _asyncSafetyEngine.FindConcurrentCollectionOpportunitiesAsync("C.cs");
        Assert.That(reports, Is.Empty);
    }

    // --- FindUnsafeLazyInit ---

    [Test]
    public async Task FindUnsafeLazyInit_Flags_DoubleCheckedLockingWithoutVolatile()
    {
        var src = @"
public class C {
    private object _lock = new();
    private string _svc;
    public string GetSvc() {
        if (_svc == null) { lock(_lock) { if (_svc == null) { _svc = ""new""; } } }
        return _svc;
    }
}";
        SetSource(src, "C.cs");
        var reports = await _asyncSafetyEngine.FindUnsafeLazyInitAsync("C.cs");
        Assert.That(reports, Is.Not.Empty);
        Assert.That(reports[0].Reason, Does.Contain("volatile"));
    }

    [Test]
    public async Task FindUnsafeLazyInit_DoesNotFlag_VolatileField()
    {
        var src = @"
public class C {
    private object _lock = new();
    private volatile string _svc;
    public string GetSvc() {
        if (_svc == null) { lock(_lock) { if (_svc == null) { _svc = ""new""; } } }
        return _svc;
    }
}";
        SetSource(src, "C.cs");
        var reports = await _asyncSafetyEngine.FindUnsafeLazyInitAsync("C.cs");
        Assert.That(reports, Is.Empty);
    }

    // --- AddConfigureAwaitFalse ---

    [Test]
    public async Task AddConfigureAwaitFalse_AddsToAwaitExpressions()
    {
        SetSource("public class C { public async System.Threading.Tasks.Task M() { await System.Threading.Tasks.Task.Delay(100); } }", "C.cs");
        var result = await _asyncOptimizationEngine.AddConfigureAwaitFalseAsync("C.cs", libraryMode: true);
        Assert.That(result, Does.Contain("ConfigureAwait(false)"));
    }

    [Test]
    public async Task AddConfigureAwaitFalse_IsIdempotent()
    {
        SetSource("public class C { public async System.Threading.Tasks.Task M() { await System.Threading.Tasks.Task.Delay(100).ConfigureAwait(false); } }", "C.cs");
        var result = await _asyncOptimizationEngine.AddConfigureAwaitFalseAsync("C.cs", libraryMode: true);
        // Should still only have one ConfigureAwait, not double-wrapped
        Assert.That(result, Does.Contain("ConfigureAwait(false)"));
        Assert.That(result.UpdatedText!.IndexOf("ConfigureAwait", StringComparison.Ordinal),
            Is.EqualTo(result.UpdatedText!.LastIndexOf("ConfigureAwait", StringComparison.Ordinal)));
    }

    [Test]
    public async Task AddConfigureAwaitTrue_WhenLibraryModeFalse()
    {
        SetSource("public class C { public async System.Threading.Tasks.Task M() { await System.Threading.Tasks.Task.Delay(100); } }", "C.cs");
        var result = await _asyncOptimizationEngine.AddConfigureAwaitFalseAsync("C.cs", libraryMode: false);
        Assert.That(result, Does.Contain("ConfigureAwait(true)"));
    }

    // --- RemoveConfigureAwaitFalse ---

    [Test]
    public async Task RemoveConfigureAwaitFalse_RemovesConfigureAwait()
    {
        SetSource("public class C { public async System.Threading.Tasks.Task M() { await System.Threading.Tasks.Task.Delay(100).ConfigureAwait(false); } }", "C.cs");
        var result = await _asyncOptimizationEngine.RemoveConfigureAwaitFalseAsync("C.cs");
        Assert.That(result, Does.Not.Contain("ConfigureAwait"));
        Assert.That(result, Does.Contain("Task.Delay(100)"));
    }

    [Test]
    public async Task RemoveConfigureAwaitFalse_DoesNotModify_CleanCode()
    {
        const string source = "public class C { public async System.Threading.Tasks.Task M() { await System.Threading.Tasks.Task.Delay(100); } }";
        SetSource(source, "C.cs");
        var result = await _asyncOptimizationEngine.RemoveConfigureAwaitFalseAsync("C.cs");
        Assert.That(result, Does.Not.Contain("ConfigureAwait"));
        Assert.That(result, Does.Contain("Task.Delay(100)"));
    }

    // --- ConvertLockToSemaphoreSlim ---

    [Test]
    public async Task ConvertLockToSemaphoreSlim_TransformsLock()
    {
        var src = @"
public class C {
    private readonly object _lock = new();
    public void DoWork() { lock(_lock) { var x = 1; } }
}";
        SetSource(src, "C.cs");
        var result = await _threadSafetyEngine.ConvertLockToSemaphoreSlimAsync("C.cs", "DoWork");
        Assert.That(result, Does.Contain("SemaphoreSlim"));
        Assert.That(result, Does.Contain("WaitAsync"));
        Assert.That(result, Does.Contain("finally"));
        Assert.That(result, Does.Contain("Release"));
    }

    [Test]
    public async Task ConvertLockToSemaphoreSlim_MakesMethodAsync()
    {
        var src = @"
public class C {
    private readonly object _lock = new();
    public void DoWork() { lock(_lock) { var x = 1; } }
}";
        SetSource(src, "C.cs");
        var result = await _threadSafetyEngine.ConvertLockToSemaphoreSlimAsync("C.cs", "DoWork");
        Assert.That(result, Does.Contain("async"));
        Assert.That(result, Does.Contain("Task"));
    }

    // --- ConvertToAsyncEnumerable ---

    [Test]
    public async Task ConvertToAsyncEnumerable_TransformsListReturn()
    {
        var src = @"
using System.Collections.Generic;
using System.Threading.Tasks;
public class C {
    public async Task<List<string>> GetNames() {
        var results = new List<string>();
        results.Add(""Alice"");
        results.Add(""Bob"");
        return results;
    }
}";
        SetSource(src, "C.cs");
        var result = await _asyncOptimizationEngine.ConvertToAsyncEnumerableAsync("C.cs", "GetNames");
        Assert.That(result, Does.Contain("IAsyncEnumerable"));
        Assert.That(result, Does.Contain("yield return"));
    }

    [Test]
    public async Task ConvertToAsyncEnumerable_DoesNotModify_AlreadyAsyncEnumerable()
    {
        var src = @"
using System.Collections.Generic;
public class C {
    public async IAsyncEnumerable<string> GetNames() { yield return ""Alice""; }
}";
        SetSource(src, "C.cs");
        var result = await _asyncOptimizationEngine.ConvertToAsyncEnumerableAsync("C.cs", "GetNames");
        Assert.That(result, Does.Contain("IAsyncEnumerable"));
        Assert.That(result, Does.Not.Contain("Task<"));
    }
}
