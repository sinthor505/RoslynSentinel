// UndoLastApply — SentinelWorkspaceTools. Zero coverage before this file (GetTestCoverageMap
// flagged it as the highest-priority gap: it's the recently-fixed atomicity/rollback path per
// project_workspace_manager_deferred_gaps.md, so regressions here are the costliest to miss).
//
// Blob-lookup/parsing branches (blobPath == null, revertable.Count == 0, path-traversal skip) are
// exercised against FakeWorkspaceManager with a hand-written operation blob on disk — the blob
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
    private SentinelWorkspaceTools _fakeTools;
    private string _tempDir;

    [SetUp]
    public void Setup()
    {
        _fakeWorkspaceManager = new FakeWorkspaceManager();
        _tempDir = Path.Combine(Path.GetTempPath(), "UndoLastApplyTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _fakeWorkspaceManager.SolutionPath = Path.Combine(_tempDir, "Test.sln");

        _fakeTools = BuildTools(_fakeWorkspaceManager);
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

    private static SentinelWorkspaceTools BuildTools(IWorkspaceManager workspaceManager)
    {
        var config = new SentinelConfiguration();
        var diffEngine = new DiffEngine();
        var validationEngine = new ValidationEngine(NullLogger<ValidationEngine>.Instance, workspaceManager, diffEngine);
        var diagnosticEngine = new DiagnosticEngine(workspaceManager);
        var solutionManagementEngine = new SolutionManagementEngine(workspaceManager);
        var structuralRefinementEngine = new StructuralRefinementEngine(workspaceManager, config);
        var dependencyEngine = new DependencyEngine(workspaceManager);
        var projectConsistencyEngine = new ProjectConsistencyEngine(workspaceManager);
        return new SentinelWorkspaceTools(
            workspaceManager, validationEngine, diffEngine, diagnosticEngine,
            solutionManagementEngine, structuralRefinementEngine, dependencyEngine,
            projectConsistencyEngine, config, NullLogger<SentinelWorkspaceTools>.Instance,
            new BuildEngine(workspaceManager, diagnosticEngine),
            new SymbolNavigationEngine(workspaceManager, NullLogger<SymbolNavigationEngine>.Instance),
            new TestRunEngine(workspaceManager));
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
        var result = await _fakeTools.UndoLastApply("nonexistent-change-id");

        Assert.That(result.Success, Is.False);
        Assert.That(result.Error!.ErrorCode, Is.EqualTo("NoOperationBlobFound"));
    }

    [Test]
    public async Task UndoLastApply_BlobHasNoSucceededItems_ReturnsNoReversibleItemsAsync()
    {
        var changeId = "change-failed-only";
        WriteBlob(changeId, new[]
        {
            new { FilePath = Path.Combine(_tempDir, "Foo.cs"), Outcome = ItemRecordOutcome.Failed, BeforeSource = (string?)null },
        });

        var result = await _fakeTools.UndoLastApply(changeId);

        Assert.That(result.Success, Is.False);
        Assert.That(result.Error!.ErrorCode, Is.EqualTo("NoReversibleItems"));
    }

    [Test]
    public async Task UndoLastApply_BlobHasSucceededItemWithNullBeforeSource_ReturnsNoReversibleItemsAsync()
    {
        var changeId = "change-null-before";
        WriteBlob(changeId, new[]
        {
            new { FilePath = Path.Combine(_tempDir, "Foo.cs"), Outcome = ItemRecordOutcome.Succeeded, BeforeSource = (string?)null },
        });

        var result = await _fakeTools.UndoLastApply(changeId);

        Assert.That(result.Success, Is.False);
        Assert.That(result.Error!.ErrorCode, Is.EqualTo("NoReversibleItems"));
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

        var result = await _fakeTools.UndoLastApply(changeId);

        // revertChanges ends up empty (item skipped as outside solution root), so
        // ApplyProposedChangesAsync is never called — reaches the tool's success path with 0
        // reverted files and a recorded failure, entirely on the fake.
        Assert.That(result.Success, Is.True);
        Assert.That((string)result.Data!, Does.Contain("Reverted 0 files"));
        Assert.That((string)result.Data!, Does.Contain("outside solution root, skipped"));
    }

    [Test]
    public async Task UndoLastApply_RealRevert_RestoresFileToPreApplyContentAsync()
    {
        using var fixture = new TestSolutionFixture();
        using var workspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        await workspaceManager.LoadSolutionAsync(fixture.SolutionPath);
        var tools = BuildTools(workspaceManager);

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
        // needs its own acknowledgment here — otherwise the revert write below is refused by the drift
        // guard in ApplyProposedChangesAsync (RoslynSentinel.Common/PersistentWorkspaceManager.cs)
        // before it ever reaches undo logic.
        workspaceManager.ClearExternalFileChanges();

        var result = await tools.UndoLastApply(changeId);

        Assert.That(result.Success, Is.True);
        Assert.That((string)result.Data!, Does.Contain("Reverted 1 files"));
        Assert.That(await File.ReadAllTextAsync(targetFile), Is.EqualTo(originalContent));
    }
}
