using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Engines.Basic;

#pragma warning disable CS8618
namespace RoslynSentinel.Tests.Advanced;

/// <summary>
/// C5b-2b characterization tests (docs/current/proposals/proposal_enforce_normalize_whole_subtree_whitespace_rule.md).
/// Byte-exact pins for three span-scoped <c>Formatter.FormatAsync(doc, annotation)</c> sites routed onto
/// <c>RoslynFormattingHelper.FormatAnnotatedSpansAsync</c>:
/// <c>ConstructorCallSiteFinder.ApplyAsync</c> (driven through <c>MemberRefactoringEngine.AddConstructorParameterWithCallSitesAsync</c>),
/// <c>MemberRefactoringEngine.RemoveMethodParameterAsync</c> and the usage-replacement format in
/// <c>SemanticRefactoringEngine.InlineVariableAsync</c>. The source carries irregular spacing, comments and blank lines
/// in code the engine does not touch, so a regression that formats the whole document shows up as a diff. Written
/// against the unmodified calls (plain <c>Formatter.FormatAsync(doc, annotation)</c>). The CRLF tests are
/// byte-identical to the pre-routing behaviour; the LF and mixed-EOL tests encode the corrected output (the old
/// code leaked formatter-inserted CRLF into LF documents, or flattened a stray CRLF line to LF).
/// <para>
/// The constructor cascade is not pinned for mixed EOL, only for single-EOL files. Both of its output paths still
/// flatten a stray CRLF line and neither is a span-scoped <c>Formatter.FormatAsync</c> site: caller files go through
/// a whole-file <c>EolUtilities.NormalizeEol</c> in <c>AddConstructorParameterWithCallSitesAsync</c>, and the class's
/// own file goes through <c>RoslynFormattingHelper.ReplaceNodeAndFormatReplacementAsync</c> (also whole-file
/// <c>NormalizeEol</c>). The cascade tests are therefore guards for the finder's routing, not red-to-green pins.
/// </para>
/// </summary>
[TestFixture]
[Category("MemberRefactoringEngine")] // sentinel:auto-category
public class RefactoringEngineFormatCharacterizationTests
{
    private IWorkspaceManager _workspaceManager;
    private MemberRefactoringEngine _memberEngine;
    private SemanticRefactoringEngine _semanticEngine;

    [SetUp]
    public void Setup()
    {
        _workspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        var navigation = new SymbolNavigationEngine(_workspaceManager, NullLogger<SymbolNavigationEngine>.Instance);
        _memberEngine = new MemberRefactoringEngine(_workspaceManager, navigation, new ValidationEngine(_workspaceManager));
        _semanticEngine = new SemanticRefactoringEngine(_workspaceManager);
    }

    [TearDown]
    public void TearDown() => _workspaceManager?.Dispose();

    private void SetFiles(params (string name, string content)[] files) =>
        _workspaceManager.SetTestSolution(TestSolutionBuilder.CreateSolutionWithProject("TestProj", files));

    /// <summary>Joins lines with LF (a trailing newline only if the last line is empty), then converts to CRLF when requested.</summary>
    private static string Src(bool crlf, params string[] lines)
    {
        var text = string.Join("\n", lines);
        return crlf ? text.Replace("\n", "\r\n") : text;
    }

    /// <summary>An LF text with exactly one CRLF terminator: the last character of <paramref name="anchor"/>.</summary>
    private static string WithStrayCrlf(string lfText, string anchor)
    {
        Assert.That(lfText, Does.Contain(anchor), "Precondition: the anchor line is present.");
        Assert.That(lfText.Split(anchor).Length, Is.EqualTo(2), "Precondition: the anchor line is unique.");
        var mixed = lfText.Replace(anchor, anchor.Substring(0, anchor.Length - 1) + "\r\n");
        Assert.That(mixed.Count(c => c == '\r'), Is.EqualTo(1), "Precondition: exactly one stray CRLF.");
        return mixed;
    }

    // ================================================================ ConstructorCallSiteFinder.ApplyAsync

    private const string SvcSource = "namespace TestProj;\npublic class Config { }\npublic class Svc\n{\n    public Svc(int a) { }\n}\n";

