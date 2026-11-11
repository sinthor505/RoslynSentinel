using System.Collections.Concurrent;
using LibGit2Sharp;

namespace RoslynSentinel.Common;

/// <summary>
/// The reader the server's probes use: tries the primary (libgit2) implementation first and answers from the fallback (CLI)
/// when the primary throws a native-load or libgit2 failure. Every call is wrapped, so a native library that loads fine for
/// one call and fails on a later one (or a <see cref="TypeInitializationException"/> that sticks after a failed static
/// constructor) is still covered.
/// <para>
/// A fallback is never silent. The first fallback for each distinct exception type is written once to stderr
/// (stdout is the MCP JSON-RPC pipe) with the exception's type name and message, and the active backend is observable:
/// <see cref="LastBackend"/> names the implementation that answered the most recent call, <see cref="Libgit2Count"/> and
/// <see cref="CliFallbackCount"/> count calls per backend, and <see cref="FirstFallbackReason"/> keeps the first reason.
/// All of it is thread-safe. A test that must prove libgit2 (not the fallback) serves a call asserts on these.
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
    private int _lastBackend;
    private string? _firstFallbackReason;

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

    /// <summary>Calls answered by the CLI fallback because the primary failed.</summary>
    internal long CliFallbackCount => Interlocked.Read(ref _fallbackCount);

    /// <summary>"ExceptionType: message" of the first failure that caused a fallback; null while none has.</summary>
    internal string? FirstFallbackReason => Volatile.Read(ref _firstFallbackReason);

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

    public IReadOnlyList<CommitMessage>? GetCommitMessages(string workingDirectory, TimeSpan cliTimeout) =>
        Invoke(reader => reader.GetCommitMessages(workingDirectory, cliTimeout));

    private T Invoke<T>(Func<IGitReader, T> call)
    {
        try
        {
            var result = call(_primary);
            Interlocked.Increment(ref _primaryCount);
            Volatile.Write(ref _lastBackend, (int)GitReaderBackend.Libgit2);
            return result;
        }
        // LibGit2SharpException: libgit2 could not do it (cannot open the repo, unsupported repo format, ownership check).
        // DllNotFoundException / BadImageFormatException: the native library is missing or the wrong architecture.
        // TypeInitializationException: a static constructor failed earlier (it rethrows on every later use).
        // NotSupportedException: the primary declined a request it cannot reproduce (pathspec magic, a path outside the working tree).
        // IOException / UnauthorizedAccessException: libgit2 or the directory scan could not read the repository files.
        catch (Exception ex) when (ex is LibGit2SharpException or DllNotFoundException or BadImageFormatException
            or TypeInitializationException or NotSupportedException or IOException or UnauthorizedAccessException)
        {
            RecordFallback(ex);
            var result = call(_fallback);
            Interlocked.Increment(ref _fallbackCount);
            Volatile.Write(ref _lastBackend, (int)GitReaderBackend.Cli);
            return result;
        }
    }

    private void RecordFallback(Exception ex)
    {
        var reason = $"{ex.GetType().Name}: {ex.Message}";
        Interlocked.CompareExchange(ref _firstFallbackReason, reason, null);

        // Once per exception type, so a harmless NotSupportedException (pathspec magic) cannot hide a later native-load failure.
        if (_loggedKinds.TryAdd(ex.GetType().FullName ?? ex.GetType().Name, 0))
        {
            _log($"[RoslynSentinel.GitReader] libgit2 failed, using the git CLI instead (logged once per exception type): {reason}");
        }
    }

    // Stdout carries the MCP JSON-RPC stream, so diagnostics go to stderr only.
    private static void WriteToStandardError(string line)
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
