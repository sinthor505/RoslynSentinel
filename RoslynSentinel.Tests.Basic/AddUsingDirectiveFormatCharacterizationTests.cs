using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Engines.Basic;

#pragma warning disable CS8618
namespace RoslynSentinel.Tests.Basic;

/// <summary>
/// C5b-1 characterization tests (docs/current/proposals/proposal_enforce_normalize_whole_subtree_whitespace_rule.md).
/// Byte-exact pins for <c>BasicRefactoringEngine.AddUsingDirectiveAsync</c>, an annotated-container site routed onto
/// <c>RoslynFormattingHelper.FormatAnnotatedSpansAsync</c>. The source carries irregular spacing, comments and blank
/// lines in code the engine does not touch, so a regression that formats the whole document shows up as a diff.
/// Written against the unmodified call (plain <c>Formatter.FormatAsync(doc, annotation)</c>) and kept unchanged
/// across the routing, except where a test comment says the expectation is the corrected end-of-line output.
/// The <c>simplifyExisting: true</c> tests pin the second, whole-document format that path runs; that call is not an
/// annotated-container site and is deliberately left in place by C5b-1.
/// </summary>
[TestFixture]
[Category("BasicRefactoringEngine")] // sentinel:auto-category
public class AddUsingDirectiveFormatCharacterizationTests
{
    private IWorkspaceManager _workspaceManager;
    private BasicRefactoringEngine _engine;

    [SetUp]
    public void Setup()
    {
        _workspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        _engine = new BasicRefactoringEngine(
            _workspaceManager,
            NullLogger<BasicRefactoringEngine>.Instance,
            new SentinelConfiguration());
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

    private static string[] WithUsingsInput() =>
    [
        "using System;",
        "",
        "namespace Demo",
        "{",
        "    // keep   this   comment",
        "    public class   Foo",
        "    {",
        "        private int   _x=1;",
        "",
        "",
        "        public void  M( ) { }",
        "    }",
        "}",
    ];

    private static string[] WithUsingsExpected() =>
    [
        "using System;",
        "using System.Linq;",
        "",
        "namespace Demo",
        "{",
        "    // keep   this   comment",
        "    public class   Foo",
        "    {",
        "        private int   _x=1;",
        "",
        "",
        "        public void  M( ) { }",
        "    }",
        "}",
    ];

    private static string[] NoUsingsInput() =>
    [
        "// header   comment",
        "public class   Foo",
        "{",
        "    private int   _x=1;",
        "",
        "    public void  M( ) { }",
        "}",
    ];

    private static string[] NoUsingsExpected() =>
    [
        "using System.Linq;",
        "// header   comment",
        "public class   Foo",
        "{",
        "    private int   _x=1;",
        "",
        "    public void  M( ) { }",
        "}",
    ];

    // With simplifyExisting the engine runs a second, whole-document Formatter.FormatAsync; this is what it does
    // to irregular code today (the Simplifier.ReduceAsync step has no Simplifier.Annotation to act on).
    private static string[] SimplifyExpected() =>
    [
        "using System;",
        "using System.Linq;",
        "",
        "namespace Demo",
        "{",
        "    // keep   this   comment",
        "    public class Foo",
        "    {",
        "        private int _x = 1;",
        "",
        "",
        "        public void M() { }",
        "    }",
        "}",
    ];

    [TestCase(false)]
    [TestCase(true)]
    public async Task AddUsingDirective_ExistingUsings_UntouchedCodeKeepsIrregularSpacing_ByteExact(bool crlf)
    {
        SetSource(Src(crlf, WithUsingsInput()));

        var result = await _engine.AddUsingDirectiveAsync("Test.cs", "System.Linq");

        Assert.That(result.Outcome, Is.EqualTo(EditOutcome.Modified));
        Assert.That(result.UpdatedText, Is.EqualTo(Src(crlf, WithUsingsExpected())));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task AddUsingDirective_NoExistingUsings_UntouchedCodeKeepsIrregularSpacing_ByteExact(bool crlf)
    {
        SetSource(Src(crlf, NoUsingsInput()));

        var result = await _engine.AddUsingDirectiveAsync("Test.cs", "System.Linq");

        Assert.That(result.Outcome, Is.EqualTo(EditOutcome.Modified));
        Assert.That(result.UpdatedText, Is.EqualTo(Src(crlf, NoUsingsExpected())));
    }

    [Test]
    public async Task AddUsingDirective_LfDocument_ResultHasNoCarriageReturn()
    {
        SetSource(Src(false, WithUsingsInput()));

        var result = await _engine.AddUsingDirectiveAsync("Test.cs", "System.Linq");

        Assert.That(result.UpdatedText, Does.Not.Contain("\r"));
    }

    [Test]
    public async Task AddUsingDirective_CrlfDocument_ResultHasNoBareLineFeed()
    {
        SetSource(Src(true, WithUsingsInput()));

        var result = await _engine.AddUsingDirectiveAsync("Test.cs", "System.Linq");

        Assert.That(result.UpdatedText!.Replace("\r\n", ""), Does.Not.Contain("\n"));
    }

    [Test]
    public async Task AddUsingDirective_MixedEol_StrayCrlfLineOutsideTheEditKeepsItsTerminator()
    {
        // An LF document with one CRLF terminator on a line the engine does not touch. Mixed documents are the only
        // case whose bytes can differ across the routing: the old tail normalized only single-style documents.
        var source = Src(false, WithUsingsInput()).Replace("        private int   _x=1;\n", "        private int   _x=1;\r\n");
        SetSource(source);

        var result = await _engine.AddUsingDirectiveAsync("Test.cs", "System.Linq");

        var expected = Src(false, WithUsingsExpected()).Replace("        private int   _x=1;\n", "        private int   _x=1;\r\n");
        Assert.That(result.UpdatedText, Is.EqualTo(expected));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task AddUsingDirective_SimplifyExisting_WholeDocumentFormatPinned_ByteExact(bool crlf)
    {
        SetSource(Src(crlf, WithUsingsInput()));

        var result = await _engine.AddUsingDirectiveAsync("Test.cs", "System.Linq", simplifyExisting: true);

        Assert.That(result.Outcome, Is.EqualTo(EditOutcome.Modified));
        Assert.That(result.UpdatedText, Is.EqualTo(Src(crlf, SimplifyExpected())));
    }
}
