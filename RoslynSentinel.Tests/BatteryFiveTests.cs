#pragma warning disable CS8618
using Microsoft.Extensions.Logging.Abstractions;
using RoslynSentinel.Server;

namespace RoslynSentinel.Tests;

/// <summary>
/// Battery #5 — First-ever functional tests for five engines with 0–1 test coverage:
///   A. CodeSmellAndStyleEngine  (4 tests) — EPC33 detection, no-match, stub UseSwitchExpression
///   B. AnalyzerEngineStubs      (4 tests) — ExhaustiveAnalyzerEngine + MassiveAnalyzerEngine stubs
///   C. SecurityAndSafetyEngine  (5 tests) — FindUnsafeTypeCasts (real logic), DetectMissingNullChecks
///   D. InstrumentationEngine    (5 tests) — AddTryCatch, AddTryCatchToClass, AddStopwatch
///
/// Total: 18 tests. All workspace-based (SetSource / SetTestSolution).
/// </summary>

// ════════════════════════════════════════════════════════════════════════════════
// A. CodeSmellAndStyleEngine
// ════════════════════════════════════════════════════════════════════════════════
[TestFixture]
public class CodeSmellAndStyleEngineTests
{
    private PersistentWorkspaceManager _workspaceManager;
    private CodeSmellAndStyleEngine _engine;

    [SetUp]
    public void Setup()
    {
        _workspaceManager = new PersistentWorkspaceManager(NullLogger<PersistentWorkspaceManager>.Instance);
        _engine = new CodeSmellAndStyleEngine(_workspaceManager);
    }

    [TearDown]
    public void TearDown() => _workspaceManager?.Dispose();

    private void SetSource(string source, string fileName = "Test.cs")
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [(fileName, source)]);
        _workspaceManager.SetTestSolution(solution);
    }

    [Test]
    public async Task ScanForSmells_AsyncMethodWithThreadSleep_ReportsEPC33()
    {
        SetSource(@"
using System.Threading;
public class Service
{
    public async Task ProcessAsync()
    {
        Thread.Sleep(500);
    }
}");
        var smells = await _engine.ScanForSmellsAsync("Test.cs");

        Assert.That(smells, Is.Not.Empty);
        Assert.That(smells.Any(s => s.Id == "EPC33"), Is.True, "Should detect EPC33 for Thread.Sleep in async");
        Assert.That(smells.First(s => s.Id == "EPC33").Severity, Is.EqualTo("Warning"));
    }

    [Test]
    public async Task ScanForSmells_SyncMethodWithThreadSleep_NoEPC33()
    {
        SetSource(@"
using System.Threading;
public class Service
{
    public void Process()
    {
        Thread.Sleep(500); // Sync method — not flagged
    }
}");
        var smells = await _engine.ScanForSmellsAsync("Test.cs");

        Assert.That(smells.Any(s => s.Id == "EPC33"), Is.False, "Thread.Sleep in sync method should not trigger EPC33");
    }

    [Test]
    public async Task ScanForSmells_FileNotInWorkspace_ReturnsEmpty()
    {
        SetSource("public class C { }", "Test.cs");

        // Request a file path that does NOT exist in the workspace
        var smells = await _engine.ScanForSmellsAsync("NonExistent.cs");

        Assert.That(smells, Is.Empty, "Should return empty list for unknown file");
    }

    [Test]
    public async Task UseSwitchExpression_ValidFile_ReturnsSourceContent()
    {
        const string source = @"
public class C
{
    public string GetLabel(int x)
    {
        switch (x)
        {
            case 1: return ""one"";
            default: return ""other"";
        }
    }
}";
        SetSource(source);

        var result = await _engine.UseSwitchExpressionAsync("Test.cs");

        // Stub returns root.ToFullString() — non-empty and contains source identifiers
        Assert.That(result, Is.Not.Empty);
        Assert.That(result, Does.Contain("GetLabel"), "Returned source should contain method name");
    }
}

// ════════════════════════════════════════════════════════════════════════════════
// B. AnalyzerEngineStubs — ExhaustiveAnalyzerEngine + MassiveAnalyzerEngine
// ════════════════════════════════════════════════════════════════════════════════
[TestFixture]
public class AnalyzerEngineStubTests
{
    private PersistentWorkspaceManager _workspaceManager;
    private ExhaustiveAnalyzerEngine _exhaustiveEngine;
    private MassiveAnalyzerEngine _massiveEngine;

    [SetUp]
    public void Setup()
    {
        _workspaceManager = new PersistentWorkspaceManager(NullLogger<PersistentWorkspaceManager>.Instance);
        _exhaustiveEngine = new ExhaustiveAnalyzerEngine(_workspaceManager);
        _massiveEngine = new MassiveAnalyzerEngine(_workspaceManager);
    }

    [TearDown]
    public void TearDown() => _workspaceManager?.Dispose();

    private void SetSource(string source, string fileName = "Test.cs")
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [(fileName, source)]);
        _workspaceManager.SetTestSolution(solution);
    }

    [Test]
    public async Task ExhaustiveAnalyzer_FileNotFound_ReturnsEmpty()
    {
        SetSource("public class C { }", "Test.cs");

        var results = await _exhaustiveEngine.RunDiagnosticRuleAsync("NonExistent.cs", "CA1000");

        Assert.That(results, Is.Empty, "Unknown file should yield empty list");
    }

    [Test]
    public async Task ExhaustiveAnalyzer_FileFound_ReturnsSingleDiagnostic()
    {
        SetSource("public class C { }", "Test.cs");

        var results = await _exhaustiveEngine.RunDiagnosticRuleAsync("Test.cs", "CA2000");

        Assert.That(results.Count, Is.EqualTo(1));
        Assert.That(results[0].RuleId, Is.EqualTo("CA2000"));
        Assert.That(results[0].Message, Does.Contain("CA2000"), "Message should reference the requested rule ID");
    }

    [Test]
    public async Task MassiveAnalyzer_FileNotFound_ReturnsEmpty()
    {
        SetSource("public class C { }", "Test.cs");

        var results = await _massiveEngine.RunSpecificRuleAsync("Missing.cs", "IDE0001");

        Assert.That(results, Is.Empty, "Unknown file should yield empty list");
    }

    [Test]
    public async Task MassiveAnalyzer_FileFound_ReturnsSingleDiagnostic()
    {
        SetSource("public class C { }", "Test.cs");

        var results = await _massiveEngine.RunSpecificRuleAsync("Test.cs", "IDE0017");

        Assert.That(results.Count, Is.EqualTo(1));
        Assert.That(results[0].RuleId, Is.EqualTo("IDE0017"));
        Assert.That(results[0].Message, Does.Contain("IDE0017"), "Message should echo back rule ID");
    }
}

