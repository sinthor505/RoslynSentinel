using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis.Text;
using RoslynSentinel.Common.SnippetMatching;
using static RoslynSentinel.Tests.Support.Sources;

namespace RoslynSentinel.Tests;

/// <summary>
/// Tests for the four stage-pure matchers (slices 2b and 2d of the whitespace-tolerance proposal): the splice
/// invariant (the 2026-09-10 corruption regression), stage purity, and the matcher-by-fixture matrix.
/// </summary>
[Parallelizable(ParallelScope.All)]
public class SnippetMatcherTests
{
    private static readonly ISnippetMatcher[] Matchers =
    {
        new ExactMatcher(),
        new LineEndingMatcher(),
        new IndentationMatcher(),
        new CollapsedWhitespaceMatcher(),
    };

    /// <summary>The shared corpus of (text, snippet) cases; also used by the dispatcher parity tests.</summary>
    public static IEnumerable<TestCaseData> Corpus()
    {
        yield return new TestCaseData("int x = 1;\nint y = 2;\n", "int y = 2;").SetName("SingleLine_Verbatim");
        yield return new TestCaseData("a\nb\nc\n", "a\nb").SetName("MultiLine_LfFileLfSnippet");
        yield return new TestCaseData("a\r\nb\r\nc\r\n", "a\nb").SetName("MultiLine_CrlfFileLfSnippet");
        yield return new TestCaseData("a\nb\nc\n", "a\r\nb").SetName("MultiLine_LfFileCrlfSnippet");
        yield return new TestCaseData("    int x = 1;\n", "int x = 1;").SetName("SingleLine_DroppedIndentation");
        yield return new TestCaseData("        }\n        var trailingIssue = x;\n", "}\nvar trailingIssue = x;").SetName("MultiLine_DroppedIndentation");
        yield return new TestCaseData("    a\r\n    b\r\n", "a\nb").SetName("MultiLine_DroppedIndentation_CrlfFile");
        yield return new TestCaseData("var r = a  +  b;\n", "a + b;").SetName("Interior_WhitespaceDifference");
        yield return new TestCaseData("Console.WriteLine(X);\n", "console.writeline(x);").SetName("CaseDifference");
        yield return new TestCaseData("if (a) return x;\n", "return x;").SetName("MidLine_Fragment");
        yield return new TestCaseData("foo();\nfoo();\nfoo();\n", "foo();").SetName("Repeated_SingleLine");
        yield return new TestCaseData("    a\n    b\n\n    a\n    b\n", "a\nb").SetName("Repeated_MultiLineWindow");
        yield return new TestCaseData("aaaa", "aa").SetName("Overlapping_Ordinal");
        yield return new TestCaseData("    return foo;\n}\nother();\n", "return foo;\n}\n").SetName("MultiLine_TrailingNewlineInSnippet");
        yield return new TestCaseData("int x = 1;\n", "   ").SetName("WhitespaceOnlySnippet");
        yield return new TestCaseData("int x = 1;\n", "").SetName("EmptySnippet");
        yield return new TestCaseData("int x = 1;\n", "nothing like this").SetName("NoMatch");
        yield return new TestCaseData("foo();   \nbar();  \n", "foo();\nbar();").SetName("MultiLine_TrailingWhitespaceInFile");
        yield return new TestCaseData("foo();\nbar();\n", "foo();  \nbar();  ").SetName("MultiLine_TrailingWhitespaceInSnippet");
        yield return new TestCaseData("    a\n\n    b\n", "a\n\nb").SetName("MultiLine_BlankInteriorLine");
        yield return new TestCaseData("a b\n  a\n  b\n", "a\nb").SetName("MultiLine_CollapsedLineAndIndentedWindow");
        yield return new TestCaseData("if (a) {\n  b();\n} else {\n", "{\n  b();\n}").SetName("MultiLine_MidLineStartAndEnd");
        yield return new TestCaseData("\n  \n    a\n    b\n  \n", "\n\na\nb\n\n").SetName("MultiLine_BlankLeadingAndTrailingSnippetLines");
    }

