// Fixture tests (no model) for proposal_semantic_edit_impact_diagnostics.md "Second slice", step 2: errors in an edited file but outside the
// edited span are attributed like errors in an unedited file, locals and local functions are diffed, and a name that only a rejected sibling
// edit declares (pattern B: parallel calls) is attributed to that unapplied edit. Runs on InMemoryWorkspace (real compile gate, no disk writes).
using RoslynSentinel.Common;
using RoslynSentinel.Engines.Basic;
using RoslynSentinel.Tests.Battery.Basic;
using RoslynSentinel.Tests.Fakes;
using RoslynSentinel.Tools.Basic;

namespace RoslynSentinel.Tests;

[TestFixture]
[Category("WorkspaceTools")]
[Category("EditImpactGate")]
public class EditImpactSpanAttributionTests
{
    private const string PricingRelativePath = "ContosoOrders.Core/OrderPricingCalculator.cs";
    private const string CheckoutRelativePath = "ContosoOrders.Core/OrderCheckout.cs";

    private const string PricingSource = """
    namespace ContosoOrders.Core;

    public class OrderPricingCalculator
    {
        public decimal CalcDisc(decimal amount, decimal rate)
        {
            var discount = amount * rate;
            return amount - discount;
        }
    }
    """;

    private const string CheckoutSource = """
    namespace ContosoOrders.Core;

    class OrderCheckout
    {
        decimal F(OrderPricingCalculator c) => c.CalcDisc(1m, 2m);
    }
    """;

    private static InMemoryWorkspace CreateWorkspace() =>
        InMemoryWorkspace.Create((PricingRelativePath, PricingSource), (CheckoutRelativePath, CheckoutSource));

    [Test]
    public async Task RenamedLocal_WithAUseOutsideTheSnippet_IsAttributedWithAnOutsideUseCount()
    {
        using var workspace = CreateWorkspace();

        var result = await TestTools.WorkspaceTools(workspace.Manager).ReplaceSnippetTool(
            reason: "rename the local on its declaration only",
            ProposedChangeAction.apply,
            filePath: workspace.PathOf(PricingRelativePath),
            oldContent: "var discount = amount * rate;",
            newContent: "var scaled = amount * rate;");

        Assert.That(result.IsError, Is.True, "the later use of the old local name is outside the snippet and must break");
        var message = result.ErrorData?.Message ?? string.Empty;
        Assert.Multiple(() =>
        {
            Assert.That(message, Does.Contain("Next step:"));
            Assert.That(message, Does.Contain("renames local discount to scaled"));
            Assert.That(message, Does.Contain("renamed local discount -> scaled"));
            Assert.That(message, Does.Contain("1 use(s) outside your edit"));
            Assert.That(message, Does.Contain("return amount - discount;"), "the offending line outside the snippet is quoted");
            Assert.That(workspace.ReadText(PricingRelativePath), Does.Contain("var discount = amount * rate;"), "nothing is written");
        });
    }

    [Test]
    public async Task RenamedParameter_WithAUseOutsideTheSnippet_IsAttributedWithAnOutsideUseCount()
    {
        using var workspace = CreateWorkspace();

        var result = await TestTools.WorkspaceTools(workspace.Manager).ReplaceSnippetTool(
            reason: "rename the parameter in the signature only",
            ProposedChangeAction.apply,
            filePath: workspace.PathOf(PricingRelativePath),
            oldContent: "CalcDisc(decimal amount, decimal rate)",
            newContent: "CalcDisc(decimal amount, decimal discountRate)");

        Assert.That(result.IsError, Is.True, "the body still uses the old parameter name, outside the snippet");
        var message = result.ErrorData?.Message ?? string.Empty;
        Assert.Multiple(() =>
        {
            Assert.That(message, Does.Contain("Next step:"));
            Assert.That(message, Does.Contain("renames parameter rate to discountRate"));
            Assert.That(message, Does.Contain("renamed parameter rate -> discountRate"));
            Assert.That(message, Does.Contain("1 use(s) outside your edit"));
            Assert.That(message, Does.Contain("var discount = amount * rate;"));
        });
        var verdicts = result.ErrorData?.StructuredDetail?.OfType<EditImpactDetail>().Single().Verdicts ?? [];
        Assert.That(verdicts, Has.Count.EqualTo(1), string.Join(" | ", verdicts.Select(v => $"{v.Kind} {v.DeclarationKind} {v.OldName}->{v.NewName} {v.Verdict}")));
        Assert.That(verdicts[0], Has.Property("DeclarationKind").EqualTo("parameter").And.Property("Verdict").EqualTo("breaksUses"));
    }

