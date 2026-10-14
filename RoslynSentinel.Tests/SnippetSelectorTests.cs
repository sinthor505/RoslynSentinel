using Microsoft.CodeAnalysis.Text;
using RoslynSentinel.Common.SnippetMatching;

namespace RoslynSentinel.Tests;

/// <summary>
/// Tests for the selector post-filter of <see cref="SnippetResolver"/> (slice 2e of the whitespace-tolerance proposal):
/// withinMember, matchLine, lineBefore and lineAfter narrow the matches at the winning tier, every failure is an outcome
/// and never an exception, and the chosen match agrees with <c>ContextHelper.FindExactSnippetPosition</c>.
/// </summary>
[Parallelizable(ParallelScope.All)]
public class SnippetSelectorTests
{
    // Snippet occurs at lines 5-6 (in A), 12-13 (in B) and 19-20 (in the B(int) overload). Same fixture as ContextHelperSelectorTests.
    private static readonly string Source = string.Join("\n",
        "class C",
        "{",
        "    void A()",
        "    {",
        "        x = 1;",
        "        y = 2;",
        "        return;",
        "    }",
        "",
        "    void B()",
        "    {",
        "        x = 1;",
        "        y = 2;",
        "        return;",
        "    }",
        "",
        "    void B(int n)",
        "    {",
        "        x = 1;",
        "        y = 2;",
        "        throw;",
        "    }",
        "}");

    private const string Snippet = "x = 1;\n        y = 2;";

    private static int OffsetOfLine(int oneBasedLine) =>
        SourceText.From(Source).Lines[oneBasedLine - 1].Start;

    private static int OffsetOfSnippetAtLine(int oneBasedLine, string snippet = Snippet) =>
        Source.IndexOf(snippet, OffsetOfLine(oneBasedLine), StringComparison.Ordinal);

    // Member lookup keyed off line ranges: A = lines 3-8, B = lines 10-15 and 17-22 (overload-like duplicate name).
    private static Func<int, string?> MemberByLines(SourceText text) => offset =>
    {
        var line = text.Lines.GetLinePosition(offset).Line + 1;
        return line switch
        {
            >= 3 and <= 8 => "A",
            >= 10 and <= 15 => "B",
            >= 17 and <= 22 => "B",
            _ => null,
        };
    };

    private static SnippetMatchResult Resolve(
        SnippetSelectors? selectors, bool withMembers = true, string snippet = Snippet,
        WhitespaceTolerance tolerance = WhitespaceTolerance.IgnoreLineEndings)
    {
        var text = SourceText.From(Source);
        return SnippetResolver.Resolve(
            text, snippet, tolerance, SnippetCaseMode.CaseSensitive, selectors, withMembers ? MemberByLines(text) : null);
    }

    private static SnippetMatchResult ResolveIn(string source, string snippet, SnippetSelectors? selectors)
        => SnippetResolver.Resolve(
            SourceText.From(source), snippet, WhitespaceTolerance.IgnoreLineEndings, SnippetCaseMode.CaseSensitive, selectors, null);

    // ---- happy paths: one test per selector ----

    [Test]
    public void MatchLine_SelectsTheIntendedMatch()
    {
        var result = Resolve(new SnippetSelectors(MatchLine: 12));

        Assert.Multiple(() =>
        {
            Assert.That(result.Outcome, Is.EqualTo(SnippetMatchOutcome.Matched));
            Assert.That(result.Single().Start, Is.EqualTo(OffsetOfSnippetAtLine(12)));
            Assert.That(result.Single().Line, Is.EqualTo(12));
            Assert.That(result.FilterTrail, Is.EqualTo(new[] { new SnippetFilterStep("matchLine", 2) }));
            Assert.That((result.CandidatesBeforeSelectors, result.CandidatesAfterSelectors, result.CandidatesAfterAdjacentLines), Is.EqualTo((3, 1, 1)));
        });
    }

    [Test]
    public void WithinMember_SelectsTheMatchInThatMember()
    {
        var result = Resolve(new SnippetSelectors(WithinMember: "A"));

        Assert.Multiple(() =>
        {
            Assert.That(result.Outcome, Is.EqualTo(SnippetMatchOutcome.Matched));
            Assert.That(result.Single().Start, Is.EqualTo(OffsetOfSnippetAtLine(5)));
            Assert.That(result.FilterTrail, Is.EqualTo(new[] { new SnippetFilterStep("withinMember", 2) }));
        });
    }

