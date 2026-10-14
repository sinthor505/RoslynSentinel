using Microsoft.CodeAnalysis.Text;

namespace RoslynSentinel.Common.SnippetMatching;

/// <summary>
/// Dispatcher for snippet-in-text matching. Runs the matchers in ladder order up to the requested
/// <see cref="WhitespaceTolerance"/>, stops at the first tier that finds a match, and reports the outcome as data
/// (never an exception for a match outcome). The ordinal, line-ending and whitespace-collapsing matchers delegate to
/// the stage logic in <c>SnippetStages</c>; <c>IndentationMatcher</c> is the one new matcher, and the cap and the
/// near-match probe are the dispatcher's own behaviour.
/// </summary>
/// <remarks>
/// The matchers and <c>ISnippetMatcher</c> are internal: callers go through <see cref="Resolve"/> only. Selectors
/// (withinMember, matchLine, lineBefore, lineAfter) are not part of the dispatcher yet; callers keep using the
/// <see cref="ContextHelper"/> selector code. The older <see cref="ContextHelper"/> snippet helpers keep their own
/// behaviour and do not delegate here; callers move to <see cref="Resolve"/> deliberately, batch by batch.
/// </remarks>
public static class SnippetResolver
{
    /// <summary>Maximum number of near-matches listed in a result; the full count is in NearMatchTotalCount.</summary>
    internal const int MaxNearMatches = 5;

    // Ladder order, strictest first.
    private static readonly ISnippetMatcher[] Ladder =
    {
        new ExactMatcher(),
        new LineEndingMatcher(),
        new IndentationMatcher(),
        new CollapsedWhitespaceMatcher(),
    };

    /// <summary>
    /// Looks <paramref name="snippet"/> up in <paramref name="sourceText"/> and reports the outcome as data.
    /// <list type="bullet">
    /// <item>Matchers run in ladder order up to <paramref name="tolerance"/>; the first tier that matches wins (the
    /// cap: an <see cref="WhitespaceTolerance.Exact"/> request never matches looser).</item>
    /// <item>One match: <see cref="SnippetMatchOutcome.Matched"/>. Several: <see cref="SnippetMatchOutcome.Ambiguous"/>
    /// with all of them in <c>Matches</c>.</item>
    /// <item>None: looser tiers are probed read-only. If one matches, the outcome is
    /// <see cref="SnippetMatchOutcome.MatchedLooserThanRequested"/> with up to <see cref="MaxNearMatches"/> near-matches
    /// (the lowest looser tier that matches, the file's actual lines verbatim, no structured differences yet) and
    /// the total in <c>NearMatchTotalCount</c>. Otherwise <see cref="SnippetMatchOutcome.NoMatch"/> with empty lists
    /// (closest windows arrive in a later slice).</item>
    /// <item>An empty or whitespace-only snippet is <see cref="SnippetMatchOutcome.NoMatch"/>, not an exception.</item>
    /// </list>
    /// Case handling: <paramref name="caseMode"/> = <see cref="SnippetCaseMode.CaseInsensitive"/> is accepted only with
    /// <see cref="WhitespaceTolerance.IgnoreAllWhitespace"/>, because the whitespace-collapsing stages are the only
    /// case-insensitive stages today and no caller needs more. Under it, the ordinal and line-ending stages still
    /// compare case-sensitively, exactly as <see cref="ContextHelper.FindAllSnippetMatchesWithLength"/> does.
    /// <para>What each tier accepts is documented on <see cref="WhitespaceTolerance"/>. <c>Matches</c> are at or
    /// stricter than <paramref name="tolerance"/> and each span is the true span in the text. <c>NearMatches</c> are
    /// reported only when there are no accepted matches, and are never edit candidates: they are a different type
    /// from <see cref="SnippetMatch"/>. This method never throws for a match outcome (no match, several matches,
    /// near-matches only, a blank snippet); every such outcome is returned in the result.</para>
    /// </summary>
    /// <param name="sourceText">The text to search.</param>
    /// <param name="snippet">The snippet to find. Empty or whitespace-only yields <see cref="SnippetMatchOutcome.NoMatch"/>.</param>
    /// <param name="tolerance">The loosest tier the caller accepts. There is no default.</param>
    /// <param name="caseMode">Whether letter case is compared. There is no default; see <see cref="SnippetCaseMode"/>.</param>
    /// <returns>The matches, near-matches and outcome; see <see cref="SnippetMatchResult"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="sourceText"/> or <paramref name="snippet"/> is null.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="tolerance"/> or <paramref name="caseMode"/> is not a defined value, or <paramref name="caseMode"/>
    /// is CaseInsensitive with a tolerance below IgnoreAllWhitespace. These are programming errors, never match
    /// outcomes.
    /// </exception>
    public static SnippetMatchResult Resolve(
        SourceText sourceText, string snippet, WhitespaceTolerance tolerance, SnippetCaseMode caseMode)
    {
        ArgumentNullException.ThrowIfNull(sourceText);
        ArgumentNullException.ThrowIfNull(snippet);

        if (!Enum.IsDefined(tolerance))
        {
            throw new ArgumentException($"Unknown WhitespaceTolerance value {(int)tolerance}.", nameof(tolerance));
        }

        if (!Enum.IsDefined(caseMode))
        {
            throw new ArgumentException($"Unknown SnippetCaseMode value {(int)caseMode}.", nameof(caseMode));
        }

        if (caseMode == SnippetCaseMode.CaseInsensitive && tolerance < WhitespaceTolerance.IgnoreAllWhitespace)
        {
            throw new ArgumentException(
                $"SnippetCaseMode.CaseInsensitive is supported only with WhitespaceTolerance.IgnoreAllWhitespace (requested {tolerance}): " +
                "the whitespace-collapsing stages are the only case-insensitive stages today.",
                nameof(caseMode));
        }

        if (string.IsNullOrWhiteSpace(snippet))
        {
            return Build(tolerance, caseMode, SnippetMatchOutcome.NoMatch, Array.Empty<SnippetMatch>(), Array.Empty<SnippetNearMatch>(), 0);
        }

        // Requested tiers, strictest first; the first one that matches ends the search.
        foreach (var matcher in Ladder)
        {
            if (matcher.Tier > tolerance)
            {
                break;
            }

            var matches = matcher.FindAll(sourceText, snippet, CaseModeFor(matcher, caseMode));
            if (matches.Count > 0)
            {
                return Build(
                    tolerance, caseMode,
                    matches.Count == 1 ? SnippetMatchOutcome.Matched : SnippetMatchOutcome.Ambiguous,
                    matches, Array.Empty<SnippetNearMatch>(), 0);
            }
        }

        // Nothing at or below the requested tier: probe the looser tiers, read-only, lowest first.
        foreach (var matcher in Ladder)
        {
            if (matcher.Tier <= tolerance)
            {
                continue;
            }

            var found = matcher.FindAll(sourceText, snippet, CaseModeFor(matcher, caseMode));
            if (found.Count > 0)
            {
                var nearMatches = found.Take(MaxNearMatches).Select(m => ToNearMatch(sourceText, m)).ToList();
                return Build(
                    tolerance, caseMode, SnippetMatchOutcome.MatchedLooserThanRequested,
                    Array.Empty<SnippetMatch>(), nearMatches, found.Count);
            }
        }

        return Build(tolerance, caseMode, SnippetMatchOutcome.NoMatch, Array.Empty<SnippetMatch>(), Array.Empty<SnippetNearMatch>(), 0);
    }

