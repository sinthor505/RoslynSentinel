using System.Collections.Concurrent;

namespace RoslynSentinel.Common;

/// <summary>
/// The abbreviation length git itself uses for a repository, probed once with <c>git rev-parse --short HEAD</c> and cached.
/// <para>
/// Why a probe: git's default abbreviation (<c>%h</c>, <c>rev-parse --short</c>) depends on the repository's object count (7 below 16,384 packed
/// objects, 8 below 65,536, 9 below 262,144, ...) and on <c>core.abbrev</c>; libgit2 has no such rule and the count cannot be computed portably
/// (multi-pack-index, alternates). Asking git once is exact for every configuration. The length is then the minimum handed to
/// <c>ObjectDatabase.ShortenObjectId</c>, which already extends it on ambiguity the way git does.
/// </para>
/// <para>
/// Cache: keyed by the repository's git common directory (a linked worktree shares its repository's entry), case-insensitive on Windows. The solution's
/// repository is refreshed on every LoadSolution (<see cref="Refresh"/>, run by <see cref="GitBackendWarmup.StartRepoProbe"/>); any other repository
/// (reached by a <c>repoPath</c>) is probed lazily by the first <see cref="Get"/> that needs an abbreviation, synchronously on that call, once.
/// A failed probe (unborn HEAD, git missing, timeout, unparsable output) is never cached and makes <see cref="Get"/> throw
/// <see cref="NotSupportedException"/>, which the fallback reader turns into a CLI answer.
/// </para>
/// <para>
/// Accepted staleness: the cached length is the one git printed at probe time. Objects accumulate slowly, so it can lag git by one digit between the
/// moment the object count crosses a threshold (or <c>core.abbrev</c> is edited) and the next LoadSolution (or server restart, for a repoPath
/// repository). The probe also reports the length git needed for HEAD's own prefix, which exceeds the automatic minimum only in the (very rare) case
/// that HEAD's short prefix collides with another object.
/// </para>
/// </summary>
internal sealed class ShortHashLengths
{
    private static readonly TimeSpan DefaultProbeTimeout = TimeSpan.FromSeconds(10);

    /// <summary>The process-wide cache, probing through the git CLI.</summary>
    internal static ShortHashLengths Shared { get; } = new(ProbeWithCli);

    private readonly Func<string, TimeSpan, int?> _probe;
    private readonly ConcurrentDictionary<string, int> _byCommonDirectory =
        new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private int _lastRefreshed;

    /// <param name="probe">Returns the abbreviation length git prints for HEAD of the working directory, or null when it cannot be determined.</param>
    internal ShortHashLengths(Func<string, TimeSpan, int?> probe)
    {
        _probe = probe;
    }

    /// <summary>
    /// The length last stored by <see cref="Refresh"/> (the solution's repository, since only the warmup refreshes); null before the first
    /// successful refresh or after a failed one.
    /// </summary>
    internal int? SolutionRepositoryLength => Volatile.Read(ref _lastRefreshed) is var value and > 0 ? value : null;

    /// <summary>The cached length for a common directory without probing; null when none is cached.</summary>
    internal int? Peek(string commonDirectory) =>
        _byCommonDirectory.TryGetValue(Normalize(commonDirectory), out var length) ? length : null;

    /// <summary>
    /// The abbreviation length for the repository with this common directory: the cached one, else probed now (synchronously, one CLI spawn).
    /// </summary>
    /// <exception cref="NotSupportedException">The probe failed, so git must answer.</exception>
    internal int Get(string commonDirectory, string workingDirectory, TimeSpan cliTimeout)
    {
        var key = Normalize(commonDirectory);
        if (_byCommonDirectory.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var probed = _probe(workingDirectory, cliTimeout > TimeSpan.Zero ? cliTimeout : DefaultProbeTimeout)
            ?? throw new NotSupportedException("git's abbreviation length could not be probed (unborn HEAD, git missing, a timeout or unreadable output), so git answers.");
        _byCommonDirectory[key] = probed;
        return probed;
    }

    /// <summary>
    /// Probes again and replaces the cached length. On failure the entry is dropped (the next <see cref="Get"/> probes lazily and declines to the CLI
    /// if that fails too) and null is returned.
    /// </summary>
    internal int? Refresh(string commonDirectory, string workingDirectory, TimeSpan cliTimeout)
    {
        var key = Normalize(commonDirectory);
        var probed = _probe(workingDirectory, cliTimeout > TimeSpan.Zero ? cliTimeout : DefaultProbeTimeout);
        if (probed is null)
        {
            _byCommonDirectory.TryRemove(key, out _);
            Volatile.Write(ref _lastRefreshed, 0);
            return null;
        }

        _byCommonDirectory[key] = probed.Value;
        Volatile.Write(ref _lastRefreshed, probed.Value);
        return probed;
    }

    private static string Normalize(string directory) =>
        Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    /// <summary>One <c>git rev-parse --short HEAD</c>; its output's length, or null when it fails or the output is not a 4-40 digit hex id.</summary>
    private static int? ProbeWithCli(string workingDirectory, TimeSpan timeout)
    {
        var run = GitProcess.TryRun(workingDirectory, timeout, "rev-parse", "--short", "HEAD");
        if (run is not { ExitCode: 0 })
        {
            return null;
        }

        var text = run.Value.StdOut.Trim();
        return text.Length is >= 4 and <= 40 && text.All(Uri.IsHexDigit) ? text.Length : null;
    }
}
