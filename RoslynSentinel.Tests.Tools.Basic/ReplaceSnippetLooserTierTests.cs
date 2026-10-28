// Slice 4a of the whitespace-tolerance proposal: ReplaceSnippetTool and its batch form resolve oldContent through
// SnippetResolver (tier IgnoreLineEndings, case-sensitive) and report failures through SnippetErrorFormatter.
// An oldContent that differs from the file only by indentation matches at a looser tier than the edit path
// accepts: it must fail with MatchedLooserThanRequested, show the file's own lines, and write nothing.
// Runs on an InMemoryWorkspace (no temp directory, MSBuild load or disk write).

using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Engines.Basic;
using RoslynSentinel.Tests.Fakes;
using RoslynSentinel.Tools.Basic;

namespace RoslynSentinel.Tests.Battery.Basic;

[TestFixture]
[Parallelizable(ParallelScope.All)]
[Category("WorkspaceTools")] // sentinel:auto-category
public class ReplaceSnippetLooserTierTests
{
    private const string RelativePath = "ContosoOrders.Core/IndentDemo.cs";

    private const string Source =
        "class IndentDemo\n" +
        "{\n" +
        "    void Work()\n" +
        "    {\n" +
        "        var total = Compute();\n" +
        "        Log(total);\n" +
        "    }\n" +
        "\n" +
        "    int Compute() => 1;\n" +
        "\n" +
        "    void Log(int x) { }\n" +
        "}\n";

    // The file's own two lines.
    private const string FileLineOne = "        var total = Compute();";
    private const string FileLineTwo = "        Log(total);";
    private const string FileBlock = FileLineOne + "\n" + FileLineTwo;

    // Same text, second line indented by 4 spaces instead of 8: matches only at IgnoreIndentation.
    private const string WronglyIndentedBlock = "var total = Compute();\n    Log(total);";

    // One character off ("totals"), so it matches at no tier; the file's closest windows must be listed.
    private const string NoMatchBlock = "var total = Compute();\n        Log(totals);";

    private const string Replacement = "        Log(Compute());";

    private sealed record Harness(InMemoryWorkspace Workspace, WorkspaceTools Tools) : IDisposable
    {
        public string Path => Workspace.PathOf(RelativePath);

        public string Text => Workspace.ReadText(RelativePath);

        public void Dispose() => Workspace.Dispose();
    }

    private static Harness CreateHarness()
    {
        var workspace = InMemoryWorkspace.Create((RelativePath, Source));
        var manager = workspace.Manager;

        var config = TestEngines.SentinelConfiguration();
        var diffEngine = TestEngines.DiffEngine();
        var diagnosticEngine = TestEngines.DiagnosticEngine(manager);
        var validationEngine = TestEngines.ValidationEngine(manager, diffEngine, NullLogger<ValidationEngine>.Instance);
        var symbolNavigationEngine = TestEngines.SymbolNavigationEngine(manager, NullLogger<SymbolNavigationEngine>.Instance);
        var tools = TestTools.WorkspaceTools(
            manager,
            validationEngine,
            diffEngine, diagnosticEngine,
            TestEngines.SolutionManagementEngine(manager),
            TestEngines.StructuralRefinementEngine(manager, config),
            TestEngines.DependencyEngine(manager),
            TestEngines.ProjectConsistencyEngine(manager),
            config, NullLogger<WorkspaceTools>.Instance,
            TestEngines.BuildEngine(manager, diagnosticEngine),
            symbolNavigationEngine,
            new TestRunEngine(manager),
            TestTools.WorkspaceReadNavigationImpl(manager, NullLogger<WorkspaceReadNavigationImpl>.Instance),
            WriteToolAdviceHelper.WithAllToolsExposed());
        return new Harness(workspace, tools);
    }

    [Test]
    public async Task ReplaceSnippet_Single_OldContentDiffersOnlyByIndentation_FailsAsLooserTierShowsFileLinesAndWritesNothingAsync()
    {
        using var h = CreateHarness();

        var result = await h.Tools.ReplaceSnippetTool(
            reason: "indentation-only difference",
            ProposedChangeAction.apply,
            filePath: h.Path,
            oldContent: WronglyIndentedBlock,
            newContent: Replacement);

        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData!.ErrorCode, Is.EqualTo(ToolErrorCode.MatchedLooserThanRequested));
        Assert.That(result.ErrorData.Message, Does.Contain(FileLineOne).And.Contain(FileLineTwo),
            "the message must carry the file's own lines so the caller can copy them");
        Assert.That(result.ErrorData.Message, Does.Contain("NOT used").Or.Contain("nothing was applied"));
        Assert.That(h.Text, Is.EqualTo(Source), "a looser-tier near-match must never be applied");
    }

    [Test]
    public async Task ReplaceSnippet_Single_FileLinesSentBack_SucceedsAsync()
    {
        using var h = CreateHarness();

        var result = await h.Tools.ReplaceSnippetTool(
            reason: "resend the file's own lines",
            ProposedChangeAction.apply,
            filePath: h.Path,
            oldContent: FileBlock,
            newContent: Replacement);

        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        Assert.That(h.Text, Is.EqualTo(Source.Replace(FileBlock, Replacement)));
    }

    [Test]
    public async Task ReplaceSnippet_Single_NoMatchAtAnyTier_ListsClosestWindowsAndWritesNothingAsync()
    {
        using var h = CreateHarness();

        var result = await h.Tools.ReplaceSnippetTool(
            reason: "no match anywhere",
            ProposedChangeAction.apply,
            filePath: h.Path,
            oldContent: NoMatchBlock,
            newContent: Replacement);

        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData!.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound));
        Assert.That(result.ErrorData.Message, Does.Contain("Closest windows in the file"));
        Assert.That(result.ErrorData.Message, Does.Contain(FileLineOne).And.Contain(FileLineTwo),
            "the closest window must be the file's own text");
        Assert.That(h.Text, Is.EqualTo(Source));
    }

    [Test]
    public async Task ReplaceSnippet_Batch_OldContentDiffersOnlyByIndentation_FailsAsLooserTierShowsFileLinesAndWritesNothingAsync()
    {
        using var h = CreateHarness();

        var result = await h.Tools.ReplaceSnippetTool(
            reason: "batch indentation-only difference",
            ProposedChangeAction.apply,
            batchEdits: [new SnippetEdit { FilePath = h.Path, OldContent = WronglyIndentedBlock, NewContent = Replacement }]);

        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData!.ErrorCode, Is.EqualTo(ToolErrorCode.MatchedLooserThanRequested));
        Assert.That(result.ErrorData.Message, Does.Contain(FileLineOne).And.Contain(FileLineTwo));
        Assert.That(h.Text, Is.EqualTo(Source), "a rejected batch must not write anything");
    }

    [Test]
    public async Task ReplaceSnippet_Batch_FileLinesSentBack_SucceedsAsync()
    {
        using var h = CreateHarness();

        var result = await h.Tools.ReplaceSnippetTool(
            reason: "batch resend the file's own lines",
            ProposedChangeAction.apply,
            batchEdits: [new SnippetEdit { FilePath = h.Path, OldContent = FileBlock, NewContent = Replacement }]);

        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        Assert.That(h.Text, Is.EqualTo(Source.Replace(FileBlock, Replacement)));
    }
}
