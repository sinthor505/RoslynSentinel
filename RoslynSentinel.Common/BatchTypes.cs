namespace RoslynSentinel.Common;

/// <summary>Canonical batch input — used by all batch-first mutation tools.</summary>
public class BatchTargetInput
{
    public List<BatchTarget> Targets { get; set; } = new();
    public bool DryRun { get; set; } = false;
    public int MaxItems { get; set; } = 100;
}

/// <summary>One unit of batch work: a file path plus an optional method-name filter (null = whole file).</summary>
public class BatchTarget
{
    private string _filePath = "";
    public string FilePath
    {
        get => _filePath;
        set => _filePath = RoslynSentinel.Common.FilePath.NormalizeWirePath(value ?? "");
    }
    public string[]? MethodNames
    {
        get; set;
    }
}

/// <summary>Operation counts for a single Asyncify phase.</summary>
public record AsyncifyPhaseCount
{
    public int Succeeded { get; init; }
    public int Failed { get; init; }
    public int Skipped { get; init; }
}

/// <summary>
/// Per-phase breakdown of an Asyncify run. All phases are present; counts are zero when
/// a phase did not run (e.g. Phase 0 only runs when HandlerExtractCandidate flags exist).
/// Use this to distinguish bridge conversions from uplift call-site updates in the totals.
/// </summary>
public record AsyncifyPhaseBreakdown
{
    /// <summary>Phase 0 — HandlerExtract: event-handler bodies extracted into new methods.</summary>
    public AsyncifyPhaseCount HandlerExtract { get; init; } = new();
    /// <summary>Phase 1 — Flag: methods newly annotated [MigrationCandidate("AsyncBridgeCandidate")].</summary>
    public AsyncifyPhaseCount Flag { get; init; } = new();
    /// <summary>Phase 2 — Bridge: sync methods converted to bridge pattern (sync stub + async overload).</summary>
    public AsyncifyPhaseCount Bridge { get; init; } = new();
    /// <summary>Phase 3 — Uplift: caller methods updated to use async overloads directly.</summary>
    public AsyncifyPhaseCount Uplift { get; init; } = new();
    /// <summary>Phase 3a — HandlerToAsync: extracted event-handler bodies bridged.</summary>
    public AsyncifyPhaseCount HandlerToAsync { get; init; } = new();
    /// <summary>Phase 3b — Handler: AsyncHandlerCandidate event handlers converted in-place to async void.</summary>
    public AsyncifyPhaseCount Handler { get; init; } = new();
    /// <summary>Phase 4 — PropagateCt: CancellationToken forwarded through bridged-file call sites.</summary>
    public AsyncifyPhaseCount PropagateCt { get; init; } = new();
}

/// <summary>Agent-facing return from every batch-first mutation tool.</summary>
public record BatchResultSummary : EngineResultBase
{
    public string BlobName { get; init; } = "";
    public int Succeeded
    {
        get; init;
    }
    public int Skipped
    {
        get; init;
    }
    public int RolledBack
    {
        get; init;
    }
    public int Failed
    {
        get; init;
    }
    public int Attempted
    {
        get; init;
    }
    /// <summary>Inline failures, capped at 10. When Failed>10, this is a sample; check FailuresByReason for the full breakdown.</summary>
    public List<FailureDetail> Failures { get; init; } = new();
    /// <summary>True when Failed>10 and Failures is a partial sample rather than the full list.</summary>
    public bool FailuresTruncated { get; init; }
    /// <summary>Populated when FailuresTruncated=true. Reason→count over the captured sample (first 10 failures).</summary>
    public Dictionary<string, int>? FailuresByReason { get; init; }
    /// <summary>"ok" | "caution" | "halt" — keyed field, never infer from prose.</summary>
    public string Severity { get; init; } = "ok";
    public string Directive { get; init; } = "";
    public bool BreakerOpen
    {
        get; init;
    }
    /// <summary>
    /// Score value to help calibrate <c>scoreThreshold</c> on subsequent Asyncify calls.
    /// From FlagAsyncMigrationCandidates: the lowest score among newly-flagged methods — set
    /// scoreThreshold at or below this to include all flagged candidates.
    /// From Asyncify when no candidates qualified: the highest score among candidates that fell
    /// below scoreThreshold — lower scoreThreshold to this value to include the best one.
    /// Null when no candidates were scored or the operation used scope="targets".
    /// </summary>
    public int? MinCandidateScore { get; init; }
    /// <summary>
    /// Actionable diagnostic suggestions, populated automatically when <c>succeeded==0</c> or
    /// <c>failed&gt;0</c>. Each entry is a self-contained sentence describing a detected pattern
    /// and the recommended next step. Null when everything succeeded or no patterns were detected.
    /// </summary>
    public List<string>? Suggestions { get; init; }
    /// <summary>
    /// Per-phase operation counts. Only populated by Asyncify; null for all other tools.
    /// Breaks down the aggregate <see cref="Succeeded"/> total by phase so callers can distinguish
    /// bridge conversions (Phase 2) from uplift call-site updates (Phase 3).
    /// </summary>
    public AsyncifyPhaseBreakdown? PhaseBreakdown { get; init; }
}