    private static string Collapse(string s) => Regex.Replace(s.Trim(), @"\s+", " ");

    private static int OneBasedLine(string text, int start) => 1 + text.Take(start).Count(c => c == '\n');

    /// <summary>Line endings normalised, every line trimmed at both ends.</summary>
    private static string TrimLines(string s) => string.Join("\n", Lf(s).Split('\n').Select(l => l.Trim()));

    /// <summary>The snippet as the indentation matcher sees it: fully blank leading and trailing lines dropped, then <see cref="TrimLines"/>.</summary>
    private static string SnippetContentTrimmed(string snippet)
        => TrimLines(string.Join("\n", Lf(snippet).Split('\n')
            .SkipWhile(string.IsNullOrWhiteSpace).Reverse().SkipWhile(string.IsNullOrWhiteSpace).Reverse()));

    // ---- (2) splice invariant, per matcher ----

    [TestCaseSource(nameof(Corpus))]
    public void EveryMatcher_ReportsTheTrueSpan_SpliceReproducesTheFile(string text, string snippet)
    {
        var sourceText = SourceText.From(text);

        foreach (var matcher in Matchers)
        {
            var caseModes = matcher.Tier == WhitespaceTolerance.IgnoreAllWhitespace
                ? new[] { SnippetCaseMode.CaseSensitive, SnippetCaseMode.CaseInsensitive }
                : new[] { SnippetCaseMode.CaseSensitive };

            foreach (var caseMode in caseModes)
            {
                var label = $"{matcher.Name}/{caseMode}";
                var comparison = caseMode == SnippetCaseMode.CaseInsensitive ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
                foreach (var match in matcher.FindAll(sourceText, snippet, caseMode))
                {
                    Assert.Multiple(() =>
                    {
                        Assert.That(match.Start, Is.GreaterThanOrEqualTo(0), label);
                        Assert.That(match.Length, Is.GreaterThan(0), label);
                        Assert.That(match.Start + match.Length, Is.LessThanOrEqualTo(text.Length), label);
                        Assert.That(match.Tier, Is.EqualTo(matcher.Tier), label);
                        Assert.That(match.MatcherName, Is.EqualTo(matcher.Name), label);
                        Assert.That(match.Line, Is.EqualTo(OneBasedLine(text, match.Start)), label + " line");

                        var matched = text.Substring(match.Start, match.Length);
                        var spliced = text.Remove(match.Start, match.Length).Insert(match.Start, matched);
                        Assert.That(spliced, Is.EqualTo(text), label + " splice");

                        // Length is the REAL matched length: the matched text must equal the snippet at this tier.
                        switch (matcher.Tier)
                        {
                            case WhitespaceTolerance.Exact:
                                Assert.That(matched, Is.EqualTo(snippet), label + " exact text");
                                break;
                            case WhitespaceTolerance.IgnoreLineEndings:
                                Assert.That(Lf(matched), Is.EqualTo(Lf(snippet)), label + " line-ending text");
                                break;
                            case WhitespaceTolerance.IgnoreIndentation:
                                Assert.That(TrimLines(matched), Is.EqualTo(SnippetContentTrimmed(snippet)), label + " indentation text");
                                break;
                            default:
                                Assert.That(Collapse(matched).Equals(Collapse(snippet), comparison), Is.True,
                                    $"{label} collapsed text: matched '{matched}' vs snippet '{snippet}'");
                                break;
                        }
                    });
                }
            }
        }
    }

    [Test]
    public void CollapsedMatcher_InteriorWhitespaceDifference_LengthIsRealSpanNotSnippetLength()
    {
        const string text = "var r = a  +  b;\n";

        var match = new CollapsedWhitespaceMatcher().FindAll(SourceText.From(text), "a + b;", SnippetCaseMode.CaseSensitive).Single();

        Assert.That(text.Substring(match.Start, match.Length), Is.EqualTo("a  +  b;"));
        Assert.That(match.Length, Is.Not.EqualTo("a + b;".Length));
    }

