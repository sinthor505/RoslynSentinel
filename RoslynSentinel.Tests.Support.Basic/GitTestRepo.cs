using System.Diagnostics;
using System.Text;

namespace RoslynSentinel.Tests.Battery.Basic;

/// <summary>
/// Shared git plumbing for tests that build throwaway repos. Plain <see cref="Process"/> (never GitProcess, which is the
/// code under test elsewhere), stdout/stderr drained on dedicated LongRunning threads so a saturated thread pool (parallel
/// fixtures) cannot starve the EOF reads, and an isolated config set PER PROCESS through the child's environment
/// (not Environment.SetEnvironmentVariable, which is process-wide and races under parallel fixtures): no system config,
/// no background gc/maintenance/fsmonitor grandchild that could hold the pipes open, no commit signing.
/// </summary>
public static class GitTestRepo
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(60);

    private static readonly TimeSpan EofGrace = TimeSpan.FromSeconds(10);

    /// <summary>True when <c>git --version</c> runs and exits 0.</summary>
    public static bool IsGitAvailable()
    {
        try
        {
            return RunGitRaw(Path.GetTempPath(), "--version").ExitCode == 0;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException or TimeoutException)
        {
            return false;
        }
    }

    /// <summary>Creates <paramref name="directory"/> if needed and runs <c>git init --quiet</c> in it.</summary>
    public static void InitRepo(string directory)
    {
        Directory.CreateDirectory(directory);
        RunGit(directory, "init", "--quiet");
    }

    /// <summary>Runs git and returns trimmed stdout; throws InvalidOperationException (with stderr) on a non-zero exit code.</summary>
    public static string RunGit(string workingDirectory, params string[] args)
    {
        var (exitCode, stdOut, stdErr) = RunGitRaw(workingDirectory, args);
        if (exitCode != 0)
        {
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed with exit code {exitCode} in '{workingDirectory}': {stdErr.Trim()}");
        }

        return stdOut.Trim();
    }

    /// <summary>Stages everything and commits (empty commits allowed) with the subject and an optional trailer paragraph; returns the full sha.</summary>
    public static string CommitAll(string workingDirectory, string subject, string? trailers = null)
    {
        RunGit(workingDirectory, "add", "-A");
        var args = new List<string> { "-c", "user.name=Test", "-c", "user.email=test@example.com", "commit", "--quiet", "--allow-empty", "-m", subject };
        if (trailers is not null)
        {
            args.Add("-m");
            args.Add(trailers);
        }

        RunGit(workingDirectory, [.. args]);
        return RunGit(workingDirectory, "rev-parse", "HEAD");
    }

    /// <summary>Runs git with the default timeout and returns exit code, stdout and stderr regardless of the exit code.</summary>
    public static (int ExitCode, string StdOut, string StdErr) RunGitRaw(string workingDirectory, params string[] args) =>
        RunGitRaw(workingDirectory, DefaultTimeout, args);

    /// <summary>Runs git and returns exit code, stdout and stderr regardless of the exit code; throws TimeoutException (process killed) past <paramref name="timeout"/> or when the pipes do not reach EOF after exit.</summary>
    public static (int ExitCode, string StdOut, string StdErr) RunGitRaw(string workingDirectory, TimeSpan timeout, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        // Isolated config, set on THIS child only (race-free under parallel fixtures).
        psi.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        psi.Environment["GIT_CONFIG_COUNT"] = "4";
        psi.Environment["GIT_CONFIG_KEY_0"] = "gc.auto";
        psi.Environment["GIT_CONFIG_VALUE_0"] = "0";
        psi.Environment["GIT_CONFIG_KEY_1"] = "maintenance.auto";
        psi.Environment["GIT_CONFIG_VALUE_1"] = "false";
        psi.Environment["GIT_CONFIG_KEY_2"] = "core.fsmonitor";
        psi.Environment["GIT_CONFIG_VALUE_2"] = "false";
        psi.Environment["GIT_CONFIG_KEY_3"] = "commit.gpgsign";
        psi.Environment["GIT_CONFIG_VALUE_3"] = "false";

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Process.Start returned null for git.");
        process.StandardInput.Close();
        var stdOut = ReadOnDedicatedThread(process.StandardOutput);
        var stdErr = ReadOnDedicatedThread(process.StandardError);

        if (!process.WaitForExit(timeout))
        {
            TryKill(process);
            throw new TimeoutException($"git {string.Join(' ', args)} did not exit within {timeout.TotalSeconds:0}s in '{workingDirectory}' and was killed.");
        }

        var exitedAt = Stopwatch.GetTimestamp();
        if (!Task.WaitAll([stdOut, stdErr], EofGrace))
        {
            throw new TimeoutException(
                $"git {string.Join(' ', args)}: stdout/stderr did not reach EOF within {EofGrace.TotalSeconds:0}s after the process exited " +
                $"(elapsed since exit {Stopwatch.GetElapsedTime(exitedAt).TotalMilliseconds:0} ms, ThreadPool.ThreadCount={ThreadPool.ThreadCount}, PendingWorkItemCount={ThreadPool.PendingWorkItemCount}).");
        }

        return (process.ExitCode, stdOut.Result, stdErr.Result);
    }

    private static Task<string> ReadOnDedicatedThread(StreamReader reader) =>
        Task.Factory.StartNew(
            () =>
            {
                try
                {
                    return reader.ReadToEnd();
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
                {
                    return string.Empty;
                }
            },
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);

    private static void TryKill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            // Already exited or not killable: nothing more to do.
        }
    }
}