    // Batch H5 R5: the method, its parameter (signature only) and an added helper with the same parameter types in one edit. The method's key changes with its name, and
    // the helper shares its parameter types, so the parameter rename was skipped as "member is gone or new" until the pairing pass matched them by unchanged body.
    [Test]
    public async Task RenamedParameter_WhileTheMethodIsAlsoRenamed_IsStillAttributed()
    {
        using var _ = LiftRenamesOptions.OverrideForCurrentContext(false);
        using var workspace = CreateWorkspace();

        var result = await TestTools.WorkspaceTools(workspace.Manager).ReplaceSnippetTool(
            reason: "rename the method and its parameter in the signature only, and add a helper",
            ProposedChangeAction.apply,
            filePath: workspace.PathOf(PricingRelativePath),
            oldContent: "public decimal CalcDisc(decimal amount, decimal rate)",
            newContent: "private static decimal MultiplyAmountAndRate(decimal amount, decimal rate) => amount * rate;\n\n    public decimal CalculateDiscountedTotal(decimal amount, decimal discountRate)");

        Assert.That(result.IsError, Is.True);
        var message = result.ErrorData?.Message ?? string.Empty;
        Assert.Multiple(() =>
        {
            Assert.That(message, Does.Contain("renamed parameter rate -> discountRate"), message);
            Assert.That(message, Does.Contain("use(s) outside your edit"), message);
        });
    }

    // Batch H5 R5: with the lift on the merged set (method rename applied to the caller) is rejected for the parameter; the errors sit in the model's own file, outside its
    // edit, and used to be treated as inside it because no edited spans were passed after a lift.
    [Test]
    public async Task RenamedParameter_WhileTheMethodIsAlsoRenamed_WithTheLiftOn_IsStillAttributed()
    {
        using var _ = LiftRenamesOptions.OverrideForCurrentContext(true);
        using var workspace = CreateWorkspace();

        var result = await TestTools.WorkspaceTools(workspace.Manager).ReplaceSnippetTool(
            reason: "rename the method and its parameter in the signature only, and add a helper",
            ProposedChangeAction.apply,
            filePath: workspace.PathOf(PricingRelativePath),
            oldContent: "public decimal CalcDisc(decimal amount, decimal rate)",
            // The helper has another parameter list than the method: the lift's symbol diff refuses to pair a rename when two added methods share its shape (test (a) covers that shape).
            newContent: "private static decimal Double(decimal amount) => amount * 2m;\n\n    public decimal CalculateDiscountedTotal(decimal amount, decimal discountRate)");

        Assert.That(result.IsError, Is.True, "the body still uses the old parameter name");
        var message = result.ErrorData?.Message ?? string.Empty;
        Assert.Multiple(() =>
        {
            Assert.That(message, Does.Contain("trial was rolled back"), "the lift was tried: " + message);
            Assert.That(message, Does.Contain("renamed parameter rate -> discountRate"), message);
            Assert.That(message, Does.Contain("use(s) outside your edit"), message);
        });
        var verdicts = result.ErrorData?.StructuredDetail?.OfType<EditImpactDetail>().Single().Verdicts ?? [];
        Assert.That(verdicts.Any(v => v.DeclarationKind == "parameter"), Is.True, string.Join(" | ", verdicts.Select(v => $"{v.Kind} {v.DeclarationKind} {v.OldName}->{v.NewName} {v.Verdict}")));
    }

