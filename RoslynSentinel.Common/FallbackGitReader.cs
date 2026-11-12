using System.Collections.Concurrent;
using LibGit2Sharp;

namespace RoslynSentinel.Common;

/// <summary>
/// The reader the server's probes use: tries the primary (libgit2) implementation first and answers from the fallback (CLI)
/// when the primary throws a native-load or libgit2 failure. Every call is wrapped, so a native library that loads fine for
/// one call and fails on a later one (or a <see cref="TypeInitializationException"/> that sticks after a failed static
/// constructor) is still covered.
/// <para>
/// Two different reasons send a call to the CLI, and they are counted apart.
/// A decline is planned: the libgit2 reader throws <see cref="NotSupportedException"/> (not <see cref="PlatformNotSupportedException"/>) for a request it
/// cannot reproduce byte for byte (pathspec magic, a carriage return in a file to stage, an unmerged or intent-to-add entry, a held index.lock, a config
/// setting that changes the answer, a failed abbreviation-length probe, ...). It is the only decline signal: no null or sentinel result means a decline.
/// A decline is normal, so it is never written to stderr (only a <c>Debug</c> trace) and it is not a fault: see <see cref="DeclineCount"/> and
/// <see cref="LastDeclineReason"/>.
/// A failure is real: <see cref="LibGit2SharpException"/> (for example a repository libgit2 cannot open), <see cref="DllNotFoundException"/>,
/// <see cref="BadImageFormatException"/>, <see cref="TypeInitializationException"/>, <see cref="PlatformNotSupportedException"/>, <see cref="IOException"/> or
/// <see cref="UnauthorizedAccessException"/>. A failure is never silent: the first one for each distinct exception type is written once to stderr
/// (stdout is the MCP JSON-RPC pipe) with the exception's type name and message, and it is counted in <see cref="CliFallbackCount"/> and kept in
/// <see cref="FirstFallbackReason"/>. Any other exception type is not caught and propagates to the caller.
/// </para>
/// <para>
/// The active backend is observable: <see cref="LastBackend"/> names the implementation that answered the most recent call (the CLI for a decline and for a
/// failure alike), <see cref="Libgit2Count"/> counts calls libgit2 served, <see cref="DeclineCount"/> and <see cref="CliFallbackCount"/> count the calls the
/// CLI served after a decline and after a failure, and the two reason properties keep the first failure and the latest decline.
/// All of it is thread-safe. A test that must prove libgit2 (not the CLI) serves a call asserts on these; a call that must be declined asserts on the
/// decline members and that the failure members stayed untouched.
/// </para>
/// </summary>
internal sealed class FallbackGitReader : IGitReader
{
    private readonly IGitReader _primary;
    private readonly IGitReader _fallback;
    private readonly Action<string> _log;
    private readonly ConcurrentDictionary<string, byte> _loggedKinds = new(StringComparer.Ordinal);
    private long _primaryCount;
    private long _fallbackCount;
    private long _declineCount;
    private int _lastBackend;
    private string? _firstFallbackReason;
    private string? _lastDeclineReason;

    /// <param name="primary">Tried first.</param>
    /// <param name="fallback">Answers when <paramref name="primary"/> throws a native-load or libgit2 failure.</param>
    /// <param name="log">Receives one line per distinct fallback exception type; defaults to stderr.</param>
    internal FallbackGitReader(IGitReader primary, IGitReader fallback, Action<string>? log = null)
    {
        _primary = primary;
        _fallback = fallback;
        _log = log ?? WriteToStandardError;
    }

    /// <summary>The implementation that answered the most recent call (<see cref="GitReaderBackend.None"/> before any).</summary>
    internal GitReaderBackend LastBackend => (GitReaderBackend)Volatile.Read(ref _lastBackend);

    /// <summary>Calls answered by the primary (libgit2) implementation.</summary>
    internal long Libgit2Count => Interlocked.Read(ref _primaryCount);

