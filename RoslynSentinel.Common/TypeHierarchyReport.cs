namespace RoslynSentinel.Common;

public record TypeHierarchyReport : IResultStatus
{
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsError => Error is not null;

    [System.Text.Json.Serialization.JsonIgnore]
    public ResultStatus Status => ResultStatusExtensions.FromFlag(IsError, Code);

    [System.Text.Json.Serialization.JsonIgnore]
    public string? Code => IsError ? ToolErrorCode.Exception : null;

    [System.Text.Json.Serialization.JsonIgnore]
    public string? Message => Error;

    public string? Error
    {
        get; init;
    }
    public string TypeName { get; init; } = "";
    public string? BaseClass
    {
        get; init;
    }
    public List<string> BaseClassChain { get; init; } = new();
    public List<string> ImplementedInterfaces { get; init; } = new();
    public List<TypeHierarchyEntry> DerivedTypes { get; init; } = new();
    public List<TypeHierarchyEntry> ImplementingTypes { get; init; } = new();
    public bool IsInterface
    {
        get; init;
    }
    public bool IsAbstract
    {
        get; init;
    }
    public bool IsSealed
    {
        get; init;
    }
}
