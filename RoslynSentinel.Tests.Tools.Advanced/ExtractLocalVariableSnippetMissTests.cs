using RoslynSentinel.Common;
using RoslynSentinel.Engines.Basic;
using RoslynSentinel.Tests.Fakes;
using RoslynSentinel.Tools.Advanced;
using RoslynSentinel.Tools.Basic;

namespace RoslynSentinel.Tests.Tools.Advanced;

// Step 5e slice 6 (inventory E-BRE5b and C6e): ExtractLocalVariable's snippet miss carries the formatter's facts plus an
// option sentence (GetFileOutline with the request's filePath prefilled, and the lineBefore/lineAfter narrowing), not the
// order "Re-check the snippet ... to disambiguate". An adjacent-line mismatch lists the lineBefore/lineAfter values that
// select each candidate instead of telling the caller to copy them. Each test builds its own workspace.
[TestFixture]
[Parallelizable(ParallelScope.All)]
public sealed class ExtractLocalVariableSnippetMissTests
{
    private const string Source =
        "public class C\r\n{\r\n    public int Compute(int n) => n;\r\n    public void M()\r\n    {\r\n        var x = Compute(7);\r\n        var y = Compute(7);\r\n    }\r\n}\r\n";

    [Test]
    [Category("RefactoringExtractionDocsTools")] // sentinel:auto-category
    public async Task ExtractLocalVariable_ExpressionNotInFile_NamesOutlineAndNarrowingOptionsWithoutAnOrder()
    {
        using var workspace = InMemoryWorkspace.Create(("C.cs", Source));
        var tools = BatteryToolFactory.CreateRefactoringExtractionDocsTools(workspace.Manager);

        var result = await tools.ExtractLocalVariableTool(reason: "snippet miss option test", workspace.PathOf("C.cs"), "Compute(99) + 1", "z");

        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData!.ErrorCode, Is.EqualTo(ToolErrorCode.InvalidArgument));
        var message = result.ErrorData.Message;
        Assert.That(message, Does.Contain("GetFileOutline(").And.Contain("C.cs"));
        Assert.That(message, Does.Contain("shows what the file declares; lineBefore/lineAfter (the single line immediately before/after) narrow a match to one place."));
        Assert.That(message, Does.Not.Contain("Re-check the snippet"));
        Assert.That(message, Does.Not.Contain("to disambiguate"));
        Assert.That(workspace.ReadText("C.cs"), Is.EqualTo(Source));
    }

    [Test]
    [Category("RefactoringExtractionDocsTools")] // sentinel:auto-category
    public async Task ExtractLocalVariable_LineBeforeMatchesNothing_ListsTheSelectingValuesAsFactsWithoutACopyOrder()
    {
        using var workspace = InMemoryWorkspace.Create(("C.cs", Source));
        var tools = BatteryToolFactory.CreateRefactoringExtractionDocsTools(workspace.Manager);

        var result = await tools.ExtractLocalVariableTool(
            reason: "adjacent line mismatch test", workspace.PathOf("C.cs"), "Compute(7)", "z", lineBefore: "this line is not in the file");

        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData!.ErrorCode, Is.EqualTo(ToolErrorCode.InvalidArgument));
        var message = result.ErrorData.Message;
        Assert.That(message, Does.Contain("but not next to the lineBefore you supplied"));
        Assert.That(message, Does.Contain("with the lineBefore/lineAfter values that select each"));
        Assert.That(message, Does.Not.Contain("copy the"));
        Assert.That(message, Does.Not.Contain("the one you meant"));
        Assert.That(workspace.ReadText("C.cs"), Is.EqualTo(Source));
    }
}
