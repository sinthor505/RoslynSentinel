using System.Diagnostics;
using System.Management;
using System.Runtime.Versioning;

namespace RoslynSentinel.Engines.Basic;

/// <summary>A running dotnet/msbuild process whose command line targets the same solution or project
/// as the build or test run about to start.</summary>
public sealed record CompetingBuildProcess(int ProcessId, string ProcessName, string CommandLine);

/// <summary>Keeps the `dotnet build` / `dotnet test` processes the Build and RunTest tools spawn from
/// colliding with other sessions' builds. Two mechanisms: <see cref="ApplyArtifactsPath"/> redirects
/// bin/ and obj/ into a per-server-instance directory under .roslynsentinel/build/ (the default for Build
/// and always for RunTest), and <see cref="WaitForNoCompetingBuildAsync"/> waits briefly for a matching
/// build already in flight rather than racing it for the same obj/ and bin/ files (level=fullBuild only).</summary>
public static class BuildIsolation
{
    public static readonly TimeSpan DefaultMaxWait = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>Environment variable naming this server instance; set by the launcher. When absent the
    /// process id is used.</summary>
    public const string InstanceIdEnvironmentVariable = "ROSLYNSENTINEL_INSTANCE_ID";

    private const string BuildRootFolderName = ".roslynsentinel";
    private const string BuildSubfolderName = "build";
    private const int MaxInstanceIdChars = 64;
    private const int MaxCommandLineChars = 160;

    private static readonly string[] BuildVerbs = ["build", "test", "publish", "pack", "restore", "msbuild"];

    /// <summary>Adds `-p:ArtifactsPath=...` so bin/ and obj/ for every project in the invocation land in a
    /// directory unique to this server instance instead of next to the sources. Reused across calls
    /// by the same instance so restore and incremental state stay warm.</summary>
    public static string ApplyArtifactsPath(ProcessStartInfo startInfo, string solutionDirectory)
    {
        var artifactsPath = GetArtifactsPath(solutionDirectory);
        startInfo.ArgumentList.Add($"-p:ArtifactsPath={artifactsPath}");
        return artifactsPath;
    }

    /// <summary>The root that holds every instance's build output: &lt;solutionDirectory&gt;/.roslynsentinel/build.
    /// Pure path computation; nothing is created.</summary>
    public static string GetBuildRoot(string solutionDirectory) =>
        Path.Combine(solutionDirectory, BuildRootFolderName, BuildSubfolderName);

    /// <summary>The artifacts directory for this server instance, under
    /// &lt;solutionDirectory&gt;/.roslynsentinel/build/&lt;instanceId&gt;. It stays inside the solution tree
    /// on purpose: tests that locate the repo root by walking up from AppContext.BaseDirectory keep working.
    /// The build root carries a catch-all .gitignore so it never shows in git status, and output sits
    /// under bin/ and obj/ segments, which the workspace file watcher already ignores.</summary>
    public static string GetArtifactsPath(string solutionDirectory)
    {
        var root = GetBuildRoot(solutionDirectory);
        Directory.CreateDirectory(root);

        var gitIgnore = Path.Combine(root, ".gitignore");
        if (!File.Exists(gitIgnore))
        {
            File.WriteAllText(gitIgnore, "*" + Environment.NewLine);
        }

        var instanceId = ResolveInstanceId();
        PruneDirectoriesOfExitedProcesses(root, instanceId);

        var path = Path.Combine(root, instanceId);
        Directory.CreateDirectory(path);
        return path;
    }

    // TODO Part 3: replace with the ServerInstancePaths working-dir leaf name (plan_build_isolation_prod_instances_and_test_servers.md).
    /// <summary>The id naming this instance's artifacts directory: <see cref="InstanceIdEnvironmentVariable"/>
    /// when set (sanitised to a safe folder name), otherwise the process id.</summary>
    public static string ResolveInstanceId()
    {
        var fromEnvironment = SanitizeInstanceId(Environment.GetEnvironmentVariable(InstanceIdEnvironmentVariable));
        return fromEnvironment ?? Environment.ProcessId.ToString();
    }

