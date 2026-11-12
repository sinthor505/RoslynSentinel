using System.Collections.Concurrent;

namespace RoslynSentinel.Common;

/// <summary>
/// Finds Git for Windows' <c>sh.exe</c> (the shell git runs hooks with) from the git installation, never from a fixed install path. Used by <see cref="GitHooks"/>; no other caller.
/// <para>
/// Probe order, first existing file wins. (1) Relative to the directory of the git executable that <see cref="GitProcess.ResolveGitExecutablePath"/> resolves from PATH, nearest first:
/// <c>..\usr\bin\sh.exe</c> (Git\cmd\git.exe), <c>..\..\usr\bin\sh.exe</c> (Git\mingw64\bin\git.exe), <c>..\bin\sh.exe</c> (Git\cmd\git.exe, the launcher copy), <c>..\..\bin\sh.exe</c>, and
/// <c>sh.exe</c> beside git.exe. (2) Last resort, for a git.exe that is a shim or a launcher elsewhere: <c>git --exec-path</c> (Git\mingw64\libexec\git-core), then <c>usr\bin\sh.exe</c> and
/// <c>bin\sh.exe</c> under each ancestor of that directory (up to <see cref="MaxExecPathAncestors"/>). Measured on the development machine (2026-11-11): Git\usr\bin\sh.exe and Git\bin\sh.exe exist,
/// Git\mingw64\bin\sh.exe does not. Not found returns a result naming the probes, so the caller declines to the git CLI.
/// </para>
/// <para>
/// Only file probes decide, so nothing can be stale beyond one process: the resolved path is memoized per git executable path and re-checked with <see cref="File.Exists(string)"/> on every call
/// (a removed sh.exe falls through to a fresh probe).
/// </para>
/// </summary>
internal static class GitShellLocator
{
    private const int MaxExecPathAncestors = 6;

    private static readonly string[] RelativeToGitExecutable =
    [
        @"..\usr\bin\sh.exe", @"..\..\usr\bin\sh.exe", @"..\bin\sh.exe", @"..\..\bin\sh.exe", "sh.exe",
    ];

    private static readonly string[] BelowInstallRoot = [@"usr\bin\sh.exe", @"bin\sh.exe"];

    private static readonly ConcurrentDictionary<string, string> Resolved = new(StringComparer.OrdinalIgnoreCase);

    private static readonly TimeSpan ExecPathTimeout = TimeSpan.FromSeconds(5);

    /// <param name="gitExecutable">The git executable to start from; null resolves it from PATH like every other git call.</param>
    /// <param name="execPathProbe">Test seam: returns the output of <c>git --exec-path</c> (null when it cannot run); null runs the real one.</param>
    internal static ShLocateResult Locate(string? gitExecutable = null, Func<string?>? execPathProbe = null)
    {
        var gitExe = gitExecutable ?? GitProcess.ResolveGitExecutablePath();
        if (Resolved.TryGetValue(gitExe, out var memo))
        {
            if (File.Exists(memo))
            {
                return new ShLocateResult(memo);
            }

            Resolved.TryRemove(gitExe, out _);
        }

        var probed = new List<string>();
        if (File.Exists(gitExe))
        {
            var gitDirectory = Path.GetDirectoryName(Path.GetFullPath(gitExe))!;
            foreach (var relative in RelativeToGitExecutable)
            {
                var candidate = Path.GetFullPath(Path.Combine(gitDirectory, relative));
                if (File.Exists(candidate))
                {
                    return Remember(gitExe, candidate);
                }

                probed.Add(candidate);
            }
        }
        else
        {
            probed.Add($"(git executable '{gitExe}' is not a file)");
        }

        var execPath = (execPathProbe ?? (() => ReadExecPath(gitExe)))();
        if (string.IsNullOrWhiteSpace(execPath))
        {
            probed.Add("(git --exec-path gave nothing)");
        }
        else
        {
            var directory = TryFullPath(execPath.Trim());
            for (var depth = 0; directory is not null && depth < MaxExecPathAncestors; depth++, directory = Path.GetDirectoryName(directory))
            {
                foreach (var relative in BelowInstallRoot)
                {
                    var candidate = Path.Combine(directory, relative);
                    if (File.Exists(candidate))
                    {
                        return Remember(gitExe, candidate);
                    }

                    probed.Add(candidate);
                }
            }
        }

        return new ShLocateResult(null, $"sh.exe not found; probed {string.Join("; ", probed)}");
    }

    private static ShLocateResult Remember(string gitExe, string shPath)
    {
        Resolved[gitExe] = shPath;
        return new ShLocateResult(shPath);
    }

    private static string? TryFullPath(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    /// <summary>The output of <c>git --exec-path</c>, null when git cannot be run or fails; never throws.</summary>
    private static string? ReadExecPath(string gitExe)
    {
        var run = GitProcess.Run(Path.GetTempPath(), ["--exec-path"], ExecPathTimeout, options: new GitRunOptions { GitExecutable = gitExe });
        return run.Completed && run.ExitCode == 0 ? run.StdOut.Trim() : null;
    }
}
