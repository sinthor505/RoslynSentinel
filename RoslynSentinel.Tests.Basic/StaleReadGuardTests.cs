using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging.Abstractions;

using NUnit.Framework;

using RoslynSentinel.Common;

namespace RoslynSentinel.Tests.Basic;

// Stale-read guard at the write chokepoint (observed-content ledger, read-ledger plan step 3a): a batch is refused
// with ToolErrorCode.StaleRead when a target this agent recorded via RecordObserved no longer matches the disk
// pre-image or the in-memory document. Uses only the public API (RecordObserved, ApplyProposedChangesAsync) because
// this project has no InternalsVisibleTo for Common. Every test uses the real manager, never FakeWorkspaceManager.
[TestFixture]
public class StaleReadGuardTests
{
    private const string Original = "namespace T;\r\npublic class A\r\n{\r\n    public int X() => 1;\r\n}\r\n";

    [SetUp]
    public void ResetIdentity() => ToolCallContext.Set(null);

    [TearDown]
    public void ClearIdentity() => ToolCallContext.Set(null);

    private static string Variant(int n) => Original.Replace("=> 1", $"=> {n}");

    private static void AsAgent(string agentId, long startSeq = 0) =>
        ToolCallContext.Set(new ToolCallContext(agentId, null, "WriteFile", null, startSeq));

    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "StaleReadGuard", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    // Real documents whose FilePath is the temp file, so the in-memory branch of the guard has something to compare.
    private static Solution SolutionWithDocuments(params (string File, string Text)[] documents)
    {
        var project = new AdhocWorkspace().AddProject("StaleRead", LanguageNames.CSharp);
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

    [Test]
    public async Task ObservedStaleOnDisk_RefusesWithStaleReadAndWritesNothing()
    {
        var dir = NewTempDir();
        try
        {
            var file = Path.Combine(dir, "A.cs");
            File.WriteAllText(file, Original);
            using var manager = NewManager((file, Original));
            AsAgent("agentA");
            manager.RecordObserved(new FilePathWrapper(file), Original);
            File.WriteAllText(file, Variant(3));

            var result = await manager.ApplyProposedChangesAsync(
                new() { [file] = Variant(2) }, retryCount: 0, validateChanges: false,
                staleReadPolicy: StaleReadPolicy.RefuseIfObservedStale);

            Assert.That(result.IsError, Is.True, result.Summary);
            Assert.That(result.RefusalCode, Is.EqualTo(ToolErrorCode.StaleRead));
            Assert.That(result.Summary, Does.StartWith("StaleRead:"));
            Assert.That(result.FailedFiles.ContainsKey(new FilePathWrapper(file)), Is.True);
            Assert.That(await File.ReadAllTextAsync(file), Is.EqualTo(Variant(3)),
                "The externally written disk text must be left untouched.");
            Assert.That(manager.IsSessionHalted(), Is.False, "A stale read is non-latching.");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task ObservedStaleInMemoryOnly_RefusesWithStaleRead()
    {
        var dir = NewTempDir();
        try
        {
            var file = Path.Combine(dir, "A.cs");
            File.WriteAllText(file, Original);
            // Disk still matches the ledger; only the in-memory document differs from it.
            using var manager = NewManager((file, Variant(4)));
            AsAgent("agentA");
            manager.RecordObserved(new FilePathWrapper(file), Original);

            var result = await manager.ApplyProposedChangesAsync(
                new() { [file] = Variant(2) }, retryCount: 0, validateChanges: false,
                staleReadPolicy: StaleReadPolicy.RefuseIfObservedStale);

            Assert.That(result.IsError, Is.True, result.Summary);
            Assert.That(result.RefusalCode, Is.EqualTo(ToolErrorCode.StaleRead));
            Assert.That(result.FailedFiles.ContainsKey(new FilePathWrapper(file)), Is.True);
            Assert.That(await File.ReadAllTextAsync(file), Is.EqualTo(Original));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task ObservedStaleOnDisk_DefaultPolicy_IsNotChecked()
    {
        var dir = NewTempDir();
        try
        {
            var file = Path.Combine(dir, "A.cs");
            File.WriteAllText(file, Original);
            using var manager = NewManager((file, Original));
            AsAgent("agentA");
            manager.RecordObserved(new FilePathWrapper(file), Original);
            File.WriteAllText(file, Variant(3));

            var result = await manager.ApplyProposedChangesAsync(
                new() { [file] = Variant(2) }, retryCount: 0, validateChanges: false);

            Assert.That(result.IsError, Is.False, result.Summary);
            Assert.That(result.RefusalCode, Is.Not.EqualTo(ToolErrorCode.StaleRead));
            Assert.That(await File.ReadAllTextAsync(file), Is.EqualTo(Variant(2)));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task NoLedgerEntryForTarget_IsAllowed()
    {
        var dir = NewTempDir();
        try
        {
            var file = Path.Combine(dir, "A.cs");
            File.WriteAllText(file, Original);
            using var manager = NewManager((file, Original));
            AsAgent("agentA");
            File.WriteAllText(file, Variant(3));

            var result = await manager.ApplyProposedChangesAsync(
                new() { [file] = Variant(2) }, retryCount: 0, validateChanges: false,
                staleReadPolicy: StaleReadPolicy.RefuseIfObservedStale);

            Assert.That(result.IsError, Is.False, result.Summary);
            Assert.That(await File.ReadAllTextAsync(file), Is.EqualTo(Variant(2)));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task ExactRestore_IsExemptFromStaleCheck()
    {
        var dir = NewTempDir();
        try
        {
            var file = Path.Combine(dir, "A.cs");
            File.WriteAllText(file, Original);
            using var manager = NewManager((file, Original));
            AsAgent("agentA");
            manager.RecordObserved(new FilePathWrapper(file), Original);
            File.WriteAllText(file, Variant(3));

            var result = await manager.ApplyProposedChangesAsync(
                new() { [file] = Variant(2) }, retryCount: 0, validateChanges: false,
                exactRestore: true, staleReadPolicy: StaleReadPolicy.RefuseIfObservedStale);

            Assert.That(result.IsError, Is.False, result.Summary);
            Assert.That(result.RefusalCode, Is.Not.EqualTo(ToolErrorCode.StaleRead));
            Assert.That(await File.ReadAllTextAsync(file), Is.EqualTo(Variant(2)));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // TODO row F: drift-flagged target + stale ledger entry (latch must win) needs a test seam for PersistentWorkspaceManager._externalChanges; only the unrecoverable-breaker ordering is covered.
    // Covers the session-halt-first ordering using the public unrecoverable breaker, which the manager checks before
    // the stale-read guard. It does not exercise a drift-flagged target: no public seam sets the drift latch.
    [Test]
    public async Task SessionHaltTripped_WinsOverStaleLedgerEntry_ThrowsSessionHaltedException()
    {
        var dir = NewTempDir();
        try
        {
            var file = Path.Combine(dir, "A.cs");
            File.WriteAllText(file, Original);
            using var manager = NewManager((file, Original));
            AsAgent("agentA");
            manager.RecordObserved(new FilePathWrapper(file), Original);
            File.WriteAllText(file, Variant(3));
            ((IUnrecoverableBreaker)manager).Trip("WriteFile", "change-1", "diagnostic");

            await Assert.ThrowsAsync<SessionHaltedException>(async () => await manager.ApplyProposedChangesAsync(
                new() { [file] = Variant(2) }, retryCount: 0, validateChanges: false,
                staleReadPolicy: StaleReadPolicy.RefuseIfObservedStale));

            Assert.That(await File.ReadAllTextAsync(file), Is.EqualTo(Variant(3)));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task OtherAgentWrote_StaleForAgentA_RefusedWithStaleRead()
    {
        var dir = NewTempDir();
        try
        {
            var file = Path.Combine(dir, "A.cs");
            File.WriteAllText(file, Original);
            using var manager = NewManager((file, Original));

            AsAgent("agentA");
            manager.RecordObserved(new FilePathWrapper(file), Original);
            AsAgent("agentB");
            manager.RecordObserved(new FilePathWrapper(file), Original);

            var writeB = await manager.ApplyProposedChangesAsync(
                new() { [file] = Variant(5) }, retryCount: 0, validateChanges: false,
                staleReadPolicy: StaleReadPolicy.RefuseIfObservedStale);
            Assert.That(writeB.IsError, Is.False, writeB.Summary);

            // StartSeq at long.MaxValue means this call started after every recorded write, so the concurrent-edit
            // check cannot fire and only the stale-read guard decides the outcome.
            AsAgent("agentA", startSeq: long.MaxValue);
            var writeA = await manager.ApplyProposedChangesAsync(
                new() { [file] = Variant(2) }, retryCount: 0, validateChanges: false,
                staleReadPolicy: StaleReadPolicy.RefuseIfObservedStale);

            Assert.That(writeA.IsError, Is.True, writeA.Summary);
            Assert.That(writeA.RefusalCode, Is.EqualTo(ToolErrorCode.StaleRead));
            Assert.That(writeA.FailedFiles.ContainsKey(new FilePathWrapper(file)), Is.True);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task StaleFileInBatch_RefusesWholeBatchAndWritesNeitherFile()
    {
        var dir = NewTempDir();
        try
        {
            var stale = Path.Combine(dir, "Stale.cs");
            var fresh = Path.Combine(dir, "Fresh.cs");
            File.WriteAllText(stale, Original);
            File.WriteAllText(fresh, Original);
            using var manager = NewManager((stale, Original), (fresh, Original));
            AsAgent("agentA");
            manager.RecordObserved(new FilePathWrapper(stale), Original);
            manager.RecordObserved(new FilePathWrapper(fresh), Original);
            File.WriteAllText(stale, Variant(3));

            var result = await manager.ApplyProposedChangesAsync(
                new() { [stale] = Variant(2), [fresh] = Variant(2) }, retryCount: 0, validateChanges: false,
                staleReadPolicy: StaleReadPolicy.RefuseIfObservedStale);

            Assert.That(result.IsError, Is.True, result.Summary);
            Assert.That(result.RefusalCode, Is.EqualTo(ToolErrorCode.StaleRead));
            Assert.That(result.FailedFiles.ContainsKey(new FilePathWrapper(stale)), Is.True);
            Assert.That(result.FailedFiles.ContainsKey(new FilePathWrapper(fresh)), Is.False,
                "Only the stale file is named; the fresh file is refused by the batch, not by its own check.");
            Assert.That(await File.ReadAllTextAsync(stale), Is.EqualTo(Variant(3)));
            Assert.That(await File.ReadAllTextAsync(fresh), Is.EqualTo(Original),
                "All-or-nothing: the fresh file must not be written when the batch is refused.");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
