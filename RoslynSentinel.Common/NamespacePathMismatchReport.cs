namespace RoslynSentinel.Common;

public class NamespacePathMismatchReport
{
    public List<NamespacePathMismatch> Errors { get; set; } = [];
    public List<NamespacePathMismatch> Warnings { get; set; } = [];
    public int TotalFiles
    {
        get; set;
    }
    public int MismatchCount
    {
        get; set;
    }
    public bool IsClean
    {
        get; set;
    }
    public string Summary { get; set; } = "";
}
