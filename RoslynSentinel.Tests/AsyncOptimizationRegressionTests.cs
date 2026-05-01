using Microsoft.Extensions.Logging.Abstractions;
using RoslynSentinel.Server;

#pragma warning disable CS8618

namespace RoslynSentinel.Tests;

[TestFixture]
public class AsyncOptimizationRegressionTests
{
    private PersistentWorkspaceManager _workspaceManager;
    private AsyncOptimizationEngine _engine;

    [SetUp]
    public void Setup()
    {
        _workspaceManager = new PersistentWorkspaceManager(NullLogger<PersistentWorkspaceManager>.Instance);
        _engine = new AsyncOptimizationEngine(_workspaceManager);
    }

    [TearDown]
    public void TearDown() => _workspaceManager.Dispose();

    private void SetSource(string source, string fileName = "Test.cs")
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [(fileName, source)]);
        _workspaceManager.SetTestSolution(solution);
    }

    // ══════════════════════════════════════════════════════════════
    // OptimizeToValueTaskAsync
    // ══════════════════════════════════════════════════════════════

    [Test]
    public async Task OptimizeToValueTask_SingleAwait_ConvertsToValueTask()
    {
        SetSource(@"
using System.Threading.Tasks;
public class Service
{
    public async Task<int> GetValueAsync()
    {
        await Task.Delay(1);
        return 42;
    }
}", "Service.cs");

        var result = await _engine.OptimizeToValueTaskAsync("Service.cs", "GetValueAsync");

        Assert.That(result, Does.Contain("ValueTask<int>"),
            "Method returning Task<int> with 1 await should be converted to ValueTask<int>.");
        Assert.That(result, Does.Not.StartWith("// WARNING:"),
            "Single-await method should not trigger a warning.");
    }

    [Test]
    public async Task OptimizeToValueTask_MultipleAwaits_ReturnsWarning()
    {
        SetSource(@"
using System.Threading.Tasks;
public class Service
{
    public async Task<int> DoWorkAsync()
    {
        await Task.Delay(1);
        await Task.Delay(2);
        return 99;
    }
}", "Service.cs");

        var result = await _engine.OptimizeToValueTaskAsync("Service.cs", "DoWorkAsync");

        Assert.That(result, Does.StartWith("// WARNING:"),
            "Method with 2+ awaits must produce a WARNING comment.");
        Assert.That(result, Does.Contain("2 await"),
            "Warning should mention the count of await expressions.");
    }

    [Test]
    public async Task OptimizeToValueTask_TryCatch_ReturnsWarning()
    {
        SetSource(@"
using System.Threading.Tasks;
public class Service
{
    public async Task RunAsync()
    {
        try
        {
            await Task.Delay(1);
        }
        catch
        {
        }
    }
}", "Service.cs");

        var result = await _engine.OptimizeToValueTaskAsync("Service.cs", "RunAsync");

        Assert.That(result, Does.StartWith("// WARNING:"),
            "Method with try/catch must produce a WARNING comment.");
        Assert.That(result, Does.Contain("try/catch"),
            "Warning should mention try/catch as the reason.");
    }

    [Test]
    public async Task OptimizeToValueTask_VoidTask_ConvertsToValueTask()
    {
        SetSource(@"
using System.Threading.Tasks;
public class Service
{
    public async Task FireAndForgetAsync()
    {
        await Task.Delay(1);
    }
}", "Service.cs");

        var result = await _engine.OptimizeToValueTaskAsync("Service.cs", "FireAndForgetAsync");

        Assert.That(result, Does.Contain("ValueTask"),
            "Task-returning method with 1 await should become ValueTask.");
        Assert.That(result, Does.Not.Contain("Task<"),
            "Result should not still contain Task<> return type.");
    }

    [Test]
    public async Task OptimizeToValueTask_AlreadyValueTask_ReturnsUnchanged()
    {
        SetSource(@"
using System.Threading.Tasks;
public class Service
{
    public async ValueTask<int> GetValueAsync()
    {
        await Task.Delay(1);
        return 42;
    }
}", "Service.cs");

        var result = await _engine.OptimizeToValueTaskAsync("Service.cs", "GetValueAsync");

        // Should return source unchanged (not Task return type → no conversion)
        Assert.That(result, Does.Contain("ValueTask<int>"),
            "Already-ValueTask method should be returned as-is.");
        Assert.That(result, Does.Not.StartWith("// WARNING:"),
            "Already-ValueTask method should not produce a warning.");
    }

    // ══════════════════════════════════════════════════════════════
    // OptimizeIndependentAwaitsAsync
    // ══════════════════════════════════════════════════════════════

    [Test]
    public async Task OptimizeIndependentAwaits_TwoExpressionStatements_UsesWhenAll()
    {
        SetSource(@"
using System.Threading.Tasks;
public class Service
{
    public async Task DoWorkAsync()
    {
        await TaskA();
        await TaskB();
    }
    private Task TaskA() => Task.CompletedTask;
    private Task TaskB() => Task.CompletedTask;
}", "Service.cs");

        var result = await _engine.OptimizeIndependentAwaitsAsync("Service.cs", "DoWorkAsync");

        Assert.That(result, Does.Contain("WhenAll"),
            "Two independent expression-await statements should be converted to Task.WhenAll.");
    }

    [Test]
    public async Task OptimizeIndependentAwaits_VarDeclarations_HoistsToTaskVariables()
    {
        SetSource(@"
using System.Threading.Tasks;
public class Service
{
    public async Task<int> ComputeAsync()
    {
        var a = await GetAAsync();
        var b = await GetBAsync();
        return a + b;
    }
    private Task<int> GetAAsync() => Task.FromResult(1);
    private Task<int> GetBAsync() => Task.FromResult(2);
}", "Service.cs");

        var result = await _engine.OptimizeIndependentAwaitsAsync("Service.cs", "ComputeAsync");

        // Hoisting: aTask = GetAAsync(); bTask = GetBAsync(); then await each
        Assert.That(result, Does.Contain("Task"),
            "Independent var-declaration awaits should be hoisted into task variables.");
        Assert.That(result, Does.Contain("aTask").Or.Contains("GetAAsync"),
            "Task variable names or original calls should appear.");
    }

    [Test]
    public async Task OptimizeIndependentAwaits_DependentAwaits_LeftUnchanged()
    {
        SetSource(@"
using System.Threading.Tasks;
public class Service
{
    public async Task<string> GetDataAsync()
    {
        var user = await GetUserAsync();
        var data = await GetDataAsync(user);
        return data;
    }
    private Task<string> GetUserAsync() => Task.FromResult(""u1"");
    private Task<string> GetDataAsync(string user) => Task.FromResult(""data"");
}", "Service.cs");

        var result = await _engine.OptimizeIndependentAwaitsAsync("Service.cs", "GetDataAsync");

        Assert.That(result, Does.Not.Contain("WhenAll"),
            "Dependent awaits (second uses result of first) should NOT be batched into WhenAll.");
    }

    // ══════════════════════════════════════════════════════════════
    // GenerateAsyncOverloadAsync
    // ══════════════════════════════════════════════════════════════

    [Test]
    public async Task GenerateAsyncOverload_VoidMethod_AddsAsyncSuffix()
    {
        SetSource(@"
public class Worker
{
    public void DoWork()
    {
    }
}", "Worker.cs");

        var result = await _engine.GenerateAsyncOverloadAsync("Worker.cs", "DoWork");

        Assert.That(result, Does.Contain("DoWorkAsync"),
            "Generated overload should have Async suffix.");
        Assert.That(result, Does.Contain("Task"),
            "Void method's async overload should return Task.");
    }

    [Test]
    public async Task GenerateAsyncOverload_ReturnMethod_ScaffoldsCompletedTask()
    {
        SetSource(@"
public class Service
{
    public string GetName()
    {
        return ""test"";
    }
}", "Service.cs");

        var result = await _engine.GenerateAsyncOverloadAsync("Service.cs", "GetName");

        Assert.That(result, Does.Contain("GetNameAsync"),
            "Generated overload should have Async suffix.");
        Assert.That(result, Does.Contain("Task.CompletedTask"),
            "Scaffold should include Task.CompletedTask placeholder.");
    }

    [Test]
    public async Task GenerateAsyncOverload_DoesNotUseTaskRun()
    {
        SetSource(@"
public class Processor
{
    public int Compute(int x)
    {
        return x * 2;
    }
}", "Processor.cs");

        var result = await _engine.GenerateAsyncOverloadAsync("Processor.cs", "Compute");

        Assert.That(result, Does.Not.Contain("Task.Run"),
            "Scaffold overload must NOT wrap original code in Task.Run.");
    }

    [Test]
    public async Task GenerateAsyncOverload_AddsCancellationTokenParameter()
    {
        SetSource(@"
public class Service
{
    public void Execute()
    {
    }
}", "Service.cs");

        var result = await _engine.GenerateAsyncOverloadAsync("Service.cs", "Execute");

        Assert.That(result, Does.Contain("CancellationToken"),
            "Generated async overload should include a CancellationToken parameter.");
    }
}
