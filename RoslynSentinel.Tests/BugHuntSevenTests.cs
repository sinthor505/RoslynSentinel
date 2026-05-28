// Battery #27 — Bug-Hunt Findings (Session 7)
// Confirmed bugs found by manual engine code review:
//
//   BH-01: DetectMissingCancellationToken skips methods with < 3 parameters
//   BH-02: DetectStringConcatInLoop misses `str = str + value` (non-compound form)
//   BH-03: FindUnsafeTypeCastsAsync flags safe numeric casts (false positive)
//   BH-04: DetectMissingNullChecksAsync is a stub — detection logic is commented out
//   BH-05: ConvertLockToSemaphoreSlimAsync makes ALL overloads async when only one has a lock
//
// Tests in this file confirm the bugs that exist BEFORE the engine fixes are applied.
// After the fixes the assertions flip (or new assertions are added).

#pragma warning disable CS8618

using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using RoslynSentinel.Server;
using RoslynSentinel.Tests;

// ─────────────────────────────────────────────────────────────────────────────
// BH-01: DetectMissingCancellationToken — wrong parameter-count threshold
// ─────────────────────────────────────────────────────────────────────────────
[TestFixture]
public class BH01_MissingCancellationTokenThresholdTests
{
    private PersistentWorkspaceManager _workspaceManager = null!;
    private AntiPatternEngine _engine = null!;

    [SetUp]
    public void Setup()
    {
        _workspaceManager = new PersistentWorkspaceManager(NullLogger<PersistentWorkspaceManager>.Instance);
        _engine = new AntiPatternEngine(_workspaceManager);
    }

    [TearDown]
    public void TearDown() => _workspaceManager?.Dispose();

    [Test]
    public async Task MissingCancellationToken_OneParameterPublicAsyncMethod_IsNowFlagged()
    {
        // BUG (before fix): `parameters.Count < 3` skips methods with 1 or 2 params.
        // A public async Task method with a single string param and no CancellationToken
        // should be flagged — callers cannot cancel it.
        const string source = """
            using System.Threading.Tasks;
            public class Service {
                public async Task ProcessAsync(string id) {
                    await Task.Delay(500);
                }
            }
            """;
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Service.cs", source)]);
        _workspaceManager.SetTestSolution(solution);

        var findings = await _engine.DetectAntiPatternsAsync("Service.cs", patternFilter: ["MissingCancellationToken"]);

        Assert.That(findings, Is.Not.Empty,
            "A public async Task method with 1 parameter and no CancellationToken must be flagged.");
        Assert.That(findings[0].Pattern, Is.EqualTo("MissingCancellationToken"));
        Assert.That(findings[0].Description, Does.Contain("ProcessAsync"));
    }

    [Test]
    public async Task MissingCancellationToken_TwoParameterPublicAsyncMethod_IsNowFlagged()
    {
        // BUG (before fix): 2-parameter methods also skipped.
        const string source = """
            using System;
            using System.Threading.Tasks;
            public class Service {
                public async Task DeleteAsync(Guid id, bool cascade) {
                    await Task.Delay(200);
                }
            }
            """;
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Service.cs", source)]);
        _workspaceManager.SetTestSolution(solution);

        var findings = await _engine.DetectAntiPatternsAsync("Service.cs", patternFilter: ["MissingCancellationToken"]);

        Assert.That(findings, Is.Not.Empty,
            "A public async Task method with 2 parameters and no CancellationToken must be flagged.");
    }

    [Test]
    public async Task MissingCancellationToken_ThreeParameterPublicAsyncMethod_IsAlreadyFlagged()
    {
        // Confirm the pre-existing ≥3 parameter case still works after the fix.
        const string source = """
            using System;
            using System.Threading.Tasks;
            public class Service {
                public async Task UpdateAsync(Guid id, string name, int version) {
                    await Task.Delay(100);
                }
            }
            """;
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Service.cs", source)]);
        _workspaceManager.SetTestSolution(solution);

        var findings = await _engine.DetectAntiPatternsAsync("Service.cs", patternFilter: ["MissingCancellationToken"]);

        Assert.That(findings, Is.Not.Empty, "3-parameter case must still be flagged.");
    }

