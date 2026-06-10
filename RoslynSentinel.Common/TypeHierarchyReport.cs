namespace RoslynSentinel.Common;

public record TypeHierarchyReport
{
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
