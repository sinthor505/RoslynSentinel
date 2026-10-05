using System.Diagnostics;
using System.Management;
using System.Runtime.Versioning;

namespace RoslynSentinel.Engines.Basic;

/// <summary>A running dotnet/msbuild process whose command line targets the same solution or project
/// as the build or test run about to start.</summary>
public sealed record CompetingBuildProcess(int ProcessId, string ProcessName, string CommandLine);

/// <summary>Keeps the `dotnet build` / `dotnet test` processes the Build and RunTest tools spawn from
/// colliding with other sessions' builds. Two mechanisms: <see cref="ApplyScratchArtifactsPath"/> redirects
/// bin/ and obj/ into a per-server-process scratch directory (opt-in via useScratchDir), and
/// <see cref="WaitForNoCompetingBuildAsync"/> waits briefly for a matching build already in flight rather
/// than racing it for the same obj/ and bin/ files.</summary>
public static class BuildIsolation
{
    public static readonly TimeSpan DefaultMaxWait = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    private const string ScratchFolderName = ".scratch";
    private const string ScratchSubfolderName = "roslynsentinel-builds";
    private const int MaxCommandLineChars = 160;

    private static readonly string[] BuildVerbs = ["build", "test", "publish", "pack", "restore", "msbuild"];

    /// <summary>Adds `-p:ArtifactsPath=...` so bin/ and obj/ for every project in the invocation land in a
    /// scratch directory unique to this server process instead of next to the sources. Reused across calls
    /// by the same process so incremental builds still work.</summary>
    public static string ApplyScratchArtifactsPath(ProcessStartInfo startInfo, string solutionDirectory)
    {
        var artifactsPath = GetScratchArtifactsPath(solutionDirectory);
        startInfo.ArgumentList.Add($"-p:ArtifactsPath={artifactsPath}");
        return artifactsPath;
    }

    /// <summary>The scratch directory for this server process, under
    /// &lt;solutionDirectory&gt;/.scratch/roslynsentinel-builds/&lt;pid&gt;. It stays inside the solution tree
    /// on purpose: tests that locate the repo root by walking up from AppContext.BaseDirectory keep working.
    /// The parent folder carries a catch-all .gitignore so it never shows in git status, and output sits
    /// under bin/ and obj/ segments, which the workspace file watcher already ignores.</summary>
    public static string GetScratchArtifactsPath(string solutionDirectory)
    {
        var root = Path.Combine(solutionDirectory, ScratchFolderName, ScratchSubfolderName);
        Directory.CreateDirectory(root);

        var gitIgnore = Path.Combine(root, ".gitignore");
        if (!File.Exists(gitIgnore))
        {
            File.WriteAllText(gitIgnore, "*" + Environment.NewLine);
        }

        PruneDirectoriesOfExitedProcesses(root);

        var path = Path.Combine(root, Environment.ProcessId.ToString());
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>Polls (every <see cref="PollInterval"/>) until no matching build is running or
    /// <paramref name="maxWait"/> elapses. Returns the processes still running at that point: empty means
    /// clear to start. A failing process probe counts as clear, so the probe can never block a build.</summary>
    public static async Task<IReadOnlyList<CompetingBuildProcess>> WaitForNoCompetingBuildAsync(
        IReadOnlyCollection<string> targetPaths,
        TimeSpan maxWait,
        CancellationToken cancellationToken = default)
    {
        var waited = Stopwatch.StartNew();
        while (true)
        {
            var competing = await Task.Run(() => FindCompetingBuilds(targetPaths), cancellationToken);
            if (competing.Count == 0 || waited.Elapsed >= maxWait)
            {
                return competing;
            }

            await Task.Delay(PollInterval, cancellationToken);
        }
    }

    public static IReadOnlyList<CompetingBuildProcess> FindCompetingBuilds(IReadOnlyCollection<string> targetPaths)
    {
        if (targetPaths.Count == 0)
        {
            return [];
        }

        try
        {
            var self = Environment.ProcessId;
            return EnumerateBuildProcesses()
                .Where(p => p.ProcessId != self && IsCompetingBuild(p.ProcessName, p.CommandLine, targetPaths))
                .Select(p => new CompetingBuildProcess(p.ProcessId, p.ProcessName, p.CommandLine))
                .ToList();
        }
        catch (Exception)
        {
            // WMI unavailable, /proc unreadable, access denied: no verdict is better than blocking the build.
            return [];
        }
    }

    /// <summary>True for a dotnet process running a build-like verb (build/test/publish/pack/restore/msbuild),
    /// or any msbuild process, whose command line contains one of <paramref name="targetPaths"/>.</summary>
    public static bool IsCompetingBuild(string processName, string commandLine, IReadOnlyCollection<string> targetPaths)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return false;
        }

