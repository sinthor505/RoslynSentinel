using System.Collections.Concurrent;
using System.Diagnostics;
using LibGit2Sharp;

namespace RoslynSentinel.Common;

/// <summary>
/// Warms and reports the libgit2 backend of <see cref="GitReaders.Default"/> without ever sitting on the git call path.
/// <para>
/// Native task: started at host start (<see cref="Start"/>), captured, never awaited by the host and never faulting. It reads
/// <c>GlobalSettings.Version</c>, which forces the native library to load without needing a repository, and records
/// Libgit2 + version, or CliFallback + the exception type and message (logged once to stderr, never stdout: stdout is the
/// MCP JSON-RPC pipe). Repo task: <see cref="StartRepoProbe"/> after a successful solution load starts a second captured,
/// never-faulting task that awaits the native task and then opens the solution's repository the way a read would, so a
/// repository libgit2 rejects (unsupported extension, ownership check) shows up before the first real call. A load never
/// waits for it and each load replaces it.
/// </para>
/// <para>
/// Callers of the git seam neither wait for nor consult any of this: LibGit2Sharp loads its natives in a static type
/// initializer, so a racing call blocks on the same initializer, and a failed load is cached as a
/// <see cref="TypeInitializationException"/>, which <see cref="FallbackGitReader"/> already catches. The only waiter is
/// <see cref="Report"/>, called by McpServerStatus, which waits a bounded time and reports "warming" on timeout.
/// </para>
/// </summary>
public sealed class GitBackendWarmup
{
    internal const string BackendLibgit2 = "Libgit2";
    internal const string BackendCliFallback = "CliFallback";
    internal const string Warming = "warming";
    internal const string RepoOk = "ok";
    internal const string RepoNoRepository = "no repository";
    internal const string RepoNotStarted = "not started (no solution loaded)";

    /// <summary>How long McpServerStatus waits for the warmup before it reports "warming".</summary>
    internal static readonly TimeSpan StatusWait = TimeSpan.FromSeconds(2);

    private static readonly TimeSpan RepoProbeCliTimeout = TimeSpan.FromSeconds(5);

    /// <summary>The process-wide instance: probes libgit2 itself and reads the counters of <see cref="GitReaders.Default"/>.</summary>
    public static GitBackendWarmup Shared { get; } =
        new(ProbeNativeLibrary, repoProbe: null, GitReaders.Default, FallbackGitReader.WriteToStandardError);

    private readonly Func<string> _nativeProbe;
    private readonly Func<string, string> _repoProbe;
    private readonly FallbackGitReader _reader;
    private readonly ShortHashLengths _lengths;
    private readonly InProcessCommitRoute _commitRoute;
    private readonly Action<string> _log;
    private readonly Lazy<Task<GitBackendStatus>> _native;
    private readonly ConcurrentDictionary<string, byte> _loggedRepoFailureKinds = new(StringComparer.Ordinal);
    private Task<string>? _repoTask;

