using System.Reflection;
using System.Text.Json.Serialization;

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
    public static readonly int Pid;
    /// <summary>
    /// Git commit the running binary was built from: the hex run after '+' in the entry assembly's
    /// <c>AssemblyInformationalVersion</c> (the SDK appends it when <c>SourceRevisionId</c> is set at build time).
    /// Null for an unstamped build (plain local <c>dotnet build</c>).
    /// </summary>
    public static readonly string? BuildSha;

    static ServerBuildInfo()
    {
        var assembly = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
        Version = assembly.GetName().Version?.ToString() ?? "Unknown";
        BuildTimeUtc = File.Exists(assembly.Location) ? File.GetLastWriteTimeUtc(assembly.Location) : default;
        BinaryPath = assembly.Location;
        Pid = Environment.ProcessId;
        BuildSha = ParseBuildSha(assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);
    }

    /// <summary>
    /// Extracts the commit sha from an informational version such as <c>1.0.0+abc1234</c> or
    /// <c>1.0.0+&lt;40 hex&gt;.dirty</c>: the leading run of hex digits after the first '+', accepted only when it is
    /// 7 to 40 characters long. Null when there is no '+' suffix or no such run.
    /// </summary>
    public static string? ParseBuildSha(string? informationalVersion)
    {
        var plus = informationalVersion?.IndexOf('+') ?? -1;
        if (plus < 0)
        {
            return null;
        }

        var metadata = informationalVersion!.AsSpan(plus + 1);
        var length = 0;
        while (length < metadata.Length && char.IsAsciiHexDigit(metadata[length]))
        {
            length++;
        }

        return length is >= 7 and <= 40 ? metadata[..length].ToString() : null;
    }
}

// ── ErrorData codes ───────────────────────────────────────────────────────────────
public static class ToolErrorCode
{
    public const string SolutionNotLoaded = "SolutionNotLoaded";
    public const string SolutionLoadTimeout = "SolutionLoadTimeout";
    public const string FeatureDisabled = "FeatureDisabled";
    public const string InvalidArgument = "InvalidArgument";
    public const string BuildFailed = "BuildFailed";
    public const string TestRunFailed = "TestRunFailed";
    public const string NotFound = "NotFound";
    public const string Ambiguous = "Ambiguous";

    /// <summary>
    /// A contextSnippet/oldContent matched the file only at a looser whitespace tolerance than the call accepts
    /// (for example the snippet's indentation or line endings differ from the file's), so nothing was located or
    /// applied. The message lists each near-match as the file's own copy-ready lines plus the exact whitespace
    /// differences; resend the file's text as shown. Distinct from <see cref="NotFound"/> (nothing close exists:
    /// re-read the file) and <see cref="Ambiguous"/> (several accepted matches: pick one).
    /// </summary>
    public const string MatchedLooserThanRequested = "MatchedLooserThanRequested";

    public const string DiffApplyFailed = "DiffApplyFailed";
    public const string Exception = "Exception";
    public const string NotImplemented = "NotImplemented";
    public const string SessionHalted = "SessionHalted";

    /// <summary>
    /// Legacy code: a search that matches nothing is now a successful, empty result (isError false, totalRecords 0, a statusMessage with guidance). Kept for compatibility; no tool returns it.
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

    /// <summary>
    /// The write-path tripwire refused a change because, for a file whose producing operation declared its text
    /// edits, the text about to be written is not exactly the file's pre-image plus those edits (see
    /// <c>CollateralChangeGuard</c>). Nothing was written. This is the fingerprint of a step that ran after the
    /// edits were built and rewrote more than the operation intended (for example a whole-file reformat); the
    /// message names each file and its first differing line. Distinct from <see cref="EolChangeRefused"/> (only a
    /// line-ending style changed), <see cref="ValidationFailed"/> (compiler errors) and <see cref="Exception"/>.
    /// </summary>
    public const string CollateralChangeRefused = "CollateralChangeRefused";

    /// <summary>
    /// The write-path refused a change because a scoped operation ledger is open (for example after MoveMember left
    /// unresolved call sites) and the target file is not one of the ledger's unresolved files. Nothing was written. The
    /// message carries ready-to-run ExternalFileDrift calls (LedgerList, then LedgerFix or LedgerRelease). Distinct from
    /// <see cref="SessionHalted"/> (the whole session is halted) and <see cref="Exception"/> (unexpected failure).
    /// </summary>
    public const string ScopedLedgerBlocked = "ScopedLedgerBlocked";

