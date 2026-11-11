using System.Text;
using System.Buffers.Binary;
using System.Numerics;
using System.Text.RegularExpressions;
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
/// (1) Short hashes: git's default abbreviation grows with the repository's object count (and follows core.abbrev); libgit2's
///     (<see cref="ObjectDatabase.ShortenObjectId(GitObject, int)"/>) has no such rule. The minimum length handed to it is therefore the one
///     <c>git rev-parse --short HEAD</c> printed (see <see cref="ShortHashLengths"/>), cached per git common directory and refreshed on every
///     LoadSolution, so the two agree. Accepted staleness: the cached length can lag git by one digit until the next LoadSolution after the
///     object count crosses a threshold (or core.abbrev is edited); a failed probe declines to the CLI.
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
/// (9) GetLog is answered by libgit2 only where git's output can be reproduced exactly; everything else is declined (NotSupportedException) and the CLI answers:
///     pickaxe (-S, -G) and --follow (no libgit2 equivalent; a reimplementation would differ on binary files, textconv, regex dialect and rename heuristics);
///     revisions other than a plain ref, hash or HEAD~N/^N (ranges, :/text, @{...}); pathspecs with glob characters, magic or relative segments;
///     a path-limited walk that reaches a merge commit (git's history simplification is not reproduced); shallow clones, info/grafts and refs/replace;
///     commits with a non-UTF-8 encoding header, text with U+FFFD (invalid UTF-8) or the field separators the CLI parser uses.
///     Reproduced: git's default order (a commit-date queue, ties in insertion order, not libgit2's sorter), %h's abbreviation length
///     (probed from git once per repository, so core.abbrev, any object count, a multi-pack-index and alternates are all covered; see (1)), %aI (author offset,
///     a zero offset spelled "Z" or "+00:00" as the installed git does, learned from one CLI call per process), and case-sensitive pathspec matching
///     (git's tree diff ignores core.ignorecase) by comparing the tree entry id at each pathspec with the first parent's. A path-limited walk is declined
///     after 2,000 commits examined. A custom GIT_OBJECT_DIRECTORY or GIT_ALTERNATE_OBJECT_DIRECTORIES is declined.
///     The short branch name follows git's ambiguity rule for the common cases (a tag, remote or refs/ entry of the same name gives "heads/name").
///     A path containing a line feed is returned verbatim here; the Git tool's old CLI path re-joined it with the platform newline.
/// </para>
/// </summary>
internal sealed class Libgit2GitReader : IGitReader
{
    private readonly ShortHashLengths _lengths;

    internal Libgit2GitReader() : this(ShortHashLengths.Shared)
    {
    }

    /// <param name="lengths">Where git's per-repository abbreviation length is cached and probed (a test injects a counting probe).</param>
    internal Libgit2GitReader(ShortHashLengths lengths)
    {
        _lengths = lengths;
    }

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

