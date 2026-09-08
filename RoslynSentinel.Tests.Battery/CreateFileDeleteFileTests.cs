// CreateFile/DeleteFile — SentinelWorkspaceTools. New tools that route through the same
// ApplyProposedChangesAsync chokepoint as every other mutating tool (drift-checked, undo-tracked),
// extended with a deletePaths parameter for DeleteFile. Requires a real disk-backed solution
// (PersistentWorkspaceManager + TestSolutionFixture) since these tools do real File.Exists/
// File.Delete checks that FakeWorkspaceManager can't satisfy.

using Microsoft.Extensions.Logging.Abstractions;

#pragma warning disable CS8618
namespace RoslynSentinel.Tests.Battery;

[TestFixture]
public class CreateFileDeleteFileTests
{
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

        var workspaceTools = new SentinelWorkspaceTools(
            workspaceManager, validationEngine, diffEngine, diagnosticEngine,
            solutionManagementEngine, structuralRefinementEngine, dependencyEngine,
            projectConsistencyEngine, config, NullLogger<SentinelWorkspaceTools>.Instance,
            new BuildEngine(workspaceManager, diagnosticEngine),
            new SymbolNavigationEngine(workspaceManager, NullLogger<SymbolNavigationEngine>.Instance),
            new TestRunEngine(workspaceManager),
            new WorkspaceReadNavigationTools(new WorkspaceReadNavigationImpl(workspaceManager, NullLogger<WorkspaceReadNavigationImpl>.Instance)));

        var wholeFileWriteTools = new SentinelWholeFileWriteTools(workspaceManager, workspaceTools, validationEngine, diffEngine, NullLogger<SentinelWholeFileWriteTools>.Instance, new SymbolNavigationEngine(workspaceManager, NullLogger<SymbolNavigationEngine>.Instance));

