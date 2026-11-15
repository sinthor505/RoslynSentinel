using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using RoslynSentinel.Common.SnippetMatching;

namespace RoslynSentinel.Tests;

[TestFixture]
[Parallelizable(ParallelScope.All)]
public class NewContentIndenterTests
{
    // Steps 5 and 6 append their tests below the Step 4 group; keep helpers at the bottom of the class.

    // ---- Step 4: InferIndentUnit ----

    [Test]
    public void InferIndentUnit_FourSpaceFile_ReturnsSpacesFour()
    {
        var text = SourceText.From(Lines("class C", "{", "    void M()", "    {", "        int x;", "    }", "}", ""));

        var unit = NewContentIndenter.InferIndentUnit(text);

        Assert.That(unit, Is.EqualTo((UseTabs: false, Width: 4)));
    }

    [Test]
    public void InferIndentUnit_TabFile_ReturnsTabs()
    {
        var text = SourceText.From(Lines("class C", "{", "\tvoid M()", "\t{", "\t\tint x;", "\t}", "}", ""));

        var unit = NewContentIndenter.InferIndentUnit(text);

        Assert.That(unit.UseTabs, Is.True);
    }

    [Test]
    public void InferIndentUnit_NoIndentedLines_DefaultsToFourSpaces()
    {
        var text = SourceText.From(Lines("class C { }", "class D { }", ""));

        var unit = NewContentIndenter.InferIndentUnit(text);

        Assert.That(unit, Is.EqualTo((UseTabs: false, Width: 4)));
    }

    // ---- Step 4: EligibleLines ----

    [Test]
    public void EligibleLines_LinesInsideMultiLineVerbatimString_AreSkipped()
    {
        var source = Lines("class C", "{", "    string s = @\"a", "b", "  c\";", "    int x;", "}");

        var eligible = Eligible(source, WholeText(source), skipBlockCommentLines: true);

        // Lines 3 and 4 start inside the verbatim string token.
        Assert.That(eligible, Is.EqualTo(new[] { 0, 1, 2, 5, 6 }));
    }

    [Test]
    public void EligibleLines_LinesInsideRawString_AreSkipped()
    {
        var source = Lines(
            "class C", "{", "    string s = \"\"\"", "        a", "      b", "        \"\"\";", "    int x;", "}");

        var eligible = Eligible(source, WholeText(source), skipBlockCommentLines: true);

        // Lines 3 to 5 start inside the raw string token (including the closing delimiter line).
        Assert.That(eligible, Is.EqualTo(new[] { 0, 1, 2, 6, 7 }));
    }

    [Test]
    public void EligibleLines_BlockCommentContinuation_SkippedOnlyWhenRequested()
    {
        var source = Lines("class C", "{", "    /*", "     * doc", "     */", "    int x;", "}");

        var skipped = Eligible(source, WholeText(source), skipBlockCommentLines: true);
        var kept = Eligible(source, WholeText(source), skipBlockCommentLines: false);

        Assert.Multiple(() =>
        {
            Assert.That(skipped, Is.EqualTo(new[] { 0, 1, 2, 5, 6 }));
            Assert.That(kept, Is.EqualTo(new[] { 0, 1, 2, 3, 4, 5, 6 }));
        });
    }

    [Test]
    public void EligibleLines_MidLineInsertion_FirstLineNotEligible()
    {
        var source = Lines("class C", "{", "    int a = 1; int b = 2;", "    int c;", "}", "");
        var start = source.IndexOf("int b", StringComparison.Ordinal);
        var end = source.IndexOf("int c", StringComparison.Ordinal);

        var eligible = Eligible(source, TextSpan.FromBounds(start, end), skipBlockCommentLines: true);

        // Line 2 has code before the insertion; only line 3 (which starts inside the insertion) qualifies.
        Assert.That(eligible, Is.EqualTo(new[] { 3 }));
    }

    // ---- Step 4: ApplyIndents ----

    [Test]
    public void ApplyIndents_WhitespacePrefixOnFirstLine_ChangesOnlyInsertedPart()
    {
        // The edit replaced "old;" (after a four-space prefix) with "x;\n    y;".
        var source = "    x;\n    y;";
        var insertion = TextSpan.FromBounds(4, source.Length);
        var eightSpaces = new string(' ', 8);

        var result = NewContentIndenter.ApplyIndents(
            SourceText.From(source), [insertion], new Dictionary<int, string> { [0] = eightSpaces, [1] = eightSpaces }, usedFallback: false);

        var left = NewContentIndenter.ApplyIndents(
            SourceText.From(source), [insertion], new Dictionary<int, string> { [0] = "\t\t" }, usedFallback: true);

        Assert.Multiple(() =>
        {
            Assert.That(result.Text, Is.EqualTo("        x;\n        y;"));
            Assert.That(result.InsertedTexts, Is.EqualTo(new[] { "    x;\n        y;" }));
            Assert.That(result.LinesAdjusted, Is.EqualTo(2));
            Assert.That(result.UsedFallback, Is.False);

            // A target that does not start with the existing prefix leaves line 1 as written.
            Assert.That(left.Text, Is.EqualTo(source));
            Assert.That(left.InsertedTexts, Is.EqualTo(new[] { "x;\n    y;" }));
            Assert.That(left.LinesAdjusted, Is.EqualTo(0));
            Assert.That(left.UsedFallback, Is.True);
        });
    }

    [Test]
    public void ApplyIndents_ReturnsInsertedTextsInInputOrder()
    {
        var source = "    x;\n    y;\n    z;\n    w;\n";
        var first = TextSpan.FromBounds(0, 14);
        var second = TextSpan.FromBounds(14, source.Length);
        var indents = new Dictionary<int, string> { [0] = "", [1] = "", [2] = new string(' ', 8), [3] = new string(' ', 8) };

        // Spans are passed out of position order on purpose.
        var result = NewContentIndenter.ApplyIndents(SourceText.From(source), [second, first], indents, usedFallback: false);

        Assert.Multiple(() =>
        {
            Assert.That(result.Text, Is.EqualTo("x;\ny;\n        z;\n        w;\n"));
            Assert.That(result.InsertedTexts, Is.EqualTo(new[] { "        z;\n        w;\n", "x;\ny;\n" }));
            Assert.That(result.LinesAdjusted, Is.EqualTo(4));
        });
    }

    // ---- helpers ----

    private static string Lines(params string[] lines) => string.Join("\n", lines);

    private static TextSpan WholeText(string source) => new(0, source.Length);

    private static IReadOnlyList<int> Eligible(string source, TextSpan inserted, bool skipBlockCommentLines)
    {
        var tree = CSharpSyntaxTree.ParseText(source);
        return NewContentIndenter.EligibleLines(
            SourceText.From(source), tree.GetRoot(), [inserted], skipBlockCommentLines);
    }
}
