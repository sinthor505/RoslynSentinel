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

    private const string BlockFile = "class C\n{\n    void M()\n    {\n        if (x)\n        {\n            Foo();\n        }\n    }\n}\n";

    private const string BlockText = "        if (x)\n        {\n            Foo();\n        }";

    private const string TwoBlocksFile =
        "class C\n{\n    void A()\n    {\n        Foo();\n        Bar();\n    }\n\n    void B()\n    {\n        Foo();\n        Bar();\n    }\n}\n";

    private const string TwoBlocksText = "        Foo();\n        Bar();";

    [Test]
    public void Resolve_TabsWhereFileHasSpaces_AcceptsWholeLineSpan()
    {
        const string snippet = "\t\tif (x)\n\t\t{\n\t\t\tFoo();\n\t\t}";

        var result = ResolveIn(BlockFile, snippet);

        AssertSingleSpan(result, BlockFile, BlockText, expectedLine: 5);
        Assert.That(result!.Single().Tier, Is.EqualTo(WhitespaceTolerance.IgnoreIndentation));
    }

    [Test]
    public void Resolve_DroppedIndentation_Accepts()
    {
        const string snippet = "if (x)\n{\nFoo();\n}";

        AssertSingleSpan(ResolveIn(BlockFile, snippet), BlockFile, BlockText, expectedLine: 5);
    }

    [Test]
    public void Resolve_WrongIndentWidth_Accepts()
    {
        const string snippet = "  if (x)\n  {\n    Foo();\n  }";

        AssertSingleSpan(ResolveIn(BlockFile, snippet), BlockFile, BlockText, expectedLine: 5);
    }

    [Test]
    public void Resolve_IndentDifferenceInsideMultiLineRawString_ReturnsNull()
    {
        const string file =
            "class C\n{\n    string S()\n    {\n        return \"\"\"\n            a\n            b\n            \"\"\";\n    }\n}\n";
        const string snippet = "return \"\"\"\n    a\n    b\n    \"\"\";";

        // The matchers do find the block; it is the raw string's own indentation that makes the tokens differ.
        Assert.That(SnippetTokenEquivalence.FindLooserCandidates(Microsoft.CodeAnalysis.Text.SourceText.From(file), snippet), Is.Not.Empty);
        Assert.That(ResolveIn(file, snippet), Is.Null);
    }

    [Test]
    public void Resolve_CandidateOnlyInsideStringLiteral_ReturnsNull()
    {
        const string file = "class C\n{\n    string s = \"call foo(); now\";\n}\n";
        const string snippet = "  foo();";

        Assert.That(SnippetTokenEquivalence.FindLooserCandidates(Microsoft.CodeAnalysis.Text.SourceText.From(file), snippet), Is.Not.Empty);
        Assert.That(ResolveIn(file, snippet), Is.Null);
    }

    [Test]
    public void Resolve_TwoTokenEquivalentCandidates_IsAmbiguous()
    {
        const string snippet = "Foo();\nBar();";

        var result = ResolveIn(TwoBlocksFile, snippet);

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Outcome, Is.EqualTo(SnippetMatchOutcome.Ambiguous));
        Assert.That(result.Matches.Select(match => match.Start),
            Is.EqualTo(new[] { TwoBlocksFile.IndexOf(TwoBlocksText, StringComparison.Ordinal), TwoBlocksFile.LastIndexOf(TwoBlocksText, StringComparison.Ordinal) }));
        Assert.That(result.Matches.Select(match => match.Length), Is.All.EqualTo(TwoBlocksText.Length));
        Assert.That(result.Matches.Select(match => match.MatcherName), Is.All.EqualTo(SnippetTokenEquivalence.MatcherName));
    }

    [Test]
    public void Resolve_TwoCandidatesOneWithIdenticalLeadingWhitespace_TakesIt()
    {
        // Block A has the snippet's exact indentation but trailing spaces after Foo(); so the strict tier missed it;
        // block B is tab-indented.
        const string file =
            "class C\n{\n    void A()\n    {\n        Foo();   \n        Bar();\n    }\n\n    void B()\n    {\n\t\tFoo();\n\t\tBar();\n    }\n}\n";
        const string snippet = "        Foo();\n        Bar();";
        const string expectedText = "        Foo();   \n        Bar();";

        AssertSingleSpan(ResolveIn(file, snippet), file, expectedText, expectedLine: 5);
    }

    [Test]
    public void Resolve_MidLineStart_SpanStartsAtFirstToken()
    {
        const string file = "class C\n{\n    void M()\n    {\n        int total = 0; Foo();\n        Bar();\n    }\n}\n";
        const string snippet = "Foo();\n    Bar();";
        const string expectedText = "Foo();\n        Bar();";

        // The file's text before the first token on its line ("        int total = 0; ") stays outside the span.
        AssertSingleSpan(ResolveIn(file, snippet), file, expectedText, expectedLine: 5);
    }

    [Test]
    public void Resolve_SnippetEndsWithLineBreak_SpanIncludesLineBreak()
    {
        const string file = "class C\n{\n    void M()\n    {\n        Foo();\n        Bar();\n        Baz();\n    }\n}\n";
        const string snippet = "Foo();\nBar();\n";
        const string expectedText = "        Foo();\n        Bar();\n";

        AssertSingleSpan(ResolveIn(file, snippet), file, expectedText, expectedLine: 5);
    }

    [Test]
    public void Resolve_WithMatchLineSelector_PicksThatCandidate()
    {
        const string snippet = "Foo();\nBar();";

        var result = ResolveIn(TwoBlocksFile, snippet, new SnippetSelectors(MatchLine: 11));

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.CandidatesBeforeSelectors, Is.EqualTo(2));
        Assert.That(result.Outcome, Is.EqualTo(SnippetMatchOutcome.Matched));
        var match = result.Single();
        Assert.That(match.Start, Is.EqualTo(TwoBlocksFile.LastIndexOf(TwoBlocksText, StringComparison.Ordinal)));
        Assert.That(match.Length, Is.EqualTo(TwoBlocksText.Length));
        Assert.That(match.Line, Is.EqualTo(11));
        Assert.That(match.MatcherName, Is.EqualTo(SnippetTokenEquivalence.MatcherName));
    }

    private static SnippetMatchResult? ResolveIn(string file, string snippet, SnippetSelectors? selectors = null)
    {
        var (root, _) = ParseFile(file);
        return SnippetTokenEquivalence.Resolve(Microsoft.CodeAnalysis.Text.SourceText.From(file), root, snippet, selectors, null);
    }

    private static void AssertSingleSpan(SnippetMatchResult? result, string file, string expectedText, int expectedLine)
    {
        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Outcome, Is.EqualTo(SnippetMatchOutcome.Matched));
        var match = result.Single();
        Assert.That(match.Start, Is.EqualTo(file.IndexOf(expectedText, StringComparison.Ordinal)));
        Assert.That(match.Length, Is.EqualTo(expectedText.Length));
        Assert.That(match.Line, Is.EqualTo(expectedLine));
        Assert.That(match.MatcherName, Is.EqualTo(SnippetTokenEquivalence.MatcherName));
    }

    private static (Microsoft.CodeAnalysis.SyntaxNode Root, CSharpParseOptions Options) ParseFile(string file)
    {
        var tree = CSharpSyntaxTree.ParseText(file, Options);
        return (tree.GetRoot(), (CSharpParseOptions)tree.Options);
    }
}
