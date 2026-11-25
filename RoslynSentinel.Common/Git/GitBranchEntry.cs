namespace RoslynSentinel.Tools.Basic;

public record GitBranchEntry
{
    public string Name { get; set; } = "";
    public bool IsCurrent
    {
        get; set;
    }
    public bool IsRemote
    {
        get; set;
    }
}
