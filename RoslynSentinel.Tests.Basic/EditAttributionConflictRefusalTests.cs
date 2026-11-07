using System.Text;

using Microsoft.CodeAnalysis;
using Microsoft.Extensions.Logging.Abstractions;

using NUnit.Framework;

using RoslynSentinel.Common;

namespace RoslynSentinel.Tests.Basic;

// Concurrent-edit refusal at the write chokepoint: a batch is refused when a different agent wrote one of its targets
// after the calling tool started. Every test uses the real manager, never FakeWorkspaceManager.
[TestFixture]
public class EditAttributionConflictRefusalTests
{
    private const string CrlfSource = "namespace T;\r\npublic class A\r\n{\r\n    public int X() => 1;\r\n}\r\n";

    [SetUp]
    public void SetUpIdentity()
    {
        ResetIdentity();
    }

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
        var dir = Path.Combine(Path.GetTempPath(), "EditAttributionConflictRefusal", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string Variant(int n) => CrlfSource.Replace("=> 1", $"=> {n}");

    private static void Seed(string file) => File.WriteAllBytes(file, Encoding.UTF8.GetBytes(CrlfSource));

    [Test]
    public async Task DifferentAgentWroteAfterStart_RefusesNamesWriterAndWritesNothing()
    {
        var dir = NewTempDir();
        try
        {
            var file = Path.Combine(dir, "A.cs");
            Seed(file);
            var bContent = Variant(3);
            using var manager = NewManager();

            ToolCallContext.Set(new ToolCallContext("B", "implementer", "Member", "add Foo", 0));
            var writeB = await manager.ApplyProposedChangesAsync(
                new() { [file] = bContent }, retryCount: 0, validateChanges: false);
            Assert.That(writeB.IsError, Is.False, writeB.Summary);

            ToolCallContext.Set(new ToolCallContext("A", "coder", "ReplaceSnippet", "rename the constant", 0));
            var result = await manager.ApplyProposedChangesAsync(
                new() { [file] = Variant(2) }, retryCount: 0, validateChanges: false);

            Assert.That(result.IsError, Is.True, result.Summary);
            Assert.That(result.RefusalCode, Is.EqualTo(ToolErrorCode.ConcurrentEditRefused));
            Assert.That(result.Summary, Does.Contain("MULTI-AGENT:"));
            Assert.That(result.Summary, Does.Contain("agent 'B'"));
            Assert.That(result.Summary, Does.Contain("via Member"));
            Assert.That(result.FailedFiles.ContainsKey(file), Is.True);
            Assert.That(await File.ReadAllTextAsync(file), Is.EqualTo(bContent));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task SameAgent_IsNotRefused()
    {
        var dir = NewTempDir();
        try
        {
            var file = Path.Combine(dir, "A.cs");
            Seed(file);
            using var manager = NewManager();
            ToolCallContext.Set(new ToolCallContext("A", "coder", "ReplaceSnippet", "first write", 0));

            var first = await manager.ApplyProposedChangesAsync(
                new() { [file] = Variant(2) }, retryCount: 0, validateChanges: false);
            Assert.That(first.IsError, Is.False, first.Summary);

            var second = await manager.ApplyProposedChangesAsync(
                new() { [file] = Variant(3) }, retryCount: 0, validateChanges: false);
            Assert.That(second.IsError, Is.False, second.Summary);
            Assert.That(await File.ReadAllTextAsync(file), Is.EqualTo(Variant(3)));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task SuccessfulWrite_RecordsTheWriteTimeOnTheCall()
    {
        var dir = NewTempDir();
        try
        {
            var file = Path.Combine(dir, "A.cs");
            Seed(file);
            using var manager = NewManager();
            var context = new ToolCallContext("A", "coder", "ReplaceSnippet", "stamp test", 0);
            ToolCallContext.Set(context);
            var before = DateTime.UtcNow;

            var result = await manager.ApplyProposedChangesAsync(
                new() { [file] = Variant(2) }, retryCount: 0, validateChanges: false);

            Assert.That(result.IsError, Is.False, result.Summary);
            Assert.That(context.LastWriteUtc, Is.Not.Null);
            Assert.That(context.LastWriteUtc!.Value, Is.GreaterThanOrEqualTo(before).And.LessThanOrEqualTo(DateTime.UtcNow));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task RefusedWrite_DoesNotRecordAWriteTime()
    {
        var dir = NewTempDir();
        try
        {
            var file = Path.Combine(dir, "A.cs");
            Seed(file);
            using var manager = NewManager();

            ToolCallContext.Set(new ToolCallContext("B", "implementer", "Member", "add Foo", 0));
            var writeB = await manager.ApplyProposedChangesAsync(
                new() { [file] = Variant(3) }, retryCount: 0, validateChanges: false);
            Assert.That(writeB.IsError, Is.False, writeB.Summary);

            var context = new ToolCallContext("A", "coder", "ReplaceSnippet", "stamp test", 0);
            ToolCallContext.Set(context);
            var result = await manager.ApplyProposedChangesAsync(
                new() { [file] = Variant(2) }, retryCount: 0, validateChanges: false);

            Assert.That(result.RefusalCode, Is.EqualTo(ToolErrorCode.ConcurrentEditRefused));
            Assert.That(context.LastWriteUtc, Is.Null);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task NullAgentIds_CallerWithoutIdentity_IsNotRefused()
    {
        var dir = NewTempDir();
        try
        {
            var file = Path.Combine(dir, "A.cs");
            Seed(file);
            using var manager = NewManager();

            ToolCallContext.Set(new ToolCallContext("B", "implementer", "Member", "add Foo", 0));
            var writeB = await manager.ApplyProposedChangesAsync(
                new() { [file] = Variant(3) }, retryCount: 0, validateChanges: false);
            Assert.That(writeB.IsError, Is.False, writeB.Summary);

            ToolCallContext.Set(new ToolCallContext(null, null, "ReplaceSnippet", "anonymous call", 0));
            var anonymous = await manager.ApplyProposedChangesAsync(
                new() { [file] = Variant(4) }, retryCount: 0, validateChanges: false);
            Assert.That(anonymous.IsError, Is.False, anonymous.Summary);

            ToolCallContext.Set(null);
            var noContext = await manager.ApplyProposedChangesAsync(
                new() { [file] = Variant(5) }, retryCount: 0, validateChanges: false);
            Assert.That(noContext.IsError, Is.False, noContext.Summary);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task NullAgentIds_AnonymousWriter_IsNotRefused()
    {
        var dir = NewTempDir();
        try
        {
            var file = Path.Combine(dir, "A.cs");
            Seed(file);
            using var manager = NewManager();

            ToolCallContext.Set(null);
            var anonymousWrite = await manager.ApplyProposedChangesAsync(
                new() { [file] = Variant(3) }, retryCount: 0, validateChanges: false);
            Assert.That(anonymousWrite.IsError, Is.False, anonymousWrite.Summary);

            ToolCallContext.Set(new ToolCallContext("A", "coder", "ReplaceSnippet", "rename the constant", 0));
            var result = await manager.ApplyProposedChangesAsync(
                new() { [file] = Variant(4) }, retryCount: 0, validateChanges: false);
            Assert.That(result.IsError, Is.False, result.Summary);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task WriterBeforeCallStart_IsNotRefused()
    {
        var dir = NewTempDir();
        try
        {
            var file = Path.Combine(dir, "A.cs");
            Seed(file);
            using var manager = NewManager();

            ToolCallContext.Set(new ToolCallContext("B", "implementer", "Member", "add Foo", 0));
            var writeB = await manager.ApplyProposedChangesAsync(
                new() { [file] = Variant(3) }, retryCount: 0, validateChanges: false);
            Assert.That(writeB.IsError, Is.False, writeB.Summary);

            var startSeq = manager.EditAttributions.CurrentSeq;
            ToolCallContext.Set(new ToolCallContext("A", "coder", "ReplaceSnippet", "after B", startSeq));
            var result = await manager.ApplyProposedChangesAsync(
                new() { [file] = Variant(2) }, retryCount: 0, validateChanges: false);
            Assert.That(result.IsError, Is.False, result.Summary);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task ExactRestore_IsNotRefused()
    {
        var dir = NewTempDir();
        try
        {
            var file = Path.Combine(dir, "A.cs");
            Seed(file);
            using var manager = NewManager();

            ToolCallContext.Set(new ToolCallContext("B", "implementer", "Member", "add Foo", 0));
            var writeB = await manager.ApplyProposedChangesAsync(
                new() { [file] = Variant(3) }, retryCount: 0, validateChanges: false);
            Assert.That(writeB.IsError, Is.False, writeB.Summary);

            ToolCallContext.Set(new ToolCallContext("A", "coder", "ReplaceSnippet", "undo", 0));
            var restore = await manager.ApplyProposedChangesAsync(
                new() { [file] = CrlfSource }, retryCount: 0, validateChanges: false, exactRestore: true);
            Assert.That(restore.RefusalCode, Is.Not.EqualTo(ToolErrorCode.ConcurrentEditRefused));
            Assert.That(restore.IsError, Is.False, restore.Summary);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task RefusedBatch_WritesNothing_AndNamesEveryConflict()
    {
        var dir = NewTempDir();
        try
        {
            var file1 = Path.Combine(dir, "F1.cs");
            var file2 = Path.Combine(dir, "F2.cs");
            var file3 = Path.Combine(dir, "F3.cs");
            Seed(file1);
            Seed(file2);
            using var manager = NewManager();

            ToolCallContext.Set(new ToolCallContext("B", "implementer", "Member", "add Foo", 0));
            var writeB = await manager.ApplyProposedChangesAsync(
                new() { [file1] = Variant(3), [file2] = Variant(3) }, retryCount: 0, validateChanges: false);
            Assert.That(writeB.IsError, Is.False, writeB.Summary);
            var file1Before = await File.ReadAllTextAsync(file1);
            var file2Before = await File.ReadAllTextAsync(file2);

            ToolCallContext.Set(new ToolCallContext("A", "coder", "ReplaceSnippet", "batch", 0));
            var result = await manager.ApplyProposedChangesAsync(
                new() { [file1] = Variant(2), [file2] = Variant(2), [file3] = CrlfSource },
                retryCount: 0, validateChanges: false);

            Assert.That(result.IsError, Is.True, result.Summary);
            Assert.That(result.RefusalCode, Is.EqualTo(ToolErrorCode.ConcurrentEditRefused));
            Assert.That(result.Summary, Does.Contain("2 target file(s)"));
            Assert.That(result.FailedFiles.Count, Is.EqualTo(2));
            Assert.That(result.FailedFiles.ContainsKey(file1), Is.True);
            Assert.That(result.FailedFiles.ContainsKey(file2), Is.True);
            Assert.That(result.FailedFiles.ContainsKey(file3), Is.False);
            Assert.That(File.Exists(file3), Is.False);
            Assert.That(await File.ReadAllTextAsync(file1), Is.EqualTo(file1Before));
            Assert.That(await File.ReadAllTextAsync(file2), Is.EqualTo(file2Before));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
