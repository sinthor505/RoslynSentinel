// DTO-EXEMPT: test fixture class, not a DTO.
using Microsoft.Extensions.Logging.Abstractions;

namespace RoslynSentinel.Tests.Battery.Advanced;

[TestFixture]
[NonParallelizable]
public class WorkspaceDebounceReloadTests
{
    // The tests drive the debounce-driven full-solution reload (OnDebounceTimerElapsed, 2A path) through a
    // real PersistentWorkspaceManager, so they cannot run in parallel.
    [Test]
    [NonParallelizable]
    public async Task DebounceReload_OpenThrows_RecordsFailureAndKeepsOldSolution()
    {
        using var fixture = new TestSolutionFixture();
        using var manager = Basic.TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
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
        using var manager = Basic.TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
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
        using var manager = Basic.TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
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
        using var manager = Basic.TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
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

    [Test]
    [NonParallelizable]
    public async Task PostWriteFoldFails_QueuesReload_NextWriteSucceedsWithoutLoadSolution()
    {
        using var fixture = new TestSolutionFixture();
        using var manager = Basic.TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        await manager.LoadSolutionAsync(fixture.SolutionPath);

        var path = FindFixtureCsFile(manager, fixture);
        var original = await File.ReadAllTextAsync(path);
        var foldCalls = 0;
        manager.PostWriteFoldOverride = (files, ct) =>
        {
            if (Interlocked.Increment(ref foldCalls) == 1)
            {
                throw new IOException("fold boom");
            }
            return manager.ApplyInMemoryDocumentUpdatesAsync(files, ct);
        };

        var firstText = original + "\n// fold failure test\n";
        var first = await manager.ApplyProposedChangesAsync(new Dictionary<FilePathWrapper, string> { [new FilePathWrapper(path)] = firstText });
        Assert.That(first.WorkspaceInSync, Is.False, "a failed post-write fold must leave the workspace out of sync");

        // The queued reload swaps in a fresh workspace from disk, which clears the PostWriteSync latch without LoadSolution.
        await WaitUntilAsync(() => manager.ReloadFailure is null, TimeSpan.FromSeconds(60));
        Assert.That(manager.ReloadFailure, Is.Null, "the queued post-write reload did not clear the fold-failure latch");

        var second = await manager.ApplyProposedChangesAsync(new Dictionary<FilePathWrapper, string> { [new FilePathWrapper(path)] = firstText + "// second\n" });
        Assert.That(second.Summary, Does.Not.StartWith("Refused"), second.Summary);
        Assert.That(second.WorkspaceInSync, Is.True);
        Assert.That(foldCalls, Is.EqualTo(2));
    }

    [Test]
    [NonParallelizable]
    public async Task PostWriteFoldFails_ReloadAlsoFails_LatchStaysAndNextWriteIsRefused()
    {
        using var fixture = new TestSolutionFixture();
        using var manager = Basic.TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        await manager.LoadSolutionAsync(fixture.SolutionPath);

        var path = FindFixtureCsFile(manager, fixture);
        var original = await File.ReadAllTextAsync(path);
        var foldCalls = 0;
        manager.PostWriteFoldOverride = (files, ct) =>
        {
            if (Interlocked.Increment(ref foldCalls) == 1)
            {
                throw new IOException("fold boom");
            }
            return manager.ApplyInMemoryDocumentUpdatesAsync(files, ct);
        };
        manager.OpenSolutionOverride = (ws, p, ct) => throw new IOException("boom");

        var first = await manager.ApplyProposedChangesAsync(new Dictionary<FilePathWrapper, string> { [new FilePathWrapper(path)] = original + "\n// fold failure test\n" });
        Assert.That(first.WorkspaceInSync, Is.False, "a failed post-write fold must leave the workspace out of sync");

        // The queued reload also fails: the latch moves to PostWriteReload, the old workspace is kept, and it stays set.
        await WaitUntilAsync(() => manager.ReloadFailure is { Trigger: WorkspaceReloadTriggerKind.PostWriteReload }, TimeSpan.FromSeconds(60));
        Assert.That(manager.ReloadFailure, Is.Not.Null, "the failed queued reload did not record a failure");
        Assert.That(manager.ReloadFailure!.Trigger, Is.EqualTo(WorkspaceReloadTriggerKind.PostWriteReload));
        Assert.That(manager.ReloadFailure.OldWorkspaceKept, Is.True);

        var second = await manager.ApplyProposedChangesAsync(new Dictionary<FilePathWrapper, string> { [new FilePathWrapper(path)] = original + "\n// fold failure test\n// second\n" });
        Assert.That(second.Summary, Does.StartWith("Refused - the workspace is stale"), second.Summary);
        Assert.That(foldCalls, Is.EqualTo(1));
    }

    [Test]
    [NonParallelizable]
    public async Task DebounceReload_FailsOnceThenSucceeds_RetriesWithoutRecordingFailure()
    {
        using var fixture = new TestSolutionFixture();
        using var manager = Basic.TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        await manager.LoadSolutionAsync(fixture.SolutionPath);

        var projectFile = FindFixtureProjectFile(manager, fixture);
        var projectCountBefore = manager.CurrentSolution!.Projects.Count();
        var before = manager.CurrentSolution;
        // Capture the default open seam so the retry can delegate to the real open path.
        var defaultOpen = manager.OpenSolutionOverride;
        var openCalls = 0;
        manager.DebounceRetryDelayMs = 200;
        manager.OpenSolutionOverride = (ws, path, ct) =>
        {
            if (Interlocked.Increment(ref openCalls) == 1)
            {
                throw new IOException("boom");
            }
            return defaultOpen!(ws, path, ct);
        };

        var originalContent = await File.ReadAllTextAsync(projectFile);
        await File.WriteAllTextAsync(projectFile, originalContent + "\n<!-- debounce reload retry -->\n");

        // Sample ReloadFailure on every poll: a failure must never be visible while the retry is pending.
        var failureSeen = false;
        await WaitUntilAsync(() =>
        {
            failureSeen |= manager.ReloadFailure is not null;
            return !ReferenceEquals(manager.CurrentSolution, before);
        }, TimeSpan.FromSeconds(20));

        Assert.That(manager.CurrentSolution, Is.Not.SameAs(before), "the retried debounce reload did not swap in a new solution within 20 s");
        Assert.That(failureSeen, Is.False, "ReloadFailure was recorded even though the retry succeeded");
        Assert.That(manager.ReloadFailure, Is.Null);
        Assert.That(Volatile.Read(ref openCalls), Is.GreaterThanOrEqualTo(2));
        Assert.That(manager.CurrentSolution!.Projects.Count(), Is.EqualTo(projectCountBefore));
    }

    [Test]
    [NonParallelizable]
    public async Task DebounceReload_FailsTwice_RecordsFailureOnlyAfterRetry()
    {
        using var fixture = new TestSolutionFixture();
        using var manager = Basic.TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        await manager.LoadSolutionAsync(fixture.SolutionPath);

        var projectFile = FindFixtureProjectFile(manager, fixture);
        var before = manager.CurrentSolution;
        var openCalls = 0;
        manager.DebounceRetryDelayMs = 200;
        manager.OpenSolutionOverride = (ws, path, ct) =>
        {
            Interlocked.Increment(ref openCalls);
            throw new IOException("boom");
        };

        var originalContent = await File.ReadAllTextAsync(projectFile);
        await File.WriteAllTextAsync(projectFile, originalContent + "\n<!-- debounce reload double failure -->\n");
        await WaitUntilAsync(() => manager.ReloadFailure is not null, TimeSpan.FromSeconds(20));

        var failure = manager.ReloadFailure;
        Assert.That(failure, Is.Not.Null, "the retried debounce reload did not record a failure within 20 s");
        // >= because file-watcher events can add an extra debounce run.
        Assert.That(Volatile.Read(ref openCalls), Is.GreaterThanOrEqualTo(2));
        Assert.That(failure!.Trigger, Is.EqualTo(WorkspaceReloadTriggerKind.ProjectFileChange));
        Assert.That(failure.Attempts, Is.EqualTo(1));
        Assert.That(failure.OldWorkspaceKept, Is.True);
        Assert.That(manager.CurrentSolution, Is.SameAs(before));
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

    private static string FindFixtureCsFile(PersistentWorkspaceManager manager, TestSolutionFixture fixture)
    => manager.CurrentSolution!.Projects
        .SelectMany(p => p.Documents)
        .Select(d => d.FilePath)
        .First(f => f is not null && f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) && File.Exists(f) && f.StartsWith(fixture.SolutionDirectory, StringComparison.OrdinalIgnoreCase))!;
}
