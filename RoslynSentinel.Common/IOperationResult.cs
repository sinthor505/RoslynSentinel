namespace RoslynSentinel.Common;

/// <summary>
/// The outcome every operation result exposes, so a result can be inspected, branched on and passed upward
/// without re-parsing it or translating between per-layer vocabularies. <see cref="IsError"/> is derived from
/// <see cref="Status"/> and is the same flag as the MCP protocol's <c>CallToolResult.isError</c>; there is
/// deliberately no <c>IsSuccess</c>.
/// </summary>
/// <remarks>
/// Error detail (code, message, structured detail) is not on this interface: it lives on the exception that was
/// thrown (<see cref="IOperationError"/>) and on the wire <see cref="ResultError"/>. After an engine call, a failure
/// means an exception was thrown; a returned result describes an operation that ran. A wrapper or a type with no
/// status of its own may store its flag by implementing <see cref="IsError"/> itself, which overrides the default.
/// </remarks>
public interface IOperationResult
{
    /// <summary>The canonical outcome.</summary>
    ResultStatus Status
    {
        get;
    }

    /// <summary>True when the operation did not succeed. Derived from <see cref="Status"/>; matches the MCP <c>isError</c> flag.</summary>
    bool IsError => Status.IsErrorStatus();
}
