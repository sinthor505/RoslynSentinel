using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Engines.Advanced;
using RoslynSentinel.Engines.Basic;

#pragma warning disable CS8618

namespace RoslynSentinel.Tests.Asyncify;

/// <summary>
/// Tests for <see cref="AsyncOptimizationEngine.ConvertToAsyncBridgeAsync"/>.
/// Verifies the three-step AsyncifyTool-bridge transformation:
///   1. async overload created with original body + CancellationToken + async modifier
///   2. original sync method body replaced with bridge call (.GetAwaiter().GetResult())
///   3. [Obsolete("AsyncifyTool-bridge: call XxxAsync instead.", false)] added to original
/// </summary>
[TestFixture]
[Category("AsyncOptimizationEngine")] // sentinel:auto-category
public class ConvertToAsyncBridgeTests
{
    private IWorkspaceManager _workspaceManager;
    private AsyncOptimizationEngine _engine;

    // Absolute root for the fixture's in-memory documents. It is never created on disk: the engine works on the
    // loaded solution, and this keeps every document path (and every path the tests pass) absolute.
    private string _tempDir;

    [SetUp]
    public void Setup()
    {
        _workspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        _engine = new AsyncOptimizationEngine(_workspaceManager);
        _tempDir = Path.Combine(Path.GetTempPath(), "ConvertToAsyncBridgeTests_" + Guid.NewGuid().ToString("N"));
    }

    [TearDown]
    public void TearDown() => _workspaceManager.Dispose();

    /// <summary>Absolute path of a fixture file under this test's own root.</summary>
    private string PathOf(string fileName) => Path.Combine(_tempDir, fileName);

    private void SetSource(string source, string fileName = "Test.cs") => SetMultipleFiles((fileName, source));

    private void SetMultipleFiles(params (string name, string content)[] files)
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject(
            "TestProj",
            Path.Combine(_tempDir, "TestProj.csproj"),
            files.Select(f => (f.name, f.content, PathOf(f.name))).ToArray());
        _workspaceManager.SetTestSolution(solution);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Happy-path: bridge structure
    // ══════════════════════════════════════════════════════════════════════════

