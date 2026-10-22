namespace RoslynSentinel.Common;

/// <summary>
/// The one shared status carrier for every result at every layer (engine, tool and wire). <see cref="IsError"/> is
/// derived from <see cref="Status"/> so the two cannot disagree, and it maps to the MCP <c>isError</c> flag. After an
/// engine call, failure means an exception was thrown, so a single-target engine result is always
/// <see cref="ResultStatus.Success"/> or <see cref="ResultStatus.AlreadyInState"/>.
/// </summary>
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

/// <summary>
/// The exception-side counterpart of <see cref="IOperationResult"/>: a failure carried by an exception. Implemented by
/// ToolException and by the wire ResultError.
/// </summary>
public interface IOperationError
{
    /// <summary>The stable error code naming the failure cause.</summary>
    string ErrorCode
    {
        get;
    }

    /// <summary>Human-readable explanation of the failure.</summary>
    string Message
    {
        get;
    }

    /// <summary>The cause category, as a <see cref="ResultStatus"/>.</summary>
    ResultStatus Category
    {
        get;
    }

    /// <summary>Wire-safe payload for the tool layer; null when there is none.</summary>
    System.Collections.Generic.IReadOnlyList<object>? StructuredDetail
    {
        get;
    }
}
