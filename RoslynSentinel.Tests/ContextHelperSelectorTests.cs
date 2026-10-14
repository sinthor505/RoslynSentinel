using Microsoft.CodeAnalysis.Text;
using RoslynSentinel.Common.SnippetMatching;

namespace RoslynSentinel.Tests;

[Parallelizable(ParallelScope.All)]
public class ContextHelperSelectorTests
{
    // Snippet occurs at lines 5-6 (in A), 12-13 (in B) and 19-20 (in the B(int) overload).
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

    [Test]
    public void MatchLine_SelectsTheIntendedMatch()
    {
        var result = SnippetResolver.Resolve(
            SourceText.From(Source), Snippet, WhitespaceTolerance.IgnoreLineEndings, SnippetCaseMode.CaseSensitive,
            new SnippetSelectors(MatchLine: 12), memberNameAt: null);

        Assert.Multiple(() =>
        {
            Assert.That(result.Outcome, Is.EqualTo(SnippetMatchOutcome.Matched));
            Assert.That(result.Single().Start, Is.EqualTo(OffsetOfSnippetAtLine(12)));
        });
    }

    [Test]
    public void MatchLine_MatchingNone_ThrowsDistinctErrorListingCurrentMatchLines()
    {
        var text = SourceText.From(Source);
        var result = SnippetResolver.Resolve(
            text, Snippet, WhitespaceTolerance.IgnoreLineEndings, SnippetCaseMode.CaseSensitive,
            new SnippetSelectors(MatchLine: 99), memberNameAt: null);
        var ex = SnippetErrorFormatter.Build(result, text, Snippet, "oldContent", selectorsSupported: true);

        Assert.Multiple(() =>
        {
            Assert.That(result.Outcome, Is.EqualTo(SnippetMatchOutcome.SelectorMismatch));
            Assert.That(ex, Is.InstanceOf<ToolNotFoundException>());
            Assert.That(ex.Message, Does.Contain("was found (3 match(es)), but not at the matchLine you supplied"));
            Assert.That(ex.Message, Does.Contain("matchLine removed 3"));
            Assert.That(ex.Message, Does.Contain("Candidate 1 of 3 (lines 5-6"));
            Assert.That(ex.Message, Does.Contain("Candidate 2 of 3 (lines 12-13"));
            Assert.That(ex.Message, Does.Contain("Candidate 3 of 3 (lines 19-20"));
            Assert.That(ex.Message, Does.Not.Contain("not in the arrangement you supplied"));
        });
    }

    [Test]
    public void MatchLine_MatchingNone_WithMemberNameAt_NamesEnclosingMembersInListing()
    {
        var text = SourceText.From(Source);

        var memberNameAt = MemberByLines(text);
        var result = SnippetResolver.Resolve(
            text, Snippet, WhitespaceTolerance.IgnoreLineEndings, SnippetCaseMode.CaseSensitive,
            new SnippetSelectors(MatchLine: 99), memberNameAt);
        var ex = SnippetErrorFormatter.Build(result, text, Snippet, "oldContent", memberNameAt, selectorsSupported: true);

        Assert.Multiple(() =>
        {
            Assert.That(result.Outcome, Is.EqualTo(SnippetMatchOutcome.SelectorMismatch));
            Assert.That(ex, Is.InstanceOf<ToolNotFoundException>());
            Assert.That(ex.Message, Does.Contain("Candidate 1 of 3 (lines 5-6, in A)"));
            Assert.That(ex.Message, Does.Contain("Candidate 2 of 3 (lines 12-13, in B)"));
        });
    }

    [Test]
    public void WithinMember_SelectsTheMatchInThatMember()
    {
        var text = SourceText.From(Source);

        var result = SnippetResolver.Resolve(
            text, Snippet, WhitespaceTolerance.IgnoreLineEndings, SnippetCaseMode.CaseSensitive,
            new SnippetSelectors(WithinMember: "A"), MemberByLines(text));

        Assert.Multiple(() =>
        {
            Assert.That(result.Outcome, Is.EqualTo(SnippetMatchOutcome.Matched));
            Assert.That(result.Single().Start, Is.EqualTo(OffsetOfSnippetAtLine(5)));
        });
    }