    [Test]
    public void CollapsedMatcher_WindowSpan_StartsAtLineStartAndEndsAtLastLineEndWithoutLineBreak()
    {
        const string text = "x\r\n        }\r\n        var trailingIssue = x;\r\ny\r\n";

        var match = new CollapsedWhitespaceMatcher()
            .FindAll(SourceText.From(text), "}\nvar trailingIssue = x;", SnippetCaseMode.CaseSensitive).Single();

        Assert.That(text.Substring(match.Start, match.Length), Is.EqualTo("        }\r\n        var trailingIssue = x;"));
        Assert.That(match.Line, Is.EqualTo(2));
    }

    // ---- stage purity ----

    [Test]
    public void ExactMatcher_DoesNotAcceptLineEndingDifference_ButLineEndingMatcherDoes()
    {
        var sourceText = SourceText.From("a\r\nb\r\nc\r\n");

        Assert.That(new ExactMatcher().FindAll(sourceText, "a\nb", SnippetCaseMode.CaseSensitive), Is.Empty);
        var le = new LineEndingMatcher().FindAll(sourceText, "a\nb", SnippetCaseMode.CaseSensitive);
        Assert.That(le, Has.Count.EqualTo(1));
        Assert.That(le[0].Length, Is.EqualTo(4), "a CRLF b is 4 chars, not the 3 of the LF snippet");
    }

    [Test]
    public void LineEndingMatcher_SingleLineSnippet_YieldsNothing()
    {
        Assert.That(new LineEndingMatcher().FindAll(SourceText.From("int x = 1;\n"), "int x = 1;", SnippetCaseMode.CaseSensitive), Is.Empty);
    }

    [Test]
    public void ExactAndLineEndingMatchers_DoNotAcceptDroppedIndentation()
    {
        var sourceText = SourceText.From("    a\n    b\n");

        Assert.That(new ExactMatcher().FindAll(sourceText, "a\nb", SnippetCaseMode.CaseSensitive), Is.Empty);
        Assert.That(new LineEndingMatcher().FindAll(sourceText, "a\nb", SnippetCaseMode.CaseSensitive), Is.Empty);
    }

    [Test]
    public void ExactAndLineEndingMatchers_CaseInsensitiveRequest_ThrowsArgumentException()
    {
        var sourceText = SourceText.From("Foo();\n");

        Assert.Throws<ArgumentException>(() => new ExactMatcher().FindAll(sourceText, "foo();", SnippetCaseMode.CaseInsensitive));
        Assert.Throws<ArgumentException>(() => new LineEndingMatcher().FindAll(sourceText, "foo();", SnippetCaseMode.CaseInsensitive));
    }

    [Test]
    public void CollapsedMatcher_HonoursCaseMode()
    {
        var sourceText = SourceText.From("Console.WriteLine(X);\n");

        var matcher = new CollapsedWhitespaceMatcher();

        Assert.That(matcher.FindAll(sourceText, "console.writeline(x);", SnippetCaseMode.CaseSensitive), Is.Empty);
        Assert.That(matcher.FindAll(sourceText, "console.writeline(x);", SnippetCaseMode.CaseInsensitive), Has.Count.EqualTo(1));
    }

    [Test]
    public void CollapsedMatcher_WindowStageRunsOnlyWhenSingleLineStageFoundNothing()
    {
        // Single-line stage matches the one line "a b" for the two-line snippet "a\nb"; the window stage
        // (which would also match the two-line block below) must not add its match, as in the legacy code.
        var sourceText = SourceText.From("a b\na\nb\n");

        var matches = new CollapsedWhitespaceMatcher().FindAll(sourceText, "a\nb", SnippetCaseMode.CaseSensitive);

        Assert.That(matches, Has.Count.EqualTo(1));
        Assert.That(matches[0].Start, Is.EqualTo(0));
    }

