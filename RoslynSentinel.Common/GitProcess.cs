using System.ComponentModel;
using System.Diagnostics;

namespace RoslynSentinel.Common;

/// <summary>
/// Runs one non-interactive <c>git</c> command with a hard timeout, for diagnostics that must never hang or
/// throw (server binary staleness). Unlike <c>GitWorktreeManager.RunGit</c> it drains stdout and stderr
/// concurrently on dedicated LongRunning threads (not the thread pool), so a large stderr cannot deadlock the
/// child and a saturated pool cannot delay EOF observation; it kills the process tree on timeout.
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
    /// Runs <c>git &lt;gitArgs&gt;</c> in <paramref name="workingDirectory"/>. Returns the exit code and stdout, or
    /// null when git cannot be started (missing, bad directory), times out (the process is killed), or its output
    /// cannot be read. Never throws for those conditions.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "Disposed by using statement")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("AsyncFixer", "AsyncFixer04")]
    internal static (int ExitCode, string StdOut)? TryRun(string workingDirectory, TimeSpan timeout, params string[] gitArgs)
    {
        LastFailure = null;
        try
        {
            var psi = new ProcessStartInfo("git")
            {
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            // Non-interactive by construction: git must never prompt for credentials.
            psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
            psi.Environment["GCM_INTERACTIVE"] = "never";
            foreach (var a in gitArgs)
            {
                psi.ArgumentList.Add(a);
            }

            using var process = Process.Start(psi);
            if (process is null)
            {
                LastFailure = "Process.Start returned null";
                return null;
            }

            // Close stdin so the child never inherits the host's live stdio (the MCP JSON-RPC pipe).
            process.StandardInput.Close();
            // Reads run on dedicated LongRunning threads, not the thread pool, so a saturated pool (parallel
            // test fixtures) cannot delay EOF observation. The pool numbers recorded in LastFailure exist to
            // confirm or reject starvation on the next failure.
            var stdOutTask = ReadAllOnDedicatedThread(process.StandardOutput);
            var stdErrTask = ReadAllOnDedicatedThread(process.StandardError);

            if (!process.WaitForExit(timeout))
            {
                TryKill(process);
                LastFailure = $"timed out after {timeout.TotalSeconds:0}s and was killed";
                return null;
            }

            var exitedAt = Stopwatch.GetTimestamp();
            // The process has exited; the pipes reach EOF promptly unless a grandchild still holds them.
            var grace = timeout < TimeSpan.FromSeconds(10) ? timeout : TimeSpan.FromSeconds(10);
            if (!Task.WaitAll([stdOutTask, stdErrTask], grace))
            {
                LastFailure = $"stdout/stderr did not reach EOF within {grace.TotalSeconds:0}s after the process exited (elapsed since exit {Stopwatch.GetElapsedTime(exitedAt).TotalMilliseconds:0} ms, ThreadPool.ThreadCount={ThreadPool.ThreadCount}, PendingWorkItemCount={ThreadPool.PendingWorkItemCount}; a grandchild may hold the pipe or the reads were starved)";
                return null;
            }

            return (process.ExitCode, stdOutTask.Result);
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or InvalidOperationException or ArgumentException or AggregateException)
        {
            LastFailure = $"{ex.GetType().Name}: {ex.Message}";
            return null;
        }
    }

    /// <summary>
    /// Drains <paramref name="reader"/> to EOF on a dedicated LongRunning thread. Returns empty text when the stream
    /// is closed or breaks mid-read, so a failed grace wait never leaves an unobserved faulted task behind.
    /// </summary>
    private static Task<string> ReadAllOnDedicatedThread(StreamReader reader)
    {
        return Task.Factory.StartNew(() =>
        {
            try
            {
                return reader.ReadToEnd();
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
            {
                return string.Empty;
            }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
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
