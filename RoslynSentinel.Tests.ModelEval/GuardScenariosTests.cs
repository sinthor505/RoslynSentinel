using NUnit.Framework;
using RoslynSentinel.Tests.ModelEval.Fixtures;

namespace RoslynSentinel.Tests.ModelEval;

[TestFixture]
public class GuardScenariosTests
{
    private const string SolutionDirectory = @"C:\x\ContosoOrders.Core";
    private const string SummarizeShippingStart = "public string SummarizeShipping";

    private static string StartingCalculator => OrderPricingRefactorReproducer.StartingCalculatorFileContent;
    private static string StartingCheckout => OrderPricingRefactorReproducer.CheckoutCallerFileContent;
    private static string StartingInterface => InterfaceScenarios.InterfaceStartingContent;

    private static string CalculatorWithNationalPost => StartingCalculator.Replace("\"national\"", "\"national-post\"");

    private static IEnumerable<ScenarioDefinition> GuardDefinitions =>
    [
        GuardScenarios.TwinApplyCalls,
        GuardScenarios.EmptyInterfaceRemoval,
    ];

    // ---- Registry ----

    [TestCase(GuardScenarios.TwinApplyCallsId)]
    [TestCase(GuardScenarios.EmptyInterfaceRemovalId)]
    public void Registry_FindsGuardScenarioIgnoringCase(string id)
    {
        var scenario = ScenarioRegistry.Find(id.ToUpperInvariant());

        Assert.That(scenario, Is.Not.Null);
        Assert.That(scenario!.Id, Is.EqualTo(id));
        Assert.That(ScenarioRegistry.Find(id.ToLowerInvariant()), Is.SameAs(scenario));
        Assert.That(ScenarioRegistry.All, Does.Contain(scenario));
    }

    // ---- Definition shape ----

    [TestCaseSource(nameof(GuardDefinitions))]
    public void Guard_ExpectsRecovery(ScenarioDefinition scenario)
    {
        Assert.That(scenario.Expected, Is.EqualTo(ExpectedOutcome.Recover));
    }

    [TestCaseSource(nameof(GuardDefinitions))]
    public void Guard_ReproducesAtIsNotMeasured(ScenarioDefinition scenario)
    {
        Assert.That(scenario.ReproducesAt, Does.StartWith("not measured"));
    }

    [TestCaseSource(nameof(GuardDefinitions))]
    public void Guard_PromptFormatsAndIsAscii(ScenarioDefinition scenario)
    {
        string prompt = null!;
        Assert.DoesNotThrow(() => prompt = string.Format(scenario.PromptTemplate, SolutionDirectory));
        Assert.That(prompt.All(c => c <= 127), Is.True, "prompt must be pure ASCII");
    }

    [Test]
    public void TwinApplyCalls_PromptContainsMarkers()
    {
        var prompt = string.Format(GuardScenarios.TwinApplyCalls.PromptTemplate, SolutionDirectory);

        Assert.That(prompt, Does.Contain("national-post"));
        Assert.That(prompt, Does.Contain("SummarizeShipping"));
        Assert.That(prompt, Does.Contain("ReplaceSnippet"));
    }

    [Test]
    public void EmptyInterfaceRemoval_PromptContainsMarkers()
    {
        var prompt = string.Format(GuardScenarios.EmptyInterfaceRemoval.PromptTemplate, SolutionDirectory);

        Assert.That(prompt, Does.Contain("IOrderPricingCalculator"));
        Assert.That(prompt, Does.Contain("Remove"));
        Assert.That(prompt, Does.Contain("ReplaceSnippet"));
    }

    [Test]
    public void TwinApplyCalls_ExpectedRecoveryToolIsReplaceSnippet()
    {
        Assert.That(GuardScenarios.TwinApplyCalls.ExpectedRecoveryTool, Is.EqualTo("ReplaceSnippet"));
    }

    [Test]
    public void EmptyInterfaceRemoval_HasNoExpectedRecoveryTool()
    {
        Assert.That(GuardScenarios.EmptyInterfaceRemoval.ExpectedRecoveryTool, Is.Null);
    }

    // ---- Staged files ----

    [Test]
    public void TwinApplyCalls_StagesChainStartingFiles()
    {
        Assert.That(
            GuardScenarios.TwinApplyCalls.StagedFiles.Keys,
            Is.EquivalentTo(ScenarioFixtureFiles.ChainStartingFiles.Keys));
    }

    [Test]
    public void EmptyInterfaceRemoval_StagesChainStartingFilesPlusInterface()
    {
        var staged = GuardScenarios.EmptyInterfaceRemoval.StagedFiles;

        Assert.That(
            staged.Keys,
            Is.EquivalentTo(ScenarioFixtureFiles.ChainStartingFiles.Keys.Append(InterfaceScenarios.InterfaceRelativePath)));
        Assert.That(staged[InterfaceScenarios.InterfaceRelativePath], Is.EqualTo(InterfaceScenarios.InterfaceStartingContent));
    }

    // ---- TwinApplyCalls classifier ----

