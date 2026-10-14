using Microsoft.CodeAnalysis.Text;
using RoslynSentinel.Common.SnippetMatching;

namespace RoslynSentinel.Tests;

/// <summary>
/// Tests for the <see cref="SnippetResolver"/> dispatcher (slice 2b of the whitespace-tolerance proposal): parity
/// with the legacy <see cref="ContextHelper"/> helpers, the cap, the near-match probe and the programming-error guards.
/// Test methods take tier values as <c>int</c> because <see cref="WhitespaceTolerance"/> is internal and an NUnit
/// test method must be public.
/// </summary>
[Parallelizable(ParallelScope.All)]
public class SnippetResolverTests
{
    private static SnippetMatchResult Resolve(string text, string snippet, WhitespaceTolerance tolerance, SnippetCaseMode caseMode = SnippetCaseMode.CaseSensitive)
        => SnippetResolver.Resolve(SourceText.From(text), snippet, tolerance, caseMode);

    // ---- (1) parity with the legacy helpers over the shared corpus ----

    [TestCaseSource(typeof(SnippetMatcherTests), nameof(SnippetMatcherTests.Corpus))]
    public void Parity_IgnoreAllWhitespaceCaseInsensitive_EqualsFindAllSnippetMatchesWithLength(string text, string snippet)
    {
        var sourceText = SourceText.From(text);

        var legacy = ContextHelper.FindAllSnippetMatchesWithLength(sourceText, snippet);
        var result = SnippetResolver.Resolve(sourceText, snippet, WhitespaceTolerance.IgnoreAllWhitespace, SnippetCaseMode.CaseInsensitive);

        Assert.That(result.Matches.Select(m => (m.Start, m.Length)), Is.EqualTo(legacy.Select(m => (m.Start, m.Length))));
        Assert.That(result.NearMatches, Is.Empty, "the loosest tier has nothing looser to probe");
        AssertOutcomeMatchesCount(result);
    }

    [TestCaseSource(typeof(SnippetMatcherTests), nameof(SnippetMatcherTests.Corpus))]
    public void Parity_IgnoreLineEndingsCaseSensitive_EqualsFindAllExactSnippetMatches(string text, string snippet)
    {
        var sourceText = SourceText.From(text);

        var legacy = ContextHelper.FindAllExactSnippetMatches(sourceText, snippet);
        var result = SnippetResolver.Resolve(sourceText, snippet, WhitespaceTolerance.IgnoreLineEndings, SnippetCaseMode.CaseSensitive);

        Assert.That(result.Matches.Select(m => (m.Start, m.Length)), Is.EqualTo(legacy.Select(m => (m.Start, m.Length))));
        if (legacy.Count > 0)
        {
            AssertOutcomeMatchesCount(result);
        }
    }

    private static void AssertOutcomeMatchesCount(SnippetMatchResult result)
    {
        switch (result.Matches.Count)
        {
            case 0:
                Assert.That(result.Outcome, Is.EqualTo(SnippetMatchOutcome.NoMatch));
                break;
            case 1:
                Assert.That(result.Outcome, Is.EqualTo(SnippetMatchOutcome.Matched));
                break;
            default:
                Assert.That(result.Outcome, Is.EqualTo(SnippetMatchOutcome.Ambiguous));
                break;
        }
    }

    // ---- outcomes ----

    [Test]
    public void Resolve_OneMatch_IsMatched_WithRealSpanLineAndTier()
    {
        var result = Resolve("int x = 1;\nint y = 2;\n", "int y = 2;", WhitespaceTolerance.Exact);

        Assert.Multiple(() =>
        {
            Assert.That(result.Outcome, Is.EqualTo(SnippetMatchOutcome.Matched));
            Assert.That(result.IsSingleMatch, Is.True);
            Assert.That(result.Single().Start, Is.EqualTo(11));
            Assert.That(result.Single().Line, Is.EqualTo(2));
            Assert.That(result.Single().Length, Is.EqualTo(10));
            Assert.That(result.Single().Tier, Is.EqualTo(WhitespaceTolerance.Exact));
            Assert.That(result.Single().MatcherName, Is.EqualTo(nameof(ExactMatcher)));
            Assert.That(result.RequestedTolerance, Is.EqualTo(WhitespaceTolerance.Exact));
            Assert.That(result.RequestedCaseMode, Is.EqualTo(SnippetCaseMode.CaseSensitive));
            Assert.That(result.CandidatesBeforeSelectors, Is.EqualTo(1));
        });
    }

