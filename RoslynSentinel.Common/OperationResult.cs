namespace RoslynSentinel.Common;

public class OperationResult
{
    public bool Success
    {
        get; set;
    }

    public object Data
    {
        get; set;
    }

    public OperationResult(bool success, object data)
    {
        Success = success;
        Data = data;
    }
}
