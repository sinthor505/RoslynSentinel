namespace RoslynSentinel.Common;

/// <summary>
/// Aggregate result of a <c>run_bridge_batch</c> call.
/// </summary>
/// <param name="Applied">Methods that were successfully bridged and written to disk.</param>
/// <param name="Skipped">
/// Methods that were skipped (pre-condition failure or compiler errors after transform).
/// Each skipped method is flagged <c>[MigrationCandidate("NeedsManualReview")]</c>.
/// </param>
/// <param name="RemainingCandidates">
/// Number of eligible <c>[MigrationCandidate("AsyncBridgeCandidate")]</c> methods still
/// present in the solution after this batch (i.e., not yet bridged or skipped this run).
/// </param>
/// <param name="StopReason">
/// Why the batch ended:
/// <list type="bullet">
///   <item><c>batch_complete</c> — all eligible candidates were processed.</item>
///   <item><c>budget_exhausted</c> — <c>maxBridges</c> limit reached before all candidates processed.</item>
///   <item><c>no_candidates</c> — no <c>[MigrationCandidate("AsyncBridgeCandidate")]</c> methods found.</item>
///   <item><c>dry_run</c> — dry-run mode; no files written.</item>
/// </list>
/// </param>
public sealed record BridgeBatchResult : EngineResultBase
{
    public List<BridgeAppliedInfo> Applied
    {
        get; init;
    }
    public List<BridgeSkippedInfo> Skipped
    {
        get; init;
    }
    public int RemainingCandidates
    {
        get; init;
    }
    public string StopReason
    {
        get; init;
    }

    public BridgeBatchResult(
        List<BridgeAppliedInfo> applied,
        List<BridgeSkippedInfo> skipped,
        int remainingCandidates,
        string stopReason)
    {
        this.Applied = applied;
        this.Skipped = skipped;
        this.RemainingCandidates = remainingCandidates;
        this.StopReason = stopReason;
    }
}