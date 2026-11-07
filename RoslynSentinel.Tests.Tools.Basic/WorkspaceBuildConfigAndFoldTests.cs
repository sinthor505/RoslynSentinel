// DTO-EXEMPT: test fixture class, not a DTO.
using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Common;

namespace RoslynSentinel.Tests.Tools.Basic;

// Covers TODO #22: .props/.targets routing to the full reload, and the per-file fold reporting a read failure
// (sticky SourceFileChange record + HasUnfoldedFiles, which ApplyProposedChangesAsync folds into WorkspaceInSync).
[TestFixture]
[NonParallelizable]
public class WorkspaceBuildConfigAndFoldTests
{
    [Test]
    public void WatchedFilePatterns_IncludeBuildConfigFiles()
    {
        Assert.That(PersistentWorkspaceManager.WatchedFilePatterns, Does.Contain("*.props").And.Contain("*.targets"));
        Assert.That(PersistentWorkspaceManager.WatchedFilePatterns, Does.Contain("*.csproj"), "existing patterns stay watched");
    }

    [TestCase(".props", true)]
    [TestCase(".targets", true)]
    [TestCase(".PROPS", true)]
    [TestCase(".csproj", true)]
    [TestCase(".cs", true)]
    [TestCase(".txt", false)]
    [TestCase("", false)]
    public void IsWatchedExtension_ClassifiesByExtension(string extension, bool expected) =>
        Assert.That(PersistentWorkspaceManager.IsWatchedExtension(extension), Is.EqualTo(expected));

    [TestCase("Directory.Build.props")]
    [TestCase("Directory.Build.targets")]
    [TestCase("Directory.Packages.props")]
    [TestCase("Common.targets")]
    public void ClassifyReloadTrigger_BuildConfigFile_IsProjectFileChange(string fileName)
    {
        var path = Path.Combine(Path.GetTempPath(), "repo", fileName);

        var trigger = PersistentWorkspaceManager.ClassifyReloadTrigger([path], watcherErrored: false, overflowed: false, out var triggerFile);

        Assert.That(trigger, Is.EqualTo(WorkspaceReloadTriggerKind.ProjectFileChange));
        Assert.That(triggerFile, Is.EqualTo(fileName));
    }

    [Test]
    public void ClassifyReloadTrigger_SourceFileOnly_IsSourceFileChange()
    {
        var trigger = PersistentWorkspaceManager.ClassifyReloadTrigger([Path.Combine(Path.GetTempPath(), "A.cs")], watcherErrored: false, overflowed: false, out var triggerFile);

        Assert.That(trigger, Is.EqualTo(WorkspaceReloadTriggerKind.SourceFileChange));
        Assert.That(triggerFile, Is.EqualTo("A.cs"));
    }

    [Test]
    public async Task InMemoryFold_BuildConfigFile_RequestsFullReload()
    {
        using var fixture = new TestSolutionFixture();
        using var manager = Battery.Basic.TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        await manager.LoadSolutionAsync(fixture.SolutionPath);

        var needsFullReload = await manager.ApplyInMemoryDocumentUpdatesAsync([Path.Combine(fixture.SolutionDirectory, "Directory.Build.props")], CancellationToken.None);

        Assert.That(needsFullReload, Is.True, "a .props change must take the full reload, not the per-document fold");
    }

    [Test]
    public async Task DebounceReload_BuildConfigFileChange_SwapsInFreshSolution()
    {
        using var fixture = new TestSolutionFixture();
        using var manager = Battery.Basic.TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        await manager.LoadSolutionAsync(fixture.SolutionPath);
        var before = manager.CurrentSolution;

        await File.WriteAllTextAsync(Path.Combine(fixture.SolutionDirectory, "Directory.Build.props"), "<Project>\n</Project>\n");

        await WaitUntilAsync(() => !ReferenceEquals(manager.CurrentSolution, before), TimeSpan.FromSeconds(30));

        Assert.That(manager.CurrentSolution, Is.Not.SameAs(before), "a new Directory.Build.props did not trigger the full reload within 30 s");
        Assert.That(manager.ReloadFailure, Is.Null);
    }

