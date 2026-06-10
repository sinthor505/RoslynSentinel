namespace RoslynSentinel.Common;

/// <summary>
/// A single method flagged by a scout tool via <c>flag_migration_candidate</c>.
/// </summary>
/// <param name="FilePath">Absolute path of the source file containing the method.</param>
/// <param name="MethodName">The method's identifier (case-sensitive).</param>
/// <param name="ClassName">The class that declares the method.</param>
/// <param name="Pattern">
/// The refactoring pattern the method is earmarked for:
/// <c>"AsyncBridgeCandidate"</c>, <c>"HandlerExtract"</c>, <c>"HandlerToAsync"</c>, <c>"AsyncCallerUplift"</c>, etc.
/// </param>
/// <param name="Score">Eligibility score from the scout tool (0 = unscored).</param>
/// <param name="Reason">Human-readable rationale for the flag, or <c>null</c> if none was supplied.</param>
/// <param name="FlaggedDate">ISO date string (yyyy-MM-dd) when the method was flagged, or <c>null</c>.</param>
/// <param name="Line">1-based source line of the method declaration.</param>
public record MigrationCandidateFinding(
    FilePath FilePath,
    string MethodName,
    string ClassName,
    string Pattern,
    int Score,
    string? Reason,
    string? FlaggedDate,
    int Line,
    string ProjectName = ""
)
{
    /// <summary>
    /// Human-readable one-liner combining the most useful fields.
    /// Example: <c>"AsyncBridgeCandidate (score=70): Calls search/isExistedInDB — updateSettlement"</c>.
    /// </summary>
    public string Summary =>
        $"{Pattern} (score={Score})" +
        (string.IsNullOrWhiteSpace(Reason) ? "" : $": {Reason}") +
        $" — {MethodName}";
}
// v2 — ScanOptions() now derived from SentinelScanTools.scan_descriptors (single source of truth)