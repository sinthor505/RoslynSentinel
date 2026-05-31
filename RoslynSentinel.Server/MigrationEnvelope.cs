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
    int     TotalRecords
);

// ── Scan summary ─────────────────────────────────────────────────────────────

/// <summary>
/// Aggregate summary produced when <c>scan_migration_candidates</c> is called with
/// <c>summarize=true</c>.
/// </summary>
public record MigrationScanSummary(
    int                                 TotalCandidates,
    Dictionary<string, int>             ByPattern,
    Dictionary<string, int>             ByProject,
    Dictionary<string, int>             ByScoreBucket
);
