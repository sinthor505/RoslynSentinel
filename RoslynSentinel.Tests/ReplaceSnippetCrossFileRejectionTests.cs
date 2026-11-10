// End-to-end coverage of the compile gate on ReplaceSnippet when a rename in one file breaks a caller in another.
// The single-edit rejection must name the untouched caller file and the batchEdits route, and must not blame the
// caller's call shape. A caller-first edit is rejected in the edited file, so it must not claim an untouched file.
// Runs on InMemoryWorkspace (real compile gate, no disk writes).
using RoslynSentinel.Common;
using RoslynSentinel.Engines.Basic;
using RoslynSentinel.Tests.Battery.Basic;
using RoslynSentinel.Tests.Fakes;
using RoslynSentinel.Tools.Basic;

namespace RoslynSentinel.Tests;

[TestFixture]
[Category("WorkspaceTools")]
public class ReplaceSnippetCrossFileRejectionTests
{
    private const string PricingRelativePath = "ContosoOrders.Core/OrderPricingCalculator.cs";
    private const string CheckoutRelativePath = "ContosoOrders.Core/OrderCheckout.cs";

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

    [Test]
    public async Task ReplaceSnippet_RenameDeclarationOnly_RejectsAndNamesTheUntouchedCaller()
    {
        using var workspace = CreateWorkspace();
        var tools = TestTools.WorkspaceTools(workspace.Manager);
        var originalPricing = workspace.ReadText(PricingRelativePath);

        var result = await tools.ReplaceSnippetTool(
            reason: "rename the discount method on its declaration only",
            ProposedChangeAction.apply,
            filePath: workspace.PathOf(PricingRelativePath),
            oldContent: "CalcDisc(decimal a)",
            newContent: "CalculateDiscountedTotal(decimal a)");

        Assert.That(result.IsError, Is.True, "a declaration-only rename must be rejected by the compile gate");
        var message = result.ErrorData?.Message ?? string.Empty;
        Assert.Multiple(() =>
        {
            // The edit-impact analyzer recognises the rename and replaces the generic
            // untouched-file sentence with a RenameSymbol next step.
            Assert.That(message, Does.Contain("renamed CalcDisc -> CalculateDiscountedTotal"));
            Assert.That(message, Does.Contain("OrderCheckout.cs"));
            Assert.That(message, Does.Contain("RenameSymbol("));
            Assert.That(message, Does.Contain("is declared in"));
            Assert.That(message, Does.Not.Contain("isn't accessible the way it was called"));
        });
        Assert.That(workspace.ReadText(PricingRelativePath), Is.EqualTo(originalPricing), "a rejected edit must not write file A");
    }

    // 10-chain run 301 (2026-11-10) re-sent the same rejected edit for 12 turns; the second identical rejection says so.
    [Test]
    public async Task ReplaceSnippet_SameRejectedEditTwice_SecondRejectionSaysItIsARepeat()
    {
        using var workspace = CreateWorkspace();
        var tools = TestTools.WorkspaceTools(workspace.Manager);

        Task<SentinelCallToolResult<ReplaceSnippetResult>> Send() => tools.ReplaceSnippetTool(
            reason: "rename the discount method on its declaration only",
            ProposedChangeAction.apply,
            filePath: workspace.PathOf(PricingRelativePath),
            oldContent: "CalcDisc(decimal a)",
            newContent: "CalculateDiscountedTotal(decimal a)");

        var first = await Send();
        var second = await Send();

        Assert.Multiple(() =>
        {
            Assert.That(first.IsError && second.IsError, Is.True);
            Assert.That(first.ErrorData?.Message, Does.Not.Contain("You already sent this exact edit"));
            Assert.That(second.ErrorData?.Message, Does.Contain("You already sent this exact edit and it was rejected the same way"));
            Assert.That(second.ErrorData?.Message, Does.Contain("RenameSymbol("));
        });
    }

    [Test]
    public async Task ReplaceSnippet_CallerFirstEdit_RejectionIsNotBlamedOnAnUntouchedFile()
    {
        using var workspace = CreateWorkspace();
        var tools = TestTools.WorkspaceTools(workspace.Manager);
        var originalCheckout = workspace.ReadText(CheckoutRelativePath);

        var result = await tools.ReplaceSnippetTool(
            reason: "rename the call site while the declaration keeps the old name",
            ProposedChangeAction.apply,
            filePath: workspace.PathOf(CheckoutRelativePath),
            oldContent: "c.CalcDisc(1m)",
            newContent: "c.CalculateDiscountedTotal(1m)");

        Assert.That(result.IsError, Is.True, "a caller-only rename must be rejected by the compile gate");
        var message = result.ErrorData?.Message ?? string.Empty;
        Assert.Multiple(() =>
        {
            Assert.That(message, Does.Contain("introduces new compiler errors"));
            Assert.That(message, Does.Not.Contain("this edit did not change"));
        });
        Assert.That(workspace.ReadText(CheckoutRelativePath), Is.EqualTo(originalCheckout), "a rejected edit must not write file B");
    }

    [Test]
    public async Task ReplaceSnippet_RenameDeclarationAndCallerInOneBatch_Applies()
    {
        using var workspace = CreateWorkspace();
        var tools = TestTools.WorkspaceTools(workspace.Manager);

        var result = await tools.ReplaceSnippetTool(
            reason: "rename the declaration and its caller in one batch call",
            ProposedChangeAction.apply,
            batchEdits:
            [
                new SnippetEdit { FilePath = workspace.PathOf(PricingRelativePath), OldContent = "CalcDisc(decimal a)", NewContent = "CalculateDiscountedTotal(decimal a)" },
                new SnippetEdit { FilePath = workspace.PathOf(CheckoutRelativePath), OldContent = "c.CalcDisc(1m)", NewContent = "c.CalculateDiscountedTotal(1m)" },
            ]);

        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        Assert.Multiple(() =>
        {
            Assert.That(workspace.ReadText(PricingRelativePath), Does.Contain("CalculateDiscountedTotal(decimal a)"));
            Assert.That(workspace.ReadText(CheckoutRelativePath), Does.Contain("c.CalculateDiscountedTotal(1m)"));
        });
    }
}
