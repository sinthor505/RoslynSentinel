using System.Text.RegularExpressions;
using LibGit2Sharp;

namespace RoslynSentinel.Common;

/// <summary>
/// The index-writing half of <see cref="Libgit2GitReader"/>: whole-file stage (<c>git add</c>) and path unstage (<c>git reset -- paths</c>). The aim is the
/// spawn time of ordinary text-file staging, not coverage: every condition below that would make libgit2 and git differ is a DECLINE
/// (<see cref="NotSupportedException"/>), and <see cref="FallbackGitReader"/> then runs the same git command it always ran. All decisions are made before the
/// first index mutation, and the index is written once, at the end, through libgit2's index.lock plus rename (the same protocol git uses).
/// <para>
/// Decline rules, in the order they are checked (each is one condition that routes to the git CLI):
/// (S1) repository shape: a process environment variable that git honours and libgit2 does not (GIT_DIR, GIT_WORK_TREE, GIT_INDEX_FILE, GIT_CONFIG_*, see <c>OverridingEnvironmentVariables</c>),
/// extensions.worktreeConfig with a config.worktree, not inside a repository, or a directory below the repository root (the first two are decided by <c>Open</c>, for every reader method);
/// (S2) index-format features: core.fsmonitor, core.untrackedCache, core.splitIndex (or a sharedindex file, which <c>OpenForIndexRead</c> declines for every index read), core.sparseCheckout, index.sparse, feature.manyFiles or index.skipHash enabled, or index.version set;
/// (S3) a submodule, a gitlink entry, an unmerged entry or an assume-unchanged entry in the index;
/// (S4) a possible intent-to-add entry (stage only: the empty blob, absent from HEAD), whose flag libgit2 does not expose;
/// (S5) core.safecrlf set to anything but false-like or warn (stage only: git can fail the add where libgit2 would not);
/// (S6) a listed path git normalises or rejects, or, for stage, one that matches nothing git add would stage (so git words the error or the advisory);
/// (S7) a file to add that is not a plain regular file (a directory such as an embedded repository, a symlink or reparse point, a type change), or that replaces a non-regular index entry;
/// (S8) a file to add that contains a carriage return byte: EOL conversion (core.autocrlf, eol, text, safecrlf round trips) is git's, so only files it cannot change are staged here;
/// (S9) a .gitattributes (any directory from the root to the file), .git/info/attributes, global or system attributes file that mentions filter, ident or working-tree-encoding;
/// (S10) index.lock exists: this call creates index.lock itself, exclusively, before it reads the index and holds it through every decision (as git does for its whole
/// read-modify-write), so a lock held by anyone else is declined and no honouring writer can finish a write in between; libgit2 takes its own lock only for the final write,
/// and a writer that slips into that gap makes libgit2's lock fail, which is declined as well;
/// (S11) any libgit2 or argument failure during the mutation (git redoes the idempotent operation and words its own error);
/// (U1) unstage with no paths (the whole-index reset also moves ORIG_HEAD, writes a reflog entry and clears merge state), or a path with a glob character (git reset reads pathspecs as globs).
/// A CR-free file stages identically whatever core.autocrlf, core.eol, text, eol, -text or binary say, which is why those need no rule of their own.
/// </para>
/// </summary>
internal sealed partial class Libgit2GitReader
{
    private static readonly ObjectId EmptyBlobId = new(EmptyBlobSha);

    // Attributes that change the bytes git stores (clean filter, $Id$ expansion, re-encoding). A word match is deliberately broad: a false positive only costs a spawn.
    private static readonly Regex ConversionAttributePattern = new(@"\b(filter|ident|working-tree-encoding)\b", RegexOptions.CultureInvariant);

    // Config that changes what git writes into the index file, or that libgit2's rewrite of it could not carry over.
    private static readonly string[] IndexFeatureKeys =
    [
        "core.fsmonitor", "core.untrackedcache", "core.splitindex", "core.sparsecheckout", "index.sparse", "feature.manyfiles", "index.skiphash",
    ];