    /// <summary>Reduces <paramref name="raw"/> to letters, digits, '.', '_' and '-' (anything else becomes '_'),
    /// capped at 64 characters. Returns null when nothing usable is left (null, blank, only dots).</summary>
    public static string? SanitizeInstanceId(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var chars = raw.Trim().Select(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' ? c : '_').ToArray();
        var sanitized = new string(chars);
        if (sanitized.Length > MaxInstanceIdChars)
        {
            sanitized = sanitized[..MaxInstanceIdChars];
        }

        // "." and ".." would escape or alias the build root.
        return sanitized.Trim('.').Length == 0 ? null : sanitized;
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
               "Retry when it finishes, or use level=fullBuildThrowaway to build into an isolated per-server output folder instead.";
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

    // Deletes numeric (pid-named) sibling directories whose process is gone. Named instance directories
    // (ROSLYNSENTINEL_INSTANCE_ID) are never pruned here; Part 3 of the build-isolation plan sweeps them.
    private static void PruneDirectoriesOfExitedProcesses(string buildRoot, string currentInstanceId)
    {
        foreach (var directory in Directory.EnumerateDirectories(buildRoot))
        {
            var name = Path.GetFileName(directory);
            if (name == currentInstanceId || !int.TryParse(name, out var pid) || pid == Environment.ProcessId || IsProcessRunning(pid))
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

// TODO move the three BuildGate types below into their own BuildGate.cs; CreateFile's compile gate rejected a new file here (CS0518 on the seeded class), so they live beside BuildIsolation for now.

/// <summary>Thrown by <see cref="BuildGate.AcquireAsync"/> when the bounded wait elapsed while another
/// build or test run held the gate. The message names the in-flight operation and its start time (UTC).</summary>
public sealed class BuildGateBusyException : Exception
{
    public BuildGateBusyException(string? inFlightOperation, DateTime? inFlightStartedUtc, string requestedOperation)
        : base(BuildGate.DescribeBusy(inFlightOperation, inFlightStartedUtc))
    {
        InFlightOperation = inFlightOperation;
        InFlightStartedUtc = inFlightStartedUtc;
        RequestedOperation = requestedOperation;
    }

    /// <summary>The operation holding the gate, or null when it was released between the timeout and the read.</summary>
    public string? InFlightOperation { get; }

    /// <summary>When the holder acquired the gate (UTC), or null as for <see cref="InFlightOperation"/>.</summary>
    public DateTime? InFlightStartedUtc { get; }

    /// <summary>The operation that gave up waiting.</summary>
    public string RequestedOperation { get; }
}

/// <summary>Holds a <see cref="BuildGate"/> until disposed. Disposing more than once is harmless.</summary>
public sealed class BuildGateLease : IDisposable
{
    private BuildGate? _gate;

    internal BuildGateLease(BuildGate gate) => _gate = gate;

    public void Dispose() => Interlocked.Exchange(ref _gate, null)?.Release();
}

/// <summary>Serialises the `dotnet build` / `dotnet test` subprocesses one server spawns: they share the
/// solution's obj/ and the instance's artifacts directory, so two at once would race each other. Callers
/// (<see cref="BuildEngine.RunFullBuildAsync"/>, <see cref="TestRunEngine.RunAsync"/>) take the lease once
/// around the whole operation and never call each other while holding it, so there is no nested
/// acquisition (the semaphore is not re-entrant). quickBuild is in-memory and does not take the gate.
/// Production code uses <see cref="Shared"/>; tests create their own instance.</summary>
public sealed class BuildGate
{
    /// <summary>Default bounded wait: the longest RunTest.</summary>
    public static readonly TimeSpan DefaultMaxWait = TimeSpan.FromMinutes(10);

    /// <summary>The one gate for this server process.</summary>
    public static BuildGate Shared { get; } = new();

    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private readonly object _holderLock = new();
    private string? _holderOperation;
    private DateTime? _holderStartedUtc;

    /// <summary>Waits up to <paramref name="maxWait"/> for the gate. Throws <see cref="BuildGateBusyException"/>
    /// on timeout and <see cref="OperationCanceledException"/> when <paramref name="cancellationToken"/> fires
    /// (nothing is held in either case).</summary>
    public async Task<BuildGateLease> AcquireAsync(string operationName, TimeSpan maxWait, CancellationToken cancellationToken = default)
    {
        if (!await _semaphore.WaitAsync(maxWait, cancellationToken))
        {
            string? operation;
            DateTime? startedUtc;
            lock (_holderLock)
            {
                operation = _holderOperation;
                startedUtc = _holderStartedUtc;
            }

            throw new BuildGateBusyException(operation, startedUtc, operationName);
        }

        lock (_holderLock)
        {
            _holderOperation = operationName;
            _holderStartedUtc = DateTime.UtcNow;
        }

        return new BuildGateLease(this);
    }

    /// <summary>The message shared by the timeout exception and the engines' not-run results.</summary>
    public static string DescribeBusy(string? inFlightOperation, DateTime? inFlightStartedUtc)
    {
        var what = string.IsNullOrEmpty(inFlightOperation) ? "build or test run" : inFlightOperation;
        var since = inFlightStartedUtc is { } started ? $" started {started:yyyy-MM-dd HH:mm:ss} UTC" : string.Empty;
        return $"Another {what}{since} is still running in this server; retry when it finishes.";
    }

    internal void Release()
    {
        lock (_holderLock)
        {
            _holderOperation = null;
            _holderStartedUtc = null;
        }

        _semaphore.Release();
    }
}
