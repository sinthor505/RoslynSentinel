using System.Diagnostics;

using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Common;
using RoslynSentinel.Engines.Basic;

namespace RoslynSentinel.Tests.Battery.Basic;

[TestFixture]
[Category("BuildIsolation")] // sentinel:auto-category
public class BuildIsolationTests
{
    private const string Solution = @"C:\work\RoslynSentinel\RoslynSentinel.slnx";

    [TestCase("dotnet.exe", @"""C:\Program Files\dotnet\dotnet.exe"" build C:\work\RoslynSentinel\RoslynSentinel.slnx --nologo", true)]
    [TestCase("dotnet.exe", "dotnet test C:/work/RoslynSentinel/RoslynSentinel.slnx", true)]
    [TestCase("dotnet.exe", @"dotnet BUILD ""c:\WORK\roslynsentinel\roslynsentinel.slnx""", true)]
    [TestCase("MSBuild.exe", @"MSBuild.exe C:\work\RoslynSentinel\RoslynSentinel.slnx /m", true)]
    [TestCase("dotnet.exe", @"dotnet build C:\work\Other\Other.slnx", false)]
    [TestCase("dotnet.exe", @"dotnet RoslynSentinel.Server.Advanced.dll --solution C:\work\RoslynSentinel\RoslynSentinel.slnx", false)]
    [TestCase("notepad.exe", @"notepad.exe C:\work\RoslynSentinel\RoslynSentinel.slnx", false)]
    [TestCase("dotnet.exe", "", false)]
    public void IsCompetingBuild_MatchesOnlyBuildVerbAgainstTarget(string processName, string commandLine, bool expected)
    {
        var actual = BuildIsolation.IsCompetingBuild(processName, commandLine, [Solution]);

        Assert.That(actual, Is.EqualTo(expected));
    }

    [Test]
    [Category("CompetingBuildProcess")] // sentinel:auto-category
    public void DescribeCompetingBuilds_NamesTheBlockerAndTheEscapeHatch()
    {
        var message = BuildIsolation.DescribeCompetingBuilds(
            [new CompetingBuildProcess(4242, "dotnet.exe", "dotnet build " + Solution)], TimeSpan.FromSeconds(30));

        Assert.That(message, Does.Contain("pid 4242"));
        Assert.That(message, Does.Contain("fullBuildThrowaway"));
        Assert.That(message, Does.Not.Contain(".scratch"));
    }

    [Test]
    [NonParallelizable]
    [Category("BuildEngine")] // sentinel:auto-category
    [Category("DiagnosticEngine")] // sentinel:auto-category
    public async Task RunFullBuildAsync_FullBuildThrowaway_WritesOutputUnderInstanceDirNotProjectBinAsync()
    {
        using var fixture = new TestSolutionFixture();
        using var workspaceManager = TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        await workspaceManager.LoadSolutionAsync(fixture.SolutionPath);
        var buildEngine = TestEngines.BuildEngine(workspaceManager, TestEngines.DiagnosticEngine(workspaceManager));
        var solutionDirectory = Path.GetDirectoryName(fixture.SolutionPath)!;
        var projectDirectory = Path.Combine(solutionDirectory, "ContosoOrders.Core");
        var projectBin = Path.Combine(projectDirectory, "bin");
        var binBefore = SnapshotFileSystemEntries(projectBin);
        var assembliesBefore = SnapshotAssemblyWriteTimes(projectDirectory);

        var result = await buildEngine.RunFullBuildAsync(CancellationToken.None, maxDetails: 50, level: BuildVerifyLevel.fullBuildThrowaway);

        Assert.That(result.Outcome, Is.EqualTo(EngineOutcome.Success), result.Error?.Message);
        Assert.That(result.Data!.ErrorCount, Is.Zero, result.Data.StdoutTail);
        Assert.That(result.Data.Level, Is.EqualTo(BuildVerifyLevel.fullBuildThrowaway));
        var instanceId = BuildIsolation.ResolveInstanceId();
        var buildRoot = Path.Combine(solutionDirectory, ".roslynsentinel", "build");
        var instanceBin = Path.Combine(buildRoot, instanceId, "bin");
        Assert.That(Directory.Exists(instanceBin), Is.True, "instance bin/ should exist after a throwaway build");
        Assert.That(Directory.EnumerateFiles(instanceBin, "ContosoOrders.Core.dll", SearchOption.AllDirectories), Is.Not.Empty);
        Assert.That(result.Data.Detail, Does.Contain(".roslynsentinel/build/" + instanceId));
        Assert.That(result.Data.Detail, Does.Contain("bin/ was not updated"));
        Assert.That(result.Data.Detail, Does.Not.Contain(".scratch"));
        // Loading the workspace can create empty bin/Debug folders, so compare before/after rather than demanding absence.
        Assert.That(SnapshotFileSystemEntries(projectBin), Is.EqualTo(binBefore),
            "a throwaway build must not write into the project's own bin/ folder");
        // The build itself writes no project bin/obj assembly. The workspace's post-build design-time reloads
        // DO rewrite obj/ files (*.AssemblyInfo.cs, *.AssemblyReference.cache) and are expected, so only
        // assemblies are compared, never every file under obj/ (see the plan's Problem section).
        Assert.That(SnapshotAssemblyWriteTimes(projectDirectory), Is.EqualTo(assembliesBefore),
            "a throwaway build must not write a project bin/ or obj/ assembly");
        Assert.That(File.ReadAllText(Path.Combine(buildRoot, ".gitignore")).Trim(), Is.EqualTo("*"));
    }

