// CreateFileTool/DeleteFileTool -> WorkspaceTools. New tools that route through the same
// ApplyProposedChangesAsync chokepoint as every other mutating tool (drift-checked, undo-tracked),
// extended with a deletePaths parameter for DeleteFileTool. Requires a real disk-backed solution
// (PersistentWorkspaceManager + TestSolutionFixture) since these tools do real File.Exists/
// File.Delete checks that FakeWorkspaceManager can't satisfy.

using Microsoft.Extensions.Logging.Abstractions;

//using RoslynSentinel.Engines.Advanced;
using RoslynSentinel.Engines.Basic;
using RoslynSentinel.Tools.Basic;

#pragma warning disable CS8618
namespace RoslynSentinel.Tests.Battery.Advanced;

[TestFixture]
[Parallelizable(ParallelScope.All)]
[Category("SymbolNavigationEngine")] // sentinel:auto-category
[Category("WholeFileWriteTools")] // sentinel:auto-category
public class CreateFileDeleteFileTests
{
    private static WorkspaceTools BuildTools(IWorkspaceManager workspaceManager) =>
        Basic.TestTools.WorkspaceTools(workspaceManager);

    [Test]
    public async Task CreateFile_NewPath_WritesContentAndReturnsSuccessAsync()
    {
        using var fixture = new TestSolutionFixture();
        using var workspaceManager = Basic.TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        await workspaceManager.LoadSolutionAsync(fixture.SolutionPath);
        var workspaceTools = BuildTools(workspaceManager);
        var diffEngine = Basic.TestEngines.DiffEngine();
        var validationEngine = Basic.TestEngines.ValidationEngine(workspaceManager, diffEngine, NullLogger<ValidationEngine>.Instance);
        WholeFileWriteTools wholeFileWriteTools = Basic.TestTools.WholeFileWriteTools(workspaceManager, workspaceTools, validationEngine, diffEngine, NullLogger<WholeFileWriteTools>.Instance, Basic.TestEngines.SymbolNavigationEngine(workspaceManager, NullLogger<SymbolNavigationEngine>.Instance));

        var newFile = Path.Combine(fixture.SolutionDirectory, "NewFile.cs");
        var content = "public class NewFile { }";

        var result = await wholeFileWriteTools.WriteFileTool(reason: "test message", WriteFileOperation.CreateFile, newFile, content);

        Assert.That(!result.IsError, Is.True);
        Assert.That(File.Exists(newFile), Is.True);
        Assert.That(await File.ReadAllTextAsync(newFile), Is.EqualTo(content));
    }

    [Test]
    public async Task CreateFile_AlreadyExists_FailsWithoutOverwritingAsync()
    {
        using var fixture = new TestSolutionFixture();
        using var workspaceManager = Basic.TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        await workspaceManager.LoadSolutionAsync(fixture.SolutionPath);
        var workspaceTools = BuildTools(workspaceManager);
        var diffEngine = Basic.TestEngines.DiffEngine();
        var validationEngine = Basic.TestEngines.ValidationEngine(workspaceManager, diffEngine, NullLogger<ValidationEngine>.Instance);
        WholeFileWriteTools wholeFileWriteTools = Basic.TestTools.WholeFileWriteTools(workspaceManager, workspaceTools, validationEngine, diffEngine, NullLogger<WholeFileWriteTools>.Instance, Basic.TestEngines.SymbolNavigationEngine(workspaceManager, NullLogger<SymbolNavigationEngine>.Instance));

        var existingFile = Directory.EnumerateFiles(fixture.SolutionDirectory, "*.cs", SearchOption.AllDirectories).First();
        var originalContent = await File.ReadAllTextAsync(existingFile);

        var result = await wholeFileWriteTools.WriteFileTool(reason: "test message", WriteFileOperation.CreateFile, existingFile, "replacement content");

        Assert.That(!result.IsError, Is.False);
        Assert.That(result.ErrorData!.ErrorCode, Is.EqualTo(ToolErrorCode.InvalidArgument));
        Assert.That(await File.ReadAllTextAsync(existingFile), Is.EqualTo(originalContent));
    }

    [Test]
    public async Task ReplaceFile_ExistingFile_OverwritesContentAsync()
    {
        using var fixture = new TestSolutionFixture();
        using var workspaceManager = Basic.TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        await workspaceManager.LoadSolutionAsync(fixture.SolutionPath);
        var workspaceTools = BuildTools(workspaceManager);
        var diffEngine = Basic.TestEngines.DiffEngine();
        var validationEngine = Basic.TestEngines.ValidationEngine(workspaceManager, diffEngine, NullLogger<ValidationEngine>.Instance);
        WholeFileWriteTools wholeFileWriteTools = Basic.TestTools.WholeFileWriteTools(workspaceManager, workspaceTools, validationEngine, diffEngine, NullLogger<WholeFileWriteTools>.Instance, Basic.TestEngines.SymbolNavigationEngine(workspaceManager, NullLogger<SymbolNavigationEngine>.Instance));

        var existingFile = Directory.EnumerateFiles(fixture.SolutionDirectory, "*.cs", SearchOption.AllDirectories).First();
        var replacementContent = "public class Replaced { }";

        // validateOnApply: false -> the fixture's other files may reference the original type in
        // existingFile, so a delta-compile of an unrelated replacement would fail; this test only
        // exercises the ReplaceFile exists-check + overwrite plumbing, not compilation validity.
        var result = await wholeFileWriteTools.WriteFileTool(reason: "test message", WriteFileOperation.ReplaceFile, existingFile, replacementContent, validateOnApply: false);

        Assert.That(!result.IsError, Is.True);
        Assert.That(await File.ReadAllTextAsync(existingFile), Is.EqualTo(replacementContent));
    }

    [Test]
    public async Task Create_AliasOfCreateFile_NewPathCreatesExistingPathFailsAsync()
    {
        using var fixture = new TestSolutionFixture();
        using var workspaceManager = Basic.TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        await workspaceManager.LoadSolutionAsync(fixture.SolutionPath);
        var workspaceTools = BuildTools(workspaceManager);
        var diffEngine = Basic.TestEngines.DiffEngine();
        var validationEngine = Basic.TestEngines.ValidationEngine(workspaceManager, diffEngine, NullLogger<ValidationEngine>.Instance);
        WholeFileWriteTools wholeFileWriteTools = Basic.TestTools.WholeFileWriteTools(workspaceManager, workspaceTools, validationEngine, diffEngine, NullLogger<WholeFileWriteTools>.Instance, Basic.TestEngines.SymbolNavigationEngine(workspaceManager, NullLogger<SymbolNavigationEngine>.Instance));

        var newFile = Path.Combine(fixture.SolutionDirectory, "ViaCreateAlias.cs");

        var created = await wholeFileWriteTools.WriteFileTool(reason: "test message", WriteFileOperation.Create, newFile, "public class ViaCreateAlias { }");
        Assert.That(!created.IsError, Is.True, created.ErrorData?.Message);
        Assert.That(File.ReadAllText(newFile), Is.EqualTo("public class ViaCreateAlias { }"));

        var again = await wholeFileWriteTools.WriteFileTool(reason: "test message", WriteFileOperation.Create, newFile, "public class Other { }");
        Assert.That(!again.IsError, Is.False);
        Assert.That(again.ErrorData!.ErrorCode, Is.EqualTo(ToolErrorCode.InvalidArgument));
        Assert.That(File.ReadAllText(newFile), Is.EqualTo("public class ViaCreateAlias { }"));
    }

    [Test]
    public async Task Replace_AliasOfReplaceFile_OverwritesExistingAndCreatesMissingAsync()
    {
        using var fixture = new TestSolutionFixture();
        using var workspaceManager = Basic.TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        await workspaceManager.LoadSolutionAsync(fixture.SolutionPath);
        var workspaceTools = BuildTools(workspaceManager);
        var diffEngine = Basic.TestEngines.DiffEngine();
        var validationEngine = Basic.TestEngines.ValidationEngine(workspaceManager, diffEngine, NullLogger<ValidationEngine>.Instance);
        WholeFileWriteTools wholeFileWriteTools = Basic.TestTools.WholeFileWriteTools(workspaceManager, workspaceTools, validationEngine, diffEngine, NullLogger<WholeFileWriteTools>.Instance, Basic.TestEngines.SymbolNavigationEngine(workspaceManager, NullLogger<SymbolNavigationEngine>.Instance));

        var newFile = Path.Combine(fixture.SolutionDirectory, "ViaReplaceAlias.cs");

        var created = await wholeFileWriteTools.WriteFileTool(reason: "test message", WriteFileOperation.Replace, newFile, "public class ViaReplaceAlias { }");
        Assert.That(!created.IsError, Is.True, created.ErrorData?.Message);
        Assert.That(File.ReadAllText(newFile), Is.EqualTo("public class ViaReplaceAlias { }"));

        var replaced = await wholeFileWriteTools.WriteFileTool(reason: "test message", WriteFileOperation.Replace, newFile, "public class ViaReplaceAlias2 { }");
        Assert.That(!replaced.IsError, Is.True, replaced.ErrorData?.Message);
        Assert.That(File.ReadAllText(newFile), Is.EqualTo("public class ViaReplaceAlias2 { }"));
    }

