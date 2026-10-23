using System.Diagnostics;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace RoslynSentinel.Common;

/// <summary>A RoslynSentinel assembly the running server has loaded (identity read from the loaded module, not the file).</summary>
public sealed record LoadedBinary(string Name, string Path, Guid Mvid, string? Configuration);

/// <summary>A loaded assembly for which a newer, different build exists in the repo's bin folders.</summary>
public sealed record StaleAssembly(string Name, string LoadedPath, DateTime LoadedWriteTimeUtc, string NewerPath, DateTime NewerWriteTimeUtc);

/// <summary>A commit after the running binary's build sha that touches server source (hash is the abbreviated <c>%h</c>).</summary>
public sealed record StaleCommit(string Hash, string Subject);

/// <summary>
/// Result of comparing the running server to newer source. <paramref name="StaleAssemblies"/> is filled by the bin-folder
/// MVID scan (unstamped binary); <paramref name="StaleCommits"/> is non-null only on the git path (stamped binary),
/// where it lists the commits since the build sha that touch server source (empty when none).
/// </summary>
public sealed record ServerBinaryStalenessReport(bool IsStale, IReadOnlyList<StaleAssembly> StaleAssemblies, string? ScanRoot, IReadOnlyList<StaleCommit>? StaleCommits = null);

/// <summary>
/// Detects that the running server executes older code than the repo holds. Two paths:
/// a binary stamped with its build sha (<see cref="ServerBuildInfo.BuildSha"/>) is stale when the loaded solution's
/// repo has later commits touching server source (<see cref="EvaluateGit"/>); an unstamped binary falls back to scanning
/// the repo's bin folders, where a newer DLL counts only when its module version id (MVID) differs from the loaded one,
/// so a rebuild with identical content is not a false alarm.
/// </summary>
public static class ServerBinaryStaleness
{
    private const string AssemblyPrefix = "RoslynSentinel.";

    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(5);
    private static readonly object Gate = new();
    private static ServerBinaryStalenessReport? _cached;
    private static long _cachedAtTimestamp;
    private static volatile bool _cacheInvalidated;
    private static volatile string? _loadedSolutionDirectory;

    /// <summary>Repo-relative path prefixes whose changes make a stamped binary stale (server source, not tests/docs).</summary>
    internal static readonly string[] SourcePathPrefixes =
    [
        "RoslynSentinel.Common/",
        "RoslynSentinel.Engines.",
        "RoslynSentinel.Tools.",
        "RoslynSentinel.Server.",
    ];

    private static readonly TimeSpan GitTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Directory of the currently loaded solution, set by the workspace manager when a load completes; null until then.
    /// The git path uses it as the repo to inspect - the loaded solution's directory, not the binary's location,
    /// because the server binary may live outside the repo (per-sha prod builds). Changing it drops the cached result.
    /// </summary>
    public static string? LoadedSolutionDirectory
    {
        get => _loadedSolutionDirectory;
        set
        {
            if (!string.Equals(_loadedSolutionDirectory, value, StringComparison.Ordinal))
            {
                _loadedSolutionDirectory = value;
                _cacheInvalidated = true;
            }
        }
    }