    [Test]
    [NonParallelizable]
    [Category("BuildEngine")]
    public async Task RunFullBuildAsync_FullBuild_WritesProjectBinAndReportsFullBuildLevelAsync()
    {
        using var fixture = new TestSolutionFixture();
        using var workspaceManager = TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        await workspaceManager.LoadSolutionAsync(fixture.SolutionPath);
        var buildEngine = TestEngines.BuildEngine(workspaceManager, TestEngines.DiagnosticEngine(workspaceManager));
        var solutionDirectory = Path.GetDirectoryName(fixture.SolutionPath)!;
        var projectDirectory = Path.Combine(solutionDirectory, "ContosoOrders.Core");

        var result = await buildEngine.RunFullBuildAsync(CancellationToken.None, maxDetails: 50, level: BuildVerifyLevel.fullBuild);

        Assert.That(result.Outcome, Is.EqualTo(EngineOutcome.Success), result.Error?.Message);
        Assert.That(result.Data!.ErrorCount, Is.Zero, result.Data.StdoutTail);
        Assert.That(result.Data.Level, Is.EqualTo(BuildVerifyLevel.fullBuild));
        Assert.That(result.Data.Detail, Is.Null.Or.Not.Contain(".roslynsentinel"));
        Assert.That(Directory.EnumerateFiles(Path.Combine(projectDirectory, "bin"), "ContosoOrders.Core.dll", SearchOption.AllDirectories), Is.Not.Empty,
            "fullBuild is the level that updates the project's own bin/");
        Assert.That(Directory.Exists(Path.Combine(solutionDirectory, ".roslynsentinel", "build", BuildIsolation.ResolveInstanceId())), Is.False);
    }

    private static string[] SnapshotFileSystemEntries(string directory) =>
        Directory.Exists(directory)
            ? Directory.EnumerateFileSystemEntries(directory, "*", SearchOption.AllDirectories)
                .Select(p => Path.GetRelativePath(directory, p))
                .Order(StringComparer.Ordinal)
                .ToArray()
            : [];

    private static Dictionary<string, DateTime> SnapshotAssemblyWriteTimes(string projectDirectory)
    {
        var snapshot = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        foreach (var folder in new[] { "bin", "obj" })
        {
            var directory = Path.Combine(projectDirectory, folder);
            if (!Directory.Exists(directory))
            {
                continue;
            }

            foreach (var assembly in Directory.EnumerateFiles(directory, "*.dll", SearchOption.AllDirectories))
            {
                snapshot[Path.GetRelativePath(projectDirectory, assembly)] = File.GetLastWriteTimeUtc(assembly);
            }
        }

        return snapshot;
    }