    [Test]
    public void LineBefore_SelectsTheMatchPrecededByThatLine()
    {
        const string source = "a();\nx();\nb();\nc();\nx();\nd();";

        var result = ResolveIn(source, "x();", new SnippetSelectors(LineBefore: "c();"));

        Assert.Multiple(() =>
        {
            Assert.That(result.Outcome, Is.EqualTo(SnippetMatchOutcome.Matched));
            Assert.That(result.Single().Line, Is.EqualTo(5));
            Assert.That(result.FilterTrail, Is.EqualTo(new[] { new SnippetFilterStep("lineBefore", 1) }));
        });
    }

    [Test]
    public void LineAfter_SelectsTheMatchFollowedByThatLine()
    {
        const string source = "a();\nx();\nb();\nc();\nx();\nd();";

        var result = ResolveIn(source, "x();", new SnippetSelectors(LineAfter: "b();"));

        Assert.Multiple(() =>
        {
            Assert.That(result.Outcome, Is.EqualTo(SnippetMatchOutcome.Matched));
            Assert.That(result.Single().Line, Is.EqualTo(2));
            Assert.That(result.FilterTrail, Is.EqualTo(new[] { new SnippetFilterStep("lineAfter", 1) }));
        });
    }

    [Test]
    public void WithinMemberAndMatchLine_AreIntersected()
    {
        var hit = Resolve(new SnippetSelectors(WithinMember: "B", MatchLine: 19));
        var miss = Resolve(new SnippetSelectors(WithinMember: "A", MatchLine: 12));

        Assert.Multiple(() =>
        {
            Assert.That(hit.Single().Start, Is.EqualTo(OffsetOfSnippetAtLine(19)));
            Assert.That(miss.Outcome, Is.EqualTo(SnippetMatchOutcome.SelectorMismatch));
        });
    }

    [Test]
    public void Precedence_WithinMemberAndMatchLineActFirst_ThenLineBeforeAndLineAfterFilterTheSurvivors()
    {
        // withinMember "B" narrows 3 matches to 2 (lines 12 and 19); lineAfter "throw;" then picks line 19.
        var result = Resolve(new SnippetSelectors(WithinMember: "B", LineAfter: "throw;"));

        Assert.Multiple(() =>
        {
            Assert.That(result.Outcome, Is.EqualTo(SnippetMatchOutcome.Matched));
            Assert.That(result.Single().Start, Is.EqualTo(OffsetOfSnippetAtLine(19)));
            Assert.That(result.FilterTrail, Is.EqualTo(new[] { new SnippetFilterStep("withinMember", 1), new SnippetFilterStep("lineAfter", 1) }));
            Assert.That((result.CandidatesBeforeSelectors, result.CandidatesAfterSelectors, result.CandidatesAfterAdjacentLines), Is.EqualTo((3, 2, 1)));
            Assert.That(result.SelectorSurvivors.Select(m => m.Line), Is.EqualTo(new[] { 12, 19 }));
        });
    }

    [Test]
    public void MatchBeginningWithNewline_IsAttributedToItsFirstNonWhitespaceLine()
    {
        // Match 2 starts with the newline ending the "{" line (11), but a human sees "x = 1;" on line 12.
        const string newlineSnippet = "\n        x = 1;\n        y = 2;";

        var onTextLine = Resolve(new SnippetSelectors(MatchLine: 12), snippet: newlineSnippet);
        var onNewlineLine = Resolve(new SnippetSelectors(MatchLine: 11), snippet: newlineSnippet);

        Assert.Multiple(() =>
        {
            Assert.That(onTextLine.Single().Start, Is.EqualTo(OffsetOfSnippetAtLine(11, newlineSnippet)));
            Assert.That(onNewlineLine.Outcome, Is.EqualTo(SnippetMatchOutcome.SelectorMismatch));
        });
    }