    [Test]
    public async Task InMemoryFold_UnreadableFile_ReportsNotInSyncUntilALaterFoldSucceeds()
    {
        using var fixture = new TestSolutionFixture();
        using var manager = Battery.Basic.TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        await manager.LoadSolutionAsync(fixture.SolutionPath);

        var projectFile = manager.CurrentSolution!.Projects
            .Select(p => p.FilePath)
            .First(f => f is not null && File.Exists(f) && f.StartsWith(fixture.SolutionDirectory, StringComparison.OrdinalIgnoreCase))!;
        var sourceFile = manager.CurrentSolution.Projects
            .SelectMany(p => p.Documents)
            .Select(d => d.FilePath)
            .First(f => f is not null && File.Exists(f) && f.StartsWith(fixture.SolutionDirectory, StringComparison.OrdinalIgnoreCase))!;
        Assert.That(manager.HasUnfoldedFiles, Is.False);

        // FileShare.None makes the fold's read throw IOException. The fold is called directly (the watcher path swallows a
        // locked file as "our own write"), exactly as ApplyProposedChangesAsync and the debounce handler call it.
        bool needsFullReload;
        using (new FileStream(sourceFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            needsFullReload = await manager.ApplyInMemoryDocumentUpdatesAsync([sourceFile], CancellationToken.None);
        }

        Assert.That(needsFullReload, Is.False);
        // ApplyProposedChangesAsync computes WorkspaceInSync as !needsFullReload && !HasUnfoldedFiles.
        Assert.That(manager.HasUnfoldedFiles, Is.True, "an unreadable changed file must leave the workspace reported as not in sync");
        var failure = manager.ReloadFailure;
        Assert.That(failure, Is.Not.Null, "the unreadable file must leave a sticky record");
        Assert.That(failure!.Trigger, Is.EqualTo(WorkspaceReloadTriggerKind.SourceFileChange));
        Assert.That(failure.TriggerFile, Is.EqualTo(Path.GetFileName(sourceFile)));
        Assert.That(failure.OldWorkspaceKept, Is.True);
        Assert.That(failure.Cause, Does.Contain("could not be read"));
        Assert.That(failure.Cause, Does.Not.Contain(fixture.SolutionDirectory), "the cause must not leak a path");

        // While recorded, a source write is refused (the stale latch), so WorkspaceInSync is not reported true.
        var refused = await manager.ApplyProposedChangesAsync(new Dictionary<FilePathWrapper, string>
        {
            [Path.Combine(Path.GetDirectoryName(projectFile)!, "FoldProbe_Refused.cs")] = "class FoldProbeRefused { }\n",
        });
        Assert.That(refused.IsError, Is.True);
        Assert.That(refused.WorkspaceInSync, Is.False);

        // A later successful fold of the same file clears both the set and the sticky record.
        var secondNeedsFullReload = await manager.ApplyInMemoryDocumentUpdatesAsync([sourceFile], CancellationToken.None);

        Assert.That(secondNeedsFullReload, Is.False);
        Assert.That(manager.HasUnfoldedFiles, Is.False);
        Assert.That(manager.ReloadFailure, Is.Null, "a successful later fold must clear the read-failure record");

        var written = await manager.ApplyProposedChangesAsync(new Dictionary<FilePathWrapper, string>
        {
            [Path.Combine(Path.GetDirectoryName(projectFile)!, "FoldProbe_Ok.cs")] = "class FoldProbeOk { }\n",
        });
        Assert.That(written.IsError, Is.False, written.Summary);
        Assert.That(written.WorkspaceInSync, Is.True);
    }

    [Test]
    public async Task InMemoryFold_UnreadableFileThenFullLoad_ClearsUnfoldedState()
    {
        using var fixture = new TestSolutionFixture();
        using var manager = Battery.Basic.TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        await manager.LoadSolutionAsync(fixture.SolutionPath);
        var sourceFile = manager.CurrentSolution!.Projects
            .SelectMany(p => p.Documents)
            .Select(d => d.FilePath)
            .First(f => f is not null && File.Exists(f) && f.StartsWith(fixture.SolutionDirectory, StringComparison.OrdinalIgnoreCase))!;

        using (new FileStream(sourceFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            await manager.ApplyInMemoryDocumentUpdatesAsync([sourceFile], CancellationToken.None);
        }

        Assert.That(manager.HasUnfoldedFiles, Is.True);

        await manager.LoadSolutionAsync(fixture.SolutionPath);

        Assert.That(manager.HasUnfoldedFiles, Is.False, "a successful full load resyncs every file from disk");
        Assert.That(manager.ReloadFailure, Is.Null);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline && !condition())
        {
            await Task.Delay(100);
        }
    }
}
