namespace RoslynSentinel.Common;

/// <summary>
/// Canonical outcome vocabulary shared by every status-carrying result type. Which members are errors is
/// decided in exactly one place, <see cref="ResultStatusExtensions.IsErrorStatus"/>.
/// See docs/current/proposals/proposal_shared_result_status_interface.md.
/// </summary>
public enum ResultStatus
{
    /// <summary>The operation completed and did what was asked.</summary>
    Success,

    /// <summary>Nothing to do because the target was already in the requested state. Not an error.</summary>
    AlreadyInState,

    /// <summary>The target (symbol, file, member, line) was not located.</summary>
    NotFound,

    /// <summary>More than one target matched where exactly one was required.</summary>
    Ambiguous,

    /// <summary>A parameter or input was invalid.</summary>
    InvalidInput,

    /// <summary>The target exists but the operation cannot be applied to it.</summary>
    NotApplicable,

    /// <summary>The operation is disabled in this server configuration.</summary>
    FeatureDisabled,

    /// <summary>The operation was cancelled before it finished.</summary>
    Cancelled,

    /// <summary>The operation exceeded its time limit.</summary>
    TimedOut,

    /// <summary>The operation failed for a reason none of the other members describes.</summary>
    Failed
}

/// <summary>
/// The status every status-carrying result exposes, so a result can be inspected, branched on and passed upward
/// without re-parsing it or translating between per-layer vocabularies. <see cref="IsError"/> is the same flag
/// as the MCP protocol's <c>CallToolResult.isError</c>; there is deliberately no <c>IsSuccess</c>.
/// </summary>
/// <remarks>
/// A type that has its own outcome or status member should compute <see cref="IsError"/> from
/// <see cref="Status"/> via <see cref="ResultStatusExtensions.IsErrorStatus"/> so the two cannot disagree;
/// wrappers and types with no status of their own store it.
/// </remarks>
public interface IResultStatus
{
    /// <summary>True when the operation did not succeed. Matches the MCP <c>isError</c> flag.</summary>
    bool IsError
    {
        get;
    }

    /// <summary>The canonical outcome. Consistent with <see cref="IsError"/>.</summary>
    ResultStatus Status
    {
        get;
    }

    /// <summary>A <see cref="ToolErrorCode"/> value naming the failure cause; null when there is none.</summary>
    string? Code
    {
        get;
    }

    /// <summary>Human-readable explanation of the failure; null when there is none.</summary>
    string? Message
    {
        get;
    }
}

/// <summary>Single source of truth for which <see cref="ResultStatus"/> values are errors and how a tool error code maps to one.</summary>
public static class ResultStatusExtensions
{
    /// <summary>True for every status except <see cref="ResultStatus.Success"/> and <see cref="ResultStatus.AlreadyInState"/>.</summary>
    public static bool IsErrorStatus(this ResultStatus status) =>
        status is not (ResultStatus.Success or ResultStatus.AlreadyInState);

    /// <summary>
    /// Maps a <see cref="ToolErrorCode"/> string to its canonical status. A null or empty code means no error was
    /// recorded, so <see cref="ResultStatus.Success"/>; an unrecognized code is <see cref="ResultStatus.Failed"/>.
    /// </summary>
    public static ResultStatus FromErrorCode(string? errorCode) => errorCode switch
    {
        null or "" => ResultStatus.Success,
        ToolErrorCode.NotFound or ToolErrorCode.NoMatches => ResultStatus.NotFound,
        ToolErrorCode.Ambiguous => ResultStatus.Ambiguous,
        ToolErrorCode.InvalidArgument => ResultStatus.InvalidInput,
        ToolErrorCode.TargetIneligible => ResultStatus.NotApplicable,
        ToolErrorCode.FeatureDisabled => ResultStatus.FeatureDisabled,
        _ => ResultStatus.Failed
    };
}