/// <summary>Per-failure detail included inline in BatchResultSummary (capped at 10 when Failed>10).</summary>
public class FailureDetail
{
    public string FilePath { get; set; } = "";
    public string? MethodName
    {
        get; set;
    }
    public string Reason { get; set; } = "";
    /// <summary>"failed" | "rolledback" | "skipped"</summary>
    public ItemRecordOutcome Outcome { get; set; } = ItemRecordOutcome.Unset;
    /// <summary>Structured Roslyn diagnostics that caused the failure. Null when failure is not compiler-error-related.</summary>
    public List<DiagnosticInfo>? CompilerDiagnostics { get; set; }
}

// ── Phase 4 — Batch-first input types ─────────────────────────────────────────

/// <summary>One target in a <c>handler_extract</c> call.</summary>
public class HandlerExtractTarget
{
    /// <summary>Absolute path of the .cs file containing the code to extract.</summary>
    public FilePath FilePath { get; set; } = "";
    /// <summary>Valid C# identifier for the new extracted method.</summary>
    public string NewMethodName { get; set; } = "";
    /// <summary>A short unique code snippet that identifies the block of statements to extract.</summary>
    public string ContextSnippet { get; set; } = "";
    /// <summary>Optional line immediately before the snippet for disambiguation.</summary>
    public string? LineBefore { get; set; }
    /// <summary>Optional line immediately after the snippet for disambiguation.</summary>
    public string? LineAfter { get; set; }
    /// <summary>
    /// When <c>true</c>, <see cref="ContextSnippet"/> is the name of the source method
    /// whose entire body is extracted. <see cref="LineBefore"/> and <see cref="LineAfter"/>
    /// are ignored. Mutually exclusive with providing a code snippet.
    /// </summary>
    public bool ExtractEntireBody { get; set; } = false;
}

/// <summary>A single bridged-method target for <c>run_uplift</c>.</summary>
public class UpliftTarget
{
    /// <summary>Name of the Asyncify-bridge sync method whose callers should be uplifted.</summary>
    public string BridgedMethodName { get; set; } = "";
    /// <summary>Restrict caller scan to one project. null = entire solution.</summary>
    public string? ProjectName { get; set; }
    /// <summary>
    /// Optional Roslyn documentation-comment ID (e.g. <c>M:Avaal.Service.CommonSearch.search(System.String)</c>)
    /// that uniquely identifies the bridge symbol. When set, only callers of this exact overload are uplifted —
    /// unrelated methods with the same name on other types are ignored. Copy from <c>ObsoleteCallerFinding.SymbolId</c>.
    /// </summary>
    public string? SymbolId { get; set; }
}

/// <summary>Canonical input for <c>run_uplift</c>.</summary>
public class RunUpliftInput
{
    public List<UpliftTarget> Targets { get; set; } = new();
    public bool DryRun { get; set; } = false;
    public int MaxCallersPerMethod { get; set; } = 10;
    public bool PropagateCancellationTokens { get; set; } = true;
}

/// <summary>One target in a <c>flag_migration_candidates</c> call (scope="targets").</summary>
public class FlagCandidateTarget
{
    public FilePath FilePath { get; set; } = "";
    public string MethodName { get; set; } = "";
    public string Pattern { get; set; } = "AsyncBridgeCandidate";
    public int Score { get; set; } = 0;
    public string? Reason
    {
        get; set;
    }
}

