using LibGit2Sharp;

namespace RoslynSentinel.Common;

/// <summary>
/// The compare-and-swap commit primitive of <see cref="Libgit2GitReader"/> (proposal_libgit2sharp_in_process_git.md, step 5b result and 5c-1). No caller yet. The sequence is the
/// one the 5b spike proved safe against another committer (a CLI <c>git commit</c> or a second process): take <c>index.lock</c> exclusively, open a fresh repository, read HEAD,
/// build the tree, create the commit object (no ref touched), move the ref with <see cref="Libgit2Native.CompareAndSwapReference"/> (succeeds only if the ref is still the tip
/// the commit was built on), release the lock. The index FILE is not rewritten: a plain <c>git commit</c> refreshes stat data in it, this does not, so a later
/// <c>git status</c> is clean and refreshes stat-dirty entries lazily (as it does after any checkout). A commit object that loses the race is an unreferenced loose
/// object, exactly what a losing <c>git commit</c> leaves; <c>git gc</c> prunes it.
/// <para>
/// Declines (<see cref="CasCommitOutcome.Declined"/>, nothing written), in the order checked: (C1) any process environment variable in <see cref="CommitEnvironmentVariables"/>
/// (identity, dates and reflog action git reads; libgit2 does not) or in <c>OverridingEnvironmentVariables</c>; (C2) the repository-shape declines of <c>OpenForIndexWrite</c>
/// (worktree config, split index, index-format features, a directory below the repository root); (C3) <c>author.*</c> or <c>committer.*</c> config, a missing or unusual
/// <c>user.name</c>/<c>user.email</c> (git would auto-detect, strip or refuse); (C4) a merge, cherry-pick, revert, rebase or am in progress, so also never a merge commit
/// (exactly one parent: the tip); (C5) HEAD is not a branch under refs/heads or a detached commit; (C6) an unmerged index entry, or an entry that may be intent-to-add
/// (the empty blob that HEAD lacks, in an index of version 3 or 4; a version 2 index cannot hold the flag); (C7) HeadPlusPaths only: no paths, a path git would normalise, a
/// path that matches nothing, a gitlink, assume-unchanged entry or non-regular mode at or under a path, or a working tree that differs from the index at or under a path
/// (<c>git commit --only</c> commits the working-tree content, so the caller stages first); (C8) a message with a NUL byte.
/// </para>
/// <para>
/// Not decided here (the caller's policy, step 5c-2): hooks, <c>commit.gpgsign</c>, trailers, <c>amend</c>. The message is stored byte for byte (no cleanup, no added newline).
/// </para>
/// </summary>
internal sealed partial class Libgit2GitReader
{
    // Read by git for a commit and ignored by libgit2 (which takes identity from config only): committing here would write a different author, date or reflog text.
    // GIT_REFLOG_ACTION replaces "commit" in the reflog text. GIT_NAMESPACE prefixes every ref name; GIT_INDEX_VERSION changes what git writes.
    // EMAIL (git's last-resort email) is deliberately absent: it only matters when user.email is unset, which ReadCommitIdentity declines anyway.
    private static readonly string[] CommitEnvironmentVariables =
    [
        "GIT_AUTHOR_NAME", "GIT_AUTHOR_EMAIL", "GIT_AUTHOR_DATE", "GIT_COMMITTER_NAME", "GIT_COMMITTER_EMAIL", "GIT_COMMITTER_DATE",
        "GIT_REFLOG_ACTION", "GIT_NAMESPACE", "GIT_INDEX_VERSION",
    ];

    private static readonly string[] CommitIdentityConfigKeys = ["author.name", "author.email", "committer.name", "committer.email"];

    private static readonly ObjectId EmptyTreeId = new("4b825dc642cb6eb9a060e54bf8d69288fbee4904");

