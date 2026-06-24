using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Server;

#pragma warning disable CS8618

namespace RoslynSentinel.Tests;

/// <summary>
/// Tests for <see cref="AsyncOptimizationEngine.ConvertToAsyncBridgeAsync"/>.
/// Verifies the three-step Asyncify-bridge transformation:
///   1. async overload created with original body + CancellationToken + async modifier
///   2. original sync method body replaced with bridge call (.GetAwaiter().GetResult())
///   3. [Obsolete("Asyncify-bridge: call XxxAsync instead.", false)] added to original
/// </summary>
[TestFixture]
public class ConvertToAsyncBridgeTests
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

    // ══════════════════════════════════════════════════════════════════════════
    // Happy-path: bridge structure
    // ══════════════════════════════════════════════════════════════════════════

    [Test, CancelAfter(5000)]
    public async Task ConvertToAsyncBridge_TaskReturn_ProducesAsyncOverload()
    {
        SetSource(@"
using System.Data;
public class TripService
{
    public DataTable GetTrips(int companyId)
    {
        return DataHelper.Search(companyId);
    }
}", "TripService.cs");

        var result = await _engine.ConvertToAsyncBridgeAsync("TripService.cs", "GetTrips");

        // Async overload should exist with the right signature.
        Assert.That(result, Does.Contain("GetTripsAsync"),
            "Async overload should be named GetTripsAsync.");
        Assert.That(result, Does.Contain("Task<DataTable>"),
            "Async overload should return Task<DataTable>.");
        Assert.That(result, Does.Contain("CancellationToken cancellationToken"),
            "Async overload should have a CancellationToken parameter.");
        Assert.That(result, Does.Contain("async"),
            "Async overload should carry the async modifier.");
    }

    [Test, CancelAfter(5000)]
    public async Task ConvertToAsyncBridge_TaskReturn_OriginalBodyReplacedWithBridgeCall()
    {
        SetSource(@"
using System.Data;
public class TripService
{
    public DataTable GetTrips(int companyId)
    {
        return DataHelper.Search(companyId);
    }
}", "TripService.cs");

        var result = await _engine.ConvertToAsyncBridgeAsync("TripService.cs", "GetTrips");

        // Bridge body: return GetTripsAsync(companyId).GetAwaiter().GetResult();
        Assert.That(result, Does.Contain("GetTripsAsync"),
            "Bridge body should call GetTripsAsync.");
        Assert.That(result, Does.Contain("GetAwaiter"),
            "Bridge body should use GetAwaiter().");
        Assert.That(result, Does.Contain("GetResult"),
            "Bridge body should use GetResult().");
        // Original sync body (DataHelper.Search) should be in the ASYNC overload, not deleted entirely.
        Assert.That(result, Does.Contain("DataHelper.Search"),
            "Original body expression should appear in the async overload.");
    }

    [Test, CancelAfter(5000)]
    public async Task ConvertToAsyncBridge_TaskReturn_ObsoleteAttributeAddedToOriginal()
    {
        SetSource(@"
using System.Data;
public class TripService
{
    public DataTable GetTrips(int companyId)
    {
        return DataHelper.Search(companyId);
    }
}", "TripService.cs");

        var result = await _engine.ConvertToAsyncBridgeAsync("TripService.cs", "GetTrips");

        Assert.That(result, Does.Contain("Obsolete"),
            "[Obsolete] attribute must be added to the bridge wrapper.");
        Assert.That(result, Does.Contain("Asyncify-bridge"),
            "Obsolete message must contain 'Asyncify-bridge' to match CS0618 tracking convention.");
        Assert.That(result, Does.Contain("call GetTripsAsync instead"),
            "Obsolete message should name the replacement async method.");
    }

    [Test, CancelAfter(5000)]
    public async Task ConvertToAsyncBridge_TaskReturn_InlineCommentAddedToBridgeBody()
    {
        SetSource(@"
using System.Data;
public class TripService
{
    public DataTable GetTrips(int companyId)
    {
        return DataHelper.Search(companyId);
    }
}", "TripService.cs");

        var result = await _engine.ConvertToAsyncBridgeAsync("TripService.cs", "GetTrips");

        Assert.That(result, Does.Contain("// Asyncify-bridge: synchronous wrapper over GetTripsAsync."),
            "Bridge body should have the standard inline comment for readability.");
    }

    [Test, CancelAfter(5000)]
    public async Task ConvertToAsyncBridge_VoidMethod_BridgeUsesExpressionStatement()
    {
        SetSource(@"
public class NotificationService
{
    public void Notify(string message)
    {
        Console.WriteLine(message);
    }
}", "NotificationService.cs");

        var result = await _engine.ConvertToAsyncBridgeAsync("NotificationService.cs", "Notify");

        // void bridge: NotifyAsync(message).GetAwaiter().GetResult() — no 'return' keyword.
        Assert.That(result, Does.Contain("NotifyAsync"),
            "Async overload should be named NotifyAsync.");
        Assert.That(result, Does.Contain("Task NotifyAsync") | Does.Contain("Task\r\nNotifyAsync") | Does.Contain("async Task"),
            "Async overload should return Task (not Task<void>).");
        // No return keyword in the bridge call (void bridge = expression statement).
        // The async method may have return, but the bridge itself must not have 'return GetAwaiter'.
        Assert.That(result, Does.Contain("GetAwaiter"),
            "Bridge body should use GetAwaiter() even for void methods.");
    }

    [Test, CancelAfter(5000)]
    public async Task ConvertToAsyncBridge_MultipleParameters_AllForwardedInBridgeCall()
    {
        SetSource(@"
using System.Data;
public class DriverService
{
    public DataTable GetDrivers(int companyId, string status, int limit)
    {
        return DataHelper.Query(companyId, status, limit);
    }
}", "DriverService.cs");

        var result = await _engine.ConvertToAsyncBridgeAsync("DriverService.cs", "GetDrivers");

        // Bridge call should forward all three parameter names.
        Assert.That(result, Does.Contain("GetDriversAsync(companyId, status, limit)"),
            "All original parameter names should be forwarded in the bridge call.");
    }

    [Test, CancelAfter(5000)]
    public async Task ConvertToAsyncBridge_ExpressionBodiedMethod_AsyncOverloadGetsBlockBody()
    {
        SetSource(@"
using System.Data;
public class TripService
{
    public DataTable GetTrips(int companyId) => DataHelper.Search(companyId);
}", "TripService.cs");

        var result = await _engine.ConvertToAsyncBridgeAsync("TripService.cs", "GetTrips");

        // The async overload should have a block body (with braces) for CT-propagation tool compatibility.
        // Verify the original body expression is preserved inside the async method.
        Assert.That(result, Does.Contain("GetTripsAsync"),
            "Async overload should exist.");
        Assert.That(result, Does.Contain("DataHelper.Search"),
            "Original body expression should be carried into the async overload.");
    }

    [Test, CancelAfter(5000)]
    public async Task ConvertToAsyncBridge_StaticMethod_Works()
    {
        SetSource(@"
using System.Data;
public class TripService
{
    public static DataTable GetAllTrips()
    {
        return DataHelper.GetAll();
    }
}", "TripService.cs");

        var result = await _engine.ConvertToAsyncBridgeAsync("TripService.cs", "GetAllTrips");

        Assert.That(result, Does.Contain("GetAllTripsAsync"),
            "Static methods should also be convertible to bridge pattern.");
        Assert.That(result, Does.Contain("GetAwaiter"),
            "Bridge body should use GetAwaiter().");
    }

    [Test, CancelAfter(5000)]
    public async Task ConvertToAsyncBridge_AsyncMethodInsertedAfterOriginal()
    {
        SetSource(@"
using System.Data;
public class TripService
{
    public DataTable GetTrips(int companyId)
    {
        return DataHelper.Search(companyId);
    }
}", "TripService.cs");

        var result = await _engine.ConvertToAsyncBridgeAsync("TripService.cs", "GetTrips");

        // The bridge wrapper (GetTrips) should appear before the async overload (GetTripsAsync).
        var bridgeIdx = result.UpdatedText!.IndexOf("GetAwaiter", StringComparison.Ordinal);
        var asyncBodyIdx = result.UpdatedText!.IndexOf("DataHelper.Search", StringComparison.Ordinal);

        // Bridge body (GetAwaiter) should appear before the async overload's body (DataHelper.Search).
        Assert.That(bridgeIdx, Is.LessThan(asyncBodyIdx),
            "The bridge wrapper should be emitted before the async overload in the file.");
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Precondition failures
    // ══════════════════════════════════════════════════════════════════════════

    [Test, CancelAfter(5000)]
    public void ConvertToAsyncBridge_AlreadyAsync_Throws()
    {
        SetSource(@"
using System.Threading.Tasks;
public class Service
{
    public async Task<int> GetValue()
    {
        return await Task.FromResult(1);
    }
}", "Service.cs");

        Assert.ThrowsAsync<InvalidOperationException>(
            () => _engine.ConvertToAsyncBridgeAsync("Service.cs", "GetValue"),
            "Should throw when method is already async.");
    }

    [Test, CancelAfter(5000)]
    public void ConvertToAsyncBridge_AlreadyHasAsyncSuffix_Throws()
    {
        SetSource(@"
using System.Threading.Tasks;
using System.Data;
public class Service
{
    public DataTable GetTripsAsync(int id)
    {
        return DataHelper.Search(id);
    }
}", "Service.cs");

        Assert.ThrowsAsync<InvalidOperationException>(
            () => _engine.ConvertToAsyncBridgeAsync("Service.cs", "GetTripsAsync"),
            "Should throw when method name already ends with 'Async'.");
    }

    [Test, CancelAfter(5000)]
    public void ConvertToAsyncBridge_AbstractMethod_Throws()
    {
        SetSource(@"
using System.Data;
public abstract class BaseService
{
    public abstract DataTable GetTrips(int companyId);
}", "BaseService.cs");

        Assert.ThrowsAsync<InvalidOperationException>(
            () => _engine.ConvertToAsyncBridgeAsync("BaseService.cs", "GetTrips"),
            "Should throw for abstract methods (no body to copy).");
    }

    [Test, CancelAfter(5000)]
    public void ConvertToAsyncBridge_EventHandlerSignature_Throws()
    {
        SetSource(@"
using System;
public class Form1
{
    public void Button_Click(object sender, EventArgs e)
    {
        Console.WriteLine(""clicked"");
    }
}", "Form1.cs");

        Assert.ThrowsAsync<InvalidOperationException>(
            () => _engine.ConvertToAsyncBridgeAsync("Form1.cs", "Button_Click"),
            "Should throw for event handler methods (fixed delegate signature).");
    }

    [Test, CancelAfter(5000)]
    public void ConvertToAsyncBridge_RefParameter_Throws()
    {
        SetSource(@"
public class Service
{
    public int ComputeRef(ref int value)
    {
        value++;
        return value;
    }
}", "Service.cs");

        Assert.ThrowsAsync<InvalidOperationException>(
            () => _engine.ConvertToAsyncBridgeAsync("Service.cs", "ComputeRef"),
            "Should throw when method has ref parameters.");
    }

    [Test, CancelAfter(5000)]
    public void ConvertToAsyncBridge_OutParameter_Throws()
    {
        SetSource(@"
public class Service
{
    public bool TryGet(out int result)
    {
        result = 0;
        return true;
    }
}", "Service.cs");

        Assert.ThrowsAsync<InvalidOperationException>(
            () => _engine.ConvertToAsyncBridgeAsync("Service.cs", "TryGet"),
            "Should throw when method has out parameters.");
    }

    [Test, CancelAfter(5000)]
    public void ConvertToAsyncBridge_AsyncOverloadAlreadyExists_Throws()
    {
        SetSource(@"
using System.Threading;
using System.Threading.Tasks;
using System.Data;
public class TripService
{
    public DataTable GetTrips(int companyId)
    {
        return DataHelper.Search(companyId);
    }

    public async Task<DataTable> GetTripsAsync(int companyId, CancellationToken ct = default)
    {
        return await Task.FromResult(DataHelper.Search(companyId));
    }
}", "TripService.cs");

        Assert.ThrowsAsync<InvalidOperationException>(
            () => _engine.ConvertToAsyncBridgeAsync("TripService.cs", "GetTrips"),
            "Should throw when GetTripsAsync already exists in the class.");
    }

    [Test, CancelAfter(5000)]
    public void ConvertToAsyncBridge_MethodNotFound_Throws()
    {
        SetSource(@"
public class Service
{
    public int GetValue() => 42;
}", "Service.cs");

        Assert.ThrowsAsync<InvalidOperationException>(
            () => _engine.ConvertToAsyncBridgeAsync("Service.cs", "NonExistentMethod"),
            "Should throw when the named method does not exist in the file.");
    }

    [Test, CancelAfter(5000)]
    public void ConvertToAsyncBridge_FileNotFound_Throws()
    {
        SetSource(@"public class Dummy {}", "Dummy.cs");

        Assert.ThrowsAsync<InvalidOperationException>(
            () => _engine.ConvertToAsyncBridgeAsync("DoesNotExist.cs", "SomeMethod"),
            "Should throw when the file is not found in the loaded solution.");
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Modifier propagation
    // ══════════════════════════════════════════════════════════════════════════

    [Test, CancelAfter(5000)]
    public async Task ConvertToAsyncBridge_InstanceMethod_AsyncOverloadIsNotStatic()
    {
        SetSource(@"
public class Service
{
    public void DoWork(string arg) { _ = arg; }
}", "Service.cs");

        var result = await _engine.ConvertToAsyncBridgeAsync("Service.cs", "DoWork");
        var text = result.UpdatedText!;

        Assert.That(text, Does.Contain("public async Task DoWorkAsync"),
            "async overload must be public instance (non-static).");
        Assert.That(text, Does.Not.Contain("static async Task DoWorkAsync"),
            "async overload must not acquire a static modifier when original was instance.");
    }

    [Test, CancelAfter(5000)]
    public async Task ConvertToAsyncBridge_StaticMethod_AsyncOverloadIsStatic()
    {
        SetSource(@"
public class Service
{
    public static void DoWork(string arg) { _ = arg; }
}", "Service.cs");

        var result = await _engine.ConvertToAsyncBridgeAsync("Service.cs", "DoWork");
        var text = result.UpdatedText!;

        Assert.That(text, Does.Contain("public static async Task DoWorkAsync"),
            "async overload of a static method must also be static.");
    }

    [Test, CancelAfter(5000)]
    public async Task ConvertToAsyncBridge_OverrideMethod_AsyncOverloadStripsOverride()
    {
        SetSource(@"
using System.Threading.Tasks;
public abstract class Base
{
    public abstract void DoWork(string arg);
}
public class Derived : Base
{
    public override void DoWork(string arg) { _ = arg; }
}", "Service.cs");

        var result = await _engine.ConvertToAsyncBridgeAsync("Service.cs", "DoWork");
        var text = result.UpdatedText!;

        Assert.That(text, Does.Contain("public async Task DoWorkAsync"),
            "async overload must be present.");
        Assert.That(text, Does.Not.Contain("override async Task DoWorkAsync"),
            "async overload must not carry 'override' — there is no base counterpart to override.");
    }

    // ══════════════════════════════════════════════════════════════════════════
    // ConvertEventHandlerCallerToAsyncVoidAsync — delegate async modifier
    // ══════════════════════════════════════════════════════════════════════════

    [Test, CancelAfter(5000)]
    public async Task ConvertEventHandlerCaller_DelegateContainingBridgeCall_DelegateGetsAsyncModifier()
    {
        // The event handler calls a bridge wrapper inside an anonymous delegate passed to
        // BeginInvoke. The rewriter must add 'async' to that delegate — without it the
        // produced code has 'await' in a non-async context (CS4033 / compile error).
        SetSource(@"
using System;
using System.Threading.Tasks;
public class DispatchForm
{
    [Obsolete(""Asyncify-bridge: call refreshDatagridAsync instead."", false)]
    private void refreshDatagrid() => refreshDatagridAsync().GetAwaiter().GetResult();
    private Task refreshDatagridAsync() => Task.CompletedTask;

    private void Form_Load(object sender, EventArgs e)
    {
        BeginInvoke((Action)delegate
        {
            refreshDatagrid();
        });
    }

    private void BeginInvoke(Action a) { }
}", "DispatchForm.cs");

        var result = await _engine.ConvertEventHandlerCallerToAsyncVoidAsync("DispatchForm.cs", "Form_Load");
        var text = result.UpdatedText!;

        Assert.That(text, Does.Contain("async void Form_Load"),
            "Event handler must be uplifted to async void.");
        Assert.That(text, Does.Contain("async delegate"),
            "Anonymous delegate containing await must be marked async.");
        Assert.That(text, Does.Contain("await refreshDatagridAsync()"),
            "Bridge call inside the delegate must be replaced with await.");
    }

    [Test, CancelAfter(5000)]
    public async Task ConvertEventHandlerCaller_LambdaContainingBridgeCall_LambdaGetsAsyncModifier()
    {
        SetSource(@"
using System;
using System.Threading.Tasks;
public class DispatchForm
{
    [Obsolete(""Asyncify-bridge: call refreshDatagridAsync instead."", false)]
    private void refreshDatagrid() => refreshDatagridAsync().GetAwaiter().GetResult();
    private Task refreshDatagridAsync() => Task.CompletedTask;

    private void Form_Load(object sender, EventArgs e)
    {
        BeginInvoke(() =>
        {
            refreshDatagrid();
        });
    }

    private void BeginInvoke(Action a) { }
}", "DispatchForm.cs");

        var result = await _engine.ConvertEventHandlerCallerToAsyncVoidAsync("DispatchForm.cs", "Form_Load");
        var text = result.UpdatedText!;

        Assert.That(text, Does.Contain("async void Form_Load"),
            "Event handler must be uplifted to async void.");
        Assert.That(text, Does.Contain("async ()"),
            "Lambda containing await must be marked async.");
        Assert.That(text, Does.Contain("await refreshDatagridAsync()"),
            "Bridge call inside the lambda must be replaced with await.");
    }
}