    /// <summary>
    /// The write chokepoint refused a batch because a different agent wrote one of the target files after this call
    /// started (see EditAttributionLedger). Nothing was written. The message names the writer. Distinct from
    /// <see cref="ScopedLedgerBlocked"/> (an open scoped operation ledger) and <see cref="SessionHalted"/> (the whole
    /// session is halted).
    /// </summary>
    public const string ConcurrentEditRefused = "ConcurrentEditRefused";

    /// <summary>
    /// The operation was cancelled before it finished.
    /// </summary>
    public const string Cancelled = "Cancelled";

    /// <summary>
    /// The operation exceeded its time limit. Distinct from <see cref="SolutionLoadTimeout"/>, which is specific to the solution load.
    /// </summary>
    public const string TimedOut = "TimedOut";
}

// ── Envelope ──────────────────────────────────────────────────────────────────

/// <summary>Non-generic view of a <see cref="SentinelCallToolResult{TSuccess, TError}"/>, so a caller holding the result as an object can add findings to an error without reflection.</summary>
public interface IFindingsEnvelope
{
    /// <summary>True when the operation did not succeed.</summary>
    bool IsError { get; }

    /// <summary>The error code, or null when there is none.</summary>
    string? Code { get; }

    /// <summary>A copy of this result, of the same runtime type, with <paramref name="extra"/> appended to its findings.</summary>
    object WithAddedFindings(IReadOnlyList<Finding> extra);
}

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
public record SentinelCallToolResult<TSuccess, TError> : IOperationResult, IFindingsEnvelope, IDetachedPayloadSource
{
    /// <summary>
    /// <c>true</c> when a newer build of the server's own binaries exists in the repo than the ones this process
    /// loaded, i.e. the running server is stale (see <see cref="ServerBinaryStaleness"/>). Null - and so omitted
    /// from the JSON - when the server is current. Call McpServerStatus for which assemblies differ.
    /// </summary>
    public bool? IsServerBinaryStale { get; init; } = ServerBinaryStaleness.EnvelopeFlag;

    /// <summary>
    /// One-shot text, set only on the first envelope after a launch whose build of source HEAD failed and fell back to an
    /// older build (see <see cref="LaunchBuildMarker"/>). Null - and so omitted - otherwise.
    /// </summary>
    public string? LaunchWarning { get; init; } = LaunchBuildMarker.TakeEnvelopeWarning();

    // Fails closed: an envelope nobody marked successful is an error, so a construction site that forgets to
    // set a flag reports failure rather than success.
    private bool _isError = true;

    /// <summary>
    /// <see cref="IOperationResult.IsError"/>: true when the operation did not succeed. The one canonical success flag on
    /// the wire, same polarity and name as the MCP <c>CallToolResult.isError</c> the server sets from it.
    /// </summary>
    public bool IsError
    {
        get => _isError;
        init => _isError = value;
    }

    /// <summary>
    /// <see cref="IOperationResult.Status"/>: <see cref="ResultStatus.Success"/> on success, otherwise the status for
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

    /// <summary><see cref="Code"/>: the <see cref="ResultError.ErrorCode"/> when <see cref="ErrorData"/> is a <see cref="ResultError"/>, else null. Not serialized.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string? Code => (ErrorData as ResultError)?.ErrorCode;

    /// <summary><see cref="Message"/>: the <see cref="ResultError.Message"/> when <see cref="ErrorData"/> is a <see cref="ResultError"/>, else null. Not serialized.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string? Message => (ErrorData as ResultError)?.Message;

    /// <summary>
    /// Inline payload. A plain passthrough - this record does not decide on its own whether a
    /// value is "too large," since doing that here would need an async disk write inside a
    /// property accessor, which isn't possible. Callers that might produce an oversized payload
    /// the result marshaller offloads oversized results centrally, so callers set this directly.
    /// </summary>
    public TSuccess? SuccessData
    {
        get; init;
    }

    /// <summary>
    /// Server-side declaration of which lists of <see cref="SuccessData"/> form the pageable record sequence when the
    /// result is over the inline budget (see <see cref="PagingHints"/>, proposal_sequence_pager.md). Not serialized,
    /// like <see cref="Status"/>, <see cref="Code"/> and <see cref="Message"/>. Null means the default sequence.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public PagingHints? Paging { get; init; }

    /// <summary>
    /// Server-side route for a result to name the tools it suggests next when the suggestion is not built through
    /// <see cref="ToolCallHint"/> (guidance-adherence telemetry; see <see cref="GuidanceHint"/>). Not serialized.
    /// <c>ToolResultMarshaller</c> forwards it into the open <see cref="ToolGuidanceCollector"/>. Null means none.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public GuidanceHint? GuidanceHint { get; init; }