    /// <summary>
    /// Commits <paramref name="treeSource"/> on top of HEAD's current tip and moves the branch (or detached HEAD) with a compare-and-swap, as described on the partial.
    /// Never throws for a decline, a lost race or a libgit2 error (see <see cref="CasCommitResult"/>); native load failures
    /// (<see cref="DllNotFoundException"/>, <see cref="TypeInitializationException"/>, <see cref="EntryPointNotFoundException"/>) propagate for <see cref="FallbackGitReader"/> to count.
    /// </summary>
    /// <param name="workingDirectory">The repository's (or linked worktree's) root directory.</param>
    /// <param name="message">The complete, final commit message text to store.</param>
    /// <param name="treeSource">Which tree to commit.</param>
    /// <param name="paths">Repository-relative literal paths for <see cref="CasCommitTreeSource.HeadPlusPaths"/>; ignored for <see cref="CasCommitTreeSource.WholeIndex"/>.</param>
    /// <param name="betweenCommitAndRefUpdate">Test seam only: runs after the commit object exists and before the ref update, to move the branch from outside.</param>
    internal static CasCommitResult CommitWithCompareAndSwap(
        string workingDirectory, string message, CasCommitTreeSource treeSource, IReadOnlyList<string>? paths = null, Action? betweenCommitAndRefUpdate = null)
    {
        try
        {
            return CommitCompareAndSwapCore(workingDirectory, message, treeSource, paths, betweenCommitAndRefUpdate);
        }
        catch (NotSupportedException ex) when (ex is not PlatformNotSupportedException)
        {
            return CasCommitResult.Declined(ex.Message);
        }
        catch (LockedFileException ex)
        {
            return CasCommitResult.LockContention(ex.Message);
        }
        catch (LibGit2SharpException ex)
        {
            return CasCommitResult.Failed($"{ex.GetType().Name}: {ex.Message}");
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or InvalidCastException)
        {
            return CasCommitResult.Declined($"libgit2 could not build the commit ({ex.GetType().Name}: {ex.Message}); git decides.");
        }
    }

    private static CasCommitResult CommitCompareAndSwapCore(
        string workingDirectory, string message, CasCommitTreeSource treeSource, IReadOnlyList<string>? paths, Action? betweenCommitAndRefUpdate)
    {
        foreach (var name in CommitEnvironmentVariables)
        {
            if (Environment.GetEnvironmentVariable(name) is not null)
            {
                throw Decline($"{name} is set in the process environment; git honours it for a commit and libgit2 does not.");
            }
        }

        if (message.Contains('\0'))
        {
            throw Decline("The message contains a NUL byte, which a git commit message cannot hold.");
        }

        HeldIndexLock? indexLock = null;
        try
        {
            string gitDirectory;
            Signature signature;
            using (var probe = OpenForIndexWrite(workingDirectory))
            {
                if (probe.Info.IsBare)
                {
                    throw Decline("A bare repository has no index to commit.");
                }

                var (name, email) = ReadCommitIdentity(probe);
                signature = new Signature(name, email, DateTimeOffset.Now);
                gitDirectory = probe.Info.Path;

                try
                {
                    indexLock = HeldIndexLock.Take(probe);
                }
                catch (NotSupportedException ex) when (ex.InnerException is IOException)
                {
                    return CasCommitResult.LockContention(ex.Message);
                }
            }

            // A fresh repository AFTER the lock: nothing it reads (index, refs) can predate the lock, so no honouring writer finished a write in between.
            using var repo = new Repository(gitDirectory);
            if (GitOperationState.Detect(gitDirectory) is { } operation)
            {
                throw Decline($"A {operation} is in progress; git concludes it with its own commit rules.");
            }

            var (referenceName, tip) = ReadCommitTarget(repo);
            var tree = treeSource switch
            {
                CasCommitTreeSource.WholeIndex => BuildWholeIndexTree(repo, tip),
                CasCommitTreeSource.HeadPlusPaths => BuildHeadPlusPathsTree(repo, tip, paths),
                _ => throw new ArgumentOutOfRangeException(nameof(treeSource), treeSource, "Unknown tree source."),
            };

            if (tree.Id == (tip?.Tree.Id ?? EmptyTreeId))
            {
                return CasCommitResult.NothingToCommit();
            }

            var commit = repo.ObjectDatabase.CreateCommit(signature, signature, message, tree, tip is null ? [] : [tip], prettifyMessage: false);
            betweenCommitAndRefUpdate?.Invoke();

            // An all-zero expected id means "the ref must not exist yet" (the root commit of an unborn branch).
            var expectedOldId = tip is null ? new byte[20] : tip.Id.RawId;
            var reflogMessage = (tip is null ? "commit (initial): " : "commit: ") + ReflogSubject(message);
            var (code, text) = Libgit2Native.CompareAndSwapReference(gitDirectory, referenceName, commit.Id.RawId, expectedOldId, reflogMessage);
            return code switch
            {
                0 => CasCommitResult.Committed(commit.Sha, referenceName, tip?.Sha),
                Libgit2Native.ErrorModified or Libgit2Native.ErrorNotFound => CasCommitResult.BranchMoved(tip?.Sha, CurrentTip(repo, referenceName), text),
                Libgit2Native.ErrorLocked => CasCommitResult.LockContention(text),
                _ => CasCommitResult.Failed($"libgit2 error {code}: {text}"),
            };
        }
        finally
        {
            indexLock?.Dispose();
        }
    }

