using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace RoslynSentinel.Common;

public enum EngineErrorCode
{
    General,
    StaleSession,
    SymbolNotResolved,
    BuildNotRun,
    /// <summary>A method docCommentId without its parameter list names several overloads; the message lists their full ids. Callers map it to an invalid-argument error (the caller must pick one), not NotFound.</summary>
    MissingParameterListOverloads,
}

public class EngineError
{
    public string Message
    {
        get;
    }
    public Exception? Exception
    {
        get;
    }
    public EngineErrorCode? Code
    {
        get;
    }
    public DataTag? Tag
    {
        get;
    }

    public EngineError(string message, Exception? exception = null)
    {
        Message = message;
        Exception = exception;
    }

    public EngineError(EngineErrorCode code, string message, DataTag? tag = null)
    {
        Code = code;
        Message = message;
        Tag = tag;
    }

    public string ToToolResponse()
    {
        return JsonSerializer.Serialize(new
        {
            isError = true,
            error = Message,
            errorCode = Code?.ToString() ?? EngineErrorCode.General.ToString()
        });
    }
}
