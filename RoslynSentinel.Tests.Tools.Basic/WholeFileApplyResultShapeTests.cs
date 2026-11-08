// Pins the success-result shape of the whole-file apply tools: ReplaceSnippet (single and batch), CreateFile, WriteFile,
// DeleteFile and ApplyDiff (files and diff format). None of them goes through ValidateAndApplyHelper (they call
// IWorkspaceManager.ApplyProposedChangesAsync directly), so unlike Member/ModifyModifier/... they carry no changeId, no
// AppliedChangeSummary and NO detached "changedFiles" payload. That omission is intentional: the caller already holds the
// content it just sent (or, for DeleteFile, there is none), so a GetLargeResult pointer to it would be noise. These tests
// keep a refactor of the apply path from adding one by accident, or from silently changing the payload type.
//
// ReplaceSnippet runs on an InMemoryWorkspace (see ReplaceSnippetBatchTests); the others check File.Exists / write to disk,
// so they use a real TestSolutionFixture (see CreateFileDeleteFileTests).

using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Common;
using RoslynSentinel.Engines.Basic;
using RoslynSentinel.Tests.Fakes;
using RoslynSentinel.Tools.Basic;

namespace RoslynSentinel.Tests.Battery.Basic;

[TestFixture]
[Parallelizable(ParallelScope.All)]
[Category("WholeFileWriteTools")]
[Category("WorkspaceTools")]
public class WholeFileApplyResultShapeTests
{
    private const string ChangedFilesPayloadName = "changedFiles";

    private const string LineRelativePath = "ContosoOrders.Core/OrderLine.cs";
    private const string StatusRelativePath = "ContosoOrders.Core/OrderStatus.cs";

    private const string LineSource = "namespace ContosoOrders.Core;\n\npublic record OrderLine(string Sku, int Quantity);\n";
    private const string StatusSource = "namespace ContosoOrders.Core;\n\npublic enum OrderStatus\n{\n    Pending,\n    Shipped,\n}\n";

    [TearDown]
    public void ResetContext() => ToolCallContext.Set(null);

    private static WorkspaceTools BuildTools(IWorkspaceManager workspaceManager) =>
        TestTools.WorkspaceTools(workspaceManager);

    private static WholeFileWriteTools BuildWholeFileWriteTools(IWorkspaceManager workspaceManager) =>
        TestTools.WholeFileWriteTools(workspaceManager, BuildTools(workspaceManager));

    // ToolCallContext.Current is null in tests unless set; without one a stray DetachCurrent would be a silent no-op and the
    // "nothing was detached" assertions below would pass for the wrong reason.
    private static ToolCallContext StartToolCall(string toolName)
    {
        var context = new ToolCallContext("agent-1", "implementer", toolName, "result shape test", 0);
        ToolCallContext.Set(context);
        return context;
    }

    // The omission is intentional (see the header comment): the apply tools hand back ApplyChangesResult.SucceededFiles
    // and the caller already holds the content, so no changedFiles payload is declared on the context or the result.
    private static void AssertNoChangedFilesDetached(ToolCallContext context, IReadOnlyList<(string Name, object Payload)> resultDetached)
    {
        Assert.Multiple(() =>
        {
            Assert.That(context.Detached.Any(d => d.Name == ChangedFilesPayloadName), Is.False, "no changedFiles on the call context (intentional)");
            Assert.That(resultDetached.Any(d => d.Name == ChangedFilesPayloadName), Is.False, "no changedFiles on the result (intentional)");
        });
    }

