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

/// <summary>A commit's full hash and its raw message exactly as stored (line endings and trailing newline untouched).</summary>
internal sealed record CommitMessage(string Sha, string Message);

/// <summary>
/// Which implementation answered a git read. <see cref="None"/> until the first call.
/// </summary>
internal enum GitReaderBackend
{
    None = 0,
    Libgit2,
    Cli,
}

/// <summary>
/// The typed read-only git probes the server runs on its own behalf (staleness check, build stamp, not-found evidence,
/// residual-mention scan, doc trailer scan), behind one seam so a libgit2 implementation and the git CLI implementation
/// can be compared and swapped. Every method takes the directory git would run in (it need not be the repo root, and
/// relative paths are relative to it, as with the CLI) and returns null when git cannot answer: not a repository, an
/// unborn HEAD, an unknown revision, git missing or timed out. A method never throws for those conditions.
/// <paramref name="cliTimeout"/> bounds the CLI implementation only; libgit2 calls cannot be timed out.
/// </summary>
internal interface IGitReader
{
    /// <summary>HEAD's full sha, or its abbreviation (at least 7 characters) when <paramref name="shortForm"/>.</summary>
    string? GetHeadSha(string workingDirectory, bool shortForm, TimeSpan cliTimeout);

    /// <summary>The commits in <c>&lt;sha&gt;..HEAD</c>, newest first, each with the paths it changes; null when <paramref name="sha"/> (full or abbreviated) does not resolve to a commit.</summary>
    IReadOnlyList<ChangedCommit>? GetCommitsSince(string workingDirectory, string sha, TimeSpan cliTimeout);

    /// <summary>
    /// Index and working-tree changes as <c>git status --porcelain</c> reports them (ignored files excluded, untracked
    /// directories collapsed). Tracked changes come first, then untracked entries, each group in path order.
    /// <paramref name="pathspecs"/> restrict the rows (relative to <paramref name="workingDirectory"/>; git glob semantics, no pathspec magic).
    /// </summary>
    IReadOnlyList<WorkingTreeChange>? GetStatus(string workingDirectory, IReadOnlyList<string>? pathspecs, TimeSpan cliTimeout);

    /// <summary>The content of the blob at <paramref name="relativePath"/> (relative to <paramref name="workingDirectory"/>) in HEAD, decoded as UTF-8; null when HEAD has no such blob (missing path, or a directory).</summary>
    string? ReadBlobAtHead(string workingDirectory, string relativePath, TimeSpan cliTimeout);

    /// <summary>The paths in the index (<c>ls-files --cached</c>) under <paramref name="workingDirectory"/>, relative to it, in index order.</summary>
    IReadOnlyList<string>? ListTrackedPaths(string workingDirectory, TimeSpan cliTimeout);

    /// <summary>Every commit reachable from HEAD (<c>git log</c> with no range), newest first, with its raw message.</summary>
    IReadOnlyList<CommitMessage>? GetCommitMessages(string workingDirectory, TimeSpan cliTimeout);
}
