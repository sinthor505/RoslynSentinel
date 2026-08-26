#pragma warning disable CS8618
using Microsoft.Extensions.Logging.Abstractions;

namespace RoslynSentinel.Tests.Battery;

/// <summary>
/// Battery #5 — First-ever functional tests for engines with 0–1 test coverage:
///   C. SecurityAndSafetyEngine  (5 tests) — FindUnsafeTypeCasts (real logic), DetectMissingNullChecks
///   D. InstrumentationEngine    (5 tests) — AddTryCatch, AddTryCatchToClass, AddStopwatch
///
/// Total: 10 tests. All workspace-based (SetSource / SetTestSolution).
/// </summary>

// ════════════════════════════════════════════════════════════════════════════════
// C. SecurityAndSafetyEngine
// ════════════════════════════════════════════════════════════════════════════════
[TestFixture]
public class SecurityAndSafetyEngineTests
{
    private IWorkspaceManager _workspaceManager;
    private SecurityAndSafetyEngine _engine;

    [SetUp]
    public void Setup()
    {
        _workspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        _engine = new SecurityAndSafetyEngine(_workspaceManager);
    }

    [TearDown]
    public void TearDown() => _workspaceManager?.Dispose();

    private void SetSource(string source, string fileName = "Test.cs")
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [(fileName, source)]);
        _workspaceManager.SetTestSolution(solution);
    }

    [Test]
    public async Task FindUnsafeTypeCasts_WithDirectCast_ReportsUnsafeCastIssue()
    {
        SetSource(@"
public class Processor
{
    public void Process(object input)
    {
        var value = (int)input; // Direct cast — should flag UnsafeCast
    }
}");
        var issues = await _engine.FindUnsafeTypeCastsAsync("Test.cs");

        Assert.That(issues, Is.Not.Empty);
        Assert.That(issues.Any(i => i.Type == "UnsafeCast"), Is.True);
        Assert.That(issues[0].Description, Does.Contain("'as' operator"), "Should recommend safer alternative");
    }

    [Test]
    public async Task FindUnsafeTypeCasts_NoCasts_ReturnsEmpty()
    {
        SetSource(@"
public class Processor
{
    public string Process(object input)
    {
        return input as string ?? string.Empty; // Safe — no direct cast
    }
}");
        var issues = await _engine.FindUnsafeTypeCastsAsync("Test.cs");

        Assert.That(issues, Is.Empty, "Safe 'as' cast should not flag UnsafeCast");
    }

    [Test]
    public async Task FindUnsafeTypeCasts_MultipleCasts_ReportsAllInstances()
    {
        SetSource(@"
public class Converter
{
    public void Convert(object a, object b, object c)
    {
        var x = (int)a;
        var y = (string)b;
        var z = (double)c;
    }
}");
        var issues = await _engine.FindUnsafeTypeCastsAsync("Test.cs");

        Assert.That(issues.Count, Is.EqualTo(3), "Should report all three direct casts");
        Assert.That(issues.All(i => i.Type == "UnsafeCast"), Is.True);
    }

    [Test]
    public async Task FindUnsafeTypeCasts_FileNotFound_ThrowsFileNotFound()
    {
        SetSource("public class C { }", "Test.cs");

        await Assert.ThrowsAsync<FileNotFoundException>(async () =>
            await _engine.FindUnsafeTypeCastsAsync("NonExistent.cs"));
    }

    [Test]
    public async Task DetectMissingNullChecks_PublicMethod_UnguardedReferenceParam_IsReported()
    {
        // Formerly documented as stub — now properly implemented.
        // Public method uses reference-type parameter without null guard = MissingNullCheck.
        SetSource(@"
public class Service
{
    private readonly string _name;
    public Service(string name) { _name = name; }
    public int Length() => _name.Length;
}");
        var issues = await _engine.DetectMissingNullChecksAsync("Test.cs");

        Assert.That(issues, Is.Not.Empty, "Constructor accepting 'string name' without null guard must be flagged.");
        Assert.That(issues.Any(i => i.Type == "MissingNullCheck"), Is.True);
    }

    [Test]
    public async Task DetectMissingNullChecks_PrivateMethod_IsNotFlagged()
    {
        // Only public methods are checked — private methods are trusted internal callers.
        SetSource(@"
public class Service
{
    private void Process(string value) { var x = value.Length; }
}");
        var issues = await _engine.DetectMissingNullChecksAsync("Test.cs");
        Assert.That(issues.Any(i => i.Description.Contains("value")), Is.False,
            "Private methods must NOT be flagged for missing null checks.");
    }

    [Test]
    public async Task DetectMissingNullChecks_NullableReferenceParam_IsNotFlagged()
    {
        // string? is explicitly nullable — the caller knows it can be null.
        SetSource(@"
public class Service
{
    public void Process(string? value) { var x = value?.Length; }
}");
        var issues = await _engine.DetectMissingNullChecksAsync("Test.cs");
        Assert.That(issues.Any(i => i.Description.Contains("value")), Is.False,
            "Nullable reference parameters must NOT be flagged.");
    }

    [Test]
    public async Task DetectMissingNullChecks_WithIsNullGuard_IsNotFlagged()
    {
        SetSource(@"
public class Service
{
    public void Process(string value)
    {
        if (value is null) throw new ArgumentNullException(nameof(value));
        var x = value.Length;
    }
}");
        var issues = await _engine.DetectMissingNullChecksAsync("Test.cs");
        Assert.That(issues.Any(i => i.Description.Contains("value")), Is.False,
            "'is null' pattern guard must prevent flagging.");
    }

    [Test]
    public async Task DetectMissingNullChecks_WithIsNotNullGuard_IsNotFlagged()
    {
        SetSource(@"
public class Service
{
    public void Process(string value)
    {
        if (value is not null) _ = value.Length;
    }
}");
        var issues = await _engine.DetectMissingNullChecksAsync("Test.cs");
        Assert.That(issues.Any(i => i.Description.Contains("value")), Is.False,
            "'is not null' pattern guard must prevent flagging.");
    }

    [Test]
    public async Task DetectMissingNullChecks_WithThrowIfNullOrEmpty_IsNotFlagged()
    {
        SetSource(@"
public class Service
{
    public void Process(string value)
    {
        ArgumentNullException.ThrowIfNullOrEmpty(value);
        _ = value.Length;
    }
}");
        var issues = await _engine.DetectMissingNullChecksAsync("Test.cs");
        Assert.That(issues.Any(i => i.Description.Contains("value")), Is.False,
            "ThrowIfNullOrEmpty guard must prevent flagging.");
    }

    [Test]
    public async Task DetectMissingNullChecks_ValueTypeParam_IsNotFlagged()
    {
        // int/bool/struct params cannot be null — should never be flagged.
        SetSource(@"
public class Service
{
    public void Process(int count) { _ = count + 1; }
}");
        var issues = await _engine.DetectMissingNullChecksAsync("Test.cs");
        Assert.That(issues.Any(i => i.Description.Contains("count")), Is.False,
            "Value type parameters must NOT be flagged.");
    }

    [Test]
    public async Task DetectMissingNullChecks_UnusedParam_IsNotFlagged()
    {
        // Unused params cannot cause a null dereference in the method body.
        SetSource(@"
public class Service
{
    public void Process(string value) { _ = ""constant""; }
}");
        var issues = await _engine.DetectMissingNullChecksAsync("Test.cs");
        Assert.That(issues.Any(i => i.Description.Contains("value")), Is.False,
            "Unused parameters must NOT be flagged.");
    }

    [Test]
    public async Task DetectMissingNullChecks_ParamWithNullDefault_IsNotFlagged()
    {
        // Optional null default means the param is intentionally nullable by API design.
        SetSource(@"
public class Service
{
    public void Process(string value = null) { _ = value?.Length ?? 0; }
}");
        var issues = await _engine.DetectMissingNullChecksAsync("Test.cs");
        Assert.That(issues.Any(i => i.Description.Contains("value")), Is.False,
            "Optional params with null default must NOT be flagged.");
    }

    [Test]
    public async Task DetectMissingNullChecks_MultipleParams_OnlyUnguardedFlagged()
    {
        SetSource(@"
public class Service
{
    public void Process(string safe, string risky)
    {
        ArgumentNullException.ThrowIfNull(safe);
        _ = safe.Length + risky.Length;
    }
}");
        var issues = await _engine.DetectMissingNullChecksAsync("Test.cs");
        Assert.That(issues.Any(i => i.Description.Contains("risky")), Is.True,
            "Unguarded 'risky' param must be flagged.");
        Assert.That(issues.Any(i => i.Description.Contains("safe")), Is.False,
            "Guarded 'safe' param must NOT be flagged.");
    }
}

// ════════════════════════════════════════════════════════════════════════════════
// D. InstrumentationEngine
// ════════════════════════════════════════════════════════════════════════════════
[TestFixture]
public class InstrumentationEngineTests
{
    private IWorkspaceManager _workspaceManager;
    private InstrumentationEngine _engine;

    [SetUp]
    public void Setup()
    {
        _workspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        _engine = new InstrumentationEngine(_workspaceManager);
    }

    [TearDown]
    public void TearDown() => _workspaceManager?.Dispose();

    private void SetSource(string source, string fileName = "Test.cs")
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [(fileName, source)]);
        _workspaceManager.SetTestSolution(solution);
    }

    [Test]
    public async Task AddTryCatch_ToSingleMethod_WrapsBodyInTryCatch()
    {
        SetSource(@"
public class OrderService
{
    public void SubmitOrder(int orderId)
    {
        var result = orderId * 2;
    }
}");
        var result = await _engine.AddTryCatchToMethodAsync("Test.cs", "SubmitOrder");

        Assert.That(result.UpdatedText, Does.Contain("try"), "Output should contain try block");
        Assert.That(result.UpdatedText, Does.Contain("catch"), "Output should contain catch block");
        Assert.That(result.UpdatedText, Does.Contain("Exception"), "Catch should use Exception type by default");
        Assert.That(result.UpdatedText, Does.Contain("SubmitOrder"), "Method name should be preserved");
    }

    [Test]
    public async Task AddTryCatch_WithCustomExceptionTypeAndFinally_IncludesAll()
    {
        SetSource(@"
public class FileProcessor
{
    public void ProcessFile(string path)
    {
        System.IO.File.ReadAllText(path);
    }
}");
        var result = await _engine.AddTryCatchToMethodAsync("Test.cs", "ProcessFile",
            exceptionType: "IOException", addFinally: true);

        Assert.That(result.UpdatedText, Does.Contain("IOException"), "Should use custom exception type");
        Assert.That(result.UpdatedText, Does.Contain("finally"), "Should include finally block when requested");
    }

    [Test]
    public async Task AddTryCatch_ToClass_WrapsAllPublicMethodsNotPrivate()
    {
        SetSource(@"
public class UserService
{
    public void CreateUser(string name) { var x = name; }
    public void DeleteUser(int id) { var y = id; }
    private void AuditLog(string msg) { var z = msg; }
}");
        var result = await _engine.AddTryCatchToClassAsync("Test.cs", "UserService");

        // Each public method should have a try/catch wrapper
        // Count 'try' occurrences — should be 2 (CreateUser + DeleteUser), not 3
        var tryCount = CountOccurrences(result.UpdatedText!, "try");
        Assert.That(tryCount, Is.EqualTo(2), "Should wrap exactly 2 public methods");
        Assert.That(result.UpdatedText, Does.Contain("AuditLog"), "Private method should still appear but without wrapping");
    }

    [Test]
    public async Task AddStopwatch_ToMethod_InjectsStopwatchAndFinallyLog()
    {
        SetSource(@"
public class MetricsService
{
    public void RunQuery()
    {
        var data = new int[10];
    }
}");
        var result = await _engine.AddStopwatchDiagnosticsAsync("Test.cs", "RunQuery");

        Assert.That(result.UpdatedText, Does.Contain("Stopwatch"), "Should inject Stopwatch");
        Assert.That(result.UpdatedText, Does.Contain("StartNew"), "Should start a new stopwatch");
        Assert.That(result.UpdatedText, Does.Contain("finally"), "Should use finally block for guaranteed logging");
        Assert.That(result.UpdatedText, Does.Contain("ElapsedMilliseconds"), "Should log elapsed time");
        Assert.That(result.UpdatedText, Does.Contain("System.Diagnostics"), "Should add using directive for Diagnostics");
    }

    [Test]
    public async Task AddTryCatch_MethodNotFound_ThrowsException()
    {
        SetSource(@"public class C { public void Existing() { } }");

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await _engine.AddTryCatchToMethodAsync("Test.cs", "NonExistentMethod"));
    }

    // Helper: count non-overlapping occurrences of a substring
    private static int CountOccurrences(string text, string pattern)
    {
        int count = 0, idx = 0;
        while ((idx = text.IndexOf(pattern, idx, StringComparison.Ordinal)) >= 0)
        {
            count++;
            idx += pattern.Length;
        }
        return count;
    }
}
