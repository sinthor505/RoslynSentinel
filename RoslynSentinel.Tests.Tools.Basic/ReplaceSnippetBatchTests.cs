// ReplaceSnippetTool's batch path (the 'batchEdits' parameter). Per the design doc
// (docs/current/proposals/closed/proposal_batch_replacesnippet.md) every edit in a batch is anchored against its
// file's ORIGINAL content, never a prior edit's output, so these tests specifically cover: multiple
// batchEdits to one file, batchEdits spanning multiple files, overlap rejection, and the either/or validation
// against the singular filepath/oldContent/newContent parameters.
//
// About the tool's code-correctness, so it runs on an InMemoryWorkspace (no temp directory, MSBuild load or disk
// write). See ModifyAttributeBatchTests for the pattern. Disk-level fidelity (BOM, EOL) lives in DiskWriteRoundTripTests.

using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Engines.Basic;
using RoslynSentinel.Tests.Fakes;
using RoslynSentinel.Tools.Basic;

namespace RoslynSentinel.Tests.Battery.Basic;

[TestFixture]
[Parallelizable(ParallelScope.All)]
[Category("WorkspaceTools")] // sentinel:auto-category
public class ReplaceSnippetBatchTests
{
    private const string StatusRelativePath = "ContosoOrders.Core/OrderStatus.cs";
    private const string LineRelativePath = "ContosoOrders.Core/OrderLine.cs";

    private const string StatusSource = """
    namespace ContosoOrders.Core;

    public enum OrderStatus
    {
        Pending,
        Shipped,
    }
    """;

    private const string LineSource = """
    namespace ContosoOrders.Core;

    public record OrderLine(string Sku, int Quantity);
    """;

    private static InMemoryWorkspace CreateWorkspace() =>
        InMemoryWorkspace.Create((StatusRelativePath, StatusSource), (LineRelativePath, LineSource));

    private static string FirstLine(string text) => text.Split('\n')[0].TrimEnd('\r');

    private static string LastNonEmptyLine(string text) =>
        text.Split('\n').Reverse().First(l => !string.IsNullOrWhiteSpace(l)).TrimEnd('\r');

    private static WorkspaceTools BuildTools(IWorkspaceManager workspaceManager) =>
        TestTools.WorkspaceTools(workspaceManager);

    [Test]
    public async Task ReplaceSnippet_BatchTwoEditsSameFile_BothApplyAgainstOriginalSnapshotAsync()
    {
        using var workspace = CreateWorkspace();
        var tools = BuildTools(workspace.Manager);

        var targetFile = workspace.PathOf(StatusRelativePath);
        var originalContent = workspace.ReadText(StatusRelativePath);
        // Two distinct, non-adjacent anchor lines in the same file -> both resolved against the
        // one original snapshot, not against each other's output.
        var firstAnchor = FirstLine(originalContent);
        var lastNonEmptyLine = LastNonEmptyLine(originalContent);

        var result = await tools.ReplaceSnippetTool(
            reason: "test batch same file",
            ProposedChangeAction.validate,
            batchEdits:
            [
                new SnippetEdit { FilePath = targetFile, OldContent = firstAnchor, NewContent = firstAnchor + " // edit-a" },
                new SnippetEdit { FilePath = targetFile, OldContent = lastNonEmptyLine, NewContent = lastNonEmptyLine + " // edit-b" },
            ]);

        Assert.That(!result.IsError, Is.True, result.ErrorData?.Message);
    }

    [Test]
    public async Task ReplaceSnippet_BatchAcrossTwoFiles_AppliesBothInOneCallAsync()
    {
        using var workspace = CreateWorkspace();
        var tools = BuildTools(workspace.Manager);

        var fileA = workspace.PathOf(LineRelativePath);
        var fileB = workspace.PathOf(StatusRelativePath);
        var anchorA = FirstLine(workspace.ReadText(LineRelativePath));
        var anchorB = FirstLine(workspace.ReadText(StatusRelativePath));

        var result = await tools.ReplaceSnippetTool(
            reason: "test batch across files",
            ProposedChangeAction.apply,
            batchEdits:
            [
                new SnippetEdit { FilePath = fileA, OldContent = anchorA, NewContent = anchorA + " // touched-a" },
                new SnippetEdit { FilePath = fileB, OldContent = anchorB, NewContent = anchorB + " // touched-b" },
            ]);

        Assert.That(!result.IsError, Is.True, result.ErrorData?.Message);

        var newContentA = workspace.ReadText(LineRelativePath);
        var newContentB = workspace.ReadText(StatusRelativePath);
        Assert.Multiple(() =>
        {
            Assert.That(newContentA, Does.Contain("// touched-a"));
            Assert.That(newContentB, Does.Contain("// touched-b"));
        });
    }