    /// <summary>C3: the identity git would commit as, from config only; declines everything git resolves differently.</summary>
    private static (string Name, string Email) ReadCommitIdentity(Repository repo)
    {
        foreach (var key in CommitIdentityConfigKeys)
        {
            if (EffectiveConfigValue(repo, key) is not null)
            {
                throw Decline($"{key} is configured; git prefers it over user.*, libgit2 does not.");
            }
        }

        var name = EffectiveConfigValue(repo, "user.name");
        var email = EffectiveConfigValue(repo, "user.email");
        if (name is null || email is null)
        {
            throw Decline("user.name or user.email is not configured; git auto-detects an identity or refuses, in its own words.");
        }

        if (!IsPlainIdentityPart(name) || !IsPlainIdentityPart(email))
        {
            throw Decline("user.name or user.email has characters git strips or refuses in an identity.");
        }

        return (name, email);
    }

    /// <summary>An identity part git would store unchanged: non-empty, no angle brackets or line breaks, no leading or trailing "crud" (git's strip set).</summary>
    private static bool IsPlainIdentityPart(string value) =>
        value.Length > 0 && value.IndexOfAny(['<', '>', '\n', '\r', '\0']) < 0 && !IsIdentityCrud(value[0]) && !IsIdentityCrud(value[^1]);

    private static bool IsIdentityCrud(char c) => c <= ' ' || c is '.' or ',' or ':' or ';' or '<' or '>' or '"' or '\\' or '\'';

    /// <summary>C5: the ref to swap (a branch under refs/heads, or HEAD itself when detached) and its current tip (null for an unborn branch).</summary>
    private static (string ReferenceName, Commit? Tip) ReadCommitTarget(Repository repo)
    {
        var head = repo.Refs.Head;
        if (head is SymbolicReference symbolic)
        {
            var name = symbolic.TargetIdentifier;
            if (!name.StartsWith("refs/heads/", StringComparison.Ordinal))
            {
                throw Decline($"HEAD points at '{name}', outside refs/heads.");
            }

            return repo.Refs[name] switch
            {
                null => (name, null),
                DirectReference { Target: Commit commit } => (name, commit),
                _ => throw Decline($"'{name}' is not a plain reference to a commit."),
            };
        }

        return head is DirectReference { Target: Commit detached }
            ? ("HEAD", detached)
            : throw Decline("HEAD does not name a commit.");
    }

    private static string? CurrentTip(Repository repo, string referenceName) =>
        repo.Refs[referenceName] is DirectReference { TargetIdentifier: var id } ? id : null;

