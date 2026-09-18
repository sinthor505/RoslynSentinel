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
// Added by AddTopLevelType (expected - used for diagnostics)
public enum CallSiteStatus
{
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