    /// <summary>
    /// Calls answered by the CLI because the primary REALLY FAILED (native library missing or broken, a repository libgit2 cannot open, an I/O error).
    /// A planned decline is not counted here: see <see cref="DeclineCount"/>. Zero in a healthy environment.
    /// </summary>
    internal long CliFallbackCount => Interlocked.Read(ref _fallbackCount);

    /// <summary>
    /// "ExceptionType: message" of the first REAL failure that caused a fallback; null while none has. A planned decline never sets it
    /// (see <see cref="LastDeclineReason"/>), so null is the expected value in a healthy environment.
    /// </summary>
    internal string? FirstFallbackReason => Volatile.Read(ref _firstFallbackReason);

    /// <summary>Calls answered by the CLI after the primary planned-declined them (<see cref="NotSupportedException"/>). Normal traffic, not a fault.</summary>
    internal long DeclineCount => Interlocked.Read(ref _declineCount);

    /// <summary>The message of the most recent planned decline; null while none has happened.</summary>
    internal string? LastDeclineReason => Volatile.Read(ref _lastDeclineReason);

    public string? GetHeadSha(string workingDirectory, bool shortForm, TimeSpan cliTimeout) =>
        Invoke(reader => reader.GetHeadSha(workingDirectory, shortForm, cliTimeout));

    public IReadOnlyList<ChangedCommit>? GetCommitsSince(string workingDirectory, string sha, TimeSpan cliTimeout) =>
        Invoke(reader => reader.GetCommitsSince(workingDirectory, sha, cliTimeout));

    public IReadOnlyList<WorkingTreeChange>? GetStatus(string workingDirectory, IReadOnlyList<string>? pathspecs, TimeSpan cliTimeout) =>
        Invoke(reader => reader.GetStatus(workingDirectory, pathspecs, cliTimeout));

    public WorkingTreeStatus? GetRepositoryStatus(string workingDirectory, TimeSpan cliTimeout) =>
        Invoke(reader => reader.GetRepositoryStatus(workingDirectory, cliTimeout));

    public IReadOnlyList<LogCommit>? GetLog(string workingDirectory, int maxCount, string? revision, IReadOnlyList<string>? paths, string? pickaxe, bool pickaxeRegex, bool follow, TimeSpan cliTimeout) =>
        Invoke(reader => reader.GetLog(workingDirectory, maxCount, revision, paths, pickaxe, pickaxeRegex, follow, cliTimeout));

    public string? ReadBlobAtHead(string workingDirectory, string relativePath, TimeSpan cliTimeout) =>
        Invoke(reader => reader.ReadBlobAtHead(workingDirectory, relativePath, cliTimeout));

    public IReadOnlyList<string>? ListTrackedPaths(string workingDirectory, TimeSpan cliTimeout) =>
        Invoke(reader => reader.ListTrackedPaths(workingDirectory, cliTimeout));

    public IReadOnlyList<string>? ListIndexPathsMatching(string workingDirectory, IReadOnlyList<string> paths, TimeSpan cliTimeout) =>
        Invoke(reader => reader.ListIndexPathsMatching(workingDirectory, paths, cliTimeout));

    public IReadOnlyList<string>? ListHeadPathsMatching(string workingDirectory, IReadOnlyList<string> paths, TimeSpan cliTimeout) =>
        Invoke(reader => reader.ListHeadPathsMatching(workingDirectory, paths, cliTimeout));

    public IReadOnlyList<string>? FindIgnoredPaths(string workingDirectory, IReadOnlyList<string> paths, TimeSpan cliTimeout) =>
        Invoke(reader => reader.FindIgnoredPaths(workingDirectory, paths, cliTimeout));

    public IReadOnlyList<BranchRef>? ListBranches(string workingDirectory, TimeSpan cliTimeout) =>
        Invoke(reader => reader.ListBranches(workingDirectory, cliTimeout));