    private static string[] CallerInput(string className = "Caller") =>
    [
        "namespace TestProj;",
        "public class " + className,
        "{",
        "    // keep   this   comment",
        "    private   int _n;",
        "",
        "    public Svc Make()",
        "    {",
        "        return new Svc",
        "            (",
        "",
        "",
        "                  1);",
        "    }",
        "",
        "    private int   Helper( ) { return  1; }",
        "}",
        "",
    ];

    private static string[] CallerExpected(string className = "Caller") =>
    [
        "namespace TestProj;",
        "public class " + className,
        "{",
        "    // keep   this   comment",
        "    private   int _n;",
        "",
        "    public Svc Make()",
        "    {",
        "        return new Svc",
        "            (",
        "",
        "",
        "                  1, Create(() => { return 1; }));",
        "    }",
        "",
        "    private int   Helper( ) { return  1; }",
        "}",
        "",
    ];

    private async Task<AddConstructorParameterCascadeResult> CascadeAsync(string fixupKey)
    {
        var result = await _memberEngine.AddConstructorParameterWithCallSitesAsync("Svc.cs", "Svc", "config", "Config", new Dictionary<string, string> { [fixupKey] = "Create(() => { return 1; })" });
        Assert.That(result.InvalidArgumentMessage, Is.Null, result.ClassEdit.Message);
        Assert.That(result.UnresolvedSites, Is.Empty);
        Assert.That(result.CallSitesUpdated, Is.EqualTo(1));
        return result;
    }

    private static string ChangeFor(AddConstructorParameterCascadeResult result, string fileName) =>
        result.Changes.Single(kv => Path.GetFileName(kv.Key.ToString()) == fileName).Value;

    [TestCase(false)]
    [TestCase(true)]
    public async Task ConstructorCascade_CallerInOtherFile_UntouchedCodeKeepsIrregularSpacing_ByteExact(bool crlf)
    {
        SetFiles(("Svc.cs", Src(crlf, SvcSource.Split('\n'))), ("Caller.cs", Src(crlf, CallerInput())));

        var result = await CascadeAsync("Caller.cs:*");

        Assert.That(ChangeFor(result, "Caller.cs"), Is.EqualTo(Src(crlf, CallerExpected())));
    }

    // The call site lives in the class's own file: the cascade merges it with the class edit, so the text the
    // call-site pass produced is the pre-image of the class edit and a leaked CRLF would survive to the result.

    private static string[] SameFileInput() =>
    [
        "namespace TestProj;",
        "public class Config { }",
        "public class Svc",
        "{",
        "    public Svc(int a) { }",
        "}",
        "",
        .. CallerInput("Maker").Skip(1),
    ];

    // Pre-existing quirk pinned as-is (not part of this slice): the class edit puts the new field and the constructor
    // of a one-line-member class on the same line.
    private static string[] SameFileExpected() =>
    [
        "namespace TestProj;",
        "public class Config { }",
        "public class Svc",
        "{",
        "    private readonly Config _config; public Svc(int a, Config config) { _config = config; }",
        "}",
        "",
        .. CallerExpected("Maker").Skip(1),
    ];

    [TestCase(false)]
    [TestCase(true)]
    public async Task ConstructorCascade_CallerInClassFile_UntouchedCodeKeepsIrregularSpacing_ByteExact(bool crlf)
    {
        SetFiles(("Svc.cs", Src(crlf, SameFileInput())));

        var result = await CascadeAsync("Svc.cs:*");

        Assert.That(ChangeFor(result, "Svc.cs"), Is.EqualTo(Src(crlf, SameFileExpected())));
    }

    [Test]
    public async Task ConstructorCascade_CallerInClassFile_LfDocument_ResultHasNoCarriageReturn()
    {
        SetFiles(("Svc.cs", Src(false, SameFileInput())));

        var result = await CascadeAsync("Svc.cs:*");

        Assert.That(ChangeFor(result, "Svc.cs"), Does.Not.Contain("\r"));
    }

    [Test]
    public async Task ConstructorCascade_CallerInClassFile_CrlfDocument_ResultHasNoBareLineFeed()
    {
        SetFiles(("Svc.cs", Src(true, SameFileInput())));

        var result = await CascadeAsync("Svc.cs:*");

        Assert.That(ChangeFor(result, "Svc.cs").Replace("\r\n", ""), Does.Not.Contain("\n"));
    }

