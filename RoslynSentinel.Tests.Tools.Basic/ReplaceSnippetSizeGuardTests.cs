// ReplaceSnippetTool's per-parameter size guard. Run 20260910-013550-398 died here: the old shared
// 200-char cap rejected an ordinary 6-line insertion, the message named all four bounds at once so
// the model kept guessing which it had hit, and the one escape hatch it named (WriteFileTool) was gated
// off for that run. These tests pin all three fixes -> the raised caps, the per-bound message naming
// the actual value, and advice sourced from WriteToolAdviceHelper rather than a hardcoded name.
//
// About the tool's code-correctness, so it runs on an InMemoryWorkspace (no temp directory, MSBuild load or disk
// write). See ModifyAttributeBatchTests for the pattern.

using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Engines.Basic;
using RoslynSentinel.Tests.Fakes;
using RoslynSentinel.Tools.Basic;

namespace RoslynSentinel.Tests.Battery.Basic;

[TestFixture]
[Parallelizable(ParallelScope.All)]
[Category("WorkspaceTools")] // sentinel:auto-category
public class ReplaceSnippetSizeGuardTests
{
    private const string TargetRelativePath = "ContosoOrders.Core/OrderStatus.cs";

    private const string TargetSource = """
    namespace ContosoOrders.Core;

    public enum OrderStatus
    {
        Pending,
        Shipped,
    }
    """;

    private static InMemoryWorkspace CreateWorkspace() => InMemoryWorkspace.Create((TargetRelativePath, TargetSource));

    private static string FirstLine(string text) => text.Split('\n')[0].TrimEnd('\r');

    private static WorkspaceTools BuildTools(
        IWorkspaceManager workspaceManager,
        WriteToolAdviceHelper? writeAdvice = null)
    {
        var config = TestEngines.SentinelConfiguration();
        var diffEngine = TestEngines.DiffEngine();
        var diagnosticEngine = TestEngines.DiagnosticEngine(workspaceManager);
        return TestTools.WorkspaceTools(
            workspaceManager, TestEngines.ValidationEngine(workspaceManager, diffEngine, NullLogger<ValidationEngine>.Instance), diffEngine, diagnosticEngine, TestEngines.StructuralRefinementEngine(workspaceManager, config), config, NullLogger<WorkspaceTools>.Instance, TestEngines.BuildEngine(workspaceManager, diagnosticEngine), TestEngines.SymbolNavigationEngine(workspaceManager, NullLogger<SymbolNavigationEngine>.Instance), TestEngines.TestRunEngine(workspaceManager), TestTools.WorkspaceReadNavigationImpl(workspaceManager, NullLogger<WorkspaceReadNavigationImpl>.Instance), writeAdvice ?? WriteToolAdviceHelper.WithAllToolsExposed());
    }

    [Test]
    public async Task ReplaceSnippet_MidSizeEdit_IsNoLongerRejectedForSizeAsync()
    {
        // ~40 lines / ~1200 chars of newContent: comfortably over the old 20-line/200-char caps and
        // under the current default caps (line/char limits are startup-configurable via
        // ReplaceSnippetOptions; these tests run against its built-in defaults). The assertion is
        // specifically that it isn't *size*-rejected -> validation may still reject the content on
        // its own merits.
        using var workspace = CreateWorkspace();
        var tools = BuildTools(workspace.Manager);

        var targetFile = workspace.PathOf(TargetRelativePath);
        var anchor = FirstLine(workspace.ReadText(TargetRelativePath));

        var newContent = anchor + "\n" + string.Join('\n',
            Enumerable.Range(0, 39).Select(i => $"// padding line {i}"));
        Assert.That(newContent.Length, Is.GreaterThan(200), "must exceed the old char cap to be a regression test");
        Assert.That(newContent.Length, Is.LessThan(2000), "must stay under the new char cap");

        var result = await tools.ReplaceSnippetTool(
            reason: "test message", ProposedChangeAction.validate, targetFile,
            oldContent: anchor, newContent: newContent);

        Assert.That(result.ErrorData?.Message ?? "", Does.Not.Contain("limit"),
            "a mid-size edit must not be rejected for size");
    }

    [Test]
    public async Task ReplaceSnippet_OverCapNewContent_NamesTheBoundAndActualValueAsync()
    {
        using var workspace = CreateWorkspace();
        var tools = BuildTools(workspace.Manager);

        var targetFile = workspace.PathOf(TargetRelativePath);
        var anchor = FirstLine(workspace.ReadText(TargetRelativePath));
        var oversized = new string('x', 2500);

        var result = await tools.ReplaceSnippetTool(
            reason: "test message", ProposedChangeAction.validate, targetFile,
            oldContent: anchor, newContent: oversized);

        Assert.That(!result.IsError, Is.False);
        Assert.That(result.ErrorData!.Message, Does.Contain("newContent is 2500 chars"),
            "the message must name the bound that tripped and the actual value");
        Assert.That(result.ErrorData!.Message, Does.Contain("limit 2000"));
        Assert.That(result.ErrorData!.Message, Does.Not.Contain("oldContent"),
            "bounds that were not exceeded must not be mentioned");
    }