        return shortForm ? ShortId(repo, tip, ShortLength(repo, workingDirectory, cliTimeout)) : tip.Sha;
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
        // Probed on the first commit only (an empty range needs no abbreviation).
        int? length = null;
        foreach (var commit in repo.Commits.QueryBy(filter))
        {
            commits.Add(new ChangedCommit(
                ShortId(repo, commit, length ??= ShortLength(repo, workingDirectory, cliTimeout)),
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

    /// <summary>
    /// Histories git walks differently from a plain parent chain: shallow clones, info/grafts and replace refs (loose, or named in packed-refs).
    /// </summary>
    private static void RequirePlainHistory(Repository repo)
    {
        var common = CommonDirectory(repo);
        var packedRefs = Path.Combine(common, "packed-refs");
        if (repo.Info.IsShallow
            || File.Exists(Path.Combine(common, "info", "grafts"))
            || Directory.Exists(Path.Combine(common, "refs", "replace"))
            || (File.Exists(packedRefs) && File.ReadLines(packedRefs).Any(line => line.Contains(" refs/replace/", StringComparison.Ordinal))))
        {
            throw new NotSupportedException("The repository is shallow or has grafts or replace refs, so git walks its history.");
        }
    }

    /// <summary>The git directory shared by all worktrees (the per-worktree directory itself unless it has a commondir file).</summary>
    private static string CommonDirectory(Repository repo)
    {
        var gitDirectory = repo.Info.Path;
        var commonFile = Path.Combine(gitDirectory, "commondir");
        return File.Exists(commonFile)
            ? Path.GetFullPath(Path.Combine(gitDirectory, File.ReadAllText(commonFile).Trim()))
            : gitDirectory;
    }

    /// <summary>
    /// The hex length git's <c>%h</c> starts from for this repository: what <c>git rev-parse --short HEAD</c> printed (see <see cref="ShortHashLengths"/>:
    /// cached per git common directory, refreshed on every LoadSolution, probed lazily here for a repository reached another way). It covers
    /// <c>core.abbrev</c> (a number, a false-like value, <c>auto</c>), the object count, a multi-pack-index and alternates alike. The caller makes the
    /// result unique with <see cref="ObjectDatabase.ShortenObjectId(GitObject, int)"/>, which extends it exactly as git does.
    /// Accepted staleness: the length can lag git by one digit until the next LoadSolution after the object count crosses a threshold.
    /// </summary>
    /// <exception cref="NotSupportedException">The object store is relocated by environment variables, or the probe failed: git answers.</exception>
    private int ShortLength(Repository repo, string workingDirectory, TimeSpan cliTimeout)
    {
        // libgit2 does not read these, so git would walk a different object store; this guard predates the probe and is about the walk, not the length.
        if (Environment.GetEnvironmentVariable("GIT_OBJECT_DIRECTORY") is not null
            || Environment.GetEnvironmentVariable("GIT_ALTERNATE_OBJECT_DIRECTORIES") is not null)
        {
            throw new NotSupportedException("The object store location is overridden by an environment variable, so git reads it.");
        }

        return _lengths.Get(CommonDirectory(repo), workingDirectory, cliTimeout);
    }

    /// <summary>A short id at least <paramref name="length"/> hex digits long; ShortenObjectId returns an all-zero id for a full-length request, where git prints the whole hash.</summary>
    private static string ShortId(Repository repo, GitObject target, int length)
    {
        if (length >= 40)
        {
            return target.Sha;
        }

        if (!string.Equals(repo.Config.Get<string>("core.abbrev")?.Value, "auto", StringComparison.OrdinalIgnoreCase))
        {
            return repo.ObjectDatabase.ShortenObjectId(target, length);
        }

        for (var digits = length; digits < 40; digits++)
        {
            var prefix = target.Sha[..digits];
            try
            {
                if (repo.Lookup(prefix) is not null)
                {
                    return prefix;
                }
            }
            catch (AmbiguousSpecificationException)
            {
                // Another object shares this prefix: one more digit, exactly as git extends it.
            }
        }

        return target.Sha;
    }

    /// <summary>
    /// Re-probes git's abbreviation length for the repository of <paramref name="workingDirectory"/> and replaces the cached entry (the warmup's
    /// second task calls this on every LoadSolution). Null when there is no repository or the probe failed.
    /// </summary>
    internal int? RefreshShortHashLength(string workingDirectory, TimeSpan cliTimeout)
    {
        using var repo = Open(workingDirectory);
        return repo is null ? null : _lengths.Refresh(CommonDirectory(repo), workingDirectory, cliTimeout);
    }

    /// <summary>
    /// git log's default walk: a queue ordered by committer date (a new entry goes after every entry with the same or a later date),
    /// popping the front and queueing its unseen parents. Not libgit2's sorter, which breaks same-second ties differently.
    /// </summary>
    private List<LogCommit> WalkLog(Repository repo, Commit tip, int maxCount, List<(string Path, bool DirectoryOnly)>? specs, string workingDirectory, TimeSpan cliTimeout)
    {
        var filtered = specs is { Count: > 0 };
        var abbreviation = ShortLength(repo, workingDirectory, cliTimeout);
        var commits = new List<LogCommit>();
        var seen = new HashSet<string>(StringComparer.Ordinal) { tip.Sha };
        var queue = new List<Commit> { tip };
        var examined = 0;
        while (queue.Count > 0 && commits.Count < maxCount)
        {
            var commit = queue[0];
            queue.RemoveAt(0);
            if (filtered && ++examined > MaxPathLimitedCommitsExamined)
            {
                throw new NotSupportedException($"A path-limited walk passed {MaxPathLimitedCommitsExamined} commits without finishing; git's own walk is faster from here, so git answers.");
            }
            var parents = commit.Parents.ToList();
            if (filtered && parents.Count > 1)
            {
                throw new NotSupportedException("A path-limited walk reached a merge commit; git's history simplification is not reproduced, so git answers.");
            }

            // Linear history: a commit is listed when it changes something under a pathspec (no rename detection), a root commit when it adds something.
            var parentTree = filtered && parents.Count > 0 ? parents[0].Tree : null;
            if (!filtered || specs!.Any(spec => ChangesPathspec(commit.Tree, parentTree, spec.Path, spec.DirectoryOnly)))
            {
                commits.Add(ToLogCommit(repo, commit, abbreviation, workingDirectory, cliTimeout));
            }

            foreach (var parent in parents)
            {
                if (seen.Add(parent.Sha))
                {
                    InsertByCommitDate(queue, parent);
                }
            }
        }

        return commits;
    }

    private static void InsertByCommitDate(List<Commit> queue, Commit commit)
    {
        var date = commit.Committer.When.ToUnixTimeSeconds();
        var index = queue.FindIndex(queued => queued.Committer.When.ToUnixTimeSeconds() < date);
        if (index < 0)
        {
            queue.Add(commit);
        }
        else
        {
            queue.Insert(index, commit);
        }
    }

    private static LogCommit ToLogCommit(Repository repo, Commit commit, int abbreviation, string workingDirectory, TimeSpan cliTimeout)
    {
        // Encoding header, invalid UTF-8 and the CLI parser's separator characters: git answers those (see RequireStableCommitText).
        RequireStableCommitText(commit);
        var author = commit.Author;
        var message = commit.Message;

        // git's strict ISO date: the author's own offset, with a zero offset spelled the way the installed git spells it.
        var date = StrictIsoDate(author.When, workingDirectory, cliTimeout, commit.Sha, "%aI");

        var shortSha = ShortId(repo, commit, abbreviation);
        return new LogCommit(commit.Sha, shortSha, author.Name, date, message);
    }

    private static List<(string Path, bool DirectoryOnly)> ParseLogPathspecs(IReadOnlyList<string> paths)
    {
        var specs = new List<(string Path, bool DirectoryOnly)>();
        foreach (var raw in paths)
        {
            var directoryOnly = raw.EndsWith('/');
            var spec = directoryOnly ? raw[..^1] : raw;
            // Plain relative paths only: glob characters, pathspec magic, a drive or backslash, and ".", ".." or empty segments need git's pathspec rules.
            if (spec.Length == 0 || spec.IndexOfAny(['*', '?', '[', ']', '\\', ':', '!', '^']) >= 0 || spec.Split('/').Any(segment => segment is "" or "." or ".."))
            {
                throw new NotSupportedException($"Pathspec '{raw}' needs git's pathspec rules, so git answers.");
            }

            specs.Add((spec, directoryOnly));
        }

        return specs;
    }

    /// <summary>
    /// A commit changes a pathspec when the entry at that path (a file, or a directory standing for everything under it) differs from its first
    /// parent's, or from nothing for a root commit: git's pathspec-limited tree diff, answered by comparing one entry id per tree instead of
    /// diffing whole trees. Case-sensitive even with core.ignorecase: git's tree diff only folds case for the :(icase) magic, which
    /// ParseLogPathspecs sends to git.
    /// </summary>
    private static bool ChangesPathspec(Tree tree, Tree? parentTree, string path, bool directoryOnly) =>
        EntryIdAt(tree, path, directoryOnly) != (parentTree is null ? null : EntryIdAt(parentTree, path, directoryOnly));

    /// <summary>The object id at <paramref name="path"/>, or null when nothing is there (for a trailing-slash pathspec, when it is not a directory).</summary>
    private static string? EntryIdAt(Tree tree, string path, bool directoryOnly)
    {
        var entry = tree[path];
        return entry is null || (directoryOnly && entry.TargetType != TreeEntryTargetType.Tree) ? null : entry.Target.Sha;
    }

    // A revision this reader resolves itself: a ref name, hash or HEAD with optional ~N / ^N suffixes. Ranges, :/text, @{...} and globs go to git.
    // ".." would match inside a name here, so it is rejected separately.
    private static readonly Regex SimpleRevision = new(@"^[A-Za-z0-9_][A-Za-z0-9_./+\-]*([~^][0-9]*)*$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    public IReadOnlyList<LogCommit>? GetLog(string workingDirectory, int maxCount, string? revision, IReadOnlyList<string>? paths, string? pickaxe, bool pickaxeRegex, bool follow, TimeSpan cliTimeout)
    {
        if (!string.IsNullOrWhiteSpace(pickaxe))
        {
            throw new NotSupportedException("libgit2 has no pickaxe search; -S and -G differ on binary files, textconv and regex dialect, so git answers.");
        }

        if (follow)
        {
            throw new NotSupportedException("git log --follow is a rename-detection heuristic libgit2 does not offer, so git answers.");
        }

        var specs = paths is null ? null : ParseLogPathspecs(paths);
        var hasRevision = !string.IsNullOrWhiteSpace(revision);
        if (hasRevision && (revision!.Contains("..", StringComparison.Ordinal) || !SimpleRevision.IsMatch(revision)))
        {
            throw new NotSupportedException($"'{revision}' is not a plain ref, hash or HEAD~N form, so git resolves it.");
        }

        using var repo = Open(workingDirectory);
        if (repo is null)
        {
            return null;
        }

        RequirePlainHistory(repo);

        Commit? tip;
        if (hasRevision)
        {
            // Without "--" git refuses a name that is both a revision and a file (and the CLI error text must stay the tool's); libgit2 would resolve it.
            if (paths is null && (File.Exists(Path.Combine(workingDirectory, revision!)) || Directory.Exists(Path.Combine(workingDirectory, revision!))))
            {
                throw new NotSupportedException($"'{revision}' is also a path in the working directory; git decides whether that is ambiguous.");
            }

            // Unknown revision: null, as the CLI's non-zero exit, so the tool reports git's own message.
            tip = repo.Lookup<Commit>(revision!);
        }
        else
        {
            tip = repo.Head.Tip; // null on an unborn branch
        }

        if (tip is null)
        {
            return null;
        }

        try
        {
            return WalkLog(repo, tip, maxCount, specs, workingDirectory, cliTimeout);
        }
        catch (ArgumentOutOfRangeException ex)
        {
            throw new NotSupportedException("A commit date or UTC offset is outside .NET's range, so git answers.", ex);
        }
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

    /// <summary>
    /// The pathspecs the two path-matching reads accept: ones git reads the same literally and after its own normalisation. A path git rewrites
    /// ("./x", "a//b", "a/../b", an absolute or drive path, a trailing slash) or rejects (empty, outside the repository) is left to git.
    /// </summary>
    private static void RequireCleanRelativePaths(IReadOnlyList<string> paths)
    {
        foreach (var path in paths)
        {
            if (path.Length == 0 || path.Contains('\\') || path.Contains(':') || path[0] == '/' || path.Split('/').Any(segment => segment is "" or "." or ".."))
            {
                throw new NotSupportedException($"'{path}' is not a clean repo-relative path, so git normalises or rejects it.");
            }
        }
    }

    /// <summary>True when <paramref name="path"/> is one of <paramref name="wanted"/> or lies under one of them as a directory.</summary>
    private static bool IsAtOrUnder(string path, HashSet<string> wanted)
    {
        if (wanted.Contains(path))
        {
            return true;
        }

        for (var slash = path.LastIndexOf('/'); slash > 0; slash = path.LastIndexOf('/', slash - 1))
        {
            if (wanted.Contains(path[..slash]))
            {
                return true;
            }
        }

        return false;
    }

    public IReadOnlyList<string>? ListIndexPathsMatching(string workingDirectory, IReadOnlyList<string> paths, TimeSpan cliTimeout)
    {
        RequireCleanRelativePaths(paths);
        using var repo = Open(workingDirectory);
        if (repo is null)
        {
            return null;
        }

        // git reads the pathspecs relative to the directory it runs in; the Git tool always passes the repository root.
        if (RelativeToWorkdir(repo, workingDirectory).Length != 0)
        {
            throw new NotSupportedException("The working directory is below the repository root, so git resolves the pathspecs against it.");
        }

        // Literal and case-sensitive, even with core.ignorecase (git's index pathspec match only folds case for the :(icase) magic).
        var wanted = new HashSet<string>(paths, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var matches = new List<string>();
        foreach (var entry in repo.Index)
        {
            // An unmerged path has one entry per stage; it is listed once.
            if (IsAtOrUnder(entry.Path, wanted) && seen.Add(entry.Path))
            {
                matches.Add(entry.Path);
            }
        }

        return matches;
    }

    public IReadOnlyList<string>? ListHeadPathsMatching(string workingDirectory, IReadOnlyList<string> paths, TimeSpan cliTimeout)
    {
        RequireCleanRelativePaths(paths);
        using var repo = Open(workingDirectory);
        if (repo is null)
        {
            return null;
        }

        if (RelativeToWorkdir(repo, workingDirectory).Length != 0)
        {
            throw new NotSupportedException("The working directory is below the repository root, so git resolves the pathspecs against it.");
        }

        // ls-tree HEAD fails on an unborn branch; null, as the CLI's non-zero exit.
        var tip = repo.Head.Tip;
        if (tip is null)
        {
            return null;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var matches = new List<string>();
        foreach (var path in paths)
        {
            // Null for a missing path, or one through a file or a submodule: ls-tree lists nothing for those either.
            var entry = tip.Tree[path];
            if (entry is not null)
            {
                AddHeadFiles(entry, path, seen, matches);
            }
        }

        return matches;
    }

    public IReadOnlyList<string>? FindIgnoredPaths(string workingDirectory, IReadOnlyList<string> paths, TimeSpan cliTimeout)
    {
        RequireCleanRelativePaths(paths);
        using var repo = Open(workingDirectory);
        if (repo is null)
        {
            return null;
        }

        if (RelativeToWorkdir(repo, workingDirectory).Length != 0)
        {
            throw new NotSupportedException("The working directory is below the repository root, so git resolves the pathspecs against it.");
        }

        // libgit2's Ignore reads .gitignore files, .git/info/exclude and core.excludesFile with git's precedence and negation (verified against
        // git check-ignore); what it does not do is git's "a tracked file is never ignored", so that is applied here.
        var ignored = new List<string>();
        foreach (var path in paths)
        {
            if (repo.Index[path] is null && repo.Ignore.IsPathIgnored(path))
            {
                ignored.Add(path);
            }
        }

        return ignored;
    }

    /// <summary>ls-tree -r --name-only: a file or submodule is listed by its path; a directory only through the files (and submodules) beneath it.</summary>
    private static void AddHeadFiles(TreeEntry entry, string path, HashSet<string> seen, List<string> matches)
    {
        if (entry.TargetType != TreeEntryTargetType.Tree)
        {
            if (seen.Add(path))
            {
                matches.Add(path);
            }

            return;
        }

        foreach (var child in (Tree)entry.Target)
        {
            AddHeadFiles(child, path + "/" + child.Name, seen, matches);
        }
    }

    /// <summary>git's strict ISO date (<c>%aI</c>, <c>%cI</c>): the signature's own offset, a zero offset spelled the way the installed git spells it.</summary>
    private static string StrictIsoDate(DateTimeOffset when, string workingDirectory, TimeSpan cliTimeout, string sha, string format) =>
        when.Offset == TimeSpan.Zero
            ? when.ToString("yyyy-MM-dd'T'HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture) + ZeroOffsetDesignator(workingDirectory, cliTimeout, sha, format)
            : when.ToString("yyyy-MM-dd'T'HH:mm:sszzz", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Orders ref names by their UTF-8 bytes, as git sorts refs (strcmp), which .NET's UTF-16 ordinal order does not for supplementary characters.</summary>
    private static readonly Comparer<string> RefNameOrder = Comparer<string>.Create(
        (left, right) => Encoding.UTF8.GetBytes(left).AsSpan().SequenceCompareTo(Encoding.UTF8.GetBytes(right)));

    public IReadOnlyList<BranchRef>? ListBranches(string workingDirectory, TimeSpan cliTimeout)
    {
        using var repo = Open(workingDirectory);
        if (repo is null)
        {
            return null;
        }

        // A detached HEAD makes git print a "(HEAD detached at <abbreviated sha>)" row, and branch.sort reorders the list: both are git's to answer.
        if (repo.Info.IsHeadDetached)
        {
            throw new NotSupportedException("HEAD is detached, so git adds a pseudo-branch row with an abbreviated hash.");
        }

        if (repo.Config.Get<string>("branch.sort") is not null)
        {
            throw new NotSupportedException("branch.sort is configured, so git orders the list.");
        }

        const string heads = "refs/heads/";
        const string remotes = "refs/remotes/";
        var current = repo.Head.CanonicalName;
        var rows = new List<(string Canonical, BranchRef Branch)>();
        foreach (var reference in repo.Refs)
        {
            // A symbolic ref (remotes/origin/HEAD -> origin/main) is a row the Git tool skips.
            if (reference is SymbolicReference)
            {
                continue;
            }

            var canonical = reference.CanonicalName;
            if (canonical.StartsWith(heads, StringComparison.Ordinal))
            {
                rows.Add((canonical, new BranchRef(canonical[heads.Length..], canonical == current, false)));
            }
            else if (canonical.StartsWith(remotes, StringComparison.Ordinal))
            {
                rows.Add((canonical, new BranchRef(canonical[remotes.Length..], false, true)));
            }
        }

        // git sorts by ref name: every refs/heads/ name before every refs/remotes/ one.
        return [.. rows.OrderBy(row => row.Canonical, RefNameOrder).Select(row => row.Branch)];
    }

    public IReadOnlyList<StashRef>? ListStashes(string workingDirectory, TimeSpan cliTimeout)
    {
        using var repo = Open(workingDirectory);
        if (repo is null)
        {
            return null;
        }

        var stashes = repo.Stashes.ToList();
        if (stashes.Count == 0)
        {
            return [];
        }

        // Needed only when there is an entry to abbreviate; a failed length probe (see ShortLength) leaves the list to git.
        var abbreviation = ShortLength(repo, workingDirectory, cliTimeout);
        try
        {
            return [.. stashes.Select((stash, index) =>
            {
                var commit = stash.WorkTree;
                var shortSha = ShortId(repo, commit, abbreviation);
                return new StashRef(index, shortSha, StrictIsoDate(commit.Committer.When, workingDirectory, cliTimeout, commit.Sha, "%cI"), stash.Message);
            })];
        }
        catch (ArgumentOutOfRangeException ex)
        {
            throw new NotSupportedException("A commit date or UTC offset is outside .NET's range, so git answers.", ex);
        }
    }

    public int? CountStashes(string workingDirectory, TimeSpan cliTimeout)
    {
        using var repo = Open(workingDirectory);
        return repo?.Stashes.Count();
    }

    public IReadOnlyList<NameStatusEntry>? GetNameStatus(string workingDirectory, string target, IReadOnlyList<string>? paths, TimeSpan cliTimeout)
    {
        // Only comparisons that involve no working-tree file are served: tree to index ("staged") and tree to tree (refA..refB). libgit2's index-to-workdir
        // diff (and its status) lists a stat-dirty file whose only difference is EOL conversion (core.autocrlf, .gitattributes text/eol) as modified, where git
        // runs its clean filters and lists nothing; its tree-to-workdir diff gets that right but has no index awareness. Neither reproduces git's answer
        // without a filter-aware hash, which libgit2's managed API does not offer, so "working" and a single ref stay with git. (A repository like this one,
        // with core.autocrlf and a .gitattributes, would be declined anyway.)
        var isRange = target.Contains("..", StringComparison.Ordinal);
        if (target != "staged" && !isRange)
        {
            throw new NotSupportedException($"'{target}' compares against the working tree, whose EOL-filtered content libgit2's diff does not reproduce, so git answers.");
        }

        var specs = paths is { Count: > 0 } ? ParseLogPathspecs(paths) : null;
        using var repo = Open(workingDirectory);
        if (repo is null)
        {
            return null;
        }

        RequireNameStatusRepository(repo, workingDirectory);
        // CompareOptions' default similarity detects renames; git's detection is not reproduced, so it is off and a result that could hold a rename is declined.
        var options = new CompareOptions { Similarity = SimilarityOptions.None };
        TreeChanges changes;
        if (!isRange)
        {
            RequirePlainHistory(repo);
            var head = repo.Head.Tip ?? throw new NotSupportedException("HEAD is unborn, so git diffs the index against the empty tree.");
            changes = repo.Diff.Compare<TreeChanges>(head.Tree, DiffTargets.Index, null, null, options);
        }
        else
        {
            // refA..refB with a plain revision each side: tree to tree. refA...refB (merge base) and an empty side (meaning HEAD) are left to git.
            var sides = target.Split("..");
            if (sides.Length != 2 || target.Contains("...", StringComparison.Ordinal) || sides.Any(side => side.Length == 0))
            {
                throw new NotSupportedException($"'{target}' is not a plain refA..refB range, so git resolves it.");
            }

            // A "--" in the command (paths given, even none) makes a name that is both a revision and a file unambiguous.
            var pathsGiven = paths is not null;
            if (!pathsGiven && PathExists(workingDirectory, target))
            {
                throw new NotSupportedException($"'{target}' is also a path in the working directory; git decides whether that is ambiguous.");
            }

            var from = ResolveSimpleCommit(repo, workingDirectory, sides[0], pathsGiven);
            var to = ResolveSimpleCommit(repo, workingDirectory, sides[1], pathsGiven);
            if (from is null || to is null)
            {
                return null;
            }

            changes = repo.Diff.Compare<TreeChanges>(from.Tree, to.Tree, options);
        }

        using (changes)
        {
            return ToNameStatus(changes, specs, againstIndex: !isRange);
        }
    }

    public IReadOnlyList<NameStatusEntry>? GetCommitNameStatus(string workingDirectory, string revision, IReadOnlyList<string>? paths, TimeSpan cliTimeout)
    {
        var specs = paths is { Count: > 0 } ? ParseLogPathspecs(paths) : null;
        using var repo = Open(workingDirectory);
        if (repo is null)
        {
            return null;
        }

        RequireNameStatusRepository(repo, workingDirectory);
        // The tool's header call has no "--", so the name is checked for a clash with a file whatever the paths are.
        var commit = ResolveSimpleCommit(repo, workingDirectory, revision, pathsGiven: false);
        if (commit is null)
        {
            return null;
        }

        // git diff <rev>^ <rev>: a merge commit against its first parent, a root commit against the empty tree.
        var options = new CompareOptions { Similarity = SimilarityOptions.None };
        using var changes = repo.Diff.Compare<TreeChanges>(commit.Parents.FirstOrDefault()?.Tree, commit.Tree, options);
        return ToNameStatus(changes, specs, againstIndex: false);
    }

    public CommitHeader? GetCommitHeader(string workingDirectory, string revision, TimeSpan cliTimeout)
    {
        if (revision.Contains("..", StringComparison.Ordinal) || !SimpleRevision.IsMatch(revision))
        {
            throw new NotSupportedException($"'{revision}' is not a plain ref, hash or HEAD~N form, so git resolves it.");
        }

        using var repo = Open(workingDirectory);
        if (repo is null)
        {
            return null;
        }

        RequirePlainHistory(repo);
        if (PathExists(workingDirectory, revision))
        {
            throw new NotSupportedException($"'{revision}' is also a path in the working directory; git decides whether that is ambiguous.");
        }

        // Unknown revision: null, as the CLI's non-zero exit, so the tool reports git's own message.
        var found = repo.Lookup(revision);
        if (found is null)
        {
            return null;
        }

        // An annotated tag makes git show print the tag's own header first; a tree or blob has no author at all.
        if (found is not Commit commit)
        {
            throw new NotSupportedException($"'{revision}' is not a plain commit, so git shows it.");
        }

        RequireStableCommitText(commit);
        try
        {
            return new CommitHeader(commit.Sha, commit.Author.Name, StrictIsoDate(commit.Author.When, workingDirectory, cliTimeout, commit.Sha, "%aI"), commit.Message);
        }
        catch (ArgumentOutOfRangeException ex)
        {
            throw new NotSupportedException("A commit date or UTC offset is outside .NET's range, so git answers.", ex);
        }
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

    private static bool PathExists(string workingDirectory, string name) =>
        File.Exists(Path.Combine(workingDirectory, name)) || Directory.Exists(Path.Combine(workingDirectory, name));

    /// <summary>A plain revision (see <see cref="SimpleRevision"/>) as a commit; null when it does not resolve to one, so the CLI reports git's own error.</summary>
    private static Commit? ResolveSimpleCommit(Repository repo, string workingDirectory, string revision, bool pathsGiven)
    {
        if (revision.Contains("..", StringComparison.Ordinal) || !SimpleRevision.IsMatch(revision))
        {
            throw new NotSupportedException($"'{revision}' is not a plain ref, hash or HEAD~N form, so git resolves it.");
        }

        if (!pathsGiven && PathExists(workingDirectory, revision))
        {
            throw new NotSupportedException($"'{revision}' is also a path in the working directory; git decides whether that is ambiguous.");
        }

        RequirePlainHistory(repo);
        return repo.Lookup<Commit>(revision);
    }

    /// <summary>The checks every name-status read shares: git runs at the repository root, and no config that changes rename handling or ordering is set.</summary>
    private static void RequireNameStatusRepository(Repository repo, string workingDirectory)
    {
        if (RelativeToWorkdir(repo, workingDirectory).Length != 0)
        {
            throw new NotSupportedException("The working directory is below the repository root, so git resolves the pathspecs against it.");
        }

        foreach (var key in new[] { "diff.renames", "diff.orderfile" })
        {
            if (repo.Config.Get<string>(key) is not null)
            {
                throw new NotSupportedException($"{key} is configured, so git decides rename handling and order.");
            }
        }
    }

    /// <summary>The checks <see cref="ToLogCommit"/> makes before it trusts a commit's text: git re-encodes a declared encoding and decodes invalid UTF-8 its own way.</summary>
    private static void RequireStableCommitText(Commit commit)
    {
        if (commit.Encoding is { } encoding && !encoding.Equals("UTF-8", StringComparison.OrdinalIgnoreCase) && !encoding.Equals("UTF8", StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException($"Commit {commit.Sha} declares encoding '{encoding}', so git converts it.");
        }

        if (commit.Author.Name.AsSpan().IndexOfAny('�', '\u001e', '\u001f') >= 0 || commit.Message.AsSpan().IndexOfAny('�', '\u001e', '\u001f') >= 0)
        {
            throw new NotSupportedException($"Commit {commit.Sha} holds invalid UTF-8 or a separator character, so git answers.");
        }
    }

    /// <summary>
    /// The rows of a tree comparison as <c>git diff --name-status</c> prints them, limited to <paramref name="specs"/> when given (git diffs only the limited paths, so
    /// the checks apply to those). Declines what the comparison cannot reproduce: a result with both an added and a deleted path (git would pair them as a rename
    /// above its similarity threshold, and libgit2's score differs), submodule entries, unmerged or otherwise unmapped kinds, and a path git prints quoted.
    /// </summary>
    private static List<NameStatusEntry> ToNameStatus(TreeChanges changes, List<(string Path, bool DirectoryOnly)>? specs, bool againstIndex)
    {
        var entries = new List<NameStatusEntry>();
        foreach (var change in changes)
        {
            // On Windows LibGit2Sharp hands paths back with backslashes; elsewhere a backslash is part of a name (and git quotes it).
            var path = OperatingSystem.IsWindows() ? change.Path.Replace('\\', '/') : change.Path;
            if (specs is not null && !specs.Any(spec => (!spec.DirectoryOnly && path == spec.Path) || path.StartsWith(spec.Path + "/", StringComparison.Ordinal)))
            {
                continue;
            }

            if (change.Mode == Mode.GitLink || change.OldMode == Mode.GitLink)
            {
                throw new NotSupportedException("A submodule entry changed; git compares it by its own rules.");
            }

            // An intent-to-add path (git add -N) sits in the index as an empty blob; libgit2 lists it as added, git diff --cached leaves it out. The index does not
            // say which empty blob is one, so an empty file new in the index is left to git too.
            if (againstIndex && change.Exists && change.Oid.Sha == EmptyBlobSha)
            {
                throw new NotSupportedException($"'{path}' is an empty blob in the index, which may be an intent-to-add entry that git leaves out.");
            }

            var status = change.Status switch
            {
                ChangeKind.Added => "A",
                ChangeKind.Deleted => "D",
                ChangeKind.Modified => "M",
                ChangeKind.TypeChanged => "T",
                _ => throw new NotSupportedException($"A {change.Status} entry is left to git."),
            };
            // core.quotePath=false leaves bytes above 0x7f alone but git still quotes a quote, a backslash and control characters; invalid UTF-8 decodes differently.
            if (path.Any(ch => ch < ' ' || ch is '"' or '\\' or '\u007f' or '�'))
            {
                throw new NotSupportedException($"'{path}' is a name git prints quoted or decodes differently.");
            }

            entries.Add(new NameStatusEntry(status, path));
        }

        if (entries.Any(entry => entry.Status == "A") && entries.Any(entry => entry.Status == "D"))
        {
            throw new NotSupportedException("The result has added and deleted paths, which git may pair as renames.");
        }

        // Byte-wise path order is git's tree-walk order (a directory sorts as "name/", which is the same comparison on whole paths).
        entries.Sort((left, right) => Utf8PathOrder.Compare(left.Path, right.Path));
        return entries;
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

    private static string? _zeroOffsetDesignator;

    /// <summary>
    /// How the installed git prints a zero UTC offset in <c>%aI</c>: "Z" on current versions, "+00:00" on older ones. Asked of the CLI once per
    /// process (with a commit whose author offset is zero), so the libgit2 path reproduces whichever git the tool would otherwise have run.
    /// </summary>
    private static string ZeroOffsetDesignator(string workingDirectory, TimeSpan cliTimeout, string sha, string format = "%aI")
    {
        var known = Volatile.Read(ref _zeroOffsetDesignator);
        if (known is not null)
        {
            return known;
        }

        // <format> is %aI or %cI, whichever date the commit given has a zero offset in; git spells both the same way.
        var run = GitProcess.TryRun(workingDirectory, cliTimeout, "log", "-1", "--format=" + format, sha);
        var text = run is { ExitCode: 0 } ? run.Value.StdOut.Trim() : null;
        if (text is not null && text.EndsWith('Z'))
        {
            known = "Z";
        }
        else if (text is not null && text.EndsWith("+00:00", StringComparison.Ordinal))
        {
            known = "+00:00";
        }
        else
        {
            throw new NotSupportedException("The installed git did not report how it prints a zero UTC offset, so git answers.");
        }

        Volatile.Write(ref _zeroOffsetDesignator, known);
        return known;
    }

    /// <summary>
    /// A path-limited walk looks at every commit it passes (tens of microseconds each) and on a history with merges ends in a decline anyway; past
    /// this many the git CLI (which prunes with its commit-graph) answers, so the wasted in-process work stays well under one CLI call's cost.
    /// </summary>
    // TODO a path-limited log on a merge-heavy history (this repo's) walks to its first merge and then declines: measured 25-100 ms of wasted in-process work
    // before the CLI's roughly 110 ms. Reproducing git's history simplification at a merge (or a cheap look-ahead for merges) would remove it.
    private const int MaxPathLimitedCommitsExamined = 2000;
}