    [Test]
    public void WithinMember_MatchingNone_ThrowsDistinctErrorNamingTheMember()
    {
        var text = SourceText.From(Source);

        var memberNameAt = MemberByLines(text);
        var result = SnippetResolver.Resolve(
            text, Snippet, WhitespaceTolerance.IgnoreLineEndings, SnippetCaseMode.CaseSensitive,
            new SnippetSelectors(WithinMember: "Missing"), memberNameAt);
        var ex = SnippetErrorFormatter.Build(result, text, Snippet, "oldContent", memberNameAt, selectorsSupported: true);

        Assert.Multiple(() =>
        {
            Assert.That(result.Outcome, Is.EqualTo(SnippetMatchOutcome.SelectorMismatch));
            Assert.That(ex, Is.InstanceOf<ToolNotFoundException>());
            Assert.That(ex.Message, Does.Contain("but not at the withinMember you supplied"));
            Assert.That(ex.Message, Does.Contain("withinMember removed 3"));
            Assert.That(ex.Message, Does.Contain("Candidate 1 of 3 (lines 5-6, in A)"));
        });
    }

    [Test]
    public void WithinMemberAndMatchLine_AreIntersected()
    {
        var text = SourceText.From(Source);
        var memberNameAt = MemberByLines(text);

        var matched = SnippetResolver.Resolve(
            text, Snippet, WhitespaceTolerance.IgnoreLineEndings, SnippetCaseMode.CaseSensitive,
            new SnippetSelectors(WithinMember: "B", MatchLine: 19), memberNameAt);
        var mismatched = SnippetResolver.Resolve(
            text, Snippet, WhitespaceTolerance.IgnoreLineEndings, SnippetCaseMode.CaseSensitive,
            new SnippetSelectors(WithinMember: "A", MatchLine: 12), memberNameAt);
        var ex = SnippetErrorFormatter.Build(mismatched, text, Snippet, "oldContent", memberNameAt, selectorsSupported: true);

        Assert.Multiple(() =>
        {
            Assert.That(matched.Outcome, Is.EqualTo(SnippetMatchOutcome.Matched));
            Assert.That(matched.Single().Start, Is.EqualTo(OffsetOfSnippetAtLine(19)));
            Assert.That(mismatched.Outcome, Is.EqualTo(SnippetMatchOutcome.SelectorMismatch));
            Assert.That(ex, Is.InstanceOf<ToolNotFoundException>());
            Assert.That(ex.Message, Does.Contain("but not at the matchLine/withinMember you supplied"));
            Assert.That(ex.Message, Does.Contain("matchLine removed 2, withinMember removed 1"));
        });
    }

    [Test]
    public void WithinMember_OverloadLikeDuplicateNames_StaysAmbiguousWithoutMatchLineAndResolvesWithIt()
    {
        var text = SourceText.From(Source);
        var memberNameAt = MemberByLines(text);

        var ambiguous = SnippetResolver.Resolve(
            text, Snippet, WhitespaceTolerance.IgnoreLineEndings, SnippetCaseMode.CaseSensitive,
            new SnippetSelectors(WithinMember: "B"), memberNameAt);
        var ex = SnippetErrorFormatter.Build(ambiguous, text, Snippet, "oldContent", memberNameAt, selectorsSupported: true);
        var resolved = SnippetResolver.Resolve(
            text, Snippet, WhitespaceTolerance.IgnoreLineEndings, SnippetCaseMode.CaseSensitive,
            new SnippetSelectors(WithinMember: "B", MatchLine: 19), memberNameAt);

        Assert.Multiple(() =>
        {
            Assert.That(ambiguous.Outcome, Is.EqualTo(SnippetMatchOutcome.StillAmbiguous));
            Assert.That(ex, Is.InstanceOf<ToolAmbiguousMatchException>());
            Assert.That(ex.Message, Does.Contain("oldContent is still ambiguous: 2 matches remain after the selectors you supplied"));
            Assert.That(ex.Message, Does.Contain("Candidate 1 of 2 (lines 12-13, in B)"));
            Assert.That(ex.Message, Does.Contain("Candidate 2 of 2 (lines 19-20, in B)"));
            Assert.That(resolved.Outcome, Is.EqualTo(SnippetMatchOutcome.Matched));
            Assert.That(resolved.Single().Start, Is.EqualTo(OffsetOfSnippetAtLine(19)));
        });
    }

