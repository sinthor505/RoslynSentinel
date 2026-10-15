namespace RoslynSentinel.Common.SnippetMatching;

/// <summary>
/// One accepted match of a snippet in a text, at or stricter than the requested tier. This is the
/// only match type an edit path accepts: <see cref="SnippetNearMatch"/> and
/// <see cref="SnippetClosestWindow"/> are deliberately unrelated types, so passing a looser-tier
/// hit or a guess to an edit is a compile error.
/// <para>Which <c>SnippetMatch</c> to use: this type, obtained from <see cref="SnippetResolver.Resolve"/>, is the
/// one for new code (it adds the 1-based line, the tier and the matcher name). It has the same simple name as the
/// legacy nested <c>ContextHelper.SnippetMatch(Start, Length)</c>, which the older <c>ContextHelper.FindAll...</c>
/// helpers still return and which is removed in the final migration slice. Inside the
/// <c>RoslynSentinel.Common.SnippetMatching</c> namespace the simple name means this type; refer to the legacy
/// one as <c>ContextHelper.SnippetMatch</c>.</para>
/// <para>Like the legacy type, <see cref="Length"/> is the true length of the span in the file, not the snippet's
/// length, so a caller that replaces or removes the span must use it.</para>
/// </summary>
/// <param name="Start">0-based character offset where the matched span starts.</param>
/// <param name="Line">1-based line number of <paramref name="Start"/>.</param>
/// <param name="Length">The true length of the matched span in the file text (never the snippet's length).</param>
/// <param name="Tier">The tier at which this match was found; never looser than the requested tier.</param>
/// <param name="MatcherName">Name of the matcher that produced the match.</param>
public sealed record SnippetMatch(int Start, int Line, int Length, WhitespaceTolerance Tier, string MatcherName);

/// <summary>What kind of difference a <see cref="SnippetDifference"/> describes.</summary>
public enum SnippetDifferenceKind
{
    /// <summary>The line ending after the line differs (CR, LF or CRLF). Expected and Got hold the names CR, LF or CRLF.</summary>
    LineEnding,

    /// <summary>The line's leading whitespace differs.</summary>
    LeadingWhitespace,

    /// <summary>The line's trailing whitespace differs.</summary>
    TrailingWhitespace,

    /// <summary>
    /// Whitespace inside the line differs (a run of a different width, a tab for spaces, or a different line break
    /// structure inside a collapsed window). Expected and Got hold the trimmed line text.
    /// </summary>
    InteriorWhitespace,
}

/// <summary>
/// A structured difference between a snippet and the file text a near-match was found in. Data
/// only; wording is built elsewhere. <see cref="SnippetResolver.Resolve"/> fills these into every near-match's
/// <see cref="SnippetNearMatch.Differences"/>: one entry per kind per differing line, in snippet-line order. A line that
/// would be accepted by the exact matcher (including a first line that starts mid-line and a last line that stops
/// mid-line) has no entry.
/// </summary>
/// <param name="Kind">What differs.</param>
/// <param name="Line">1-based line within the snippet as the caller sent it (leading blank lines count).</param>
/// <param name="Expected">The snippet's text for the differing part, verbatim.</param>
/// <param name="Got">The file's text for the differing part, verbatim.</param>
/// <param name="FileLine">1-based line in the file the snippet line was compared with; 0 when not recorded.</param>
/// <param name="ActualLineText">The whole file line, verbatim and without its line break, ready to be copied into the
/// next snippet; empty when not recorded.</param>
public sealed record SnippetDifference(
    SnippetDifferenceKind Kind, int Line, string Expected, string Got, int FileLine = 0, string ActualLineText = "");

/// <summary>
/// A match that exists only at a looser tier than the one requested. Never usable for an edit:
/// it shares no base type or interface with <see cref="SnippetMatch"/>, so passing one to an edit path is a
/// compile error. Its <see cref="ActualLines"/> are the file's own text, to be sent back as the next snippet.
/// </summary>
/// <param name="LowestTier">The lowest tier at which the snippet matches.</param>
/// <param name="StartLine">1-based line in the file where the near-match starts.</param>
/// <param name="ActualLines">The file's actual lines, verbatim and copy-ready as the next oldContent.</param>
/// <param name="Differences">Structured differences between the snippet and <paramref name="ActualLines"/>.</param>
public sealed record SnippetNearMatch(
    WhitespaceTolerance LowestTier,
    int StartLine,
    IReadOnlyList<string> ActualLines,
    IReadOnlyList<SnippetDifference> Differences);

