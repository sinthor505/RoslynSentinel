namespace RoslynSentinel.Git;

public record GitLogResult : GitResult
{
    public List<GitCommitEntry> Commits { get; set; } = [];

    /// <summary>One "&lt;shortHash&gt; &lt;subject&gt;" line per commit, joined with "\n"; set only when log runs with nameOnly, and Commits is then empty.</summary>
    public string? NameOnlyLines
    {
        get; set;
    }
}