    [Test]
    public async Task RenamedOutVariable_WithAUseOutsideTheSnippet_IsAttributed()
    {
        const string readerPath = "ContosoOrders.Core/QuantityReader.cs";
        using var workspace = InMemoryWorkspace.Create((readerPath, """
            namespace ContosoOrders.Core;

            public class QuantityReader
            {
                public int Read(string text)
                {
                    if (int.TryParse(text, out var parsed))
                    {
                        return parsed * 2;
                    }

                    return 0;
                }
            }
            """));

        var result = await TestTools.WorkspaceTools(workspace.Manager).ReplaceSnippetTool(
            reason: "rename the out variable on its declaration only",
            ProposedChangeAction.apply,
            filePath: workspace.PathOf(readerPath),
            oldContent: "out var parsed))",
            newContent: "out var value))");

        Assert.That(result.IsError, Is.True);
        var message = result.ErrorData?.Message ?? string.Empty;
        Assert.Multiple(() =>
        {
            Assert.That(message, Does.Contain("renames local parsed to value"));
            Assert.That(message, Does.Contain("1 use(s) outside your edit"));
            Assert.That(message, Does.Contain("return parsed * 2;"));
        });
    }

    [Test]
    public async Task ATypoInsideTheSnippet_IsNotAttributedToAnything()
    {
        using var workspace = CreateWorkspace();

        var result = await TestTools.WorkspaceTools(workspace.Manager).ReplaceSnippetTool(
            reason: "introduce a typo inside the snippet",
            ProposedChangeAction.apply,
            filePath: workspace.PathOf(PricingRelativePath),
            oldContent: "return amount - discount;",
            newContent: "return amount - missingName;");

        Assert.That(result.IsError, Is.True);
        var message = result.ErrorData?.Message ?? string.Empty;
        Assert.Multiple(() =>
        {
            Assert.That(message, Does.Not.Contain("Next step:"), "an error inside the edit with no unapplied sibling stays a plain compiler error");
            Assert.That(message, Does.Not.Contain("Edit impact"));
            Assert.That(message, Does.Contain("missingName"));
        });
    }

    // Pattern B of the proposal: call 1 (a rename that also names a base interface nobody declared) is rejected; call 2 (the caller file, sent in parallel)
    // uses that interface, which only the rejected call 1 mentions. Call 2 used to get a bare CS0246.
    [Test]
    public async Task ParallelCallerEdit_UsingANameOnlyARejectedSiblingMentions_GetsAnAttributedNextStep()
    {
        using var workspace = CreateWorkspace();
        var tools = TestTools.WorkspaceTools(workspace.Manager);

        var first = await tools.ReplaceSnippetTool(
            reason: "rename the calculator and give it a base interface",
            ProposedChangeAction.apply,
            filePath: workspace.PathOf(PricingRelativePath),
            oldContent: "public class OrderPricingCalculator",
            newContent: "public class StandardPricingCalculator : IPricingPolicyContract");
        Assert.That(first.IsError, Is.True, "IPricingPolicyContract is declared nowhere, so call 1 is rejected");

        var second = await tools.ReplaceSnippetTool(
            reason: "point the caller at the interface",
            ProposedChangeAction.apply,
            filePath: workspace.PathOf(CheckoutRelativePath),
            oldContent: "decimal F(OrderPricingCalculator c) => c.CalcDisc(1m, 2m);",
            newContent: "decimal F(IPricingPolicyContract c) => 1m;");

        Assert.That(second.IsError, Is.True);
        var message = second.ErrorData?.Message ?? string.Empty;
        Assert.Multiple(() =>
        {
            Assert.That(message, Does.Contain("Next step:"));
            Assert.That(message, Does.Contain("IPricingPolicyContract is declared nowhere in the solution"));
            Assert.That(message, Does.Contain("your other edit of OrderPricingCalculator.cs"));
            Assert.That(message, Does.Contain("fix it first if it was rejected"));
        });
    }

