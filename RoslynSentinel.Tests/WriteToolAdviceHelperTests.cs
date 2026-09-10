using RoslynSentinel.Server.Basic;

namespace RoslynSentinel.Tests;

/// <summary>
/// Guards the one invariant that matters here: advice never names a tool the server didn't
/// register. Run 20260910-013550-398 livelocked for 24 turns on a size error whose only stated
/// escape hatch was WriteFile, which that run had gated off — the advice was unfollowable.
/// </summary>
/// <remarks>
/// Plain unit tests with no server bootstrap: <see cref="WriteToolAdviceHelper"/> takes its tool
/// set as a constructor argument rather than reading static state, so each case can just pass the
/// class list it wants.
/// </remarks>
[TestFixture]
public class WriteToolAdviceHelperTests
{
    private const string WholeFileWriteClass = "SentinelWholeFileWriteTools";
    private const string RefactoringClass = "SentinelRefactoringTools";

    [Test]
    public void IsExposed_UnknownToolName_ReturnsFalse()
    {
        // A name the map doesn't cover can't be vouched for, so it must not be advertised —
        // even when every class this server has is active.
        var helper = new WriteToolAdviceHelper([WholeFileWriteClass, RefactoringClass]);

        Assert.That(helper.IsExposed("SomeToolThatDoesNotExist"), Is.False);
    }

    [Test]
    public void AdviseForOversizedEdit_WriteFileExposed_PrefersWholeFileRewrite()
    {
        var helper = new WriteToolAdviceHelper([WholeFileWriteClass, RefactoringClass]);

        var advice = helper.AdviseForOversizedEdit("ReplaceSnippet");

        Assert.That(advice.Route, Is.EqualTo(WriteEscapeRoute.WholeFileRewrite));
        Assert.That(advice.ToolNames, Does.Contain("WriteFile"));
        Assert.That(advice.Sentence, Does.Contain("WriteFile"));
    }

    [Test]
    public void AdviseForOversizedEdit_NoWholeFileWriteClass_FallsBackToStructuralTools()
    {
        // The exact run-398 gating: SentinelWholeFileWriteTools is off, so all four of WriteFile,
        // DeleteFile, ApplyDiff and ApplyUnifiedDiff are unmentionable at once.
        var helper = new WriteToolAdviceHelper([RefactoringClass]);

        var advice = helper.AdviseForOversizedEdit("ReplaceSnippet");

        Assert.That(advice.Route, Is.EqualTo(WriteEscapeRoute.StructuredEdit));
        Assert.That(advice.Sentence, Does.Not.Contain("WriteFile"));
        Assert.That(advice.Sentence, Does.Not.Contain("ApplyDiff"));
        Assert.That(advice.Sentence, Does.Not.Contain("ApplyUnifiedDiff"));
        Assert.That(advice.Sentence, Does.Contain("ReplaceSnippet"), "must still say how to proceed");
    }

    [Test]
    public void AdviseForOversizedEdit_NoWriteToolAtAll_StillReturnsAReachableRoute()
    {
        var helper = new WriteToolAdviceHelper([]);

        var advice = helper.AdviseForOversizedEdit("ReplaceSnippet");

        Assert.That(advice.Route, Is.EqualTo(WriteEscapeRoute.SplitIntoSmallerEdits));
        Assert.That(advice.ToolNames, Is.Not.Empty, "advice must never be empty");
        Assert.That(advice.Sentence, Does.Contain("ReplaceSnippet"));
    }

    [Test]
    public void AdviseForOversizedEdit_NamesOnlyRegisteredTools_AcrossEveryClassSubset()
    {
        // Exhaustive over the two classes that declare escape-hatch tools: whatever the subset,
        // every tool named in the advice must be exposed. This is the invariant, stated directly.
        string[][] subsets =
        [
            [],
            [WholeFileWriteClass],
            [RefactoringClass],
            [WholeFileWriteClass, RefactoringClass],
        ];

        foreach (var subset in subsets)
        {
            var helper = new WriteToolAdviceHelper(subset);
            var advice = helper.AdviseForOversizedEdit("ReplaceSnippet");

            foreach (var toolName in advice.ToolNames)
            {
                // SplitIntoSmallerEdits names the rejecting tool itself, which is by definition
                // callable (the agent just called it) but isn't in the escape-hatch map.
                if (advice.Route == WriteEscapeRoute.SplitIntoSmallerEdits)
                {
                    continue;
                }

                Assert.That(helper.IsExposed(toolName), Is.True,
                    $"advice for classes [{string.Join(", ", subset)}] named unexposed tool '{toolName}'");
            }
        }
    }

    [Test]
    public void WithAllToolsExposed_ExposesEveryEscapeHatchTool()
    {
        var helper = WriteToolAdviceHelper.WithAllToolsExposed();

        Assert.Multiple(() =>
        {
            Assert.That(helper.IsExposed("WriteFile"), Is.True);
            Assert.That(helper.IsExposed("ApplyUnifiedDiff"), Is.True);
            Assert.That(helper.IsExposed("Member"), Is.True);
        });
    }
}
