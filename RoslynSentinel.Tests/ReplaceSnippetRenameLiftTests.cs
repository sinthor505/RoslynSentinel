// Fixture tests (no model) for experimental rename lifting (--lift-renames, proposal_semantic_edit_impact_diagnostics.md
// "Second slice", step 1) on ReplaceSnippet: a rejected edit that is one unambiguous rename is re-validated as the
// RenameSymbol output for the files the model did not edit plus the model's own text for the files it did edit.
// Runs on InMemoryWorkspace (real compile gate, no disk writes). The flag is forced per test through an AsyncLocal override,
// so these tests are safe under the assembly's parallel-fixtures setting.
using RoslynSentinel.Common;
using RoslynSentinel.Engines.Basic;
using RoslynSentinel.Tests.Battery.Basic;
using RoslynSentinel.Tests.Fakes;
using RoslynSentinel.Tools.Basic;

namespace RoslynSentinel.Tests;

[TestFixture]
[Category("WorkspaceTools")]
[Category("EditImpactGate")]
public class ReplaceSnippetRenameLiftTests
{
    private const string PricingRelativePath = "ContosoOrders.Core/OrderPricingCalculator.cs";
    private const string CheckoutRelativePath = "ContosoOrders.Core/OrderCheckout.cs";

    private const string PricingSource = """
    namespace ContosoOrders.Core;

    public interface IPriced
    {
    }

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

    private static Task<SentinelCallToolResult<ReplaceSnippetResult>> RenameMethodDeclarationOnly(InMemoryWorkspace workspace) =>
        TestTools.WorkspaceTools(workspace.Manager).ReplaceSnippetTool(
            reason: "rename the discount method on its declaration only",
            ProposedChangeAction.apply,
            filePath: workspace.PathOf(PricingRelativePath),
            oldContent: "CalcDisc(decimal a)",
            newContent: "CalculateDiscountedTotal(decimal a)");

    [Test]
    public async Task FlagOn_DeclarationOnlyMethodRename_IsLiftedAndTheCallerIsUpdated()
    {
        using var _ = LiftRenamesOptions.OverrideForCurrentContext(true);
        using var workspace = CreateWorkspace();

        var result = await RenameMethodDeclarationOnly(workspace);

        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        var summary = result.SuccessData?.ReplacementResult?.Summary ?? string.Empty;
        Assert.Multiple(() =>
        {
            Assert.That(workspace.ReadText(PricingRelativePath), Does.Contain("CalculateDiscountedTotal(decimal a)"));
            Assert.That(workspace.ReadText(CheckoutRelativePath), Does.Contain("c.CalculateDiscountedTotal(1m)"));
            Assert.That(workspace.ReadText(CheckoutRelativePath), Does.Not.Contain("CalcDisc"));
            Assert.That(summary, Does.Contain("Applied as RenameSymbol"));
            Assert.That(summary, Does.Contain("method CalcDisc -> CalculateDiscountedTotal"));
            Assert.That(summary, Does.Contain("1 call site(s) in 1 other file(s) (OrderCheckout.cs)"));
            Assert.That(summary, Does.Contain("Files changed: "));
            Assert.That(summary, Does.Contain("OrderPricingCalculator.cs"));
        });
    }

    [Test]
    public async Task FlagOff_SameEdit_GetsTheFirstSliceRedirectAndNothingIsWritten()
    {
        using var _ = LiftRenamesOptions.OverrideForCurrentContext(false);
        using var workspace = CreateWorkspace();
        var originalPricing = workspace.ReadText(PricingRelativePath);

        var result = await RenameMethodDeclarationOnly(workspace);

        Assert.That(result.IsError, Is.True, "with the flag off the declaration-only rename must still be rejected");
        var message = result.ErrorData?.Message ?? string.Empty;
        Assert.Multiple(() =>
        {
            Assert.That(message, Does.Contain("renamed CalcDisc -> CalculateDiscountedTotal"));
            Assert.That(message, Does.Contain("RenameSymbol("));
            Assert.That(message, Does.Not.Contain("Applied as RenameSymbol"));
            Assert.That(workspace.ReadText(PricingRelativePath), Is.EqualTo(originalPricing));
            Assert.That(workspace.ReadText(CheckoutRelativePath), Does.Contain("c.CalcDisc(1m)"));
        });
    }

    // Pattern A of the proposal (a pure class rename), here together with an added base interface in the same edit.
    [Test]
    public async Task FlagOn_ClassRenameWithAddedBaseInterface_IsLifted()
    {
        using var _ = LiftRenamesOptions.OverrideForCurrentContext(true);
        using var workspace = CreateWorkspace();
        var tools = TestTools.WorkspaceTools(workspace.Manager);

        var result = await tools.ReplaceSnippetTool(
            reason: "rename the calculator class and make it implement IPriced",
            ProposedChangeAction.apply,
            filePath: workspace.PathOf(PricingRelativePath),
            oldContent: "public class OrderPricingCalculator",
            newContent: "public class DiscountPricingCalculator : IPriced");

        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        var summary = result.SuccessData?.ReplacementResult?.Summary ?? string.Empty;
        Assert.Multiple(() =>
        {
            Assert.That(workspace.ReadText(PricingRelativePath), Does.Contain("public class DiscountPricingCalculator : IPriced"));
            Assert.That(workspace.ReadText(CheckoutRelativePath), Does.Contain("decimal F(DiscountPricingCalculator c)"));
            Assert.That(workspace.ReadText(CheckoutRelativePath), Does.Not.Contain("OrderPricingCalculator"));
            Assert.That(summary, Does.Contain("Applied as RenameSymbol"));
            Assert.That(summary, Does.Contain("class OrderPricingCalculator -> DiscountPricingCalculator"));
        });
    }

    // The batch path shares the helper; a rename plus a body change in one call is "the rename combined with other edits".
    [Test]
    public async Task FlagOn_BatchWithRenameAndBodyChange_IsLiftedAndKeepsTheModelsBodyChange()
    {
        using var _ = LiftRenamesOptions.OverrideForCurrentContext(true);
        using var workspace = CreateWorkspace();
        var tools = TestTools.WorkspaceTools(workspace.Manager);

        var result = await tools.ReplaceSnippetTool(
            reason: "rename the discount method and change its body in one batch",
            ProposedChangeAction.apply,
            batchEdits:
            [
                new SnippetEdit { FilePath = workspace.PathOf(PricingRelativePath), OldContent = "CalcDisc(decimal a)", NewContent = "CalculateDiscountedTotal(decimal a)" },
                new SnippetEdit { FilePath = workspace.PathOf(PricingRelativePath), OldContent = "=> a;", NewContent = "=> a * 0.9m;" },
            ]);

        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        var summary = result.SuccessData?.ReplacementResult?.Summary ?? string.Empty;
        Assert.Multiple(() =>
        {
            Assert.That(workspace.ReadText(PricingRelativePath), Does.Contain("=> a * 0.9m;"), "the model's own text must be kept for the file it edited");
            Assert.That(workspace.ReadText(CheckoutRelativePath), Does.Contain("c.CalculateDiscountedTotal(1m)"));
            Assert.That(summary, Does.Contain("Applied as RenameSymbol"));
        });
    }

    // A self-call in the same file, outside the edited snippet, is renamed by the lift as well.
    [Test]
    public async Task FlagOn_MethodRenameWithSelfCallOutsideTheSnippet_IsLifted()
    {
        using var _ = LiftRenamesOptions.OverrideForCurrentContext(true);
        const string pricingWithSelfCall = """
        namespace ContosoOrders.Core;