    [Test]
    public void Resolve_SeveralMatches_IsAmbiguous_WithAllOfThem()
    {
        var result = Resolve("foo();\nfoo();\nfoo();\n", "foo();", WhitespaceTolerance.Exact);

        Assert.That(result.Outcome, Is.EqualTo(SnippetMatchOutcome.Ambiguous));
        Assert.That(result.Matches.Select(m => m.Line), Is.EqualTo(new[] { 1, 2, 3 }));
        Assert.That(result.CandidatesAfterAdjacentLines, Is.EqualTo(3));
    }

    [Test]
    public void Resolve_NoMatchAtAnyTier_IsNoMatch_WithEmptyLists()
    {
        var result = Resolve("int x = 1;\n", "nothing like this", WhitespaceTolerance.Exact);

        Assert.Multiple(() =>
        {
            Assert.That(result.Outcome, Is.EqualTo(SnippetMatchOutcome.NoMatch));
            Assert.That(result.Matches, Is.Empty);
            Assert.That(result.NearMatches, Is.Empty);
            Assert.That(result.ClosestWindows, Is.Empty);
        });
    }

    [TestCase(0)]
    [TestCase(1)]
    [TestCase(3)]
    public void Resolve_WhitespaceOnlyOrEmptySnippet_IsNoMatch_AndDoesNotThrow(int toleranceValue)
    {
        var tolerance = (WhitespaceTolerance)toleranceValue;

        foreach (var snippet in new[] { "", "   ", "\n\t \r\n" })
        {
            var result = Resolve("int x = 1;\n", snippet, tolerance);

            Assert.That(result.Outcome, Is.EqualTo(SnippetMatchOutcome.NoMatch), $"'{snippet}'");
            Assert.That(result.Matches, Is.Empty);
        }
    }

    [Test]
    public void Resolve_CaseInsensitiveIgnoreAllWhitespace_MatchesDifferentCase_CaseSensitiveDoesNot()
    {
        var insensitive = Resolve("Console.WriteLine(X);\n", "console.writeline(x);", WhitespaceTolerance.IgnoreAllWhitespace, SnippetCaseMode.CaseInsensitive);
        var sensitive = Resolve("Console.WriteLine(X);\n", "console.writeline(x);", WhitespaceTolerance.IgnoreAllWhitespace, SnippetCaseMode.CaseSensitive);

        Assert.That(insensitive.Outcome, Is.EqualTo(SnippetMatchOutcome.Matched));
        Assert.That(sensitive.Outcome, Is.EqualTo(SnippetMatchOutcome.NoMatch));
    }

    // ---- (2) splice invariant through the dispatcher ----

    [TestCaseSource(typeof(SnippetMatcherTests), nameof(SnippetMatcherTests.Corpus))]
    public void Resolve_EveryAcceptedMatch_SpliceReproducesTheFile(string text, string snippet)
    {
        foreach (var tolerance in new[] { WhitespaceTolerance.Exact, WhitespaceTolerance.IgnoreLineEndings, WhitespaceTolerance.IgnoreAllWhitespace })
        {
            var result = Resolve(text, snippet, tolerance);
            foreach (var match in result.Matches)
            {
                var matched = text.Substring(match.Start, match.Length);
                Assert.That(text.Remove(match.Start, match.Length).Insert(match.Start, matched), Is.EqualTo(text));
                Assert.That(match.Tier, Is.LessThanOrEqualTo(tolerance));
            }
        }
    }

    // ---- (3) the cap ----

    [Test]
    public void Cap_ExactRequest_NeverReturnsALooserMatch_EvenWhenOneExistsElsewhere()
    {
        // The verbatim block is at the top; the indented block further down matches only at a looser tier.
        const string text = "a\nb\nmid();\n    a\n    b\n";

        var exact = Resolve(text, "a\nb", WhitespaceTolerance.Exact);

        Assert.That(exact.Outcome, Is.EqualTo(SnippetMatchOutcome.Matched));
        Assert.That(exact.Matches.Select(m => (m.Start, m.Tier)), Is.EqualTo(new[] { (0, WhitespaceTolerance.Exact) }));
        Assert.That(exact.NearMatches, Is.Empty);
    }

