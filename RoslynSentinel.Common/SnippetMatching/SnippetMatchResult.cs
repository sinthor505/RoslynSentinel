namespace RoslynSentinel.Common.SnippetMatching;

/// <summary>
/// One accepted match of a snippet in a text, at or stricter than the requested tier. This is the
/// only match type an edit path accepts: <see cref="SnippetNearMatch"/> and
/// <see cref="SnippetClosestWindow"/> are deliberately unrelated types, so passing a looser-tier
/// hit or a guess to an edit is a compile error. Distinct from the legacy nested
/// <c>ContextHelper.SnippetMatch(Start, Length)</c>, which is removed in the final migration slice.
/// </summary>
/// <param name="Start">0-based character offset where the matched span starts.</param>
/// <param name="Line">1-based line number of <paramref name="Start"/>.</param>
/// <param name="Length">The true length of the matched span in the file text (never the snippet's length).</param>
/// <param name="Tier">The tier at which this match was found; never looser than the requested tier.</param>
/// <param name="MatcherName">Name of the matcher that produced the match.</param>
internal sealed record SnippetMatch(int Start, int Line, int Length, WhitespaceTolerance Tier, string MatcherName);

/// <summary>What kind of difference a <see cref="SnippetDifference"/> describes.</summary>
internal enum SnippetDifferenceKind
{
    /// <summary>The line ending differs (CR, LF or CRLF).</summary>
    LineEnding,

    /// <summary>The line's leading whitespace differs.</summary>
    LeadingWhitespace,

    /// <summary>The line's trailing whitespace differs.</summary>
    TrailingWhitespace,

    /// <summary>Whitespace inside the line differs.</summary>
    InteriorWhitespace,
}

/// <summary>
/// A structured difference between a snippet and the file text a near-match was found in. Data
/// only; wording is built elsewhere.
/// </summary>
/// <param name="Kind">What differs.</param>
/// <param name="Line">1-based line within the snippet.</param>
/// <param name="Expected">The snippet's text for the differing part, verbatim.</param>
/// <param name="Got">The file's text for the differing part, verbatim.</param>
internal sealed record SnippetDifference(SnippetDifferenceKind Kind, int Line, string Expected, string Got);

/// <summary>
/// A match that exists only at a looser tier than the one requested. Never usable for an edit:
/// it shares no base type or interface with <see cref="SnippetMatch"/>.
/// </summary>
/// <param name="LowestTier">The lowest tier at which the snippet matches.</param>
/// <param name="StartLine">1-based line in the file where the near-match starts.</param>
/// <param name="ActualLines">The file's actual lines, verbatim and copy-ready as the next oldContent.</param>
/// <param name="Differences">Structured differences between the snippet and <paramref name="ActualLines"/>.</param>
internal sealed record SnippetNearMatch(
    WhitespaceTolerance LowestTier,
    int StartLine,
    IReadOnlyList<string> ActualLines,
    IReadOnlyList<SnippetDifference> Differences);

/// <summary>
/// One of the file's windows that scored closest to a snippet that matched at no tier. Never
/// usable for an edit: it shares no base type or interface with <see cref="SnippetMatch"/>.
/// </summary>
/// <param name="StartLine">1-based line in the file where the window starts.</param>
/// <param name="ActualLines">The file's actual lines, verbatim.</param>
/// <param name="Score">Similarity score; higher is closer.</param>
internal sealed record SnippetClosestWindow(int StartLine, IReadOnlyList<string> ActualLines, double Score);

/// <summary>
/// Result of looking a snippet up in a text. Carries data only (no message text): wording lives
/// in <see cref="ContextErrorBuilder"/>. Build one with <see cref="Create"/>, which enforces:
/// <see cref="NearMatches"/> is non-empty only when <see cref="Matches"/> is empty, and
/// <see cref="ClosestWindows"/> is non-empty only when both are empty. A violation throws
/// <see cref="ArgumentException"/> (an internal programming error, not a tool error).
/// </summary>
internal sealed record SnippetMatchResult
{
    private SnippetMatchResult(
        WhitespaceTolerance requestedTolerance, SnippetCaseMode requestedCaseMode, SnippetMatchOutcome outcome,
        IReadOnlyList<SnippetMatch> matches, IReadOnlyList<SnippetNearMatch> nearMatches, int nearMatchTotalCount,
        IReadOnlyList<SnippetClosestWindow> closestWindows,
        int candidatesBeforeSelectors, int candidatesAfterSelectors, int candidatesAfterAdjacentLines)
    {
        RequestedTolerance = requestedTolerance;
        RequestedCaseMode = requestedCaseMode;
        Outcome = outcome;
        Matches = matches;
        NearMatches = nearMatches;
        NearMatchTotalCount = nearMatchTotalCount;
        ClosestWindows = closestWindows;
        CandidatesBeforeSelectors = candidatesBeforeSelectors;
        CandidatesAfterSelectors = candidatesAfterSelectors;
        CandidatesAfterAdjacentLines = candidatesAfterAdjacentLines;
    }

