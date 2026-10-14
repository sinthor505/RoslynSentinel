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
    public void FindExactSnippetPosition_InteriorIndentationDropped_NamesLeadingWhitespaceWithCountsAndQuotesFileLine()
    {
        // Mirrors the Qwen run: the interior line lost its indentation, the other lines kept theirs.
        const string source = "class C\n{\n    void M()\n    {\n        var first = 1;\n        {\n            var trailingIssue = first;\n        }\n    }\n}";
        const string snippet = "var first = 1;\n        {\nvar trailingIssue = first;\n        }";

        var ex = Assert.Throws<ToolNotFoundException>(() => ContextHelper.FindExactSnippetPosition(
            SourceText.From(source), snippet));

        Assert.Multiple(() =>
        {
            Assert.That(ex!.Message, Does.Contain("snippet line 3 matches file line 7 except leading whitespace: expected 12 spaces, got 0"));
            Assert.That(ex.Message, Does.Contain("\n            var trailingIssue = first;"), "the file's actual line must be quoted verbatim");
            Assert.That(ex.Message, Does.Contain("source lines 5-8"));
            Assert.That(ex.Message, Does.Contain("byte-exact"));
            Assert.That(ex.Message, Does.Not.Contain("not in the arrangement you supplied"));
            Assert.That(ex.Message, Does.Not.Contain("contextSnippet"));
        });
    }

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

    [Test]
    public void FindExactSnippetPosition_SnippetWithBlankLine_NumbersEveryLabelOnOneBasisAndNeverBlamesBlankSourceLine()
    {
        // Snippet lines (blank included): 1 "a = 1;", 2 "", 3 "b = 99;", 4 "c = 3;". The file's blank
        // line is source line 6, the stale line's real counterpart "b = 2;" is source line 7.
        const string snippet = "a = 1;\n\n        b = 99;\n        c = 3;";

        var ex = Assert.Throws<ToolNotFoundException>(() => ContextHelper.FindExactSnippetPosition(
            SourceText.From(BlankLineSource), snippet));

        var snippetLineNumbers = System.Text.RegularExpressions.Regex.Matches(ex!.Message, @"snippet line (\d+)")
            .Select(m => m.Groups[1].Value).Distinct().ToArray();
        Assert.Multiple(() =>
        {
            Assert.That(ex.Message, Does.Contain("line 3 (\"b = 99;\") did not match"));
            Assert.That(ex.Message, Does.Contain("snippet line 3 (\"b = 99;\") differs from source line 7 (\"b = 2;\")"));
            Assert.That(snippetLineNumbers, Is.EqualTo(new[] { "3" }), "every 'snippet line N' label must use the same numbering");
            Assert.That(ex.Message, Does.Not.Contain("source line 6 (\"\")"));
            Assert.That(ex.Message, Does.Not.Contain("differs from source line 6"));
        });
    }

    [Test]
    public void FindExactSnippetPosition_SnippetOmitsFilesBlankLine_DescribesBlankLineInsteadOfQuotingEmptyContent()
    {
        const string snippet = "a = 1;\n        b = 2;\n        c = 3;";

        var ex = Assert.Throws<ToolNotFoundException>(() => ContextHelper.FindExactSnippetPosition(
            SourceText.From(BlankLineSource), snippet));

        Assert.Multiple(() =>
        {
            Assert.That(ex!.Message, Does.Contain("snippet line 2 (\"b = 2;\") differs from source line 6, a blank line that your snippet does not include"));
            Assert.That(ex.Message, Does.Not.Contain("source line 6 (\"\")"));
        });
    }

    [Test]
    public void FindSnippetPositionWithLength_WhitespaceOnlyNonContiguous_DoesNotClaimByteExactAndKeepsDefaultLabel()
    {
        var ex = Assert.Throws<ToolNotFoundException>(() => ContextHelper.FindSnippetPositionWithLength(
            SourceText.From(SpacedSource), SpacedSnippet));

        Assert.Multiple(() =>
        {
            Assert.That(ex!.Message, Does.Contain("Every line of your contextSnippet matches source line content exactly except for whitespace"));
            Assert.That(ex.Message, Does.Not.Contain("byte-exact"));
            Assert.That(ex.Message, Does.Not.Contain("ReplaceSnippet requires"));
        });
    }

    [Test]
    public void FindExactSnippetPosition_WhitespaceOnlyNonContiguous_UsesOldContentLabelAndStatesByteExact()
    {
        var ex = Assert.Throws<ToolNotFoundException>(() => ContextHelper.FindExactSnippetPosition(
            SourceText.From(SpacedSource), SpacedSnippet));

        Assert.Multiple(() =>
        {
            Assert.That(ex!.Message, Does.Contain("Every line of your oldContent matches source line content exactly except for whitespace"));
            Assert.That(ex.Message, Does.Contain("byte-exact"));
            Assert.That(ex.Message, Does.Not.Contain("contextSnippet"));
        });
    }

    [Test]
    public void FindExactSnippetPosition_LargeFileAllIndentationDropped_StaysBounded()
    {
        var fileLines = Enumerable.Range(0, 3000).Select(i => $"        step{i}();").ToList();
        var source = string.Join("\n", fileLines);
        var snippet = string.Join("\n", fileLines.Skip(1000).Take(40).Select(l => l.TrimStart()));

        var ex = Assert.Throws<ToolNotFoundException>(() => ContextHelper.FindExactSnippetPosition(
            SourceText.From(source), snippet));

        var quotedFileLines = ex!.Message.Split('\n').Count(l => l.StartsWith("        step", StringComparison.Ordinal));
        Assert.Multiple(() =>
        {
            Assert.That(ex.Message, Does.Contain("except leading whitespace: expected 8 spaces, got 0"));
            Assert.That(ex.Message, Does.Contain("more line(s) differ in whitespace"));
            Assert.That(ex.Message, Does.Contain("more line(s) not shown"));
            Assert.That(quotedFileLines, Is.LessThanOrEqualTo(12), "verbatim file block is capped at 12 lines");
            Assert.That(ex.Message.Length, Is.LessThan(3000));
        });
    }
}
