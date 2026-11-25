namespace RoslynSentinel.Common;

/// <summary>Which tree <see cref="Libgit2GitReader.CommitWithCompareAndSwap"/> commits.</summary>
internal enum CasCommitTreeSource
{
    /// <summary>Everything staged: the index written as a tree (what <c>git commit</c> without paths commits).</summary>
    WholeIndex,

    /// <summary>
    /// HEAD's tree with the listed paths' index entries replaced, added or removed (what <c>git commit --only -- paths</c> commits once the listed paths' working-tree
    /// content is already staged; a listed path whose working tree differs from the index is declined, because git would commit the working-tree content).
    /// </summary>
    HeadPlusPaths,
}

/// <summary>How a compare-and-swap commit ended.</summary>
internal enum CasCommitOutcome
{
    /// <summary>The commit exists and the branch (or detached HEAD) moved from the expected tip to it.</summary>
    Committed,

    /// <summary>The ref no longer pointed at the tip the commit was built on (libgit2 -15, or the ref vanished): nothing moved, no commit is reachable; rebuild and retry or give up.</summary>
    BranchMoved,

    /// <summary>index.lock existed, or libgit2 could not lock the ref file (-14): another git process is committing; retryable.</summary>
    LockContention,

    /// <summary>A planned decline: a condition git handles differently from libgit2 (environment variable, config, repository state, path rule). Nothing was written; the git CLI answers.</summary>
    Declined,

    /// <summary>The tree equals the parent's tree (or the empty tree on an unborn branch): git refuses such a commit without --allow-empty.</summary>
    NothingToCommit,

    /// <summary>An unexpected libgit2 error; <see cref="CasCommitResult.Detail"/> carries its text. Nothing was written to any ref.</summary>
    Failed,
}

/// <summary>
/// Result of <see cref="Libgit2GitReader.CommitWithCompareAndSwap"/>. <see cref="CommitId"/> is set for <see cref="CasCommitOutcome.Committed"/>;
/// <see cref="ExpectedTip"/> and <see cref="ActualTip"/> (null when the ref did not exist) for <see cref="CasCommitOutcome.BranchMoved"/>;
/// <see cref="Detail"/> holds the reason or libgit2's own error text for every outcome but Committed and NothingToCommit.
/// <see cref="ReferenceName"/> and <see cref="ParentId"/> are set for <see cref="CasCommitOutcome.Committed"/>: the full ref the compare-and-swap moved (HEAD when detached)
/// and the tip the commit was built on (null for an unborn branch).
/// </summary>
internal sealed record CasCommitResult(CasCommitOutcome Outcome, string? CommitId = null, string? ExpectedTip = null, string? ActualTip = null, string? Detail = null, string? ReferenceName = null, string? ParentId = null)
{
    internal bool IsCommitted => Outcome == CasCommitOutcome.Committed;

    internal static CasCommitResult Committed(string commitId, string? referenceName = null, string? parentId = null) =>
        new(CasCommitOutcome.Committed, CommitId: commitId, ReferenceName: referenceName, ParentId: parentId);

    internal static CasCommitResult BranchMoved(string? expectedTip, string? actualTip, string detail) =>
        new(CasCommitOutcome.BranchMoved, ExpectedTip: expectedTip, ActualTip: actualTip, Detail: detail);

    internal static CasCommitResult LockContention(string detail) => new(CasCommitOutcome.LockContention, Detail: detail);

    internal static CasCommitResult Declined(string reason) => new(CasCommitOutcome.Declined, Detail: reason);

    internal static CasCommitResult NothingToCommit() => new(CasCommitOutcome.NothingToCommit);

    internal static CasCommitResult Failed(string detail) => new(CasCommitOutcome.Failed, Detail: detail);
}
