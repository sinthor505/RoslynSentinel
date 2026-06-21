namespace RoslynSentinel.Common;

// ── Error codes ───────────────────────────────────────────────────────────────
public static class MigrationErrorCode
{
    public const string SolutionNotLoaded = "SolutionNotLoaded";
    public const string FeatureDisabled = "FeatureDisabled";
    public const string InvalidArgument = "InvalidArgument";
    public const string Exception = "Exception";
}

// ── Envelope ──────────────────────────────────────────────────────────────────

/// <summary>
/// Typed envelope returned by migration scan tools.
/// Exactly one of <see cref="Data"/>, <see cref="Error"/>, or <see cref="LargeResult"/> is populated.
/// </summary>
public record MigrationEnvelope<T> : ToolResult<T> where T : class
{

}

// ── Scan summary ─────────────────────────────────────────────────────────────

/// <summary>
/// Per-class row in <see cref="MigrationScanSummary.ByClass"/> (paged/non-summary path).
/// Groups all candidates that share the same class and source file.
/// </summary>
public sealed class ClassCandidateSummary
{
    public string ClassName { get; set; } = string.Empty;
    public string ProjectName { get; set; } = string.Empty;  // .csproj name, not full path
    public FilePath filePath { get; set; } = string.Empty;  // absolute path to the source file
    public int Count
    {
        get; set;
    }
}

/// <summary>
/// Slim per-class row used in <see cref="MigrationScanSummary.ByClass"/> (summarize=true path).
/// Omits FilePath to keep the summary response small.
/// </summary>
public sealed record ClassCandidateSummarySlim(
    string ClassName,
    string ProjectName,
    int Count);

/// <summary>
/// Slim candidate entry used in <see cref="MigrationScanSummary.TopCandidates"/> (summarize=true path).
/// Omits FilePath, FlaggedDate, Line, and full Reason breakdown to keep the summary small.
/// </summary>
public sealed record TopCandidateSummaryEntry(
    string MethodName,
    string ClassName,
    string Pattern,
    int Score,
    string Summary);  // truncated to 120 chars

/// <summary>
/// Aggregate summary produced when <c>scan_migration_candidates</c> is called with
/// <c>summarize=true</c>.
/// </summary>
public record MigrationScanSummary(
    int TotalCandidates,
    Dictionary<string, int> ByPattern,
    List<ClassCandidateSummarySlim> ByClass,
    Dictionary<string, int> ByScoreBucket,
    List<TopCandidateSummaryEntry>? TopCandidates = null,
    bool ByClassTruncated = false,
    /// <summary>
    /// Minimum score across all scanned candidates.
    /// Use as the lower bound when setting <c>scoreThreshold</c> in Asyncify.
    /// </summary>
    int? MinScore = null
);
