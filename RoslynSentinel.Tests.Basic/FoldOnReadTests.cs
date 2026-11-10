// DTO-EXEMPT: test fixture class, not a DTO.
using System.Diagnostics;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging.Abstractions;

using NUnit.Framework;

using RoslynSentinel.Common;
using RoslynSentinel.Tests.Fakes;

namespace RoslynSentinel.Tests.Basic;

// Read-path fold (stale-read follow-ups). SyncFileFromDiskIfStaleAsync folds one file's disk text into memory when it
// differs, under the cross-process write lock with FoldOnReadWait, and fails open (no throw, nothing folded) when the
// file vanished, has no document, or the lock is busy. FlushPendingFoldsAsync folds every queued path in one pass, but
// leaves everything queued when a structural file (.csproj) is pending, when the lock is busy, or when nothing is queued
// (no lock is taken). A flushed fold must not later trip the FoldPending guard on a write.
[TestFixture]
public class FoldOnReadTests
{
    private const string Original = "namespace T;\r\npublic class A\r\n{\r\n    public int X() => 1;\r\n}\r\n";

    private readonly List<string> _tempDirs = new();

    [SetUp]
    public void ResetIdentity() => ToolCallContext.Set(null);

    [TearDown]
    public void ClearIdentity()
    {
        ToolCallContext.Set(null);
        foreach (var dir in _tempDirs)
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }

