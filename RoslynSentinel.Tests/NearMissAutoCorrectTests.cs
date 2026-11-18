// Eval-only near-miss auto-correction (plan_server_slip_recovery step 12b), driven through the real ReplaceSnippet tool on an InMemoryWorkspace.
// A rejected compile gate is corrected and written only when every new error is a unique near miss, each has exactly one candidate in range,
// no wrong name is declared elsewhere, and the corrected changeset validates clean. Otherwise the ordinary rejection (with its near-miss hint)
// stands and nothing is written.
using RoslynSentinel.Common;
using RoslynSentinel.Engines.Basic;
using RoslynSentinel.Tests.Battery.Basic;
using RoslynSentinel.Tests.Fakes;
using RoslynSentinel.Tools.Basic;

namespace RoslynSentinel.Tests;

[TestFixture]
[Category("EditImpactGate")]
[Category("NearMissDetector")]
public class NearMissAutoCorrectTests
{
    private const string ShippingKindsRelativePath = "ContosoOrders.Core/ShippingKinds.cs";
    private const string CheckoutRelativePath = "ContosoOrders.Core/OrderCheckout.cs";
    private const string LegacyRelativePath = "ContosoOrders.Core/LegacyKinds.cs";

    private const string OriginalLine = "public ShippingKinds Pick() => ShippingKinds.Overnight;";
    private const string SlippedLine = "public ShippingKinds Pick() => ShippingKinds.Oovernight;";

    private const string ShippingKindsSource = """
        namespace ContosoOrders.Core;

        public enum ShippingKinds
        {
            Standard,
            Overnight,
            TwoDay,
        }
        """;

    // "Overnight" and "Overnights" are both within the CorrectedLineHint limit of "Oovernight" (distance 1 and 2), so the correction is ambiguous.
    private const string TwoCandidateShippingKindsSource = """
        namespace ContosoOrders.Core;

        public enum ShippingKinds
        {
            Standard,
            Overnight,
            Overnights,
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

    // Declares the wrong spelling on another type, so the detector marks the near miss WrongNameDeclaredElsewhere.
    private const string LegacyKindsSource = """
        namespace ContosoOrders.Core;

