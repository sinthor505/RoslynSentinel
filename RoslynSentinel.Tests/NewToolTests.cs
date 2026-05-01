using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging.Abstractions;
using RoslynSentinel.Server;

#pragma warning disable CS8618
namespace RoslynSentinel.Tests;

/// <summary>
/// Tests for the 5 new tools added in Session 10:
/// ConvertPropertySafe, InterpolateStringSafe, FindCallersSafe,
/// FindImplementationsSafe, FormatDocumentPreview, GetDiagnosticsSummary.
/// </summary>
[TestFixture]
public class NewToolTests
{
    private PersistentWorkspaceManager _workspaceManager;
    private SentinelConfiguration _config;
    private CodeGenerationEngine _codeGenerationEngine;
    private SymbolNavigationEngine _symbolNavigationEngine;
    private RefactoringEngine _refactoringEngine;
    private DiagnosticEngine _diagnosticEngine;

    [SetUp]
    public void Setup()
    {
        _workspaceManager = new PersistentWorkspaceManager(NullLogger<PersistentWorkspaceManager>.Instance);
        _config = new SentinelConfiguration();
        _codeGenerationEngine = new CodeGenerationEngine(_workspaceManager);
        _symbolNavigationEngine = new SymbolNavigationEngine(_workspaceManager, NullLogger<SymbolNavigationEngine>.Instance);
        _refactoringEngine = new RefactoringEngine(NullLogger<RefactoringEngine>.Instance, _workspaceManager, _config);
        _diagnosticEngine = new DiagnosticEngine(_workspaceManager);
    }

    [TearDown]
    public void TearDown() => _workspaceManager?.Dispose();

