using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using RoslynSentinel.Common;
using RoslynSentinel.Engines.Basic;
using RoslynSentinel.Tools.Advanced;
using RoslynSentinel.Tools.Basic;

namespace RoslynSentinel.Tests.Tools.Advanced;

// Not-found texts of the BasicRefactoringEngine producers that share DeclarationNotFoundDetail.
// Each test builds its own workspace, so the fixture is safe under ParallelScope.All.
[TestFixture]
[Parallelizable(ParallelScope.All)]
public class BasicRefactoringNameMissTests
{
    private const string CalcSource = """
public class OrderPricingCalculator
{
    public decimal CalcDisc(decimal total) { return total * 0.1m; }
    public decimal OrderCheckout(decimal total) { return total - CalcDisc(total); }
    public decimal GetTotal(decimal total) { return total; }
}
""";

    private static void LoadCalculator(out IWorkspaceManager workspace)
    {
        var persistent = Battery.Basic.TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Calc.cs", CalcSource)]);
        persistent.SetTestSolution(solution);
        workspace = persistent;
    }

    private static BasicRefactoringEngine EngineFor(IWorkspaceManager workspace)
    {
        return Battery.Basic.TestEngines.BasicRefactoringEngine(workspace, NullLogger<BasicRefactoringEngine>.Instance, Battery.Basic.TestEngines.SentinelConfiguration());
    }

    private static void AssertNoCandidateText(string message)
    {
        Assert.That(message, Does.Contain("has a similar name"));
        Assert.That(message, Does.Contain("does not exist"));
        Assert.That(message, Does.Not.Contain("Did you mean"));
        Assert.That(message, Does.Not.Contain("tell the user"));
        Assert.That(message, Does.Not.Contain("You MUST"));
    }