    // The other order of pattern B: the caller edit is checked while the sibling edit is still running (no rejection has been recorded yet). Deterministic:
    // the sibling is held inside its apply by FakeWorkspaceManager.BeforeApply, the caller edit runs to completion, then the sibling is released.
    [Test]
    public async Task ParallelCallerEdit_CheckedWhileTheSiblingIsStillInFlight_GetsAnAttributedNextStep()
    {
        using var workspace = CreateWorkspace();
        var tools = TestTools.WorkspaceTools(workspace.Manager);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int applies = 0;
        workspace.Manager.BeforeApply = async () =>
        {
            if (Interlocked.Increment(ref applies) == 1)
            {
                entered.SetResult();
                await release.Task;
            }
        };

        var siblingTask = tools.ReplaceSnippetTool(
            reason: "rename the calculator and give it a base interface",
            ProposedChangeAction.apply,
            filePath: workspace.PathOf(PricingRelativePath),
            oldContent: "public class OrderPricingCalculator",
            newContent: "public class StandardPricingCalculator : IPricingPolicyContract");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.That(siblingTask.IsCompleted, Is.False, "the sibling must still be in flight, with no rejection recorded yet");

        var caller = await tools.ReplaceSnippetTool(
            reason: "point the caller at the interface",
            ProposedChangeAction.apply,
            filePath: workspace.PathOf(CheckoutRelativePath),
            oldContent: "decimal F(OrderPricingCalculator c) => c.CalcDisc(1m, 2m);",
            newContent: "decimal F(IPricingPolicyContract c) => 1m;");

        release.SetResult();
        var sibling = await siblingTask;

        Assert.That(sibling.IsError, Is.True, "IPricingPolicyContract is declared nowhere, so the sibling is rejected too");
        Assert.That(caller.IsError, Is.True);
        var message = caller.ErrorData?.Message ?? string.Empty;
        Assert.Multiple(() =>
        {
            Assert.That(message, Does.Contain("Next step:"));
            Assert.That(message, Does.Contain("IPricingPolicyContract is declared nowhere in the solution"));
            Assert.That(message, Does.Contain("your other edit of OrderPricingCalculator.cs"));
        });
    }