    [Test]
    public void EveryMatcher_BlankSnippet_ReturnsNoMatchesAndDoesNotThrow()
    {
        var sourceText = SourceText.From("int x = 1;\n");

        foreach (var matcher in Matchers)
        {
            Assert.That(matcher.FindAll(sourceText, "   ", SnippetCaseMode.CaseSensitive), Is.Empty, matcher.Name);
            Assert.That(matcher.FindAll(sourceText, string.Empty, SnippetCaseMode.CaseSensitive), Is.Empty, matcher.Name);
        }
    }

    // ---- IndentationMatcher (slice 2d) ----

    [Test]
    public void IndentationMatcher_QwenSnippet_SpanStartsAfterFirstLineIndentation_AndEndsAtLastFragment()
    {
        const string text = "x\r\n        }\r\n        var trailingIssue = x;\r\ny\r\n";

        var match = new IndentationMatcher().FindAll(SourceText.From(text), "}\nvar trailingIssue = x;", SnippetCaseMode.CaseSensitive).Single();

        Assert.Multiple(() =>
        {
            Assert.That(text.Substring(match.Start, match.Length), Is.EqualTo("}\r\n        var trailingIssue = x;"));
            Assert.That(match.Start, Is.EqualTo(text.IndexOf('}')), "starts after the first line's indentation");
            Assert.That(match.Line, Is.EqualTo(2));
            Assert.That(match.Tier, Is.EqualTo(WhitespaceTolerance.IgnoreIndentation));
            Assert.That(match.MatcherName, Is.EqualTo(nameof(IndentationMatcher)));
        });
    }

    [TestCase("foo();   \nbar();  \n", "foo();\nbar();", "foo();   \nbar();", TestName = "Trailing_FileHasItSnippetLacks")]
    [TestCase("foo();\nbar();\n", "foo();   \nbar();  ", "foo();\nbar();", TestName = "Trailing_SnippetHasItFileLacks")]
    [TestCase("x = 1;\n", "  x = 1;  ", "x = 1;", TestName = "Trailing_SingleLineSnippetPadded")]
    [TestCase("\tx = 1;\t\n", "\t\tx = 1;", "x = 1;", TestName = "Tabs_SingleLineSnippetIndentedDeeper")]
    public void IndentationMatcher_TrailingWhitespace_MatchesWhereExactAndLineEndingDoNot(string text, string snippet, string expectedMatchedText)
    {
        var sourceText = SourceText.From(text);

        var matches = new IndentationMatcher().FindAll(sourceText, snippet, SnippetCaseMode.CaseSensitive);

        Assert.Multiple(() =>
        {
            Assert.That(new ExactMatcher().FindAll(sourceText, snippet, SnippetCaseMode.CaseSensitive), Is.Empty, "Exact");
            Assert.That(new LineEndingMatcher().FindAll(sourceText, snippet, SnippetCaseMode.CaseSensitive), Is.Empty, "LineEnding");
            Assert.That(matches, Has.Count.EqualTo(1), "Indentation");
            Assert.That(text.Substring(matches[0].Start, matches[0].Length), Is.EqualTo(expectedMatchedText));
        });
    }

    [TestCase("var r = a  +  b;\n", "a + b;", TestName = "Interior_SingleLine")]
    [TestCase("    a  b\n    c\n", "a b\nc", TestName = "Interior_FirstLineOfWindow")]
    [TestCase("x\n  a  +  b\ny\n", "x\na + b\ny", TestName = "Interior_InteriorWindowLine")]
    [TestCase("x\ty\n", "x y", TestName = "Interior_TabVersusSpace")]
    public void IndentationMatcher_InteriorWhitespace_IsComparedAsWritten_CollapsedMatcherAcceptsIt(string text, string snippet)
    {
        var sourceText = SourceText.From(text);

        Assert.Multiple(() =>
        {
            Assert.That(new IndentationMatcher().FindAll(sourceText, snippet, SnippetCaseMode.CaseSensitive), Is.Empty, "Indentation");
            Assert.That(new CollapsedWhitespaceMatcher().FindAll(sourceText, snippet, SnippetCaseMode.CaseSensitive), Has.Count.EqualTo(1), "Collapsed");
        });
    }

