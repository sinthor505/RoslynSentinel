using RoslynSentinel.Tests.ModelEval.Fixtures;

namespace RoslynSentinel.Tests.ModelEval;

/// <summary>
/// No-model self-test for the non-default ShippingKind fixture styles (see <see cref="ShippingKindFixtureStyle"/>): each step's
/// style golden must pass that step's check, and the previous step's style golden must not. The default style is covered by
/// <see cref="ShippingKindStepGoldenTests"/>. One fixture instance per style, each with its own sample copy and git repository;
/// the methods share them, so they must not run in parallel.
/// </summary>
[TestFixture(ShippingKindFixtureStyle.UsingStatic)]
[TestFixture(ShippingKindFixtureStyle.PriorityOvernight)]
[Explicit("No model needed. Run after changing a ShippingKind golden or check: ShippingKindStepVariantGoldenTests")]
public class ShippingKindStepVariantGoldenTests
{
    private const string FirstStepCannedReply =
        "ShippingFeeCalculator.cs, Calculate, standard / ShipmentValidator.cs, IsValid, express / DeliveryEstimator.cs, EstimateDays, pickup / CarrierSelector.cs, Select, overnight";

    private readonly ShippingKindFixtureStyle _style;
    private RoslynSentinel.Tests.TestSolutionFixture? _fixture;
    private GoldenStepRepository? _repository;

    public ShippingKindStepVariantGoldenTests(ShippingKindFixtureStyle style)
    {
        _style = style;
    }

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
            step => ShippingKindStepGoldens.FilesForStep(step, false, _style));
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
        _repository!.CheckoutStep(step, $"verify/{_style}/step-{step}");
        var changedPaths = PathsChangedFromPreviousStep(step);
        var finalMessage = step == 1 ? FirstStepCannedReply : null;

        var (outcome, detail) = await ShippingKindStepChecks.CheckAsync(
            step,
            _fixture!.SolutionDirectory,
            finalMessage,
            changedPaths,
            TestContext.CurrentContext.CancellationToken,
            _style);

        Assert.That(outcome, Is.EqualTo(ShippingKindStepOutcome.Pass), $"{_style} step {step}: {detail}");
    }

    [TestCase(2)]
    [TestCase(3)]
    [TestCase(4)]
    [TestCase(5)]
    [TestCase(6)]
    public async Task PreviousGolden_FailsTheCheck(int step)
    {
        _repository!.CheckoutStep(step - 1, $"verify/{_style}/previous-of-step-{step}");

        var (outcome, detail) = await ShippingKindStepChecks.CheckAsync(
            step,
            _fixture!.SolutionDirectory,
            null,
            Array.Empty<string>(),
            TestContext.CurrentContext.CancellationToken,
            _style);
        TestContext.Out.WriteLine($"{_style}: step {step} check on step-{step - 1} golden: {outcome}: {detail}");

        Assert.That(
            outcome,
            Is.Not.EqualTo(ShippingKindStepOutcome.Pass),
            $"{_style}: step {step} check passed on the step-{step - 1} golden: {detail}");
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
        var injected = ShippingKindStepGoldens.HarnessInjectedPathsForStep(step, _style);
        return output
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && !injected.Contains(line, StringComparer.OrdinalIgnoreCase))
            .ToList();
    }
}