/// <summary>
/// One of the file's windows that scored closest to a snippet that matched at no tier. Never
/// usable for an edit: it shares no base type or interface with <see cref="SnippetMatch"/>. The dispatcher lists
/// these in <see cref="SnippetMatchResult.ClosestWindows"/> only when nothing matched at any tier (see
/// <c>SnippetCandidateFinder</c> for the scoring, threshold and cap).
/// </summary>
/// <param name="StartLine">1-based line in the file where the window starts.</param>
/// <param name="ActualLines">The file's actual lines, verbatim.</param>
/// <param name="Score">Similarity score in 0..1; higher is closer. Never 1 (a full match would have been found).</param>
public sealed record SnippetClosestWindow(int StartLine, IReadOnlyList<string> ActualLines, double Score);

/// <summary>
/// One entry of a result's filter trail: a selector that was applied after tier resolution and how many matches it
/// removed. Data only. Entries are in application order: matchLine, withinMember, lineBefore, lineAfter (a selector
/// that was not supplied has no entry).
/// </summary>
/// <param name="Selector">The selector's parameter name: <c>matchLine</c>, <c>withinMember</c>, <c>lineBefore</c> or <c>lineAfter</c>.</param>
/// <param name="Removed">How many of the then-remaining matches this selector removed.</param>
public sealed record SnippetFilterStep(string Selector, int Removed);

