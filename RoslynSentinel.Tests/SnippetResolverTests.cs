using Microsoft.CodeAnalysis.Text;
using RoslynSentinel.Common.SnippetMatching;

namespace RoslynSentinel.Tests;

/// <summary>
/// Tests for the <see cref="SnippetResolver"/> dispatcher (slice 2b of the whitespace-tolerance proposal): fixed
/// expectations captured from the legacy ContextHelper helpers (removed in slice 5f), the cap, the near-match probe and the programming-error guards.
/// Test methods take tier values as <c>int</c> because <see cref="WhitespaceTolerance"/> is internal and an NUnit
/// test method must be public.
/// </summary>
[Parallelizable(ParallelScope.All)]
public class SnippetResolverTests
{
    private static SnippetMatchResult Resolve(string text, string snippet, WhitespaceTolerance tolerance, SnippetCaseMode caseMode = SnippetCaseMode.CaseSensitive)
        => SnippetResolver.Resolve(SourceText.From(text), snippet, tolerance, caseMode);

    // ---- (1) fixed expectations (formerly parity with the legacy helpers) over the shared corpus ----

    // Corpus cases where the ladder now stops at IgnoreIndentation, before the collapse stages, and so reports the
    // narrower true span (or a different window) instead of what FindAllSnippetMatchesWithLength reports. Each value
    // lists the expected (Start, matched text) per match. Every other corpus case still equals the legacy result.
    private static readonly Dictionary<(string Text, string Snippet), (int Start, string Matched)[]> ParityDivergences = new()
    {
        // Legacy window stage started at the line start (indentation included): (0, 40). Now starts at the fragment.
        [("        }\n        var trailingIssue = x;\n", "}\nvar trailingIssue = x;")] = new[] { (8, "}\n        var trailingIssue = x;") },
        [("    a\r\n    b\r\n", "a\nb")] = new[] { (4, "a\r\n    b") },
        [("    a\n    b\n\n    a\n    b\n", "a\nb")] = new[] { (4, "a\n    b"), (17, "a\n    b") },
        // Legacy window stage kept the last line's trailing whitespace in the span; the new span stops after "bar();".
        [("foo();   \nbar();  \n", "foo();\nbar();")] = new[] { (0, "foo();   \nbar();") },
        [("    a\n\n    b\n", "a\n\nb")] = new[] { (4, "a\n\n    b") },
        // Legacy collapsed single-line stage matched the one line "a b" at (0, 3); the real two-line block now wins first.
        [("a b\n  a\n  b\n", "a\nb")] = new[] { (6, "a\n  b") },
        [("\n  \n    a\n    b\n  \n", "\n\na\nb\n\n")] = new[] { (8, "a\n    b") },
    };

    // Fixed (Start, Length) per match that the removed ContextHelper.FindAllSnippetMatchesWithLength produced for every
    // corpus case, captured once by running that helper in slice 5e before it was deleted. Cases listed in
    // ParityDivergences differ from these on purpose; every other case must still equal them.
    private static readonly Dictionary<(string Text, string Snippet), (int Start, int Length)[]> LegacyAllWhitespaceSpans = new()
    {
        [("int x = 1;\nint y = 2;\n", "int y = 2;")] = new[] { (11, 10) },
        [("a\nb\nc\n", "a\nb")] = new[] { (0, 3) },
        [("a\r\nb\r\nc\r\n", "a\nb")] = new[] { (0, 4) },
        [("a\nb\nc\n", "a\r\nb")] = new[] { (0, 3) },
        [("    int x = 1;\n", "int x = 1;")] = new[] { (4, 10) },
        [("        }\n        var trailingIssue = x;\n", "}\nvar trailingIssue = x;")] = new[] { (0, 40) },
        [("    a\r\n    b\r\n", "a\nb")] = new[] { (0, 12) },
        [("var r = a  +  b;\n", "a + b;")] = new[] { (8, 8) },
        [("Console.WriteLine(X);\n", "console.writeline(x);")] = new[] { (0, 21) },
        [("if (a) return x;\n", "return x;")] = new[] { (7, 9) },
        [("foo();\nfoo();\nfoo();\n", "foo();")] = new[] { (0, 6), (7, 6), (14, 6) },
        [("    a\n    b\n\n    a\n    b\n", "a\nb")] = new[] { (0, 11), (13, 11) },
        [("aaaa", "aa")] = new[] { (0, 2), (1, 2), (2, 2) },
        [("    return foo;\n}\nother();\n", "return foo;\n}\n")] = new[] { (4, 14) },
        [("int x = 1;\n", "   ")] = Array.Empty<(int, int)>(),
        [("int x = 1;\n", "")] = Array.Empty<(int, int)>(),
        [("int x = 1;\n", "nothing like this")] = Array.Empty<(int, int)>(),
        [("foo();   \nbar();  \n", "foo();\nbar();")] = new[] { (0, 18) },
        [("foo();\nbar();\n", "foo();  \nbar();  ")] = new[] { (0, 13) },
        [("    a\n\n    b\n", "a\n\nb")] = new[] { (0, 12) },
        [("a b\n  a\n  b\n", "a\nb")] = new[] { (0, 3) },
        [("if (a) {\n  b();\n} else {\n", "{\n  b();\n}")] = new[] { (7, 10) },
        [("\n  \n    a\n    b\n  \n", "\n\na\nb\n\n")] = new[] { (4, 11) },
    };

