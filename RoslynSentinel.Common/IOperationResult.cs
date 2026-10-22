namespace RoslynSentinel.Common;

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
public interface IOperationResult
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