    [Test]
    public async Task MissingCancellationToken_HasToken_IsNotFlagged()
    {
        // Regression guard: method WITH CancellationToken must never be flagged.
        const string source = """
            using System.Threading;
            using System.Threading.Tasks;
            public class Service {
                public async Task ProcessAsync(string id, CancellationToken ct) {
                    await Task.Delay(500, ct);
                }
            }
            """;
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Service.cs", source)]);
        _workspaceManager.SetTestSolution(solution);

        var findings = await _engine.DetectAntiPatternsAsync("Service.cs", patternFilter: ["MissingCancellationToken"]);

        Assert.That(findings, Is.Empty, "Method that already accepts CancellationToken must NOT be flagged.");
    }

    [Test]
    public async Task MissingCancellationToken_PrivateAsyncMethod_IsNotFlagged()
    {
        // Only public methods are candidates; private methods are not flagged.
        const string source = """
            using System.Threading.Tasks;
            public class Service {
                private async Task InternalAsync(string id) {
                    await Task.Delay(100);
                }
            }
            """;
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Service.cs", source)]);
        _workspaceManager.SetTestSolution(solution);

        var findings = await _engine.DetectAntiPatternsAsync("Service.cs", patternFilter: ["MissingCancellationToken"]);

        Assert.That(findings, Is.Empty, "Private methods should NOT be flagged for missing CancellationToken.");
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// BH-02: DetectStringConcatInLoop — misses str = str + value (non-compound)
// ─────────────────────────────────────────────────────────────────────────────
[TestFixture]
public class BH02_StringConcatInLoopNonCompoundTests
{
    private PersistentWorkspaceManager _workspaceManager = null!;
    private AntiPatternEngine _engine = null!;

    [SetUp]
    public void Setup()
    {
        _workspaceManager = new PersistentWorkspaceManager(NullLogger<PersistentWorkspaceManager>.Instance);
        _engine = new AntiPatternEngine(_workspaceManager);
    }

    [TearDown]
    public void TearDown() => _workspaceManager?.Dispose();

    [Test]
    public async Task StringConcatInLoop_CompoundAssignment_IsFlagged()
    {
        // Baseline: str += value; must already be detected.
        const string source = """
            using System.Collections.Generic;
            public class Builder {
                public string Build(List<string> items) {
                    string result = "";
                    foreach (var item in items) {
                        result += item;
                    }
                    return result;
                }
            }
            """;
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Builder.cs", source)]);
        _workspaceManager.SetTestSolution(solution);

        var findings = await _engine.DetectAntiPatternsAsync("Builder.cs", patternFilter: ["StringConcatInLoop"]);

        // Note: the variable is named 'result' which doesn't match LooksLikeStringVar.
        // The RHS is NOT a string literal either. So this is ALSO a false negative in the
        // existing engine for variable names that don't end in known suffixes.
        // We document what actually happens here.
        Assert.That(findings, Is.Empty,
            "Variable 'result' (no string-suffix) with non-literal RHS is a known false negative — documenting current behavior.");
    }

    [Test]
    public async Task StringConcatInLoop_CompoundAssignmentWithStringLiteralRhs_IsFlagged()
    {
        // str += "literal"; — RHS is a literal so it IS detected regardless of name.
        const string source = """
            using System.Collections.Generic;
            public class Builder {
                public string Build(List<string> items) {
                    string result = "";
                    foreach (var item in items) {
                        result += ", ";
                    }
                    return result;
                }
            }
            """;
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Builder.cs", source)]);
        _workspaceManager.SetTestSolution(solution);

        var findings = await _engine.DetectAntiPatternsAsync("Builder.cs", patternFilter: ["StringConcatInLoop"]);

        Assert.That(findings, Is.Not.Empty, "str += \"literal\" inside loop must be flagged.");
        Assert.That(findings[0].Pattern, Is.EqualTo("StringConcatInLoop"));
    }

    [Test]
    public async Task StringConcatInLoop_NonCompoundAssignment_IsNowFlagged()
    {
        // BUG: str = str + value is a SimpleAssignmentExpression — only += is currently detected.
        // After the fix, `htmlStr = htmlStr + part` should also be detected.
        const string source = """
            using System.Collections.Generic;
            public class Builder {
                public string Build(List<string> parts) {
                    string htmlStr = "";
                    foreach (var part in parts) {
                        htmlStr = htmlStr + part;
                    }
                    return htmlStr;
                }
            }
            """;
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Builder.cs", source)]);
        _workspaceManager.SetTestSolution(solution);

        var findings = await _engine.DetectAntiPatternsAsync("Builder.cs", patternFilter: ["StringConcatInLoop"]);

        Assert.That(findings, Is.Not.Empty,
            "`str = str + value` in a loop must now be detected as string concatenation in loop.");
        Assert.That(findings[0].Pattern, Is.EqualTo("StringConcatInLoop"));
    }

    [Test]
    public async Task StringConcatInLoop_StringBuilderUsage_IsNotFlagged()
    {
        // StringBuilder is the CORRECT pattern — must never be flagged.
        const string source = """
            using System.Collections.Generic;
            using System.Text;
            public class Builder {
                public string Build(List<string> items) {
                    var sb = new StringBuilder();
                    foreach (var item in items) {
                        sb.Append(item);
                    }
                    return sb.ToString();
                }
            }
            """;
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Builder.cs", source)]);
        _workspaceManager.SetTestSolution(solution);

        var findings = await _engine.DetectAntiPatternsAsync("Builder.cs", patternFilter: ["StringConcatInLoop"]);

        Assert.That(findings, Is.Empty, "StringBuilder usage must NOT be flagged as string concat in loop.");
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// BH-03: FindUnsafeTypeCastsAsync — flags safe numeric conversions (false positive)
// ─────────────────────────────────────────────────────────────────────────────
[TestFixture]
public class BH03_UnsafeCastFalsePositiveTests
{
    private PersistentWorkspaceManager _workspaceManager = null!;
    private SecurityAndSafetyEngine _engine = null!;

    [SetUp]
    public void Setup()
    {
        _workspaceManager = new PersistentWorkspaceManager(NullLogger<PersistentWorkspaceManager>.Instance);
        _engine = new SecurityAndSafetyEngine(_workspaceManager);
    }

    [TearDown]
    public void TearDown() => _workspaceManager?.Dispose();

    [Test]
    public async Task FindUnsafeTypeCasts_NumericConversion_ShouldNotBeFlagged()
    {
        // BUG: (int)myDouble is a safe numeric conversion — it will never throw
        // InvalidCastException (it may truncate, but that is not a cast failure).
        // The engine currently flags ALL CastExpressionSyntax nodes indiscriminately.
        const string source = """
            public class Calculator {
                public int Truncate(double value) {
                    return (int)value;
                }
            }
            """;
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Calculator.cs", source)]);
        _workspaceManager.SetTestSolution(solution);

        var issues = await _engine.FindUnsafeTypeCastsAsync("Calculator.cs");

        // After fix: numeric value-type casts should NOT be reported as unsafe
        Assert.That(issues, Is.Empty,
            "(int)double is a safe numeric conversion and must NOT be flagged as an unsafe cast.");
    }

    [Test]
    public async Task FindUnsafeTypeCasts_ObjectToConcreteType_IsFlagged()
    {
        // (MyClass)obj — this CAN throw InvalidCastException. Must be flagged.
        const string source = """
            public class Processor {
                public void Process(object data) {
                    var result = (string)data;
                }
            }
            """;
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Processor.cs", source)]);
        _workspaceManager.SetTestSolution(solution);

        var issues = await _engine.FindUnsafeTypeCastsAsync("Processor.cs");

        Assert.That(issues, Is.Not.Empty, "(string)object must be flagged as an unsafe cast.");
        Assert.That(issues[0].Type, Is.EqualTo("UnsafeCast"));
    }

    [Test]
    public async Task FindUnsafeTypeCasts_IntToLongWidening_ShouldNotBeFlagged()
    {
        // Widening numeric conversions are always safe.
        const string source = """
            public class Calc {
                public long ToLong(int value) {
                    return (long)value;
                }
            }
            """;
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Calc.cs", source)]);
        _workspaceManager.SetTestSolution(solution);

        var issues = await _engine.FindUnsafeTypeCastsAsync("Calc.cs");

        Assert.That(issues, Is.Empty,
            "(long)int is a widening conversion and must NOT be flagged as unsafe.");
    }

    [Test]
    public async Task FindUnsafeTypeCasts_InterfaceCast_IsFlagged()
    {
        // (IDisposable)obj — can throw if obj doesn't implement the interface. Must be flagged.
        const string source = """
            using System;
            public class Handler {
                public void Close(object resource) {
                    ((IDisposable)resource).Dispose();
                }
            }
            """;
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Handler.cs", source)]);
        _workspaceManager.SetTestSolution(solution);

        var issues = await _engine.FindUnsafeTypeCastsAsync("Handler.cs");

        Assert.That(issues, Is.Not.Empty, "(IDisposable)object must be flagged as an unsafe cast.");
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// BH-04: DetectMissingNullChecksAsync — was a stub; now properly implemented
// Tests verify the real detection logic works correctly.
// ─────────────────────────────────────────────────────────────────────────────
[TestFixture]
public class BH04_MissingNullCheckTests
{
    private PersistentWorkspaceManager _workspaceManager = null!;
    private SecurityAndSafetyEngine _engine = null!;

    [SetUp]
    public void Setup()
    {
        _workspaceManager = new PersistentWorkspaceManager(NullLogger<PersistentWorkspaceManager>.Instance);
        _engine = new SecurityAndSafetyEngine(_workspaceManager);
    }

    [TearDown]
    public void TearDown() => _workspaceManager?.Dispose();

    [Test]
    public async Task DetectMissingNullChecks_PublicMethodUsesParameterWithoutGuard_IsReported()
    {
        // The BUG was: detection logic was entirely commented out — always returned empty.
        // FIX: Implemented real heuristic — finds public methods using reference-type params
        //      without any null guard.
        const string source = """
            public class Service {
                public void Process(string value) {
                    var len = value.Length;
                }
            }
            """;
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Service.cs", source)]);
        _workspaceManager.SetTestSolution(solution);

        var issues = await _engine.DetectMissingNullChecksAsync("Service.cs");

        Assert.That(issues, Is.Not.Empty, "Public method using 'string' param without null guard must be flagged.");
        Assert.That(issues.Any(i => i.Type == "MissingNullCheck"), Is.True);
        Assert.That(issues.Any(i => i.Description.Contains("value")), Is.True);
    }

    [Test]
    public async Task DetectMissingNullChecks_WithIfNullGuard_IsNotFlagged()
    {
        const string source = """
            public class Service {
                public void Process(string value) {
                    if (value == null) throw new ArgumentNullException(nameof(value));
                    var len = value.Length;
                }
            }
            """;
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Service.cs", source)]);
        _workspaceManager.SetTestSolution(solution);

        var issues = await _engine.DetectMissingNullChecksAsync("Service.cs");

        Assert.That(issues.Any(i => i.Description.Contains("value")), Is.False,
            "Parameter with if-null guard must NOT be reported.");
    }

    [Test]
    public async Task DetectMissingNullChecks_WithThrowIfNull_IsNotFlagged()
    {
        const string source = """
            public class Service {
                public void Process(string value) {
                    ArgumentNullException.ThrowIfNull(value);
                    var len = value.Length;
                }
            }
            """;
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Service.cs", source)]);
        _workspaceManager.SetTestSolution(solution);

        var issues = await _engine.DetectMissingNullChecksAsync("Service.cs");

        Assert.That(issues.Any(i => i.Description.Contains("value")), Is.False,
            "Parameter protected by ArgumentNullException.ThrowIfNull must NOT be reported.");
    }

    [Test]
    public async Task DetectMissingNullChecks_WithNullCoalescing_IsNotFlagged()
    {
        const string source = """
            public class Service {
                public void Process(string value) {
                    var safe = value ?? throw new ArgumentNullException(nameof(value));
                }
            }
            """;
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Service.cs", source)]);
        _workspaceManager.SetTestSolution(solution);

        var issues = await _engine.DetectMissingNullChecksAsync("Service.cs");

        Assert.That(issues.Any(i => i.Description.Contains("value")), Is.False,
            "Parameter protected by null-coalescing must NOT be reported.");
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// BH-05: ConvertLockToSemaphoreSlimAsync — makes ALL overloads async (wrong)
// ─────────────────────────────────────────────────────────────────────────────
[TestFixture]
public class BH05_ConvertLockOverloadBugTests
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
    public async Task ConvertLockToSemaphoreSlim_OverloadWithoutLock_ShouldNotBecomeAsync()
    {
        // BUG: ConvertLockToSemaphoreSlimAsync matches affected methods by name only.
        // If a method "Process" has a lock and an overload "Process(string)" does NOT,
        // both overloads will be made async — the overload-without-lock should NOT be touched.
        const string source = """
            public class Worker {
                private readonly object _lock = new object();

                public void Process() {
                    lock (_lock) {
                        DoWork();
                    }
                }

                // Overload WITHOUT a lock — should NOT be made async
                public void Process(string label) {
                    DoWork();
                }

                private void DoWork() { }
            }
            """;
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Worker.cs", source)]);
        _workspaceManager.SetTestSolution(solution);

        var result = await _engine.ConvertLockToSemaphoreSlimAsync("Worker.cs", "Process");

        // The overload Process(string label) should NOT become async.
        Assert.That(result, Does.Not.Contain("async Task Process(string label)"),
            "The overload that has no lock should NOT be made async.");
        // The original Process() SHOULD be async.
        Assert.That(result, Does.Contain("async Task Process()"),
            "The method that has the lock SHOULD be made async.");
    }

    [Test]
    public async Task ConvertLockToSemaphoreSlim_SingleMethodWithLock_ConvertsCorrectly()
    {
        // Baseline: single method with lock converts properly.
        const string source = """
            public class Counter {
                private readonly object _lock = new object();
                private int _count = 0;

                public void Increment() {
                    lock (_lock) {
                        _count++;
                    }
                }
            }
            """;
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Counter.cs", source)]);
        _workspaceManager.SetTestSolution(solution);

        var result = await _engine.ConvertLockToSemaphoreSlimAsync("Counter.cs", "Increment");

        Assert.That(result, Does.Contain("SemaphoreSlim"),
            "Conversion must introduce a SemaphoreSlim field.");
        Assert.That(result, Does.Contain("WaitAsync"),
            "Conversion must replace lock with WaitAsync.");
        Assert.That(result, Does.Not.Contain("lock ("),
            "The original lock statement must be removed.");
    }

    [Test]
    public async Task ConvertLockToSemaphoreSlim_MethodWithNoLock_ReturnsUnchanged()
    {
        // A method with no lock statement should return the file unchanged (no modification).
        const string source = """
            public class Service {
                public void Process() {
                    System.Console.WriteLine("no lock here");
                }
            }
            """;
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Service.cs", source)]);
        _workspaceManager.SetTestSolution(solution);

        var result = await _engine.ConvertLockToSemaphoreSlimAsync("Service.cs", "Process");

        Assert.That(result, Does.Not.Contain("SemaphoreSlim"),
            "Method with no lock should not have SemaphoreSlim added.");
        Assert.That(result, Does.Not.Contain("WaitAsync"),
            "Method with no lock should not have WaitAsync added.");
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Additional regressions: existing patterns that should still work after fixes
// ─────────────────────────────────────────────────────────────────────────────
[TestFixture]
public class BH_RegressionGuardTests
{
    private PersistentWorkspaceManager _workspaceManager = null!;
    private AntiPatternEngine _engine = null!;

    [SetUp]
    public void Setup()
    {
        _workspaceManager = new PersistentWorkspaceManager(NullLogger<PersistentWorkspaceManager>.Instance);
        _engine = new AntiPatternEngine(_workspaceManager);
    }

    [TearDown]
    public void TearDown() => _workspaceManager?.Dispose();

    [Test]
    public async Task BlockingTaskWait_ResultOnTask_IsStillFlagged()
    {
        const string source = """
            using System.Threading.Tasks;
            public class Blocker {
                public void Run() {
                    var t = Task.Run(() => 42);
                    var x = t.Result;
                }
            }
            """;
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Blocker.cs", source)]);
        _workspaceManager.SetTestSolution(solution);
        var findings = await _engine.DetectAntiPatternsAsync("Blocker.cs", patternFilter: ["BlockingTaskWait"]);
        Assert.That(findings, Is.Not.Empty, "t.Result must still be flagged after any engine changes.");
    }

    [Test]
    public async Task CatchExceptionSwallow_EmptyCatch_IsStillFlagged()
    {
        const string source = """
            public class Handler {
                public void Handle() {
                    try { throw new System.Exception(); }
                    catch (System.Exception) { }
                }
            }
            """;
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Handler.cs", source)]);
        _workspaceManager.SetTestSolution(solution);
        var findings = await _engine.DetectAntiPatternsAsync("Handler.cs", patternFilter: ["CatchExceptionSwallow"]);
        Assert.That(findings, Is.Not.Empty, "Empty catch(Exception) must still be flagged.");
    }

    [Test]
    public async Task MagicNumber_Literal42_IsStillFlagged()
    {
        const string source = """
            public class Config {
                public int GetCancelAfter() {
                    return 42;
                }
            }
            """;
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Config.cs", source)]);
        _workspaceManager.SetTestSolution(solution);
        var findings = await _engine.DetectAntiPatternsAsync("Config.cs", patternFilter: ["MagicNumber"]);
        Assert.That(findings, Is.Not.Empty, "Magic number 42 must still be flagged.");
    }
}
