using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using RoslynSentinel.Common;

namespace RoslynSentinel.Engines.Basic;

/// <summary>A launch/stop failure that names its own <see cref="ToolErrorCode"/>; the tool layer maps it to a ResultError.</summary>
public sealed class TestInstanceException(string errorCode, string message) : ToolException(message)
{
    public override string ErrorCode { get; } = errorCode;
}

/// <summary>
/// Starts, tracks and stops HTTP copies of this server built from the working tree ("test instances"), for
/// developing the server itself. Each instance runs the throwaway build output on a free loopback port with
/// <c>--respawn=none</c> and its own <c>--working-dir</c>; the caller drives it with raw JSON-RPC over HTTP
/// (see <see cref="BuildHowToCall"/>). Children are killed when the owning server exits.
/// </summary>
public sealed class TestInstanceLauncher
{
    public const string ServerProjectName = "RoslynSentinel.Server.Advanced";

    private const string ServerDllName = ServerProjectName + ".dll";
    private const string ListeningPrefix = "listening: ";
    private static readonly TimeSpan ListenTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ReadyTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ReadyPollInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan ReadyAttemptTimeout = TimeSpan.FromSeconds(5);

    // Env vars the MSBuildLocator pins in this process; the child must resolve its own toolset (same list as BuildEngine).
    private static readonly string[] MsBuildPinnedVariables =
    [
        "MSBUILD_EXE_PATH", "MSBuildExtensionsPath", "MSBuildSDKsPath", "MSBuildLoadMicrosoftTargetsReadOnly",
        "DOTNET_HOST_PATH", "DOTNET_MSBUILD_SDK_RESOLVER_SDKS_DIR", "DOTNET_MSBUILD_SDK_RESOLVER_CLI_DIR",
    ];

    private readonly IWorkspaceManager _workspaceManager;
    private readonly BuildEngine _buildEngine;
    private readonly ServerInstancePaths _paths;
    private readonly ConcurrentDictionary<int, TestInstanceHandle> _instances = new();
    private readonly HttpClient _http = new() { Timeout = Timeout.InfiniteTimeSpan };
    private int _launchCounter;
    private int _exitHookRegistered;

    // TODO children are not in a job object: a hard kill of this server (taskkill /F) leaves them running and untracked.
    public TestInstanceLauncher(IWorkspaceManager workspaceManager, BuildEngine buildEngine, ServerInstancePaths? paths = null)
    {
        _workspaceManager = workspaceManager;
        _buildEngine = buildEngine;
        _paths = paths ?? ServerInstancePaths.Current;
    }

    // ---- launch ----

    /// <summary>Validates the loaded solution, builds when <paramref name="build"/> says so, then starts the server.</summary>
    public async Task<LaunchedTestInstance> LaunchAsync(TestInstanceBuildMode build, string mode, string? includeTools, CancellationToken cancellationToken)
    {
        var solution = await _workspaceManager.GetSolutionAsync(ReadSource.Committed, cancellationToken);
        var solutionPath = solution?.FilePath ?? _workspaceManager.SolutionPath;
        ValidateLaunchTarget(solutionPath, solution?.Projects.Select(p => p.Name));

        var solutionDirectory = Path.GetDirectoryName(solutionPath)!;
        var outputDirectory = GetServerOutputDirectory(solutionDirectory);
        var serverDll = Path.Combine(outputDirectory, ServerDllName);

        var rebuild = DecideRebuild(build, solutionDirectory);
        if (rebuild)
        {
            await RebuildAsync(cancellationToken);
            if (!File.Exists(serverDll))
            {
                throw new TestInstanceException(ToolErrorCode.BuildFailed,
                    $"The build succeeded but produced no {ServerDllName} at '{Path.GetRelativePath(solutionDirectory, outputDirectory)}'. "
                    + "Check that the solution builds the RoslynSentinel.Server.Advanced project.");
            }
        }

        var (sha, dirty) = ReadGitStamp(solutionDirectory);
        var source = new TestInstanceBuildSource(outputDirectory, File.GetLastWriteTimeUtc(serverDll), sha, dirty, rebuild);
        return await LaunchFromOutputAsync(solutionPath!, outputDirectory, mode, includeTools, source, cancellationToken);
    }

