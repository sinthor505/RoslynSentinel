// ReplaceSnippet's batch path (the 'batchEdits' parameter). Per the design doc
// (docs/current/proposal_batch_replacesnippet.md) every edit in a batch is anchored against its
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

    private static WorkspaceTools BuildTools(IWorkspaceManager workspaceManager)
    {
        var config = new SentinelConfiguration();
        var diffEngine = new DiffEngine();
        var diagnosticEngine = new DiagnosticEngine(workspaceManager);
        return new WorkspaceTools(
            workspaceManager,
            new ValidationEngine(workspaceManager, diffEngine, NullLogger<ValidationEngine>.Instance),
            diffEngine, diagnosticEngine,
            new SolutionManagementEngine(workspaceManager),
            new StructuralRefinementEngine(workspaceManager, config),
            new DependencyEngine(workspaceManager),
            new ProjectConsistencyEngine(workspaceManager),
            config, NullLogger<WorkspaceTools>.Instance,
            new BuildEngine(workspaceManager, diagnosticEngine),
            new SymbolNavigationEngine(workspaceManager, NullLogger<SymbolNavigationEngine>.Instance),
            new TestRunEngine(workspaceManager),
            new WorkspaceReadNavigationImpl(workspaceManager, NullLogger<WorkspaceReadNavigationImpl>.Instance),
            WriteToolAdviceHelper.WithAllToolsExposed());
    }

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

        var result = await tools.ReplaceSnippet(
            reason: "test batch same file",
            ProposedChangeAction.validate,
            batchEdits:
            [
                new SnippetEdit { FilePath = targetFile, OldContent = firstAnchor, NewContent = firstAnchor + " // edit-a" },
                new SnippetEdit { FilePath = targetFile, OldContent = lastNonEmptyLine, NewContent = lastNonEmptyLine + " // edit-b" },
            ]);

        Assert.That(result.IsSuccess, Is.True, result.ErrorData?.Message);
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

        var result = await tools.ReplaceSnippet(
            reason: "test batch across files",
            ProposedChangeAction.apply,
            batchEdits:
            [
                new SnippetEdit { FilePath = fileA, OldContent = anchorA, NewContent = anchorA + " // touched-a" },
                new SnippetEdit { FilePath = fileB, OldContent = anchorB, NewContent = anchorB + " // touched-b" },
            ]);

        Assert.That(result.IsSuccess, Is.True, result.ErrorData?.Message);

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

        var result = await tools.ReplaceSnippet(
            reason: "test batch overlap rejection",
            ProposedChangeAction.apply,
            batchEdits:
            [
                new SnippetEdit { FilePath = targetFile, OldContent = firstLine, NewContent = "// replaced-whole-line" },
                new SnippetEdit { FilePath = targetFile, OldContent = overlappingFragment, NewContent = "// replaced-fragment" },
            ]);

        Assert.That(result.IsSuccess, Is.False);
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

        var result = await tools.ReplaceSnippet(
            reason: "test batch partial failure",
            ProposedChangeAction.apply,
            batchEdits:
            [
                new SnippetEdit { FilePath = fileA, OldContent = anchorA, NewContent = anchorA + " // should-not-land" },
                new SnippetEdit { FilePath = fileA, OldContent = "this text does not exist anywhere in the file", NewContent = "irrelevant" },
            ]);

        Assert.That(result.IsSuccess, Is.False);

        var unchangedContentA = workspace.ReadText(LineRelativePath);
        Assert.That(unchangedContentA, Is.EqualTo(originalContentA),
            "one unmatched edit in a batch must roll back the whole batch, not partially apply it");
    }

    [Test]
    public async Task ReplaceSnippet_BothEditsAndSingularParamsSupplied_RejectsAsInvalidArgumentAsync()
    {
        using var workspace = CreateWorkspace();
        var tools = BuildTools(workspace.Manager);

        var targetFile = workspace.PathOf(StatusRelativePath);
        var anchor = FirstLine(workspace.ReadText(StatusRelativePath));

        var result = await tools.ReplaceSnippet(
            reason: "test both supplied",
            ProposedChangeAction.validate,
            filePath: targetFile,
            oldContent: anchor,
            newContent: anchor + " // x",
            batchEdits: [new SnippetEdit { FilePath = targetFile, OldContent = anchor, NewContent = anchor + " // y" }]);

        Assert.That(result.IsSuccess, Is.False);
        Assert.That(result.ErrorData!.Message, Does.Contain("not both"));
    }

    [Test]
    public async Task ReplaceSnippet_EmptyEditsArray_RejectsAsInvalidArgumentAsync()
    {
        using var workspace = CreateWorkspace();
        var tools = BuildTools(workspace.Manager);

        var result = await tools.ReplaceSnippet(
            reason: "test empty batchEdits",
            ProposedChangeAction.validate,
            batchEdits: []);

        Assert.That(result.IsSuccess, Is.False);
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

        var result = await tools.ReplaceSnippet(
            reason: "test over cap",
            ProposedChangeAction.validate,
            batchEdits: edits);

        Assert.That(result.IsSuccess, Is.False);
        Assert.That(result.ErrorData!.Message, Does.Contain("20"));
    }

    [Test]
    public async Task ReplaceSnippet_BatchOversizeEdit_AppendsEscapeHatchAdviceOnceAsync()
    {
        // The single-edit path always carried escape-hatch advice on a size rejection; the batch
        // path used to list the per-edit bounds with no way forward. Two oversized edits must still
        // yield exactly one advice sentence, led by Member(replace).
        using var workspace = CreateWorkspace();
        var tools = BuildTools(workspace.Manager);

        var targetFile = workspace.PathOf(StatusRelativePath);
        var anchor = FirstLine(workspace.ReadText(StatusRelativePath));

        var result = await tools.ReplaceSnippet(
            reason: "test batch oversize advice",
            ProposedChangeAction.validate,
            batchEdits:
            [
                new SnippetEdit { FilePath = targetFile, OldContent = anchor, NewContent = new string('x', 2500) },
                new SnippetEdit { FilePath = targetFile, OldContent = anchor, NewContent = new string('y', 2500) },
            ]);

        Assert.That(result.IsSuccess, Is.False);
        var message = result.ErrorData!.Message;
        Assert.Multiple(() =>
        {
            Assert.That(message, Does.Contain("newContent is 2500 chars"));
            Assert.That(message, Does.Contain("Member(operation: replace)"));
            Assert.That(message.Split("Member(operation: replace)").Length - 1, Is.EqualTo(1),
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

        var result = await tools.ReplaceSnippet(
            reason: "test batch missing newContent",
            ProposedChangeAction.validate,
            batchEdits: [new SnippetEdit { FilePath = targetFile, OldContent = anchor, NewContent = null! }]);

        Assert.That(result.IsSuccess, Is.False);
        Assert.Multiple(() =>
        {
            Assert.That(result.ErrorData!.Message, Does.Contain("newContent is required"));
            Assert.That(result.ErrorData!.Message, Does.Not.Contain("Member(operation: replace)"),
                "size advice is noise for a non-size rejection");
        });
    }
}