    [TestCase(null, null)]
    [TestCase("", null)]
    [TestCase("   ", null)]
    [TestCase("..", null)]
    [TestCase("abc-1.2_x", "abc-1.2_x")]
    [TestCase("win\\dow/s:id*", "win_dow_s_id_")]
    [TestCase("  padded  ", "padded")]
    public void SanitizeInstanceId_KeepsSafeCharactersOnly(string? raw, string? expected)
    {
        Assert.That(BuildIsolation.SanitizeInstanceId(raw), Is.EqualTo(expected));
    }

    [Test]
    public void SanitizeInstanceId_CapsLength()
    {
        Assert.That(BuildIsolation.SanitizeInstanceId(new string('a', 200))!.Length, Is.EqualTo(64));
    }

    [Test]
    [NonParallelizable] // mutates a process-wide environment variable
    public void ResolveInstanceId_UsesEnvironmentVariableWhenSetElseProcessId()
    {
        var original = Environment.GetEnvironmentVariable(BuildIsolation.InstanceIdEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(BuildIsolation.InstanceIdEnvironmentVariable, "win:1/abc");
            Assert.That(BuildIsolation.ResolveInstanceId(), Is.EqualTo("win_1_abc"));

            Environment.SetEnvironmentVariable(BuildIsolation.InstanceIdEnvironmentVariable, null);
            Assert.That(BuildIsolation.ResolveInstanceId(), Is.EqualTo(Environment.ProcessId.ToString()));
        }
        finally
        {
            Environment.SetEnvironmentVariable(BuildIsolation.InstanceIdEnvironmentVariable, original);
        }
    }

    [Test]
    [NonParallelizable] // reads the process-wide instance id environment variable
    public void GetArtifactsPath_PrunesOnlyNumericDirectoriesOfExitedProcesses()
    {
        var solutionDirectory = Path.Combine(Path.GetTempPath(), "roslynsentinel_artifacts_" + Guid.NewGuid().ToString("n"));
        var buildRoot = BuildIsolation.GetBuildRoot(solutionDirectory);
        Directory.CreateDirectory(buildRoot);
        try
        {
            // A pid no live process can have, and a named instance (never pruned here).
            var deadPidDirectory = Directory.CreateDirectory(Path.Combine(buildRoot, "2147483000")).FullName;
            var namedDirectory = Directory.CreateDirectory(Path.Combine(buildRoot, "vscode-abc123")).FullName;

            var path = BuildIsolation.GetArtifactsPath(solutionDirectory);

            Assert.That(path, Is.EqualTo(Path.Combine(buildRoot, BuildIsolation.ResolveInstanceId())));
            Assert.That(Directory.Exists(path), Is.True);
            Assert.That(Directory.Exists(deadPidDirectory), Is.False, "a pid-named directory of an exited process is pruned");
            Assert.That(Directory.Exists(namedDirectory), Is.True, "named instance directories are never pruned here");
            Assert.That(File.ReadAllText(Path.Combine(buildRoot, ".gitignore")).Trim(), Is.EqualTo("*"));
        }
        finally
        {
            Directory.Delete(solutionDirectory, recursive: true);
        }
    }

    // The plan asked for a fake process runner around two concurrent RunFullBuildAsync calls; none exists, so
    // the gate (which is what serialises them) is tested directly. The engines' wiring is covered by the build tests above.
    [Test]
    public async Task BuildGate_SecondAcquireWaitsUntilFirstLeaseIsDisposedAsync()
    {
        var gate = new BuildGate();
        var first = await gate.AcquireAsync("Build (fullBuild)", TimeSpan.FromSeconds(5));

        var secondTask = gate.AcquireAsync("RunTest", TimeSpan.FromSeconds(30));
        await Task.Delay(200);
        Assert.That(secondTask.IsCompleted, Is.False, "the second operation must wait while the first holds the gate");

        first.Dispose();
        using var second = await secondTask.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.That(second, Is.Not.Null);
    }

