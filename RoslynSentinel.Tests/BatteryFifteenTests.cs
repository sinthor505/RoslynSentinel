using Microsoft.CodeAnalysis;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using RoslynSentinel.Server;

namespace RoslynSentinel.Tests;

// ────────────────────────────────────────────────────────────────────────────
// Battery #15 — ApiIntegrationEngine, AsyncOptimizationEngine,
//               CodeFlowEngine, CodeHealingEngine
// ────────────────────────────────────────────────────────────────────────────

[TestFixture]
public class ApiIntegrationEngineTests
{
    private PersistentWorkspaceManager _mgr = null!;
    private ApiIntegrationEngine _engine = null!;
    private static readonly (string, string)[] Stub = [("Other.cs", "public class Other {}")];

    [SetUp]
    public void SetUp()
    {
        _mgr = new PersistentWorkspaceManager(NullLogger<PersistentWorkspaceManager>.Instance);
        _engine = new ApiIntegrationEngine(_mgr);
        _mgr.SetTestSolution(TestSolutionBuilder.CreateSolutionWithProject("TestProj", Stub));
    }

    [TearDown]
    public void TearDown() => _mgr?.Dispose();

    [Test]
    public void AddValidationToPoco_UnknownFile_ThrowsException()
    {
        Assert.ThrowsAsync<Exception>(
            async () => await _engine.AddValidationToPocoAsync("NoSuchFile.cs", "PersonDto"),
            "missing file should throw Exception");
    }

    [Test]
    public async Task AddValidationToPoco_StringProperty_AddsRequiredAttribute()
    {
        const string source = @"
public class PersonDto
{
    public string Name { get; set; }
}";
        _mgr.SetTestSolution(TestSolutionBuilder.CreateSolutionWithProject("TestProj",
            [("PersonDto.cs", source)]));

        var result = await _engine.AddValidationToPocoAsync("PersonDto.cs", "PersonDto");

        Assert.That(result, Does.Contain("Required"), "string property should get [Required] attribute");
        Assert.That(result, Does.Contain("StringLength"), "string property should get [StringLength] attribute");
    }

    [Test]
    public async Task AddValidationToPoco_IntProperty_AddsRangeAttribute()
    {
        const string source = @"
public class ProductDto
{
    public int Quantity { get; set; }
}";
        _mgr.SetTestSolution(TestSolutionBuilder.CreateSolutionWithProject("TestProj",
            [("ProductDto.cs", source)]));

        var result = await _engine.AddValidationToPocoAsync("ProductDto.cs", "ProductDto");

        Assert.That(result, Does.Contain("Range"), "int property should get [Range] attribute");
    }
}

[TestFixture]
public class AsyncOptimizationEngineTests
{
    private PersistentWorkspaceManager _mgr = null!;
    private AsyncOptimizationEngine _engine = null!;
    private static readonly (string, string)[] Stub = [("Other.cs", "public class Other {}")];

    [SetUp]
    public void SetUp()
    {
        _mgr = new PersistentWorkspaceManager(NullLogger<PersistentWorkspaceManager>.Instance);
        _engine = new AsyncOptimizationEngine(_mgr);
        _mgr.SetTestSolution(TestSolutionBuilder.CreateSolutionWithProject("TestProj", Stub));
    }

    [TearDown]
    public void TearDown() => _mgr?.Dispose();

    [Test]
    public void OptimizeToValueTask_UnknownFile_ThrowsException()
    {
        Assert.ThrowsAsync<InvalidOperationException>(
            async () => await _engine.OptimizeToValueTaskAsync("NoSuchFile.cs", "DoWork"),
            "missing file should throw InvalidOperationException");
    }

    [Test]
    public async Task OptimizeToValueTask_TaskReturningMethod_ConvertsToValueTask()
    {
        const string source = @"
using System.Threading.Tasks;
public class MyService
{
    public async Task DoWorkAsync()
    {
        await Task.CompletedTask;
    }
}";
        _mgr.SetTestSolution(TestSolutionBuilder.CreateSolutionWithProject("TestProj",
            [("MyService.cs", source)]));

        var result = await _engine.OptimizeToValueTaskAsync("MyService.cs", "DoWorkAsync");

        Assert.That(result, Does.Contain("ValueTask"),
            "Task-returning method with single await should be convertable to ValueTask");
    }

    [Test]
    public async Task OptimizeToValueTask_MethodWithMultipleAwaits_ReturnsWarning()
    {
        const string source = @"
using System.Threading.Tasks;
public class MyService
{
    public async Task ProcessAsync()
    {
        await Task.CompletedTask;
        await Task.CompletedTask;
    }
}";
        _mgr.SetTestSolution(TestSolutionBuilder.CreateSolutionWithProject("TestProj",
            [("MyService.cs", source)]));

        var result = await _engine.OptimizeToValueTaskAsync("MyService.cs", "ProcessAsync");

        Assert.That(result, Does.Contain("WARNING"), "method with multiple awaits should produce a warning comment");
    }
}

