using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Common;
using RoslynSentinel.Engines.Basic;
using RoslynSentinel.Tests.Battery.Basic;
using RoslynSentinel.Tests.Fakes;
using RoslynSentinel.Tools.Basic;

namespace RoslynSentinel.Tests.Tools.Basic;

/// <summary>
/// Consumer-path proof for the shared docCommentId not-found path (SymbolWireResolver, behind
/// ISymbolResolver.ResolveFromWireAsync): each test calls the real tool entry point (RenameSymbol, DeleteUnusedSymbol,
/// PreviewRenameImpact), never the resolver or the text builder directly. A misspelt id gets the ranked candidate with
/// file, container and a prefilled call; an id nothing resembles gets the no-similar-name text with one orientation call
/// and the report-it-does-not-exist sentence; a method id without a parameter list resolves to the only method of that
/// name, or lists the overloads as an invalid-argument error. Project names contain no "Test".
/// </summary>
[TestFixture]
[Category("RefactoringSignatureTools")]
public class DocCommentIdNotFoundCandidateTests
{
    private const string ProjectName = "OrderApp";

    private const string OverloadSource = """
        namespace Fixture;

        public class Overloaded
        {
            public int Get(int a) => a;

            public int Get(string s) => s.Length;
        }
        """;

    // A real PersistentWorkspaceManager over the in-memory fixture solution: not-found paths never write, so no files are needed.
    private static PersistentWorkspaceManager NewManager()
    {
        var manager = TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        manager.SetTestSolution(TestSolutionBuilder.CreateSolutionWithProject(ProjectName, [
            (OrderPricingFixture.FileName, OrderPricingFixture.Source),
        ]));
        return manager;
    }

    // CalcDisc's exact id in the OrderPricingFixture; the misspelt ids below (CalcDsc) must lead here.
    private const string CorrectedCalcDiscId = "M:Fixture.OrderPricingCalculator.CalcDisc(System.Decimal,System.Decimal)";

    private static async Task<SentinelCallToolResult<object>> RenameAsync(string docCommentId)
    {
        using var manager = NewManager();
        var tools = new RefactoringSignatureTools(TestTools.RefactoringSignatureImpl(manager));
        return await tools.RenameSymbolTool(reason: "test message", projectName: ProjectName, docCommentId: docCommentId, newName: "Whatever", dryRun: true);
    }

    private static async Task<SentinelCallToolResult<object>> PreviewAsync(string docCommentId)
    {
        using var manager = NewManager();
        var navigation = TestEngines.SymbolNavigationEngine(manager);
        var discovery = TestEngines.DiscoveryEngine(manager, navigation);
        var tools = new SymbolRelationshipTools(TestTools.SymbolRelationshipImpl(discovery, navigation, manager, NullLogger<SymbolRelationshipImpl>.Instance));
        return await tools.PreviewRenameImpactTool(reason: "test message", docCommentId: docCommentId, projectName: ProjectName);
    }

