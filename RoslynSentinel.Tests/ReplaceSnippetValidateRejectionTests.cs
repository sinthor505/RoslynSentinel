// Fixture tests (no model) for ReplaceSnippet action "validate" giving the same rejection body as "apply" (eval 20261112-151531-550 T3, T4/T13, T28):
// the impact verdict with the RenameSymbol redirect, the file:line of the broken use and the compiler-error hint, written nothing, and no
// rejected-edit memory (a validate is a dry run, so a following apply of the same edit is not a "repeat"). Runs on InMemoryWorkspace (real compile gate).
// The rename-lift flag is forced off per test through an AsyncLocal override, so these tests are safe under the assembly's parallel-fixtures setting.
using RoslynSentinel.Common;
using RoslynSentinel.Engines.Basic;
using RoslynSentinel.Tests.Battery.Basic;
using RoslynSentinel.Tests.Fakes;
using RoslynSentinel.Tools.Basic;

namespace RoslynSentinel.Tests;

[TestFixture]
[Category("WorkspaceTools")]
[Category("EditImpactGate")]
public class ReplaceSnippetValidateRejectionTests
{
    private const string PricingRelativePath = "ContosoOrders.Core/OrderPricingCalculator.cs";
    private const string CheckoutRelativePath = "ContosoOrders.Core/OrderCheckout.cs";
    private const string RepeatSentence = "You already sent this exact edit";

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

    private static Task<SentinelCallToolResult<ReplaceSnippetResult>> RenameDeclarationOnly(InMemoryWorkspace workspace, ProposedChangeAction action) =>
        TestTools.WorkspaceTools(workspace.Manager).ReplaceSnippetTool(
            reason: "rename the discount method on its declaration only",
            action,
            filePath: workspace.PathOf(PricingRelativePath),
            oldContent: "CalcDisc(decimal a)",
            newContent: "CalculateDiscountedTotal(decimal a)");

    private static Task<SentinelCallToolResult<ReplaceSnippetResult>> RenameDeclarationOnlyAsBatch(InMemoryWorkspace workspace, ProposedChangeAction action) =>
        TestTools.WorkspaceTools(workspace.Manager).ReplaceSnippetTool(
            reason: "rename the discount method on its declaration only, as a batch",
            action,
            batchEdits:
            [
                new SnippetEdit
                {
                    FilePath = workspace.PathOf(PricingRelativePath),
                    OldContent = "CalcDisc(decimal a)",
                    NewContent = "CalculateDiscountedTotal(decimal a)",
                },
            ]);

    private static void AssertValidateRejectionBody(string message)
    {
        Assert.Multiple(() =>
        {
            Assert.That(message, Does.Contain("validate"), message);
            Assert.That(message, Does.Contain("nothing was written"), message);
            Assert.That(message, Does.Contain("renamed CalcDisc -> CalculateDiscountedTotal"), message);
            Assert.That(message, Does.Contain("RenameSymbol("), message);
            Assert.That(message, Does.Contain("[COMPILER ERROR]"), message);
            // The broken use sits on line 5 of OrderCheckout.cs.
            Assert.That(message, Does.Match(@"OrderCheckout\.cs\W{1,3}5\b"), message);
            Assert.That(message, Does.Not.Contain(RepeatSentence), message);
        });
    }

    [Test]
    public async Task SingleEditValidate_RenameBreakingACallSite_CarriesTheRenameSymbolRedirectAndTheBrokenUseLocation_AndWritesNothing()
    {
        using var _ = LiftRenamesOptions.OverrideForCurrentContext(false);
        using var workspace = CreateWorkspace();
        var originalPricing = workspace.ReadText(PricingRelativePath);
        var originalCheckout = workspace.ReadText(CheckoutRelativePath);

        var result = await RenameDeclarationOnly(workspace, ProposedChangeAction.validate);

        Assert.That(result.IsError, Is.True, "the rename leaves a broken call site, so validate must reject it");
        AssertValidateRejectionBody(result.ErrorData?.Message ?? string.Empty);
        Assert.Multiple(() =>
        {
            Assert.That(result.ErrorData?.StructuredDetail, Is.Not.Null, "validate carries the same structured detail as apply");
            Assert.That(workspace.ReadText(PricingRelativePath), Is.EqualTo(originalPricing));
            Assert.That(workspace.ReadText(CheckoutRelativePath), Is.EqualTo(originalCheckout));
        });
    }

