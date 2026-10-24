// McpServerControl test instances: pure helpers (freshness, build decision, working dir, howToCall), the structured
// error paths of the three operations, and one [Explicit] end-to-end launch of the real server.
using System.Diagnostics;
using System.Text.Json;

using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Engines.Basic;
using RoslynSentinel.Tools.Basic;

namespace RoslynSentinel.Tests.Battery.Basic;

[TestFixture]
[Category("AdminTools")] // sentinel:auto-category
[Category("TestInstanceLauncher")] // sentinel:auto-category
public class TestInstanceLauncherTests
{
    private string _dir = null!;

    [SetUp]
    public void Setup()
    {
        _dir = Path.Combine(Path.GetTempPath(), "rs-testinstance-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    [TearDown]
    public void TearDown()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // Best effort cleanup of a temp folder.
        }
    }

    private string Touch(string relativePath, DateTime utc)
    {
        var path = Path.Combine(_dir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "x");
        File.SetLastWriteTimeUtc(path, utc);
        return path;
    }

    // ---- freshness (auto build decision) ----

    [Test]
    public void EvaluateOutputFreshness_NoDll_IsNotFresh()
    {
        var (fresh, reason) = TestInstanceLauncher.EvaluateOutputFreshness(Path.Combine(_dir, "missing.dll"), [_dir]);

        Assert.That(fresh, Is.False);
        Assert.That(reason, Does.Contain("no server build output"));
    }

    [Test]
    public void EvaluateOutputFreshness_DllNewerThanEverySource_IsFresh()
    {
        var now = DateTime.UtcNow;
        Touch("src/A.cs", now.AddMinutes(-10));
        Touch("src/B.csproj", now.AddMinutes(-9));
        var dll = Touch("out/Server.dll", now.AddMinutes(-1));

        var (fresh, _) = TestInstanceLauncher.EvaluateOutputFreshness(dll, [Path.Combine(_dir, "src")]);

        Assert.That(fresh, Is.True);
    }

    [Test]
    public void EvaluateOutputFreshness_OneSourceNewerThanDll_IsNotFreshAndNamesTheFile()
    {
        var now = DateTime.UtcNow;
        Touch("src/A.cs", now.AddMinutes(-10));
        Touch("src/deep/Changed.cs", now.AddMinutes(1));
        var dll = Touch("out/Server.dll", now.AddMinutes(-1));

        var (fresh, reason) = TestInstanceLauncher.EvaluateOutputFreshness(dll, [Path.Combine(_dir, "src")]);

        Assert.That(fresh, Is.False);
        Assert.That(reason, Does.Contain("Changed.cs"));
    }

    [Test]
    public void EvaluateOutputFreshness_NewerFilesUnderBinObjOrOfOtherKinds_AreIgnored()
    {
        var now = DateTime.UtcNow;
        Touch("src/A.cs", now.AddMinutes(-10));
        Touch("src/bin/Generated.cs", now.AddMinutes(5));
        Touch("src/obj/Debug/Generated.cs", now.AddMinutes(5));
        Touch("src/notes.txt", now.AddMinutes(5));
        var dll = Touch("out/Server.dll", now.AddMinutes(-1));

        var (fresh, _) = TestInstanceLauncher.EvaluateOutputFreshness(dll, [Path.Combine(_dir, "src")]);

        Assert.That(fresh, Is.True);
    }

    [Test]
    public void EvaluateOutputFreshness_AFileRootNewerThanDll_IsNotFresh()
    {
        var now = DateTime.UtcNow;
        var props = Touch("Directory.Build.props", now.AddMinutes(2));
        var dll = Touch("out/Server.dll", now.AddMinutes(-1));

        var (fresh, _) = TestInstanceLauncher.EvaluateOutputFreshness(dll, [props, Path.Combine(_dir, "nope")]);

        Assert.That(fresh, Is.False);
    }

    [Test]
    public void GetSourceRoots_PicksCommonEnginesToolsAndServerFoldersOnly()
    {
        foreach (var name in new[]
        {
            "RoslynSentinel.Common", "RoslynSentinel.Engines.Basic", "RoslynSentinel.Tools.Advanced", "RoslynSentinel.Server.Advanced",
            "RoslynSentinel.Tests.Tools.Basic", "RoslynSentinel.Utilities.PlanStepRunner", "docs",
        })
        {
            Directory.CreateDirectory(Path.Combine(_dir, name));
        }

        var folders = TestInstanceLauncher.GetSourceRoots(_dir).Where(Directory.Exists).Select(Path.GetFileName).ToList();

        Assert.That(folders, Is.EquivalentTo(new[]
        {
            "RoslynSentinel.Common", "RoslynSentinel.Engines.Basic", "RoslynSentinel.Tools.Advanced", "RoslynSentinel.Server.Advanced",
        }));
    }

