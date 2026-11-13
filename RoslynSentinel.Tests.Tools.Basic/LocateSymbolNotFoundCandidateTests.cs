using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

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

    private const string TestSdkCsproj =
        "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <ItemGroup>\n    <PackageReference Include=\"Microsoft.NET.Test.Sdk\" Version=\"18.10.1\" />\n  </ItemGroup>\n</Project>\n";

    private const string PlainCsproj =
        "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <ItemGroup>\n    <PackageReference Include=\"Newtonsoft.Json\" Version=\"13.0.3\" />\n  </ItemGroup>\n</Project>\n";

    private const string TestOnlySource = "public class TestMarkerType { }";
    private const string AppSource = "public class AppMarkerType { }";

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

    // Arm I (plan_error_policy_pointer Step 11g): the containingType argument reaches the inventory; the switch is forced per async flow and the context cleared in the finally.
    [Test]
    public async Task Search_InventorySwitchOnWithContainingType_AppendsTheTypesMembers()
    {
        try
        {
            ToolCallContext.Set(new ToolCallContext(null, null, "Search", null, 0) { WorkspaceCurrent = true });
            using var on = NotFoundInventoryOptions.OverrideForCurrentContext(true);

            var result = await _tools.SearchSolutionTool(reason: "test message", mode: SearchMode.symbol, query: "ApplyLoyaltyBonus", containingType: "OrderPricingCalculator", includeTests: true);

            Assert.That(result.ErrorData?.Message, Does.EndWith(" Type `Fixture.OrderPricingCalculator` declares 3 members: CalcDisc, GetTotal, OrderCheckout"));
        }
        finally
        {
            ToolCallContext.Set(null);
        }
    }

    [Test]
    public async Task Search_InventorySwitchOffWithContainingType_AppendsNothing()
    {
        try
        {
            ToolCallContext.Set(new ToolCallContext(null, null, "Search", null, 0) { WorkspaceCurrent = true });
            using var off = NotFoundInventoryOptions.OverrideForCurrentContext(false);

            var result = await _tools.SearchSolutionTool(reason: "test message", mode: SearchMode.symbol, query: "ApplyLoyaltyBonus", containingType: "OrderPricingCalculator", includeTests: true);

            Assert.That(result.ErrorData?.Message, Does.Not.Contain("declares"));
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

    // Arm H, commit fb239287: TestProjectDetector reads the csproj from disk, so the test project's file must exist. Returns the temp directory for the caller to delete.
    private string SetSolutionWithTestProject()
    {
        var dir = Path.Combine(Path.GetTempPath(), "LocateSymbolNotFoundCandidateTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "TestsProj"));
        Directory.CreateDirectory(Path.Combine(dir, ProjectName));
        File.WriteAllText(Path.Combine(dir, "TestsProj", "TestsProj.csproj"), TestSdkCsproj);
        File.WriteAllText(Path.Combine(dir, ProjectName, ProjectName + ".csproj"), PlainCsproj);

        var solution = TestSolutionBuilder.CreateSolutionWithProject(
            "TestsProj",
            Path.Combine(dir, "TestsProj", "TestsProj.csproj"),
            [("UnitTest1.cs", TestOnlySource, Path.Combine(dir, "TestsProj", "UnitTest1.cs"))]);

        var appProjectId = ProjectId.CreateNewId();
        var appProject = ProjectInfo.Create(appProjectId, VersionStamp.Default, ProjectName, ProjectName, LanguageNames.CSharp)
            .WithFilePath(Path.Combine(dir, ProjectName, ProjectName + ".csproj"));
        solution = solution.AddProject(appProject)
            .AddDocument(DocumentId.CreateNewId(appProjectId, "App.cs"), "App.cs",
                SourceText.From(AppSource), filePath: Path.Combine(dir, ProjectName, "App.cs"));

        _workspaceManager.SetTestSolution(solution);
        return dir;
    }

    // Arm H, commit fb239287: includeTests false in a solution with a test project still counts as whole-solution when the include-tests retry also finds nothing.
    [Test]
    public async Task Search_IncludeTestsFalseTestProjectMiss_LeadsWithTheWellFormedFact()
    {
        var dir = SetSolutionWithTestProject();
        try
        {
            ToolCallContext.Set(new ToolCallContext(null, null, "Search", null, 0) { WorkspaceCurrent = true });

            var result = await _tools.SearchSolutionTool(reason: "test message", mode: SearchMode.symbol, query: "ApplyLoyaltyBonus", includeTests: false);

            Assert.That(result.IsError, Is.True);
            Assert.That(result.ErrorData?.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound));
            Assert.That(result.ErrorData?.Message, Does.StartWith("Your tool call was well-formed, but symbol `ApplyLoyaltyBonus` does not exist in the solution."));
            Assert.That(result.ErrorData?.Message, Does.Contain("test project(s) were also searched and hold no match"), "the test-project note follows the lead");
            Assert.That(result.ErrorData?.Message, Does.Not.Contain("ListAll"));
        }
        finally
        {
            ToolCallContext.Set(null);
            Directory.Delete(dir, recursive: true);
        }
    }

    // Arm H, commit fb239287: a name the include-tests retry finds is not a whole-solution miss, so no lead and the hidden-declaration note stays.
    [Test]
    public async Task Search_IncludeTestsFalseTestOnlyName_KeepsHiddenNoteAndNoLead()
    {
        var dir = SetSolutionWithTestProject();
        try
        {
            ToolCallContext.Set(new ToolCallContext(null, null, "Search", null, 0) { WorkspaceCurrent = true });

            var result = await _tools.SearchSolutionTool(reason: "test message", mode: SearchMode.symbol, query: "TestMarkerType", includeTests: false);

            Assert.That(result.IsError, Is.True);
            Assert.That(result.ErrorData?.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound));
            Assert.That(result.ErrorData?.Message, Does.Contain("hidden because includeTests is false"));
            Assert.That(result.ErrorData?.Message, Does.Not.Contain("well-formed"));
        }
        finally
        {
            ToolCallContext.Set(null);
            Directory.Delete(dir, recursive: true);
        }
    }
}
