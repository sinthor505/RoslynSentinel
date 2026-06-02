using System.Collections.Generic;

namespace RoslynSentinel.Server;

// ── Error codes ───────────────────────────────────────────────────────────────
public static class MigrationErrorCode
{
    public const string SolutionNotLoaded = "SolutionNotLoaded";
    public const string FeatureDisabled   = "FeatureDisabled";
    public const string InvalidArgument   = "InvalidArgument";
    public const string Exception         = "Exception";
}

// ── Envelope ──────────────────────────────────────────────────────────────────

/// <summary>
/// Typed envelope returned by migration scan tools.
/// Exactly one of <see cref="Data"/>, <see cref="Error"/>, or <see cref="LargeResult"/> is populated.
/// </summary>
public record MigrationResult<T>
{
    /// <summary>True when the operation completed without error.</summary>
    public bool Success { get; init; }

    /// <summary>Inline payload. Non-null on success when the result fit below the size threshold.</summary>
    public T? Data { get; init; }

    /// <summary>Error details. Non-null when <see cref="Success"/> is false.</summary>
    public ResultError? Error { get; init; }

    /// <summary>
    /// Present when the result exceeded the inline-size threshold and was written to disk.
    /// Use <c>get_scan_result</c> with <see cref="LargeResultInfo.OperationId"/> to page through it.
    /// </summary>
    public LargeResultInfo? LargeResult { get; init; }

    /// <summary>
    /// Total number of records before pagination was applied.
    /// Null when result is a summary (<c>summarize=true</c>) or when paging was not used.
    /// </summary>
    public int? TotalRecords { get; init; }

    /// <summary>True when there are additional pages beyond the current offset+limit window.</summary>
    public bool HasMore { get; init; }
}

// ── Error detail ─────────────────────────────────────────────────────────────

/// <summary>Structured error returned inside <see cref="MigrationResult{T}"/>.</summary>
public record ResultError(
    string  ErrorCode,
    string  Message,
    string? Detail = null
);

// ── Large-result descriptor ───────────────────────────────────────────────────

/// <summary>
/// Metadata for a scan result written to <c>.roslynsentinel/operations/scan_*.json</c>.
/// </summary>
public record LargeResultInfo(
    bool    WrittenToFile,
    string  FilePath,
    string  OperationId,
    long    SizeBytes,
    int     TotalRecords,
    string? Message = null
);

// ── Scan summary ─────────────────────────────────────────────────────────────

/// <summary>
/// Per-class row in <see cref="MigrationScanSummary.ByClass"/> (paged/non-summary path).
/// Groups all candidates that share the same class and source file.
/// </summary>
public sealed class ClassCandidateSummary
{
    public string ClassName   { get; set; } = string.Empty;
    public string ProjectName { get; set; } = string.Empty;  // .csproj name, not full path
    public string FilePath    { get; set; } = string.Empty;  // absolute path to the source file
    public int    Count       { get; set; }
}

/// <summary>
/// Slim per-class row used in <see cref="MigrationScanSummary.ByClass"/> (summarize=true path).
/// Omits FilePath to keep the summary response small.
/// </summary>
public sealed record ClassCandidateSummarySlim(
    string ClassName,
    string ProjectName,
    int    Count);

/// <summary>
/// Slim candidate entry used in <see cref="MigrationScanSummary.TopCandidates"/> (summarize=true path).
/// Omits FilePath, FlaggedDate, Line, and full Reason breakdown to keep the summary small.
/// </summary>
public sealed record TopCandidateSummaryEntry(
    string MethodName,
    string ClassName,
    string Pattern,
    int    Score,
    string Summary);  // truncated to 120 chars

/// <summary>
/// Aggregate summary produced when <c>scan_migration_candidates</c> is called with
/// <c>summarize=true</c>.
/// </summary>
public record MigrationScanSummary(
    int                                  TotalCandidates,
    Dictionary<string, int>              ByPattern,
    List<ClassCandidateSummarySlim>      ByClass,
    Dictionary<string, int>              ByScoreBucket,
    List<TopCandidateSummaryEntry>?      TopCandidates    = null,
    bool                                 ByClassTruncated = false
);

// ── Tool options (describe_advanced_tool_options return type) ─────────────────────────

/// <summary>
/// Return type for <c>describe_advanced_tool_options</c>. Contains the reference enumeration
/// (valid values, field tables, transform catalogues) that was removed from tool
/// descriptions to reduce per-session schema token cost.
/// </summary>
public sealed class ToolOptionsResult
{
    /// <summary>Human-readable reference table (operation×field lists, transform names, etc.).</summary>
    public string? Description { get; set; }

    /// <summary>Machine-readable map of option key → field-list or value list.</summary>
    public Dictionary<string, object>? StructuredOptions { get; set; }

    /// <summary>Non-null when the requested tool name is not recognised.</summary>
    public ResultError? Error { get; set; }
}