    [Test]
    public async Task ReplaceSnippet_BatchOverlappingMatchesSameFile_RejectsWithoutWritingAsync()
    {
        using var workspace = CreateWorkspace();
        var tools = BuildTools(workspace.Manager);

        var targetFile = workspace.PathOf(StatusRelativePath);
        var originalContent = workspace.ReadText(StatusRelativePath);
        var firstLine = FirstLine(originalContent);
        // Second edit's oldContent is a substring of the first edit's oldContent, in the same file
        // -> the two matched spans necessarily overlap.
        var overlappingFragment = firstLine.Length > 4 ? firstLine[..^2] : firstLine;

        var result = await tools.ReplaceSnippetTool(
            reason: "test batch overlap rejection",
            ProposedChangeAction.apply,
            batchEdits:
            [
                new SnippetEdit { FilePath = targetFile, OldContent = firstLine, NewContent = "// replaced-whole-line" },
                new SnippetEdit { FilePath = targetFile, OldContent = overlappingFragment, NewContent = "// replaced-fragment" },
            ]);

        Assert.That(!result.IsError, Is.False);
        Assert.That(result.ErrorData!.Message, Does.Contain("overlap"));

        var unchangedContent = workspace.ReadText(StatusRelativePath);
        Assert.That(unchangedContent, Is.EqualTo(originalContent), "an overlap rejection must not write anything");
    }

    [Test]
    public async Task ReplaceSnippet_BatchOneEditNotFound_RejectsWholeBatchWithoutWritingAsync()
    {
        using var workspace = CreateWorkspace();
        var tools = BuildTools(workspace.Manager);

        var fileA = workspace.PathOf(LineRelativePath);
        var originalContentA = workspace.ReadText(LineRelativePath);
        var anchorA = FirstLine(originalContentA);

        var result = await tools.ReplaceSnippetTool(
            reason: "test batch partial failure",
            ProposedChangeAction.apply,
            batchEdits:
            [
                new SnippetEdit { FilePath = fileA, OldContent = anchorA, NewContent = anchorA + " // should-not-land" },
                new SnippetEdit { FilePath = fileA, OldContent = "this text does not exist anywhere in the file", NewContent = "irrelevant" },
            ]);

        Assert.That(!result.IsError, Is.False);

        var unchangedContentA = workspace.ReadText(LineRelativePath);
        Assert.That(unchangedContentA, Is.EqualTo(originalContentA),
            "one unmatched edit in a batch must roll back the whole batch, not partially apply it");
    }

    [Test]
    public async Task ReplaceSnippet_TopLevelOldNewWithBatchEdits_AppliesAsExtraItemAsync()
    {
        using var workspace = CreateWorkspace();
        var tools = BuildTools(workspace.Manager);

        var fileA = workspace.PathOf(LineRelativePath);
        var fileB = workspace.PathOf(StatusRelativePath);
        var anchorA = FirstLine(workspace.ReadText(LineRelativePath));
        var anchorB = FirstLine(workspace.ReadText(StatusRelativePath));

        // The top-level oldContent/newContent is appended to the batch as one more item and uses the top-level filePath.
        var result = await tools.ReplaceSnippetTool(
            reason: "test top-level edit beside batch",
            ProposedChangeAction.apply,
            filePath: fileA,
            oldContent: anchorA,
            newContent: anchorA + " // top-level-item",
            batchEdits: [new SnippetEdit { FilePath = fileB, OldContent = anchorB, NewContent = anchorB + " // batch-item" }]);

        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        Assert.Multiple(() =>
        {
            Assert.That(workspace.ReadText(LineRelativePath), Does.Contain("// top-level-item"));
            Assert.That(workspace.ReadText(StatusRelativePath), Does.Contain("// batch-item"));
        });
    }

    [Test]
    public async Task ReplaceSnippet_TopLevelFilePathWithBatchEdits_DefaultsItemFilePathAsync()
    {
        using var workspace = CreateWorkspace();
        var tools = BuildTools(workspace.Manager);

        var targetFile = workspace.PathOf(StatusRelativePath);
        var anchor = FirstLine(workspace.ReadText(StatusRelativePath));

        // A top-level filePath beside batchEdits is tolerated when nothing else singular is set;
        // the batch item with no filePath of its own is applied to that top-level file.
        var result = await tools.ReplaceSnippetTool(
            reason: "test top-level filePath default",
            ProposedChangeAction.apply,
            filePath: targetFile,
            batchEdits: [new SnippetEdit { OldContent = anchor, NewContent = anchor + " // default-file" }]);

        Assert.That(!result.IsError, Is.True, result.ErrorData?.Message);
        Assert.That(workspace.ReadText(StatusRelativePath), Does.Contain("// default-file"));
    }

