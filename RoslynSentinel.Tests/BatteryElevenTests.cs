// Battery #11 — ImmutabilityEngine / ThreadSafetyEngine / AsyncSafetyEngine / DeadCodeEngine
// Adds dedicated XxxEngineTests fixture classes for 4 more engine classes.
// All tests run in-memory via AdhocWorkspace (no MSBuild/project-file loading).

using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using RoslynSentinel.Server;

namespace RoslynSentinel.Tests;

// ════════════════════════════════════════════════════════════════════════════════
// A. ImmutabilityEngine
// ════════════════════════════════════════════════════════════════════════════════
[TestFixture]
public class ImmutabilityEngineTests
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
    public async Task MakeClassImmutable_ClassWithMutableField_AddsReadonlyModifier()
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj",
            [("Entity.cs", "public class Entity { private int _age; }")]);
        _workspaceManager.SetTestSolution(solution);

        var result = await _engine.MakeClassImmutableAsync("Entity.cs", "Entity");

        Assert.That(result, Does.Contain("readonly"), "Mutable field should get readonly modifier");
    }

    [Test]
    public async Task MakeClassImmutable_ClassWithSetterProperty_ConvertsToInit()
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj",
            [("Entity.cs", "public class Entity { public string Name { get; set; } }")]);
        _workspaceManager.SetTestSolution(solution);

        var result = await _engine.MakeClassImmutableAsync("Entity.cs", "Entity");

        Assert.That(result, Does.Contain("init"), "set accessor should be replaced with init");
        Assert.That(result, Does.Not.Contain(" set;"), "No plain setter should remain");
    }

    [Test]
    public async Task MakeClassImmutable_UnknownFile_ReturnsEmptyString()
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj",
            [("Other.cs", "public class Other {}")]);
        _workspaceManager.SetTestSolution(solution);

        var result = await _engine.MakeClassImmutableAsync("DoesNotExist.cs", "Entity");

        Assert.That(result, Is.Empty, "Unknown file should return empty string, not throw");
    }
}

// ════════════════════════════════════════════════════════════════════════════════
// B. ThreadSafetyEngine
// ════════════════════════════════════════════════════════════════════════════════
[TestFixture]
public class ThreadSafetyEngineTests
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
    public async Task MakeMethodThreadSafe_SimpleMethod_AddsLockStatement()
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj",
            [("Counter.cs", "public class Counter { private int _count; public void Increment() { _count++; } }")]);
        _workspaceManager.SetTestSolution(solution);

        var result = await _engine.MakeMethodThreadSafeAsync("Counter.cs", "Increment");

        Assert.That(result, Does.Contain("lock"), "Method body should be wrapped in a lock statement");
        Assert.That(result, Does.Contain("_lock"), "A lock object field should be added");
        Assert.That(result.UpdatedText!, Does.Not.StartWith("// Error:"), "Should not return an error comment");
    }

    [Test]
    public async Task MakeMethodThreadSafe_UnknownFile_ReturnsErrorComment()
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj",
            [("Counter.cs", "public class Counter { public void Inc() { } }")]);
        _workspaceManager.SetTestSolution(solution);

        var result = await _engine.MakeMethodThreadSafeAsync("DoesNotExist.cs", "Inc");

        Assert.That(result.UpdatedText!, Does.StartWith("// Error:"), "Unknown file should return error comment");
    }

    [Test]
    public async Task MakeMethodThreadSafe_UnknownMethod_ReturnsErrorComment()
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj",
            [("Counter.cs", "public class Counter { public void Inc() { } }")]);
        _workspaceManager.SetTestSolution(solution);

        var result = await _engine.MakeMethodThreadSafeAsync("Counter.cs", "NonExistentMethod");

        Assert.That(result.UpdatedText!, Does.StartWith("// Error:"), "Unknown method should return error comment");
        Assert.That(result, Does.Contain("NonExistentMethod"), "Error should mention the missing method name");
    }
}

