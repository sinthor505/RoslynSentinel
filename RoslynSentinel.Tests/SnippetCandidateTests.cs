using Microsoft.CodeAnalysis.Text;
using RoslynSentinel.Common.SnippetMatching;

namespace RoslynSentinel.Tests;

/// <summary>
/// Tests for the evidence <see cref="SnippetResolver"/> populates for a failed lookup (slice 3a of the
/// whitespace-tolerance proposal): structured near-match differences, ranked closest windows, the tiers probed and
/// <see cref="SnippetMatchResult.HasNoCandidates"/>. Data only; no wording is asserted.
/// </summary>
[Parallelizable(ParallelScope.All)]
public class SnippetCandidateTests
{
    private const string Pad = "        ";

    private static string Lines(params string[] lines) => string.Join("\n", lines);

    private static SnippetMatchResult Resolve(
        string source, string snippet,
        WhitespaceTolerance tolerance = WhitespaceTolerance.Exact,
        SnippetCaseMode caseMode = SnippetCaseMode.CaseSensitive)
        => SnippetResolver.Resolve(SourceText.From(source), snippet, tolerance, caseMode);

    // The Qwen relabel shape: a snippet retyped at column 0 against a file indented by 8 spaces.
    private static readonly string QwenSource = Lines(
        "class C",
        "{",
        "    void M()",
        "    {",
        Pad + "var a = 1;",
        Pad + "var b = 2;",
        Pad + "return a + b;",
        "    }",
        "}");

    private const string QwenSnippet = "var a = 1;\nvar b = 2;\nreturn a + b;";

    private static string[] Rep(int count, params string[] pair) =>
        Enumerable.Range(0, count).SelectMany(_ => pair).ToArray();

    // ---- near-match differences ----

    [Test]
    public void QwenRelabel_ColumnZeroSnippetAgainstEightSpaceFile_IsANearMatchWithLeadingWhitespaceDifferences()
    {
        var result = Resolve(QwenSource, QwenSnippet);

        Assert.That(result.Outcome, Is.EqualTo(SnippetMatchOutcome.MatchedLooserThanRequested));
        Assert.That(result.Matches, Is.Empty);
        Assert.That(result.NearMatches, Has.Count.EqualTo(1));
        var near = result.NearMatches[0];
        Assert.That(near.LowestTier, Is.EqualTo(WhitespaceTolerance.IgnoreIndentation));
        Assert.That(near.StartLine, Is.EqualTo(5));

        // Line 1 is a fragment at its start (it ends the file line), so it is not a difference; lines 2 and 3 are.
        Assert.That(near.Differences, Has.Count.EqualTo(2));
        Assert.That(near.Differences.Select(d => d.Kind), Is.All.EqualTo(SnippetDifferenceKind.LeadingWhitespace));
        Assert.That(near.Differences.Select(d => d.Line), Is.EqualTo(new[] { 2, 3 }));
        Assert.That(near.Differences.Select(d => d.FileLine), Is.EqualTo(new[] { 6, 7 }));
        Assert.That(near.Differences.Select(d => d.Expected), Is.All.EqualTo(string.Empty));
        Assert.That(near.Differences.Select(d => d.Got), Is.All.EqualTo(Pad));
        Assert.That(near.Differences[0].ActualLineText, Is.EqualTo(Pad + "var b = 2;"));
        Assert.That(near.Differences[1].ActualLineText, Is.EqualTo(Pad + "return a + b;"));
        Assert.That(result.ClosestWindows, Is.Empty);
        Assert.That(result.HasNoCandidates, Is.False);
    }

    [Test]
    public void LeadingWhitespaceDifference_SnippetLineNumbersCountLeadingBlankLines()
    {
        var result = Resolve(QwenSource, "\nvar a = 1;\nvar b = 2;");

        Assert.That(result.Outcome, Is.EqualTo(SnippetMatchOutcome.MatchedLooserThanRequested));
        var difference = result.NearMatches[0].Differences.Single();
        Assert.That(difference.Kind, Is.EqualTo(SnippetDifferenceKind.LeadingWhitespace));
        Assert.That(difference.Line, Is.EqualTo(3), "the snippet's third line, counting the leading blank one");
        Assert.That(difference.FileLine, Is.EqualTo(6));
    }