    /// <summary>Instance id for a test-instance child: <c>test-{parentPid}-{working dir leaf}</c>. Gives the child its own
    /// .roslynsentinel/build/{id}/ so its builds never share obj/bin with the parent.</summary>
    public static string ResolveChildInstanceId(int parentProcessId, string workingDirectory)
    {
        var leaf = Path.GetFileName(workingDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return BuildIsolation.SanitizeInstanceId($"test-{parentProcessId}-{leaf}") ?? $"test-{parentProcessId}";
    }

    /// <summary>Copies <paramref name="sourceDirectory"/> recursively into the <c>server</c> folder under
    /// <paramref name="workingDirectory"/> and returns that copy. A real copy, not hard links: a running exe or dll
    /// locks the parent's bin and, through hard links, its obj files. A missing source is returned unchanged so the
    /// caller's NotFound path still applies.</summary>
    public static string CopyServerOutput(string sourceDirectory, string workingDirectory)
    {
        if (!Directory.Exists(sourceDirectory))
        {
            return sourceDirectory;
        }

        var destination = Path.Combine(workingDirectory, "server");
        foreach (var sourceFile in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(destination, Path.GetRelativePath(sourceDirectory, sourceFile));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(sourceFile, target, overwrite: true);
        }

        return destination;
    }

    /// <summary>Starts the server from an existing build output and waits until it answers <c>initialize</c>.
    /// Public so an end-to-end test can launch from any built output without a loaded workspace.</summary>
    public async Task<LaunchedTestInstance> LaunchFromOutputAsync(
        string solutionPath, string serverOutputDirectory, string mode, string? includeTools,
        TestInstanceBuildSource buildSource, CancellationToken cancellationToken)
    {
        var solutionDirectory = Path.GetDirectoryName(solutionPath)!;
        var workingDirectory = CreateInstanceWorkingDirectory(solutionDirectory);
        // TODO delete <workingDirectory>\server when the instance is stopped (KillHandle/Stop); today the copy stays until the instances dir is swept.
        var copiedDirectory = CopyServerOutput(serverOutputDirectory, workingDirectory);
        var (fileName, firstArgument, exePath) = ResolveExecutable(copiedDirectory);
        var stdoutPath = Path.Combine(workingDirectory, "stdout.txt");
        var stderrPath = Path.Combine(workingDirectory, "stderr.txt");
        var logPaths = new TestInstanceLogPaths(stdoutPath, stderrPath, Path.Combine(workingDirectory, "logs"));

        var startInfo = new ProcessStartInfo(fileName)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        if (firstArgument is not null)
        {
            startInfo.ArgumentList.Add(firstArgument);
        }

        foreach (var argument in BuildServerArguments(mode, includeTools, solutionPath, workingDirectory))
        {
            startInfo.ArgumentList.Add(argument);
        }

        foreach (var variable in MsBuildPinnedVariables)
        {
            startInfo.EnvironmentVariables.Remove(variable);
        }

        startInfo.EnvironmentVariables[BuildIsolation.InstanceIdEnvironmentVariable] = ResolveChildInstanceId(Environment.ProcessId, workingDirectory);

        var stdout = new OutputCapture(stdoutPath);
        var stderr = new OutputCapture(stderrPath);
        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        var listening = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var exited = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        process.OutputDataReceived += (_, e) =>
        {
            stdout.Add(e.Data);
            if (e.Data is { } line && line.TrimStart().StartsWith(ListeningPrefix, StringComparison.Ordinal))
            {
                listening.TrySetResult(line.TrimStart()[ListeningPrefix.Length..].Trim());
            }
        };
        process.ErrorDataReceived += (_, e) => stderr.Add(e.Data);
        process.Exited += (_, _) => exited.TrySetResult(true);

        var handle = new TestInstanceHandle(process, stdout, stderr);
        var registered = false;
        try
        {
            try
            {
                process.Start();
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
            {
                throw new TestInstanceException(ToolErrorCode.Exception,
                    $"Could not start '{Path.GetFileName(fileName)}' for the test instance: {ex.Message}");
            }

            // The child never reads stdin; keeping the redirected pipe open (not inheriting ours) is what matters.
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            var url = await WaitForListeningAsync(handle, listening.Task, exited.Task, logPaths, cancellationToken);
            await WaitForInitializeAsync(handle, url, exited.Task, logPaths, cancellationToken);

            handle.Describe(process.Id, url, mode, solutionPath, workingDirectory, exePath, DateTime.UtcNow);
            _instances[process.Id] = handle;
            registered = true;
            EnsureExitHook();

            return new LaunchedTestInstance(url, process.Id, mode, solutionPath, workingDirectory, buildSource, logPaths, BuildHowToCall(url, process.Id));
        }
        finally
        {
            if (!registered)
            {
                KillHandle(handle);
            }
        }
    }

    private async Task<string> WaitForListeningAsync(
        TestInstanceHandle handle, Task<string> listening, Task<bool> exited, TestInstanceLogPaths logs, CancellationToken cancellationToken)
    {
        var timeout = Task.Delay(ListenTimeout, cancellationToken);
        var finished = await Task.WhenAny(listening, exited, timeout);
        cancellationToken.ThrowIfCancellationRequested();
        if (finished == listening)
        {
            return listening.Result;
        }

        if (finished == exited)
        {
            throw EarlyExit(handle, logs, "before it printed its 'listening:' line");
        }

        throw new TestInstanceException(ToolErrorCode.TimedOut,
            $"The test server did not print its 'listening:' line within {ListenTimeout.TotalSeconds:0} s and was stopped. {DescribeLogs(handle, logs)}");
    }

    private async Task WaitForInitializeAsync(
        TestInstanceHandle handle, string url, Task<bool> exited, TestInstanceLogPaths logs, CancellationToken cancellationToken)
    {
        var elapsed = Stopwatch.StartNew();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (exited.IsCompleted)
            {
                throw EarlyExit(handle, logs, "before it answered initialize");
            }

            if (await TryInitializeAsync(url, cancellationToken))
            {
                return;
            }

            if (elapsed.Elapsed >= ReadyTimeout)
            {
                throw new TestInstanceException(ToolErrorCode.TimedOut,
                    $"The test server at {url} did not answer initialize with HTTP 200 within {ReadyTimeout.TotalSeconds:0} s and was stopped. {DescribeLogs(handle, logs)}");
            }

            await Task.Delay(ReadyPollInterval, cancellationToken);
        }
    }

    private async Task<bool> TryInitializeAsync(string url, CancellationToken cancellationToken)
    {
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        attempt.CancelAfter(ReadyAttemptTimeout);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
            request.Content = new StringContent(InitializeBody, Encoding.UTF8, new MediaTypeHeaderValue("application/json"));
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, attempt.Token);
            return response.IsSuccessStatusCode;
        }
        catch (HttpRequestException)
        {
            return false;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    private const string InitializeBody =
        "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2025-06-18\",\"capabilities\":{},\"clientInfo\":{\"name\":\"x\",\"version\":\"0\"}}}";

    private static TestInstanceException EarlyExit(TestInstanceHandle handle, TestInstanceLogPaths logs, string when)
    {
        int? exitCode = null;
        try
        {
            handle.Process.WaitForExit(2000);
            exitCode = handle.Process.HasExited ? handle.Process.ExitCode : null;
        }
        catch (InvalidOperationException)
        {
            // Process object not usable: report without an exit code.
        }

        return new TestInstanceException(ToolErrorCode.Exception,
            $"The test server exited (code {exitCode?.ToString(CultureInfo.InvariantCulture) ?? "unknown"}) {when}. {DescribeLogs(handle, logs)}");
    }

    private static string DescribeLogs(TestInstanceHandle handle, TestInstanceLogPaths logs) =>
        $"Last stderr: [{Flatten(handle.Stderr.Tail())}] Last stdout: [{Flatten(handle.Stdout.Tail())}] Full logs: {logs.Stdout} , {logs.Stderr} , {logs.ServerLogDir}";

    private static string Flatten(string text) => text.Replace("\r", string.Empty).Replace('\n', '|');

    private async Task RebuildAsync(CancellationToken cancellationToken)
    {
        var result = await _buildEngine.RunFullBuildAsync(cancellationToken, 5, false, BuildVerifyLevel.fullBuildThrowaway);
        if (!result.TryGetData(out var data))
        {
            throw new TestInstanceException(ToolErrorCode.BuildFailed,
                "The build needed for the test instance did not run: " + (result.Error?.Message ?? result.Outcome.ToString()));
        }

        if (data.Outcome != BuildOutcome.Succeeded)
        {
            var first = string.Join("; ", data.Errors.Take(3).Select(e => $"{e.Id} {e.Message} [{e.Project}]"));
            throw new TestInstanceException(ToolErrorCode.BuildFailed,
                $"The build needed for the test instance failed with {data.ErrorCount} error(s). First: {first}. "
                + "Fix them (Build shows the full list) and call LaunchTestInstance again.");
        }
    }

    private static (string FileName, string? FirstArgument, string ExePath) ResolveExecutable(string outputDirectory)
    {
        foreach (var name in new[] { ServerProjectName + ".exe", ServerProjectName })
        {
            var apphost = Path.Combine(outputDirectory, name);
            if (File.Exists(apphost))
            {
                return (apphost, null, apphost);
            }
        }

        var dll = Path.Combine(outputDirectory, ServerDllName);
        if (File.Exists(dll))
        {
            return ("dotnet", dll, dll);
        }

        throw new TestInstanceException(ToolErrorCode.NotFound,
            $"No {ServerDllName} or apphost in the build output '{outputDirectory}'.");
    }

    private static IEnumerable<string> BuildServerArguments(string mode, string? includeTools, string solutionPath, string workingDirectory)
    {
        yield return "--transport=http";
        yield return "--port=0";
        yield return "--bind=loopback";
        yield return "--respawn=none";
        yield return "--mode=" + mode;
        if (!string.IsNullOrWhiteSpace(includeTools))
        {
            yield return "--include-tools=" + includeTools.Trim();
        }

        yield return "--solution=" + solutionPath;
        yield return "--working-dir=" + workingDirectory;
    }

    private string CreateInstanceWorkingDirectory(string solutionDirectory)
    {
        string candidate;
        do
        {
            var n = Interlocked.Increment(ref _launchCounter);
            candidate = ResolveInstanceWorkingDirectory(solutionDirectory, _paths, Environment.ProcessId, n);
        }
        while (Directory.Exists(candidate));

        if (!_paths.IsExplicit)
        {
            // Keep the instance logs out of git status: the root carries a catch-all .gitignore (same trick as the build root).
            var root = Path.Combine(solutionDirectory, ".roslynsentinel", "test-instances");
            Directory.CreateDirectory(root);
            var gitIgnore = Path.Combine(root, ".gitignore");
            if (!File.Exists(gitIgnore))
            {
                File.WriteAllText(gitIgnore, "*" + Environment.NewLine);
            }
        }

        Directory.CreateDirectory(candidate);
        return candidate;
    }

    // ---- stop / list ----

    /// <summary>Rows for every tracked instance. Dead ones are flagged (<see cref="RunningTestInstance.IsAlive"/> false) and pruned.</summary>
    public IReadOnlyList<RunningTestInstance> List()
    {
        var now = DateTime.UtcNow;
        var rows = new List<RunningTestInstance>();
        foreach (var handle in _instances.Values.OrderBy(h => h.StartedUtc))
        {
            var alive = IsAlive(handle.Process);
            rows.Add(handle.ToRow(alive, now));
            if (!alive && _instances.TryRemove(handle.Pid, out _))
            {
                handle.Dispose();
            }
        }

        return rows;
    }

    /// <summary>Kills one instance (<paramref name="pid"/> positive) or every tracked instance (null or not positive).
    /// Throws NotFound for an unknown pid.</summary>
    public IReadOnlyList<RunningTestInstance> Stop(int? pid)
    {
        var now = DateTime.UtcNow;
        var targets = new List<TestInstanceHandle>();
        if (pid is > 0)
        {
            if (!_instances.TryRemove(pid.Value, out var handle))
            {
                throw new TestInstanceException(ToolErrorCode.NotFound,
                    $"No test instance with pid {pid.Value} is tracked by this server. Call {ToolCallHint.Call("McpServerControl", ("operation", "ListTestInstances"))} to see the running ones.");
            }

            targets.Add(handle);
        }
        else
        {
            foreach (var key in _instances.Keys.ToArray())
            {
                if (_instances.TryRemove(key, out var handle))
                {
                    targets.Add(handle);
                }
            }
        }

        var rows = new List<RunningTestInstance>();
        foreach (var handle in targets.OrderBy(h => h.StartedUtc))
        {
            var row = handle.ToRow(false, now);
            KillHandle(handle);
            rows.Add(row);
        }

        return rows;
    }

    /// <summary>Kills every tracked instance. Used by the owning server's shutdown paths; never throws.</summary>
    public void KillAll()
    {
        foreach (var key in _instances.Keys.ToArray())
        {
            if (_instances.TryRemove(key, out var handle))
            {
                KillHandle(handle);
            }
        }
    }

    private void EnsureExitHook()
    {
        if (Interlocked.Exchange(ref _exitHookRegistered, 1) == 0)
        {
            AppDomain.CurrentDomain.ProcessExit += (_, _) => KillAll();
        }
    }

    private static bool IsAlive(Process process)
    {
        try
        {
            return !process.HasExited;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static void KillHandle(TestInstanceHandle handle)
    {
        try
        {
            if (!handle.Process.HasExited)
            {
                handle.Process.Kill(entireProcessTree: true);
            }

            handle.Process.WaitForExit(5000);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            // Already gone or not killable: nothing more to do.
        }

        handle.Dispose();
    }

    // ---- pure helpers (unit-tested) ----

    /// <summary>Throws when there is no loaded solution, or the loaded solution is not the one that builds this server.</summary>
    public static void ValidateLaunchTarget(string? solutionPath, IEnumerable<string>? projectNames)
    {
        if (string.IsNullOrWhiteSpace(solutionPath))
        {
            throw new SolutionNotLoadedException(
                SolutionNotLoadedMessage.Plain + " LaunchTestInstance needs the RoslynSentinel solution loaded: call " + ToolCallHint.Call("LoadSolution") + " with its .slnx path first.");
        }

        if (projectNames is null || !projectNames.Contains(ServerProjectName, StringComparer.OrdinalIgnoreCase))
        {
            throw new ToolTargetIneligibleException(
                $"LaunchTestInstance is for developing this server: the loaded solution has no '{ServerProjectName}' project, so there is no server to build and launch. "
                + $"Load the RoslynSentinel solution ({ToolCallHint.Call("LoadSolution")}) to use it.",
                suggestedTool: ToolCallHint.Call("LoadSolution"));
        }
    }

    /// <summary>Whether a launch must build first: Always yes, Skip no (throws NotFound naming Build when there is no output),
    /// Auto yes unless the server dll is newer than every source file (see <see cref="EvaluateOutputFreshness"/>).</summary>
    public static bool DecideRebuild(TestInstanceBuildMode build, string solutionDirectory)
    {
        var outputDirectory = GetServerOutputDirectory(solutionDirectory);
        var serverDll = Path.Combine(outputDirectory, ServerDllName);
        switch (build)
        {
            case TestInstanceBuildMode.Always:
                return true;
            case TestInstanceBuildMode.Skip:
                if (!File.Exists(serverDll))
                {
                    throw new TestInstanceException(ToolErrorCode.NotFound,
                        $"build: Skip but there is no server build output at '{Path.GetRelativePath(solutionDirectory, outputDirectory)}'. "
                        + $"Run {ToolCallHint.Call("Build")} (its default level writes there) and retry, or call LaunchTestInstance with build: Auto.");
                }

                return false;
            default:
                return !EvaluateOutputFreshness(serverDll, GetSourceRoots(solutionDirectory)).IsFresh;
        }
    }

    /// <summary>The throwaway build output folder that holds the server dll for this server instance:
    /// &lt;solutionDirectory&gt;/.roslynsentinel/build/&lt;instanceId&gt;/bin/RoslynSentinel.Server.Advanced/debug. Pure path computation.</summary>
    public static string GetServerOutputDirectory(string solutionDirectory) =>
        Path.Combine(BuildIsolation.GetBuildRoot(solutionDirectory), BuildIsolation.ResolveInstanceId(), "bin", ServerProjectName, "debug");

    /// <summary>The server source folders (under the solution directory) plus solution-level build files whose
    /// change makes an existing server build stale: Common, Engines.*, Tools.*, Server.* and Directory.Build/Packages files.</summary>
    public static IReadOnlyList<string> GetSourceRoots(string solutionDirectory)
    {
        var roots = new List<string>();
        if (Directory.Exists(solutionDirectory))
        {
            foreach (var directory in Directory.EnumerateDirectories(solutionDirectory, "RoslynSentinel.*", SearchOption.TopDirectoryOnly))
            {
                var name = Path.GetFileName(directory);
                if (name == "RoslynSentinel.Common"
                    || name.StartsWith("RoslynSentinel.Engines.", StringComparison.Ordinal)
                    || name.StartsWith("RoslynSentinel.Tools.", StringComparison.Ordinal)
                    || name.StartsWith("RoslynSentinel.Server.", StringComparison.Ordinal))
                {
                    roots.Add(directory);
                }
            }
        }

        foreach (var file in new[] { "Directory.Build.props", "Directory.Build.targets", "Directory.Packages.props" })
        {
            roots.Add(Path.Combine(solutionDirectory, file));
        }

        return roots;
    }

    /// <summary>
    /// Is the server dll newer than every source/project file under <paramref name="sourceRoots"/>? An entry that is a
    /// file is checked directly; a directory is walked recursively for .cs/.csproj/.resx/.props/.targets files,
    /// skipping bin and obj folders. No dll, or any file strictly newer, means not fresh.
    /// </summary>
    public static (bool IsFresh, string Reason) EvaluateOutputFreshness(string serverDllPath, IEnumerable<string> sourceRoots)
    {
        if (!File.Exists(serverDllPath))
        {
            return (false, "there is no server build output yet");
        }

        var built = File.GetLastWriteTimeUtc(serverDllPath);
        foreach (var root in sourceRoots)
        {
            foreach (var file in EnumerateSourceFiles(root))
            {
                if (File.GetLastWriteTimeUtc(file) > built)
                {
                    return (false, $"{Path.GetFileName(file)} is newer than the server build");
                }
            }
        }

        return (true, "the server build is newer than every source file");
    }

    private static IEnumerable<string> EnumerateSourceFiles(string root)
    {
        if (File.Exists(root))
        {
            yield return root;
            yield break;
        }

        if (!Directory.Exists(root))
        {
            yield break;
        }

        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            foreach (var subdirectory in Directory.EnumerateDirectories(directory))
            {
                var name = Path.GetFileName(subdirectory);
                if (!name.Equals("bin", StringComparison.OrdinalIgnoreCase) && !name.Equals("obj", StringComparison.OrdinalIgnoreCase))
                {
                    pending.Push(subdirectory);
                }
            }

            foreach (var file in Directory.EnumerateFiles(directory))
            {
                var extension = Path.GetExtension(file);
                if (extension is ".cs" or ".csproj" or ".resx" or ".props" or ".targets")
                {
                    yield return file;
                }
            }
        }
    }

    /// <summary>Where launch number <paramref name="n"/> keeps its logs and server output: under the owner's explicit
    /// working dir when it has one, else under the solution's .roslynsentinel folder keyed by the owner's pid.</summary>
    public static string ResolveInstanceWorkingDirectory(string solutionDirectory, ServerInstancePaths ownerPaths, int ownerPid, int n) =>
        ownerPaths.IsExplicit
            ? Path.Combine(ownerPaths.WorkingDir, "test-instances", n.ToString(CultureInfo.InvariantCulture))
            : Path.Combine(solutionDirectory, ".roslynsentinel", "test-instances", $"{ownerPid}-{n}");

    /// <summary>Two paste-ready PowerShell requests for <paramref name="url"/>. The endpoint is stateless Streamable HTTP:
    /// it answers HTTP 406 unless Accept lists both application/json and text/event-stream, and always replies as
    /// Server-Sent Events (the JSON-RPC response is the line starting 'data:'). The snippets use no double quotes or
    /// backslashes so they survive JSON escaping in the tool result.</summary>
    public static TestInstanceHowToCall BuildHowToCall(string url, int pid)
    {
        const string send =
            "$r = Invoke-WebRequest -Uri '<url>' -Method Post -ContentType 'application/json' "
            + "-Headers @{ Accept = 'application/json, text/event-stream' } -Body $body -UseBasicParsing; "
            + "$line = $r.Content.Split([char]10) | Where-Object { $_ -like 'data:*' } | Select-Object -First 1; "
            + "$reply = $line.Substring(5) | ConvertFrom-Json; ";

        var list = "$body = @{ jsonrpc = '2.0'; id = 2; method = 'tools/list' } | ConvertTo-Json -Compress; "
            + send.Replace("<url>", url)
            + "$reply.result.tools.name";
        var call = "$body = @{ jsonrpc = '2.0'; id = 3; method = 'tools/call'; params = @{ name = 'McpServerStatus'; arguments = @{ reason = 'checking the test instance' } } } | ConvertTo-Json -Depth 6 -Compress; "
            + send.Replace("<url>", url)
            + "$reply.result.content[0].text";

        var note = "This test instance is driven out of band with these requests, because your own session cannot reconnect or add MCP servers; "
            + "the endpoint replies only as Server-Sent Events (the response is the line starting 'data:') and answers HTTP 406 unless Accept lists both application/json and text/event-stream; "
            + $"stop it with {ToolCallHint.Call("McpServerControl", ("operation", "StopTestInstance"), ("pid", pid.ToString()))}.";
        return new TestInstanceHowToCall(note, list, call);
    }

    private static (string? Sha, bool? Dirty) ReadGitStamp(string solutionDirectory)
    {
        string? sha = null;
        if (GitProcess.TryRun(solutionDirectory, TimeSpan.FromSeconds(10), "rev-parse", "--short", "HEAD") is { ExitCode: 0 } head
            && head.StdOut.Trim() is { Length: > 0 } text)
        {
            sha = text;
        }

        bool? dirty = null;
        var status = GitProcess.TryRun(solutionDirectory, TimeSpan.FromSeconds(20),
            "status", "--porcelain", "--", "RoslynSentinel.Common", "RoslynSentinel.Engines.*", "RoslynSentinel.Tools.*", "RoslynSentinel.Server.*");
        if (status is { ExitCode: 0 } result)
        {
            dirty = !string.IsNullOrWhiteSpace(result.StdOut);
        }

        return (sha, dirty);
    }

    // ---- private holders ----

    private sealed class TestInstanceHandle(Process process, OutputCapture stdout, OutputCapture stderr) : IDisposable
    {
        private string _url = string.Empty;
        private string _mode = string.Empty;
        private string _solutionPath = string.Empty;
        private string _workingDir = string.Empty;
        private string _exePath = string.Empty;

        public Process Process { get; } = process;
        public OutputCapture Stdout { get; } = stdout;
        public OutputCapture Stderr { get; } = stderr;
        public int Pid { get; private set; }
        public DateTime StartedUtc { get; private set; } = DateTime.UtcNow;

        public void Describe(int pid, string url, string mode, string solutionPath, string workingDir, string exePath, DateTime startedUtc)
        {
            Pid = pid;
            _url = url;
            _mode = mode;
            _solutionPath = solutionPath;
            _workingDir = workingDir;
            _exePath = exePath;
            StartedUtc = startedUtc;
        }

        public RunningTestInstance ToRow(bool alive, DateTime now) =>
            new(Pid, _url, _mode, _solutionPath, _workingDir, StartedUtc, Math.Round((now - StartedUtc).TotalSeconds, 1), _exePath, alive);

        public void Dispose()
        {
            Stdout.Dispose();
            Stderr.Dispose();
            Process.Dispose();
        }
    }

    /// <summary>Drains one redirected stream continuously into a file (so the child never blocks on a full pipe)
    /// and keeps the last few lines for error messages.</summary>
    private sealed class OutputCapture : IDisposable
    {
        private const int TailLines = 20;
        private readonly object _gate = new();
        private readonly Queue<string> _tail = new();
        private readonly StreamWriter _writer;
        private bool _disposed;

        public OutputCapture(string path)
        {
            _writer = new StreamWriter(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false))
            {
                AutoFlush = true,
            };
        }

        public void Add(string? line)
        {
            if (line is null)
            {
                return;
            }

            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                try
                {
                    _writer.WriteLine(line);
                }
                catch (IOException)
                {
                    // Disk problem on a diagnostic log: keep draining so the child is never blocked.
                }

                _tail.Enqueue(line);
                while (_tail.Count > TailLines)
                {
                    _tail.Dequeue();
                }
            }
        }

        public string Tail()
        {
            lock (_gate)
            {
                return string.Join(Environment.NewLine, _tail);
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                _writer.Dispose();
            }
        }
    }
}
