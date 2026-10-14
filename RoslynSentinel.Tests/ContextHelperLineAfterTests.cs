using Microsoft.CodeAnalysis.Text;
using RoslynSentinel.Common.SnippetMatching;

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
        var result = SnippetResolver.Resolve(
            SourceText.From(TwoBlocksDifferentTails), MultiLineSnippet, WhitespaceTolerance.IgnoreLineEndings,
            SnippetCaseMode.CaseSensitive, new SnippetSelectors(LineAfter: "throw;"), memberNameAt: null);

        Assert.Multiple(() =>
        {
            Assert.That(result.Outcome, Is.EqualTo(SnippetMatchOutcome.Matched));
            Assert.That(result.Single().Start, Is.EqualTo(TwoBlocksDifferentTails.LastIndexOf(MultiLineSnippet, StringComparison.Ordinal)));
        });
    }

    [Test]
    public void FindExactSnippetPosition_LineAfterFiltersOutEveryMatch_ReportsDistinctError()
    {
        var text = SourceText.From(TwoBlocksDifferentTails);
        var result = SnippetResolver.Resolve(
            text, MultiLineSnippet, WhitespaceTolerance.IgnoreLineEndings, SnippetCaseMode.CaseSensitive,
            new SnippetSelectors(LineAfter: "nonexistent;"), memberNameAt: null);
        var ex = SnippetErrorFormatter.Build(result, text, MultiLineSnippet, "oldContent", selectorsSupported: true);

        Assert.Multiple(() =>
        {
            Assert.That(result.Outcome, Is.EqualTo(SnippetMatchOutcome.AdjacentLineMismatch));
            Assert.That(ex, Is.InstanceOf<ToolNotFoundException>());
            Assert.That(ex.Message, Does.StartWith("oldContent was found (2 match(es)), but not next to the lineAfter you supplied"));
            Assert.That(ex.Message, Does.Contain("lineAfter removed 2"));
            Assert.That(ex.Message, Does.Contain("copy the lineBefore/lineAfter shown"));
            Assert.That(ex.Message, Does.Contain("matchLine="));
            Assert.That(ex.Message, Does.Not.Contain("verbatim"));
            Assert.That(ex.Message, Does.Not.Contain("Re-read the file"));
            Assert.That(ex.Message, Does.Not.Contain("not in the arrangement you supplied"));
        });
    }

    [Test]
    public void FindExactSnippetPosition_StillAmbiguous_ReportsLinesOfEachMatch()
    {
        var text = SourceText.From(TwoBlocksSameTails);
        var result = SnippetResolver.Resolve(
            text, MultiLineSnippet, WhitespaceTolerance.IgnoreLineEndings, SnippetCaseMode.CaseSensitive);
        var ex = SnippetErrorFormatter.Build(result, text, MultiLineSnippet, "oldContent");

        Assert.Multiple(() =>
        {
            Assert.That(result.Outcome, Is.EqualTo(SnippetMatchOutcome.Ambiguous));
            Assert.That(ex, Is.InstanceOf<ToolAmbiguousMatchException>());
            Assert.That(ex.Message, Does.Contain("lines 5-6"));
            Assert.That(ex.Message, Does.Contain("lines 12-13"));
            Assert.That(ex.Message, Does.Contain("return;"));
        });
    }

    [Test]
    public void SingleLineSnippet_LineBefore_SelectsMatchThroughBothMatchers()
    {
        var source = "a();\nfoo();\nb();\nfoo();\nc();";
        var expected = source.LastIndexOf("foo();", StringComparison.Ordinal);

        var exact = SnippetResolver.Resolve(
            SourceText.From(source), "foo();", WhitespaceTolerance.IgnoreLineEndings, SnippetCaseMode.CaseSensitive,
            new SnippetSelectors(LineBefore: "b();"), memberNameAt: null).Single();
        var loose = SnippetLookup.ResolveOrThrow(SourceText.From(source), "foo();", lineBefore: "b();");

        Assert.Multiple(() =>
        {
            Assert.That(exact.Start, Is.EqualTo(expected));
            Assert.That(loose.Start, Is.EqualTo(expected));
        });
    }

    [Test]
    public void FindExactSnippetPosition_MultiLineSnippetWithStaleMiddleLine_AppendsClosestWindowHint()
    {
        const string source = "class C\n{\n    void A()\n    {\n        a = 1;\n        b = 2;\n        c = 3;\n        d = 4;\n        e = 5;\n    }\n}";
        const string staleSnippet = "a = 1;\n        b = 2;\n        c = 99;\n        d = 4;\n        e = 5;";

        var text = SourceText.From(source);
        var result = SnippetResolver.Resolve(
            text, staleSnippet, WhitespaceTolerance.IgnoreLineEndings, SnippetCaseMode.CaseSensitive);
        var ex = SnippetErrorFormatter.Build(result, text, staleSnippet, "oldContent");

        Assert.Multiple(() =>
        {
            Assert.That(result.Outcome, Is.EqualTo(SnippetMatchOutcome.NoMatch));
            Assert.That(ex, Is.InstanceOf<ToolNotFoundException>());
            Assert.That(ex.Message, Does.Contain("Closest window"));
            Assert.That(ex.Message, Does.Contain("starts at line 5"));
            Assert.That(ex.Message, Does.Contain("c = 3;"));
        });
    }

    [Test]
    public void FindExactSnippetPosition_Ambiguous_WithMemberNameAt_NamesEnclosingMemberPerMatch()
    {
        var bStart = TwoBlocksSameTails.IndexOf("void B", StringComparison.Ordinal);
        var text = SourceText.From(TwoBlocksSameTails);
        var result = SnippetResolver.Resolve(
            text, MultiLineSnippet, WhitespaceTolerance.IgnoreLineEndings, SnippetCaseMode.CaseSensitive);
        var ex = SnippetErrorFormatter.Build(
            result, text, MultiLineSnippet, "oldContent", memberNameAt: offset => offset < bStart ? "A" : "B");

        Assert.Multiple(() =>
        {
            Assert.That(ex, Is.InstanceOf<ToolAmbiguousMatchException>());
            Assert.That(ex.Message, Does.Contain("Candidate 1 of 2 (lines 5-6, in A):"));
            Assert.That(ex.Message, Does.Contain("Candidate 2 of 2 (lines 12-13, in B):"));
        });
    }

    [Test]
    public void FindExactSnippetPosition_Ambiguous_WithoutMemberNameAt_KeepsOriginalFormat()
    {
        var text = SourceText.From(TwoBlocksSameTails);
        var result = SnippetResolver.Resolve(
            text, MultiLineSnippet, WhitespaceTolerance.IgnoreLineEndings, SnippetCaseMode.CaseSensitive);
        var ex = SnippetErrorFormatter.Build(result, text, MultiLineSnippet, "oldContent", selectorsSupported: true);

        Assert.Multiple(() =>
        {
            Assert.That(ex.Message, Does.Contain("Candidate 1 of 2 (lines 5-6):"));
            Assert.That(ex.Message, Does.Contain("matchLine=5 lineBefore="));
            Assert.That(ex.Message, Does.Contain("Candidate 2 of 2 (lines 12-13):"));
            Assert.That(ex.Message, Does.Contain("matchLine=12 lineBefore="));
            Assert.That(ex.Message, Does.Not.Contain(", in "));
        });
    }
}
