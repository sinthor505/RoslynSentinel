using RoslynSentinel.Common;
using RoslynSentinel.Tests.Battery.Basic;
using RoslynSentinel.Tests.Fakes;
using RoslynSentinel.Tools.Basic;

namespace RoslynSentinel.Tests.Tools.Basic;

/// <summary>
/// Consumer-path proof for the LocateSymbol not-found text: a symbol missing from the solution must name the similar
/// declared symbols with a prefilled Search call, or say that no similar name exists and orient with ListAll, and the
/// exactMatch=false retry is offered only when the wanted name is a substring of a declared name. Every test calls the
/// real Search(mode: symbol) entry point, which dispatches to LocateSymbol; none calls the builder directly.
/// </summary>
[TestFixture]
[Category("WorkspaceTools")] // sentinel:auto-category
public class LocateSymbolNotFoundCandidateTests
{
    private const string ProjectName = "OrderApp";

    private FakeWorkspaceManager _workspaceManager = null!;
    private WorkspaceTools _tools = null!;

    [SetUp]
    public void Setup()
    {
        _workspaceManager = FakeWorkspaceManager.ReadOnly();
        _workspaceManager.SetTestSolution(TestSolutionBuilder.CreateSolutionWithProject(ProjectName, [
            (OrderPricingFixture.FileName, OrderPricingFixture.Source),
        ]));
        _tools = TestTools.WorkspaceTools(_workspaceManager);
    }

    [TearDown]
    public void TearDown() => _workspaceManager?.Dispose();

    private Task<SentinelCallToolResult<object>> SearchSymbolAsync(string query, bool exactMatch = true) =>
        _tools.SearchSolutionTool(reason: "test message", mode: SearchMode.symbol, query: query, exactMatch: exactMatch, includeTests: true);

    [Test]
    public async Task Search_MisspeltMethod_NamesCandidateWithPrefilledSearchCall()
    {
        var result = await SearchSymbolAsync("CalcDsc");

        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData?.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound));
        var message = result.ErrorData?.Message;
        Assert.That(message, Does.Contain("CalcDisc"), "the similar declared method is named");
        Assert.That(message, Does.Contain(OrderPricingFixture.FileName), "the candidate's declaring file is named");
        Assert.That(message, Does.Contain("OrderPricingCalculator"), "the candidate's container is named");
        Assert.That(message, Does.Contain(ToolCallHint.Call("Search", ("mode", "symbol"), ("query", "CalcDisc"), ("filePath", OrderPricingFixture.FileName))),
            "the candidate carries a prefilled Search call");
        Assert.That(message, Does.Not.Contain("Try exactMatch=false"));
    }

    [Test]
    public async Task Search_NameDeclaredNowhere_SaysNoSimilarSymbolAndOrientsWithListAll()
    {
        var result = await SearchSymbolAsync("ApplyLoyaltyBonus");

        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData?.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound));
        var message = result.ErrorData?.Message;
        Assert.That(message, Does.Contain("1 project(s)"), "the scope names the one project searched");
        Assert.That(message, Does.Contain("No symbol in the solution has a similar name"));
        Assert.That(message, Does.Contain("ListAll"), "the single orientation call is ListAll");
        Assert.That(message, Does.Contain("If it is not in that list, the symbol does not exist in this solution."));
        Assert.That(message, Does.Not.Contain("tell the user"));
        Assert.That(message, Does.Not.Contain("Try exactMatch=false"));
        Assert.That(message, Does.Not.Contain("exactMatch=false would match"));
    }

    // Arm H (plan_error_policy_pointer Step 11g): the ambient context is an AsyncLocal, cleared in the finally so no other test sees it.
    [Test]
    public async Task Search_WorkspaceCurrentAndSolutionWideMiss_LeadsWithTheWellFormedFact()
    {
        try
        {
            ToolCallContext.Set(new ToolCallContext(null, null, "Search", null, 0) { WorkspaceCurrent = true });

            var result = await SearchSymbolAsync("ApplyLoyaltyBonus");

            Assert.That(result.ErrorData?.Message, Does.StartWith("Your tool call was well-formed, but symbol `ApplyLoyaltyBonus` does not exist in the solution. Searched 1 project(s), test projects included"));
            Assert.That(result.ErrorData?.Message, Does.Not.Contain("ListAll"));
        }
        finally
        {
            ToolCallContext.Set(null);
        }
    }

    [Test]
    public async Task Search_WorkspaceCurrentButNarrowedByProjectName_KeepsTheListAllText()
    {
        try
        {
            ToolCallContext.Set(new ToolCallContext(null, null, "Search", null, 0) { WorkspaceCurrent = true });

            var result = await _tools.SearchSolutionTool(reason: "test message", mode: SearchMode.symbol, query: "ApplyLoyaltyBonus", projectName: ProjectName, includeTests: true);

            Assert.That(result.ErrorData?.Message, Does.Not.Contain("well-formed"));
            Assert.That(result.ErrorData?.Message, Does.Contain("If it is not in that list, the symbol does not exist in this solution."));
        }
        finally
        {
            ToolCallContext.Set(null);
        }
    }

    [Test]
    public async Task Search_SubstringOfDeclaredName_OffersExactMatchFalseRetry()
    {
        // "CalcDis" is a substring of exactly one declared name (CalcDisc), so the retry hint reports one declaration.
        var result = await SearchSymbolAsync("CalcDis");

        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData?.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound));
        var message = result.ErrorData?.Message;
        Assert.That(message, Does.Contain("CalcDisc"), "the declared name the substring matches is listed as a candidate");
        Assert.That(message, Does.Contain("exactMatch=false would match 1 declaration(s)"));
        Assert.That(message, Does.Contain(ToolCallHint.Call("Search", ("mode", "symbol"), ("query", "CalcDis"), ("exactMatch", "false"))),
            "the retry is a prefilled Search call with exactMatch false");
    }
}
