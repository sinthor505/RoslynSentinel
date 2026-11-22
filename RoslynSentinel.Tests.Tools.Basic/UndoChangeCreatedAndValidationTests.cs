// Slice 12-7 of the UndoChange redesign (docs/current/proposals/proposal_undo_change_last_only_validated.md):
// undo deletes files the blob records as Created, compiles the restored tree before writing (validateChanges), and applies
// restores plus deletes in one atomic call. Real-disk fixture (PersistentWorkspaceManager over a TestSolutionFixture copy) because
// both the compile gate and the delete path live in the write chokepoint. The still-open case "a created marker is not set when the
// pre-image read fails" is a chokepoint concern (PersistentWorkspaceManager pre-image capture) and is not covered here.

using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Tools.Basic;

#pragma warning disable CS8618
namespace RoslynSentinel.Tests.Battery.Basic;

[TestFixture]
[Category("WorkspaceTools")] // sentinel:auto-category
public class UndoChangeCreatedAndValidationTests
{
    private const string HelperWithExtra = "namespace UndoFixtureCode;\n\npublic static class UndoHelper\n{\n    public static int Value() => 1;\n\n    public static int Extra() => 2;\n}\n";
    private const string HelperWithoutExtra = "namespace UndoFixtureCode;\n\npublic static class UndoHelper\n{\n    public static int Value() => 1;\n}\n";
    private const string ConsumerOfExtra = "namespace UndoFixtureCode;\n\npublic class UndoConsumer\n{\n    public int Run() => UndoHelper.Extra();\n}\n";
    private const string CreatedTypeSource = "namespace UndoFixtureCode;\n\npublic class CreatedByTool\n{\n}\n";

    private static string CoreFile(TestSolutionFixture fixture, string name) => Path.Combine(fixture.SolutionDirectory, "ContosoOrders.Core", name);

    private static async Task<PersistentWorkspaceManager> LoadAsync(TestSolutionFixture fixture)
    {
        var workspaceManager = TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        await workspaceManager.LoadSolutionAsync(fixture.SolutionPath);
        return workspaceManager;
    }

    // Writes a blob of hand-built items; the stamp orders it for the last-only rule (copy of the helper in UndoChangeTests, which keeps its own private).
    private static async Task<string> WriteBlobAsync(TestSolutionFixture fixture, string changeId, params OperationItemRecord[] items)
    {
        var context = new ToolCallContext("test-agent", "implementer", "ApplyDiff", "hand-built undo blob", 0) { SessionId = "s1" };
        context.MarkWritten(DateTime.UtcNow);
        ToolCallContext.Set(context);
        try
        {
            var blob = await OperationBlobWriter.WriteAsync("apply_diff", changeId, items.ToList(), fixture.SolutionDirectory);
            return Path.Combine(fixture.SolutionDirectory, ".roslynsentinel", "operations", blob.FileName!);
        }
        finally
        {
            ToolCallContext.Set(null);
        }
    }

    private static OperationItemRecord Edited(string path, string before, string after) =>
        new() { FilePath = path, Outcome = ItemRecordOutcome.Succeeded, BeforeSource = before, AfterSource = after };

    private static OperationItemRecord CreatedItem(string path, string after) =>
        new() { FilePath = path, Outcome = ItemRecordOutcome.Succeeded, BeforeSource = null, AfterSource = after, Created = true };