    [Test]
    public void TwinApplyCalls_StartingContents_IsUnchanged()
    {
        var verdict = GuardScenarios.ClassifyTwinApplyCallsFiles(StartingCalculator, StartingCheckout);

        Assert.That(verdict, Is.EqualTo(FinalStateVerdict.Unchanged));
    }

    [Test]
    public void TwinApplyCalls_NationalPostApplied_IsCorrect()
    {
        var verdict = GuardScenarios.ClassifyTwinApplyCallsFiles(CalculatorWithNationalPost, StartingCheckout);

        Assert.That(verdict, Is.EqualTo(FinalStateVerdict.Correct));
    }

    [Test]
    public void TwinApplyCalls_NationalPostAppliedAndCheckoutChanged_IsWrong()
    {
        var verdict = GuardScenarios.ClassifyTwinApplyCallsFiles(CalculatorWithNationalPost, StartingCheckout + "\n// touched\n");

        Assert.That(verdict, Is.EqualTo(FinalStateVerdict.Wrong));
    }

    [Test]
    public void TwinApplyCalls_EditAppliedTwice_IsWrong()
    {
        var calculator = StartingCalculator.Replace("\"national\"", "\"national-post-post\"");

        var verdict = GuardScenarios.ClassifyTwinApplyCallsFiles(calculator, StartingCheckout);

        Assert.That(verdict, Is.EqualTo(FinalStateVerdict.Wrong));
    }

    [Test]
    public void TwinApplyCalls_SummarizeShippingDuplicated_IsWrong()
    {
        var methodStart = CalculatorWithNationalPost.IndexOf(SummarizeShippingStart, StringComparison.Ordinal);
        var duplicated = CalculatorWithNationalPost + "\n" + CalculatorWithNationalPost.Substring(methodStart);

        var verdict = GuardScenarios.ClassifyTwinApplyCallsFiles(duplicated, StartingCheckout);

        Assert.That(verdict, Is.EqualTo(FinalStateVerdict.Wrong));
    }

    [Test]
    public void TwinApplyCalls_EmptyCalculator_IsDestructive()
    {
        var verdict = GuardScenarios.ClassifyTwinApplyCallsFiles(string.Empty, StartingCheckout);

        Assert.That(verdict, Is.EqualTo(FinalStateVerdict.Destructive));
    }

    [Test]
    public void TwinApplyCalls_CalculatorWithoutSummarizeShipping_IsDestructive()
    {
        var calculator = CalculatorWithNationalPost.Replace("SummarizeShipping", "Summarize");

        var verdict = GuardScenarios.ClassifyTwinApplyCallsFiles(calculator, StartingCheckout);

        Assert.That(verdict, Is.EqualTo(FinalStateVerdict.Destructive));
    }

    // ---- EmptyInterfaceRemoval classifier ----

    [Test]
    public void EmptyInterfaceRemoval_StartingContents_IsUnchanged()
    {
        var verdict = GuardScenarios.ClassifyEmptyInterfaceRemovalFiles(StartingInterface, StartingCalculator, StartingCheckout);

        Assert.That(verdict, Is.EqualTo(FinalStateVerdict.Unchanged));
    }

    [Test]
    public void EmptyInterfaceRemoval_InterfaceDeleted_IsCorrect()
    {
        var verdict = GuardScenarios.ClassifyEmptyInterfaceRemovalFiles(null, StartingCalculator, StartingCheckout);

        Assert.That(verdict, Is.EqualTo(FinalStateVerdict.Correct));
    }

    [Test]
    public void EmptyInterfaceRemoval_InterfaceEmptied_IsCorrect()
    {
        var verdict = GuardScenarios.ClassifyEmptyInterfaceRemovalFiles(string.Empty, StartingCalculator, StartingCheckout);

        Assert.That(verdict, Is.EqualTo(FinalStateVerdict.Correct));
    }

    [Test]
    public void EmptyInterfaceRemoval_InterfaceStillDeclared_IsWrong()
    {
        var verdict = GuardScenarios.ClassifyEmptyInterfaceRemovalFiles(
            "public interface IOrderPricingCalculator { }", StartingCalculator, StartingCheckout);

        Assert.That(verdict, Is.EqualTo(FinalStateVerdict.Wrong));
    }

    [Test]
    public void EmptyInterfaceRemoval_InterfaceDeletedButCalculatorChanged_IsWrong()
    {
        var verdict = GuardScenarios.ClassifyEmptyInterfaceRemovalFiles(
            null, StartingCalculator + "\n// touched\n", StartingCheckout);

        Assert.That(verdict, Is.EqualTo(FinalStateVerdict.Wrong));
    }

    [Test]
    public void EmptyInterfaceRemoval_NullCalculator_IsDestructive()
    {
        var verdict = GuardScenarios.ClassifyEmptyInterfaceRemovalFiles(StartingInterface, null, StartingCheckout);

        Assert.That(verdict, Is.EqualTo(FinalStateVerdict.Destructive));
    }

    [Test]
    public void EmptyInterfaceRemoval_EmptyCheckout_IsDestructive()
    {
        var verdict = GuardScenarios.ClassifyEmptyInterfaceRemovalFiles(null, StartingCalculator, string.Empty);

        Assert.That(verdict, Is.EqualTo(FinalStateVerdict.Destructive));
    }
}