    [Test]
    public async Task ReplaceFile_DoesNotExist_CreatesFileAsync()
    {
        using var fixture = new TestSolutionFixture();
        using var workspaceManager = Basic.TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        await workspaceManager.LoadSolutionAsync(fixture.SolutionPath);
        var workspaceTools = BuildTools(workspaceManager);
        var diffEngine = Basic.TestEngines.DiffEngine();
        var validationEngine = Basic.TestEngines.ValidationEngine(workspaceManager, diffEngine, NullLogger<ValidationEngine>.Instance);
        WholeFileWriteTools wholeFileWriteTools = Basic.TestTools.WholeFileWriteTools(workspaceManager, workspaceTools, validationEngine, diffEngine, NullLogger<WholeFileWriteTools>.Instance, Basic.TestEngines.SymbolNavigationEngine(workspaceManager, NullLogger<SymbolNavigationEngine>.Instance));

        var missingFile = Path.Combine(fixture.SolutionDirectory, "DoesNotExist.cs");

        var result = await wholeFileWriteTools.WriteFileTool(reason: "test message", WriteFileOperation.ReplaceFile, missingFile, "public class X { }");

        Assert.That(!result.IsError, Is.True, result.ErrorData?.Message);
        Assert.That(File.Exists(missingFile), Is.True);
        Assert.That(File.ReadAllText(missingFile), Is.EqualTo("public class X { }"));
    }

    [Test]
    public async Task CreateFile_ParentDirectoryMissing_CreatesDirectoryAsync()
    {
        using var fixture = new TestSolutionFixture();
        using var workspaceManager = Basic.TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        await workspaceManager.LoadSolutionAsync(fixture.SolutionPath);
        var workspaceTools = BuildTools(workspaceManager);
        var diffEngine = Basic.TestEngines.DiffEngine();
        var validationEngine = Basic.TestEngines.ValidationEngine(workspaceManager, diffEngine, NullLogger<ValidationEngine>.Instance);
        WholeFileWriteTools wholeFileWriteTools = Basic.TestTools.WholeFileWriteTools(workspaceManager, workspaceTools, validationEngine, diffEngine, NullLogger<WholeFileWriteTools>.Instance, Basic.TestEngines.SymbolNavigationEngine(workspaceManager, NullLogger<SymbolNavigationEngine>.Instance));

        var newFile = Path.Combine(fixture.SolutionDirectory, "NewSubdir", "Nested.cs");

        var result = await wholeFileWriteTools.WriteFileTool(reason: "test message", WriteFileOperation.CreateFile, newFile, "public class Nested { }");

        Assert.That(!result.IsError, Is.True);
        Assert.That(File.Exists(newFile), Is.True);
    }

    [Test]
    [Category("WorkspaceTools")] // sentinel:auto-category
    public async Task CreateFile_NonCsFile_ThenReadFile_ReturnsContentAsync()
    {
        // CreateFileTool writes any file regardless of extension; PersistentWorkspaceManager's
        // post-write in-memory sync only tracks `.cs` files as Roslyn Documents (see
        // ApplyInMemoryDocumentUpdatesAsync). Before the ReadFileTool disk-fallback fix, this left
        // CreateFileTool able to write files that ReadFileTool could never see again.
        using var fixture = new TestSolutionFixture();
        using var workspaceManager = Basic.TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        await workspaceManager.LoadSolutionAsync(fixture.SolutionPath);
        var workspaceTools = BuildTools(workspaceManager);
        var diffEngine = Basic.TestEngines.DiffEngine();
        var validationEngine = Basic.TestEngines.ValidationEngine(workspaceManager, diffEngine, NullLogger<ValidationEngine>.Instance);
        WholeFileWriteTools wholeFileWriteTools = Basic.TestTools.WholeFileWriteTools(workspaceManager, workspaceTools, validationEngine, diffEngine, NullLogger<WholeFileWriteTools>.Instance, Basic.TestEngines.SymbolNavigationEngine(workspaceManager, NullLogger<SymbolNavigationEngine>.Instance));

        var newFile = Path.Combine(fixture.SolutionDirectory, "Notes.txt");
        var content = "plain text notes, not a Roslyn document";

        var createResult = await wholeFileWriteTools.WriteFileTool(reason: "test message", WriteFileOperation.CreateFile, newFile, content);
        Assert.That(!createResult.IsError, Is.True);

        var readResult = await workspaceTools.ReadFileTool(reason: "test message", newFile);

        Assert.That(!readResult.IsError, Is.True, readResult.ErrorData?.Message);
        var data = readResult.SuccessData!;
        var source = (string)data.GetType().GetProperty("source")!.GetValue(data)!;
        Assert.That(source, Is.EqualTo(content));
    }

    [Test]
    [Category("WorkspaceTools")] // sentinel:auto-category
    public async Task CreateFile_CsFileWithContent_WritesTheFullBodyInOneCallAsync()
    {
        using var fixture = new TestSolutionFixture();
        using var workspaceManager = Basic.TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        // The content is written as LF, so the mode is pinned to Lf rather than the platform default.
        workspaceManager.EolModeOverride = EolMode.Lf;
        await workspaceManager.LoadSolutionAsync(fixture.SolutionPath);
        var workspaceTools = BuildTools(workspaceManager);

        var newFile = Path.Combine(fixture.SolutionDirectory, "ContosoOrders.Core", "Greeter.cs");
        var content = "namespace MyApp.Content;\n\npublic class Greeter\n{\n    public string Hello() => \"hello\";\n    public string Bye() => \"bye\";\n}\n";

        var result = await workspaceTools.CreateFileTool(reason: "test message", newFile, content: content);

        Assert.That(!result.IsError, Is.True, result.ErrorData?.Message);
        Assert.That(File.Exists(newFile), Is.True);
        Assert.That(await File.ReadAllTextAsync(newFile), Is.EqualTo(content));
    }

    [Test]
    [Category("WorkspaceTools")] // sentinel:auto-category
    public async Task CreateFile_ContentTogetherWithTypeKind_FailsInvalidArgumentAsync()
    {
        using var fixture = new TestSolutionFixture();
        using var workspaceManager = Basic.TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        await workspaceManager.LoadSolutionAsync(fixture.SolutionPath);
        var workspaceTools = BuildTools(workspaceManager);

        var newFile = Path.Combine(fixture.SolutionDirectory, "Mixed.cs");

        var result = await workspaceTools.CreateFileTool(reason: "test message", newFile, typeKind: NewTypeKind.@class, typeName: "Foo", content: "public class Foo { }\n");

        Assert.That(!result.IsError, Is.False);
        Assert.That(result.ErrorData!.ErrorCode, Is.EqualTo(ToolErrorCode.InvalidArgument));
        Assert.That(result.ErrorData!.Message, Does.Contain("pass either 'content'"));
        Assert.That(File.Exists(newFile), Is.False);
    }

    [Test]
    [Category("WorkspaceTools")] // sentinel:auto-category
    public async Task CreateFile_CsFileWithoutContentOrType_MessageOffersContentAsync()
    {
        using var fixture = new TestSolutionFixture();
        using var workspaceManager = Basic.TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        await workspaceManager.LoadSolutionAsync(fixture.SolutionPath);
        var workspaceTools = BuildTools(workspaceManager);

        var newFile = Path.Combine(fixture.SolutionDirectory, "NoBody.cs");

        var result = await workspaceTools.CreateFileTool(reason: "test message", newFile);

        Assert.That(!result.IsError, Is.False);
        Assert.That(result.ErrorData!.Message, Does.Contain("Or pass 'content'"));
        Assert.That(File.Exists(newFile), Is.False);
    }

    [Test]
    [Category("WorkspaceTools")] // sentinel:auto-category
    public async Task CreateFile_ContentExistingFile_FailsWithoutOverwritingAsync()
    {
        using var fixture = new TestSolutionFixture();
        using var workspaceManager = Basic.TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        await workspaceManager.LoadSolutionAsync(fixture.SolutionPath);
        var workspaceTools = BuildTools(workspaceManager);

        var existingFile = Directory.EnumerateFiles(fixture.SolutionDirectory, "*.cs", SearchOption.AllDirectories).First();
        var originalContent = await File.ReadAllTextAsync(existingFile);

        var result = await workspaceTools.CreateFileTool(reason: "test message", existingFile, content: "public class Replacement { }\n");

        Assert.That(!result.IsError, Is.False);
        Assert.That(await File.ReadAllTextAsync(existingFile), Is.EqualTo(originalContent));
    }

    [Test]
    [Category("WorkspaceTools")] // sentinel:auto-category
    public async Task CreateFile_CsFileContentWithCompileError_IsRejectedAndNotWrittenAsync()
    {
        using var fixture = new TestSolutionFixture();
        using var workspaceManager = Basic.TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        await workspaceManager.LoadSolutionAsync(fixture.SolutionPath);
        var workspaceTools = BuildTools(workspaceManager);

        var newFile = Path.Combine(fixture.SolutionDirectory, "ContosoOrders.Core", "Broken.cs");
        var content = "public class Broken\n{\n    void M() { UndefinedThing.Call(); }\n}\n";

        var result = await workspaceTools.CreateFileTool(reason: "test message", newFile, content: content);

        Assert.That(!result.IsError, Is.False);
        Assert.That(result.ErrorData!.Message, Does.Contain("this content would introduce new compiler errors"));
        Assert.That(File.Exists(newFile), Is.False);
    }

    [Test]
    [Category("WorkspaceTools")] // sentinel:auto-category
    public async Task CreateFile_NonCsFileWithContent_WritesItAsync()
    {
        using var fixture = new TestSolutionFixture();
        using var workspaceManager = Basic.TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        // The content is written as LF, so the mode is pinned to Lf rather than the platform default.
        workspaceManager.EolModeOverride = EolMode.Lf;
        await workspaceManager.LoadSolutionAsync(fixture.SolutionPath);
        var workspaceTools = BuildTools(workspaceManager);

        var newFile = Path.Combine(fixture.SolutionDirectory, "Notes.txt");
        var content = "plain text notes\nsecond line\n";

        var result = await workspaceTools.CreateFileTool(reason: "test message", newFile, content: content);

        Assert.That(!result.IsError, Is.True, result.ErrorData?.Message);
        Assert.That(await File.ReadAllTextAsync(newFile), Is.EqualTo(content));
    }

