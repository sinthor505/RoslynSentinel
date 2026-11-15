using Microsoft.CodeAnalysis.CSharp;
using RoslynSentinel.Common.SnippetMatching;

namespace RoslynSentinel.Tests;

/// <summary>
/// Tests for <see cref="SnippetTokenEquivalence"/>: the token comparison and tree alignment used by ReplaceSnippet's
/// token-equivalence second chance (plan_replacesnippet_token_equivalent_match_and_reindent).
/// </summary>
[TestFixture]
[Parallelizable(ParallelScope.All)]
public class SnippetTokenEquivalenceTests
{
    private static readonly CSharpParseOptions Options = new(LanguageVersion.Latest);

    [Test]
    public void AreTokenEquivalent_TabsVersusSpacesIndentation_ReturnsTrue()
    {
        const string snippet = "if (x)\n{\n\tFoo();\n}";
        const string candidate = "if (x)\n{\n        Foo();\n}";

        Assert.That(SnippetTokenEquivalence.AreTokenEquivalent(snippet, candidate, Options), Is.True);
    }

    [Test]
    public void AreTokenEquivalent_DifferenceInsideVerbatimString_ReturnsFalse()
    {
        const string snippet = "var s = @\"a\n  b\";";
        const string candidate = "var s = @\"a\n      b\";";

        Assert.That(SnippetTokenEquivalence.AreTokenEquivalent(snippet, candidate, Options), Is.False);
    }

    [Test]
    public void AreTokenEquivalent_DifferenceInsideRawString_ReturnsFalse()
    {
        const string snippet = "var s = \"\"\"\n    a\n    b\n    \"\"\";";
        const string candidate = "var s = \"\"\"\n    a\n        b\n    \"\"\";";

        Assert.That(SnippetTokenEquivalence.AreTokenEquivalent(snippet, candidate, Options), Is.False);
    }

    [Test]
    public void AreTokenEquivalent_CommentTextDiffers_ReturnsFalse()
    {
        const string snippet = "// first\nx = 1;";
        const string candidate = "// second\nx = 1;";

        Assert.That(SnippetTokenEquivalence.AreTokenEquivalent(snippet, candidate, Options), Is.False);
    }

    [Test]
    public void AreTokenEquivalent_CommentIndentationDiffers_ReturnsTrue()
    {
        const string snippet = "/* a\n   b */\nx = 1;";
        const string candidate = "/* a\n\t\t\tb */\nx = 1;";

        Assert.That(SnippetTokenEquivalence.AreTokenEquivalent(snippet, candidate, Options), Is.True);
    }

    [Test]
    public void AreTokenEquivalent_RegionDirectiveIndentationDiffers_ReturnsTrue()
    {
        const string snippet = "    #region Helpers\n    int x;\n    #endregion";
        const string candidate = "#region Helpers\nint x;\n#endregion";

        Assert.That(SnippetTokenEquivalence.AreTokenEquivalent(snippet, candidate, Options), Is.True);
    }

    [Test]
    public void AreTokenEquivalent_InterpolatedStringSameText_ReturnsTrue()
    {
        const string snippet = "    var s = $\"a {x + 1} b\";";
        const string candidate = "var s = $\"a {x + 1} b\";";

        Assert.That(SnippetTokenEquivalence.AreTokenEquivalent(snippet, candidate, Options), Is.True);
    }

    [Test]
    public void TokensAlignWithTree_CandidateStartsInsideStringLiteral_ReturnsFalse()
    {
        const string file = "class C { string s = \"hello world\"; }";
        var (root, options) = ParseFile(file);
        var start = file.IndexOf("world", StringComparison.Ordinal);

        Assert.That(SnippetTokenEquivalence.TokensAlignWithTree(root, start, "world", options), Is.False);
    }

    [Test]
    public void TokensAlignWithTree_CandidateStartsMidToken_ReturnsFalse()
    {
        const string file = "class C { int value = 1; }";
        var (root, options) = ParseFile(file);
        var start = file.IndexOf("value", StringComparison.Ordinal) + 1;

        Assert.That(SnippetTokenEquivalence.TokensAlignWithTree(root, start, "alue = 1;", options), Is.False);
    }

    [Test]
    public void TokensAlignWithTree_WholeStatements_ReturnsTrue()
    {
        // The second statement holds an interpolated string: one lexer token that spans several tree tokens.
        const string candidate = "var a = 1;\n        var b = $\"x{a}y\";";
        const string file = "class C\n{\n    void M()\n    {\n        " + candidate + "\n    }\n}";
        var (root, options) = ParseFile(file);
        var start = file.IndexOf("var a", StringComparison.Ordinal);

        Assert.That(SnippetTokenEquivalence.TokensAlignWithTree(root, start, candidate, options), Is.True);
    }

    private static (Microsoft.CodeAnalysis.SyntaxNode Root, CSharpParseOptions Options) ParseFile(string file)
    {
        var tree = CSharpSyntaxTree.ParseText(file, Options);
        return (tree.GetRoot(), (CSharpParseOptions)tree.Options);
    }
}
