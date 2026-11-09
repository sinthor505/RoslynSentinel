using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging.Abstractions;

using NUnit.Framework;

using RoslynSentinel.Common;

namespace RoslynSentinel.Tests.Basic;

// Ledger refresh after writes (observed-content ledger, read-ledger plan step 3b): a writer's own entry follows its
// successful write, other agents' entries do not, a delete clears the path for every agent, and a rollback restores
// the writer's entry to the pre-image. Uses only the public API (RecordObserved, ApplyProposedChangesAsync,
// EolModeOverride, EditAttributions is not used). Every test uses the real manager, never FakeWorkspaceManager.
[TestFixture]
public class StaleReadLedgerRefreshTests
{
    private const string Original = "namespace T;\r\npublic class A\r\n{\r\n    public int X() => 1;\r\n}\r\n";

    // Mixed line endings on disk and in the document; the manager's EOL normalization decides the bytes that land.
    private const string MixedOriginal = "namespace T;\r\npublic class A\n{\r\n    public int X() => 1;\r\n}\r\n";
    private const string MixedEdit = "namespace T;\r\npublic class A\n{\r\n    public int X() => 2;\r\n}\r\n";

    [SetUp]
    public void ResetIdentity() => ToolCallContext.Set(null);

    [TearDown]
    public void ClearIdentity() => ToolCallContext.Set(null);

    private static string Variant(int n) => Original.Replace("=> 1", $"=> {n}");