    public IReadOnlyList<StashRef>? ListStashes(string workingDirectory, TimeSpan cliTimeout) =>
        Invoke(reader => reader.ListStashes(workingDirectory, cliTimeout));

    public int? CountStashes(string workingDirectory, TimeSpan cliTimeout) =>
        Invoke(reader => reader.CountStashes(workingDirectory, cliTimeout));

    public IReadOnlyList<NameStatusEntry>? GetNameStatus(string workingDirectory, string target, IReadOnlyList<string>? paths, TimeSpan cliTimeout) =>
        Invoke(reader => reader.GetNameStatus(workingDirectory, target, paths, cliTimeout));

    public IReadOnlyList<NameStatusEntry>? GetCommitNameStatus(string workingDirectory, string revision, IReadOnlyList<string>? paths, TimeSpan cliTimeout) =>
        Invoke(reader => reader.GetCommitNameStatus(workingDirectory, revision, paths, cliTimeout));

    public CommitHeader? GetCommitHeader(string workingDirectory, string revision, TimeSpan cliTimeout) =>
        Invoke(reader => reader.GetCommitHeader(workingDirectory, revision, cliTimeout));

    public IReadOnlyList<CommitMessage>? GetCommitMessages(string workingDirectory, TimeSpan cliTimeout) =>
        Invoke(reader => reader.GetCommitMessages(workingDirectory, cliTimeout));

    public string? FindCommitByTrailer(string workingDirectory, string key, string value, int maxCommits, TimeSpan cliTimeout) =>
        Invoke(reader => reader.FindCommitByTrailer(workingDirectory, key, value, maxCommits, cliTimeout));

    public bool? GetBoolConfig(string workingDirectory, string key, TimeSpan cliTimeout) =>
        Invoke(reader => reader.GetBoolConfig(workingDirectory, key, cliTimeout));

    public HeadWithParents? GetHeadWithParents(string workingDirectory, TimeSpan cliTimeout) =>
        Invoke(reader => reader.GetHeadWithParents(workingDirectory, cliTimeout));

    public string? GetMergeHead(string workingDirectory, TimeSpan cliTimeout) =>
        Invoke(reader => reader.GetMergeHead(workingDirectory, cliTimeout));

    public string? ResolveCommitAbbreviation(string workingDirectory, string abbreviation, TimeSpan cliTimeout) =>
        Invoke(reader => reader.ResolveCommitAbbreviation(workingDirectory, abbreviation, cliTimeout));

    public string? GetCommitMessage(string workingDirectory, string revision, TimeSpan cliTimeout) =>
        Invoke(reader => reader.GetCommitMessage(workingDirectory, revision, cliTimeout));

    public string? GetUpstreamContainingHead(string workingDirectory, TimeSpan cliTimeout) =>
        Invoke(reader => reader.GetUpstreamContainingHead(workingDirectory, cliTimeout));

    public string? GetInProgressOperation(string workingDirectory, TimeSpan cliTimeout) =>
        Invoke(reader => reader.GetInProgressOperation(workingDirectory, cliTimeout));

    public bool? HasStagedChanges(string workingDirectory, IReadOnlyList<string>? paths, TimeSpan cliTimeout) =>
        Invoke(reader => reader.HasStagedChanges(workingDirectory, paths, cliTimeout));

    // The two index writes. A decline (NotSupportedException) happens before libgit2 has written anything, so the CLI then runs the whole command; see Libgit2GitReader.IndexWrites.cs.
    public GitRunResult StageWholeFiles(string workingDirectory, IndexStageMode mode, IReadOnlyList<string> paths, TimeSpan cliTimeout, CancellationToken cancellationToken) =>
        Invoke(reader => reader.StageWholeFiles(workingDirectory, mode, paths, cliTimeout, cancellationToken));

    public GitRunResult UnstagePaths(string workingDirectory, IReadOnlyList<string> paths, TimeSpan cliTimeout, CancellationToken cancellationToken) =>
        Invoke(reader => reader.UnstagePaths(workingDirectory, paths, cliTimeout, cancellationToken));

