using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis.Text;
using RoslynSentinel.Common.SnippetMatching;

namespace RoslynSentinel.Tests;

/// <summary>
/// Tests for the three stage-pure matchers (slice 2b of the whitespace-tolerance proposal): the splice
/// invariant (the 2026-09-10 corruption regression), stage purity, and the matcher-by-fixture matrix.
/// </summary>
[Parallelizable(ParallelScope.All)]
public class SnippetMatcherTests
{
    private static readonly ISnippetMatcher[] Matchers =
    {
        new ExactMatcher(),
        new LineEndingMatcher(),
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
    }

    private static string NormalizeEol(string s) => s.Replace("\r\n", "\n").Replace("\r", "\n");

    private static string Collapse(string s) => Regex.Replace(s.Trim(), @"\s+", " ");

    private static int OneBasedLine(string text, int start) => 1 + text.Take(start).Count(c => c == '\n');

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
                                Assert.That(NormalizeEol(matched), Is.EqualTo(NormalizeEol(snippet)), label + " line-ending text");
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

    // ---- (7) matrix: every matcher against every fixture ----

    // Fixture, then whether (Exact, LineEnding, Collapsed) matchers find it.
    public static IEnumerable<TestCaseData> MatcherMatrix()
    {
        yield return new TestCaseData("int x = 1;\nint y = 2;\n", "int y = 2;", true, false, true).SetName("Matrix_SingleLineVerbatim");
        yield return new TestCaseData("a\nb\nc\n", "a\nb", true, true, true).SetName("Matrix_MultiLineVerbatim");
        yield return new TestCaseData("a\r\nb\r\nc\r\n", "a\nb", false, true, true).SetName("Matrix_LineEndingDifference");
        yield return new TestCaseData("    a\r\n    b\r\n", "a\nb", false, false, true).SetName("Matrix_IndentationDifference");
        yield return new TestCaseData("var r = a  +  b;\n", "a + b;", false, false, true).SetName("Matrix_InteriorWhitespaceDifference");
        yield return new TestCaseData("int x = 1;\n", "nothing like this", false, false, false).SetName("Matrix_NoMatch");
    }

    [TestCaseSource(nameof(MatcherMatrix))]
    public void Matrix_EveryMatcherAgainstEveryFixture(string text, string snippet, bool exact, bool lineEnding, bool collapsed)
    {
        var sourceText = SourceText.From(text);
        var expected = new[] { exact, lineEnding, collapsed };

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
            WhitespaceTolerance.Exact, WhitespaceTolerance.IgnoreLineEndings, WhitespaceTolerance.IgnoreAllWhitespace,
        }));
        Assert.That(Matchers.Select(m => m.Name).Distinct().Count(), Is.EqualTo(Matchers.Length));
    }
}
