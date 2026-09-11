// ReplaceSnippet's per-parameter size guard. Run 20260910-013550-398 died here: the old shared
// 200-char cap rejected an ordinary 6-line insertion, the message named all four bounds at once so
// the model kept guessing which it had hit, and the one escape hatch it named (WriteFile) was gated
// off for that run. These tests pin all three fixes — the raised caps, the per-bound message naming
// the actual value, and advice sourced from WriteToolAdviceHelper rather than a hardcoded name.
//
// A real on-disk solution (TestSolutionFixture + PersistentWorkspaceManager) is used rather than
// the in-memory TestSolutionBuilder path because the apply cases assert on file contents.

using Microsoft.Extensions.Logging.Abstractions;

namespace RoslynSentinel.Tests.Battery;

[TestFixture]
public class ReplaceSnippetSizeGuardTests
{
    private static SentinelWorkspaceTools BuildTools(
        IWorkspaceManager workspaceManager,
        WriteToolAdviceHelper? writeAdvice = null)
    {
        var config = new SentinelConfiguration();
        var diffEngine = new DiffEngine();
        var diagnosticEngine = new DiagnosticEngine(workspaceManager);
        return new SentinelWorkspaceTools(
            workspaceManager,
            new ValidationEngine(NullLogger<ValidationEngine>.Instance, workspaceManager, diffEngine),
            diffEngine, diagnosticEngine,
            new SolutionManagementEngine(workspaceManager),
            new StructuralRefinementEngine(workspaceManager, config),
            new DependencyEngine(workspaceManager),
            new ProjectConsistencyEngine(workspaceManager),
            config, NullLogger<SentinelWorkspaceTools>.Instance,
            new BuildEngine(workspaceManager, diagnosticEngine),
            new SymbolNavigationEngine(workspaceManager, NullLogger<SymbolNavigationEngine>.Instance),
            new TestRunEngine(workspaceManager),
            new WorkspaceReadNavigationTools(new WorkspaceReadNavigationImpl(workspaceManager, NullLogger<WorkspaceReadNavigationImpl>.Instance)),
            writeAdvice ?? WriteToolAdviceHelper.WithAllToolsExposed());
    }

    [Test]
    public async Task ReplaceSnippet_MidSizeEdit_IsNoLongerRejectedForSizeAsync()
    {
        // ~40 lines / ~1200 chars of newContent: comfortably over the old 20-line/200-char caps and
        // under the new 60-line/2000-char ones. The assertion is specifically that it isn't
        // *size*-rejected — validation may still reject the content on its own merits.
        using var fixture = new TestSolutionFixture();
        using var workspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        await workspaceManager.LoadSolutionAsync(fixture.SolutionPath);
        var tools = BuildTools(workspaceManager);

        var targetFile = Directory.EnumerateFiles(fixture.SolutionDirectory, "*.cs", SearchOption.AllDirectories).First();
        var originalContent = await File.ReadAllTextAsync(targetFile);
        var anchor = originalContent.Split('\n')[0];

        var newContent = anchor + "\n" + string.Join('\n',
            Enumerable.Range(0, 39).Select(i => $"// padding line {i}"));
        Assert.That(newContent.Length, Is.GreaterThan(200), "must exceed the old char cap to be a regression test");
        Assert.That(newContent.Length, Is.LessThan(2000), "must stay under the new char cap");

        var result = await tools.ReplaceSnippet(
            reason: "test message", ProposedChangeAction.validate, targetFile,
            oldContent: anchor, newContent: newContent);

        Assert.That(result.Error?.Message ?? "", Does.Not.Contain("limit"),
            "a mid-size edit must not be rejected for size");
    }

    [Test]
    public async Task ReplaceSnippet_OverCapNewContent_NamesTheBoundAndActualValueAsync()
    {
        using var fixture = new TestSolutionFixture();
        using var workspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        await workspaceManager.LoadSolutionAsync(fixture.SolutionPath);
        var tools = BuildTools(workspaceManager);

        var targetFile = Directory.EnumerateFiles(fixture.SolutionDirectory, "*.cs", SearchOption.AllDirectories).First();
        var anchor = (await File.ReadAllTextAsync(targetFile)).Split('\n')[0];
        var oversized = new string('x', 2500);

        var result = await tools.ReplaceSnippet(
            reason: "test message", ProposedChangeAction.validate, targetFile,
            oldContent: anchor, newContent: oversized);

        Assert.That(result.Success, Is.False);
        Assert.That(result.Error!.Message, Does.Contain("newContent is 2500 chars"),
            "the message must name the bound that tripped and the actual value");
        Assert.That(result.Error!.Message, Does.Contain("limit 2000"));
        Assert.That(result.Error!.Message, Does.Not.Contain("oldContent"),
            "bounds that were not exceeded must not be mentioned");
    }

    [Test]
    public async Task ReplaceSnippet_MultipleBoundsExceeded_ReportsEachOneAsync()
    {
        using var fixture = new TestSolutionFixture();
        using var workspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        await workspaceManager.LoadSolutionAsync(fixture.SolutionPath);
        var tools = BuildTools(workspaceManager);

        var targetFile = Directory.EnumerateFiles(fixture.SolutionDirectory, "*.cs", SearchOption.AllDirectories).First();
        var tooManyLines = string.Join('\n', Enumerable.Range(0, 80).Select(i => $"line {i}"));

        var result = await tools.ReplaceSnippet(
            reason: "test message", ProposedChangeAction.validate, targetFile,
            oldContent: tooManyLines, newContent: new string('y', 2500));

        Assert.That(result.Success, Is.False);
        Assert.Multiple(() =>
        {
            Assert.That(result.Error!.Message, Does.Contain("oldContent is 80 lines"));
            Assert.That(result.Error!.Message, Does.Contain("newContent is 2500 chars"));
        });
    }

    [Test]
    public async Task ReplaceSnippet_OverCapWithWholeFileWriteGatedOff_DoesNotNameWriteFileAsync()
    {
        // The run-398 regression, stated directly: with SentinelWholeFileWriteTools gated off (the
        // configuration that scores 26/26 — see project_wholefilewrite_gating_overnight_result_2026_09_08),
        // the size error must not send the agent to a tool it cannot call.
        using var fixture = new TestSolutionFixture();
        using var workspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        await workspaceManager.LoadSolutionAsync(fixture.SolutionPath);
        var tools = BuildTools(workspaceManager, new WriteToolAdviceHelper(["SentinelRefactoringTools"]));

        var targetFile = Directory.EnumerateFiles(fixture.SolutionDirectory, "*.cs", SearchOption.AllDirectories).First();
        var anchor = (await File.ReadAllTextAsync(targetFile)).Split('\n')[0];

        var result = await tools.ReplaceSnippet(
            reason: "test message", ProposedChangeAction.validate, targetFile,
            oldContent: anchor, newContent: new string('z', 2500));

        Assert.That(result.Success, Is.False);
        Assert.Multiple(() =>
        {
            Assert.That(result.Error!.Message, Does.Not.Contain("WriteFile"));
            Assert.That(result.Error!.Message, Does.Not.Contain("ApplyUnifiedDiff"));
            Assert.That(result.Error!.Message, Does.Contain("ReplaceSnippet"), "must still state a way forward");
        });
    }
}
