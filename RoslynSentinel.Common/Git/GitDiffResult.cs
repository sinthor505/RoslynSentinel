namespace RoslynSentinel.Tools.Basic;

public record GitDiffResult : GitResult
{
    public string Diff { get; set; } = "";
    public int FilesChanged
    {
        get; set;
    }

    public string? Warning
    {
        get; set;
    }
}
