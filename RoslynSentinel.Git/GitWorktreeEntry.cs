namespace RoslynSentinel.Git;

/// <summary>One worktree in a worktree list (parsed from git worktree list --porcelain).</summary>
public record GitWorktreeEntry
{
    /// <summary>Absolute path of the worktree, normalized to the platform separator.</summary>
    public string Path { get; set; } = "";

    /// <summary>Abbreviated HEAD commit hash, when git reports one.</summary>
    public string? Head
    {
        get; set;
    }

    /// <summary>Short name of the checked-out branch; null for a detached HEAD.</summary>
    public string? Branch
    {
        get; set;
    }

    /// <summary>True for the main worktree (the first entry git lists).</summary>
    public bool IsMain
    {
        get; set;
    }

    /// <summary>True when this is the worktree the GitTool tool is operating in (the loaded solution's repository).</summary>
    public bool IsCurrent
    {
        get; set;
    }
    public bool IsDetached
    {
        get; set;
    }
    public bool IsLocked
    {
        get; set;
    }

    /// <summary>True when git reports the worktree directory as missing or stale (git worktree prune would remove it).</summary>
    public bool IsPrunable
    {
        get; set;
    }
}