    [Test]
    public async Task BuildGate_TimeoutNamesTheInFlightOperationAndItsStartTimeAsync()
    {
        var gate = new BuildGate();
        var before = DateTime.UtcNow;
        using var held = await gate.AcquireAsync("Build (fullBuild)", TimeSpan.FromSeconds(5));
        var after = DateTime.UtcNow;

        var busy = await Assert.ThrowsAsync<BuildGateBusyException>(async () => await gate.AcquireAsync("RunTest", TimeSpan.FromMilliseconds(100)));

        Assert.That(busy!.InFlightOperation, Is.EqualTo("Build (fullBuild)"));
        Assert.That(busy.RequestedOperation, Is.EqualTo("RunTest"));
        Assert.That(busy.InFlightStartedUtc, Is.InRange(before, after));
        Assert.That(busy.Message, Does.StartWith("Another Build (fullBuild) started "));
        Assert.That(busy.Message, Does.Contain(busy.InFlightStartedUtc!.Value.ToString("yyyy-MM-dd HH:mm:ss") + " UTC"));
        Assert.That(busy.Message, Does.EndWith("is still running in this server; retry when it finishes."));
    }

    [Test]
    public async Task BuildGate_CancelledWaitHoldsNothingAndTheGateStaysUsableAsync()
    {
        var gate = new BuildGate();
        var first = await gate.AcquireAsync("Build (fullBuild)", TimeSpan.FromSeconds(5));
        using var cancellation = new CancellationTokenSource();
        var cancelled = gate.AcquireAsync("RunTest", TimeSpan.FromSeconds(30), cancellation.Token);

        await cancellation.CancelAsync();
        await Assert.CatchAsync<OperationCanceledException>(async () => await cancelled);

        first.Dispose();
        using var next = await gate.AcquireAsync("Build (fullBuildThrowaway)", TimeSpan.FromSeconds(5));
        Assert.That(next, Is.Not.Null);
    }

    [Test]
    public async Task BuildGate_DisposingALeaseTwiceDoesNotReleaseTheNextHoldersGateAsync()
    {
        var gate = new BuildGate();
        var first = await gate.AcquireAsync("Build (fullBuild)", TimeSpan.FromSeconds(5));
        first.Dispose();
        using var second = await gate.AcquireAsync("RunTest", TimeSpan.FromSeconds(5));

        first.Dispose();

        await Assert.ThrowsAsync<BuildGateBusyException>(async () => await gate.AcquireAsync("Build (fullBuild)", TimeSpan.FromMilliseconds(100)));
    }

    [Test]
    public async Task WaitForNoCompetingBuildAsync_WhileMatchingBuildRuns_ReturnsItAfterMaxWaitAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), "roslynsentinel_isolation_" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(directory);
        var project = Path.Combine(directory, "SlowBuild.csproj");
        await File.WriteAllTextAsync(project, """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
              <Target Name="Slow" BeforeTargets="CoreCompile">
                <Exec Command="powershell -NoProfile -Command Start-Sleep -Seconds 60" />
              </Target>
            </Project>
            """);

        var startInfo = new ProcessStartInfo("dotnet") { WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true };
        startInfo.ArgumentList.Add("build");
        startInfo.ArgumentList.Add(project);
        using var slowBuild = Process.Start(startInfo)!;
        try
        {
            // Give the dotnet CLI a moment to appear, then the wait loop itself must see it.
            var seen = await BuildIsolation.WaitForNoCompetingBuildAsync([project], TimeSpan.Zero);
            for (var attempt = 0; seen.Count == 0 && attempt < 40; attempt++)
            {
                await Task.Delay(250);
                seen = await BuildIsolation.WaitForNoCompetingBuildAsync([project], TimeSpan.Zero);
            }

            Assert.That(seen, Is.Not.Empty, "the running 'dotnet build <project>' should be reported as a competing build");

            var waitStart = Stopwatch.StartNew();
            var stillRunning = await BuildIsolation.WaitForNoCompetingBuildAsync([project], TimeSpan.FromSeconds(1));
            Assert.That(stillRunning, Is.Not.Empty);
            Assert.That(waitStart.Elapsed, Is.GreaterThanOrEqualTo(TimeSpan.FromMilliseconds(900)), "should poll for roughly maxWait before giving up");

            slowBuild.Kill(entireProcessTree: true);
            await slowBuild.WaitForExitAsync();
            var cleared = await BuildIsolation.WaitForNoCompetingBuildAsync([project], TimeSpan.FromSeconds(5));
            Assert.That(cleared, Is.Empty, "once the build exits the wait should return clear");
        }
        finally
        {
            if (!slowBuild.HasExited)
            {
                slowBuild.Kill(entireProcessTree: true);
            }

            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort temp cleanup.
            }
        }
    }
}