    private static bool SamePath(string left, string right) =>
        string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);

    private static void AssertSucceededFilesAre(ApplyChangesResult applied, params string[] expectedPaths)
    {
        Assert.Multiple(() =>
        {
            Assert.That(applied.IsError, Is.False, applied.Summary);
            Assert.That(applied.SucceededFiles, Has.Count.EqualTo(expectedPaths.Length), string.Join(", ", applied.SucceededFiles));
            foreach (var expected in expectedPaths)
            {
                Assert.That(applied.SucceededFiles.Any(f => SamePath(f, expected)), Is.True, $"'{expected}' in [{string.Join(", ", applied.SucceededFiles)}]");
            }

            Assert.That(applied.FailedFiles, Is.Empty);
            Assert.That(applied.PreImages, Is.Null, "PreImages are stripped from the response; they live in the undo blob");
        });
    }

    private static string ProjectDirectoryOf(TestSolutionFixture fixture) =>
        Path.GetDirectoryName(Directory.EnumerateFiles(fixture.SolutionDirectory, "*.csproj", SearchOption.AllDirectories).First())!;

    [Test]
    public async Task ReplaceSnippet_SingleApply_ReturnsReplacementResultAndNoChangedFilesAsync()
    {
        using var workspace = InMemoryWorkspace.Create((LineRelativePath, LineSource));
        var tools = BuildTools(workspace.Manager);
        var context = StartToolCall("ReplaceSnippet");
        var target = workspace.PathOf(LineRelativePath);

        var result = await tools.ReplaceSnippetTool(
            reason: "result shape single",
            ProposedChangeAction.apply,
            filePath: target,
            oldContent: "public record OrderLine(string Sku, int Quantity);",
            newContent: "public record OrderLine(string Sku, int Quantity); // shape");

        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        var payload = result.SuccessData!;
        Assert.Multiple(() =>
        {
            Assert.That(payload, Is.TypeOf<ReplaceSnippetResult>());
            Assert.That(payload.ValidationReport, Is.Null, "apply sets ReplacementResult, not ValidationReport");
            Assert.That(payload.DiffContent, Is.Null, "no diff unless returnDiff is asked for");
            Assert.That(workspace.ReadText(LineRelativePath), Does.Contain("// shape"), "the edit must have landed");
        });
        AssertSucceededFilesAre(payload.ReplacementResult!, target);
        AssertNoChangedFilesDetached(context, result.Detached);
    }

    [Test]
    public async Task ReplaceSnippet_BatchApply_ReturnsReplacementResultListingEveryFileAndNoChangedFilesAsync()
    {
        using var workspace = InMemoryWorkspace.Create((LineRelativePath, LineSource), (StatusRelativePath, StatusSource));
        var tools = BuildTools(workspace.Manager);
        var context = StartToolCall("ReplaceSnippet");
        var lineFile = workspace.PathOf(LineRelativePath);
        var statusFile = workspace.PathOf(StatusRelativePath);

        var result = await tools.ReplaceSnippetTool(
            reason: "result shape batch",
            ProposedChangeAction.apply,
            batchEdits:
            [
                new SnippetEdit { FilePath = lineFile, OldContent = "public record OrderLine(string Sku, int Quantity);", NewContent = "public record OrderLine(string Sku, int Quantity); // batch-a" },
                new SnippetEdit { FilePath = statusFile, OldContent = "    Shipped,", NewContent = "    Shipped, // batch-b" },
            ]);

        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        var payload = result.SuccessData!;
        Assert.That(payload, Is.TypeOf<ReplaceSnippetResult>());
        Assert.That(payload.ValidationReport, Is.Null);
        AssertSucceededFilesAre(payload.ReplacementResult!, lineFile, statusFile);
        AssertNoChangedFilesDetached(context, result.Detached);
    }

    [Test]
    public async Task CreateFile_WithContent_ReturnsApplyChangesResultAndNoChangedFilesAsync()
    {
        using var fixture = new TestSolutionFixture();
        using var workspaceManager = TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        await workspaceManager.LoadSolutionAsync(fixture.SolutionPath);
        var tools = BuildTools(workspaceManager);
        var context = StartToolCall("CreateFile");
        var newFile = Path.Combine(ProjectDirectoryOf(fixture), "ShapeCreated.cs");

        var result = await tools.CreateFileTool(reason: "result shape create", newFile, content: "namespace MyApp.Shape;\n\npublic class ShapeCreated\n{\n}\n");

        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        Assert.That(File.Exists(newFile), Is.True);
        Assert.That(result.SuccessData, Is.TypeOf<ApplyChangesResult>());
        AssertSucceededFilesAre((ApplyChangesResult)result.SuccessData!, newFile);
        AssertNoChangedFilesDetached(context, result.Detached);
    }

    [Test]
    public async Task WriteFile_ReplaceFile_ReturnsApplyChangesResultAndNoChangedFilesAsync()
    {
        using var fixture = new TestSolutionFixture();
        using var workspaceManager = TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        var existingFile = Path.Combine(ProjectDirectoryOf(fixture), "ShapeReplaced.cs");
        await File.WriteAllTextAsync(existingFile, "namespace MyApp.Shape;\n\npublic class ShapeReplaced\n{\n}\n");
        await workspaceManager.LoadSolutionAsync(fixture.SolutionPath);
        var tools = BuildWholeFileWriteTools(workspaceManager);
        var context = StartToolCall("WriteFile");

        var result = await tools.WriteFileTool(
            reason: "result shape write", WriteFileOperation.ReplaceFile, existingFile,
            content: "namespace MyApp.Shape;\n\npublic class ShapeReplaced\n{\n    public int Value { get; set; }\n}\n");

        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        Assert.That(await File.ReadAllTextAsync(existingFile), Does.Contain("public int Value"));
        Assert.That(result.SuccessData, Is.TypeOf<ApplyChangesResult>());
        AssertSucceededFilesAre((ApplyChangesResult)result.SuccessData!, existingFile);
        AssertNoChangedFilesDetached(context, result.Detached);
    }

    [Test]
    public async Task WriteFile_CreateFile_ReturnsApplyChangesResultAndNoChangedFilesAsync()
    {
        using var fixture = new TestSolutionFixture();
        using var workspaceManager = TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        await workspaceManager.LoadSolutionAsync(fixture.SolutionPath);
        var tools = BuildWholeFileWriteTools(workspaceManager);
        var context = StartToolCall("WriteFile");
        var newFile = Path.Combine(ProjectDirectoryOf(fixture), "ShapeWritten.cs");

        var result = await tools.WriteFileTool(
            reason: "result shape write create", WriteFileOperation.CreateFile, newFile,
            content: "namespace MyApp.Shape;\n\npublic class ShapeWritten\n{\n}\n");

        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        Assert.That(File.Exists(newFile), Is.True);
        Assert.That(result.SuccessData, Is.TypeOf<ApplyChangesResult>());
        AssertSucceededFilesAre((ApplyChangesResult)result.SuccessData!, newFile);
        AssertNoChangedFilesDetached(context, result.Detached);
    }

    [Test]
    public async Task DeleteFile_ExistingFile_ListsTheDeletedPathAndNoChangedFilesAsync()
    {
        using var fixture = new TestSolutionFixture();
        using var workspaceManager = TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        var doomedFile = Path.Combine(ProjectDirectoryOf(fixture), "ShapeDoomed.cs");
        await File.WriteAllTextAsync(doomedFile, "namespace MyApp.Shape;\n\npublic class ShapeDoomed\n{\n}\n");
        await workspaceManager.LoadSolutionAsync(fixture.SolutionPath);
        var tools = BuildWholeFileWriteTools(workspaceManager);
        var context = StartToolCall("DeleteFile");

        var result = await tools.DeleteFileTool(reason: "result shape delete", doomedFile);

        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        Assert.That(File.Exists(doomedFile), Is.False);
        Assert.That(result.SuccessData, Is.TypeOf<ApplyChangesResult>());
        AssertSucceededFilesAre((ApplyChangesResult)result.SuccessData!, doomedFile);
        AssertNoChangedFilesDetached(context, result.Detached);
    }

    [Test]
    public async Task ApplyDiff_FilesFormat_ReturnsApplyChangesResultAndNoChangedFilesAsync()
    {
        using var fixture = new TestSolutionFixture();
        using var workspaceManager = TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        var targetFile = Path.Combine(ProjectDirectoryOf(fixture), "ShapeFilesFormat.cs");
        await File.WriteAllTextAsync(targetFile, "namespace MyApp.Shape;\n\npublic class ShapeFilesFormat\n{\n}\n");
        await workspaceManager.LoadSolutionAsync(fixture.SolutionPath);
        var tools = BuildWholeFileWriteTools(workspaceManager);
        var context = StartToolCall("ApplyDiff");

        var result = await tools.ApplyDiffTool(
            reason: "result shape apply diff files", ChangesetFormat.files, ProposedChangeAction.apply,
            changes: new Dictionary<string, string> { [targetFile] = "namespace MyApp.Shape;\n\npublic class ShapeFilesFormat\n{\n    public int Value { get; set; }\n}\n" });

        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        Assert.That(await File.ReadAllTextAsync(targetFile), Does.Contain("public int Value"));
        Assert.That(result.SuccessData, Is.TypeOf<ApplyChangesResult>(), "returnDiff is off, so the bare ApplyChangesResult");
        AssertSucceededFilesAre((ApplyChangesResult)result.SuccessData!, targetFile);
        AssertNoChangedFilesDetached(context, result.Detached);
    }

    [Test]
    public async Task ApplyDiff_DiffFormat_ReturnsApplyChangesResultAndNoChangedFilesAsync()
    {
        using var fixture = new TestSolutionFixture();
        using var workspaceManager = TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        var targetFile = Path.Combine(ProjectDirectoryOf(fixture), "ShapeDiffFormat.cs");
        await File.WriteAllTextAsync(targetFile, "namespace MyApp.Shape;\npublic class ShapeDiffFormat\n{\n}\n");
        await workspaceManager.LoadSolutionAsync(fixture.SolutionPath);
        var tools = BuildWholeFileWriteTools(workspaceManager);
        var context = StartToolCall("ApplyDiff");
        var diff = "--- a/ShapeDiffFormat.cs\n+++ b/ShapeDiffFormat.cs\n@@ -1,4 +1,4 @@\n namespace MyApp.Shape;\n-public class ShapeDiffFormat\n+public class ShapeDiffFormat2\n {\n }\n";

        var result = await tools.ApplyDiffTool(
            reason: "result shape apply diff diff", ChangesetFormat.diff, ProposedChangeAction.apply,
            filePath: targetFile, unifiedDiff: diff);

        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        Assert.That(await File.ReadAllTextAsync(targetFile), Does.Contain("ShapeDiffFormat2"));
        Assert.That(result.SuccessData, Is.TypeOf<ApplyChangesResult>(), "a clean hunk with returnDiff off is the bare ApplyChangesResult");
        AssertSucceededFilesAre((ApplyChangesResult)result.SuccessData!, targetFile);
        AssertNoChangedFilesDetached(context, result.Detached);
    }
}
