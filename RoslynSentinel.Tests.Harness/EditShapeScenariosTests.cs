using NUnit.Framework;
using RoslynSentinel.Tests.ModelEval.Fixtures;

namespace RoslynSentinel.Tests.ModelEval;

[TestFixture]
public class EditShapeScenariosTests
{
    private const string Method = "CalcDisc";
    private const string MethodRenamed = "CalculateDiscountedTotal";
    private const string DocComment = "/// <summary>Returns the price after discount.</summary>";

    private static string StartingCalculator => OrderPricingRefactorReproducer.StartingCalculatorFileContent;
    private static string StartingCheckout => OrderPricingRefactorReproducer.CheckoutCallerFileContent;

    private static IEnumerable<ScenarioDefinition> EditShapeScenarios =>
    [
        TwoRenameScenarios.TwoRenamesWholeFile,
        InterfaceScenarios.InterfaceMemberRedeclared,
        BatchShapeScenarios.MixedSingleAndBatchShape,
    ];

    // ---- Registry ----

    [TestCase(TwoRenameScenarios.TwoRenamesWholeFileId)]
    [TestCase(InterfaceScenarios.InterfaceMemberRedeclaredId)]
    public void Registry_FindsEditShapeScenarioIgnoringCase(string id)
    {
        var scenario = ScenarioRegistry.Find(id.ToUpperInvariant());

        Assert.That(scenario, Is.Not.Null);
        Assert.That(ScenarioRegistry.Find(id.ToLowerInvariant()), Is.SameAs(scenario));
        Assert.That(ScenarioRegistry.All, Does.Contain(scenario));
    }

    [Test]
    public void Registry_IdsAreUnique()
    {
        var ids = ScenarioRegistry.All.Select(s => s.Id).ToList();

        Assert.That(ids.Select(id => id.ToUpperInvariant()).ToList(), Is.Unique);
        Assert.That(ids, Does.Contain(TwoRenameScenarios.TwoRenamesWholeFileId));
        Assert.That(ids, Does.Contain(InterfaceScenarios.InterfaceMemberRedeclaredId));
        Assert.That(ids, Does.Not.Contain(BatchShapeScenarios.MixedSingleAndBatchShapeId));
        Assert.That(ScenarioRegistry.Find(BatchShapeScenarios.MixedSingleAndBatchShapeId), Is.Null);
        Assert.That(ids, Does.Contain(RenameScenarios.CalcDiscRenameId));
    }

    // ---- Definition shape ----

    [TestCaseSource(nameof(EditShapeScenarios))]
    public void Scenario_ExpectsRecovery(ScenarioDefinition scenario)
    {
        Assert.That(scenario.Expected, Is.EqualTo(ExpectedOutcome.Recover));
    }

    [TestCase(TwoRenameScenarios.TwoRenamesWholeFileId, "RenameSymbol")]
    [TestCase(InterfaceScenarios.InterfaceMemberRedeclaredId, "ReplaceSnippet")]
    public void Scenario_ExpectedRecoveryTool(string id, string expectedTool)
    {
        Assert.That(ScenarioRegistry.Find(id)!.ExpectedRecoveryTool, Is.EqualTo(expectedTool));
    }

    [Test]
    public void Batch_ExpectedRecoveryTool_IsReplaceSnippet()
    {
        Assert.That(BatchShapeScenarios.MixedSingleAndBatchShape.ExpectedRecoveryTool, Is.EqualTo("ReplaceSnippet"));
    }

    [TestCaseSource(nameof(EditShapeScenarios))]
    public void Scenario_PromptFormatsAndIsAscii(ScenarioDefinition scenario)
    {
        string prompt = null!;
        Assert.DoesNotThrow(() => prompt = string.Format(scenario.PromptTemplate, @"C:\x\ContosoOrders.Core"));
        Assert.That(prompt.All(c => c <= 127), Is.True, "prompt must be pure ASCII");
    }

    [Test]
    public void TwoRenames_PromptContainsMarkers()
    {
        var prompt = string.Format(TwoRenameScenarios.TwoRenamesWholeFile.PromptTemplate, @"C:\x\ContosoOrders.Core");

        Assert.That(prompt, Does.Contain(Method));
        Assert.That(prompt, Does.Contain(MethodRenamed));
        Assert.That(prompt, Does.Contain("StandardOrderPricingCalculator"));
    }