    // Step 3: the verdict list reaches the wire in errorData.structuredDetail (not only as message text), so a harness can assert on it.
    [Test]
    public async Task RejectedRename_CarriesTheVerdictListInStructuredDetail_OnTheWire()
    {
        using var _ = LiftRenamesOptions.OverrideForCurrentContext(false);
        using var workspace = CreateWorkspace();

        var result = await TestTools.WorkspaceTools(workspace.Manager).ReplaceSnippetTool(
            reason: "rename the discount method on its declaration only",
            ProposedChangeAction.apply,
            filePath: workspace.PathOf(PricingRelativePath),
            oldContent: "CalcDisc(decimal amount, decimal rate)",
            newContent: "CalculateTotal(decimal amount, decimal rate)");

        Assert.That(result.IsError, Is.True);
        var detail = result.ErrorData?.StructuredDetail?.OfType<EditImpactDetail>().Single();
        Assert.That(detail, Is.Not.Null, "structuredDetail is one {verdicts, diagnostics} item");
        Assert.That(detail!.Verdicts, Has.Count.EqualTo(1));
        Assert.That(detail.Diagnostics.Any(d => d.Id == "CS1061"), Is.True, "the single path carries its compiler diagnostics too");
        var verdict = detail.Verdicts[0];
        Assert.Multiple(() =>
        {
            Assert.That(verdict.Kind, Is.EqualTo("renamed"));
            Assert.That(verdict.DeclarationKind, Is.EqualTo("method"));
            Assert.That(verdict.OldName, Is.EqualTo("CalcDisc"));
            Assert.That(verdict.NewName, Is.EqualTo("CalculateTotal"));
            Assert.That(verdict.Verdict, Is.EqualTo("breaksUses"));
            Assert.That(verdict.BrokenUseCount, Is.EqualTo(1));
            Assert.That(verdict.BrokenUses.Single().FilePath.ToString(), Does.EndWith("OrderCheckout.cs"));
            Assert.That(verdict.SuggestedTool, Is.EqualTo("RenameSymbol"));
            Assert.That(verdict.NextStep, Does.Contain("RenameSymbol("));
            Assert.That(verdict.Location, Does.StartWith("OrderPricingCalculator.cs:"));
        });

        // The wire form: camelCase properties, the verdict named in plain text.
        var json = System.Text.Json.JsonSerializer.Serialize(result.ErrorData, new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase });
        Assert.That(json, Does.Contain("\"verdicts\":[{").And.Contain("\"diagnostics\":[{").And.Contain("\"verdict\":\"breaksUses\"").And.Contain("\"kind\":\"renamed\"").And.Contain("\"brokenUseCount\":1"));
    }

    [Test]
    public async Task RejectedBatchRename_CarriesVerdictsBeforeTheCompilerDiagnosticsItAlreadyHad()
    {
        using var _ = LiftRenamesOptions.OverrideForCurrentContext(false);
        using var workspace = CreateWorkspace();

        var result = await TestTools.WorkspaceTools(workspace.Manager).ReplaceSnippetTool(
            reason: "rename the discount method inside a batch",
            ProposedChangeAction.apply,
            batchEdits:
            [
                new SnippetEdit { FilePath = workspace.PathOf(PricingRelativePath), OldContent = "CalcDisc(decimal amount, decimal rate)", NewContent = "CalculateTotal(decimal amount, decimal rate)" },
            ]);

        Assert.That(result.IsError, Is.True);
        var detail = result.ErrorData?.StructuredDetail?.OfType<EditImpactDetail>().Single();
        Assert.That(detail, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(detail!.Verdicts.Single().Verdict, Is.EqualTo("breaksUses"));
            Assert.That(detail.Diagnostics.Any(d => d.Id == "CS1061"), Is.True, "the batch path's diagnostics are in the same item");
        });
    }

    [Test]
    public async Task LiftedRename_ReportsVerdictLiftedInTheFindings()
    {
        using var _ = LiftRenamesOptions.OverrideForCurrentContext(true);
        using var workspace = CreateWorkspace();

        var result = await TestTools.WorkspaceTools(workspace.Manager).ReplaceSnippetTool(
            reason: "rename the discount method on its declaration only",
            ProposedChangeAction.apply,
            filePath: workspace.PathOf(PricingRelativePath),
            oldContent: "CalcDisc(decimal amount, decimal rate)",
            newContent: "CalculateTotal(decimal amount, decimal rate)");

        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        var finding = result.Findings.SingleOrDefault(f => f.Source == "EditImpact");
        Assert.That(finding, Is.Not.Null, "a lifted write carries its verdict as a finding");
        Assert.That(finding!.Message, Does.Contain("verdict=lifted").And.Contain("kind=renamed").And.Contain("old=CalcDisc").And.Contain("new=CalculateTotal"));
    }

    [Test]
    public async Task EditThatBreaksNothing_HasNoVerdictFinding()
    {
        using var workspace = CreateWorkspace();

        var result = await TestTools.WorkspaceTools(workspace.Manager).ReplaceSnippetTool(
            reason: "change the body only",
            ProposedChangeAction.apply,
            filePath: workspace.PathOf(PricingRelativePath),
            oldContent: "var discount = amount * rate;",
            newContent: "var discount = amount * rate * 2m;");

        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        Assert.That(result.Findings.Any(f => f.Source == "EditImpact"), Is.False);
    }
}
