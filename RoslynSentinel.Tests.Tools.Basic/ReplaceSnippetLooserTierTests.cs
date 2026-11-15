// Slice 4a of the whitespace-tolerance proposal: ReplaceSnippetTool and its batch form resolve oldContent through
// SnippetResolver (tier IgnoreLineEndings, case-sensitive) and report failures through SnippetErrorFormatter.
// An oldContent that differs from the file only in whitespace trivia (indentation, end-of-line) matches the file's
// C# tokens: the token step accepts it and the apply notes it. A looser-tier near-match that is not token-equivalent
// still fails with MatchedLooserThanRequested, shows the file's own lines, and writes nothing.
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

    private const string VerbatimPath = "ContosoOrders.Core/VerbatimDemo.cs";

    private const string VerbatimSource =
        "class VerbatimDemo\n" +
        "{\n" +
        "    string Text() => @\"line one\n" +
        "    line two\";\n" +
        "}\n";

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

        var tools = TestTools.WorkspaceTools(manager);
        return new Harness(workspace, tools);
    }

    [Test]
    public async Task ReplaceSnippet_Single_OldContentDiffersOnlyByIndentation_AppliesAndNotesTokenMatchAsync()
    {
        using var h = CreateHarness();

        var result = await h.Tools.ReplaceSnippetTool(
            reason: "indentation-only difference",
            ProposedChangeAction.apply,
            filePath: h.Path,
            oldContent: WronglyIndentedBlock,
            newContent: Replacement);

        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        Assert.That(result.SuccessData!.ReplacementResult!.Summary, Does.Contain("by its C# tokens"));
        Assert.That(h.Text, Is.EqualTo(Source.Replace(FileBlock, Replacement)));
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
    public async Task ReplaceSnippet_Batch_OldContentDiffersOnlyByIndentation_AppliesAsync()
    {
        using var h = CreateHarness();

        var result = await h.Tools.ReplaceSnippetTool(
            reason: "batch indentation-only difference",
            ProposedChangeAction.apply,
            batchEdits: [new SnippetEdit { FilePath = h.Path, OldContent = WronglyIndentedBlock, NewContent = Replacement }]);

        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        Assert.That(h.Text, Is.EqualTo(Source.Replace(FileBlock, Replacement)));
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

    [Test]
    public async Task ReplaceSnippet_Single_OldContentWithTabsWhereFileHasSpaces_AppliesAsync()
    {
        using var h = CreateHarness();

        var result = await h.Tools.ReplaceSnippetTool(
            reason: "tabs in oldContent where the file has spaces",
            ProposedChangeAction.apply,
            filePath: h.Path,
            oldContent: "\t\tvar total = Compute();\n\t\tLog(total);",
            newContent: Replacement);

        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        Assert.That(h.Text, Is.EqualTo(Source.Replace(FileBlock, Replacement)));
    }

    [Test]
    public async Task ReplaceSnippet_Single_LooserMatchStartingMidToken_StillFailsAsLooserTierAsync()
    {
        using var h = CreateHarness();

        var result = await h.Tools.ReplaceSnippetTool(
            reason: "looser match that starts mid-token",
            ProposedChangeAction.apply,
            filePath: h.Path,
            oldContent: "otal = Compute();\nLog(total);",
            newContent: Replacement);

        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData!.ErrorCode, Is.EqualTo(ToolErrorCode.MatchedLooserThanRequested));
        Assert.That(h.Text, Is.EqualTo(Source));
    }

    [Test]
    public async Task ReplaceSnippet_Single_IndentDifferenceInsideVerbatimString_StillFailsAsLooserTierAsync()
    {
        using var workspace = InMemoryWorkspace.Create((RelativePath, Source), (VerbatimPath, VerbatimSource));
        var tools = TestTools.WorkspaceTools(workspace.Manager);

        var result = await tools.ReplaceSnippetTool(
            reason: "indentation difference inside a verbatim string literal",
            ProposedChangeAction.apply,
            filePath: workspace.PathOf(VerbatimPath),
            oldContent: "string Text() => @\"line one\nline two\";",
            newContent: "string Text() => \"changed\";");

        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData!.ErrorCode, Is.EqualTo(ToolErrorCode.MatchedLooserThanRequested));
        Assert.That(workspace.ReadText(VerbatimPath), Is.EqualTo(VerbatimSource));
    }

    [Test]
    public async Task ReplaceSnippet_Single_TokenMatchWithUnindentedNewContent_WritesFileIndentationAsync()
    {
        using var h = CreateHarness();

        var result = await h.Tools.ReplaceSnippetTool(
            reason: "unindented newContent re-indented to the file",
            ProposedChangeAction.apply,
            filePath: h.Path,
            oldContent: WronglyIndentedBlock,
            newContent: "Log(Compute());\nLog(0);");

        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        Assert.That(h.Text, Is.EqualTo(Source.Replace(FileBlock, "        Log(Compute());\n        Log(0);")));
    }
}