    [Test]
    public void TrailingWhitespaceDifference_IsReportedOnTheInteriorLine()
    {
        var source = Lines("void M()", "{", "    a();", "    foo();   ", "    b();", "}");

        var result = Resolve(source, "a();\n    foo();\n    b();");

        Assert.That(result.Outcome, Is.EqualTo(SnippetMatchOutcome.MatchedLooserThanRequested));
        var difference = result.NearMatches[0].Differences.Single();
        Assert.That(difference.Kind, Is.EqualTo(SnippetDifferenceKind.TrailingWhitespace));
        Assert.That(difference.Line, Is.EqualTo(2));
        Assert.That(difference.FileLine, Is.EqualTo(4));
        Assert.That(difference.Expected, Is.EqualTo(string.Empty));
        Assert.That(difference.Got, Is.EqualTo("   "));
        Assert.That(difference.ActualLineText, Is.EqualTo("    foo();   "));
    }

    [Test]
    public void InteriorWhitespaceDifference_IsReportedWithTheTrimmedTexts()
    {
        var source = Lines("void M()", "{", "    var  x = 1;", "}");

        var result = Resolve(source, "var x = 1;");

        Assert.That(result.Outcome, Is.EqualTo(SnippetMatchOutcome.MatchedLooserThanRequested));
        Assert.That(result.NearMatches[0].LowestTier, Is.EqualTo(WhitespaceTolerance.IgnoreAllWhitespace));
        var difference = result.NearMatches[0].Differences.Single();
        Assert.That(difference.Kind, Is.EqualTo(SnippetDifferenceKind.InteriorWhitespace));
        Assert.That(difference.Line, Is.EqualTo(1));
        Assert.That(difference.FileLine, Is.EqualTo(3));
        Assert.That(difference.Expected, Is.EqualTo("var x = 1;"));
        Assert.That(difference.Got, Is.EqualTo("var  x = 1;"));
        Assert.That(difference.ActualLineText, Is.EqualTo("    var  x = 1;"));
    }

    [Test]
    public void LineEndingDifference_LfSnippetAgainstCrLfFile_NamesBothEndings()
    {
        var source = "class C\r\n{\r\n    int a;\r\n    int b;\r\n}\r\n";

        var result = Resolve(source, "    int a;\n    int b;");

        Assert.That(result.Outcome, Is.EqualTo(SnippetMatchOutcome.MatchedLooserThanRequested));
        Assert.That(result.NearMatches[0].LowestTier, Is.EqualTo(WhitespaceTolerance.IgnoreLineEndings));
        var difference = result.NearMatches[0].Differences.Single();
        Assert.That(difference.Kind, Is.EqualTo(SnippetDifferenceKind.LineEnding));
        Assert.That(difference.Line, Is.EqualTo(1));
        Assert.That(difference.FileLine, Is.EqualTo(3));
        Assert.That(difference.Expected, Is.EqualTo("LF"));
        Assert.That(difference.Got, Is.EqualTo("CRLF"));
        Assert.That(difference.ActualLineText, Is.EqualTo("    int a;"), "the file line, without its line break");
    }

    [Test]
    public void LineEndingDifference_CrLfSnippetAgainstLfFile_NamesBothEndings()
    {
        var source = "class C\n{\n    int a;\n    int b;\n}\n";

        var result = Resolve(source, "    int a;\r\n    int b;");

        var difference = result.NearMatches[0].Differences.Single();
        Assert.That(difference.Kind, Is.EqualTo(SnippetDifferenceKind.LineEnding));
        Assert.That(difference.Expected, Is.EqualTo("CRLF"));
        Assert.That(difference.Got, Is.EqualTo("LF"));
    }

