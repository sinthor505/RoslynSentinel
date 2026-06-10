namespace RoslynSentinel.Common;

public record VariableLifetimeReport
{
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
    public List<VariableAccess> Accesses { get; init; } = new();
}
