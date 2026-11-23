// Punchlist 22: the text edit tools (ReplaceSnippet single/batch/validate, ReplaceRange) accept an existing plain-text file that lies
// under the solution root and that no project owns, and agree with DeleteFile about which files exist. Disk-backed (real file edits),
// so each test owns a TestSolutionFixture.

using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Engines.Basic;
using RoslynSentinel.Tools.Basic;

namespace RoslynSentinel.Tests.Battery.Basic;

[TestFixture]
[Parallelizable(ParallelScope.All)]
[Category("WorkspaceTools")] // sentinel:auto-category
public class ReplaceSnippetNonProjectFileTests
{
    private sealed record Harness(TestSolutionFixture Fixture, PersistentWorkspaceManager Manager, WorkspaceTools Tools, WholeFileWriteTools Writer) : IDisposable
    {
        public string NotesPath => Path.Combine(Fixture.SolutionDirectory, "notes.md");

        public void Dispose()
        {
            Manager.Dispose();
            Fixture.Dispose();
        }
    }

    private static async Task<Harness> CreateHarnessAsync(string notesText)
    {
        var fixture = new TestSolutionFixture();
        var manager = TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        // 120 s load timeout: the 30 s default flakes under full/parallel runs (punchlist F1).
        await manager.LoadSolutionAsync(fixture.SolutionPath, null, TestSolutionFixture.LoadTimeout);
        var tools = TestTools.WorkspaceTools(manager);
        var diffEngine = TestEngines.DiffEngine();
        var validationEngine = TestEngines.ValidationEngine(manager, diffEngine, NullLogger<ValidationEngine>.Instance);
        var writer = TestTools.WholeFileWriteTools(manager, tools, validationEngine, diffEngine, NullLogger<WholeFileWriteTools>.Instance, TestEngines.SymbolNavigationEngine(manager, NullLogger<SymbolNavigationEngine>.Instance));
        var harness = new Harness(fixture, manager, tools, writer);
        await File.WriteAllTextAsync(harness.NotesPath, notesText);
        return harness;
    }

    [Test]
    public async Task ReplaceSnippet_MarkdownUnderRootOutsideEveryProject_AppliesAndCanBeUndoneAsync()
    {
        using var h = await CreateHarnessAsync("alpha\nbeta\ngamma\n");

        var result = await h.Tools.ReplaceSnippetTool(reason: "edit a non-project md", ProposedChangeAction.apply, filePath: h.NotesPath, oldContent: "beta", newContent: "BETA");

        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        Assert.That(await File.ReadAllTextAsync(h.NotesPath), Does.Contain("BETA").And.Not.Contain("beta"));
        Assert.That(result.SuccessData!.ChangeId, Is.Not.Null.And.Not.Empty, "the apply must write an operation blob so UndoChange works");

        var undo = await h.Tools.UndoChangeTool(reason: "undo the non-project edit", result.SuccessData.ChangeId!);

        Assert.That(undo.IsError, Is.False, undo.ErrorData?.Message);
        Assert.That(await File.ReadAllTextAsync(h.NotesPath), Does.Contain("beta"));
    }

    [Test]
    public async Task ReplaceSnippet_Validate_OnNonProjectMarkdown_WritesNothingAsync()
    {
        using var h = await CreateHarnessAsync("alpha\nbeta\ngamma\n");

        var result = await h.Tools.ReplaceSnippetTool(reason: "validate a non-project md edit", ProposedChangeAction.validate, filePath: h.NotesPath, oldContent: "beta", newContent: "BETA");

        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        Assert.That(await File.ReadAllTextAsync(h.NotesPath), Is.EqualTo("alpha\nbeta\ngamma\n"));
    }

