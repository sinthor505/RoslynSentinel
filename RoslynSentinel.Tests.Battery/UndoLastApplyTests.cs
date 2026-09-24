// UndoLastApply -> WorkspaceTools. Zero coverage before this file (GetTestCoverageMap
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

using RoslynSentinel.Tests.Fakes;

#pragma warning disable CS8618
namespace RoslynSentinel.Tests.Battery;

[TestFixture]
public class UndoLastApplyTests
{
    private static readonly JsonSerializerOptions PrettyJson = new() { WriteIndented = true };

    private FakeWorkspaceManager _fakeWorkspaceManager;
    private WorkspaceTools _fakeWorkspaceTools;
    private string _tempDir;

    [SetUp]
    public void Setup()
    {
        _fakeWorkspaceManager = new FakeWorkspaceManager();
        _tempDir = Path.Combine(Path.GetTempPath(), "UndoLastApplyTests_" + Guid.NewGuid().ToString("N"));
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

    private static WorkspaceTools BuildTools(IWorkspaceManager workspaceManager)
    {
        var config = new SentinelConfiguration();
        var diffEngine = new DiffEngine();
        var validationEngine = new ValidationEngine(workspaceManager, diffEngine, NullLogger<ValidationEngine>.Instance);
        var diagnosticEngine = new DiagnosticEngine(workspaceManager);
        var solutionManagementEngine = new SolutionManagementEngine(workspaceManager);
        var structuralRefinementEngine = new StructuralRefinementEngine(workspaceManager, config);
        var dependencyEngine = new DependencyEngine(workspaceManager);
        var projectConsistencyEngine = new ProjectConsistencyEngine(workspaceManager);
        return new WorkspaceTools(
            workspaceManager, validationEngine, diffEngine, diagnosticEngine,
            solutionManagementEngine, structuralRefinementEngine, dependencyEngine,
            projectConsistencyEngine, config, NullLogger<WorkspaceTools>.Instance,
            new BuildEngine(workspaceManager, diagnosticEngine),
            new SymbolNavigationEngine(workspaceManager, NullLogger<SymbolNavigationEngine>.Instance),
            new TestRunEngine(workspaceManager),
            new WorkspaceReadNavigationImpl(workspaceManager, NullLogger<WorkspaceReadNavigationImpl>.Instance),
            WriteToolAdviceHelper.WithAllToolsExposed());
    }

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
    public async Task UndoLastApply_NoBlobForChangeId_ReturnsNoOperationBlobFoundAsync()
    {
        var result = await _fakeWorkspaceTools.UndoLastApply(reason: "test message", "nonexistent-change-id");

        Assert.That(result.IsSuccess, Is.False);
        Assert.That(result.ErrorData!.ErrorCode, Is.EqualTo("NoOperationBlobFound"));
    }

    [Test]
    public async Task UndoLastApply_BlobHasNoSucceededItems_ReturnsNoReversibleItemsAsync()
    {
        var changeId = "change-failed-only";
        WriteBlob(changeId, new[]
        {
            new { FilePath = Path.Combine(_tempDir, "Foo.cs"), Outcome = ItemRecordOutcome.Failed, BeforeSource = (string?)null },
        });

        var result = await _fakeWorkspaceTools.UndoLastApply(reason: "test message", changeId);

        Assert.That(result.IsSuccess, Is.False);
        Assert.That(result.ErrorData!.ErrorCode, Is.EqualTo("NoReversibleItems"));
    }

    [Test]
    public async Task UndoLastApply_BlobHasSucceededItemWithNullBeforeSource_ReturnsNoReversibleItemsAsync()
    {
        var changeId = "change-null-before";
        WriteBlob(changeId, new[]
        {
            new { FilePath = Path.Combine(_tempDir, "Foo.cs"), Outcome = ItemRecordOutcome.Succeeded, BeforeSource = (string?)null },
        });

        var result = await _fakeWorkspaceTools.UndoLastApply(reason: "test message", changeId);

        Assert.That(result.IsSuccess, Is.False);
        Assert.That(result.ErrorData!.ErrorCode, Is.EqualTo("NoReversibleItems"));
    }

    [Test]
    public async Task UndoLastApply_AllRevertibleItemsOutsideSolutionRoot_SkipsAllAndReturnsSuccessWithFailuresAsync()
    {
        var changeId = "change-outside-root";
        var outsidePath = Path.Combine(Path.GetTempPath(), "SomewhereElse_" + Guid.NewGuid().ToString("N"), "Foo.cs");
        WriteBlob(changeId, new[]
        {
            new { FilePath = outsidePath, Outcome = ItemRecordOutcome.Succeeded, BeforeSource = "old content" },
        });

        var result = await _fakeWorkspaceTools.UndoLastApply(reason: "test message", changeId: changeId);

        // revertChanges ends up empty (item skipped as outside solution root), so
        // ApplyProposedChangesAsync is never called -> reaches the tool's success path with 0
        // reverted files and a recorded failure, entirely on the fake.
        Assert.That(result.IsSuccess, Is.True);
        Assert.That((string)result.SuccessData!, Does.Contain("Reverted 0 files"));
        Assert.That((string)result.SuccessData!, Does.Contain("outside solution root, skipped"));
    }

    [Test]
    public async Task UndoLastApply_RealRevert_RestoresFileToPreApplyContentAsync()
    {
        using var fixture = new TestSolutionFixture();
        using var workspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        await workspaceManager.LoadSolutionAsync(fixture.SolutionPath);
        var workspaceTools = BuildTools(workspaceManager);

        var targetFile = Directory.EnumerateFiles(fixture.SolutionDirectory, "*.cs", SearchOption.AllDirectories).First();
        var originalContent = await File.ReadAllTextAsync(targetFile);
        var modifiedContent = originalContent + "\n// modified for UndoLastApply test\n";
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

        var result = await workspaceTools.UndoLastApply(reason: "test message", changeId: changeId);

        Assert.That(result.IsSuccess, Is.True);
        Assert.That((string)result.SuccessData!, Does.Contain("Reverted 1 files"));
        Assert.That(await File.ReadAllTextAsync(targetFile), Is.EqualTo(originalContent));
    }
    // Added by InsertMemberAfter (expected - used for diagnostics)
    [Test]
    public async Task UndoLastApply_NoOpRevert_ReportsDistinctlyFromRealRevertAsync()
    {
        // BeforeSource in the blob is identical to the file's current on-disk content, so
        // ApplyProposedChangesAsync's no-op skip path (PersistentWorkspaceManager.cs, "Skipping
        // no-op write") fires: the file lands in SucceededFiles but nothing is actually written.
        // Before this fix, UndoLastApply reported this the same as a real revert ("Reverted 1
        // files"), which is exactly the confusion observed this session -> a file believed
        // reverted was provably unchanged.
        using var fixture = new TestSolutionFixture();
        using var workspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
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

        var result = await workspaceTools.UndoLastApply(reason: "test message", changeId: changeId);

        Assert.That(result.IsSuccess, Is.True);
        Assert.That((string)result.SuccessData!, Does.Contain("Reverted 0 files"));
        Assert.That((string)result.SuccessData!, Does.Contain("already matched pre-apply state"));
        Assert.That((string)result.SuccessData!, Does.Contain(targetFile));
        Assert.That(await File.ReadAllTextAsync(targetFile), Is.EqualTo(currentContent));
    }


    // Added by AddMember (expected - used for diagnostics)
    // Regression test for blocking_error_synctypeandfilename_wrong_type_undolastapply_no_reversible_items.md
    // Symptom 2: SyncTypeAndFilename used to delete the old file via a bare FileIoHelper.DeleteAsync
    // call outside ApplyProposedChangesAsync's tracked delete path, so the old path's content was
    // never captured as a pre-image anywhere and UndoLastApply could only ever report
    // NoReversibleItems for a rename changeId. Now that the delete goes through deletePaths, its
    // pre-image is captured like any other tracked delete, so a rename changeId should be fully
    // revertible: old file restored.
    [Test]
    public async Task UndoLastApply_RealRevert_RestoresRenamedFileFromSyncTypeAndFilenameAsync()
    {
        using var fixture = new TestSolutionFixture();
        using var workspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        await workspaceManager.LoadSolutionAsync(fixture.SolutionPath);
        var workspaceTools = BuildTools(workspaceManager);

        const string widgetSource = "namespace ContosoOrders;\n\npublic class Widget\n{\n    public int Id { get; set; }\n}\n";
        await fixture.AddFileToSolution(workspaceManager, Path.Combine("ContosoOrders.Core", "Mismatched.cs"), widgetSource);

        var config = new SentinelConfiguration();
        var structuralRefinementEngine = new StructuralRefinementEngine(workspaceManager, config);
        var validationEngine = new ValidationEngine(workspaceManager, new DiffEngine(), NullLogger<ValidationEngine>.Instance);
        var refactoringEngine = new RefactoringEngine(workspaceManager, NullLogger<RefactoringEngine>.Instance, config);
        var symbolNavigationEngine = new SymbolNavigationEngine(workspaceManager, NullLogger<SymbolNavigationEngine>.Instance);
        var structuralTools = new RefactoringStructuralTools(refactoringEngine, structuralRefinementEngine, symbolNavigationEngine, workspaceManager, validationEngine, NullLogger<RefactoringStructuralTools>.Instance);

        var oldPath = Path.Combine(fixture.SolutionDirectory, "ContosoOrders.Core", "Mismatched.cs");
        var newPath = Path.Combine(fixture.SolutionDirectory, "ContosoOrders.Core", "Widget.cs");

        var renameResult = await structuralTools.SyncTypeAndFilename(reason: "test message", oldPath);
        Assert.That(renameResult.IsSuccess, Is.True, $"Expected rename to succeed; error: {renameResult.ErrorData?.Message}");
        var changeId = ((AppliedChangeSummary)renameResult.SuccessData!).ChangeId;

        Assert.That(File.Exists(oldPath), Is.False, "Old file should be gone after the rename.");
        Assert.That(File.Exists(newPath), Is.True, "New file should exist after the rename.");

        var undoResult = await workspaceTools.UndoLastApply(reason: "test message", changeId: changeId!);

        Assert.That(undoResult.IsSuccess, Is.True, $"Expected undo to succeed; error: {undoResult.ErrorData?.Message}");
        Assert.That(File.Exists(oldPath), Is.True, "Old file should be restored by UndoLastApply.");
        Assert.That(await File.ReadAllTextAsync(oldPath), Is.EqualTo(widgetSource));
    }
}
