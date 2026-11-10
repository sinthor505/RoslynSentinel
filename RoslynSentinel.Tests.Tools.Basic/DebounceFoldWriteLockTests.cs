// DTO-EXEMPT: test fixture class, not a DTO.
using Microsoft.Extensions.Logging.Abstractions;

namespace RoslynSentinel.Tests.Battery.Advanced;

[TestFixture]
[NonParallelizable]
public class DebounceFoldWriteLockTests
{
    // The debounce fold takes the cross-process write lock (CrossProcessWriteLock) inside _solutionLock. These tests
    // drive it through a real PersistentWorkspaceManager, so they cannot run in parallel.
    [Test]
    [NonParallelizable]
    [CancelAfter(60000)]
    public async Task DebounceFold_LockFree_FoldsExternalWriteAsBefore()
    {
        using var fixture = new TestSolutionFixture();
        using var manager = Basic.TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        await manager.LoadSolutionAsync(fixture.SolutionPath);
        manager.WriteLockOptions = new() { DebounceWait = TimeSpan.FromMilliseconds(200) };

        var sourceFile = FindFixtureSourceFile(manager, fixture);
        var original = await File.ReadAllTextAsync(sourceFile);
        await File.WriteAllTextAsync(sourceFile, original + "\n// debounce lock free\n");

        // Generous wait: the watcher event, the debounce timer and the fold all run before the text appears.
        await WaitUntilAsync(() => ReadInMemoryText(manager, sourceFile)?.Contains("// debounce lock free") == true, TimeSpan.FromSeconds(10));

        Assert.That(ReadInMemoryText(manager, sourceFile), Does.Contain("// debounce lock free"),
            "the debounce fold did not fold the external write into the in-memory workspace within 10 s");
    }

    [Test]
    [NonParallelizable]
    [CancelAfter(60000)]
    public async Task DebounceFold_LockHeldElsewhere_DoesNotFoldRequeuesAndDoesNotHalt()
    {
        using var fixture = new TestSolutionFixture();
        using var manager = Basic.TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        await manager.LoadSolutionAsync(fixture.SolutionPath);
        manager.WriteLockOptions = new() { DebounceWait = TimeSpan.FromMilliseconds(200) };

        var sourceFile = FindFixtureSourceFile(manager, fixture);
        var original = await File.ReadAllTextAsync(sourceFile);

        // Trap: the first GetTextAsync on an unloaded FileTextLoader document reads the disk file, so the
        // in-memory read must happen before the external write below or it observes the modified disk text.
        Assert.That(ReadInMemoryText(manager, sourceFile), Is.EqualTo(original), "precondition: in-memory text equals disk before the external write");
        var solutionBefore = manager.CurrentSolution;

        // Another process holds the cross-process write lock for the whole test body.
        var held = await CrossProcessWriteLock.AcquireAsync(
            manager.GetSolutionRoot()!,
            new WriteLockHolder(null, "other", null, null, DateTime.UtcNow),
            TimeSpan.FromSeconds(1),
            CancellationToken.None);
        Assert.That(held.Acquired, Is.True, "the test could not take the cross-process write lock");

        await File.WriteAllTextAsync(sourceFile, original + "\n// debounce lock held elsewhere\n");

        // Several debounce rounds run while the lock is held; each must re-queue rather than fold.
        await Task.Delay(TimeSpan.FromSeconds(2));

        Assert.That(ReadInMemoryText(manager, sourceFile), Does.Not.Contain("// debounce lock held elsewhere"),
            "the debounce fold ran while another process held the write lock");
        Assert.That(manager.CurrentSolution, Is.SameAs(solutionBefore), "the debounce handler must neither fold nor reload while the write lock is busy");
        Assert.That(manager.IsSessionHalted, Is.False, "a busy write lock must not halt the session");
        Assert.That(manager.ReloadFailure, Is.Null, "a busy write lock must not record a reload failure");

        await held.Lease!.DisposeAsync();

        // The re-queued change folds once the lock is free.
        await WaitUntilAsync(() => ReadInMemoryText(manager, sourceFile)?.Contains("// debounce lock held elsewhere") == true, TimeSpan.FromSeconds(10));

        Assert.That(ReadInMemoryText(manager, sourceFile), Does.Contain("// debounce lock held elsewhere"),
            "the re-queued change was not folded within 10 s after the lock was released");
    }

    [Test]
    [NonParallelizable]
    [CancelAfter(60000)]
    public async Task DebounceFold_ReleasesLockAfterFold()
    {
        using var fixture = new TestSolutionFixture();
        using var manager = Basic.TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        await manager.LoadSolutionAsync(fixture.SolutionPath);
        manager.WriteLockOptions = new() { DebounceWait = TimeSpan.FromMilliseconds(200) };

        var sourceFile = FindFixtureSourceFile(manager, fixture);
        var original = await File.ReadAllTextAsync(sourceFile);
        await File.WriteAllTextAsync(sourceFile, original + "\n// debounce release after fold\n");
        await WaitUntilAsync(() => ReadInMemoryText(manager, sourceFile)?.Contains("// debounce release after fold") == true, TimeSpan.FromSeconds(10));
        Assert.That(ReadInMemoryText(manager, sourceFile), Does.Contain("// debounce release after fold"),
            "the debounce fold did not complete within 10 s");

        // The fold must have released the lease, so a fresh acquire succeeds without waiting.
        var after = await CrossProcessWriteLock.AcquireAsync(
            manager.GetSolutionRoot()!,
            new WriteLockHolder(null, "after-fold", null, null, DateTime.UtcNow),
            TimeSpan.FromSeconds(1),
            CancellationToken.None);
        Assert.That(after.Acquired, Is.True, "the debounce fold did not release the cross-process write lock");
        await after.Lease!.DisposeAsync();
    }

    private static string FindFixtureSourceFile(PersistentWorkspaceManager manager, TestSolutionFixture fixture)
        => manager.CurrentSolution!.Projects
            .SelectMany(p => p.Documents)
            .Select(d => d.FilePath)
            .First(f => f is not null && f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                && File.Exists(f) && f.StartsWith(fixture.SolutionDirectory, StringComparison.OrdinalIgnoreCase))!;

    private static string? ReadInMemoryText(PersistentWorkspaceManager manager, string path)
    {
        var document = manager.CurrentSolution!.Projects
            .SelectMany(p => p.Documents)
            .FirstOrDefault(d => string.Equals(d.FilePath, path, StringComparison.OrdinalIgnoreCase));
        return document?.GetTextAsync(CancellationToken.None).GetAwaiter().GetResult().ToString();
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(25);
        }
    }
}