    [Test]
    public void Interface_PromptContainsMarkers()
    {
        var prompt = string.Format(InterfaceScenarios.InterfaceMemberRedeclared.PromptTemplate, @"C:\x\ContosoOrders.Core");

        Assert.That(prompt, Does.Contain("DescribeOrder"));
        Assert.That(prompt, Does.Contain("IOrderPricingCalculator"));
    }

    [Test]
    public void Batch_PromptContainsMarkers()
    {
        var prompt = string.Format(BatchShapeScenarios.MixedSingleAndBatchShape.PromptTemplate, @"C:\x\ContosoOrders.Core");

        Assert.That(prompt, Does.Contain("local-pickup"));
        Assert.That(prompt, Does.Contain("regional-courier"));
        Assert.That(prompt, Does.Contain("ReplaceSnippet"));
    }

    // ---- Staged files ----

    [Test]
    public void TwoRenames_StagesChainStartingFiles()
    {
        Assert.That(
            TwoRenameScenarios.TwoRenamesWholeFile.StagedFiles.Keys,
            Is.EquivalentTo(ScenarioFixtureFiles.ChainStartingFiles.Keys));
    }

    [Test]
    public void Interface_StagesChainStartingFilesPlusInterface()
    {
        var staged = InterfaceScenarios.InterfaceMemberRedeclared.StagedFiles;

        Assert.That(staged.Keys, Is.EquivalentTo(ScenarioFixtureFiles.ChainStartingFiles.Keys.Append(InterfaceScenarios.InterfaceRelativePath)));
        Assert.That(staged[InterfaceScenarios.InterfaceRelativePath], Is.EqualTo(InterfaceScenarios.InterfaceStartingContent));
    }

    [Test]
    public void Batch_StagesChainStartingFiles()
    {
        Assert.That(
            BatchShapeScenarios.MixedSingleAndBatchShape.StagedFiles.Keys,
            Is.EquivalentTo(ScenarioFixtureFiles.ChainStartingFiles.Keys));
    }

    // ---- TwoRenames classifier ----

    [Test]
    public void TwoRenames_StartingContents_IsUnchanged()
    {
        var verdict = TwoRenameScenarios.ClassifyFiles(StartingCalculator, StartingCheckout);

        Assert.That(verdict, Is.EqualTo(FinalStateVerdict.Unchanged));
    }

    [Test]
    public void TwoRenames_BothRenamesApplied_IsCorrect()
    {
        var calculator = StartingCalculator
            .Replace("class OrderPricingCalculator", "class StandardOrderPricingCalculator")
            .Replace(Method, MethodRenamed);
        var checkout = StartingCheckout
            .Replace(Method, MethodRenamed)
            .Replace("OrderPricingCalculator", "StandardOrderPricingCalculator");

        var verdict = TwoRenameScenarios.ClassifyFiles(calculator, checkout);

        Assert.That(verdict, Is.EqualTo(FinalStateVerdict.Correct));
    }

    [Test]
    public void TwoRenames_OnlyMethodRenamed_IsWrong()
    {
        var calculator = StartingCalculator.Replace(Method, MethodRenamed);
        var checkout = StartingCheckout.Replace(Method, MethodRenamed);

        var verdict = TwoRenameScenarios.ClassifyFiles(calculator, checkout);

        Assert.That(verdict, Is.EqualTo(FinalStateVerdict.Wrong));
    }

    [Test]
    public void TwoRenames_EmptyCalculator_IsDestructive()
    {
        var verdict = TwoRenameScenarios.ClassifyFiles(string.Empty, StartingCheckout);

        Assert.That(verdict, Is.EqualTo(FinalStateVerdict.Destructive));
    }

    [Test]
    public void TwoRenames_NullCheckout_IsDestructive()
    {
        var verdict = TwoRenameScenarios.ClassifyFiles(StartingCalculator, null);

        Assert.That(verdict, Is.EqualTo(FinalStateVerdict.Destructive));
    }

    // ---- Interface classifier ----

    private const string DescribeOrderLine = "    string DescribeOrder(int id, string label);";
    private const string SummarizeShippingLine = "    string SummarizeShipping(int zone);";

    private static string InterfaceWith(params string[] addedLines) =>
        InterfaceScenarios.InterfaceStartingContent.Replace(
            "bool isPreferredCustomer);",
            "bool isPreferredCustomer);" + string.Concat(addedLines.Select(line => "\n" + line)));

