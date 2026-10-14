using Microsoft.CodeAnalysis.Text;
using RoslynSentinel.Common.SnippetMatching;

namespace RoslynSentinel.Tests;

[Parallelizable(ParallelScope.All)]
public class SnippetMatchResultTests
{
    private static SnippetMatch Match(int start = 0, int line = 1, int length = 3, WhitespaceTolerance tier = WhitespaceTolerance.Exact)
        => new(start, line, length, tier, "TestMatcher");

    private static SnippetNearMatch NearMatch()
        => new(
            WhitespaceTolerance.IgnoreIndentation,
            StartLine: 4,
            ActualLines: new[] { "        var x = 1;" },
            Differences: new[] { new SnippetDifference(SnippetDifferenceKind.LeadingWhitespace, 1, string.Empty, "        ") });

    private static SnippetClosestWindow Window()
        => new(StartLine: 7, ActualLines: new[] { "var y = 2;" }, Score: 0.5);

    private static SnippetMatchResult Create(
        SnippetMatchOutcome outcome,
        IReadOnlyList<SnippetMatch>? matches = null,
        IReadOnlyList<SnippetNearMatch>? nearMatches = null,
        int? nearMatchTotalCount = null,
        IReadOnlyList<SnippetClosestWindow>? closestWindows = null)
    {
        matches ??= Array.Empty<SnippetMatch>();
        nearMatches ??= Array.Empty<SnippetNearMatch>();
        closestWindows ??= Array.Empty<SnippetClosestWindow>();
        return SnippetMatchResult.Create(
            WhitespaceTolerance.IgnoreLineEndings, SnippetCaseMode.CaseSensitive, outcome,
            matches, nearMatches, nearMatchTotalCount ?? nearMatches.Count, closestWindows,
            candidatesBeforeSelectors: matches.Count, candidatesAfterSelectors: matches.Count, candidatesAfterAdjacentLines: matches.Count);
    }

    // ---- WhitespaceTolerance ladder ----

    [Test]
    public void WhitespaceTolerance_IsOrderedFromStrictestToLoosest()
    {
        Assert.That(WhitespaceTolerance.Exact, Is.LessThan(WhitespaceTolerance.IgnoreLineEndings));
        Assert.That(WhitespaceTolerance.IgnoreLineEndings, Is.LessThan(WhitespaceTolerance.IgnoreIndentation));
        Assert.That(WhitespaceTolerance.IgnoreIndentation, Is.LessThan(WhitespaceTolerance.IgnoreAllWhitespace));
    }

    [Test]
    public void WhitespaceTolerance_DeclaresExactlyTheFourTiersInLadderOrder()
    {
        var names = Enum.GetValues<WhitespaceTolerance>().OrderBy(t => (int)t).Select(t => t.ToString()).ToArray();

        Assert.That(names, Is.EqualTo(new[] { "Exact", "IgnoreLineEndings", "IgnoreIndentation", "IgnoreAllWhitespace" }));
    }

    [Test]
    public void SnippetCaseMode_DeclaresBothModes()
    {
        Assert.That(Enum.GetNames<SnippetCaseMode>(), Is.EquivalentTo(new[] { "CaseSensitive", "CaseInsensitive" }));
    }

    // ---- Invariants ----

    [Test]
    public void Create_MatchesOnly_Succeeds()
    {
        var result = Create(SnippetMatchOutcome.Matched, matches: new[] { Match() });

        Assert.That(result.Matches, Has.Count.EqualTo(1));
        Assert.That(result.NearMatches, Is.Empty);
        Assert.That(result.ClosestWindows, Is.Empty);
    }

    [Test]
    public void Create_NearMatchesWithEmptyMatches_Succeeds()
    {
        var result = Create(SnippetMatchOutcome.MatchedLooserThanRequested, nearMatches: new[] { NearMatch() });

        Assert.That(result.HasNearMatches, Is.True);
        Assert.That(result.NearMatchTotalCount, Is.EqualTo(1));
    }