        public class OrderPricingCalculator
        {
            public decimal CalcDisc(decimal a) => a;

            public decimal Total(decimal a) => CalcDisc(a) + 1m;
        }
        """;
        using var workspace = InMemoryWorkspace.Create((PricingRelativePath, pricingWithSelfCall), (CheckoutRelativePath, CheckoutSource));
        var tools = TestTools.WorkspaceTools(workspace.Manager);

        var result = await tools.ReplaceSnippetTool(
            reason: "rename the discount method on its declaration only",
            ProposedChangeAction.apply,
            filePath: workspace.PathOf(PricingRelativePath),
            oldContent: "CalcDisc(decimal a)",
            newContent: "CalculateDiscountedTotal(decimal a)");

        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        Assert.Multiple(() =>
        {
            Assert.That(workspace.ReadText(PricingRelativePath), Does.Contain("CalculateDiscountedTotal(a) + 1m"));
            Assert.That(workspace.ReadText(PricingRelativePath), Does.Not.Contain("CalcDisc"));
            Assert.That(workspace.ReadText(CheckoutRelativePath), Does.Contain("c.CalculateDiscountedTotal(1m)"));
        });
    }

    // The model edits only the class declaration line; the class's own constructor, outside the snippet, still carries the old
    // name. The lift renames it too (renamer changes outside the edited span are applied on top of the model's text).
    [Test]
    public async Task FlagOn_ClassRenameWithOwnConstructorOutsideTheSnippet_IsLiftedAndTheConstructorIsRenamed()
    {
        using var _ = LiftRenamesOptions.OverrideForCurrentContext(true);
        const string pricingWithConstructor = """
        namespace ContosoOrders.Core;

