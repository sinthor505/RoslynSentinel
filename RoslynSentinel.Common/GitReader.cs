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
/// One row of <c>git branch --list --all</c> as the Git tool reports it: <see cref="Name"/> is the branch name (a remote-tracking branch without the
/// "remotes/" prefix), <see cref="IsCurrent"/> is true for the row git marks with "* " (the branch HEAD is on in this working tree).
/// </summary>
internal sealed record BranchRef(string Name, bool IsCurrent, bool IsRemote);

/// <summary>
/// One <c>git stash list</c> row: <see cref="Index"/> is N of stash@{N}, <see cref="ShortSha"/> the stash commit's abbreviation (<c>%h</c>),
/// <see cref="CommitterDate"/> its strict ISO committer date (<c>%cI</c>) and <see cref="Subject"/> the raw reflog subject (<c>%gs</c>, for example
/// "WIP on main: abc1234 subject" or "On main: label").
/// </summary>
internal sealed record StashRef(int Index, string ShortSha, string CommitterDate, string Subject);

/// <summary>
/// One row of <c>git diff --name-status</c>: <see cref="Status"/> is git's letter ("A", "D", "M", "T", or for a rename or copy the letter with its
/// similarity such as "R087"), <see cref="Path"/> the path as git prints it (repo-root-relative, '/' separators, never octal-escaped; git's own quoting for a name
/// with a quote, backslash or control character) and <see cref="OldPath"/> the pre-rename path for "R"/"C" rows, else null.
/// </summary>
internal sealed record NameStatusEntry(string Status, string Path, string? OldPath = null);

