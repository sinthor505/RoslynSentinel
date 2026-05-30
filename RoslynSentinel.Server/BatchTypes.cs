namespace RoslynSentinel.Server;

/// <summary>Canonical batch input — used by all batch-first mutation tools.</summary>
public class BatchTargetInput
{
    public List<BatchTarget> Targets  { get; set; } = new();
    public bool              DryRun   { get; set; } = false;
    public int               MaxItems { get; set; } = 100;
}

/// <summary>One unit of batch work: a file path plus an optional method-name filter (null = whole file).</summary>
public class BatchTarget
{
    public string    FilePath    { get; set; } = "";
    public string[]? MethodNames { get; set; }
}

/// <summary>Agent-facing return from every batch-first mutation tool.</summary>
public class BatchResultSummary
{
    public string              ChangeId    { get; set; } = "";
    public string              BlobName    { get; set; } = "";
    public int                 Succeeded   { get; set; }
    public int                 Skipped     { get; set; }
    public int                 RolledBack  { get; set; }
    public int                 Failed      { get; set; }
    public int                 Attempted   { get; set; }
    /// <summary>Inline failures, capped at 15. Enough to course-correct; not enough to flood.</summary>
    public List<FailureDetail> Failures    { get; set; } = new();
    /// <summary>"ok" | "caution" | "halt" — keyed field, never infer from prose.</summary>
    public string              Severity    { get; set; } = "ok";
    public string              Directive   { get; set; } = "";
    public bool                BreakerOpen { get; set; }
}

/// <summary>Per-failure detail included inline in BatchResultSummary (capped at 15).</summary>
public class FailureDetail
{
    public string  FilePath   { get; set; } = "";
    public string? MethodName { get; set; }
    public string  Reason     { get; set; } = "";
    /// <summary>"failed" | "rolledback" | "skipped"</summary>
    public string  Outcome    { get; set; } = "";
}

/// <summary>Per-item forensic record written to the operation blob on disk.</summary>
public class OperationItemRecord
{
    public string  FilePath     { get; set; } = "";
    public string? MethodName   { get; set; }
    /// <summary>"succeeded" | "skipped" | "failed" | "rolledback"</summary>
    public string  Outcome      { get; set; } = "";
    public string? Reason       { get; set; }
    /// <summary>Full source text before the operation — enables undo_last_apply.</summary>
    public string? BeforeSource { get; set; }
    /// <summary>Full source text after the operation.</summary>
    public string? AfterSource  { get; set; }
}

/// <summary>Return type for get_operation_detail — a filtered slice of an operation blob.</summary>
public class OperationDetailResult
{
    public string                    ChangeId      { get; set; } = "";
    public string                    BlobName      { get; set; } = "";
    public int                       TotalItems    { get; set; }
    public int                       ReturnedItems { get; set; }
    public string?                   Filter        { get; set; }
    public List<OperationItemRecord> Items         { get; set; } = new();
}
