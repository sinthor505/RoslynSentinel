namespace RoslynSentinel.Common;

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
