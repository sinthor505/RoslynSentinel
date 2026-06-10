using System.Text.Json;

namespace RoslynSentinel.Common;

// ── Error codes ───────────────────────────────────────────────────────────────
public static class ToolErrorCode
{
    public const string SolutionNotLoaded = "SolutionNotLoaded";
    public const string FeatureDisabled = "FeatureDisabled";
    public const string InvalidArgument = "InvalidArgument";
    public const string Exception = "Exception";
}

// ── Envelope ──────────────────────────────────────────────────────────────────

/// <summary>
/// Typed envelope returned by Tool scan tools.
/// Exactly one of <see cref="Data"/>, <see cref="Error"/>, or <see cref="LargeResult"/> is populated.
/// </summary>
public record ToolResult<T>
{
    /// <summary>Threshold in bytes for inlining scan results. Results exceeding this size are written to disk.</summary>
    internal const int ThresholdBytes = 30 * 1024;

    /// <summary>True when the operation completed without error.</summary>
    public bool Success
    {
        get; init;
    }

    /// <summary>Inline payload. Non-null on success when the result fit below the size threshold.</summary>
    public T? Data
    {
        get;
        init
        {
            if (value is null) { return; }
            string json = JsonSerializer.Serialize(value);
            if (json.Length > ThresholdBytes)
            {
                // TODO: write to disk, set _largeResult
                // For now just set the field to the value, but in a real implementation this would be null and the LargeResult property would be populated instead.
                field = value;
            }
            else
            {
                field = value;
            }
        }
    }

    /// <summary>Error details. Non-null when <see cref="Success"/> is false.</summary>
    public ResultError? Error
    {
        get; init;
    }

    /// <summary>
    /// Present when the result exceeded the inline-size threshold and was written to disk.
    /// Use <c>get_scan_result</c> with <see cref="LargeResultInfo.ScanId"/> to page through it.
    /// </summary>
    public LargeResultInfo? LargeResult
    {
        get; init;
    }

    /// <summary>
    /// Total number of records before pagination was applied.
    /// Null when result is a summary (<c>summarize=true</c>) or when paging was not used.
    /// </summary>
    public int? TotalRecords
    {
        get; init;
    }

    /// <summary>True when there are additional pages beyond the current offset+limit window.</summary>
    public bool HasMore
    {
        get; init;
    }
}

// ── Error detail ─────────────────────────────────────────────────────────────

/// <summary>Structured error returned inside <see cref="ToolResult{T}"/>.</summary>
public record ResultError(
    string ErrorCode,
    string Message,
    string? Detail = null
);

// ── Large-result descriptor ───────────────────────────────────────────────────

/// <summary>
/// Metadata for a scan result written to <c>.roslynsentinel/scans/scan_*.json</c>.
/// </summary>
public record LargeResultInfo
{
    public string ResultType
    {
        get; init;
    }
    public bool WrittenToFile
    {
        get; init;
    }
    public FilePath FilePath
    {
        get; init;
    }
    public string ScanId
    {
        get; init;
    }
    public long SizeBytes
    {
        get; init;
    }
    public int TotalRecords
    {
        get; init;
    }
    public string? Message
    {
        get; init;
    }

    public LargeResultInfo(
    string resultType,
    bool writtenToFile,
    FilePath filePath,
    string scanId,
    long sizeBytes,
    int totalRecords,
    string? message = null
)
    {
        this.ResultType = resultType ?? throw new ArgumentNullException(nameof(resultType));
        this.WrittenToFile = writtenToFile;
        this.FilePath = filePath;
        this.ScanId = scanId ?? throw new ArgumentNullException(nameof(scanId));
        this.SizeBytes = sizeBytes == 0 ? throw new ArgumentOutOfRangeException(nameof(sizeBytes)) : sizeBytes;
        this.TotalRecords = totalRecords < 0 ? throw new ArgumentOutOfRangeException(nameof(totalRecords)) : totalRecords;
        this.Message = message;
    }
}

// ── Tool options (describe_advanced_tool_options return type) ─────────────────────────

/// <summary>
/// Return type for <c>describe_advanced_tool_options</c>. Contains the reference enumeration
/// (valid values, field tables, transform catalogues) that was removed from tool
/// descriptions to reduce per-session schema token cost.
/// </summary>
public sealed class ToolOptionsResult
{
    /// <summary>Human-readable reference table (operation×field lists, transform names, etc.).</summary>
    public string? Description
    {
        get; set;
    }

    /// <summary>Machine-readable map of option key → field-list or value list.</summary>
    public Dictionary<string, object>? StructuredOptions
    {
        get; set;
    }

    /// <summary>Non-null when the requested tool name is not recognised.</summary>
    public ResultError? Error
    {
        get; set;
    }
}

