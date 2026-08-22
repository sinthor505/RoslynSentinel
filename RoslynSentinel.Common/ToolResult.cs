namespace RoslynSentinel.Common;

// ── Error codes ───────────────────────────────────────────────────────────────
public static class ToolErrorCode
{
    public const string SolutionNotLoaded = "SolutionNotLoaded";
    public const string FeatureDisabled = "FeatureDisabled";
    public const string InvalidArgument = "InvalidArgument";
    public const string BuildFailed = "BuildFailed";
    public const string NotFound = "NotFound";
    public const string Ambiguous = "Ambiguous";
    public const string DiffApplyFailed = "DiffApplyFailed";
    public const string Exception = "Exception";
}

// ── Envelope ──────────────────────────────────────────────────────────────────

/// <summary>
/// Typed envelope returned by Tool scan tools.
/// Exactly one of <see cref="Data"/>, <see cref="Error"/>, or <see cref="LargeResult"/> is populated.
/// </summary>
public record ToolResult<T>
{
    /// <summary>True when the operation completed without error.</summary>
    public bool Success
    {
        get; init;
    }

    /// <summary>
    /// Inline payload. A plain passthrough - this record does not decide on its own whether a
    /// value is "too large," since doing that here would need an async disk write inside a
    /// property accessor, which isn't possible. Callers that might produce an oversized payload
    /// should use <see cref="ForPossiblyLargeDataAsync"/> instead of setting this directly.
    /// </summary>
    public T? Data
    {
        get; init;
    }

    /// <summary>
    /// Builds a <see cref="ToolResult{T}"/> for <paramref name="data"/>, offloading to disk via
    /// <see cref="ScanResultHelper.StoreScanResultAsync{T}"/> (populating <see cref="LargeResult"/>
    /// instead of <see cref="Data"/>) when the serialized payload exceeds
    /// <see cref="ScanResultHelper.ThresholdBytes"/>. Use this instead of hand-rolling a
    /// size-check/write-to-disk block per tool (that duplication is what let GetMethodSource and
    /// ReadFile's offload paths silently diverge from GetScanResult's expected file format).
    /// </summary>
    public static async Task<ToolResult<T>> ForPossiblyLargeDataAsync(
        T data, string? solutionRoot, string resultType, ScanWrapperType wrapperType, int? totalRecords = null, int? workspaceVersion = null)
    {
        var stored = await ScanResultHelper.StoreScanResultAsync(data, solutionRoot, wrapperType);
        if (!stored.offloaded)
        {
            return new ToolResult<T> { Success = true, Data = data, TotalRecords = totalRecords, WorkspaceVersion = workspaceVersion };
        }

        return new ToolResult<T>
        {
            Success = true,
            TotalRecords = totalRecords,
            WorkspaceVersion = workspaceVersion,
            LargeResult = new LargeResultInfo(
                resultType: resultType,
                writtenToFile: true,
                filePath: stored.filePath,
                scanId: stored.scanId!,
                sizeBytes: stored.jsonBytes.Length,
                totalRecords: totalRecords ?? 1,
                message: $"Result is {stored.jsonBytes.Length} bytes (threshold: {ScanResultHelper.ThresholdBytes}). " +
                         $"Use GetScanResult(scanId: \"{stored.scanId}\") to page through results.")
        };
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
    public bool HasMorePages
    {
        get; init;
    }

    /// <summary>
    /// Optional non-fatal hint surfaced alongside a successful result (e.g. a likely-mistaken
    /// argument value). Null when there is nothing noteworthy to flag.
    /// </summary>
    public string? Warning
    {
        get; init;
    }

    /// <summary>
    /// <see cref="IWorkspaceManager.WorkspaceVersion"/> at the time this result was
    /// produced. Null when the tool that produced this result doesn't stamp it. Lets a caller
    /// compare a version fetched by a read tool against one returned by a later write to tell
    /// whether the workspace changed between the two calls (e.g. a cached line number may no
    /// longer be valid).
    /// </summary>
    public int? WorkspaceVersion
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

