using System.ComponentModel;
using System.Diagnostics;

namespace RoslynSentinel.Common;

/// <summary>
/// Runs one non-interactive <c>git</c> command with a hard timeout, for diagnostics that must never hang or
/// throw (server binary staleness). Unlike <c>GitWorktreeManager.RunGit</c> it drains stdout and stderr
/// concurrently, so a large stderr cannot deadlock the child, and it kills the process tree on timeout.
/// </summary>
internal static class GitProcess
{
    [ThreadStatic]
    private static string? _lastFailure;

    /// <summary>Why the most recent <see cref="TryRun"/> on this thread returned null; null after success.</summary>
    internal static string? LastFailure => _lastFailure;

    /// <summary>
    /// Runs <c>git &lt;gitArgs&gt;</c> in <paramref name="workingDirectory"/>. Returns the exit code and stdout, or
    /// null when git cannot be started (missing, bad directory), times out (the process is killed), or its output
    /// cannot be read. Never throws for those conditions.
    /// </summary>
    internal static (int ExitCode, string StdOut)? TryRun(string workingDirectory, TimeSpan timeout, params string[] gitArgs)
    {
        _lastFailure = null;
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
                _lastFailure = "Process.Start returned null";
                return null;
            }

            // Close stdin so the child never inherits the host's live stdio (the MCP JSON-RPC pipe).
            process.StandardInput.Close();
            var stdOutTask = process.StandardOutput.ReadToEndAsync();
            var stdErrTask = process.StandardError.ReadToEndAsync();

            if (!process.WaitForExit(timeout))
            {
                TryKill(process);
                _lastFailure = $"timed out after {timeout.TotalSeconds:0}s and was killed";
                return null;
            }

            // The process has exited; the pipes reach EOF promptly unless a grandchild still holds them.
            if (!Task.WaitAll([stdOutTask, stdErrTask], TimeSpan.FromSeconds(2)))
            {
                _lastFailure = "stdout/stderr did not reach EOF within 2s after the process exited (a grandchild may hold the pipe)";
                return null;
            }

            return (process.ExitCode, stdOutTask.Result);
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or InvalidOperationException or ArgumentException or AggregateException)
        {
            _lastFailure = $"{ex.GetType().Name}: {ex.Message}";
            return null;
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
