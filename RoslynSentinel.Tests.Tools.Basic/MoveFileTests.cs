using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Engines.Basic;
using RoslynSentinel.Tools.Basic;

namespace RoslynSentinel.Tests.Tools.Basic;

// MoveFile (WholeFileWriteTools.MoveFileTool), slice 11-1 (namespaceHandling keep): the move runs through ValidateAndApplyAsync as one
// change {destination: text} + deletePaths [source], so it is compile-gated, undoable and recorded in one operation blob. Refusals are
// facts-only and leave every file as it was. Runs on a real PersistentWorkspaceManager over a TestSolutionFixture copy of ContosoOrders
// (two projects: ContosoOrders.Core and ContosoOrders.Tests). Byte identity (BOM, LF-only, mixed EOL) is covered by the *_KeepsTheBytesExact tests (slice 11-3, the chokepoint's verbatim parameter).
[TestFixture]
[Parallelizable(ParallelScope.All)]
[Category("WholeFileWriteTools")]
public sealed class MoveFileTests
{
    private const string CoreFolder = "ContosoOrders.Core";
    private const string TestsFolder = "ContosoOrders.Tests";

    private static async Task<PersistentWorkspaceManager> LoadAsync(TestSolutionFixture fixture)
    {
        var manager = Battery.Basic.TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        await manager.LoadSolutionAsync(fixture.SolutionPath, null, TestSolutionFixture.LoadTimeout);
        return manager;
    }

    private static WholeFileWriteTools BuildTools(IWorkspaceManager manager, WorkspaceTools workspaceTools)
    {
        var diffEngine = Battery.Basic.TestEngines.DiffEngine();
        var validationEngine = Battery.Basic.TestEngines.ValidationEngine(manager, diffEngine, NullLogger<ValidationEngine>.Instance);
        return Battery.Basic.TestTools.WholeFileWriteTools(manager, workspaceTools, validationEngine, diffEngine, NullLogger<WholeFileWriteTools>.Instance, Battery.Basic.TestEngines.SymbolNavigationEngine(manager, NullLogger<SymbolNavigationEngine>.Instance));
    }

    private static WholeFileWriteTools BuildTools(IWorkspaceManager manager) => BuildTools(manager, Battery.Basic.TestTools.WorkspaceTools(manager));

    private static Task<SentinelCallToolResult<AppliedChangeSummary>> MoveAsync(WholeFileWriteTools tools, string source, string destination, MoveFileNamespaceHandling handling) =>
        tools.MoveFileTool(reason: "test message", source, destination, handling);

    private static string PathIn(TestSolutionFixture fixture, params string[] segments) =>
        Path.Combine(new[] { fixture.SolutionDirectory }.Concat(segments).ToArray());

    private static void AssertRefused(SentinelCallToolResult<AppliedChangeSummary> result, string expectedCode, params string[] expectedFragments)
    {
        Assert.That(result.IsError, Is.True, "The move should have been refused.");
        Assert.That(result.ErrorData!.ErrorCode, Is.EqualTo(expectedCode));
        foreach (var fragment in expectedFragments)
        {
            Assert.That(result.ErrorData.Message, Does.Contain(fragment));
        }

        // Facts and options only: no policy words (CLAUDE.md, tool error text).
        Assert.That(result.ErrorData.Message, Does.Not.Contain("MUST"));
        Assert.That(result.ErrorData.Message, Does.Not.Contain("stop").IgnoreCase);
        Assert.That(result.ErrorData.Message, Does.Not.Contain("do not").IgnoreCase);
    }

    [Test]
    public async Task MoveFile_CsFileInProject_MovesTheTextAndPassesTheCompileGateAsync()
    {
        using var fixture = new TestSolutionFixture();
        using var manager = await LoadAsync(fixture);
        var tools = BuildTools(manager);
        const string text = "namespace ContosoOrders.Core;\r\n\r\npublic class Movable\r\n{\r\n}\r\n";
        await fixture.AddFileToSolution(manager, Path.Combine(CoreFolder, "Movable.cs"), text);
        var source = PathIn(fixture, CoreFolder, "Movable.cs");
        var destination = PathIn(fixture, CoreFolder, "Sub", "Movable.cs");

        var result = await MoveAsync(tools, source, destination, MoveFileNamespaceHandling.keep);

        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        Assert.That(File.Exists(source), Is.False, "The source is gone.");
        Assert.That(File.ReadAllText(destination), Is.EqualTo(text), "The destination holds the same text.");
        var summary = result.SuccessData!;
        Assert.That(summary.Status, Is.EqualTo("applied"));
        Assert.That(summary.ChangeId, Is.Not.Null.And.Not.Empty);
        Assert.That(summary.Description, Does.Contain("Moved"));
        Assert.That(summary.Description, Does.Contain(CoreFolder), "The project is named.");
        // The destination folder (Sub) makes the folder-derived namespace ContosoOrders.Core.Sub, which differs from the declared one.
        Assert.That(summary.Description, Does.Contain("The namespace 'ContosoOrders.Core' was kept"));
        Assert.That(summary.Note, Does.Contain("UndoChange"), "The undo hint is carried.");
    }

