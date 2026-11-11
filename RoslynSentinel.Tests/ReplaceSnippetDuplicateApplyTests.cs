// Fixture tests (no model) for the duplicate-apply no-op on ReplaceSnippet (Batch H4 R1): an identical edit sent twice in one
// turn (action replace, then apply) must not fail "oldContent was not found" after the first call already landed. Runs on
// InMemoryWorkspace (real compile gate, no disk writes). Each test uses its own file path, because AppliedEditMemory is
// process-wide and keyed by full path.
using RoslynSentinel.Common;
using RoslynSentinel.Engines.Basic;
using RoslynSentinel.Tests.Battery.Basic;
using RoslynSentinel.Tests.Fakes;
using RoslynSentinel.Tools.Basic;

namespace RoslynSentinel.Tests;

[TestFixture]
[Category("WorkspaceTools")]
public class ReplaceSnippetDuplicateApplyTests
{
    private const string Source = """
    namespace ContosoOrders.Core;

    public class Totals
    {
        public int Total() => 1;
    }
    """;

    [Test]
    public async Task SameEditSentTwice_SecondCallIsANoOpSuccessNamingTheFirstChangeId()
    {
        var relativePath = $"DuplicateApply/Totals_{Guid.NewGuid():n}.cs";
        using var workspace = InMemoryWorkspace.Create((relativePath, Source));
        var tools = TestTools.WorkspaceTools(workspace.Manager);

        var first = await tools.ReplaceSnippetTool(
            reason: "change the total from 1 to 2",
            ProposedChangeAction.apply,
            filePath: workspace.PathOf(relativePath),
            oldContent: "public int Total() => 1;",
            newContent: "public int Total() => 2;");
        Assert.That(first.IsError, Is.False, first.ErrorData?.Message);
        var afterFirst = workspace.ReadText(relativePath);

        var second = await tools.ReplaceSnippetTool(
            reason: "send the same edit again in the same turn",
            ProposedChangeAction.apply,
            filePath: workspace.PathOf(relativePath),
            oldContent: "public int Total() => 1;",
            newContent: "public int Total() => 2;");

        Assert.Multiple(() =>
        {
            Assert.That(second.IsError, Is.False, second.ErrorData?.Message);
            Assert.That(second.SuccessData?.ReplacementResult?.Summary ?? string.Empty, Does.Contain("already applied"));
            Assert.That(second.SuccessData?.ReplacementResult?.Summary ?? string.Empty, Does.Contain("changeId " + first.SuccessData?.ChangeId));
            Assert.That(workspace.ReadText(relativePath), Is.EqualTo(afterFirst));
        });
    }

    [Test]
    public async Task DifferentEditWhoseOldContentIsMissing_StillFailsNotFound()
    {
        var relativePath = $"DuplicateApply/Totals_{Guid.NewGuid():n}.cs";
        using var workspace = InMemoryWorkspace.Create((relativePath, Source));
        var tools = TestTools.WorkspaceTools(workspace.Manager);

        var first = await tools.ReplaceSnippetTool(
            reason: "change the total from 1 to 2",
            ProposedChangeAction.apply,
            filePath: workspace.PathOf(relativePath),
            oldContent: "public int Total() => 1;",
            newContent: "public int Total() => 2;");
        Assert.That(first.IsError, Is.False, first.ErrorData?.Message);

        var other = await tools.ReplaceSnippetTool(
            reason: "edit a line that was never in the file",
            ProposedChangeAction.apply,
            filePath: workspace.PathOf(relativePath),
            oldContent: "public int Total() => 7;",
            newContent: "public int Total() => 3;");

        Assert.That(other.IsError, Is.True);
        Assert.That(other.ErrorData!.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound), other.ErrorData.Message);
    }
}