    [Test]
    public async Task RenameSymbol_MisspeltMethodId_NamesCandidateWithItsIdAndTheCorrectedRenameCall()
    {
        var result = await RenameAsync("M:Fixture.OrderPricingCalculator.CalcDsc(System.Decimal,System.Decimal)");

        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData?.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound));
        var message = result.ErrorData?.Message;
        Assert.Multiple(() =>
        {
            Assert.That(message, Does.Contain("CalcDisc"), "the similar declared method is named");
            Assert.That(message, Does.Contain(OrderPricingFixture.FileName), "the candidate's declaring file is named");
            Assert.That(message, Does.Contain("OrderPricingCalculator"), "the candidate's container is named");
            Assert.That(message, Does.Contain($"docCommentId '{CorrectedCalcDiscId}'"), "the candidate's exact id is stated as a fact");
            Assert.That(message, Does.Contain(ToolCallHint.Call("RenameSymbol", ("projectName", ProjectName), ("docCommentId", CorrectedCalcDiscId), ("newName", "Whatever"), ("dryRun", "true"))),
                "the option is the request's own RenameSymbol call (its projectName, newName and dryRun) with only the id corrected");
            Assert.That(message, Does.Not.Contain("Search("), "the per-candidate Search call is replaced");
            Assert.That(message, Does.Not.Contain("CalcDsc(System.Decimal"), "the corrected call does not repeat the misspelt id");
            Assert.That(message, Does.Contain($"project '{ProjectName}'"), "the scope names the project searched");
            Assert.That(message, Does.Not.Contain("no longer resolves"));
        });
    }

    [Test]
    public async Task RenameSymbol_MethodIdNothingResembles_SaysNoSimilarNameOrientsWithFileOutlineAndOffersToReport()
    {
        var result = await RenameAsync("M:Fixture.OrderPricingCalculator.ApplyLoyaltyBonus(System.Decimal)");

        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData?.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound));
        var message = result.ErrorData?.Message;
        Assert.Multiple(() =>
        {
            Assert.That(message, Does.Contain("ApplyLoyaltyBonus"));
            Assert.That(message, Does.Contain("No symbol in the solution has a similar name"));
            Assert.That(message, Does.Contain(ToolCallHint.Call("GetFileOutline", ("filePath", OrderPricingFixture.FileName))),
                "the container resolves, so the one orientation call is the outline of its file");
            Assert.That(message, Does.Contain("the symbol does not exist in this solution."));
            Assert.That(message, Does.Not.Contain("tell the user"));
            Assert.That(message, Does.Not.Contain("no longer resolves"));
        });
    }

    [Test]
    public async Task PreviewRenameImpact_MisspeltMethodId_NamesCandidateWithItsIdAndTheCorrectedPreviewCall()
    {
        var result = await PreviewAsync("M:Fixture.OrderPricingCalculator.CalcDsc(System.Decimal,System.Decimal)");

        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData?.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound), result.ErrorData?.Message);
        var message = result.ErrorData?.Message;
        Assert.Multiple(() =>
        {
            Assert.That(message, Does.Contain("CalcDisc"));
            Assert.That(message, Does.Contain($"docCommentId '{CorrectedCalcDiscId}'"), "the candidate's exact id is stated as a fact");
            Assert.That(message, Does.Contain(ToolCallHint.Call("PreviewRenameImpact", ("docCommentId", CorrectedCalcDiscId), ("projectName", ProjectName))),
                "the option is the request's own PreviewRenameImpact call (its projectName) with only the id corrected");
            Assert.That(message, Does.Not.Contain("Search("), "the per-candidate Search call is replaced");
            Assert.That(message, Does.Not.Contain("no longer resolves"));
        });
    }

    [Test]
    public async Task DeleteUnusedSymbol_MethodIdWithoutParameterList_UniqueMethod_ResolvesAndDeletesIt()
    {
        // FakeWorkspaceManager.ResolveFromWireAsync runs the same SymbolWireResolver as the real manager.
        using var workspace = InMemoryWorkspace.Create((OrderPricingFixture.FileName, OrderPricingFixture.Source));
        var tools = TestTools.WorkspaceTools(workspace.Manager);

        var result = await tools.DeleteUnusedSymbolTool(reason: "test message", workspace.PathOf(OrderPricingFixture.FileName),
            projectName: "InMemoryProject", docCommentId: "M:Fixture.OrderPricingCalculator.CalcDisc");

        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        var text = workspace.ReadText(OrderPricingFixture.FileName);
        Assert.That(text, Does.Not.Contain("CalcDisc"), "the only method of that name was resolved and deleted");
        Assert.That(text, Does.Contain("OrderCheckout"), "the other methods are untouched");
    }

    [Test]
    public async Task DeleteUnusedSymbol_MethodIdWithoutParameterList_TwoOverloads_ErrorsInvalidArgumentAndListsBothIdsWithoutWriting()
    {
        using var workspace = InMemoryWorkspace.Create(("Overloaded.cs", OverloadSource));
        var tools = TestTools.WorkspaceTools(workspace.Manager);

        var result = await tools.DeleteUnusedSymbolTool(reason: "test message", workspace.PathOf("Overloaded.cs"),
            projectName: "InMemoryProject", docCommentId: "M:Fixture.Overloaded.Get");

        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData!.ErrorCode, Is.EqualTo(ToolErrorCode.InvalidArgument), result.ErrorData.Message);
        Assert.Multiple(() =>
        {
            Assert.That(result.ErrorData.Message, Does.Contain("missing its parameter list"));
            Assert.That(result.ErrorData.Message, Does.Contain("M:Fixture.Overloaded.Get(System.Int32)"));
            Assert.That(result.ErrorData.Message, Does.Contain("M:Fixture.Overloaded.Get(System.String)"));
            Assert.That(result.ErrorData.Message, Does.Contain("the one you mean"));
            Assert.That(workspace.ReadText("Overloaded.cs"), Does.Contain("public int Get(string s)"), "nothing was deleted");
        });
    }

    [Test]
    public async Task DeleteUnusedSymbol_MisspeltMethodId_NamesCandidateAndDeletesNothing()
    {
        using var workspace = InMemoryWorkspace.Create((OrderPricingFixture.FileName, OrderPricingFixture.Source));
        var tools = TestTools.WorkspaceTools(workspace.Manager);

        var result = await tools.DeleteUnusedSymbolTool(reason: "test message", workspace.PathOf(OrderPricingFixture.FileName),
            projectName: "InMemoryProject", docCommentId: "M:Fixture.OrderPricingCalculator.CalcDsc(System.Decimal,System.Decimal)");

        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData!.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound), result.ErrorData.Message);
        Assert.That(result.ErrorData.Message, Does.Contain("CalcDisc"));
        Assert.That(workspace.ReadText(OrderPricingFixture.FileName), Does.Contain("CalcDisc"));
        var filePath = workspace.PathOf(OrderPricingFixture.FileName);
        Assert.Multiple(() =>
        {
            Assert.That(result.ErrorData.Message, Does.Contain($"docCommentId '{CorrectedCalcDiscId}'"), "the candidate's exact id is stated as a fact");
            Assert.That(result.ErrorData.Message, Does.Contain(ToolCallHint.Call("DeleteUnusedSymbol", ("filePath", filePath), ("projectName", "InMemoryProject"), ("docCommentId", CorrectedCalcDiscId))),
                "the option is the request's own DeleteUnusedSymbol call (its filePath and projectName) with only the id corrected");
            Assert.That(result.ErrorData.Message, Does.Not.Contain("Search("), "the per-candidate Search call is replaced");
        });
    }

    [Test]
    public async Task DeleteUnusedSymbol_MethodIdNothingResembles_SaysNoSimilarNameWithoutIdFactOrRetryCall()
    {
        using var workspace = InMemoryWorkspace.Create((OrderPricingFixture.FileName, OrderPricingFixture.Source));
        var tools = TestTools.WorkspaceTools(workspace.Manager);

        var result = await tools.DeleteUnusedSymbolTool(reason: "test message", workspace.PathOf(OrderPricingFixture.FileName),
            projectName: "InMemoryProject", docCommentId: "M:Fixture.OrderPricingCalculator.ApplyLoyaltyBonus(System.Decimal)");

        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData!.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound), result.ErrorData.Message);
        Assert.Multiple(() =>
        {
            Assert.That(result.ErrorData.Message, Does.Contain("No symbol in the solution has a similar name"));
            Assert.That(result.ErrorData.Message, Does.Contain("the symbol does not exist in this solution."));
            Assert.That(result.ErrorData.Message, Does.Not.Contain("docCommentId '"), "no candidate, so no id fact");
            Assert.That(result.ErrorData.Message, Does.Not.Contain("DeleteUnusedSymbol("), "no candidate, so no retry call");
            Assert.That(workspace.ReadText(OrderPricingFixture.FileName), Does.Contain("CalcDisc"));
        });
    }

    [Test]
    public async Task PreviewRenameImpact_MethodIdNothingResembles_SaysNoSimilarNameWithoutIdFactOrRetryCall()
    {
        var result = await PreviewAsync("M:Fixture.OrderPricingCalculator.ApplyLoyaltyBonus(System.Decimal)");

        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData?.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound), result.ErrorData?.Message);
        var message = result.ErrorData?.Message;
        Assert.Multiple(() =>
        {
            Assert.That(message, Does.Contain("No symbol in the solution has a similar name"));
            Assert.That(message, Does.Contain("the symbol does not exist in this solution."));
            Assert.That(message, Does.Not.Contain("docCommentId '"), "no candidate, so no id fact");
            Assert.That(message, Does.Not.Contain("PreviewRenameImpact("), "no candidate, so no retry call");
        });
    }
}