    [Test]
    public async Task MoveFile_CsFileWhoseNamespaceMatchesTheDestinationFolder_SaysNothingAboutTheNamespaceAsync()
    {
        using var fixture = new TestSolutionFixture();
        using var manager = await LoadAsync(fixture);
        var tools = BuildTools(manager);
        await fixture.AddFileToSolution(manager, Path.Combine(CoreFolder, "Renamed.cs"), "namespace ContosoOrders.Core;\r\n\r\npublic class Renamed\r\n{\r\n}\r\n");

        var result = await MoveAsync(tools, PathIn(fixture, CoreFolder, "Renamed.cs"), PathIn(fixture, CoreFolder, "RenamedAgain.cs"), MoveFileNamespaceHandling.keep);

        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        Assert.That(result.SuccessData!.Description, Does.Not.Contain("was kept"));
    }

    [Test]
    public async Task MoveFile_CsFileAcrossProjects_MovesItAndNamesBothProjectsAsync()
    {
        // Untested at write time (the slice was built without running tests): the Tests project has no restore assets in this fixture,
        // so the compile gate must tolerate its pre-existing errors.
        using var fixture = new TestSolutionFixture();
        using var manager = await LoadAsync(fixture);
        var tools = BuildTools(manager);
        const string text = "namespace ContosoOrders.Core;\r\n\r\npublic class CrossMover\r\n{\r\n}\r\n";
        await fixture.AddFileToSolution(manager, Path.Combine(CoreFolder, "CrossMover.cs"), text);
        var source = PathIn(fixture, CoreFolder, "CrossMover.cs");
        var destination = PathIn(fixture, TestsFolder, "CrossMover.cs");

        var result = await MoveAsync(tools, source, destination, MoveFileNamespaceHandling.keep);

        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        Assert.That(File.Exists(source), Is.False);
        Assert.That(File.ReadAllText(destination), Is.EqualTo(text));
        Assert.That(result.SuccessData!.Description, Does.Contain($"project '{CoreFolder}'"));
        Assert.That(result.SuccessData.Description, Does.Contain($"project '{TestsFolder}'"));
        Assert.That(result.SuccessData.Description, Does.Contain("was kept"));
    }

    [Test]
    public async Task MoveFile_NonCsFile_MovesItAndCreatesTheDestinationFolderAsync()
    {
        using var fixture = new TestSolutionFixture();
        using var manager = await LoadAsync(fixture);
        var tools = BuildTools(manager);
        const string text = "# Note\r\n\r\nSome text.\r\n";
        await fixture.AddFileToSolution(manager, Path.Combine("docs", "Note.md"), text);
        var source = PathIn(fixture, "docs", "Note.md");
        var destination = PathIn(fixture, "docs", "archive", "Note.md");

        var result = await MoveAsync(tools, source, destination, MoveFileNamespaceHandling.keep);

        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        Assert.That(File.Exists(source), Is.False);
        Assert.That(File.ReadAllText(destination), Is.EqualTo(text));
        Assert.That(result.SuccessData!.Description, Does.Not.Contain("was kept"), "The namespace fact is for .cs files only.");
    }

    // Seeds the file with exactly `text` (a leading U+FEFF is written as the UTF-8 BOM), records its bytes, moves it and asserts the destination bytes
    // are identical and the source is gone: MoveFile writes through the chokepoint with verbatim, so no EOL or BOM normalization applies.
    private static async Task AssertMoveKeepsBytesAsync(string relativeSource, string relativeDestination, string text)
    {
        using var fixture = new TestSolutionFixture();
        using var manager = await LoadAsync(fixture);
        var tools = BuildTools(manager);
        await fixture.AddFileToSolution(manager, relativeSource, text);
        var source = Path.Combine(fixture.SolutionDirectory, relativeSource);
        var destination = Path.Combine(fixture.SolutionDirectory, relativeDestination);
        var originalBytes = File.ReadAllBytes(source);

        var result = await MoveAsync(tools, source, destination, MoveFileNamespaceHandling.keep);

        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        Assert.That(File.Exists(source), Is.False, "The source is gone.");
        Assert.That(File.ReadAllBytes(destination), Is.EqualTo(originalBytes), "The destination holds the original bytes exactly.");
    }