[TestFixture]
public class CodeFlowEngineTests
{
    private PersistentWorkspaceManager _mgr = null!;
    private CodeFlowEngine _engine = null!;
    private static readonly (string, string)[] Stub = [("Other.cs", "public class Other {}")];

    [SetUp]
    public void SetUp()
    {
        _mgr = new PersistentWorkspaceManager(NullLogger<PersistentWorkspaceManager>.Instance);
        _engine = new CodeFlowEngine(_mgr);
        _mgr.SetTestSolution(TestSolutionBuilder.CreateSolutionWithProject("TestProj", Stub));
    }

    [TearDown]
    public void TearDown() => _mgr?.Dispose();

    [Test]
    public async Task ReduceBlockDepth_UnknownFile_ReturnsErrorComment()
    {
        var result = await _engine.ReduceBlockDepthAsync("NoSuchFile.cs", "Process");
        Assert.That(result, Does.StartWith("// Error:"), "unknown file should return an error comment string");
    }

    [Test]
    public async Task ReduceBlockDepth_SingleIfBody_InvertsConditionToEarlyReturn()
    {
        const string source = @"
public class Processor
{
    public void Process(string item)
    {
        if (item != null)
        {
            System.Console.WriteLine(item);
        }
    }
}";
        _mgr.SetTestSolution(TestSolutionBuilder.CreateSolutionWithProject("TestProj",
            [("Processor.cs", source)]));

        var result = await _engine.ReduceBlockDepthAsync("Processor.cs", "Process");

        Assert.That(result, Does.Contain("return"), "early return should be added");
        Assert.That(result, Does.Contain("!"), "inverted condition should use logical NOT");
    }

    [Test]
    public async Task ReduceBlockDepth_MethodWithNoOptimizableIf_ReturnsUnchanged()
    {
        const string source = @"
public class Logger
{
    public void Log(string msg)
    {
        System.Console.WriteLine(msg);
        System.Console.WriteLine(""done"");
    }
}";
        _mgr.SetTestSolution(TestSolutionBuilder.CreateSolutionWithProject("TestProj",
            [("Logger.cs", source)]));

        var result = await _engine.ReduceBlockDepthAsync("Logger.cs", "Log");

        Assert.That(result, Does.Not.StartWith("// Error:"), "should return source, not error comment");
        Assert.That(result, Does.Contain("Log"), "method name should still appear");
    }
}

[TestFixture]
public class CodeHealingEngineTests
{
    private PersistentWorkspaceManager _mgr = null!;
    private CodeHealingEngine _engine = null!;
    private static readonly (string, string)[] Stub = [("Other.cs", "public class Other {}")];

    [SetUp]
    public void SetUp()
    {
        _mgr = new PersistentWorkspaceManager(NullLogger<PersistentWorkspaceManager>.Instance);
        _engine = new CodeHealingEngine(_mgr, new SentinelConfiguration());
        _mgr.SetTestSolution(TestSolutionBuilder.CreateSolutionWithProject("TestProj", Stub));
    }

    [TearDown]
    public void TearDown() => _mgr?.Dispose();

    [Test]
    public async Task FixThreadSleep_UnknownFile_ReturnsEmptyString()
    {
        var result = await _engine.FixThreadSleepAsync("NoSuchFile.cs");
        Assert.That(result, Is.EqualTo(string.Empty), "unknown file should return empty string");
    }

    [Test]
    public async Task FixThreadSleep_InAsyncMethod_ConvertsToTaskDelay()
    {
        const string source = @"
using System.Threading;
using System.Threading.Tasks;
public class Worker
{
    public async Task RunAsync()
    {
        Thread.Sleep(1000);
    }
}";
        _mgr.SetTestSolution(TestSolutionBuilder.CreateSolutionWithProject("TestProj",
            [("Worker.cs", source)]));

        var result = await _engine.FixThreadSleepAsync("Worker.cs");

        Assert.That(result, Does.Contain("Task.Delay"), "Thread.Sleep in async method should be replaced with Task.Delay");
        Assert.That(result, Does.Contain("await"), "replacement should be awaited");
    }

    [Test]
    public async Task FixThreadSleep_InSyncMethod_LeavesUnchanged()
    {
        const string source = @"
using System.Threading;
public class SyncWorker
{
    public void RunSync()
    {
        Thread.Sleep(500);
    }
}";
        _mgr.SetTestSolution(TestSolutionBuilder.CreateSolutionWithProject("TestProj",
            [("SyncWorker.cs", source)]));

        var result = await _engine.FixThreadSleepAsync("SyncWorker.cs");

        Assert.That(result, Does.Contain("Thread.Sleep"), "Thread.Sleep in sync method should not be touched");
        Assert.That(result, Does.Not.Contain("Task.Delay"), "no Task.Delay should appear for sync context");
    }
}