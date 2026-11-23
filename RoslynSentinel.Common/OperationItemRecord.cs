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
    /// <summary>Full source text before the operation -> enables UndoChange.</summary>
    public string? BeforeSource
    {
        get; set;
    }
    /// <summary>Full source text after the operation.</summary>
    public string? AfterSource
    {
        get; set;
    }
    /// <summary>When set, AfterSource is not stored: the after-text equals the BeforeSource of the item in the same blob whose FilePath is this value (the text is stored once). Null on every item of an older blob. Readers call ResolveAfterSources after deserializing.</summary>
    public string? AfterSourceFromFilePath
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

    /// <summary>True when the chokepoint created this file (it did not exist before the write). Not inferred from a null BeforeSource: a failed pre-image read also leaves BeforeSource null. Read by UndoChange to delete the file.</summary>
    public bool Created
    {
        get; set;
    }

    /// <summary>Fills AfterSource from the referenced item's BeforeSource for every item that carries AfterSourceFromFilePath and has no AfterSource; leaves every other item untouched. A reference that matches no item (FilePath compared OrdinalIgnoreCase) or whose item has a null BeforeSource leaves AfterSource null.</summary>
    public static void ResolveAfterSources(IReadOnlyList<OperationItemRecord> items)
    {
        foreach (var item in items)
        {
            if (item.AfterSourceFromFilePath is null || item.AfterSource is not null)
            {
                continue;
            }
            OperationItemRecord? referenced = null;
            foreach (var candidate in items)
            {
                if (string.Equals(candidate.FilePath, item.AfterSourceFromFilePath, StringComparison.OrdinalIgnoreCase))
                {
                    referenced = candidate;
                    break;
                }
            }
            if (referenced is not null)
            {
                item.AfterSource = referenced.BeforeSource;
            }
        }
    }
}
