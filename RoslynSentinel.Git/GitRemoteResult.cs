namespace RoslynSentinel.Git;

public record GitRemoteResult : GitResult
{
    public string Operation { get; set; } = "";
    public string Detail { get; set; } = "";
}