    private T Invoke<T>(Func<IGitReader, T> call)
    {
        try
        {
            var result = call(_primary);
            Interlocked.Increment(ref _primaryCount);
            Volatile.Write(ref _lastBackend, (int)GitReaderBackend.Libgit2);
            return result;
        }
        // NotSupportedException: the primary declined a request it cannot reproduce (pathspec magic, a path outside the working tree, a carriage
        // return in a file to stage, a held index.lock, ...). Planned and normal: counted as a decline, never logged to stderr.
        catch (Exception ex) when (IsDecline(ex))
        {
            RecordDecline(ex);
            var result = call(_fallback);
            Interlocked.Increment(ref _declineCount);
            Volatile.Write(ref _lastBackend, (int)GitReaderBackend.Cli);
            return result;
        }
        // LibGit2SharpException: libgit2 could not do it (cannot open the repo, unsupported repo format, ownership check).
        // DllNotFoundException / BadImageFormatException: the native library is missing or the wrong architecture.
        // TypeInitializationException: a static constructor failed earlier (it rethrows on every later use).
        // PlatformNotSupportedException: a NotSupportedException subtype, but the runtime or native layer says so, not a planned decline.
        // IOException / UnauthorizedAccessException: libgit2 or the directory scan could not read the repository files.
        catch (Exception ex) when (IsFailure(ex))
        {
            RecordFallback(ex);
            var result = call(_fallback);
            Interlocked.Increment(ref _fallbackCount);
            Volatile.Write(ref _lastBackend, (int)GitReaderBackend.Cli);
            return result;
        }
    }

    /// <summary>A planned decline: the libgit2 reader's <see cref="NotSupportedException"/>, the only decline signal (see the class summary).</summary>
    internal static bool IsDecline(Exception ex) => ex is NotSupportedException and not PlatformNotSupportedException;

    /// <summary>A real failure of the primary that the composite answers from the CLI for; any other exception type propagates.</summary>
    internal static bool IsFailure(Exception ex) =>
        ex is LibGit2SharpException or DllNotFoundException or BadImageFormatException or TypeInitializationException
            or PlatformNotSupportedException or IOException or UnauthorizedAccessException;

    private void RecordDecline(Exception ex)
    {
        var reason = string.IsNullOrEmpty(ex.Message) ? ex.GetType().Name : ex.Message;
        Volatile.Write(ref _lastDeclineReason, reason);

        // Declines are routine (every pathspec-magic log, every CR-containing stage): a debug-build trace only, nothing on stderr.
        System.Diagnostics.Debug.WriteLine($"[RoslynSentinel.GitReader] libgit2 declined, the git CLI answers: {reason}");
    }

    private void RecordFallback(Exception ex)
    {
        var reason = $"{ex.GetType().Name}: {ex.Message}";
        Interlocked.CompareExchange(ref _firstFallbackReason, reason, null);

        // Once per exception type, so one repeated failure kind cannot hide a different one.
        if (_loggedKinds.TryAdd(ex.GetType().FullName ?? ex.GetType().Name, 0))
        {
            _log($"[RoslynSentinel.GitReader] libgit2 failed, using the git CLI instead (logged once per exception type): {reason}");
        }
    }

    // Stdout carries the MCP JSON-RPC stream, so diagnostics go to stderr only.
    internal static void WriteToStandardError(string line)
    {
        try
        {
            Console.Error.WriteLine(line);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // stderr closed: nothing more to do, and a diagnostic must never fail the probe.
        }
    }
}

/// <summary>The process-wide <see cref="IGitReader"/> the server's probes share: libgit2 first, the git CLI as the fallback.</summary>
internal static class GitReaders
{
    /// <summary>Shared instance; its counters and <see cref="FallbackGitReader.LastBackend"/> are the process-wide record of which backend served.</summary>
    internal static readonly FallbackGitReader Default = new(new Libgit2GitReader(), new CliGitReader());
}
