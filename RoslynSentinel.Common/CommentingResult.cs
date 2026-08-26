namespace RoslynSentinel.Common;

/// <summary>
/// Return type for <c>BulkComment</c>. Counts are the authoritative completion signal for a run —
/// not agent prose. <see cref="RemainingStale"/> tells the caller whether another call is needed
/// to finish the scope; because progress is tracked via <c>[ContentHash]</c> attributes on disk,
/// re-invoking with the same scope resumes for free.
/// </summary>
public sealed class CommentingResult
{
    /// <summary>Total members in scope (methods, constructors, properties, enums).</summary>
    public int TotalMembers { get; set; }

    /// <summary>Members that already had a current (matching) content hash — no work needed.</summary>
    public int AlreadyCurrent { get; set; }

    /// <summary>Members newly stamped with the sentinel <c>[ContentHash]</c> during this call's seed phase.</summary>
    public int Seeded { get; set; }

    /// <summary>Members commented (or re-commented) during this call's work phase.</summary>
    public int CommentedThisCall { get; set; }

    /// <summary>Members still stale (never processed, or changed since last processed) after this call — nonzero means re-invoke to continue.</summary>
    public int RemainingStale { get; set; }

    /// <summary>
    /// Reason→count over every member skipped this call (LLM failure, apply failure,
    /// maxMembers/maxRuntimeSeconds cap reached). No per-member detail — file/method names aren't
    /// actionable here; re-invoking with the same scope is the fix for a nonzero
    /// <see cref="RemainingStale"/> regardless of reason. Empty when nothing was skipped.
    /// </summary>
    public Dictionary<string, int> SkippedByReason { get; set; } = [];

    /// <summary>Number of distinct files touched this call. No per-file breakdown — the top-level counts already cover the run.</summary>
    public int FilesTouched { get; set; }

    /// <summary>"ok" | "caution" | "halt" — mirrors <see cref="BatchResultSummary.Severity"/>; keyed field, never infer from prose.</summary>
    public string Severity { get; set; } = "ok";

    public bool BreakerOpen { get; set; }

    /// <summary>True when this call ran with dryRun=true — no LLM calls or writes were made, counts reflect planned work only.</summary>
    public bool DryRun { get; set; }

    /// <summary>Forensic operation blob filename (enables UndoLastApply) — empty when dryRun (nothing was written).</summary>
    public string BlobName { get; set; } = "";
}