    [Test]
    public void WithinMember_AsksTheDelegateAtTheFirstNonWhitespaceCharacter()
    {
        const string newlineSnippet = "\n        x = 1;\n        y = 2;";
        var offsetsAsked = new List<int>();

        SnippetResolver.Resolve(
            SourceText.From(Source), newlineSnippet, WhitespaceTolerance.IgnoreLineEndings, SnippetCaseMode.CaseSensitive,
            new SnippetSelectors(WithinMember: "A"),
            offset =>
            {
                offsetsAsked.Add(offset);
                return "other";
            });

        Assert.That(offsetsAsked.Select(o => Source[o]), Is.All.EqualTo('x'));
    }

    [Test]
    public void SelectorsApplyToTheMatchesAtTheWinningLooserTier()
    {
        // The indentation-dropped snippet matches only at IgnoreIndentation; selectors narrow those matches.
        var result = Resolve(
            new SnippetSelectors(MatchLine: 12), snippet: "x = 1;\ny = 2;", tolerance: WhitespaceTolerance.IgnoreIndentation);

        Assert.Multiple(() =>
        {
            Assert.That(result.Outcome, Is.EqualTo(SnippetMatchOutcome.Matched));
            Assert.That(result.Single().Tier, Is.EqualTo(WhitespaceTolerance.IgnoreIndentation));
            Assert.That(result.Single().Line, Is.EqualTo(12));
        });
    }

    // ---- failure outcomes ----

    [Test]
    public void SelectorMismatch_KeepsThePreFilterMatchesAndNoAcceptedMatch()
    {
        var result = Resolve(new SnippetSelectors(MatchLine: 99));

        Assert.Multiple(() =>
        {
            Assert.That(result.Outcome, Is.EqualTo(SnippetMatchOutcome.SelectorMismatch));
            Assert.That(result.Matches, Is.Empty);
            Assert.That(result.IsSingleMatch, Is.False);
            Assert.That(result.PreFilterMatches.Select(m => m.Line), Is.EqualTo(new[] { 5, 12, 19 }));
            Assert.That(result.SelectorSurvivors, Is.Empty);
            Assert.That(result.FilterTrail, Is.EqualTo(new[] { new SnippetFilterStep("matchLine", 3) }));
            Assert.That((result.CandidatesBeforeSelectors, result.CandidatesAfterSelectors, result.CandidatesAfterAdjacentLines), Is.EqualTo((3, 0, 0)));
        });
    }

    [Test]
    public void SelectorMismatch_WithinMemberNamingNoMember()
    {
        var result = Resolve(new SnippetSelectors(WithinMember: "Missing"));

        Assert.Multiple(() =>
        {
            Assert.That(result.Outcome, Is.EqualTo(SnippetMatchOutcome.SelectorMismatch));
            Assert.That(result.PreFilterMatches, Has.Count.EqualTo(3));
        });
    }

    [Test]
    public void SelectorMismatch_OnASingleTierMatch()
    {
        var miss = Resolve(new SnippetSelectors(MatchLine: 99), snippet: "throw;");
        var hit = Resolve(new SnippetSelectors(MatchLine: 21), snippet: "throw;");

        Assert.Multiple(() =>
        {
            Assert.That(miss.Outcome, Is.EqualTo(SnippetMatchOutcome.SelectorMismatch));
            Assert.That(miss.PreFilterMatches, Has.Count.EqualTo(1));
            Assert.That(hit.Outcome, Is.EqualTo(SnippetMatchOutcome.Matched));
        });
    }

    [Test]
    public void AdjacentLineMismatch_AfterSelectors_KeepsSurvivorsAndPreFilterMatches()
    {
        var result = Resolve(new SnippetSelectors(WithinMember: "B", LineAfter: "nonexistent;"));

        Assert.Multiple(() =>
        {
            Assert.That(result.Outcome, Is.EqualTo(SnippetMatchOutcome.AdjacentLineMismatch));
            Assert.That(result.Matches, Is.Empty);
            Assert.That(result.PreFilterMatches, Has.Count.EqualTo(3));
            Assert.That(result.SelectorSurvivors.Select(m => m.Line), Is.EqualTo(new[] { 12, 19 }));
            Assert.That(result.FilterTrail, Is.EqualTo(new[] { new SnippetFilterStep("withinMember", 1), new SnippetFilterStep("lineAfter", 2) }));
            Assert.That((result.CandidatesBeforeSelectors, result.CandidatesAfterSelectors, result.CandidatesAfterAdjacentLines), Is.EqualTo((3, 2, 0)));
        });
    }