    [Test]
    public async Task MoveFile_CsFileWithBomAndCrlf_KeepsTheBytesExactAsync()
    {
        const string text = "﻿namespace ContosoOrders.Core;\r\n\r\npublic class BomMover\r\n{\r\n}\r\n";

        await AssertMoveKeepsBytesAsync(Path.Combine(CoreFolder, "BomMover.cs"), Path.Combine(CoreFolder, "Sub", "BomMover.cs"), text);
    }

    [Test]
    public async Task MoveFile_CsFileWithLfOnly_KeepsTheBytesExactAsync()
    {
        const string text = "namespace ContosoOrders.Core;\n\npublic class LfMover\n{\n}\n";

        await AssertMoveKeepsBytesAsync(Path.Combine(CoreFolder, "LfMover.cs"), Path.Combine(CoreFolder, "Sub", "LfMover.cs"), text);
    }

    [Test]
    public async Task MoveFile_TextFileWithMixedEol_KeepsTheBytesExactAsync()
    {
        const string text = "first\r\nsecond\nthird\r\nfourth\n";

        await AssertMoveKeepsBytesAsync(Path.Combine("docs", "mixed.txt"), Path.Combine("docs", "archive", "mixed.txt"), text);
    }

    [Test]
    public async Task MoveFile_Apply_WritesOneOperationBlobNamingBothPathsAsync()
    {
        using var fixture = new TestSolutionFixture();
        using var manager = await LoadAsync(fixture);
        var tools = BuildTools(manager);
        await fixture.AddFileToSolution(manager, Path.Combine(CoreFolder, "BlobFrom.cs"), "namespace ContosoOrders.Core;\r\n\r\npublic class Payload\r\n{\r\n}\r\n");

        var result = await MoveAsync(tools, PathIn(fixture, CoreFolder, "BlobFrom.cs"), PathIn(fixture, CoreFolder, "Moved", "BlobTo.cs"), MoveFileNamespaceHandling.keep);

        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        var changeId = result.SuccessData!.ChangeId;
        Assert.That(changeId, Is.Not.Null.And.Not.Empty);
        var blobPath = OperationBlobWriter.FindBlobPath(changeId!, fixture.SolutionDirectory);
        Assert.That(blobPath, Is.Not.Null);
        var blob = File.ReadAllText(blobPath!);
        Assert.That(blob, Does.Contain("BlobFrom.cs"), "The deleted source is in the blob.");
        Assert.That(blob, Does.Contain("BlobTo.cs"), "The written destination is in the blob.");
    }

    [Test]
    public async Task MoveFile_ThenUndoChange_RestoresTheSourceAndRemovesTheDestinationAsync()
    {
        // Untested at write time (the slice was built without running tests): relies on UndoChange deleting a file the change created.
        using var fixture = new TestSolutionFixture();
        using var manager = await LoadAsync(fixture);
        var workspaceTools = Battery.Basic.TestTools.WorkspaceTools(manager);
        var tools = BuildTools(manager, workspaceTools);
        const string text = "namespace ContosoOrders.Core;\r\n\r\npublic class Undoable\r\n{\r\n}\r\n";
        await fixture.AddFileToSolution(manager, Path.Combine(CoreFolder, "UndoFrom.cs"), text);
        var source = PathIn(fixture, CoreFolder, "UndoFrom.cs");
        var destination = PathIn(fixture, CoreFolder, "UndoTo.cs");
        var moved = await MoveAsync(tools, source, destination, MoveFileNamespaceHandling.keep);
        Assert.That(moved.IsError, Is.False, moved.ErrorData?.Message);

        var undone = await workspaceTools.UndoChangeTool(reason: "test message", moved.SuccessData!.ChangeId!);

        Assert.That(undone.IsError, Is.False, undone.ErrorData?.Message);
        Assert.That(File.ReadAllText(source), Is.EqualTo(text));
        Assert.That(File.Exists(destination), Is.False);
    }

