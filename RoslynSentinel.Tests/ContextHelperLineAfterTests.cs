using Microsoft.CodeAnalysis.Text;

namespace RoslynSentinel.Tests;

[Parallelizable(ParallelScope.All)]
public class ContextHelperLineAfterTests
{
    private const string TwoBlocksDifferentTails =
        "class C\n{\n    void A()\n    {\n        x = 1;\n        y = 2;\n        return;\n    }\n\n    void B()\n    {\n        x = 1;\n        y = 2;\n        throw;\n    }\n}";

    private const string TwoBlocksSameTails =
        "class C\n{\n    void A()\n    {\n        x = 1;\n        y = 2;\n        return;\n    }\n\n    void B()\n    {\n        x = 1;\n        y = 2;\n        return;\n    }\n}";

    private const string MultiLineSnippet = "x = 1;\n        y = 2;";

    [Test]
    public void FindExactSnippetPosition_MultiLineSnippet_LineAfterSelectsMatchByLastLine()
    {
        var match = ContextHelper.FindExactSnippetPosition(
            SourceText.From(TwoBlocksDifferentTails), MultiLineSnippet, lineAfter: "throw;");

        Assert.That(match.Start, Is.EqualTo(TwoBlocksDifferentTails.LastIndexOf(MultiLineSnippet, StringComparison.Ordinal)));
    }

    [Test]
    public void FindExactSnippetPosition_LineAfterFiltersOutEveryMatch_ReportsDistinctError()
    {
        var ex = Assert.Throws<ToolNotFoundException>(() => ContextHelper.FindExactSnippetPosition(
            SourceText.From(TwoBlocksDifferentTails), MultiLineSnippet, lineAfter: "nonexistent;"));

        Assert.That(ex!.Message, Does.Contain("filtered out all of them"));
        Assert.That(ex.Message, Does.Not.Contain("not in the arrangement you supplied"));
    }

    [Test]
    public void FindExactSnippetPosition_StillAmbiguous_ReportsLinesOfEachMatch()
    {
        var ex = Assert.Throws<ToolAmbiguousMatchException>(() => ContextHelper.FindExactSnippetPosition(
            SourceText.From(TwoBlocksSameTails), MultiLineSnippet));

        Assert.That(ex!.Message, Does.Contain("lines 5-6"));
        Assert.That(ex.Message, Does.Contain("lines 12-13"));
        Assert.That(ex.Message, Does.Contain("return;"));
    }

    [Test]
    public void SingleLineSnippet_LineBefore_SelectsMatchThroughBothMatchers()
    {
        var source = "a();\nfoo();\nb();\nfoo();\nc();";
        var expected = source.LastIndexOf("foo();", StringComparison.Ordinal);

        var exact = ContextHelper.FindExactSnippetPosition(SourceText.From(source), "foo();", lineBefore: "b();");
        var loose = ContextHelper.FindSnippetPosition(SourceText.From(source), "foo();", lineBefore: "b();");

        Assert.Multiple(() =>
        {
            Assert.That(exact.Start, Is.EqualTo(expected));
            Assert.That(loose, Is.EqualTo(expected));
        });
    }

    [Test]
    public void FindExactSnippetPosition_MultiLineSnippetWithStaleMiddleLine_AppendsClosestWindowHint()
    {
        const string source = "class C\n{\n    void A()\n    {\n        a = 1;\n        b = 2;\n        c = 3;\n        d = 4;\n        e = 5;\n    }\n}";
        const string staleSnippet = "a = 1;\n        b = 2;\n        c = 99;\n        d = 4;\n        e = 5;";

        var ex = Assert.Throws<ToolNotFoundException>(() => ContextHelper.FindExactSnippetPosition(
            SourceText.From(source), staleSnippet));

        Assert.That(ex!.Message, Does.Contain("Closest window"));
        Assert.That(ex.Message, Does.Contain("source lines 5-6"));
        Assert.That(ex.Message, Does.Contain("c = 3;"));
    }