    private static void AsAgent(string agentId, long startSeq = 0) =>
        ToolCallContext.Set(new ToolCallContext(agentId, null, "WriteFile", null, startSeq));

    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "StaleReadLedgerRefresh", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
        return dir;
    }

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

    private static Task<ApplyChangesResult> WriteAsync(PersistentWorkspaceManager manager, string file, string text) =>
        manager.ApplyProposedChangesAsync(
            new() { [file] = text }, retryCount: 0, validateChanges: false,
            staleReadPolicy: StaleReadPolicy.RefuseIfObservedStale);

    [Test]
    public async Task OwnWrite_RefreshesWritersEntry_SoSecondWriteWithoutReReadIsAllowed()
    {
        var dir = NewTempDir();
        try
        {
            var file = Path.Combine(dir, "A.cs");
            File.WriteAllText(file, Original);
            using var manager = NewManager((file, Original));
            AsAgent("agentA");
            manager.RecordObserved(new FilePathWrapper(file), Original);

            var first = await WriteAsync(manager, file, Variant(2));
            Assert.That(first.IsError, Is.False, first.Summary);

            var second = await WriteAsync(manager, file, Variant(3));

            Assert.That(second.IsError, Is.False, second.Summary);
            Assert.That(second.RefusalCode, Is.Not.EqualTo(ToolErrorCode.StaleRead));
            Assert.That(await File.ReadAllTextAsync(file), Is.EqualTo(Variant(3)));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task OtherAgentsEntry_IsNotRefreshedByAnotherAgentsWrite()
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

            var writeB1 = await WriteAsync(manager, file, Variant(5));
            Assert.That(writeB1.IsError, Is.False, writeB1.Summary);
            var writeB2 = await WriteAsync(manager, file, Variant(6));
            Assert.That(writeB2.IsError, Is.False, writeB2.Summary);

            // StartSeq at long.MaxValue: this call started after every recorded write, so the concurrent-edit check
            // cannot fire and only the stale-read guard decides the outcome.
            AsAgent("agentA", startSeq: long.MaxValue);
            var writeA = await WriteAsync(manager, file, Variant(2));

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
    public async Task AgentWithNoEntry_GainsEntryFromItsOwnWrite_ThenExternalChangeMakesNextWriteStaleRead()
    {
        var dir = NewTempDir();
        try
        {
            var file = Path.Combine(dir, "A.cs");
            File.WriteAllText(file, Original);
            using var manager = NewManager((file, Original));
            AsAgent("agentC");

            var first = await WriteAsync(manager, file, Variant(2));
            Assert.That(first.IsError, Is.False, first.Summary);

            // A successful write creates the writer's entry, hashed from the text that reached disk.
            Assert.That(manager.TryGetObservedHash(new FilePathWrapper(file), out var observed), Is.True);
            Assert.That(observed, Is.EqualTo(ContentFingerprint.Compute(Variant(2))));

            // Agent C now observed the file through its own write, so an external change is stale for it.
            File.WriteAllText(file, Variant(7));
            var second = await WriteAsync(manager, file, Variant(3));

            Assert.That(second.IsError, Is.True, second.Summary);
            Assert.That(second.RefusalCode, Is.EqualTo(ToolErrorCode.StaleRead));
            Assert.That(await File.ReadAllTextAsync(file), Is.EqualTo(Variant(7)));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task MixedEolWrite_RefreshesToNormalizedText_SoSecondWriteIsAllowed()
    {
        var dir = NewTempDir();
        try
        {
            var file = Path.Combine(dir, "A.cs");
            File.WriteAllText(file, MixedOriginal);
            using var manager = NewManager((file, MixedOriginal));
            AsAgent("agentA");
            manager.RecordObserved(new FilePathWrapper(file), MixedOriginal);

            var first = await WriteAsync(manager, file, MixedEdit);
            Assert.That(first.IsError, Is.False, first.Summary);

            // Precondition: the write reached disk normalized, so the refreshed ledger entry must hash those bytes.
            var onDisk = await File.ReadAllTextAsync(file);
            Assert.That(onDisk.Replace("\r\n", string.Empty), Does.Not.Contain("\n"),
                "EolModeOverride.Crlf should normalize the mixed proposal before it reaches disk.");

            var second = await WriteAsync(manager, file, onDisk.Replace("=> 2", "=> 3"));

            Assert.That(second.IsError, Is.False, second.Summary);
            Assert.That(second.RefusalCode, Is.Not.EqualTo(ToolErrorCode.StaleRead));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task Delete_RemovesEntryForEveryAgent()
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

            AsAgent("agentA");
            var delete = await manager.ApplyProposedChangesAsync(
                new Dictionary<FilePathWrapper, string>(), retryCount: 0, validateChanges: false,
                deletePaths: new List<FilePathWrapper> { file },
                staleReadPolicy: StaleReadPolicy.RefuseIfObservedStale);
            Assert.That(delete.IsError, Is.False, delete.Summary);
            Assert.That(File.Exists(file), Is.False);

            // Recreate with different text: if B's entry survived the delete, this write would be stale.
            File.WriteAllText(file, Variant(3));
            AsAgent("agentB", startSeq: long.MaxValue);
            var write = await WriteAsync(manager, file, Variant(2));

            Assert.That(write.IsError, Is.False, write.Summary);
            Assert.That(write.RefusalCode, Is.Not.EqualTo(ToolErrorCode.StaleRead));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task RollbackOfPartialFailure_RestoresWritersEntryToPreImage()
    {
        var dir = NewTempDir();
        var fileA = Path.Combine(dir, "A.cs");
        var fileB = Path.Combine(dir, "B.cs");
        try
        {
            File.WriteAllText(fileA, Original);
            File.WriteAllText(fileB, Original);
            using var manager = NewManager((fileA, Original), (fileB, Original));
            AsAgent("agentA");
            manager.RecordObserved(new FilePathWrapper(fileA), Original);

            // Dictionary order writes A first; B is read-only so its write is refused and A is rolled back.
            File.SetAttributes(fileB, FileAttributes.ReadOnly);
            var batch = await manager.ApplyProposedChangesAsync(
                new() { [fileA] = Variant(2), [fileB] = Variant(2) }, retryCount: 0, validateChanges: false,
                rollbackOnPartialFailure: true, staleReadPolicy: StaleReadPolicy.RefuseIfObservedStale);
            Assert.That(batch.IsError, Is.True, batch.Summary);
            Assert.That(batch.RolledBackFiles, Does.Contain(fileA));
            Assert.That(await File.ReadAllTextAsync(fileA), Is.EqualTo(Original));

            // The writer's entry must now be the pre-image hash: a follow-up write on the restored text is allowed.
            var followUp = await WriteAsync(manager, fileA, Variant(3));
            Assert.That(followUp.IsError, Is.False, followUp.Summary);

            // And the entry is live again: an external change after that write is refused.
            File.WriteAllText(fileA, Variant(5));
            var stale = await WriteAsync(manager, fileA, Variant(4));
            Assert.That(stale.IsError, Is.True, stale.Summary);
            Assert.That(stale.RefusalCode, Is.EqualTo(ToolErrorCode.StaleRead));
        }
        finally
        {
            if (File.Exists(fileB))
            {
                File.SetAttributes(fileB, FileAttributes.Normal);
            }

            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task RollbackOfPartialFailure_CreatesNoEntryForWriter()
    {
        var dir = NewTempDir();
        var fileA = Path.Combine(dir, "A.cs");
        var fileB = Path.Combine(dir, "B.cs");
        try
        {
            File.WriteAllText(fileA, Original);
            File.WriteAllText(fileB, Original);
            using var manager = NewManager((fileA, Original), (fileB, Original));
            AsAgent("agentA");

            // Agent A has no entry for either file. B is read-only, so A's write lands and is then rolled back.
            File.SetAttributes(fileB, FileAttributes.ReadOnly);
            var batch = await manager.ApplyProposedChangesAsync(
                new()
                {
                    [fileA] = Variant(2),
                    [fileB] = Variant(2)
                }, retryCount: 0, validateChanges: false,
                rollbackOnPartialFailure: true, staleReadPolicy: StaleReadPolicy.RefuseIfObservedStale);
            Assert.That(batch.IsError, Is.True, batch.Summary);
            Assert.That(batch.RolledBackFiles, Does.Contain(fileA));
            Assert.That(await File.ReadAllTextAsync(fileA), Is.EqualTo(Original));

            // The rolled-back write created nothing: neither file has an entry for agent A.
            Assert.That(manager.TryGetObservedHash(new FilePathWrapper(fileA), out _), Is.False);
            Assert.That(manager.TryGetObservedHash(new FilePathWrapper(fileB), out _), Is.False);
        }
        finally
        {
            if (File.Exists(fileB))
            {
                File.SetAttributes(fileB, FileAttributes.Normal);
            }

            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task WriteThroughIWorkspaceMutator_CreatesWriterEntryWithoutToolEdit()
    {
        var dir = NewTempDir();
        try
        {
            var file = Path.Combine(dir, "A.cs");
            File.WriteAllText(file, Original);
            using var manager = NewManager((file, Original));
            AsAgent("agentA");

            // An engine or helper caller holds only the interface: no RecordObserved and no tool-side edit.
            IWorkspaceMutator mutator = manager;
            var result = await mutator.ApplyProposedChangesAsync(
                new()
                {
                    [file] = Variant(2)
                }, retryCount: 0, validateChanges: false,
                staleReadPolicy: StaleReadPolicy.RefuseIfObservedStale);
            Assert.That(result.IsError, Is.False, result.Summary);

            Assert.That(manager.TryGetObservedHash(new FilePathWrapper(file), out var observed), Is.True);
            Assert.That(observed, Is.EqualTo(ContentFingerprint.Compute(Variant(2))));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