    /// <summary>The tier the caller asked for (echoed back).</summary>
    public WhitespaceTolerance RequestedTolerance { get; }

    /// <summary>The case mode the caller asked for (echoed back).</summary>
    public SnippetCaseMode RequestedCaseMode { get; }

    /// <summary>Why the lookup ended as it did, for error-code mapping.</summary>
    public SnippetMatchOutcome Outcome { get; }

    /// <summary>Accepted matches, at or stricter than <see cref="RequestedTolerance"/>.</summary>
    public IReadOnlyList<SnippetMatch> Matches { get; }

    /// <summary>Matches that exist only at a looser tier (capped list). Never usable for an edit.</summary>
    public IReadOnlyList<SnippetNearMatch> NearMatches { get; }

    /// <summary>Total number of near-matches found, which can exceed <see cref="NearMatches"/>.Count when the list was capped.</summary>
    public int NearMatchTotalCount { get; }

    /// <summary>The file's windows closest to the snippet, listed only when nothing matched at any tier.</summary>
    public IReadOnlyList<SnippetClosestWindow> ClosestWindows { get; }

    /// <summary>Filter trail: candidates before withinMember/matchLine selectors ran.</summary>
    public int CandidatesBeforeSelectors { get; }

    /// <summary>Filter trail: candidates remaining after withinMember/matchLine selectors.</summary>
    public int CandidatesAfterSelectors { get; }

    /// <summary>Filter trail: candidates remaining after lineBefore/lineAfter filtering.</summary>
    public int CandidatesAfterAdjacentLines { get; }

    /// <summary>True when exactly one accepted match exists: the only check an editing caller needs.</summary>
    public bool IsSingleMatch => Matches.Count == 1;

    /// <summary>True when near-matches exist. A convenience for diagnostics, never part of a success check.</summary>
    public bool HasNearMatches => NearMatches.Count > 0;

    /// <summary>The one accepted match. Throws <see cref="InvalidOperationException"/> unless <see cref="IsSingleMatch"/>.</summary>
    public SnippetMatch Single()
    {
        if (Matches.Count != 1)
        {
            throw new InvalidOperationException(
                $"Single() requires exactly one accepted match, but there are {Matches.Count}. Check IsSingleMatch first.");
        }

        return Matches[0];
    }

    /// <summary>Gets the one accepted match; returns false (and null) when there are zero or several.</summary>
    public bool TryGetSingle(out SnippetMatch match)
    {
        if (Matches.Count == 1)
        {
            match = Matches[0];
            return true;
        }

        match = null!;
        return false;
    }

    /// <summary>
    /// Creates a result, enforcing the invariants. Throws <see cref="ArgumentException"/> when
    /// near-matches are supplied alongside accepted matches, when closest windows are supplied
    /// alongside matches or near-matches, or when a count is inconsistent.
    /// </summary>
    public static SnippetMatchResult Create(
        WhitespaceTolerance requestedTolerance, SnippetCaseMode requestedCaseMode, SnippetMatchOutcome outcome,
        IReadOnlyList<SnippetMatch> matches, IReadOnlyList<SnippetNearMatch> nearMatches, int nearMatchTotalCount,
        IReadOnlyList<SnippetClosestWindow> closestWindows,
        int candidatesBeforeSelectors, int candidatesAfterSelectors, int candidatesAfterAdjacentLines)
    {
        ArgumentNullException.ThrowIfNull(matches);
        ArgumentNullException.ThrowIfNull(nearMatches);
        ArgumentNullException.ThrowIfNull(closestWindows);

        if (nearMatches.Count > 0 && matches.Count > 0)
        {
            throw new ArgumentException(
                "NearMatches may be non-empty only when Matches is empty: a result has accepted matches or near-matches, never both.",
                nameof(nearMatches));
        }

        if (closestWindows.Count > 0 && (matches.Count > 0 || nearMatches.Count > 0))
        {
            throw new ArgumentException(
                "ClosestWindows may be non-empty only when both Matches and NearMatches are empty.",
                nameof(closestWindows));
        }

        if (nearMatchTotalCount < nearMatches.Count)
        {
            throw new ArgumentOutOfRangeException(
                nameof(nearMatchTotalCount), nearMatchTotalCount, "NearMatchTotalCount cannot be less than NearMatches.Count.");
        }

        if (candidatesBeforeSelectors < 0 || candidatesAfterSelectors < 0 || candidatesAfterAdjacentLines < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(candidatesBeforeSelectors), "Candidate counts cannot be negative.");
        }

        return new SnippetMatchResult(
            requestedTolerance, requestedCaseMode, outcome, matches, nearMatches, nearMatchTotalCount, closestWindows,
            candidatesBeforeSelectors, candidatesAfterSelectors, candidatesAfterAdjacentLines);
    }
}
