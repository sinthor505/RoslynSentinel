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
/// C5c-2 routed the two remaining constructor-cascade output paths (the caller-file whole-file <c>NormalizeEol</c> in
/// <c>AddConstructorParameterWithCallSitesAsync</c> and <c>RoslynFormattingHelper.ReplaceNodeAndFormatReplacementAsync</c>)
/// onto the changed-lines EOL rule, and added the mixed-EOL cascade tests plus byte-level tests for
/// <c>AddTopLevelTypeAsync</c> (no-namespace path) and <c>MemberRefactoringEngine.ExtractMethodAsync</c>.
/// </para>
/// </summary>
[TestFixture]
[Category("MemberRefactoringEngine")] // sentinel:auto-category
public class RefactoringEngineFormatCharacterizationTests
{
    private PersistentWorkspaceManager _workspaceManager;
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

    // Mixed-EOL cascade tests live at the end of the file (C5c-2).

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
        "    public int Compute(int a) { return  a +  _count ; }",
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

        Assert.That(result.UpdatedText.Replace("\r\n", ""), Does.Not.Contain("\n"));
    }

    // Stray CRLF on: the field line right before the rewritten method (formatter-owned line break), a line in the
    // call-site method, and an untouched member far from any edit.
    [TestCase("    private   int _count;\n")]
    [TestCase("        // run comment\n")]
    [TestCase("    private int   Helper( ) { return  1; }\n")]
    public async Task RemoveMethodParameter_MixedEol_StrayCrlfLineKeepsItsTerminator(string anchor)
    {
        // Changed lines take the resolved EOL, not the majority one; Lf keeps this LF-majority mixed source's pinned expectation.
        _workspaceManager.EolModeOverride = EolMode.Lf;
        SetFiles(("Test.cs", WithStrayCrlf(Src(false, RemoveParamInput()), anchor)));

        var result = await _memberEngine.RemoveMethodParameterAsync("Test.cs", "Compute", "b");

        Assert.That(result.UpdatedText, Is.EqualTo(WithStrayCrlf(Src(false, RemoveParamExpected()), anchor)));
    }

    // The mirror shape: a CRLF document with one bare-LF terminator. The LF sits on an UNCHANGED line (the blank line
    // right after the rewritten method, or a line far from any edit), so it must survive byte-for-byte while the
    // rewritten line takes the dominant CRLF. This is the shape MethodSignature(remove) produces from a fixture that
    // splices "\n\n" into a CRLF file; the result is legitimately mixed, with the same dominant EOL as before.
    [TestCase("    public int Compute( int  a, int  b ) { return  a +  _count ; }\r\n\r\n", "    public int Compute(int a) { return  a +  _count ; }\r\n\r\n")]
    [TestCase("    private int   Helper( ) { return  1; }\r\n", "    private int   Helper( ) { return  1; }\r\n")]
    public async Task RemoveMethodParameter_CrlfDocument_StrayBareLfOnUnchangedLineIsKept(string inputAnchor, string expectedAnchor)
    {
        // Changed lines take the resolved EOL, not the majority one; Crlf keeps this CRLF-majority mixed source's pinned expectation (Auto would follow the platform).
        _workspaceManager.EolModeOverride = EolMode.Crlf;
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
        // Changed lines take the resolved EOL, not the majority one; Lf keeps this LF-majority mixed source's pinned expectation.
        _workspaceManager.EolModeOverride = EolMode.Lf;
        SetFiles(("Test.cs", WithStrayCrlf(Src(false, InlineInput()), anchor)));

        var result = await _semanticEngine.InlineVariableAsync("Test.cs", "total");

        Assert.That(result, Is.EqualTo(WithStrayCrlf(Src(false, InlineExpected()), anchor)));
    }

    // ================================================================ C5c-2: constructor cascade mixed EOL

    // Stray CRLF on an unchanged line of the caller file (the cascade used to flatten it with a whole-file NormalizeEol).
    [TestCase("    // keep   this   comment\n")]
    [TestCase("    private int   Helper( ) { return  1; }\n")]
    public async Task ConstructorCascade_CallerInOtherFile_MixedEol_StrayCrlfLineKeepsItsTerminator(string anchor)
    {
        SetFiles(("Svc.cs", Src(false, SvcSource.Split('\n'))), ("Caller.cs", WithStrayCrlf(Src(false, CallerInput()), anchor)));

        // Changed lines take the resolved EOL, not the majority one; Lf keeps this LF-majority mixed source's pinned expectation.
        _workspaceManager.EolModeOverride = EolMode.Lf;
        var result = await CascadeAsync("Caller.cs:*");

        Assert.That(ChangeFor(result, "Caller.cs"), Is.EqualTo(WithStrayCrlf(Src(false, CallerExpected()), anchor)));
    }

    // Same, for the class's own file (the class edit goes through ReplaceNodeAndFormatReplacementAsync). The anchors sit
    // outside the edited class (Config) and in the call-site type (Maker).
    [TestCase("public class Config { }\n")]
    [TestCase("    // keep   this   comment\n")]
    [TestCase("    private int   Helper( ) { return  1; }\n")]
    public async Task ConstructorCascade_CallerInClassFile_MixedEol_StrayCrlfLineKeepsItsTerminator(string anchor)
    {
        SetFiles(("Svc.cs", WithStrayCrlf(Src(false, SameFileInput()), anchor)));

        // Changed lines take the resolved EOL, not the majority one; Lf keeps this LF-majority mixed source's pinned expectation.
        _workspaceManager.EolModeOverride = EolMode.Lf;
        var result = await CascadeAsync("Svc.cs:*");

        Assert.That(ChangeFor(result, "Svc.cs"), Is.EqualTo(WithStrayCrlf(Src(false, SameFileExpected()), anchor)));
    }

    // The class edit alone (no call sites): a stray CRLF outside the edited class must survive.
    [TestCase("public class Config { }\n")]
    [TestCase("public class Other   { int   z; }\n")]
    public async Task AddConstructorParameter_MixedEol_StrayCrlfOutsideEditedClassKeepsItsTerminator(string anchor)
    {
        const string source = "namespace TestProj;\npublic class Config { }\npublic class Other   { int   z; }\npublic class Svc\n{\n    public Svc(int a) { }\n}\n";
        // Changed lines take the resolved EOL, not the majority one; Lf keeps this LF-majority mixed source's pinned expectation.
        _workspaceManager.EolModeOverride = EolMode.Lf;
        SetFiles(("Svc.cs", WithStrayCrlf(source, anchor)));

        var result = await _memberEngine.AddConstructorParameterAsync("Svc.cs", "Svc", "config", "Config");

        Assert.That(result.Outcome, Is.EqualTo(EditOutcome.Modified), result.Message);
        Assert.That(result.UpdatedText, Does.Contain(anchor.Substring(0, anchor.Length - 1) + "\r\n"));
        Assert.That(result.UpdatedText.Count(c => c == '\r'), Is.EqualTo(1), "Only the stray CRLF may carry a carriage return.");
    }

    // ================================================================ AddTopLevelTypeAsync (no-namespace path)

    private static string[] TopLevelInput() =>
    [
        "using System;",
        "",
        "public class A",
        "{",
        "    public   int  X;",
        "}",
        "",
    ];

    private async Task<string> AddTopLevelAsync(string text)
    {
        SetFiles(("Test.cs", text));
        var result = await _memberEngine.AddTopLevelTypeAsync("Test.cs", "public class B\n{\n    public int Y;\n}");
        Assert.That(result.Outcome, Is.EqualTo(EditOutcome.Modified), result.Message);
        return result.UpdatedText;
    }

    [Test]
    public async Task AddTopLevelType_NoNamespace_LfDocument_KeepsUntouchedSpacingAndHasNoCarriageReturn()
    {
        var text = await AddTopLevelAsync(Src(false, TopLevelInput()));

        Assert.That(text, Does.Not.Contain("\r"));
        Assert.That(text, Does.StartWith(Src(false, TopLevelInput()).TrimEnd('\n')));
        Assert.That(text, Does.Contain("public class B\n{\n    public int Y;\n}"));
    }

    [Test]
    public async Task AddTopLevelType_NoNamespace_CrlfDocument_IsLfResultInCrlf_ByteExact()
    {
        var lf = await AddTopLevelAsync(Src(false, TopLevelInput()));

        var crlf = await AddTopLevelAsync(Src(true, TopLevelInput()));

        Assert.That(crlf, Is.EqualTo(lf.Replace("\n", "\r\n")));
    }

    [TestCase("using System;\n")]
    [TestCase("    public   int  X;\n")]
    public async Task AddTopLevelType_NoNamespace_MixedEol_StrayCrlfLineKeepsItsTerminator(string anchor)
    {
        // Changed lines take the resolved EOL, not the majority one; Lf keeps this LF-majority mixed source's pinned expectation.
        _workspaceManager.EolModeOverride = EolMode.Lf;
        var lf = await AddTopLevelAsync(Src(false, TopLevelInput()));

        var mixed = await AddTopLevelAsync(WithStrayCrlf(Src(false, TopLevelInput()), anchor));

        Assert.That(mixed, Is.EqualTo(WithStrayCrlf(lf, anchor)));
    }

    // ================================================================ MemberRefactoringEngine.ExtractMethodAsync

    private static string[] ExtractInput() =>
    [
        "namespace TestProj;",
        "public class Calc",
        "{",
        "    private   int _n;",
        "",
        "    public int Run(int a)",
        "    {",
        "        var x = a + 1;",
        "        var y = x * 2;",
        "        return y;",
        "    }",
        "",
        "    private int   Helper( ) { return  1; }",
        "}",
        "",
    ];

    private async Task<string> ExtractAsync(string text)
    {
        SetFiles(("Test.cs", text));
        var result = await _memberEngine.ExtractMethodAsync("Test.cs", 9, "var y = x * 2;", 9, "var y = x * 2;", "Doubled");
        Assert.That(result.IsError, Is.False, result.ErrorMessage);
        return result.UpdatedSourceContent!;
    }

    [Test]
    public async Task ExtractMethod_LfDocument_UntouchedCodeKeepsIrregularSpacing_NoCarriageReturn()
    {
        var text = await ExtractAsync(Src(false, ExtractInput()));

        Assert.That(text, Does.Not.Contain("\r"));
        Assert.That(text, Does.Contain("    private   int _n;\n"));
        Assert.That(text, Does.Contain("    private int   Helper( ) { return  1; }\n"));
        Assert.That(text, Does.Contain("var y = Doubled(x);"));
        Assert.That(text, Does.Contain("private int Doubled(int x)\n    {\n        var y = x * 2;\n        return y;\n    }"));
    }

    [Test]
    public async Task ExtractMethod_CrlfDocument_IsLfResultInCrlf_ByteExact()
    {
        var lf = await ExtractAsync(Src(false, ExtractInput()));

        var crlf = await ExtractAsync(Src(true, ExtractInput()));

        Assert.That(crlf, Is.EqualTo(lf.Replace("\n", "\r\n")));
    }

    [TestCase("    private   int _n;\n")]
    [TestCase("        var x = a + 1;\n")]
    [TestCase("    private int   Helper( ) { return  1; }\n")]
    public async Task ExtractMethod_MixedEol_StrayCrlfLineKeepsItsTerminator(string anchor)
    {
        // Changed lines take the resolved EOL, not the majority one; Lf keeps this LF-majority mixed source's pinned expectation.
        _workspaceManager.EolModeOverride = EolMode.Lf;
        var lf = await ExtractAsync(Src(false, ExtractInput()));

        var mixed = await ExtractAsync(WithStrayCrlf(Src(false, ExtractInput()), anchor));

        Assert.That(mixed, Is.EqualTo(WithStrayCrlf(lf, anchor)));
    }
}
