// UndoChangeTool -> WorkspaceTools. Zero coverage before this file (GetTestCoverageMap
// flagged it as the highest-priority gap: it's the recently-fixed atomicity/rollback path per
// project_workspace_manager_deferred_gaps.md, so regressions here are the costliest to miss).
//
// Blob-lookup/parsing branches (blobPath == null, revertable.Count == 0, path-traversal skip) are
// exercised against FakeWorkspaceManager with a hand-written operation blob on disk -> the blob
// schema (OperationBlobWriter.WriteAsync: {toolName, changeId, generatedUtc, itemCount, items} at
// .roslynsentinel/operations/{toolName}_{timestamp}_{changeId}.json) doesn't require a real apply
// to produce, just matching JSON. ItemRecordOutcome now carries JsonStringEnumConverter like its
// siblings ItemOutcome/OperationOutcome, so Outcome round-trips as its string name.
// The real-revert path requires ApplyProposedChangesAsync, which FakeWorkspaceManager deliberately
// leaves unimplemented, so that test uses PersistentWorkspaceManager + TestSolutionFixture instead.

using System.Text.Json;

using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Engines.Basic;
using RoslynSentinel.Tests.Fakes;
using RoslynSentinel.Tools.Basic;

#pragma warning disable CS8618
namespace RoslynSentinel.Tests.Battery.Basic;

[TestFixture]
[Category("WorkspaceTools")] // sentinel:auto-category
public class UndoChangeTests
{
    private static readonly JsonSerializerOptions PrettyJson = new() { WriteIndented = true };

    private FakeWorkspaceManager _fakeWorkspaceManager;
    private WorkspaceTools _fakeWorkspaceTools;
    private string _tempDir;

