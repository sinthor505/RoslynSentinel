namespace RoslynSentinel.Common;

/// <summary>
/// Shared helpers for computing score summaries over migration candidate collections.
/// Used by FlagAsyncMigrationCandidates, ScanAsyncMigrationCandidates, and Asyncify to surface
/// consistent score distribution data so callers can calibrate scoreThreshold.
/// </summary>
public static class CandidateScoreAnalyzer
{
    /// <summary>
    /// Buckets matching those used by <c>scan_migration_candidates</c>:
    /// "&lt;0", "0-25", "26-50", "51-75", "76plus".
    /// </summary>
    public static Dictionary<string, int> ComputeBuckets(IEnumerable<int> scores)
    {
        var buckets = new Dictionary<string, int>
        {
            ["<0"] = 0,
            ["0-25"] = 0,
            ["26-50"] = 0,
            ["51-75"] = 0,
            ["76plus"] = 0,
        };
        foreach (var s in scores)
            buckets[s < 0 ? "<0" : s <= 25 ? "0-25" : s <= 50 ? "26-50" : s <= 75 ? "51-75" : "76plus"]++;
        return buckets;
    }

    /// <summary>Returns the minimum score, or <c>null</c> when the sequence is empty.</summary>
    public static int? ComputeMin(IEnumerable<int> scores)
    {
        int? min = null;
        foreach (var s in scores)
            if (min is null || s < min) min = s;
        return min;
    }

    /// <summary>Returns the maximum score, or <c>null</c> when the sequence is empty.</summary>
    public static int? ComputeMax(IEnumerable<int> scores)
    {
        int? max = null;
        foreach (var s in scores)
            if (max is null || s > max) max = s;
        return max;
    }
}
