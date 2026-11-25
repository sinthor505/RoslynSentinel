namespace RoslynSentinel.Common;

/// <summary>
/// One <c>git status --porcelain</c> row. <see cref="Path"/> is repo-root-relative with '/' separators (an untracked
/// directory is collapsed to <c>dir/</c>, as git does); <see cref="OriginalPath"/> is the pre-rename path for a rename
/// or copy, else null. <see cref="IndexState"/> and <see cref="WorkTreeState"/> are the porcelain X and Y letters
/// (' ' unchanged, 'M', 'A', 'D', 'R', 'T', 'U' conflicted, '?' untracked).
/// </summary>
internal sealed record WorkingTreeChange(string Path, string? OriginalPath, char IndexState, char WorkTreeState);

/// <summary>
/// A commit with the paths it changes. <see cref="ShortHash"/> is the abbreviated hash (at least 7 characters),
/// <see cref="Paths"/> the repo-root-relative paths the commit touches ('/' separators, no rename detection, so a
/// rename lists both the old and the new path); it is empty for a merge commit (git log shows no diff for merges).
/// </summary>
internal sealed record ChangedCommit(string ShortHash, string Subject, IReadOnlyList<string> Paths);

/// <summary>
/// The typed read-only git queries the server runs on its own behalf (staleness check, build stamp, not-found evidence,
/// residual-mention scan, doc trailer scan). Implemented by <see cref="IGitReader"/>; every method returns null when git
/// cannot answer and never throws for those conditions. <paramref name="cliTimeout"/> bounds the CLI implementation only.
/// </summary>
internal interface IRepoQuery
{
    /// <summary>HEAD's full sha, or its abbreviation (at least 7 characters) when <paramref name="shortForm"/>.</summary>
    string? GetHeadSha(string workingDirectory, bool shortForm, TimeSpan cliTimeout);

    /// <summary>The commits in <c>&lt;sha&gt;..HEAD</c>, newest first, each with the paths it changes; null when <paramref name="sha"/> does not resolve to a commit.</summary>
    IReadOnlyList<ChangedCommit>? GetCommitsSince(string workingDirectory, string sha, TimeSpan cliTimeout);

    /// <summary>
    /// Index and working-tree changes as <c>git status --porcelain</c> reports them (ignored files excluded, untracked
    /// directories collapsed). Tracked changes come first, then untracked entries, each group in path order.
    /// <paramref name="pathspecs"/> restrict the rows (relative to <paramref name="workingDirectory"/>; git glob semantics, no pathspec magic).
    /// </summary>
    IReadOnlyList<WorkingTreeChange>? GetStatus(string workingDirectory, IReadOnlyList<string>? pathspecs, TimeSpan cliTimeout);

    /// <summary>The content of the blob at <paramref name="relativePath"/> (relative to <paramref name="workingDirectory"/>) in HEAD, decoded as UTF-8; null when HEAD has no such blob.</summary>
    string? ReadBlobAtHead(string workingDirectory, string relativePath, TimeSpan cliTimeout);
}

/// <summary>
/// Holder of the process-wide git query seam. This is a process-wide static: null means git cannot answer, the same
/// answer readers already give for "not a repository". A test that sets <see cref="Current"/> must restore it.
/// </summary>
internal static class RepoQueryProvider
{
    /// <summary>The active query seam, or null when git cannot answer.</summary>
    internal static IRepoQuery? Current { get; set; }
}

/// <summary>
/// Started after a successful solution load so the git backend can probe the solution's repository ahead of the first
/// real call. Implemented by <c>GitBackendWarmup</c>.
/// </summary>
public interface IRepoProbe
{
    /// <summary>Starts the repo probe for the directory of a just-loaded solution and returns at once.</summary>
    void StartRepoProbe(string solutionDirectory);
}