    [Test]
    public async Task DeleteFile_ExistingFile_RemovesFromDiskAsync()
    {
        using var fixture = new TestSolutionFixture();
        using var workspaceManager = Basic.TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        await workspaceManager.LoadSolutionAsync(fixture.SolutionPath);
        var workspaceTools = BuildTools(workspaceManager);
        var diffEngine = Basic.TestEngines.DiffEngine();
        var validationEngine = Basic.TestEngines.ValidationEngine(workspaceManager, diffEngine, NullLogger<ValidationEngine>.Instance);
        WholeFileWriteTools wholeFileWriteTools = Basic.TestTools.WholeFileWriteTools(workspaceManager, workspaceTools, validationEngine, diffEngine, NullLogger<WholeFileWriteTools>.Instance, Basic.TestEngines.SymbolNavigationEngine(workspaceManager, NullLogger<SymbolNavigationEngine>.Instance));

        var newFile = Path.Combine(fixture.SolutionDirectory, "ToDelete.cs");
        await fixture.AddFileToSolution(workspaceManager, "ToDelete.cs", "public class ToDelete { }");

        var result = await wholeFileWriteTools.DeleteFileTool(reason: "test message", newFile);

        Assert.That(!result.IsError, Is.True);
        Assert.That(File.Exists(newFile), Is.False);
    }