    // Only the whitespace-collapsing matcher is case-insensitive; the ordinal and line-ending stages stay
    // case-sensitive under a CaseInsensitive request, as in ContextHelper.FindAllSnippetMatchesWithLength.
    private static SnippetCaseMode CaseModeFor(ISnippetMatcher matcher, SnippetCaseMode requested)
        => matcher.Tier == WhitespaceTolerance.IgnoreAllWhitespace ? requested : SnippetCaseMode.CaseSensitive;

    private static SnippetNearMatch ToNearMatch(SourceText sourceText, SnippetMatch match)
    {
        // The last character of the span decides the last line (a span ending in a line break belongs to the
        // line that break ends). The lines are the file's own text, verbatim, without line breaks.
        var firstLine = sourceText.Lines.GetLinePosition(match.Start).Line;
        var lastLine = sourceText.Lines.GetLinePosition(Math.Max(match.Start, match.Start + match.Length - 1)).Line;
        var actualLines = new List<string>();
        for (var i = firstLine; i <= lastLine; i++)
        {
            actualLines.Add(sourceText.Lines[i].ToString());
        }

        return new SnippetNearMatch(match.Tier, firstLine + 1, actualLines, Array.Empty<SnippetDifference>());
    }

    private static SnippetMatchResult Build(
        WhitespaceTolerance tolerance, SnippetCaseMode caseMode, SnippetMatchOutcome outcome,
        IReadOnlyList<SnippetMatch> matches, IReadOnlyList<SnippetNearMatch> nearMatches, int nearMatchTotalCount)
        => SnippetMatchResult.Create(
            tolerance, caseMode, outcome, matches, nearMatches, nearMatchTotalCount, Array.Empty<SnippetClosestWindow>(),
            candidatesBeforeSelectors: matches.Count, candidatesAfterSelectors: matches.Count,
            candidatesAfterAdjacentLines: matches.Count);
}
