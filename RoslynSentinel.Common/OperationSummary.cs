namespace RoslynSentinel.Common;

/// <summary>
/// Substrate-derived operation result with structured outcome classification and routed failure hints (spec §3.6).
/// Always produced by <see cref="FromCounts"/> — never constructed at call sites.
/// </summary>
public sealed class OperationSummary
{
    public string BlobName { get; init; } = "";
    public string ChangeId { get; init; } = "";

    public int Succeeded { get; init; }
    public int AlreadySatisfied { get; init; }
    public int Skipped { get; init; }
    public int Failed { get; init; }
    public int Blocked { get; init; }
    public int Attempted { get; init; }

    /// <summary>Substrate-derived verdict — never infer this from Severity or prose scanning.</summary>
    public OperationOutcome Outcome { get; init; }

    /// <summary>
    /// Short substrate-authored sentence stating the verdict and the single most useful next move.
    /// Must not claim completion when <see cref="Outcome"/> is <see cref="OperationOutcome.PartialProgress"/> or <see cref="OperationOutcome.NoProgress"/>.
    /// </summary>
    public string Directive { get; init; } = "";

    /// <summary>Only <see cref="ItemOutcome.Failed"/> and <see cref="ItemOutcome.Blocked"/> items. AlreadySatisfied/Skipped/Succeeded never appear here.</summary>
    public IReadOnlyList<ItemFailure> Actionable { get; init; } = Array.Empty<ItemFailure>();
    public bool ActionableTruncated { get; init; }

    public bool BreakerOpen { get; init; }

    // ── Derivation ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Derives the <see cref="OperationOutcome"/> from counts according to spec §3.2.
    /// This is the single canonical derivation site — no call site may compute it independently.
    /// </summary>
    public static OperationOutcome DeriveOutcome(
        int succeeded,
        int alreadySatisfied,
        int skipped,
        int failed,
        int blocked,
        int attempted)
    {
        if (attempted == 0)
        {
            return OperationOutcome.NothingToDo;
        }

        bool hasFailures = failed > 0 || blocked > 0;

        if (!hasFailures && alreadySatisfied == 0 && succeeded > 0)
        {
            return OperationOutcome.CompletedFully;
        }

        if (!hasFailures && alreadySatisfied > 0)
        {
            return OperationOutcome.CompletedWithNoOps;
        }

        if (succeeded > 0 && hasFailures)
        {
            return OperationOutcome.PartialProgress;
        }

        if (succeeded == 0 && hasFailures)
        {
            return OperationOutcome.NoProgress;
        }

        return OperationOutcome.NothingToDo;
    }

    /// <summary>
    /// Factory — the only way to produce an <see cref="OperationSummary"/>.
    /// Derives <see cref="Outcome"/> from counts in one place; never passes it in.
    /// </summary>
    public static OperationSummary FromCounts(
        string blobName,
        string changeId,
        int succeeded,
        int alreadySatisfied,
        int skipped,
        int failed,
        int blocked,
        int attempted,
        IReadOnlyList<ItemFailure> actionable,
        bool actionableTruncated,
        string directive,
        bool breakerOpen)
    {
        OperationOutcome outcome = DeriveOutcome(succeeded, alreadySatisfied, skipped, failed, blocked, attempted);

        return new OperationSummary
        {
            BlobName = blobName,
            ChangeId = changeId,
            Succeeded = succeeded,
            AlreadySatisfied = alreadySatisfied,
            Skipped = skipped,
            Failed = failed,
            Blocked = blocked,
            Attempted = attempted,
            Outcome = outcome,
            Directive = directive,
            Actionable = actionable,
            ActionableTruncated = actionableTruncated,
            BreakerOpen = breakerOpen,
        };
    }
}
