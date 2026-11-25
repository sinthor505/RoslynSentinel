namespace RoslynSentinel.Tools.Basic;

/// <summary>Result of operation=hunks: the unstaged hunks of one file. Fingerprint covers the whole file diff,
/// not only the listed hunks, and must be passed back when staging.</summary>
public record GitHunksResult : GitResult
{
    public string Path { get; set; } = "";
    public string Fingerprint { get; set; } = "";
    public int TotalHunks
    {
        get; set;
    }
    public bool IsTruncated
    {
        get; set;
    }
    public List<GitHunkEntry> Hunks { get; set; } = [];
    public string? Note
    {
        get; set;
    }
}