    [Test]
    public async Task ReplaceSnippet_BatchItemWithoutFilePath_ErrorNamesBothFixOptionsAsync()
    {
        using var workspace = CreateWorkspace();
        var tools = BuildTools(workspace.Manager);

        var anchor = FirstLine(workspace.ReadText(StatusRelativePath));

        var result = await tools.ReplaceSnippetTool(
            reason: "test missing item filePath",
            ProposedChangeAction.validate,
            batchEdits: [new SnippetEdit { OldContent = anchor, NewContent = anchor + " // x" }]);

        Assert.That(result.IsError, Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(result.ErrorData!.ErrorCode, Is.EqualTo(ToolErrorCode.InvalidArgument));
            Assert.That(result.ErrorData.Message, Does.Contain("batchEdits[0] has no filePath"));
            Assert.That(result.ErrorData.Message, Does.Contain("set filePath on batchEdits[0]"));
            Assert.That(result.ErrorData.Message, Does.Contain("top-level filePath"));
        });
    }

    [Test]
    public async Task ReplaceSnippet_TopLevelOldContentWithoutNewContentBesideBatch_ErrorNamesFixAsync()
    {
        using var workspace = CreateWorkspace();
        var tools = BuildTools(workspace.Manager);

        var targetFile = workspace.PathOf(StatusRelativePath);
        var original = workspace.ReadText(StatusRelativePath);
        var anchor = FirstLine(original);

        var result = await tools.ReplaceSnippetTool(
            reason: "test top-level oldContent only",
            ProposedChangeAction.apply,
            filePath: targetFile,
            oldContent: anchor,
            batchEdits: [new SnippetEdit { FilePath = targetFile, OldContent = anchor, NewContent = anchor + " // y" }]);

        Assert.That(result.IsError, Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(result.ErrorData!.ErrorCode, Is.EqualTo(ToolErrorCode.InvalidArgument));
            Assert.That(result.ErrorData.Message, Does.Contain("top-level oldContent"));
            Assert.That(result.ErrorData.Message, Does.Contain("newContent is not set"));
            Assert.That(result.ErrorData.Message, Does.Contain("move the edit into batchEdits"));
            Assert.That(workspace.ReadText(StatusRelativePath), Is.EqualTo(original), "a rejected call must not write anything");
        });
    }

    [Test]
    public async Task ReplaceSnippet_TopLevelOldNewWithoutAnyFilePathBesideBatch_ErrorNamesItemIndexAsync()
    {
        using var workspace = CreateWorkspace();
        var tools = BuildTools(workspace.Manager);

        var fileB = workspace.PathOf(StatusRelativePath);
        var anchorA = FirstLine(workspace.ReadText(LineRelativePath));
        var anchorB = FirstLine(workspace.ReadText(StatusRelativePath));

        var result = await tools.ReplaceSnippetTool(
            reason: "test top-level edit without filePath",
            ProposedChangeAction.validate,
            oldContent: anchorA,
            newContent: anchorA + " // z",
            batchEdits: [new SnippetEdit { FilePath = fileB, OldContent = anchorB, NewContent = anchorB + " // w" }]);

        Assert.That(result.IsError, Is.True);
        // The top-level edit is appended after the one supplied item, so it is batchEdits[1].
        Assert.That(result.ErrorData!.Message, Does.Contain("batchEdits[1] has no filePath"));
    }

    [Test]
    public async Task ReplaceSnippet_EmptyEditsArray_RejectsAsInvalidArgumentAsync()
    {
        using var workspace = CreateWorkspace();
        var tools = BuildTools(workspace.Manager);

        var result = await tools.ReplaceSnippetTool(
            reason: "test empty batchEdits",
            ProposedChangeAction.validate,
            batchEdits: []);

        Assert.That(!result.IsError, Is.False);
        Assert.That(result.ErrorData!.Message, Does.Contain("empty"));
    }

    [Test]
    public async Task ReplaceSnippet_BatchExceedsMaxEditsCap_RejectsBeforeAnchoringAsync()
    {
        using var workspace = CreateWorkspace();
        var tools = BuildTools(workspace.Manager);

        var targetFile = workspace.PathOf(StatusRelativePath);
        var edits = Enumerable.Range(0, 21)
            .Select(i => new SnippetEdit { FilePath = targetFile, OldContent = $"nonexistent-{i}", NewContent = "x" })
            .ToList();

        var result = await tools.ReplaceSnippetTool(
            reason: "test over cap",
            ProposedChangeAction.validate,
            batchEdits: edits);

        Assert.That(!result.IsError, Is.False);
        Assert.That(result.ErrorData!.Message, Does.Contain("20"));
    }

    [Test]
    public async Task ReplaceSnippet_BatchOversizeEdit_AppendsEscapeHatchAdviceOnceAsync()
    {
        // The single-edit path always carried escape-hatch advice on a size rejection; the batch
        // path used to list the per-edit bounds with no way forward. Two oversized edits must still
        // yield exactly one advice sentence, led by a complete Member(reason: ..., operation: replace) call.
        using var workspace = CreateWorkspace();
        var tools = BuildTools(workspace.Manager);

        var targetFile = workspace.PathOf(StatusRelativePath);
        var anchor = FirstLine(workspace.ReadText(StatusRelativePath));

        var result = await tools.ReplaceSnippetTool(
            reason: "test batch oversize advice",
            ProposedChangeAction.validate,
            batchEdits:
            [
                new SnippetEdit { FilePath = targetFile, OldContent = anchor, NewContent = new string('x', 2500) },
                new SnippetEdit { FilePath = targetFile, OldContent = anchor, NewContent = new string('y', 2500) },
            ]);

        Assert.That(!result.IsError, Is.False);
        var message = result.ErrorData!.Message;
        Assert.Multiple(() =>
        {
            Assert.That(message, Does.Contain("newContent is 2500 chars"));
            Assert.That(message, Does.Contain("Member(reason:"));
            Assert.That(message, Does.Contain("operation: replace"));
            Assert.That(message.Split("Member(reason:").Length - 1, Is.EqualTo(1),
                "the advice must be appended once per batch, not once per edit");
        });
    }

    [Test]
    public async Task ReplaceSnippet_BatchNonSizeError_DoesNotAppendEscapeHatchAdviceAsync()
    {
        using var workspace = CreateWorkspace();
        var tools = BuildTools(workspace.Manager);

        var targetFile = workspace.PathOf(StatusRelativePath);
        var anchor = FirstLine(workspace.ReadText(StatusRelativePath));

        var result = await tools.ReplaceSnippetTool(
            reason: "test batch missing newContent",
            ProposedChangeAction.validate,
            batchEdits: [new SnippetEdit { FilePath = targetFile, OldContent = anchor, NewContent = null! }]);

        Assert.That(!result.IsError, Is.False);
        Assert.Multiple(() =>
        {
            Assert.That(result.ErrorData!.Message, Does.Contain("newContent is required"));
            Assert.That(result.ErrorData!.Message, Does.Not.Contain("Member(reason:"),
                "size advice is noise for a non-size rejection");
        });
    }

    [Test]
    public async Task ReplaceSnippet_FifteenNonMatchingBatchEdits_MessageIsBoundedAndNamesEveryIndexAsync()
    {
        using var workspace = CreateWorkspace();
        var tools = BuildTools(workspace.Manager);

        var targetFile = workspace.PathOf(StatusRelativePath);
        var originalContent = workspace.ReadText(StatusRelativePath);

        var edits = new List<SnippetEdit>();
        for (int i = 0; i < 15; i++)
        {
            // Each oldContent is about 600 chars of distinct, deeply indented lines that never occur in the file,
            // so every batch item fails with its own large error and the combined message is what must stay bounded.
            var oldContent = string.Concat(Enumerable.Repeat(
                $"placeholder block {i} that never occurs in the file\n" + new string(' ', 60) + $"continuation line for item {i}\n", 6));
            edits.Add(new SnippetEdit { FilePath = targetFile, OldContent = oldContent.TrimEnd('\n'), NewContent = "// unused" });
        }

        var result = await tools.ReplaceSnippetTool(
            reason: "test bounded batch rejection message",
            ProposedChangeAction.apply,
            batchEdits: [.. edits]);

        Assert.That(result.IsError, Is.True);
        var message = result.ErrorData!.Message;
        Assert.That(message.Length, Is.LessThan(12800), "the combined rejection must stay bounded");
        for (int i = 0; i < 15; i++)
        {
            Assert.That(message, Does.Contain($"batchEdits[{i}]"));
        }

        Assert.That(workspace.ReadText(StatusRelativePath), Is.EqualTo(originalContent), "a rejected batch must not write anything");
    }
}