    [Test]
    public void Cap_ExactRequest_WhereOnlyALooserTierMatches_ReturnsNearMatchesNotMatches()
    {
        var result = Resolve("a\r\nb\r\nc\r\n", "a\nb", WhitespaceTolerance.Exact);

        Assert.Multiple(() =>
        {
            Assert.That(result.Outcome, Is.EqualTo(SnippetMatchOutcome.MatchedLooserThanRequested));
            Assert.That(result.Matches, Is.Empty);
            Assert.That(result.IsSingleMatch, Is.False);
            Assert.That(result.NearMatches.Single().LowestTier, Is.EqualTo(WhitespaceTolerance.IgnoreLineEndings));
        });
    }

    [Test]
    public void Cap_IgnoreLineEndingsRequest_DoesNotReachTheCollapsedTier()
    {
        var result = Resolve("    a\n    b\n", "a\nb", WhitespaceTolerance.IgnoreLineEndings);

        Assert.That(result.Outcome, Is.EqualTo(SnippetMatchOutcome.MatchedLooserThanRequested));
        Assert.That(result.Matches, Is.Empty);
        Assert.That(result.NearMatches.Single().LowestTier, Is.EqualTo(WhitespaceTolerance.IgnoreAllWhitespace));
    }

    // ---- (4) the Qwen regression ----

    [Test]
    public void QwenRegression_SnippetAtColumnZeroAgainstEightSpaces_ExactGivesNearMatchWithTheFilesIndentedLines()
    {
        const string text = "class C\n{\n    void M()\n    {\n        if (a)\n        {\n            y();\n        }\n        var trailingIssue = x;\n    }\n}\n";
        const string snippet = "}\nvar trailingIssue = x;";

        var result = Resolve(text, snippet, WhitespaceTolerance.Exact);

        Assert.Multiple(() =>
        {
            Assert.That(result.Outcome, Is.EqualTo(SnippetMatchOutcome.MatchedLooserThanRequested));
            Assert.That(result.Matches, Is.Empty);
            Assert.That(result.NearMatches, Has.Count.EqualTo(1));
            Assert.That(result.NearMatchTotalCount, Is.EqualTo(1));
            Assert.That(result.NearMatches[0].ActualLines, Is.EqualTo(new[] { "        }", "        var trailingIssue = x;" }));
            Assert.That(result.NearMatches[0].StartLine, Is.EqualTo(8));
            Assert.That(result.NearMatches[0].Differences, Is.Empty, "structured differences arrive in slice 3");
            // IgnoreAllWhitespace for now because no IndentationMatcher exists; slice 2d lowers this to IgnoreIndentation.
            Assert.That(result.NearMatches[0].LowestTier, Is.EqualTo(WhitespaceTolerance.IgnoreAllWhitespace));
        });
    }

    [Test]
    public void QwenRegression_SendingTheNearMatchBack_SucceedsAtExact()
    {
        const string text = "    {\n        }\n        var trailingIssue = x;\n    }\n";
        var near = Resolve(text, "}\nvar trailingIssue = x;", WhitespaceTolerance.Exact).NearMatches.Single();

        var retry = Resolve(text, string.Join("\n", near.ActualLines), WhitespaceTolerance.Exact);

        Assert.That(retry.Outcome, Is.EqualTo(SnippetMatchOutcome.Matched));
    }

    // ---- (5) near-match cap ----

    [Test]
    public void NearMatches_AreCappedAtFive_WithTheTotalCount()
    {
        var text = string.Concat(Enumerable.Repeat("    x\n    y\n\n", 7));

        var result = Resolve(text, "x\ny", WhitespaceTolerance.Exact);

        Assert.Multiple(() =>
        {
            Assert.That(result.Outcome, Is.EqualTo(SnippetMatchOutcome.MatchedLooserThanRequested));
            Assert.That(result.NearMatches, Has.Count.EqualTo(5));
            Assert.That(result.NearMatchTotalCount, Is.EqualTo(7));
            Assert.That(result.NearMatches.Select(n => n.StartLine), Is.EqualTo(new[] { 1, 4, 7, 10, 13 }), "file order, first five");
        });
    }

    [Test]
    public void NearMatches_BelowTheCap_AreAllListed()
    {
        var text = string.Concat(Enumerable.Repeat("    x\n    y\n\n", 2));

        var result = Resolve(text, "x\ny", WhitespaceTolerance.Exact);

        Assert.That(result.NearMatches, Has.Count.EqualTo(2));
        Assert.That(result.NearMatchTotalCount, Is.EqualTo(2));
    }

