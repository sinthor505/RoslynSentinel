using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Formatting;
using RoslynSentinel.Common;

namespace RoslynSentinel.Tests.Basic;

/// <summary>
/// Slice C5a of docs/current/proposals/proposal_enforce_normalize_whole_subtree_whitespace_rule.md:
/// <see cref="RoslynFormattingHelper.FormatAnnotatedSpansAsync"/> (annotated-container sites) and
/// <see cref="RoslynFormattingHelper.FormatWholeDocumentAsync"/> (the one named whole-document chokepoint).
/// Both must leave untouched code byte-identical and keep the document's own line-ending style.
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

    private Document MakeDocument(string content)
    {
        _workspace?.Dispose();
        _workspace = new AdhocWorkspace();
        var projectId = ProjectId.CreateNewId();
        var documentId = DocumentId.CreateNewId(projectId);
        var solution = _workspace.CurrentSolution
            .AddProject(projectId, "Test", "Test", LanguageNames.CSharp)
            .AddMetadataReference(projectId, MetadataReference.CreateFromFile(typeof(object).Assembly.Location))
            .AddDocument(documentId, "Test.cs", content);
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
    public async Task FormatAnnotatedSpans_UntouchedCodeStaysByteIdentical_OnlyAnnotatedNodeIsFormatted()
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

        Assert.That(result, Does.Contain("    private   int _a=1;\n"));
        Assert.That(result, Does.Contain("    public void Other(  ) { var   q=2; }\n"));
        Assert.That(result, Does.Not.Contain("Target(  )"), "The annotated method is formatted.");
        Assert.That(result, Does.Contain("var z = 3;"));
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
        Assert.That(result, Does.Contain("var   b=2;"), "The un-annotated method must stay byte-identical.");
    }

    [Test]
    public async Task FormatAnnotatedSpans_LfDocumentWithSynthesizedBlock_EndsWithNoCarriageReturn()
    {
        var document = MakeDocument(TwoMethodSource(false));
        var root = (await document.GetSyntaxRootAsync())!;
        var rewritten = AddAnnotatedMethod(root);

        // Precondition: the raw formatter leaks its own NewLine option (CRLF on a Windows host) into the LF document.
        var raw = await TextOf(await Formatter.FormatAsync(document.WithSyntaxRoot(rewritten), Formatter.Annotation));
        Assume.That(raw, Does.Contain("\r"), "Raw formatter output has no CR on this host, so there is no leak to correct.");

        var result = await TextOf(await RoslynFormattingHelper.FormatAnnotatedSpansAsync(document, rewritten));

        Assert.That(result, Does.Not.Contain("\r"));
        Assert.That(result, Does.Contain("public void Added()"));
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
    public async Task FormatAnnotatedSpans_NoAnnotation_ReturnsRewrittenTextUnformatted_InDominantEol(bool crlf)
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
        var document = MakeDocument(TwoMethodSource(false));

        var result = await TextOf(await RoslynFormattingHelper.FormatWholeDocumentAsync(document));

        Assert.That(result, Does.Not.Contain("\r"));
        Assert.That(result, Does.Contain("var a = 1;"));
    }

    [Test]
    public async Task FormatWholeDocument_CrlfStaysCrlf()
    {
        var document = MakeDocument(TwoMethodSource(true));

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