    [Test]
    public void Create_ClosestWindowsWithNothingElse_Succeeds()
    {
        var result = Create(SnippetMatchOutcome.NoMatch, closestWindows: new[] { Window() });

        Assert.That(result.ClosestWindows, Has.Count.EqualTo(1));
    }

    [Test]
    public void Create_NearMatchesAlongsideMatches_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() =>
            Create(SnippetMatchOutcome.Matched, matches: new[] { Match() }, nearMatches: new[] { NearMatch() }));
    }

    [Test]
    public void Create_ClosestWindowsAlongsideMatches_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() =>
            Create(SnippetMatchOutcome.Matched, matches: new[] { Match() }, closestWindows: new[] { Window() }));
    }

    [Test]
    public void Create_ClosestWindowsAlongsideNearMatches_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() =>
            Create(SnippetMatchOutcome.MatchedLooserThanRequested, nearMatches: new[] { NearMatch() }, closestWindows: new[] { Window() }));
    }

    [Test]
    public void Create_NearMatchTotalCountBelowListedCount_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            Create(SnippetMatchOutcome.MatchedLooserThanRequested, nearMatches: new[] { NearMatch(), NearMatch() }, nearMatchTotalCount: 1));
    }

    [Test]
    public void Create_NearMatchTotalCountAboveListedCount_IsKept()
    {
        var result = Create(SnippetMatchOutcome.MatchedLooserThanRequested, nearMatches: new[] { NearMatch() }, nearMatchTotalCount: 9);

        Assert.That(result.NearMatchTotalCount, Is.EqualTo(9));
    }

    [Test]
    public void Create_EchoesRequestAndFilterTrail()
    {
        var result = SnippetMatchResult.Create(
            WhitespaceTolerance.IgnoreIndentation, SnippetCaseMode.CaseInsensitive, SnippetMatchOutcome.AdjacentLineMismatch,
            Array.Empty<SnippetMatch>(), Array.Empty<SnippetNearMatch>(), 0, Array.Empty<SnippetClosestWindow>(),
            candidatesBeforeSelectors: 5, candidatesAfterSelectors: 3, candidatesAfterAdjacentLines: 0);

        Assert.Multiple(() =>
        {
            Assert.That(result.RequestedTolerance, Is.EqualTo(WhitespaceTolerance.IgnoreIndentation));
            Assert.That(result.RequestedCaseMode, Is.EqualTo(SnippetCaseMode.CaseInsensitive));
            Assert.That(result.Outcome, Is.EqualTo(SnippetMatchOutcome.AdjacentLineMismatch));
            Assert.That(result.CandidatesBeforeSelectors, Is.EqualTo(5));
            Assert.That(result.CandidatesAfterSelectors, Is.EqualTo(3));
            Assert.That(result.CandidatesAfterAdjacentLines, Is.EqualTo(0));
        });
    }

    // ---- IsSingleMatch / Single / TryGetSingle ----

    [Test]
    public void SingleMatch_IsSingleMatchTrue_SingleAndTryGetSingleReturnIt()
    {
        var match = Match(start: 10, line: 2, length: 4);
        var result = Create(SnippetMatchOutcome.Matched, matches: new[] { match });

        Assert.That(result.IsSingleMatch, Is.True);
        Assert.That(result.Single(), Is.SameAs(match));
        Assert.That(result.TryGetSingle(out var got), Is.True);
        Assert.That(got, Is.SameAs(match));
    }

    [Test]
    public void NoMatches_IsSingleMatchFalse_SingleThrows_TryGetSingleFalse()
    {
        var result = Create(SnippetMatchOutcome.NoMatch);

        Assert.That(result.IsSingleMatch, Is.False);
        Assert.Throws<InvalidOperationException>(() => result.Single());
        Assert.That(result.TryGetSingle(out _), Is.False);
    }

    [Test]
    public void SeveralMatches_IsSingleMatchFalse_SingleThrows_TryGetSingleFalse()
    {
        var result = Create(SnippetMatchOutcome.Ambiguous, matches: new[] { Match(0, 1), Match(20, 3) });

        Assert.That(result.IsSingleMatch, Is.False);
        Assert.Throws<InvalidOperationException>(() => result.Single());
        Assert.That(result.TryGetSingle(out _), Is.False);
    }

    [Test]
    public void NearMatchesOnly_IsNotASingleMatch_HasNearMatchesTrue()
    {
        var result = Create(SnippetMatchOutcome.MatchedLooserThanRequested, nearMatches: new[] { NearMatch() });

        Assert.That(result.IsSingleMatch, Is.False);
        Assert.That(result.HasNearMatches, Is.True);
        Assert.Throws<InvalidOperationException>(() => result.Single());
    }

    // ---- Compile-time separation of near-matches from edit-path matches ----

    [Test]
    public void NearMatchAndClosestWindow_ShareNoBaseTypeOrInterfaceWithSnippetMatch()
    {
        foreach (var type in new[] { typeof(SnippetNearMatch), typeof(SnippetClosestWindow) })
        {
            Assert.That(typeof(SnippetMatch).IsAssignableFrom(type), Is.False, type.Name);
            Assert.That(type.BaseType, Is.EqualTo(typeof(object)), type.Name);
            Assert.That(type.GetInterfaces().Intersect(typeof(SnippetMatch).GetInterfaces()).Where(i => i != typeof(IEquatable<>).MakeGenericType(type)),
                Is.Empty, type.Name);
        }
    }

    // ---- ContextErrorBuilder.Build for the new outcomes ----

    [Test]
    public void ContextErrorBuilder_Build_Matched_ThrowsInvalidOperationException()
    {
        Assert.Throws<InvalidOperationException>(() =>
            ContextErrorBuilder.Build(SnippetMatchOutcome.Matched, "x", SourceText.From("x")));
    }

    [Test]
    public void ContextErrorBuilder_Build_MatchedLooserThanRequested_MapsToNotFoundLikeNoMatch()
    {
        var looser = ContextErrorBuilder.Build(SnippetMatchOutcome.MatchedLooserThanRequested, "x", SourceText.From("x"));
        var noMatch = ContextErrorBuilder.Build(SnippetMatchOutcome.NoMatch, "x", SourceText.From("x"));

        Assert.That(looser, Is.TypeOf<ToolNotFoundException>());
        Assert.That(looser.Message, Is.EqualTo(noMatch.Message));
    }

    [Test]
    public void ContextErrorBuilder_Build_ExistingOutcomesKeepTheirExceptionTypes()
    {
        var source = SourceText.From("int x = 1; int y = 1;");

        Assert.That(ContextErrorBuilder.Build(SnippetMatchOutcome.NoMatch, "q", source), Is.TypeOf<ToolNotFoundException>());
        Assert.That(ContextErrorBuilder.Build(SnippetMatchOutcome.Ambiguous, "int", source, new List<ContextHelper.SnippetMatch>()), Is.TypeOf<ToolAmbiguousMatchException>());
        Assert.That(ContextErrorBuilder.Build(SnippetMatchOutcome.StillAmbiguous, "int", source), Is.TypeOf<ToolAmbiguousMatchException>());
        Assert.That(ContextErrorBuilder.Build(SnippetMatchOutcome.TrueTie, "int", source), Is.TypeOf<ToolAmbiguousMatchException>());
        Assert.That(ContextErrorBuilder.Build(SnippetMatchOutcome.SelectorMismatch, "int", source), Is.TypeOf<ToolNotFoundException>());
        Assert.That(ContextErrorBuilder.Build(SnippetMatchOutcome.AdjacentLineMismatch, "int", source), Is.TypeOf<ToolNotFoundException>());
        Assert.That(ContextErrorBuilder.Build(SnippetMatchOutcome.InvalidDisambiguator, "int", source, parameterName: "lineBefore", invalidValue: "a\nb", isAfter: false), Is.TypeOf<ToolNotFoundException>());
    }
}