    [Test]
    public async Task MoveFile_Keep_StoresTheTextOnceInTheBlob_AndUndoChangeRestoresTheSourceAsync()
    {
        // A keep move's destination carries the source's text by reference: the blob stores the text once
        // (on the deleted source) and the destination names that source in AfterSourceFromFilePath.
        using var fixture = new TestSolutionFixture();
        using var manager = await LoadAsync(fixture);
        var workspaceTools = Battery.Basic.TestTools.WorkspaceTools(manager);
        var tools = BuildTools(manager, workspaceTools);
        const string text = "namespace ContosoOrders.Core;\r\n\r\n// STORED_ONCE_MARKER\r\npublic class StoredOnce\r\n{\r\n}\r\n";
        await fixture.AddFileToSolution(manager, Path.Combine(CoreFolder, "StoredFrom.cs"), text);
        var source = PathIn(fixture, CoreFolder, "StoredFrom.cs");
        var destination = PathIn(fixture, CoreFolder, "Moved", "StoredTo.cs");

        var moved = await MoveAsync(tools, source, destination, MoveFileNamespaceHandling.keep);

        Assert.That(moved.IsError, Is.False, moved.ErrorData?.Message);
        var changeId = moved.SuccessData!.ChangeId!;
        var blobPath = OperationBlobWriter.FindBlobPath(changeId, fixture.SolutionDirectory);
        Assert.That(blobPath, Is.Not.Null);
        var blobText = File.ReadAllText(blobPath!);
        Assert.That(blobText.Split("STORED_ONCE_MARKER").Length - 1, Is.EqualTo(1), "the moved text must be stored exactly once");
        using var doc = System.Text.Json.JsonDocument.Parse(blobText);
        var items = doc.RootElement.GetProperty("items").EnumerateArray()
            .Select(e => System.Text.Json.JsonSerializer.Deserialize<OperationItemRecord>(e.GetRawText())!)
            .ToList();
        var destinationItem = items.Single(i =>
            string.Equals(Path.GetFullPath(i.FilePath!), Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase));
        Assert.That(destinationItem.AfterSourceFromFilePath, Is.Not.Null);
        Assert.That(Path.GetFullPath(destinationItem.AfterSourceFromFilePath!), Is.EqualTo(Path.GetFullPath(source)).IgnoreCase);

        var undone = await workspaceTools.UndoChangeTool(reason: "test message", changeId);

        Assert.That(undone.IsError, Is.False, undone.ErrorData?.Message);
        Assert.That(File.ReadAllText(source), Is.EqualTo(text));
        Assert.That(File.Exists(destination), Is.False);
    }

    [Test]
    public async Task MoveFile_MissingSource_FailsWithNotFoundAsync()
    {
        using var fixture = new TestSolutionFixture();
        using var manager = await LoadAsync(fixture);
        var source = PathIn(fixture, CoreFolder, "Nothing.cs");
        var destination = PathIn(fixture, CoreFolder, "Elsewhere.cs");

        var result = await MoveAsync(BuildTools(manager), source, destination, MoveFileNamespaceHandling.keep);

        AssertRefused(result, ToolErrorCode.NotFound, "does not exist on disk", source);
        Assert.That(File.Exists(destination), Is.False);
    }

    [Test]
    public async Task MoveFile_DirectorySource_FailsAsNotAFileAsync()
    {
        using var fixture = new TestSolutionFixture();
        using var manager = await LoadAsync(fixture);
        var source = PathIn(fixture, CoreFolder);
        var destination = PathIn(fixture, "Copy");

        var result = await MoveAsync(BuildTools(manager), source, destination, MoveFileNamespaceHandling.keep);

        AssertRefused(result, ToolErrorCode.TargetIneligible, "is a directory", source);
        Assert.That(Directory.Exists(source), Is.True);
        Assert.That(Directory.Exists(destination), Is.False);
    }

    [Test]
    public async Task MoveFile_BinarySource_FailsAndMovesNothingAsync()
    {
        using var fixture = new TestSolutionFixture();
        using var manager = await LoadAsync(fixture);
        var source = PathIn(fixture, "data.bin");
        var destination = PathIn(fixture, "moved.bin");
        File.WriteAllBytes(source, [0x50, 0x4B, 0x00, 0x00, 0x01, 0x02]);

        var result = await MoveAsync(BuildTools(manager), source, destination, MoveFileNamespaceHandling.keep);

        AssertRefused(result, ToolErrorCode.TargetIneligible, "NUL byte", "binary");
        Assert.That(File.Exists(source), Is.True);
        Assert.That(File.Exists(destination), Is.False);
    }

