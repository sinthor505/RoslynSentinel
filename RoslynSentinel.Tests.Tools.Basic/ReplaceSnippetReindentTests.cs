// newContent is re-indented to the file's indentation unit before it is spliced, on the single and batch
// ReplaceSnippet paths. These tests pin the written text and the summary note for tab-indented, mid-line
// and already-correct newContent. Runs on an InMemoryWorkspace (no temp directory, MSBuild load or disk write).

using RoslynSentinel.Engines.Basic;
using RoslynSentinel.Tests.Fakes;
using RoslynSentinel.Tools.Basic;

namespace RoslynSentinel.Tests.Battery.Basic;

[TestFixture]
[Parallelizable(ParallelScope.All)]
[Category("WorkspaceTools")] // sentinel:auto-category
public class ReplaceSnippetReindentTests
{
    private const string RelativePath = "ContosoOrders.Core/Calc.cs";

    private const string Source =
        "namespace Demo;\n" +
        "\n" +
        "public class Calc\n" +
        "{\n" +
        "    public int Compute()\n" +
        "    {\n" +
        "        var total = 1;\n" +
        "        return total;\n" +
        "    }\n" +
        "}\n";

    // The file's own two lines at 8 spaces (method body indentation).
    private const string OldBlock = "        var total = 1;\n        return total;";

    // Tab-indented replacement: the file uses 4-space indentation, so these must land at 8 spaces.
    private const string TabIndentedNewContent = "\t\tvar total = 1;\n\t\tvar bonus = 2;\n\t\treturn total + bonus;";

    // The same three lines already at the file's indentation.
    private const string AbsoluteNewContent = "        var total = 1;\n        var bonus = 2;\n        return total + bonus;";

    // Old text starts mid-line, after the 8-space indent; the later lines of newContent are already absolute.
    private const string MidLineOld = "var total = 1;\n        return total;";
    private const string MidLineNew = "var total = 1;\n        var bonus = 2;\n        return total + bonus;";

    private const string AdjustedNote = "newContent indentation was adjusted to the file's on";

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
    public async Task ReplaceSnippet_Single_ExactMatchTabIndentedNewContent_WritesFileIndentationAsync()
    {
        using var h = CreateHarness();

        var result = await h.Tools.ReplaceSnippetTool(
            reason: "tab-indented newContent over an exact match",
            ProposedChangeAction.apply,
            filePath: h.Path,
            oldContent: OldBlock,
            newContent: TabIndentedNewContent);

        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        var expectedNew = "        var total = 1;\n        var bonus = 2;\n        return total + bonus;";
        Assert.That(h.Text, Is.EqualTo(Source.Replace(OldBlock, expectedNew)),
            "tab-indented newContent must be written at the file's 8-space indentation");
        Assert.That(h.Text, Does.Not.Contain("\t"), "no tab character may reach the file");
    }

    [Test]
    public async Task ReplaceSnippet_Batch_ExactMatchTabIndentedNewContent_WritesFileIndentationAsync()
    {
        using var h = CreateHarness();

        var result = await h.Tools.ReplaceSnippetTool(
            reason: "batch tab-indented newContent over an exact match",
            ProposedChangeAction.apply,
            batchEdits: [new SnippetEdit { FilePath = h.Path, OldContent = OldBlock, NewContent = TabIndentedNewContent }]);

        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        var expectedNew = "        var total = 1;\n        var bonus = 2;\n        return total + bonus;";
        Assert.That(h.Text, Is.EqualTo(Source.Replace(OldBlock, expectedNew)),
            "tab-indented newContent must be written at the file's 8-space indentation");
        Assert.That(h.Text, Does.Not.Contain("\t"), "no tab character may reach the file");
    }

    [Test]
    public async Task ReplaceSnippet_Single_MidLineOldContent_SecondLineNotDoubleIndentedAsync()
    {
        using var h = CreateHarness();

        var result = await h.Tools.ReplaceSnippetTool(
            reason: "old text starts mid-line, later lines absolute",
            ProposedChangeAction.apply,
            filePath: h.Path,
            oldContent: MidLineOld,
            newContent: MidLineNew);

        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        Assert.That(h.Text, Is.EqualTo(Source.Replace(MidLineOld, MidLineNew)),
            "the second and third lines must stay at 8 spaces, not 16");
    }

    [Test]
    public async Task ReplaceSnippet_Single_IndentAdjusted_SummaryStatesLineCountAsync()
    {
        using var h = CreateHarness();

        var result = await h.Tools.ReplaceSnippetTool(
            reason: "tab-indented newContent must report the adjustment",
            ProposedChangeAction.apply,
            filePath: h.Path,
            oldContent: OldBlock,
            newContent: TabIndentedNewContent);

        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        var summary = result.SuccessData!.ReplacementResult!.Summary;
        Assert.That(summary, Does.Contain(AdjustedNote));
        Assert.That(summary, Does.Match(@"adjusted to the file's on [1-9]\d* line\(s\)"),
            "the summary must state a non-zero adjusted line count");
    }

    [Test]
    public async Task ReplaceSnippet_Single_CorrectlyIndentedNewContent_WritesVerbatimAndNoNoteAsync()
    {
        using var h = CreateHarness();

        var result = await h.Tools.ReplaceSnippetTool(
            reason: "newContent already at the file's indentation",
            ProposedChangeAction.apply,
            filePath: h.Path,
            oldContent: OldBlock,
            newContent: AbsoluteNewContent);

        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        Assert.That(h.Text, Is.EqualTo(Source.Replace(OldBlock, AbsoluteNewContent)),
            "correctly indented newContent must be written verbatim");
        Assert.That(result.SuccessData!.ReplacementResult!.Summary, Does.Not.Contain("indentation was adjusted"));
    }
}