    /// <summary>C6, then everything staged as a tree. Unmerged entries are declined first (libgit2 cannot write such a tree, git refuses the commit in its own words).</summary>
    private static Tree BuildWholeIndexTree(Repository repo, Commit? tip)
    {
        RequireNoUnmergedOrIntentToAdd(repo, tip, wanted: null);
        return repo.Index.WriteToTree();
    }

    /// <summary>C6, C7: HEAD's tree, with the index entries at or under <paramref name="paths"/> replacing what HEAD has there.</summary>
    private static Tree BuildHeadPlusPathsTree(Repository repo, Commit? tip, IReadOnlyList<string>? paths)
    {
        if (paths is null || paths.Count == 0)
        {
            throw Decline("HeadPlusPaths needs at least one path.");
        }

        RequireCleanRelativePaths(paths);
        var wanted = new HashSet<string>(paths, StringComparer.Ordinal);
        var indexEntries = RequireNoUnmergedOrIntentToAdd(repo, tip, wanted);

        var headFiles = new Dictionary<string, TreeEntry>(StringComparer.Ordinal);
        if (tip is not null)
        {
            foreach (var path in wanted)
            {
                if (tip.Tree[path] is { } headEntry)
                {
                    CollectHeadFiles(headEntry, path, headFiles);
                }
            }
        }

        var matched = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in indexEntries.Keys.Concat(headFiles.Keys))
        {
            MarkMatches(path, wanted, matched);
        }

        if (matched.Count != wanted.Count)
        {
            throw Decline("A listed path matches nothing in the index or HEAD, so git words the error.");
        }

        RequireWorkingTreeMatchesIndex(repo, wanted);

        var definition = tip is null ? new TreeDefinition() : TreeDefinition.From(tip.Tree);
        // Removals first, so a file replaced by a directory of the same name (or the reverse) ends with one entry.
        foreach (var path in headFiles.Keys.Where(path => !indexEntries.ContainsKey(path)))
        {
            definition.Remove(path);
        }

        foreach (var (path, entry) in indexEntries)
        {
            definition.Add(path, entry.Id, entry.Mode);
        }