    [SetUp]
    public void Setup()
    {
        _fakeWorkspaceManager = FakeWorkspaceManager.ReadOnly();
        _tempDir = Path.Combine(Path.GetTempPath(), "UndoChangeTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _fakeWorkspaceManager.SolutionPath = Path.Combine(_tempDir, "Test.sln");

        _fakeWorkspaceTools = BuildTools(_fakeWorkspaceManager);
    }

    [TearDown]
    public void TearDown()
    {
        _fakeWorkspaceManager?.Dispose();
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    private static WorkspaceTools BuildTools(IWorkspaceManager workspaceManager) =>
        TestTools.WorkspaceTools(workspaceManager);

    private string WriteBlob(string changeId, object items)
    {
        var dir = Path.Combine(_tempDir, ".roslynsentinel", "operations");
        Directory.CreateDirectory(dir);
        var fileName = $"apply_diff_20260101T000000Z_{changeId}.json";
        var payload = new
        {
            toolName = "apply_diff",
            changeId,
            generatedUtc = DateTime.UtcNow.ToString("O"),
            itemCount = ((System.Collections.ICollection)items).Count,
            items,
        };
        File.WriteAllText(Path.Combine(dir, fileName), JsonSerializer.Serialize(payload, PrettyJson));
        return fileName;
    }

    [Test]
    public async Task UndoChange_NoBlobForChangeId_ReturnsNoOperationBlobFoundAsync()
    {
        var result = await _fakeWorkspaceTools.UndoChangeTool(reason: "test message", "nonexistent-change-id");

        Assert.That(!result.IsError, Is.False);
        Assert.That(result.ErrorData!.ErrorCode, Is.EqualTo("NoOperationBlobFound"));
    }

    [Test]
    public async Task UndoChange_BlobHasNoSucceededItems_ReturnsNoReversibleItemsAsync()
    {
        var changeId = "change-failed-only";
        WriteBlob(changeId, new[]
        {
            new { FilePath = Path.Combine(_tempDir, "Foo.cs"), Outcome = ItemRecordOutcome.Failed, BeforeSource = (string?)null },
        });

        var result = await _fakeWorkspaceTools.UndoChangeTool(reason: "test message", changeId);

        Assert.That(!result.IsError, Is.False);
        Assert.That(result.ErrorData!.ErrorCode, Is.EqualTo("NoReversibleItems"));
    }

    [Test]
    public async Task UndoChange_BlobHasSucceededItemWithNullBeforeSource_ReturnsNoReversibleItemsAsync()
    {
        var changeId = "change-null-before";
        WriteBlob(changeId, new[]
        {
            new { FilePath = Path.Combine(_tempDir, "Foo.cs"), Outcome = ItemRecordOutcome.Succeeded, BeforeSource = (string?)null },
        });

        var result = await _fakeWorkspaceTools.UndoChangeTool(reason: "test message", changeId);

        Assert.That(!result.IsError, Is.False);
        Assert.That(result.ErrorData!.ErrorCode, Is.EqualTo("NoReversibleItems"));
    }

    [Test]
    public async Task UndoChange_AllRevertibleItemsOutsideSolutionRoot_SkipsAllAndReturnsSuccessWithFailuresAsync()
    {
        var changeId = "change-outside-root";
        var outsidePath = Path.Combine(Path.GetTempPath(), "SomewhereElse_" + Guid.NewGuid().ToString("N"), "Foo.cs");
        WriteBlob(changeId, new[]
        {
            new { FilePath = outsidePath, Outcome = ItemRecordOutcome.Succeeded, BeforeSource = "old content" },
        });

        var result = await _fakeWorkspaceTools.UndoChangeTool(reason: "test message", changeId: changeId);

        // revertChanges ends up empty (item skipped as outside solution root), so
        // ApplyProposedChangesAsync is never called -> reaches the tool's success path with 0
        // reverted files and a recorded failure, entirely on the fake.
        Assert.That(!result.IsError, Is.True);
        Assert.That((string)result.SuccessData!, Does.Contain("Reverted 0 files"));
        Assert.That((string)result.SuccessData!, Does.Contain("outside solution root, skipped"));
    }

    [Test]
    public async Task UndoChange_RealRevert_RestoresFileToPreApplyContentAsync()
    {
        using var fixture = new TestSolutionFixture();
        using var workspaceManager = TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        await workspaceManager.LoadSolutionAsync(fixture.SolutionPath);
        var workspaceTools = BuildTools(workspaceManager);

        var targetFile = Directory.EnumerateFiles(fixture.SolutionDirectory, "*.cs", SearchOption.AllDirectories).First();
        var originalContent = await File.ReadAllTextAsync(targetFile);
        var modifiedContent = originalContent + "\n// modified for UndoChange test\n";
        var targetRelativePath = Path.GetRelativePath(fixture.SolutionDirectory, targetFile);
        await fixture.ModifyFileInSolution(workspaceManager, targetRelativePath, modifiedContent);

        var changeId = "change-real-revert";
        var dir = Path.Combine(fixture.SolutionDirectory, ".roslynsentinel", "operations");
        Directory.CreateDirectory(dir);
        var payload = new
        {
            toolName = "apply_diff",
            changeId,
            generatedUtc = DateTime.UtcNow.ToString("O"),
            itemCount = 1,
            items = new[]
            {
                new { FilePath = targetFile, Outcome = ItemRecordOutcome.Succeeded, BeforeSource = originalContent },
            },
        };
        await File.WriteAllTextAsync(
            Path.Combine(dir, $"apply_diff_20260101T000000Z_{changeId}.json"),
            JsonSerializer.Serialize(payload, PrettyJson));

        // ModifyFileInSolution already cleared drift from the target-file write above. The
        // operation-blob write just above is a second, separate out-of-band write (outside
        // ApplyProposedChangesAsync) that the FileSystemWatcher also records as external drift, so it
        // needs its own acknowledgment here -> otherwise the revert write below is refused by the drift
        // guard in ApplyProposedChangesAsync (RoslynSentinel.Common/PersistentWorkspaceManager.cs)
        // before it ever reaches undo logic.
        workspaceManager.ClearExternalFileChanges();

        var result = await workspaceTools.UndoChangeTool(reason: "test message", changeId: changeId);

        Assert.That(!result.IsError, Is.True);
        Assert.That((string)result.SuccessData!, Does.Contain("Reverted 1 files"));
        Assert.That(await File.ReadAllTextAsync(targetFile), Is.EqualTo(originalContent));
    }
    [Test]
    public async Task UndoChange_NoOpRevert_ReportsDistinctlyFromRealRevertAsync()
    {
        // BeforeSource in the blob is identical to the file's current on-disk content, so
        // ApplyProposedChangesAsync's no-op skip path (PersistentWorkspaceManager.cs, "Skipping
        // no-op write") fires: the file lands in SucceededFiles but nothing is actually written.
        // Before this fix, UndoChangeTool reported this the same as a real revert ("Reverted 1
        // files"), which is exactly the confusion observed this session -> a file believed
        // reverted was provably unchanged.
        using var fixture = new TestSolutionFixture();
        using var workspaceManager = TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        await workspaceManager.LoadSolutionAsync(fixture.SolutionPath);
        var workspaceTools = BuildTools(workspaceManager);

        var targetFile = Directory.EnumerateFiles(fixture.SolutionDirectory, "*.cs", SearchOption.AllDirectories).First();
        var currentContent = await File.ReadAllTextAsync(targetFile);

        var changeId = "change-noop-revert";
        var dir = Path.Combine(fixture.SolutionDirectory, ".roslynsentinel", "operations");
        Directory.CreateDirectory(dir);
        var payload = new
        {
            toolName = "apply_diff",
            changeId,
            generatedUtc = DateTime.UtcNow.ToString("O"),
            itemCount = 1,
            items = new[]
            {
                // BeforeSource matches the file's CURRENT content exactly, so reverting to it is
                // a no-op write, not a real one.
                new { FilePath = targetFile, Outcome = ItemRecordOutcome.Succeeded, BeforeSource = currentContent },
            },
        };
        await File.WriteAllTextAsync(
            Path.Combine(dir, $"apply_diff_20260101T000000Z_{changeId}.json"),
            JsonSerializer.Serialize(payload, PrettyJson));

        workspaceManager.ClearExternalFileChanges();

        var result = await workspaceTools.UndoChangeTool(reason: "test message", changeId: changeId);

        Assert.That(!result.IsError, Is.True);
        Assert.That((string)result.SuccessData!, Does.Contain("Reverted 0 files"));
        Assert.That((string)result.SuccessData!, Does.Contain("already matched pre-apply state"));
        Assert.That((string)result.SuccessData!, Does.Contain(targetFile));
        Assert.That(await File.ReadAllTextAsync(targetFile), Is.EqualTo(currentContent));
    }

    // Regression test for blocking_error_synctypeandfilename_wrong_type_undolastapply_no_reversible_items.md
    // Symptom 2: SyncTypeAndFilenameTool used to delete the old file via a bare FileIoHelper.DeleteAsync
    // call outside ApplyProposedChangesAsync's tracked delete path, so the old path's content was
    // never captured as a pre-image anywhere and UndoChangeTool could only ever report
    // NoReversibleItems for a rename changeId. Now that the delete goes through deletePaths, its
    // pre-image is captured like any other tracked delete, so a rename changeId should be fully
    // revertible: old file restored.
    [Test]
    [Category("BasicRefactoringEngine")] // sentinel:auto-category
    [Category("MemberRefactoringEngine")] // sentinel:auto-category
    [Category("RefactoringStructuralImpl")] // sentinel:auto-category
    [Category("RefactoringStructuralTools")] // sentinel:auto-category
    [Category("StructuralRefinementEngine")] // sentinel:auto-category
    [Category("SymbolNavigationEngine")] // sentinel:auto-category
    public async Task UndoChange_RealRevert_RestoresRenamedFileFromSyncTypeAndFilenameAsync()
    {
        using var fixture = new TestSolutionFixture();
        using var workspaceManager = TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        await workspaceManager.LoadSolutionAsync(fixture.SolutionPath);
        var workspaceTools = BuildTools(workspaceManager);

        const string widgetSource = "namespace ContosoOrders;\n\npublic class Widget\n{\n    public int Id { get; set; }\n}\n";
        await fixture.AddFileToSolution(workspaceManager, Path.Combine("ContosoOrders.Core", "Mismatched.cs"), widgetSource);

        var config = TestEngines.SentinelConfiguration();
        var structuralRefinementEngine = TestEngines.StructuralRefinementEngine(workspaceManager, config);
        var validationEngine = TestEngines.ValidationEngine(workspaceManager, TestEngines.DiffEngine(), NullLogger<ValidationEngine>.Instance);
        var refactoringEngine = TestEngines.BasicRefactoringEngine(workspaceManager, NullLogger<BasicRefactoringEngine>.Instance, config);
        var symbolNavigationEngine = TestEngines.SymbolNavigationEngine(workspaceManager, NullLogger<SymbolNavigationEngine>.Instance);
        var memberRefactoringEngine = TestEngines.MemberRefactoringEngine(workspaceManager, symbolNavigationEngine, validationEngine, config);
        var structuralTools = TestTools.RefactoringStructuralTools(TestTools.RefactoringStructuralImpl(refactoringEngine, memberRefactoringEngine, structuralRefinementEngine, symbolNavigationEngine, workspaceManager, validationEngine, NullLogger<RefactoringStructuralImpl>.Instance));

        var oldPath = Path.Combine(fixture.SolutionDirectory, "ContosoOrders.Core", "Mismatched.cs");
        var newPath = Path.Combine(fixture.SolutionDirectory, "ContosoOrders.Core", "Widget.cs");

        var renameResult = await structuralTools.SyncTypeAndFilenameTool(reason: "test message", oldPath);
        Assert.That(!renameResult.IsError, Is.True, $"Expected rename to succeed; error: {renameResult.ErrorData?.Message}");
        var changeId = ((AppliedChangeSummary)renameResult.SuccessData!).ChangeId;

        Assert.That(File.Exists(oldPath), Is.False, "Old file should be gone after the rename.");
        Assert.That(File.Exists(newPath), Is.True, "New file should exist after the rename.");

        var undoResult = await workspaceTools.UndoChangeTool(reason: "test message", changeId: changeId!);

        Assert.That(!undoResult.IsError, Is.True, $"Expected undo to succeed; error: {undoResult.ErrorData?.Message}");
        Assert.That(File.Exists(oldPath), Is.True, "Old file should be restored by UndoChange.");
        Assert.That(await File.ReadAllTextAsync(oldPath), Is.EqualTo(widgetSource));
    }

    // Regression tests for blocking_error_undolastapply_refused_eol_after_apply_normalized_mixed_file.md.
    // LF-dominant file with two stray CRLF lines (one before, one after the insertion point). Built with
    // explicit escapes so no tool or editor can normalize the fixture.
    private const string MixedEolSource = "namespace Fx;\n\npublic class Sample\r\n{\n    public int A() => 1;\n    public int B() => 2;\r\n}\n";

    private const string MixedEolWithInsertedLine = "namespace Fx;\n\npublic class Sample\r\n{\n    // added\n    public int A() => 1;\n    public int B() => 2;\r\n}\n";

    private static async Task WriteUndoBlobAsync(TestSolutionFixture fixture, string changeId, string filePath, string beforeSource)
    {
        var dir = Path.Combine(fixture.SolutionDirectory, ".roslynsentinel", "operations");
        Directory.CreateDirectory(dir);
        var payload = new
        {
            toolName = "apply_diff",
            changeId,
            generatedUtc = DateTime.UtcNow.ToString("O"),
            itemCount = 1,
            items = new[] { new { FilePath = filePath, Outcome = ItemRecordOutcome.Succeeded, BeforeSource = beforeSource } },
        };
        await File.WriteAllTextAsync(
            Path.Combine(dir, $"apply_diff_20260101T000000Z_{changeId}.json"),
            JsonSerializer.Serialize(payload, PrettyJson));
    }

    [Test]
    [Description("A file already normalized to pure LF by an earlier write can still be restored to its mixed-EOL pre-image: undo is an explicit exact restore, not a normal edit")]
    public async Task UndoChange_PreImageIsMixedButFileIsNowPureLf_RestoresExactBytesAsync()
    {
        using var fixture = new TestSolutionFixture();
        var targetFile = Path.Combine(fixture.SolutionDirectory, "ContosoOrders.Core", "DamagedEol.cs");
        // The state the blocker left on disk: pure LF (stray CRLFs already rewritten) plus the inserted line.
        await File.WriteAllTextAsync(targetFile, MixedEolWithInsertedLine.Replace("\r\n", "\n"));

        using var workspaceManager = TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        await workspaceManager.LoadSolutionAsync(fixture.SolutionPath);
        var workspaceTools = BuildTools(workspaceManager);

        var changeId = "change-mixed-eol-damaged";
        await WriteUndoBlobAsync(fixture, changeId, targetFile, MixedEolSource);
        workspaceManager.ClearExternalFileChanges();

        var result = await workspaceTools.UndoChangeTool(reason: "test message", changeId: changeId);

        Assert.That(!result.IsError, Is.True, result.ErrorData?.Message);
        Assert.That((string)result.SuccessData!, Does.Contain("Reverted 1 files").And.Not.Contain("Failures"));
        Assert.That(
            await File.ReadAllBytesAsync(targetFile),
            Is.EqualTo(System.Text.Encoding.UTF8.GetBytes(MixedEolSource)),
            "undo must restore the exact pre-image bytes, including the stray CRLF lines");
    }

    [Test]
    [Description("Apply on a mixed-EOL file converges the whole file to CRLF, and UndoChange then restores the exact pre-image bytes")]
    public async Task UndoChange_AfterApplyOnMixedEolFile_RestoresByteIdenticalPreImageAsync()
    {
        using var fixture = new TestSolutionFixture();
        var targetFile = Path.Combine(fixture.SolutionDirectory, "ContosoOrders.Core", "MixedEol.cs");
        var originalBytes = System.Text.Encoding.UTF8.GetBytes(MixedEolSource);
        await File.WriteAllBytesAsync(targetFile, originalBytes);

        // EOL tracking is captured at load, so the file must exist on disk before LoadSolutionAsync.
        using var workspaceManager = TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        workspaceManager.EolModeOverride = EolMode.Crlf;
        await workspaceManager.LoadSolutionAsync(fixture.SolutionPath);
        var workspaceTools = BuildTools(workspaceManager);

        var apply = await workspaceManager.ApplyProposedChangesAsync(
            new Dictionary<FilePathWrapper, string> { [new FilePathWrapper(targetFile)] = MixedEolWithInsertedLine });
        Assert.That(!apply.IsError, Is.True, apply.Summary);
        var uniformCrlf = MixedEolWithInsertedLine.Replace("\r\n", "\n").Replace("\n", "\r\n");
        Assert.That(
            await File.ReadAllBytesAsync(targetFile),
            Is.EqualTo(System.Text.Encoding.UTF8.GetBytes(uniformCrlf)),
            "the apply must converge the whole file to uniform CRLF, stray lines included");

        var changeId = "change-mixed-eol-roundtrip";
        await WriteUndoBlobAsync(fixture, changeId, targetFile, apply.PreImages![targetFile]!);
        workspaceManager.ClearExternalFileChanges();

        var result = await workspaceTools.UndoChangeTool(reason: "test message", changeId: changeId);

        Assert.That(!result.IsError, Is.True, result.ErrorData?.Message);
        Assert.That((string)result.SuccessData!, Does.Contain("Reverted 1 files").And.Not.Contain("Failures"));
        Assert.That(await File.ReadAllBytesAsync(targetFile), Is.EqualTo(originalBytes), "undo must restore the exact pre-image bytes");
    }

    // LOAD-SENSITIVE: failed once in a parallel worktree run ("BuildHost process is not responding" on the ContosoOrders.sln reload), passed alone; disregard a failure here when the change did not touch workspace loading, and re-run the test alone first.
    [Test]
    [Description("Apply on a BOM-prefixed mixed-EOL file converges to CRLF and keeps the BOM, and UndoChange then restores the exact BOM-prefixed pre-image bytes")]
    public async Task UndoChange_AfterApplyOnBomMixedEolFile_RestoresBomBytesAsync()
    {
        using var fixture = new TestSolutionFixture();
        var targetFile = Path.Combine(fixture.SolutionDirectory, "ContosoOrders.Core", "BomMixedEol.cs");
        var originalBytes = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(System.Text.Encoding.UTF8.GetBytes(MixedEolSource)).ToArray();
        await File.WriteAllBytesAsync(targetFile, originalBytes);

        // EOL and BOM tracking is captured at load, so the file must exist on disk before LoadSolutionAsync.
        using var workspaceManager = TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        workspaceManager.EolModeOverride = EolMode.Crlf;
        await workspaceManager.LoadSolutionAsync(fixture.SolutionPath);
        var workspaceTools = BuildTools(workspaceManager);

        var apply = await workspaceManager.ApplyProposedChangesAsync(
            new Dictionary<FilePathWrapper, string> { [new FilePathWrapper(targetFile)] = MixedEolWithInsertedLine });
        Assert.That(!apply.IsError, Is.True, apply.Summary);

        var changeId = "change-bom-mixed-eol-roundtrip";
        await WriteUndoBlobAsync(fixture, changeId, targetFile, apply.PreImages![targetFile]!);
        workspaceManager.ClearExternalFileChanges();

        var result = await workspaceTools.UndoChangeTool(reason: "test message", changeId: changeId);

        Assert.That(!result.IsError, Is.True, result.ErrorData?.Message);
        Assert.That((string)result.SuccessData!, Does.Contain("Reverted 1 files").And.Not.Contain("Failures"));
        Assert.That(await File.ReadAllBytesAsync(targetFile), Is.EqualTo(originalBytes), "undo must restore the exact BOM-prefixed pre-image bytes");
    }

    private static async Task<string> WriteStampedUndoBlobAsync(TestSolutionFixture fixture, string changeId, DateTime stamp, string filePath, string beforeSource)
    {
        // Blob timestamps come from ToolCallContext.LastWriteUtc, so the stamp orders the blobs for the last-only rule.
        var context = new ToolCallContext("test-agent", "implementer", "ApplyDiff", "stamped undo blob", 0) { SessionId = "s1" };
        context.MarkWritten(stamp);
        ToolCallContext.Set(context);
        try
        {
            var blob = await OperationBlobWriter.WriteAsync(
                "apply_diff",
                changeId,
                [new OperationItemRecord { FilePath = filePath, Outcome = ItemRecordOutcome.Succeeded, BeforeSource = beforeSource }],
                fixture.SolutionDirectory);
            return Path.Combine(fixture.SolutionDirectory, ".roslynsentinel", "operations", blob.FileName!);
        }
        finally
        {
            ToolCallContext.Set(null);
        }
    }

    [Test]
    public async Task UndoChange_OlderChangeShadowedByNewerBlobOnSameFile_IsRefusedAndFileUnchangedAsync()
    {
        using var fixture = new TestSolutionFixture();
        using var workspaceManager = TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        await workspaceManager.LoadSolutionAsync(fixture.SolutionPath);
        var workspaceTools = BuildTools(workspaceManager);

        var targetFile = Directory.EnumerateFiles(fixture.SolutionDirectory, "*.cs", SearchOption.AllDirectories).First();
        var originalContent = await File.ReadAllTextAsync(targetFile);
        var firstModified = originalContent + "\n// first edit for UndoChange test\n";
        var secondModified = firstModified + "\n// second edit for UndoChange test\n";
        var targetRelativePath = Path.GetRelativePath(fixture.SolutionDirectory, targetFile);
        await fixture.ModifyFileInSolution(workspaceManager, targetRelativePath, secondModified);

        var stamp = DateTime.UtcNow;
        await WriteStampedUndoBlobAsync(fixture, "change-older", stamp, targetFile, originalContent);
        await WriteStampedUndoBlobAsync(fixture, "change-newer", stamp.AddMinutes(1), targetFile, firstModified);
        workspaceManager.ClearExternalFileChanges();

        var result = await workspaceTools.UndoChangeTool(reason: "test message", changeId: "change-older");

        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData!.ErrorCode, Is.EqualTo(ToolErrorCode.PreconditionNotMet));
        Assert.That(result.ErrorData!.Message, Does.Contain("change-newer"));
        Assert.That(await File.ReadAllTextAsync(targetFile), Is.EqualTo(secondModified));
    }

    [Test]
    public async Task UndoChange_NewestChange_StillRevertsToPreApplyTextAsync()
    {
        using var fixture = new TestSolutionFixture();
        using var workspaceManager = TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        await workspaceManager.LoadSolutionAsync(fixture.SolutionPath);
        var workspaceTools = BuildTools(workspaceManager);

        var files = Directory.EnumerateFiles(fixture.SolutionDirectory, "*.cs", SearchOption.AllDirectories).Take(2).ToArray();
        Assert.That(files.Length, Is.EqualTo(2));
        var targetFile = files[0];
        var otherFile = files[1];
        var originalContent = await File.ReadAllTextAsync(targetFile);
        var otherContent = await File.ReadAllTextAsync(otherFile);
        var modifiedContent = originalContent + "\n// modified for UndoChange test\n";
        var targetRelativePath = Path.GetRelativePath(fixture.SolutionDirectory, targetFile);
        await fixture.ModifyFileInSolution(workspaceManager, targetRelativePath, modifiedContent);

        // A newer blob on a different file must not shadow the change to targetFile.
        var stamp = DateTime.UtcNow;
        await WriteStampedUndoBlobAsync(fixture, "change-target", stamp, targetFile, originalContent);
        await WriteStampedUndoBlobAsync(fixture, "change-other", stamp.AddMinutes(1), otherFile, otherContent);
        workspaceManager.ClearExternalFileChanges();

        var result = await workspaceTools.UndoChangeTool(reason: "test message", changeId: "change-target");

        Assert.That(result.IsError, Is.False);
        Assert.That((string)result.SuccessData!, Does.Contain("Reverted 1 files"));
        Assert.That(await File.ReadAllTextAsync(targetFile), Is.EqualTo(originalContent));
    }

    [Test]
    public async Task UndoChange_NewerBlobMarkedUndone_DoesNotBlockOlderChangeAsync()
    {
        using var fixture = new TestSolutionFixture();
        using var workspaceManager = TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        await workspaceManager.LoadSolutionAsync(fixture.SolutionPath);
        var workspaceTools = BuildTools(workspaceManager);

        var targetFile = Directory.EnumerateFiles(fixture.SolutionDirectory, "*.cs", SearchOption.AllDirectories).First();
        var originalContent = await File.ReadAllTextAsync(targetFile);
        var firstModified = originalContent + "\n// first edit for UndoChange test\n";
        var secondModified = firstModified + "\n// second edit for UndoChange test\n";
        var targetRelativePath = Path.GetRelativePath(fixture.SolutionDirectory, targetFile);
        await fixture.ModifyFileInSolution(workspaceManager, targetRelativePath, secondModified);

        var stamp = DateTime.UtcNow;
        await WriteStampedUndoBlobAsync(fixture, "change-older", stamp, targetFile, originalContent);
        var newerBlobPath = await WriteStampedUndoBlobAsync(fixture, "change-newer", stamp.AddMinutes(1), targetFile, firstModified);
        OperationBlobWriter.MarkUndone(newerBlobPath);
        workspaceManager.ClearExternalFileChanges();

        var result = await workspaceTools.UndoChangeTool(reason: "test message", changeId: "change-older");

        Assert.That(result.IsError, Is.False);
        Assert.That(await File.ReadAllTextAsync(targetFile), Is.EqualTo(originalContent));
    }
}
