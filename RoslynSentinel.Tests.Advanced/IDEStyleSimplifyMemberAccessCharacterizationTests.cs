using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Engines.Advanced;

#pragma warning disable CS8618
namespace RoslynSentinel.Tests.Advanced;

/// <summary>
/// C5a characterization tests (docs/current/proposals/proposal_enforce_normalize_whole_subtree_whitespace_rule.md).
/// Byte-exact pins for <c>IDEStyleEngine.SimplifyMemberAccessAsync</c>, the annotated-container site routed onto
/// <c>RoslynFormattingHelper.FormatAnnotatedSpansAsync</c>. The source carries irregular spacing, comments and blank
/// lines in code the engine does not touch, so a regression that formats the whole document shows up as a diff.
/// Written against the unmodified call (plain <c>Formatter.FormatAsync(doc, annotation)</c>) and kept unchanged
/// across the routing.
/// </summary>
[TestFixture]
[Category("IDEStyleEngine")] // sentinel:auto-category
public class IDEStyleSimplifyMemberAccessCharacterizationTests
{
    private IWorkspaceManager _workspaceManager;
    private IDEStyleEngine _engine;

    [SetUp]
    public void Setup()
    {
        _workspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        _engine = new IDEStyleEngine(_workspaceManager);
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
        "public class Counter",
        "{",
        "    // keep   this   comment",
        "    private   int _count;",
        "    private int   _other=5;",
        "",
        "",
        "    public int GetCount() { return this._count; }",
        "",
        "    public void  Increment( )",
        "    {",
        "        this._count++;",
        "        var   x=  this._other;",
        "        Console.WriteLine( x );",
        "    }",
        "}",
    ];

    [TestCase(false)]
    [TestCase(true)]
    public async Task SimplifyMemberAccess_UntouchedCodeKeepsIrregularSpacing_ByteExact(bool crlf)
    {
        SetSource(Src(crlf, Input()));

        var result = await _engine.SimplifyMemberAccessAsync("Test.cs");

        Assert.That(result.Outcome, Is.EqualTo(EditOutcome.Modified));
        var expected = Src(crlf,
            "using System;",
            "",
            "public class Counter",
            "{",
            "    // keep   this   comment",
            "    private   int _count;",
            "    private int   _other=5;",
            "",
            "",
            "    public int GetCount() { return _count; }",
            "",
            "    public void  Increment( )",
            "    {",
            "        _count++;",
            "        var   x= _other;",
            "        Console.WriteLine( x );",
            "    }",
            "}");
        // Before slice C5a (plain Formatter.FormatAsync(doc, annotation)) an LF document came back with one bare
        // CRLF after the opening brace of Increment: the formatter rewrites the line break in front of the annotated
        // node with its own NewLine option (CRLF on this host). Routing the call onto FormatAnnotatedSpansAsync
        // normalizes it, so this expectation is the corrected output; everything else is unchanged.
        Assert.That(result.UpdatedText, Is.EqualTo(expected));
    }

    private static string[] CommentedInput() =>
    [
        "public class Counter",
        "{",
        "    private int _count;",
        "",
        "    public void Reset()",
        "    {",
        "        var y = 1;",
        "        // reset the counter",
        "",
        "        this._count = 0;",
        "    }",
        "}",
    ];

    private static string[] CommentedExpected() =>
    [
        "public class Counter",
        "{",
        "    private int _count;",
        "",
        "    public void Reset()",
        "    {",
        "        var y = 1;",
        "        // reset the counter",
        "",
        "        _count = 0;",
        "    }",
        "}",
    ];

    // Regression pin: the replacement must keep the trivia that precedes 'this' (a comment and a blank line).
    [TestCase(false)]
    [TestCase(true)]
    public async Task SimplifyMemberAccess_CommentAndBlankLineBeforeThis_ArePreservedByteExact(bool crlf)
    {
        SetSource(Src(crlf, CommentedInput()));

        var result = await _engine.SimplifyMemberAccessAsync("Test.cs");

        Assert.That(result.Outcome, Is.EqualTo(EditOutcome.Modified));
        Assert.That(result.UpdatedText, Is.EqualTo(Src(crlf, CommentedExpected())));
    }

    [Test]
    public async Task SimplifyMemberAccess_LfDocument_ResultHasNoCarriageReturn()
    {
        // Failed before C5a routing (one bare CRLF after the opening brace); the intended EOL-consistency fix.
        SetSource(Src(false, Input()));

        var result = await _engine.SimplifyMemberAccessAsync("Test.cs");

        Assert.That(result.UpdatedText, Does.Not.Contain("\r"));
    }

    [Test]
    public async Task SimplifyMemberAccess_CrlfDocument_ResultHasNoBareLineFeed()
    {
        SetSource(Src(true, Input()));

        var result = await _engine.SimplifyMemberAccessAsync("Test.cs");

        Assert.That(result.UpdatedText!.Replace("\r\n", ""), Does.Not.Contain("\n"));
    }

    [Test]
    public async Task SimplifyMemberAccess_MixedEol_StrayCrlfOnTheBraceLineBeforeTheEditKeepsItsTerminator()
    {
        // An LF document with one CRLF terminator on the "{" line of the edited method: the very line whose
        // terminator the formatter rewrites with its own NewLine option (the line break in front of the first
        // annotated statement), so this discriminates. The routed helper keeps the terminator of a line that
        // survives, so the stray CRLF must stay, while every edited line takes the document's dominant (LF) ending.
        var source = Src(false, Input()).Replace("    {\n        this._count++;", "    {\r\n        this._count++;");
        Assert.That(source, Does.Contain("    {\r\n        this._count++;"), "Precondition: the stray CRLF is on the brace line.");
        SetSource(source);

        var result = await _engine.SimplifyMemberAccessAsync("Test.cs");

        var expected = source
            .Replace("return this._count;", "return _count;")
            .Replace("this._count++;", "_count++;")
            .Replace("=  this._other;", "= _other;");
        Assert.That(result.UpdatedText, Is.EqualTo(expected));
    }

    [Test]
    public async Task SimplifyMemberAccess_NoThisAccess_ReturnsNoCandidatesWithSourceUnchanged()
    {
        var source = Src(false, "public class C", "{", "    public int  M( ) { return 1; }", "}");
        SetSource(source);

        var result = await _engine.SimplifyMemberAccessAsync("Test.cs");

        Assert.That(result.Outcome, Is.EqualTo(EditOutcome.NoCandidatesFound));
        Assert.That(result.UpdatedText, Is.EqualTo(source));
    }

    [Test]
    public async Task SimplifyMemberAccess_MissingFile_ThrowsNotFound()
    {
        SetSource("public class C { }");
        var ex = await Assert.ThrowsAsync<ToolNotFoundException>(async () => await _engine.SimplifyMemberAccessAsync("Missing.cs"));
        Assert.That(ex.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound));
    }
}
