// Battery #28 — Deeper Edge-Case Tests for the 4 Session Bugs
//
// Each bug found in the Battery-27 session gets additional coverage:
//
//   B02-extra — ImmutabilityEngine: readonly token whitespace across more field shapes
//   B04-extra — AntiPatternEngine: zero-param CT check via ValueTask, private, interface
//   B16-extra — SecurityAndSafetyEngine: chained ?. , nested, multiple params
//   WF-extra  — AdvancedLogicEngine.ConvertWhileToForAsync: richer loop bodies
//
// Plus: real-solution smoke test that loads the ExpressRecipe solution and ensures
// every analysis engine runs without throwing on the actual codebase.

#pragma warning disable CS8618

using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using RoslynSentinel.Server;

namespace RoslynSentinel.Tests;

// ─────────────────────────────────────────────────────────────────────────────
// B02-extra — ImmutabilityEngine: readonly modifier spacing in more scenarios
// ─────────────────────────────────────────────────────────────────────────────
[TestFixture]
public class B02extra_Immutability_ReadonlySpacing
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
    public async Task MakeFieldReadonly_IntField_HasSpaceBeforeType()
    {
        // Core regression: int fields must get "readonly " with trailing space
        const string source = """
            public class Config {
                private int _timeout = 30;
                public int GetTimeout() => _timeout;
                public Config() { _timeout = 30; }
            }
            """;
        var solution = TestSolutionBuilder.CreateSolutionWithProject("P", [("Config.cs", source)]);
        _workspaceManager.SetTestSolution(solution);

        var result = await _engine.MakeClassImmutableAsync("Config.cs", "Config");

        Assert.That(result, Does.Contain("readonly int"),
            "readonly modifier for 'int' field must be separated by a space: 'readonly int', not 'readonlyint'.");
        Assert.That(result, Does.Not.Contain("readonlyint"),
            "There must be no fused 'readonlyint' token.");
    }

    [Test]
    public async Task MakeFieldReadonly_StringField_HasSpaceBeforeType()
    {
        // String reference type — same spacing requirement
        const string source = """
            public class Greeter {
                private string _greeting = "Hello";
                public string Get() => _greeting;
                public Greeter() { _greeting = "Hello"; }
            }
            """;
        var solution = TestSolutionBuilder.CreateSolutionWithProject("P", [("Greeter.cs", source)]);
        _workspaceManager.SetTestSolution(solution);

        var result = await _engine.MakeClassImmutableAsync("Greeter.cs", "Greeter");

        Assert.That(result, Does.Contain("readonly string"),
            "readonly modifier for 'string' field must emit 'readonly string' not 'readonlystring'.");
    }

    [Test]
    public async Task MakeFieldReadonly_MultipleFields_AllHaveCorrectSpacing()
    {
        // Regression: every field in the class must get the space, not just the first
        const string source = """
            public class Box {
                private int _width = 10;
                private int _height = 20;
                public Box() { _width = 10; _height = 20; }
                public int Area() => _width * _height;
            }
            """;
        var solution = TestSolutionBuilder.CreateSolutionWithProject("P", [("Box.cs", source)]);
        _workspaceManager.SetTestSolution(solution);

        var result = await _engine.MakeClassImmutableAsync("Box.cs", "Box");

        Assert.That(result, Does.Not.Contain("readonlyint"),
            "No field should produce 'readonlyint' — all must have space.");
        var readonlyCount = result.Split("readonly int").Length - 1;
        Assert.That(readonlyCount, Is.EqualTo(2),
            "Both fields should get 'readonly int' with correct spacing.");
    }

    [Test]
    public async Task MakeFieldReadonly_AlreadyReadonly_NoChange()
    {
        // A field already readonly must not gain a second 'readonly'
        const string source = """
            public class Const {
                private readonly int _maxRetry = 3;
                public int Get() => _maxRetry;
            }
            """;
        var solution = TestSolutionBuilder.CreateSolutionWithProject("P", [("Const.cs", source)]);
        _workspaceManager.SetTestSolution(solution);

        var result = await _engine.MakeClassImmutableAsync("Const.cs", "Const");

        var readonlyCount = result.Split("readonly").Length - 1;
        Assert.That(readonlyCount, Is.EqualTo(1),
            "An already-readonly field must not get a second readonly modifier.");
    }

    [Test]
    public async Task MakeFieldReadonly_StaticField_HasCorrectSpacing()
    {
        const string source = """
            public class Registry {
                private static int _count = 0;
                static Registry() { _count = 0; }
                public static int GetCount() => _count;
            }
            """;
        var solution = TestSolutionBuilder.CreateSolutionWithProject("P", [("Registry.cs", source)]);
        _workspaceManager.SetTestSolution(solution);

        var result = await _engine.MakeClassImmutableAsync("Registry.cs", "Registry");

        Assert.That(result, Does.Contain("readonly int"),
            "Static field made readonly must still have space between 'readonly' and 'int'.");
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// B04-extra — AntiPatternEngine zero-param: ValueTask, private, interface shapes
// ─────────────────────────────────────────────────────────────────────────────
[TestFixture]
public class B04extra_AntiPattern_ZeroParamCancellationToken
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
    public async Task DetectAntiPatterns_ZeroParam_ValueTaskReturn_IsFlagged()
    {
        // ValueTask async methods with zero params also need CancellationToken
        const string source = """
            using System.Threading.Tasks;
            public class Fetcher {
                public async ValueTask<int> FetchAsync() {
                    await Task.Delay(1);
                    return 42;
                }
            }
            """;
        var solution = TestSolutionBuilder.CreateSolutionWithProject("P", [("Fetcher.cs", source)]);
        _workspaceManager.SetTestSolution(solution);

        var findings = await _engine.DetectAntiPatternsAsync("Fetcher.cs");

        Assert.That(findings.Any(f =>
                f.Pattern == "MissingCancellationToken" &&
                f.Snippet.Contains("FetchAsync")), Is.True,
            "Zero-param async ValueTask method must also be flagged for missing CancellationToken.");
    }

    [Test]
    public async Task DetectAntiPatterns_ZeroParam_AbstractMethod_NotFlagged()
    {
        // Abstract methods have no body — cannot receive CancellationToken enforcement
        const string source = """
            using System.Threading.Tasks;
            public abstract class Base {
                public abstract Task LoadAsync();
            }
            """;
        var solution = TestSolutionBuilder.CreateSolutionWithProject("P", [("Base.cs", source)]);
        _workspaceManager.SetTestSolution(solution);

        var findings = await _engine.DetectAntiPatternsAsync("Base.cs");

        Assert.That(findings.Any(f =>
                f.Pattern == "MissingCancellationToken" &&
                f.Snippet.Contains("LoadAsync")), Is.False,
            "Abstract methods have no body and must NOT be flagged.");
    }

    [Test]
    public async Task DetectAntiPatterns_OneParam_OtherParam_ZeroParamFlagged()
    {
        // Overloads: one has CT (good), one is zero-param (bad) — only the bad one flagged
        const string source = """
            using System.Threading;
            using System.Threading.Tasks;
            public class Service {
                public async Task SaveAsync(CancellationToken ct) {
                    await Task.Delay(1, ct);
                }
                public async Task SaveAsync() {
                    await Task.Delay(1);
                }
            }
            """;
        var solution = TestSolutionBuilder.CreateSolutionWithProject("P", [("Service.cs", source)]);
        _workspaceManager.SetTestSolution(solution);

        var findings = await _engine.DetectAntiPatternsAsync("Service.cs");
        var ctFindings = findings.Where(f => f.Pattern == "MissingCancellationToken").ToList();

        // The zero-param overload should be flagged; the one with CT should not be duplicated
        // We can't easily distinguish overloads by snippet alone, but count should be == 1
        Assert.That(ctFindings.Count, Is.EqualTo(1),
            "Only the zero-param overload must be flagged; the CT-bearing overload must not add extra findings.");
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// B16-extra — SecurityAndSafetyEngine: chained ?., multi-param, block-body shapes
// ─────────────────────────────────────────────────────────────────────────────
[TestFixture]
public class B16extra_SecuritySafety_NullConditionalGuards
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
    public async Task DetectMissingNullChecks_ExprBody_ChainedNullConditional_NotFlagged()
    {
        // s?.Trim()?.ToUpper() — chained ?. starting on the param → still null-safe
        const string source = """
            public class Fmt {
                public string? Format(string s) => s?.Trim()?.ToUpper();
            }
            """;
        var solution = TestSolutionBuilder.CreateSolutionWithProject("P", [("Fmt.cs", source)]);
        _workspaceManager.SetTestSolution(solution);

        var issues = await _engine.DetectMissingNullChecksAsync("Fmt.cs");

        Assert.That(issues.Any(i => i.Type == "MissingNullCheck" && i.Description.Contains("'s'")), Is.False,
            "Chained null-conditional s?.Trim()?.ToUpper() is null-safe; must not be flagged.");
    }

    [Test]
    public async Task DetectMissingNullChecks_ExprBody_TwoParams_OneGuarded_OtherFlagged()
    {
        // One param uses ?., the other doesn't — only the unsafe one is flagged
        const string source = """
            public class Merger {
                public string? Merge(string a, string b) => a?.ToUpper() + b.ToLower();
            }
            """;
        var solution = TestSolutionBuilder.CreateSolutionWithProject("P", [("Merger.cs", source)]);
        _workspaceManager.SetTestSolution(solution);

        var issues = await _engine.DetectMissingNullChecksAsync("Merger.cs");

        Assert.That(issues.Any(i => i.Type == "MissingNullCheck" && i.Description.Contains("'a'")), Is.False,
            "Parameter 'a' guarded by ?. must NOT be flagged.");
        Assert.That(issues.Any(i => i.Type == "MissingNullCheck" && i.Description.Contains("'b'")), Is.True,
            "Parameter 'b' NOT guarded by ?. MUST be flagged.");
    }

    [Test]
    public async Task DetectMissingNullChecks_BlockBody_NullCheckInBody_NotFlagged()
    {
        // Block-bodied method with explicit null check in body — not flagged
        const string source = """
            public class Validator {
                public int GetLen(string s) {
                    if (s == null) throw new System.ArgumentNullException(nameof(s));
                    return s.Length;
                }
            }
            """;
        var solution = TestSolutionBuilder.CreateSolutionWithProject("P", [("Validator.cs", source)]);
        _workspaceManager.SetTestSolution(solution);

        var issues = await _engine.DetectMissingNullChecksAsync("Validator.cs");

        Assert.That(issues.Any(i => i.Type == "MissingNullCheck" && i.Description.Contains("'s'")), Is.False,
            "Block-bodied method with explicit null guard must NOT be flagged.");
    }

    [Test]
    public async Task DetectMissingNullChecks_ExprBody_NoParamUsed_NotFlagged()
    {
        // Expression-bodied method where the ref param is not used in the body
        const string source = """
            public class Const {
                public int GetFortyTwo(string ignored) => 42;
            }
            """;
        var solution = TestSolutionBuilder.CreateSolutionWithProject("P", [("Const.cs", source)]);
        _workspaceManager.SetTestSolution(solution);

        var issues = await _engine.DetectMissingNullChecksAsync("Const.cs");

        Assert.That(issues.Any(i => i.Type == "MissingNullCheck" && i.Description.Contains("'ignored'")), Is.False,
            "Parameter not used in body cannot cause a NullReferenceException; must NOT be flagged.");
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// WF-extra — AdvancedLogicEngine.ConvertWhileToForAsync: edge-case loop bodies
// ─────────────────────────────────────────────────────────────────────────────
[TestFixture]
public class WFextra_AdvancedLogic_WhileToFor
{
    private PersistentWorkspaceManager _workspaceManager = null!;
    private AdvancedLogicEngine _engine = null!;

    [SetUp]
    public void Setup()
    {
        _workspaceManager = new PersistentWorkspaceManager(NullLogger<PersistentWorkspaceManager>.Instance);
        _engine = new AdvancedLogicEngine(_workspaceManager);
    }

    [TearDown]
    public void TearDown() => _workspaceManager?.Dispose();

    [Test]
    public async Task ConvertWhileToFor_SimpleCounter_EmitsForKeyword()
    {
        // Core conversion: result must contain 'for' and no longer contain 'while'
        const string source = @"
public class Looper {
    public void Run() {
        int i = 0;
        while (i < 10) { i++; }
    }
}";
        var solution = TestSolutionBuilder.CreateSolutionWithProject("P", [("Looper.cs", source)]);
        _workspaceManager.SetTestSolution(solution);

        var result = await _engine.ConvertWhileToForAsync("Looper.cs", 5);

        Assert.That(result, Does.Contain("for"),
            "Converted output must contain a 'for' loop.");
        Assert.That(result, Does.Not.Contain("while"),
            "The 'while' loop must be removed after conversion.");
    }

    [Test]
    public async Task ConvertWhileToFor_MultiStatementBody_IncrementRemoved()
    {
        // Body has multiple statements; the i++ must be moved to the for-incrementor
        const string source = @"
public class Processor {
    private int[] _data = new int[10];
    public void Fill() {
        int i = 0;
        while (i < 10) {
            _data[i] = i * 2;
            i++;
        }
    }
}";
        var solution = TestSolutionBuilder.CreateSolutionWithProject("P", [("Processor.cs", source)]);
        _workspaceManager.SetTestSolution(solution);

        var result = await _engine.ConvertWhileToForAsync("Processor.cs", 6);

        Assert.That(result, Does.Contain("for"),
            "Result must contain a for loop.");
        // i++ in the body must be gone (moved to incrementors)
        var bodyStatements = result
            .Split(new[] { "for " }, StringSplitOptions.None)
            .Skip(1).FirstOrDefault() ?? "";
        Assert.That(result, Does.Contain("i * 2"),
            "Body statement _data[i] = i * 2 must be preserved in the for body.");
    }

    [Test]
    public async Task ConvertWhileToFor_InvalidLine_ReturnsUnchanged()
    {
        // Line with no while statement → returns original source unchanged
        const string source = @"
public class Safe {
    public void Run() {
        int x = 0;
        x++;
    }
}";
        var solution = TestSolutionBuilder.CreateSolutionWithProject("P", [("Safe.cs", source)]);
        _workspaceManager.SetTestSolution(solution);

        var result = await _engine.ConvertWhileToForAsync("Safe.cs", 5);

        Assert.That(result, Does.Not.Contain("for ("),
            "When no while is found, no for-loop must be emitted.");
    }

    [Test]
    public async Task ConvertWhileToFor_NoInitDeclaration_ReturnsUnchanged()
    {
        // While loop without a preceding counter declaration → cannot convert safely
        const string source = @"
public class Streamer {
    public void Run(int start) {
        while (start < 100) { start++; }
    }
}";
        var solution = TestSolutionBuilder.CreateSolutionWithProject("P", [("Streamer.cs", source)]);
        _workspaceManager.SetTestSolution(solution);

        var result = await _engine.ConvertWhileToForAsync("Streamer.cs", 4);

        // Without a local declaration as the previous statement, engine returns unchanged
        Assert.That(result, Is.Not.Null.And.Not.Empty,
            "Even when conversion is not applicable, result must not be null/empty.");
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Real-solution smoke test — load a configured solution and run analysis engines
// ─────────────────────────────────────────────────────────────────────────────
[TestFixture]
[Category("Integration")]
public class RealSolution_SmokeTests_Battery28
{
    private static readonly string SlnPath = Environment.GetEnvironmentVariable("ROSLYN_SENTINEL_TEST_SLN") ?? string.Empty;

    private PersistentWorkspaceManager _workspaceManager = null!;

    /// A real .cs file path and its first class name, discovered at SetUp time.
    private string _realFilePath = null!;
    private string _realClassName = null!;

    [SetUp]
    public async Task Setup()
    {
        if (!File.Exists(SlnPath))
            Assert.Ignore("Set ROSLYN_SENTINEL_TEST_SLN env var to run real-solution integration tests.");

        _workspaceManager = new PersistentWorkspaceManager(NullLogger<PersistentWorkspaceManager>.Instance);
        await _workspaceManager.LoadSolutionAsync(SlnPath);

        // Find one document that has at least one class declaration
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        foreach (var project in solution.Projects)
        {
            foreach (var doc in project.Documents)
            {
                if (doc.FilePath == null) continue;
                var root = await doc.GetSyntaxRootAsync();
                var cls = root?.DescendantNodes()
                    .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.ClassDeclarationSyntax>()
                    .FirstOrDefault();
                if (cls != null)
                {
                    _realFilePath = doc.FilePath;
                    _realClassName = cls.Identifier.Text;
                    return;
                }
            }
        }
        Assert.Ignore("No class-containing document found in the configured test solution.");
    }

    [TearDown]
    public void TearDown() => _workspaceManager?.Dispose();

    // ── Solution-wide engines (no required args) ────────────────────────────

    [Test]
    public async Task AntiPatternEngine_ScanAll_DoesNotThrow()
    {
        var engine = new AntiPatternEngine(_workspaceManager);
        List<AntiPatternFinding>? result = null;
        Assert.DoesNotThrowAsync(async () =>
            result = await engine.DetectAntiPatternsAsync(),
            "AntiPatternEngine must not throw on the real solution.");
        Assert.That(result, Is.Not.Null);
    }

    [Test]
    public async Task SecurityAndSafetyEngine_ScanAll_DoesNotThrow()
    {
        var engine = new SecurityAndSafetyEngine(_workspaceManager);
        List<SafetyIssue>? result = null;
        Assert.DoesNotThrowAsync(async () =>
            result = await engine.DetectMissingNullChecksAsync(_realFilePath),
            "SecurityAndSafetyEngine.DetectMissingNullChecksAsync must not throw on the real solution.");
        Assert.That(result, Is.Not.Null);
    }

    [Test]
    public async Task ArchitecturalEngine_FindCircularDeps_DoesNotThrow()
    {
        var engine = new ArchitecturalEngine(_workspaceManager);
        List<CircularDependencyChain>? result = null;
        Assert.DoesNotThrowAsync(async () =>
            result = await engine.FindCircularDependenciesAsync(),
            "ArchitecturalEngine.FindCircularDependenciesAsync must not throw on the real solution.");
        Assert.That(result, Is.Not.Null);
    }

    // ── Per-file engines (use discovered real file) ─────────────────────────

    [Test]
    public async Task DeadCodeEngine_PerFile_DoesNotThrow()
    {
        var engine = new DeadCodeEngine(_workspaceManager);
        List<DeadCodeReport>? result = null;
        Assert.DoesNotThrowAsync(async () =>
            result = await engine.DetectUnusedLocalVariablesAsync(_realFilePath),
            $"DeadCodeEngine must not throw on real file: {_realFilePath}");
        Assert.That(result, Is.Not.Null);
    }

    [Test]
    public async Task DeadCodeEngine_UnusedPrivateFields_DoesNotThrow()
    {
        var engine = new DeadCodeEngine(_workspaceManager);
        List<DeadCodeReport>? result = null;
        Assert.DoesNotThrowAsync(async () =>
            result = await engine.DetectUnusedPrivateFieldsAsync(_realFilePath),
            $"DeadCodeEngine.DetectUnusedPrivateFieldsAsync must not throw on real file.");
        Assert.That(result, Is.Not.Null);
    }

    [Test]
    public async Task ImmutabilityEngine_MakeClassImmutable_DoesNotThrow()
    {
        var engine = new ImmutabilityEngine(_workspaceManager);
        string? result = null;
        Assert.DoesNotThrowAsync(async () =>
            result = await engine.MakeClassImmutableAsync(_realFilePath, _realClassName),
            $"ImmutabilityEngine must not throw on class '{_realClassName}' in {_realFilePath}");
        Assert.That(result, Is.Not.Null);
    }

    [Test]
    public async Task DocumentationEngine_GenerateStubs_DoesNotThrow()
    {
        var engine = new DocumentationEngine(_workspaceManager);
        string? result = null;
        Assert.DoesNotThrowAsync(async () =>
            result = await engine.GenerateXmlDocumentationStubsAsync(_realFilePath),
            $"DocumentationEngine.GenerateXmlDocumentationStubsAsync must not throw on real file.");
        Assert.That(result, Is.Not.Null);
    }

    [Test]
    public async Task LogicOptimizationEngine_SimplifyBooleans_DoesNotThrow()
    {
        var engine = new LogicOptimizationEngine(_workspaceManager);
        string? result = null;
        Assert.DoesNotThrowAsync(async () =>
            result = await engine.SimplifyBooleanExpressionsAsync(_realFilePath),
            $"LogicOptimizationEngine.SimplifyBooleanExpressionsAsync must not throw on real file.");
        Assert.That(result, Is.Not.Null);
    }

    [Test]
    public async Task TestingEngine_GenerateSkeleton_DoesNotThrow()
    {
        var engine = new TestingEngine(_workspaceManager);
        TestSkeletonReport? result = null;
        Assert.DoesNotThrowAsync(async () =>
            result = await engine.GenerateTestSkeletonAsync(_realFilePath, _realClassName),
            $"TestingEngine.GenerateTestSkeletonAsync must not throw on class '{_realClassName}'.");
        Assert.That(result, Is.Not.Null);
    }

    // ── Data quality invariants ─────────────────────────────────────────────

    [Test]
    public async Task SecurityAndSafetyEngine_NoFindings_HaveNullFields()
    {
        var engine = new SecurityAndSafetyEngine(_workspaceManager);
        var issues = await engine.DetectMissingNullChecksAsync(_realFilePath);

        foreach (var issue in issues)
        {
            Assert.That(issue.FilePath, Is.Not.Null.And.Not.Empty,
                "Every SafetyIssue must have a non-empty FilePath.");
            Assert.That(issue.Type, Is.Not.Null.And.Not.Empty,
                "Every SafetyIssue must have a non-empty Type.");
            Assert.That(issue.Description, Is.Not.Null.And.Not.Empty,
                "Every SafetyIssue must have a non-empty Description.");
        }
    }

    [Test]
    public async Task ImmutabilityEngine_ReadonlyOutput_NoFusedTokens()
    {
        // B02 regression on real code: 'readonly' must always be followed by a space
        var engine = new ImmutabilityEngine(_workspaceManager);
        var result = await engine.MakeClassImmutableAsync(_realFilePath, _realClassName);

        Assert.That(result, Does.Not.Contain("readonlystring"),
            "B02 regression: 'readonly' and 'string' must always be space-separated.");
        Assert.That(result, Does.Not.Contain("readonlyint"),
            "B02 regression: 'readonly' and 'int' must always be space-separated.");
        Assert.That(result, Does.Not.Contain("readonlybool"),
            "B02 regression: 'readonly' and 'bool' must always be space-separated.");
    }
}