        public enum LegacyKinds
        {
            Oovernight,
        }
        """;

    private static InMemoryWorkspace CreateWorkspace(string shippingKindsSource = ShippingKindsSource) =>
        InMemoryWorkspace.Create((ShippingKindsRelativePath, shippingKindsSource), (CheckoutRelativePath, CheckoutSource));

    private static EditImpactDetail DetailOf(ResultError? error)
    {
        Assert.That(error, Is.Not.Null, "the edit must be rejected");
        var first = error!.StructuredDetail?.FirstOrDefault();
        Assert.That(first, Is.TypeOf<EditImpactDetail>(), "errorData.structuredDetail[0] must be the EditImpactDetail");
        return (EditImpactDetail)first!;
    }

    [Test]
    public async Task AutoCorrectOn_UniqueNearMiss_AppliesTheCorrectedLineAsync()
    {
        using var workspace = CreateWorkspace();
        using var options = NearMissOptions.OverrideForCurrentContext(autoCorrect: true, nameDisplay: NearMissNameDisplay.Show);
        using var scope = NearMissRecorder.Begin();

        var result = await TestTools.WorkspaceTools(workspace.Manager).ReplaceSnippetTool(
            reason: "introduce the enum member slip on the return expression",
            ProposedChangeAction.apply,
            filePath: workspace.PathOf(CheckoutRelativePath),
            oldContent: OriginalLine,
            newContent: SlippedLine);

        Assert.Multiple(() =>
        {
            Assert.That(result.IsError, Is.False, "a unique near miss is corrected and written instead of rejected");
            Assert.That(workspace.ReadText(CheckoutRelativePath), Does.Contain("ShippingKinds.Overnight").And.Not.Contain("Oovernight"));
            Assert.That(result.Findings?.Any(f => f.Message.Contains("Oovernight") && f.Message.Contains("Overnight")) ?? false, Is.True,
                "Show mode: a finding names both the wrong and the corrected spelling");
            Assert.That(scope.Records, Has.Count.EqualTo(1), "the substitution is recorded once");
            Assert.That(scope.Records[0].Tool, Is.EqualTo("ReplaceSnippet"));
            Assert.That(scope.Records[0].Applied, Is.True, "the substitution was written");
            Assert.That(scope.Records[0].From, Is.EqualTo("Oovernight"));
            Assert.That(scope.Records[0].To, Is.EqualTo("Overnight"));
        });
    }

    [Test]
    public async Task AutoCorrectOn_TwoCandidatesInRange_RejectsWithQualifiedCandidatesAsync()
    {
        using var workspace = CreateWorkspace(TwoCandidateShippingKindsSource);
        using var options = NearMissOptions.OverrideForCurrentContext(autoCorrect: true, nameDisplay: NearMissNameDisplay.Show);

        var result = await TestTools.WorkspaceTools(workspace.Manager).ReplaceSnippetTool(
            reason: "introduce the enum member slip when two names are in range",
            ProposedChangeAction.apply,
            filePath: workspace.PathOf(CheckoutRelativePath),
            oldContent: OriginalLine,
            newContent: SlippedLine);

        Assert.That(result.IsError, Is.True, "two candidates in range mean no unique correction, so the edit is rejected");
        var detail = DetailOf(result.ErrorData);
        Assert.That(detail.NearMisses, Is.Not.Null, "the near miss hint still rides along with the rejection");
        Assert.That(detail.NearMisses!, Has.Count.EqualTo(1));
        var candidateNames = detail.NearMisses![0].Candidates.Select(c => c.Name).ToList();
        Assert.Multiple(() =>
        {
            Assert.That(candidateNames, Does.Contain("Overnight"));
            Assert.That(candidateNames, Does.Contain("Overnights"));
            Assert.That(workspace.ReadText(CheckoutRelativePath), Is.EqualTo(CheckoutSource), "the rejected edit wrote nothing");
        });
    }

    [Test]
    public async Task AutoCorrectOn_SlipPlusRealError_RejectsBothWithoutPartialFixAsync()
    {
        using var workspace = CreateWorkspace();
        using var options = NearMissOptions.OverrideForCurrentContext(autoCorrect: true, nameDisplay: NearMissNameDisplay.Show);

        var result = await TestTools.WorkspaceTools(workspace.Manager).ReplaceSnippetTool(
            reason: "introduce the enum member slip together with an unrelated type error",
            ProposedChangeAction.apply,
            filePath: workspace.PathOf(CheckoutRelativePath),
            oldContent: OriginalLine,
            newContent: $"{SlippedLine}\n        public int Other() => \"text\";");

        Assert.That(result.IsError, Is.True, "an error that is not a near miss means no partial fix");
        var detail = DetailOf(result.ErrorData);
        Assert.Multiple(() =>
        {
            Assert.That(detail.Diagnostics, Has.Some.Matches<DiagnosticInfo>(d => d!.Id == "CS0117"), "the slip is among the diagnostics");
            Assert.That(detail.Diagnostics, Has.Some.Matches<DiagnosticInfo>(d => d!.Id == "CS0029"), "the unrelated error is among the diagnostics");
            Assert.That(workspace.ReadText(CheckoutRelativePath), Is.EqualTo(CheckoutSource), "the slip is not fixed and nothing is written");
        });
    }

    [Test]
    public async Task AutoCorrectOn_WrongNameDeclaredOnAnotherType_RejectsAsync()
    {
        using var workspace = InMemoryWorkspace.Create(
            (ShippingKindsRelativePath, ShippingKindsSource),
            (CheckoutRelativePath, CheckoutSource),
            (LegacyRelativePath, LegacyKindsSource));
        using var options = NearMissOptions.OverrideForCurrentContext(autoCorrect: true, nameDisplay: NearMissNameDisplay.Show);

        var result = await TestTools.WorkspaceTools(workspace.Manager).ReplaceSnippetTool(
            reason: "introduce a slip whose wrong spelling is declared on another type",
            ProposedChangeAction.apply,
            filePath: workspace.PathOf(CheckoutRelativePath),
            oldContent: OriginalLine,
            newContent: SlippedLine);

        Assert.That(result.IsError, Is.True, "a wrong name declared elsewhere is never corrected");
        var detail = DetailOf(result.ErrorData);
        Assert.That(detail.NearMisses, Is.Not.Null, "precondition: the detector reports the near miss");
        Assert.That(detail.NearMisses![0].WrongNameDeclaredElsewhere, Is.True, "precondition: the wrong name is declared on LegacyKinds");
        Assert.That(workspace.ReadText(CheckoutRelativePath), Is.EqualTo(CheckoutSource), "the rejected edit wrote nothing");
    }

    [Test]
    public async Task AutoCorrectOff_UniqueNearMiss_RejectsAsTodayAsync()
    {
        using var workspace = CreateWorkspace();
        using var options = NearMissOptions.OverrideForCurrentContext(autoCorrect: false, nameDisplay: NearMissNameDisplay.Show);

        var result = await TestTools.WorkspaceTools(workspace.Manager).ReplaceSnippetTool(
            reason: "introduce the unique slip with auto-correction switched off",
            ProposedChangeAction.apply,
            filePath: workspace.PathOf(CheckoutRelativePath),
            oldContent: OriginalLine,
            newContent: SlippedLine);

        Assert.Multiple(() =>
        {
            Assert.That(result.IsError, Is.True, "with the option off the slip is rejected as before");
            Assert.That(DetailOf(result.ErrorData).NearMisses![0].Replacement, Is.EqualTo("ShippingKinds.Overnight"));
            Assert.That(result.Findings?.Any(f => f.Message.Contains("Corrected a misspelled name")) ?? false, Is.False,
                "no substitution finding when the option is off");
            Assert.That(workspace.ReadText(CheckoutRelativePath), Is.EqualTo(CheckoutSource), "the rejected edit wrote nothing");
        });
    }

    [Test]
    public async Task AutoCorrectOn_HideMode_FindingNamesOnlyTheCorrectedLineAsync()
    {
        using var workspace = CreateWorkspace();
        using var options = NearMissOptions.OverrideForCurrentContext(autoCorrect: true, nameDisplay: NearMissNameDisplay.Hide);

        var result = await TestTools.WorkspaceTools(workspace.Manager).ReplaceSnippetTool(
            reason: "introduce the unique slip with the name display hidden",
            ProposedChangeAction.apply,
            filePath: workspace.PathOf(CheckoutRelativePath),
            oldContent: OriginalLine,
            newContent: SlippedLine);

        Assert.That(result.IsError, Is.False, "a unique near miss is corrected and written");
        var messages = result.Findings?.Select(f => f.Message).ToList() ?? [];
        Assert.Multiple(() =>
        {
            Assert.That(messages, Has.Some.Contains("ShippingKinds.Overnight"), "Hide mode states the corrected line");
            Assert.That(messages.Any(m => m.Contains("Oovernight")), Is.False, "Hide mode never names the wrong spelling");
        });
    }
}
