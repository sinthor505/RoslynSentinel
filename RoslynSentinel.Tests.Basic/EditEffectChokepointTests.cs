using System.Text;
using System.Text.Json;

using Microsoft.CodeAnalysis;
using Microsoft.Extensions.Logging.Abstractions;

using NUnit.Framework;

using RoslynSentinel.Common;

namespace RoslynSentinel.Tests.Basic;

// Edit effects at the write chokepoint and in the operation blob. Every test uses the real manager, never FakeWorkspaceManager.
[TestFixture]
public class EditEffectChokepointTests
{
    private const string CrlfSource = "namespace T;\r\npublic class A\r\n{\r\n    public int X() => 1;\r\n}\r\n";
    private const string MixedSource = "namespace T;\r\npublic class A\n{\r\n    public int X() => 1;\r\n}\r\n";
    private const string MixedSourceWithAddedLine = "namespace T;\r\npublic class A\n{\r\n    public int X() => 1;\r\n    public int Y() => 2;\r\n}\r\n";

    [TearDown]
    public void ResetContext()
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
        var dir = Path.Combine(Path.GetTempPath(), "EditEffectChokepoint", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static ToolCallContext StartCall()
    {
        var context = new ToolCallContext("agent-1", "coder", "RenameSymbol", "rename it", 0) { SessionId = "session-1" };
        context.AssignToolCallId("a76e9f1c");
        ToolCallContext.Set(context);
        return context;
    }

    private static void RecordRename(ToolCallContext context)
    {
        context.RecordEffect(EditEffectKind.Renamed, "T.A.X", "M:T.A.X", "X", "Z", null, "A.cs");
    }

    [Test]
    public async Task SuccessfulApply_CommitsTheEffects_AndTheBlobStampsThemWithTheChangeId()
    {
        var dir = NewTempDir();
        try
        {
            var file = Path.Combine(dir, "A.cs");
            File.WriteAllBytes(file, Encoding.UTF8.GetBytes(CrlfSource));
            var context = StartCall();
            using var manager = NewManager();
            Assert.That(manager is IEditEffectIndexProvider, Is.True);
            RecordRename(context);

            var result = await manager.ApplyProposedChangesAsync(
                new() { [file] = CrlfSource.Replace("X()", "Z()") }, retryCount: 0, validateChanges: false);

            Assert.That(result.IsError, Is.False, result.Summary);
            Assert.That(context.TakePendingEffects(), Is.Empty, "the chokepoint took the pending effects");
            var committed = manager.EditEffects.Find("X");
            Assert.That(committed, Has.Count.EqualTo(1));
            Assert.That(committed[0].ChangeId, Is.Null, "the change id is minted by the caller after the chokepoint returns");
            Assert.That(committed[0].ToolCallId, Is.EqualTo("a76e9f1c"));

            // The caller then writes the blob with its change id; that stamps the effects in the blob and in the index.
            var blob = await OperationBlobWriter.WriteAsync(
                "RenameSymbol", "abc12345", [new OperationItemRecord { FilePath = file, Outcome = ItemRecordOutcome.Succeeded }], dir);

            Assert.That(blob.Written, Is.True, blob.Diagnostic);
            Assert.That(manager.EditEffects.Find("X").Single().ChangeId, Is.EqualTo("abc12345"));
            Assert.That(manager.EditEffects.MarkReverted("abc12345"), Is.EqualTo(1));
            Assert.That(manager.EditEffects.Find("X"), Is.Empty);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task RolledBackApply_CommitsNothing_AndDropsThePendingEffects()
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
                var context = StartCall();
                using var manager = NewManager();
                RecordRename(context);

                var result = await manager.ApplyProposedChangesAsync(
                    new Dictionary<FilePathWrapper, string>
                    {
                        [fileA] = MixedSourceWithAddedLine,
                        [fileB] = CrlfSource.Replace("=> 1", "=> 2"),
                    },
                    retryCount: 0, validateChanges: false, rollbackOnPartialFailure: true);

                Assert.That(result.IsError, Is.True, "B's refused write must fail the batch");
                Assert.That(manager.EditEffects.Count, Is.EqualTo(0));
                Assert.That(context.TakePendingEffects(), Is.Empty);
                Assert.That(context.StampCommittedEffects("abc12345"), Is.Empty);
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
    public async Task PartialFailureWithoutRollback_CommitsNothing()
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
                var context = StartCall();
                using var manager = NewManager();
                RecordRename(context);

                var result = await manager.ApplyProposedChangesAsync(
                    new Dictionary<FilePathWrapper, string>
                    {
                        [fileA] = MixedSourceWithAddedLine,
                        [fileB] = CrlfSource.Replace("=> 1", "=> 2"),
                    },
                    retryCount: 0, validateChanges: false, rollbackOnPartialFailure: false);

                Assert.That(result.IsError, Is.True);
                Assert.That(result.SucceededFiles, Has.Count.EqualTo(1), "file A did land");
                Assert.That(manager.EditEffects.Count, Is.EqualTo(0), "an effect claims a whole operation, so a partly failed batch commits none");
                Assert.That(context.TakePendingEffects(), Is.Empty);
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
    public async Task RefusedBeforeTheLock_CommitsNothing_AndDropsThePendingEffects()
    {
        var dir = NewTempDir();
        try
        {
            var file = Path.Combine(dir, "A.cs");
            File.WriteAllBytes(file, Encoding.UTF8.GetBytes(CrlfSource));
            var context = StartCall();
            using var manager = NewManager();
            RecordRename(context);

            // A path given as both a write and a delete target is refused before the chokepoint takes any lock.
            var result = await manager.ApplyProposedChangesAsync(
                new() { [file] = CrlfSource.Replace("X()", "Z()") }, retryCount: 0, validateChanges: false,
                deletePaths: new List<FilePathWrapper> { file });

            Assert.That(result.IsError, Is.True);
            Assert.That(manager.EditEffects.Count, Is.EqualTo(0));
            Assert.That(context.TakePendingEffects(), Is.Empty, "a refusal must not leave effects for a later apply in the same call");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task NoOpApply_CommitsNothing()
    {
        var dir = NewTempDir();
        try
        {
            var file = Path.Combine(dir, "A.cs");
            File.WriteAllBytes(file, Encoding.UTF8.GetBytes(CrlfSource));
            var context = StartCall();
            using var manager = NewManager();
            RecordRename(context);

            var result = await manager.ApplyProposedChangesAsync(
                new() { [file] = CrlfSource }, retryCount: 0, validateChanges: false);

            Assert.That(result.IsError, Is.False, result.Summary);
            Assert.That(manager.EditEffects.Count, Is.EqualTo(0), "nothing changed on disk, so nothing happened to the symbol");
            Assert.That(context.TakePendingEffects(), Is.Empty);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task ApplyWithNoContext_StillWrites_AndIndexesNothing()
    {
        var dir = NewTempDir();
        try
        {
            var file = Path.Combine(dir, "A.cs");
            File.WriteAllBytes(file, Encoding.UTF8.GetBytes(CrlfSource));
            ToolCallContext.Set(null);
            using var manager = NewManager();

            var result = await manager.ApplyProposedChangesAsync(
                new() { [file] = CrlfSource.Replace("X()", "Z()") }, retryCount: 0, validateChanges: false);

            Assert.That(result.IsError, Is.False, result.Summary);
            Assert.That(manager.EditEffects.Count, Is.EqualTo(0));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task Blob_CarriesToolCallIdAndEffects_AndKeepsTheLegacyFields()
    {
        var dir = NewTempDir();
        try
        {
            var context = StartCall();
            using var manager = NewManager();
            RecordRename(context);
            context.CommitPendingEffects(manager.EditEffects);

            var blob = await OperationBlobWriter.WriteAsync(
                "RenameSymbol", "abc12345", [new OperationItemRecord { FilePath = "A.cs", Outcome = ItemRecordOutcome.Succeeded, BeforeSource = "before" }], dir);

            Assert.That(blob.Written, Is.True, blob.Diagnostic);
            var path = OperationBlobWriter.FindBlobPath("abc12345", dir);
            Assert.That(path, Is.Not.Null);
            using var json = JsonDocument.Parse(await File.ReadAllBytesAsync(path!));
            var root = json.RootElement;
            Assert.That(root.GetProperty("toolCallId").GetString(), Is.EqualTo("a76e9f1c"));
            var effects = root.GetProperty("effects");
            Assert.That(effects.GetArrayLength(), Is.EqualTo(1));
            var effect = effects[0];
            Assert.That(effect.GetProperty("kind").GetString(), Is.EqualTo("renamed"));
            Assert.That(effect.GetProperty("simpleName").GetString(), Is.EqualTo("X"));
            Assert.That(effect.GetProperty("newName").GetString(), Is.EqualTo("Z"));
            Assert.That(effect.GetProperty("changeId").GetString(), Is.EqualTo("abc12345"));
            Assert.That(effect.GetProperty("toolCallId").GetString(), Is.EqualTo("a76e9f1c"));

            // The fields readers (undo, NotFoundEvidence) look up by name are unchanged.
            Assert.That(root.GetProperty("toolName").GetString(), Is.EqualTo("RenameSymbol"));
            Assert.That(root.GetProperty("changeId").GetString(), Is.EqualTo("abc12345"));
            Assert.That(root.TryGetProperty("generatedUtc", out _), Is.True);
            Assert.That(root.GetProperty("itemCount").GetInt32(), Is.EqualTo(1));
            Assert.That(root.GetProperty("items")[0].GetProperty("BeforeSource").GetString(), Is.EqualTo("before"));

            // The effects array reads back through the shared options.
            var back = JsonSerializer.Deserialize<List<EditEffect>>(effects.GetRawText(), EditEffect.JsonOptions);
            Assert.That(back!.Single().SimpleName, Is.EqualTo("X"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task Blob_WithNoContext_WritesAnEmptyEffectsArray_AndAnOldBlobWithoutTheFieldsStillHasItsItems()
    {
        var dir = NewTempDir();
        try
        {
            ToolCallContext.Set(null);

            await OperationBlobWriter.WriteAsync(
                "Member", "def67890", [new OperationItemRecord { FilePath = "A.cs", Outcome = ItemRecordOutcome.Succeeded }], dir);

            var path = OperationBlobWriter.FindBlobPath("def67890", dir);
            using var json = JsonDocument.Parse(await File.ReadAllBytesAsync(path!));
            Assert.That(json.RootElement.GetProperty("effects").GetArrayLength(), Is.EqualTo(0));
            Assert.That(json.RootElement.GetProperty("toolCallId").ValueKind, Is.EqualTo(JsonValueKind.Null));

            // An older blob (no toolCallId, no effects) is read by property name, so the absent fields are simply not there.
            const string oldBlob = "{\"toolName\":\"Member\",\"changeId\":\"old00001\",\"generatedUtc\":\"2026-11-01T00:00:00.0000000Z\",\"itemCount\":1,\"items\":[{\"FilePath\":\"A.cs\",\"BeforeSource\":\"x\"}]}";
            using var old = JsonDocument.Parse(oldBlob);
            Assert.That(old.RootElement.TryGetProperty("effects", out _), Is.False);
            Assert.That(old.RootElement.GetProperty("items")[0].GetProperty("BeforeSource").GetString(), Is.EqualTo("x"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