    // ---- (6) programming errors ----

    [Test]
    public void Resolve_IgnoreIndentation_ThrowsArgumentException_NamingSlice2d()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            Resolve("a\n", "a", WhitespaceTolerance.IgnoreIndentation));

        Assert.That(ex!.Message, Does.Contain("IgnoreIndentation"));
        Assert.That(ex.Message, Does.Contain("2d"));
    }

    [TestCase(0)]
    [TestCase(1)]
    public void Resolve_CaseInsensitiveBelowIgnoreAllWhitespace_ThrowsArgumentException(int toleranceValue)
    {
        var tolerance = (WhitespaceTolerance)toleranceValue;

        var ex = Assert.Throws<ArgumentException>(() =>
            Resolve("a\n", "a", tolerance, SnippetCaseMode.CaseInsensitive));

        Assert.That(ex!.Message, Does.Contain("CaseInsensitive"));
        Assert.That(ex.Message, Does.Contain("IgnoreAllWhitespace"));
    }

    [Test]
    public void Resolve_UndefinedEnumValues_ThrowArgumentException()
    {
        Assert.Throws<ArgumentException>(() => Resolve("a\n", "a", (WhitespaceTolerance)99));
        Assert.Throws<ArgumentException>(() => Resolve("a\n", "a", WhitespaceTolerance.Exact, (SnippetCaseMode)99));
    }

    [Test]
    public void Resolve_NullArguments_Throw()
    {
        Assert.Throws<ArgumentNullException>(() => SnippetResolver.Resolve(null!, "a", WhitespaceTolerance.Exact, SnippetCaseMode.CaseSensitive));
        Assert.Throws<ArgumentNullException>(() => SnippetResolver.Resolve(SourceText.From("a"), null!, WhitespaceTolerance.Exact, SnippetCaseMode.CaseSensitive));
    }

    // ---- (7) matrix: every fixture against every tier request ----

    // Fixture text, snippet, and the lowest tier (as its int value) at which it matches.
    public static IEnumerable<TestCaseData> TierFixtures()
    {
        yield return new TestCaseData("int x = 1;\nint y = 2;\n", "int y = 2;", (int)WhitespaceTolerance.Exact).SetName("Fixture_SingleLineVerbatim");
        yield return new TestCaseData("a\nb\nc\n", "a\nb", (int)WhitespaceTolerance.Exact).SetName("Fixture_MultiLineVerbatim");
        yield return new TestCaseData("a\r\nb\r\nc\r\n", "a\nb", (int)WhitespaceTolerance.IgnoreLineEndings).SetName("Fixture_LineEndingDifference");
        yield return new TestCaseData("    a\r\n    b\r\n", "a\nb", (int)WhitespaceTolerance.IgnoreAllWhitespace).SetName("Fixture_IndentationDifference");
        yield return new TestCaseData("var r = a  +  b;\n", "a + b;", (int)WhitespaceTolerance.IgnoreAllWhitespace).SetName("Fixture_InteriorWhitespaceDifference");
    }

    [TestCaseSource(nameof(TierFixtures))]
    public void Matrix_EveryFixtureAgainstEveryTierRequest(string text, string snippet, int lowestTierValue)
    {
        var lowestTier = (WhitespaceTolerance)lowestTierValue;

        foreach (var requested in new[] { WhitespaceTolerance.Exact, WhitespaceTolerance.IgnoreLineEndings, WhitespaceTolerance.IgnoreAllWhitespace })
        {
            var result = Resolve(text, snippet, requested);
            var label = $"requested {requested}";

            if (requested >= lowestTier)
            {
                Assert.That(result.Outcome, Is.EqualTo(SnippetMatchOutcome.Matched), label);
                Assert.That(result.Single().Tier, Is.EqualTo(lowestTier), label);
                Assert.That(result.NearMatches, Is.Empty, label);
            }
            else
            {
                Assert.That(result.Outcome, Is.EqualTo(SnippetMatchOutcome.MatchedLooserThanRequested), label);
                Assert.That(result.Matches, Is.Empty, label);
                Assert.That(result.NearMatches.Single().LowestTier, Is.EqualTo(lowestTier), label);
            }
        }
    }
}
