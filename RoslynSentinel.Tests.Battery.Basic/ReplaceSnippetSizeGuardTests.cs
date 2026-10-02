// ReplaceSnippet's per-parameter size guard. Run 20260910-013550-398 died here: the old shared
// 200-char cap rejected an ordinary 6-line insertion, the message named all four bounds at once so
// the model kept guessing which it had hit, and the one escape hatch it named (WriteFile) was gated
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
            writeAdvice ?? WriteToolAdviceHelper.WithAllToolsExposed());
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

        var result = await tools.ReplaceSnippet(
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

        var result = await tools.ReplaceSnippet(
            reason: "test message", ProposedChangeAction.validate, targetFile,
            oldContent: anchor, newContent: oversized);

        Assert.That(result.IsSuccess, Is.False);
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

        var result = await tools.ReplaceSnippet(
            reason: "test message", ProposedChangeAction.validate, targetFile,
            oldContent: tooManyLines, newContent: new string('y', 2500));

        Assert.That(result.IsSuccess, Is.False);
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
        var tools = BuildTools(workspace.Manager, new WriteToolAdviceHelper(["RefactoringTools"]));

        var targetFile = workspace.PathOf(TargetRelativePath);
        var anchor = FirstLine(workspace.ReadText(TargetRelativePath));

        var result = await tools.ReplaceSnippet(
            reason: "test message", ProposedChangeAction.validate, targetFile,
            oldContent: anchor, newContent: new string('z', 2500));

        Assert.That(result.IsSuccess, Is.False);
        Assert.Multiple(() =>
        {
            Assert.That(result.ErrorData!.Message, Does.Not.Contain("WriteFile"));
            Assert.That(result.ErrorData!.Message, Does.Not.Contain("ApplyUnifiedDiff"));
            Assert.That(result.ErrorData!.Message, Does.Contain("ReplaceSnippet"), "must still state a way forward");
        });
    }
}