    /// <summary>
    /// <c>.git/index.lock</c>, created exclusively before the index is read and held through every decision, as git holds it for its whole read-modify-write: no other
    /// writer that honours the lock can finish a write in between, so the index libgit2 read is the one it replaces. The file is opened delete-on-close, so it also
    /// disappears if this process dies. libgit2 cannot write while this lock exists (it takes its own for the write), so <see cref="Release"/> comes first; a writer that
    /// slips into that gap makes libgit2's own lock fail, which is declined to git.
    /// </summary>
    private sealed class HeldIndexLock : IDisposable
    {
        private FileStream? _stream;

        private HeldIndexLock(FileStream stream)
        {
            _stream = stream;
        }

        internal static HeldIndexLock Take(Repository repo)
        {
            var path = Path.Combine(repo.Info.Path, "index.lock");
            try
            {
                return new HeldIndexLock(new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, bufferSize: 1, FileOptions.DeleteOnClose));
            }
            catch (IOException ex) when (File.Exists(path))
            {
                throw new NotSupportedException("index.lock exists (another git process holds it); git reports that in its own words.", ex);
            }
        }

        internal void Release()
        {
            _stream?.Dispose();
            _stream = null;
        }

        public void Dispose() => Release();
    }

    public GitRunResult StageWholeFiles(string workingDirectory, IndexStageMode mode, IReadOnlyList<string> paths, TimeSpan cliTimeout, CancellationToken cancellationToken)
    {
        var listed = mode is IndexStageMode.UpdateListed or IndexStageMode.AddListed;
        if (listed)
        {
            if (paths.Count == 0)
            {
                throw Decline("A listed stage without paths is not a plain git add; git decides.");
            }

            RequireCleanRelativePaths(paths);
        }

        using var repo = OpenForIndexWrite(workingDirectory);
        RequireSafeCrlfNotFatal(repo);
        using var indexLock = HeldIndexLock.Take(repo);
        var entries = LoadOrdinaryIndex(repo, rejectIntentToAdd: true);

        var wanted = listed ? new HashSet<string>(paths, StringComparer.Ordinal) : null;
        var matched = new HashSet<string>(StringComparer.Ordinal);
        if (wanted is not null)
        {
            foreach (var indexed in entries.Keys)
            {
                MarkMatches(indexed, wanted, matched);
            }
        }

        var addUntracked = mode is IndexStageMode.AddAll or IndexStageMode.AddListed;
        var options = new StatusOptions
        {
            Show = StatusShowOption.IndexAndWorkDir,
            IncludeIgnored = false,
            IncludeUntracked = addUntracked,
            RecurseUntrackedDirs = true,
            RecurseIgnoredDirs = false,
            IncludeUnaltered = false,
            DetectRenamesInIndex = false,
            DetectRenamesInWorkDir = false,
        };

        var toAdd = new List<string>();
        var toRemove = new List<string>();
        try
        {
            foreach (var entry in repo.RetrieveStatus(options))
            {
                var path = entry.FilePath;
                if (wanted is not null && !IsAtOrUnder(path, wanted))
                {
                    continue;
                }

                var state = entry.State;
                if (state.HasFlag(FileStatus.Conflicted) || state.HasFlag(FileStatus.Unreadable) || state.HasFlag(FileStatus.TypeChangeInWorkdir))
                {
                    throw Decline($"'{path}' is conflicted, unreadable or changed type; git decides.");
                }

                if (state.HasFlag(FileStatus.DeletedFromWorkdir))
                {
                    toRemove.Add(path);
                }
                else if (state.HasFlag(FileStatus.ModifiedInWorkdir))
                {
                    toAdd.Add(path);
                }
                else if (state.HasFlag(FileStatus.NewInWorkdir) && addUntracked)
                {
                    toAdd.Add(path);
                    if (wanted is not null)
                    {
                        MarkMatches(path, wanted, matched);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            throw new NotSupportedException($"libgit2 could not read the working tree status ({ex.GetType().Name}); git decides.", ex);
        }

        if (wanted is not null && matched.Count != wanted.Count)
        {
            throw Decline("A listed path matches no tracked file and no stageable untracked file, so git words the error or advisory.");
        }

        RequirePlainFilesWithoutConversion(repo, toAdd, entries);

        CommitIndexChanges(repo, indexLock, toRemove.Count + toAdd.Count > 0, () =>
        {
            // Deletions first: on a case-insensitive file system a case-only rename adds the new spelling and removes the old one.
            foreach (var path in toRemove)
            {
                repo.Index.Remove(path);
            }

            foreach (var path in toAdd)
            {
                repo.Index.Add(path);
            }
        });
        return CompletedIndexWrite();
    }

    public GitRunResult UnstagePaths(string workingDirectory, IReadOnlyList<string> paths, TimeSpan cliTimeout, CancellationToken cancellationToken)
    {
        if (paths.Count == 0)
        {
            throw Decline("A whole-index reset also moves ORIG_HEAD, writes a reflog entry and clears merge state; git does it.");
        }

        RequireCleanRelativePaths(paths);
        if (paths.Any(path => path.IndexOfAny(['*', '?', '[']) >= 0))
        {
            throw Decline("git reset reads its paths as globs; a glob character is git's to expand.");
        }

        using var repo = OpenForIndexWrite(workingDirectory);
        using var indexLock = HeldIndexLock.Take(repo);
        var entries = LoadOrdinaryIndex(repo, rejectIntentToAdd: false);
        var wanted = new HashSet<string>(paths, StringComparer.Ordinal);

        var toRemove = new List<string>();
        var toRestore = new List<(string Path, TreeEntry Entry)>();
        try
        {
            // HEAD's files at or under the paths (none on an unborn branch: the reset then simply drops the entries).
            var headFiles = new Dictionary<string, TreeEntry>(StringComparer.Ordinal);
            var tip = repo.Head.Tip;
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

            foreach (var path in entries.Keys)
            {
                if (IsAtOrUnder(path, wanted) && !headFiles.ContainsKey(path))
                {
                    toRemove.Add(path);
                }
            }

            foreach (var (path, headEntry) in headFiles)
            {
                if (entries.TryGetValue(path, out var current) && current.Mode == headEntry.Mode && current.Id == headEntry.Target.Id)
                {
                    continue;
                }

                toRestore.Add((path, headEntry));
            }
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or InvalidCastException)
        {
            throw new NotSupportedException($"libgit2 could not read HEAD's tree ({ex.GetType().Name}); git decides.", ex);
        }

        CommitIndexChanges(repo, indexLock, toRemove.Count + toRestore.Count > 0, () =>
        {
            foreach (var path in toRemove)
            {
                repo.Index.Remove(path);
            }

            foreach (var (path, headEntry) in toRestore)
            {
                repo.Index.Add((Blob)headEntry.Target, path, headEntry.Mode);
            }
        });
        return CompletedIndexWrite();
    }

    private static GitRunResult CompletedIndexWrite() => new(GitRunOutcome.Completed, 0, string.Empty, [], string.Empty, null);

    private static NotSupportedException Decline(string reason) => new(reason);

    /// <summary>S1, S2: the repository at <paramref name="workingDirectory"/>, opened for an index write; declines what libgit2 would write differently from git.</summary>
    private static Repository OpenForIndexWrite(string workingDirectory)
    {
        // The process-environment declines (GIT_DIR, GIT_WORK_TREE, GIT_INDEX_FILE, ... see OverridingEnvironmentVariables), a worktree config and a split index are
        // all decided by the shared open helpers, so the index writes and the reads cannot disagree about them.
        var repo = OpenForIndexRead(workingDirectory) ?? throw Decline($"'{workingDirectory}' is not inside a git repository; git words the error.");
        try
        {
            if (RelativeToWorkdir(repo, workingDirectory).Length != 0)
            {
                throw Decline("The working directory is below the repository root, so git resolves the paths against it.");
            }

            foreach (var key in IndexFeatureKeys)
            {
                if (EffectiveConfigValue(repo, key) is { } value && !IsFalseLike(value))
                {
                    throw Decline($"{key} is '{value}': the index would need extensions or flags libgit2's rewrite does not carry over.");
                }
            }

            if (EffectiveConfigValue(repo, "index.version") is { } version)
            {
                throw Decline($"index.version is '{version}': git decides the index format.");
            }

            return repo;
        }
        catch
        {
            repo.Dispose();
            throw;
        }
    }

    /// <summary>
    /// A config value at every level (local over global over system) also for a linked worktree, whose <c>repo.Config</c> misses the main repository's config (see
    /// <see cref="ConfigValue"/>): the main repository is opened for the read, which sees the global and system files as well.
    /// </summary>
    private static string? EffectiveConfigValue(Repository repo, string key)
    {
        if (!IsLinkedWorktree(repo))
        {
            return repo.Config.Get<string>(key)?.Value;
        }

        using var main = new Repository(CommonDirectory(repo));
        return main.Config.Get<string>(key)?.Value;
    }

    private static bool IsFalseLike(string value) => value.Trim().ToLowerInvariant() is "false" or "no" or "off" or "0";

    /// <summary>S5: core.safecrlf=true makes git fail an add whose conversion is not reversible; false-like and warn only print.</summary>
    private static void RequireSafeCrlfNotFatal(Repository repo)
    {
        if (EffectiveConfigValue(repo, "core.safecrlf") is { } value && !IsFalseLike(value) && !value.Trim().Equals("warn", StringComparison.OrdinalIgnoreCase))
        {
            throw Decline($"core.safecrlf is '{value}': git may refuse the add where libgit2 would not.");
        }
    }

    /// <summary>S3, S4: the index as a path map, after declining the entries libgit2 and git treat differently.</summary>
    private static Dictionary<string, IndexEntry> LoadOrdinaryIndex(Repository repo, bool rejectIntentToAdd)
    {
        if (repo.Submodules.Any())
        {
            throw Decline("The repository has submodules; gitlinks are not staged here.");
        }

        var tip = rejectIntentToAdd ? repo.Head.Tip : null;
        var entries = new Dictionary<string, IndexEntry>(StringComparer.Ordinal);
        foreach (var entry in repo.Index)
        {
            if (entry.StageLevel != StageLevel.Staged)
            {
                throw Decline($"'{entry.Path}' is unmerged; git add and git reset resolve it their own way.");
            }

            if (entry.Mode == Mode.GitLink || entry.AssumeUnchanged)
            {
                throw Decline($"'{entry.Path}' is a gitlink or assume-unchanged entry; git decides.");
            }

            // An intent-to-add entry is the empty blob that HEAD lacks; libgit2 does not expose the flag, so a staged empty new file lands here too (merely slower).
            if (rejectIntentToAdd && entry.Id == EmptyBlobId && (tip is null || tip.Tree[entry.Path] is null))
            {
                throw Decline($"'{entry.Path}' may be an intent-to-add entry, which libgit2 cannot tell from a staged empty file.");
            }

            entries[entry.Path] = entry;
        }

        return entries;
    }

    /// <summary>Records which of <paramref name="wanted"/> are <paramref name="path"/> itself or one of its parent directories.</summary>
    private static void MarkMatches(string path, HashSet<string> wanted, HashSet<string> matched)
    {
        if (wanted.Contains(path))
        {
            matched.Add(path);
        }

        for (var slash = path.LastIndexOf('/'); slash > 0; slash = path.LastIndexOf('/', slash - 1))
        {
            var parent = path[..slash];
            if (wanted.Contains(parent))
            {
                matched.Add(parent);
            }
        }
    }

    /// <summary>S7, S8, S9: every file about to be added must be a plain regular file that git stores byte for byte.</summary>
    private static void RequirePlainFilesWithoutConversion(Repository repo, List<string> toAdd, Dictionary<string, IndexEntry> entries)
    {
        var workdir = repo.Info.WorkingDirectory ?? throw Decline("The repository has no working tree.");
        foreach (var path in toAdd)
        {
            if (path.EndsWith('/'))
            {
                throw Decline($"'{path}' is a directory (an embedded repository); git adds it as a gitlink.");
            }

            if (entries.TryGetValue(path, out var existing)
                && existing.Mode is not (Mode.NonExecutableFile or Mode.NonExecutableGroupWritableFile or Mode.ExecutableFile))
            {
                throw Decline($"'{path}' is tracked as something other than a regular file.");
            }

            var fullPath = Path.Combine(workdir, path);
            FileAttributes attributes;
            try
            {
                attributes = File.GetAttributes(fullPath);
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                throw Decline($"'{path}' vanished while staging.");
            }

            if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            {
                throw Decline($"'{path}' is a directory, symlink or reparse point.");
            }

            if (ContainsCarriageReturn(fullPath))
            {
                throw Decline($"'{path}' contains a carriage return; line-ending conversion is git's.");
            }
        }

        RequireNoConversionAttributes(repo, workdir, toAdd);
    }

    private static bool ContainsCarriageReturn(string fullPath)
    {
        using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, bufferSize: 1, FileOptions.SequentialScan);
        var buffer = new byte[81920];
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            if (buffer.AsSpan(0, read).Contains((byte)'\r'))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>S9: declines when any attributes source that applies to the files mentions a filter, ident or working-tree-encoding attribute.</summary>
    private static void RequireNoConversionAttributes(Repository repo, string workdir, List<string> toAdd)
    {
        var sources = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Path.Combine(workdir, ".gitattributes"),
            Path.Combine(CommonDirectory(repo), "info", "attributes"),
        };

        // .gitattributes of every directory from the root down to each file.
        foreach (var path in toAdd)
        {
            var prefix = string.Empty;
            var segments = path.Split('/');
            for (var i = 0; i < segments.Length - 1; i++)
            {
                prefix += segments[i] + "/";
                sources.Add(Path.Combine(workdir, prefix + ".gitattributes"));
            }
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (EffectiveConfigValue(repo, "core.attributesfile") is { Length: > 0 } configured)
        {
            sources.Add(configured.StartsWith('~') ? home + configured[1..] : configured);
        }
        else
        {
            var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            sources.Add(Path.Combine(string.IsNullOrEmpty(xdg) ? Path.Combine(home, ".config") : xdg, "git", "attributes"));
        }

        foreach (var directory in GlobalSettings.GetConfigSearchPaths(ConfigurationLevel.System))
        {
            sources.Add(Path.Combine(directory, "gitattributes"));
        }

        foreach (var source in sources)
        {
            if (File.Exists(source) && ConversionAttributePattern.IsMatch(File.ReadAllText(source)))
            {
                throw Decline($"'{source}' mentions filter, ident or working-tree-encoding; git applies those.");
            }
        }
    }

    /// <summary>ls-tree -r: the files (blobs) under <paramref name="entry"/>, by repo-relative path; a gitlink is declined.</summary>
    private static void CollectHeadFiles(TreeEntry entry, string path, Dictionary<string, TreeEntry> files)
    {
        switch (entry.TargetType)
        {
            case TreeEntryTargetType.Blob:
                files[path] = entry;
                break;
            case TreeEntryTargetType.Tree:
                foreach (var child in (Tree)entry.Target)
                {
                    CollectHeadFiles(child, path + "/" + child.Name, files);
                }

                break;
            default:
                throw Decline($"'{path}' is a gitlink in HEAD; git decides.");
        }
    }

    /// <summary>
    /// S10, S11: applies <paramref name="mutate"/> to the in-memory index and writes it once, after handing back the lock this call held while it read. A no-op writes
    /// nothing (the lock held at the start already made it fail the way git does when the lock is taken).
    /// </summary>
    private static void CommitIndexChanges(Repository repo, HeldIndexLock heldLock, bool hasChanges, Action mutate)
    {
        try
        {
            if (!hasChanges)
            {
                return;
            }

            mutate();
            // libgit2 takes index.lock itself for the write; a writer that got in since this release makes that fail (declined below).
            heldLock.Release();
            repo.Index.Write();
        }
        catch (LockedFileException ex)
        {
            throw new NotSupportedException("index.lock is held by another process; git reports it in its own words.", ex);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or InvalidCastException)
        {
            throw new NotSupportedException($"libgit2 could not update the index ({ex.GetType().Name}); git decides.", ex);
        }
    }
}
