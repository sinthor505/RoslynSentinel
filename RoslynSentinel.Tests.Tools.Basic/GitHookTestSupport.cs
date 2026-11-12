using System.Diagnostics;
using System.Text;
using static RoslynSentinel.Tests.Battery.Basic.GitTestRepo;

namespace RoslynSentinel.Tests.Tools.Basic;

/// <summary>
/// Helpers shared by the hook runner test classes. Paths are compared in their long form because the temp directory of this machine is reached through an 8.3 name
/// (<c>ADMINI~1</c>) while git prints the long one; a hook written for a marker is a LF-only script (CRLF would break sh) that writes its marker atomically (temp file, then mv).
/// </summary>
internal static class GitHookTestSupport
{
    /// <summary>A new empty directory under the (long-form) temp directory.</summary>
    internal static string NewRoot(string prefix)
    {
        var path = Path.Combine(LongPath(Path.GetTempPath()), prefix + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>The path with every 8.3 segment that exists replaced by its long name; segments that do not exist are kept as they are.</summary>
    internal static string LongPath(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full)!;
        var current = root;
        foreach (var segment in full[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            var next = Path.Combine(current, segment);
            if (segment.Contains('~') && Directory.Exists(current) && new DirectoryInfo(current).EnumerateFileSystemInfos(segment).FirstOrDefault() is { } match)
            {
                next = match.FullName;
            }

            current = next;
        }

        return current;
    }

    /// <summary>A path in the form two spellings of the same file compare equal in (long names, backslashes, no trailing separator); compare case-insensitively.</summary>
    internal static string Canon(string path) => Path.TrimEndingDirectorySeparator(LongPath(path.Replace('/', '\\')));

    /// <summary>The path git prints for <c>hooks/&lt;name&gt;</c>, run in <paramref name="workTreeRoot"/> (ground truth for the resolver).</summary>
    internal static string GitSays(string workTreeRoot, string hookName, IReadOnlyDictionary<string, string>? extraEnvironment = null) =>
        Canon(RunGitWithEnvironment(workTreeRoot, extraEnvironment, "rev-parse", "--path-format=absolute", "--git-path", "hooks/" + hookName));

    /// <summary>Runs git with extra environment variables on THAT child only (never this process) and returns trimmed stdout; throws on a non-zero exit.</summary>
    internal static string RunGitWithEnvironment(string workingDirectory, IReadOnlyDictionary<string, string>? extraEnvironment, params string[] args)
    {
        if (extraEnvironment is null || extraEnvironment.Count == 0)
        {
            return RunGit(workingDirectory, args);
        }

        var startInfo = new ProcessStartInfo("git")
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
            startInfo.ArgumentList.Add(arg);
        }

        startInfo.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        startInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";
        foreach (var (name, value) in extraEnvironment)
        {
            startInfo.Environment[name] = value;
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Process.Start returned null for git.");
        process.StandardInput.Close();
        var stdErr = process.StandardError.ReadToEndAsync();
        var stdOut = process.StandardOutput.ReadToEnd();
        if (!process.WaitForExit(TimeSpan.FromSeconds(60)))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"git {string.Join(' ', args)} did not exit within 60 s.");
        }

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed with exit code {process.ExitCode}: {stdErr.GetAwaiter().GetResult().Trim()}");
        }

        return stdOut.Trim();
    }

    /// <summary>Writes a hook script with LF line endings (sh cannot run CRLF).</summary>
    internal static string WriteHook(string hooksDirectory, string name, string script)
    {
        Directory.CreateDirectory(hooksDirectory);
        var path = Path.Combine(hooksDirectory, name);
        File.WriteAllText(path, script.Replace("\r\n", "\n"), new UTF8Encoding(false));
        return path;
    }

    /// <summary>Forward-slash form of a path for use inside a sh script.</summary>
    internal static string ForSh(string path) => path.Replace('\\', '/');

    /// <summary>
    /// A <c>#!/bin/sh</c> hook that runs <paramref name="prelude"/> (may be empty) and then writes <paramref name="marker"/> atomically with: the working directory (<c>cwd=</c>, Windows form),
    /// the number of GIT_* variables (<c>gitvars=</c>), the arguments (<c>args=</c>), <c>sentinel=</c> (the RS_SENTINEL variable), <c>tool=</c> (output of <c>echo ok | cat</c>, which needs git's tool directories on PATH) and <c>END</c>.
    /// </summary>
    internal static string MarkerScript(string marker, string prelude = "")
    {
        var m = ForSh(marker);
        return "#!/bin/sh\n" + prelude + "\n" +
               "{\n" +
               "  echo \"cwd=$(pwd -W)\"\n" +
               "  echo \"gitvars=$(env | grep -c '^GIT_')\"\n" +
               "  echo \"args=$*\"\n" +
               "  echo \"sentinel=$RS_SENTINEL\"\n" +
               "  echo \"tool=$(echo ok | cat)\"\n" +
               "  echo END\n" +
               $"}} > '{m}.tmp'\n" +
               $"mv '{m}.tmp' '{m}'\n";
    }

    /// <summary>Waits until <paramref name="marker"/> exists and returns its lines; fails the test after <paramref name="timeout"/>.</summary>
    internal static Dictionary<string, string> WaitForMarker(string marker, TimeSpan timeout)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < timeout)
        {
            if (File.Exists(marker))
            {
                var lines = File.ReadAllLines(marker);
                if (lines.Contains("END"))
                {
                    return lines.Where(line => line.Contains('=')).ToDictionary(line => line[..line.IndexOf('=')], line => line[(line.IndexOf('=') + 1)..]);
                }
            }

            Thread.Sleep(50);
        }

        Assert.Fail($"The hook marker '{marker}' did not appear within {timeout.TotalSeconds:0} s.");
        return [];
    }

    /// <summary>The real environment as an ambient-environment map (case-insensitive names) plus <paramref name="extra"/>.</summary>
    internal static Dictionary<string, string> AmbientWith(params (string Name, string Value)[] extra)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            map[(string)entry.Key] = (string?)entry.Value ?? string.Empty;
        }

        foreach (var (name, value) in extra)
        {
            map[name] = value;
        }

        return map;
    }

    /// <summary>Kills a process (and its tree) by id when it still exists; a launched hook that outlived its test must not hold the temp directory.</summary>
    internal static void KillIfRunning(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
            }
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            // Already gone.
        }
    }

    /// <summary>Deletes a directory tree, retrying while a just-exited child still holds it as its working directory; gives up silently after about 10 s (a leftover temp directory is not a test failure).</summary>
    internal static void DeleteTreeWithRetries(string path)
    {
        for (var attempt = 0; attempt < 40; attempt++)
        {
            try
            {
                CasCommitTestSupport.DeleteTree(path);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(250);
            }
        }
    }
}