    [Test]
    [Description("Test (b): restoring a pre-image that removes a member another file still uses is refused by the compile gate; nothing is written and the blob is not marked undone")]
    public async Task UndoChange_RestoreBreaksDependentCode_IsRefusedWithNothingWrittenAsync()
    {
        using var fixture = new TestSolutionFixture();
        var helper = CoreFile(fixture, "UndoHelper.cs");
        await File.WriteAllTextAsync(helper, HelperWithExtra);
        await File.WriteAllTextAsync(CoreFile(fixture, "UndoConsumer.cs"), ConsumerOfExtra);
        using var workspaceManager = await LoadAsync(fixture);
        var workspaceTools = TestTools.WorkspaceTools(workspaceManager);

        var blobPath = await WriteBlobAsync(fixture, "change-breaks-dependent", Edited(helper, HelperWithoutExtra, HelperWithExtra));
        workspaceManager.ClearExternalFileChanges();

        var result = await workspaceTools.UndoChangeTool(reason: "test message", changeId: "change-breaks-dependent");

        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData!.ErrorCode, Is.EqualTo(ToolErrorCode.ValidationFailed));
        Assert.That(result.ErrorData!.Message, Does.Contain("nothing was written"));
        Assert.That(result.ErrorData!.Message, Does.Match("CS0117|CS1061|CS0103"));
        Assert.That(result.ErrorData!.Message, Does.Contain("change-breaks-dependent"));
        Assert.That(await File.ReadAllTextAsync(helper), Is.EqualTo(HelperWithExtra), "a refused undo must leave the file untouched");
        Assert.That(OperationBlobWriter.IsUndone(blobPath), Is.False, "a refused undo must not mark the blob undone");
    }

    [Test]
    [Description("Test (c): undo of a CreateFile change removes the file, using the changeId the CreateFile tool call returned")]
    public async Task UndoChange_UndoOfCreateFileChange_DeletesTheCreatedFileAsync()
    {
        using var fixture = new TestSolutionFixture();
        using var workspaceManager = await LoadAsync(fixture);
        var workspaceTools = TestTools.WorkspaceTools(workspaceManager);
        var created = CoreFile(fixture, "CreatedByTool.cs");

        var create = await workspaceTools.CreateFileTool(reason: "test message", created, content: CreatedTypeSource);
        Assert.That(create.IsError, Is.False, create.ErrorData?.Message);
        var changeId = ((ApplyChangesResult)create.SuccessData!).ChangeId;
        Assert.That(changeId, Is.Not.Null.And.Not.Empty);
        Assert.That(File.Exists(created), Is.True);

        var undo = await workspaceTools.UndoChangeTool(reason: "test message", changeId: changeId!);

        Assert.That(undo.IsError, Is.False, undo.ErrorData?.Message);
        Assert.That((string)undo.SuccessData!, Does.Contain("Reverted 0 files; deleted 1 created files"));
        Assert.That(File.Exists(created), Is.False, "undo of a created file must delete it");
        Assert.That(OperationBlobWriter.IsUndone(OperationBlobWriter.FindBlobPath(changeId!, fixture.SolutionDirectory)!), Is.True);
    }

    [Test]
    [Description("The drift guard still fires for a created item after the last-only check passes: a raw disk edit of the created file refuses the undo and keeps the file")]
    public async Task UndoChange_CreatedFileEditedOnDiskAfterCreation_IsRefusedAndFileKeptAsync()
    {
        using var fixture = new TestSolutionFixture();
        using var workspaceManager = await LoadAsync(fixture);
        var workspaceTools = TestTools.WorkspaceTools(workspaceManager);
        var created = CoreFile(fixture, "CreatedByTool.cs");

        var create = await workspaceTools.CreateFileTool(reason: "test message", created, content: CreatedTypeSource);
        Assert.That(create.IsError, Is.False, create.ErrorData?.Message);
        var changeId = ((ApplyChangesResult)create.SuccessData!).ChangeId!;
        var edited = CreatedTypeSource + "\n// raw edit after creation\n";
        await File.WriteAllTextAsync(created, edited);
        workspaceManager.ClearExternalFileChanges();

        var undo = await workspaceTools.UndoChangeTool(reason: "test message", changeId: changeId);

        Assert.That(undo.IsError, Is.True);
        Assert.That(undo.ErrorData!.ErrorCode, Is.EqualTo(ToolErrorCode.ConcurrentEditRefused));
        Assert.That(File.Exists(created), Is.True);
        Assert.That(await File.ReadAllTextAsync(created), Is.EqualTo(edited));
    }

    [Test]
    [Description("Deleting a created type that other code references is refused with the compile errors and the file is kept")]
    public async Task UndoChange_CreatedTypeReferencedByOtherCode_IsRefusedAndFileKeptAsync()
    {
        using var fixture = new TestSolutionFixture();
        using var workspaceManager = await LoadAsync(fixture);
        var workspaceTools = TestTools.WorkspaceTools(workspaceManager);
        var created = CoreFile(fixture, "CreatedByTool.cs");

        var create = await workspaceTools.CreateFileTool(reason: "test message", created, content: CreatedTypeSource);
        Assert.That(create.IsError, Is.False, create.ErrorData?.Message);
        var changeId = ((ApplyChangesResult)create.SuccessData!).ChangeId!;
        // A later raw write that leaves no blob: code now depends on the created type.
        await fixture.AddFileToSolution(workspaceManager, Path.Combine("ContosoOrders.Core", "CreatedUser.cs"),
            "namespace UndoFixtureCode;\n\npublic class CreatedUser\n{\n    public CreatedByTool Held { get; set; } = new();\n}\n");

        var undo = await workspaceTools.UndoChangeTool(reason: "test message", changeId: changeId);

        Assert.That(undo.IsError, Is.True);
        Assert.That(undo.ErrorData!.ErrorCode, Is.EqualTo(ToolErrorCode.ValidationFailed));
        Assert.That(undo.ErrorData!.Message, Does.Match("CS0246|CS0103"));
        Assert.That(File.Exists(created), Is.True, "a refused undo must keep the created file");
    }

    [Test]
    [Description("A mixed undo (one edited file plus one created file) restores and deletes in one call")]
    public async Task UndoChange_EditedAndCreatedFiles_AreBothRevertedInOneCallAsync()
    {
        using var fixture = new TestSolutionFixture();
        var edited = CoreFile(fixture, "UndoEdited.cs");
        var created = CoreFile(fixture, "CreatedByTool.cs");
        const string before = "namespace UndoFixtureCode;\n\npublic class UndoEdited\n{\n}\n";
        const string after = "namespace UndoFixtureCode;\n\npublic class UndoEdited\n{\n    public int Added() => 1;\n}\n";
        await File.WriteAllTextAsync(edited, after);
        await File.WriteAllTextAsync(created, CreatedTypeSource);
        using var workspaceManager = await LoadAsync(fixture);
        var workspaceTools = TestTools.WorkspaceTools(workspaceManager);
        await WriteBlobAsync(fixture, "change-mixed", Edited(edited, before, after), CreatedItem(created, CreatedTypeSource));
        workspaceManager.ClearExternalFileChanges();

        var undo = await workspaceTools.UndoChangeTool(reason: "test message", changeId: "change-mixed");

        Assert.That(undo.IsError, Is.False, undo.ErrorData?.Message);
        Assert.That((string)undo.SuccessData!, Does.Contain("Reverted 1 files; deleted 1 created files"));
        Assert.That(await File.ReadAllTextAsync(edited), Is.EqualTo(before));
        Assert.That(File.Exists(created), Is.False);
    }

    [Test]
    [Description("When the compile gate refuses a mixed undo, neither the edited file nor the created file is touched")]
    public async Task UndoChange_MixedUndoRefusedByCompileGate_TouchesNeitherFileAsync()
    {
        using var fixture = new TestSolutionFixture();
        var edited = CoreFile(fixture, "UndoEdited.cs");
        var created = CoreFile(fixture, "CreatedByTool.cs");
        const string brokenBefore = "namespace UndoFixtureCode;\n\npublic class UndoEdited\n{\n    public int Broken() => MissingName;\n}\n";
        const string after = "namespace UndoFixtureCode;\n\npublic class UndoEdited\n{\n}\n";
        await File.WriteAllTextAsync(edited, after);
        await File.WriteAllTextAsync(created, CreatedTypeSource);
        using var workspaceManager = await LoadAsync(fixture);
        var workspaceTools = TestTools.WorkspaceTools(workspaceManager);
        var blobPath = await WriteBlobAsync(fixture, "change-mixed-refused", Edited(edited, brokenBefore, after), CreatedItem(created, CreatedTypeSource));
        workspaceManager.ClearExternalFileChanges();

        var undo = await workspaceTools.UndoChangeTool(reason: "test message", changeId: "change-mixed-refused");

        Assert.That(undo.IsError, Is.True);
        Assert.That(undo.ErrorData!.ErrorCode, Is.EqualTo(ToolErrorCode.ValidationFailed));
        Assert.That(undo.ErrorData!.Message, Does.Contain("CS0103"));
        Assert.That(await File.ReadAllTextAsync(edited), Is.EqualTo(after));
        Assert.That(File.Exists(created), Is.True);
        Assert.That(await File.ReadAllTextAsync(created), Is.EqualTo(CreatedTypeSource));
        Assert.That(OperationBlobWriter.IsUndone(blobPath), Is.False);
    }

    [Test]
    [Description("A created file that is already gone from disk is not drift: the undo succeeds, names it as already absent and marks the blob undone")]
    public async Task UndoChange_CreatedFileAlreadyAbsent_SucceedsAndReportsAlreadyAbsentAsync()
    {
        using var fixture = new TestSolutionFixture();
        using var workspaceManager = await LoadAsync(fixture);
        var workspaceTools = TestTools.WorkspaceTools(workspaceManager);
        var gone = CoreFile(fixture, "CreatedThenRemoved.cs");
        var blobPath = await WriteBlobAsync(fixture, "change-created-absent", CreatedItem(gone, CreatedTypeSource));
        workspaceManager.ClearExternalFileChanges();

        var undo = await workspaceTools.UndoChangeTool(reason: "test message", changeId: "change-created-absent");

        Assert.That(undo.IsError, Is.False, undo.ErrorData?.Message);
        Assert.That((string)undo.SuccessData!, Does.Contain("already absent"));
        Assert.That(File.Exists(gone), Is.False);
        Assert.That(OperationBlobWriter.IsUndone(blobPath), Is.True);
    }

    [Test]
    [Description("A succeeded item with a null BeforeSource that is not marked Created (a failed pre-image read looks the same) is still not reversible")]
    public async Task UndoChange_NullBeforeSourceWithoutCreatedMarker_StillReturnsNoReversibleItemsAsync()
    {
        using var fixture = new TestSolutionFixture();
        using var workspaceManager = await LoadAsync(fixture);
        var workspaceTools = TestTools.WorkspaceTools(workspaceManager);
        var file = CoreFile(fixture, "Unmarked.cs");
        await File.WriteAllTextAsync(file, CreatedTypeSource);
        await WriteBlobAsync(fixture, "change-unmarked", new OperationItemRecord { FilePath = file, Outcome = ItemRecordOutcome.Succeeded, BeforeSource = null, AfterSource = CreatedTypeSource, Created = false });
        workspaceManager.ClearExternalFileChanges();

        var undo = await workspaceTools.UndoChangeTool(reason: "test message", changeId: "change-unmarked");

        Assert.That(undo.IsError, Is.True);
        Assert.That(undo.ErrorData!.ErrorCode, Is.EqualTo("NoReversibleItems"));
        Assert.That(File.Exists(file), Is.True, "an unmarked null pre-image must never lead to a delete");
    }
}