/// <summary>Canonical input for <c>flag_migration_candidates</c>.</summary>
public class FlagCandidatesInput
{
    /// <summary>"targets" — process explicit list; "project" — autonomous project-level scan.</summary>
    public string Scope { get; set; } = "targets";
    /// <summary>Required when Scope="targets".</summary>
    public List<FlagCandidateTarget>? Targets
    {
        get; set;
    }
    /// <summary>Required when Scope="project". null = entire solution.</summary>
    public string? ProjectName
    {
        get; set;
    }
    /// <summary>Migration pattern. Default "AsyncBridgeCandidate".</summary>
    public string Pattern { get; set; } = "AsyncBridgeCandidate";
    /// <summary>Minimum score to flag a method (Scope="project" only). Default 50.</summary>
    public int MinScore { get; set; } = 50;
    /// <summary>When true, reports what would be flagged without writing files.</summary>
    public bool DryRun { get; set; } = false;
    /// <summary>When true, re-evaluates every method even if already flagged (Scope="project" only).</summary>
    public bool ForceRescan { get; set; } = false;
}

// ── Phase 6 — asyncify macro input ────────────────────────────────────────────

/// <summary>Canonical input for the <c>asyncify</c> macro-workflow tool.</summary>
public class AsyncifyInput
{
    /// <summary>
    /// Project to asyncify. null = entire solution.
    /// Used for autonomous candidate discovery when no explicit MethodTargets are supplied.
    /// </summary>
    public string? ProjectName
    {
        get; set;
    }

    /// <summary>
    /// Optional explicit list of methods to asyncify. When set, skips the autonomous
    /// flag-scan phase and starts directly at the bridge-conversion phase.
    /// </summary>
    public List<FlagCandidateTarget>? MethodTargets
    {
        get; set;
    }

    /// <summary>Method names to skip in every phase.</summary>
    public List<string>? Exclusions
    {
        get; set;
    }

    /// <summary>When true, reports what would change without writing any files.</summary>
    public bool DryRun { get; set; } = false;

    /// <summary>
    /// When true (default), propagates CancellationToken to inner async callees in
    /// both the bridge and uplift phases, and runs a final CT-propagation sweep.
    /// </summary>
    public bool PropagateCancellationTokens { get; set; } = true;

    /// <summary>Max methods to convert in the bridge phase per run. Default 50.</summary>
    public int MaxMethods { get; set; } = 50;

    /// <summary>Max callers to uplift per bridged method. Default 10.</summary>
    public int MaxCallersPerMethod { get; set; } = 10;

    /// <summary>Minimum score to flag a method in the discovery phase. Default 50.</summary>
    public int MinScore { get; set; } = 50;

    /// <summary>Score threshold for bridge conversion (score ≥ threshold eligible). Default 60. Raise to focus on highest-impact candidates; lower to include more.</summary>
    public int ScoreThreshold { get; set; } = 60;

    /// <summary>
    /// Wall-clock limit in seconds. The current phase completes its in-progress item,
    /// then the macro stops and returns a partial result. 0 = no limit (default).
    /// </summary>
    public int MaxRuntimeSeconds { get; set; } = 0;

    /// <summary>
    /// Total items cap across all phases (methods bridged + callers uplifted + CT files).
    /// Remaining phases are skipped when the cumulative count meets or exceeds this value.
    /// 0 = no limit (default).
    /// </summary>
    public int MaxIterations { get; set; } = 0;
}

// ── Phase 7 — per-operation result types with next-step chaining fields ───────

/// <summary>
/// Return type for <c>bridge_async_methods</c>. Wraps <see cref="BatchResultSummary"/> and adds
/// <see cref="SuggestedUpliftTargets"/> — the exact input for the next workflow step
/// (<c>uplift_callers</c>).
/// </summary>
public class BridgeAsyncMethodsResult
{
    public BatchResultSummary Summary { get; init; } = new();
    /// <summary>
    /// Bridged method names ready to pass as <c>targets</c> to <c>uplift_callers</c>.
    /// Each entry has <c>BridgedMethodName</c> and <c>SymbolId</c>; <c>ProjectName</c> is null (solution-scoped).
    /// </summary>
    public List<UpliftTarget> SuggestedUpliftTargets { get; init; } = new();
}