    [Test]
    public void LineWhichTheExactMatcherAccepts_HasNoDifferenceEntry()
    {
        // The first line starts mid-line and the last stops mid-line, so both are fine at Exact; only the middle
        // line carries a (trailing whitespace) difference.
        var source = Lines("if (a) first();", "    middle();  ", "    last(); // note");

        var result = Resolve(source, "first();\n    middle();\n    last();");

        var difference = result.NearMatches[0].Differences.Single();
        Assert.That(difference.Kind, Is.EqualTo(SnippetDifferenceKind.TrailingWhitespace));
        Assert.That(difference.Line, Is.EqualTo(2));
    }

    [Test]
    public void Differences_AreDeterministic()
    {
        var first = Resolve(QwenSource, QwenSnippet).NearMatches[0].Differences;
        var second = Resolve(QwenSource, QwenSnippet).NearMatches[0].Differences;

        Assert.That(second, Is.EqualTo(first));
    }

    // ---- closest windows: ranking ----

    private static readonly string[] TypoFile =
    {
        "    var total = Compute(a, b);",
        "    return totl;",
        "    unrelated line zzz",
        "    var total = Compute(a, b);",
        "    return nothing;",
        "    qqq",
    };

    private const string TypoSnippet = "var total = Compute(a, b);\nreturn total;";

    [Test]
    public void ClosestWindows_AreRankedBySimilarityDescending_AndKeepTheFilesOwnLinesVerbatim()
    {
        var result = Resolve(string.Join("\n", TypoFile), TypoSnippet);

        Assert.That(result.Outcome, Is.EqualTo(SnippetMatchOutcome.NoMatch));
        Assert.That(result.Matches, Is.Empty);
        Assert.That(result.NearMatches, Is.Empty);
        Assert.That(result.HasNoCandidates, Is.False);
        Assert.That(result.ClosestWindows.Select(w => w.StartLine).Take(2), Is.EqualTo(new[] { 1, 4 }));
        Assert.That(result.ClosestWindows[0].Score, Is.GreaterThan(result.ClosestWindows[1].Score));
        Assert.That(result.ClosestWindows[0].ActualLines, Is.EqualTo(new[] { TypoFile[0], TypoFile[1] }));
        Assert.That(result.ClosestWindows[1].ActualLines, Is.EqualTo(new[] { TypoFile[3], TypoFile[4] }));
        Assert.That(result.ClosestWindowTotalCount, Is.EqualTo(result.ClosestWindows.Count));
        Assert.That(result.ClosestWindows.Select(w => w.Score), Is.Ordered.Descending);
        Assert.That(result.ClosestWindows.Select(w => w.Score), Is.All.LessThan(1.0));
    }

    [Test]
    public void ClosestWindows_EqualScores_BreakTiesByFileOrder()
    {
        // Three identical half-matching windows (score exactly 0.5), listed in file order whatever the scan order.
        var file = Rep(3, "aaaaaaaaaa", "zzzzzzzzzz");

        var search = SnippetCandidateFinder.FindClosestWindows(file, "aaaaaaaaaa\nbbbbbbbbbb", SnippetCaseMode.CaseSensitive);

        Assert.That(search.Windows.Select(w => w.StartLine), Is.EqualTo(new[] { 1, 3, 5 }));
        Assert.That(search.Windows.Select(w => w.Score), Is.All.EqualTo(0.5));
    }

    [Test]
    public void ClosestWindows_ScoreExactlyAtTheThreshold_Qualifies()
    {
        var search = SnippetCandidateFinder.FindClosestWindows(
            new[] { "aaaaaaaaaa", "zzzzzzzzzz" }, "aaaaaaaaaa\nbbbbbbbbbb", SnippetCaseMode.CaseSensitive);

        Assert.That(SnippetCandidateFinder.MinClosestWindowScore, Is.EqualTo(0.5));
        Assert.That(search.TotalQualifying, Is.EqualTo(1));
        Assert.That(search.Windows.Single().Score, Is.EqualTo(0.5));
    }

    [Test]
    public void ClosestWindows_ScoreJustBelowTheThreshold_IsDropped()
    {
        // 10 of 21 characters of weight earned (0.476).
        var search = SnippetCandidateFinder.FindClosestWindows(
            new[] { "aaaaaaaaaa", "zzzzzzzzzz" }, "aaaaaaaaaa\nbbbbbbbbbbb", SnippetCaseMode.CaseSensitive);

        Assert.That(search.Windows, Is.Empty);
        Assert.That(search.TotalQualifying, Is.EqualTo(0));
    }

