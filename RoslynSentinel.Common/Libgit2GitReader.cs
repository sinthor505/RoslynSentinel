using System.Text;
using LibGit2Sharp;

namespace RoslynSentinel.Common;

/// <summary>
/// <see cref="IGitReader"/> over LibGit2Sharp (in-process, no process spawn). A <see cref="Repository"/> is opened per
/// call inside <c>using</c> and never cached: an open repository holds pack-file handles on Windows, which blocks
/// deleting a worktree or directory. The repository is found by walking up from the working directory
/// (<see cref="Repository.Discover"/>), so a subdirectory and a linked worktree work as they do for the CLI; a
/// directory that is not inside a repository returns null (the CLI would exit non-zero too).
/// <para>
/// Native failures (<see cref="LibGit2SharpException"/>, <see cref="DllNotFoundException"/>,
/// <see cref="TypeInitializationException"/>) propagate to <see cref="FallbackGitReader"/>, which falls back to the CLI
/// and records it. A request this implementation cannot reproduce faithfully throws <see cref="NotSupportedException"/> for the same reason.
/// </para>
/// <para>
/// Accepted differences from the CLI implementation (parity-tested in GitReaderParityTests):
/// (1) Short hashes: git's default abbreviation grows with the repository's object count, libgit2's
///     (<see cref="ObjectDatabase.ShortenObjectId(GitObject, int)"/>) stays at 7 until a prefix is ambiguous. Both are
///     at least 7 characters and unique, and they agree on a small repository; they can differ on a large one.
/// (2) Conflicted status entries are reported as <c>UU</c> whatever the conflict kind (git distinguishes AA, DD, AU, ...).
/// (3) Pathspec magic (a leading ':', as in ':(top)' or ':!x') is not supported and falls back to the CLI; plain paths and
///     globs are evaluated by libgit2's own matcher, which matches git's for the plain-path and '*' glob cases the callers use.
/// (4) Merge commits: git log shows no diff for a merge, so a merge reports no changed paths; the same here.
/// (5) Status and EOL handling use libgit2's own reading of config (system/global/local) and .gitattributes, not git's:
///     a setting only git honors could change the answer (none is known; the CRLF/autocrlf case is parity-tested). Repo
///     formats libgit2 cannot open throw and fall back.
/// (7) Commit order is date order with a parent never before its child (libgit2's Topological|Time); git's default is
///     date order. They agree except for same-second commits on parallel branches (git breaks such ties by traversal
///     order, libgit2 differently), so a log range of commits made within one second on different branches may be listed in a different order.
/// (6) A blob that is a directory in HEAD, or a missing path, is null here; the CLI reader uses cat-file blob, which also fails for a directory.
/// (8) GetRepositoryStatus: a repository with submodules is answered by the CLI (a dirty submodule is not reproduced); a HEAD outside refs/heads does the same,
///     and so does a possible intent-to-add file (a new-in-index, modified-in-workdir entry holding the empty blob).
///     The short branch name follows git's ambiguity rule for the common cases (a tag, remote or refs/ entry of the same name gives "heads/name").
///     A path containing a line feed is returned verbatim here; the Git tool's old CLI path re-joined it with the platform newline.
/// </para>
/// </summary>
internal sealed class Libgit2GitReader : IGitReader
{
    private const int MinimumShortHashLength = 7;

    // The empty blob in a SHA-1 repository, the id git gives an intent-to-add index entry.
    private const string EmptyBlobSha = "e69de29bb2d1d6434b8b29ae775ad8c2e48c5391";

    public string? GetHeadSha(string workingDirectory, bool shortForm, TimeSpan cliTimeout)
    {
        using var repo = Open(workingDirectory);
        var tip = repo?.Head.Tip;
        if (repo is null || tip is null)
        {
            return null;
        }

        return shortForm ? repo.ObjectDatabase.ShortenObjectId(tip, MinimumShortHashLength) : tip.Sha;
    }

