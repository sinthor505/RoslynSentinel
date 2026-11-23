// Accuracy regression tests for the four improved tools.
// Each [Test] exercises exactly one detection rule -> positive (should flag) and
// negative (should NOT flag, verifying false-positive guards).
using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Engines.Basic;

namespace RoslynSentinel.Tests.Advanced;

[TestFixture]
[Category("ThreadSafetyEngine")] // sentinel:auto-category
public class ThreadSafetyEngineExtendedTests
{
    private IWorkspaceManager _workspaceManager = null!;
    private ThreadSafetyEngine _engine = null!;
    [SetUp]
    public void Setup()
    {
        _workspaceManager = Battery.Basic.TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        _engine = Battery.Basic.TestEngines.ThreadSafetyEngine(_workspaceManager);
    }

    [TearDown]
    public void TearDown() => _workspaceManager?.Dispose();
    private void SetSource(string source) => _workspaceManager.SetTestSolution(TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Test.cs", source)]));
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
        Assert.That(results, Has.Count.EqualTo(1), "Null-check then assign without lock or volatile should be flagged");
        Assert.That(results[0], Does.Contain("_instance"));
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
        Assert.That(results, Is.Empty, "Null-check inside lock is safe - must not be flagged");
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
        Assert.That(results, Has.Count.EqualTo(1), "CAS retry loop without SpinWait should be flagged");
        Assert.That(results[0], Does.Contain("CompareExchange"));
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
        Assert.That(results, Is.Empty, "CAS loop with SpinWait is the correct pattern - must not be flagged");
    }
}