// ════════════════════════════════════════════════════════════════════════════════
// C. AsyncSafetyEngine
// ════════════════════════════════════════════════════════════════════════════════
[TestFixture]
public class AsyncSafetyEngineTests
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

    private void SetSource(string source, string fileName = "Async.cs")
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [(fileName, source)]);
        _workspaceManager.SetTestSolution(solution);
    }

    [Test]
    public async Task DetectAsyncVoidMethods_HasAsyncVoidHandler_DetectsIt()
    {
        SetSource(@"
using System.Threading.Tasks;
public class EventHandler
{
    public async void OnButtonClicked() { await System.Threading.Tasks.Task.Delay(1); }
}");
        var reports = await _engine.DetectAsyncVoidMethodsAsync("Async.cs");

        Assert.That(reports, Is.Not.Empty, "async void method should be reported");
        Assert.That(reports.Any(r => r.MethodName == "OnButtonClicked"), Is.True,
            "Should report the async void method by name");
    }

    [Test]
    public async Task DetectAsyncVoidMethods_OnlyAsyncTaskMethods_ReturnsEmpty()
    {
        SetSource(@"
using System.Threading.Tasks;
public class Service
{
    public async Task RunAsync() { await System.Threading.Tasks.Task.Delay(1); }
}");
        var reports = await _engine.DetectAsyncVoidMethodsAsync("Async.cs");

        Assert.That(reports, Is.Empty, "async Task methods should NOT be reported");
    }

    [Test]
    public async Task FindTaskYieldUsage_HasTaskYieldCall_DetectsIt()
    {
        SetSource(@"
using System.Threading.Tasks;
public class Worker
{
    public async Task DoWorkAsync()
    {
        await Task.Yield();
    }
}");
        var reports = await _engine.FindTaskYieldUsageAsync("Async.cs");

        Assert.That(reports, Is.Not.Empty, "Task.Yield() call should be flagged");
        Assert.That(reports.Any(r => r.MethodName == "DoWorkAsync"), Is.True);
    }
}

// ════════════════════════════════════════════════════════════════════════════════
// D. DeadCodeEngine
// ════════════════════════════════════════════════════════════════════════════════
[TestFixture]
public class DeadCodeEngineTests
{
    private PersistentWorkspaceManager _workspaceManager = null!;
    private DeadCodeEngine _engine = null!;

    [SetUp]
    public void Setup()
    {
        _workspaceManager = new PersistentWorkspaceManager(NullLogger<PersistentWorkspaceManager>.Instance);
        _engine = new DeadCodeEngine(_workspaceManager);
    }

    [TearDown]
    public void TearDown() => _workspaceManager?.Dispose();

    [Test]
    public async Task FindUnusedPrivateMembers_NeverCalledPrivateMethod_ReportsIt()
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj",
            [("Service.cs", @"
public class Service
{
    private void NeverCalled() { }
    public void Run() { }
}")]);
        _workspaceManager.SetTestSolution(solution);

        var reports = await _engine.FindUnusedPrivateMembersAsync("Service.cs", "Service");

        Assert.That(reports, Is.Not.Empty, "NeverCalled should be reported as dead code");
        Assert.That(reports.Any(r => r.SymbolName == "NeverCalled"), Is.True);
    }

    [Test]
    public async Task FindUnusedPrivateMembers_PrivateMethodCalledFromPublic_ReturnsEmpty()
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj",
            [("Service.cs", @"
public class Service
{
    private void Helper() { }
    public void Run() { Helper(); }
}")]);
        _workspaceManager.SetTestSolution(solution);

        var reports = await _engine.FindUnusedPrivateMembersAsync("Service.cs", "Service");

        Assert.That(reports.Any(r => r.SymbolName == "Helper"), Is.False,
            "Used private method should NOT be reported as dead code");
    }

    [Test]
    public void FindUnusedPrivateMembers_UnknownFile_ThrowsException()
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj",
            [("Service.cs", "public class Service { }")]);
        _workspaceManager.SetTestSolution(solution);

        Assert.ThrowsAsync<Exception>(() =>
            _engine.FindUnusedPrivateMembersAsync("DoesNotExist.cs", "Service"));
    }
}