    public IReadOnlyList<ChangedCommit>? GetCommitsSince(string workingDirectory, string sha, TimeSpan cliTimeout)
    {
        using var repo = Open(workingDirectory);
        var head = repo?.Head.Tip;
        if (repo is null || head is null)
        {
            return null;
        }

        // A sha that is not a commit (or is unknown) is "no answer", like the CLI's non-zero exit.
        if (repo.Lookup<Commit>(sha) is not { } since)
        {
            return null;
        }

        var filter = new CommitFilter
        {
            IncludeReachableFrom = head,
            ExcludeReachableFrom = since,
            // Newest first by commit date, a parent never before its child (git log's default order, and --date-order's guarantee).
            // Time alone is not enough: libgit2 breaks same-second ties arbitrarily and can list a parent before its child.
            // Remaining difference: same-second commits on parallel branches may be ordered differently from git.
            SortBy = CommitSortStrategies.Topological | CommitSortStrategies.Time,
        };

        var commits = new List<ChangedCommit>();
        foreach (var commit in repo.Commits.QueryBy(filter))
        {
            commits.Add(new ChangedCommit(
                repo.ObjectDatabase.ShortenObjectId(commit, MinimumShortHashLength),
                commit.MessageShort,
                ChangedPaths(repo, commit)));
        }

        return commits;
    }

    public IReadOnlyList<WorkingTreeChange>? GetStatus(string workingDirectory, IReadOnlyList<string>? pathspecs, TimeSpan cliTimeout)
    {
        using var repo = Open(workingDirectory);
        if (repo is null)
        {
            return null;
        }

        var options = new StatusOptions
        {
            Show = StatusShowOption.IndexAndWorkDir,
            // libgit2 defaults IncludeIgnored to true; git status --porcelain omits ignored files.
            IncludeIgnored = false,
            IncludeUntracked = true,
            // False collapses a wholly untracked directory to "dir/", as git does by default.
            RecurseUntrackedDirs = false,
            RecurseIgnoredDirs = false,
            IncludeUnaltered = false,
            // Renames between HEAD and the index only: what git status reports.
            DetectRenamesInIndex = true,
            DetectRenamesInWorkDir = false,
        };

        if (pathspecs is { Count: > 0 })
        {
            // Git pathspecs are relative to the working directory, libgit2's to the repository root.
            var prefix = RelativeToWorkdir(repo, workingDirectory);
            options.PathSpec = [.. pathspecs.Select(spec => RootRelativePathspec(prefix, spec))];
        }

        var tracked = new List<WorkingTreeChange>();
        var untracked = new List<WorkingTreeChange>();
        foreach (var entry in repo.RetrieveStatus(options))
        {
            AddRows(entry, tracked, untracked);
        }

        // git prints tracked changes first, then untracked entries; libgit2 interleaves them by path.
        tracked.AddRange(untracked);
        return tracked;
    }

    // git sorts status rows by path in byte (UTF-8) order; libgit2 follows the file system's case rules, which are case-insensitive on Windows.
    private static readonly Comparer<string> Utf8PathOrder = Comparer<string>.Create(
        (x, y) => Encoding.UTF8.GetBytes(x).AsSpan().SequenceCompareTo(Encoding.UTF8.GetBytes(y)));

    public WorkingTreeStatus? GetRepositoryStatus(string workingDirectory, TimeSpan cliTimeout)
    {
        using var repo = Open(workingDirectory);
        if (repo is null)
        {
            return null;
        }

        // libgit2 and git can disagree on a dirty submodule (new commits, modified or untracked content): the CLI answers for any repository that has one.
        if (repo.Submodules.Any())
        {
            throw new NotSupportedException("The repository has submodules; libgit2's submodule status is not reproduced.");
        }

        var options = new StatusOptions
        {
            Show = StatusShowOption.IndexAndWorkDir,
            IncludeIgnored = false,
            IncludeUntracked = true,
            // --untracked-files=all: every untracked file individually, not a collapsed "dir/".
            RecurseUntrackedDirs = true,
            RecurseIgnoredDirs = false,
            IncludeUnaltered = false,
            DetectRenamesInIndex = true,
            DetectRenamesInWorkDir = false,
        };

        var tracked = new List<WorkingTreeChange>();
        var untracked = new List<WorkingTreeChange>();
        foreach (var entry in repo.RetrieveStatus(options))
        {
            // An intent-to-add file (git add -N) is " A" in git but added-and-modified here, and libgit2sharp does not expose the
            // flag. Its index entry is the empty blob, so a new-in-index and modified-in-workdir entry holding it is left to the CLI
            // (a real empty file staged and then edited lands there too, which is merely slower, never wrong).
            if (entry.State.HasFlag(FileStatus.NewInIndex) && entry.State.HasFlag(FileStatus.ModifiedInWorkdir)
                && repo.Index[entry.FilePath]?.Id.Sha == EmptyBlobSha)
            {
                throw new NotSupportedException("A new-in-index file holding the empty blob may be intent-to-add, which libgit2 reports differently from git.");
            }

            AddRows(entry, tracked, untracked);
        }

        var rows = new List<WorkingTreeChange>(tracked.Count + untracked.Count);
        rows.AddRange(tracked.OrderBy(change => change.Path, Utf8PathOrder));
        rows.AddRange(untracked.OrderBy(change => change.Path, Utf8PathOrder));
        return new WorkingTreeStatus(HeadBranchName(repo), rows, GitOperationState.Detect(repo.Info.Path));
    }