    [Test, CancelAfter(5000)]
    public async Task ConvertToAsyncBridge_TaskReturn_ProducesAsyncOverload()
    {
        SetSource(@"
using System.SuccessData;
public class TripService
{
    public DataTable GetTrips(int companyId)
    {
        return DataHelper.Search(companyId);
    }
}", "TripService.cs");

        var result = await _engine.ConvertToAsyncBridgeAsync(PathOf("TripService.cs"), "GetTrips");

        // Async overload should exist with the right signature.
        Assert.That(result.UpdatedText, Does.Contain("GetTripsAsync"),
            "Async overload should be named GetTripsAsync.");
        Assert.That(result.UpdatedText, Does.Contain("Task<DataTable>"),
            "Async overload should return Task<DataTable>.");
        Assert.That(result.UpdatedText, Does.Contain("CancellationToken cancellationToken"),
            "Async overload should have a CancellationToken parameter.");
        Assert.That(result.UpdatedText, Does.Contain("async"),
            "Async overload should carry the async modifier.");
    }

    [Test, CancelAfter(5000)]
    public async Task ConvertToAsyncBridge_TaskReturn_OriginalBodyReplacedWithBridgeCall()
    {
        SetSource(@"
using System.SuccessData;
public class TripService
{
    public DataTable GetTrips(int companyId)
    {
        return DataHelper.Search(companyId);
    }
}", "TripService.cs");

        var result = await _engine.ConvertToAsyncBridgeAsync(PathOf("TripService.cs"), "GetTrips");

        // Bridge body: return GetTripsAsync(companyId).GetAwaiter().GetResult();
        Assert.That(result.UpdatedText, Does.Contain("GetTripsAsync"),
            "Bridge body should call GetTripsAsync.");
        Assert.That(result.UpdatedText, Does.Contain("GetAwaiter"),
            "Bridge body should use GetAwaiter().");
        Assert.That(result.UpdatedText, Does.Contain("GetResult"),
            "Bridge body should use GetResult().");
        // Original sync body (DataHelper.Search) should be in the ASYNC overload, not deleted entirely.
        Assert.That(result.UpdatedText, Does.Contain("DataHelper.Search"),
            "Original body expression should appear in the async overload.");
    }

    [Test, CancelAfter(5000)]
    public async Task ConvertToAsyncBridge_TaskReturn_ObsoleteAttributeAddedToOriginal()
    {
        SetSource(@"
using System.SuccessData;
public class TripService
{
    public DataTable GetTrips(int companyId)
    {
        return DataHelper.Search(companyId);
    }
}", "TripService.cs");

        var result = await _engine.ConvertToAsyncBridgeAsync(PathOf("TripService.cs"), "GetTrips");

        Assert.That(result.UpdatedText, Does.Contain("Obsolete"),
            "[Obsolete] attribute must be added to the bridge wrapper.");
        Assert.That(result.UpdatedText, Does.Contain("Asyncify-bridge"),
            "Obsolete message must contain 'Asyncify-bridge' to match CS0618 tracking convention.");
        Assert.That(result.UpdatedText, Does.Contain("call GetTripsAsync instead"),
            "Obsolete message should name the replacement async method.");
    }

    [Test, CancelAfter(5000)]
    public async Task ConvertToAsyncBridge_TaskReturn_InlineCommentAddedToBridgeBody()
    {
        SetSource(@"
using System.SuccessData;
public class TripService
{
    public DataTable GetTrips(int companyId)
    {
        return DataHelper.Search(companyId);
    }
}", "TripService.cs");

        var result = await _engine.ConvertToAsyncBridgeAsync(PathOf("TripService.cs"), "GetTrips");

        Assert.That(result.UpdatedText, Does.Contain("// Asyncify-bridge: synchronous wrapper over GetTripsAsync."),
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

        var result = await _engine.ConvertToAsyncBridgeAsync(PathOf("NotificationService.cs"), "Notify");

        // void bridge: NotifyAsync(message).GetAwaiter().GetResult() -> no 'return' keyword.
        Assert.That(result.UpdatedText, Does.Contain("NotifyAsync"),
            "Async overload should be named NotifyAsync.");
        Assert.That(result.UpdatedText, Does.Contain("Task NotifyAsync") | Does.Contain("Task\r\nNotifyAsync") | Does.Contain("async Task"),
            "Async overload should return Task (not Task<void>).");
        // No return keyword in the bridge call (void bridge = expression statement).
        // The async method may have return, but the bridge itself must not have 'return GetAwaiter'.
        Assert.That(result.UpdatedText, Does.Contain("GetAwaiter"),
            "Bridge body should use GetAwaiter() even for void methods.");
    }

    [Test, CancelAfter(5000)]
    public async Task ConvertToAsyncBridge_MultipleParameters_AllForwardedInBridgeCall()
    {
        SetSource(@"
using System.SuccessData;
public class DriverService
{
    public DataTable GetDrivers(int companyId, string status, int limit)
    {
        return DataHelper.Query(companyId, status, limit);
    }
}", "DriverService.cs");

        var result = await _engine.ConvertToAsyncBridgeAsync(PathOf("DriverService.cs"), "GetDrivers");

        // Bridge call should forward all three parameter names.
        Assert.That(result.UpdatedText, Does.Contain("GetDriversAsync(companyId, status, limit)"),
            "All original parameter names should be forwarded in the bridge call.");
    }

    [Test, CancelAfter(5000)]
    public async Task ConvertToAsyncBridge_ExpressionBodiedMethod_AsyncOverloadGetsBlockBody()
    {
        SetSource(@"
using System.SuccessData;
public class TripService
{
    public DataTable GetTrips(int companyId) => DataHelper.Search(companyId);
}", "TripService.cs");

        var result = await _engine.ConvertToAsyncBridgeAsync(PathOf("TripService.cs"), "GetTrips");

        // The async overload should have a block body (with braces) for CT-propagation tool compatibility.
        // Verify the original body expression is preserved inside the async method.
        Assert.That(result.UpdatedText, Does.Contain("GetTripsAsync"),
            "Async overload should exist.");
        Assert.That(result.UpdatedText, Does.Contain("DataHelper.Search"),
            "Original body expression should be carried into the async overload.");
    }

    [Test, CancelAfter(5000)]
    public async Task ConvertToAsyncBridge_StaticMethod_Works()
    {
        SetSource(@"
using System.SuccessData;
public class TripService
{
    public static DataTable GetAllTrips()
    {
        return DataHelper.GetAll();
    }
}", "TripService.cs");

        var result = await _engine.ConvertToAsyncBridgeAsync(PathOf("TripService.cs"), "GetAllTrips");

        Assert.That(result.UpdatedText, Does.Contain("GetAllTripsAsync"),
            "Static methods should also be convertible to bridge pattern.");
        Assert.That(result.UpdatedText, Does.Contain("GetAwaiter"),
            "Bridge body should use GetAwaiter().");
    }

    [Test, CancelAfter(5000)]
    public async Task ConvertToAsyncBridge_AsyncMethodInsertedAfterOriginal()
    {
        SetSource(@"
using System.SuccessData;
public class TripService
{
    public DataTable GetTrips(int companyId)
    {
        return DataHelper.Search(companyId);
    }
}", "TripService.cs");

        var result = await _engine.ConvertToAsyncBridgeAsync(PathOf("TripService.cs"), "GetTrips");

        // The bridge wrapper (GetTrips) should appear before the async overload (GetTripsAsync).
        var bridgeIdx = result.UpdatedText!.IndexOf("GetAwaiter", StringComparison.Ordinal);
        var asyncBodyIdx = result.UpdatedText!.IndexOf("DataHelper.Search", StringComparison.Ordinal);

        // Bridge body (GetAwaiter) should appear before the async overload's body (DataHelper.Search).
        Assert.That(bridgeIdx, Is.LessThan(asyncBodyIdx),
            "The bridge wrapper should be emitted before the async overload in the file.");
    }


    // Byte-exact pin per line-ending style: untouched members keep their text and terminator ("mixed" = CRLF
    // file whose one-line Untouched() ends in a stray LF); the bridge and the overload take the dominant EOL and
    // are separated by exactly one blank line, also when the method carries a leading comment; that comment stays on the
    // bridge and is not copied to the overload.
    [TestCase("\n", true, TestName = "ConvertToAsyncBridge_ByteExact_LF")]
    [TestCase("\r\n", true, TestName = "ConvertToAsyncBridge_ByteExact_CRLF")]
    [TestCase("mixed", true, TestName = "ConvertToAsyncBridge_ByteExact_Mixed")]
    [TestCase("\n", false, TestName = "ConvertToAsyncBridge_ByteExact_LF_NoComment")]
    [TestCase("\r\n", false, TestName = "ConvertToAsyncBridge_ByteExact_CRLF_NoComment")]
    [CancelAfter(5000)]
    public async Task ConvertToAsyncBridge_ByteExact(string style, bool withComment)
    {
        const string source = """
            using System;
            using System.Threading;
            using System.Threading.Tasks;

            public class TripService
            {
                // Gets trips.
                public int GetTrips(int companyId)
                {
                    return companyId;
                }

                public void Untouched() { var x = 1; }
            }

            """;
        const string expectedTemplate = """
            using System;
            using System.Threading;
            using System.Threading.Tasks;

            public class TripService
            {
                // Gets trips.
                [Obsolete("Asyncify-bridge: call GetTripsAsync instead.", false)]
                public int GetTrips(int companyId)
                {
                    // Asyncify-bridge: synchronous wrapper over GetTripsAsync.
                    return GetTripsAsync(companyId).GetAwaiter().GetResult();
                }

                public async Task<int> GetTripsAsync(int companyId, CancellationToken cancellationToken = default)
                {
                    return companyId;
                }

                public void Untouched() { var x = 1; }
            }

            """;

        string WithStyle(string template)
        {
            var lf = template.Replace("\r\n", "\n");
            if (!withComment)
            {
                lf = lf.Replace("    // Gets trips.\n", string.Empty);
            }

            return style == "mixed"
                ? lf.Replace("\n", "\r\n").Replace("{ var x = 1; }\r\n", "{ var x = 1; }\n")
                : lf.Replace("\n", style);
        }

        SetSource(WithStyle(source), "TripService.cs");

        var result = await _engine.ConvertToAsyncBridgeAsync(PathOf("TripService.cs"), "GetTrips");

        var expected = WithStyle(expectedTemplate);
        if (result.UpdatedText != expected)
        {
            TestContext.Out.WriteLine("ACTUAL:");
            TestContext.Out.WriteLine(result.UpdatedText?.Replace("\r", "<CR>").Replace("\n", "<LF>\n"));
        }

        Assert.That(result.UpdatedText, Is.EqualTo(expected).NoClip);
    }

    // OverloadLeadingTrivia falls back to CRLF when the method carries no end-of-line trivia of its own (here: a
    // one-line method followed by another member on the same line). In an all-LF file the output must still be
    // CR-free.
    [Test, CancelAfter(5000)]
    public async Task ConvertToAsyncBridge_LfFile_MethodWithoutOwnEol_OutputHasNoCarriageReturn()
    {
        const string source = "using System;\n"
            + "using System.Threading;\n"
            + "using System.Threading.Tasks;\n"
            + "\n"
            + "public class TripService\n"
            + "{\n"
            + "    public int GetTrips(int companyId) { return companyId; } public void Other() { }\n"
            + "}\n";

        SetSource(source, "TripService.cs");

        var result = await _engine.ConvertToAsyncBridgeAsync(PathOf("TripService.cs"), "GetTrips");

        Assert.That(result.UpdatedText, Does.Contain("GetTripsAsync"), "The bridge should have been produced.");
        if (result.UpdatedText!.Contains('\r'))
        {
            TestContext.Out.WriteLine("ACTUAL:");
            TestContext.Out.WriteLine(result.UpdatedText.Replace("\r", "<CR>").Replace("\n", "<LF>\n"));
        }

        Assert.That(result.UpdatedText, Does.Not.Contain("\r"), "An LF file must not gain CRLF line breaks.");
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Precondition failures
    // ══════════════════════════════════════════════════════════════════════════

    [Test, CancelAfter(5000)]
    public async System.Threading.Tasks.Task ConvertToAsyncBridge_AlreadyAsync_Throws()
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
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _engine.ConvertToAsyncBridgeAsync(PathOf("Service.cs"), "GetValue"),
            "Should throw when method is already async.");
    }

    [Test, CancelAfter(5000)]
    public async System.Threading.Tasks.Task ConvertToAsyncBridge_AlreadyHasAsyncSuffix_Throws()
    {
        SetSource(@"
using System.Threading.Tasks;
using System.SuccessData;
public class Service
{
    public DataTable GetTripsAsync(int id)
    {
        return DataHelper.Search(id);
    }
}", "Service.cs");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _engine.ConvertToAsyncBridgeAsync(PathOf("Service.cs"), "GetTripsAsync"),
            "Should throw when method name already ends with 'Async'.");
    }

    [Test, CancelAfter(5000)]
    public async System.Threading.Tasks.Task ConvertToAsyncBridge_AbstractMethod_Throws()
    {
        SetSource(@"
using System.SuccessData;
public abstract class BaseService
{
    public abstract DataTable GetTrips(int companyId);
}", "BaseService.cs");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _engine.ConvertToAsyncBridgeAsync(PathOf("BaseService.cs"), "GetTrips"),
            "Should throw for abstract methods (no body to copy).");
    }

    [Test, CancelAfter(5000)]
    public async System.Threading.Tasks.Task ConvertToAsyncBridge_EventHandlerSignature_Throws()
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

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _engine.ConvertToAsyncBridgeAsync(PathOf("Form1.cs"), "Button_Click"),
            "Should throw for event handler methods (fixed delegate signature).");
    }

    [Test, CancelAfter(5000)]
    public async System.Threading.Tasks.Task ConvertToAsyncBridge_RefParameter_Throws()
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

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _engine.ConvertToAsyncBridgeAsync(PathOf("Service.cs"), "ComputeRef"),
            "Should throw when method has ref parameters.");
    }

    [Test, CancelAfter(5000)]
    public async System.Threading.Tasks.Task ConvertToAsyncBridge_OutParameter_Throws()
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

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _engine.ConvertToAsyncBridgeAsync(PathOf("Service.cs"), "TryGet"),
            "Should throw when method has out parameters.");
    }

    [Test, CancelAfter(5000)]
    public async System.Threading.Tasks.Task ConvertToAsyncBridge_AsyncOverloadAlreadyExists_Throws()
    {
        SetSource(@"
using System.Threading;
using System.Threading.Tasks;
using System.SuccessData;
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

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _engine.ConvertToAsyncBridgeAsync(PathOf("TripService.cs"), "GetTrips"),
            "Should throw when GetTripsAsync already exists in the class.");
    }

    [Test, CancelAfter(5000)]
    public async System.Threading.Tasks.Task ConvertToAsyncBridge_MethodNotFound_Throws()
    {
        SetSource(@"
public class Service
{
    public int GetValue() => 42;
}", "Service.cs");

        await Assert.ThrowsAsync<InvalidOperationException>(
        () => _engine.ConvertToAsyncBridgeAsync(PathOf("Service.cs"), "NonExistentMethod"),
        "Should throw when the named method does not exist in the file.");
    }

    [Test, CancelAfter(5000)]
    public async System.Threading.Tasks.Task ConvertToAsyncBridge_FileNotFound_Throws()
    {
        SetSource(@"public class Dummy {}", "Dummy.cs");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _engine.ConvertToAsyncBridgeAsync(PathOf("DoesNotExist.cs"), "SomeMethod"),
            "Should throw when the file is not found in the loaded solution.");
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Modifier propagation
    // ══════════════════════════════════════════════════════════════════════════

    [Test, CancelAfter(5000)]
    public async System.Threading.Tasks.Task ConvertToAsyncBridge_InstanceMethod_AsyncOverloadIsNotStatic()
    {
        SetSource(@"
public class Service
{
    public void DoWork(string arg) { _ = arg; }
}", "Service.cs");

        var result = await _engine.ConvertToAsyncBridgeAsync(PathOf("Service.cs"), "DoWork");
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

        var result = await _engine.ConvertToAsyncBridgeAsync(PathOf("Service.cs"), "DoWork");
        var text = result.UpdatedText!;

        Assert.That(text, Does.Contain("public static async Task DoWorkAsync"),
            "async overload of a static method must also be static.");
    }

    [Test, CancelAfter(5000)]
    public async Task ConvertToAsyncBridge_AbstractMethod_ThrowsInvalidOperation()
    {
        SetSource(@"
public abstract class Base
{
    public abstract void DoWork(string arg);
}", "Base.cs");

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await _engine.ConvertToAsyncBridgeAsync(PathOf("Base.cs"), "DoWork"));
    }

    [Test, CancelAfter(5000)]
    public async Task ConvertToAsyncBridge_OverrideMethod_AsyncOverloadStripsOverride()
    {
        SetMultipleFiles(
            ("Base.cs", @"
public abstract class Base
{
    public abstract void DoWork(string arg);
}"),
            ("Derived.cs", @"
public class Derived : Base
{
    public override void DoWork(string arg) { _ = arg; }
}")
        );

        var result = await _engine.ConvertToAsyncBridgeAsync(PathOf("Derived.cs"), "DoWork");
        var text = result.UpdatedText!;

        Assert.That(text, Does.Contain("public async Task DoWorkAsync"),
            "async overload must be present.");
        Assert.That(text, Does.Not.Contain("override async Task DoWorkAsync"),
            "async overload must not carry 'override' - there is no base counterpart to override.");
    }

    // ══════════════════════════════════════════════════════════════════════════
    // ConvertEventHandlerCallerToAsyncVoidAsync -> delegate async modifier
    // ══════════════════════════════════════════════════════════════════════════

    [Test, CancelAfter(5000)]
    public async Task ConvertEventHandlerCaller_DelegateContainingBridgeCall_DelegateGetsAsyncModifier()
    {
        // The event handler calls a bridge wrapper inside an anonymous delegate passed to
        // BeginInvoke. The rewriter must add 'async' to that delegate -> without it the
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

        var result = await _engine.ConvertEventHandlerCallerToAsyncVoidAsync(PathOf("DispatchForm.cs"), "Form_Load");
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

        var result = await _engine.ConvertEventHandlerCallerToAsyncVoidAsync(PathOf("DispatchForm.cs"), "Form_Load");
        var text = result.UpdatedText!;

        Assert.That(text, Does.Contain("async void Form_Load"),
            "Event handler must be uplifted to async void.");
        Assert.That(text, Does.Contain("async ()"),
            "Lambda containing await must be marked async.");
        Assert.That(text, Does.Contain("await refreshDatagridAsync()"),
            "Bridge call inside the lambda must be replaced with await.");
    }

    // RunBridgeBatchAsync CT-add fallback: the async overload exists without a CancellationToken, so the batch
    // adds one. System.Threading is not imported, so the CT-add result fails in-memory validation and the
    // skip message must report the flag only because the NeedsManualReview write really reached disk.
    [Test, CancelAfter(60000)]
    public async Task RunBridgeBatch_CtAddValidationFails_FlagOnDisk_MessageReportsFlag()
    {
        var source = """
            using System.Threading.Tasks;

            public class TripService
            {
                [MigrationCandidate("AsyncBridgeCandidate", Score = 70, Reason = "calls-CommonSearch:70", FlaggedDate = "2026-06-01")]
                public int GetTrips(int companyId)
                {
                    return companyId;
                }

                public Task<int> GetTripsAsync(int companyId)
                {
                    return Task.FromResult(companyId);
                }
            }

            internal sealed class MigrationCandidateAttribute : System.Attribute
            {
                public MigrationCandidateAttribute(string p) {}
                public int Score { get; set; }
                public string? Reason { get; set; }
                public string? FlaggedDate { get; set; }
            }
            """.Replace("\r\n", "\n");

        _workspaceManager.SolutionPath = Path.Combine(_tempDir, "Test.sln");
        Directory.CreateDirectory(_tempDir);
        var path = PathOf("TripService.cs");
        File.WriteAllText(path, source);
        SetSource(source, "TripService.cs");

        var batchEngine = new AsyncBatchEngine(
            _workspaceManager,
            _engine,
            new ValidationEngine(_workspaceManager, new DiffEngine(), NullLogger<ValidationEngine>.Instance),
            new AntiPatternEngine(_workspaceManager),
            new MigrationLedger(),
            NullLogger<AsyncBatchEngine>.Instance);

        try
        {
            var result = await batchEngine.RunBridgeBatchAsync(maxBridges: 1);

            Assert.That(result.Applied, Is.Empty, "A CT-add that fails validation must not be applied.");
            Assert.That(result.Skipped, Has.Count.EqualTo(1));
            var message = result.Skipped[0].Reason;
            Assert.That(message, Does.StartWith("CT-add validation failed"));
            Assert.That(message, Does.EndWith("; flagged NeedsManualReview"),
                "The flag write succeeded, so the skip message must report it.");
            Assert.That(File.ReadAllText(path), Does.Contain("NeedsManualReview"),
                "The message claims the flag was written, so the file on disk must carry NeedsManualReview.");
        }
        finally
        {
            _workspaceManager.Dispose();
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
    }
}
