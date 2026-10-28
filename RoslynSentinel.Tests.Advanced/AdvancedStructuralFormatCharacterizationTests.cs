using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Engines.Advanced;

#pragma warning disable CS8618
namespace RoslynSentinel.Tests.Advanced;

/// <summary>
/// C5b-2a characterization tests (docs/current/proposals/proposal_enforce_normalize_whole_subtree_whitespace_rule.md).
/// Byte-exact pins for <c>StructuralRefactoringEngine.IntroduceParameterObjectAsync</c>: the main path (rewritten method
/// plus appended record) and the interface-warning path (rewritten method only, returned with a warning message), the
/// two annotated-container sites routed onto <c>RoslynFormattingHelper.FormatAnnotatedSpansAsync</c>. The source
/// carries irregular spacing, comments and blank lines in code the engine does not touch, so a regression that formats
/// the whole document shows up as a diff. Written against the unmodified call (plain
/// <c>Formatter.FormatAsync(doc, annotation)</c>). The CRLF tests are byte-identical to the pre-routing behaviour; the
/// LF and mixed-EOL tests encode the corrected output (the old code leaked formatter-inserted CRLF).
/// </summary>
[TestFixture]
[Category("StructuralRefactoringEngine")] // sentinel:auto-category
public class AdvancedStructuralFormatCharacterizationTests
{
    private PersistentWorkspaceManager _workspaceManager;
    private StructuralRefactoringEngine _engine;

    [SetUp]
    public void Setup()
    {
        _workspaceManager = Battery.Basic.TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        _engine = Battery.Advanced.TestEnginesAdvanced.StructuralRefactoringEngine(_workspaceManager);
    }

    [TearDown]
    public void TearDown() => _workspaceManager?.Dispose();

