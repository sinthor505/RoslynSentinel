using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace RoslynSentinel.Common;

public enum EngineErrorCode
{
    General,
    StaleSession,
    SymbolNotResolved,
    BuildNotRun,
}

public sealed class EngineResultWrapper<T> : IResultStatus
{
    public EngineOutcome Outcome
    {
        get;
    }
    public EngineError? Error
    {
        get;
    }

    public IReadOnlyList<Finding> Findings { get; init; } = [];

    private readonly T? _data;

    public bool TryGetData([NotNullWhen(true)] out T? data)
    {
        data = this._data;
        return this.Outcome == EngineOutcome.Success && this._data is not null;
    }

    public T Data
    {
        get
        {
            if (this.Outcome != EngineOutcome.Success)
            {
                throw new InvalidOperationException($"No data on {this.Outcome} result.");
            }
            return this._data!;
        }
    }

    public EngineResultWrapper(EngineOutcome outcome, T? data = default, EngineError? error = null)
    {
        Outcome = outcome;
        _data = data;
        Error = error;
    }
    public static EngineResultWrapper<T> Failure(EngineOutcome outcome, EngineError error) => new(outcome, default, error);

    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsError => Outcome != EngineOutcome.Success;

    [System.Text.Json.Serialization.JsonIgnore]
    public ResultStatus Status => Outcome switch
    {
        EngineOutcome.Success => ResultStatus.Success,
        EngineOutcome.DocumentNotFound or EngineOutcome.TargetNotFound => ResultStatus.NotFound,
        EngineOutcome.InvalidInput => ResultStatus.InvalidInput,
        EngineOutcome.Timeout => ResultStatus.TimedOut,
        _ => ResultStatus.Failed
    };

    [System.Text.Json.Serialization.JsonIgnore]
    public string? Code => Status switch
    {
        ResultStatus.Success => null,
        ResultStatus.NotFound => ToolErrorCode.NotFound,
        ResultStatus.InvalidInput => ToolErrorCode.InvalidArgument,
        ResultStatus.TimedOut => ToolErrorCode.TimedOut,
        _ => ToolErrorCode.Exception
    };

    [System.Text.Json.Serialization.JsonIgnore]
    public string? Message => Error?.Message;
}

public enum EngineOutcome
{
    Success,
    Failure,
    DocumentNotFound,
    TargetNotFound,
    InvalidInput,
    Timeout,
    InternalError
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
