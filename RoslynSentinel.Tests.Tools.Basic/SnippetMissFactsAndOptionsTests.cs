// Step 5e slice 6: the snippet-miss texts of SnippetErrorFormatter (inventory C6a-C6d) carry facts and options only, no
// orders ("copy verbatim", "re-read the file and check the file path", "Supply the text"). Reached through ReplaceSnippet,
// the tool that hosts them. Runs on an InMemoryWorkspace (no temp directory, MSBuild load or disk write).

using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Common;
using RoslynSentinel.Engines.Basic;
using RoslynSentinel.Tests.Fakes;
using RoslynSentinel.Tools.Basic;

namespace RoslynSentinel.Tests.Battery.Basic;

[TestFixture]
[Parallelizable(ParallelScope.All)]
[Category("WorkspaceTools")] // sentinel:auto-category
public class SnippetMissFactsAndOptionsTests
{
    private const string RelativePath = "ContosoOrders.Core/SnippetMiss.cs";

    // The block "var total = Compute(); Log(total);" appears twice (lines 5-6 and 11-12), so a matchLine of 2 selects nothing.
    private const string Source =
        "class SnippetMiss\n" +
        "{\n" +
        "    void First()\n" +
        "    {\n" +
        "        var total = Compute();\n" +
        "        Log(total);\n" +
        "    }\n" +
        "\n" +
        "    void Second()\n" +
        "    {\n" +
        "        var total = Compute();\n" +
        "        Log(total);\n" +
        "    }\n" +
        "\n" +
        "    int Compute() => 1;\n" +
        "\n" +
        "    void Log(int x) { }\n" +
        "}\n";

    private const string FileLineOne = "        var total = Compute();";
    private const string FileLineTwo = "        Log(total);";
    private const string DuplicatedBlock = FileLineOne + "\n" + FileLineTwo;

    // One character off ("totals"), so it matches at no tier; the file's closest windows are listed.
    private const string NearMissBlock = "var total = Compute();\n        Log(totals);";

    // Nothing in the file resembles it, so no window reaches the similarity threshold.
    private const string UnrelatedBlock = "qqqq zzzz\nwwww jjjj";

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
        return new Harness(workspace, TestTools.WorkspaceTools(workspace.Manager));
    }

    private static Task<SentinelCallToolResult<ReplaceSnippetResult>> ReplaceAsync(Harness h, string oldContent, int? matchLine = null) =>
        h.Tools.ReplaceSnippetTool(
            reason: "snippet miss facts and options",
            ProposedChangeAction.apply,
            filePath: h.Path,
            oldContent: oldContent,
            newContent: Replacement,
            matchLine: matchLine);

    [Test]
    public async Task ReplaceSnippet_WhitespaceOnlyOldContent_StatesNothingWasSearchedAndNoCandidatesWithoutAnOrderAsync()
    {
        using var h = CreateHarness();

        var result = await ReplaceAsync(h, "   ");

        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData!.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound));
        var message = result.ErrorData.Message;
        Assert.That(message, Does.Contain("empty or whitespace only"));
        Assert.That(message, Does.Contain("nothing was searched"));
        Assert.That(message, Does.Contain("No candidates exist"));
        Assert.That(message, Does.Not.Contain("verbatim"));
        Assert.That(message, Does.Not.Contain("Supply"));
        Assert.That(h.Text, Is.EqualTo(Source));
    }

    [Test]
    public async Task ReplaceSnippet_NearMiss_ListsTheClosestWindowsAsFactsWithoutACopyOrderAsync()
    {
        using var h = CreateHarness();

        var result = await ReplaceAsync(h, NearMissBlock);

        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData!.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound));
        var message = result.ErrorData.Message;
        Assert.That(message, Does.Contain("Closest windows in the file, best first (the file's own text, as written in the file):"));
        Assert.That(message, Does.Contain(FileLineOne).And.Contain(FileLineTwo));
        Assert.That(message, Does.Not.Contain("copy the one you meant"));
        Assert.That(message, Does.Not.Contain("as your next oldContent"));
        Assert.That(h.Text, Is.EqualTo(Source));
    }

    [Test]
    public async Task ReplaceSnippet_NothingClose_StatesTheTextIsNotInTheFileSearchedWithoutAReReadOrderAsync()
    {
        using var h = CreateHarness();

        var result = await ReplaceAsync(h, UnrelatedBlock);

        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData!.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound));
        var message = result.ErrorData.Message;
        Assert.That(message, Does.Contain("No candidates: no window of the file reaches a similarity of 0.50"));
        Assert.That(message, Does.Contain("The text does not appear in the file that was searched."));
        Assert.That(message, Does.Not.Contain("re-read the file"));
        Assert.That(message, Does.Not.Contain("check the file path"));
        Assert.That(message, Does.Not.Contain("Closest windows"));
        Assert.That(h.Text, Is.EqualTo(Source));
    }

    [Test]
    public async Task ReplaceSnippet_StaleMatchLine_ListsTheSelectingValuesAsFactsWithoutACopyOrderAsync()
    {
        using var h = CreateHarness();

        var result = await ReplaceAsync(h, DuplicatedBlock, matchLine: 2);

        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData!.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound));
        var message = result.ErrorData.Message;
        Assert.That(message, Does.Contain("but not at the matchLine you supplied"));
        Assert.That(message, Does.Contain("Candidates in file order (the file's own text, with the matchLine/withinMember values that select each):"));
        Assert.That(message, Does.Contain("matchLine=5").And.Contain("matchLine=11"));
        Assert.That(message, Does.Not.Contain("the one you meant"));
        Assert.That(message, Does.Not.Contain("copy the"));
        Assert.That(h.Text, Is.EqualTo(Source));
    }
}
