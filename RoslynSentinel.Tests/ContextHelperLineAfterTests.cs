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
}