    private void SetSource(string source, string fileName = "Test.cs")
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [(fileName, source)]);
        _workspaceManager.SetTestSolution(solution);
    }

    /// <summary>Joins lines with LF (a trailing newline only if the last line is empty), then converts to CRLF when requested.</summary>
    private static string Src(bool crlf, params string[] lines)
    {
        var text = string.Join("\n", lines);
        return crlf ? text.Replace("\n", "\r\n") : text;
    }

    // An LF document with exactly one CRLF terminator, the last character of the anchor. Two placements: the opening
    // brace of the method the engine rewrites (inside the annotated node), and the `_count` field line right before
    // the rewritten method (the line break there is formatter-owned: the formatter may re-space the whitespace in
    // front of an annotated node). The expected bytes are the LF expectation with that same one terminator kept.
    private const string MethodBraceAnchor = "\n    {\n";
    private const string PreMethodLineAnchor = "    private   int _count;\n";

    private static string WithStrayCrlf(string lfText, string anchor)
    {
        Assert.That(lfText, Does.Contain(anchor), "Precondition: the anchor line is present.");
        var mixed = lfText.Replace(anchor, anchor.Substring(0, anchor.Length - 1) + "\r\n");
        Assert.That(mixed.Count(c => c == '\r'), Is.EqualTo(1), "Precondition: exactly one stray CRLF.");
        return mixed;
    }

    // ---- main path: no interface involved; the method is rewritten and the record appended ----

    private static string[] MainInput() =>
    [
        "using System;",
        "",
        "public class Worker",
        "{",
        "    // keep   this   comment",
        "    private   int _count;",
        "",
        "",
        "    public void  Run( int  a, string b )",
        "    {",
        "        // inner comment",
        "        _count += a;",
        "",
        "        Console.WriteLine( b );",
        "    }",
        "",
        "    private int   Helper( ) { return  1; }",
        "}",
    ];

    private static string[] MainExpected() =>
    [
        "using System;",
        "",
        "public class Worker",
        "{",
        "    // keep   this   comment",
        "    private   int _count;",
        "",
        "",
        "    public void Run(RunParameters request)",
        "    {",
        "        // TODO: Update call sites to use RunParameters",
        "        // inner comment",
        "        _count += request.A;",
        "",
        "        Console.WriteLine(request.B);",
        "    }",
        "",
        "    private int   Helper( ) { return  1; }",
        "}",
        "",
        "public record RunParameters(int A, string B);",
        "",
    ];

    [TestCase(false)]
    [TestCase(true)]
    public async Task IntroduceParameterObject_MainPath_UntouchedCodeKeepsIrregularSpacing_ByteExact(bool crlf)
    {
        SetSource(Src(crlf, MainInput()));

        var result = await _engine.IntroduceParameterObjectAsync("Test.cs", "Run");

        Assert.That(result.Outcome, Is.EqualTo(EditOutcome.Modified));
        Assert.That(result.UpdatedText, Is.EqualTo(Src(crlf, MainExpected())));
    }

    [Test]
    public async Task IntroduceParameterObject_MainPath_LfDocument_ResultHasNoCarriageReturn()
    {
        SetSource(Src(false, MainInput()));

        var result = await _engine.IntroduceParameterObjectAsync("Test.cs", "Run");

        Assert.That(result.UpdatedText, Does.Not.Contain("\r"));
    }

    [Test]
    public async Task IntroduceParameterObject_MainPath_CrlfDocument_ResultHasNoBareLineFeed()
    {
        SetSource(Src(true, MainInput()));

        var result = await _engine.IntroduceParameterObjectAsync("Test.cs", "Run");

        Assert.That(result.UpdatedText.Replace("\r\n", ""), Does.Not.Contain("\n"));
    }

    [TestCase(MethodBraceAnchor)]
    [TestCase(PreMethodLineAnchor)]
    public async Task IntroduceParameterObject_MainPath_MixedEol_StrayCrlfLineKeepsItsTerminator(string anchor)
    {
        // Changed lines take the resolved EOL, not the majority one; Lf keeps this LF-majority mixed source's pinned expectation.
        _workspaceManager.EolModeOverride = EolMode.Lf;

        SetSource(WithStrayCrlf(Src(false, MainInput()), anchor));

        var result = await _engine.IntroduceParameterObjectAsync("Test.cs", "Run");

        Assert.That(result.UpdatedText, Is.EqualTo(WithStrayCrlf(Src(false, MainExpected()), anchor)));
    }

    // ---- empty body: the engine puts the TODO comment on the method's closing brace trivia ----

    private static string[] EmptyBodyInput() =>
    [
        "using System;",
        "",
        "public class Worker",
        "{",
        "    private   int _count;",
        "",
        "    public void  Run( int  a, string b )",
        "    {",
        "    }",
        "}",
    ];

    private static string[] EmptyBodyExpected() =>
    [
        "using System;",
        "",
        "public class Worker",
        "{",
        "    private   int _count;",
        "",
        "    public void Run(RunParameters request)",
        "    {",
        "        // TODO: Update call sites to use RunParameters",
        "    }",
        "}",
        "",
        "public record RunParameters(int A, string B);",
        "",
    ];

    [TestCase(false)]
    [TestCase(true)]
    public async Task IntroduceParameterObject_EmptyBody_ByteExact(bool crlf)
    {
        SetSource(Src(crlf, EmptyBodyInput()));

        var result = await _engine.IntroduceParameterObjectAsync("Test.cs", "Run");

        Assert.That(result.Outcome, Is.EqualTo(EditOutcome.Modified));
        Assert.That(result.UpdatedText, Is.EqualTo(Src(crlf, EmptyBodyExpected())));
    }

    // ---- warning path: the method implements an interface, so only the method is rewritten ----
    // The interface is declared after the class so that the by-name lookup finds the class method first.

    private static string[] WarnInput() =>
    [
        "using System;",
        "",
        "public class Worker : IWorker",
        "{",
        "    // keep   this   comment",
        "    private   int _count;",
        "",
        "",
        "    public void  Run( int  a, string b )",
        "    {",
        "        // inner comment",
        "        _count += a;",
        "",
        "        Console.WriteLine( b );",
        "    }",
        "",
        "    private int   Helper( ) { return  1; }",
        "}",
        "",
        "public interface IWorker",
        "{",
        "    void Run(int a, string b);",
        "}",
    ];

    private static string[] WarnExpected() =>
    [
        "using System;",
        "",
        "public class Worker : IWorker",
        "{",
        "    // keep   this   comment",
        "    private   int _count;",
        "",
        "",
        "    public void Run(RunParameters request)",
        "    {",
        "        // TODO: Update call sites to use RunParameters",
        "        // inner comment",
        "        _count += request.A;",
        "",
        "        Console.WriteLine(request.B);",
        "    }",
        "",
        "    private int   Helper( ) { return  1; }",
        "}",
        "",
        "public interface IWorker",
        "{",
        "    void Run(int a, string b);",
        "}",
        "",
        "public record RunParameters(int A, string B);",
        "",
    ];

    [TestCase(false)]
    [TestCase(true)]
    public async Task IntroduceParameterObject_WarningPath_UntouchedCodeKeepsIrregularSpacing_ByteExact(bool crlf)
    {
        SetSource(Src(crlf, WarnInput()));

        var result = await _engine.IntroduceParameterObjectAsync("Test.cs", "Run");

        Assert.That(result.Outcome, Is.EqualTo(EditOutcome.Modified));
        Assert.That(result.Message, Does.Contain("WARNING: This method implements an interface."));
        Assert.That(result.UpdatedText, Is.EqualTo(Src(crlf, WarnExpected())));
    }

    [Test]
    public async Task IntroduceParameterObject_WarningPath_LfDocument_ResultHasNoCarriageReturn()
    {
        SetSource(Src(false, WarnInput()));

        var result = await _engine.IntroduceParameterObjectAsync("Test.cs", "Run");

        Assert.That(result.Message, Does.Contain("WARNING: This method implements an interface."));
        Assert.That(result.UpdatedText, Does.Not.Contain("\r"));
    }

    [Test]
    public async Task IntroduceParameterObject_WarningPath_CrlfDocument_ResultHasNoBareLineFeed()
    {
        SetSource(Src(true, WarnInput()));

        var result = await _engine.IntroduceParameterObjectAsync("Test.cs", "Run");

        Assert.That(result.Message, Does.Contain("WARNING: This method implements an interface."));
        Assert.That(result.UpdatedText.Replace("\r\n", ""), Does.Not.Contain("\n"));
    }

    [TestCase(MethodBraceAnchor)]
    [TestCase(PreMethodLineAnchor)]
    public async Task IntroduceParameterObject_WarningPath_MixedEol_StrayCrlfLineKeepsItsTerminator(string anchor)
    {
        // Changed lines take the resolved EOL, not the majority one; Lf keeps this LF-majority mixed source's pinned expectation.
        _workspaceManager.EolModeOverride = EolMode.Lf;

        SetSource(WithStrayCrlf(Src(false, WarnInput()), anchor));

        var result = await _engine.IntroduceParameterObjectAsync("Test.cs", "Run");

        Assert.That(result.Message, Does.Contain("WARNING: This method implements an interface."));
        Assert.That(result.UpdatedText, Is.EqualTo(WithStrayCrlf(Src(false, WarnExpected()), anchor)));
    }
}
