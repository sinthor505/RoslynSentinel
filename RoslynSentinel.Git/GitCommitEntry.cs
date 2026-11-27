namespace RoslynSentinel.Git;

public record GitCommitEntry
{
    public string Hash { get; set; } = "";
    public string ShortHash { get; set; } = "";
    public string Author { get; set; } = "";
    public string Date { get; set; } = "";
    public string Message { get; set; } = "";
}
