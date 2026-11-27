namespace RoslynSentinel.Git;

/// <summary>Result of operation=worktree. Which fields are set depends on the action: list sets Worktrees;
/// add sets Added, Branch and Note; remove sets Removed, Branch, Note and, when uncommitted changes were
/// discarded, DiscardedPaths.</summary>
public record GitWorktreeResult : GitResult
{
    public string Action { get; set; } = "";
    public List<GitWorktreeEntry> Worktrees { get; set; } = [];
    public string? Added
    {
        get; set;
    }
    public string? Removed
    {
        get; set;
    }
    public string? Branch
    {
        get; set;
    }
    public List<string>? DiscardedPaths
    {
        get; set;
    }
    public string? Note
    {
        get; set;
    }
}
