using System.Diagnostics.CodeAnalysis;

namespace RoslynSentinel.Common;

public sealed class EngineResultWrapper<T>
{
    public EngineOutcome Outcome
    {
        get;
    }
    public EngineError? Error
    {
        get;
    }
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
    public EngineError(string message, Exception? exception = null)
    {
        Message = message;
        Exception = exception;
    }
}
