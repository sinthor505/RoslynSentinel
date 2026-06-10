namespace RoslynSentinel.Common;

public class NamespacePathMismatch
{
    public string FilePath { get; set; } = "";
    public string ProjectName { get; set; } = "";
    public string DeclaredNamespace { get; set; } = "";
    public string ExpectedNamespace { get; set; } = "";
    public string Severity { get; set; } = "";
    public string Reason { get; set; } = "";
    public List<string> ConflictingFiles { get; set; } = [];
}
