// Coverage for plan_error_codes_follow_cause.md, slice (2): CodeTransformTools decides success from the engine
// result's Outcome, not from whether UpdatedText is empty. Runs the real tool class against the real engines
// resolved from the production registration, over an in-memory test solution.
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Server.Advanced;
using RoslynSentinel.Tools.Experimental;

namespace RoslynSentinel.Tests.Server;

[TestFixture]
[Category("CodeTransformTools")]
public class CodeTransformToolsOutcomeTests
{
    private ServiceProvider _provider = null!;
    private IWorkspaceManager _workspace = null!;
    private CodeTransformTools _tools = null!;

    [SetUp]
    public void SetUp()
    {
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddRoslynSentinelEnginesAdvanced();
        var mcpBuilder = services.AddMcpServer();
        mcpBuilder.AddRoslynSentinelToolsAdvanced(
            services, new HashSet<string>(["Experimental"], StringComparer.OrdinalIgnoreCase));

        _provider = services.BuildServiceProvider();
        _workspace = _provider.GetRequiredService<IWorkspaceManager>();
        _tools = _provider.GetRequiredService<CodeTransformTools>();
    }

    [TearDown]
    public void TearDown() => _provider.Dispose();

    // Absolute path: Generate (unlike the method-scope transforms) rejects an unvalidated relative path, and an
    // in-memory test solution has no root to resolve a relative one against.
    private string SetSource(string fileName, string source)
    {
        var path = Path.Combine(Path.GetTempPath(), "TestProj", fileName);
        _workspace.SetTestSolution(RoslynSentinel.Tests.TestSolutionBuilder.CreateSolutionWithProject("TestProj", [(path, source)]));
        return path;
    }

    private const string CounterWithoutTargetMethod = @"
public class Counter
{
    private int _n;
    public void Other() { _n++; }
}";

    // The engines report facts (outcome, optional code, a short reason); the tool owns every word of guidance.
    // Each test pins one row of the per-tool behaviour table in the slice (2) rework.

