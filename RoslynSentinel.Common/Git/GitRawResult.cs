namespace RoslynSentinel.Tools.Basic;

public record GitRawResult
{
    public int ExitCode
    {
        get; set;
    }
    public string Stdout { get; set; } = "";
    public string Stderr { get; set; } = "";
}
