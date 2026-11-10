using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging.Abstractions;

using NUnit.Framework;

using RoslynSentinel.Common;
using RoslynSentinel.Tests.Fakes;

namespace RoslynSentinel.Tests.Basic;

// Cross-process write lock at the ApplyProposedChangesAsync chokepoint (stale-read follow-ups step 9b). The batch takes
// the OS lock after the revalidation gate and before pre-image capture, releases it in finally before _solutionLock, and
// refuses with ToolErrorCode.WriteLockBusy naming the holder. Validation runs before the lock, outside it. A null root
// (no SetTestSolutionRoot on an in-memory solution) takes no OS lock at all.
[TestFixture]
public class CrossProcessWriteLockChokepointTests
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

    private static void AsAgent(string agentId) =>
        ToolCallContext.Set(new ToolCallContext(agentId, null, "WriteFile", null, 0));

    private string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "CrossProcessWriteLockChokepoint", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    // Real documents whose FilePath is the temp file, so the fold guard has an in-memory text to compare with disk.
    private static Solution SolutionWithDocuments(params (string File, string Text)[] documents)
    {
        var project = new AdhocWorkspace().AddProject("CrossProcessWriteLock", LanguageNames.CSharp);
        foreach (var (file, text) in documents)
        {
            project = project.AddDocument(Path.GetFileName(file), SourceText.From(text), filePath: file).Project;
        }

        return project.Solution;
    }

    private static PersistentWorkspaceManager NewManager(params (string File, string Text)[] documents)
    {
        var manager = Battery.Basic.TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        manager.EolModeOverride = EolMode.Crlf;
        manager.SetTestSolution(SolutionWithDocuments(documents));
        return manager;
    }

    private static WriteLockHolder OtherHolder() =>
        new("other-session", "other-agent", "WriteFile", "held by a test", DateTime.UtcNow);

    private static WriteLockHolder ProbeHolder() =>
        new("probe-session", "probe-agent", "probe", null, DateTime.UtcNow);

    [Test]
    [CancelAfter(30000)]
    public async Task Write_Succeeds_AndReleasesLock()
    {
        var root = NewTempDir();
        var file = Path.Combine(root, "A.cs");
        File.WriteAllText(file, Original);
        using var manager = NewManager((file, Original));
        manager.SetTestSolutionRoot(root);
        manager.WriteLockOptions = new() { ChokepointWait = TimeSpan.FromMilliseconds(200) };
        AsAgent("agentA");

        var result = await manager.ApplyProposedChangesAsync(
            new() { [file] = Original.Replace("=> 1", "=> 2") }, retryCount: 0, validateChanges: false);

        Assert.That(result.IsError, Is.False, result.Summary);
        Assert.That(await File.ReadAllTextAsync(file), Does.Contain("=> 2"));

        var probe = await CrossProcessWriteLock.AcquireAsync(root, ProbeHolder(), TimeSpan.FromMilliseconds(200), CancellationToken.None);
        Assert.That(probe.Acquired, Is.True, "A successful write must release the cross-process lock.");
        await probe.Lease!.DisposeAsync();
    }

    [Test]
    [CancelAfter(30000)]
    public async Task LockHeldByAnotherLease_RefusesWriteLockBusyNamingHolder_NothingWritten()
    {
        var root = NewTempDir();
        var file = Path.Combine(root, "A.cs");
        File.WriteAllText(file, Original);
        using var manager = NewManager((file, Original));
        manager.SetTestSolutionRoot(root);
        manager.WriteLockOptions = new() { ChokepointWait = TimeSpan.FromMilliseconds(200) };
        AsAgent("agentA");

        var held = await CrossProcessWriteLock.AcquireAsync(root, OtherHolder(), TimeSpan.FromMilliseconds(200), CancellationToken.None);
        Assert.That(held.Acquired, Is.True, "Test setup: the lock must be free before the other holder takes it.");
        try
        {
            var refused = await manager.ApplyProposedChangesAsync(
                new() { [file] = Original.Replace("=> 1", "=> 2") }, retryCount: 0, validateChanges: false);

            Assert.That(refused.IsError, Is.True, refused.Summary);
            Assert.That(refused.RefusalCode, Is.EqualTo(ToolErrorCode.WriteLockBusy));
            Assert.That(refused.Summary, Does.Contain("other-agent"));
            Assert.That(refused.FailedFiles.ContainsKey(new FilePathWrapper(file)), Is.True);
            Assert.That(await File.ReadAllTextAsync(file), Is.EqualTo(Original),
                "A busy-lock refusal must not write anything.");
            Assert.That(manager.IsSessionHalted(), Is.False, "A busy-lock refusal is non-latching.");
        }
        finally
        {
            await held.Lease!.DisposeAsync();
        }
    }

    [Test]
    [CancelAfter(30000)]
    public async Task RefusalBeforeWrite_ReleasesLock()
    {
        var root = NewTempDir();
        var file = Path.Combine(root, "A.cs");
        // Disk diverges from the in-memory document, so the chokepoint refuses with FoldPending before any write.
        File.WriteAllText(file, Original.Replace("=> 1", "=> 3"));
        using var manager = NewManager((file, Original));
        manager.SetTestSolutionRoot(root);
        manager.WriteLockOptions = new() { ChokepointWait = TimeSpan.FromMilliseconds(200) };
        AsAgent("agentA");

        var refused = await manager.ApplyProposedChangesAsync(
            new() { [file] = Original.Replace("=> 1", "=> 2") }, retryCount: 0, validateChanges: false);

        Assert.That(refused.IsError, Is.True, refused.Summary);
        Assert.That(refused.RefusalCode, Is.EqualTo(ToolErrorCode.FoldPending));

        var probe = await CrossProcessWriteLock.AcquireAsync(root, ProbeHolder(), TimeSpan.FromMilliseconds(200), CancellationToken.None);
        Assert.That(probe.Acquired, Is.True, "A refusal after the lock is taken must still release it.");
        await probe.Lease!.DisposeAsync();
    }

    [Test]
    [CancelAfter(30000)]
    public async Task NullRoot_NoLockFileCreated_AndWriteSucceedsWhileAnotherLeaseHoldsAnyRoot()
    {
        var dir = NewTempDir();
        var file = Path.Combine(dir, "A.cs");
        File.WriteAllText(file, Original);
        using var manager = NewManager((file, Original));   // no SetTestSolutionRoot: root stays null
        manager.WriteLockOptions = new() { ChokepointWait = TimeSpan.FromMilliseconds(200) };
        AsAgent("agentA");

        // A lease on a different root must not affect a manager that has no root.
        var otherRoot = NewTempDir();
        var held = await CrossProcessWriteLock.AcquireAsync(otherRoot, OtherHolder(), TimeSpan.FromMilliseconds(200), CancellationToken.None);
        Assert.That(held.Acquired, Is.True, "Test setup: the other root's lock must be free.");
        try
        {
            var result = await manager.ApplyProposedChangesAsync(
                new() { [file] = Original.Replace("=> 1", "=> 2") }, retryCount: 0, validateChanges: false);

            Assert.That(result.IsError, Is.False, result.Summary);
            Assert.That(await File.ReadAllTextAsync(file), Does.Contain("=> 2"));
            Assert.That(Directory.Exists(Path.Combine(dir, ".roslynsentinel")), Is.False,
                "A null root must not create a lock directory next to the written file.");
        }
        finally
        {
            await held.Lease!.DisposeAsync();
        }
    }

    [Test]
    [CancelAfter(30000)]
    public async Task ValidationFailure_WithLockHeldElsewhere_ReturnsValidationError_NotBusy()
    {
        var root = NewTempDir();
        var file = Path.Combine(root, "A.cs");
        File.WriteAllText(file, Original);
        using var manager = NewManager((file, Original));
        manager.SetTestSolutionRoot(root);
        manager.WriteLockOptions = new() { ChokepointWait = TimeSpan.FromMilliseconds(200) };
        AsAgent("agentA");

        var held = await CrossProcessWriteLock.AcquireAsync(root, OtherHolder(), TimeSpan.FromMilliseconds(200), CancellationToken.None);
        Assert.That(held.Acquired, Is.True, "Test setup: the lock must be free before the other holder takes it.");
        try
        {
            var broken = "namespace T;\r\npublic class A\r\n{\r\n    public int X() => ;\r\n}\r\n";
            var result = await manager.ApplyProposedChangesAsync(
                new() { [file] = broken }, retryCount: 0, validateChanges: true);

            Assert.That(result.IsError, Is.True, result.Summary);
            Assert.That(result.Summary, Does.StartWith("Validation failed"));
            Assert.That(result.RefusalCode, Is.Not.EqualTo(ToolErrorCode.WriteLockBusy),
                "Validation runs before the lock, so a held lock must not turn a validation failure into WriteLockBusy.");
            Assert.That(await File.ReadAllTextAsync(file), Is.EqualTo(Original));
        }
        finally
        {
            await held.Lease!.DisposeAsync();
        }
    }
}