    /// <summary>
    /// What <c>git rev-parse --abbrev-ref HEAD</c> prints: the short branch name ("heads/name" when a tag or another ref would
    /// make the short name ambiguous), "HEAD" when detached, null for an unborn branch.
    /// </summary>
    private static string? HeadBranchName(Repository repo)
    {
        if (repo.Info.IsHeadDetached)
        {
            return "HEAD";
        }

        var head = repo.Head;
        if (head.Tip is null)
        {
            return null;
        }

        const string headsPrefix = "refs/heads/";
        if (!head.CanonicalName.StartsWith(headsPrefix, StringComparison.Ordinal))
        {
            throw new NotSupportedException($"HEAD points at '{head.CanonicalName}', outside refs/heads.");
        }

        var name = head.CanonicalName[headsPrefix.Length..];
        string[] competing = [$"refs/{name}", $"refs/tags/{name}", $"refs/remotes/{name}", $"refs/remotes/{name}/HEAD"];
        return competing.Any(reference => repo.Refs[reference] is not null) ? "heads/" + name : name;
    }

    public string? ReadBlobAtHead(string workingDirectory, string relativePath, TimeSpan cliTimeout)
    {
        using var repo = Open(workingDirectory);
        var tip = repo?.Head.Tip;
        if (repo is null || tip is null)
        {
            return null;
        }

        var prefix = RelativeToWorkdir(repo, workingDirectory);
        var repoPath = prefix.Length == 0 ? relativePath : $"{prefix}/{relativePath}";
        var normalized = NormalizeRepoPath(repoPath);
        if (normalized is null || tip[normalized]?.Target is not Blob blob)
        {
            return null;
        }

        using var stream = blob.GetContentStream();
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        var bytes = buffer.ToArray();

        // Same decoding as GitProcess: UTF-8 with a leading byte-order mark dropped.
        var preamble = Encoding.UTF8.Preamble;
        var skip = bytes.AsSpan().StartsWith(preamble) ? preamble.Length : 0;
        return Encoding.UTF8.GetString(bytes, skip, bytes.Length - skip);
    }

    public IReadOnlyList<string>? ListTrackedPaths(string workingDirectory, TimeSpan cliTimeout)
    {
        using var repo = Open(workingDirectory);
        if (repo is null)
        {
            return null;
        }

        // ls-files lists the index under the working directory, relative to it; .gitignore plays no part.
        var prefix = RelativeToWorkdir(repo, workingDirectory);
        var leading = prefix.Length == 0 ? string.Empty : prefix + "/";
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var paths = new List<string>();
        foreach (var entry in repo.Index)
        {
            var path = entry.Path.Replace('\\', '/');
            if (path.StartsWith(leading, StringComparison.Ordinal) && seen.Add(path))
            {
                paths.Add(path[leading.Length..]);
            }
        }

        return paths;
    }

    public IReadOnlyList<CommitMessage>? GetCommitMessages(string workingDirectory, TimeSpan cliTimeout)
    {
        using var repo = Open(workingDirectory);
        var head = repo?.Head.Tip;
        if (repo is null || head is null)
        {
            return null;
        }

        var filter = new CommitFilter { IncludeReachableFrom = head, SortBy = CommitSortStrategies.Topological | CommitSortStrategies.Time };
        // Commit.Message is the raw message as stored, like git's %B.
        return [.. repo.Commits.QueryBy(filter).Select(commit => new CommitMessage(commit.Sha, commit.Message))];
    }

    /// <summary>Opens the repository containing <paramref name="workingDirectory"/>; null when there is none.</summary>
    private static Repository? Open(string workingDirectory)
    {
        if (!Directory.Exists(workingDirectory))
        {
            return null;
        }

        var gitDirectory = Repository.Discover(workingDirectory);
        return gitDirectory is null ? null : new Repository(gitDirectory);
    }