    [Test]
    public void ClosestWindows_ScoreJustAboveTheThreshold_Qualifies()
    {
        // 10 of 19 characters of weight earned (0.526).
        var search = SnippetCandidateFinder.FindClosestWindows(
            new[] { "aaaaaaaaaa", "zzzzzzzzzz" }, "aaaaaaaaaa\nbbbbbbbbb", SnippetCaseMode.CaseSensitive);

        Assert.That(search.TotalQualifying, Is.EqualTo(1));
        Assert.That(search.Windows.Single().Score, Is.GreaterThan(SnippetCandidateFinder.MinClosestWindowScore));
    }

    [Test]
    public void ClosestWindows_AreCappedAtFive_AndTheTotalCountSaysHowManyQualified()
    {
        var file = Rep(8, "aaaaaaaaaa", "zzzzzzzzzz");

        var search = SnippetCandidateFinder.FindClosestWindows(file, "aaaaaaaaaa\nbbbbbbbbbb", SnippetCaseMode.CaseSensitive);

        Assert.That(SnippetCandidateFinder.MaxClosestWindows, Is.EqualTo(5));
        Assert.That(search.Windows, Has.Count.EqualTo(5));
        Assert.That(search.TotalQualifying, Is.EqualTo(8));
        Assert.That(search.WindowsExamined, Is.EqualTo(file.Length));
        Assert.That(search.Windows.Select(w => w.StartLine), Is.EqualTo(new[] { 1, 3, 5, 7, 9 }));
    }

    [Test]
    public void ClosestWindows_RankBeforeTruncating_BestWindowBeyondTheFifthInFileOrderIsListedFirst()
    {
        // Seven half-matching windows come first in the file; the best window (line 15) comes after all of them.
        var file = Rep(7, "aaaaaaaaaa", "zzzzzzzzzz").Concat(new[] { "aaaaaaaaaa", "bbbbbbbbbc" }).ToArray();

        var search = SnippetCandidateFinder.FindClosestWindows(file, "aaaaaaaaaa\nbbbbbbbbbb", SnippetCaseMode.CaseSensitive);

        Assert.That(search.TotalQualifying, Is.EqualTo(8));
        Assert.That(search.Windows, Has.Count.EqualTo(5));
        Assert.That(search.Windows[0].StartLine, Is.EqualTo(15));
        Assert.That(search.Windows[0].Score, Is.GreaterThan(0.9));
        Assert.That(search.Windows.Skip(1).Select(w => w.StartLine), Is.EqualTo(new[] { 1, 3, 5, 7 }));
    }

    [Test]
    public void ClosestWindows_CaseInsensitive_NormalizesBothSides()
    {
        var file = new[] { "PUBLIC VOID RUN()", "RETURN VALUES;" };
        const string snippet = "public void run()\nreturn value;";

        var insensitive = SnippetCandidateFinder.FindClosestWindows(file, snippet, SnippetCaseMode.CaseInsensitive);
        var sensitive = SnippetCandidateFinder.FindClosestWindows(file, snippet, SnippetCaseMode.CaseSensitive);

        Assert.That(insensitive.Windows.Single().StartLine, Is.EqualTo(1));
        Assert.That(insensitive.Windows.Single().ActualLines, Is.EqualTo(file), "the file's own text, not the normalized text");
        Assert.That(sensitive.Windows, Is.Empty, "differing case is not similar when case is compared");
    }

    [Test]
    public void ClosestWindows_AreDeterministic()
    {
        var source = string.Join("\n", Rep(8, "aaaaaaaaaa", "zzzzzzzzzz"));

        var first = Resolve(source, "aaaaaaaaaa\nbbbbbbbbbb").ClosestWindows;
        var second = Resolve(source, "aaaaaaaaaa\nbbbbbbbbbb").ClosestWindows;

        Assert.That(second.Select(w => (w.StartLine, w.Score, string.Join("|", w.ActualLines))),
            Is.EqualTo(first.Select(w => (w.StartLine, w.Score, string.Join("|", w.ActualLines)))));
    }

