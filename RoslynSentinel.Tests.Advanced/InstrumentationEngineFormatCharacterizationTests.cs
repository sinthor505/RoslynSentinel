using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Engines.Advanced;

#pragma warning disable CS8618
namespace RoslynSentinel.Tests.Advanced;

/// <summary>
/// C5b-1 characterization tests (docs/current/proposals/proposal_enforce_normalize_whole_subtree_whitespace_rule.md).
/// Byte-exact pins for <c>InstrumentationEngine.AddTryCatchToClassAsync</c> and <c>AddStopwatchDiagnosticsAsync</c>,
/// the annotated-container sites routed onto <c>RoslynFormattingHelper.FormatAnnotatedSpansAsync</c>. The source
/// carries irregular spacing, comments and blank lines in code the engine does not touch (the sibling members), so a
/// regression that formats the whole document shows up as a diff. Written against the unmodified call (plain
/// <c>Formatter.FormatAsync(doc, annotation)</c>) and kept unchanged across the routing, except where a test comment
/// says the expectation is the corrected end-of-line output.
/// </summary>
[TestFixture]
[Category("InstrumentationEngine")] // sentinel:auto-category
public class InstrumentationEngineFormatCharacterizationTests
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

    /// <summary>Joins lines with LF (no trailing newline), then converts to CRLF when requested.</summary>
    private static string Src(bool crlf, params string[] lines)
    {
        var text = string.Join("\n", lines);
        return crlf ? text.Replace("\n", "\r\n") : text;
    }

    private static string[] Input() =>
    [
        "using System;",
        "",
        "public class Worker",
        "{",
        "    // keep   this   comment",
        "    private   int _count;",
        "",
        "",
        "    public void  Run( )",
        "    {",
        "        // inner comment",
        "        _count++;",
        "",
        "        Console.WriteLine( _count );",
        "    }",
        "",
        "    private int   Helper( ) { return  1; }",
        "}",
    ];

    private static string[] TryCatchExpected() =>
    [
        "using System;",
        "",
        "public class Worker",
        "{",
        "    // keep   this   comment",
        "    private   int _count;",
        "",
        "",
        "    public void Run()",
        "    {",
        "        try",
        "        {",
        "            // inner comment",
        "            _count++;",
        "",
        "            Console.WriteLine(_count);",
        "        }",
        "        catch (Exception ex)",
        "        {",
        "            throw;",
        "        }",
        "    }",
        "",
        "    private int   Helper( ) { return  1; }",
        "}",
    ];

    private static string[] StopwatchExpected() =>
    [
        "using System;",
        "using System.Diagnostics;",
        "",
        "public class Worker",
        "{",
        "    // keep   this   comment",
        "    private   int _count;",
        "",
        "",
        "    public void Run()",
        "    {",
        "        var sw = Stopwatch.StartNew();",
        "        try",
        "        {",
        "            // inner comment",
        "            _count++;",
        "",
        "            Console.WriteLine(_count);",
        "        }",
        "        finally",
        "        {",
        "            Console.WriteLine($\"[Run] completed in {sw.ElapsedMilliseconds}ms\");",
        "        }",
        "    }",
        "",
        "    private int   Helper( ) { return  1; }",
        "}",
    ];

    // ---- AddTryCatchToClassAsync ----

    [TestCase(false)]
    [TestCase(true)]
    public async Task AddTryCatchToClass_UntouchedCodeKeepsIrregularSpacing_ByteExact(bool crlf)
    {
        SetSource(Src(crlf, Input()));

        var result = await _engine.AddTryCatchToClassAsync("Test.cs", "Worker");

        Assert.That(result.Outcome, Is.EqualTo(EditOutcome.Modified));
        Assert.That(result.UpdatedText, Is.EqualTo(Src(crlf, TryCatchExpected())));
    }

    [Test]
    public async Task AddTryCatchToClass_LfDocument_ResultHasNoCarriageReturn()
    {
        SetSource(Src(false, Input()));

        var result = await _engine.AddTryCatchToClassAsync("Test.cs", "Worker");

        Assert.That(result.UpdatedText, Does.Not.Contain("\r"));
    }

    [Test]
    public async Task AddTryCatchToClass_CrlfDocument_ResultHasNoBareLineFeed()
    {
        SetSource(Src(true, Input()));

        var result = await _engine.AddTryCatchToClassAsync("Test.cs", "Worker");

        Assert.That(result.UpdatedText!.Replace("\r\n", ""), Does.Not.Contain("\n"));
    }

    [Test]
    public async Task AddTryCatchToClass_MixedEol_StrayCrlfLineOutsideTheEditKeepsItsTerminator()
    {
        // An LF document with one CRLF terminator on a line the engine does not touch.
        var source = Src(false, Input()).Replace("    private   int _count;\n", "    private   int _count;\r\n");
        SetSource(source);

        var result = await _engine.AddTryCatchToClassAsync("Test.cs", "Worker");

        var expected = Src(false, TryCatchExpected()).Replace("    private   int _count;\n", "    private   int _count;\r\n");
        Assert.That(result.UpdatedText, Is.EqualTo(expected));
    }

    // ---- AddStopwatchDiagnosticsAsync ----

    [TestCase(false)]
    [TestCase(true)]
    public async Task AddStopwatchDiagnostics_UntouchedCodeKeepsIrregularSpacing_ByteExact(bool crlf)
    {
        SetSource(Src(crlf, Input()));

        var result = await _engine.AddStopwatchDiagnosticsAsync("Test.cs", "Run");

        Assert.That(result.Outcome, Is.EqualTo(EditOutcome.Modified));
        Assert.That(result.UpdatedText, Is.EqualTo(Src(crlf, StopwatchExpected())));
    }

    [Test]
    public async Task AddStopwatchDiagnostics_LfDocument_ResultHasNoCarriageReturn()
    {
        SetSource(Src(false, Input()));

        var result = await _engine.AddStopwatchDiagnosticsAsync("Test.cs", "Run");

        Assert.That(result.UpdatedText, Does.Not.Contain("\r"));
    }

    [Test]
    public async Task AddStopwatchDiagnostics_CrlfDocument_ResultHasNoBareLineFeed()
    {
        SetSource(Src(true, Input()));

        var result = await _engine.AddStopwatchDiagnosticsAsync("Test.cs", "Run");

        Assert.That(result.UpdatedText!.Replace("\r\n", ""), Does.Not.Contain("\n"));
    }

    [Test]
    public async Task AddStopwatchDiagnostics_MixedEol_StrayCrlfLineOutsideTheEditKeepsItsTerminator()
    {
        var source = Src(false, Input()).Replace("    private   int _count;\n", "    private   int _count;\r\n");
        SetSource(source);

        var result = await _engine.AddStopwatchDiagnosticsAsync("Test.cs", "Run");

        var expected = Src(false, StopwatchExpected()).Replace("    private   int _count;\n", "    private   int _count;\r\n");
        Assert.That(result.UpdatedText, Is.EqualTo(expected));
    }
}
