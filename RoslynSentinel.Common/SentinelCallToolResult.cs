using System.Reflection;

namespace RoslynSentinel.Common;

// ── Server build info ────────────────────────────────────────────────────────

/// <summary>
/// Identifies the running server build. Computed once from the entry assembly and reported by the
/// McpServerStatus tool (not stamped on every response, to save tokens) so a stale-server bug
/// (running binaries older than the latest source) can be checked for on demand.
/// </summary>
public static class ServerBuildInfo
{
    public static readonly string Version;
    public static readonly DateTime BuildTimeUtc;
    /// <summary>
    /// Full path to the running server's entry assembly (.dll) on disk. Lets a caller compare the
    /// binary's actual location against the repo/worktree path it's editing -> resolving both which
    /// server instance it's talking to (multi-instance ambiguity) and whether that instance is even
    /// in the right repo, without a watcher, restart, or new failure mode. Empty when the entry
    /// assembly has no on-disk location (e.g. single-file publish).
    /// </summary>
    /// 
    public static readonly string BinaryPath;
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

    /// <summary>
    /// MoveMember's changeset was rejected by the compile-gate because one or more call sites
    /// of a moved instance member could not be rewritten unambiguously, and the engine already
    /// explained exactly why (see MoveMemberResult.PendingLedgerEntries/SkippedCallSites). The
    /// caller should retry with callSiteFixups keyed by "FilePath:Line" using one of the named
    /// candidates (or "new"), rather than treating this as an opaque compiler-error failure.
    /// </summary>
    public const string UnresolvedCallSites = "UnresolvedCallSites";

    /// <summary>
    /// The proposed change set was well-formed and matched its target(s), but the write-path
    /// compile gate (ValidationEngine delta-compile) found it would introduce new compiler errors,
    /// so nothing was written. Distinct from <see cref="Exception"/> (an unexpected internal
    /// failure) -> this is an expected, recoverable rejection: fix the listed diagnostics and retry.
    /// </summary>
    public const string ValidationFailed = "ValidationFailed";

    /// <summary>
    /// The target was found and the arguments are well-formed, but the requested change CANNOT be
    /// made to it (e.g. the method is not an extension method, has no body, or has no parameters).
    /// Distinct from <see cref="NotFound"/> (fix the name) and <see cref="InvalidArgument"/> (fix the
    /// argument) -> pick a different target. A target that is already in the requested state is an
    /// idempotent no-op, not this error.
    /// </summary>
    public const string TargetIneligible = "TargetIneligible";

    /// <summary>
    /// The write-path guardrail refused a change to an existing C# file because it would alter that
    /// file's line-ending style (CRLF to LF, or a single style to mixed). Nothing was written. This is
    /// the fingerprint of a whole-file re-serialize/normalize rather than a minimal edit; the message
    /// names each file and its before/after style. Distinct from <see cref="ValidationFailed"/>
    /// (compiler errors) and <see cref="Exception"/> (unexpected failure).
    /// </summary>
    public const string EolChangeRefused = "EolChangeRefused";
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
public record SentinelCallToolResult<TSuccess, TError> : IResultStatus
{
    /// <summary>
    /// <c>true</c> when a newer build of the server's own binaries exists in the repo than the ones this process
    /// loaded, i.e. the running server is stale (see <see cref="ServerBinaryStaleness"/>). Null - and so omitted
    /// from the JSON - when the server is current. Call McpServerStatus for which assemblies differ.
    /// </summary>
    public bool? IsServerBinaryStale { get; init; } = ServerBinaryStaleness.EnvelopeFlag;

    // Fails closed: an envelope nobody marked successful is an error. This is the default the old IsSuccess
    // (default false) had, so a construction site that forgets to set a flag behaves as before.
    private bool _isError = true;

    /// <summary>
    /// <see cref="IResultStatus.IsError"/>: true when the operation did not succeed. The one canonical success flag on
    /// the wire, same polarity and name as the MCP <c>CallToolResult.isError</c> the server sets from it.
    /// </summary>
    public bool IsError
    {
        get => _isError;
        init => _isError = value;
    }

    /// <summary>
    /// Source-compat alias for <c>!</c><see cref="IsError"/> so the many existing <c>IsSuccess = true</c> construction and
    /// read sites keep compiling while they migrate to <see cref="IsError"/>. Never serialized: the wire carries only
    /// <c>isError</c>. Do not add new uses.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsSuccess
    {
        get => !_isError;
        init => _isError = !value;
    }

