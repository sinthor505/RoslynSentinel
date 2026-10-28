using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Common;
using RoslynSentinel.Engines.Advanced;

namespace RoslynSentinel.Tests.Battery.Advanced;

[TestFixture]
[Category("AsyncOptimizationEngine")] // sentinel:auto-category
public class AsyncOptimizationEngineTests
{
    private PersistentWorkspaceManager _mgr = null!;
    private AsyncOptimizationEngine _engine = null!;
    private static readonly (string, string)[] Stub = [("Other.cs", "public class Other {}")];
    [SetUp]
    public void SetUp()
    {
        _mgr = Basic.TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        _engine = TestEnginesAdvanced.AsyncOptimizationEngine(_mgr);
        _mgr.SetTestSolution(TestSolutionBuilder.CreateSolutionWithProject("TestProj", Stub));
    }

    [TearDown]
    public async Task TearDown() => _mgr?.Dispose();
    [Test]
    public async Task OptimizeToValueTask_UnknownFile_ThrowsException()
    {
        await Assert.ThrowsAsync<ToolNotFoundException>(async () => await _engine.OptimizeToValueTaskAsync("NoSuchFile.cs", "DoWork"), "missing file should throw ToolNotFoundException");
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
        _mgr.SetTestSolution(TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("MyService.cs", source)]));
        var result = await _engine.OptimizeToValueTaskAsync("MyService.cs", "DoWorkAsync");
        Assert.That(result.UpdatedText, Does.Contain("ValueTask"), "Task-returning method with single await should be convertable to ValueTask");
    }

    [Test]
    public async Task OptimizeToValueTask_MethodWithMultipleAwaits_ThrowsTargetIneligible()
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
        _mgr.SetTestSolution(TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("MyService.cs", source)]));
        var ex = await Assert.ThrowsAsync<ToolTargetIneligibleException>(async () =>
        {
            await _engine.OptimizeToValueTaskAsync("MyService.cs", "ProcessAsync");
        });
        Assert.That(ex!.Message, Does.Contain("Cannot safely convert to ValueTask"), "method with multiple awaits should be rejected as target-ineligible");
    }
}

[TestFixture]
[Category("LogicSimplificationEngine")] // sentinel:auto-category
public class CodeFlowEngineTests
{
    private PersistentWorkspaceManager _mgr = null!;
    private LogicSimplificationEngine _engine = null!;
    private static readonly (string, string)[] Stub = [("Other.cs", "public class Other {}")];
    [SetUp]
    public void SetUp()
    {
        _mgr = Basic.TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        _engine = TestEnginesAdvanced.LogicSimplificationEngine(_mgr);
        _mgr.SetTestSolution(TestSolutionBuilder.CreateSolutionWithProject("TestProj", Stub));
    }

    [TearDown]
    public void TearDown() => _mgr?.Dispose();
    [Test]
    public async Task ReduceBlockDepth_MissingFile_ThrowsNotFound()
    {
        var ex = await Assert.ThrowsAsync<ToolNotFoundException>(async () => await TestEnginesAdvanced.LogicSimplificationEngine(_mgr).ReduceBlockDepthAsync("NoSuchFile.cs", "Process"));
        Assert.That(ex.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound));
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
        _mgr.SetTestSolution(TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Processor.cs", source)]));
        var result = await TestEnginesAdvanced.LogicSimplificationEngine(_mgr).ReduceBlockDepthAsync("Processor.cs", "Process");
        Assert.That(result.UpdatedText, Does.Contain("return"), "early return should be added");
        Assert.That(result.UpdatedText, Does.Contain("!"), "inverted condition should use logical NOT");
    }

    [Test]
    public async Task ReduceBlockDepth_MethodWithNoOptimizableIf_ThrowsTargetIneligible()
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
        _mgr.SetTestSolution(TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Logger.cs", source)]));
        // The engine throws ToolTargetIneligibleException (ErrorCode TargetIneligible) when there is no nested if to flatten.
        var ex = await Assert.ThrowsAsync<ToolTargetIneligibleException>(async () =>
            await TestEnginesAdvanced.LogicSimplificationEngine(_mgr).ReduceBlockDepthAsync("Logger.cs", "Log"));
        Assert.That(ex.ErrorCode, Is.EqualTo(ToolErrorCode.TargetIneligible));
        Assert.That(ex.Message, Does.Contain("cannot have its block depth reduced"));
    }
}

[TestFixture]
[Category("CodeHealingEngine")] // sentinel:auto-category
public class CodeHealingEngineTests
{
    private PersistentWorkspaceManager _mgr = null!;
    private CodeHealingEngine _engine = null!;
    private static readonly (string, string)[] Stub = [("Other.cs", "public class Other {}")];
    [SetUp]
    public void SetUp()
    {
        _mgr = Basic.TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        _engine = TestEnginesAdvanced.CodeHealingEngine(_mgr, Basic.TestEngines.SentinelConfiguration());
        _mgr.SetTestSolution(TestSolutionBuilder.CreateSolutionWithProject("TestProj", Stub));
    }

    [TearDown]
    public void TearDown() => _mgr?.Dispose();
    [Test]
    public async Task FixThreadSleep_MissingFile_ThrowsNotFound()
    {
        var ex = await Assert.ThrowsAsync<ToolNotFoundException>(async () => await _engine.FixThreadSleepAsync("NoSuchFile.cs"));
        Assert.That(ex.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound));
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
        _mgr.SetTestSolution(TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Worker.cs", source)]));
        var result = await _engine.FixThreadSleepAsync("Worker.cs");
        Assert.That(result.UpdatedText, Does.Contain("Task.Delay"), "Thread.Sleep in async method should be replaced with Task.Delay");
        Assert.That(result.UpdatedText, Does.Contain("await"), "replacement should be awaited");
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
        _mgr.SetTestSolution(TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("SyncWorker.cs", source)]));
        var result = await _engine.FixThreadSleepAsync("SyncWorker.cs");
        Assert.That(result.UpdatedText, Does.Contain("Thread.Sleep"), "Thread.Sleep in sync method should not be touched");
        Assert.That(result.UpdatedText, Does.Not.Contain("Task.Delay"), "no Task.Delay should appear for sync context");
    }
}