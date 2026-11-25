namespace RoslynSentinel.Tools.Basic;

public record GitShowResult : GitResult
{
    public string Hash { get; set; } = "";
    public string Author { get; set; } = "";
    public string Date { get; set; } = "";
    public string Message { get; set; } = "";
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