    [Test]
    public async Task DeleteFile_NonExistentFile_FailsWithNotFoundAsync()
    {
        using var fixture = new TestSolutionFixture();
        using var workspaceManager = Basic.TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        await workspaceManager.LoadSolutionAsync(fixture.SolutionPath);
        var workspaceTools = BuildTools(workspaceManager);
        var diffEngine = Basic.TestEngines.DiffEngine();
        var validationEngine = Basic.TestEngines.ValidationEngine(workspaceManager, diffEngine, NullLogger<ValidationEngine>.Instance);
        WholeFileWriteTools wholeFileWriteTools = Basic.TestTools.WholeFileWriteTools(workspaceManager, workspaceTools, validationEngine, diffEngine, NullLogger<WholeFileWriteTools>.Instance, Basic.TestEngines.SymbolNavigationEngine(workspaceManager, NullLogger<SymbolNavigationEngine>.Instance));

        var missingFile = Path.Combine(fixture.SolutionDirectory, "DoesNotExist.cs");

        var result = await wholeFileWriteTools.DeleteFileTool(reason: "test message", missingFile);

        Assert.That(!result.IsError, Is.False);
        Assert.That(result.ErrorData!.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound));
        Assert.That(result.ErrorData!.Message, Does.Contain("does not exist"));
        Assert.That(result.ErrorData!.Message, Does.Contain("Search"));
    }

    [Test]
    [Category("WorkspaceTools")] // sentinel:auto-category
    public async Task DeleteFile_ThenUndoLastApply_RestoresFileAsync()
    {
        using var fixture = new TestSolutionFixture();
        using var workspaceManager = Basic.TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        await workspaceManager.LoadSolutionAsync(fixture.SolutionPath);
        var workspaceTools = BuildTools(workspaceManager);
        var diffEngine = Basic.TestEngines.DiffEngine();
        var validationEngine = Basic.TestEngines.ValidationEngine(workspaceManager, diffEngine, NullLogger<ValidationEngine>.Instance);
        WholeFileWriteTools wholeFileWriteTools = Basic.TestTools.WholeFileWriteTools(workspaceManager, workspaceTools, validationEngine, diffEngine, NullLogger<WholeFileWriteTools>.Instance, Basic.TestEngines.SymbolNavigationEngine(workspaceManager, NullLogger<SymbolNavigationEngine>.Instance));

        var newFile = Path.Combine(fixture.SolutionDirectory, "Undoable.cs");
        var content = "public class Undoable { }";
        await fixture.AddFileToSolution(workspaceManager, "Undoable.cs", content);

        var deleteResult = await wholeFileWriteTools.DeleteFileTool(reason: "test message", newFile);
        Assert.That(!deleteResult.IsError, Is.True);
        Assert.That(File.Exists(newFile), Is.False);

        // changeId isn't exposed on ApplyChangesResult directly -> recover it from the blob
        // filename ({toolName}_{timestamp}_{changeId}.json), same as UndoLastApplyTests does.
        var blobDir = Path.Combine(fixture.SolutionDirectory, ".roslynsentinel", "operations");
        var blobFile = Directory.EnumerateFiles(blobDir, "delete_file_*").OrderByDescending(f => f).First();
        var changeId = Path.GetFileNameWithoutExtension(blobFile).Split('_').Last();

        var undoResult = await workspaceTools.UndoLastApplyTool(reason: "test message", changeId);

        Assert.That(!undoResult.IsError, Is.True);
        Assert.That(File.Exists(newFile), Is.True);
        Assert.That(await File.ReadAllTextAsync(newFile), Is.EqualTo(content));
    }

    [Test]
    public async Task DeleteFile_DriftedFile_RefusesDeleteAsync()
    {
        using var fixture = new TestSolutionFixture();
        using var workspaceManager = Basic.TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        await workspaceManager.LoadSolutionAsync(fixture.SolutionPath);
        var workspaceTools = BuildTools(workspaceManager);
        var diffEngine = Basic.TestEngines.DiffEngine();
        var validationEngine = Basic.TestEngines.ValidationEngine(workspaceManager, diffEngine, NullLogger<ValidationEngine>.Instance);
        WholeFileWriteTools wholeFileWriteTools = Basic.TestTools.WholeFileWriteTools(workspaceManager, workspaceTools, validationEngine, diffEngine, NullLogger<WholeFileWriteTools>.Instance, Basic.TestEngines.SymbolNavigationEngine(workspaceManager, NullLogger<SymbolNavigationEngine>.Instance));

        var targetFile = Directory.EnumerateFiles(fixture.SolutionDirectory, "*.cs", SearchOption.AllDirectories).First();
        // Modify the file directly on disk (bypassing ApplyProposedChangesAsync) without
        // reloading/clearing drift, so DeleteFileTool sees it as externally modified since last sync.
        await fixture.ModifyFileInSolution(workspaceManager, Path.GetRelativePath(fixture.SolutionDirectory, targetFile), await File.ReadAllTextAsync(targetFile) + "\n// drift\n", reloadSolution: false);

        // ApplyProposedChangesAsync's drift check (GetExternalFileChanges()) reads a set populated
        // asynchronously by a FileSystemWatcher callback (OnFileSystemChanged), not synchronously by
        // the write above -> under heavy concurrent disk I/O from other test assemblies in a full
        // parallel run, the watcher event can lag past the point where DeleteFileTool below checks it,
        // making the delete wrongly succeed (root-caused 2026-09-06, was previously a documented
        // flake here). Poll for the watcher to actually report the drift before proceeding, instead
        // of assuming it already has.
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!workspaceManager.GetExternalFileChanges().Contains(targetFile, StringComparer.OrdinalIgnoreCase) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(25);
        }

        var result = await wholeFileWriteTools.DeleteFileTool(reason: "test message", targetFile);

        Assert.That(!result.IsError, Is.False);
        Assert.That(result.Message, Does.Contain("ExternalFileDrift"));
        Assert.That(File.Exists(targetFile), Is.True);
    }

    // Timestamp-only touch of a workspace document that has NO recorded baseline hash must not be
    // flagged as external drift (the flag would later latch SessionHalted on a write/delete of it).
    // The fixture cannot produce a baseline-less document naturally (PopulateKnownFileHashes hashes
    // every loaded document), so the baseline map is cleared via reflection to simulate one; the
    // production path under test (OnFileSystemChanged's no-baseline branch) is otherwise real.
    // A real content change afterwards must still be flagged, proving the watcher is live and the
    // fix does not suppress genuine drift.
    [Test]
    public async Task ExternalTouch_BaselinelessDocumentWithIdenticalContent_IsNotFlaggedAsDriftAsync()
    {
        using var fixture = new TestSolutionFixture();
        using var workspaceManager = Basic.TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        await workspaceManager.LoadSolutionAsync(fixture.SolutionPath);

        var document = workspaceManager.CurrentSolution!.Projects
            .SelectMany(p => p.Documents)
            .First(d => d.FilePath is not null && File.Exists(d.FilePath) && d.FilePath.StartsWith(fixture.SolutionDirectory, StringComparison.OrdinalIgnoreCase));
        var targetFile = document.FilePath!;
        // Materialize the document text so the production check (TryGetText) can compare it.
        await document.GetTextAsync();

        var hashes = (System.Collections.IDictionary)typeof(PersistentWorkspaceManager)
            .GetField("_knownFileHashes", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(workspaceManager)!;
        hashes.Clear();

        File.SetLastWriteTimeUtc(targetFile, DateTime.UtcNow.AddMinutes(-5));

        // Longer than the 500 ms debounce, so a flagged touch would certainly be recorded by now.
        await Task.Delay(2000);
        Assert.That(workspaceManager.GetExternalFileChanges(), Does.Not.Contain(targetFile).IgnoreCase);

        // Control: a real content change on the same baseline-less document is still flagged.
        await File.WriteAllTextAsync(targetFile, await File.ReadAllTextAsync(targetFile) + "\n// drift\n");
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!workspaceManager.GetExternalFileChanges().Contains(targetFile, StringComparer.OrdinalIgnoreCase) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(25);
        }

        Assert.That(workspaceManager.GetExternalFileChanges(), Does.Contain(targetFile).IgnoreCase);
    }

    // Project/solution files are watched (.csproj/.sln/.slnx) but are not documents; they used to have
    // no baseline hash, so a timestamp-only touch (identical content) was flagged as external drift.
    // PopulateKnownFileHashes now baselines them, so the touch must be suppressed. Real watcher, real
    // fixture files, no reflection: this covers the load-time baseline for BOTH the project file and the
    // solution file (the fixture solution is a .sln); the .slnx case is covered by the two *Slnx* tests below.
    [Test]
    public async Task ExternalTouch_ProjectAndSolutionFileWithIdenticalContent_IsNotFlaggedAsDriftAsync()
    {
        using var fixture = new TestSolutionFixture();
        using var workspaceManager = Basic.TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        await workspaceManager.LoadSolutionAsync(fixture.SolutionPath);

        var projectFile = workspaceManager.CurrentSolution!.Projects
            .Select(p => p.FilePath)
            .First(f => f is not null && File.Exists(f) && f.StartsWith(fixture.SolutionDirectory, StringComparison.OrdinalIgnoreCase))!;

        File.SetLastWriteTimeUtc(projectFile, DateTime.UtcNow.AddMinutes(-5));
        File.SetLastWriteTimeUtc(fixture.SolutionPath, DateTime.UtcNow.AddMinutes(-5));

        // Longer than the 500 ms debounce, so a flagged touch would certainly be recorded by now.
        await Task.Delay(2000);
        Assert.That(workspaceManager.GetExternalFileChanges(), Does.Not.Contain(projectFile).IgnoreCase);
        Assert.That(workspaceManager.GetExternalFileChanges(), Does.Not.Contain(fixture.SolutionPath).IgnoreCase);
    }

    // Control for the test above: a REAL content change to the .csproj must still be flagged, proving the
    // watcher is live and the new baseline does not suppress genuine project-file drift. The file is
    // restored afterwards (the fixture is a throwaway copy, but the restore keeps the test self-contained).
    [Test]
    public async Task ExternalEdit_ProjectFileWithChangedContent_IsFlaggedAsDriftAsync()
    {
        using var fixture = new TestSolutionFixture();
        using var workspaceManager = Basic.TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        await workspaceManager.LoadSolutionAsync(fixture.SolutionPath);

        var projectFile = workspaceManager.CurrentSolution!.Projects
            .Select(p => p.FilePath)
            .First(f => f is not null && File.Exists(f) && f.StartsWith(fixture.SolutionDirectory, StringComparison.OrdinalIgnoreCase))!;
        var originalContent = await File.ReadAllTextAsync(projectFile);

        try
        {
            // A trailing XML comment after the root element keeps the project file well-formed.
            await File.WriteAllTextAsync(projectFile, originalContent + "\n<!-- drift -->\n");
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (!workspaceManager.GetExternalFileChanges().Contains(projectFile, StringComparer.OrdinalIgnoreCase) && DateTime.UtcNow < deadline)
            {
                await Task.Delay(25);
            }

            Assert.That(workspaceManager.GetExternalFileChanges(), Does.Contain(projectFile).IgnoreCase);
        }
        finally
        {
            await File.WriteAllTextAsync(projectFile, originalContent);
        }
    }

    // Waits on the debounce-driven project reload (OnDebounceTimerElapsed), so it cannot run in parallel.
    // A real .csproj content change reloads the project and must re-baseline its hash, so a later
    // timestamp-only touch with identical content is not flagged as drift.
    [Test]
    [NonParallelizable]
    public async Task ExternalTouch_ProjectFileAfterDebounceReload_IsNotFlaggedAsDriftAsync()
    {
        using var fixture = new TestSolutionFixture();
        using var workspaceManager = Basic.TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        await workspaceManager.LoadSolutionAsync(fixture.SolutionPath);

        var projectFile = workspaceManager.CurrentSolution!.Projects
            .Select(p => p.FilePath)
            .First(f => f is not null && File.Exists(f) && f.StartsWith(fixture.SolutionDirectory, StringComparison.OrdinalIgnoreCase))!;
        var hashes = (System.Collections.IDictionary)typeof(PersistentWorkspaceManager)
            .GetField("_knownFileHashes", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(workspaceManager)!;
        var key = new RoslynSentinel.Common.FilePathWrapper(projectFile);

        static string HashOf(string content)
            => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(content)));

        var originalContent = await File.ReadAllTextAsync(projectFile);

        // A trailing XML comment after the root element keeps the project file well-formed.
        await File.WriteAllTextAsync(projectFile, originalContent + "\n<!-- drift -->\n");
        var editedContent = await File.ReadAllTextAsync(projectFile);
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!workspaceManager.GetExternalFileChanges().Contains(projectFile, StringComparer.OrdinalIgnoreCase) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(25);
        }

        Assert.That(workspaceManager.GetExternalFileChanges(), Does.Contain(projectFile).IgnoreCase);

        // Poll the baseline entry until the debounce reload records the edited content's hash.
        deadline = DateTime.UtcNow.AddSeconds(15);
        while ((hashes[key] as string) != HashOf(editedContent) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(25);
        }

        Assert.That((hashes[key] as string) == HashOf(editedContent), Is.True,
            "the debounce reload did not re-baseline the project file");

        // Drop the content-change flag, then touch the file without changing its content.
        workspaceManager.ClearExternalFileChanges(new[] { projectFile });
        File.SetLastWriteTimeUtc(projectFile, DateTime.UtcNow.AddMinutes(-5));

        // Longer than the 500 ms debounce, so a flagged touch would certainly be recorded by now.
        await Task.Delay(2000);
        Assert.That(workspaceManager.GetExternalFileChanges(), Does.Not.Contain(projectFile).IgnoreCase);
    }

    // .slnx counterpart of the .sln touch test above: a timestamp-only touch of a solution file with
    // identical content must not be flagged as drift. Covered together with the control test below,
    // which proves the watcher does report a real .slnx content change.
    [Test]
    [NonParallelizable]
    public async Task ExternalTouch_SlnxFileWithIdenticalContent_IsNotFlaggedAsDriftAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "RoslynSentinelSlnxTouch_" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);
        try
        {
            var slnxPath = WriteSlnxFixture(root);
            using var workspaceManager = Basic.TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
            await workspaceManager.LoadSolutionAsync(slnxPath);

            File.SetLastWriteTimeUtc(slnxPath, DateTime.UtcNow.AddMinutes(-5));

            // Longer than the 500 ms debounce, so a flagged touch would certainly be recorded by now.
            await Task.Delay(2000);
            Assert.That(workspaceManager.GetExternalFileChanges(), Does.Not.Contain(slnxPath).IgnoreCase);
        }
        finally
        {
            DeleteFixtureDirectory(root);
        }
    }

    // Control for the .slnx touch test above: a REAL content change to the solution file must be flagged,
    // the debounce-driven full-solution reload must re-baseline its hash, and a later touch with
    // identical content must then not be flagged.
    [Test]
    [NonParallelizable]
    public async Task ExternalEdit_SlnxFileWithChangedContent_IsFlaggedThenRebaselinedAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "RoslynSentinelSlnxEdit_" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);
        try
        {
            var slnxPath = WriteSlnxFixture(root);
            using var workspaceManager = Basic.TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
            await workspaceManager.LoadSolutionAsync(slnxPath);
            var hashes = (System.Collections.IDictionary)typeof(PersistentWorkspaceManager)
                .GetField("_knownFileHashes", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .GetValue(workspaceManager)!;
            var key = new RoslynSentinel.Common.FilePathWrapper(slnxPath);

            static string HashOf(string content)
                => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(content)));

            var originalContent = await File.ReadAllTextAsync(slnxPath);

            // A trailing XML comment after the root element keeps the solution file well-formed.
            await File.WriteAllTextAsync(slnxPath, originalContent + "\n<!-- drift -->\n");
            var editedContent = await File.ReadAllTextAsync(slnxPath);
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (!workspaceManager.GetExternalFileChanges().Contains(slnxPath, StringComparer.OrdinalIgnoreCase) && DateTime.UtcNow < deadline)
            {
                await Task.Delay(25);
            }

            Assert.That(workspaceManager.GetExternalFileChanges(), Does.Contain(slnxPath).IgnoreCase);

            // Poll the baseline entry until the debounce reload records the edited content's hash.
            deadline = DateTime.UtcNow.AddSeconds(15);
            while ((hashes[key] as string) != HashOf(editedContent) && DateTime.UtcNow < deadline)
            {
                await Task.Delay(25);
            }

            Assert.That((hashes[key] as string) == HashOf(editedContent), Is.True,
                "the debounce reload did not re-baseline the .slnx file");

            // Drop the content-change flag, then touch the file without changing its content.
            workspaceManager.ClearExternalFileChanges(new[] { slnxPath });
            File.SetLastWriteTimeUtc(slnxPath, DateTime.UtcNow.AddMinutes(-5));

            // Longer than the 500 ms debounce, so a flagged touch would certainly be recorded by now.
            await Task.Delay(2000);
            Assert.That(workspaceManager.GetExternalFileChanges(), Does.Not.Contain(slnxPath).IgnoreCase);
        }
        finally
        {
            DeleteFixtureDirectory(root);
        }
    }

    // A flagged real external edit is folded into the in-memory workspace by the watcher's debounce
    // (OnDebounceTimerElapsed -> ApplyInMemoryDocumentUpdatesAsync, the real route; nothing is called
    // directly). The fold must record the folded content's hash as the document's baseline so it is never
    // baseline-less afterwards.
    // What this really covers: (1) the discriminating assertion is the reflection check that
    // _knownFileHashes moves from the load-time hash to the hash of the folded (drifted) content; without
    // the fix it stays at the load-time hash. (2) The identical-content touch and (3) the further real edit
    // are behaviour pins that would ALSO pass without the fix in this fixture, because the folded document's
    // text is still resident (TryGetText true) so the older no-baseline branch decides them; the case the
    // fix exists for (folded text released by Roslyn, then a real edit) cannot be forced deterministically.
    [Test]
    public async Task ExternalEdit_FoldedDocument_GetsBaselineHashThenTouchIgnoredAndEditFlaggedAsync()
    {
        using var fixture = new TestSolutionFixture();
        using var workspaceManager = Basic.TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        await workspaceManager.LoadSolutionAsync(fixture.SolutionPath);

        var targetFile = workspaceManager.CurrentSolution!.Projects
            .SelectMany(p => p.Documents)
            .First(d => d.FilePath is not null && File.Exists(d.FilePath) && d.FilePath.StartsWith(fixture.SolutionDirectory, StringComparison.OrdinalIgnoreCase))
            .FilePath!;
        var hashes = (System.Collections.IDictionary)typeof(PersistentWorkspaceManager)
            .GetField("_knownFileHashes", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(workspaceManager)!;
        var key = new RoslynSentinel.Common.FilePathWrapper(targetFile);
        var loadTimeHash = (string)hashes[key]!;

        static string HashOf(string content)
            => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(content)));

        async Task<bool> WaitUntilAsync(Func<bool> condition, int seconds)
        {
            var deadline = DateTime.UtcNow.AddSeconds(seconds);
            while (!condition() && DateTime.UtcNow < deadline)
            {
                await Task.Delay(25);
            }

            return condition();
        }

        bool IsFlagged() => workspaceManager.GetExternalFileChanges().Contains(targetFile, StringComparer.OrdinalIgnoreCase);

        // Real external edit -> flagged, then folded in by the debounce.
        var foldedContent = await File.ReadAllTextAsync(targetFile) + "\n// drift 1\n";
        await File.WriteAllTextAsync(targetFile, foldedContent);
        Assert.That(await WaitUntilAsync(IsFlagged, 5), Is.True, "the real edit should be flagged");
        Assert.That(await WaitUntilAsync(() => hashes[key] as string == HashOf(foldedContent), 15), Is.True,
            "the fold should record the folded content's hash (load-time hash was " + loadTimeHash + ")");

        // The recoverable-text reasoning behind RecordFoldedDocumentBaseline: the fold stores constant text (WithDocumentText from a SourceText),
        // so the workspace document is resident/recoverable and equals the folded content - which is why the content hash is the correct baseline.
        var foldedDoc = workspaceManager.CurrentSolution!.Projects.SelectMany(p => p.Documents)
            .First(d => string.Equals(d.FilePath, targetFile, StringComparison.OrdinalIgnoreCase));
        Assert.That(foldedDoc.TryGetText(out var residentText), Is.True, "the folded document must hold constant, recoverable text");
        Assert.That(residentText!.ToString(), Is.EqualTo(foldedContent), "the recoverable text must be exactly the content the baseline hash was taken from");

        // Acknowledge the drift, as ExternalFileDrift(Acknowledge) does.
        workspaceManager.ClearExternalFileChanges([targetFile]);
        Assert.That(IsFlagged(), Is.False);

        // Identical-content touch of the folded document is not flagged.
        File.SetLastWriteTimeUtc(targetFile, DateTime.UtcNow.AddMinutes(-5));
        await Task.Delay(2000);
        Assert.That(IsFlagged(), Is.False);

        // A further real edit is flagged again.
        await File.WriteAllTextAsync(targetFile, foldedContent + "// drift 2\n");
        Assert.That(await WaitUntilAsync(IsFlagged, 5), Is.True, "a further real edit should be flagged again");
    }

    // CreateFileTool (the MCP tool, WorkspaceTools.CreateFileTool) -> distinct from
    // WriteFileTool(operation=CreateFileTool) exercised above; the two share a name coincidentally (tool
    // name vs WriteFileOperation enum member). This tool never accepts free-form content: for .cs
    // files, typeKind + typeName are mandatory (seeding one empty top-level type skeleton in a single
    // call, so a model can't forget them and need a second MemberTool(add) round-trip). namespaceName
    // is optional: null/whitespace means the global namespace, by design (e.g. an NUnit [SetUpFixture]).

    [Test]
    [Category("WorkspaceTools")] // sentinel:auto-category
    public async Task CreateFileTool_NoNamespaceForCsFile_CreatesGlobalNamespaceTypeAsync()
    {
        using var fixture = new TestSolutionFixture();
        using var workspaceManager = Basic.TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        await workspaceManager.LoadSolutionAsync(fixture.SolutionPath);
        var workspaceTools = BuildTools(workspaceManager);

        var newFile = Path.Combine(fixture.SolutionDirectory, "GlobalNamespace.cs");

        var result = await workspaceTools.CreateFileTool(reason: "test message", newFile, typeKind: NewTypeKind.@class, typeName: "Foo");

        Assert.That(result.IsError, Is.False);
        Assert.That(File.Exists(newFile), Is.True);
        var content = await File.ReadAllTextAsync(newFile);
        Assert.That(content, Does.Contain("public class Foo"));
        Assert.That(content, Does.Not.Contain("namespace"));
    }

    [TestCase(null, "Foo", TestName = "CreateFileTool_MissingTypeKindForCsFile_FailsAsync")]
    [TestCase(NewTypeKind.@class, null, TestName = "CreateFileTool_MissingTypeNameForCsFile_FailsAsync")]
    [Category("WorkspaceTools")] // sentinel:auto-category
    public async Task CreateFileTool_MissingTypeKindOrTypeNameForCsFile_FailsAsync(NewTypeKind? typeKind, string? typeName)
    {
        using var fixture = new TestSolutionFixture();
        using var workspaceManager = Basic.TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        await workspaceManager.LoadSolutionAsync(fixture.SolutionPath);
        var workspaceTools = BuildTools(workspaceManager);

        var newFile = Path.Combine(fixture.SolutionDirectory, "Bad.cs");

        var result = await workspaceTools.CreateFileTool(reason: "test message", newFile, namespaceName: "MyApp.Bad", typeKind: typeKind, typeName: typeName);

        Assert.That(!result.IsError, Is.False);
        Assert.That(result.ErrorData!.ErrorCode, Is.EqualTo(ToolErrorCode.InvalidArgument));
        Assert.That(File.Exists(newFile), Is.False);
    }

    [Test]
    [Category("WorkspaceTools")] // sentinel:auto-category
    public async Task CreateFileTool_AlreadyExists_FailsWithoutOverwritingAsync()
    {
        using var fixture = new TestSolutionFixture();
        using var workspaceManager = Basic.TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        await workspaceManager.LoadSolutionAsync(fixture.SolutionPath);
        var workspaceTools = BuildTools(workspaceManager);

        var existingFile = Directory.EnumerateFiles(fixture.SolutionDirectory, "*.cs", SearchOption.AllDirectories).First();
        var originalContent = await File.ReadAllTextAsync(existingFile);

        var result = await workspaceTools.CreateFileTool(reason: "test message", existingFile, namespaceName: "MyApp.Whatever", typeKind: NewTypeKind.@class, typeName: "Whatever");

        Assert.That(!result.IsError, Is.False);
        Assert.That(result.ErrorData!.ErrorCode, Is.EqualTo(ToolErrorCode.InvalidArgument));
        Assert.That(await File.ReadAllTextAsync(existingFile), Is.EqualTo(originalContent));
    }

    [Test]
    [Category("WorkspaceTools")] // sentinel:auto-category
    public async Task CreateFileTool_ParentDirectoryMissing_CreatesDirectoryAsync()
    {
        using var fixture = new TestSolutionFixture();
        using var workspaceManager = Basic.TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        await workspaceManager.LoadSolutionAsync(fixture.SolutionPath);
        var workspaceTools = BuildTools(workspaceManager);

        var newFile = Path.Combine(fixture.SolutionDirectory, "NewSubdir", "Nested.cs");

        var result = await workspaceTools.CreateFileTool(reason: "test message", newFile, namespaceName: "MyApp.Nested", typeKind: NewTypeKind.@class, typeName: "Nested");

        Assert.That(!result.IsError, Is.True, result.ErrorData?.Message);
        Assert.That(File.Exists(newFile), Is.True);
    }

    [Test]
    [Category("WorkspaceTools")] // sentinel:auto-category
    public async Task CreateFileTool_NonCsFile_SeedsEmptyContentIgnoringNamespaceAsync()
    {
        using var fixture = new TestSolutionFixture();
        using var workspaceManager = Basic.TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        await workspaceManager.LoadSolutionAsync(fixture.SolutionPath);
        var workspaceTools = BuildTools(workspaceManager);

        var newFile = Path.Combine(fixture.SolutionDirectory, "Notes.txt");

        var result = await workspaceTools.CreateFileTool(reason: "test message", newFile);

        Assert.That(!result.IsError, Is.True, result.ErrorData?.Message);
        Assert.That(File.Exists(newFile), Is.True);
        Assert.That(await File.ReadAllTextAsync(newFile), Is.EqualTo(""));
    }

    [TestCase(NewTypeKind.@class, "public class Foo\n{\n}\n")]
    [TestCase(NewTypeKind.record, "public record Foo\n{\n}\n")]
    [TestCase(NewTypeKind.@interface, "public interface Foo\n{\n}\n")]
    [TestCase(NewTypeKind.@enum, "public enum Foo\n{\n}\n")]
    [TestCase(NewTypeKind.@struct, "public struct Foo\n{\n}\n")]
    [TestCase(NewTypeKind.staticClass, "public static class Foo\n{\n}\n")]
    [Category("WorkspaceTools")] // sentinel:auto-category
    public async Task CreateFileTool_WithTypeKindAndName_SeedsTypeSkeletonAsync(NewTypeKind typeKind, string expectedTypeSource)
    {
        using var fixture = new TestSolutionFixture();
        using var workspaceManager = Basic.TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        // Explicit EOL mode for this manager.
        // The new file is expected as LF, so the mode is pinned to Lf rather than the platform default.
        workspaceManager.EolModeOverride = EolMode.Lf;
        await workspaceManager.LoadSolutionAsync(fixture.SolutionPath);
        var workspaceTools = BuildTools(workspaceManager);

        var newFile = Path.Combine(fixture.SolutionDirectory, $"Foo_{typeKind}.cs");

        var result = await workspaceTools.CreateFileTool(reason: "test message", newFile, namespaceName: "MyApp.Typed", typeKind: typeKind, typeName: "Foo");

        Assert.That(!result.IsError, Is.True, result.ErrorData?.Message);
        var content = await File.ReadAllTextAsync(newFile);
        Assert.That(content, Is.EqualTo("namespace MyApp.Typed;\n\n" + expectedTypeSource));

        var tree = Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(content);
        Assert.That(tree.GetDiagnostics().Where(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error), Is.Empty);
    }

    [Test]
    [Category("WorkspaceTools")] // sentinel:auto-category
    public async Task CreateFileTool_EditorConfigCrlf_AutoMode_WritesUniformCrlfAsync()
    {
        // Depends on EolPolicyOptions.Mode being Auto (its static default) and on no ROSLYNSENTINEL_EOL variable,
        // so EolModeOverride is deliberately left unset and the fixture's .editorconfig decides the EOL.
        // root = true stops the upward walk from picking up any .editorconfig above the fixture directory.
        using var fixture = new TestSolutionFixture();
        await File.WriteAllTextAsync(Path.Combine(fixture.SolutionDirectory, ".editorconfig"), "root = true\n\n[*.cs]\nend_of_line = crlf\n");
        using var workspaceManager = Basic.TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        await workspaceManager.LoadSolutionAsync(fixture.SolutionPath);
        var workspaceTools = BuildTools(workspaceManager);

        var newFile = Path.Combine(fixture.SolutionDirectory, "Foo.cs");
        var result = await workspaceTools.CreateFileTool(reason: "test message", newFile, namespaceName: "MyApp.Eol", typeKind: NewTypeKind.@class, typeName: "Foo");

        Assert.That(!result.IsError, Is.True, result.ErrorData?.Message);
        var bytes = await File.ReadAllBytesAsync(newFile);
        Assert.That(bytes.Where((b, i) => b == (byte)'\n' && (i == 0 || bytes[i - 1] != (byte)'\r')).Count(), Is.Zero, "no bare LF byte may survive");
        Assert.That(bytes.Contains((byte)'\r'), Is.True, "the file must be CRLF");
    }

    [Test]
    [Category("WorkspaceTools")] // sentinel:auto-category
    public async Task CreateFileTool_EditorConfigLf_AutoMode_WritesNoCrAsync()
    {
        // Same dependencies as the crlf variant: EolPolicyOptions.Mode stays Auto and ROSLYNSENTINEL_EOL is unset,
        // so the fixture's root = true .editorconfig (end_of_line = lf) is what decides the EOL.
        using var fixture = new TestSolutionFixture();
        await File.WriteAllTextAsync(Path.Combine(fixture.SolutionDirectory, ".editorconfig"), "root = true\n\n[*.cs]\nend_of_line = lf\n");
        using var workspaceManager = Basic.TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        await workspaceManager.LoadSolutionAsync(fixture.SolutionPath);
        var workspaceTools = BuildTools(workspaceManager);

        var newFile = Path.Combine(fixture.SolutionDirectory, "Foo.cs");
        var result = await workspaceTools.CreateFileTool(reason: "test message", newFile, namespaceName: "MyApp.Eol", typeKind: NewTypeKind.@class, typeName: "Foo");

        Assert.That(!result.IsError, Is.True, result.ErrorData?.Message);
        var bytes = await File.ReadAllBytesAsync(newFile);
        Assert.That(bytes.Contains((byte)'\r'), Is.False, "the file must be LF with no carriage return");
    }

    [Test]
    [Category("BasicRefactoringEngine")] // sentinel:auto-category
    [Category("MemberRefactoringEngine")] // sentinel:auto-category
    [Category("MsToolAugmentEngine")] // sentinel:auto-category
    [Category("RefactoringStructuralImpl")] // sentinel:auto-category
    [Category("RefactoringStructuralTools")] // sentinel:auto-category
    [Category("SemanticRefactoringEngine")] // sentinel:auto-category
    [Category("StructuralRefinementEngine")] // sentinel:auto-category
    [Category("WorkspaceTools")] // sentinel:auto-category
    public async Task CreateFileTool_ThenMemberAdd_PopulatesMembersAndAddsSecondTypeAsync()
    {
        // The actual end-to-end scenario the tool exists for: MemberTool/ModifyEnumTool/etc. all require
        // an already-existing Document, so a brand-new file could never be populated before
        // CreateFileTool existed. Confirms CreateFileTool's seeded type is a valid target for MemberTool(add)
        // to populate members into, AND that a second top-level type can be added afterward via
        // MemberTool(add, containerName: null, ...) -> the mandatory-typeKind design only seeds the
        // FIRST type; anything beyond that is deliberately a follow-up MemberTool(add) call.
        using var fixture = new TestSolutionFixture();
        using var workspaceManager = Basic.TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        await workspaceManager.LoadSolutionAsync(fixture.SolutionPath);
        var workspaceTools = BuildTools(workspaceManager);
        var config = Basic.TestEngines.SentinelConfiguration();
        var refactoringEngine = Basic.TestEngines.BasicRefactoringEngine(workspaceManager, NullLogger<BasicRefactoringEngine>.Instance, config);
        var semanticRefactoringEngine = Basic.TestEngines.SemanticRefactoringEngine(workspaceManager);
        var structuralRefinementEngine = Basic.TestEngines.StructuralRefinementEngine(workspaceManager, config);
        var msToolAugmentEngine = Basic.TestEngines.MsToolAugmentEngine(workspaceManager);
        var symbolNavigationEngine = Basic.TestEngines.SymbolNavigationEngine(workspaceManager, NullLogger<SymbolNavigationEngine>.Instance);
        var diffEngine = Basic.TestEngines.DiffEngine();
        var validationEngine = Basic.TestEngines.ValidationEngine(workspaceManager, diffEngine, NullLogger<ValidationEngine>.Instance);
        var memberRefactoringEngine = Basic.TestEngines.MemberRefactoringEngine(workspaceManager, symbolNavigationEngine, validationEngine);
        var refactoringStructuralTools = Basic.TestTools.RefactoringStructuralTools(Basic.TestTools.RefactoringStructuralImpl(
            refactoringEngine,
            memberRefactoringEngine,
            structuralRefinementEngine,
            symbolNavigationEngine,
            workspaceManager,
            validationEngine,
            NullLogger<RefactoringStructuralImpl>.Instance));

        var existingProjectDir = Path.GetDirectoryName(Directory.EnumerateFiles(fixture.SolutionDirectory, "*.csproj", SearchOption.AllDirectories).First())!;
        var newFile = Path.Combine(existingProjectDir, "Populated.cs");
        var createResult = await workspaceTools.CreateFileTool(reason: "test message", newFile, namespaceName: "MyApp.Populated", typeKind: NewTypeKind.@class, typeName: "Foo");
        Assert.That(!createResult.IsError, Is.True, createResult.ErrorData?.Message);

        // The new file isn't part of the loaded Roslyn solution until reloaded from disk -> CreateFileTool
        // (like WriteFileTool) writes through disk, it doesn't add a Document to the in-memory workspace itself.
        await workspaceManager.LoadSolutionAsync(fixture.SolutionPath);

        // Populate a member inside the type CreateFileTool seeded.
        var populateResult = await refactoringStructuralTools.MemberTool(
            reason: "test message",
            operation: MemberAction.addMember,
            filePath: newFile,
            containerName: "Foo",
            newMemberSource: "public int Value { get; set; }");
        Assert.That(!populateResult.IsError, Is.True, populateResult.ErrorData?.Message);

        // Add a second top-level type -> CreateFileTool only seeds the first.
        var secondTypeResult = await refactoringStructuralTools.MemberTool(
            reason: "test message",
            operation: MemberAction.addTopLevelType,
            filePath: newFile,
            newMemberSource: "public class Bar { }");
        Assert.That(!secondTypeResult.IsError, Is.True, secondTypeResult.ErrorData?.Message);

        var content = await File.ReadAllTextAsync(newFile);
        Assert.That(content, Does.Contain("namespace MyApp.Populated"));
        Assert.That(content, Does.Contain("public class Foo"));
        Assert.That(content, Does.Contain("public int Value"));
        Assert.That(content, Does.Contain("public class Bar"));
    }

    private static string WriteSlnxFixture(string root)
    {
        var slnxPath = Path.Combine(root, "SlnxFixture.slnx");
        File.WriteAllText(slnxPath, """
            <Solution>
              <Project Path="Lib/Lib.csproj" />
            </Solution>
            """);

        var directory = Path.Combine(root, "Lib");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "Lib.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
            </Project>
            """);
        File.WriteAllText(Path.Combine(directory, "LibValue.cs"), """
            namespace Slnx.Lib;

            public static class LibValue
            {
                public static string Name => "lib";
            }
            """);
        return slnxPath;
    }

    private static void DeleteFixtureDirectory(string root)
    {
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort temp cleanup; a watcher or build node may still hold a handle briefly.
        }
        catch (UnauthorizedAccessException)
        {
            // Best-effort temp cleanup.
        }
    }

    [Test]
    [Category("WorkspaceTools")] // sentinel:auto-category
    public async Task CreateFileTool_EolCreatorUnderCrlf_LfSeed_IsUniformCrlfAsync()
    {
        // Creators emit LF text. Under Crlf the write chokepoint must convert the new file to uniform CRLF.
        using var fixture = new TestSolutionFixture();
        using var workspaceManager = Basic.TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        workspaceManager.EolModeOverride = EolMode.Crlf;
        await workspaceManager.LoadSolutionAsync(fixture.SolutionPath);
        var workspaceTools = BuildTools(workspaceManager);

        var newFile = Path.Combine(fixture.SolutionDirectory, "EolCreatorCrlf.cs");
        var result = await workspaceTools.CreateFileTool(reason: "test message", newFile, namespaceName: "MyApp.Eol", typeKind: NewTypeKind.@class, typeName: "Foo");

        Assert.That(!result.IsError, Is.True, result.ErrorData?.Message);
        var bytes = await File.ReadAllBytesAsync(newFile);
        Assert.That(bytes.Where((b, i) => b == (byte)'\n' && (i == 0 || bytes[i - 1] != (byte)'\r')).Count(), Is.Zero, "no bare LF byte may survive");
        Assert.That(bytes.Contains((byte)'\r'), Is.True, "the file must be CRLF");
    }

    [Test]
    [Category("WorkspaceTools")] // sentinel:auto-category
    [Category("RefactoringStructuralTools")] // sentinel:auto-category
    public async Task MemberTool_AddTopLevelType_EolCreatorUnderCrlf_LfSource_IsUniformCrlfAsync()
    {
        // The LF source file is written before the solution loads. addTopLevelType must leave the file uniform CRLF.
        using var fixture = new TestSolutionFixture();
        using var workspaceManager = Basic.TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        workspaceManager.EolModeOverride = EolMode.Crlf;
        var existingProjectDir = Path.GetDirectoryName(Directory.EnumerateFiles(fixture.SolutionDirectory, "*.csproj", SearchOption.AllDirectories).First())!;
        var sourceFile = Path.Combine(existingProjectDir, "EolMemberSource.cs");
        await File.WriteAllTextAsync(sourceFile, "namespace MyApp.Eol;\npublic class Foo\n{\n}\n");
        await workspaceManager.LoadSolutionAsync(fixture.SolutionPath);

        var config = Basic.TestEngines.SentinelConfiguration();
        var refactoringEngine = Basic.TestEngines.BasicRefactoringEngine(workspaceManager, NullLogger<BasicRefactoringEngine>.Instance, config);
        var structuralRefinementEngine = Basic.TestEngines.StructuralRefinementEngine(workspaceManager, config);
        var symbolNavigationEngine = Basic.TestEngines.SymbolNavigationEngine(workspaceManager, NullLogger<SymbolNavigationEngine>.Instance);
        var validationEngine = Basic.TestEngines.ValidationEngine(workspaceManager, Basic.TestEngines.DiffEngine(), NullLogger<ValidationEngine>.Instance);
        var memberRefactoringEngine = Basic.TestEngines.MemberRefactoringEngine(workspaceManager, symbolNavigationEngine, validationEngine);
        var refactoringStructuralTools = Basic.TestTools.RefactoringStructuralTools(Basic.TestTools.RefactoringStructuralImpl(
            refactoringEngine,
            memberRefactoringEngine,
            structuralRefinementEngine,
            symbolNavigationEngine,
            workspaceManager,
            validationEngine,
            NullLogger<RefactoringStructuralImpl>.Instance));

        var result = await refactoringStructuralTools.MemberTool(
            reason: "test message",
            operation: MemberAction.addTopLevelType,
            filePath: sourceFile,
            newMemberSource: "public class Bar { }");

        Assert.That(!result.IsError, Is.True, result.ErrorData?.Message);
        var bytes = await File.ReadAllBytesAsync(sourceFile);
        Assert.That(bytes.Where((b, i) => b == (byte)'\n' && (i == 0 || bytes[i - 1] != (byte)'\r')).Count(), Is.Zero, "no bare LF byte may survive");
        Assert.That(bytes.Contains((byte)'\r'), Is.True, "the file must be CRLF");
    }

    [Test]
    public async Task ReplaceFile_MixedPreImage_UnderCrlf_RecordsOneWriteChokepointFindingAsync()
    {
        // The pre-image on disk mixes CRLF and LF. The chokepoint must still apply the write and attribute the mixed pre-image in one envelope finding.
        using var fixture = new TestSolutionFixture();
        using var workspaceManager = Basic.TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        workspaceManager.EolModeOverride = EolMode.Crlf;
        var targetFile = Path.Combine(fixture.SolutionDirectory, "EolMixedPreImage.cs");
        await File.WriteAllTextAsync(targetFile, "namespace MyApp.Eol;\r\npublic class Foo\n{\r\n}\n");
        await workspaceManager.LoadSolutionAsync(fixture.SolutionPath);
        var workspaceTools = BuildTools(workspaceManager);
        var diffEngine = Basic.TestEngines.DiffEngine();
        var validationEngine = Basic.TestEngines.ValidationEngine(workspaceManager, diffEngine, NullLogger<ValidationEngine>.Instance);
        var wholeFileWriteTools = Basic.TestTools.WholeFileWriteTools(workspaceManager, workspaceTools, validationEngine, diffEngine, NullLogger<WholeFileWriteTools>.Instance, Basic.TestEngines.SymbolNavigationEngine(workspaceManager, NullLogger<SymbolNavigationEngine>.Instance));

        // validateOnApply: false -> the replacement is not a valid compilation unit for this fixture; this test only checks the EOL finding.
        var result = await wholeFileWriteTools.WriteFileTool(reason: "test message", WriteFileOperation.ReplaceFile, targetFile, "public class Replaced { }\n", validateOnApply: false);

        Assert.That(!result.IsError, Is.True, result.ErrorData?.Message);
        // The unobserved-target finding is excluded because these tests cover EOL attribution only.
        var eolFindings = (result.Findings ?? []).Where(f => !f.Message.Contains("read them first")).ToList();
        Assert.That(eolFindings.Count, Is.EqualTo(1));
        Assert.That(eolFindings[0].Source, Is.EqualTo("WriteChokepoint"));
    }

    [Test]
    public async Task ReplaceFile_UniformCrlfPreImage_UnderCrlf_RecordsNoFindingsAsync()
    {
        // A uniform CRLF pre-image already matches the resolved target, so the chokepoint has nothing to attribute.
        using var fixture = new TestSolutionFixture();
        using var workspaceManager = Basic.TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        workspaceManager.EolModeOverride = EolMode.Crlf;
        var targetFile = Path.Combine(fixture.SolutionDirectory, "EolUniformCrlfPreImage.cs");
        await File.WriteAllTextAsync(targetFile, "namespace MyApp.Eol;\r\npublic class Foo\r\n{\r\n}\r\n");
        await workspaceManager.LoadSolutionAsync(fixture.SolutionPath);
        var workspaceTools = BuildTools(workspaceManager);
        var diffEngine = Basic.TestEngines.DiffEngine();
        var validationEngine = Basic.TestEngines.ValidationEngine(workspaceManager, diffEngine, NullLogger<ValidationEngine>.Instance);
        var wholeFileWriteTools = Basic.TestTools.WholeFileWriteTools(workspaceManager, workspaceTools, validationEngine, diffEngine, NullLogger<WholeFileWriteTools>.Instance, Basic.TestEngines.SymbolNavigationEngine(workspaceManager, NullLogger<SymbolNavigationEngine>.Instance));

        // validateOnApply: false -> the replacement is not a valid compilation unit for this fixture; this test only checks the EOL finding.
        var result = await wholeFileWriteTools.WriteFileTool(reason: "test message", WriteFileOperation.ReplaceFile, targetFile, "public class Replaced { }\n", validateOnApply: false);

        Assert.That(!result.IsError, Is.True, result.ErrorData?.Message);
        // The unobserved-target finding is excluded because these tests cover EOL attribution only.
        var eolFindings = (result.Findings ?? []).Where(f => !f.Message.Contains("read them first")).ToList();
        Assert.That(eolFindings, Is.Empty);
    }

    [Test]
    [Category("WorkspaceTools")] // sentinel:auto-category
    [Category("RefactoringStructuralTools")] // sentinel:auto-category
    public async Task MemberTool_AddTopLevelType_MixedPreImageUnderCrlf_FindingReachesToolResponseAsync()
    {
        // The AppliedChangeSummary the tool builds by hand must carry ApplyOutcome.Findings so the chokepoint attribution reaches the response envelope.
        using var fixture = new TestSolutionFixture();
        using var workspaceManager = Basic.TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        workspaceManager.EolModeOverride = EolMode.Crlf;
        var existingProjectDir = Path.GetDirectoryName(Directory.EnumerateFiles(fixture.SolutionDirectory, "*.csproj", SearchOption.AllDirectories).First())!;
        var sourceFile = Path.Combine(existingProjectDir, "EolMemberFindingSource.cs");
        await File.WriteAllTextAsync(sourceFile, "namespace MyApp.Eol;\r\npublic class Foo\n{\r\n}\n");
        await workspaceManager.LoadSolutionAsync(fixture.SolutionPath);

        var config = Basic.TestEngines.SentinelConfiguration();
        var refactoringEngine = Basic.TestEngines.BasicRefactoringEngine(workspaceManager, NullLogger<BasicRefactoringEngine>.Instance, config);
        var structuralRefinementEngine = Basic.TestEngines.StructuralRefinementEngine(workspaceManager, config);
        var symbolNavigationEngine = Basic.TestEngines.SymbolNavigationEngine(workspaceManager, NullLogger<SymbolNavigationEngine>.Instance);
        var validationEngine = Basic.TestEngines.ValidationEngine(workspaceManager, Basic.TestEngines.DiffEngine(), NullLogger<ValidationEngine>.Instance);
        var memberRefactoringEngine = Basic.TestEngines.MemberRefactoringEngine(workspaceManager, symbolNavigationEngine, validationEngine);
        var refactoringStructuralTools = Basic.TestTools.RefactoringStructuralTools(Basic.TestTools.RefactoringStructuralImpl(
            refactoringEngine,
            memberRefactoringEngine,
            structuralRefinementEngine,
            symbolNavigationEngine,
            workspaceManager,
            validationEngine,
            NullLogger<RefactoringStructuralImpl>.Instance));

        var result = await refactoringStructuralTools.MemberTool(
            reason: "test message",
            operation: MemberAction.addTopLevelType,
            filePath: sourceFile,
            newMemberSource: "public class Bar { }");

        Assert.That(!result.IsError, Is.True, result.ErrorData?.Message);
        Assert.That(result.Findings.Any(f => f.Source == "WriteChokepoint"), Is.True, "the mixed pre-image finding must reach the Member tool response");
    }

    [Test]
    [Category("WorkspaceTools")] // sentinel:auto-category
    public async Task DeleteUnusedSymbol_MixedPreImageUnderCrlf_FindingReachesToolResponseAsync()
    {
        // DeleteUnusedSymbol builds its AppliedChangeSummary by hand; it must carry ApplyOutcome.Findings so the chokepoint attribution reaches the envelope.
        using var fixture = new TestSolutionFixture();
        using var workspaceManager = Basic.TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        workspaceManager.EolModeOverride = EolMode.Crlf;
        var existingProjectDir = Path.GetDirectoryName(Directory.EnumerateFiles(fixture.SolutionDirectory, "*.csproj", SearchOption.AllDirectories).First())!;
        var sourceFile = Path.Combine(existingProjectDir, "EolDeleteUnusedFindingSource.cs");
        await File.WriteAllTextAsync(sourceFile, "namespace MyApp.Eol;\r\npublic class Foo\n{\r\n    private int DeadHelper() { return 1; }\n}\r\n");
        await workspaceManager.LoadSolutionAsync(fixture.SolutionPath);
        var workspaceTools = BuildTools(workspaceManager);

        var result = await workspaceTools.DeleteUnusedSymbolTool(reason: "test message", sourceFile, symbolName: "DeadHelper");

        Assert.That(!result.IsError, Is.True, result.ErrorData?.Message);
        Assert.That(result.Findings.Any(f => f.Source == "WriteChokepoint"), Is.True, "the mixed pre-image finding must reach the DeleteUnusedSymbol tool response");
    }

    private static WholeFileWriteTools BuildWholeFileWriteTools(IWorkspaceManager workspaceManager)
    {
        var workspaceTools = BuildTools(workspaceManager);
        var diffEngine = Basic.TestEngines.DiffEngine();
        var validationEngine = Basic.TestEngines.ValidationEngine(workspaceManager, diffEngine, NullLogger<ValidationEngine>.Instance);
        return Basic.TestTools.WholeFileWriteTools(workspaceManager, workspaceTools, validationEngine, diffEngine, NullLogger<WholeFileWriteTools>.Instance, Basic.TestEngines.SymbolNavigationEngine(workspaceManager, NullLogger<SymbolNavigationEngine>.Instance));
    }

    [Test]
    [Category("WholeFileWriteTools")] // sentinel:auto-category
    public async Task ApplyUnifiedDiff_MixedPreImageUnderCrlf_ChokepointAndHunkFindingsBothReachToolResponseAsync()
    {
        // ApplyUnifiedDiff sets explicit envelope findings (hunk analyzer); that list replaces the AppliedChangeSummary lift,
        // so the chokepoint finding must be merged in explicitly. The "\ No newline" marker line is an unrecognized hunk line -> a DiffHunkAnalyzer finding.
        using var fixture = new TestSolutionFixture();
        using var workspaceManager = Basic.TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        workspaceManager.EolModeOverride = EolMode.Crlf;
        var existingProjectDir = Path.GetDirectoryName(Directory.EnumerateFiles(fixture.SolutionDirectory, "*.csproj", SearchOption.AllDirectories).First())!;
        var sourceFile = Path.Combine(existingProjectDir, "EolUnifiedDiffFindingSource.cs");
        await File.WriteAllTextAsync(sourceFile, "namespace MyApp.Eol;\r\npublic class Foo\n{\r\n}\n");
        await workspaceManager.LoadSolutionAsync(fixture.SolutionPath);
        var wholeFileWriteTools = BuildWholeFileWriteTools(workspaceManager);
        var diff = "--- a/EolUnifiedDiffFindingSource.cs\n+++ b/EolUnifiedDiffFindingSource.cs\n@@ -1,4 +1,4 @@\n namespace MyApp.Eol;\n-public class Foo\n+public class Foo2\n {\n }\n\\ No newline at end of file\n";

        var result = await wholeFileWriteTools.ApplyUnifiedDiffTool(reason: "test message", ProposedChangeAction.apply, sourceFile, diff);

        Assert.That(!result.IsError, Is.True, result.ErrorData?.Message);
        Assert.That(result.Findings.Count(f => f.Source == "WriteChokepoint"), Is.EqualTo(1), "the mixed pre-image finding must reach the ApplyUnifiedDiff response exactly once");
        Assert.That(result.Findings.Count(f => f.Source == "DiffHunkAnalyzer"), Is.EqualTo(1), "the existing hunk-analyzer finding must still be present");
    }

    [Test]
    [Category("WholeFileWriteTools")] // sentinel:auto-category
    public async Task ApplyDiff_DiffFormat_MixedPreImageUnderCrlf_FindingReachesToolResponseAsync()
    {
        // ApplyDiff's diff branch returns an ApplyChangesResult payload (not an AppliedChangeSummary), so the envelope Findings must be set explicitly from result.Findings.
        using var fixture = new TestSolutionFixture();
        using var workspaceManager = Basic.TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        workspaceManager.EolModeOverride = EolMode.Crlf;
        var existingProjectDir = Path.GetDirectoryName(Directory.EnumerateFiles(fixture.SolutionDirectory, "*.csproj", SearchOption.AllDirectories).First())!;
        var sourceFile = Path.Combine(existingProjectDir, "EolApplyDiffFindingSource.cs");
        await File.WriteAllTextAsync(sourceFile, "namespace MyApp.Eol;\r\npublic class Foo\n{\r\n}\n");
        await workspaceManager.LoadSolutionAsync(fixture.SolutionPath);
        var wholeFileWriteTools = BuildWholeFileWriteTools(workspaceManager);
        var diff = "--- a/EolApplyDiffFindingSource.cs\n+++ b/EolApplyDiffFindingSource.cs\n@@ -1,4 +1,4 @@\n namespace MyApp.Eol;\n-public class Foo\n+public class Foo2\n {\n }\n";

        var result = await wholeFileWriteTools.ApplyDiffTool(reason: "test message", ChangesetFormat.diff, ProposedChangeAction.apply, filePath: sourceFile, unifiedDiff: diff);

        Assert.That(!result.IsError, Is.True, result.ErrorData?.Message);
        Assert.That(result.Findings.Any(f => f.Source == "WriteChokepoint"), Is.True, "the mixed pre-image finding must reach the ApplyDiff (diff format) response");
    }

    [Test]
    [Category("WholeFileWriteTools")] // sentinel:auto-category
    public async Task ApplyDiff_DiffFormat_UnrecognizedHunkLine_HunkWarningReachesToolResponseAsync()
    {
        // ApplyDiff's diff branch builds its envelope Findings from the diff engine report too, so the DiffHunkAnalyzer warning must be merged in explicitly. The "\ No newline" marker line is an unrecognized hunk line -> a DiffHunkAnalyzer finding.
        using var fixture = new TestSolutionFixture();
        using var workspaceManager = Basic.TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        workspaceManager.EolModeOverride = EolMode.Crlf;
        var existingProjectDir = Path.GetDirectoryName(Directory.EnumerateFiles(fixture.SolutionDirectory, "*.csproj", SearchOption.AllDirectories).First())!;
        var sourceFile = Path.Combine(existingProjectDir, "EolApplyDiffHunkWarningSource.cs");
        await File.WriteAllTextAsync(sourceFile, "namespace MyApp.Eol;\r\npublic class Foo\n{\r\n}\n");
        await workspaceManager.LoadSolutionAsync(fixture.SolutionPath);
        var wholeFileWriteTools = BuildWholeFileWriteTools(workspaceManager);
        var diff = "--- a/EolApplyDiffHunkWarningSource.cs\n+++ b/EolApplyDiffHunkWarningSource.cs\n@@ -1,4 +1,4 @@\n namespace MyApp.Eol;\n-public class Foo\n+public class Foo2\n {\n }\n\\ No newline at end of file\n";

        var result = await wholeFileWriteTools.ApplyDiffTool(reason: "test message", ChangesetFormat.diff, ProposedChangeAction.apply, filePath: sourceFile, unifiedDiff: diff);

        Assert.That(!result.IsError, Is.True, result.ErrorData?.Message);
        Assert.That(result.Findings.Count(f => f.Source == "DiffHunkAnalyzer"), Is.EqualTo(1), "the hunk-analyzer warning must reach the ApplyDiff response");
    }
}
