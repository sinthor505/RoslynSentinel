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

// ── Phase 4 — Batch-first input types ─────────────────────────────────────────

/// <summary>A single bridged-method target for <c>run_uplift</c>.</summary>
public class UpliftTarget
{
    /// <summary>Name of the Asyncify-bridge sync method whose callers should be uplifted.</summary>
    public string  BridgedMethodName { get; set; } = "";
    /// <summary>Restrict caller scan to one project. null = entire solution.</summary>
    public string? ProjectName       { get; set; }
}

/// <summary>Canonical input for <c>run_uplift</c>.</summary>
public class RunUpliftInput
{
    public List<UpliftTarget> Targets                    { get; set; } = new();
    public bool               DryRun                     { get; set; } = false;
    public int                MaxCallersPerMethod         { get; set; } = 10;
    public bool               PropagateCancellationTokens { get; set; } = true;
}

/// <summary>One target in a <c>flag_migration_candidates</c> call (scope="targets").</summary>
public class FlagCandidateTarget
{
    public string  FilePath   { get; set; } = "";
    public string  MethodName { get; set; } = "";
    public string  Pattern    { get; set; } = "AsyncBridgeCandidate";
    public int     Score      { get; set; } = 0;
    public string? Reason     { get; set; }
}

/// <summary>Canonical input for <c>flag_migration_candidates</c>.</summary>
public class FlagCandidatesInput
{
    /// <summary>"targets" — process explicit list; "project" — autonomous project-level scan.</summary>
    public string                    Scope       { get; set; } = "targets";
    /// <summary>Required when Scope="targets".</summary>
    public List<FlagCandidateTarget>? Targets     { get; set; }
    /// <summary>Required when Scope="project". null = entire solution.</summary>
    public string?                   ProjectName { get; set; }
    /// <summary>Migration pattern. Default "AsyncBridgeCandidate".</summary>
    public string                    Pattern     { get; set; } = "AsyncBridgeCandidate";
    /// <summary>Minimum score to flag a method (Scope="project" only). Default 50.</summary>
    public int                       MinScore    { get; set; } = 50;
    /// <summary>When true, reports what would be flagged without writing files.</summary>
    public bool                      DryRun      { get; set; } = false;
    /// <summary>When true, re-evaluates every method even if already flagged (Scope="project" only).</summary>
    public bool                      ForceRescan { get; set; } = false;
}

// ── Phase 6 — asyncify macro input ────────────────────────────────────────────

/// <summary>Canonical input for the <c>asyncify</c> macro-workflow tool.</summary>
public class AsyncifyInput
{
    /// <summary>
    /// Project to asyncify. null = entire solution.
    /// Used for autonomous candidate discovery when no explicit MethodTargets are supplied.
    /// </summary>
    public string? ProjectName                   { get; set; }

    /// <summary>
    /// Optional explicit list of methods to asyncify. When set, skips the autonomous
    /// flag-scan phase and starts directly at the bridge-conversion phase.
    /// </summary>
    public List<FlagCandidateTarget>? MethodTargets { get; set; }

    /// <summary>Method names to skip in every phase.</summary>
    public List<string>? Exclusions              { get; set; }

    /// <summary>When true, reports what would change without writing any files.</summary>
    public bool          DryRun                  { get; set; } = false;

    /// <summary>
    /// When true (default), propagates CancellationToken to inner async callees in
    /// both the bridge and uplift phases, and runs a final CT-propagation sweep.
    /// </summary>
    public bool          PropagateCancellationTokens { get; set; } = true;

    /// <summary>Max methods to convert in the bridge phase per run. Default 50.</summary>
    public int           MaxMethods              { get; set; } = 50;

    /// <summary>Max callers to uplift per bridged method. Default 10.</summary>
    public int           MaxCallersPerMethod     { get; set; } = 10;

    /// <summary>Minimum score to flag a method in the discovery phase. Default 50.</summary>
    public int           MinScore                { get; set; } = 50;

    /// <summary>Score threshold for bridge conversion (score ≤ threshold eligible). Default 60.</summary>
    public int           ScoreThreshold          { get; set; } = 60;
}

// ── Phase 7 — async_migrate combined input ────────────────────────────────────

/// <summary>
/// Combined input for <c>async_migrate</c>. Only populate the fields relevant to
/// the chosen <c>operation</c>; all others are ignored.
/// </summary>
public class AsyncMigrateInput
{
    // ── Shared ────────────────────────────────────────────────────────────────

    /// <summary>When true, reports what would change without writing any files.</summary>
    public bool DryRun { get; set; } = false;

    // ── BatchTargetInput ops: propagate_cancellation_token, convert_to_async_bridge, add_cancellation_token ─

    /// <summary>
    /// File/method targets. Each entry has FilePath and optional MethodNames array.
    /// Used by: propagate_cancellation_token, convert_to_async_bridge, add_cancellation_token.
    /// </summary>
    public List<BatchTarget>? Targets { get; set; }

    /// <summary>Max files/items to process (default 100). propagate_cancellation_token and add_cancellation_token.</summary>
    public int MaxItems { get; set; } = 100;

    /// <summary>Propagate CancellationToken in new async overloads (default true). convert_to_async_bridge and run_uplift.</summary>
    public bool PropagateCancellationTokens { get; set; } = true;

    // ── run_uplift ─────────────────────────────────────────────────────────────

    /// <summary>Bridge method targets to uplift. Each has BridgedMethodName and optional ProjectName. run_uplift only.</summary>
    public List<UpliftTarget>? UpliftTargets { get; set; }

    /// <summary>Max callers per bridged method (default 10). run_uplift and asyncify.</summary>
    public int MaxCallersPerMethod { get; set; } = 10;

    // ── flag_migration_candidates ──────────────────────────────────────────────

    /// <summary>"targets" (default) or "project". flag_migration_candidates only.</summary>
    public string FlagScope { get; set; } = "targets";

    /// <summary>Explicit methods to flag (FlagScope="targets"). flag_migration_candidates only.</summary>
    public List<FlagCandidateTarget>? FlagTargets { get; set; }

    /// <summary>Restrict scan to one project; null = entire solution. flag_migration_candidates (scope=project) and asyncify.</summary>
    public string? ProjectName { get; set; }

    /// <summary>Migration pattern (default "AsyncBridgeCandidate"). flag_migration_candidates and asyncify.</summary>
    public string Pattern { get; set; } = "AsyncBridgeCandidate";

    /// <summary>Minimum score to flag (default 50). flag_migration_candidates and asyncify.</summary>
    public int MinScore { get; set; } = 50;

    /// <summary>Re-evaluate already-flagged methods (flag_migration_candidates scope=project only).</summary>
    public bool ForceRescan { get; set; } = false;

    // ── asyncify ───────────────────────────────────────────────────────────────

    /// <summary>Explicit (FilePath, MethodName) targets; skips the flag-discovery phase. asyncify only.</summary>
    public List<FlagCandidateTarget>? MethodTargets { get; set; }

    /// <summary>Method names to skip in all asyncify phases. asyncify only.</summary>
    public List<string>? Exclusions { get; set; }

    /// <summary>Max methods to convert in the bridge phase (default 50). asyncify only.</summary>
    public int MaxMethods { get; set; } = 50;

    /// <summary>Max score eligible for bridge conversion (default 60). asyncify only.</summary>
    public int ScoreThreshold { get; set; } = 60;
}