    [TestCase("if (a) return x;\n", "return x;", TestName = "MidLine_SingleLine")]
    [TestCase("if (a) {\n  b();\n} else {\n", "{\n  b();\n}", TestName = "MidLine_MultiLineStartAndEnd")]
    [TestCase("if (a) {\r\n  b();\r\n} else {\r\n", "{\n  b();\n}", TestName = "MidLine_MultiLineStartAndEnd_CrlfFile")]
    [TestCase("a;b;c;\n", "b;c;", TestName = "MidLine_SingleLineTwoTokens")]
    [TestCase("foo(); foo();\n", "foo();", TestName = "MidLine_TwoOccurrencesOnOneLine")]
    public void IndentationMatcher_MidLineFragment_HasTheSameStartAndLengthAsTheLowerTier(string text, string snippet)
    {
        var sourceText = SourceText.From(text);
        var lower = new ExactMatcher().FindAll(sourceText, snippet, SnippetCaseMode.CaseSensitive);
        if (lower.Count == 0)
        {
            lower = new LineEndingMatcher().FindAll(sourceText, snippet, SnippetCaseMode.CaseSensitive);
        }

        var indentation = new IndentationMatcher().FindAll(sourceText, snippet, SnippetCaseMode.CaseSensitive);

        Assert.That(lower, Is.Not.Empty, "fixture must match at a lower tier");
        Assert.That(indentation.Select(m => (m.Start, m.Length)), Is.EqualTo(lower.Select(m => (m.Start, m.Length))));
    }

    [Test]
    public void IndentationMatcher_BlankLeadingAndTrailingSnippetLines_AreDropped()
    {
        const string text = "    a\n    b\n";

        var match = new IndentationMatcher().FindAll(SourceText.From(text), "\n\na\nb\n\n", SnippetCaseMode.CaseSensitive).Single();

        Assert.That(text.Substring(match.Start, match.Length), Is.EqualTo("a\n    b"));
    }

    [TestCase("a\n\nb\n", 1, TestName = "BlankInterior_EmptySourceLine")]
    [TestCase("a\n   \nb\n", 1, TestName = "BlankInterior_WhitespaceOnlySourceLine")]
    [TestCase("a\nx\nb\n", 0, TestName = "BlankInterior_NonBlankSourceLine")]
    [TestCase("a\nb\n", 0, TestName = "BlankInterior_NoSourceLineAtAll")]
    public void IndentationMatcher_BlankInteriorSnippetLine_MatchesOnlyAWhitespaceOnlySourceLine(string text, int expectedCount)
    {
        var matches = new IndentationMatcher().FindAll(SourceText.From(text), "a\n\nb", SnippetCaseMode.CaseSensitive);

        Assert.That(matches, Has.Count.EqualTo(expectedCount));
    }

    [Test]
    public void IndentationMatcher_WhitespaceOnlyOrEmptySnippet_MatchesNothing()
    {
        var sourceText = SourceText.From("  \n\t\nx\n");

        foreach (var snippet in new[] { string.Empty, " ", "\n", "  \n\t\n", "\r\n" })
        {
            Assert.That(new IndentationMatcher().FindAll(sourceText, snippet, SnippetCaseMode.CaseSensitive), Is.Empty, $"'{snippet}'");
        }
    }

    [Test]
    public void IndentationMatcher_CrlfFileLfSnippet_ReportsTheRealSpanIncludingTheCrlf()
    {
        const string text = "    a\r\n    b\r\n";

        var match = new IndentationMatcher().FindAll(SourceText.From(text), "a\nb", SnippetCaseMode.CaseSensitive).Single();

        Assert.Multiple(() =>
        {
            Assert.That(match.Start, Is.EqualTo(4));
            Assert.That(text.Substring(match.Start, match.Length), Is.EqualTo("a\r\n    b"));
            Assert.That(match.Length, Is.Not.EqualTo("a\nb".Length));
        });
    }

