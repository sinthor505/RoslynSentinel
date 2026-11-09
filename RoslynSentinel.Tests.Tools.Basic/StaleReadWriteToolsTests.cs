using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging.Abstractions;

using NUnit.Framework;

using RoslynSentinel.Common;
using RoslynSentinel.Engines.Basic;
using RoslynSentinel.Tools.Basic;

namespace RoslynSentinel.Tests.Battery.Basic;

// Read-ledger step 5 (plan_read_ledger_stale_read_guard.md, test rows N and O). The three opted-in write tools
// (WriteFile ReplaceFile, DeleteFile, ApplyDiff files format) must refuse a target this agent observed and that changed
// since, with ToolErrorCode.StaleRead naming the file and saying to re-read it. ApplyDiff diff format and
// ApplyUnifiedDiff stay out of scope in v1, so a stale entry must not produce StaleRead there. Runs on a real
// PersistentWorkspaceManager (never FakeWorkspaceManager, which has its own copy of the stale check).
[TestFixture]
[Category("WholeFileWriteTools")]
public class StaleReadWriteToolsTests
{
    private const string Original = "namespace T;\r\npublic class A\r\n{\r\n    public int X() => 1;\r\n}\r\n";

    // Reading agent's StartSeq past any competing write, so ConcurrentEditRefused cannot pre-empt StaleRead on one process.
    private const long StartSeqAfterEveryWrite = long.MaxValue;

    private static string External => Original.Replace("=> 1", "=> 3");

    private static string Edited => Original.Replace("=> 1", "=> 2");

    [SetUp]
    public void ResetIdentity() => ToolCallContext.Set(null);

    [TearDown]
    public void ClearIdentity() => ToolCallContext.Set(null);

    private static void AsAgent(string agentId) =>
        ToolCallContext.Set(new ToolCallContext(agentId, null, "WriteFile", null, StartSeqAfterEveryWrite));

    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "StaleReadWriteTools", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    // A real document whose FilePath is the temp file, so the in-memory branch of the guard and ExistingTextReader both see it.
    private static PersistentWorkspaceManager NewManager(string file, string documentText)
    {
        var project = new AdhocWorkspace().AddProject("StaleReadWriteTools", LanguageNames.CSharp);
        var solution = project.AddDocument(Path.GetFileName(file), SourceText.From(documentText), filePath: file).Project.Solution;
        var manager = TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        manager.EolModeOverride = EolMode.Crlf;
        manager.SetTestSolution(solution);
        return manager;
    }

    private static WholeFileWriteTools BuildWholeFileWriteTools(IWorkspaceManager workspaceManager) =>
        TestTools.WholeFileWriteTools(workspaceManager, TestTools.WorkspaceTools(workspaceManager));

    private static void AssertStaleRead(SentinelCallToolResult<object> result, string fileName)
    {
        Assert.That(result.IsError, Is.True, "a stale target must be refused");
        Assert.That(result.ErrorData, Is.Not.Null, "a refusal must carry a ResultError");
        Assert.That(result.ErrorData!.ErrorCode, Is.EqualTo(ToolErrorCode.StaleRead));
        Assert.That(result.ErrorData.ErrorCode, Is.Not.EqualTo(ToolErrorCode.Exception), "StaleRead must not be flattened to Exception");
        Assert.That(result.ErrorData.Message, Does.Contain(fileName));
        Assert.That(result.ErrorData.Message, Does.Contain("ReadFile"));
        Assert.That(result.ErrorData.Message, Does.Contain("wait a moment"));
    }