        return workspaceTools;
    }

    [Test]
    public async Task CreateFile_NewPath_WritesContentAndReturnsSuccessAsync()
    {
        using var fixture = new TestSolutionFixture();
        using var workspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        await workspaceManager.LoadSolutionAsync(fixture.SolutionPath);
        var workspaceTools = BuildTools(workspaceManager);
        var diffEngine = new DiffEngine();
        var validationEngine = new ValidationEngine(NullLogger<ValidationEngine>.Instance, workspaceManager, diffEngine);
        SentinelWholeFileWriteTools wholeFileWriteTools = new SentinelWholeFileWriteTools(workspaceManager, workspaceTools, validationEngine, diffEngine, NullLogger<SentinelWholeFileWriteTools>.Instance, new SymbolNavigationEngine(workspaceManager, NullLogger<SymbolNavigationEngine>.Instance));

        var newFile = Path.Combine(fixture.SolutionDirectory, "NewFile.cs");
        var content = "public class NewFile { }";

        var result = await wholeFileWriteTools.WriteFile(reason: "test", WriteFileOperation.CreateFile, newFile, content);

        Assert.That(result.Success, Is.True);
        Assert.That(File.Exists(newFile), Is.True);
        Assert.That(await File.ReadAllTextAsync(newFile), Is.EqualTo(content));
    }

    [Test]
    public async Task CreateFile_AlreadyExists_FailsWithoutOverwritingAsync()
    {
        using var fixture = new TestSolutionFixture();
        using var workspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        await workspaceManager.LoadSolutionAsync(fixture.SolutionPath);
        var workspaceTools = BuildTools(workspaceManager);
        var diffEngine = new DiffEngine();
        var validationEngine = new ValidationEngine(NullLogger<ValidationEngine>.Instance, workspaceManager, diffEngine);
        SentinelWholeFileWriteTools wholeFileWriteTools = new SentinelWholeFileWriteTools(workspaceManager, workspaceTools, validationEngine, diffEngine, NullLogger<SentinelWholeFileWriteTools>.Instance, new SymbolNavigationEngine(workspaceManager, NullLogger<SymbolNavigationEngine>.Instance));

        var existingFile = Directory.EnumerateFiles(fixture.SolutionDirectory, "*.cs", SearchOption.AllDirectories).First();
        var originalContent = await File.ReadAllTextAsync(existingFile);

        var result = await wholeFileWriteTools.WriteFile(reason: "test", WriteFileOperation.CreateFile, existingFile, "replacement content");

        Assert.That(result.Success, Is.False);
        Assert.That(result.Error!.ErrorCode, Is.EqualTo(ToolErrorCode.InvalidArgument));
        Assert.That(await File.ReadAllTextAsync(existingFile), Is.EqualTo(originalContent));
    }

    [Test]
    public async Task ReplaceFile_ExistingFile_OverwritesContentAsync()
    {
        using var fixture = new TestSolutionFixture();
        using var workspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        await workspaceManager.LoadSolutionAsync(fixture.SolutionPath);
        var workspaceTools = BuildTools(workspaceManager);
        var diffEngine = new DiffEngine();
        var validationEngine = new ValidationEngine(NullLogger<ValidationEngine>.Instance, workspaceManager, diffEngine);
        SentinelWholeFileWriteTools wholeFileWriteTools = new SentinelWholeFileWriteTools(workspaceManager, workspaceTools, validationEngine, diffEngine, NullLogger<SentinelWholeFileWriteTools>.Instance, new SymbolNavigationEngine(workspaceManager, NullLogger<SymbolNavigationEngine>.Instance));

        var existingFile = Directory.EnumerateFiles(fixture.SolutionDirectory, "*.cs", SearchOption.AllDirectories).First();
        var replacementContent = "public class Replaced { }";

        // validateOnApply: false — the fixture's other files may reference the original type in
        // existingFile, so a delta-compile of an unrelated replacement would fail; this test only
        // exercises the ReplaceFile exists-check + overwrite plumbing, not compilation validity.
        var result = await wholeFileWriteTools.WriteFile(reason: "test", WriteFileOperation.ReplaceFile, existingFile, replacementContent, validateOnApply: false);

        Assert.That(result.Success, Is.True);
        Assert.That(await File.ReadAllTextAsync(existingFile), Is.EqualTo(replacementContent));
    }

    [Test]
    public async Task ReplaceFile_DoesNotExist_FailsAsync()
    {
        using var fixture = new TestSolutionFixture();
        using var workspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        await workspaceManager.LoadSolutionAsync(fixture.SolutionPath);
        var workspaceTools = BuildTools(workspaceManager);
        var diffEngine = new DiffEngine();
        var validationEngine = new ValidationEngine(NullLogger<ValidationEngine>.Instance, workspaceManager, diffEngine);
        SentinelWholeFileWriteTools wholeFileWriteTools = new SentinelWholeFileWriteTools(workspaceManager, workspaceTools, validationEngine, diffEngine, NullLogger<SentinelWholeFileWriteTools>.Instance, new SymbolNavigationEngine(workspaceManager, NullLogger<SymbolNavigationEngine>.Instance));

        var missingFile = Path.Combine(fixture.SolutionDirectory, "DoesNotExist.cs");

        var result = await wholeFileWriteTools.WriteFile(reason: "test", WriteFileOperation.ReplaceFile, missingFile, "public class X { }");

        Assert.That(result.Success, Is.False);
        Assert.That(result.Error!.ErrorCode, Is.EqualTo(ToolErrorCode.InvalidArgument));
        Assert.That(File.Exists(missingFile), Is.False);
    }

    [Test]
    public async Task CreateFile_ParentDirectoryMissing_CreatesDirectoryAsync()
    {
        using var fixture = new TestSolutionFixture();
        using var workspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        await workspaceManager.LoadSolutionAsync(fixture.SolutionPath);
        var workspaceTools = BuildTools(workspaceManager);
        var diffEngine = new DiffEngine();
        var validationEngine = new ValidationEngine(NullLogger<ValidationEngine>.Instance, workspaceManager, diffEngine);
        SentinelWholeFileWriteTools wholeFileWriteTools = new SentinelWholeFileWriteTools(workspaceManager, workspaceTools, validationEngine, diffEngine, NullLogger<SentinelWholeFileWriteTools>.Instance, new SymbolNavigationEngine(workspaceManager, NullLogger<SymbolNavigationEngine>.Instance));

        var newFile = Path.Combine(fixture.SolutionDirectory, "NewSubdir", "Nested.cs");

        var result = await wholeFileWriteTools.WriteFile(reason: "test", WriteFileOperation.CreateFile, newFile, "public class Nested { }");

        Assert.That(result.Success, Is.True);
        Assert.That(File.Exists(newFile), Is.True);
    }

    [Test]
    public async Task CreateFile_NonCsFile_ThenReadFile_ReturnsContentAsync()
    {
        // CreateFile writes any file regardless of extension; PersistentWorkspaceManager's
        // post-write in-memory sync only tracks `.cs` files as Roslyn Documents (see
        // ApplyInMemoryDocumentUpdatesAsync). Before the ReadFile disk-fallback fix, this left
        // CreateFile able to write files that ReadFile could never see again.
        using var fixture = new TestSolutionFixture();
        using var workspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        await workspaceManager.LoadSolutionAsync(fixture.SolutionPath);
        var workspaceTools = BuildTools(workspaceManager);
        var diffEngine = new DiffEngine();
        var validationEngine = new ValidationEngine(NullLogger<ValidationEngine>.Instance, workspaceManager, diffEngine);
        SentinelWholeFileWriteTools wholeFileWriteTools = new SentinelWholeFileWriteTools(workspaceManager, workspaceTools, validationEngine, diffEngine, NullLogger<SentinelWholeFileWriteTools>.Instance, new SymbolNavigationEngine(workspaceManager, NullLogger<SymbolNavigationEngine>.Instance));

        var newFile = Path.Combine(fixture.SolutionDirectory, "Notes.txt");
        var content = "plain text notes, not a Roslyn document";

        var createResult = await wholeFileWriteTools.WriteFile(reason: "test", WriteFileOperation.CreateFile, newFile, content);
        Assert.That(createResult.Success, Is.True);

        var readResult = await workspaceTools.ReadFile(reason: "test", newFile);

        Assert.That(readResult.Success, Is.True, readResult.Error?.Message);
        var data = readResult.Data!;
        var source = (string)data.GetType().GetProperty("source")!.GetValue(data)!;
        Assert.That(source, Is.EqualTo(content));
    }

    [Test]
    public async Task DeleteFile_ExistingFile_RemovesFromDiskAsync()
    {
        using var fixture = new TestSolutionFixture();
        using var workspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        await workspaceManager.LoadSolutionAsync(fixture.SolutionPath);
        var workspaceTools = BuildTools(workspaceManager);
        var diffEngine = new DiffEngine();
        var validationEngine = new ValidationEngine(NullLogger<ValidationEngine>.Instance, workspaceManager, diffEngine);
        SentinelWholeFileWriteTools wholeFileWriteTools = new SentinelWholeFileWriteTools(workspaceManager, workspaceTools, validationEngine, diffEngine, NullLogger<SentinelWholeFileWriteTools>.Instance, new SymbolNavigationEngine(workspaceManager, NullLogger<SymbolNavigationEngine>.Instance));

        var newFile = Path.Combine(fixture.SolutionDirectory, "ToDelete.cs");
        await fixture.AddFileToSolution(workspaceManager, "ToDelete.cs", "public class ToDelete { }");

        var result = await wholeFileWriteTools.DeleteFile(reason: "test", newFile);

        Assert.That(result.Success, Is.True);
        Assert.That(File.Exists(newFile), Is.False);
    }

    [Test]
    public async Task DeleteFile_NonExistentFile_FailsWithInvalidArgumentAsync()
    {
        using var fixture = new TestSolutionFixture();
        using var workspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        await workspaceManager.LoadSolutionAsync(fixture.SolutionPath);
        var workspaceTools = BuildTools(workspaceManager);
        var diffEngine = new DiffEngine();
        var validationEngine = new ValidationEngine(NullLogger<ValidationEngine>.Instance, workspaceManager, diffEngine);
        SentinelWholeFileWriteTools wholeFileWriteTools = new SentinelWholeFileWriteTools(workspaceManager, workspaceTools, validationEngine, diffEngine, NullLogger<SentinelWholeFileWriteTools>.Instance, new SymbolNavigationEngine(workspaceManager, NullLogger<SymbolNavigationEngine>.Instance));

        var missingFile = Path.Combine(fixture.SolutionDirectory, "DoesNotExist.cs");

        var result = await wholeFileWriteTools.DeleteFile(reason: "test", missingFile);

        Assert.That(result.Success, Is.False);
        Assert.That(result.Error!.ErrorCode, Is.EqualTo(ToolErrorCode.InvalidArgument));
    }

    [Test]
    public async Task DeleteFile_ThenUndoLastApply_RestoresFileAsync()
    {
        using var fixture = new TestSolutionFixture();
        using var workspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        await workspaceManager.LoadSolutionAsync(fixture.SolutionPath);
        var workspaceTools = BuildTools(workspaceManager);
        var diffEngine = new DiffEngine();
        var validationEngine = new ValidationEngine(NullLogger<ValidationEngine>.Instance, workspaceManager, diffEngine);
        SentinelWholeFileWriteTools wholeFileWriteTools = new SentinelWholeFileWriteTools(workspaceManager, workspaceTools, validationEngine, diffEngine, NullLogger<SentinelWholeFileWriteTools>.Instance, new SymbolNavigationEngine(workspaceManager, NullLogger<SymbolNavigationEngine>.Instance));

        var newFile = Path.Combine(fixture.SolutionDirectory, "Undoable.cs");
        var content = "public class Undoable { }";
        await fixture.AddFileToSolution(workspaceManager, "Undoable.cs", content);

        var deleteResult = await wholeFileWriteTools.DeleteFile(reason: "test", newFile);
        Assert.That(deleteResult.Success, Is.True);
        Assert.That(File.Exists(newFile), Is.False);

        // changeId isn't exposed on ApplyChangesResult directly — recover it from the blob
        // filename ({toolName}_{timestamp}_{changeId}.json), same as UndoLastApplyTests does.
        var blobDir = Path.Combine(fixture.SolutionDirectory, ".roslynsentinel", "operations");
        var blobFile = Directory.EnumerateFiles(blobDir, "delete_file_*").OrderByDescending(f => f).First();
        var changeId = Path.GetFileNameWithoutExtension(blobFile).Split('_').Last();

        var undoResult = await workspaceTools.UndoLastApply(reason: "test", changeId);

        Assert.That(undoResult.Success, Is.True);
        Assert.That(File.Exists(newFile), Is.True);
        Assert.That(await File.ReadAllTextAsync(newFile), Is.EqualTo(content));
    }

    [Test]
    public async Task DeleteFile_DriftedFile_RefusesDeleteAsync()
    {
        using var fixture = new TestSolutionFixture();
        using var workspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        await workspaceManager.LoadSolutionAsync(fixture.SolutionPath);
        var workspaceTools = BuildTools(workspaceManager);
        var diffEngine = new DiffEngine();
        var validationEngine = new ValidationEngine(NullLogger<ValidationEngine>.Instance, workspaceManager, diffEngine);
        SentinelWholeFileWriteTools wholeFileWriteTools = new SentinelWholeFileWriteTools(workspaceManager, workspaceTools, validationEngine, diffEngine, NullLogger<SentinelWholeFileWriteTools>.Instance, new SymbolNavigationEngine(workspaceManager, NullLogger<SymbolNavigationEngine>.Instance));

        var targetFile = Directory.EnumerateFiles(fixture.SolutionDirectory, "*.cs", SearchOption.AllDirectories).First();
        // Modify the file directly on disk (bypassing ApplyProposedChangesAsync) without
        // reloading/clearing drift, so DeleteFile sees it as externally modified since last sync.
        await fixture.ModifyFileInSolution(workspaceManager, Path.GetRelativePath(fixture.SolutionDirectory, targetFile), await File.ReadAllTextAsync(targetFile) + "\n// drift\n", reloadSolution: false);

        // ApplyProposedChangesAsync's drift check (GetExternalFileChanges()) reads a set populated
        // asynchronously by a FileSystemWatcher callback (OnFileSystemChanged), not synchronously by
        // the write above — under heavy concurrent disk I/O from other test assemblies in a full
        // parallel run, the watcher event can lag past the point where DeleteFile below checks it,
        // making the delete wrongly succeed (root-caused 2026-09-06, was previously a documented
        // flake here). Poll for the watcher to actually report the drift before proceeding, instead
        // of assuming it already has.
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!workspaceManager.GetExternalFileChanges().Contains(targetFile, StringComparer.OrdinalIgnoreCase) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(25);
        }

        var result = await wholeFileWriteTools.DeleteFile(reason: "test", targetFile);

        Assert.That(result.Success, Is.False);
        Assert.That(File.Exists(targetFile), Is.True);
    }
}