    /// <summary>
    /// <see cref="IResultStatus.Status"/>: <see cref="ResultStatus.Success"/> on success, otherwise the status for
    /// <see cref="Code"/> (<see cref="ResultStatus.Failed"/> when there is no code or it is unrecognized). Not serialized.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public ResultStatus Status
    {
        get
        {
            if (!IsError)
            {
                return ResultStatus.Success;
            }

            var status = ResultStatusExtensions.FromErrorCode(Code);
            return status == ResultStatus.Success ? ResultStatus.Failed : status;
        }
    }

    /// <summary><see cref="IResultStatus.Code"/>: the <see cref="ResultError.ErrorCode"/> when <see cref="ErrorData"/> is a <see cref="ResultError"/>, else null. Not serialized.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string? Code => (ErrorData as ResultError)?.ErrorCode;

    /// <summary><see cref="IResultStatus.Message"/>: the <see cref="ResultError.Message"/> when <see cref="ErrorData"/> is a <see cref="ResultError"/>, else null. Not serialized.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string? Message => (ErrorData as ResultError)?.Message;

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

    /// <summary>ErrorData details. Non-null when <see cref="IsError"/> is false.</summary>
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
    public bool HasMoreData
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

    public string? StatusMessage
    {
        get; init;
    }

    public ListSummary? ListSummary
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
        T data, string? solutionRoot, string resultType, ResultWrapperType wrapperType, int? totalRecords = null, int? workspaceVersion = null, string? statusMessage = null, ListSummary? listSummary = null, CancellationToken cancellationToken = default)
    {
        var stored = await LargeResultHelper.StoreLargeResultAsync(data, solutionRoot, wrapperType, cancellationToken);
        if (!stored.offloaded)
        {
            return new SentinelCallToolResult<T> { IsError = false, SuccessData = data, TotalRecords = totalRecords, WorkspaceVersion = workspaceVersion, StatusMessage = statusMessage, ListSummary = listSummary };
        }

        return new SentinelCallToolResult<T>
        {
            IsError = false,
            TotalRecords = totalRecords,
            WorkspaceVersion = workspaceVersion,
            StatusMessage = statusMessage,
            ListSummary = listSummary,
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
    string? Detail = null,
    IReadOnlyList<object>? StructuredDetail = null,
    LargeResultInfo? StructuredDetailLargeResult = null
)
{
    /// <summary>
    /// Builds a <see cref="ResultError"/> for a <paramref name="structuredDetail"/> list that may be
    /// too large to inline (e.g. MoveMember's per-call-site unresolved-entry list). Mirrors
    /// <see cref="SentinelCallToolResult{T}.ForPossiblyLargeDataAsync"/>: offloads to disk via
    /// <see cref="LargeResultHelper.StoreLargeResultAsync{T}"/> and populates
    /// <see cref="StructuredDetailLargeResult"/> instead of <see cref="StructuredDetail"/> when the
    /// serialized list exceeds <see cref="LargeResultHelper.OffloadThresholdBytes"/> - without this,
    /// an oversized error fell through to the generic Raw-text offload backstop
    /// (ServiceRegistrationExtensionsBasic.cs's AddCallToolFilter), which pages the whole serialized
    /// envelope as an opaque character window and destroys structuredDetail's JSON structure in the
    /// process. detail/message stay inline either way so the error remains legible without a
    /// GetLargeResult round trip.
    /// </summary>
    public static async Task<ResultError> ForPossiblyLargeDetailAsync(
        string errorCode, string message, string? detail, IReadOnlyList<object>? structuredDetail,
        string? solutionRoot, CancellationToken cancellationToken)
    {
        if (structuredDetail is null or { Count: 0 })
        {
            return new ResultError(errorCode, message, detail, structuredDetail);
        }

        var stored = await LargeResultHelper.StoreLargeResultAsync(
            structuredDetail, solutionRoot, ResultWrapperType.ErrorStructuredDetailList, cancellationToken);
        if (!stored.offloaded)
        {
            return new ResultError(errorCode, message, detail, structuredDetail);
        }

        return new ResultError(errorCode, message, detail, StructuredDetail: null, StructuredDetailLargeResult: new LargeResultInfo(
            resultType: "ErrorStructuredDetailList",
            writtenToFile: true,
            filePath: stored.filePath,
            resultId: stored.resultId!,
            sizeBytes: stored.jsonBytes.Length,
            totalRecords: structuredDetail.Count,
            message: $"structuredDetail is {stored.jsonBytes.Length} bytes ({structuredDetail.Count} entries), over the inline threshold. " +
                     $"Use GetLargeResult(resultId: \"{stored.resultId}\") to page through the full structured list."));
    }
}

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