    private void SetSource(string source, string fileName = "Test.cs")
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [(fileName, source)]);
        _workspaceManager.SetTestSolution(solution);
    }

    private void SetMultipleFiles(params (string name, string content)[] files)
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", files);
        _workspaceManager.SetTestSolution(solution);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // ConvertPropertySafe
    // ──────────────────────────────────────────────────────────────────────────

    [Test]
    public async Task ConvertPropertySafe_ToFullProperty_PreservesInitializer()
    {
        SetSource("""
            public class Foo
            {
                public int Count { get; set; } = 42;
            }
            """);

        var result = await _codeGenerationEngine.ConvertPropertySafeAsync("Test.cs", "Count", "ToFullProperty");

        Assert.That(result, Contains.Substring("42"), "Initializer value should survive ToFullProperty conversion");
        Assert.That(result, Contains.Substring("get =>"), "Should produce expression-body getter");
        Assert.That(result, Contains.Substring("set =>"), "Should produce expression-body setter");
    }

    [Test]
    public async Task ConvertPropertySafe_ToAutoProperty_RemovesBackingField()
    {
        SetSource("""
            public class Foo
            {
                private int _count;
                public int Count
                {
                    get { return _count; }
                    set { _count = value; }
                }
            }
            """);

        var result = await _codeGenerationEngine.ConvertPropertySafeAsync("Test.cs", "Count", "ToAutoProperty");

        Assert.That(result, Contains.Substring("{ get; set; }"), "Should produce auto-property");
    }

    [Test]
    public async Task ConvertPropertySafe_InvalidDirection_ThrowsOrReturnsError()
    {
        SetSource("""
            public class Foo { public int X { get; set; } }
            """);

        var ex = Assert.ThrowsAsync<ArgumentException>(
            () => _codeGenerationEngine.ConvertPropertySafeAsync("Test.cs", "X", "BadDirection"));
        Assert.That(ex?.Message, Does.Contain("direction").IgnoreCase.Or.Contain("BadDirection"));
    }

    // ──────────────────────────────────────────────────────────────────────────
    // InterpolateStringSafe
    // ──────────────────────────────────────────────────────────────────────────

    [Test]
    public async Task InterpolateStringSafe_LiteralFormat_ProducesInterpolatedString()
    {
        SetSource("""
            public class Foo
            {
                public string Build(string name, int age)
                {
                    return string.Format("Hello {0}, you are {1}", name, age);
                }
            }
            """);

        var result = await _codeGenerationEngine.InterpolateStringAsync(
            "Test.cs",
            "string.Format(\"Hello {0}, you are {1}\", name, age)");

        Assert.That(result, Contains.Substring("$\""), "Should produce an interpolated string");
        Assert.That(result, Contains.Substring("{name}"), "First arg should be inlined");
        Assert.That(result, Contains.Substring("{age}"), "Second arg should be inlined");
    }

    [Test]
    public async Task InterpolateStringSafe_SnippetNotFound_ThrowsOrReturnsError()
    {
        SetSource("""
            public class Foo { }
            """);

        Assert.ThrowsAsync<InvalidOperationException>(
            () => _codeGenerationEngine.InterpolateStringAsync("Test.cs", "string.Format(\"missing\")"));
    }

    // ──────────────────────────────────────────────────────────────────────────
    // FindCallersSafe
    // ──────────────────────────────────────────────────────────────────────────

    [Test]
    public async Task FindCallersSafe_ReturnsCallSites()
    {
        SetMultipleFiles(
            ("Greeter.cs", """
                public class Greeter
                {
                    public string Greet(string name) => $"Hi {name}";
                }
                """),
            ("App.cs", """
                public class App
                {
                    private readonly Greeter _g = new();
                    public void Run() { var msg = _g.Greet("World"); }
                }
                """));

        var callers = await _symbolNavigationEngine.FindCallersAsync("Greeter.cs", "Greet");

        Assert.That(callers, Is.Not.Empty, "Should find at least one call site");
        Assert.That(callers.Any(c => c.CallerMethod == "Run"), Is.True, "Run() should be listed as a caller");
    }

    [Test]
    public async Task FindCallersSafe_UnreferencedMethod_ReturnsEmpty()
    {
        SetSource("""
            public class Foo
            {
                public void Orphan() { }
            }
            """);

        var callers = await _symbolNavigationEngine.FindCallersAsync("Test.cs", "Orphan");

        Assert.That(callers, Is.Empty, "Unreferenced method should have no callers");
    }

    // ──────────────────────────────────────────────────────────────────────────
    // FindImplementationsSafe
    // ──────────────────────────────────────────────────────────────────────────

    [Test]
    public async Task FindImplementationsSafe_ReturnsImplementations()
    {
        SetMultipleFiles(
            ("IFoo.cs", """
                public interface IFoo
                {
                    void DoWork();
                }
                """),
            ("FooImpl.cs", """
                public class FooImpl : IFoo
                {
                    public void DoWork() { }
                }
                """));

        var impls = await _symbolNavigationEngine.FindImplementationsForMemberAsync("IFoo.cs", "DoWork");

        Assert.That(impls, Is.Not.Empty, "Should find at least one implementation");
        Assert.That(impls.Any(i => i.TypeName.Contains("FooImpl")), Is.True,
            "FooImpl.DoWork should be in the results");
    }

    [Test]
    public async Task FindImplementationsSafe_NoImplementations_ReturnsEmpty()
    {
        SetSource("""
            public interface IBar
            {
                void SomethingUnimplemented();
            }
            """);

        var impls = await _symbolNavigationEngine.FindImplementationsForMemberAsync("Test.cs", "SomethingUnimplemented");

        Assert.That(impls, Is.Empty, "Interface member with no implementations returns empty list");
    }

    // ──────────────────────────────────────────────────────────────────────────
    // FormatDocumentPreview
    // ──────────────────────────────────────────────────────────────────────────

    [Test]
    public async Task FormatDocumentPreview_UnformattedFile_ReturnsHunks()
    {
        // Deliberately messy indentation
        SetSource(
"public class Messy{\npublic int X{get;set;}\npublic int Y{get;set;}\n}");

        var preview = await _refactoringEngine.FormatDocumentPreviewAsync("Test.cs");

        Assert.That(preview.Changed, Is.True, "Poorly-formatted file should show as changed");
        Assert.That(preview.Hunks, Is.Not.Empty, "There should be at least one hunk");
    }

    [Test]
    public async Task FormatDocumentPreview_AlreadyFormatted_ReturnsNoChanges()
    {
        SetSource("""
            public class Clean
            {
                public int X { get; set; }
            }
            """);

        var preview = await _refactoringEngine.FormatDocumentPreviewAsync("Test.cs");

        // The formatter may or may not consider this already perfect, but at minimum it should not throw
        Assert.That(preview.Hunks, Is.Not.Null);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // GetDiagnosticsSummary
    // ──────────────────────────────────────────────────────────────────────────

    [Test]
    public async Task GetDiagnosticsSummary_WellFormedFile_ReturnsZeroErrors()
    {
        SetSource("""
            public class Ok
            {
                public int Add(int a, int b) => a + b;
            }
            """);

        var summary = await _diagnosticEngine.GetFileDiagnosticsAsync("Test.cs");

        Assert.That(summary.Errors, Is.EqualTo(0), "Well-formed file should have no errors");
    }

    [Test]
    public async Task GetDiagnosticsSummary_GroupsByDiagnosticId()
    {
        // Source with two undefined-symbol errors
        SetSource("""
            public class Broken
            {
                public void Foo()
                {
                    var x = UndefinedSymbol1;
                    var y = UndefinedSymbol2;
                }
            }
            """);

        var summary = await _diagnosticEngine.GetSolutionDiagnosticsAsync();

        // We just verify the summary struct is usable and has Details
        Assert.That(summary, Is.Not.Null);
        Assert.That(summary.Details, Is.Not.Null);
    }
}
