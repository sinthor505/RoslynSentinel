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
    public ItemRecordOutcome Outcome { get; set; } = ItemRecordOutcome.Unset;
    public string? Reason
    {
        get; set;
    }
    /// <summary>Full source text before the operation -> enables undo_last_apply.</summary>
    public string? BeforeSource
    {
        get; set;
    }
    /// <summary>Full source text after the operation.</summary>
    public string? AfterSource
    {
        get; set;
    }
    /// <summary>Structured Roslyn diagnostics that caused a validation failure. Null when outcome is not a compiler error.</summary>
    public List<DiagnosticInfo>? CompilerDiagnostics { get; set; }

    /// <summary>Agent id of the call that applied this change, from ToolCallContext; null when the call carried no identity. Used by the undo guard.</summary>
    public string? AppliedByAgentId
    {
        get; set;
    }

    /// <summary>Session id of the call that applied this change; null when the call carried none. Used by the not-found notes to tell windows apart.</summary>
    public string? AppliedBySessionId
    {
        get; set;
    }

    /// <summary>Agent type of the call that applied this change; null when the call carried none.</summary>
    public string? AppliedByAgentType
    {
        get; set;
    }

    /// <summary>Stated reason of the call that applied this change; null when the call carried none.</summary>
    public string? AppliedByReason
    {
        get; set;
    }
}
