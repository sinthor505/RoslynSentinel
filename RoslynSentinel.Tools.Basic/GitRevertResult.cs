namespace RoslynSentinel.Tools.Basic;

public record GitRevertResult : GitResult
{
    public string CommitHash { get; set; } = "";

    // See CommitHashLength on GitCommitResult - same rationale, applied here since revert returns
    // a hash-typed field too (the finding doc's guardrail originally covered commit only).
    public int CommitHashLength => CommitHash.Length;
    public string Message { get; set; } = "";
    public bool PendingCommit
    {
        get; set;
    }
}
