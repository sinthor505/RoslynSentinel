using System.Diagnostics;

using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Engines.Basic;

namespace RoslynSentinel.Tests.Battery.Basic;

[TestFixture]
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
    public void DescribeCompetingBuilds_NamesTheBlockerAndTheEscapeHatch()
    {
        var message = BuildIsolation.DescribeCompetingBuilds(
            [new CompetingBuildProcess(4242, "dotnet.exe", "dotnet build " + Solution)], TimeSpan.FromSeconds(30));

        Assert.That(message, Does.Contain("pid 4242"));
        Assert.That(message, Does.Contain("useScratchDir:true"));
    }

    [Test]
    public async Task RunFullBuildAsync_WithScratchDir_WritesOutputUnderScratchNotProjectBinAsync()
    {
        using var fixture = new TestSolutionFixture();
        using var workspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        await workspaceManager.LoadSolutionAsync(fixture.SolutionPath);
        var buildEngine = new BuildEngine(workspaceManager, new DiagnosticEngine(workspaceManager));
        var solutionDirectory = Path.GetDirectoryName(fixture.SolutionPath)!;
        var projectBin = Path.Combine(solutionDirectory, "ContosoOrders.Core", "bin");
        var binBefore = SnapshotFileSystemEntries(projectBin);

        var result = await buildEngine.RunFullBuildAsync(CancellationToken.None, maxDetails: 50, useScratchDir: true);

        Assert.That(result.Outcome, Is.EqualTo(EngineOutcome.Success), result.Error?.Message);
        Assert.That(result.Data!.ErrorCount, Is.Zero, result.Data.StdoutTail);
        var scratchBin = Path.Combine(solutionDirectory, ".scratch", "roslynsentinel-builds", Environment.ProcessId.ToString(), "bin");
        Assert.That(Directory.Exists(scratchBin), Is.True, "scratch bin/ should exist after a useScratchDir build");
        Assert.That(Directory.EnumerateFiles(scratchBin, "ContosoOrders.Core.dll", SearchOption.AllDirectories), Is.Not.Empty);
        // Loading the workspace can create empty bin/Debug folders, so compare before/after rather than demanding absence.
        Assert.That(SnapshotFileSystemEntries(projectBin), Is.EqualTo(binBefore),
            "a scratch build must not write into the project's own bin/ folder");
        Assert.That(File.ReadAllText(Path.Combine(solutionDirectory, ".scratch", "roslynsentinel-builds", ".gitignore")).Trim(), Is.EqualTo("*"));
    }

    private static string[] SnapshotFileSystemEntries(string directory) =>
        Directory.Exists(directory)
            ? Directory.EnumerateFileSystemEntries(directory, "*", SearchOption.AllDirectories)
                .Select(p => Path.GetRelativePath(directory, p))
                .Order(StringComparer.Ordinal)
                .ToArray()
            : [];

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
