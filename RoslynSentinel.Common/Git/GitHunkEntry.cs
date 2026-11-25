namespace RoslynSentinel.Tools.Basic;

/// <summary>One zero-context hunk of a file's unstaged diff, as listed by operation=hunks.</summary>
public record GitHunkEntry
{
    /// <summary>1-based hunk id; pass it in hunkIds when staging.</summary>
    public int Index
    {
        get; set;
    }
    public int OldStart
    {
        get; set;
    }
    public int OldCount
    {
        get; set;
    }
    public int NewStart
    {
        get; set;
    }
    public int NewCount
    {
        get; set;
    }

    /// <summary>Text after the second @@ in the hunk header (git's function-context hint); may be empty.</summary>
    public string Header { get; set; } = "";
    public int Added
    {
        get; set;
    }
    public int Removed
    {
        get; set;
    }

    /// <summary>The first few changed lines (+/-), decoded as UTF-8 and truncated.</summary>
    public string Preview { get; set; } = "";
}
