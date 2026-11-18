// Step 12b-1 of plan_server_slip_recovery: NearMissAutoCorrector.TryAutoCorrectAsync, the shared engine step for the sites that write through
// ApplyProposedChangesAsync directly (ReplaceSnippet, WriteFile, ApplyDiff, ApplyUnifiedDiff). These tests drive the real compile gate on an
// InMemoryWorkspace: a rejected apply yields the report, then the helper is asked to correct and write the same change set.
using RoslynSentinel.Common;
using RoslynSentinel.Engines.Basic;
using RoslynSentinel.Tests.Fakes;

namespace RoslynSentinel.Tests;

[TestFixture]
[Category("NearMissDetector")]
public class NearMissAutoCorrectEngineTests
{
    private const string ShippingKindsRelativePath = "ContosoOrders.Core/ShippingKinds.cs";
    private const string CheckoutRelativePath = "ContosoOrders.Core/OrderCheckout.cs";

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

    // Applies the proposed text through the compile gate (validateChanges: true), which rejects it and writes nothing, and returns its report.
    private static async Task<(Dictionary<FilePathWrapper, string> Proposed, DiagnosticReport Report)> RejectedAsync(InMemoryWorkspace workspace, string proposedCheckout)
    {
        var proposed = new Dictionary<FilePathWrapper, string> { [new FilePathWrapper(workspace.PathOf(CheckoutRelativePath))] = proposedCheckout };
        var rejected = await workspace.Manager.ApplyProposedChangesAsync(proposed, validateChanges: true);
        Assert.That(rejected.IsError, Is.True, "precondition: the compile gate must reject the proposed text");
        Assert.That(rejected.ValidationResult, Is.Not.Null, "precondition: the rejection carries the compile gate's report");
        Assert.That(workspace.ReadText(CheckoutRelativePath), Is.EqualTo(CheckoutSource), "precondition: the rejected apply wrote nothing");
        return (proposed, rejected.ValidationResult!);
    }

    [Test]
    public async Task AutoCorrectOn_UniqueNearMiss_WritesTheCorrectedTextAndReturnsTheFindingsAsync()
    {
        using var workspace = CreateWorkspace();
        using var options = NearMissOptions.OverrideForCurrentContext(autoCorrect: true, nameDisplay: NearMissNameDisplay.Show);
        using var scope = NearMissRecorder.Begin();
        var (proposed, report) = await RejectedAsync(workspace, SlippedCheckoutSource);

        var outcome = await NearMissAutoCorrector.TryAutoCorrectAsync(workspace.Manager, proposed, report, "ReplaceSnippet", StaleReadPolicy.None, CancellationToken.None);

        Assert.That(outcome, Is.Not.Null, "a unique near miss must be corrected");
        Assert.Multiple(() =>
        {
            Assert.That(outcome!.ApplyResult.IsError, Is.False);
            Assert.That(workspace.ReadText(CheckoutRelativePath), Does.Contain("ShippingKinds.Overnight").And.Not.Contain("Oovernight"));
            Assert.That(outcome.Findings, Is.Not.Empty);
            Assert.That(outcome.CorrectedChanges.Values.Single(), Does.Contain("ShippingKinds.Overnight"));
            Assert.That(scope.Records, Has.Count.EqualTo(1));
            Assert.That(scope.Records[0].Applied, Is.True, "the substitution is recorded as applied");
            Assert.That(scope.Records[0].Tool, Is.EqualTo("ReplaceSnippet"));
        });
    }

    [Test]
    public async Task AutoCorrectOff_UniqueNearMiss_IsNotApplicableAndWritesNothingAsync()
    {
        using var workspace = CreateWorkspace();
        using var options = NearMissOptions.OverrideForCurrentContext(autoCorrect: false, nameDisplay: NearMissNameDisplay.Show);
        using var scope = NearMissRecorder.Begin();
        var (proposed, report) = await RejectedAsync(workspace, SlippedCheckoutSource);

        var outcome = await NearMissAutoCorrector.TryAutoCorrectAsync(workspace.Manager, proposed, report, "ReplaceSnippet", StaleReadPolicy.None, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(outcome, Is.Null);
            Assert.That(workspace.ReadText(CheckoutRelativePath), Is.EqualTo(CheckoutSource));
            Assert.That(scope.Records, Is.Empty, "with the option off the helper does no detection and records nothing");
        });
    }

    [Test]
    public async Task AutoCorrectOn_SlipPlusUnrelatedError_IsNotApplicableWritesNothingAndRecordsTheHintAsync()
    {
        using var workspace = CreateWorkspace();
        using var options = NearMissOptions.OverrideForCurrentContext(autoCorrect: true, nameDisplay: NearMissNameDisplay.Show);
        using var scope = NearMissRecorder.Begin();
        var proposedText = SlippedCheckoutSource.Replace("}", "    public int Other() => \"text\";\n}");
        var (proposed, report) = await RejectedAsync(workspace, proposedText);

        var outcome = await NearMissAutoCorrector.TryAutoCorrectAsync(workspace.Manager, proposed, report, "WriteFile", StaleReadPolicy.None, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(outcome, Is.Null, "an error that is not a near miss means no partial fix");
            Assert.That(workspace.ReadText(CheckoutRelativePath), Is.EqualTo(CheckoutSource), "the original file is unchanged");
            Assert.That(scope.Records, Has.Count.EqualTo(1), "the slip is recorded once, as a hint");
            Assert.That(scope.Records[0].Applied, Is.False);
            Assert.That(scope.Records[0].Tool, Is.EqualTo("WriteFile"));
        });
    }
}