    /// <param name="nativeProbe">Forces the native load and returns the libgit2 version text; throws when the load fails.</param>
    /// <param name="repoProbe">Probes the repository of a directory and returns "ok", "no repository" or a result text; throws when libgit2 rejects it. Null (the default) opens the repository with libgit2, reads HEAD and refreshes the short-hash length in <paramref name="lengths"/>.</param>
    /// <param name="reader">Source of the fallback counters and first fallback reason that <see cref="Report"/> shows.</param>
    /// <param name="log">Receives the one-line failure messages; defaults to stderr.</param>
    /// <param name="lengths">The short-hash length cache the default repo probe refreshes and <see cref="Report"/> reads; defaults to <see cref="ShortHashLengths.Shared"/>.</param>
    /// <param name="commitRoute">Source of the in-process commit counters that <see cref="Report"/> shows; defaults to <see cref="InProcessCommitRoute.Shared"/>.</param>
    internal GitBackendWarmup(Func<string> nativeProbe, Func<string, string>? repoProbe, FallbackGitReader reader, Action<string>? log = null, ShortHashLengths? lengths = null, InProcessCommitRoute? commitRoute = null)
    {
        _lengths = lengths ?? ShortHashLengths.Shared;
        _commitRoute = commitRoute ?? InProcessCommitRoute.Shared;
        _nativeProbe = nativeProbe;
        _repoProbe = repoProbe ?? (directory => ProbeRepository(directory, new Libgit2GitReader(_lengths)));
        _reader = reader;
        _log = log ?? FallbackGitReader.WriteToStandardError;
        _native = new Lazy<Task<GitBackendStatus>>(() => Task.Run(RunNativeProbe), LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>The captured native-load task. Never faults; starts on first access if <see cref="Start"/> was not called.</summary>
    internal Task<GitBackendStatus> NativeStatus => _native.Value;

    /// <summary>The captured repo-probe task of the latest <see cref="StartRepoProbe"/>; null before the first one.</summary>
    internal Task<string>? RepoStatus => Volatile.Read(ref _repoTask);

    /// <summary>Starts the native probe in the background and returns at once. Idempotent.</summary>
    public void Start() => _ = _native.Value;

    /// <summary>
    /// Starts (replacing any earlier) the repo probe for the directory of a just-loaded solution and returns at once; the
    /// caller never awaits it.
    /// </summary>
    public void StartRepoProbe(string solutionDirectory)
    {
        var native = _native.Value;
        Volatile.Write(ref _repoTask, Task.Run(() => RunRepoProbeAsync(native, solutionDirectory)));
    }

    /// <summary>
    /// Snapshot for McpServerStatus: waits up to <paramref name="wait"/> in total for the captured tasks and reports
    /// "warming" for whatever has not finished. Never throws and never blocks longer than <paramref name="wait"/>.
    /// </summary>
    // Sync wait on purpose: McpServerStatusTool is a synchronous tool method used by many tests and returns at once when the
    // tasks are done (the normal case); only a still-warming backend holds a pool thread, and only up to the bound.
    internal GitBackendSnapshot Report(TimeSpan wait)
    {
        var elapsed = Stopwatch.StartNew();
        var native = _native.Value;
        var nativeDone = native.IsCompleted || native.Wait(wait);
        var status = nativeDone ? native.Result : null;

        string repo;
        var repoTask = RepoStatus;
        if (repoTask is null)
        {
            repo = RepoNotStarted;
        }
        else
        {
            var remaining = wait - elapsed.Elapsed;
            repo = repoTask.IsCompleted || (remaining > TimeSpan.Zero && repoTask.Wait(remaining)) ? repoTask.Result : Warming;
        }

        return new GitBackendSnapshot(
            ActiveBackend: status?.Backend ?? Warming,
            Libgit2Version: status?.Libgit2Version,
            NativeLoad: status is null ? Warming : status.Failure is null ? "ok" : "failed: " + status.Failure,
            RepoProbe: repo,
            FirstFallbackReason: _reader.FirstFallbackReason,
            Libgit2Count: _reader.Libgit2Count,
            CliFallbackCount: _reader.CliFallbackCount,
            ShortHashLength: _lengths.SolutionRepositoryLength,
            DeclineCount: _reader.DeclineCount,
            LastDeclineReason: _reader.LastDeclineReason,
            InProcessCommitCount: _commitRoute.TakenCount,
            InProcessCommitDeclineCount: _commitRoute.DeclineCount,
            LastInProcessCommitDeclineReason: _commitRoute.LastDeclineReason);
    }

    private GitBackendStatus RunNativeProbe()
    {
        try
        {
            return new GitBackendStatus(BackendLibgit2, _nativeProbe() ?? string.Empty, Failure: null);
        }
        // Any exception: this task must never fault, whatever the native loader throws (DllNotFoundException,
        // BadImageFormatException, TypeInitializationException, ...).
        catch (Exception ex)
        {
            var reason = Describe(ex);
            SafeLog($"[RoslynSentinel.GitBackend] libgit2 native library did not load, git reads use the git CLI instead (logged once): {reason}");
            return new GitBackendStatus(BackendCliFallback, Libgit2Version: null, reason);
        }
    }

    private async Task<string> RunRepoProbeAsync(Task<GitBackendStatus> native, string solutionDirectory)
    {
        try
        {
            var status = await native.ConfigureAwait(false);
            if (status.Backend != BackendLibgit2)
            {
                return $"fallback: libgit2 native library did not load ({status.Failure})";
            }

            return _repoProbe(solutionDirectory);
        }
        // Any exception: this task must never fault; a repo-specific rejection is the expected content of this result.
        catch (Exception ex)
        {
            var reason = Describe(ex);
            if (_loggedRepoFailureKinds.TryAdd(ex.GetType().FullName ?? ex.GetType().Name, 0))
            {
                SafeLog($"[RoslynSentinel.GitBackend] libgit2 cannot read the loaded solution's repository, git reads there use the git CLI instead (logged once per exception type): {reason}");
            }

            return "fallback: " + reason;
        }
    }

    private void SafeLog(string line)
    {
        try
        {
            _log(line);
        }
        catch (Exception)
        {
            // A diagnostic must never fault the warmup task.
        }
    }

    private static string Describe(Exception ex) => $"{ex.GetType().Name}: {ex.Message}";

    private static string ProbeNativeLibrary() => GlobalSettings.Version.ToString();

    // The same open a real read does (inside the reader's per-call using), so a repo libgit2 rejects throws here. Then one `git rev-parse --short HEAD`
    // refreshes the cached abbreviation length (every LoadSolution; see ShortHashLengths for the accepted staleness in between).
    private static string ProbeRepository(string solutionDirectory, Libgit2GitReader reader)
    {
        if (Repository.Discover(solutionDirectory) is null)
        {
            return RepoNoRepository;
        }

        _ = reader.GetHeadSha(solutionDirectory, shortForm: false, RepoProbeCliTimeout);
        return reader.RefreshShortHashLength(solutionDirectory, RepoProbeCliTimeout) is null
            ? RepoOk + " (short-hash length not probed: reads that abbreviate use the git CLI)"
            : RepoOk;
    }
}

/// <summary>Outcome of the native-load probe: which backend the process can use, the libgit2 version, or why it cannot.</summary>
/// <param name="Backend">"Libgit2" or "CliFallback".</param>
/// <param name="Libgit2Version">Version text from the loaded library; null on failure.</param>
/// <param name="Failure">"ExceptionType: message" of the failed load; null on success.</param>
internal sealed record GitBackendStatus(string Backend, string? Libgit2Version, string? Failure);

/// <summary>
/// The <c>gitBackend</c> block of McpServerStatus. <c>ActiveBackend</c> is Libgit2, CliFallback or warming;
/// <c>NativeLoad</c> is ok, "failed: ..." or warming; <c>RepoProbe</c> is ok, "no repository", "fallback: ...", "not started (no
/// solution loaded)" or warming. <c>ShortHashLength</c> is the abbreviation length git printed for the solution's repository at the last LoadSolution
/// (null until probed, or when the probe failed). The remaining fields are the process-wide counters of the git reader seam.
/// <c>FirstFallbackReason</c> and <c>CliFallbackCount</c> mean REAL FAILURES only (native load, a repository libgit2 cannot open, an I/O error):
/// null and 0 are the healthy values. A planned decline (a request libgit2 deliberately leaves to the git CLI, such as pathspec magic or a CR in a
/// file to stage) is reported in <c>DeclineCount</c> / <c>LastDeclineReason</c> instead and is normal traffic, not a fault.
/// <c>Libgit2Count</c> is the calls libgit2 served.
/// The last three fields are the in-process commit route's counters (git config <c>roslynsentinel.inProcessCommit</c>, off by default): <c>InProcessCommitCount</c> commits made in process,
/// <c>InProcessCommitDeclineCount</c> / <c>LastInProcessCommitDeclineReason</c> the commits of an enabled route that the git CLI made instead (a hook, signing, a merge in progress, a lost race, ...). Normal traffic, not a fault.
/// </summary>
public sealed record GitBackendSnapshot(
    string ActiveBackend,
    string? Libgit2Version,
    string NativeLoad,
    string RepoProbe,
    string? FirstFallbackReason,
    long Libgit2Count,
    long CliFallbackCount,
    int? ShortHashLength = null,
    long DeclineCount = 0,
    string? LastDeclineReason = null,
    long InProcessCommitCount = 0,
    long InProcessCommitDeclineCount = 0,
    string? LastInProcessCommitDeclineReason = null);
