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
/// One commit as the Git tool's <c>log</c> operation presents it: the full and abbreviated hash (<c>%H</c>, <c>%h</c>), the author name
/// (<c>%an</c>, no mailmap), the author date as strict ISO 8601 with the author's own UTC offset (<c>%aI</c>, e.g.
/// <c>2026-01-02T03:04:05+05:30</c>, <c>+00:00</c> never <c>Z</c>) and the raw message (<c>%B</c>, subject and body exactly as stored,
/// still with its original line endings and trailing newline; the tool normalizes those when it builds its DTO).
/// </summary>
internal sealed record LogCommit(string Sha, string ShortSha, string AuthorName, string AuthorDate, string Message);

/// <summary>
/// Everything the Git tool's <c>status</c> operation reads in one typed answer. <see cref="Branch"/> is what
/// <c>git rev-parse --abbrev-ref HEAD</c> prints: the branch's short name, <c>HEAD</c> when detached, null when HEAD
/// cannot be resolved (an unborn branch). <see cref="Changes"/> are the porcelain rows with every untracked file listed
/// individually (<c>--untracked-files=all</c>), tracked rows first and then untracked rows, each group in git's
/// byte-wise path order. <see cref="InProgress"/> names the half-finished multi-step operation
/// (see <see cref="GitOperationState.Detect"/>), null when none.
/// </summary>
internal sealed record WorkingTreeStatus(string? Branch, IReadOnlyList<WorkingTreeChange> Changes, string? InProgress);

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

    /// <summary>
    /// What the Git tool's <c>status</c> operation presents: branch, every changed and untracked file individually, and the
    /// in-progress operation (see <see cref="WorkingTreeStatus"/>). Null when git cannot answer (not a repository, git
    /// missing or timed out, <c>status</c> failing); an unborn HEAD is an answer with a null <see cref="WorkingTreeStatus.Branch"/>.
    /// </summary>
    WorkingTreeStatus? GetRepositoryStatus(string workingDirectory, TimeSpan cliTimeout);

    /// <summary>
    /// What the Git tool's <c>log</c> operation presents: up to <paramref name="maxCount"/> commits, newest first in git's default order,
    /// from <paramref name="revision"/> (null or blank is HEAD), limited to <paramref name="paths"/> when given (an empty list is the bare <c>--</c>),
    /// optionally by pickaxe (<c>-S</c>, or <c>-G</c> when <paramref name="pickaxeRegex"/>; blank means none) and following renames of one path
    /// (<paramref name="follow"/>). The CLI implementation runs <c>git log</c> with exactly those options; the libgit2 implementation throws
    /// <see cref="NotSupportedException"/> for what it cannot reproduce, so the composite reader answers from the CLI. Null when git cannot
    /// answer (not a repository, unknown revision, unborn HEAD, git missing or timed out).
    /// </summary>
    IReadOnlyList<LogCommit>? GetLog(string workingDirectory, int maxCount, string? revision, IReadOnlyList<string>? paths, string? pickaxe, bool pickaxeRegex, bool follow, TimeSpan cliTimeout);

    /// <summary>The content of the blob at <paramref name="relativePath"/> (relative to <paramref name="workingDirectory"/>) in HEAD, decoded as UTF-8; null when HEAD has no such blob (missing path, or a directory).</summary>
    string? ReadBlobAtHead(string workingDirectory, string relativePath, TimeSpan cliTimeout);

    /// <summary>The paths in the index (<c>ls-files --cached</c>) under <paramref name="workingDirectory"/>, relative to it, in index order.</summary>
    IReadOnlyList<string>? ListTrackedPaths(string workingDirectory, TimeSpan cliTimeout);

    /// <summary>
    /// The index entries (<c>git --literal-pathspecs ls-files -z --cached -- paths</c>) that are one of <paramref name="paths"/> or lie under one as a
    /// directory, repo-relative with '/', each once, in index order. Pathspecs are literal and case-sensitive. The libgit2 reader throws
    /// <see cref="NotSupportedException"/> for a path that is not a clean repo-relative one (git normalises or rejects those). Null when git cannot
    /// answer (not a repository, a path git rejects, git missing or timed out).
    /// </summary>
    IReadOnlyList<string>? ListIndexPathsMatching(string workingDirectory, IReadOnlyList<string> paths, TimeSpan cliTimeout);

    /// <summary>
    /// The files in HEAD's tree (<c>git --literal-pathspecs ls-tree -r -z --name-only HEAD -- paths</c>) that are one of <paramref name="paths"/> or lie
    /// under one as a directory (a directory itself is never listed), each once, in no guaranteed order. Same path rules as
    /// <see cref="ListIndexPathsMatching"/>; null as well on an unborn HEAD.
    /// </summary>
    IReadOnlyList<string>? ListHeadPathsMatching(string workingDirectory, IReadOnlyList<string> paths, TimeSpan cliTimeout);

    /// <summary>
    /// The members of <paramref name="paths"/> that <c>git check-ignore -q -- path</c> reports as ignored (exit 0), in the order given: ignore rules from
    /// every .gitignore, .git/info/exclude and core.excludesFile, an ignored parent directory included, and never a file already in the index. A path
    /// git rejects (outside the repository) is simply not ignored. The libgit2 reader throws <see cref="NotSupportedException"/> for a path that is
    /// not a clean repo-relative one. Null when there is no repository.
    /// </summary>
    IReadOnlyList<string>? FindIgnoredPaths(string workingDirectory, IReadOnlyList<string> paths, TimeSpan cliTimeout);

    /// <summary>Every commit reachable from HEAD (<c>git log</c> with no range), newest first, with its raw message.</summary>
    IReadOnlyList<CommitMessage>? GetCommitMessages(string workingDirectory, TimeSpan cliTimeout);
}