    [Test]
    public async Task AnalyzeControlFlow_CandidateName_ToolNamesNearestMethod()
    {
        LoadCalculator(out var workspace);
        var scan = BatteryToolFactory.CreateScanTools(workspace);
        var result = await scan.AnalyzeMethodTool(reason: "name miss candidate test", "Calc.cs", "CalcDsc", "controlFlow");
        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData!.Message, Does.Contain("Did you mean 'CalcDisc'"));
    }

    [Test]
    public async Task AnalyzeControlFlow_NoCandidateName_ToolStatesFactsOnly()
    {
        LoadCalculator(out var workspace);
        var scan = BatteryToolFactory.CreateScanTools(workspace);
        var result = await scan.AnalyzeMethodTool(reason: "name miss no-candidate test", "Calc.cs", "ApplyLoyaltyBonus", "controlFlow");
        Assert.That(result.IsError, Is.True);
        AssertNoCandidateText(result.ErrorData!.Message);
    }

    [Test]
    public async Task AnalyzeControlFlow_NoCandidateName_EngineThrowsToolNotFound()
    {
        LoadCalculator(out var workspace);
        var engine = EngineFor(workspace);
        var ex = await Assert.ThrowsAsync<ToolNotFoundException>(async () => await engine.AnalyzeControlFlowAsync("Calc.cs", "ApplyLoyaltyBonus"));
        AssertNoCandidateText(ex!.Message);
    }

    [Test]
    public async Task AnalyzeDataFlow_CandidateName_ToolNamesNearestMethod()
    {
        LoadCalculator(out var workspace);
        var scan = BatteryToolFactory.CreateScanTools(workspace);
        var result = await scan.AnalyzeMethodTool(reason: "name miss candidate test", "Calc.cs", "CalcDsc", "dataFlow");
        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData!.Message, Does.Contain("Did you mean 'CalcDisc'"));
    }

    [Test]
    public async Task AnalyzeDataFlow_NoCandidateName_ToolStatesFactsOnly()
    {
        LoadCalculator(out var workspace);
        var scan = BatteryToolFactory.CreateScanTools(workspace);
        var result = await scan.AnalyzeMethodTool(reason: "name miss no-candidate test", "Calc.cs", "ApplyLoyaltyBonus", "dataFlow");
        Assert.That(result.IsError, Is.True);
        AssertNoCandidateText(result.ErrorData!.Message);
    }

    [Test]
    public async Task GetSummaryComment_CandidateName_ToolNamesNearestDeclaration()
    {
        LoadCalculator(out var workspace);
        var tools = BatteryToolFactory.CreateRefactoringExtractionDocsTools(workspace);
        var result = await tools.SummaryCommentTool(reason: "name miss candidate test", "Calc.cs", AddRemoveViewAction.view, "CalcDsc");
        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData!.Message, Does.Contain("Did you mean 'CalcDisc'"));
    }

    [Test]
    public async Task GetSummaryComment_NoCandidateName_ToolStatesFactsOnly()
    {
        LoadCalculator(out var workspace);
        var tools = BatteryToolFactory.CreateRefactoringExtractionDocsTools(workspace);
        var result = await tools.SummaryCommentTool(reason: "name miss no-candidate test", "Calc.cs", AddRemoveViewAction.view, "ApplyLoyaltyBonus");
        Assert.That(result.IsError, Is.True);
        AssertNoCandidateText(result.ErrorData!.Message);
    }

    [Test]
    public async Task SummaryCommentAdd_UnknownName_WritesNothing()
    {
        LoadCalculator(out var workspace);
        var tools = BatteryToolFactory.CreateRefactoringExtractionDocsTools(workspace);
        var versionBefore = workspace.WorkspaceVersion;
        var result = await tools.SummaryCommentTool(reason: "name miss write-tool test", "Calc.cs", AddRemoveViewAction.add, "ApplyLoyaltyBonus", "Adds a loyalty bonus.");
        Assert.That(result.IsError, Is.True);
        Assert.That(workspace.WorkspaceVersion, Is.EqualTo(versionBefore));
    }

    [Test]
    public async Task UpdateXmlDocsFromSignature_CandidateName_EngineNamesNearestMethod()
    {
        LoadCalculator(out var workspace);
        var engine = EngineFor(workspace);
        var ex = await Assert.ThrowsAsync<ToolNotFoundException>(async () => await engine.UpdateXmlDocsFromSignatureAsync("Calc.cs", "CalcDsc"));
        Assert.That(ex!.Message, Does.Contain("Method 'CalcDsc' not found in 'Calc.cs'"));
        Assert.That(ex.Message, Does.Contain("Did you mean 'CalcDisc'"));
    }

    [Test]
    public async Task UpdateXmlDocsFromSignature_NoCandidateName_EngineStatesFactsOnly()
    {
        LoadCalculator(out var workspace);
        var engine = EngineFor(workspace);
        var ex = await Assert.ThrowsAsync<ToolNotFoundException>(async () => await engine.UpdateXmlDocsFromSignatureAsync("Calc.cs", "ApplyLoyaltyBonus"));
        Assert.That(ex!.Message, Does.Contain("Method 'ApplyLoyaltyBonus' not found in 'Calc.cs'"));
        AssertNoCandidateText(ex.Message);
    }

    private const string UsingSource = """
using System.Linq;

public class Counter
{
    public int Count(int[] values) { return values.Length; }
    public int CalcDisc(int total) { return total; }
}
""";

    private static void LoadSource(string fileName, string source, out IWorkspaceManager workspace)
    {
        var persistent = Battery.Basic.TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [(fileName, source)]);
        persistent.SetTestSolution(solution);
        workspace = persistent;
    }

    [Test]
    public async Task RemoveUsingDirective_CandidateName_ToolNamesNearestUsing()
    {
        LoadSource("Counter.cs", UsingSource, out var workspace);
        var tools = BatteryToolFactory.CreateRefactoringExtractionDocsTools(workspace);
        var result = await tools.UsingDirectiveTool(reason: "name miss candidate test", "Counter.cs", AddRemoveViewAction.remove, "System.Linqq");
        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData!.Message, Does.Contain("Did you mean 'System.Linq'"));
        Assert.That(result.ErrorData!.Message, Does.Not.Contain("tell the user"));
    }

    [Test]
    public async Task RemoveUsingDirective_NoCandidateName_ToolStatesFactsOnly()
    {
        LoadSource("Counter.cs", UsingSource, out var workspace);
        var tools = BatteryToolFactory.CreateRefactoringExtractionDocsTools(workspace);
        var versionBefore = workspace.WorkspaceVersion;
        var result = await tools.UsingDirectiveTool(reason: "name miss no-candidate test", "Counter.cs", AddRemoveViewAction.remove, "Contoso.Missing.Zzz");
        Assert.That(result.IsError, Is.True);
        AssertNoCandidateText(result.ErrorData!.Message);
        Assert.That(workspace.WorkspaceVersion, Is.EqualTo(versionBefore));
    }

    [Test]
    public async Task SummaryCommentAdd_CandidateName_ToolNamesNearestDeclaration()
    {
        LoadCalculator(out var workspace);
        var tools = BatteryToolFactory.CreateRefactoringExtractionDocsTools(workspace);
        var result = await tools.SummaryCommentTool(reason: "name miss candidate test", "Calc.cs", AddRemoveViewAction.add, "CalcDsc", "Calculates the discount.");
        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData!.Message, Does.Contain("Did you mean 'CalcDisc'"));
    }

    [Test]
    public async Task SummaryCommentAdd_NoCandidateName_ToolStatesFactsOnly()
    {
        LoadCalculator(out var workspace);
        var tools = BatteryToolFactory.CreateRefactoringExtractionDocsTools(workspace);
        var versionBefore = workspace.WorkspaceVersion;
        var result = await tools.SummaryCommentTool(reason: "name miss no-candidate test", "Calc.cs", AddRemoveViewAction.add, "ApplyLoyaltyBonus", "Adds a loyalty bonus.");
        Assert.That(result.IsError, Is.True);
        AssertNoCandidateText(result.ErrorData!.Message);
        Assert.That(workspace.WorkspaceVersion, Is.EqualTo(versionBefore));
    }

    [Test]
    public async Task SummaryCommentRemove_CandidateName_ToolNamesNearestDeclaration()
    {
        LoadCalculator(out var workspace);
        var tools = BatteryToolFactory.CreateRefactoringExtractionDocsTools(workspace);
        var result = await tools.SummaryCommentTool(reason: "name miss candidate test", "Calc.cs", AddRemoveViewAction.remove, "CalcDsc");
        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData!.Message, Does.Contain("Did you mean 'CalcDisc'"));
    }

    [Test]
    public async Task SummaryCommentRemove_NoCandidateName_ToolStatesFactsOnly()
    {
        LoadCalculator(out var workspace);
        var tools = BatteryToolFactory.CreateRefactoringExtractionDocsTools(workspace);
        var versionBefore = workspace.WorkspaceVersion;
        var result = await tools.SummaryCommentTool(reason: "name miss no-candidate test", "Calc.cs", AddRemoveViewAction.remove, "ApplyLoyaltyBonus");
        Assert.That(result.IsError, Is.True);
        AssertNoCandidateText(result.ErrorData!.Message);
        Assert.That(workspace.WorkspaceVersion, Is.EqualTo(versionBefore));
    }
}
