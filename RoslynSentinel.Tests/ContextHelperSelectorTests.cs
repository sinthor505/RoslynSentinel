using Microsoft.CodeAnalysis.Text;

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
        var match = ContextHelper.FindExactSnippetPosition(SourceText.From(Source), Snippet, matchLine: 12);

        Assert.That(match.Start, Is.EqualTo(OffsetOfSnippetAtLine(12)));
    }

    [Test]
    public void MatchLine_MatchingNone_ThrowsDistinctErrorListingCurrentMatchLines()
    {
        var ex = Assert.Throws<ToolNotFoundException>(() => ContextHelper.FindExactSnippetPosition(
            SourceText.From(Source), Snippet, matchLine: 99));

        Assert.Multiple(() =>
        {
            Assert.That(ex!.Message, Does.Contain("matches 3 place(s) exactly, but none satisfied the selector(s) you supplied (matchLine=99)"));
            Assert.That(ex.Message, Does.Contain("Match 1 (lines 5-6"));
            Assert.That(ex.Message, Does.Contain("Match 2 (lines 12-13"));
            Assert.That(ex.Message, Does.Contain("Match 3 (lines 19-20"));
            Assert.That(ex.Message, Does.Not.Contain("not in the arrangement you supplied"));
        });
    }

    [Test]
    public void MatchLine_MatchingNone_WithMemberNameAt_NamesEnclosingMembersInListing()
    {
        var text = SourceText.From(Source);

        var ex = Assert.Throws<ToolNotFoundException>(() => ContextHelper.FindExactSnippetPosition(
            text, Snippet, memberNameAt: MemberByLines(text), matchLine: 99));

        Assert.Multiple(() =>
        {
            Assert.That(ex!.Message, Does.Contain("Match 1 (lines 5-6, in A)"));
            Assert.That(ex.Message, Does.Contain("Match 2 (lines 12-13, in B)"));
        });
    }

    [Test]
    public void WithinMember_SelectsTheMatchInThatMember()
    {
        var text = SourceText.From(Source);

        var match = ContextHelper.FindExactSnippetPosition(
            text, Snippet, memberNameAt: MemberByLines(text), withinMember: "A");

        Assert.That(match.Start, Is.EqualTo(OffsetOfSnippetAtLine(5)));
    }

    [Test]
    public void WithinMember_MatchingNone_ThrowsDistinctErrorNamingTheMember()
    {
        var text = SourceText.From(Source);

        var ex = Assert.Throws<ToolNotFoundException>(() => ContextHelper.FindExactSnippetPosition(
            text, Snippet, memberNameAt: MemberByLines(text), withinMember: "Missing"));

        Assert.Multiple(() =>
        {
            Assert.That(ex!.Message, Does.Contain("none satisfied the selector(s) you supplied (withinMember=\"Missing\")"));
            Assert.That(ex.Message, Does.Contain("Match 1 (lines 5-6, in A)"));
        });
    }

    [Test]
    public void WithinMemberAndMatchLine_AreIntersected()
    {
        var text = SourceText.From(Source);
        var memberNameAt = MemberByLines(text);

        var match = ContextHelper.FindExactSnippetPosition(
            text, Snippet, memberNameAt: memberNameAt, withinMember: "B", matchLine: 19);
        var ex = Assert.Throws<ToolNotFoundException>(() => ContextHelper.FindExactSnippetPosition(
            text, Snippet, memberNameAt: memberNameAt, withinMember: "A", matchLine: 12));

        Assert.Multiple(() =>
        {
            Assert.That(match.Start, Is.EqualTo(OffsetOfSnippetAtLine(19)));
            Assert.That(ex!.Message, Does.Contain("matchLine=12, withinMember=\"A\""));
        });
    }

    [Test]
    public void WithinMember_OverloadLikeDuplicateNames_StaysAmbiguousWithoutMatchLineAndResolvesWithIt()
    {
        var text = SourceText.From(Source);
        var memberNameAt = MemberByLines(text);

        var ex = Assert.Throws<ToolAmbiguousMatchException>(() => ContextHelper.FindExactSnippetPosition(
            text, Snippet, memberNameAt: memberNameAt, withinMember: "B"));
        var match = ContextHelper.FindExactSnippetPosition(
            text, Snippet, memberNameAt: memberNameAt, withinMember: "B", matchLine: 19);

        Assert.Multiple(() =>
        {
            Assert.That(ex!.Message, Does.Contain("oldContent is ambiguous (2 matches)"));
            Assert.That(ex.Message, Does.Contain("Match 1 (lines 12-13, in B)"));
            Assert.That(ex.Message, Does.Contain("Match 2 (lines 19-20, in B)"));
            Assert.That(match.Start, Is.EqualTo(OffsetOfSnippetAtLine(19)));
        });
    }

    [Test]
    public void Selectors_ActBeforeLineAfter_SelectorNarrowsToTwoThenLineAfterPicksOne()
    {
        var text = SourceText.From(Source);

        var match = ContextHelper.FindExactSnippetPosition(
            text, Snippet, lineAfter: "throw;", memberNameAt: MemberByLines(text), withinMember: "B");

        Assert.That(match.Start, Is.EqualTo(OffsetOfSnippetAtLine(19)));
    }

    [Test]
    public void Selectors_ThenLineAfterFiltersOutEverySurvivor_ReportsAdjacentLineError()
    {
        var text = SourceText.From(Source);

        var ex = Assert.Throws<ToolNotFoundException>(() => ContextHelper.FindExactSnippetPosition(
            text, Snippet, lineAfter: "nonexistent;", memberNameAt: MemberByLines(text), withinMember: "B"));

        Assert.Multiple(() =>
        {
            Assert.That(ex!.Message, Does.Contain("matches 2 place(s) exactly (after the withinMember/matchLine selector)"));
            Assert.That(ex.Message, Does.Contain("filtered out all of them"));
            Assert.That(ex.Message, Does.Not.Contain("not in the arrangement you supplied"));
        });
    }

    [Test]
    public void Selectors_StillRejectMultiLineLineAfter()
    {
        var ex = Assert.Throws<ToolNotFoundException>(() => ContextHelper.FindExactSnippetPosition(
            SourceText.From(Source), Snippet, lineAfter: "a\nb", matchLine: 12));

        Assert.That(ex!.Message, Does.Contain("lineAfter must be a single line"));
    }

    [Test]
    public void WithinMember_WithoutMemberNameAt_ThrowsActionableError()
    {
        var ex = Assert.Throws<ToolNotFoundException>(() => ContextHelper.FindExactSnippetPosition(
            SourceText.From(Source), Snippet, withinMember: "A"));

        Assert.Multiple(() =>
        {
            Assert.That(ex!.Message, Does.Contain("withinMember cannot be applied"));
            Assert.That(ex.Message, Does.Contain("no syntax tree"));
            Assert.That(ex.Message, Does.Contain("matchLine"));
        });
    }

    [Test]
    public void NoSelectors_KeepsExistingAmbiguousBehaviourAndWording()
    {
        var ex = Assert.Throws<ToolAmbiguousMatchException>(() => ContextHelper.FindExactSnippetPosition(
            SourceText.From(Source), Snippet));

        Assert.Multiple(() =>
        {
            Assert.That(ex!.Message, Does.StartWith("oldContent is ambiguous (3 matches): \"x = 1;\n        y = 2;\". "));
            Assert.That(ex.Message, Does.Contain(
                "Provide matchLine (and withinMember where listed) copied verbatim from one of the matches below to select the intended match; lineBefore/lineAfter may be added as an extra filter:\n"));
            Assert.That(ex.Message, Does.Contain("Match 1 (lines 5-6): matchLine=5 lineBefore=\"{\" lineAfter=\"return;\""));
            Assert.That(ex.Message, Does.Not.Contain("withinMember=\""), "no member lookup was supplied, so no member can be offered");
        });
    }

    [Test]
    public void MatchLine_MatchBeginningWithNewline_IsAttributedToItsFirstNonWhitespaceLine()
    {
        // Match 2 starts with the newline ending the "{" line (11), but a human sees "x = 1;" on line 12.
        const string newlineSnippet = "\n        x = 1;\n        y = 2;";

        var match = ContextHelper.FindExactSnippetPosition(
            SourceText.From(Source), newlineSnippet, matchLine: 12);
        var ex = Assert.Throws<ToolNotFoundException>(() => ContextHelper.FindExactSnippetPosition(
            SourceText.From(Source), newlineSnippet, matchLine: 11));

        Assert.Multiple(() =>
        {
            Assert.That(match.Start, Is.EqualTo(OffsetOfSnippetAtLine(11, newlineSnippet)));
            Assert.That(ex!.Message, Does.Contain("Match 2 (lines 12-13"));
        });
    }

    [Test]
    public void WithinMember_MatchBeginningWithNewline_LooksUpMemberAtFirstNonWhitespaceCharacter()
    {
        const string newlineSnippet = "\n        x = 1;\n        y = 2;";
        var offsetsAsked = new List<int>();

        ContextHelper.FindExactSnippetPosition(
            SourceText.From(Source), newlineSnippet,
            memberNameAt: offset =>
            {
                offsetsAsked.Add(offset);
                return offset == OffsetOfSnippetAtLine(5) ? "A" : "other";
            },
            withinMember: "A");

        Assert.That(offsetsAsked.Select(o => Source[o]), Is.All.EqualTo('x'));
    }

    [Test]
    public void ExactAmbiguous_ListsMatchLinePerMatch_AndWithinMemberOnlyForUniqueMemberNames()
    {
        var text = SourceText.From(Source);

        var ex = Assert.Throws<ToolAmbiguousMatchException>(() => ContextHelper.FindExactSnippetPosition(
            text, Snippet, memberNameAt: MemberByLines(text)));

        Assert.Multiple(() =>
        {
            Assert.That(ex!.Message, Does.Contain("Match 1 (lines 5-6, in A): withinMember=\"A\" matchLine=5 lineBefore="));
            Assert.That(ex.Message, Does.Contain("Match 2 (lines 12-13, in B): matchLine=12 lineBefore="));
            Assert.That(ex.Message, Does.Contain("Match 3 (lines 19-20, in B): matchLine=19 lineBefore="));
            Assert.That(ex.Message, Does.Not.Contain("withinMember=\"B\""), "B names two matches, so it cannot separate either");
        });
    }

    [Test]
    public void ExactAmbiguous_TieAdvicePointsAtTheSelectors_NotAtAnotherTool()
    {
        var text = SourceText.From(Source);

        var ex = Assert.Throws<ToolAmbiguousMatchException>(() => ContextHelper.FindExactSnippetPosition(
            text, Snippet, memberNameAt: MemberByLines(text)));

        Assert.Multiple(() =>
        {
            Assert.That(ex!.Message, Does.Contain(
                "Matches 1 and 2 have identical surrounding lines, so lineBefore/lineAfter cannot tell them apart. " +
                "Pass matchLine (and withinMember where listed) copied from the list above, or widen oldContent."));
            Assert.That(ex.Message, Does.Not.Contain("different tool"));
            Assert.That(ex.Message, Does.Not.Contain("longer oldContent"));
        });
    }

    [Test]
    public void ExactAmbiguous_SingleLineMatch_PrintsLineNotALineRange()
    {
        var text = SourceText.From(Source);

        var ex = Assert.Throws<ToolAmbiguousMatchException>(() => ContextHelper.FindExactSnippetPosition(
            text, "return;", memberNameAt: MemberByLines(text)));

        Assert.Multiple(() =>
        {
            Assert.That(ex!.Message, Does.Contain("Match 1 (line 7, in A): withinMember=\"A\" matchLine=7 lineBefore="));
            Assert.That(ex.Message, Does.Contain("Match 2 (line 14, in B): withinMember=\"B\" matchLine=14 lineBefore="));
        });
    }

    [Test]
    public void ExactStillAmbiguous_ListsTheSameSelectorFormat()
    {
        var text = SourceText.From(Source);

        // Every match is preceded by a line containing "{", so lineBefore alone does not narrow anything.
        var ex = Assert.Throws<ToolAmbiguousMatchException>(() => ContextHelper.FindExactSnippetPosition(
            text, Snippet, lineBefore: "{", memberNameAt: MemberByLines(text)));

        Assert.Multiple(() =>
        {
            Assert.That(ex!.Message, Does.Contain("is still ambiguous (3 matches remain)"));
            Assert.That(ex.Message, Does.Contain("Pass matchLine (and withinMember where listed) copied from the candidates below"));
            Assert.That(ex.Message, Does.Contain("Match 1 (lines 5-6, in A): withinMember=\"A\" matchLine=5 lineBefore="));
            Assert.That(ex.Message, Does.Contain("Match 3 (lines 19-20, in B): matchLine=19 lineBefore="));
            Assert.That(ex.Message, Does.Not.Contain("different tool"));
        });
    }

    [Test]
    public void SelectorMismatch_DoesNotClaimAVerbatimFailure_AndListsCopyableSelectors()
    {
        var text = SourceText.From(Source);

        var ex = Assert.Throws<ToolNotFoundException>(() => ContextHelper.FindExactSnippetPosition(
            text, Snippet, memberNameAt: MemberByLines(text), matchLine: 99));

        Assert.Multiple(() =>
        {
            Assert.That(ex!.Message, Does.StartWith("oldContent was found, but not at the withinMember/matchLine you supplied: "));
            Assert.That(ex.Message, Does.Not.Contain("verbatim"));
            Assert.That(ex.Message, Does.Not.Contain("Re-read the file"));
            Assert.That(ex.Message, Does.Not.Contain("could not be located"));
            Assert.That(ex.Message, Does.Contain("matches 3 place(s) exactly, but none satisfied the selector(s) you supplied (matchLine=99)"));
            Assert.That(ex.Message, Does.Contain("Match 1 (lines 5-6, in A): withinMember=\"A\" matchLine=5 "));
            Assert.That(ex.Message, Does.Contain("matchLine=12 "));
            Assert.That(ex.Message, Does.Contain("matchLine=19 "));
        });
    }

    [Test]
    public void GenuineNoMatch_WithASelector_StillSaysVerbatimRequired()
    {
        var ex = Assert.Throws<ToolNotFoundException>(() => ContextHelper.FindExactSnippetPosition(
            SourceText.From(Source), "no such text", matchLine: 5));

        Assert.That(ex!.Message, Does.Contain("(verbatim required)").And.Contain("Re-read the file"));
    }

    [TestCase("")]
    [TestCase("  ")]
    public void BlankWithinMember_IsTreatedAsUnset(string blank)
    {
        var text = SourceText.From(Source);

        // No memberNameAt: a blank withinMember must not trigger the "cannot be applied" error either.
        var match = ContextHelper.FindExactSnippetPosition(text, Snippet, withinMember: blank, matchLine: 12);
        var blankEx = Assert.Throws<ToolAmbiguousMatchException>(() => ContextHelper.FindExactSnippetPosition(
            text, Snippet, memberNameAt: MemberByLines(text), withinMember: blank));
        var noneEx = Assert.Throws<ToolAmbiguousMatchException>(() => ContextHelper.FindExactSnippetPosition(
            text, Snippet, memberNameAt: MemberByLines(text)));

        Assert.Multiple(() =>
        {
            Assert.That(match.Start, Is.EqualTo(OffsetOfSnippetAtLine(12)));
            Assert.That(blankEx!.Message, Is.EqualTo(noneEx!.Message));
        });
    }

    [TestCase(0)]
    [TestCase(-1)]
    public void NonPositiveMatchLine_IsTreatedAsUnset(int unset)
    {
        var text = SourceText.From(Source);
        var memberNameAt = MemberByLines(text);

        var unsetEx = Assert.Throws<ToolAmbiguousMatchException>(() => ContextHelper.FindExactSnippetPosition(
            text, Snippet, memberNameAt: memberNameAt, matchLine: unset));
        var noneEx = Assert.Throws<ToolAmbiguousMatchException>(() => ContextHelper.FindExactSnippetPosition(
            text, Snippet, memberNameAt: memberNameAt));
        var match = ContextHelper.FindExactSnippetPosition(
            text, Snippet, memberNameAt: memberNameAt, withinMember: "A", matchLine: unset);

        Assert.Multiple(() =>
        {
            Assert.That(unsetEx!.Message, Is.EqualTo(noneEx!.Message));
            Assert.That(match.Start, Is.EqualTo(OffsetOfSnippetAtLine(5)));
        });
    }

    [Test]
    public void TolerantMatcher_AmbiguousMessage_IsUnchangedAndOffersNoSelectors()
    {
        var ex = Assert.Throws<ToolAmbiguousMatchException>(() => ContextHelper.FindSnippetPosition(
            SourceText.From(Source), Snippet));

        Assert.Multiple(() =>
        {
            Assert.That(ex!.Message, Does.Contain(
                "Provide lineBefore and/or lineAfter using one of the exact values below (copy verbatim, do not retype from memory) to select the intended match:\n"));
            Assert.That(ex.Message, Does.Contain("Match 1 (lines 5-6): lineBefore=\"{\" lineAfter=\"return;\""));
            Assert.That(ex.Message, Does.Contain("disambiguated by lineBefore/lineAfter alone; use a different tool or a longer contextSnippet."));
            Assert.That(ex.Message, Does.Not.Contain("matchLine"));
            Assert.That(ex.Message, Does.Not.Contain("withinMember"));
        });
    }
}
