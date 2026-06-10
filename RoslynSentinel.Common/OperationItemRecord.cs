namespace RoslynSentinel.Common;

/// <summary>Per-item forensic record written to the operation blob on disk.</summary>
public class OperationItemRecord
{
    public string FilePath { get; set; } = "";
    public string? MethodName
    {
        get; set;
    }
    /// <summary>"succeeded" | "skipped" | "failed" | "rolledback"</summary>
    public OperationOutcome Outcome { get; set; } = OperationOutcome.Unset;
    public string? Reason
    {
        get; set;
    }
    /// <summary>Full source text before the operation — enables undo_last_apply.</summary>
    public string? BeforeSource
    {
        get; set;
    }
    /// <summary>Full source text after the operation.</summary>
    public string? AfterSource
    {
        get; set;
    }
}
