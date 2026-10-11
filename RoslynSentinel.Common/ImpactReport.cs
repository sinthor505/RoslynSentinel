namespace RoslynSentinel.Common;

public record ImpactReport(
    string SymbolName,
    string SymbolKind,
    List<ReferenceInfo> References,
    int TotalCallSites,
    int AffectedProjectsCount,
    string? Error = null
) : IResultStatus
{
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsError => Error is not null;

    [System.Text.Json.Serialization.JsonIgnore]
    public ResultStatus Status => ResultStatusExtensions.FromFlag(IsError, Code);

    [System.Text.Json.Serialization.JsonIgnore]
    public string? Code => IsError ? ToolErrorCode.Exception : null;

    [System.Text.Json.Serialization.JsonIgnore]
    public string? Message => Error;
}