        return repo.ObjectDatabase.CreateTree(definition);
    }

    /// <summary>
    /// One pass over the index: declines an unmerged entry anywhere, and an entry that may be intent-to-add anywhere (whole index) or at or under <paramref name="wanted"/>.
    /// With <paramref name="wanted"/>, also declines a gitlink, assume-unchanged or non-regular entry at or under a path and returns those entries by path.
    /// </summary>
    private static Dictionary<string, IndexEntry> RequireNoUnmergedOrIntentToAdd(Repository repo, Commit? tip, HashSet<string>? wanted)
    {
        var mayHaveIntentToAdd = IndexMayHoldIntentToAdd(repo);
        var trustsExecutableBit = wanted is null || !(EffectiveConfigValue(repo, "core.filemode") is { } filemode && IsFalseLike(filemode));
        var underWanted = new Dictionary<string, IndexEntry>(StringComparer.Ordinal);
        foreach (var entry in repo.Index)
        {
            if (entry.StageLevel != StageLevel.Staged)
            {
                throw Decline($"'{entry.Path}' is unmerged; git refuses or resolves it in its own words.");
            }

            var relevant = wanted is null || IsAtOrUnder(entry.Path, wanted);
            if (!relevant)
            {
                continue;
            }

            // An intent-to-add entry is the empty blob that HEAD lacks; libgit2 does not expose the flag, so a staged empty new file in a v3/v4 index lands here too (merely slower).
            if (mayHaveIntentToAdd && entry.Id == EmptyBlobId && (tip is null || tip.Tree[entry.Path] is null))
            {
                throw Decline($"'{entry.Path}' may be an intent-to-add entry, which libgit2 cannot tell from a staged empty file.");
            }

            if (wanted is null)
            {
                continue;
            }

            if (entry.Mode == Mode.GitLink || entry.AssumeUnchanged
                || entry.Mode is not (Mode.NonExecutableFile or Mode.ExecutableFile or Mode.SymbolicLink))
            {
                throw Decline($"'{entry.Path}' is a gitlink, assume-unchanged or non-regular entry; git decides what --only commits for it.");
            }

            // git commit --only rebuilds the listed paths from the working tree, and with core.filemode=false it cannot see an executable bit there: the mode is HEAD's (100644
            // for a path HEAD lacks), so a staged mode change would be lost by git and kept here. Decline that case.
            if (!trustsExecutableBit && entry.Mode is Mode.NonExecutableFile or Mode.ExecutableFile)
            {
                var headMode = tip?.Tree[entry.Path]?.Mode;
                var gitsMode = headMode is Mode.NonExecutableFile or Mode.ExecutableFile ? headMode.Value : Mode.NonExecutableFile;
                if (entry.Mode != gitsMode)
                {
                    throw Decline($"'{entry.Path}' has a staged mode change and core.filemode is false: git commit --only takes the mode from HEAD, not the index.");
                }
            }

            underWanted[entry.Path] = entry;
        }

        return underWanted;
    }

    /// <summary>An index file of version 3 or 4 may carry the intent-to-add flag (it lives in the extended flags); version 2, or no index file, cannot.</summary>
    private static bool IndexMayHoldIntentToAdd(Repository repo)
    {
        var indexFile = Path.Combine(repo.Info.Path, "index");
        if (!File.Exists(indexFile))
        {
            return false;
        }

        var header = new byte[8];
        using var stream = new FileStream(indexFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Read(header, 0, header.Length) < header.Length)
        {
            return true;
        }

        var version = (header[4] << 24) | (header[5] << 16) | (header[6] << 8) | header[7];
        return version != 2;
    }

    /// <summary>
    /// C7: <c>git commit --only</c> commits the WORKING-TREE content of the listed paths, this commits their index entries; they are the same exactly when the working tree
    /// has no change at or under a listed path. Anything else is declined, and the caller stages those paths first (which is also what leaves git's real index in the same final state).
    /// </summary>
    private static void RequireWorkingTreeMatchesIndex(Repository repo, HashSet<string> wanted)
    {
        // Pathspec narrows the scan to the listed paths; glob metacharacters fall back to the full scan because libgit2 treats them as patterns, and IsAtOrUnder stays the exact filter.
        var options = new StatusOptions
        {
            Show = StatusShowOption.IndexAndWorkDir,
            IncludeUntracked = false,
            IncludeIgnored = false,
            IncludeUnaltered = false,
            RecurseUntrackedDirs = false,
            DetectRenamesInIndex = false,
            DetectRenamesInWorkDir = false,
        };

        if (!wanted.Any(path => path.IndexOfAny(['*', '?', '[', ']', '\\']) >= 0))
        {
            options.PathSpec = wanted.ToArray();
        }

        const FileStatus workingTreeDifference = FileStatus.ModifiedInWorkdir | FileStatus.DeletedFromWorkdir | FileStatus.TypeChangeInWorkdir | FileStatus.RenamedInWorkdir
            | FileStatus.Conflicted | FileStatus.Unreadable;
        foreach (var entry in repo.RetrieveStatus(options))
        {
            if ((entry.State & workingTreeDifference) != 0 && IsAtOrUnder(entry.FilePath, wanted))
            {
                throw Decline($"'{entry.FilePath}' differs between the working tree and the index; git commit --only would commit the working-tree content, so stage it first.");
            }
        }
    }

    /// <summary>
    /// What git writes after "commit: " (probed: a two-line first paragraph gives only its first line): the first line that is not blank, with trailing whitespace removed
    /// (git's whitespace cleanup of a -m message). Empty for an all-blank message.
    /// </summary>
    private static string ReflogSubject(string message)
    {
        var lines = message.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        return lines.FirstOrDefault(line => !string.IsNullOrWhiteSpace(line))?.TrimEnd() ?? string.Empty;
    }
}