    // ---- build decision ----

    [Test]
    public void DecideRebuild_SkipWithoutOutput_ThrowsNotFoundNamingBuild()
    {
        var ex = Assert.Throws<TestInstanceException>(() => TestInstanceLauncher.DecideRebuild(TestInstanceBuildMode.Skip, _dir));

        Assert.That(ex!.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound));
        Assert.That(ex.Message, Does.Contain("Build"));
    }

    [Test]
    public void DecideRebuild_AlwaysSkipAndAuto_FollowTheirContract()
    {
        var now = DateTime.UtcNow;
        var dll = Path.Combine(TestInstanceLauncher.GetServerOutputDirectory(_dir), "RoslynSentinel.Server.Advanced.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(dll)!);
        File.WriteAllText(dll, "x");
        File.SetLastWriteTimeUtc(dll, now);
        var source = Touch("RoslynSentinel.Tools.Basic/A.cs", now.AddMinutes(-5));

        Assert.That(TestInstanceLauncher.DecideRebuild(TestInstanceBuildMode.Always, _dir), Is.True);
        Assert.That(TestInstanceLauncher.DecideRebuild(TestInstanceBuildMode.Skip, _dir), Is.False);
        Assert.That(TestInstanceLauncher.DecideRebuild(TestInstanceBuildMode.Auto, _dir), Is.False, "dll newer than the only source");

        File.SetLastWriteTimeUtc(source, now.AddMinutes(5));
        Assert.That(TestInstanceLauncher.DecideRebuild(TestInstanceBuildMode.Auto, _dir), Is.True, "a source newer than the dll");
    }

    // ---- launch target validation ----

    [Test]
    public void ValidateLaunchTarget_NoSolution_ThrowsSolutionNotLoaded()
    {
        Assert.Throws<SolutionNotLoadedException>(() => TestInstanceLauncher.ValidateLaunchTarget(null, null));
        Assert.Throws<SolutionNotLoadedException>(() => TestInstanceLauncher.ValidateLaunchTarget("  ", ["RoslynSentinel.Server.Advanced"]));
    }

    [Test]
    public void ValidateLaunchTarget_SolutionWithoutServerProject_ThrowsTargetIneligibleNamingTheProject()
    {
        var ex = Assert.Throws<ToolTargetIneligibleException>(
            () => TestInstanceLauncher.ValidateLaunchTarget(@"C:\other\Other.slnx", ["Other.Core", "Other.Tests"]));

        Assert.That(ex!.Message, Does.Contain("RoslynSentinel.Server.Advanced"));
        Assert.That(ex.Message, Does.Contain("developing this server"));
    }

    [Test]
    public void ValidateLaunchTarget_SolutionWithServerProject_Passes()
    {
        Assert.DoesNotThrow(() => TestInstanceLauncher.ValidateLaunchTarget(@"C:\r\RoslynSentinel.slnx", ["RoslynSentinel.Common", "RoslynSentinel.Server.Advanced"]));
    }

    // ---- working dir ----

    [Test]
    public void ResolveInstanceWorkingDirectory_ExplicitOwner_UsesItsWorkingDirTestInstancesFolder()
    {
        var owner = ServerInstancePaths.Resolve(["--working-dir=" + Path.Combine(_dir, "owner")], _dir);

        var path = TestInstanceLauncher.ResolveInstanceWorkingDirectory(Path.Combine(_dir, "sol"), owner, 4242, 3);

        Assert.That(path, Is.EqualTo(Path.Combine(_dir, "owner", "test-instances", "3")));
    }

    [Test]
    public void ResolveInstanceWorkingDirectory_NoExplicitDir_UsesSolutionRoslynSentinelFolderKeyedByOwnerPid()
    {
        var owner = ServerInstancePaths.Resolve([], _dir);
        var solution = Path.Combine(_dir, "sol");

        var path = TestInstanceLauncher.ResolveInstanceWorkingDirectory(solution, owner, 4242, 3);

        Assert.That(path, Is.EqualTo(Path.Combine(solution, ".roslynsentinel", "test-instances", "4242-3")));
    }

    // ---- child instance id and output copy ----

    [Test]
    public void ResolveChildInstanceId_UsesParentPidAndWorkingDirLeaf()
    {
        var workingDir = Path.Combine(_dir, "test-instances", "3");

        var id = TestInstanceLauncher.ResolveChildInstanceId(4242, workingDir);

        Assert.That(id, Is.EqualTo("test-4242-3"));
    }

    [Test]
    public void ResolveChildInstanceId_DiffersFromTheParentInstanceId()
    {
        var id = TestInstanceLauncher.ResolveChildInstanceId(4242, Path.Combine(_dir, "test-instances", "3"));

        Assert.That(id, Is.Not.EqualTo(BuildIsolation.ResolveInstanceId()));
        Assert.That(id, Does.Contain("test-"));
    }

    [Test]
    public void CopyServerOutput_CopiesFilesIntoServerSubfolderAsRealCopies()
    {
        var source = Path.Combine(_dir, "bin");
        Directory.CreateDirectory(Path.Combine(source, "nested"));
        File.WriteAllText(Path.Combine(source, "RoslynSentinel.Server.Advanced.dll"), "top");
        File.WriteAllText(Path.Combine(source, "nested", "Engine.dll"), "nested");
        var workingDir = Path.Combine(_dir, "wd");
        Directory.CreateDirectory(workingDir);

        var copied = TestInstanceLauncher.CopyServerOutput(source, workingDir);

        Assert.That(copied, Is.EqualTo(Path.Combine(workingDir, "server")));
        Assert.That(copied, Is.Not.EqualTo(source));
        Assert.That(File.ReadAllText(Path.Combine(copied, "RoslynSentinel.Server.Advanced.dll")), Is.EqualTo("top"));
        Assert.That(File.ReadAllText(Path.Combine(copied, "nested", "Engine.dll")), Is.EqualTo("nested"));
    }

    [Test]
    public void CopyServerOutput_MissingSource_ReturnsSourceUnchanged()
    {
        var missing = Path.Combine(_dir, "missing");

        var copied = TestInstanceLauncher.CopyServerOutput(missing, Path.Combine(_dir, "wd"));

        Assert.That(copied, Is.EqualTo(missing));
        Assert.That(Directory.Exists(Path.Combine(_dir, "wd", "server")), Is.False);
    }

    // ---- howToCall ----

    [Test]
    public void BuildHowToCall_CarriesUrlBothAcceptValuesContentTypeSseRecipeAndStopHint()
    {
        const string url = "http://127.0.0.1:51234/mcp";

        var how = TestInstanceLauncher.BuildHowToCall(url, 777);

        foreach (var snippet in new[] { how.ToolsList, how.ToolsCall })
        {
            Assert.That(snippet, Does.Contain(url));
            Assert.That(snippet, Does.Contain("application/json, text/event-stream"), "both Accept values");
            Assert.That(snippet, Does.Contain("-ContentType 'application/json'"));
            Assert.That(snippet, Does.Contain("data:"), "SSE data: line recipe");
            Assert.That(snippet, Does.Contain("Substring(5)"));
            Assert.That(snippet, Does.Not.Contain("\""), "no double quotes: the snippet must survive JSON escaping");
            Assert.That(snippet, Does.Not.Contain("\\"), "no backslashes: the snippet must survive JSON escaping");
        }

        Assert.That(how.ToolsList, Does.Contain("tools/list"));
        Assert.That(how.ToolsCall, Does.Contain("tools/call").And.Contain("McpServerStatus").And.Contain("reason"));
        Assert.That(how.Note, Does.Contain("StopTestInstance").And.Contain("pid: 777"));
        Assert.That(how.Note, Does.Contain("406").And.Contain("text/event-stream").And.Contain("data:"));
        Assert.That(how.Note, Does.Contain("out of band"));
    }

    // ---- the three operations: structured results and errors ----

    private static (AdminTools Tools, PersistentWorkspaceManager Workspace) NewTools()
    {
        var workspace = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        return (new AdminTools(workspace), workspace);
    }

    private static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    [Test]
    public async Task LaunchTestInstance_NoSolutionLoaded_ReturnsStructuredSolutionNotLoadedError()
    {
        var (tools, workspace) = NewTools();
        using var _ = workspace;

        var json = Parse(await tools.ControlTestInstancesAsync(
            AdminTools.McpServerControlOperation.LaunchTestInstance, build: null, mode: null, includeTools: null, pid: null, CancellationToken.None));

        Assert.That(json.GetProperty("isError").GetBoolean(), Is.True);
        var error = json.GetProperty("errorData");
        Assert.That(error.GetProperty("errorCode").GetString(), Is.EqualTo(ToolErrorCode.SolutionNotLoaded));
        Assert.That(error.GetProperty("message").GetString(), Does.Contain("LoadSolution"));
        Assert.That(json.ToString(), Does.Not.Contain("   at "), "no stack trace on the wire");
    }

    [Test]
    public async Task StopTestInstance_UnknownPid_ReturnsNotFoundNamingListTestInstances()
    {
        var (tools, workspace) = NewTools();
        using var _ = workspace;

        var json = Parse(await tools.ControlTestInstancesAsync(
            AdminTools.McpServerControlOperation.StopTestInstance, build: null, mode: null, includeTools: null, pid: 99999991, CancellationToken.None));

        Assert.That(json.GetProperty("isError").GetBoolean(), Is.True);
        Assert.That(json.GetProperty("errorData").GetProperty("errorCode").GetString(), Is.EqualTo(ToolErrorCode.NotFound));
        Assert.That(json.GetProperty("errorData").GetProperty("message").GetString(), Does.Contain("ListTestInstances"));
    }

    [Test]
    public async Task StopTestInstance_NoPidAndNothingRunning_ReportsNothingToStop()
    {
        var (tools, workspace) = NewTools();
        using var _ = workspace;

        var json = Parse(await tools.ControlTestInstancesAsync(
            AdminTools.McpServerControlOperation.StopTestInstance, build: null, mode: null, includeTools: null, pid: null, CancellationToken.None));

        Assert.That(json.GetProperty("stopped").GetArrayLength(), Is.EqualTo(0));
        Assert.That(json.GetProperty("note").GetString(), Does.Contain("No test instances"));
    }

    [Test]
    public async Task ListTestInstances_Empty_ReturnsEmptyListAndHowToStart()
    {
        var (tools, workspace) = NewTools();
        using var _ = workspace;

        var json = Parse(await tools.ControlTestInstancesAsync(
            AdminTools.McpServerControlOperation.ListTestInstances, build: null, mode: null, includeTools: null, pid: null, CancellationToken.None));

        Assert.That(json.GetProperty("instances").GetArrayLength(), Is.EqualTo(0));
        Assert.That(json.GetProperty("note").GetString(), Does.Contain("LaunchTestInstance"));
    }

    [Test]
    public void McpServerControl_NewOperationsAndParameters_AreInTheToolSignature()
    {
        Assert.That(Enum.GetNames<AdminTools.McpServerControlOperation>(),
            Is.SupersetOf(new[] { "LaunchTestInstance", "StopTestInstance", "ListTestInstances" }));

        var parameters = typeof(AdminTools).GetMethod(nameof(AdminTools.McpServerControlTool))!.GetParameters().Select(p => p.Name).ToList();
        Assert.That(parameters, Is.SupersetOf(new[] { "build", "mode", "includeTools", "pid" }));
        Assert.That(parameters.Last(), Is.EqualTo("cancellationToken"), "CancellationToken stays last");
    }

    // ---- end to end (real server) ----

    /// <summary>Launches the real server from an existing built output (no build), calls tools/list over HTTP and through the
    /// returned PowerShell snippet, stops it and asserts the process is gone. Needs a prior Build/RunTest that produced
    /// RoslynSentinel.Server.Advanced.dll; ignored when none is found.</summary>
    [Test]
    [Explicit("Starts the real server process (~10-30 s); run by name: TestInstanceLauncher_EndToEnd")]
    public async Task TestInstanceLauncher_EndToEnd_LaunchListToolsAndStop()
    {
        var repoRoot = FindRepoRoot();
        var outputDirectory = FindBuiltServerOutput(repoRoot);
        if (outputDirectory is null)
        {
            Assert.Ignore("No built RoslynSentinel.Server.Advanced output found under .roslynsentinel/build or .scratch; run Build first.");
            return;
        }

        TestContext.Out.WriteLine($"server output: {outputDirectory}");
        var solutionPath = Path.Combine(repoRoot, "RoslynSentinel.slnx");
        var workspace = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        var launcher = new TestInstanceLauncher(workspace, new BuildEngine(workspace));
        var buildSource = new TestInstanceBuildSource(outputDirectory, File.GetLastWriteTimeUtc(Path.Combine(outputDirectory, "RoslynSentinel.Server.Advanced.dll")), null, null, false);
        int? pid = null;
        try
        {
            var launched = await launcher.LaunchFromOutputAsync(solutionPath, outputDirectory, "claude-lean", null, buildSource, CancellationToken.None);
            pid = launched.Pid;
            TestContext.Out.WriteLine($"launched pid={launched.Pid} url={launched.Url} workingDir={launched.WorkingDir}");

            Assert.That(launched.Url, Does.StartWith("http://127.0.0.1:").And.EndWith("/mcp"));
            Assert.That(File.Exists(launched.LogPaths.Stdout), Is.True);

            var listed = launcher.List();
            Assert.That(listed.Select(r => r.Pid), Does.Contain(launched.Pid));
            Assert.That(listed.Single(r => r.Pid == launched.Pid).IsAlive, Is.True);

            // tools/list over HTTP with both Accept values; the reply is a Server-Sent Events body.
            using var http = new HttpClient();
            using var request = new HttpRequestMessage(HttpMethod.Post, launched.Url);
            request.Headers.TryAddWithoutValidation("Accept", "application/json, text/event-stream");
            request.Content = new StringContent("{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/list\"}", System.Text.Encoding.UTF8, new System.Net.Http.Headers.MediaTypeHeaderValue("application/json"));
            using var response = await http.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();
            Assert.That((int)response.StatusCode, Is.EqualTo(200), body);
            var dataLine = body.Split('\n').First(l => l.StartsWith("data:", StringComparison.Ordinal));
            var tools = Parse(dataLine[5..]).GetProperty("result").GetProperty("tools");
            TestContext.Out.WriteLine($"tools/list returned {tools.GetArrayLength()} tools");
            Assert.That(tools.GetArrayLength(), Is.GreaterThan(5));

            // The returned PowerShell snippet works verbatim.
            var snippetOutput = await RunPowerShellAsync(launched.HowToCall.ToolsList);
            if (snippetOutput is not null)
            {
                TestContext.Out.WriteLine("howToCall.toolsList output (first lines): " + string.Join(" | ", snippetOutput.Split('\n').Take(5).Select(l => l.Trim())));
                Assert.That(snippetOutput, Does.Contain("McpServerControl"));
            }

            var stopped = launcher.Stop(launched.Pid);
            Assert.That(stopped.Single().Pid, Is.EqualTo(launched.Pid));
            Assert.That(IsProcessGone(launched.Pid), Is.True, "stopped test instance must not be running");
            Assert.That(launcher.List(), Is.Empty);
            pid = null;
        }
        finally
        {
            launcher.KillAll();
            workspace.Dispose();
            if (pid is { } leaked && !IsProcessGone(leaked))
            {
                Assert.Fail($"test instance {leaked} outlived the test");
            }
        }
    }

    private static bool IsProcessGone(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return p.HasExited;
        }
        catch (ArgumentException)
        {
            return true;
        }
    }

    private static async Task<string?> RunPowerShellAsync(string command)
    {
        foreach (var shell in new[] { "pwsh", "powershell" })
        {
            var psi = new ProcessStartInfo(shell) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-NonInteractive");
            psi.ArgumentList.Add("-Command");
            psi.ArgumentList.Add(command);
            try
            {
                using var p = Process.Start(psi)!;
                var stdout = p.StandardOutput.ReadToEndAsync();
                var stderr = p.StandardError.ReadToEndAsync();
                if (!p.WaitForExit(60000))
                {
                    p.Kill(entireProcessTree: true);
                    Assert.Fail($"{shell} snippet timed out");
                }

                var output = await stdout;
                Assert.That(p.ExitCode, Is.EqualTo(0), await stderr);
                return output;
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // Shell not installed: try the next one.
            }
        }

        return null;
    }

    private static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "RoslynSentinel.slnx")))
            {
                return dir.FullName;
            }
        }

        Assert.Ignore("RoslynSentinel.slnx not found above the test binary.");
        return string.Empty;
    }

    private static string? FindBuiltServerOutput(string repoRoot)
    {
        var fromEnvironment = Environment.GetEnvironmentVariable("ROSLYNSENTINEL_TEST_SERVER_OUTPUT");
        if (!string.IsNullOrWhiteSpace(fromEnvironment) && File.Exists(Path.Combine(fromEnvironment, "RoslynSentinel.Server.Advanced.dll")))
        {
            return fromEnvironment;
        }

        var roots = new[]
        {
            Path.Combine(repoRoot, ".roslynsentinel", "build"),
            Path.Combine(repoRoot, ".scratch", "roslynsentinel-builds"),
        };
        return roots.Where(Directory.Exists)
            .SelectMany(Directory.GetDirectories)
            .Select(instance => Path.Combine(instance, "bin", "RoslynSentinel.Server.Advanced", "debug"))
            .Where(d => File.Exists(Path.Combine(d, "RoslynSentinel.Server.Advanced.dll")))
            .OrderByDescending(d => File.GetLastWriteTimeUtc(Path.Combine(d, "RoslynSentinel.Server.Advanced.dll")))
            .FirstOrDefault();
    }
}
