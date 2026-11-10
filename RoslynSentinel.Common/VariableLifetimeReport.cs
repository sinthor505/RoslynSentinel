namespace RoslynSentinel.Common;

public record VariableLifetimeReport : IOperationResult
{
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsError => Error is not null;

    [System.Text.Json.Serialization.JsonIgnore]
    public ResultStatus Status => ResultStatusExtensions.FromFlag(IsError, Code);

    [System.Text.Json.Serialization.JsonIgnore]
    public string? Code => IsError ? ErrorCode ?? ToolErrorCode.Exception : null;

    // Cause of Error when it is not an internal fault; null means Exception.
    [System.Text.Json.Serialization.JsonIgnore]
    public string? ErrorCode { get; init; }

    [System.Text.Json.Serialization.JsonIgnore]
    public string? Message => Error;

    public string? Error
    {
        get; init;
    }
    public string VariableName { get; init; } = "";
    public string TypeName { get; init; } = "";
    public string DeclarationFile { get; init; } = "";
    public int DeclarationLine
    {
        get; init;
    }
    public string ScopeDescription { get; init; } = "";
    public bool IsDefinitelyAssigned
    {
        get; init;
    }
    public bool IsAlwaysAssigned
    {
        get; init;
    }
    public bool IsCapturedInClosure
    {
        get; init;
    }
    // Set when the variable name matches 2+ declarations near lineNumber: the first was traced; the text names the others and the retry.
    public string? Warning { get; init; }
    public List<VariableAccess> Accesses { get; init; } = new();
}