        public class OrderPricingCalculator
        {
            public OrderPricingCalculator()
            {
            }

            public decimal CalcDisc(decimal a) => a;
        }
        """;
        using var workspace = InMemoryWorkspace.Create((PricingRelativePath, pricingWithConstructor), (CheckoutRelativePath, CheckoutSource));
        var tools = TestTools.WorkspaceTools(workspace.Manager);

        var result = await tools.ReplaceSnippetTool(
            reason: "rename the calculator class on its declaration line only",
            ProposedChangeAction.apply,
            filePath: workspace.PathOf(PricingRelativePath),
            oldContent: "public class OrderPricingCalculator",
            newContent: "public class DiscountPricingCalculator");

        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        var summary = result.SuccessData?.ReplacementResult?.Summary ?? string.Empty;
        Assert.Multiple(() =>
        {
            Assert.That(workspace.ReadText(PricingRelativePath), Does.Contain("public class DiscountPricingCalculator"));
            Assert.That(workspace.ReadText(PricingRelativePath), Does.Contain("public DiscountPricingCalculator()"));
            Assert.That(workspace.ReadText(PricingRelativePath), Does.Not.Contain("OrderPricingCalculator"));
            Assert.That(workspace.ReadText(CheckoutRelativePath), Does.Contain("decimal F(DiscountPricingCalculator c)"));
            Assert.That(summary, Does.Contain("Applied as RenameSymbol"));
            Assert.That(summary, Does.Contain("more use(s) in the file(s) you edited"));
        });
    }

    // Removing CalcDisc while adding two methods of the same shape leaves no unique pairing, so the diff reports no rename
    // and the lift must refuse: the rejection is the ordinary one and nothing is written.
    [Test]
    public async Task FlagOn_AmbiguousPairing_IsNotLifted()
    {
        using var _ = LiftRenamesOptions.OverrideForCurrentContext(true);
        using var workspace = CreateWorkspace();
        var tools = TestTools.WorkspaceTools(workspace.Manager);
        var originalPricing = workspace.ReadText(PricingRelativePath);

        var result = await tools.ReplaceSnippetTool(
            reason: "extract a base calculation and rename in one edit",
            ProposedChangeAction.apply,
            filePath: workspace.PathOf(PricingRelativePath),
            oldContent: "public decimal CalcDisc(decimal a) => a;",
            newContent: "public decimal CalculateBaseDiscount(decimal a) => a;\n    public decimal CalculateDiscountedTotal(decimal a) => CalculateBaseDiscount(a);");

        Assert.That(result.IsError, Is.True, "an ambiguous rename must not be lifted");
        var message = result.ErrorData?.Message ?? string.Empty;
        Assert.Multiple(() =>
        {
            Assert.That(message, Does.Contain("introduces new compiler errors"));
            Assert.That(message, Does.Not.Contain("Applied as RenameSymbol"));
            Assert.That(workspace.ReadText(PricingRelativePath), Is.EqualTo(originalPricing));
            Assert.That(workspace.ReadText(CheckoutRelativePath), Does.Contain("c.CalcDisc(1m)"));
        });
    }

    // An overloaded method name resolves to more than one symbol; the lift stays away from it even though the diff paired one overload by signature.
    [Test]
    public async Task FlagOn_RenameOfAnOverloadedMethodName_IsNotLifted()
    {
        using var _ = LiftRenamesOptions.OverrideForCurrentContext(true);
        const string overloadedPricing = """
        namespace ContosoOrders.Core;