    [Test]
    public void Selectors_ActBeforeLineAfter_SelectorNarrowsToTwoThenLineAfterPicksOne()
    {
        var text = SourceText.From(Source);

        var result = SnippetResolver.Resolve(
            text, Snippet, WhitespaceTolerance.IgnoreLineEndings, SnippetCaseMode.CaseSensitive,
            new SnippetSelectors(WithinMember: "B", LineAfter: "throw;"), MemberByLines(text));

        Assert.Multiple(() =>
        {
            Assert.That(result.Outcome, Is.EqualTo(SnippetMatchOutcome.Matched));
            Assert.That(result.Single().Start, Is.EqualTo(OffsetOfSnippetAtLine(19)));
        });
    }

    [Test]
    public void Selectors_ThenLineAfterFiltersOutEverySurvivor_ReportsAdjacentLineError()
    {
        var text = SourceText.From(Source);

        var memberNameAt = MemberByLines(text);
        var result = SnippetResolver.Resolve(
            text, Snippet, WhitespaceTolerance.IgnoreLineEndings, SnippetCaseMode.CaseSensitive,
            new SnippetSelectors(WithinMember: "B", LineAfter: "nonexistent;"), memberNameAt);
        var ex = SnippetErrorFormatter.Build(result, text, Snippet, "oldContent", memberNameAt, selectorsSupported: true);

        Assert.Multiple(() =>
        {
            Assert.That(result.Outcome, Is.EqualTo(SnippetMatchOutcome.AdjacentLineMismatch));
            Assert.That(ex, Is.InstanceOf<ToolNotFoundException>());
            Assert.That(ex.Message, Does.Contain("was found (2 match(es)), but not next to the lineAfter you supplied"));
            Assert.That(ex.Message, Does.Contain("withinMember removed 1, lineAfter removed 2"));
            Assert.That(ex.Message, Does.Not.Contain("not in the arrangement you supplied"));
        });
    }

    [Test]
    public void Selectors_StillRejectMultiLineLineAfter()
    {
        var text = SourceText.From(Source);
        var result = SnippetResolver.Resolve(
            text, Snippet, WhitespaceTolerance.IgnoreLineEndings, SnippetCaseMode.CaseSensitive,
            new SnippetSelectors(MatchLine: 12, LineAfter: "a\nb"), memberNameAt: null);
        var ex = SnippetErrorFormatter.Build(result, text, Snippet, "oldContent", selectorsSupported: true);

        Assert.Multiple(() =>
        {
            Assert.That(result.Outcome, Is.EqualTo(SnippetMatchOutcome.InvalidDisambiguator));
            Assert.That(ex, Is.InstanceOf<ToolNotFoundException>());
            Assert.That(ex.Message, Does.Contain("lineAfter must be a single line"));
        });
    }

    [Test]
    public void WithinMember_WithoutMemberNameAt_ThrowsActionableError()
    {
        var text = SourceText.From(Source);
        var result = SnippetResolver.Resolve(
            text, Snippet, WhitespaceTolerance.IgnoreLineEndings, SnippetCaseMode.CaseSensitive,
            new SnippetSelectors(WithinMember: "A"), memberNameAt: null);
        var ex = SnippetErrorFormatter.Build(result, text, Snippet, "oldContent", selectorsSupported: true);

        Assert.Multiple(() =>
        {
            Assert.That(result.Outcome, Is.EqualTo(SnippetMatchOutcome.InvalidDisambiguator));
            Assert.That(ex, Is.InstanceOf<ToolNotFoundException>());
            Assert.That(ex.Message, Does.Contain("withinMember (\"A\") cannot be applied"));
            Assert.That(ex.Message, Does.Contain("member names cannot be resolved"));
            Assert.That(ex.Message, Does.Contain("matchLine"));
        });
    }

    [Test]
    public void MatchLine_MatchBeginningWithNewline_IsAttributedToItsFirstNonWhitespaceLine()
    {
        // Match 2 starts with the newline ending the "{" line (11), but a human sees "x = 1;" on line 12.
        const string newlineSnippet = "\n        x = 1;\n        y = 2;";

        var text = SourceText.From(Source);
        var matched = SnippetResolver.Resolve(
            text, newlineSnippet, WhitespaceTolerance.IgnoreLineEndings, SnippetCaseMode.CaseSensitive,
            new SnippetSelectors(MatchLine: 12), memberNameAt: null);
        var mismatched = SnippetResolver.Resolve(
            text, newlineSnippet, WhitespaceTolerance.IgnoreLineEndings, SnippetCaseMode.CaseSensitive,
            new SnippetSelectors(MatchLine: 11), memberNameAt: null);
        var ex = SnippetErrorFormatter.Build(mismatched, text, newlineSnippet, "oldContent", selectorsSupported: true);

        Assert.Multiple(() =>
        {
            Assert.That(matched.Outcome, Is.EqualTo(SnippetMatchOutcome.Matched));
            Assert.That(matched.Single().Start, Is.EqualTo(OffsetOfSnippetAtLine(11, newlineSnippet)));
            Assert.That(mismatched.Outcome, Is.EqualTo(SnippetMatchOutcome.SelectorMismatch));
            Assert.That(ex, Is.InstanceOf<ToolNotFoundException>());
            // The heading span is the match's physical lines (it starts with the newline ending line 11); the
            // first-non-whitespace attribution to line 12 is carried by the matchLine selector the candidate offers.
            Assert.That(ex.Message, Does.Contain("Candidate 2 of 3 (lines 11-13):\n  matchLine=12 lineBefore="));
        });
    }

