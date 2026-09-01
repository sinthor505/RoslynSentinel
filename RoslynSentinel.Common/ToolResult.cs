using System.Reflection;

namespace RoslynSentinel.Common;

// ── Server build info ────────────────────────────────────────────────────────

/// <summary>
/// Identifies the running server build. Computed once from the entry assembly so tool
/// responses carry a version signal — without this, a stale-server bug (running binaries
/// older than the latest committed source) is invisible until behavior is investigated by hand.
/// </summary>
public static class ServerBuildInfo
{
    public static readonly string Version;
    public static readonly DateTime BuildTimeUtc;

    static ServerBuildInfo()
    {
        var assembly = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
        Version = assembly.GetName().Version?.ToString() ?? "Unknown";
        BuildTimeUtc = File.Exists(assembly.Location) ? File.GetLastWriteTimeUtc(assembly.Location) : default;
    }
}

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
    public const string NotImplemented = "NotImplemented";

    /// <summary>
    /// A search ran successfully but matched zero results. Distinct from <see cref="NotFound"/>
    /// (a named lookup whose input didn't resolve) — the search itself is valid, it just found
    /// nothing. Surfaced as an error (rather than a quiet success with a warning) so a client
    /// relying on the protocol-level IsError flag sees it as a signal to change approach.
    /// </summary>
    public const string NoMatches = "NoMatches";

    /// <summary>
    /// A <c>files</c>-format apply was rejected because one or more files would shrink by more
    /// than <c>ApplyDiff</c>'s whole-file-rewrite size threshold (a common signature of the
    /// caller submitting only a fragment as if it were the entire file). The response's
    /// <c>message</c> carries a confirmation code the caller can replay via
    /// <c>action: confirmationCode</c> to proceed with the same (cached) changeset.
    /// </summary>
    public const string ConfirmationRequired = "ConfirmationRequired";
}

// ── Envelope ──────────────────────────────────────────────────────────────────

/// <summary>
/// Typed envelope returned by Tool scan tools.
/// Exactly one of <see cref="Data"/>, <see cref="Error"/>, or <see cref="LargeResult"/> is populated.
/// </summary>
public record ToolResult<T>
{
    /// <summary>
    /// Server build identity (assembly version + binary write time). Not settable — every
    /// <see cref="ToolResult{T}"/> carries the same value, computed once in <see cref="ServerBuildInfo"/>.
    /// Lets a caller notice a running server predates a source change without checking DLL
    /// timestamps by hand (see docs/current/feedback_stale_server_before_rebuild.md).
    /// </summary>
    public string ServerVersion { get; init; } = ServerBuildInfo.Version;

    /// <summary>Build timestamp (UTC) of the running server binary. See <see cref="ServerVersion"/>.</summary>
    public DateTime ServerBuildTimeUtc { get; init; } = ServerBuildInfo.BuildTimeUtc;

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
    /// <see cref="LargeResultHelper.StoreLargeResultAsync{T}"/> (populating <see cref="LargeResult"/>
    /// instead of <see cref="Data"/>) when the serialized payload exceeds
    /// <see cref="LargeResultHelper.OffloadThresholdBytes"/>. Use this instead of hand-rolling a
    /// size-check/write-to-disk block per tool (that duplication is what let GetMethodSource and
    /// ReadFile's offload paths silently diverge from GetLargeResult's expected file format).
    /// </summary>
    public static async Task<ToolResult<T>> ForPossiblyLargeDataAsync(
        T data, string? solutionRoot, string resultType, ResultWrapperType wrapperType, int? totalRecords = null, int? workspaceVersion = null, CancellationToken cancellationToken = default)
    {
        var stored = await LargeResultHelper.StoreLargeResultAsync(data, solutionRoot, wrapperType, cancellationToken);
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
                resultId: stored.resultId!,
                sizeBytes: stored.jsonBytes.Length,
                totalRecords: totalRecords ?? 1,
                message: $"Result is {stored.jsonBytes.Length} bytes (threshold: {LargeResultHelper.OffloadThresholdBytes}). " +
                         $"Use GetLargeResult(resultId: \"{stored.resultId}\") to page through results.")
        };
    }

    /// <summary>Error details. Non-null when <see cref="Success"/> is false.</summary>
    public ResultError? Error
    {
        get; init;
    }

    /// <summary>
    /// Present when the result exceeded the inline-size threshold and was written to disk.
    /// Use <c>get_large_result</c> with <see cref="LargeResultInfo.ResultId"/> to page through it.
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
/// Metadata for a large result written to <c>.roslynsentinel/largeresults/largeresult_*.json</c>.
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
    public string ResultId
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
    string resultId,
    long sizeBytes,
    int totalRecords,
    string? message = null
)
    {
        this.ResultType = resultType ?? throw new ArgumentNullException(nameof(resultType));
        this.WrittenToFile = writtenToFile;
        this.FilePath = filePath;
        this.ResultId = resultId ?? throw new ArgumentNullException(nameof(resultId));
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

