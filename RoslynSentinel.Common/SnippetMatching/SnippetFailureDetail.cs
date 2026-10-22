namespace RoslynSentinel.Common.SnippetMatching;

/// <summary>
/// Wire-safe projection of a <see cref="SnippetMatchResult"/> for the tool layer: the lookup outcome and the
/// candidate lists an agent needs to correct a snippet. Deliberately omits the search-effort counters, the full
/// Matches list and other internal fields. ClosestWindows is the engine-capped list; ClosestWindowTotalCount says
/// how many windows existed before the cap.
/// </summary>
public sealed record SnippetFailureDetail(
    string Outcome,
    string RequestedTolerance,
    string RequestedCaseMode,
    IReadOnlyList<string> TiersProbed,
    IReadOnlyList<SnippetClosestWindow> ClosestWindows,
    int ClosestWindowTotalCount,
    int NearMatchTotalCount,
    IReadOnlyList<SnippetFilterStep> FilterTrail,
    int CandidatesBeforeSelectors,
    int CandidatesAfterSelectors,
    string? InvalidParameterName,
    string? InvalidParameterValue)
{
    /// <summary>Projects a lookup result onto its wire-safe shape.</summary>
    public static SnippetFailureDetail From(SnippetMatchResult result)
    {
        return new SnippetFailureDetail(
            result.Outcome.ToString(),
            result.RequestedTolerance.ToString(),
            result.RequestedCaseMode.ToString(),
            result.TiersProbed.Select(tier => tier.ToString()).ToList(),
            result.ClosestWindows,
            result.ClosestWindowTotalCount,
            result.NearMatchTotalCount,
            result.FilterTrail,
            result.CandidatesBeforeSelectors,
            result.CandidatesAfterSelectors,
            result.InvalidParameterName,
            result.InvalidParameterValue);
    }
}