    [Test]
    public void Interface_StartingContents_IsUnchanged()
    {
        var verdict = InterfaceScenarios.ClassifyFiles(
            InterfaceScenarios.InterfaceStartingContent, StartingCalculator, StartingCheckout);

        Assert.That(verdict, Is.EqualTo(FinalStateVerdict.Unchanged));
    }

    [Test]
    public void Interface_MemberAddedOnce_IsCorrect()
    {
        var verdict = InterfaceScenarios.ClassifyFiles(InterfaceWith(DescribeOrderLine), StartingCalculator, StartingCheckout);

        Assert.That(verdict, Is.EqualTo(FinalStateVerdict.Correct));
    }

    [Test]
    public void Interface_MemberAddedTwice_IsWrong()
    {
        var verdict = InterfaceScenarios.ClassifyFiles(
            InterfaceWith(DescribeOrderLine, DescribeOrderLine), StartingCalculator, StartingCheckout);

        Assert.That(verdict, Is.EqualTo(FinalStateVerdict.Wrong));
    }

    [Test]
    public void Interface_ExtraSummarizeShippingMember_IsWrong()
    {
        var verdict = InterfaceScenarios.ClassifyFiles(
            InterfaceWith(DescribeOrderLine, SummarizeShippingLine), StartingCalculator, StartingCheckout);

        Assert.That(verdict, Is.EqualTo(FinalStateVerdict.Wrong));
    }

    [Test]
    public void Interface_EmptyInterface_IsDestructive()
    {
        var verdict = InterfaceScenarios.ClassifyFiles(string.Empty, StartingCalculator, StartingCheckout);

        Assert.That(verdict, Is.EqualTo(FinalStateVerdict.Destructive));
    }

    [Test]
    public void Interface_CorrectInterfaceButCalculatorChanged_IsWrong()
    {
        var verdict = InterfaceScenarios.ClassifyFiles(
            InterfaceWith(DescribeOrderLine), StartingCalculator + "\n// touched\n", StartingCheckout);

        Assert.That(verdict, Is.EqualTo(FinalStateVerdict.Wrong));
    }

    // ---- Batch classifier ----

    private static string CalculatorWithLiteralEdits =>
        StartingCalculator
            .Replace("\"local\"", "\"local-pickup\"")
            .Replace("\"regional\"", "\"regional-courier\"");

    private static string CheckoutWithDocComment =>
        StartingCheckout.Replace("public decimal GetFinalPrice", DocComment + "\n    public decimal GetFinalPrice");

    [Test]
    public void Batch_StartingContents_IsUnchanged()
    {
        var verdict = BatchShapeScenarios.ClassifyFiles(StartingCalculator, StartingCheckout);

        Assert.That(verdict, Is.EqualTo(FinalStateVerdict.Unchanged));
    }

    [Test]
    public void Batch_AllThreeEdits_IsCorrect()
    {
        var verdict = BatchShapeScenarios.ClassifyFiles(CalculatorWithLiteralEdits, CheckoutWithDocComment);

        Assert.That(verdict, Is.EqualTo(FinalStateVerdict.Correct));
    }

    [Test]
    public void Batch_OnlyCalculatorEdits_IsWrong()
    {
        var verdict = BatchShapeScenarios.ClassifyFiles(CalculatorWithLiteralEdits, StartingCheckout);

        Assert.That(verdict, Is.EqualTo(FinalStateVerdict.Wrong));
    }

    [Test]
    public void Batch_LocalStillPresent_IsWrong()
    {
        var calculator = StartingCalculator.Replace("\"regional\"", "\"regional-courier\"");

        var verdict = BatchShapeScenarios.ClassifyFiles(calculator, CheckoutWithDocComment);

        Assert.That(verdict, Is.EqualTo(FinalStateVerdict.Wrong));
    }

    [Test]
    public void Batch_CalculatorMissingDescribeOrder_IsDestructive()
    {
        var calculator = CalculatorWithLiteralEdits.Replace("DescribeOrder", "Describe");

        var verdict = BatchShapeScenarios.ClassifyFiles(calculator, CheckoutWithDocComment);

        Assert.That(verdict, Is.EqualTo(FinalStateVerdict.Destructive));
    }
}
