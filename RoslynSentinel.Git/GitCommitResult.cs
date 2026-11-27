namespace RoslynSentinel.Git;

public record GitCommitResult : GitResult
{
    public string CommitHash { get; set; } = "";

    // Sibling of CommitHash, always its literal .Length - a byte-counted answer next to the hash
    // so a caller never has to visually count/transcribe the hex string to know its length. See
    // docs/current/findings/closed/finding_git_commit_commithash_length_unconfirmed_no_source_mechanism.md: every
    // "41-character hash" report to date turned out to be an agent miscounting by eye, never an
    // actual defect - this field makes that whole category of doubt unnecessary to raise.
    public int CommitHashLength => CommitHash.Length;
    public string Message { get; set; } = "";

    public List<string> RemainingStaged { get; set; } = [];
}
