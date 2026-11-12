using RoslynSentinel.Common;
using RoslynSentinel.Tests.Battery.Basic;
using RoslynSentinel.Tests.Fakes;
using RoslynSentinel.Tools.Basic;

namespace RoslynSentinel.Tests.Tools.Basic;

/// <summary>
/// Consumer-path proof for plan_not_found_error_orientation Step 3c. Each file-consuming tool, called with a misspelt
/// file (NEAR: OrderPricingCalculater.cs, one edit from a loaded file) or a file that does not exist (NONE:
/// ShippingLabelPrinter.cs), must return the new FileNotFoundMessage text: the candidate text with a prefilled ReadFile
/// call for NEAR, and "No file in the solution has a similar name" with a ListSolutionItems offer for NONE. Every test
/// calls the real tool entry point; none calls the builder directly.
/// </summary>
[TestFixture]
[Category("WorkspaceTools")] // sentinel:auto-category
public class FileNotFoundConsumerPathTests
{
    private const string NearPath = "OrderPricingCalculater.cs";
    private const string NonePath = "ShippingLabelPrinter.cs";

    private const string OrderLineSource = """
        namespace Fixture;

        public class OrderLine
        {
            public decimal Price { get; set; }
        }
        """;

    private const string CustomerRepositorySource = """
        namespace Fixture;

        public class CustomerRepository
        {
            public string Find(int id) => "customer" + id;
        }
        """;

    private FakeWorkspaceManager _workspaceManager = null!;
    private WorkspaceTools _tools = null!;
    private RefactoringStructuralTools _structuralTools = null!;

    [SetUp]
    public void Setup()
    {
        _workspaceManager = FakeWorkspaceManager.ReadOnly();
        _workspaceManager.SetTestSolution(TestSolutionBuilder.CreateSolutionWithProject("TestProj", [
            (OrderPricingFixture.FileName, OrderPricingFixture.Source),
            ("OrderLine.cs", OrderLineSource),
            ("CustomerRepository.cs", CustomerRepositorySource),
        ]));
        _tools = TestTools.WorkspaceTools(_workspaceManager);
        _structuralTools = TestTools.RefactoringStructuralTools(_workspaceManager);
    }

    [TearDown]
    public void TearDown() => _workspaceManager?.Dispose();

    // The NEAR text: the real path of the file the misspelling meant, with a prefilled ReadFile call, and no order to list.
    private static void AssertCandidateText(string? message)
    {
        Assert.That(message, Does.Contain(OrderPricingFixture.FileName), "the real path of the file the misspelling meant is named");
        Assert.That(message, Does.Contain(ToolCallHint.Call("ReadFile", ("filePath", OrderPricingFixture.FileName))), "the candidate carries a prefilled ReadFile call");
        Assert.That(message, Does.Contain("does not exist in this solution"));
        Assert.That(message, Does.Not.Contain("tell the user"));
        Assert.That(message, Does.Not.Contain("You MUST"));
    }

    // The NONE text: says no similar name exists, offers the listing (not CreateFile), and states the facts about a file that is not in the solution.
    private static void AssertNoCandidateText(string? message)
    {
        Assert.That(message, Does.Contain("No file in the solution has a similar name"));
        Assert.That(message, Does.Contain(ToolCallHint.Call("ListSolutionItems", ("kind", "all"))), "the listing is offered as a prefilled call");
        Assert.That(message, Does.Contain("does not exist in this solution"));
        Assert.That(message, Does.Contain("may since have been renamed or moved"));
        Assert.That(message, Does.Not.Contain("CreateFile"), "the no-candidate text does not offer to create the file");
        Assert.That(message, Does.Not.Contain("tell the user"));
        Assert.That(message, Does.Not.Contain("If your task is to create this file"));
        Assert.That(message, Does.Not.Contain("You MUST"));
    }

    // ---- ReadFile (WorkspaceFileEditImpl.BuildFileNotFoundError) ----

    [Test]
    public async Task ReadFile_MisspeltFile_ReturnsCandidateText()
    {
        var result = await _tools.ReadFileTool(reason: "test message", NearPath);

        Assert.That(result.IsError, Is.True);
        AssertCandidateText(result.ErrorData?.Message);
    }

