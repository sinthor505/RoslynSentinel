// Step 5 of plan_server_slip_recovery: a rejected compile gate carries the near-miss names in errorData.structuredDetail[0].NearMisses
// (EditImpactDetail), on every gate site, when the detector finds a token slip such as ShippingKinds.Oovernight. These tests drive the real
// compile-gate rejection path (InMemoryWorkspace, no disk writes) rather than hand-built diagnostics, so the Candidates list is the real one.
using System.Text.Json;

using RoslynSentinel.Common;
using RoslynSentinel.Engines.Basic;
using RoslynSentinel.Tests.Battery.Basic;
using RoslynSentinel.Tests.Fakes;
using RoslynSentinel.Tools.Basic;

namespace RoslynSentinel.Tests;

[TestFixture]
[Category("EditImpactGate")]
[Category("NearMissDetector")]
public class NearMissRejectionTests
{
    private const string ShippingKindsRelativePath = "ContosoOrders.Core/ShippingKinds.cs";
    private const string CheckoutRelativePath = "ContosoOrders.Core/OrderCheckout.cs";

    // The slipped line is line 5 of the checkout file; the near-miss assertions rely on that.
    private const int SlipLine = 5;

    private const string ShippingKindsSource = """
        namespace ContosoOrders.Core;

        public enum ShippingKinds
        {
            Standard,
            Overnight,
            TwoDay,
        }
        """;

    private const string CheckoutSource = """
        namespace ContosoOrders.Core;

        public class OrderCheckout
        {
            public ShippingKinds Pick() => ShippingKinds.Overnight;
        }
        """;

    private static readonly string SlippedCheckoutSource = CheckoutSource.Replace("ShippingKinds.Overnight", "ShippingKinds.Oovernight");

    private static InMemoryWorkspace CreateWorkspace() =>
        InMemoryWorkspace.Create((ShippingKindsRelativePath, ShippingKindsSource), (CheckoutRelativePath, CheckoutSource));

    private static WholeFileWriteTools WholeFileTools(InMemoryWorkspace workspace) =>
        TestTools.WholeFileWriteTools(workspace.Manager, TestTools.WorkspaceTools(workspace.Manager));

    private static EditImpactDetail DetailOf(ResultError? error)
    {
        Assert.That(error, Is.Not.Null, "the edit must be rejected");
        var first = error!.StructuredDetail?.FirstOrDefault();
        Assert.That(first, Is.TypeOf<EditImpactDetail>(), "errorData.structuredDetail[0] must be the EditImpactDetail");
        return (EditImpactDetail)first!;
    }

    private static void AssertEnumMemberSlipNearMiss(EditImpactDetail detail)
    {
        Assert.That(detail.NearMisses, Is.Not.Null, "the detector must report the ShippingKinds.Oovernight slip");
        var nearMisses = detail.NearMisses!;
        Assert.That(nearMisses, Has.Count.EqualTo(1), "exactly one unresolved member in the proposed edit");
        var nearMiss = nearMisses[0];
        Assert.Multiple(() =>
        {
            Assert.That(nearMiss.DiagnosticId, Is.EqualTo("CS0117"));
            Assert.That(nearMiss.FilePath, Does.EndWith("OrderCheckout.cs"));
            Assert.That(nearMiss.Line, Is.EqualTo(SlipLine));
            Assert.That(nearMiss.Replacement, Is.EqualTo("ShippingKinds.Overnight"));
            Assert.That(nearMiss.Candidates, Is.Not.Empty, "the real candidate list must travel with the near miss");
            Assert.That(nearMiss.Candidates.Any(c => c.Name == "Overnight"), Is.True, "the candidate list must contain Overnight");
        });
    }

