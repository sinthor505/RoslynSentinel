using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis.Text;
using RoslynSentinel.Common;
using RoslynSentinel.Common.SnippetMatching;

namespace RoslynSentinel.Tests;

/// <summary>
/// Tests for <see cref="SnippetErrorFormatter"/> (slice 3b of the whitespace-tolerance proposal): every failure outcome
/// renders the file's own copy-ready candidates, and the candidate lines shown in the message resolve back to a single
/// match when they are sent to <see cref="SnippetResolver"/> again (the round trip).
/// </summary>
[Parallelizable(ParallelScope.All)]
public class SnippetErrorFormatterTests
{
    private const string Pad = "        ";

    private static string Lines(params string[] lines) => string.Join("\n", lines);

    private static SnippetMatchResult Resolve(
        string source, string snippet,
        WhitespaceTolerance tolerance = WhitespaceTolerance.Exact,
        SnippetCaseMode caseMode = SnippetCaseMode.CaseSensitive,
        SnippetSelectors? selectors = null,
        Func<int, string?>? memberNameAt = null)
        => SnippetResolver.Resolve(SourceText.From(source), snippet, tolerance, caseMode, selectors, memberNameAt);

    private static ToolException Build(
        string source, string snippet, SnippetMatchResult result,
        Func<int, string?>? memberNameAt = null, bool selectorsSupported = false)
        => SnippetErrorFormatter.Build(result, SourceText.From(source), snippet, "oldContent", memberNameAt, selectorsSupported);

    // The lines inside each fenced block of a message, in order.
    private static List<string[]> Fences(string message)
    {
        var blocks = new List<string[]>();
        List<string>? current = null;
        foreach (var line in message.Split('\n'))
        {
            var isFence = line.Length >= 3 && line.All(c => c == '`');
            if (isFence && current == null)
            {
                current = new List<string>();
            }
            else if (isFence)
            {
                blocks.Add(current!.ToArray());
                current = null;
            }
            else
            {
                current?.Add(line);
            }
        }

        return blocks;
    }

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