    [Test]
    public async Task BatchEditsValidate_RenameBreakingACallSite_CarriesTheRenameSymbolRedirectAndTheBrokenUseLocation_AndWritesNothing()
    {
        using var _ = LiftRenamesOptions.OverrideForCurrentContext(false);
        using var workspace = CreateWorkspace();
        var originalPricing = workspace.ReadText(PricingRelativePath);
        var originalCheckout = workspace.ReadText(CheckoutRelativePath);

        var result = await RenameDeclarationOnlyAsBatch(workspace, ProposedChangeAction.validate);

        Assert.That(result.IsError, Is.True, "the rename leaves a broken call site, so validate must reject it");
        AssertValidateRejectionBody(result.ErrorData?.Message ?? string.Empty);
        Assert.Multiple(() =>
        {
            Assert.That(result.ErrorData?.StructuredDetail, Is.Not.Null, "validate carries the same structured detail as apply");
            Assert.That(workspace.ReadText(PricingRelativePath), Is.EqualTo(originalPricing));
            Assert.That(workspace.ReadText(CheckoutRelativePath), Is.EqualTo(originalCheckout));
        });
    }

    // A validate must not record into the rejected-edit memory: the apply that follows it is the first time the model sends the edit for real, so it must
    // not be told it is repeating itself. The second apply (a genuine resend) is the control that the repeat note does fire here.
    [Test]
    public async Task SingleEdit_ValidateThenApplyOfTheSameBadEdit_ApplyIsNotFlaggedAsARepeat()
    {
        using var _ = LiftRenamesOptions.OverrideForCurrentContext(false);
        using var workspace = CreateWorkspace();

        var validated = await RenameDeclarationOnly(workspace, ProposedChangeAction.validate);
        var applied = await RenameDeclarationOnly(workspace, ProposedChangeAction.apply);
        var appliedAgain = await RenameDeclarationOnly(workspace, ProposedChangeAction.apply);

        Assert.Multiple(() =>
        {
            Assert.That(validated.IsError, Is.True);
            Assert.That(validated.ErrorData?.Message, Does.Not.Contain(RepeatSentence));
            Assert.That(applied.IsError, Is.True);
            Assert.That(applied.ErrorData?.Message, Does.Not.Contain(RepeatSentence), "validate is a dry run and must not make the first apply a repeat");
            Assert.That(applied.ErrorData?.Message, Does.Contain("The file is unchanged"));
            Assert.That(appliedAgain.ErrorData?.Message, Does.Contain(RepeatSentence), "control: a second real apply of the same rejected edit is a repeat");
        });
    }

    [Test]
    public async Task BatchEdits_ValidateThenApplyOfTheSameBadEdit_ApplyIsNotFlaggedAsARepeat()
    {
        using var _ = LiftRenamesOptions.OverrideForCurrentContext(false);
        using var workspace = CreateWorkspace();

        var validated = await RenameDeclarationOnlyAsBatch(workspace, ProposedChangeAction.validate);
        var applied = await RenameDeclarationOnlyAsBatch(workspace, ProposedChangeAction.apply);
        var appliedAgain = await RenameDeclarationOnlyAsBatch(workspace, ProposedChangeAction.apply);

        Assert.Multiple(() =>
        {
            Assert.That(validated.IsError, Is.True);
            Assert.That(validated.ErrorData?.Message, Does.Not.Contain(RepeatSentence));
            Assert.That(applied.IsError, Is.True);
            Assert.That(applied.ErrorData?.Message, Does.Not.Contain(RepeatSentence), "validate is a dry run and must not make the first apply a repeat");
            Assert.That(appliedAgain.ErrorData?.Message, Does.Contain(RepeatSentence), "control: a second real apply of the same rejected edit is a repeat");
        });
    }
}