    [Test]
    public void FindExactSnippetPosition_NonContiguousSnippetWithHundredsOfHitsPerLine_CapsDiagnosis()
    {
        // Every snippet line occurs 300 times, but the file never has gamma() directly before alpha().
        var source = string.Concat(Enumerable.Repeat("    alpha();\n    beta();\n", 300))
            + string.Concat(Enumerable.Repeat("    gamma();\n", 300));
        const string snippet = "    gamma();\n    alpha();\n    beta();";

        var ex = Assert.Throws<ToolNotFoundException>(() => ContextHelper.FindExactSnippetPosition(
            SourceText.From(source), snippet));

        Assert.That(ex!.Message, Does.Contain("were located individually in"));
        Assert.That(ex.Message, Does.Contain("more candidate line(s) not shown"));
        Assert.That(ex.Message.Length, Is.LessThan(2300));
    }

    [Test]
    public void FindExactSnippetPosition_RareLineWithBoilerplateLine_ReportsRareLineAndTooCommonNotice()
    {
        // "return;" occurs 60 times (boilerplate, 4+ chars so it survives the MinLineLength filter);
        // rare_marker() occurs once and is never directly followed by "return;".
        var source = "class C\n{\n" + string.Concat(Enumerable.Repeat("    if (a)\n    {\n        return;\n    }\n", 60))
            + "    rare_marker();\n    c();\n}\n";
        var rareLine = source.Split('\n').ToList().FindIndex(l => l.Contains("rare_marker", StringComparison.Ordinal)) + 1;
        const string snippet = "    rare_marker();\n    return;";

        var ex = Assert.Throws<ToolNotFoundException>(() => ContextHelper.FindExactSnippetPosition(
            SourceText.From(source), snippet));

        Assert.That(ex!.Message, Does.Contain($"was located at source line {rareLine}:"));
        Assert.That(ex.Message, Does.Contain("too common"));
    }

    [Test]
    public void FindExactSnippetPosition_Ambiguous_WithMemberNameAt_NamesEnclosingMemberPerMatch()
    {
        var bStart = TwoBlocksSameTails.IndexOf("void B", StringComparison.Ordinal);

        var ex = Assert.Throws<ToolAmbiguousMatchException>(() => ContextHelper.FindExactSnippetPosition(
            SourceText.From(TwoBlocksSameTails), MultiLineSnippet,
            memberNameAt: offset => offset < bStart ? "A" : "B"));

        Assert.Multiple(() =>
        {
            Assert.That(ex!.Message, Does.Contain("Match 1 (lines 5-6, in A)"));
            Assert.That(ex.Message, Does.Contain("Match 2 (lines 12-13, in B)"));
        });
    }

    [Test]
    public void FindExactSnippetPosition_Ambiguous_WithoutMemberNameAt_KeepsOriginalFormat()
    {
        var ex = Assert.Throws<ToolAmbiguousMatchException>(() => ContextHelper.FindExactSnippetPosition(
            SourceText.From(TwoBlocksSameTails), MultiLineSnippet));

        Assert.Multiple(() =>
        {
            Assert.That(ex!.Message, Does.Contain("Match 1 (lines 5-6): matchLine=5 lineBefore="));
            Assert.That(ex.Message, Does.Contain("Match 2 (lines 12-13): matchLine=12 lineBefore="));
            Assert.That(ex.Message, Does.Not.Contain(", in "));
        });
    }

    [Test]
    public void FindExactSnippetPosition_StillAmbiguous_ListsCandidatesCappedAtFive()
    {
        var blocks = string.Concat(Enumerable.Range(1, 7).Select(i =>
            $"    void M{i}()\n    {{\n        x = 1;\n        y = 2;\n        return;\n    }}\n\n"));
        var source = "class C\n{\n" + blocks + "}";

        var ex = Assert.Throws<ToolAmbiguousMatchException>(() => ContextHelper.FindExactSnippetPosition(
            SourceText.From(source), MultiLineSnippet, lineBefore: "{", lineAfter: "return;",
            memberNameAt: offset => "M" + (source.Substring(0, offset).Split("void M").Length - 1)));

        Assert.Multiple(() =>
        {
            Assert.That(ex!.Message, Does.Contain("still ambiguous (7 matches remain)"));
            Assert.That(ex.Message, Does.Contain("Match 1 (lines 5-6, in M1)"));
            Assert.That(ex.Message, Does.Contain("Match 5 ("));
            Assert.That(ex.Message, Does.Not.Contain("Match 6 ("));
            Assert.That(ex.Message, Does.Contain("... and 2 more"));
            Assert.That(ex.Message.Length, Is.LessThan(2048));
        });
    }
}
