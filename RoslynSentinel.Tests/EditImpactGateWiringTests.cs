// Slice E2a (proposal_semantic_edit_impact_diagnostics.md, "First slice"): the edit-impact analyzer is wired into the
// compile-gate rejection text of the gated tools outside WorkspaceFileEditImpl (here: Member through
// RefactoringStructuralImpl, plus ValidateAndApplyHelper directly). The analyzer's text replaces the f0830fc9
// untouched-file sentence rather than adding to it; when the analyzer has nothing to say that sentence stays.
// Runs on InMemoryWorkspace (real compile gate, no disk writes). No ActiveToolSurface is supplied, so every tool
// counts as callable and the rename next step names RenameSymbol.
using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Common;
using RoslynSentinel.Engines.Basic;
using RoslynSentinel.Tests.Battery.Basic;
using RoslynSentinel.Tests.Fakes;

namespace RoslynSentinel.Tests;

[TestFixture]
[Category("EditImpactGate")]
public class EditImpactGateWiringTests
{
    private const string PricingRelativePath = "ContosoOrders.Core/OrderPricingCalculator.cs";
    private const string CheckoutRelativePath = "ContosoOrders.Core/OrderCheckout.cs";

    private const string UntouchedSentence = "this edit did not change";

    private const string PricingSource = """
    namespace ContosoOrders.Core;

    public class OrderPricingCalculator
    {
        public decimal CalcDisc(decimal a) => a;
    }
    """;

    private const string CheckoutSource = """
    namespace ContosoOrders.Core;

    class OrderCheckout
    {
        decimal F(OrderPricingCalculator c) => c.CalcDisc(1m);
    }
    """;

    private static InMemoryWorkspace CreateWorkspace() =>
        InMemoryWorkspace.Create((PricingRelativePath, PricingSource), (CheckoutRelativePath, CheckoutSource));

    // The declaration-only rename that broke the OrderCheckout caller in the 2026-11-09 chain evals, made through Member.
    private static Task<SentinelCallToolResult<object>> RenameDeclarationThroughMemberAsync(InMemoryWorkspace workspace) =>
        TestTools.RefactoringStructuralTools(workspace.Manager).MemberTool(
            reason: "rename the discount method on its declaration only",
            operation: MemberAction.replace,
            filePath: workspace.PathOf(PricingRelativePath),
            memberName: "CalcDisc",
            newMemberSource: "public decimal CalculateDiscountedTotal(decimal a) => a;");

    [Test]
    public async Task Member_DeclarationOnlyRename_RejectionCarriesTheEditImpactLineAndRenameSymbolNextStepAsync()
    {
        using var workspace = CreateWorkspace();
        var originalPricing = workspace.ReadText(PricingRelativePath);

        var result = await RenameDeclarationThroughMemberAsync(workspace);

        Assert.That(result.IsError, Is.True, "a declaration-only rename must be rejected by the compile gate");
        Assert.That(result.ErrorData, Is.Not.Null);
        Assert.That(result.ErrorData!.ErrorCode, Is.EqualTo(ToolErrorCode.ValidationFailed), result.ErrorData.Message);
        var message = result.ErrorData.Message;
        Assert.Multiple(() =>
        {
            Assert.That(message, Does.Contain("This edit renames CalcDisc to CalculateDiscountedTotal"));
            Assert.That(message, Does.Contain("RenameSymbol"));
            Assert.That(message, Does.Contain("OrderCheckout.cs"));
        });
        Assert.That(workspace.ReadText(PricingRelativePath), Is.EqualTo(originalPricing), "a rejected edit must not write file A");
    }

    [Test]
    public async Task Member_DeclarationOnlyRename_RejectionDoesNotRepeatTheUntouchedFileSentenceAsync()
    {
        using var workspace = CreateWorkspace();

        var result = await RenameDeclarationThroughMemberAsync(workspace);

        Assert.That(result.IsError, Is.True);
        var message = result.ErrorData?.Message ?? string.Empty;
        Assert.That(message, Does.Contain("This edit renames CalcDisc"), "precondition: the analyzer text must be present for the duplicate check to mean anything");
        Assert.That(message, Does.Not.Contain(UntouchedSentence), "the analyzer text already names the untouched caller; the fallback sentence would repeat it");
    }

    private static async Task<ResultError> RejectRenameThroughHelperAsync(
        InMemoryWorkspace workspace, Func<DiagnosticReport, CancellationToken, Task<string?>>? describeEditImpact)
    {
        var changes = new Dictionary<FilePathWrapper, string>
        {
            [new FilePathWrapper(workspace.PathOf(PricingRelativePath))] = PricingSource.Replace("CalcDisc", "CalculateDiscountedTotal"),
        };

        var outcome = await ValidateAndApplyHelper.ValidateAndApplyAsync(
            new ValidationEngine(workspace.Manager), workspace.Manager, NullLogger.Instance, changes, "TestOperation",
            dryRun: true, describeEditImpact: describeEditImpact);

        Assert.That(outcome.Error, Is.Not.Null, "the declaration-only rename must be rejected by the compile gate");
        return outcome.Error!;
    }

    [Test]
    public async Task Helper_DescribeEditImpactReturnsNull_KeepsTheUntouchedFileSentenceAsync()
    {
        using var workspace = CreateWorkspace();

        var error = await RejectRenameThroughHelperAsync(workspace, (_, _) => Task.FromResult<string?>(null));

        Assert.That(error.Message, Does.Contain(UntouchedSentence));
        Assert.That(error.Message, Does.Contain("OrderCheckout.cs"));
    }

    [Test]
    public async Task Helper_NoDescribeEditImpact_KeepsTheUntouchedFileSentenceAsync()
    {
        using var workspace = CreateWorkspace();

        var error = await RejectRenameThroughHelperAsync(workspace, describeEditImpact: null);

        Assert.That(error.Message, Does.Contain(UntouchedSentence));
    }

    [Test]
    public async Task Helper_DescribeEditImpactReturnsText_ReplacesTheUntouchedFileSentenceAsync()
    {
        using var workspace = CreateWorkspace();

        var error = await RejectRenameThroughHelperAsync(workspace, (_, _) => Task.FromResult<string?>("Next step: IMPACT-MARKER"));

        Assert.That(error.Message, Does.Contain("IMPACT-MARKER"));
        Assert.That(error.Message, Does.Not.Contain(UntouchedSentence));
        // The impact text ends with a Next step; a trailing "retry" contradicted it (proposal_semantic_edit_impact_diagnostics.md, Second slice step 0).
        Assert.That(error.Message, Does.Not.Contain("Fix the issue(s) below and retry"));
    }
}
