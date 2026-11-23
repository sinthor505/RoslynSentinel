using NUnit.Framework;
using RoslynSentinel.Tests.ModelEval.Fixtures;

namespace RoslynSentinel.Tests.ModelEval;

[TestFixture]
public class RenameScenariosTests
{
    private const string CalcDisc = "CalcDisc";
    private const string CalculateDiscountedTotal = "CalculateDiscountedTotal";

    [Test]
    public void Registry_FindsCalcDiscRename()
    {
        Assert.That(ScenarioRegistry.Find("calcdiscrename"), Is.SameAs(RenameScenarios.CalcDiscRename));
        Assert.That(ScenarioRegistry.Find("CALCDISCRENAME"), Is.SameAs(RenameScenarios.CalcDiscRename));
        Assert.That(ScenarioRegistry.All.Select(s => s.Id), Is.Unique);
    }

    [Test]
    public void Scenario_StagesThreeFilesAndPromptFormats()
    {
        var scenario = RenameScenarios.CalcDiscRename;

        Assert.That(scenario.StagedFiles.Keys, Is.EquivalentTo(new[]
        {
            ScenarioFixtureFiles.CalculatorRelativePath,
            ScenarioFixtureFiles.CheckoutRelativePath,
            ScenarioFixtureFiles.CheckoutTestsRelativePath,
        }));

        string prompt = null!;
        Assert.DoesNotThrow(() => prompt = string.Format(scenario.PromptTemplate, @"C:\x\ContosoOrders.Core"));
        Assert.That(prompt, Does.Contain(@"C:\x\ContosoOrders.Core/FixtureHelpers/OrderCheckout.cs"));
        Assert.That(prompt, Does.Contain(CalcDisc));
        Assert.That(prompt, Does.Contain(CalculateDiscountedTotal));
        Assert.That(prompt.All(c => c <= 127), Is.True, "prompt must be pure ASCII");
    }

    [Test]
    public void ClassifyFiles_StartingContents_IsUnchanged()
    {
        var verdict = RenameScenarios.ClassifyFiles(
            OrderPricingRefactorReproducer.StartingCalculatorFileContent,
            OrderPricingRefactorReproducer.CheckoutCallerFileContent);

        Assert.That(verdict, Is.EqualTo(FinalStateVerdict.Unchanged));
    }

    [Test]
    public void ClassifyFiles_BothRenamed_IsCorrect()
    {
        var verdict = RenameScenarios.ClassifyFiles(
            OrderPricingRefactorReproducer.StartingCalculatorFileContent.Replace(CalcDisc, CalculateDiscountedTotal),
            OrderPricingRefactorReproducer.CheckoutCallerFileContent.Replace(CalcDisc, CalculateDiscountedTotal));

        Assert.That(verdict, Is.EqualTo(FinalStateVerdict.Correct));
    }

    [Test]
    public void ClassifyFiles_OnlyCalculatorRenamed_IsWrong()
    {
        var verdict = RenameScenarios.ClassifyFiles(
            OrderPricingRefactorReproducer.StartingCalculatorFileContent.Replace(CalcDisc, CalculateDiscountedTotal),
            OrderPricingRefactorReproducer.CheckoutCallerFileContent);

        Assert.That(verdict, Is.EqualTo(FinalStateVerdict.Wrong));
    }

    [Test]
    public void ClassifyFiles_EmptyCalculator_IsDestructive()
    {
        var verdict = RenameScenarios.ClassifyFiles(string.Empty, OrderPricingRefactorReproducer.CheckoutCallerFileContent);

        Assert.That(verdict, Is.EqualTo(FinalStateVerdict.Destructive));
    }

    [Test]
    public void ClassifyFiles_SummarizeShippingRenamed_IsDestructive()
    {
        var verdict = RenameScenarios.ClassifyFiles(
            OrderPricingRefactorReproducer.StartingCalculatorFileContent.Replace("SummarizeShipping", "Summarize"),
            OrderPricingRefactorReproducer.CheckoutCallerFileContent);

        Assert.That(verdict, Is.EqualTo(FinalStateVerdict.Destructive));
    }

    [Test]
    public void ClassifyFiles_NullCheckout_IsDestructive()
    {
        var verdict = RenameScenarios.ClassifyFiles(OrderPricingRefactorReproducer.StartingCalculatorFileContent, null);

        Assert.That(verdict, Is.EqualTo(FinalStateVerdict.Destructive));
    }

    [Test]
    public async Task CheckFinalState_StartingFilesOnDisk_IsUnchanged()
    {
        var solutionDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            foreach (var staged in ScenarioFixtureFiles.ChainStartingFiles)
            {
                var fullPath = ScenarioFixtureFiles.Absolute(solutionDirectory, staged.Key);
                Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
                await File.WriteAllTextAsync(fullPath, staged.Value);
            }

            var verdict = await RenameScenarios.CheckFinalStateAsync(solutionDirectory, CancellationToken.None);

            Assert.That(verdict, Is.EqualTo(FinalStateVerdict.Unchanged));
        }
        finally
        {
            if (Directory.Exists(solutionDirectory))
            {
                Directory.Delete(solutionDirectory, recursive: true);
            }
        }
    }
}
