namespace RoslynSentinel.Git;

public record GitStatusResult : GitResult
{
    public string Branch { get; set; } = "";
    public bool IsClean
    {
        get; set;
    }
    public List<GitStatusEntry> Staged { get; set; } = [];
    public List<GitStatusEntry> Unstaged { get; set; } = [];
    public List<string> Untracked { get; set; } = [];
    // Set when the total entry count exceeds the caller's maxEntries (default 50); the lists above are
    // then capped to a sample of the first 10 entries each and the Total*/*ByStatus counts carry the
    // full picture. Re-call status with a larger maxEntries (max 5000) to get every entry.
    // TODO Drop this payload flag once the envelope warningDetails (GitHintText.TruncationWarning) is the only truncation signal; see TODO.md.
    public bool IsTruncated
    {
        get; set;
    }
    public int? TotalStagedCount
    {
        get; set;
    }
    public int? TotalUnstagedCount
    {
        get; set;
    }
    public int? TotalUntrackedCount
    {
        get; set;
    }
    public Dictionary<string, int>? StagedByStatus
    {
        get; set;
    }
    public Dictionary<string, int>? UnstagedByStatus
    {
        get; set;
    }

    /// <summary>Which multi-step operation the repository is in the middle of: "merge", "rebase",
    /// "revert", "cherry-pick" or "am". Null when none is in progress. Set by status; when set,
    /// GitTool(operation: abort) backs out of it.</summary>
    public string? InProgress
    {
        get; set;
    }

    /// <summary>Set only by operation=abort: which in-progress operation was aborted.</summary>
    public string? AbortedOperation
    {
        get; set;
    }
}
