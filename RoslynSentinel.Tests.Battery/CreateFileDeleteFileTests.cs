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
        return new SentinelWorkspaceTools(
            workspaceManager, validationEngine, diffEngine, diagnosticEngine,
            solutionManagementEngine, structuralRefinementEngine, dependencyEngine,
            projectConsistencyEngine, config, NullLogger<SentinelWorkspaceTools>.Instance,
            new BuildEngine(workspaceManager, diagnosticEngine),
            new SymbolNavigationEngine(workspaceManager, NullLogger<SymbolNavigationEngine>.Instance));
    }

    [Test]
    public async Task CreateFile_NewPath_WritesContentAndReturnsSuccessAsync()
    {
        using var fixture = new TestSolutionFixture();
        using var workspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        await workspaceManager.LoadSolutionAsync(fixture.SolutionPath);
        var tools = BuildTools(workspaceManager);

        var newFile = Path.Combine(fixture.SolutionDirectory, "NewFile.cs");
        var content = "public class NewFile { }";

        var result = await tools.CreateFile(newFile, content);

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
        var tools = BuildTools(workspaceManager);

        var existingFile = Directory.EnumerateFiles(fixture.SolutionDirectory, "*.cs", SearchOption.AllDirectories).First();
        var originalContent = await File.ReadAllTextAsync(existingFile);

        var result = await tools.CreateFile(existingFile, "replacement content");

        Assert.That(result.Success, Is.False);
        Assert.That(result.Error!.ErrorCode, Is.EqualTo(ToolErrorCode.InvalidArgument));
        Assert.That(await File.ReadAllTextAsync(existingFile), Is.EqualTo(originalContent));
    }

    [Test]
    public async Task CreateFile_ParentDirectoryMissing_CreatesDirectoryAsync()
    {
        using var fixture = new TestSolutionFixture();
        using var workspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        await workspaceManager.LoadSolutionAsync(fixture.SolutionPath);
        var tools = BuildTools(workspaceManager);

        var newFile = Path.Combine(fixture.SolutionDirectory, "NewSubdir", "Nested.cs");

        var result = await tools.CreateFile(newFile, "public class Nested { }");

        Assert.That(result.Success, Is.True);
        Assert.That(File.Exists(newFile), Is.True);
    }

    [Test]
    public async Task DeleteFile_ExistingFile_RemovesFromDiskAsync()
    {
        using var fixture = new TestSolutionFixture();
        using var workspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        await workspaceManager.LoadSolutionAsync(fixture.SolutionPath);
        var tools = BuildTools(workspaceManager);

        var newFile = Path.Combine(fixture.SolutionDirectory, "ToDelete.cs");
        await fixture.AddFileToSolution(workspaceManager, "ToDelete.cs", "public class ToDelete { }");

        var result = await tools.DeleteFile(newFile);

        Assert.That(result.Success, Is.True);
        Assert.That(File.Exists(newFile), Is.False);
    }

    [Test]
    public async Task DeleteFile_NonExistentFile_FailsWithInvalidArgumentAsync()
    {
        using var fixture = new TestSolutionFixture();
        using var workspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        await workspaceManager.LoadSolutionAsync(fixture.SolutionPath);
        var tools = BuildTools(workspaceManager);

        var missingFile = Path.Combine(fixture.SolutionDirectory, "DoesNotExist.cs");

        var result = await tools.DeleteFile(missingFile);

        Assert.That(result.Success, Is.False);
        Assert.That(result.Error!.ErrorCode, Is.EqualTo(ToolErrorCode.InvalidArgument));
    }

    [Test]
    public async Task DeleteFile_ThenUndoLastApply_RestoresFileAsync()
    {
        using var fixture = new TestSolutionFixture();
        using var workspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        await workspaceManager.LoadSolutionAsync(fixture.SolutionPath);
        var tools = BuildTools(workspaceManager);

        var newFile = Path.Combine(fixture.SolutionDirectory, "Undoable.cs");
        var content = "public class Undoable { }";
        await fixture.AddFileToSolution(workspaceManager, "Undoable.cs", content);

        var deleteResult = await tools.DeleteFile(newFile);
        Assert.That(deleteResult.Success, Is.True);
        Assert.That(File.Exists(newFile), Is.False);

        // changeId isn't exposed on ApplyChangesResult directly — recover it from the blob
        // filename ({toolName}_{timestamp}_{changeId}.json), same as UndoLastApplyTests does.
        var blobDir = Path.Combine(fixture.SolutionDirectory, ".roslynsentinel", "operations");
        var blobFile = Directory.EnumerateFiles(blobDir, "delete_file_*").OrderByDescending(f => f).First();
        var changeId = Path.GetFileNameWithoutExtension(blobFile).Split('_').Last();

        var undoResult = await tools.UndoLastApply(changeId);

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
        var tools = BuildTools(workspaceManager);

        var targetFile = Directory.EnumerateFiles(fixture.SolutionDirectory, "*.cs", SearchOption.AllDirectories).First();
        // Modify the file directly on disk (bypassing ApplyProposedChangesAsync) without
        // reloading/clearing drift, so DeleteFile sees it as externally modified since last sync.
        await fixture.ModifyFileInSolution(workspaceManager, Path.GetRelativePath(fixture.SolutionDirectory, targetFile), await File.ReadAllTextAsync(targetFile) + "\n// drift\n", reloadSolution: false);

        var result = await tools.DeleteFile(targetFile);

        Assert.That(result.Success, Is.False);
        Assert.That(File.Exists(targetFile), Is.True);
    }
}
