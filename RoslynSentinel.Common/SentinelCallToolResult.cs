using System.Reflection;

namespace RoslynSentinel.Common;

// ── Server build info ────────────────────────────────────────────────────────

/// <summary>
/// Identifies the running server build. Computed once from the entry assembly so tool
/// responses carry a version signal -> without this, a stale-server bug (running binaries
/// older than the latest committed source) is invisible until behavior is investigated by hand.
/// </summary>
public static class ServerBuildInfo
{
    public static readonly string Version;
    public static readonly DateTime BuildTimeUtc;
    // Added by InsertMemberAfter (expected - used for diagnostics)
    /// <summary>
    /// Full path to the running server's entry assembly (.dll) on disk. Lets a caller compare the
    /// binary's actual location against the repo/worktree path it's editing -> resolving both which
    /// server instance it's talking to (multi-instance ambiguity) and whether that instance is even
    /// in the right repo, without a watcher, restart, or new failure mode. Empty when the entry
    /// assembly has no on-disk location (e.g. single-file publish).
    /// </summary>
    public static readonly string BinaryPath;
    // Added by InsertMemberAfter (expected - used for diagnostics)
    /// <summary>
    /// Process ID of the running server. Lets a caller that has already compared <see cref="BinaryPath"/>
    /// across multiple running instances (multi-instance ambiguity, see <see cref="BinaryPath"/>'s remarks)
    /// kill the exact stale process by PID, rather than correlating binary path back to a live process by
    /// hand. Read from <see cref="Environment.ProcessId"/> -> a static property, no I/O, cannot fail.
    /// </summary>
    public static readonly int Pid; static ServerBuildInfo()
    {
        var assembly = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
        Version = assembly.GetName().Version?.ToString() ?? "Unknown";
        BuildTimeUtc = File.Exists(assembly.Location) ? File.GetLastWriteTimeUtc(assembly.Location) : default;
        BinaryPath = assembly.Location;
        Pid = Environment.ProcessId;
    }
}

// ── ErrorData codes ───────────────────────────────────────────────────────────────
public static class ToolErrorCode
{
    public const string SolutionNotLoaded = "SolutionNotLoaded";
    public const string FeatureDisabled = "FeatureDisabled";
    public const string InvalidArgument = "InvalidArgument";
    public const string BuildFailed = "BuildFailed";
    public const string TestRunFailed = "TestRunFailed";
    public const string NotFound = "NotFound";
    public const string Ambiguous = "Ambiguous";
    public const string DiffApplyFailed = "DiffApplyFailed";
    public const string Exception = "Exception";
    public const string NotImplemented = "NotImplemented";
    public const string SessionHalted = "SessionHalted";

    /// <summary>
    /// A search ran successfully but matched zero results. Distinct from <see cref="NotFound"/>
    /// (a named lookup whose input didn't resolve) -> the search itself is valid, it just found
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
/// Exactly one of <see cref="SuccessData"/>, <see cref="ErrorData"/>, or <see cref="LargeResult"/> is populated.
/// </summary>
/// <remarks>
/// Two generic parameters: <typeparamref name="TSuccess"/> for <see cref="SuccessData"/>,
/// <typeparamref name="TError"/> for <see cref="ErrorData"/> - most tools only need to vary the
/// success shape, so <see cref="SentinelCallToolResult{T}"/> (below) fixes TError to the shared
/// <see cref="ResultError"/> and is what nearly every tool method actually returns. A tool opts into
/// a structured, non-ResultError error shape by returning this base type directly instead.
/// </remarks>
public record SentinelCallToolResult<TSuccess, TError>
{
    /// <summary>
    /// Server build identity (assembly version + binary write time). Not settable -> every
    /// <see cref="SentinelCallToolResult{T}"/> carries the same value, computed once in <see cref="ServerBuildInfo"/>.
    /// Lets a caller notice a running server predates a source change without checking DLL
    /// timestamps by hand (see docs/current/feedback_stale_server_before_rebuild.md).
    /// </summary>
    public ServerInfo ServerInfo { get; init; } = new();

    // Added by InsertMemberAfter (expected - used for diagnostics)
    /// <summary>
    /// Unique identifier for this specific response, generated fresh per instance. Not settable ->
    /// exists solely so a human or agent reviewing a transcript/log can locate the exact tool
    /// response being discussed (e.g. "the call with ResponseId abc123..."), which a duplicate
    /// tool name + similar arguments across many turns cannot do on its own.
    /// </summary>
    public string ResponseId { get; init; } = Guid.NewGuid().ToString();

