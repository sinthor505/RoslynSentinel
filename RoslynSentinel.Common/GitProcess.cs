using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace RoslynSentinel.Common;

/// <summary>
/// The single runner for non-interactive <c>git</c> commands, with a hard timeout. <see cref="Run"/> and
/// <see cref="RunAsync"/> return a <see cref="GitRunResult"/> and never throw for start failure or timeout (only
/// <see cref="OperationCanceledException"/> for the caller's token); <see cref="TryRun"/> is the null-on-failure
/// form for diagnostics that must never hang or throw (server binary staleness). Stdout and stderr are drained
/// concurrently on dedicated LongRunning threads (not the thread pool), so a large stderr cannot deadlock the
/// child and a saturated pool cannot delay EOF observation; the process tree is killed on timeout or cancel.
/// <c>GitImpl</c> (Tools.Basic) and <c>GitWorktreeManager</c> are thin wrappers over this class.
/// </summary>
internal static class GitProcess
{
    /// <summary>Why the most recent <see cref="TryRun"/> on this thread returned null; null after success.</summary>
    [field: ThreadStatic]
    internal static string? LastFailure
    {
        get; private set;
    }

    /// <summary>
    /// Runs <c>git &lt;gitArgs&gt;</c> in <paramref name="workingDirectory"/>, blocking. Start failure, timeout (the
    /// process tree is killed) and an undrained pipe come back as a non-Completed <see cref="GitRunResult"/>;
    /// caller cancellation kills the tree and throws <see cref="OperationCanceledException"/>.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "Disposed by using statement")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("AsyncFixer", "AsyncFixer04")]
    internal static GitRunResult Run(
        string workingDirectory,
        IReadOnlyList<string> gitArgs,
        TimeSpan timeout,
        CancellationToken cancellationToken = default,
        GitRunOptions? options = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            using var process = Process.Start(BuildStartInfo(workingDirectory, gitArgs, options));
            if (process is null)
            {
                return GitRunResult.Failure(GitRunOutcome.Failed, "Process.Start returned null");
            }

            // Close stdin so the child never inherits the host's live stdio (the MCP JSON-RPC pipe).
            process.StandardInput.Close();
            var pumps = new OutputPumps(process);
            // Declared after the process so it is disposed first (waits for a running callback) and never kills a disposed process.
            using var cancelRegistration = cancellationToken.Register(static state => TryKill((Process)state!), process);

            if (!process.WaitForExit(timeout))
            {
                TryKill(process);
                pumps.WaitForAll(KillDrainGrace);
                cancellationToken.ThrowIfCancellationRequested();
                return GitRunResult.Failure(GitRunOutcome.TimedOut, TimedOutDetail(timeout));
            }

            // A cancel that killed the process makes WaitForExit return true: report it as cancellation.
            cancellationToken.ThrowIfCancellationRequested();
            var exitedAt = Stopwatch.GetTimestamp();
            var grace = DrainGrace(timeout);
            if (!pumps.WaitForAll(grace))
            {
                return GitRunResult.Failure(GitRunOutcome.OutputNotDrained, NotDrainedDetail(grace, exitedAt));
            }

            return pumps.ToResult(process.ExitCode, options);
        }
        catch (Exception ex) when (IsProcessFailure(ex))
        {
            return GitRunResult.Failure(GitRunOutcome.Failed, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Async form of <see cref="Run"/>: same results and kill-the-tree behavior, but the wait for exit is awaited.
    /// A timeout is returned as <see cref="GitRunOutcome.TimedOut"/> (caller-visible, so a wrapper can map it to its own
    /// exception); caller cancellation kills the tree and throws <see cref="OperationCanceledException"/>. The output
    /// reads still run on dedicated threads, not the pool.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "Disposed by using statement")]
    internal static async Task<GitRunResult> RunAsync(
        string workingDirectory,
        IReadOnlyList<string> gitArgs,
        TimeSpan timeout,
        CancellationToken cancellationToken = default,
        GitRunOptions? options = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            using var process = Process.Start(BuildStartInfo(workingDirectory, gitArgs, options));
            if (process is null)
            {
                return GitRunResult.Failure(GitRunOutcome.Failed, "Process.Start returned null");
            }

            process.StandardInput.Close();
            var pumps = new OutputPumps(process);
            // The linked token carries the timeout; the caller's token is checked separately to tell the two apart.
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeout);
            try
            {
                await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                TryKill(process);
                await pumps.WaitForAllAsync(KillDrainGrace).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                return GitRunResult.Failure(GitRunOutcome.TimedOut, TimedOutDetail(timeout));
            }

            var exitedAt = Stopwatch.GetTimestamp();
            var grace = DrainGrace(timeout);
            if (!await pumps.WaitForAllAsync(grace).ConfigureAwait(false))
            {
                return GitRunResult.Failure(GitRunOutcome.OutputNotDrained, NotDrainedDetail(grace, exitedAt));
            }

            return pumps.ToResult(process.ExitCode, options);
        }
        catch (Exception ex) when (IsProcessFailure(ex))
        {
            return GitRunResult.Failure(GitRunOutcome.Failed, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Runs <c>git &lt;gitArgs&gt;</c> in <paramref name="workingDirectory"/>. Returns the exit code and stdout, or
    /// null when git cannot be started (missing, bad directory), times out (the process is killed), or its output
    /// cannot be read. Never throws for those conditions.
    /// </summary>
    internal static (int ExitCode, string StdOut)? TryRun(string workingDirectory, TimeSpan timeout, params string[] gitArgs)
    {
        LastFailure = null;
        var run = Run(workingDirectory, gitArgs, timeout);
        if (!run.Completed)
        {
            LastFailure = run.FailureDetail;
            return null;
        }

        return (run.ExitCode, run.StdOut);
    }

    /// <summary>
    /// Resolves the git executable once from PATH/PATHEXT, falling back to the bare name "git" so a PATH this scan
    /// cannot see (an App Paths registry redirect) still has a chance to work. Resolving it up front avoids
    /// CreateProcess re-resolving the bare name on every call, a suspected source of intermittent 30 s stalls
    /// (docs/current/blockers/blocking_error_git_status_timeout.md).
    /// </summary>
    internal static string ResolveGitExecutablePath()
    {
        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
        var extensions = OperatingSystem.IsWindows()
            ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE").Split(';')
            : [""];

        foreach (var dir in pathVar.Split(Path.PathSeparator))
        {
            if (dir.Length == 0)
            {
                continue;
            }

            foreach (var ext in extensions)
            {
                var candidate = Path.Combine(dir, "git" + ext);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return "git";
    }

    /// <summary>How long the reads get to finish after a kill (the pipes close with the process, so this is normally instant).</summary>
    private static readonly TimeSpan KillDrainGrace = TimeSpan.FromSeconds(2);

    private static ProcessStartInfo BuildStartInfo(string workingDirectory, IReadOnlyList<string> gitArgs, GitRunOptions? options)
    {
        var psi = new ProcessStartInfo(options?.GitExecutable ?? "git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            // Without explicit encodings Process decodes the pipes with Console.OutputEncoding, the legacy OS
            // codepage on Windows, while git writes UTF-8: every multi-byte character turns into mojibake
            // (docs/current/blockers/blocking_error_git_diff_mojibake_display.md). Stdout is read as bytes and
            // decoded as UTF-8 by OutputPumps.ToResult; the encoding here covers stderr.
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        // Non-interactive by construction: git must never prompt for credentials.
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        psi.Environment["GCM_INTERACTIVE"] = "never";
        if (options?.PrefixArgs is { } prefix)
        {
            foreach (var a in prefix)
            {
                psi.ArgumentList.Add(a);
            }
        }

        foreach (var a in gitArgs)
        {
            psi.ArgumentList.Add(a);
        }

        return psi;
    }

    private static TimeSpan DrainGrace(TimeSpan timeout) => timeout < TimeSpan.FromSeconds(10) ? timeout : TimeSpan.FromSeconds(10);

    private static string TimedOutDetail(TimeSpan timeout) => $"timed out after {timeout.TotalSeconds:0}s and was killed";

    // The pool numbers exist to confirm or reject starvation on the next failure.
    private static string NotDrainedDetail(TimeSpan grace, long exitedAt) =>
        $"stdout/stderr did not reach EOF within {grace.TotalSeconds:0}s after the process exited (elapsed since exit {Stopwatch.GetElapsedTime(exitedAt).TotalMilliseconds:0} ms, ThreadPool.ThreadCount={ThreadPool.ThreadCount}, PendingWorkItemCount={ThreadPool.PendingWorkItemCount}; a grandchild may hold the pipe or the reads were starved)";

    private static bool IsProcessFailure(Exception ex) =>
        ex is Win32Exception or IOException or InvalidOperationException or ArgumentException or AggregateException;

    /// <summary>
    /// Drains the child's stdout (raw bytes) and stderr to EOF on dedicated LongRunning threads, not the thread pool,
    /// so a saturated pool (parallel test fixtures) cannot delay EOF observation. A read that breaks yields empty
    /// output, so a failed grace wait never leaves an unobserved faulted task behind.
    /// </summary>
    private sealed class OutputPumps
    {
        private readonly Task<byte[]> _stdOut;
        private readonly Task<string> _stdErr;

        internal OutputPumps(Process process)
        {
            var stdOutStream = process.StandardOutput.BaseStream;
            var stdErrReader = process.StandardError;
            _stdOut = ReadOnDedicatedThread(
                () =>
                {
                    using var buffer = new MemoryStream();
                    stdOutStream.CopyTo(buffer);
                    return buffer.ToArray();
                },
                []);
            _stdErr = ReadOnDedicatedThread(stdErrReader.ReadToEnd, string.Empty);
        }

        internal bool WaitForAll(TimeSpan grace) => Task.WaitAll([_stdOut, _stdErr], grace);

        internal async Task<bool> WaitForAllAsync(TimeSpan grace)
        {
            try
            {
                await Task.WhenAll(_stdOut, _stdErr).WaitAsync(grace).ConfigureAwait(false);
                return true;
            }
            catch (TimeoutException)
            {
                return false;
            }
        }

        internal GitRunResult ToResult(int exitCode, GitRunOptions? options)
        {
            var bytes = _stdOut.Result;
            var stdErr = _stdErr.Result;
            if (options?.StdOutAsBytes == true)
            {
                return new GitRunResult(GitRunOutcome.Completed, exitCode, string.Empty, bytes, stdErr, null);
            }

            var preamble = Encoding.UTF8.Preamble;
            var skip = bytes.AsSpan().StartsWith(preamble) ? preamble.Length : 0;
            return new GitRunResult(GitRunOutcome.Completed, exitCode, Encoding.UTF8.GetString(bytes, skip, bytes.Length - skip), [], stdErr, null);
        }

        private static Task<T> ReadOnDedicatedThread<T>(Func<T> read, T fallback)
        {
            return Task.Factory.StartNew(() =>
            {
                try
                {
                    return read();
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
                {
                    return fallback;
                }
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            // Already exited or not killable: nothing more to do.
        }
    }
}
