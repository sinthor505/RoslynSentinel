using Microsoft.CodeAnalysis.Text;
using RoslynSentinel.Common.SnippetMatching;

namespace RoslynSentinel.Tests;

/// <summary>
/// Slice 1 of proposal_internal_whitespace_tolerance_contract_for_snippet_helpers.md: the NoMatch
/// diagnosis must name dropped indentation (the Qwen run 20261013-191017 failure), number snippet
/// lines on one basis, and not claim "byte-exact" or "contextSnippet" where that is untrue.
/// </summary>
[Parallelizable(ParallelScope.All)]
public class ContextHelperIndentationDiagnosisTests
{
    private const string BlankLineSource =
        "class C\n{\n    void M()\n    {\n        a = 1;\n\n        b = 2;\n        c = 3;\n    }\n}";

    private const string SpacedSource = "var  x = 1;\nother();\nvar  y = 2;";

    private const string SpacedSnippet = "var x = 1;\nvar y = 2;";

    [Test]
    public void FindExactSnippetPosition_TabsWhereFileHasSpaces_NamesTabsVersusSpaces()
    {
        const string source = "    void M()\n    {\n        a = 1;\n        b = 2;\n    }";
        const string snippet = "a = 1;\n\t\tb = 2;";

        var text = SourceText.From(source);
        var result = SnippetResolver.Resolve(
            text, snippet, WhitespaceTolerance.IgnoreLineEndings, SnippetCaseMode.CaseSensitive);
        var ex = SnippetErrorFormatter.Build(result, text, snippet, "oldContent");

        Assert.Multiple(() =>
        {
            Assert.That(result.Outcome, Is.EqualTo(SnippetMatchOutcome.MatchedLooserThanRequested));
            Assert.That(ex.Message, Does.Contain(
                "line 2: file has 8 leading spaces, snippet has 2 leading tabs (file line 4)"));
        });
    }

    [Test]
    public void FindExactSnippetPosition_SnippetHasExtraTrailingWhitespace_NamesTrailingWhitespace()
    {
        const string source = "    void M()\n    {\n        a = 1;\n        b = 2;\n        c = 3;\n    }";
        const string snippet = "a = 1;   \n        b = 2;\n        c = 3;";

        var text = SourceText.From(source);
        var result = SnippetResolver.Resolve(
            text, snippet, WhitespaceTolerance.IgnoreLineEndings, SnippetCaseMode.CaseSensitive);
        var ex = SnippetErrorFormatter.Build(result, text, snippet, "oldContent");

        Assert.Multiple(() =>
        {
            Assert.That(result.Outcome, Is.EqualTo(SnippetMatchOutcome.MatchedLooserThanRequested));
            Assert.That(ex.Message, Does.Contain(
                "line 1: file has none, snippet has 3 trailing spaces (file line 3)"));
        });
    }
}
