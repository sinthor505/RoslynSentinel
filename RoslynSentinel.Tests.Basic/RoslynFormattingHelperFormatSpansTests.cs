using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.Text;
using RoslynSentinel.Common;

namespace RoslynSentinel.Tests.Basic;

/// <summary>
/// Slice C5a of docs/current/proposals/proposal_enforce_normalize_whole_subtree_whitespace_rule.md:
/// <see cref="RoslynFormattingHelper.FormatAnnotatedSpansAsync"/> (annotated-container sites) and
/// <see cref="RoslynFormattingHelper.FormatWholeDocumentAsync"/> (the one named whole-document chokepoint).
/// Both must leave code the edit did not touch alone (apart from the whitespace and line break the formatter owns
/// directly in front of an annotated node) and keep the document's own line-ending style.
/// </summary>
public class RoslynFormattingHelperFormatSpansTests
{
    private AdhocWorkspace _workspace = null!;

    [TearDown]
    public void TearDown() => _workspace?.Dispose();

    private static string Src(bool crlf, params string[] lines)
    {
        var text = string.Join("\n", lines) + "\n";
        return crlf ? text.Replace("\n", "\r\n") : text;
    }

    // formatterNewLine: when set ("crlf" or "lf"), a global analyzer config pins the formatter's own NewLine option
    // (end_of_line) so a test does not depend on the host's default line ending.
    private Document MakeDocument(string content, string? formatterNewLine = null)
    {
        _workspace?.Dispose();
        _workspace = new AdhocWorkspace();
        var projectId = ProjectId.CreateNewId();
        var documentId = DocumentId.CreateNewId(projectId);
        var solution = _workspace.CurrentSolution
            .AddProject(projectId, "Test", "Test", LanguageNames.CSharp)
            .AddMetadataReference(projectId, MetadataReference.CreateFromFile(typeof(object).Assembly.Location));
        if (formatterNewLine is null)
        {
            solution = solution.AddDocument(documentId, "Test.cs", content);
        }
        else
        {
            solution = solution
                .AddDocument(documentId, "Test.cs", content, filePath: "/Test.cs")
                .AddAnalyzerConfigDocument(
                    DocumentId.CreateNewId(projectId),
                    ".globalconfig",
                    SourceText.From($"is_global = true\nend_of_line = {formatterNewLine}\n"),
                    filePath: "/.globalconfig");
        }

        return solution.GetDocument(documentId)!;
    }

    private static async Task<string> TextOf(Document document) => (await document.GetTextAsync()).ToString();

    private static MethodDeclarationSyntax Method(SyntaxNode root, string name)
        => root.DescendantNodes().OfType<MethodDeclarationSyntax>().First(m => m.Identifier.ValueText == name);

    private static string TwoMethodSource(bool crlf) => Src(crlf,
        "public class C",
        "{",
        "    public void First()",
        "    {",
        "        var   a=1;",
        "    }",
        "",
        "    public void Second()",
        "    {",
        "        var   b=2;",
        "    }",
        "}");

    // The opening braces sit on the signature lines, so a format must insert line breaks (and so must pick an ending).
    private static string BraceOnSignatureLineSource(bool crlf) => Src(crlf,
        "public class C",
        "{",
        "    public void First() {",
        "        var   a=1;",
        "    }",
        "}");

