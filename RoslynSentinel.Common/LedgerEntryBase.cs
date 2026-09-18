namespace RoslynSentinel.Common;

public abstract record LedgerEntryBase
{
    public required string EntryId
    {
        get; init;
    }
    public required string FilePath
    {
        get; init;
    }
    public required int Line
    {
        get; init;
    }
    public bool IsFixed
    {
        get; init;
    }
    public string? ChangeId
    {
        get; init;
    }
}
// Shared by CallSiteLedgerEntry and MoveMember's PreviewCallSite report. Valid rows never become
// ledger entries (see proposal_scoped_operation_ledger.md), so CallSiteLedgerEntry.Status is never
// constructed with Valid - it exists here only so PreviewCallSite can reuse this enum instead of a
// near-duplicate one.
public enum CallSiteStatus
{
    Valid,
    Ambiguous,
    NoCandidateIntroducible,
    NoCandidateBlocked,
    MoveOrderDependent
}
// Added by AddTopLevelType (expected - used for diagnostics)
public sealed record CallSiteLedgerEntry : LedgerEntryBase
{
    public required string BrokenExpression
    {
        get; init;
    }
    public required string OldStaticType
    {
        get; init;
    }
    public required CallSiteStatus Status
    {
        get; init;
    }
    public string? BlockReason
    {
        get; init;
    }
    public string? SuggestedFix
    {
        get; init;
    }
    public IReadOnlyList<string> CandidatesInScope { get; init; } = [];
}