/// <summary>
/// Return type for <c>uplift_callers</c>. Wraps <see cref="BatchResultSummary"/> and adds
/// <see cref="SuggestedPropagateTargets"/> — the exact input for the next workflow step
/// (<c>propagate_cancellation_token</c>).
/// </summary>
public class UpliftCallersResult
{
    public BatchResultSummary Summary { get; init; } = new();
    /// <summary>
    /// Files touched during uplift, ready to pass as <c>targets</c> to
    /// <c>propagate_cancellation_token</c>. Each entry has <c>FilePath</c>;
    /// <c>MethodNames</c> is null (process whole file).
    /// </summary>
    public List<BatchTarget> SuggestedPropagateTargets { get; init; } = new();
    /// <summary>
    /// Structured outcome classification with routed failure hints (spec §3.6).
    /// Substrate-derived — never infer outcome from Summary.Severity or prose scanning.
    /// </summary>
    public OperationSummary? OperationSummary { get; init; }
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
    public List<BatchTarget>? BatchTargets
    {
        get; set;
    }

    /// <summary>Max files/items to process (default 100). propagate_cancellation_token and add_cancellation_token.</summary>
    public int MaxItems { get; set; } = 100;

    /// <summary>Propagate CancellationToken in new async overloads (default true). convert_to_async_bridge and run_uplift.</summary>
    public bool PropagateCancellationTokens { get; set; } = true;

    // ── run_uplift ─────────────────────────────────────────────────────────────

    /// <summary>Bridge method targets to uplift. Each has BridgedMethodName and optional ProjectName. run_uplift only.</summary>
    public List<UpliftTarget>? UpliftTargets
    {
        get; set;
    }

    /// <summary>Max callers per bridged method (default 10). run_uplift and asyncify.</summary>
    public int MaxCallersPerMethod { get; set; } = 10;

    // ── flag_migration_candidates ──────────────────────────────────────────────

    /// <summary>"targets" (default) or "project". flag_migration_candidates only.</summary>
    public string FlagScope { get; set; } = "targets";

    /// <summary>Explicit methods to flag (FlagScope="targets"). flag_migration_candidates only.</summary>
    public List<FlagCandidateTarget>? FlagTargets
    {
        get; set;
    }

    /// <summary>Restrict scan to one project; null = entire solution. flag_migration_candidates (scope=project) and asyncify.</summary>
    public string? ProjectName
    {
        get; set;
    }

    /// <summary>Migration pattern (default "AsyncBridgeCandidate"). flag_migration_candidates and asyncify.</summary>
    public string Pattern { get; set; } = "AsyncBridgeCandidate";

    /// <summary>Minimum score to flag (default 50). flag_migration_candidates and asyncify.</summary>
    public int MinScore { get; set; } = 50;

    /// <summary>Re-evaluate already-flagged methods (flag_migration_candidates scope=project only).</summary>
    public bool ForceRescan { get; set; } = false;

    // ── asyncify ───────────────────────────────────────────────────────────────

    /// <summary>Explicit (FilePath, MethodName) targets; skips the flag-discovery phase. asyncify only.</summary>
    public List<FlagCandidateTarget>? MethodTargets
    {
        get; set;
    }

    /// <summary>Method names to skip in all asyncify phases. asyncify only.</summary>
    public List<string>? Exclusions
    {
        get; set;
    }

    /// <summary>Max methods to convert in the bridge phase (default 50). asyncify only.</summary>
    public int MaxMethods { get; set; } = 50;

    /// <summary>Min score eligible for bridge conversion (score ≥ threshold eligible, default 60). asyncify only.</summary>
    public int ScoreThreshold { get; set; } = 60;

    // ── handler_extract ────────────────────────────────────────────────────────

    /// <summary>
    /// Extraction targets for <c>handler_extract</c>. Each entry specifies one code block to
    /// extract into a new private method. ContextSnippet identifies the block to extract.
    /// </summary>
    public List<HandlerExtractTarget>? HandlerExtractTargets { get; set; }
}