    [Test]
    public void ClosestWindows_ASingleLineSnippetWithATypo_FindsTheLine()
    {
        var source = Lines("void M()", "{", "    var count = Items.Count();", "    return count;", "}");

        var result = Resolve(source, "var count = Items.Count;");

        Assert.That(result.Outcome, Is.EqualTo(SnippetMatchOutcome.NoMatch));
        // "var count = Items.Count;" is not a substring of the file line at any tier ("Count();" has parens), so the
        // closest window is that line.
        Assert.That(result.ClosestWindows.Select(w => w.StartLine), Does.Contain(3));
    }

    [Test]
    public void ClosestWindows_ShortStrayLinesAloneDoNotQualifyAWindow()
    {
        // A closing brace matches a snippet line exactly, but it weighs 1 against a long unrelated line.
        var search = SnippetCandidateFinder.FindClosestWindows(
            new[] { "}", "}", "}" }, "}\nthis_is_a_long_unrelated_statement();", SnippetCaseMode.CaseSensitive);

        Assert.That(search.Windows, Is.Empty);
    }

    // ---- no candidates, tiers probed, invariants ----

    [Test]
    public void SnippetWithNoResemblance_HasNoCandidatesAndNoWindows()
    {
        var result = Resolve(QwenSource, "qqqq zzzz\nwwww jjjj");

        Assert.That(result.Outcome, Is.EqualTo(SnippetMatchOutcome.NoMatch));
        Assert.That(result.ClosestWindows, Is.Empty);
        Assert.That(result.ClosestWindowTotalCount, Is.EqualTo(0));
        Assert.That(result.HasNoCandidates, Is.True);
        Assert.That(result.LinesExamined, Is.EqualTo(9));
        Assert.That(result.WindowsExamined, Is.EqualTo(9));
    }

    [Test]
    public void BlankSnippet_HasNoCandidatesAndProbedNoTier()
    {
        var result = Resolve(QwenSource, "  \n ");

        Assert.That(result.Outcome, Is.EqualTo(SnippetMatchOutcome.NoMatch));
        Assert.That(result.HasNoCandidates, Is.True);
        Assert.That(result.TiersProbed, Is.Empty);
        Assert.That(result.LinesExamined, Is.EqualTo(0));
    }

    [Test]
    public void TiersProbed_NoMatch_ListsEveryTierStrictestFirst()
    {
        var result = Resolve(QwenSource, "qqqq zzzz", WhitespaceTolerance.IgnoreIndentation);

        Assert.That(result.TiersProbed, Is.EqualTo(new[]
        {
            WhitespaceTolerance.Exact, WhitespaceTolerance.IgnoreLineEndings,
            WhitespaceTolerance.IgnoreIndentation, WhitespaceTolerance.IgnoreAllWhitespace,
        }));
        Assert.That(result.RequestedCaseMode, Is.EqualTo(SnippetCaseMode.CaseSensitive));
    }

    [Test]
    public void TiersProbed_NearMatch_StopsAtTheTierThatMatched()
    {
        var result = Resolve(QwenSource, QwenSnippet);

        Assert.That(result.TiersProbed, Is.EqualTo(new[]
        {
            WhitespaceTolerance.Exact, WhitespaceTolerance.IgnoreLineEndings, WhitespaceTolerance.IgnoreIndentation,
        }));
    }

    [Test]
    public void TiersProbed_AcceptedMatch_StopsAtTheWinningTier()
    {
        var result = Resolve(QwenSource, "var a = 1;");

        Assert.That(result.Outcome, Is.EqualTo(SnippetMatchOutcome.Matched));
        Assert.That(result.TiersProbed, Is.EqualTo(new[] { WhitespaceTolerance.Exact }));
        Assert.That(result.HasNoCandidates, Is.False);
    }

    [Test]
    public void TiersProbed_CaseInsensitiveLookup_RecordsTheCaseMode()
    {
        var result = Resolve(
            QwenSource, "qqqq zzzz", WhitespaceTolerance.IgnoreAllWhitespace, SnippetCaseMode.CaseInsensitive);

        Assert.That(result.TiersProbed, Has.Count.EqualTo(4));
        Assert.That(result.RequestedCaseMode, Is.EqualTo(SnippetCaseMode.CaseInsensitive));
    }

