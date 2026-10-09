using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Common;

#pragma warning disable CS8618
namespace RoslynSentinel.Tests.Tools.Basic;

[TestFixture]
public class LargeResultLoadSweepTests
{
    [Test]
    public async Task LoadSolution_SweepsExpiredLargeResultsUnderSolutionRoot()
    {
        using var fixture = new TestSolutionFixture();
        var largeResultsDir = Path.Combine(fixture.SolutionDirectory, ".roslynsentinel", "largeresults");
        Directory.CreateDirectory(largeResultsDir);

        var oldFilePath = Path.Combine(largeResultsDir, "largeresult_old.json");
        var newFilePath = Path.Combine(largeResultsDir, "largeresult_new.json");

        // Create old file (8 days ago)
        File.WriteAllText(oldFilePath, "{}");
        File.SetLastWriteTimeUtc(oldFilePath, DateTime.UtcNow.AddDays(-8));

        // Create new file (1 day ago)
        File.WriteAllText(newFilePath, "{}");
        File.SetLastWriteTimeUtc(newFilePath, DateTime.UtcNow.AddDays(-1));

        Assert.That(File.Exists(oldFilePath), Is.True, "Old file should exist before load");
        Assert.That(File.Exists(newFilePath), Is.True, "New file should exist before load");

        using var workspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        await workspaceManager.LoadSolutionAsync(fixture.SolutionPath);

        // Poll for up to 10 seconds for the sweep to complete
        var maxWaitMs = 10000;
        var pollIntervalMs = 100;
        var elapsedMs = 0;
        var oldFileDeleted = false;

        while (elapsedMs < maxWaitMs)
        {
            if (!File.Exists(oldFilePath))
            {
                oldFileDeleted = true;
                break;
            }

            await Task.Delay(pollIntervalMs);
            elapsedMs += pollIntervalMs;
        }

        Assert.That(oldFileDeleted, Is.True, "Old large result file should have been swept");
        Assert.That(File.Exists(newFilePath), Is.True, "New large result file should still exist");
    }

    [Test]
    public async Task LoadSolution_NoLargeResultsFolder_StillLoads()
    {
        using var fixture = new TestSolutionFixture();
        // Do NOT create .roslynsentinel folder

        using var workspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        await workspaceManager.LoadSolutionAsync(fixture.SolutionPath);

        Assert.That(workspaceManager.CurrentSolution, Is.Not.Null, "Solution should load successfully");
        Assert.That(workspaceManager.CurrentSolution!.Projects.Count(), Is.GreaterThan(0), "Solution should have projects");
    }
}
