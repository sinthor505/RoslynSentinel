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
using RoslynSentinel.Tools.Basic;

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
        InMemoryWorkspace workspace, Func<DiagnosticReport, CancellationToken, Task<(string? Text, IReadOnlyList<object>? StructuredDetail)>>? describeEditImpact)
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

        var error = await RejectRenameThroughHelperAsync(workspace, (_, _) => Task.FromResult<(string? Text, IReadOnlyList<object>? StructuredDetail)>((null, null)));

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

        var error = await RejectRenameThroughHelperAsync(workspace, (_, _) => Task.FromResult<(string? Text, IReadOnlyList<object>? StructuredDetail)>(("Next step: IMPACT-MARKER", null)));

        Assert.That(error.Message, Does.Contain("IMPACT-MARKER"));
        Assert.That(error.Message, Does.Not.Contain(UntouchedSentence));
        // The impact text ends with a Next step; a trailing "retry" contradicted it (proposal_semantic_edit_impact_diagnostics.md, Second slice step 0).
        Assert.That(error.Message, Does.Not.Contain("Fix the issue(s) below and retry"));
    }

    // Step 3 follow-up: every gate site that carries edit-impact text also carries structuredDetail[0] = EditImpactDetail { verdicts, diagnostics },
    // the shape a rejected ReplaceSnippet already has. The message text is asserted unchanged by the tests above and by the existing tool tests.
    private static EditImpactDetail DetailOf(ResultError? error)
    {
        Assert.That(error, Is.Not.Null, "the edit must be rejected");
        Assert.That(error!.ErrorCode, Is.EqualTo(ToolErrorCode.ValidationFailed), error.Message);
        var first = error.StructuredDetail?.FirstOrDefault();
        Assert.That(first, Is.TypeOf<EditImpactDetail>(), "errorData.structuredDetail[0] must be the EditImpactDetail");
        return (EditImpactDetail)first!;
    }

    private static void AssertBreaksUsesOfCalcDisc(ResultError? error)
    {
        var detail = DetailOf(error);
        Assert.Multiple(() =>
        {
            Assert.That(detail.Verdicts, Has.Some.Matches<ImpactVerdict>(v => v!.Verdict == "breaksUses" && v.OldName == "CalcDisc" && v.BrokenUseCount > 0),
                string.Join(" | ", detail.Verdicts.Select(v => $"{v.Kind} {v.OldName}->{v.NewName} {v.Verdict}")));
            Assert.That(detail.Diagnostics, Is.Not.Empty, "the compiler diagnostics that rejected the edit ride along");
        });
    }

    private const string RenamedPricingSource = """
    namespace ContosoOrders.Core;

    public class OrderPricingCalculator
    {
        public decimal CalculateDiscountedTotal(decimal a) => a;
    }
    """;

    private static WholeFileWriteTools WholeFileTools(InMemoryWorkspace workspace) =>
        TestTools.WholeFileWriteTools(workspace.Manager, TestTools.WorkspaceTools(workspace.Manager));

    [Test]
    public async Task Member_DeclarationOnlyRename_CarriesTheVerdictListInStructuredDetailAsync()
    {
        using var workspace = CreateWorkspace();

        var result = await RenameDeclarationThroughMemberAsync(workspace);

        AssertBreaksUsesOfCalcDisc(result.ErrorData);
    }

    [Test]
    public async Task Helper_DescribeEditImpactReturnsDetail_ReplacesTheDiagnosticListInStructuredDetailAsync()
    {
        using var workspace = CreateWorkspace();
        var marker = new object();

        var error = await RejectRenameThroughHelperAsync(workspace, (_, _) => Task.FromResult<(string? Text, IReadOnlyList<object>? StructuredDetail)>((null, new[] { marker })));

        Assert.That(error.StructuredDetail, Is.EqualTo(new[] { marker }));
    }

    [Test]
    public async Task Helper_NoDescribeEditImpact_KeepsTheCompilerDiagnosticsInStructuredDetailAsync()
    {
        using var workspace = CreateWorkspace();

        var error = await RejectRenameThroughHelperAsync(workspace, describeEditImpact: null);

        Assert.That(error.StructuredDetail, Is.Not.Null.And.Not.Empty);
        Assert.That(error.StructuredDetail, Has.All.TypeOf<DiagnosticInfo>(), "without the edit-impact callback the detail is the diagnostic list, as before");
    }

    [Test]
    public async Task WriteFile_DeclarationOnlyRename_CarriesBreaksUsesVerdictInStructuredDetailAsync()
    {
        using var workspace = CreateWorkspace();

        var result = await WholeFileTools(workspace).WriteFileTool(
            reason: "rewrite the pricing file with the method renamed",
            operation: WriteFileOperation.ReplaceFile,
            filePath: workspace.PathOf(PricingRelativePath),
            content: RenamedPricingSource);

        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData!.Message, Does.Contain("This edit renames CalcDisc to CalculateDiscountedTotal"), "the message text keeps the edit-impact report");
        AssertBreaksUsesOfCalcDisc(result.ErrorData);
    }

    [Test]
    public async Task ApplyDiff_FilesFormatDeclarationOnlyRename_CarriesBreaksUsesVerdictInStructuredDetailAsync()
    {
        using var workspace = CreateWorkspace();

        var result = await WholeFileTools(workspace).ApplyDiffTool(
            reason: "apply the renamed pricing file as a change set",
            ChangesetFormat.files,
            ProposedChangeAction.apply,
            changes: new Dictionary<string, string> { [workspace.PathOf(PricingRelativePath)] = RenamedPricingSource });

        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData!.Message, Does.Contain("This edit renames CalcDisc to CalculateDiscountedTotal"));
        AssertBreaksUsesOfCalcDisc(result.ErrorData);
    }

    [Test]
    public async Task ApplyUnifiedDiff_DeclarationOnlyRename_CarriesBreaksUsesVerdictInStructuredDetailAsync()
    {
        using var workspace = CreateWorkspace();
        const string unifiedDiff = "@@ -3,4 +3,4 @@\n public class OrderPricingCalculator\n {\n-    public decimal CalcDisc(decimal a) => a;\n+    public decimal CalculateDiscountedTotal(decimal a) => a;\n }\n";

        var result = await WholeFileTools(workspace).ApplyUnifiedDiffTool(
            reason: "rename the discount method with a unified diff",
            ProposedChangeAction.apply,
            filePath: workspace.PathOf(PricingRelativePath),
            unifiedDiff: unifiedDiff);

        Assert.That(result.IsError, Is.True, result.ErrorData?.Message);
        Assert.That(result.ErrorData!.Message, Does.Contain("This edit renames CalcDisc to CalculateDiscountedTotal"));
        AssertBreaksUsesOfCalcDisc(result.ErrorData);
    }

    // A created file only adds declarations, so it cannot break a use in another file by the symbol diff (no breaksUses verdict is possible here);
    // what CreateFile must still carry is the same structuredDetail[0] shape, with the diagnostics that rejected it.
    [Test]
    public async Task CreateFile_RejectedContent_CarriesTheEditImpactDetailShapeInStructuredDetailAsync()
    {
        using var workspace = CreateWorkspace();

        var result = await TestTools.WorkspaceTools(workspace.Manager).CreateFileTool(
            reason: "create a file that does not compile",
            filePath: workspace.PathOf("ContosoOrders.Core/OrderAudit.cs"),
            content: "namespace ContosoOrders.Core;\n\nclass OrderAudit\n{\n    int F() => MissingName;\n}\n");

        Assert.That(result.IsError, Is.True);
        var detail = DetailOf(result.ErrorData);
        Assert.That(detail.Diagnostics, Has.Some.Matches<DiagnosticInfo>(d => d!.Id == "CS0103"));
        Assert.That(result.ErrorData!.Message, Does.Contain("CS0103"), "the message text is unchanged");
    }
}
