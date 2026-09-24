// ReplaceSnippet's batch path (the 'batchEdits' parameter). Per the design doc
// (docs/current/proposal_batch_replacesnippet.md) every edit in a batch is anchored against its
// file's ORIGINAL content, never a prior edit's output, so these tests specifically cover: multiple
// batchEdits to one file, batchEdits spanning multiple files, overlap rejection, and the either/or validation
// against the singular filepath/oldContent/newContent parameters.
//
// A real on-disk solution (TestSolutionFixture + PersistentWorkspaceManager) is used, matching
// ReplaceSnippetSizeGuardTests, because batch cases assert on file contents after apply.

using Microsoft.Extensions.Logging.Abstractions;

namespace RoslynSentinel.Tests.Battery;

[TestFixture]
public class ReplaceSnippetBatchTests
{
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
        using var fixture = new TestSolutionFixture();
        using var workspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        await workspaceManager.LoadSolutionAsync(fixture.SolutionPath);
        var tools = BuildTools(workspaceManager);

        var targetFile = Path.Combine(fixture.SolutionDirectory, "ContosoOrders.Core", "OrderStatus.cs");
        var originalContent = await File.ReadAllTextAsync(targetFile);
        var lines = originalContent.Split('\n');
        // Two distinct, non-adjacent anchor lines in the same file -> both resolved against the
        // one original snapshot, not against each other's output.
        var firstAnchor = lines[0].TrimEnd('\r');
        var lastNonEmptyLine = lines.Reverse().First(l => !string.IsNullOrWhiteSpace(l)).TrimEnd('\r');

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
        using var fixture = new TestSolutionFixture();
        using var workspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        await workspaceManager.LoadSolutionAsync(fixture.SolutionPath);
        var tools = BuildTools(workspaceManager);

        var fileA = Path.Combine(fixture.SolutionDirectory, "ContosoOrders.Core", "OrderLine.cs");
        var fileB = Path.Combine(fixture.SolutionDirectory, "ContosoOrders.Core", "OrderStatus.cs");
        var anchorA = (await File.ReadAllTextAsync(fileA)).Split('\n')[0].TrimEnd('\r');
        var anchorB = (await File.ReadAllTextAsync(fileB)).Split('\n')[0].TrimEnd('\r');

        var result = await tools.ReplaceSnippet(
            reason: "test batch across files",
            ProposedChangeAction.apply,
            batchEdits:
            [
                new SnippetEdit { FilePath = fileA, OldContent = anchorA, NewContent = anchorA + " // touched-a" },
                new SnippetEdit { FilePath = fileB, OldContent = anchorB, NewContent = anchorB + " // touched-b" },
            ]);

        Assert.That(result.IsSuccess, Is.True, result.ErrorData?.Message);

        var newContentA = await File.ReadAllTextAsync(fileA);
        var newContentB = await File.ReadAllTextAsync(fileB);
        Assert.Multiple(() =>
        {
            Assert.That(newContentA, Does.Contain("// touched-a"));
            Assert.That(newContentB, Does.Contain("// touched-b"));
        });
    }

    [Test]
    public async Task ReplaceSnippet_BatchOverlappingMatchesSameFile_RejectsWithoutWritingAsync()
    {
        using var fixture = new TestSolutionFixture();
        using var workspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        await workspaceManager.LoadSolutionAsync(fixture.SolutionPath);
        var tools = BuildTools(workspaceManager);

        var targetFile = Path.Combine(fixture.SolutionDirectory, "ContosoOrders.Core", "OrderStatus.cs");
        var originalContent = await File.ReadAllTextAsync(targetFile);
        var firstLine = originalContent.Split('\n')[0].TrimEnd('\r');
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

        var unchangedContent = await File.ReadAllTextAsync(targetFile);
        Assert.That(unchangedContent, Is.EqualTo(originalContent), "an overlap rejection must not write anything");
    }

    [Test]
    public async Task ReplaceSnippet_BatchOneEditNotFound_RejectsWholeBatchWithoutWritingAsync()
    {
        using var fixture = new TestSolutionFixture();
        using var workspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        await workspaceManager.LoadSolutionAsync(fixture.SolutionPath);
        var tools = BuildTools(workspaceManager);

        var fileA = Path.Combine(fixture.SolutionDirectory, "ContosoOrders.Core", "OrderLine.cs");
        var originalContentA = await File.ReadAllTextAsync(fileA);
        var anchorA = originalContentA.Split('\n')[0].TrimEnd('\r');

        var result = await tools.ReplaceSnippet(
            reason: "test batch partial failure",
            ProposedChangeAction.apply,
            batchEdits:
            [
                new SnippetEdit { FilePath = fileA, OldContent = anchorA, NewContent = anchorA + " // should-not-land" },
                new SnippetEdit { FilePath = fileA, OldContent = "this text does not exist anywhere in the file", NewContent = "irrelevant" },
            ]);

        Assert.That(result.IsSuccess, Is.False);

        var unchangedContentA = await File.ReadAllTextAsync(fileA);
        Assert.That(unchangedContentA, Is.EqualTo(originalContentA),
            "one unmatched edit in a batch must roll back the whole batch, not partially apply it");
    }

    [Test]
    public async Task ReplaceSnippet_BothEditsAndSingularParamsSupplied_RejectsAsInvalidArgumentAsync()
    {
        using var fixture = new TestSolutionFixture();
        using var workspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        await workspaceManager.LoadSolutionAsync(fixture.SolutionPath);
        var tools = BuildTools(workspaceManager);

        var targetFile = Path.Combine(fixture.SolutionDirectory, "ContosoOrders.Core", "OrderStatus.cs");
        var anchor = (await File.ReadAllTextAsync(targetFile)).Split('\n')[0].TrimEnd('\r');

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
        using var fixture = new TestSolutionFixture();
        using var workspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        await workspaceManager.LoadSolutionAsync(fixture.SolutionPath);
        var tools = BuildTools(workspaceManager);

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
        using var fixture = new TestSolutionFixture();
        using var workspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        await workspaceManager.LoadSolutionAsync(fixture.SolutionPath);
        var tools = BuildTools(workspaceManager);

        var targetFile = Path.Combine(fixture.SolutionDirectory, "ContosoOrders.Core", "OrderStatus.cs");
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
}
