namespace RoslynSentinel.Git;

public record GitBranchResult : GitResult
{
    public List<GitBranchEntry> Branches { get; set; } = [];
    public string? Created
    {
        get; set;
    }
    public string? Deleted
    {
        get; set;
    }
}