    [Test]
    public async Task ReplaceSnippet_BatchEdits_OnNonProjectMarkdown_AppliesBothEditsAsync()
    {
        using var h = await CreateHarnessAsync("alpha\nbeta\ngamma\n");

        var result = await h.Tools.ReplaceSnippetTool(
            reason: "batch edit a non-project md",
            ProposedChangeAction.apply,
            batchEdits:
            [
                new SnippetEdit { FilePath = h.NotesPath, OldContent = "alpha", NewContent = "ALPHA" },
                new SnippetEdit { FilePath = h.NotesPath, OldContent = "gamma", NewContent = "GAMMA" },
            ]);

        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        var text = await File.ReadAllTextAsync(h.NotesPath);
        Assert.That(text, Does.Contain("ALPHA").And.Contain("beta").And.Contain("GAMMA"));
    }

    [Test]
    public async Task ReplaceRange_OnNonProjectMarkdown_ReplacesTheRangeAsync()
    {
        using var h = await CreateHarnessAsync("alpha\nbeta\ngamma\ndelta\n");

        var result = await h.Tools.ReplaceRangeTool(reason: "replace a range in a non-project md", filePath: h.NotesPath, firstLineText: "beta", lastLineText: "gamma", newContent: "MIDDLE");

        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        var text = await File.ReadAllTextAsync(h.NotesPath);
        Assert.That(text, Does.Contain("alpha").And.Contain("MIDDLE").And.Contain("delta").And.Not.Contain("beta").And.Not.Contain("gamma"));
    }

    [Test]
    public async Task ReplaceSnippetAndDeleteFile_AgreeOnNonProjectMarkdownUnderRootAsync()
    {
        using var h = await CreateHarnessAsync("alpha\nbeta\n");

        var edit = await h.Tools.ReplaceSnippetTool(reason: "edit before delete", ProposedChangeAction.apply, filePath: h.NotesPath, oldContent: "beta", newContent: "BETA");
        var delete = await h.Writer.DeleteFileTool(reason: "delete the same non-project md", h.NotesPath);

        Assert.That(edit.IsError, Is.False, edit.ErrorData?.Message);
        Assert.That(delete.IsError, Is.False, delete.ErrorData?.Message);
        Assert.That(File.Exists(h.NotesPath), Is.False);
    }

    [Test]
    public async Task ReplaceSnippet_ExistingFileOutsideSolutionRoot_KeepsNotFoundWhileDeleteFileKeepsAcceptingItAsync()
    {
        using var h = await CreateHarnessAsync("alpha\n");
        var outsideDir = Directory.CreateTempSubdirectory("rs-outside-root-");
        try
        {
            var outsidePath = Path.Combine(outsideDir.FullName, "outside.md");
            await File.WriteAllTextAsync(outsidePath, "alpha\nbeta\n");

            var edit = await h.Tools.ReplaceSnippetTool(reason: "edit a file outside the root", ProposedChangeAction.apply, filePath: outsidePath, oldContent: "beta", newContent: "BETA");

            Assert.That(edit.IsError, Is.True);
            Assert.That(edit.ErrorData!.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound));
            Assert.That(edit.ErrorData.Message, Does.Contain("outside the loaded solution"));
            Assert.That(await File.ReadAllTextAsync(outsidePath), Is.EqualTo("alpha\nbeta\n"));

            // Documented asymmetry: DeleteFile has never asked for a project document or a root, so it still accepts any existing file.
            var delete = await h.Writer.DeleteFileTool(reason: "delete a file outside the root", outsidePath);
            Assert.That(delete.IsError, Is.False, delete.ErrorData?.Message);
            Assert.That(File.Exists(outsidePath), Is.False);
        }
        finally
        {
            outsideDir.Delete(recursive: true);
        }
    }

    [Test]
    public async Task ReplaceSnippet_MissingMarkdownUnderRoot_KeepsNotFoundAndCreatesNothingAsync()
    {
        using var h = await CreateHarnessAsync("alpha\n");
        var missing = Path.Combine(h.Fixture.SolutionDirectory, "missing.md");

        var result = await h.Tools.ReplaceSnippetTool(reason: "edit a missing md", ProposedChangeAction.apply, filePath: missing, oldContent: "beta", newContent: "BETA");

        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData!.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound));
        Assert.That(File.Exists(missing), Is.False);
    }
}