    [Test]
    public async Task MoveFile_DestinationExists_FailsAndKeepsBothFilesAsync()
    {
        using var fixture = new TestSolutionFixture();
        using var manager = await LoadAsync(fixture);
        await fixture.AddFileToSolution(manager, Path.Combine(CoreFolder, "Mover.cs"), "namespace ContosoOrders.Core;\r\n\r\npublic class Mover\r\n{\r\n}\r\n");
        await fixture.AddFileToSolution(manager, Path.Combine(CoreFolder, "Occupied.cs"), "namespace ContosoOrders.Core;\r\n\r\npublic class Occupied\r\n{\r\n}\r\n");
        var source = PathIn(fixture, CoreFolder, "Mover.cs");
        var destination = PathIn(fixture, CoreFolder, "Occupied.cs");
        var destinationText = File.ReadAllText(destination);

        var result = await MoveAsync(BuildTools(manager), source, destination, MoveFileNamespaceHandling.keep);

        AssertRefused(result, ToolErrorCode.InvalidArgument, "already exists as a file", destination);
        Assert.That(File.Exists(source), Is.True);
        Assert.That(File.ReadAllText(destination), Is.EqualTo(destinationText));
    }

    [Test]
    public async Task MoveFile_DestinationIsAnExistingDirectory_FailsAsync()
    {
        using var fixture = new TestSolutionFixture();
        using var manager = await LoadAsync(fixture);
        await fixture.AddFileToSolution(manager, Path.Combine(CoreFolder, "Mover.cs"), "namespace ContosoOrders.Core;\r\n\r\npublic class Mover\r\n{\r\n}\r\n");
        var source = PathIn(fixture, CoreFolder, "Mover.cs");
        var destination = PathIn(fixture, TestsFolder);

        var result = await MoveAsync(BuildTools(manager), source, destination, MoveFileNamespaceHandling.keep);

        AssertRefused(result, ToolErrorCode.InvalidArgument, "already exists as a directory");
        Assert.That(File.Exists(source), Is.True);
    }

    [Test]
    public async Task MoveFile_SourceEqualsDestination_FailsAsync()
    {
        using var fixture = new TestSolutionFixture();
        using var manager = await LoadAsync(fixture);
        await fixture.AddFileToSolution(manager, Path.Combine(CoreFolder, "Same.cs"), "namespace ContosoOrders.Core;\r\n\r\npublic class Same\r\n{\r\n}\r\n");
        var source = PathIn(fixture, CoreFolder, "Same.cs");

        var result = await MoveAsync(BuildTools(manager), source, source, MoveFileNamespaceHandling.keep);

        AssertRefused(result, ToolErrorCode.InvalidArgument, "resolve to the same path");
        Assert.That(File.Exists(source), Is.True);
    }

    [TestCase(MoveFileNamespaceHandling.update)]
    public async Task MoveFile_CsFileWithANamespaceHandlingOtherThanKeep_FailsAsNotYetSupportedAsync(MoveFileNamespaceHandling handling)
    {
        using var fixture = new TestSolutionFixture();
        using var manager = await LoadAsync(fixture);
        await fixture.AddFileToSolution(manager, Path.Combine(CoreFolder, "Pending.cs"), "namespace ContosoOrders.Core;\r\n\r\npublic class Pending\r\n{\r\n}\r\n");
        var source = PathIn(fixture, CoreFolder, "Pending.cs");
        var destination = PathIn(fixture, CoreFolder, "PendingMoved.cs");

        var result = await MoveAsync(BuildTools(manager), source, destination, handling);

        AssertRefused(result, ToolErrorCode.NotImplemented, "not supported yet", "only 'keep' is supported", handling.ToString());
        Assert.That(File.Exists(source), Is.True);
        Assert.That(File.Exists(destination), Is.False);
    }

    [TestCase(MoveFileNamespaceHandling.update)]
    [TestCase(MoveFileNamespaceHandling.copyAndObsolete)]
    public async Task MoveFile_NonCsFileWithANamespaceHandlingOtherThanKeep_FailsAsync(MoveFileNamespaceHandling handling)
    {
        using var fixture = new TestSolutionFixture();
        using var manager = await LoadAsync(fixture);
        await fixture.AddFileToSolution(manager, Path.Combine("docs", "Readme.md"), "# Readme\r\n");
        var source = PathIn(fixture, "docs", "Readme.md");
        var destination = PathIn(fixture, "docs", "ReadmeMoved.md");

        var result = await MoveAsync(BuildTools(manager), source, destination, handling);

        AssertRefused(result, ToolErrorCode.InvalidArgument, "not a .cs file", "only 'keep'", handling.ToString());
        Assert.That(File.Exists(source), Is.True);
        Assert.That(File.Exists(destination), Is.False);
    }
}