    [Test]
    public void AdjacentLineMismatch_WithoutSelectors_ListsEveryTierMatchAsSurvivor()
    {
        var result = Resolve(new SnippetSelectors(LineBefore: "nonexistent;"));

        Assert.Multiple(() =>
        {
            Assert.That(result.Outcome, Is.EqualTo(SnippetMatchOutcome.AdjacentLineMismatch));
            Assert.That(result.PreFilterMatches, Has.Count.EqualTo(3));
            Assert.That(result.SelectorSurvivors, Has.Count.EqualTo(3));
        });
    }

    [Test]
    public void AdjacentLines_AtTheEdgesOfTheFile_NeverMatch()
    {
        const string source = "x();\nb();\nx();";

        var beforeFirst = ResolveIn(source, "x();", new SnippetSelectors(LineBefore: "", MatchLine: 1));
        var afterLast = ResolveIn(source, "x();", new SnippetSelectors(LineAfter: "", MatchLine: 3));

        Assert.Multiple(() =>
        {
            Assert.That(beforeFirst.Outcome, Is.EqualTo(SnippetMatchOutcome.AdjacentLineMismatch));
            Assert.That(afterLast.Outcome, Is.EqualTo(SnippetMatchOutcome.AdjacentLineMismatch));
        });
    }

    [Test]
    public void StillAmbiguous_WhenSeveralMatchesSurviveWithinMember()
    {
        var result = Resolve(new SnippetSelectors(WithinMember: "B"));

        Assert.Multiple(() =>
        {
            Assert.That(result.Outcome, Is.EqualTo(SnippetMatchOutcome.StillAmbiguous));
            Assert.That(result.Matches.Select(m => m.Line), Is.EqualTo(new[] { 12, 19 }));
            Assert.That(result.PreFilterMatches, Has.Count.EqualTo(3));
            Assert.That(result.IsSingleMatch, Is.False);
        });
    }

    [Test]
    public void StillAmbiguous_WhenLineBeforeKeepsSeveralMatches()
    {
        // Every match is preceded by "{".
        var result = Resolve(new SnippetSelectors(LineBefore: "{"));

        Assert.Multiple(() =>
        {
            Assert.That(result.Outcome, Is.EqualTo(SnippetMatchOutcome.StillAmbiguous));
            Assert.That(result.Matches, Has.Count.EqualTo(3));
        });
    }

    [TestCase("lineBefore", "a\nb")]
    [TestCase("lineAfter", "a\nb")]
    public void InvalidDisambiguator_MultiLineAdjacentLine(string parameter, string value)
    {
        var selectors = parameter == "lineBefore"
            ? new SnippetSelectors(LineBefore: value, MatchLine: 12)
            : new SnippetSelectors(LineAfter: value, MatchLine: 12);

        var result = Resolve(selectors);

        Assert.Multiple(() =>
        {
            Assert.That(result.Outcome, Is.EqualTo(SnippetMatchOutcome.InvalidDisambiguator));
            Assert.That(result.InvalidParameterName, Is.EqualTo(parameter));
            Assert.That(result.InvalidParameterValue, Is.EqualTo(value));
            Assert.That(result.Matches, Is.Empty);
        });
    }

    [Test]
    public void InvalidDisambiguator_WithinMemberWithoutMemberLookup()
    {
        var result = Resolve(new SnippetSelectors(WithinMember: "A"), withMembers: false);

        Assert.Multiple(() =>
        {
            Assert.That(result.Outcome, Is.EqualTo(SnippetMatchOutcome.InvalidDisambiguator));
            Assert.That(result.InvalidParameterName, Is.EqualTo("withinMember"));
            Assert.That(result.InvalidParameterValue, Is.EqualTo("A"));
        });
    }