    [Test]
    public void WithinMember_MatchBeginningWithNewline_LooksUpMemberAtFirstNonWhitespaceCharacter()
    {
        const string newlineSnippet = "\n        x = 1;\n        y = 2;";
        var offsetsAsked = new List<int>();

        SnippetResolver.Resolve(
            SourceText.From(Source), newlineSnippet, WhitespaceTolerance.IgnoreLineEndings, SnippetCaseMode.CaseSensitive,
            new SnippetSelectors(WithinMember: "A"),
            memberNameAt: offset =>
            {
                offsetsAsked.Add(offset);
                return offset == OffsetOfSnippetAtLine(5) ? "A" : "other";
            });

        Assert.That(offsetsAsked.Select(o => Source[o]), Is.All.EqualTo('x'));
    }

    [Test]
    public void ExactAmbiguous_ListsMatchLinePerMatch_AndWithinMemberOnlyForUniqueMemberNames()
    {
        var text = SourceText.From(Source);

        var memberNameAt = MemberByLines(text);
        var result = SnippetResolver.Resolve(
            text, Snippet, WhitespaceTolerance.IgnoreLineEndings, SnippetCaseMode.CaseSensitive);
        var ex = SnippetErrorFormatter.Build(result, text, Snippet, "oldContent", memberNameAt, selectorsSupported: true);

        Assert.Multiple(() =>
        {
            Assert.That(result.Outcome, Is.EqualTo(SnippetMatchOutcome.Ambiguous));
            Assert.That(ex, Is.InstanceOf<ToolAmbiguousMatchException>());
            Assert.That(ex.Message, Does.Contain("Candidate 1 of 3 (lines 5-6, in A):\n  withinMember=\"A\" matchLine=5 lineBefore="));
            Assert.That(ex.Message, Does.Contain("Candidate 2 of 3 (lines 12-13, in B):\n  matchLine=12 lineBefore="));
            Assert.That(ex.Message, Does.Contain("Candidate 3 of 3 (lines 19-20, in B):\n  matchLine=19 lineBefore="));
            Assert.That(ex.Message, Does.Not.Contain("withinMember=\"B\""), "B names two matches, so it cannot separate either");
        });
    }

    [Test]
    public void ExactAmbiguous_TieAdvicePointsAtTheSelectors_NotAtAnotherTool()
    {
        var text = SourceText.From(Source);

        var result = SnippetResolver.Resolve(
            text, Snippet, WhitespaceTolerance.IgnoreLineEndings, SnippetCaseMode.CaseSensitive);
        var ex = SnippetErrorFormatter.Build(
            result, text, Snippet, "oldContent", MemberByLines(text), selectorsSupported: true);

        Assert.Multiple(() =>
        {
            Assert.That(result.Outcome, Is.EqualTo(SnippetMatchOutcome.Ambiguous));
            Assert.That(ex, Is.InstanceOf<ToolAmbiguousMatchException>());
            Assert.That(ex.Message, Does.Contain(
                "Candidates 1 and 2 have identical surrounding lines, so lineBefore/lineAfter cannot tell them apart. " +
                "Use matchLine (and withinMember where listed), or widen oldContent."));
            Assert.That(ex.Message, Does.Not.Contain("different tool"));
            Assert.That(ex.Message, Does.Not.Contain("longer oldContent"));
        });
    }