    /// <summary>
    /// <paramref name="directory"/> relative to the repository's working tree, with '/' separators ("" at the root).
    /// Throws <see cref="NotSupportedException"/> (so the CLI answers) for a bare repository or a directory this
    /// comparison cannot place under the working tree (for example one reached through a different alias of the same folder).
    /// </summary>
    private static string RelativeToWorkdir(Repository repo, string directory)
    {
        var workdir = repo.Info.WorkingDirectory;
        if (workdir is null)
        {
            throw new NotSupportedException("The repository has no working tree.");
        }

        var relative = Path.GetRelativePath(workdir, Path.GetFullPath(directory)).Replace('\\', '/');
        if (relative == ".")
        {
            return string.Empty;
        }

        if (relative == ".." || relative.StartsWith("../", StringComparison.Ordinal) || Path.IsPathRooted(relative))
        {
            throw new NotSupportedException($"'{directory}' is not under the repository working tree '{workdir}'.");
        }

        return relative;
    }

    /// <summary>Resolves "./" and "../" segments in a repo-relative path; null when it climbs out of the repository.</summary>
    private static string? NormalizeRepoPath(string repoPath)
    {
        var segments = new List<string>();
        foreach (var segment in repoPath.Replace('\\', '/').Split('/'))
        {
            if (segment is "" or ".")
            {
                continue;
            }

            if (segment == "..")
            {
                if (segments.Count == 0)
                {
                    return null;
                }

                segments.RemoveAt(segments.Count - 1);
                continue;
            }

            segments.Add(segment);
        }

        return segments.Count == 0 ? null : string.Join('/', segments);
    }

    private static string RootRelativePathspec(string prefix, string spec)
    {
        if (spec.StartsWith(':'))
        {
            throw new NotSupportedException($"Pathspec magic is not supported by the libgit2 reader: '{spec}'.");
        }

        return prefix.Length == 0 ? spec : $"{prefix}/{spec}";
    }

    private static List<string> ChangedPaths(Repository repo, Commit commit)
    {
        // git log shows no diff for a merge commit.
        var parents = commit.Parents.ToList();
        if (parents.Count > 1)
        {
            return [];
        }

        // A root commit diffs against the empty tree. No rename detection: a rename lists both paths.
        // (CompareOptions' default similarity detects renames, so it is switched off explicitly.)
        var options = new CompareOptions { Similarity = SimilarityOptions.None };
        using var changes = repo.Diff.Compare<TreeChanges>(parents.Count == 0 ? null : parents[0].Tree, commit.Tree, options);
        return [.. changes.Select(change => change.Path.Replace('\\', '/'))];
    }

    private static void AddRows(StatusEntry entry, List<WorkingTreeChange> tracked, List<WorkingTreeChange> untracked)
    {
        var state = entry.State;
        if (state.HasFlag(FileStatus.Ignored) || state == FileStatus.Unaltered || state == FileStatus.Nonexistent)
        {
            return;
        }

        var path = entry.FilePath.Replace('\\', '/');
        if (state.HasFlag(FileStatus.Conflicted))
        {
            tracked.Add(new WorkingTreeChange(path, null, 'U', 'U'));
            return;
        }

        var index = IndexLetter(state);
        var workTree = WorkTreeLetter(state);
        var original = entry.HeadToIndexRenameDetails?.OldFilePath?.Replace('\\', '/');

        // A path deleted from the index and present again untracked is two porcelain rows ("D  f" and "?? f"), one libgit2 entry.
        if (state.HasFlag(FileStatus.NewInWorkdir) && index != ' ')
        {
            tracked.Add(new WorkingTreeChange(path, original, index, ' '));
            untracked.Add(new WorkingTreeChange(path, null, '?', '?'));
            return;
        }

        if (state.HasFlag(FileStatus.NewInWorkdir))
        {
            untracked.Add(new WorkingTreeChange(path, null, '?', '?'));
            return;
        }

        tracked.Add(new WorkingTreeChange(path, original, index, workTree));
    }

    private static char IndexLetter(FileStatus state) => state switch
    {
        _ when state.HasFlag(FileStatus.RenamedInIndex) => 'R',
        _ when state.HasFlag(FileStatus.NewInIndex) => 'A',
        _ when state.HasFlag(FileStatus.ModifiedInIndex) => 'M',
        _ when state.HasFlag(FileStatus.DeletedFromIndex) => 'D',
        _ when state.HasFlag(FileStatus.TypeChangeInIndex) => 'T',
        _ => ' ',
    };

    private static char WorkTreeLetter(FileStatus state) => state switch
    {
        _ when state.HasFlag(FileStatus.RenamedInWorkdir) => 'R',
        _ when state.HasFlag(FileStatus.ModifiedInWorkdir) => 'M',
        _ when state.HasFlag(FileStatus.DeletedFromWorkdir) => 'D',
        _ when state.HasFlag(FileStatus.TypeChangeInWorkdir) => 'T',
        _ => ' ',
    };
}