    // Fixed (Start, Length) per match that the removed ContextHelper.FindAllExactSnippetMatches produced (it never
    // collapsed whitespace, so every corpus case not listed here found nothing), captured once in slice 5e.
    private static readonly Dictionary<(string Text, string Snippet), (int Start, int Length)[]> LegacyExactSpans = new()
    {
        [("int x = 1;\nint y = 2;\n", "int y = 2;")] = new[] { (11, 10) },
        [("a\nb\nc\n", "a\nb")] = new[] { (0, 3) },
        [("a\r\nb\r\nc\r\n", "a\nb")] = new[] { (0, 4) },
        [("a\nb\nc\n", "a\r\nb")] = new[] { (0, 3) },
        [("    int x = 1;\n", "int x = 1;")] = new[] { (4, 10) },
        [("if (a) return x;\n", "return x;")] = new[] { (7, 9) },
        [("foo();\nfoo();\nfoo();\n", "foo();")] = new[] { (0, 6), (7, 6), (14, 6) },
        [("aaaa", "aa")] = new[] { (0, 2), (1, 2), (2, 2) },
        [("    return foo;\n}\nother();\n", "return foo;\n}\n")] = new[] { (4, 14) },
        [("if (a) {\n  b();\n} else {\n", "{\n  b();\n}")] = new[] { (7, 10) },
    };

    [TestCaseSource(typeof(SnippetMatcherTests), nameof(SnippetMatcherTests.Corpus))]
    public void Corpus_IgnoreAllWhitespaceCaseInsensitive_MatchesFixedSpans(string text, string snippet)
    {
        var sourceText = SourceText.From(text);

        // Fixed values the removed legacy helper produced for this case (see LegacyAllWhitespaceSpans).
        var legacy = LegacyAllWhitespaceSpans[(text, snippet)];
        var result = SnippetResolver.Resolve(sourceText, snippet, WhitespaceTolerance.IgnoreAllWhitespace, SnippetCaseMode.CaseInsensitive);

        if (ParityDivergences.TryGetValue((text, snippet), out var expected))
        {
            Assert.Multiple(() =>
            {
                Assert.That(result.Matches.Select(m => (m.Start, text.Substring(m.Start, m.Length))), Is.EqualTo(expected));
                Assert.That(result.Matches.Select(m => m.Tier).Distinct(), Is.EqualTo(new[] { WhitespaceTolerance.IgnoreIndentation }));
                Assert.That(result.Matches.Select(m => (m.Start, m.Length)), Is.Not.EqualTo(legacy),
                    "a listed divergence must really differ from the legacy result, otherwise remove it from the list");
            });
        }
        else
        {
            Assert.That(result.Matches.Select(m => (m.Start, m.Length)), Is.EqualTo(legacy));
        }

        Assert.That(result.NearMatches, Is.Empty, "the loosest tier has nothing looser to probe");
        AssertOutcomeMatchesCount(result);
    }