    private static MethodDeclarationSyntax NewSynthesizedMethod()
        => SyntaxFactory.MethodDeclaration(SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.VoidKeyword)), "Added")
            .WithModifiers(SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.PublicKeyword)))
            .WithBody(SyntaxFactory.Block(SyntaxFactory.ExpressionStatement(SyntaxFactory.InvocationExpression(SyntaxFactory.IdentifierName("Go")))));

    private static SyntaxNode AddAnnotatedMethod(SyntaxNode root)
    {
        var cls = root.DescendantNodes().OfType<ClassDeclarationSyntax>().First();
        return root.ReplaceNode(cls, cls.AddMembers(NewSynthesizedMethod().WithAdditionalAnnotations(Formatter.Annotation)));
    }

    // ---- FormatAnnotatedSpansAsync ----

    [Test]
    public async Task FormatAnnotatedSpans_UntouchedCodeIsLeftAlone_OnlyAnnotatedNodeIsFormatted()
    {
        var source = Src(false,
            "public class C",
            "{",
            "    private   int _a=1;",
            "    public void Other(  ) { var   q=2; }",
            "    public void Target(  ) { var   z=3; }",
            "}");
        var document = MakeDocument(source);
        var root = (await document.GetSyntaxRootAsync())!;
        var target = Method(root, "Target");
        var rewritten = root.ReplaceNode(target, target.WithAdditionalAnnotations(Formatter.Annotation));

        var result = await TextOf(await RoslynFormattingHelper.FormatAnnotatedSpansAsync(document, rewritten));

        var expected = Src(false,
            "public class C",
            "{",
            "    private   int _a=1;",
            "    public void Other(  ) { var   q=2; }",
            "    public void Target() { var z = 3; }",
            "}");
        Assert.That(result, Is.EqualTo(expected));
    }

    [Test]
    public async Task FormatAnnotatedSpans_AnnotatedNodeAfterBlankLineAtWrongIndent_FormatterOwnsTheWhitespaceInFrontOfIt()
    {
        var source = Src(false,
            "public class C",
            "{",
            "    private   int _a=1;",
            "",
            "          public void Target(  ) { var   z=3; }",
            "}");
        var document = MakeDocument(source);
        var root = (await document.GetSyntaxRootAsync())!;
        var target = Method(root, "Target");
        var rewritten = root.ReplaceNode(target, target.WithAdditionalAnnotations(Formatter.Annotation));

        var result = await TextOf(await RoslynFormattingHelper.FormatAnnotatedSpansAsync(document, rewritten));

        // Pins the boundary: the un-annotated field line is untouched, while the annotated method's indentation
        // (the whitespace directly in front of it) is re-spaced and the blank line before it is kept.
        var expected = Src(false,
            "public class C",
            "{",
            "    private   int _a=1;",
            "",
            "    public void Target() { var z = 3; }",
            "}");
        Assert.That(result, Is.EqualTo(expected));
    }

    [Test]
    public async Task FormatAnnotatedSpans_FormatsOnlyTheAnnotatedNode()
    {
        var document = MakeDocument(TwoMethodSource(false));
        var root = (await document.GetSyntaxRootAsync())!;
        var first = Method(root, "First");
        var rewritten = root.ReplaceNode(first, first.WithAdditionalAnnotations(Formatter.Annotation));

        var result = await TextOf(await RoslynFormattingHelper.FormatAnnotatedSpansAsync(document, rewritten));

        Assert.That(result, Does.Contain("var a = 1;"));
        Assert.That(result, Does.Contain("var   b=2;"), "The un-annotated method must stay unformatted.");
    }

    [Test]
    public async Task FormatAnnotatedSpans_LfDocumentWithSynthesizedBlock_EndsWithNoCarriageReturn()
    {
        // The formatter's NewLine option is pinned to CRLF through a global analyzer config, so the leak scenario
        // is exercised on every host, not only a Windows one.
        var document = MakeDocument(TwoMethodSource(false), formatterNewLine: "crlf");
        var root = (await document.GetSyntaxRootAsync())!;
        var rewritten = AddAnnotatedMethod(root);

        // Precondition: the raw formatter leaks its own NewLine option (CRLF) into the LF document.
        var raw = await TextOf(await Formatter.FormatAsync(document.WithSyntaxRoot(rewritten), Formatter.Annotation));
        Assert.That(raw, Does.Contain("\r"), "Raw formatter output has no CR, so the test would not exercise the leak (is the pinned NewLine option being honoured?).");

        var result = await TextOf(await RoslynFormattingHelper.FormatAnnotatedSpansAsync(document, rewritten));

        Assert.That(result, Does.Not.Contain("\r"));
        Assert.That(result, Does.Contain("public void Added()"));
    }

    [TestCase("lf", false)]
    [TestCase("crlf", true)]
    public async Task MakeDocument_FormatterNewLinePin_IsHonouredByTheRawFormatter(string newLine, bool expectCarriageReturn)
    {
        // Guards the pin used by the leak test above: if a Roslyn upgrade stopped honouring the analyzer config,
        // the pinned-CRLF scenario would silently fall back to the host default.
        var document = MakeDocument(TwoMethodSource(false), formatterNewLine: newLine);
        var root = (await document.GetSyntaxRootAsync())!;

        var raw = await TextOf(await Formatter.FormatAsync(document.WithSyntaxRoot(AddAnnotatedMethod(root)), Formatter.Annotation));

        Assert.That(raw.Contains('\r'), Is.EqualTo(expectCarriageReturn));
    }

    [Test]
    public async Task FormatAnnotatedSpans_CrlfDocumentWithLfBlock_EndsWithNoBareLineFeed()
    {
        var document = MakeDocument(TwoMethodSource(true));
        var root = (await document.GetSyntaxRootAsync())!;
        var cls = root.DescendantNodes().OfType<ClassDeclarationSyntax>().First();
        var lfMember = SyntaxFactory.ParseMemberDeclaration("public void Added()\n{\n    Go();\n}\n")!
            .WithAdditionalAnnotations(Formatter.Annotation);
        var rewritten = root.ReplaceNode(cls, cls.AddMembers(lfMember));

        var result = await TextOf(await RoslynFormattingHelper.FormatAnnotatedSpansAsync(document, rewritten));

        Assert.That(result.Replace("\r\n", ""), Does.Not.Contain("\n"));
        Assert.That(result, Does.Contain("public void Added()"));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task FormatAnnotatedSpans_NoAnnotation_ReturnsRewrittenTextUnchanged(bool crlf)
    {
        var source = TwoMethodSource(crlf);
        var document = MakeDocument(source);
        var root = (await document.GetSyntaxRootAsync())!;
        var rewritten = root.ReplaceNode(Method(root, "First"), Method(root, "First").WithIdentifier(SyntaxFactory.Identifier("Renamed")));

        var result = await TextOf(await RoslynFormattingHelper.FormatAnnotatedSpansAsync(document, rewritten));

        Assert.That(result, Is.EqualTo(source.Replace("First", "Renamed")));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task FormatAnnotatedSpans_UnchangedRootWithoutAnnotation_IsByteIdentical(bool crlf)
    {
        var source = TwoMethodSource(crlf);
        var document = MakeDocument(source);
        var root = (await document.GetSyntaxRootAsync())!;

        var result = await TextOf(await RoslynFormattingHelper.FormatAnnotatedSpansAsync(document, root));

        Assert.That(result, Is.EqualTo(source));
    }

    [Test]
    public async Task FormatAnnotatedSpans_MixedEol_StrayTerminatorOutsideTheEditSurvives()
    {
        var source = TwoMethodSource(false).Replace("public class C\n", "public class C\r\n");
        var document = MakeDocument(source);
        var root = (await document.GetSyntaxRootAsync())!;
        var rewritten = AddAnnotatedMethod(root);

        var result = await TextOf(await RoslynFormattingHelper.FormatAnnotatedSpansAsync(document, rewritten));

        Assert.That(result, Does.StartWith("public class C\r\n{\n"));
        Assert.That(result.Count(c => c == '\r'), Is.EqualTo(1), "Only the pre-existing stray CRLF may remain.");
    }

    [Test]
    public async Task FormatAnnotatedSpans_NullArguments_Throw()
    {
        var document = MakeDocument(TwoMethodSource(false));
        var root = (await document.GetSyntaxRootAsync())!;

        await Assert.ThatAsync(async () => await RoslynFormattingHelper.FormatAnnotatedSpansAsync(null!, root), Throws.TypeOf<ArgumentNullException>());
        await Assert.ThatAsync(async () => await RoslynFormattingHelper.FormatAnnotatedSpansAsync(document, null!), Throws.TypeOf<ArgumentNullException>());
    }

    // ---- FormatWholeDocumentAsync ----

    [Test]
    public async Task FormatWholeDocument_FormatsEverything()
    {
        var document = MakeDocument(Src(false,
            "public class C",
            "{",
            "    public void First( )",
            "    {",
            "        int   a=1;",
            "    }",
            "",
            "    public void Second( )",
            "    {",
            "        int   b=2;",
            "    }",
            "}"));

        var result = await TextOf(await RoslynFormattingHelper.FormatWholeDocumentAsync(document));

        Assert.That(result, Does.Contain("First()"));
        Assert.That(result, Does.Contain("Second()"));
        Assert.That(result, Does.Contain("int a = 1;"));
        Assert.That(result, Does.Contain("int b = 2;"));
    }

    [Test]
    public async Task FormatWholeDocument_LfStaysLf()
    {
        // The opening brace sits on the signature line, so the formatter inserts a line break; its NewLine option
        // is pinned to the OPPOSITE ending (CRLF) so the leak is possible, and the test can fail, on any host.
        var document = MakeDocument(BraceOnSignatureLineSource(false), formatterNewLine: "crlf");

        var raw = await TextOf(await Formatter.FormatAsync(document));
        Assert.That(raw, Does.Contain("\r"), "Raw formatter output has no CR, so the test would not exercise the leak.");

        var result = await TextOf(await RoslynFormattingHelper.FormatWholeDocumentAsync(document));

        Assert.That(result, Does.Not.Contain("\r"));
        Assert.That(result, Does.Contain("var a = 1;"));
    }

    [Test]
    public async Task FormatWholeDocument_LfDocumentWithSynthesizedCrlfBlock_EolReferenceKeepsLf()
    {
        var original = TwoMethodSource(false);
        var document = MakeDocument(original, formatterNewLine: "crlf");
        var originalText = await document.GetTextAsync();
        var root = (await document.GetSyntaxRootAsync())!;
        var cls = root.DescendantNodes().OfType<ClassDeclarationSyntax>().First();

        // A synthesized member of many CRLF lines: enough that the edited text is CRLF-dominant, which is the case
        // that flips the dominant ending when it is detected from the edited text.
        var body = string.Concat(Enumerable.Range(0, 30).Select(i => $"    Go{i}();\r\n"));
        var crlfMember = SyntaxFactory.ParseMemberDeclaration("public void Added()\r\n{\r\n" + body + "}\r\n")!;
        var edited = document.WithSyntaxRoot(root.ReplaceNode(cls, cls.AddMembers(crlfMember)));

        // Precondition: without a reference the edited text decides, so the synthesized CRLF block wins.
        var withoutReference = await TextOf(await RoslynFormattingHelper.FormatWholeDocumentAsync(edited));
        Assert.That(withoutReference, Does.Contain("Go29();\r\n"), "Without eolReference the edited text is CRLF-dominant and keeps the synthesized CRLF.");

        var result = await TextOf(await RoslynFormattingHelper.FormatWholeDocumentAsync(edited, originalText));

        Assert.That(result, Does.Not.Contain("\r"));
        Assert.That(result, Does.Contain("public void Added()"));
        Assert.That(result, Does.Contain("    Go29();\n"));
    }

    [Test]
    public async Task FormatWholeDocument_CrlfStaysCrlf()
    {
        // Formatter NewLine pinned to LF, the opposite of the document, so a bare LF can leak on any host.
        var document = MakeDocument(BraceOnSignatureLineSource(true), formatterNewLine: "lf");

        var raw = await TextOf(await Formatter.FormatAsync(document));
        Assert.That(raw.Replace("\r\n", ""), Does.Contain("\n"), "Raw formatter output has no bare LF, so the test would not exercise the leak.");

        var result = await TextOf(await RoslynFormattingHelper.FormatWholeDocumentAsync(document));

        Assert.That(result.Replace("\r\n", ""), Does.Not.Contain("\n"));
        Assert.That(result, Does.Contain("var a = 1;"));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task FormatWholeDocument_AlreadyFormattedDocument_IsByteIdentical(bool crlf)
    {
        var source = Src(crlf,
            "public class C",
            "{",
            "    public void First()",
            "    {",
            "        var a = 1;",
            "    }",
            "}");
        var document = MakeDocument(source);

        var result = await TextOf(await RoslynFormattingHelper.FormatWholeDocumentAsync(document));

        Assert.That(result, Is.EqualTo(source));
    }

    [Test]
    public async Task FormatWholeDocument_NullDocument_Throws()
    {
        await Assert.ThatAsync(async () => await RoslynFormattingHelper.FormatWholeDocumentAsync(null!), Throws.TypeOf<ArgumentNullException>());
    }
}