    [Test]
    public void InvalidDisambiguator_WinsOverNoMatch_AsInContextHelper()
    {
        var result = Resolve(new SnippetSelectors(LineAfter: "a\nb"), snippet: "does not occur");

        Assert.That(result.Outcome, Is.EqualTo(SnippetMatchOutcome.InvalidDisambiguator));
    }

    [Test]
    public void BlankSelectors_AreTreatedAsNotSupplied()
    {
        // Weak models send "" or 0 for an unused parameter; that must not filter out every match or demand a delegate.
        foreach (var blank in new[]
        {
            new SnippetSelectors(WithinMember: ""),
            new SnippetSelectors(WithinMember: "   "),
            new SnippetSelectors(MatchLine: 0),
            new SnippetSelectors(MatchLine: -3),
            new SnippetSelectors(),
        })
        {
            var result = Resolve(blank, withMembers: false);

            Assert.Multiple(() =>
            {
                Assert.That(result.Outcome, Is.EqualTo(SnippetMatchOutcome.Ambiguous), blank.ToString());
                Assert.That(result.Matches, Has.Count.EqualTo(3));
                Assert.That(result.FilterTrail, Is.Empty);
            });
        }
    }

    [Test]
    public void NoTierMatch_ReturnsTheTierResultUnchanged()
    {
        var noMatch = Resolve(new SnippetSelectors(MatchLine: 12), snippet: "does not occur");
        var looser = Resolve(new SnippetSelectors(MatchLine: 12), snippet: "x = 1;\ny = 2;", tolerance: WhitespaceTolerance.Exact);

        Assert.Multiple(() =>
        {
            Assert.That(noMatch.Outcome, Is.EqualTo(SnippetMatchOutcome.NoMatch));
            Assert.That(looser.Outcome, Is.EqualTo(SnippetMatchOutcome.MatchedLooserThanRequested));
            Assert.That(looser.NearMatches, Is.Not.Empty);
            Assert.That(looser.FilterTrail, Is.Empty);
        });
    }

    [Test]
    public void NullSelectors_EqualTheFourParameterOverload()
    {
        var text = SourceText.From(Source);

        var plain = SnippetResolver.Resolve(text, Snippet, WhitespaceTolerance.IgnoreLineEndings, SnippetCaseMode.CaseSensitive);
        var withNull = Resolve(null);

        Assert.Multiple(() =>
        {
            Assert.That(withNull.Outcome, Is.EqualTo(plain.Outcome));
            Assert.That(withNull.Matches, Is.EqualTo(plain.Matches));
            Assert.That(withNull.PreFilterMatches, Is.EqualTo(plain.Matches), "PreFilterMatches falls back to Matches when no selector ran");
        });
    }

    [Test]
    public void Selectors_NeverThrowForCallerMistakes()
    {
        var mistakes = new[]
        {
            new SnippetSelectors(WithinMember: "A"),
            new SnippetSelectors(MatchLine: int.MaxValue),
            new SnippetSelectors(MatchLine: int.MinValue),
            new SnippetSelectors(LineBefore: "a\nb"),
            new SnippetSelectors(LineAfter: "\n"),
            new SnippetSelectors(LineBefore: "", LineAfter: ""),
            new SnippetSelectors(WithinMember: "A", MatchLine: 99, LineBefore: "x", LineAfter: "y"),
        };

        foreach (var withMembers in new[] { true, false })
        {
            foreach (var snippet in new[] { Snippet, "throw;", "does not occur", "", "   " })
            {
                foreach (var mistake in mistakes)
                {
                    Assert.DoesNotThrow(() => Resolve(mistake, withMembers, snippet), $"{mistake} / withMembers={withMembers} / \"{snippet}\"");
                }
            }
        }
    }

    [Test]
    public void Create_WithoutSelectorData_DefaultsToAnEmptyTrailAndPreFilterMatchesEqualToMatches()
    {
        var match = new SnippetMatch(0, 1, 3, WhitespaceTolerance.Exact, "ExactMatcher");

        var result = SnippetMatchResult.Create(
            WhitespaceTolerance.Exact, SnippetCaseMode.CaseSensitive, SnippetMatchOutcome.Matched,
            new[] { match }, Array.Empty<SnippetNearMatch>(), 0, Array.Empty<SnippetClosestWindow>(), 1, 1, 1);

        Assert.Multiple(() =>
        {
            Assert.That(result.FilterTrail, Is.Empty);
            Assert.That(result.PreFilterMatches, Is.EqualTo(new[] { match }));
            Assert.That(result.SelectorSurvivors, Is.EqualTo(new[] { match }));
            Assert.That(result.InvalidParameterName, Is.Null);
        });
    }