    [TestCaseSource(typeof(SnippetMatcherTests), nameof(SnippetMatcherTests.Corpus))]
    public void Corpus_IgnoreLineEndingsCaseSensitive_MatchesFixedSpans(string text, string snippet)
    {
        var sourceText = SourceText.From(text);

        // Fixed values the removed legacy helper produced for this case (see LegacyExactSpans); absent means no match.
        var legacy = LegacyExactSpans.GetValueOrDefault((text, snippet), Array.Empty<(int Start, int Length)>());
        var result = SnippetResolver.Resolve(sourceText, snippet, WhitespaceTolerance.IgnoreLineEndings, SnippetCaseMode.CaseSensitive);

        Assert.That(result.Matches.Select(m => (m.Start, m.Length)), Is.EqualTo(legacy));
        if (legacy.Length > 0)
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
    [TestCase(2)]
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
        foreach (var tolerance in new[] { WhitespaceTolerance.Exact, WhitespaceTolerance.IgnoreLineEndings, WhitespaceTolerance.IgnoreIndentation, WhitespaceTolerance.IgnoreAllWhitespace })
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
    public void Cap_IgnoreLineEndingsRequest_DoesNotReachTheIndentationTier()
    {
        var result = Resolve("    a\n    b\n", "a\nb", WhitespaceTolerance.IgnoreLineEndings);

        Assert.That(result.Outcome, Is.EqualTo(SnippetMatchOutcome.MatchedLooserThanRequested));
        Assert.That(result.Matches, Is.Empty);
        Assert.That(result.NearMatches.Single().LowestTier, Is.EqualTo(WhitespaceTolerance.IgnoreIndentation));
    }

    [Test]
    public void Cap_ExactRequest_NeverReturnsAnIndentationMatch_EvenWhenOneExistsElsewhere()
    {
        // The verbatim block is first; the block further down differs by trailing whitespace and matches only at IgnoreIndentation.
        var result = Resolve("a\nb\nmid();\na   \nb  \n", "a\nb", WhitespaceTolerance.Exact);

        Assert.That(result.Outcome, Is.EqualTo(SnippetMatchOutcome.Matched));
        Assert.That(result.Matches.Select(m => (m.Start, m.Tier)), Is.EqualTo(new[] { (0, WhitespaceTolerance.Exact) }));
        Assert.That(result.NearMatches, Is.Empty);

        var indentation = Resolve("a\nb\nmid();\na   \nb  \n", "a\nb", WhitespaceTolerance.IgnoreIndentation);
        Assert.That(indentation.Matches.Select(m => m.Tier), Is.All.EqualTo(WhitespaceTolerance.Exact),
            "the ladder stops at the first tier with matches, so the looser-only block is not added");
        Assert.That(indentation.Matches, Has.Count.EqualTo(1));
    }

    [Test]
    public void Cap_ExactRequest_WhereOnlyIndentationMatches_ReportsMatchedLooserThanRequested()
    {
        var result = Resolve("foo();   \nbar();  \n", "foo();\nbar();", WhitespaceTolerance.Exact);

        Assert.Multiple(() =>
        {
            Assert.That(result.Outcome, Is.EqualTo(SnippetMatchOutcome.MatchedLooserThanRequested));
            Assert.That(result.Matches, Is.Empty);
            Assert.That(result.NearMatches.Single().LowestTier, Is.EqualTo(WhitespaceTolerance.IgnoreIndentation));
            Assert.That(result.NearMatches.Single().ActualLines, Is.EqualTo(new[] { "foo();   ", "bar();  " }));
        });
    }

    [Test]
    public void Cap_IgnoreIndentationRequest_NeverReturnsAnIgnoreAllWhitespaceOnlyMatch()
    {
        var result = Resolve("var r = a  +  b;\n", "a + b;", WhitespaceTolerance.IgnoreIndentation);

        Assert.Multiple(() =>
        {
            Assert.That(result.Outcome, Is.EqualTo(SnippetMatchOutcome.MatchedLooserThanRequested));
            Assert.That(result.Matches, Is.Empty);
            Assert.That(result.NearMatches.Single().LowestTier, Is.EqualTo(WhitespaceTolerance.IgnoreAllWhitespace));
            Assert.That(result.NearMatches.Single().ActualLines, Is.EqualTo(new[] { "var r = a  +  b;" }));
        });
    }

    [Test]
    public void Cap_IgnoreIndentationRequest_AcceptsEverythingTheLowerTiersAccept()
    {
        foreach (var (text, snippet) in new[]
                 {
                     ("int x = 1;\nint y = 2;\n", "int y = 2;"),
                     ("a\nb\nc\n", "a\nb"),
                     ("a\r\nb\r\nc\r\n", "a\nb"),
                     ("if (a) return x;\n", "return x;"),
                     ("    return foo;\n}\nother();\n", "return foo;\n}\n"),
                 })
        {
            var result = Resolve(text, snippet, WhitespaceTolerance.IgnoreIndentation);

            Assert.That(result.Matches, Is.Not.Empty, $"'{snippet}'");
            Assert.That(result.Matches.Select(m => m.Tier), Is.All.LessThanOrEqualTo(WhitespaceTolerance.IgnoreIndentation));
        }
    }

    [Test]
    public void Cap_IgnoreAllWhitespaceRequest_StillMatchesWhatOnlyTheCollapseStagesAccept()
    {
        var interior = Resolve("var r = a  +  b;\n", "a + b;", WhitespaceTolerance.IgnoreAllWhitespace);
        var windowInterior = Resolve("x\n  a  +  b\ny\n", "x\na + b\ny", WhitespaceTolerance.IgnoreAllWhitespace);

        Assert.Multiple(() =>
        {
            Assert.That(interior.Single().Tier, Is.EqualTo(WhitespaceTolerance.IgnoreAllWhitespace));
            Assert.That(windowInterior.Single().Tier, Is.EqualTo(WhitespaceTolerance.IgnoreAllWhitespace));
        });
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
            // Slice 3a: the first line is a fragment at its start, so only the second line differs (its indentation).
            var difference = result.NearMatches[0].Differences.Single();
            Assert.That(difference.Kind, Is.EqualTo(SnippetDifferenceKind.LeadingWhitespace));
            Assert.That(difference.Line, Is.EqualTo(2));
            Assert.That(difference.FileLine, Is.EqualTo(9));
            Assert.That(difference.ActualLineText, Is.EqualTo("        var trailingIssue = x;"));
            Assert.That(result.NearMatches[0].LowestTier, Is.EqualTo(WhitespaceTolerance.IgnoreIndentation));
        });
    }

    [Test]
    public void QwenRegression_IgnoreIndentationRequest_IsOneMatch_StartingAfterTheIndentation_AndSpliceReproducesTheFile()
    {
        const string text = "class C\n{\n    void M()\n    {\n        if (a)\n        {\n            y();\n        }\n        var trailingIssue = x;\n    }\n}\n";
        const string snippet = "}\nvar trailingIssue = x;";

        var result = Resolve(text, snippet, WhitespaceTolerance.IgnoreIndentation);

        var match = result.Single();
        var matched = text.Substring(match.Start, match.Length);
        Assert.Multiple(() =>
        {
            Assert.That(result.Outcome, Is.EqualTo(SnippetMatchOutcome.Matched));
            Assert.That(result.NearMatches, Is.Empty);
            Assert.That(match.Tier, Is.EqualTo(WhitespaceTolerance.IgnoreIndentation));
            Assert.That(match.Line, Is.EqualTo(8));
            Assert.That(text[match.Start], Is.EqualTo('}'), "the span starts after the first line's indentation");
            Assert.That(matched, Is.EqualTo("}\n        var trailingIssue = x;"));
            Assert.That(text.Remove(match.Start, match.Length).Insert(match.Start, matched), Is.EqualTo(text));
            // Replacing the span keeps the file's indentation on the first line.
            Assert.That(text.Remove(match.Start, match.Length).Insert(match.Start, "}\n        var y = 1;"),
                Does.Contain("        }\n        var y = 1;\n    }"));
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
    public void Resolve_IgnoreIndentation_IsAnImplementedTier_AndDoesNotThrow()
    {
        var result = Resolve("    a\n", "a", WhitespaceTolerance.IgnoreIndentation);

        Assert.That(result.Outcome, Is.EqualTo(SnippetMatchOutcome.Matched));
        Assert.That(result.RequestedTolerance, Is.EqualTo(WhitespaceTolerance.IgnoreIndentation));
    }

    [TestCase(0)]
    [TestCase(1)]
    [TestCase(2)]
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
        yield return new TestCaseData("    a\r\n    b\r\n", "a\nb", (int)WhitespaceTolerance.IgnoreIndentation).SetName("Fixture_IndentationDifference");
        yield return new TestCaseData("foo();   \nbar();  \n", "foo();\nbar();", (int)WhitespaceTolerance.IgnoreIndentation).SetName("Fixture_TrailingWhitespaceDifference");
        yield return new TestCaseData("if (a) return x;\n", "return x;", (int)WhitespaceTolerance.Exact).SetName("Fixture_MidLineFragment");
        yield return new TestCaseData("var r = a  +  b;\n", "a + b;", (int)WhitespaceTolerance.IgnoreAllWhitespace).SetName("Fixture_InteriorWhitespaceDifference");
    }

    [TestCaseSource(nameof(TierFixtures))]
    public void Matrix_EveryFixtureAgainstEveryTierRequest(string text, string snippet, int lowestTierValue)
    {
        var lowestTier = (WhitespaceTolerance)lowestTierValue;

        foreach (var requested in new[] { WhitespaceTolerance.Exact, WhitespaceTolerance.IgnoreLineEndings, WhitespaceTolerance.IgnoreIndentation, WhitespaceTolerance.IgnoreAllWhitespace })
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