// ════════════════════════════════════════════════════════════════════════════════
// C. SecurityAndSafetyEngine
// ════════════════════════════════════════════════════════════════════════════════
[TestFixture]
public class SecurityAndSafetyEngineTests
{
    private PersistentWorkspaceManager _workspaceManager;
    private SecurityAndSafetyEngine _engine;

    [SetUp]
    public void Setup()
    {
        _workspaceManager = new PersistentWorkspaceManager(NullLogger<PersistentWorkspaceManager>.Instance);
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
    public async Task FindUnsafeTypeCasts_FileNotFound_ThrowsException()
    {
        SetSource("public class C { }", "Test.cs");

        Assert.ThrowsAsync<Exception>(async () =>
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
    private PersistentWorkspaceManager _workspaceManager;
    private InstrumentationEngine _engine;

    [SetUp]
    public void Setup()
    {
        _workspaceManager = new PersistentWorkspaceManager(NullLogger<PersistentWorkspaceManager>.Instance);
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

        Assert.That(result, Does.Contain("try"), "Output should contain try block");
        Assert.That(result, Does.Contain("catch"), "Output should contain catch block");
        Assert.That(result, Does.Contain("Exception"), "Catch should use Exception type by default");
        Assert.That(result, Does.Contain("SubmitOrder"), "Method name should be preserved");
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

        Assert.That(result, Does.Contain("IOException"), "Should use custom exception type");
        Assert.That(result, Does.Contain("finally"), "Should include finally block when requested");
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
        var tryCount = CountOccurrences(result, "try");
        Assert.That(tryCount, Is.EqualTo(2), "Should wrap exactly 2 public methods");
        Assert.That(result, Does.Contain("AuditLog"), "Private method should still appear but without wrapping");
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

        Assert.That(result, Does.Contain("Stopwatch"), "Should inject Stopwatch");
        Assert.That(result, Does.Contain("StartNew"), "Should start a new stopwatch");
        Assert.That(result, Does.Contain("finally"), "Should use finally block for guaranteed logging");
        Assert.That(result, Does.Contain("ElapsedMilliseconds"), "Should log elapsed time");
        Assert.That(result, Does.Contain("System.Diagnostics"), "Should add using directive for Diagnostics");
    }

    [Test]
    public async Task AddTryCatch_MethodNotFound_ThrowsException()
    {
        SetSource(@"public class C { public void Existing() { } }");

        Assert.ThrowsAsync<Exception>(async () =>
            await _engine.AddTryCatchToMethodAsync("Test.cs", "NonExistentMethod"));
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