    private List<(string Name, object Payload)>? _detached;

    /// <summary>
    /// Payloads that belong in the result store, not in the wire text (proposal_side_stores_never_inline.md, slice 2), added by
    /// <see cref="Detach"/>. Not serialized, like <see cref="Paging"/>. In mode <c>on</c> the marshaller grafts each one into the tree
    /// at <c>successData.&lt;name&gt;</c>, stores the full tree as one Envelope and returns the text with that path moved out; in
    /// <c>off</c> and <c>passthrough</c> they are dropped. A <c>with</c> copy shares this list with the original.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyList<(string Name, object Payload)> Detached => _detached ?? [];

    /// <summary>
    /// Declares <paramref name="payload"/> (a list or a string) as stored, not inline, under <c>successData.&lt;name&gt;</c>.
    /// Throws <see cref="ArgumentException"/> for a bad name, a null payload, a dictionary or any other non-list, non-string value,
    /// or a name already detached on this result.
    /// </summary>
    public void Detach(string name, object payload)
    {
        DetachedPayloads.Validate(name, payload);
        if (_detached is not null && _detached.Exists(d => d.Name == name))
        {
            throw new ArgumentException($"A payload named '{name}' is already detached on this result.", nameof(name));
        }

        (_detached ??= []).Add((name, payload));
    }

    /// <summary>ErrorData details. Non-null when <see cref="IsError"/> is false.</summary>
    public TError? ErrorData
    {
        get; init;
    }

    /// <summary>Non-fatal observations surfaced alongside the result. Empty when there are none.</summary>
    private IReadOnlyList<Finding>? _findings;

    // Explicit findings win; otherwise lift the write-chokepoint findings carried on an AppliedChangeSummary payload,
    // else those carried on a ResultError (error fallback).
    public IReadOnlyList<Finding> Findings
    {
        get => _findings is { Count: > 0 } ? _findings
            : (SuccessData as AppliedChangeSummary)?.Findings ?? (ErrorData as ResultError)?.Findings ?? [];
        init => _findings = value;
    }

    /// <inheritdoc />
    public object WithAddedFindings(IReadOnlyList<Finding> extra) => this with { Findings = [.. Findings, .. extra] };

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
    /// Offset to pass as <c>offset</c> on the next call to continue past this page. Null when no
    /// further rows remain or the tool does not page. Omitted from JSON when null.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public int? NextOffset
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
}

// ── ErrorData detail ─────────────────────────────────────────────────────────────

/// <summary>Structured error returned inside <see cref="SentinelCallToolResult{T}"/>.</summary>
public record ResultError(
    string ErrorCode,
    string Message,
    string? Detail = null,
    IReadOnlyList<object>? StructuredDetail = null
) : IOperationError
{
    /// <summary>The cause category, derived from <see cref="ErrorCode"/>. Not serialized.</summary>
    [JsonIgnore]
    public ResultStatus Category => ResultStatusExtensions.FromErrorCode(ErrorCode);

    /// <summary>Non-fatal observations for this error. Lifted into <see cref="SentinelCallToolResult{TSuccess,TError}.Findings"/>, so it is never serialized inside errorData.</summary>
    [JsonIgnore]
    public IReadOnlyList<Finding>? Findings { get; init; }
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

    /// <summary>
    /// The cuts <see cref="ResultPager"/> made to fit this result inline, one per node (path, returned, total, unit,
    /// inferred). Authoritative for what was omitted; <see cref="TotalRecords"/> stays only for older readers. Null - and so
    /// omitted from the JSON - for every result type that is not an <c>Envelope</c>.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<OmittedPart>? Omitted
    {
        get; init;
    }

    /// <summary>
    /// Sequence-paged results (proposal_sequence_pager.md): the number of units of the sequence on this response's page
    /// (records, or lines for a text sequence); <see cref="TotalRecords"/> is then the sequence total. Null - and so
    /// omitted from the JSON - for every other result.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public int? Returned
    {
        get; init;
    }

    /// <summary>
    /// Sequence-paged results: the <c>offset</c> to pass to <c>GetLargeResult</c> for the next page. Null - and so omitted
    /// from the JSON - when there is no next page or the result is not sequence-paged.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public int? NextOffset
    {
        get; init;
    }

    /// <summary>
    /// Length of the full result's wire text in characters, the unit the inline budget is measured in (<see cref="SizeBytes"/>
    /// is the UTF-8 byte count of the same text). Null, so omitted, when the writer did not measure chars.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public long? SizeChars
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

