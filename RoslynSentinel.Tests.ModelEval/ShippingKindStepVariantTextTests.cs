using RoslynSentinel.Tests.ModelEval.Fixtures;

namespace RoslynSentinel.Tests.ModelEval;

/// <summary>
/// Fast, fixture-free checks of the ShippingKind fixture styles' golden text and prompts: the Default style is unchanged and the
/// UsingStatic style carries the directive through every step. The compile and behaviour checks of the same goldens are in
/// <see cref="ShippingKindStepVariantGoldenTests"/>.
/// </summary>
[TestFixture]
public class ShippingKindStepVariantTextTests
{
    private const string Directive = "using static ContosoOrders.Core.ShippingKinds;";

    private const string ShippingDir = "ContosoOrders.Core/Shipping/";

    private static readonly string[] TargetFiles =
    {
        ShippingDir + "ShippingFeeCalculator.cs",
        ShippingDir + "ShipmentValidator.cs",
        ShippingDir + "DeliveryEstimator.cs",
        ShippingDir + "CarrierSelector.cs",
    };

    [TestCase(0, false)]
    [TestCase(0, true)]
    [TestCase(1, false)]
    [TestCase(2, false)]
    [TestCase(3, false)]
    [TestCase(4, false)]
    [TestCase(5, false)]
    [TestCase(6, false)]
    public void DefaultStyle_IsIdenticalToTheOriginalFiles(int step, bool withVisibleTests)
    {
        var original = ShippingKindStepGoldens.FilesForStep(step, withVisibleTests);
        var styled = ShippingKindStepGoldens.FilesForStep(step, withVisibleTests, ShippingKindFixtureStyle.Default);

        Assert.That(styled, Is.EquivalentTo(original));
        Assert.That(original.Values.Any(text => text.Contains(Directive, StringComparison.Ordinal)), Is.False);
    }

    [Test]
    public void UsingStatic_Step3_SeedsTheDirectiveIntoAllFourTargetFilesStillWithLiterals()
    {
        var files = ShippingKindStepGoldens.FilesForStep(3, false, ShippingKindFixtureStyle.UsingStatic);

        foreach (var path in TargetFiles)
        {
            Assert.That(files[path], Does.StartWith(Directive + "\r\n\r\nnamespace ContosoOrders.Core;"), path);
            Assert.That(files[path], Does.Contain("\"standard\"").Or.Contain("\"express\"").Or.Contain("\"pickup\""), path);
        }

        Assert.That(files.Keys, Does.Contain(ShippingDir + "ShippingKinds.cs"));
    }

    [TestCase(4, new[] { "ShippingFeeCalculator.cs", "ShipmentValidator.cs" })]
    [TestCase(5, new[] { "DeliveryEstimator.cs", "CarrierSelector.cs" })]
    public void UsingStatic_Steps4And5_GoldensUseBareNamesAndKeepTheDirective(int step, string[] fileNames)
    {
        var files = ShippingKindStepGoldens.FilesForStep(step, false, ShippingKindFixtureStyle.UsingStatic);

        Assert.That(files.Keys, Is.EquivalentTo(fileNames.Select(name => ShippingDir + name)));
        foreach (var (path, text) in files)
        {
            Assert.That(text, Does.StartWith(Directive), path);
            Assert.That(text, Does.Not.Contain("ShippingKinds."), path);
            Assert.That(text, Does.Not.Contain("\"standard\"").And.Not.Contain("\"express\"").And.Not.Contain("\"overnight\"").And.Not.Contain("\"pickup\""), path);
        }
    }

    [Test]
    public void UsingStatic_Step5Golden_UsesBareIsExpeditedAndOvernight()
    {
        var files = ShippingKindStepGoldens.FilesForStep(5, false, ShippingKindFixtureStyle.UsingStatic);

        Assert.That(files[ShippingDir + "DeliveryEstimator.cs"], Does.Contain("if (IsExpedited(kind)) return kind == Overnight ? 1 : 2;"));
        Assert.That(files[ShippingDir + "CarrierSelector.cs"], Does.Contain("if (IsExpedited(shipment.Kind)) return \"AirFast\";"));
    }

    [Test]
    public void UsingStatic_Step6Golden_OnlyChangesTheContract()
    {
        var files = ShippingKindStepGoldens.FilesForStep(6, false, ShippingKindFixtureStyle.UsingStatic);

        Assert.That(files.Keys, Is.EquivalentTo(new[] { ShippingDir + "ShippingKinds.cs" }));
    }

    [Test]
    public void UsingStatic_Prompts_StepsOtherThan4And5ReuseFinal()
    {
        foreach (var step in new[] { 1, 2, 3, 6 })
        {
            Assert.That(ShippingKindStepGoldens.PromptForStepUsingStatic(step), Is.EqualTo(ShippingKindStepGoldens.PromptForStepFinal(step)), $"step {step}");
        }

        foreach (var step in new[] { 4, 5 })
        {
            var prompt = ShippingKindStepGoldens.PromptForStepUsingStatic(step);
            Assert.That(prompt, Does.Contain(Directive), $"step {step}");
            Assert.That(prompt, Is.Not.EqualTo(ShippingKindStepGoldens.PromptForStepFinal(step)), $"step {step}");
        }
    }
}