    internal static List<LoadedBinary> GetLoadedBinaries() =>
        AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic
                && !string.IsNullOrEmpty(a.Location)
                && a.GetName().Name?.StartsWith(AssemblyPrefix, StringComparison.Ordinal) == true)
            .Select(a => new LoadedBinary(
                a.GetName().Name!,
                a.Location,
                a.ManifestModule.ModuleVersionId,
                a.GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration))
            .ToList();

    /// <summary>Nearest ancestor of the server binary that holds a solution file (the repo root), or null.</summary>
    internal static string? FindRepoRoot(string binaryPath)
    {
        var dir = string.IsNullOrEmpty(binaryPath) ? null : Path.GetDirectoryName(binaryPath);
        while (!string.IsNullOrEmpty(dir))
        {
            if (Directory.EnumerateFiles(dir, "*.slnx").Any() || Directory.EnumerateFiles(dir, "*.sln").Any())
            {
                return dir;
            }

            dir = Path.GetDirectoryName(dir);
        }

        return null;
    }

    /// <summary>
    /// Build outputs under each top-level project's <c>bin</c> folder. Skips <c>bin-vscode</c> (the server's own
    /// per-window copies), worktree harness clones, and <c>ref</c> folders.
    /// </summary>
    public static IEnumerable<string> EnumerateRepoBinaries(string root)
    {
        foreach (var projectDir in Directory.EnumerateDirectories(root))
        {
            var bin = Path.Combine(projectDir, "bin");
            if (!Directory.Exists(bin) || projectDir.Contains("Worktree", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(bin, AssemblyPrefix + "*.dll", SearchOption.AllDirectories))
            {
                if (!HasSegment(file, "ref"))
                {
                    yield return file;
                }
            }
        }
    }

    internal static bool HasSegment(string path, string segment) =>
        path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(s => s.Equals(segment, StringComparison.OrdinalIgnoreCase));

    /// <summary>Reads the MVID straight from the PE metadata; null when the file is unreadable (e.g. mid-write).</summary>
    internal static Guid? TryReadMvid(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var pe = new PEReader(stream);
            if (!pe.HasMetadata)
            {
                return null;
            }

            var reader = pe.GetMetadataReader();
            return reader.GetGuid(reader.GetModuleDefinition().Mvid);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or BadImageFormatException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    /// Pure comparison used by <see cref="CheckNow"/>. A candidate is a newer build of a loaded assembly when it has the
    /// same assembly name, a later write time, a different MVID, and (when the loaded assembly records one) the same
    /// build configuration directory - so a Release build never flags a Debug-running server. Reports the newest
    /// candidate per assembly.
    /// </summary>
    public static ServerBinaryStalenessReport Evaluate(IReadOnlyList<LoadedBinary> loaded, IEnumerable<string> candidatePaths, string? scanRoot)
    {
        var byName = loaded.GroupBy(b => b.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var newest = new Dictionary<string, StaleAssembly>(StringComparer.OrdinalIgnoreCase);

        foreach (var path in candidatePaths)
        {
            var name = Path.GetFileNameWithoutExtension(path);
            if (!byName.TryGetValue(name, out var binary)
                || string.Equals(Path.GetFullPath(path), Path.GetFullPath(binary.Path), StringComparison.OrdinalIgnoreCase)
                || (!string.IsNullOrEmpty(binary.Configuration) && !HasSegment(path, binary.Configuration)))
            {
                continue;
            }

            var loadedTime = File.GetLastWriteTimeUtc(binary.Path);
            var candidateTime = File.GetLastWriteTimeUtc(path);
            if (candidateTime <= loadedTime
                || (newest.TryGetValue(name, out var best) && best.NewerWriteTimeUtc >= candidateTime))
            {
                continue;
            }

            var mvid = TryReadMvid(path);
            if (mvid is null || mvid == binary.Mvid)
            {
                continue;
            }

            newest[name] = new StaleAssembly(name, binary.Path, loadedTime, path, candidateTime);
        }

        var stale = newest.Values.OrderBy(s => s.Name, StringComparer.Ordinal).ToList();
        return new ServerBinaryStalenessReport(stale.Count > 0, stale, scanRoot);
    }

    /// <summary>
    /// Git path: lists commits in <c>&lt;buildSha&gt;..HEAD</c> of the repo containing <paramref name="solutionDirectory"/> that
    /// touch a path under <see cref="SourcePathPrefixes"/>; stale iff there is at least one. An unknown sha, missing git,
    /// timeout or a non-repo directory all yield "not stale" (the check is a diagnostic and must never throw).
    /// </summary>
    public static ServerBinaryStalenessReport EvaluateGit(string buildSha, string solutionDirectory)
    {
        var notStale = new ServerBinaryStalenessReport(false, [], solutionDirectory, []);
        try
        {
            // Hex only: also keeps a sha-looking argument from ever being read as a git option.
            if (buildSha.Length is < 7 or > 40 || !buildSha.All(char.IsAsciiHexDigit) || !Directory.Exists(solutionDirectory))
            {
                return notStale;
            }

            // core.quotepath=false keeps non-ASCII paths unquoted; a trailing "--" ends the revision list.
            var run = GitProcess.TryRun(
                solutionDirectory, GitTimeout, "-c", "core.quotepath=false", "log", buildSha + "..HEAD", "--name-only", "--format=%h%x09%s", "--");
            if (run is not { ExitCode: 0 })
            {
                return notStale;
            }

            var commits = ParseGitLog(run.Value.StdOut, SourcePathPrefixes);
            return new ServerBinaryStalenessReport(commits.Count > 0, [], solutionDirectory, commits);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return notStale;
        }
    }

    /// <summary>
    /// Pure parser for <c>git log --name-only --format=%h%x09%s</c> output: a header line is <c>hash&lt;TAB&gt;subject</c>
    /// (the subject may itself contain tabs), followed by its changed paths (git quotes any path holding a tab, so a
    /// path line never contains one) and a blank line. Returns, in log order, the commits with at least one path that
    /// starts with one of <paramref name="pathPrefixes"/> (ordinal, '/' separators).
    /// </summary>
    internal static IReadOnlyList<StaleCommit> ParseGitLog(string output, IReadOnlyList<string> pathPrefixes)
    {
        var commits = new List<StaleCommit>();
        string? hash = null;
        var subject = string.Empty;
        var matched = false;

        foreach (var rawLine in output.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.Length == 0)
            {
                continue;
            }

            var tab = line.IndexOf('\t');
            if (tab >= 0)
            {
                if (hash is not null && matched)
                {
                    commits.Add(new StaleCommit(hash, subject));
                }

                hash = line[..tab];
                subject = line[(tab + 1)..];
                matched = false;
            }
            else if (hash is not null && !matched)
            {
                matched = pathPrefixes.Any(p => line.StartsWith(p, StringComparison.Ordinal));
            }
        }

        if (hash is not null && matched)
        {
            commits.Add(new StaleCommit(hash, subject));
        }

        return commits;
    }

    /// <summary>Checks now (uncached) whether this process runs older code than the repo holds. Never throws.</summary>
    public static ServerBinaryStalenessReport CheckNow() => CheckNow(ServerBuildInfo.BuildSha, LoadedSolutionDirectory);

    /// <summary>
    /// Dispatch: a stamped binary with a known loaded-solution directory uses the git path; anything else (no sha, or
    /// no solution loaded yet) uses the bin-folder MVID scan so out-of-tool builds still register.
    /// </summary>
    internal static ServerBinaryStalenessReport CheckNow(string? buildSha, string? solutionDirectory)
    {
        if (buildSha is not null && solutionDirectory is not null)
        {
            return EvaluateGit(buildSha, solutionDirectory);
        }

        string? root = null;
        try
        {
            root = FindRepoRoot(ServerBuildInfo.BinaryPath);
            return root is null
                ? new ServerBinaryStalenessReport(false, [], null)
                : Evaluate(GetLoadedBinaries(), EnumerateRepoBinaries(root), root);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // A diagnostic must never fail a response: report "not stale" rather than throw.
            return new ServerBinaryStalenessReport(false, [], root);
        }
    }

    /// <summary>The latest <see cref="CheckNow"/> result, reused for a few seconds so per-response stamping stays cheap.</summary>
    public static ServerBinaryStalenessReport Current
    {
        get
        {
            lock (Gate)
            {
                if (_cached is null || _cacheInvalidated || Stopwatch.GetElapsedTime(_cachedAtTimestamp) > CacheTtl)
                {
                    _cacheInvalidated = false;
                    _cached = CheckNow();
                    _cachedAtTimestamp = Stopwatch.GetTimestamp();
                }

                return _cached;
            }
        }
    }

    /// <summary>
    /// Value for the response envelope's <c>IsServerBinaryStale</c>: <c>true</c> when stale, otherwise null so the
    /// field is omitted from the JSON. Always null outside a server process (entry assembly not RoslynSentinel.Server.*),
    /// so unit tests never scan the repo or see the flag.
    /// </summary>
    public static bool? EnvelopeFlag =>
        Assembly.GetEntryAssembly()?.GetName().Name?.StartsWith("RoslynSentinel.Server.", StringComparison.Ordinal) == true
        && Current.IsStale
            ? true
            : null;
}