        var name = Path.GetFileNameWithoutExtension(processName);
        var isDotnet = name.Equals("dotnet", StringComparison.OrdinalIgnoreCase);
        var isMsBuild = name.Equals("msbuild", StringComparison.OrdinalIgnoreCase);
        if (!isDotnet && !isMsBuild)
        {
            return false;
        }

        if (isDotnet && !HasBuildVerb(commandLine))
        {
            return false;
        }

        var normalizedCommandLine = NormalizeSeparators(commandLine);
        return targetPaths.Any(t =>
            !string.IsNullOrWhiteSpace(t) &&
            normalizedCommandLine.Contains(NormalizeSeparators(t), StringComparison.OrdinalIgnoreCase));
    }

    public static string DescribeCompetingBuilds(IReadOnlyList<CompetingBuildProcess> competing, TimeSpan waited)
    {
        var shown = string.Join("; ", competing.Take(3).Select(p =>
            $"pid {p.ProcessId} ({Truncate(p.CommandLine, MaxCommandLineChars)})"));
        var more = competing.Count > 3 ? $" and {competing.Count - 3} more" : string.Empty;
        return $"Another build is still running against this solution after waiting {waited.TotalSeconds:0}s: {shown}{more}. " +
               "Retry when it finishes, or pass useScratchDir:true to build into an isolated scratch directory instead.";
    }

    private static bool HasBuildVerb(string commandLine)
    {
        var tokens = commandLine.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        return tokens.Any(t => BuildVerbs.Contains(t.Trim('"'), StringComparer.OrdinalIgnoreCase));
    }

    private static string NormalizeSeparators(string path) => path.Replace('/', '\\');

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..max] + "...";

    private static List<(int ProcessId, string ProcessName, string CommandLine)> EnumerateBuildProcesses()
    {
        if (OperatingSystem.IsWindows())
        {
            return EnumerateWindowsProcesses();
        }

        if (OperatingSystem.IsLinux())
        {
            return EnumerateLinuxProcesses();
        }

        return [];
    }

    [SupportedOSPlatform("windows")]
    private static List<(int ProcessId, string ProcessName, string CommandLine)> EnumerateWindowsProcesses()
    {
        var results = new List<(int, string, string)>();
        using var searcher = new ManagementObjectSearcher(
            "SELECT ProcessId, Name, CommandLine FROM Win32_Process WHERE Name = 'dotnet.exe' OR Name = 'MSBuild.exe'");
        using var collection = searcher.Get();
        foreach (var item in collection)
        {
            using var processObject = item;
            var commandLine = processObject["CommandLine"] as string;
            if (commandLine is null)
            {
                continue;
            }

            results.Add((Convert.ToInt32(processObject["ProcessId"]), processObject["Name"] as string ?? string.Empty, commandLine));
        }

        return results;
    }

    private static List<(int ProcessId, string ProcessName, string CommandLine)> EnumerateLinuxProcesses()
    {
        var results = new List<(int, string, string)>();
        foreach (var directory in Directory.EnumerateDirectories("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(directory), out var pid))
            {
                continue;
            }

            try
            {
                var commandLine = File.ReadAllText(Path.Combine(directory, "cmdline")).Replace('\0', ' ').Trim();
                var executable = commandLine.Split(' ', 2)[0];
                results.Add((pid, Path.GetFileName(executable), commandLine));
            }
            catch (IOException)
            {
                // Process exited between enumeration and read.
            }
            catch (UnauthorizedAccessException)
            {
                // Not ours to inspect.
            }
        }

        return results;
    }

    private static void PruneDirectoriesOfExitedProcesses(string scratchRoot)
    {
        foreach (var directory in Directory.EnumerateDirectories(scratchRoot))
        {
            if (!int.TryParse(Path.GetFileName(directory), out var pid) || pid == Environment.ProcessId || IsProcessRunning(pid))
            {
                continue;
            }

            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort cleanup: a leftover scratch folder is not worth failing the build over.
            }
            catch (UnauthorizedAccessException)
            {
                // Same: leave it for the next call.
            }
        }
    }

    private static bool IsProcessRunning(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
