namespace RoslynSentinel.Common;

/// <summary>
/// Identity and start state of the current tool call. Set once per call by the outermost request filter, read at
/// the write chokepoint. It is ambient rather than passed as a parameter because there are roughly 50 chokepoint
/// call sites. The reference flows into awaits and <c>Task.Run</c> children, so a mutation such as
/// <see cref="MarkGatePassed"/> made deep in a call is visible to later code in that call without a slot
/// assignment having to flow back up. <see cref="Current"/> is null when no filter set it (engines, tests, other
/// hosts).
/// </summary>
public sealed class ToolCallContext : IDetachedPayloadSource
{
    private static readonly AsyncLocal<ToolCallContext?> Slot = new();

    private int _gatePassed;

    private string? _missingTargetKind;

    private readonly object _detachedLock = new();
    private readonly List<(string Name, object Payload)> _detached = [];

    /// <summary>Creates the context for one tool call.</summary>
    public ToolCallContext(string? agentId, string? agentType, string toolName, string? reason, long startSeq)
    {
        AgentId = agentId;
        AgentType = agentType;
        ToolName = toolName;
        Reason = reason;
        StartSeq = startSeq;
    }

    /// <summary>The context of the current tool call on this async flow, or null when no filter set one.</summary>
    public static ToolCallContext? Current => Slot.Value;

    /// <summary>Stores the context for the current async flow; null clears it.</summary>
    public static void Set(ToolCallContext? context) => Slot.Value = context;

    /// <summary>Agent id from the request, or null when the host does not supply one.</summary>
    public string? AgentId { get; }

    /// <summary>Agent type from the request, or null when the host does not supply one.</summary>
    public string? AgentType { get; }

    /// <summary>Name of the MCP tool being called.</summary>
    public string ToolName { get; }

    /// <summary>The caller's stated reason for the call, or null.</summary>
    public string? Reason { get; }

    /// <summary>Sequence number at call start.</summary>
    public long StartSeq { get; }

    /// <summary>
    /// The call id the response echoes as <c>toolCall.toolCallId</c>, once the echo filter has assigned it (see <see cref="AssignToolCallId"/>);
    /// null before that, when the echo is switched off, or when the host runs no echo filter. Random (48 bits of a v4 GUID), so unique across
    /// calls and across server instances sharing a working tree.
    /// </summary>
    public string? ToolCallId { get; private set; }

    /// <summary>Records the call id the echo filter generated for this call; the first assignment wins.</summary>
    public void AssignToolCallId(string toolCallId) => ToolCallId ??= toolCallId;

    /// <summary>Host session id from the request, or null when the host does not supply one.</summary>
    public string? SessionId { get; init; }

    /// <summary>The call's filePath argument, for the not-found notes (NotFoundEvidence); null when the tool has none.</summary>
    public string? TargetFilePath { get; init; }

    /// <summary>The name or snippet the call looks for in TargetFilePath, for the not-found notes; null for tools with no mapped term.</summary>
    public string? MissingTerm { get; init; }

    /// <summary>Solution root at call start, for the not-found notes; null when no solution is loaded.</summary>
    public string? SolutionRoot { get; init; }

    /// <summary>True once the write gate has passed for this call.</summary>
    public bool GatePassed => Volatile.Read(ref _gatePassed) != 0;

    /// <summary>The paths the write gate left out of its candidate solution; null when none or the gate has not passed.</summary>
    public IReadOnlyCollection<FilePathWrapper>? GateRemovePaths { get; private set; }

    /// <summary>
    /// Marks the write gate as passed for this call. Visible to every continuation that holds this instance.
    /// <paramref name="removePaths"/> are the paths the gate left out of its candidate solution, so a re-validation can do the same.
    /// </summary>
    public void MarkGatePassed(IReadOnlyCollection<FilePathWrapper>? removePaths = null)
    {
        GateRemovePaths = removePaths;
        Volatile.Write(ref _gatePassed, 1);
    }

    /// <summary>When the chokepoint last wrote files in this call, taken inside the write lock; null until then. Names the operation blob, so blob order follows write order rather than the order the blobs were written.</summary>
    public DateTime? LastWriteUtc { get; private set; }

    /// <summary>Records the time of a chokepoint write for this call.</summary>
    public void MarkWritten(DateTime utc)
    {
        LastWriteUtc = utc;
    }

    /// <summary>
    /// The kind of target (for example "document" or "method") that a not-found error in this call named, recorded by
    /// <c>ToolErrorMapper.ToResultError</c> and read by the error-policy filter after the call returns. Side channel
    /// only: never serialized, never on the wire.
    /// </summary>
    public string? MissingTargetKind => Volatile.Read(ref _missingTargetKind);

    /// <summary>Records the missing-target kind for this call; null or whitespace is ignored, and the last write wins.</summary>
    public void RecordMissingTarget(string? kind)
    {
        if (string.IsNullOrWhiteSpace(kind))
            return;
        Volatile.Write(ref _missingTargetKind, kind);
    }

    /// <summary>
    /// A snapshot of the payloads detached during this call (see <see cref="Detach"/>), for code that cannot reach the result
    /// object. The marshaller reads it from <see cref="Current"/>; in mode <c>on</c> it handles them like
    /// <c>SentinelCallToolResult.Detached</c>, in <c>off</c> and <c>passthrough</c> they are dropped.
    /// </summary>
    public IReadOnlyList<(string Name, object Payload)> Detached
    {
        get
        {
            lock (_detachedLock)
            {
                return _detached.Count == 0 ? [] : _detached.ToArray();
            }
        }
    }

    /// <summary>
    /// Declares <paramref name="payload"/> (a list or a string) as stored, not inline, under <c>successData.&lt;name&gt;</c> of this
    /// call's result. Safe from concurrent continuations of the call. Throws <see cref="ArgumentException"/> for a bad name or payload
    /// or a name already detached in this call. Use <see cref="DetachCurrent"/> where the context may be absent.
    /// </summary>
    public void Detach(string name, object payload)
    {
        DetachedPayloads.Validate(name, payload);
        lock (_detachedLock)
        {
            if (_detached.Exists(d => d.Name == name))
            {
                throw new ArgumentException($"A payload named '{name}' is already detached in this call.", nameof(name));
            }

            _detached.Add((name, payload));
        }
    }

    /// <summary>
    /// <see cref="Detach"/> on <see cref="Current"/>; a no-op (no validation, no throw) when no filter set a context, as when an
    /// engine or test calls the code directly.
    /// </summary>
    public static void DetachCurrent(string name, object payload) => Current?.Detach(name, payload);
}
