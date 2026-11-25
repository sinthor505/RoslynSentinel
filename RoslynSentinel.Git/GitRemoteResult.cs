namespace RoslynSentinel.Tools.Basic;

public record GitRemoteResult : GitResult
{
    public string Operation { get; set; } = "";
    public string Detail { get; set; } = "";
}
