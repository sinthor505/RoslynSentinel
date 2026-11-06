using System.Text;

using Microsoft.CodeAnalysis;
using Microsoft.Extensions.Logging.Abstractions;

using NUnit.Framework;

using RoslynSentinel.Common;

namespace RoslynSentinel.Tests.Basic;

// Recording of the edit attribution ledger at the write chokepoint. Every test uses the real manager, never FakeWorkspaceManager.
[TestFixture]
public class EditAttributionRecordingTests
{
    private const string CrlfSource = "namespace T;\r\npublic class A\r\n{\r\n    public int X() => 1;\r\n}\r\n";
    private const string MixedSource = "namespace T;\r\npublic class A\n{\r\n    public int X() => 1;\r\n}\r\n";
    private const string MixedSourceWithAddedLine = "namespace T;\r\npublic class A\n{\r\n    public int X() => 1;\r\n    public int Y() => 2;\r\n}\r\n";

    [TearDown]
    public void ResetIdentity()
    {
        ToolCallContext.Set(null);
    }

    private static PersistentWorkspaceManager NewManager()
    {
        var manager = Battery.Basic.TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        manager.EolModeOverride = EolMode.Crlf;
        manager.SetTestSolution(new AdhocWorkspace().CurrentSolution);
        return manager;
    }

    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "EditAttributionRecording", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Test]
    public async Task Write_RecordsOneEntryWithIdentity()
    {
        var dir = NewTempDir();
        try
        {
            var file = Path.Combine(dir, "A.cs");
            File.WriteAllBytes(file, Encoding.UTF8.GetBytes(CrlfSource));
            ToolCallContext.Set(new ToolCallContext("agent-1", "coder", "ReplaceSnippet", "rename the constant", 0));
            using var manager = NewManager();

            var result = await manager.ApplyProposedChangesAsync(
                new() { [file] = CrlfSource.Replace("=> 1", "=> 2") }, retryCount: 0, validateChanges: false);

            Assert.That(result.IsError, Is.False, result.Summary);
            Assert.That(manager is IEditAttributionLedgerProvider, Is.True);
            var history = manager.EditAttributions.GetHistory(file);
            Assert.That(history, Has.Count.EqualTo(1));
            var entry = history[0];
            Assert.That(entry.AgentId, Is.EqualTo("agent-1"));
            Assert.That(entry.AgentType, Is.EqualTo("coder"));
            Assert.That(entry.Tool, Is.EqualTo("ReplaceSnippet"));
            Assert.That(entry.Reason, Is.EqualTo("rename the constant"));
            Assert.That(entry.Op, Is.EqualTo(EditAttributionOp.Write));
            Assert.That(entry.HashAfter, Is.Not.Null.And.Not.Empty);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task NewFile_RecordsCreate()
    {
        var dir = NewTempDir();
        try
        {
            var file = Path.Combine(dir, "New.cs");
            using var manager = NewManager();

            var result = await manager.ApplyProposedChangesAsync(
                new() { [file] = CrlfSource }, retryCount: 0, validateChanges: false);

            Assert.That(result.IsError, Is.False, result.Summary);
            var history = manager.EditAttributions.GetHistory(file);
            Assert.That(history, Has.Count.EqualTo(1));
            Assert.That(history[0].Op, Is.EqualTo(EditAttributionOp.Create));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task Delete_RecordsDeleteWithNullHash()
    {
        var dir = NewTempDir();
        try
        {
            var file = Path.Combine(dir, "A.cs");
            File.WriteAllBytes(file, Encoding.UTF8.GetBytes(CrlfSource));
            using var manager = NewManager();

            var result = await manager.ApplyProposedChangesAsync(
                new Dictionary<FilePathWrapper, string>(), retryCount: 0, validateChanges: false,
                deletePaths: new List<FilePathWrapper> { file });

            Assert.That(result.IsError, Is.False, result.Summary);
            var history = manager.EditAttributions.GetHistory(file);
            Assert.That(history, Has.Count.EqualTo(1));
            Assert.That(history[0].Op, Is.EqualTo(EditAttributionOp.Delete));
            Assert.That(history[0].HashAfter, Is.Null);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task NoOp_RecordsNothing()
    {
        var dir = NewTempDir();
        try
        {
            var file = Path.Combine(dir, "A.cs");
            File.WriteAllBytes(file, Encoding.UTF8.GetBytes(CrlfSource));
            using var manager = NewManager();

            var result = await manager.ApplyProposedChangesAsync(
                new() { [file] = CrlfSource }, retryCount: 0, validateChanges: false);

            Assert.That(result.IsError, Is.False, result.Summary);
            Assert.That(manager.EditAttributions.GetHistory(file), Is.Empty);
            Assert.That(manager.EditAttributions.CurrentSeq, Is.EqualTo(0L));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task RolledBack_RecordsNothing()
    {
        var dir = NewTempDir();
        try
        {
            var fileA = Path.Combine(dir, "A.cs");
            File.WriteAllBytes(fileA, Encoding.UTF8.GetBytes(MixedSource));
            var fileB = Path.Combine(dir, "B.cs");
            File.WriteAllBytes(fileB, Encoding.UTF8.GetBytes(CrlfSource));
            File.SetAttributes(fileB, FileAttributes.ReadOnly);
            try
            {
                using var manager = NewManager();
                var changes = new Dictionary<FilePathWrapper, string>
                {
                    [fileA] = MixedSourceWithAddedLine,
                    [fileB] = CrlfSource.Replace("=> 1", "=> 2"),
                };

                var result = await manager.ApplyProposedChangesAsync(
                    changes, retryCount: 0, validateChanges: false, rollbackOnPartialFailure: true);

                Assert.That(result.IsError, Is.True, "B's refused write must fail the batch");
                Assert.That(manager.EditAttributions.CurrentSeq, Is.EqualTo(0L));
            }
            finally
            {
                File.SetAttributes(fileB, FileAttributes.Normal);
            }
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task PartialFailureWithoutRollback_RecordsOnlyTheWrittenFile()
    {
        var dir = NewTempDir();
        try
        {
            var fileA = Path.Combine(dir, "A.cs");
            File.WriteAllBytes(fileA, Encoding.UTF8.GetBytes(MixedSource));
            var fileB = Path.Combine(dir, "B.cs");
            File.WriteAllBytes(fileB, Encoding.UTF8.GetBytes(CrlfSource));
            File.SetAttributes(fileB, FileAttributes.ReadOnly);
            try
            {
                using var manager = NewManager();
                var changes = new Dictionary<FilePathWrapper, string>
                {
                    [fileA] = MixedSourceWithAddedLine,
                    [fileB] = CrlfSource.Replace("=> 1", "=> 2"),
                };

                var result = await manager.ApplyProposedChangesAsync(
                    changes, retryCount: 0, validateChanges: false, rollbackOnPartialFailure: false);

                Assert.That(result.IsError, Is.True, "B's refused write must fail the batch");
                Assert.That(manager.EditAttributions.GetHistory(fileA), Has.Count.EqualTo(1));
                Assert.That(manager.EditAttributions.GetHistory(fileB), Is.Empty);
            }
            finally
            {
                File.SetAttributes(fileB, FileAttributes.Normal);
            }
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task NoContext_RecordsNullIdentity()
    {
        var dir = NewTempDir();
        try
        {
            var file = Path.Combine(dir, "A.cs");
            File.WriteAllBytes(file, Encoding.UTF8.GetBytes(CrlfSource));
            ToolCallContext.Set(null);
            using var manager = NewManager();

            var result = await manager.ApplyProposedChangesAsync(
                new() { [file] = CrlfSource.Replace("=> 1", "=> 2") }, retryCount: 0, validateChanges: false);

            Assert.That(result.IsError, Is.False, result.Summary);
            var history = manager.EditAttributions.GetHistory(file);
            Assert.That(history, Has.Count.EqualTo(1));
            Assert.That(history[0].AgentId, Is.Null);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // Test double: every recording throws, so the test can prove the write still succeeds.
    private sealed class ThrowingEditAttributionLedger : EditAttributionLedger
    {
        public override EditAttribution Append(string path, ToolCallContext? context, EditAttributionOp op, string? hashAfter)
        {
            throw new InvalidOperationException("recording failed");
        }
    }

    [Test]
    public async Task RecordingThrows_ApplyStillSucceeds()
    {
        var dir = NewTempDir();
        try
        {
            var file = Path.Combine(dir, "A.cs");
            File.WriteAllBytes(file, Encoding.UTF8.GetBytes(CrlfSource));
            var proposed = CrlfSource.Replace("=> 1", "=> 2");
            using var manager = NewManager();
            manager.SetTestEditAttributionLedger(new ThrowingEditAttributionLedger());

            var result = await manager.ApplyProposedChangesAsync(
                new() { [file] = proposed }, retryCount: 0, validateChanges: false);

            Assert.That(result.IsError, Is.False, result.Summary);
            Assert.That(await File.ReadAllBytesAsync(file), Is.EqualTo(Encoding.UTF8.GetBytes(proposed)));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
