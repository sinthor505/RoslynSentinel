// DTO-EXEMPT: test fixture class, not a DTO.
using Microsoft.Extensions.Logging.Abstractions;

namespace RoslynSentinel.Tests.Battery.Advanced;

[TestFixture]
[NonParallelizable]
public class WorkspaceDebounceReloadTests
{
    // Both tests drive the debounce-driven full-solution reload (OnDebounceTimerElapsed, 2A path) through a
    // real PersistentWorkspaceManager, so they cannot run in parallel.
    [Test]
    [NonParallelizable]
    public async Task DebounceReload_OpenThrows_RecordsFailureAndKeepsOldSolution()
    {
        using var fixture = new TestSolutionFixture();
        using var manager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        await manager.LoadSolutionAsync(fixture.SolutionPath);

        var projectFile = FindFixtureProjectFile(manager, fixture);
        var before = manager.CurrentSolution;
        manager.OpenSolutionOverride = (ws, path, ct) => throw new IOException("boom");

        // A trailing XML comment after the root element keeps the project file well-formed.
        var originalContent = await File.ReadAllTextAsync(projectFile);
        await File.WriteAllTextAsync(projectFile, originalContent + "\n<!-- debounce reload failure -->\n");

        await WaitUntilAsync(() => manager.ReloadFailure is not null, TimeSpan.FromSeconds(10));

        var failure = manager.ReloadFailure;
        Assert.That(failure, Is.Not.Null, "the debounce reload did not record a failure within 10 s");
        Assert.That(failure!.Trigger, Is.EqualTo(WorkspaceReloadTriggerKind.ProjectFileChange));
        Assert.That(failure.OldWorkspaceKept, Is.True);
        Assert.That(failure.Attempts, Is.EqualTo(1));
        Assert.That(failure.Cause, Does.Contain("IOException"));
        Assert.That(failure.Cause, Does.Not.Contain(fixture.SolutionDirectory));
        Assert.That(manager.CurrentSolution, Is.SameAs(before));
    }

    [Test]
    [NonParallelizable]
    public async Task DebounceReload_SucceedsAfterFailure_ClearsFailureAndBumpsVersion()
    {
        using var fixture = new TestSolutionFixture();
        using var manager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        await manager.LoadSolutionAsync(fixture.SolutionPath);

        var projectFile = FindFixtureProjectFile(manager, fixture);
        var projectCountBefore = manager.CurrentSolution!.Projects.Count();
        var before = manager.CurrentSolution;
        // Capture the default open seam so it can be restored exactly after the failing override.
        var defaultOpen = manager.OpenSolutionOverride;

        // First edit: the open fails, so a failure is recorded and the old solution is kept.
        manager.OpenSolutionOverride = (ws, path, ct) => throw new IOException("boom");
        var originalContent = await File.ReadAllTextAsync(projectFile);
        await File.WriteAllTextAsync(projectFile, originalContent + "\n<!-- debounce reload failure -->\n");
        await WaitUntilAsync(() => manager.ReloadFailure is not null, TimeSpan.FromSeconds(10));
        Assert.That(manager.ReloadFailure, Is.Not.Null, "the debounce reload did not record a failure within 10 s");

        // Restore the default open, then edit again: the next debounce reload must succeed and clear the failure.
        var versionBeforeRetry = manager.WorkspaceVersion;
        manager.OpenSolutionOverride = defaultOpen;
        var failedContent = await File.ReadAllTextAsync(projectFile);
        await File.WriteAllTextAsync(projectFile, failedContent + "\n<!-- debounce reload success -->\n");
        await WaitUntilAsync(() => manager.ReloadFailure is null, TimeSpan.FromSeconds(10));

        Assert.That(manager.ReloadFailure, Is.Null, "the successful debounce reload did not clear the failure within 10 s");
        Assert.That(manager.CurrentSolution, Is.Not.SameAs(before));
        Assert.That(manager.CurrentSolution!.Projects.Count(), Is.EqualTo(projectCountBefore));
        Assert.That(manager.WorkspaceVersion, Is.GreaterThan(versionBeforeRetry));
    }

    [Test]
    [NonParallelizable]
    public async Task ReloadFromDisk_OpenThrows_RecordsPostWriteReloadAndKeepsOldSolution()
    {
        using var fixture = new TestSolutionFixture();
        using var manager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        await manager.LoadSolutionAsync(fixture.SolutionPath);

        var before = manager.CurrentSolution;
        var versionBefore = manager.WorkspaceVersion;
        manager.OpenSolutionOverride = (ws, path, ct) => throw new IOException("boom");

        // The private post-write reload is invoked directly; it is not reachable through a public mutation seam.
        await InvokeReloadFromDiskAsync(manager);

        var failure = manager.ReloadFailure;
        Assert.That(failure, Is.Not.Null, "the post-write reload did not record a failure");
        Assert.That(failure!.Trigger, Is.EqualTo(WorkspaceReloadTriggerKind.PostWriteReload));
        Assert.That(failure.OldWorkspaceKept, Is.True);
        Assert.That(failure.Attempts, Is.EqualTo(1));
        Assert.That(failure.Cause, Does.Contain("IOException"));
        Assert.That(manager.CurrentSolution, Is.SameAs(before));
        Assert.That(manager.WorkspaceVersion, Is.EqualTo(versionBefore));
    }

    [Test]
    [NonParallelizable]
    public async Task ReloadFromDisk_SucceedsAfterFailure_ClearsFailureAndBumpsVersion()
    {
        using var fixture = new TestSolutionFixture();
        using var manager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        await manager.LoadSolutionAsync(fixture.SolutionPath);

        var before = manager.CurrentSolution;
        var projectCountBefore = before!.Projects.Count();
        // Capture the default open seam so it can be restored exactly after the failing override.
        var defaultOpen = manager.OpenSolutionOverride;

        manager.OpenSolutionOverride = (ws, path, ct) => throw new IOException("boom");
        await InvokeReloadFromDiskAsync(manager);
        Assert.That(manager.ReloadFailure, Is.Not.Null, "the first post-write reload did not record a failure");

        var versionBeforeRetry = manager.WorkspaceVersion;
        manager.OpenSolutionOverride = defaultOpen;
        await InvokeReloadFromDiskAsync(manager);

        Assert.That(manager.ReloadFailure, Is.Null, "the successful post-write reload did not clear the failure");
        Assert.That(manager.CurrentSolution, Is.Not.SameAs(before));
        Assert.That(manager.CurrentSolution!.Projects.Count(), Is.EqualTo(projectCountBefore));
        Assert.That(manager.WorkspaceVersion, Is.GreaterThan(versionBeforeRetry));
    }

    private static Task InvokeReloadFromDiskAsync(PersistentWorkspaceManager manager)
    {
        var method = typeof(PersistentWorkspaceManager).GetMethod(
            "ReloadWorkspaceFromDiskAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            ?? throw new InvalidOperationException("PersistentWorkspaceManager.ReloadWorkspaceFromDiskAsync not found");
        return (Task)method.Invoke(manager, new object[] { CancellationToken.None })!;
    }

    private static string FindFixtureProjectFile(PersistentWorkspaceManager manager, TestSolutionFixture fixture)
        => manager.CurrentSolution!.Projects
            .Select(p => p.FilePath)
            .First(f => f is not null && File.Exists(f) && f.StartsWith(fixture.SolutionDirectory, StringComparison.OrdinalIgnoreCase))!;

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(25);
        }
    }
}