    [Test]
    public async Task WriteFileReplaceFile_ObservedThenChangedOnDisk_RefusesWithStaleReadAndWritesNothingAsync()
    {
        var dir = NewTempDir();
        try
        {
            var file = Path.Combine(dir, "A.cs");
            File.WriteAllText(file, Original);
            using var manager = NewManager(file, Original);
            AsAgent("agentA");
            manager.RecordObserved(new FilePathWrapper(file), Original);
            File.WriteAllText(file, External);
            var tools = BuildWholeFileWriteTools(manager);

            var result = await tools.WriteFileTool(
                reason: "test message", operation: WriteFileOperation.ReplaceFile, filePath: file,
                content: Edited, validateOnApply: false);

            AssertStaleRead(result, Path.GetFileName(file));
            Assert.That(await File.ReadAllTextAsync(file), Is.EqualTo(External), "the externally written disk text must be left untouched");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task WriteFileReplaceFile_NeverObserved_SucceedsAsync()
    {
        var dir = NewTempDir();
        try
        {
            var file = Path.Combine(dir, "A.cs");
            File.WriteAllText(file, Original);
            using var manager = NewManager(file, Original);
            AsAgent("agentA");
            File.WriteAllText(file, External);
            var tools = BuildWholeFileWriteTools(manager);

            var result = await tools.WriteFileTool(
                reason: "test message", operation: WriteFileOperation.ReplaceFile, filePath: file,
                content: Edited, validateOnApply: false);

            Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
            Assert.That(await File.ReadAllTextAsync(file), Is.EqualTo(Edited));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task DeleteFile_ObservedThenChangedOnDisk_RefusesWithStaleReadAndKeepsFileAsync()
    {
        var dir = NewTempDir();
        try
        {
            var file = Path.Combine(dir, "A.cs");
            File.WriteAllText(file, Original);
            using var manager = NewManager(file, Original);
            AsAgent("agentA");
            manager.RecordObserved(new FilePathWrapper(file), Original);
            File.WriteAllText(file, External);
            var tools = BuildWholeFileWriteTools(manager);

            var result = await tools.DeleteFileTool(reason: "test message", filePath: file);

            AssertStaleRead(result, Path.GetFileName(file));
            Assert.That(File.Exists(file), Is.True, "a refused delete must leave the file on disk");
            Assert.That(await File.ReadAllTextAsync(file), Is.EqualTo(External), "the externally written disk text must be left untouched");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task ApplyDiffFilesFormat_ObservedThenChangedOnDisk_RefusesWithStaleReadAndWritesNothingAsync()
    {
        var dir = NewTempDir();
        try
        {
            var file = Path.Combine(dir, "A.cs");
            File.WriteAllText(file, Original);
            using var manager = NewManager(file, Original);
            AsAgent("agentA");
            manager.RecordObserved(new FilePathWrapper(file), Original);
            File.WriteAllText(file, External);
            var tools = BuildWholeFileWriteTools(manager);

            // One line changed, so the size guard (50% line and active-code shrink) does not fire first.
            var result = await tools.ApplyDiffTool(
                reason: "test message", changesetFormat: ChangesetFormat.files, action: ProposedChangeAction.apply,
                changes: new Dictionary<string, string> { [file] = Edited }, validateOnApply: false);

            AssertStaleRead(result, Path.GetFileName(file));
            Assert.That(await File.ReadAllTextAsync(file), Is.EqualTo(External), "the externally written disk text must be left untouched");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // Row O: out-of-scope boundary. A stale entry must not turn the anchor/diff-format tools into StaleRead refusals in v1.
    [Test]
    public async Task ApplyDiffDiffFormat_StaleEntry_IsNotRefusedAsStaleReadAsync()
    {
        var dir = NewTempDir();
        try
        {
            var file = Path.Combine(dir, "A.cs");
            File.WriteAllText(file, Original);
            using var manager = NewManager(file, Original);
            AsAgent("agentA");
            manager.RecordObserved(new FilePathWrapper(file), Original);
            File.WriteAllText(file, External);
            var tools = BuildWholeFileWriteTools(manager);

            var diff = "--- a/A.cs\n+++ b/A.cs\n@@ -2,4 +2,4 @@\n public class A\n {\n-    public int X() => 1;\n+    public int X() => 2;\n }\n";
            var result = await tools.ApplyDiffTool(
                reason: "test message", changesetFormat: ChangesetFormat.diff, action: ProposedChangeAction.apply,
                filePath: file, unifiedDiff: diff, validateOnApply: false);

            Assert.That(result.ErrorData == null || result.ErrorData.ErrorCode != ToolErrorCode.StaleRead, Is.True,
                "ApplyDiff diff format is out of scope for the stale-read guard in v1");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task ApplyUnifiedDiff_StaleEntry_IsNotRefusedAsStaleReadAsync()
    {
        var dir = NewTempDir();
        try
        {
            var file = Path.Combine(dir, "A.cs");
            File.WriteAllText(file, Original);
            using var manager = NewManager(file, Original);
            AsAgent("agentA");
            manager.RecordObserved(new FilePathWrapper(file), Original);
            File.WriteAllText(file, External);
            var tools = BuildWholeFileWriteTools(manager);

            var diff = "--- a/A.cs\n+++ b/A.cs\n@@ -2,4 +2,4 @@\n public class A\n {\n-    public int X() => 1;\n+    public int X() => 2;\n }\n";
            var result = await tools.ApplyUnifiedDiffTool(
                reason: "test message", action: ProposedChangeAction.apply, filePath: file,
                unifiedDiff: diff, validateOnApply: false);

            Assert.That(result.ErrorData == null || result.ErrorData.ErrorCode != ToolErrorCode.StaleRead, Is.True,
                "ApplyUnifiedDiff is out of scope for the stale-read guard in v1");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task WriteFileReplaceFile_UnobservedExistingTarget_SucceedsWithUnobservedFinding()
    {
        var dir = NewTempDir();
        try
        {
            var file = Path.Combine(dir, "A.cs");
            File.WriteAllText(file, Original);
            using var manager = NewManager(file, Original);
            AsAgent("agentA");
            var tools = BuildWholeFileWriteTools(manager);

            var result = await tools.WriteFileTool(
                reason: "test message", operation: WriteFileOperation.ReplaceFile, filePath: file,
                content: Edited, validateOnApply: false);

            Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
            Assert.That(await File.ReadAllTextAsync(file), Is.EqualTo(Edited));
            var finding = result.Findings.Single(f => f.Message.Contains("read them first"));
            Assert.That(finding.Source, Is.EqualTo("WriteChokepoint"));
            Assert.That(finding.Message, Does.Contain(Path.GetFileName(file)));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task WriteFileReplaceFile_ObservedUnchangedTarget_HasNoUnobservedFinding()
    {
        var dir = NewTempDir();
        try
        {
            var file = Path.Combine(dir, "A.cs");
            File.WriteAllText(file, Original);
            using var manager = NewManager(file, Original);
            AsAgent("agentA");
            manager.RecordObserved(new FilePathWrapper(file), Original);
            var tools = BuildWholeFileWriteTools(manager);

            var result = await tools.WriteFileTool(
                reason: "test message", operation: WriteFileOperation.ReplaceFile, filePath: file,
                content: Edited, validateOnApply: false);

            Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
            Assert.That(await File.ReadAllTextAsync(file), Is.EqualTo(Edited));
            Assert.That((result.Findings ?? []).Any(f => f.Message.Contains("read them first")), Is.False,
                "An observed, unchanged target must not be reported as unobserved.");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task DeleteFile_UnobservedExistingTarget_SucceedsWithUnobservedFinding()
    {
        var dir = NewTempDir();
        try
        {
            var file = Path.Combine(dir, "A.cs");
            File.WriteAllText(file, Original);
            using var manager = NewManager(file, Original);
            AsAgent("agentA");
            var tools = BuildWholeFileWriteTools(manager);

            var result = await tools.DeleteFileTool(reason: "test message", filePath: file);

            Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
            Assert.That(File.Exists(file), Is.False);
            var finding = result.Findings.Single(f => f.Message.Contains("read them first"));
            Assert.That(finding.Source, Is.EqualTo("WriteChokepoint"));
            Assert.That(finding.Message, Does.Contain(Path.GetFileName(file)));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task ApplyDiffFilesFormat_UnobservedExistingTarget_SucceedsWithUnobservedFinding()
    {
        var dir = NewTempDir();
        try
        {
            var file = Path.Combine(dir, "A.cs");
            File.WriteAllText(file, Original);
            using var manager = NewManager(file, Original);
            AsAgent("agentA");
            var tools = BuildWholeFileWriteTools(manager);

            // One line changed, so the size guard (50% line and active-code shrink) does not fire first.
            var result = await tools.ApplyDiffTool(
                reason: "test message", changesetFormat: ChangesetFormat.files, action: ProposedChangeAction.apply,
                changes: new Dictionary<string, string> { [file] = Edited }, validateOnApply: false);

            Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
            Assert.That(await File.ReadAllTextAsync(file), Is.EqualTo(Edited));
            var finding = result.Findings.Single(f => f.Message.Contains("read them first"));
            Assert.That(finding.Source, Is.EqualTo("WriteChokepoint"));
            Assert.That(finding.Message, Does.Contain(Path.GetFileName(file)));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