    [Test]
    public async Task ReadFile_NonExistentFile_ReturnsNoCandidateText()
    {
        var result = await _tools.ReadFileTool(reason: "test message", NonePath);

        Assert.That(result.IsError, Is.True);
        AssertNoCandidateText(result.ErrorData?.Message);
    }

    // ---- GetFileOutline (WorkspaceReadNavigationImpl.BuildFileNotFoundError) ----

    [Test]
    public async Task GetFileOutline_MisspeltFile_ReturnsCandidateText()
    {
        var result = await _tools.GetFileOutlineTool(reason: "test message", NearPath);

        Assert.That(result.IsError, Is.True);
        AssertCandidateText(result.ErrorData?.Message);
    }

    [Test]
    public async Task GetFileOutline_NonExistentFile_ReturnsNoCandidateText()
    {
        var result = await _tools.GetFileOutlineTool(reason: "test message", NonePath);

        Assert.That(result.IsError, Is.True);
        AssertNoCandidateText(result.ErrorData?.Message);
    }

    // ---- ReplaceSnippet single edit (DocumentLookup route in WorkspaceFileEditImpl.ReplaceSnippet) ----
    // Action is validate, so nothing is written even if the lookup were to succeed.

    [Test]
    public async Task ReplaceSnippetSingle_MisspeltFile_ReturnsCandidateText()
    {
        var result = await _tools.ReplaceSnippetTool(
            reason: "test message",
            action: ProposedChangeAction.validate,
            filePath: NearPath,
            oldContent: "CalcDisc",
            newContent: "CalcDiscount");

        Assert.That(result.IsError, Is.True);
        AssertCandidateText(result.ErrorData?.Message);
    }

    [Test]
    public async Task ReplaceSnippetSingle_NonExistentFile_ReturnsNoCandidateText()
    {
        var result = await _tools.ReplaceSnippetTool(
            reason: "test message",
            action: ProposedChangeAction.validate,
            filePath: NonePath,
            oldContent: "Price",
            newContent: "Cost");

        Assert.That(result.IsError, Is.True);
        AssertNoCandidateText(result.ErrorData?.Message);
    }

    // ---- ReplaceSnippet batch (batchEdits path in WorkspaceFileEditImpl.ReplaceSnippetBatch) ----

    [Test]
    public async Task ReplaceSnippetBatch_MisspeltFile_ReturnsCandidateText()
    {
        var result = await _tools.ReplaceSnippetTool(
            reason: "test message",
            action: ProposedChangeAction.validate,
            batchEdits: [new SnippetEdit { FilePath = NearPath, OldContent = "CalcDisc", NewContent = "CalcDiscount" }]);

        Assert.That(result.IsError, Is.True);
        AssertCandidateText(result.ErrorData?.Message);
    }

    [Test]
    public async Task ReplaceSnippetBatch_NonExistentFile_ReturnsNoCandidateText()
    {
        var result = await _tools.ReplaceSnippetTool(
            reason: "test message",
            action: ProposedChangeAction.validate,
            batchEdits: [new SnippetEdit { FilePath = NonePath, OldContent = "Price", NewContent = "Cost" }]);

        Assert.That(result.IsError, Is.True);
        AssertNoCandidateText(result.ErrorData?.Message);
    }

    // ---- Member (MemberRefactoringEngine.AddMemberAsync, DocumentLookup.RequireDocument at MemberRefactoringEngine.cs:2434) ----

    [Test]
    public async Task MemberAddMember_MisspeltFile_ReturnsCandidateText()
    {
        var result = await _structuralTools.MemberTool(
            reason: "test message",
            operation: MemberAction.addMember,
            filePath: NearPath,
            containerName: "OrderPricingCalculator",
            newMemberSource: "public void Noop() { }");

        Assert.That(result.IsError, Is.True);
        AssertCandidateText(result.ErrorData?.Message);
    }

    [Test]
    public async Task MemberAddMember_NonExistentFile_ReturnsNoCandidateText()
    {
        var result = await _structuralTools.MemberTool(
            reason: "test message",
            operation: MemberAction.addMember,
            filePath: NonePath,
            containerName: "OrderPricingCalculator",
            newMemberSource: "public void Noop() { }");

        Assert.That(result.IsError, Is.True);
        AssertNoCandidateText(result.ErrorData?.Message);
    }
}