        public class OrderPricingCalculator
        {
            public decimal CalcDisc(decimal a) => a;
            public decimal CalcDisc(decimal a, decimal b) => a + b;
        }
        """;
        using var workspace = InMemoryWorkspace.Create((PricingRelativePath, overloadedPricing), (CheckoutRelativePath, CheckoutSource));
        var tools = TestTools.WorkspaceTools(workspace.Manager);

        var result = await tools.ReplaceSnippetTool(
            reason: "rename one overload on its declaration only",
            ProposedChangeAction.apply,
            filePath: workspace.PathOf(PricingRelativePath),
            oldContent: "CalcDisc(decimal a)",
            newContent: "CalculateDiscountedTotal(decimal a)");

        Assert.That(result.IsError, Is.True, "an overloaded old name must not be lifted");
        Assert.Multiple(() =>
        {
            Assert.That(result.ErrorData?.Message, Does.Not.Contain("Applied as RenameSymbol"));
            Assert.That(workspace.ReadText(CheckoutRelativePath), Does.Contain("c.CalcDisc(1m)"));
        });
    }

    // Any entry that cannot be resolved to exactly one symbol refuses the whole lift: the class rename alone is not applied.
    [Test]
    public async Task FlagOn_ClassAndOverloadedMethodRenamedInOneEdit_IsNotLiftedAtAll()
    {
        using var _ = LiftRenamesOptions.OverrideForCurrentContext(true);
        const string overloadedPricing = """
        namespace ContosoOrders.Core;

