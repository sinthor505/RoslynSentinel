namespace RoslynSentinel.Tools.Basic;

/// <summary>One entry in a stash list.</summary>
public record GitStashEntry
{
    /// <summary>The N of stash@{N}; 0 is the newest entry.</summary>
    public int Index
    {
        get; set;
    }

    /// <summary>Abbreviated hash of the stash commit.</summary>
    public string Hash { get; set; } = "";

    /// <summary>Branch the stash was made on, when the entry's subject names one.</summary>
    public string? Branch
    {
        get; set;
    }

    /// <summary>The label given at push time, or git's default "&lt;hash&gt; &lt;subject&gt;" of the commit stashed on.</summary>
    public string Message { get; set; } = "";

    /// <summary>ISO 8601 commit date of the stash.</summary>
    public string Date { get; set; } = "";
}