    // No mixed-EOL test for the constructor cascade: neither output path can keep a stray CRLF line yet (see the class
    // summary), so a mixed-EOL pin here would only be reachable after those two follow-up sites are routed.

    // ================================================================ MemberRefactoringEngine.RemoveMethodParameterAsync

    private static string[] RemoveParamInput() =>
    [
        "public class Worker",
        "{",
        "    // keep   this   comment",
        "    private   int _count;",
        "",
        "",
        "    public int Compute( int  a, int  b ) { return  a +  _count ; }",
        "",
        "    public int Run()",
        "    {",
        "        // run comment",
        "        return Compute( 1,   2 );",
        "    }",
        "",
        "    private int   Helper( ) { return  1; }",
        "}",
        "",
    ];

    private static string[] RemoveParamExpected() =>
    [
        "public class Worker",
        "{",
        "    // keep   this   comment",
        "    private   int _count;",
        "",
        "",
        "    public int Compute(int a) { return a + _count; }",
        "",
        "    public int Run()",
        "    {",
        "        // run comment",
        "        return Compute(1);",
        "    }",
        "",
        "    private int   Helper( ) { return  1; }",
        "}",
        "",
    ];

    [TestCase(false)]
    [TestCase(true)]
    public async Task RemoveMethodParameter_UntouchedCodeKeepsIrregularSpacing_ByteExact(bool crlf)
    {
        SetFiles(("Test.cs", Src(crlf, RemoveParamInput())));

        var result = await _memberEngine.RemoveMethodParameterAsync("Test.cs", "Compute", "b");

        Assert.That(result.Outcome, Is.EqualTo(EditOutcome.Modified));
        Assert.That(result.UpdatedText, Is.EqualTo(Src(crlf, RemoveParamExpected())));
    }

    [Test]
    public async Task RemoveMethodParameter_LfDocument_ResultHasNoCarriageReturn()
    {
        SetFiles(("Test.cs", Src(false, RemoveParamInput())));

        var result = await _memberEngine.RemoveMethodParameterAsync("Test.cs", "Compute", "b");

        Assert.That(result.UpdatedText, Does.Not.Contain("\r"));
    }

    [Test]
    public async Task RemoveMethodParameter_CrlfDocument_ResultHasNoBareLineFeed()
    {
        SetFiles(("Test.cs", Src(true, RemoveParamInput())));

        var result = await _memberEngine.RemoveMethodParameterAsync("Test.cs", "Compute", "b");

        Assert.That(result.UpdatedText!.Replace("\r\n", ""), Does.Not.Contain("\n"));
    }

    // Stray CRLF on: the field line right before the rewritten method (formatter-owned line break), a line in the
    // call-site method, and an untouched member far from any edit.
    [TestCase("    private   int _count;\n")]
    [TestCase("        // run comment\n")]
    [TestCase("    private int   Helper( ) { return  1; }\n")]
    public async Task RemoveMethodParameter_MixedEol_StrayCrlfLineKeepsItsTerminator(string anchor)
    {
        SetFiles(("Test.cs", WithStrayCrlf(Src(false, RemoveParamInput()), anchor)));

        var result = await _memberEngine.RemoveMethodParameterAsync("Test.cs", "Compute", "b");

        Assert.That(result.UpdatedText, Is.EqualTo(WithStrayCrlf(Src(false, RemoveParamExpected()), anchor)));
    }