    [Test]
    public async Task ReplaceSnippet_EnumMemberSlip_CarriesNearMissInStructuredDetailAsync()
    {
        using var workspace = CreateWorkspace();

        var result = await TestTools.WorkspaceTools(workspace.Manager).ReplaceSnippetTool(
            reason: "introduce the enum member slip on the return expression",
            ProposedChangeAction.apply,
            filePath: workspace.PathOf(CheckoutRelativePath),
            oldContent: "public ShippingKinds Pick() => ShippingKinds.Overnight;",
            newContent: "public ShippingKinds Pick() => ShippingKinds.Oovernight;");

        Assert.That(result.IsError, Is.True, "the unresolved enum member must be rejected by the compile gate");
        var detail = DetailOf(result.ErrorData);
        AssertEnumMemberSlipNearMiss(detail);

        // The wire form: the server marshals with McpJsonUtilities.DefaultOptions (SentinelToolFactory.cs:118), which is camelCase and
        // WhenWritingNull, so a present nearMisses array is written in full.
        var json = JsonSerializer.Serialize(result.ErrorData, ModelContextProtocol.McpJsonUtilities.DefaultOptions);
        Assert.That(json, Does.Contain("\"nearMisses\":[{"));
    }

    [Test]
    public async Task WriteFile_EnumMemberSlip_CarriesNearMissInStructuredDetailAsync()
    {
        using var workspace = CreateWorkspace();

        var result = await WholeFileTools(workspace).WriteFileTool(
            reason: "rewrite the checkout file with the enum member slip",
            operation: WriteFileOperation.ReplaceFile,
            filePath: workspace.PathOf(CheckoutRelativePath),
            content: SlippedCheckoutSource);

        Assert.That(result.IsError, Is.True, "the unresolved enum member must be rejected by the compile gate");
        AssertEnumMemberSlipNearMiss(DetailOf(result.ErrorData));
    }

    [Test]
    public async Task Member_EnumMemberSlip_CarriesNearMissInStructuredDetailAsync()
    {
        using var workspace = CreateWorkspace();

        var result = await TestTools.RefactoringStructuralTools(workspace.Manager).MemberTool(
            reason: "replace the Pick method with the enum member slip",
            operation: MemberAction.replace,
            filePath: workspace.PathOf(CheckoutRelativePath),
            memberName: "Pick",
            newMemberSource: "public ShippingKinds Pick() => ShippingKinds.Oovernight;");

        Assert.That(result.IsError, Is.True, "the unresolved enum member must be rejected by the compile gate");
        AssertEnumMemberSlipNearMiss(DetailOf(result.ErrorData));
    }

    [Test]
    public async Task ReplaceSnippet_UnrelatedCompileError_HasNoNearMissesAsync()
    {
        using var workspace = CreateWorkspace();

        var result = await TestTools.WorkspaceTools(workspace.Manager).ReplaceSnippetTool(
            reason: "introduce an undeclared type, which is not a member slip",
            ProposedChangeAction.apply,
            filePath: workspace.PathOf(CheckoutRelativePath),
            oldContent: "public ShippingKinds Pick() => ShippingKinds.Overnight;",
            newContent: "public ShippingKinds Pick() => new UnknownCarrier();");

        Assert.That(result.IsError, Is.True, "the undeclared type must be rejected by the compile gate");
        var detail = DetailOf(result.ErrorData);
        Assert.That(detail.Diagnostics, Has.Some.Matches<DiagnosticInfo>(d => d!.Id == "CS0246"), "the rejection carries the CS0246 diagnostic");
        Assert.That(detail.NearMisses, Is.Null, "no member diagnostic, so the detector reports no near miss");

        // The wire form omits a null nearMisses (McpJsonUtilities.DefaultOptions is WhenWritingNull); the diagnostics still ride along.
        var json = JsonSerializer.Serialize(result.ErrorData, ModelContextProtocol.McpJsonUtilities.DefaultOptions);
        Assert.That(json, Does.Contain("\"diagnostics\":[{"), "precondition: the detail is on the wire");
        Assert.That(json, Does.Not.Contain("nearMisses"));
    }
}
