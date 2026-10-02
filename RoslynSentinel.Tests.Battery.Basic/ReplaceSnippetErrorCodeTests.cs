// Regression coverage for finding_batch_edit_errors_lose_tool_exception_code.
//
// A ReplaceSnippet batch whose oldContent matched several places answered errorCode
// "InvalidArgument" even though the message said "ambiguous": the batch loop caught the
// ToolAmbiguousMatchException and kept only its text. The code must follow the cause.
//
// About the tool's error codes, so it runs on an InMemoryWorkspace (no temp directory, MSBuild load or disk write).

using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Engines.Basic;
using RoslynSentinel.Tests.Fakes;
using RoslynSentinel.Tools.Basic;

namespace RoslynSentinel.Tests.Battery.Basic;

[TestFixture]
[Parallelizable(ParallelScope.All)]
public class ReplaceSnippetErrorCodeTests
{
    private const string DupHolderRelativePath = "ContosoOrders.Core/DupHolder.cs";
    private const string DupHolderSource = "class DupHolder { void A() { Run(); } void B() { Run(); } void Run() { } }";

    private sealed record Harness(InMemoryWorkspace Workspace, WorkspaceTools Tools) : IDisposable
    {
        public string DupHolderPath => Workspace.PathOf(DupHolderRelativePath);

        public string DupHolderText => Workspace.ReadText(DupHolderRelativePath);

        public void Dispose() => Workspace.Dispose();
    }

    private static Harness CreateHarness()
    {
        var workspace = InMemoryWorkspace.Create((DupHolderRelativePath, DupHolderSource));
        var manager = workspace.Manager;

        var config = new SentinelConfiguration();
        var diffEngine = new DiffEngine();
        var diagnosticEngine = new DiagnosticEngine(manager);
        var validationEngine = new ValidationEngine(manager, diffEngine, NullLogger<ValidationEngine>.Instance);
        var symbolNavigationEngine = new SymbolNavigationEngine(manager, NullLogger<SymbolNavigationEngine>.Instance);
        var tools = new WorkspaceTools(
            manager,
            validationEngine,
            diffEngine, diagnosticEngine,
            new SolutionManagementEngine(manager),
            new StructuralRefinementEngine(manager, config),
            new DependencyEngine(manager),
            new ProjectConsistencyEngine(manager),
            config, NullLogger<WorkspaceTools>.Instance,
            new BuildEngine(manager, diagnosticEngine),
            symbolNavigationEngine,
            new TestRunEngine(manager),
            new WorkspaceReadNavigationImpl(manager, NullLogger<WorkspaceReadNavigationImpl>.Instance),
            WriteToolAdviceHelper.WithAllToolsExposed());
        return new Harness(workspace, tools);
    }

    [Test]
    public async Task ReplaceSnippet_Batch_AmbiguousOldContent_ReportsAmbiguousCodeAndNamesOldContentAsync()
    {
        using var h = CreateHarness();
        var original = h.DupHolderText;

        var result = await h.Tools.ReplaceSnippet(
            reason: "error code regression ambiguous batch",
            ProposedChangeAction.apply,
            batchEdits: [new SnippetEdit { FilePath = h.DupHolderPath, OldContent = "Run();", NewContent = "Run(1);" }]);

        Assert.That(result.IsSuccess, Is.False);
        Assert.That(result.ErrorData!.ErrorCode, Is.EqualTo(ToolErrorCode.Ambiguous));
        Assert.That(result.ErrorData.Message, Does.Contain("oldContent is ambiguous").And.Not.Contain("contextSnippet"),
            "the message must name the parameter the caller actually passed");
        Assert.That(h.DupHolderText, Is.EqualTo(original), "a rejected batch must not write anything");
    }

    [Test]
    public async Task ReplaceSnippet_Batch_MissingOldContent_ReportsNotFoundCodeAsync()
    {
        using var h = CreateHarness();

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
        using var h = CreateHarness();

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
        using var h = CreateHarness();

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
