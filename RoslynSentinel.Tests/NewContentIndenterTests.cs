using Microsoft.CodeAnalysis;
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

    // ---- Step 5: IndentsFromFormatterChanges ----

    [Test]
    public void IndentsFromFormatterChanges_PureIndentChange_IsKept()
    {
        var text = Lines("class C", "{", "        int x;", "}", "");
        var lineStart = text.IndexOf("        int x;", StringComparison.Ordinal);
        var change = new TextChange(TextSpan.FromBounds(lineStart, lineStart + 8), "    ");

        var indents = NewContentIndenter.IndentsFromFormatterChanges(SourceText.From(text), [2], [change]);

        Assert.That(indents, Is.EqualTo(new Dictionary<int, string> { [2] = Spaces(4) }));
    }

    [Test]
    public void IndentsFromFormatterChanges_ChangeSpanningLineBreakAndIndent_KeepsTrailingIndent()
    {
        var text = Lines("class C", "{", "  int x;", "}", "");
        var start = text.IndexOf('{') + 1;
        var end = text.IndexOf("int x", StringComparison.Ordinal);
        var change = new TextChange(TextSpan.FromBounds(start, end), "\n    ");

        var indents = NewContentIndenter.IndentsFromFormatterChanges(SourceText.From(text), [2], [change]);

        Assert.That(indents, Is.EqualTo(new Dictionary<int, string> { [2] = Spaces(4) }));
    }

    [Test]
    public void IndentsFromFormatterChanges_IntraLineSpacingChange_IsDiscarded()
    {
        var text = Lines("class C", "{", "    int  x;", "}", "");
        var start = text.IndexOf("int", StringComparison.Ordinal) + 3;
        var end = text.IndexOf("x;", StringComparison.Ordinal);
        var change = new TextChange(TextSpan.FromBounds(start, end), " ");

        var indents = NewContentIndenter.IndentsFromFormatterChanges(SourceText.From(text), [2], [change]);

        Assert.That(indents, Is.Empty);
    }

    [Test]
    public void IndentsFromFormatterChanges_ChangeThatJoinsLines_IsDiscarded()
    {
        var text = Lines("class C", "{", "    int x;", "}", "");
        var start = text.IndexOf('{') + 1;
        var end = text.IndexOf("int x", StringComparison.Ordinal);
        var change = new TextChange(TextSpan.FromBounds(start, end), " ");

        var indents = NewContentIndenter.IndentsFromFormatterChanges(SourceText.From(text), [2], [change]);

        Assert.That(indents, Is.Empty);
    }

    // ---- Step 5: IndentsFromShift ----

    [Test]
    public void IndentsFromShift_LineStartSpan_ShiftsByBaseDifference()
    {
        var original = Lines("class C", "{", "    void M()", "    {", "        old();", "    }", "}", "");

        // newContent is written at base 0; the replaced line sits at base 8, so every line shifts by 8.
        var indents = Shift(original, "        old();\n", "a();\n    b();\n", (false, 4));

        Assert.That(indents, Is.EqualTo(new Dictionary<int, string> { [4] = Spaces(8), [5] = Spaces(12) }));
    }

    [Test]
    public void IndentsFromShift_TabsConvertedToFileUnit()
    {
        var tabFile = Lines("class C", "{", "\tvoid M()", "\t{", "\t\told();", "\t}", "}", "");
        var spaceFile = Lines("class C", "{", "  void M()", "  {", "    old();", "  }", "}", "");

        // Tab file: a one-tab snippet base moves to two tabs, the deeper line to three.
        var inTabs = Shift(tabFile, "\t\told();\n", "\ta();\n\t\tb();\n", (true, 4));

        // Space file with a two-space unit: each leading tab of newContent counts as one unit (two columns).
        var inSpaces = Shift(spaceFile, "    old();\n", "\ta();\n\t\tb();\n", (false, 2));

        Assert.Multiple(() =>
        {
            Assert.That(inTabs, Is.EqualTo(new Dictionary<int, string> { [4] = "\t\t", [5] = "\t\t\t" }));
            Assert.That(inSpaces, Is.EqualTo(new Dictionary<int, string> { [4] = Spaces(4), [5] = Spaces(6) }));
        });
    }

    [Test]
    public void IndentsFromShift_MidLineStartWithAbsoluteIndent_LeavesLaterLines()
    {
        var original = Lines("class C", "{", "    void M()", "    {", "        if (x) old();", "    }", "}", "");

        // Later lines are already at or beyond the file base (8): taken as absolute, so no extra base is added.
        var indents = Shift(original, "old();", "{\n            a();\n        }", (false, 4));

        Assert.That(indents, Is.EqualTo(new Dictionary<int, string> { [5] = Spaces(12), [6] = Spaces(8) }));
    }

    [Test]
    public void IndentsFromShift_MidLineStartWithRelativeIndent_AddsFileBase()
    {
        var original = Lines("class C", "{", "    void M()", "    {", "        if (x) old();", "    }", "}", "");

        // The closing line sits at column 0 (< file base 8), so the lines are relative and get the file base added.
        var indents = Shift(original, "old();", "{\n    a();\n}", (false, 4));

        Assert.That(indents, Is.EqualTo(new Dictionary<int, string> { [5] = Spaces(12), [6] = Spaces(8) }));
    }

    [Test]
    public void IndentsFromShift_BlockCommentContinuation_GetsSameDelta()
    {
        var original = Lines("class C", "{", "    void M()", "    {", "        old();", "    }", "}", "");

        var indents = Shift(original, "        old();\n", "/*\n * doc\n */\nx();\n", (false, 4));

        Assert.That(
            indents,
            Is.EqualTo(new Dictionary<int, string> { [4] = Spaces(8), [5] = Spaces(9), [6] = Spaces(9), [7] = Spaces(8) }));
    }

    // ---- Step 6: ReindentAsync ----

    [Test]
    public async Task ReindentAsync_ExactMatchWithTabIndentedNewContent_ConvertsToFileIndent()
    {
        var source = Lines("class C", "{", "    void M()", "    {", "        old();", "    }", "}", "");

        var result = await Reindent(source, ("        old();\n", "\t\tif (x)\n\t\t{\n\t\t\tcall();\n\t\t}\n"));

        var expected = Lines(
            "class C", "{", "    void M()", "    {", "        if (x)", "        {", "            call();", "        }", "    }", "}", "");
        Assert.That(result.Text, Is.EqualTo(expected));
        Assert.That(
            result.InsertedTexts, Is.EqualTo(new[] { Lines("        if (x)", "        {", "            call();", "        }", "") }));
        Assert.That(result.LinesAdjusted, Is.EqualTo(4));
        Assert.That(result.UsedFallback, Is.False);
    }

    [Test]
    public async Task ReindentAsync_RegionDoesNotParse_UsesShiftFallback()
    {
        var source = Lines("class C", "{", "    void M()", "    {", "        old();", "    }", "}", "");

        // The inserted if-block is never closed, so the edited text has one more syntax error than the original.
        var result = await Reindent(source, ("        old();\n", "\t\tif (x) {\n\t\t\tcall();\n"));

        var expected = Lines(
            "class C", "{", "    void M()", "    {", "        if (x) {", "            call();", "    }", "}", "");
        Assert.That(result.UsedFallback, Is.True);
        Assert.That(result.Text, Is.EqualTo(expected));
        Assert.That(result.LinesAdjusted, Is.EqualTo(2));
    }

    [Test]
    public async Task ReindentAsync_MidLineStart_DoesNotDoubleIndentLaterLines()
    {
        var source = Lines("class C", "{", "    void M()", "    {", "        var x = old;", "    }", "}", "");

        // Line 1 starts after "var x = " and is left alone; the later lines are already at the file's indent.
        var result = await Reindent(source, ("old;", "new Foo\n        {\n            A = 1\n        };"));

        var expected = Lines(
            "class C", "{", "    void M()", "    {", "        var x = new Foo", "        {", "            A = 1", "        };",
            "    }", "}", "");
        Assert.That(result.Text, Is.EqualTo(expected));
        Assert.That(result.LinesAdjusted, Is.EqualTo(0));
        Assert.That(result.UsedFallback, Is.False);
    }

    [Test]
    public async Task ReindentAsync_NewContentWithVerbatimStringLines_LeavesThemAsWritten()
    {
        var source = Lines("class C", "{", "    void M()", "    {", "        old();", "    }", "}", "");

        // The two lines that start inside the verbatim string keep their text; the code lines around it are re-indented.
        var result = await Reindent(
            source, ("        old();\n", "\t\tvar s = @\"line1\n  line2\n\tline3\";\n\t\tnext();\n"));

        var expected = Lines(
            "class C", "{", "    void M()", "    {", "        var s = @\"line1", "  line2", "\tline3\";", "        next();",
            "    }", "}", "");
        Assert.That(result.Text, Is.EqualTo(expected));
        Assert.That(result.LinesAdjusted, Is.EqualTo(2));
    }

    [Test]
    public async Task ReindentAsync_IntraLineSpacingAndBraces_LeftAsWritten()
    {
        var source = Lines("class C", "{", "    void M()", "    {", "        old();", "    }", "}", "");

        // Only leading whitespace is adjusted; the odd spacing and the one-line block stay as written.
        var result = await Reindent(source, ("        old();\n", "\t\tint   x=1 ;\n\t\tif(x>0){x++;}\n"));

        var expected = Lines(
            "class C", "{", "    void M()", "    {", "        int   x=1 ;", "        if(x>0){x++;}", "    }", "}", "");
        Assert.That(result.Text, Is.EqualTo(expected));
        Assert.That(result.LinesAdjusted, Is.EqualTo(2));
    }

    [Test]
    public async Task ReindentAsync_CorrectlyIndentedNewContent_ReportsZeroLinesAdjusted()
    {
        var source = Lines("class C", "{", "    void M()", "    {", "        old();", "    }", "}", "");
        var newContent = Lines("        if (x)", "        {", "            call();", "        }", "");

        var result = await Reindent(source, ("        old();\n", newContent));

        var expected = Lines(
            "class C", "{", "    void M()", "    {", "        if (x)", "        {", "            call();", "        }", "    }", "}", "");
        Assert.That(result.Text, Is.EqualTo(expected));
        Assert.That(result.InsertedTexts, Is.EqualTo(new[] { newContent }));
        Assert.That(result.LinesAdjusted, Is.EqualTo(0));
        Assert.That(result.UsedFallback, Is.False);
    }

    [Test]
    public async Task ReindentAsync_TwoEdits_InsertedTextsMatchInputOrder()
    {
        var source = Lines(
            "class C", "{", "    void A()", "    {", "        oldA();", "    }", "", "    void B()", "    {", "        oldB();",
            "    }", "}", "");

        // Input order is the later edit first; InsertedTexts must follow the input order, not the position order.
        var result = await Reindent(
            source,
            ("        oldB();\n", "\t\tif (b)\n\t\t{\n\t\t\tcallB();\n\t\t}\n"),
            ("        oldA();\n", "\t\tcallA();\n"));

        var expected = Lines(
            "class C", "{", "    void A()", "    {", "        callA();", "    }", "", "    void B()", "    {", "        if (b)",
            "        {", "            callB();", "        }", "    }", "}", "");
        Assert.That(result.Text, Is.EqualTo(expected));
        Assert.That(
            result.InsertedTexts,
            Is.EqualTo(new[] { Lines("        if (b)", "        {", "            callB();", "        }", ""), "        callA();\n" }));
        Assert.That(result.LinesAdjusted, Is.EqualTo(5));
    }

    // ---- helpers ----

    /// <summary>Replaces the first occurrence of oldText with newText and runs the fallback shift for that single edit.</summary>
    private static IReadOnlyDictionary<int, string> Shift(string original, string oldText, string newText, (bool UseTabs, int Width) unit)
    {
        var start = original.IndexOf(oldText, StringComparison.Ordinal);
        var edit = new TextChange(new TextSpan(start, oldText.Length), newText);
        var originalText = SourceText.From(original);
        var spliced = originalText.WithChanges(edit);
        var inserted = new TextSpan(start, newText.Length);
        var eligible = Eligible(spliced.ToString(), inserted, skipBlockCommentLines: false);

        return NewContentIndenter.IndentsFromShift(originalText, spliced, [edit], [inserted], eligible, unit);
    }

    /// <summary>
    /// Puts <paramref name="source"/> in an AdhocWorkspace document, turns each (old, new) pair into an edit on the
    /// first occurrence of old (edits keep the order given), and runs ReindentAsync.
    /// </summary>
    private static async Task<NewContentIndentResult> Reindent(string source, params (string Old, string New)[] replacements)
    {
        using var workspace = new AdhocWorkspace();
        var project = workspace.AddProject("NewContentIndenterTests", LanguageNames.CSharp);
        var document = workspace.AddDocument(project.Id, "Source.cs", SourceText.From(source));
        var edits = replacements
            .Select(pair => new TextChange(
                new TextSpan(source.IndexOf(pair.Old, StringComparison.Ordinal), pair.Old.Length), pair.New))
            .ToList();

        return await NewContentIndenter.ReindentAsync(document, edits, CancellationToken.None);
    }

    private static string Spaces(int count) => new(' ', count);

    private static string Lines(params string[] lines) => string.Join("\n", lines);

    private static TextSpan WholeText(string source) => new(0, source.Length);

    private static IReadOnlyList<int> Eligible(string source, TextSpan inserted, bool skipBlockCommentLines)
    {
        var tree = CSharpSyntaxTree.ParseText(source);
        return NewContentIndenter.EligibleLines(
            SourceText.From(source), tree.GetRoot(), [inserted], skipBlockCommentLines);
    }
}