    // (a) A tool whose own pre-guard branch was an ERROR keeps that error and its hand-written hint, now carrying
    // the engine's code (NotFound / TargetIneligible) instead of the generic Exception - and never the engine's
    // Message verbatim (this engine puts the WHOLE source file in it).
    [Test]
    public async Task ConvertMethodToIndexer_MethodNotFound_IsNotFoundErrorWithTheToolsHintAndNoSource()
    {
        var path = SetSource("Counter.cs", CounterWithoutTargetMethod);

        var result = await _tools.ApplyMethodCodeTransform("outcome test", path, "Missing", "convert_method_to_indexer");

        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData?.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound), "the engine's code, not the generic Exception");
        var message = result.ErrorData!.Message;
        Assert.That(message, Does.Contain("Method 'Missing' not found in Counter.cs."));
        Assert.That(message, Does.Not.Contain("private int _n"), "the engine puts the whole file in Message; it must not reach the agent");
        Assert.That(message.Length, Is.LessThan(600));
    }

    [Test]
    public async Task ConvertMethodToIndexer_TwoParameters_ReportsTheEnginesSpecificReason()
    {
        var path = SetSource("Counter.cs", "public class Counter\n{\n    public int Add(int a, int b) { return a + b; }\n}");

        var result = await _tools.ApplyMethodCodeTransform("outcome test", path, "Add", "convert_method_to_indexer");

        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData?.ErrorCode, Is.EqualTo(ToolErrorCode.TargetIneligible), "the engine throws TargetIneligible for a wrong parameter count, not NotFound");
        Assert.That(result.ErrorData!.Message, Does.Contain("exactly one parameter (has 2)"));
    }

    // OptimizeToValueTask puts its reason in UpdatedText ("// WARNING: ..." ahead of the whole file) and leaves
    // Message empty. The pre-guard tool shipped that text as the new file; the guard first produced an empty
    // "(CannotOptimize)." message. Now the engine throws ToolTargetIneligibleException, which the tool maps to
    // IsError with ErrorCode TargetIneligible and the engine's one-line reason, plus no source.
    [Test]
    public async Task OptimizeToValueTask_TooManyAwaits_IsTargetIneligibleWithTheEnginesReason()
    {
        var path = SetSource("Svc.cs", "using System.Threading.Tasks;\npublic class Svc\n{\n    public async Task<int> Work()\n    {\n        await Task.Delay(1);\n        await Task.Delay(2);\n        return 1;\n    }\n}");

        var result = await _tools.ApplyMethodCodeTransform("outcome test", path, "Work", "optimize_to_value_task");

        Assert.That(result.IsError, Is.True, "the warning-prefixed whole file must not be returned as the transformed source");
        Assert.That(result.ErrorData?.ErrorCode, Is.EqualTo(ToolErrorCode.TargetIneligible));
        var message = result.ErrorData!.Message;
        Assert.That(message, Does.Contain("Cannot safely convert to ValueTask: method has 2 await expressions."));
        Assert.That(message, Does.Not.Contain("public class Svc"));
        Assert.That(message, Does.Not.Contain("(CannotOptimize)."), "the empty-reason message of the reviewed-bad guard");
    }

    [Test]
    public async Task OptimizeToValueTask_MethodDoesNotReturnTask_IsTargetIneligibleWithoutTheSource()
    {
        var path = SetSource("Svc.cs", "public class Svc\n{\n    public int Plain() { return 1; }\n}");

        var result = await _tools.ApplyMethodCodeTransform("outcome test", path, "Plain", "optimize_to_value_task");

        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData?.ErrorCode, Is.EqualTo(ToolErrorCode.TargetIneligible));
        Assert.That(result.ErrorData!.Message, Does.Contain("method does not return Task."));
        Assert.That(result.ErrorData.Message, Does.Not.Contain("public class Svc"), "this engine puts the whole file after the warning line");
    }

    [Test]
    public async Task ConvertStaticToExtension_AlreadyAnExtension_IsAnErrorNotTheUnchangedFileAsASuccess()
    {
        var path = SetSource("Ext.cs", "public static class Ext\n{\n    public static int Twice(this int x) { return x * 2; }\n}");

        var result = await _tools.ApplyMethodCodeTransform("outcome test", path, "Twice", "convert_static_to_extension");

        // The engine throws ToolTargetIneligibleException; the tool maps it to an IsError result.
        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData?.ErrorCode, Is.EqualTo(ToolErrorCode.TargetIneligible));
        Assert.That(result.ErrorData!.Message, Does.Contain("The method must be static and have at least one parameter to become the 'this' parameter."));
    }

    // The engine throws ToolNotFoundException for a missing method; the tool maps it to an IsError result.
    [Test]
    public async Task AddGuardClauses_MethodNotFound_IsNotFoundError()
    {
        var path = SetSource("Counter.cs", CounterWithoutTargetMethod);

        var result = await _tools.ApplyMethodCodeTransform("outcome test", path, "Missing", "add_guard_clauses");

        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData?.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound));
        Assert.That(result.ErrorData!.Message, Does.Contain("Method or body not found"));
    }

    [Test]
    public async Task OptimizeIndependentAwaits_MethodNotFound_IsNotFoundError()
    {
        var path = SetSource("Counter.cs", CounterWithoutTargetMethod);

        var result = await _tools.ApplyMethodCodeTransform("outcome test", path, "Missing", "optimize_independent_awaits");

        Assert.That(result.IsError, Is.True, "a missing method must be an error, not a success message");
        Assert.That(result.ErrorData?.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound));
        var message = result.ErrorData!.Message;
        Assert.That(message, Does.Contain("Method 'Missing' not found"));
        Assert.That(message, Does.Not.Contain("public class Counter"), "the error must not echo the file source");
    }

    // The pre-guard tool used any non-empty UpdatedText, and this engine returns the unchanged file with a
    // nothing-to-do outcome: still a success carrying the transform result.
    [Test]
    public async Task ConvertLockToSemaphoreSlim_MethodWithoutLock_IsNotFoundError()
    {
        var path = SetSource("Counter.cs", "public class Counter\n{\n    private int _n;\n    public void Increment() { _n++; }\n}");

        var result = await _tools.ApplyMethodCodeTransform("outcome test", path, "Increment", "convert_lock_to_semaphore_slim");

        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData?.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound));
        Assert.That(result.ErrorData!.Message, Does.Contain("No lock statement found in method"));
    }

    [Test]
    public async Task AddGuardClauses_NoReferenceTypeParameters_StaysASuccess()
    {
        var path = SetSource("Counter.cs", "public class Counter\n{\n    public int Twice(int x) { return x * 2; }\n}");

        var result = await _tools.ApplyMethodCodeTransform("outcome test", path, "Twice", "add_guard_clauses");

        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        Assert.That(result.SuccessData, Is.TypeOf<SourceTransformResult>());
        var transform = (SourceTransformResult)result.SuccessData!;
        Assert.That(transform.WroteToFile, Is.False);
        Assert.That(transform.WorkspaceUpdated, Is.False);
        // TODO add_guard_clauses no-op returns a SourceTransformResult, not the 'No parameters eligible' text; assert UpdatedSource equals the input once confirmed
    }

    [Test]
    public async Task GenerateConstructor_ClassWithoutFields_StaysASuccess()
    {
        var path = SetSource("Empty.cs", "public class Empty\n{\n    public int P { get; set; }\n}");

        var result = await _tools.Generate(CodeTransformKind.generate_constructor, "outcome test", path, className: "Empty");

        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        Assert.That(result.SuccessData, Is.TypeOf<string>());
        Assert.That((string)result.SuccessData!, Does.Contain("No fields to initialize in constructor"));
    }

    // (c) A genuine engine failure (Error / DocumentNotFound / SourceInvalid ...) is an error with the engine's
    // code, the engine's reason cut to one line, and the tool's hint.
    [Test]
    public async Task ConvertLockToSemaphoreSlim_MethodNotFound_IsNotFoundError()
    {
        var path = SetSource("Counter.cs", CounterWithoutTargetMethod);

        var result = await _tools.ApplyMethodCodeTransform("outcome test", path, "Missing", "convert_lock_to_semaphore_slim");

        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData?.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound));
        Assert.That(result.ErrorData!.Message, Does.Contain("Missing"));
    }

    [Test]
    public async Task GenerateConstructor_ClassNotFound_IsNotFoundError()
    {
        var path = SetSource("Counter.cs", CounterWithoutTargetMethod);

        var result = await _tools.Generate(CodeTransformKind.generate_constructor, "outcome test", path, className: "Missing");

        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData?.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound));
        Assert.That(result.ErrorData!.Message, Does.Contain("Class 'Missing' not found"));
    }

    // (d) AlreadyInTargetState is a success with an explicit message; nothing is written (the tool never
    // writes, and SuccessData carries no new file text).
    [Test]
    public async Task GenerateConstructor_ClassAlreadyHasConstructor_IsSuccessWithAlreadyInStateMessage()
    {
        var path = SetSource("MyService.cs", @"
public class MyService
{
    private readonly string _name;

    public MyService(string name)
    {
        _name = name;
    }
}");

        var result = await _tools.Generate(CodeTransformKind.generate_constructor, "outcome test", path, className: "MyService");

        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        Assert.That(result.Code, Is.Null);
        Assert.That(result.SuccessData, Is.TypeOf<string>());
        Assert.That((string)result.SuccessData!, Does.Contain("already in the requested state"));
    }

    // The Modified path must be unaffected by the outcome gate.
    [Test]
    public async Task GenerateConstructor_ClassWithFields_StillSucceedsWithTheGeneratedSummary()
    {
        var path = SetSource("EmailService.cs", @"
public class EmailService
{
    private readonly string _smtpHost;
    private readonly int _port;
}");

        var result = await _tools.Generate(CodeTransformKind.generate_constructor, "outcome test", path, className: "EmailService");

        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        Assert.That(result.SuccessData, Is.TypeOf<string>());
        Assert.That((string)result.SuccessData!, Does.Contain("Outcome"));
        Assert.That((string)result.SuccessData!, Does.Not.Contain("already in the requested state"));
        // DEFERRED (accepted 2026-10-22, finding #22): the generate_constructor success summary does not show the generated constructor text.
        // Left as-is on purpose until scheduled; do not re-raise in reviews.
    }

    [Test]
    public async Task ConvertLockToSemaphoreSlim_MethodWithLock_StillReturnsTheTransformedSource()
    {
        var path = SetSource("Counter.cs", @"
public class Counter
{
    private readonly object _gate = new object();
    private int _n;
    public void Increment()
    {
        lock (_gate)
        {
            _n++;
        }
    }
}");

        var result = await _tools.ApplyMethodCodeTransform("outcome test", path, "Increment", "convert_lock_to_semaphore_slim");

        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        var transform = (SourceTransformResult)result.SuccessData!;
        Assert.That(transform.UpdatedSource, Does.Contain("SemaphoreSlim"));
        Assert.That(transform.UpdatedSource, Does.Not.Contain("lock ("));
        Assert.That(transform.WroteToFile, Is.False);
    }

    // The engine THROWS ToolTargetIneligibleException for a non-interface base; the tool has no inner catch for it,
    // so the outer catch maps it via ToolErrorMapper. Pins that the actionable text reaches the caller with the
    // TargetIneligible code (not a generic or SolutionNotLoaded error) and leaks no stack trace.
    [Test]
    public async Task ConvertToBackgroundService_NonInterfaceBaseClass_ToolResultCarriesTheRefusalMessage()
    {
        var path = SetSource("W.cs", "public class Foo { }\npublic class W : Foo\n{\n}");

        var result = await _tools.ApplyClassCodeTransform("outcome test", path, "W", "convert_to_background_service");

        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData?.ErrorCode, Is.EqualTo(ToolErrorCode.TargetIneligible));
        Assert.That(result.ErrorData!.Message, Does.Contain("already derives from 'Foo'"));
        Assert.That(result.ErrorData.Message, Does.Contain("Convert it manually or remove the base class first"));
        Assert.That(result.ErrorData.Message, Does.Not.Contain("   at "), "no stack trace in the tool result");
    }
}