    // Snippet occurs at lines 5-6 (in A), 12-13 (in B) and 19-20 (in the B(int) overload).
    private static readonly string SelectorSource = Lines(
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

    private const string SelectorSnippet = "x = 1;\n        y = 2;";

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

    private static SnippetMatchResult ResolveSelectors(SnippetSelectors? selectors, bool withMembers = true)
    {
        var text = SourceText.From(SelectorSource);
        return SnippetResolver.Resolve(
            text, SelectorSnippet, WhitespaceTolerance.IgnoreLineEndings, SnippetCaseMode.CaseSensitive, selectors,
            withMembers ? MemberByLines(text) : null);
    }

    // ---- NoMatch ----

    [Test]
    public void NoMatch_WithCandidates_ListsTheFilesOwnLinesRankedWithStartLineAndScore_AndRoundTrips()
    {
        var source = string.Join("\n", TypoFile);
        var result = Resolve(source, TypoSnippet);
        Assert.That(result.Outcome, Is.EqualTo(SnippetMatchOutcome.NoMatch));

        var ex = Build(source, TypoSnippet, result);

        Assert.That(ex, Is.InstanceOf<ToolNotFoundException>());
        Assert.That(ex.Message, Does.Contain("oldContent was not found"));
        Assert.That(ex.Message, Does.Contain("Window 1 (starts at line 1, score "));
        Assert.That(ex.Message, Does.Contain("Window 2 (starts at line 4, score "));
        Assert.That(ex.Message, Does.Contain("    var total = Compute(a, b);\n    return totl;"));
        Assert.That(ex.Message, Does.Not.Contain("No candidates"));

        var firstWindow = Fences(ex.Message)[0];
        Assert.That(firstWindow, Is.EqualTo(new[] { TypoFile[0], TypoFile[1] }));
        var again = Resolve(source, string.Join("\n", firstWindow));
        Assert.That(again.Outcome, Is.EqualTo(SnippetMatchOutcome.Matched));
        Assert.That(again.Single().Line, Is.EqualTo(1));
    }

    [Test]
    public void NoMatch_WithNothingClose_SaysThereAreNoCandidates_AndListsNoWindows()
    {
        var result = Resolve(QwenSource, "qqqq zzzz\nwwww jjjj");

        var ex = Build(QwenSource, "qqqq zzzz\nwwww jjjj", result);

        Assert.That(ex, Is.InstanceOf<ToolNotFoundException>());
        Assert.That(ex.Message, Does.Contain("No candidates: no window of the file reaches a similarity of 0.50"));
        Assert.That(ex.Message, Does.Contain("re-read the file"));
        Assert.That(ex.Message, Does.Not.Contain("Closest windows"));
        Assert.That(Fences(ex.Message), Is.Empty);
    }

    [Test]
    public void NoMatch_BlankSnippet_SaysNothingWasSearched()
    {
        var result = Resolve(QwenSource, "   \n  ");

        var ex = Build(QwenSource, "   \n  ", result);

        Assert.That(ex.Message, Does.Contain("empty or whitespace only"));
        Assert.That(ex.Message, Does.Contain("No candidates exist"));
    }

    [Test]
    public void NoMatch_ProbedTiersAndCaseModeAreStated()
    {
        var result = Resolve(
            QwenSource, "qqqq zzzz\nwwww jjjj", WhitespaceTolerance.IgnoreAllWhitespace, SnippetCaseMode.CaseInsensitive);
        Assert.That(result.TiersProbed, Is.Not.Empty);

        var ex = Build(QwenSource, "qqqq zzzz\nwwww jjjj", result);

        Assert.That(ex.Message, Does.Contain("Probed tiers (strictest first): " + string.Join(", ", result.TiersProbed) + "."));
        Assert.That(ex.Message, Does.Contain("Requested tolerance: IgnoreAllWhitespace."));
        Assert.That(ex.Message, Does.Contain("Case mode: case-insensitive."));
        Assert.That(ex.Message, Does.Contain("Examined 9 file line(s)"));
    }

    [Test]
    public void NoMatch_EchoesTheSnippetWithVisibleWhitespaceInALabelledBlock()
    {
        var snippet = "\tfoo();  \r\nbar();\\ caf" + (char)0xE9;
        var result = Resolve("unrelated text here", snippet);

        var ex = Build("unrelated text here", snippet, result);

        Assert.That(ex.Message, Does.Contain("oldContent as received (2 line(s), "));
        Assert.That(ex.Message, Does.Contain("  1: [\\tfoo();  ] \\r\\n"));
        Assert.That(ex.Message, Does.Contain("  2: [bar();\\\\ caf\\u00E9] (end)"));
    }

    [Test]
    public void NoMatch_ManyQualifyingWindows_StatesHowManyWereOmitted()
    {
        var source = string.Join("\n", Enumerable.Range(0, 8).SelectMany(_ => new[] { "aaaaaaaaaa", "zzzzzzzzzz" }));
        const string snippet = "aaaaaaaaaa\nbbbbbbbbbb";
        var result = Resolve(source, snippet);
        Assert.That(result.ClosestWindows, Has.Count.EqualTo(5));
        Assert.That(result.ClosestWindowTotalCount, Is.EqualTo(8));

        var ex = Build(source, snippet, result);

        Assert.That(ex.Message, Does.Contain("(3 more window(s) omitted to keep this message short.)"));
        Assert.That(Fences(ex.Message), Has.Count.EqualTo(5));
    }

    // ---- MatchedLooserThanRequested ----

    [Test]
    public void LooserTier_QwenRelabel_NamesTheTierAndListsTheFilesIndentedLines()
    {
        var result = Resolve(QwenSource, QwenSnippet);
        Assert.That(result.Outcome, Is.EqualTo(SnippetMatchOutcome.MatchedLooserThanRequested));

        var ex = Build(QwenSource, QwenSnippet, result);

        Assert.That(ex, Is.InstanceOf<ToolMatchedLooserTierException>());
        Assert.That(ex, Is.Not.InstanceOf<ToolNotFoundException>());
        Assert.That(ex.ErrorCode, Is.EqualTo(ToolErrorCode.MatchedLooserThanRequested));
        Assert.That(ex.ErrorCode, Is.EqualTo("MatchedLooserThanRequested"));
        Assert.That(ex.Message, Does.Contain("matches at IgnoreIndentation"));
        Assert.That(ex.Message, Does.Contain("this call requires Exact"));
        Assert.That(ex.Message, Does.Contain("NOT used and nothing was applied"));
        Assert.That(ex.Message, Does.Contain("Near-match 1 of 1 (starts at line 5, matches at IgnoreIndentation)"));
        Assert.That(ex.Message, Does.Contain("- line 2: file has 8 leading spaces, snippet has none (file line 6)"));
        Assert.That(ex.Message, Does.Contain("- line 3: file has 8 leading spaces, snippet has none (file line 7)"));
        Assert.That(ex.Message, Does.Contain(Pad + "var a = 1;\n" + Pad + "var b = 2;\n" + Pad + "return a + b;"));
    }

    [Test]
    public void LooserTier_DisplayedLinesRoundTripAtExactAndIgnoreLineEndings()
    {
        var result = Resolve(QwenSource, QwenSnippet);
        var ex = Build(QwenSource, QwenSnippet, result);

        var shown = string.Join("\n", Fences(ex.Message).Single());

        foreach (var tier in new[] { WhitespaceTolerance.Exact, WhitespaceTolerance.IgnoreLineEndings })
        {
            var again = Resolve(QwenSource, shown, tier);
            Assert.That(again.Outcome, Is.EqualTo(SnippetMatchOutcome.Matched), tier.ToString());
            Assert.That(again.Single().Line, Is.EqualTo(5), tier.ToString());
        }
    }

    [Test]
    public void LooserTier_TrailingWhitespaceAndTabs_AreDescribedInPlainStatements()
    {
        var source = Lines("void M()", "{", "    a();", "\tfoo();   ", "    b();", "}");
        const string snippet = "a();\n    foo();\n    b();";
        var result = Resolve(source, snippet);
        Assert.That(result.Outcome, Is.EqualTo(SnippetMatchOutcome.MatchedLooserThanRequested));

        var ex = Build(source, snippet, result);

        Assert.That(ex.Message, Does.Contain("file has 1 leading tab, snippet has 4 leading spaces"));
        Assert.That(ex.Message, Does.Contain("file has 3 trailing spaces, snippet has none"));
        Assert.That(ex.Message, Does.Contain("\tfoo();   "), "the file's own line, trailing whitespace included");
    }

    // ---- Ambiguous family ----

    [Test]
    public void Ambiguous_ListsEveryCandidateInFileOrderWithSurroundingValues_AndRoundTripsViaLineBefore()
    {
        const string source = "a();\nx();\nb();\nc();\nx();\nd();";
        var result = Resolve(source, "x();");
        Assert.That(result.Outcome, Is.EqualTo(SnippetMatchOutcome.Ambiguous));

        var ex = Build(source, "x();", result);

        Assert.That(ex, Is.InstanceOf<ToolAmbiguousMatchException>());
        var message = ex.Message;
        Assert.That(message, Does.Contain("oldContent is ambiguous: it matches 2 places"));
        Assert.That(message.IndexOf("Candidate 1 of 2 (line 2)", StringComparison.Ordinal),
            Is.LessThan(message.IndexOf("Candidate 2 of 2 (line 5)", StringComparison.Ordinal)));
        Assert.That(message, Does.Contain("lineBefore=\"a();\" lineAfter=\"b();\""));
        Assert.That(message, Does.Contain("lineBefore=\"c();\" lineAfter=\"d();\""));
        Assert.That(message, Does.Not.Contain("matchLine"), "this tool does not accept matchLine");
        Assert.That(Fences(message).Select(f => string.Join("\n", f)), Is.EqualTo(new[] { "x();", "x();" }));

        var lineBefore = Regex.Matches(message, "lineBefore=\"([^\"]*)\"")[1].Groups[1].Value;
        var again = Resolve(source, "x();", selectors: new SnippetSelectors(LineBefore: lineBefore));
        Assert.That(again.Outcome, Is.EqualTo(SnippetMatchOutcome.Matched));
        Assert.That(again.Single().Line, Is.EqualTo(5));
    }

    [Test]
    public void Ambiguous_WithSelectors_ListsMatchLineAndOnlyUniqueMemberNames_AndRoundTripsViaMatchLine()
    {
        var text = SourceText.From(SelectorSource);
        var memberNameAt = MemberByLines(text);
        var result = ResolveSelectors(null);
        Assert.That(result.Outcome, Is.EqualTo(SnippetMatchOutcome.Ambiguous));

        var ex = Build(SelectorSource, SelectorSnippet, result, memberNameAt, selectorsSupported: true);

        var message = ex.Message;
        Assert.That(message, Does.Contain("Candidate 1 of 3 (lines 5-6, in A)"));
        Assert.That(message, Does.Contain("withinMember=\"A\" matchLine=5 "));
        Assert.That(message, Does.Contain("matchLine=12 "));
        Assert.That(message, Does.Contain("matchLine=19 "));
        Assert.That(message, Does.Not.Contain("withinMember=\"B\""), "B names two candidates, so it selects neither");
        Assert.That(message, Does.Contain("Candidates 1 and 2 have identical surrounding lines"));
        Assert.That(Fences(message)[1], Is.EqualTo(new[] { Pad + "x = 1;", Pad + "y = 2;" }));

        var matchLine = int.Parse(Regex.Matches(message, "matchLine=(\\d+)")[1].Groups[1].Value);
        var again = ResolveSelectors(new SnippetSelectors(MatchLine: matchLine));
        Assert.That(again.Outcome, Is.EqualTo(SnippetMatchOutcome.Matched));
        Assert.That(again.Single().Line, Is.EqualTo(12));
    }

    [Test]
    public void StillAmbiguous_ShowsTheFilterTrailAndTheRemainingCandidates()
    {
        var text = SourceText.From(SelectorSource);
        var result = ResolveSelectors(new SnippetSelectors(WithinMember: "B"));
        Assert.That(result.Outcome, Is.EqualTo(SnippetMatchOutcome.StillAmbiguous));

        var ex = Build(SelectorSource, SelectorSnippet, result, MemberByLines(text), selectorsSupported: true);

        Assert.That(ex, Is.InstanceOf<ToolAmbiguousMatchException>());
        Assert.That(ex.Message, Does.Contain("oldContent is still ambiguous: 2 matches remain"));
        Assert.That(ex.Message, Does.Contain("Selectors applied (3 candidate(s) before selectors): withinMember removed 1."));
        Assert.That(ex.Message, Does.Contain("Candidate 1 of 2 (lines 12-13, in B)"));
        Assert.That(ex.Message, Does.Contain("Candidate 2 of 2 (lines 19-20, in B)"));
        Assert.That(ex.Message, Does.Not.Contain("lines 5-6"));
    }

    [Test]
    public void SelectorMismatch_ListsTheCandidatesTheFilterRemoved()
    {
        var text = SourceText.From(SelectorSource);
        var result = ResolveSelectors(new SnippetSelectors(MatchLine: 99));
        Assert.That(result.Outcome, Is.EqualTo(SnippetMatchOutcome.SelectorMismatch));

        var ex = Build(SelectorSource, SelectorSnippet, result, MemberByLines(text), selectorsSupported: true);

        Assert.That(ex, Is.InstanceOf<ToolNotFoundException>());
        Assert.That(ex.Message, Does.Contain("was found (3 match(es)), but not at the matchLine you supplied; nothing was located."));
        Assert.That(ex.Message, Does.Contain("matchLine=5 "));
        Assert.That(ex.Message, Does.Contain("matchLine=12 "));
        Assert.That(ex.Message, Does.Contain("matchLine=19 "));
        Assert.That(Fences(ex.Message), Has.Count.EqualTo(3));
    }

    [Test]
    public void AdjacentLineMismatch_ListsTheSurvivorsWithTheLinesAroundThem()
    {
        const string source = "a();\nx();\nb();\nc();\nx();\nd();";
        var result = Resolve(source, "x();", selectors: new SnippetSelectors(LineBefore: "nonexistent();"));
        Assert.That(result.Outcome, Is.EqualTo(SnippetMatchOutcome.AdjacentLineMismatch));

        var ex = Build(source, "x();", result);

        Assert.That(ex, Is.InstanceOf<ToolNotFoundException>());
        Assert.That(ex.Message, Does.Contain("not next to the lineBefore you supplied"));
        Assert.That(ex.Message, Does.Contain("single real line immediately before/after the match"));
        Assert.That(ex.Message, Does.Contain("lineBefore=\"a();\" lineAfter=\"b();\""));
        Assert.That(ex.Message, Does.Contain("lineBefore=\"c();\" lineAfter=\"d();\""));
    }

    // ---- InvalidDisambiguator ----

    [Test]
    public void InvalidDisambiguator_MultiLineLineBefore_SaysWhichSelectorAndWhy()
    {
        const string source = "a();\nx();\nb();";
        var result = Resolve(source, "x();", selectors: new SnippetSelectors(LineBefore: "a();\nb();"));
        Assert.That(result.Outcome, Is.EqualTo(SnippetMatchOutcome.InvalidDisambiguator));

        var ex = Build(source, "x();", result);

        Assert.That(ex.Message, Does.Contain("lineBefore must be a single line, but the supplied value spans multiple lines"));
        Assert.That(ex.Message, Does.Contain("immediately before the match"));
    }

    [Test]
    public void InvalidDisambiguator_MultiLineLineAfter_NamesLineAfterNotLineBefore()
    {
        const string source = "a();\nx();\nb();";
        var result = Resolve(source, "x();", selectors: new SnippetSelectors(LineAfter: "b();\nc();"));
        Assert.That(result.Outcome, Is.EqualTo(SnippetMatchOutcome.InvalidDisambiguator));

        var ex = Build(source, "x();", result);

        Assert.That(ex.Message, Does.Contain("lineAfter must be a single line"));
        Assert.That(ex.Message, Does.Contain("immediately after the match"));
        Assert.That(ex.Message, Does.Not.Contain("lineBefore"));
    }

    [Test]
    public void InvalidDisambiguator_WithinMemberWithoutMemberResolver_SaysItCannotBeApplied()
    {
        var result = ResolveSelectors(new SnippetSelectors(WithinMember: "A"), withMembers: false);
        Assert.That(result.Outcome, Is.EqualTo(SnippetMatchOutcome.InvalidDisambiguator));

        var ex = Build(SelectorSource, SelectorSnippet, result);

        Assert.That(ex.Message, Does.Contain("withinMember (\"A\") cannot be applied"));
        Assert.That(ex.Message, Does.Not.Contain("single line"));
    }

    // ---- Matched, caps, determinism, ASCII ----

    [Test]
    public void Matched_Throws_BecauseASuccessHasNoError()
    {
        var result = Resolve("x();", "x();");
        Assert.That(result.Outcome, Is.EqualTo(SnippetMatchOutcome.Matched));

        Assert.Throws<InvalidOperationException>(() => Build("x();", "x();", result));
    }

    [Test]
    public void LengthCap_ManyLargeWindows_KeepsTheMessageUnderTheCap_AndStatesTheOmittedCount()
    {
        var a = new string('a', 900);
        var z = new string('z', 900);
        var b = new string('b', 900);
        var source = string.Join("\n", Enumerable.Range(0, 8).SelectMany(_ => new[] { a, z }));
        var snippet = a + "\n" + b;
        var result = Resolve(source, snippet);
        Assert.That(result.ClosestWindows, Has.Count.EqualTo(5));

        var ex = Build(source, snippet, result);

        Assert.That(ex.Message.Length, Is.LessThanOrEqualTo(SnippetErrorFormatter.MaxMessageChars));
        Assert.That(ex.Message, Does.Contain(a + "\n" + z), "the best window is listed in full");
        Assert.That(ex.Message, Does.Match(@"\(\d+ more window\(s\) omitted to keep this message short\.\)"));
        Assert.That(Fences(ex.Message).Count, Is.LessThan(5));
    }

    [Test]
    public void LengthCap_ManyAmbiguousCandidates_KeepsTheMessageUnderTheCap()
    {
        var block = new string('q', 300);
        var source = string.Join("\n", Enumerable.Range(0, 60).Select(i => block + " // " + i));
        var result = Resolve(source, block);
        Assert.That(result.Outcome, Is.EqualTo(SnippetMatchOutcome.Ambiguous));

        var ex = Build(source, block, result);

        Assert.That(ex.Message.Length, Is.LessThanOrEqualTo(SnippetErrorFormatter.MaxMessageChars));
        Assert.That(ex.Message, Does.Contain("Candidate 1 of 60"));
        Assert.That(ex.Message, Does.Match(@"\(\d+ more candidate\(s\) omitted to keep this message short\.\)"));
    }

    [Test]
    public void Output_IsDeterministic_AndAsciiWhenTheFileIs()
    {
        var results = new (string Source, string Snippet, Func<SnippetMatchResult> Resolve)[]
        {
            (QwenSource, QwenSnippet, () => Resolve(QwenSource, QwenSnippet)),
            (string.Join("\n", TypoFile), TypoSnippet, () => Resolve(string.Join("\n", TypoFile), TypoSnippet)),
            (QwenSource, "qqqq zzzz\nwwww jjjj", () => Resolve(QwenSource, "qqqq zzzz\nwwww jjjj")),
            (SelectorSource, SelectorSnippet, () => ResolveSelectors(null, withMembers: false)),
        };

        foreach (var (source, snippet, resolve) in results)
        {
            var first = Build(source, snippet, resolve()).Message;
            var second = Build(source, snippet, resolve()).Message;

            Assert.That(second, Is.EqualTo(first));
            Assert.That(first.All(c => c == '\n' || (c >= 32 && c <= 126)), Is.True, "message must be ASCII: " + first);
        }
    }

    [Test]
    public void Messages_DoNotLeakPathsOrStackTraces()
    {
        var result = Resolve(QwenSource, QwenSnippet);

        var message = Build(QwenSource, QwenSnippet, result).Message;

        Assert.That(message, Does.Not.Contain(" at RoslynSentinel."));
        Assert.That(message, Does.Not.Contain("Exception"));
        Assert.That(message, Does.Not.Contain(":\\"));
    }
}