    // ---- fixed expectations (formerly parity with ContextHelper.FindExactSnippetPosition) over the selector corpus ----
    // The trailing three TestCase arguments are what the removed legacy helper returned for that case, captured once in
    // slice 5e: a (start, length) match, or an error kind ("Ambiguous" = ToolAmbiguousMatchException, "NotFound" =
    // ToolNotFoundException).
    // Divergence (documented, not a parity failure): when several matches survive withinMember/matchLine alone, the
    // legacy helper reports Ambiguous and the filter reports StillAmbiguous; both leave no single match.

    private const string NewlineSnippet = "\n        x = 1;\n        y = 2;";

    [TestCase(null, 12, null, null, false, 109, 21, null)]
    [TestCase("A", null, null, null, false, 37, 21, null)]
    [TestCase("B", 19, null, null, false, 186, 21, null)]
    [TestCase("B", null, null, "throw;", false, 186, 21, null)]
    [TestCase(null, 12, null, null, true, 100, 30, null)]
    [TestCase(null, 11, null, null, true, null, null, "NotFound")]
    [TestCase(null, null, null, "throw;", false, 186, 21, null)]
    [TestCase("B", null, null, null, false, null, null, "Ambiguous")]
    [TestCase(null, 99, null, null, false, null, null, "NotFound")]
    [TestCase("A", 12, null, null, false, null, null, "NotFound")]
    [TestCase("Missing", null, null, null, false, null, null, "NotFound")]
    [TestCase("B", null, null, "nonexistent;", false, null, null, "NotFound")]
    [TestCase(null, null, null, null, false, null, null, "Ambiguous")]
    [TestCase(null, 12, "a\nb", null, false, null, null, "NotFound")]
    [TestCase(null, 12, null, "a\nb", false, null, null, "NotFound")]
    [TestCase(null, null, "{", null, false, null, null, "Ambiguous")]
    [TestCase(null, 0, null, null, false, null, null, "Ambiguous")]
    [TestCase("  ", null, null, null, false, null, null, "Ambiguous")]
    public void Corpus_ChoosesTheMatchTheLegacyFindExactSnippetPositionChose(
        string? withinMember, int? matchLine, string? lineBefore, string? lineAfter, bool newlineSnippet,
        int? legacyStart, int? legacyLength, string? legacyError)
    {
        var text = SourceText.From(Source);
        var snippet = newlineSnippet ? NewlineSnippet : Snippet;
        var memberNameAt = MemberByLines(text);

        var result = SnippetResolver.Resolve(
            text, snippet, WhitespaceTolerance.IgnoreLineEndings, SnippetCaseMode.CaseSensitive,
            new SnippetSelectors(withinMember, matchLine, lineBefore, lineAfter), memberNameAt);

        if (legacyError == null)
        {
            Assert.Multiple(() =>
            {
                Assert.That(result.Outcome, Is.EqualTo(SnippetMatchOutcome.Matched));
                Assert.That((result.Single().Start, result.Single().Length), Is.EqualTo((legacyStart, legacyLength)));
            });
        }
        else if (legacyError == "Ambiguous")
        {
            Assert.Multiple(() =>
            {
                Assert.That(result.IsSingleMatch, Is.False);
                Assert.That(result.Outcome, Is.AnyOf(SnippetMatchOutcome.Ambiguous, SnippetMatchOutcome.StillAmbiguous));
            });
        }
        else
        {
            Assert.Multiple(() =>
            {
                Assert.That(result.IsSingleMatch, Is.False);
                Assert.That(result.Outcome, Is.AnyOf(
                    SnippetMatchOutcome.SelectorMismatch, SnippetMatchOutcome.AdjacentLineMismatch,
                    SnippetMatchOutcome.InvalidDisambiguator, SnippetMatchOutcome.NoMatch));
            });
        }
    }
}