    /// <summary>True when the operation completed without error.</summary>
    public bool IsSuccess
    {
        get; init;
    }

    /// <summary>
    /// Inline payload. A plain passthrough - this record does not decide on its own whether a
    /// value is "too large," since doing that here would need an async disk write inside a
    /// property accessor, which isn't possible. Callers that might produce an oversized payload
    /// should use <see cref="SentinelCallToolResult{T}.ForPossiblyLargeDataAsync"/> instead of
    /// setting this directly.
    /// </summary>
    public TSuccess? SuccessData
    {
        get; init;
    }

    /// <summary>ErrorData details. Non-null when <see cref="IsSuccess"/> is false.</summary>
    public TError? ErrorData
    {
        get; init;
    }

    /// <summary>Non-fatal observations surfaced alongside the result. Empty when there are none.</summary>
    public IReadOnlyList<Finding> Findings { get; init; } = [];

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
    public string? WarningDetails
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

    // Added by AddMember (expected - used for diagnostics)
    public string? StatusMessage
    {
        get; init;
    }
}

/// <summary>
/// The envelope shape nearly every tool method returns: <see cref="SentinelCallToolResult{TSuccess, TError}.ErrorData"/>
/// fixed to the shared <see cref="ResultError"/> shape, only the success payload varies by <typeparamref name="T"/>.
/// See <see cref="SentinelCallToolResult{TSuccess, TError}"/> for the two-parameter base a tool can
/// return directly instead, when its error shape needs to be more than a flat code/message/detail.
/// </summary>
public record SentinelCallToolResult<T> : SentinelCallToolResult<T, ResultError>
{
    /// <summary>
    /// Builds a <see cref="SentinelCallToolResult{T}"/> for <paramref name="data"/>, offloading to disk via
    /// <see cref="LargeResultHelper.StoreLargeResultAsync{T}"/> (populating <see cref="SentinelCallToolResult{TSuccess, TError}.LargeResult"/>
    /// instead of <see cref="SentinelCallToolResult{TSuccess, TError}.SuccessData"/>) when the serialized payload exceeds
    /// <see cref="LargeResultHelper.OffloadThresholdBytes"/>. Use this instead of hand-rolling a
    /// size-check/write-to-disk block per tool (that duplication is what let GetMethodSource and
    /// ReadFile's offload paths silently diverge from GetLargeResult's expected file format).
    /// </summary>
    public static async Task<SentinelCallToolResult<T>> ForPossiblyLargeDataAsync(
        T data, string? solutionRoot, string resultType, ResultWrapperType wrapperType, int? totalRecords = null, int? workspaceVersion = null, string? statusMessage = null, CancellationToken cancellationToken = default)
    {
        var stored = await LargeResultHelper.StoreLargeResultAsync(data, solutionRoot, wrapperType, cancellationToken);
        if (!stored.offloaded)
        {
            return new SentinelCallToolResult<T> { IsSuccess = true, SuccessData = data, TotalRecords = totalRecords, WorkspaceVersion = workspaceVersion, StatusMessage = statusMessage };
        }

        return new SentinelCallToolResult<T>
        {
            IsSuccess = true,
            TotalRecords = totalRecords,
            WorkspaceVersion = workspaceVersion,
            StatusMessage = statusMessage,
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
}

// ── ErrorData detail ─────────────────────────────────────────────────────────────

/// <summary>Structured error returned inside <see cref="SentinelCallToolResult{T}"/>.</summary>
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
    public FilePathWrapper FilePath
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
    FilePathWrapper filePath,
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

    /// <summary>Machine-readable map of option key -> field-list or value list.</summary>
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
// Added by AddTopLevelType (expected - used for diagnostics)
/// <summary>
/// Groups the running server's build identity (version, build time, binary path, PID) under a
/// single nested field instead of 4 flat top-level properties. See <see cref="ServerBuildInfo"/>
/// for how each value is computed.
/// </summary>
public record ServerInfo
{
    public string Version { get; init; } = ServerBuildInfo.Version;
    public DateTime BuildTimeUtc { get; init; } = ServerBuildInfo.BuildTimeUtc;
    public string BinaryPath { get; init; } = ServerBuildInfo.BinaryPath;
    public int Pid { get; init; } = ServerBuildInfo.Pid;
}