/// <summary>
/// What the Git tool's <c>show</c> operation prints about a commit: the full hash (<c>%H</c>), the author name (<c>%an</c>, no mailmap), the author date as
/// strict ISO 8601 with the author's own offset (<c>%aI</c>) and the message (<c>%B</c>, raw; the tool trims and normalizes it when it builds its DTO).
/// </summary>
internal sealed record CommitHeader(string Sha, string AuthorName, string AuthorDate, string Message);

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

    /// <summary>
    /// The local and remote-tracking branches (<c>git branch --list --all</c>) in git's order (local branches by name, then remote-tracking ones; byte
    /// order of the ref name), symbolic rows such as <c>remotes/origin/HEAD -> origin/main</c> left out. The libgit2 reader throws
    /// <see cref="NotSupportedException"/> for what it cannot reproduce (a detached HEAD, which git lists as a pseudo-branch row, or a configured
    /// <c>branch.sort</c>). Null when git cannot answer (not a repository, git missing or timed out).
    /// </summary>
    IReadOnlyList<BranchRef>? ListBranches(string workingDirectory, TimeSpan cliTimeout);

    /// <summary>
    /// The stash entries (<c>git stash list --format=%gd%x09%h%x09%cI%x09%gs</c>), newest (stash@{0}) first; empty when there are none. The libgit2 reader
    /// throws <see cref="NotSupportedException"/> when it cannot learn git's abbreviation length (the probe failed, see <c>ShortHashLengths</c>), but only
    /// when there is an entry to abbreviate. Null when git cannot answer.
    /// </summary>
    IReadOnlyList<StashRef>? ListStashes(string workingDirectory, TimeSpan cliTimeout);

    /// <summary>The number of stash entries (<c>git stash list</c>); null when git cannot answer.</summary>
    int? CountStashes(string workingDirectory, TimeSpan cliTimeout);

    /// <summary>
    /// The file list of the Git tool's <c>diff</c> with <c>nameOnly</c> (<c>git -c core.quotePath=false diff --name-status</c> plus the target, then <c>-- paths</c>):
    /// <paramref name="target"/> is "working" (index to working tree), "staged" (<c>--cached</c>), a ref (against the working tree) or <c>refA..refB</c>, passed to git as
    /// given; untracked files are never listed; rows are in git's byte-wise path order. <paramref name="paths"/> null means no <c>--</c>, an empty list a bare <c>--</c>.
    /// The CLI reader answers every target. The libgit2 reader serves only "staged" and <c>refA..refB</c> and throws <see cref="NotSupportedException"/> for the others
    /// (a comparison against the working tree needs git's EOL-filtered hashing, which libgit2's diff does not reproduce) and for what else it cannot reproduce (anything
    /// that needs rename detection, submodules, unmerged paths, a path git would quote, a pathspec that is not a plain path, a revision form other than a simple ref, a
    /// repository where <c>diff.renames</c> or <c>diff.orderFile</c> is configured). Null when git cannot answer (not a repository, unknown revision, git missing or timed out).
    /// </summary>
    IReadOnlyList<NameStatusEntry>? GetNameStatus(string workingDirectory, string target, IReadOnlyList<string>? paths, TimeSpan cliTimeout);

    /// <summary>
    /// The file list of the Git tool's <c>show</c> with <c>nameOnly</c>: <c>git diff --name-status &lt;revision&gt;^ &lt;revision&gt;</c>, or against the empty tree for a root commit
    /// (a merge commit is diffed against its first parent). Same pathspec rules, decline rules and null conditions as <see cref="GetNameStatus"/>.
    /// </summary>
    IReadOnlyList<NameStatusEntry>? GetCommitNameStatus(string workingDirectory, string revision, IReadOnlyList<string>? paths, TimeSpan cliTimeout);

    /// <summary>
    /// The header of the Git tool's <c>show</c>: hash, author name, author date and message of <paramref name="revision"/> (<c>git show --no-patch</c> with those
    /// format fields). The libgit2 reader throws <see cref="NotSupportedException"/> for what it cannot reproduce (a revision that is not a plain commit, such as an
    /// annotated tag; a declared non-UTF-8 encoding; text git and .NET decode differently). Null when git cannot answer (not a repository, unknown revision).
    /// </summary>
    CommitHeader? GetCommitHeader(string workingDirectory, string revision, TimeSpan cliTimeout);

    /// <summary>Every commit reachable from HEAD (<c>git log</c> with no range), newest first, with its raw message.</summary>
    IReadOnlyList<CommitMessage>? GetCommitMessages(string workingDirectory, TimeSpan cliTimeout);

    /// <summary>
    /// The full sha of the newest commit among the first <paramref name="maxCommits"/> reachable from HEAD (newest first, as <c>git log -n</c>) whose
    /// trailer block carries <paramref name="key"/> with exactly <paramref name="value"/> (key case-insensitive, value trimmed). The CLI reader asks
    /// git itself (<c>%(trailers:key=...,valueonly,unfold)</c>); the libgit2 reader applies <see cref="GitCommitTrailers.GetValues"/>. Null when no
    /// such commit is in range or git cannot answer (not a repository, unborn HEAD, git missing or timed out): both mean "not found" to the caller.
    /// </summary>
    string? FindCommitByTrailer(string workingDirectory, string key, string value, int maxCommits, TimeSpan cliTimeout);

    /// <summary>
    /// The boolean git config value of <paramref name="key"/> as git resolves it (repo-local over global over system), or null when it is unset, not
    /// a boolean (<c>git config --type=bool</c> rejects it), or git cannot answer.
    /// </summary>
    bool? GetBoolConfig(string workingDirectory, string key, TimeSpan cliTimeout);

    /// <summary>
    /// HEAD and its parents (<c>git rev-list --parents -n 1 HEAD --</c>): the commit the Git tool reads before and after a commit or revert to prove which commit is its own.
    /// Null when HEAD does not resolve (an unborn branch, not a repository, git missing or timed out). The libgit2 reader throws <see cref="NotSupportedException"/> for a shallow
    /// clone, info/grafts or replace refs, where git rewrites the parent list.
    /// </summary>
    HeadWithParents? GetHeadWithParents(string workingDirectory, TimeSpan cliTimeout);

    /// <summary>
    /// The full hash MERGE_HEAD names (<c>git rev-parse --verify --quiet MERGE_HEAD^{commit}</c>): a merge is waiting to be concluded by the next commit. Null when no merge is in progress,
    /// when MERGE_HEAD names no commit, or when git cannot answer (not a repository, git missing or timed out). The libgit2 reader throws <see cref="NotSupportedException"/> for what it
    /// cannot reproduce: a MERGE_HEAD file that is not one plain lower-case 40-digit hash, a non-commit object (git peels a tag), replace refs, and a ref of another kind named
    /// MERGE_HEAD (git's ref-name rules would resolve a branch or tag of that name when the pseudo-ref file is absent).
    /// </summary>
    string? GetMergeHead(string workingDirectory, TimeSpan cliTimeout);

    /// <summary>
    /// Stages whole files the way the Git tool's <c>stage</c> does (<see cref="IndexStageMode"/> names the exact <c>git add</c> command); a partial (hunk) stage is not part
    /// of the seam. <paramref name="paths"/> are the literal repo-relative paths of the two <c>*Listed</c> modes (empty for the other two). Unlike the reads this
    /// WRITES the index and returns what the git command returned: a <see cref="GitRunResult"/> with exit code and stderr, so the caller maps a failure to its
    /// error text exactly as it did for the CLI. The CLI implementation runs the command; the libgit2 implementation only ever succeeds (exit 0) and throws
    /// <see cref="NotSupportedException"/> for everything it cannot reproduce byte for byte (see Libgit2GitReader.IndexWrites.cs), including a held <c>index.lock</c>, so git
    /// answers and reports its own error. A caller token cancels a running CLI process.
    /// </summary>
    GitRunResult StageWholeFiles(string workingDirectory, IndexStageMode mode, IReadOnlyList<string> paths, TimeSpan cliTimeout, CancellationToken cancellationToken);

    /// <summary>
    /// Resets index entries to HEAD like the Git tool's <c>unstage</c>: <c>git reset -- paths</c> (<c>git reset</c> for an empty list), the working tree untouched.
    /// Same contract as <see cref="StageWholeFiles"/>; the libgit2 implementation declines the whole-index form (git also moves ORIG_HEAD, writes a reflog entry
    /// and clears merge state) and every pathspec that is not a plain literal path.
    /// </summary>
    GitRunResult UnstagePaths(string workingDirectory, IReadOnlyList<string> paths, TimeSpan cliTimeout, CancellationToken cancellationToken);
}
/// <summary>
/// The four <c>git add</c> forms the Git tool's whole-file <c>stage</c> runs (see <see cref="IGitReader.StageWholeFiles"/>). DTO-EXEMPT: enum, not a data type.
/// </summary>
internal enum IndexStageMode
{
    /// <summary><c>git --literal-pathspecs add -u</c>: modifications and deletions of every tracked file, no paths.</summary>
    UpdateTracked,

    /// <summary><c>git add -A</c>: everything, untracked files included, no paths.</summary>
    AddAll,

    /// <summary><c>git --literal-pathspecs add -u -- paths</c>: modifications and deletions of tracked files at or under the paths.</summary>
    UpdateListed,

    /// <summary><c>git --literal-pathspecs add -- paths</c>: modifications, deletions and new (non-ignored) files at or under the paths.</summary>
    AddListed,
}
/// <summary>
/// HEAD's full hash and its parents' full hashes, as one <c>git rev-list --parents -n 1 HEAD</c> read, so the pair is consistent. <see cref="ParentShas"/> is empty for a root commit
/// and has two or more entries for a merge commit, in parent order.
/// </summary>
internal sealed record HeadWithParents(string Sha, IReadOnlyList<string> ParentShas);