        public class OrderPricingCalculator
        {
            public decimal CalcDisc(decimal a) => a;
            public decimal CalcDisc(decimal a, decimal b) => a + b;
        }
        """;
        using var workspace = InMemoryWorkspace.Create((PricingRelativePath, overloadedPricing), (CheckoutRelativePath, CheckoutSource));
        var tools = TestTools.WorkspaceTools(workspace.Manager);
        var originalPricing = workspace.ReadText(PricingRelativePath);

        var result = await tools.ReplaceSnippetTool(
            reason: "rename the class and one overload in one edit",
            ProposedChangeAction.apply,
            filePath: workspace.PathOf(PricingRelativePath),
            oldContent: "public class OrderPricingCalculator\n{\n    public decimal CalcDisc(decimal a) => a;",
            newContent: "public class DiscountPricingCalculator\n{\n    public decimal CalculateDiscountedTotal(decimal a) => a;");

        Assert.That(result.IsError, Is.True, "an unresolvable entry must refuse the whole lift");
        Assert.Multiple(() =>
        {
            Assert.That(result.ErrorData?.Message, Does.Not.Contain("Applied as RenameSymbol"));
            Assert.That(workspace.ReadText(PricingRelativePath), Is.EqualTo(originalPricing));
            Assert.That(workspace.ReadText(CheckoutRelativePath), Does.Contain("OrderPricingCalculator c"));
        });
    }

    // H1 R5 T3/T4: one edit renamed a class AND its method, and the lift refused because it wanted exactly one rename. Each rename is now lifted on its own
    // against the baseline and the changes are unioned: the caller's line carries both new names.
    [Test]
    public async Task FlagOn_ClassAndMethodRenamedInOneEdit_BothAreLiftedAndListed()
    {
        using var _ = LiftRenamesOptions.OverrideForCurrentContext(true);
        using var workspace = CreateWorkspace();
        var tools = TestTools.WorkspaceTools(workspace.Manager);

        var result = await tools.ReplaceSnippetTool(
            reason: "rename the calculator class and its discount method in one edit",
            ProposedChangeAction.apply,
            filePath: workspace.PathOf(PricingRelativePath),
            oldContent: "public class OrderPricingCalculator\n{\n    public decimal CalcDisc(decimal a) => a;\n}",
            newContent: "public class DiscountPricingCalculator\n{\n    public decimal CalculateDiscountedTotal(decimal a) => a;\n}");

        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        var summary = result.SuccessData?.ReplacementResult?.Summary ?? string.Empty;
        Assert.Multiple(() =>
        {
            Assert.That(workspace.ReadText(CheckoutRelativePath), Does.Contain("decimal F(DiscountPricingCalculator c) => c.CalculateDiscountedTotal(1m);"));
            Assert.That(workspace.ReadText(PricingRelativePath), Does.Contain("public class DiscountPricingCalculator"));
            Assert.That(summary, Does.Contain("Applied as RenameSymbol"));
            Assert.That(summary, Does.Contain("class OrderPricingCalculator -> DiscountPricingCalculator"));
            Assert.That(summary, Does.Contain("method CalcDisc -> CalculateDiscountedTotal"));
            Assert.That(summary, Does.Contain("OrderCheckout.cs:5  decimal F(DiscountPricingCalculator c) => c.CalculateDiscountedTotal(1m);"));
        });
    }

    // Batch G R3: a rejected edit of the caller file is remembered; when a lift then rewrites that file, a later not-found oldContent copied from the
    // rejected edit must not be told "the file still has the old text", because the lift rewrote it.
    [Test]
    public async Task FlagOn_AfterALiftWroteTheCaller_NotFoundOldContentGetsNoRejectedOriginNote()
    {
        using var _ = LiftRenamesOptions.OverrideForCurrentContext(true);
        using var workspace = CreateWorkspace();
        var tools = TestTools.WorkspaceTools(workspace.Manager);
        const string callerLine = "decimal F(OrderPricingCalculator c) => c.CalcDisc(1m);";

        // 1. A caller edit that is rejected (it adds a member with an undeclared name) and so is remembered; it keeps the old call line in its newContent.
        var rejected = await tools.ReplaceSnippetTool(
            reason: "add a helper beside the caller",
            ProposedChangeAction.apply,
            filePath: workspace.PathOf(CheckoutRelativePath),
            oldContent: callerLine,
            newContent: callerLine + "\n    int G() => undeclaredName;");
        Assert.That(rejected.IsError, Is.True, "the caller edit must be rejected so that it is remembered");

        // 2. The declaration rename is lifted and rewrites the caller file.
        var lifted = await RenameMethodDeclarationOnly(workspace);
        Assert.That(lifted.IsError, Is.False, lifted.ErrorData?.Message);

        // 3. oldContent is the caller's old text, which the lift removed: an ordinary not-found, without the rejected-origin note.
        var notFound = await tools.ReplaceSnippetTool(
            reason: "edit the caller again using its old text",
            ProposedChangeAction.apply,
            filePath: workspace.PathOf(CheckoutRelativePath),
            oldContent: callerLine,
            newContent: "decimal F(OrderPricingCalculator c) => 0m;");

        Assert.That(notFound.IsError, Is.True);
        Assert.That(notFound.ErrorData?.Message, Does.Not.Contain("comes from your rejected edit's newContent"));
    }

    // Batch G R3 T2: the model blamed the rename for errors that came from the rest of its edit. When the trial rename was clean and the remaining
    // errors sit in the file the model edited, the rejection says so and lists the errors.
    [Test]
    public async Task FlagOn_LiftTriedButOtherTextInTheEditIsBroken_SaysTheRenameWasCleanAndListsTheErrors()
    {
        using var _ = LiftRenamesOptions.OverrideForCurrentContext(true);
        using var workspace = CreateWorkspace();
        var tools = TestTools.WorkspaceTools(workspace.Manager);
        var originalPricing = workspace.ReadText(PricingRelativePath);
        var originalCheckout = workspace.ReadText(CheckoutRelativePath);

        var result = await tools.ReplaceSnippetTool(
            reason: "rename the discount method and change its body",
            ProposedChangeAction.apply,
            filePath: workspace.PathOf(PricingRelativePath),
            oldContent: "public decimal CalcDisc(decimal a) => a;",
            newContent: "public decimal CalculateDiscountedTotal(decimal a) => undeclaredValue;");

        Assert.That(result.IsError, Is.True, "the rest of the edit is broken, so the lift cannot apply");
        var message = result.ErrorData?.Message ?? string.Empty;
        Assert.Multiple(() =>
        {
            Assert.That(message, Does.Contain("the rename itself applied cleanly"));
            Assert.That(message, Does.Contain("not from the rename"));
            Assert.That(message, Does.Contain("undeclaredValue"));
            Assert.That(message, Does.Not.Contain("errors remain in files the rename rewrote"));
            Assert.That(workspace.ReadText(PricingRelativePath), Is.EqualTo(originalPricing));
            Assert.That(workspace.ReadText(CheckoutRelativePath), Is.EqualTo(originalCheckout));
        });
    }

    // Batch G R3: after a lift rewrote another file the model re-sent its own call-site edit with the old text. The success summary
    // quotes the rewritten line and says the call sites are already updated.
    [Test]
    public async Task FlagOn_LiftSummary_QuotesTheRewrittenCallerLineAndSaysNotToEditItAgain()
    {
        using var _ = LiftRenamesOptions.OverrideForCurrentContext(true);
        using var workspace = CreateWorkspace();

        var result = await RenameMethodDeclarationOnly(workspace);

        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        var summary = result.SuccessData?.ReplacementResult?.Summary ?? string.Empty;
        Assert.Multiple(() =>
        {
            Assert.That(summary, Does.Contain("Rewritten call sites:"));
            Assert.That(summary, Does.Contain("OrderCheckout.cs:5  decimal F(OrderPricingCalculator c) => c.CalculateDiscountedTotal(1m);"));
            Assert.That(summary, Does.Contain("These call sites are already updated; do not edit them again."));
        });
    }
}
