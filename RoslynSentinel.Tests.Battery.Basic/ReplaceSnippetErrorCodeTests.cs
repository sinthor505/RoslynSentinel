// Regression coverage for finding_batch_edit_errors_lose_tool_exception_code.
//
// A ReplaceSnippet batch whose oldContent matched several places answered errorCode
// "InvalidArgument" even though the message said "ambiguous": the batch loop caught the
// ToolAmbiguousMatchException and kept only its text. The code must follow the cause.

using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Engines.Basic;
using RoslynSentinel.Tools.Basic;

namespace RoslynSentinel.Tests.Battery.Basic;

[TestFixture]
public class ReplaceSnippetErrorCodeTests
{
    private const string DupHolderSource = "class DupHolder { void A() { Run(); } void B() { Run(); } void Run() { } }";

    private sealed record Harness(TestSolutionFixture Fixture, PersistentWorkspaceManager Workspace, WorkspaceTools Tools) : IDisposable
    {
        public string DupHolderPath => Path.Combine(Fixture.SolutionDirectory, "ContosoOrders.Core", "DupHolder.cs");

        public void Dispose()
        {
            Workspace.Dispose();
            Fixture.Dispose();
        }
    }

    private static async Task<Harness> CreateHarnessAsync()
    {
        var fixture = new TestSolutionFixture();
        var workspace = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        await workspace.LoadSolutionAsync(fixture.SolutionPath);
        await fixture.AddFileToSolution(workspace, Path.Combine("ContosoOrders.Core", "DupHolder.cs"), DupHolderSource);

        var config = new SentinelConfiguration();
        var diffEngine = new DiffEngine();
        var diagnosticEngine = new DiagnosticEngine(workspace);
        var validationEngine = new ValidationEngine(workspace, diffEngine, NullLogger<ValidationEngine>.Instance);
        var symbolNavigationEngine = new SymbolNavigationEngine(workspace, NullLogger<SymbolNavigationEngine>.Instance);
        var tools = new WorkspaceTools(
            workspace,
            validationEngine,
            diffEngine, diagnosticEngine,
            new SolutionManagementEngine(workspace),
            new StructuralRefinementEngine(workspace, config),
            new DependencyEngine(workspace),
            new ProjectConsistencyEngine(workspace),
            config, NullLogger<WorkspaceTools>.Instance,
            new BuildEngine(workspace, diagnosticEngine),
            symbolNavigationEngine,
            new TestRunEngine(workspace),
            new WorkspaceReadNavigationImpl(workspace, NullLogger<WorkspaceReadNavigationImpl>.Instance),
            WriteToolAdviceHelper.WithAllToolsExposed());
        return new Harness(fixture, workspace, tools);
    }

    [Test]
    public async Task ReplaceSnippet_Batch_AmbiguousOldContent_ReportsAmbiguousCodeAndNamesOldContentAsync()
    {
        using var h = await CreateHarnessAsync();
        var original = await File.ReadAllTextAsync(h.DupHolderPath);

        var result = await h.Tools.ReplaceSnippet(
            reason: "error code regression ambiguous batch",
            ProposedChangeAction.apply,
            batchEdits: [new SnippetEdit { FilePath = h.DupHolderPath, OldContent = "Run();", NewContent = "Run(1);" }]);

        Assert.That(result.IsSuccess, Is.False);
        Assert.That(result.ErrorData!.ErrorCode, Is.EqualTo(ToolErrorCode.Ambiguous));
        Assert.That(result.ErrorData.Message, Does.Contain("oldContent is ambiguous").And.Not.Contain("contextSnippet"),
            "the message must name the parameter the caller actually passed");
        Assert.That(await File.ReadAllTextAsync(h.DupHolderPath), Is.EqualTo(original), "a rejected batch must not write anything");
    }

    [Test]
    public async Task ReplaceSnippet_Batch_MissingOldContent_ReportsNotFoundCodeAsync()
    {
        using var h = await CreateHarnessAsync();

        var result = await h.Tools.ReplaceSnippet(
            reason: "error code regression missing batch",
            ProposedChangeAction.apply,
            batchEdits: [new SnippetEdit { FilePath = h.DupHolderPath, OldContent = "ThisTextIsNotInTheFile();", NewContent = "x" }]);

        Assert.That(result.IsSuccess, Is.False);
        Assert.That(result.ErrorData!.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound));
    }

    [Test]
    public async Task ReplaceSnippet_Batch_AmbiguousAndMissingTogether_ReportsAmbiguousCodeAsync()
    {
        using var h = await CreateHarnessAsync();

        var result = await h.Tools.ReplaceSnippet(
            reason: "error code regression mixed batch",
            ProposedChangeAction.apply,
            batchEdits:
            [
                new SnippetEdit { FilePath = h.DupHolderPath, OldContent = "Run();", NewContent = "Run(1);" },
                new SnippetEdit { FilePath = h.DupHolderPath, OldContent = "ThisTextIsNotInTheFile();", NewContent = "x" },
            ]);

        Assert.That(result.IsSuccess, Is.False);
        Assert.That(result.ErrorData!.ErrorCode, Is.EqualTo(ToolErrorCode.Ambiguous),
            "when every rejection is a lookup-style failure the actionable one (disambiguate) wins");
    }

    [Test]
    public async Task ReplaceSnippet_Single_AmbiguousOldContent_ReportsAmbiguousCodeAndNamesOldContentAsync()
    {
        using var h = await CreateHarnessAsync();

        var result = await h.Tools.ReplaceSnippet(
            reason: "error code regression ambiguous single",
            ProposedChangeAction.apply,
            filePath: h.DupHolderPath,
            oldContent: "Run();",
            newContent: "Run(1);");

        Assert.That(result.IsSuccess, Is.False);
        Assert.That(result.ErrorData!.ErrorCode, Is.EqualTo(ToolErrorCode.Ambiguous));
        Assert.That(result.ErrorData.Message, Does.Contain("oldContent is ambiguous").And.Not.Contain("contextSnippet"));
    }
}
