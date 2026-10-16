using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Engines.Advanced;

#pragma warning disable CS8618
namespace RoslynSentinel.Tests.Advanced;

/// <summary>
/// C5b-1 characterization tests (docs/current/proposals/proposal_enforce_normalize_whole_subtree_whitespace_rule.md).
/// Byte-exact pins for <c>MappingEngine.InvertAssignmentsAsync</c>, an annotated-container site routed onto
/// <c>RoslynFormattingHelper.FormatAnnotatedSpansAsync</c>. The source carries irregular spacing, comments and blank
/// lines in code the engine does not touch, so a regression that formats the whole document shows up as a diff.
/// Written against the unmodified call (plain <c>Formatter.FormatAsync(doc, annotation)</c>) and kept unchanged
/// across the routing, except where a test comment says the expectation is the corrected end-of-line output.
/// </summary>
[TestFixture]
[Category("MappingEngine")] // sentinel:auto-category
public class MappingEngineInvertAssignmentsCharacterizationTests
{
    private IWorkspaceManager _workspaceManager;
    private MappingEngine _engine;

    [SetUp]
    public void Setup()
    {
        _workspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        _engine = new MappingEngine(_workspaceManager);
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

    // Lines 15-16 hold the assignments the tests invert; everything else is untouched. The comment and the blank
    // lines sit before a non-assignment statement on purpose: the engine swaps whole operands including their
    // leading trivia, so a comment or blank line directly in front of an inverted assignment travels to its right-hand
    // side (a defect of the swap, not of the format step; see the C5b-1 report).
    private static string[] Input() =>
    [
        "using System;",
        "",
        "public class Mapper",
        "{",
        "    private int  a;",
        "    private int   b = 2;",
        "",
        "    public void  Sync( int x )",
        "    {",
        "        // keep   this   comment",
        "        Console.WriteLine( x );",
        "",
        "",
        "        Console.WriteLine( a );",
        "        a  =  x;",
        "        b=a;",
        "        Console.WriteLine( b );",
        "    }",
        "}",
    ];

    private static string[] Expected() =>
    [
        "using System;",
        "",
        "public class Mapper",
        "{",
        "    private int  a;",
        "    private int   b = 2;",
        "",
        "    public void  Sync( int x )",
        "    {",
        "        // keep   this   comment",
        "        Console.WriteLine( x );",
        "",
        "",
        "        Console.WriteLine( a );",
        "        x = a;",
        "        a = b;",
        "        Console.WriteLine( b );",
        "    }",
        "}",
    ];

    [TestCase(false)]
    [TestCase(true)]
    public async Task InvertAssignments_UntouchedCodeKeepsIrregularSpacing_ByteExact(bool crlf)
    {
        SetSource(Src(crlf, Input()));

        var result = await _engine.InvertAssignmentsAsync("Test.cs", 15, 16);

        Assert.That(result.Outcome, Is.EqualTo(EditOutcome.Modified));
        Assert.That(result.UpdatedText, Is.EqualTo(Src(crlf, Expected())));
    }

    [Test]
    public async Task InvertAssignments_LfDocument_ResultHasNoCarriageReturn()
    {
        SetSource(Src(false, Input()));

        var result = await _engine.InvertAssignmentsAsync("Test.cs", 15, 16);

        Assert.That(result.UpdatedText, Does.Not.Contain("\r"));
    }

    [Test]
    public async Task InvertAssignments_CrlfDocument_ResultHasNoBareLineFeed()
    {
        SetSource(Src(true, Input()));

        var result = await _engine.InvertAssignmentsAsync("Test.cs", 15, 16);

        Assert.That(result.UpdatedText!.Replace("\r\n", ""), Does.Not.Contain("\n"));
    }

    [Test]
    public async Task InvertAssignments_MixedEol_StrayCrlfLineOutsideTheEditKeepsItsTerminator()
    {
        var source = Src(false, Input()).Replace("    private int   b = 2;\n", "    private int   b = 2;\r\n");
        SetSource(source);

        var result = await _engine.InvertAssignmentsAsync("Test.cs", 15, 16);

        var expected = Src(false, Expected()).Replace("    private int   b = 2;\n", "    private int   b = 2;\r\n");
        Assert.That(result.UpdatedText, Is.EqualTo(expected));
    }

    // ---- snippet overload: InvertAssignmentsAsync(filePath, contextSnippet, lineBefore, lineAfter) ----

    // Only line 15 (`a  =  x;`) is located by the snippet, so only that assignment is inverted.
    private static string[] SnippetExpected() =>
    [
        "using System;",
        "",
        "public class Mapper",
        "{",
        "    private int  a;",
        "    private int   b = 2;",
        "",
        "    public void  Sync( int x )",
        "    {",
        "        // keep   this   comment",
        "        Console.WriteLine( x );",
        "",
        "",
        "        Console.WriteLine( a );",
        "        x = a;",
        "        b=a;",
        "        Console.WriteLine( b );",
        "    }",
        "}",
    ];

    [TestCase(false)]
    [TestCase(true)]
    public async Task InvertAssignmentsBySnippet_UntouchedCodeKeepsIrregularSpacing_ByteExact(bool crlf)
    {
        SetSource(Src(crlf, Input()));

        var result = await _engine.InvertAssignmentsAsync("Test.cs", "a  =  x;", null, null);

        Assert.That(result.Outcome, Is.EqualTo(EditOutcome.Modified));
        Assert.That(result.UpdatedText, Is.EqualTo(Src(crlf, SnippetExpected())));
    }

    [Test]
    public async Task InvertAssignmentsBySnippet_LfDocument_ResultHasNoCarriageReturn()
    {
        SetSource(Src(false, Input()));

        var result = await _engine.InvertAssignmentsAsync("Test.cs", "a  =  x;", null, null);

        Assert.That(result.UpdatedText, Does.Not.Contain("\r"));
    }

    [Test]
    public async Task InvertAssignmentsBySnippet_MixedEol_StrayCrlfLineOutsideTheEditKeepsItsTerminator()
    {
        var source = Src(false, Input()).Replace("    private int   b = 2;\n", "    private int   b = 2;\r\n");
        SetSource(source);

        var result = await _engine.InvertAssignmentsAsync("Test.cs", "a  =  x;", null, null);

        var expected = Src(false, SnippetExpected()).Replace("    private int   b = 2;\n", "    private int   b = 2;\r\n");
        Assert.That(result.UpdatedText, Is.EqualTo(expected));
    }
}