    [Test]
    public async Task ReplaceSnippet_MultipleBoundsExceeded_ReportsEachOneAsync()
    {
        using var workspace = CreateWorkspace();
        var tools = BuildTools(workspace.Manager);

        var targetFile = workspace.PathOf(TargetRelativePath);
        var tooManyLines = string.Join('\n', Enumerable.Range(0, 120).Select(i => $"line {i}"));

        var result = await tools.ReplaceSnippetTool(
            reason: "test message", ProposedChangeAction.validate, targetFile,
            oldContent: tooManyLines, newContent: new string('y', 2500));

        Assert.That(!result.IsError, Is.False);
        Assert.Multiple(() =>
        {
            Assert.That(result.ErrorData!.Message, Does.Contain("oldContent is 120 lines"));
            Assert.That(result.ErrorData!.Message, Does.Contain("newContent is 2500 chars"));
        });
    }

    [Test]
    public async Task ReplaceSnippet_OverCapWithWholeFileWriteGatedOff_DoesNotNameWriteFileAsync()
    {
        // The run-398 regression, stated directly: with WholeFileWriteTools gated off (the
        // configuration that scores 26/26 -> see project_wholefilewrite_gating_overnight_result_2026_09_08),
        // the size error must not send the agent to a tool it cannot call.
        using var workspace = CreateWorkspace();
        var tools = BuildTools(workspace.Manager, new WriteToolAdviceHelper(["RefactoringStructuralTools"]));

        var targetFile = workspace.PathOf(TargetRelativePath);
        var anchor = FirstLine(workspace.ReadText(TargetRelativePath));

        var result = await tools.ReplaceSnippetTool(
            reason: "test message", ProposedChangeAction.validate, targetFile,
            oldContent: anchor, newContent: new string('z', 2500));

        Assert.That(!result.IsError, Is.False);
        Assert.Multiple(() =>
        {
            Assert.That(result.ErrorData!.Message, Does.Not.Contain("WriteFile"));
            Assert.That(result.ErrorData!.Message, Does.Not.Contain("ApplyUnifiedDiff"));
            Assert.That(result.ErrorData!.Message, Does.Contain("ReplaceSnippet"), "must still state a way forward");
        });
    }

    [Test]
    public async Task ReplaceSnippet_OversizedSeveralMembers_AdvisesOneMemberCallPerMemberAsync()
    {
        // Several small methods (no usings) over 6000 chars: the advice must steer to one Member call per
        // member, not to the single whole-member sentence that a one-method rejection gets.
        using var workspace = CreateWorkspace();
        var tools = BuildTools(workspace.Manager);

        var targetFile = workspace.PathOf(TargetRelativePath);
        var anchor = FirstLine(workspace.ReadText(TargetRelativePath));
        var filler = new string('-', 480);
        var severalMethods = string.Join("\n\n", Enumerable.Range(0, 12).Select(i =>
            $"    public int Method{i}() {{ return {i}; /* {filler} */ }}"));
        Assert.That(severalMethods.Length, Is.GreaterThan(6000), "must exceed the char cap by a wide margin");

        var result = await tools.ReplaceSnippetTool(
            reason: "test message", ProposedChangeAction.validate, targetFile,
            oldContent: anchor, newContent: severalMethods);

        Assert.That(result.IsError, Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(result.ErrorData!.Message, Does.Contain("several members"));
            Assert.That(result.ErrorData!.Message, Does.Not.Contain("whole method/property/constructor"));
        });
    }

    [Test]
    public async Task ReplaceSnippet_OversizedSingleMethod_KeepsWholeMemberAdviceAsync()
    {
        // One large method over 6000 chars: a single declaration keeps the whole-member wording.
        using var workspace = CreateWorkspace();
        var tools = BuildTools(workspace.Manager);

        var targetFile = workspace.PathOf(TargetRelativePath);
        var anchor = FirstLine(workspace.ReadText(TargetRelativePath));
        var body = string.Join("\n", Enumerable.Range(0, 60).Select(i =>
            $"        total += {i} * 2; // {new string('-', 100)}"));
        var oneMethod = "    public int Compute()\n    {\n        var total = 0;\n" + body + "\n        return total;\n    }";
        Assert.That(oneMethod.Length, Is.GreaterThan(6000), "must exceed the char cap by a wide margin");

        var result = await tools.ReplaceSnippetTool(
            reason: "test message", ProposedChangeAction.validate, targetFile,
            oldContent: anchor, newContent: oneMethod);

        Assert.That(result.IsError, Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(result.ErrorData!.Message, Does.Contain("whole method/property/constructor"));
            Assert.That(result.ErrorData!.Message, Does.Not.Contain("several members"));
        });
    }
}
