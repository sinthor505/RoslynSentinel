namespace RoslynSentinel.Common;

/// <summary>
/// Identity and start state of the current tool call. Set once per call by the outermost request filter, read at
/// the write chokepoint. It is ambient rather than passed as a parameter because there are roughly 50 chokepoint
/// call sites. The reference flows into awaits and <c>Task.Run</c> children, so a mutation such as
/// <see cref="MarkGatePassed"/> made deep in a call is visible to later code in that call without a slot
/// assignment having to flow back up. <see cref="Current"/> is null when no filter set it (engines, tests, other
/// hosts).
/// </summary>
public sealed class ToolCallContext
{
    private static readonly AsyncLocal<ToolCallContext?> Slot = new();

    private int _gatePassed;

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

    /// <summary>Marks the write gate as passed for this call. Visible to every continuation that holds this instance.</summary>
    public void MarkGatePassed() => Volatile.Write(ref _gatePassed, 1);
}