    // The mirror shape: a CRLF document with one bare-LF terminator. The LF sits on an UNCHANGED line (the blank line
    // right after the rewritten method, or a line far from any edit), so it must survive byte-for-byte while the
    // rewritten line takes the dominant CRLF. This is the shape MethodSignature(remove) produces from a fixture that
    // splices "\n\n" into a CRLF file; the result is legitimately mixed, with the same dominant EOL as before.
    [TestCase("    public int Compute( int  a, int  b ) { return  a +  _count ; }\r\n\r\n", "    public int Compute(int a) { return a + _count; }\r\n\r\n")]
    [TestCase("    private int   Helper( ) { return  1; }\r\n", "    private int   Helper( ) { return  1; }\r\n")]
    public async Task RemoveMethodParameter_CrlfDocument_StrayBareLfOnUnchangedLineIsKept(string inputAnchor, string expectedAnchor)
    {
        var input = Src(true, RemoveParamInput());
        var expected = Src(true, RemoveParamExpected());
        Assert.That(input.Split(inputAnchor).Length, Is.EqualTo(2), "Precondition: the input anchor is unique.");
        Assert.That(expected.Split(expectedAnchor).Length, Is.EqualTo(2), "Precondition: the expected anchor is unique.");

        // The stray LF replaces the LAST CRLF of the anchor, so for the first case it is the blank line's terminator.
        static string WithBareLfAtEnd(string anchor) => anchor.Substring(0, anchor.Length - 2) + "\n";
        SetFiles(("Test.cs", input.Replace(inputAnchor, WithBareLfAtEnd(inputAnchor))));

        var result = await _memberEngine.RemoveMethodParameterAsync("Test.cs", "Compute", "b");

        Assert.That(result.Outcome, Is.EqualTo(EditOutcome.Modified));
        Assert.That(result.UpdatedText, Is.EqualTo(expected.Replace(expectedAnchor, WithBareLfAtEnd(expectedAnchor))));
    }

    // ================================================================ SemanticRefactoringEngine.InlineVariableAsync (usage replacement)

    private static string[] InlineInput() =>
    [
        "public class C",
        "{",
        "    // keep   this   comment",
        "    private   int _count;",
        "",
        "",
        "    int M()",
        "    {",
        "        var total = Calc(() => { return 1; });",
        "",
        "        // before use",
        "        return",
        "",
        "",
        "                    total * _count;",
        "    }",
        "",
        "    private int   Helper( ) { return  1; }",
        "}",
        "",
    ];

    private static string[] InlineExpected() =>
    [
        "public class C",
        "{",
        "    // keep   this   comment",
        "    private   int _count;",
        "",
        "",
        "    int M()",
        "    {",
        "",
        "        // before use",
        "        return",
        "",
        "",
        "                    (Calc(() => { return 1; })) * _count;",
        "    }",
        "",
        "    private int   Helper( ) { return  1; }",
        "}",
        "",
    ];

    [TestCase(false)]
    [TestCase(true)]
    public async Task InlineVariable_UsageReplacement_UntouchedCodeKeepsIrregularSpacing_ByteExact(bool crlf)
    {
        SetFiles(("Test.cs", Src(crlf, InlineInput())));

        var result = await _semanticEngine.InlineVariableAsync("Test.cs", "total");

        Assert.That(result, Is.EqualTo(Src(crlf, InlineExpected())));
    }

    [Test]
    public async Task InlineVariable_UsageReplacement_LfDocument_ResultHasNoCarriageReturn()
    {
        SetFiles(("Test.cs", Src(false, InlineInput())));

        var result = await _semanticEngine.InlineVariableAsync("Test.cs", "total");

        Assert.That(result, Does.Not.Contain("\r"));
    }

    [Test]
    public async Task InlineVariable_UsageReplacement_CrlfDocument_ResultHasNoBareLineFeed()
    {
        SetFiles(("Test.cs", Src(true, InlineInput())));

        var result = await _semanticEngine.InlineVariableAsync("Test.cs", "total");

        Assert.That(result.Replace("\r\n", ""), Does.Not.Contain("\n"));
    }

    // Stray CRLF on: the field line before the method, the `return` line whose break precedes the replaced usage
    // (formatter-owned), a comment line, and an untouched member.
    [TestCase("    private   int _count;\n")]
    [TestCase("        return\n")]
    [TestCase("        // before use\n")]
    [TestCase("    private int   Helper( ) { return  1; }\n")]
    public async Task InlineVariable_UsageReplacement_MixedEol_StrayCrlfLineKeepsItsTerminator(string anchor)
    {
        SetFiles(("Test.cs", WithStrayCrlf(Src(false, InlineInput()), anchor)));

        var result = await _semanticEngine.InlineVariableAsync("Test.cs", "total");

        Assert.That(result, Is.EqualTo(WithStrayCrlf(Src(false, InlineExpected()), anchor)));
    }
}