    [Test]
    public void ExactAmbiguous_SingleLineMatch_PrintsLineNotALineRange()
    {
        var text = SourceText.From(Source);

        var result = SnippetResolver.Resolve(
            text, "return;", WhitespaceTolerance.IgnoreLineEndings, SnippetCaseMode.CaseSensitive);
        var ex = SnippetErrorFormatter.Build(
            result, text, "return;", "oldContent", MemberByLines(text), selectorsSupported: true);

        Assert.Multiple(() =>
        {
            Assert.That(result.Outcome, Is.EqualTo(SnippetMatchOutcome.Ambiguous));
            Assert.That(ex, Is.InstanceOf<ToolAmbiguousMatchException>());
            Assert.That(ex.Message, Does.Contain("Candidate 1 of 2 (line 7, in A):\n  withinMember=\"A\" matchLine=7 lineBefore="));
            Assert.That(ex.Message, Does.Contain("Candidate 2 of 2 (line 14, in B):\n  withinMember=\"B\" matchLine=14 lineBefore="));
        });
    }

    [Test]
    public void ExactStillAmbiguous_ListsTheSameSelectorFormat()
    {
        var text = SourceText.From(Source);

        // Every match is preceded by a line containing "{", so lineBefore alone does not narrow anything.
        var memberNameAt = MemberByLines(text);
        var result = SnippetResolver.Resolve(
            text, Snippet, WhitespaceTolerance.IgnoreLineEndings, SnippetCaseMode.CaseSensitive,
            new SnippetSelectors(LineBefore: "{"), memberNameAt);
        var ex = SnippetErrorFormatter.Build(result, text, Snippet, "oldContent", memberNameAt, selectorsSupported: true);

        Assert.Multiple(() =>
        {
            Assert.That(result.Outcome, Is.EqualTo(SnippetMatchOutcome.StillAmbiguous));
            Assert.That(ex, Is.InstanceOf<ToolAmbiguousMatchException>());
            Assert.That(ex.Message, Does.Contain("is still ambiguous: 3 matches remain after the selectors you supplied"));
            Assert.That(ex.Message, Does.Contain("pass matchLine (and withinMember where listed) copied from one candidate below"));
            Assert.That(ex.Message, Does.Contain("Candidate 1 of 3 (lines 5-6, in A):\n  withinMember=\"A\" matchLine=5 lineBefore="));
            Assert.That(ex.Message, Does.Contain("Candidate 3 of 3 (lines 19-20, in B):\n  matchLine=19 lineBefore="));
            Assert.That(ex.Message, Does.Not.Contain("different tool"));
        });
    }

    [Test]
    public void SelectorMismatch_DoesNotClaimAVerbatimFailure_AndListsCopyableSelectors()
    {
        var text = SourceText.From(Source);

        var memberNameAt = MemberByLines(text);
        var result = SnippetResolver.Resolve(
            text, Snippet, WhitespaceTolerance.IgnoreLineEndings, SnippetCaseMode.CaseSensitive,
            new SnippetSelectors(MatchLine: 99), memberNameAt);
        var ex = SnippetErrorFormatter.Build(result, text, Snippet, "oldContent", memberNameAt, selectorsSupported: true);

        Assert.Multiple(() =>
        {
            Assert.That(result.Outcome, Is.EqualTo(SnippetMatchOutcome.SelectorMismatch));
            Assert.That(ex, Is.InstanceOf<ToolNotFoundException>());
            Assert.That(ex.Message, Does.StartWith("oldContent was found (3 match(es)), but not at the matchLine you supplied"));
            Assert.That(ex.Message, Does.Not.Contain("verbatim"));
            Assert.That(ex.Message, Does.Not.Contain("Re-read the file"));
            Assert.That(ex.Message, Does.Not.Contain("could not be located"));
            Assert.That(ex.Message, Does.Contain("matchLine removed 3"));
            Assert.That(ex.Message, Does.Contain("Candidate 1 of 3 (lines 5-6, in A):\n  withinMember=\"A\" matchLine=5 "));
            Assert.That(ex.Message, Does.Contain("matchLine=12 "));
            Assert.That(ex.Message, Does.Contain("matchLine=19 "));
        });
    }

