namespace RoslynSentinel.Common;

/// <summary>Per-file breakdown row in <see cref="CommentingResult.ByFile"/>.</summary>
public sealed class CommentingFileBreakdown
{
    public FilePath filePath { get; set; } = string.Empty;
    public int Seeded { get; set; }
    public int Commented { get; set; }
    public int AlreadyCurrent { get; set; }
    public int Skipped { get; set; }
}

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
    /// Members skipped this call, with reasons (LLM failure, apply failure, maxMembers/maxRuntimeSeconds
    /// cap reached). Capped at 10; when there are more, this is a sample — see
    /// <see cref="SkippedTruncated"/> and <see cref="SkippedByReason"/> for the full breakdown.
    /// </summary>
    public List<FailureDetail> Skipped { get; set; } = [];

    /// <summary>True when the underlying skip count exceeded 10 and <see cref="Skipped"/> is a partial sample.</summary>
    public bool SkippedTruncated { get; set; }

    /// <summary>Populated when <see cref="SkippedTruncated"/> is true. Reason→count over the full skip list.</summary>
    public Dictionary<string, int>? SkippedByReason { get; set; }

    /// <summary>
    /// Per-file breakdown, keyed implicitly by <see cref="CommentingFileBreakdown.filePath"/>. Capped
    /// at 20 rows (files with the most Skipped, then most Commented, first) for a large solution-wide
    /// run; see <see cref="ByFileTruncated"/> and <see cref="ByFileTotalCount"/>.
    /// </summary>
    public List<CommentingFileBreakdown> ByFile { get; set; } = [];

    /// <summary>True when the full per-file breakdown exceeded 20 rows and <see cref="ByFile"/> is a partial sample.</summary>
    public bool ByFileTruncated { get; set; }

    /// <summary>Total number of files touched this call, regardless of <see cref="ByFileTruncated"/>.</summary>
    public int ByFileTotalCount { get; set; }

    /// <summary>"ok" | "caution" | "halt" — mirrors <see cref="BatchResultSummary.Severity"/>; keyed field, never infer from prose.</summary>
    public string Severity { get; set; } = "ok";

    public bool BreakerOpen { get; set; }

    /// <summary>True when this call ran with dryRun=true — no LLM calls or writes were made, counts reflect planned work only.</summary>
    public bool DryRun { get; set; }

    /// <summary>Forensic operation blob filename (enables UndoLastApply) — empty when dryRun (nothing was written).</summary>
    public string BlobName { get; set; } = "";
}