/// <summary>
/// Result of looking a snippet up in a text. Carries data only (no message text): wording lives
/// in the legacy ContextErrorBuilder (removed). Build one with <see cref="Create"/>, which enforces:
/// <see cref="NearMatches"/> is non-empty only when <see cref="Matches"/> is empty, and
/// <see cref="ClosestWindows"/> is non-empty only when both are empty. A violation throws
/// <see cref="ArgumentException"/> (an internal programming error, not a tool error).
/// <para>A result has accepted matches, near-matches or neither, never both. <see cref="IsSingleMatch"/> is the
/// only check an editing caller needs; do not compare tiers (a stricter match than requested is acceptable).
/// Read <see cref="Outcome"/> to map a failure to an error code.</para>
/// </summary>
public sealed record SnippetMatchResult
{
    private SnippetMatchResult(
        WhitespaceTolerance requestedTolerance, SnippetCaseMode requestedCaseMode, SnippetMatchOutcome outcome,
        IReadOnlyList<SnippetMatch> matches, IReadOnlyList<SnippetNearMatch> nearMatches, int nearMatchTotalCount,
        IReadOnlyList<SnippetClosestWindow> closestWindows,
        int candidatesBeforeSelectors, int candidatesAfterSelectors, int candidatesAfterAdjacentLines,
        IReadOnlyList<SnippetFilterStep> filterTrail, IReadOnlyList<SnippetMatch>? preFilterMatches,
        IReadOnlyList<SnippetMatch> selectorSurvivors, string? invalidParameterName, string? invalidParameterValue,
        IReadOnlyList<WhitespaceTolerance> tiersProbed, int closestWindowTotalCount, int linesExamined, int windowsExamined)
    {
        TiersProbed = tiersProbed;
        ClosestWindowTotalCount = closestWindowTotalCount;
        LinesExamined = linesExamined;
        WindowsExamined = windowsExamined;
        FilterTrail = filterTrail;
        preFilterMatchesOrNull = preFilterMatches;
        SelectorSurvivors = selectorSurvivors;
        InvalidParameterName = invalidParameterName;
        InvalidParameterValue = invalidParameterValue;
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

    /// <summary>
    /// How many windows scored at or above the minimum similarity, which can exceed <see cref="ClosestWindows"/>.Count
    /// when the list was capped (the caller can say how many were omitted). Zero unless <see cref="ClosestWindows"/> is
    /// non-empty.
    /// </summary>
    public int ClosestWindowTotalCount { get; }

    /// <summary>
    /// The tiers the dispatcher actually ran, strictest first (it stops at the first tier that matches, and a blank
    /// snippet runs none). The case mode is <see cref="RequestedCaseMode"/>. Empty when the producer did not record it.
    /// A result rebuilt by the selector filter carries the tier result's value forward unchanged.
    /// </summary>
    public IReadOnlyList<WhitespaceTolerance> TiersProbed { get; }

    /// <summary>Number of file lines examined while looking for closest windows; zero when no window search ran.</summary>
    public int LinesExamined { get; }

    /// <summary>Number of candidate windows scored while looking for closest windows; zero when no window search ran.</summary>
    public int WindowsExamined { get; }

    /// <summary>
    /// True when this result lists no candidate of any kind: no accepted matches, no near-matches, no closest windows
    /// and no pre-filter matches. An error built from such a result must say that nothing close was found rather than
    /// stay silent. Describes the lists in this result only.
    /// </summary>
    public bool HasNoCandidates =>
        Matches.Count == 0 && NearMatches.Count == 0 && ClosestWindowTotalCount == 0 && PreFilterMatches.Count == 0;

    /// <summary>Filter trail: candidates before withinMember/matchLine selectors ran.</summary>
    public int CandidatesBeforeSelectors { get; }

    /// <summary>Filter trail: candidates remaining after withinMember/matchLine selectors.</summary>
    public int CandidatesAfterSelectors { get; }

    /// <summary>Filter trail: candidates remaining after lineBefore/lineAfter filtering.</summary>
    public int CandidatesAfterAdjacentLines { get; }

    private readonly IReadOnlyList<SnippetMatch>? preFilterMatchesOrNull;

    /// <summary>
    /// Which selectors were applied after tier resolution and how many matches each removed, in application order.
    /// Empty when no selector ran (none supplied, or the lookup ended before selectors could apply).
    /// </summary>
    public IReadOnlyList<SnippetFilterStep> FilterTrail { get; }

    /// <summary>
    /// The matches at the winning tier before any selector ran. Equal to <see cref="Matches"/> when no selector ran.
    /// On <see cref="SnippetMatchOutcome.SelectorMismatch"/> and <see cref="SnippetMatchOutcome.AdjacentLineMismatch"/>
    /// <see cref="Matches"/> is empty and this is where the candidates for the error message live.
    /// </summary>
    public IReadOnlyList<SnippetMatch> PreFilterMatches => preFilterMatchesOrNull ?? Matches;

    /// <summary>
    /// The matches that survived the withinMember/matchLine selectors, before lineBefore/lineAfter ran. Equal to
    /// <see cref="Matches"/> when the adjacent-line selectors did not run or removed nothing; on
    /// <see cref="SnippetMatchOutcome.AdjacentLineMismatch"/> it holds the matches that lineBefore/lineAfter rejected.
    /// </summary>
    public IReadOnlyList<SnippetMatch> SelectorSurvivors { get; }

    /// <summary>For <see cref="SnippetMatchOutcome.InvalidDisambiguator"/>: the name of the offending parameter (lineBefore, lineAfter or withinMember); otherwise null.</summary>
    public string? InvalidParameterName { get; }

    /// <summary>For <see cref="SnippetMatchOutcome.InvalidDisambiguator"/>: the offending value, verbatim; otherwise null.</summary>
    public string? InvalidParameterValue { get; }

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
        int candidatesBeforeSelectors, int candidatesAfterSelectors, int candidatesAfterAdjacentLines,
        IReadOnlyList<SnippetFilterStep>? filterTrail = null, IReadOnlyList<SnippetMatch>? preFilterMatches = null,
        IReadOnlyList<SnippetMatch>? selectorSurvivors = null, string? invalidParameterName = null, string? invalidParameterValue = null,
        IReadOnlyList<WhitespaceTolerance>? tiersProbed = null, int? closestWindowTotalCount = null,
        int linesExamined = 0, int windowsExamined = 0)
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

        var windowTotal = closestWindowTotalCount ?? closestWindows.Count;
        if (windowTotal < closestWindows.Count)
        {
            throw new ArgumentOutOfRangeException(
                nameof(closestWindowTotalCount), windowTotal, "ClosestWindowTotalCount cannot be less than ClosestWindows.Count.");
        }

        if (windowTotal > 0 && (matches.Count > 0 || nearMatches.Count > 0))
        {
            throw new ArgumentException(
                "ClosestWindowTotalCount may be non-zero only when both Matches and NearMatches are empty.",
                nameof(closestWindowTotalCount));
        }

        if (linesExamined < 0 || windowsExamined < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(linesExamined), "LinesExamined and WindowsExamined cannot be negative.");
        }

        return new SnippetMatchResult(
            requestedTolerance, requestedCaseMode, outcome, matches, nearMatches, nearMatchTotalCount, closestWindows,
            candidatesBeforeSelectors, candidatesAfterSelectors, candidatesAfterAdjacentLines,
            filterTrail ?? Array.Empty<SnippetFilterStep>(), preFilterMatches, selectorSurvivors ?? matches,
            invalidParameterName, invalidParameterValue,
            tiersProbed ?? Array.Empty<WhitespaceTolerance>(), windowTotal, linesExamined, windowsExamined);
    }
}