    [Test]
    public void IndentationMatcher_OverlappingAndRepeatedStarts_AreAllReportedInSourceOrder()
    {
        var windows = new IndentationMatcher().FindAll(SourceText.From("a\na\na\n"), "a\na", SnippetCaseMode.CaseSensitive);
        var ordinal = new IndentationMatcher().FindAll(SourceText.From("aaaa"), "aa", SnippetCaseMode.CaseSensitive);

        Assert.That(windows.Select(m => m.Start), Is.EqualTo(new[] { 0, 2 }), "windows starting at lines 1 and 2");
        Assert.That(ordinal.Select(m => m.Start), Is.EqualTo(new[] { 0, 1, 2 }));
    }

    [Test]
    public void IndentationMatcher_CaseInsensitiveRequest_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() =>
            new IndentationMatcher().FindAll(SourceText.From("Foo();\n"), "foo();", SnippetCaseMode.CaseInsensitive));
    }

    [Test]
    public void IndentationMatcher_IsCaseSensitive()
    {
        Assert.That(new IndentationMatcher().FindAll(SourceText.From("    Foo();\n"), "foo();", SnippetCaseMode.CaseSensitive), Is.Empty);
    }

    // ---- (7) matrix: every matcher against every fixture ----

    // Fixture, then whether (Exact, LineEnding, Indentation, Collapsed) matchers find it.
    public static IEnumerable<TestCaseData> MatcherMatrix()
    {
        yield return new TestCaseData("int x = 1;\nint y = 2;\n", "int y = 2;", true, false, true, true).SetName("Matrix_SingleLineVerbatim");
        yield return new TestCaseData("a\nb\nc\n", "a\nb", true, true, true, true).SetName("Matrix_MultiLineVerbatim");
        yield return new TestCaseData("a\r\nb\r\nc\r\n", "a\nb", false, true, true, true).SetName("Matrix_LineEndingDifference");
        yield return new TestCaseData("    a\r\n    b\r\n", "a\nb", false, false, true, true).SetName("Matrix_IndentationDifference");
        yield return new TestCaseData("foo();   \nbar();  \n", "foo();\nbar();", false, false, true, true).SetName("Matrix_TrailingWhitespaceDifference");
        yield return new TestCaseData("var r = a  +  b;\n", "a + b;", false, false, false, true).SetName("Matrix_InteriorWhitespaceDifference");
        yield return new TestCaseData("int x = 1;\n", "nothing like this", false, false, false, false).SetName("Matrix_NoMatch");
    }

    [TestCaseSource(nameof(MatcherMatrix))]
    public void Matrix_EveryMatcherAgainstEveryFixture(string text, string snippet, bool exact, bool lineEnding, bool indentation, bool collapsed)
    {
        var sourceText = SourceText.From(text);
        var expected = new[] { exact, lineEnding, indentation, collapsed };

        for (var i = 0; i < Matchers.Length; i++)
        {
            var found = Matchers[i].FindAll(sourceText, snippet, SnippetCaseMode.CaseSensitive).Count > 0;
            Assert.That(found, Is.EqualTo(expected[i]), Matchers[i].Name);
        }
    }

    [Test]
    public void Matchers_DeclareTheirTierInLadderOrder()
    {
        Assert.That(Matchers.Select(m => m.Tier), Is.EqualTo(new[]
        {
            WhitespaceTolerance.Exact, WhitespaceTolerance.IgnoreLineEndings, WhitespaceTolerance.IgnoreIndentation,
            WhitespaceTolerance.IgnoreAllWhitespace,
        }));
        Assert.That(Matchers.Select(m => m.Name).Distinct().Count(), Is.EqualTo(Matchers.Length));
    }
}
