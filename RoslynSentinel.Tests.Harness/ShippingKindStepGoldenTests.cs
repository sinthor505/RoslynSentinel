using RoslynSentinel.Tests.ModelEval.Fixtures;

namespace RoslynSentinel.Tests.ModelEval;

/// <summary>
/// No-model self-test for the ShippingKind stepped scenario goldens. Each step's golden must pass that step's check,
/// and the previous step's golden must not. All methods share one fixture and one git repository, so they must not
/// run in parallel.
/// </summary>
[TestFixture]
[Explicit("No model needed. Run after changing a ShippingKind golden or check: ShippingKindStepGoldenTests")]
public class ShippingKindStepGoldenTests
{
    private const string FirstStepCannedReply =
        "ShippingFeeCalculator.cs, Calculate, standard / ShipmentValidator.cs, IsValid, express / DeliveryEstimator.cs, EstimateDays, pickup / CarrierSelector.cs, Select, overnight";

    private RoslynSentinel.Tests.TestSolutionFixture? _fixture;
    private GoldenStepRepository? _repository;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        if (!RoslynSentinel.Tests.Battery.Basic.GitTestRepo.IsGitAvailable())
        {
            Assert.Ignore("git not available");
        }

        _fixture = new RoslynSentinel.Tests.TestSolutionFixture(restorePackages: true);
        _repository = GoldenStepRepository.Create(
            _fixture.SolutionDirectory,
            ShippingKindStepGoldens.LastStep,
            ShippingKindStepGoldens.FilesForStep);
    }

    [OneTimeTearDown]
    public void OneTimeTearDown() => _fixture?.Dispose();

    [TestCase(1)]
    [TestCase(2)]
    [TestCase(3)]
    [TestCase(4)]
    [TestCase(5)]
    [TestCase(6)]
    public async Task Golden_PassesItsOwnCheck(int step)
    {
        _repository!.CheckoutStep(step, $"verify/step-{step}");
        var changedPaths = PathsChangedFromPreviousStep(step);
        var finalMessage = step == 1 ? FirstStepCannedReply : null;

        var (outcome, detail) = await ShippingKindStepChecks.CheckAsync(
            step,
            _fixture!.SolutionDirectory,
            finalMessage,
            changedPaths,
            TestContext.CurrentContext.CancellationToken);

        Assert.That(outcome, Is.EqualTo(ShippingKindStepOutcome.Pass), $"step {step}: {detail}");
    }

    [TestCase(2)]
    [TestCase(3)]
    [TestCase(4)]
    [TestCase(5)]
    [TestCase(6)]
    public async Task PreviousGolden_FailsTheCheck(int step)
    {
        _repository!.CheckoutStep(step - 1, $"verify/previous-of-step-{step}");

        var (outcome, detail) = await ShippingKindStepChecks.CheckAsync(
            step,
            _fixture!.SolutionDirectory,
            null,
            Array.Empty<string>(),
            TestContext.CurrentContext.CancellationToken);
        TestContext.Out.WriteLine($"step {step} check on step-{step - 1} golden: {outcome}: {detail}");

        Assert.That(
            outcome,
            Is.Not.EqualTo(ShippingKindStepOutcome.Pass),
            $"step {step} check passed on the step-{step - 1} golden: {detail}");
    }

    private const string InlinePairDeliveryEstimatorSource = """
namespace ContosoOrders.Core;

public static class DeliveryEstimator
{
    public static int EstimateDays(string kind)
    {
        var expedited = ShippingKinds.IsExpedited(kind);
        if (kind == ShippingKinds.Express || kind == ShippingKinds.Overnight) return kind == ShippingKinds.Overnight ? 1 : 2;
        if (kind == ShippingKinds.Pickup) return 0;
        if (kind == ShippingKinds.Standard) return 5;
        throw new ArgumentException($"Unknown shipping kind: {kind}", nameof(kind));
    }
}
""";

    private const string InlinePairCarrierSelectorSource = """
namespace ContosoOrders.Core;

public static class CarrierSelector
{
    public static string Select(Shipment shipment)
    {
        var expedited = ShippingKinds.IsExpedited(shipment.Kind);
        if (shipment.Kind == ShippingKinds.Pickup) return "none";
        if (shipment.Kind == ShippingKinds.Express || shipment.Kind == ShippingKinds.Overnight) return "AirFast";
        if (shipment.Kind == ShippingKinds.Standard) return "GroundPost";
        throw new ArgumentException($"Unknown shipping kind: {shipment.Kind}", nameof(shipment));
    }
}
""";

    [Test]
    public async Task Step5_ConstantsWithAnInlineKindPair_PassesTheTextChecksButFailsTheContractCheck()
    {
        _repository!.CheckoutStep(4, "verify/inline-pair");
        var shippingDir = Path.Combine(_fixture!.SolutionDirectory, "ContosoOrders.Core", "Shipping");
        await File.WriteAllTextAsync(
            Path.Combine(shippingDir, "DeliveryEstimator.cs"),
            InlinePairDeliveryEstimatorSource.Replace("\r\n", "\n").Replace("\n", "\r\n"));
        await File.WriteAllTextAsync(
            Path.Combine(shippingDir, "CarrierSelector.cs"),
            InlinePairCarrierSelectorSource.Replace("\r\n", "\n").Replace("\n", "\r\n"));
        try
        {
            var (outcome, detail) = await ShippingKindStepChecks.CheckAsync(
                5,
                _fixture.SolutionDirectory,
                null,
                Array.Empty<string>(),
                TestContext.CurrentContext.CancellationToken);

            Assert.That(outcome, Is.EqualTo(ShippingKindStepOutcome.CheckFailed), detail);
            Assert.That(detail, Does.Contain("contract-change check failed"), detail);
        }
        finally
        {
            RoslynSentinel.Tests.Battery.Basic.GitTestRepo.RunGit(_repository!.RepoDirectory, "checkout", "--", ".");
        }
    }

    [Test]
    public async Task Step5_VisibleBehaviourTestsFile_DoesNotFailTheContractCheck()
    {
        _repository!.CheckoutStep(5, "verify/visible-tests");
        var visibleTests = ShippingKindStepGoldens.FilesForStep(0, withVisibleTests: true)
            .Single(entry => entry.Key.EndsWith("ShippingBehaviourTests.cs", StringComparison.Ordinal));
        var visibleTestsPath = Path.Combine(_fixture!.SolutionDirectory, visibleTests.Key.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(visibleTestsPath)!);
        await File.WriteAllTextAsync(visibleTestsPath, visibleTests.Value);
        try
        {
            var (outcome, detail) = await ShippingKindStepChecks.CheckAsync(
                5,
                _fixture.SolutionDirectory,
                null,
                Array.Empty<string>(),
                TestContext.CurrentContext.CancellationToken);

            Assert.That(outcome, Is.EqualTo(ShippingKindStepOutcome.Pass), detail);
        }
        finally
        {
            File.Delete(visibleTestsPath);
            RoslynSentinel.Tests.Battery.Basic.GitTestRepo.RunGit(_repository!.RepoDirectory, "checkout", "--", ".");
        }
    }

    private IReadOnlyList<string> PathsChangedFromPreviousStep(int step)
    {
        if (step == 1)
        {
            return Array.Empty<string>();
        }

        var output = RoslynSentinel.Tests.Battery.Basic.GitTestRepo.RunGit(
            _repository!.RepoDirectory,
            "diff",
            "--name-only",
            GoldenStepRepository.TagForStep(step - 1),
            GoldenStepRepository.TagForStep(step));
        return output
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .ToList();
    }
}