    [TestCase("")]
    [TestCase("  ")]
    public void BlankWithinMember_IsTreatedAsUnset(string blank)
    {
        var text = SourceText.From(Source);

        // No memberNameAt: a blank withinMember must not trigger the "cannot be applied" error either.
        var memberNameAt = MemberByLines(text);
        var match = SnippetResolver.Resolve(
            text, Snippet, WhitespaceTolerance.IgnoreLineEndings, SnippetCaseMode.CaseSensitive,
            new SnippetSelectors(WithinMember: blank, MatchLine: 12), memberNameAt: null);
        var blankResult = SnippetResolver.Resolve(
            text, Snippet, WhitespaceTolerance.IgnoreLineEndings, SnippetCaseMode.CaseSensitive,
            new SnippetSelectors(WithinMember: blank), memberNameAt);
        var noneResult = SnippetResolver.Resolve(
            text, Snippet, WhitespaceTolerance.IgnoreLineEndings, SnippetCaseMode.CaseSensitive,
            new SnippetSelectors(), memberNameAt);
        var blankEx = SnippetErrorFormatter.Build(blankResult, text, Snippet, "oldContent", memberNameAt, selectorsSupported: true);
        var noneEx = SnippetErrorFormatter.Build(noneResult, text, Snippet, "oldContent", memberNameAt, selectorsSupported: true);

        Assert.Multiple(() =>
        {
            Assert.That(match.Outcome, Is.EqualTo(SnippetMatchOutcome.Matched));
            Assert.That(match.Single().Start, Is.EqualTo(OffsetOfSnippetAtLine(12)));
            Assert.That(blankResult.Outcome, Is.EqualTo(SnippetMatchOutcome.Ambiguous));
            Assert.That(noneResult.Outcome, Is.EqualTo(SnippetMatchOutcome.Ambiguous));
            Assert.That(blankEx, Is.InstanceOf<ToolAmbiguousMatchException>());
            Assert.That(blankEx.Message, Is.EqualTo(noneEx.Message));
        });
    }

    [TestCase(0)]
    [TestCase(-1)]
    public void NonPositiveMatchLine_IsTreatedAsUnset(int unset)
    {
        var text = SourceText.From(Source);
        var memberNameAt = MemberByLines(text);

        var unsetResult = SnippetResolver.Resolve(
            text, Snippet, WhitespaceTolerance.IgnoreLineEndings, SnippetCaseMode.CaseSensitive,
            new SnippetSelectors(MatchLine: unset), memberNameAt);
        var noneResult = SnippetResolver.Resolve(
            text, Snippet, WhitespaceTolerance.IgnoreLineEndings, SnippetCaseMode.CaseSensitive,
            new SnippetSelectors(), memberNameAt);
        var match = SnippetResolver.Resolve(
            text, Snippet, WhitespaceTolerance.IgnoreLineEndings, SnippetCaseMode.CaseSensitive,
            new SnippetSelectors(WithinMember: "A", MatchLine: unset), memberNameAt);
        var unsetEx = SnippetErrorFormatter.Build(unsetResult, text, Snippet, "oldContent", memberNameAt, selectorsSupported: true);
        var noneEx = SnippetErrorFormatter.Build(noneResult, text, Snippet, "oldContent", memberNameAt, selectorsSupported: true);

        Assert.Multiple(() =>
        {
            Assert.That(unsetResult.Outcome, Is.EqualTo(SnippetMatchOutcome.Ambiguous));
            Assert.That(noneResult.Outcome, Is.EqualTo(SnippetMatchOutcome.Ambiguous));
            Assert.That(unsetEx, Is.InstanceOf<ToolAmbiguousMatchException>());
            Assert.That(unsetEx.Message, Is.EqualTo(noneEx.Message));
            Assert.That(match.Outcome, Is.EqualTo(SnippetMatchOutcome.Matched));
            Assert.That(match.Single().Start, Is.EqualTo(OffsetOfSnippetAtLine(5)));
        });
    }

    [Test]
    public void TolerantMatcher_AmbiguousMessage_IsUnchangedAndOffersNoSelectors()
    {
        var ex = Assert.Throws<ToolAmbiguousMatchException>(() => SnippetLookup.ResolveOrThrow(
            SourceText.From(Source), Snippet));

        Assert.Multiple(() =>
        {
            Assert.That(ex!.Message, Does.Contain(
                "pass lineBefore and/or lineAfter copied verbatim from one candidate below, or widen contextSnippet so it matches one place only."));
            Assert.That(ex.Message, Does.Contain("Candidate 1 of 3 (lines 5-6):\n  lineBefore=\"{\" lineAfter=\"return;\""));
            Assert.That(ex.Message, Does.Contain("cannot tell them apart. Widen contextSnippet with distinguishing text, or use a different tool."));
            Assert.That(ex.Message, Does.Not.Contain("matchLine"));
            Assert.That(ex.Message, Does.Not.Contain("withinMember"));
        });
    }
}