        _tempDirs.Clear();
    }

    private static string Variant(int n) => Original.Replace("=> 1", $"=> {n}");

    private static void AsAgent(string agentId) =>
        ToolCallContext.Set(new ToolCallContext(agentId, null, "WriteFile", null, 0));

    private string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "FoldOnRead", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    // Real documents whose FilePath is the temp file, so the fold has an in-memory text to compare with disk.
    private static Solution SolutionWithDocuments(params (string File, string Text)[] documents)
    {
        var project = new AdhocWorkspace().AddProject("FoldOnRead", LanguageNames.CSharp);
        foreach (var (file, text) in documents)
        {
            project = project.AddDocument(Path.GetFileName(file), SourceText.From(text), filePath: file).Project;
        }

        return project.Solution;
    }

    // The lock root is a temp directory (never the repo root); a short fold-on-read wait keeps busy cases fast.
    private static PersistentWorkspaceManager NewManager(string root, params (string File, string Text)[] documents)
    {
        var manager = Battery.Basic.TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        manager.EolModeOverride = EolMode.Crlf;
        manager.SetTestSolution(SolutionWithDocuments(documents));
        manager.SetTestSolutionRoot(root);
        manager.WriteLockOptions = new() { FoldOnReadWait = TimeSpan.FromMilliseconds(100) };
        return manager;
    }

    private static WriteLockHolder OtherHolder() =>
        new("other-session", "other-agent", "WriteFile", "held by a test", DateTime.UtcNow);

    private static async Task<WriteLockLease> HoldLock(string root)
    {
        var held = await CrossProcessWriteLock.AcquireAsync(root, OtherHolder(), TimeSpan.FromMilliseconds(200), CancellationToken.None);
        Assert.That(held.Acquired, Is.True, "Test setup: the lock must be free before the other holder takes it.");
        return held.Lease!;
    }

    private static async Task<string> InMemoryText(PersistentWorkspaceManager manager, string file)
    {
        var documentId = manager.CurrentSolution!.GetDocumentIdsWithFilePath(file).Single();
        return (await manager.CurrentSolution.GetDocument(documentId)!.GetTextAsync()).ToString();
    }

    [Test]
    [CancelAfter(30000)]
    public async Task Sync_DiskDiffersFromMemory_FoldsDocumentAndBumpsWorkspaceVersion()
    {
        var root = NewTempDir();
        var file = Path.Combine(root, "A.cs");
        File.WriteAllText(file, Variant(3));
        using var manager = NewManager(root, (file, Original));
        var versionBefore = manager.WorkspaceVersion;

        await manager.SyncFileFromDiskIfStaleAsync(new FilePathWrapper(file), CancellationToken.None);

        Assert.That(await InMemoryText(manager, file), Is.EqualTo(Variant(3)));
        Assert.That(manager.WorkspaceVersion, Is.GreaterThan(versionBefore));
    }

    [Test]
    [CancelAfter(30000)]
    public async Task Sync_DiskEqualsMemory_LeavesSolutionUntouched()
    {
        var root = NewTempDir();
        var file = Path.Combine(root, "A.cs");
        File.WriteAllText(file, Original);
        using var manager = NewManager(root, (file, Original));
        var solutionBefore = manager.CurrentSolution;
        var versionBefore = manager.WorkspaceVersion;

        await manager.SyncFileFromDiskIfStaleAsync(new FilePathWrapper(file), CancellationToken.None);

        Assert.That(manager.CurrentSolution, Is.SameAs(solutionBefore));
        Assert.That(manager.WorkspaceVersion, Is.EqualTo(versionBefore));
    }

    [Test]
    [CancelAfter(30000)]
    public async Task Sync_LockBusy_FailsOpen_DocumentNotFolded_NoThrow()
    {
        var root = NewTempDir();
        var file = Path.Combine(root, "A.cs");
        File.WriteAllText(file, Variant(3));
        using var manager = NewManager(root, (file, Original));
        var held = await HoldLock(root);
        try
        {
            var versionBefore = manager.WorkspaceVersion;

            await Assert.DoesNotThrowAsync(() => manager.SyncFileFromDiskIfStaleAsync(new FilePathWrapper(file), CancellationToken.None));

            Assert.That(await InMemoryText(manager, file), Is.EqualTo(Original), "A busy lock must leave the document unfolded.");
            Assert.That(manager.WorkspaceVersion, Is.EqualTo(versionBefore));
        }
        finally
        {
            await held.DisposeAsync();
        }
    }

    [Test]
    [CancelAfter(30000)]
    public async Task Sync_FileDeletedFromDisk_DoesNotThrow_AndDocumentUnchanged()
    {
        var root = NewTempDir();
        var file = Path.Combine(root, "A.cs");
        File.WriteAllText(file, Original);
        using var manager = NewManager(root, (file, Original));
        File.Delete(file);

        await Assert.DoesNotThrowAsync(() => manager.SyncFileFromDiskIfStaleAsync(new FilePathWrapper(file), CancellationToken.None));

        Assert.That(await InMemoryText(manager, file), Is.EqualTo(Original));
    }

    [Test]
    [CancelAfter(30000)]
    public async Task Sync_PathWithNoDocument_DoesNotThrow()
    {
        var root = NewTempDir();
        var file = Path.Combine(root, "A.cs");
        var other = Path.Combine(root, "Other.cs");
        File.WriteAllText(file, Original);
        File.WriteAllText(other, Variant(3));
        using var manager = NewManager(root, (file, Original));
        var solutionBefore = manager.CurrentSolution;

        await Assert.DoesNotThrowAsync(() => manager.SyncFileFromDiskIfStaleAsync(new FilePathWrapper(other), CancellationToken.None));

        Assert.That(manager.CurrentSolution, Is.SameAs(solutionBefore));
    }

    [Test]
    [CancelAfter(30000)]
    public async Task Sync_LargeEqualFile_CompletesWithinBudget()
    {
        // Gross-regression guard only: this catches an accidental quadratic or per-line rescan. No budget is claimed.
        var root = NewTempDir();
        var file = Path.Combine(root, "Large.cs");
        var large = "namespace T;\r\n" + string.Concat(Enumerable.Repeat("// padding line for the large equal-file check\r\n", 22000));
        File.WriteAllText(file, large);
        using var manager = NewManager(root, (file, large));
        var solutionBefore = manager.CurrentSolution;

        var clock = Stopwatch.StartNew();
        await manager.SyncFileFromDiskIfStaleAsync(new FilePathWrapper(file), CancellationToken.None);
        clock.Stop();

        Assert.That(manager.CurrentSolution, Is.SameAs(solutionBefore));
        Assert.That(clock.ElapsedMilliseconds, Is.LessThan(2000));
    }

    [Test]
    [CancelAfter(30000)]
    public async Task Sync_RemovesPathFromPendingAfterFold()
    {
        var root = NewTempDir();
        var file = Path.Combine(root, "A.cs");
        File.WriteAllText(file, Variant(3));
        using var manager = NewManager(root, (file, Original));
        manager.AddPendingChangeForTest(file);
        Assert.That(manager.IsPendingChangeForTest(file), Is.True, "Test setup: the path must be queued.");

        await manager.SyncFileFromDiskIfStaleAsync(new FilePathWrapper(file), CancellationToken.None);

        Assert.That(manager.IsPendingChangeForTest(file), Is.False);
    }

    [Test]
    [CancelAfter(30000)]
    public async Task Flush_QueuedChange_FoldsImmediatelyAndDequeues()
    {
        var root = NewTempDir();
        var file = Path.Combine(root, "A.cs");
        File.WriteAllText(file, Variant(3));
        using var manager = NewManager(root, (file, Original));
        manager.AddPendingChangeForTest(file);
        var versionBefore = manager.WorkspaceVersion;

        await manager.FlushPendingFoldsAsync(CancellationToken.None);

        Assert.That(await InMemoryText(manager, file), Is.EqualTo(Variant(3)));
        Assert.That(manager.IsPendingChangeForTest(file), Is.False);
        Assert.That(manager.WorkspaceVersion, Is.GreaterThan(versionBefore));
    }

    [Test]
    [CancelAfter(30000)]
    public async Task Flush_NothingPending_RunsNoFold()
    {
        var root = NewTempDir();
        var file = Path.Combine(root, "A.cs");
        File.WriteAllText(file, Original);
        using var manager = NewManager(root, (file, Original));
        // A long fold-on-read wait makes a lock attempt show up in the timing: a fold that took the lock would take ~5 s.
        manager.WriteLockOptions = new() { FoldOnReadWait = TimeSpan.FromSeconds(5) };
        var solutionBefore = manager.CurrentSolution;
        var held = await HoldLock(root);
        try
        {
            var clock = Stopwatch.StartNew();
            await manager.FlushPendingFoldsAsync(CancellationToken.None);
            clock.Stop();

            Assert.That(clock.ElapsedMilliseconds, Is.LessThan(500), "With nothing queued, flush must return without taking the lock.");
            Assert.That(manager.CurrentSolution, Is.SameAs(solutionBefore));
        }
        finally
        {
            await held.DisposeAsync();
        }
    }

    [Test]
    [CancelAfter(30000)]
    public async Task Flush_PendingCsproj_StaysQueuedAndNothingFolded()
    {
        var root = NewTempDir();
        var file = Path.Combine(root, "A.cs");
        var csproj = Path.Combine(root, "Proj.csproj");
        File.WriteAllText(file, Variant(3));
        using var manager = NewManager(root, (file, Original));
        manager.AddPendingChangeForTest(csproj);
        manager.AddPendingChangeForTest(file);
        var versionBefore = manager.WorkspaceVersion;

        await manager.FlushPendingFoldsAsync(CancellationToken.None);

        Assert.That(manager.IsPendingChangeForTest(csproj), Is.True, "A pending structural file must stay queued for the debounce reload.");
        Assert.That(manager.IsPendingChangeForTest(file), Is.True, "The .cs change must stay queued alongside the structural file.");
        Assert.That(await InMemoryText(manager, file), Is.EqualTo(Original));
        Assert.That(manager.WorkspaceVersion, Is.EqualTo(versionBefore));
    }

    [Test]
    [CancelAfter(30000)]
    public async Task Flush_LockBusy_FailsOpen_StaysQueued()
    {
        var root = NewTempDir();
        var file = Path.Combine(root, "A.cs");
        File.WriteAllText(file, Variant(3));
        using var manager = NewManager(root, (file, Original));
        manager.AddPendingChangeForTest(file);
        var held = await HoldLock(root);
        try
        {
            var versionBefore = manager.WorkspaceVersion;

            await Assert.DoesNotThrowAsync(() => manager.FlushPendingFoldsAsync(CancellationToken.None));

            Assert.That(manager.IsPendingChangeForTest(file), Is.True, "A busy lock must leave the change queued.");
            Assert.That(await InMemoryText(manager, file), Is.EqualTo(Original));
            Assert.That(manager.WorkspaceVersion, Is.EqualTo(versionBefore));
        }
        finally
        {
            await held.DisposeAsync();
        }
    }

    [Test]
    [CancelAfter(30000)]
    public async Task Flush_ThenWrite_IsNotRefusedFoldPending()
    {
        var root = NewTempDir();
        var file = Path.Combine(root, "A.cs");
        File.WriteAllText(file, Variant(3));
        using var manager = NewManager(root, (file, Original));
        manager.AddPendingChangeForTest(file);
        await manager.FlushPendingFoldsAsync(CancellationToken.None);
        Assert.That(await InMemoryText(manager, file), Is.EqualTo(Variant(3)), "Test setup: the flush must fold before the write.");
        AsAgent("agentA");

        var result = await manager.ApplyProposedChangesAsync(
            new() { [file] = Variant(2) }, retryCount: 0, validateChanges: false);

        Assert.That(result.IsError, Is.False, result.Summary);
        Assert.That(result.RefusalCode, Is.Not.EqualTo(ToolErrorCode.FoldPending));
        Assert.That(await File.ReadAllTextAsync(file), Is.EqualTo(Variant(2)));
    }
}