    [Test]
    public void Invariants_HoldForEveryOutcomeShape()
    {
        var results = new[]
        {
            Resolve(QwenSource, "var a = 1;"),                              // matched
            Resolve(QwenSource, QwenSnippet),                               // near-match
            Resolve(string.Join("\n", TypoFile), TypoSnippet),              // closest windows
            Resolve(QwenSource, "qqqq zzzz\nwwww jjjj"),                     // nothing at all
        };

        foreach (var result in results)
        {
            Assert.That(result.NearMatches.Count == 0 || result.Matches.Count == 0, Is.True, "near-matches only when no matches");
            Assert.That(
                result.ClosestWindows.Count == 0 || (result.Matches.Count == 0 && result.NearMatches.Count == 0), Is.True,
                "closest windows only when both are empty");
            Assert.That(result.ClosestWindowTotalCount, Is.GreaterThanOrEqualTo(result.ClosestWindows.Count));
            Assert.That(result.NearMatchTotalCount, Is.GreaterThanOrEqualTo(result.NearMatches.Count));
            Assert.That(result.ClosestWindows.Count, Is.LessThanOrEqualTo(SnippetCandidateFinder.MaxClosestWindows));
            Assert.That(result.LinesExamined, Is.GreaterThanOrEqualTo(0));
        }
    }

    [Test]
    public void Create_ClosestWindowTotalLessThanListed_Throws()
    {
        var window = new SnippetClosestWindow(1, new[] { "x" }, 0.6);

        Assert.Throws<ArgumentOutOfRangeException>(() => SnippetMatchResult.Create(
            WhitespaceTolerance.Exact, SnippetCaseMode.CaseSensitive, SnippetMatchOutcome.NoMatch,
            Array.Empty<SnippetMatch>(), Array.Empty<SnippetNearMatch>(), 0, new[] { window }, 0, 0, 0,
            closestWindowTotalCount: 0));
    }

    [Test]
    public void Create_ClosestWindowTotalAlongsideNearMatches_Throws()
    {
        var near = new SnippetNearMatch(
            WhitespaceTolerance.IgnoreIndentation, 1, new[] { "x" }, Array.Empty<SnippetDifference>());

        Assert.Throws<ArgumentException>(() => SnippetMatchResult.Create(
            WhitespaceTolerance.Exact, SnippetCaseMode.CaseSensitive, SnippetMatchOutcome.MatchedLooserThanRequested,
            Array.Empty<SnippetMatch>(), new[] { near }, 1, Array.Empty<SnippetClosestWindow>(), 0, 0, 0,
            closestWindowTotalCount: 3));
    }

    [Test]
    public void Create_NegativeExaminedCounts_Throw()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => SnippetMatchResult.Create(
            WhitespaceTolerance.Exact, SnippetCaseMode.CaseSensitive, SnippetMatchOutcome.NoMatch,
            Array.Empty<SnippetMatch>(), Array.Empty<SnippetNearMatch>(), 0, Array.Empty<SnippetClosestWindow>(), 0, 0, 0,
            linesExamined: -1));
    }

    [Test]
    public void Create_WithoutTheNewOptionalArguments_DefaultsToNothingRecorded()
    {
        var result = SnippetMatchResult.Create(
            WhitespaceTolerance.Exact, SnippetCaseMode.CaseSensitive, SnippetMatchOutcome.NoMatch,
            Array.Empty<SnippetMatch>(), Array.Empty<SnippetNearMatch>(), 0, Array.Empty<SnippetClosestWindow>(), 0, 0, 0);

        Assert.That(result.TiersProbed, Is.Empty);
        Assert.That(result.ClosestWindowTotalCount, Is.EqualTo(0));
        Assert.That(result.LinesExamined, Is.EqualTo(0));
        Assert.That(result.WindowsExamined, Is.EqualTo(0));
        Assert.That(result.HasNoCandidates, Is.True);
    }
}
