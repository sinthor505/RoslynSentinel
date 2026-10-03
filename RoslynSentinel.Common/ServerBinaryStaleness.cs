using System.Diagnostics;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace RoslynSentinel.Common;

/// <summary>A RoslynSentinel assembly the running server has loaded (identity read from the loaded module, not the file).</summary>
public sealed record LoadedBinary(string Name, string Path, Guid Mvid, string? Configuration);

/// <summary>A loaded assembly for which a newer, different build exists in the repo's bin folders.</summary>
public sealed record StaleAssembly(string Name, string LoadedPath, DateTime LoadedWriteTimeUtc, string NewerPath, DateTime NewerWriteTimeUtc);

/// <summary>Result of comparing the server's loaded binaries to the builds found under the repo root.</summary>
public sealed record ServerBinaryStalenessReport(bool IsStale, IReadOnlyList<StaleAssembly> StaleAssemblies, string? ScanRoot);

/// <summary>
/// Detects that the running server executes older binaries than a build sitting in the repo. A newer repo DLL
/// counts only when its module version id (MVID) differs from the loaded one, so a rebuild with identical content
/// is not a false alarm. Deterministic builds keep the MVID stable for unchanged content, which is why no
/// per-build version stamp is needed.
/// </summary>
public static class ServerBinaryStaleness
{
    private const string AssemblyPrefix = "RoslynSentinel.";

    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(5);
    private static readonly object Gate = new();
    private static ServerBinaryStalenessReport? _cached;
    private static long _cachedAtTimestamp;

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

    /// <summary>Scans the repo now (uncached) and compares it to what this process has loaded. Never throws.</summary>
    public static ServerBinaryStalenessReport CheckNow()
    {
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
                if (_cached is null || Stopwatch.GetElapsedTime(_cachedAtTimestamp) > CacheTtl)
                {
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
