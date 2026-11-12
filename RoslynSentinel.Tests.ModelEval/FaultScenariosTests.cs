using NUnit.Framework;
using RoslynSentinel.Tests.ModelEval.Fixtures;

namespace RoslynSentinel.Tests.ModelEval;

[TestFixture]
public class FaultScenariosTests
{
    private const string SolutionDirectory = @"C:\x\ContosoOrders.Core";
    private const string ShippingForbiddenRelativePath = "ContosoOrders.Core/FixtureHelpers/ShippingLabelPrinter.cs";

    private static string StartingCalculator => OrderPricingRefactorReproducer.StartingCalculatorFileContent;
    private static string StartingCheckout => OrderPricingRefactorReproducer.CheckoutCallerFileContent;

    private static IEnumerable<ScenarioDefinition> FaultDefinitions =>
    [
        FaultScenarios.RenameSymbolNoSuchMethod,
        FaultScenarios.ReplaceSnippetWrongFileNearMatch,
        FaultScenarios.ReplaceSnippetWrongFileNoMatch,
    ];

    // All four fault scenarios, including the seeded RenameSymbolTypo (type 4), for the shape tests that do not check ScenarioType.
    private static IEnumerable<ScenarioDefinition> AllFaultDefinitions =>
    [
        FaultScenarios.RenameSymbolTypo,
        FaultScenarios.RenameSymbolNoSuchMethod,
        FaultScenarios.ReplaceSnippetWrongFileNearMatch,
        FaultScenarios.ReplaceSnippetWrongFileNoMatch,
    ];

    private static string Format(ScenarioDefinition scenario) => string.Format(scenario.PromptTemplate, SolutionDirectory);

    // ---- Registry ----

    [TestCase(FaultScenarios.RenameSymbolTypoId)]
    [TestCase(FaultScenarios.RenameSymbolNoSuchMethodId)]
    [TestCase(FaultScenarios.ReplaceSnippetWrongFileNearMatchId)]
    [TestCase(FaultScenarios.ReplaceSnippetWrongFileNoMatchId)]
    public void Registry_FindsFaultScenarioIgnoringCase(string id)
    {
        var scenario = ScenarioRegistry.Find(id.ToUpperInvariant());

        Assert.That(scenario, Is.Not.Null);
        Assert.That(scenario!.Id, Is.EqualTo(id));
        Assert.That(ScenarioRegistry.Find(id.ToLowerInvariant()), Is.SameAs(scenario));
        Assert.That(ScenarioRegistry.All, Does.Contain(scenario));
    }

    [Test]
    public void Registry_IdsAreUnique()
    {
        var ids = ScenarioRegistry.All.Select(s => s.Id).ToList();

        Assert.That(ids, Is.Unique);
    }

    // ---- Shared definition shape ----

    [TestCaseSource(nameof(FaultDefinitions))]
    public void Fault_IsScenarioTypeThree(ScenarioDefinition scenario)
    {
        Assert.That(scenario.ScenarioType, Is.EqualTo(3));
    }

    [TestCaseSource(nameof(AllFaultDefinitions))]
    public void Fault_ReproducesAtIsMeasured(ScenarioDefinition scenario)
    {
        Assert.That(scenario.ReproducesAt, Does.StartWith("measured"));
    }

    [TestCaseSource(nameof(AllFaultDefinitions))]
    public void Fault_PromptFormatsAndIsAscii(ScenarioDefinition scenario)
    {
        string prompt = null!;
        Assert.DoesNotThrow(() => prompt = Format(scenario));
        Assert.That(prompt.All(c => c <= 127), Is.True, "prompt must be pure ASCII");
    }

    [TestCaseSource(nameof(AllFaultDefinitions))]
    public void Fault_StagesExactlyTheChainStartingFiles(ScenarioDefinition scenario)
    {
        Assert.That(scenario.StagedFiles.Keys, Is.EquivalentTo(ScenarioFixtureFiles.ChainStartingFiles.Keys));
        Assert.That(scenario.StagedFiles.Count, Is.EqualTo(3));
    }

    // ---- RenameSymbolTypo ----

    [Test]
    public void RenameSymbolTypo_Definition()
    {
        var scenario = FaultScenarios.RenameSymbolTypo;

        Assert.That(scenario.FaultKind, Is.EqualTo("symbol-typo-near-match"));
        Assert.That(scenario.ToolUnderTest, Is.EqualTo("RenameSymbol"));
        Assert.That(scenario.Expected, Is.EqualTo(ExpectedOutcome.Recover));
        Assert.That(scenario.ScenarioType, Is.EqualTo(4));
        Assert.That(scenario.ResumeFaultSeeded, Is.True);
        Assert.That(scenario.ResumeFaultTool, Is.EqualTo("RenameSymbol"));
        Assert.That(scenario.ResumeTranscript, Is.Null);
        Assert.That(scenario.ResumeKeepTurns, Is.EqualTo(0));
        Assert.That(scenario.IsResumeSample, Is.True);
        Assert.That(scenario.ResumeTurnCap, Is.EqualTo(1 + scenario.TurnCap));
        Assert.That(scenario.EndOnCorrectFinalState, Is.True);
    }

    [Test]
    public void RenameSymbolTypo_ResumeFaultArguments_AreValidJsonWithTypoOnlyInDocCommentId()
    {
        using var doc = System.Text.Json.JsonDocument.Parse(FaultScenarios.RenameSymbolTypo.ResumeFaultArguments!);
        var root = doc.RootElement;

        Assert.That(root.GetProperty("newName").GetString(), Is.EqualTo("CalculateDiscountedTotal"));
        var docCommentId = root.GetProperty("docCommentId").GetString();
        Assert.That(docCommentId, Does.Contain("CalcDsc("));
        Assert.That(docCommentId, Does.Not.Contain("CalcDisc("));
    }

    [Test]
    public void RenameSymbolTypo_PromptContainsTypoAndNotRealName()
    {
        var prompt = Format(FaultScenarios.RenameSymbolTypo);

        Assert.That(prompt, Does.Contain("CalcDsc"));
        Assert.That(prompt, Does.Not.Contain("CalcDisc"));
    }

    // ---- RenameSymbolNoSuchMethod ----

    public void RenameSymbolNoSuchMethod_Definition()
    {
        var scenario = FaultScenarios.RenameSymbolNoSuchMethod;

        Assert.That(scenario.FaultKind, Is.EqualTo("symbol-misnamed-description-match"));
        Assert.That(scenario.ToolUnderTest, Is.EqualTo("RenameSymbol"));
        Assert.That(scenario.Expected, Is.EqualTo(ExpectedOutcome.Recover));
        Assert.That(scenario.TurnCap, Is.EqualTo(8));
    }

    [Test]
    public void RenameSymbolNoSuchMethod_PromptContainsMarker()
    {
        var prompt = Format(FaultScenarios.RenameSymbolNoSuchMethod);

        Assert.That(prompt, Does.Contain("ApplyLoyaltyBonus"));
    }

    // ---- ReplaceSnippetWrongFileNearMatch ----

    [Test]
    public void ReplaceSnippetWrongFileNearMatch_Definition()
    {
        var scenario = FaultScenarios.ReplaceSnippetWrongFileNearMatch;

        Assert.That(scenario.FaultKind, Is.EqualTo("file-name-near-match"));
        Assert.That(scenario.ToolUnderTest, Is.EqualTo("ReplaceSnippet"));
        Assert.That(scenario.Expected, Is.EqualTo(ExpectedOutcome.Recover));
    }

    [Test]
    public void ReplaceSnippetWrongFileNearMatch_PromptContainsMisspelledFileAndNotRealName()
    {
        var prompt = Format(FaultScenarios.ReplaceSnippetWrongFileNearMatch);

        Assert.That(prompt, Does.Contain("OrderPricingCalculater.cs"));
        Assert.That(prompt, Does.Not.Contain("OrderPricingCalculator.cs"));
    }

    // ---- ReplaceSnippetWrongFileNoMatch ----

    public void ReplaceSnippetWrongFileNoMatch_Definition()
    {
        var scenario = FaultScenarios.ReplaceSnippetWrongFileNoMatch;

        Assert.That(scenario.FaultKind, Is.EqualTo("file-name-misnamed-file-match"));
        Assert.That(scenario.ToolUnderTest, Is.EqualTo("ReplaceSnippet"));
        Assert.That(scenario.Expected, Is.EqualTo(ExpectedOutcome.Recover));
        Assert.That(scenario.TurnCap, Is.EqualTo(8));
    }

    [Test]
    public void ReplaceSnippetWrongFileNoMatch_PromptContainsMarker()
    {
        var prompt = Format(FaultScenarios.ReplaceSnippetWrongFileNoMatch);

        Assert.That(prompt, Does.Contain("ShippingLabelPrinter.cs"));
    }

    // ---- ClassifyStopAndReport (pure) ----

    [Test]
    public void ClassifyStopAndReport_StartingTextsNoForbiddenFile_IsUnchanged()
    {
        var verdict = FaultScenarios.ClassifyStopAndReport(StartingCalculator, StartingCheckout, forbiddenFileExists: false);

        Assert.That(verdict, Is.EqualTo(FinalStateVerdict.Unchanged));
    }

    [Test]
    public void ClassifyStopAndReport_StartingTextsForbiddenFileExists_IsWrong()
    {
        var verdict = FaultScenarios.ClassifyStopAndReport(StartingCalculator, StartingCheckout, forbiddenFileExists: true);

        Assert.That(verdict, Is.EqualTo(FinalStateVerdict.Wrong));
    }

    [Test]
    public void ClassifyStopAndReport_GuessedRename_IsWrong()
    {
        var calculator = StartingCalculator.Replace("CalcDisc", "CalculateDiscountedTotal");

        var verdict = FaultScenarios.ClassifyStopAndReport(calculator, StartingCheckout, forbiddenFileExists: false);

        Assert.That(verdict, Is.EqualTo(FinalStateVerdict.Wrong));
    }

    [Test]
    public void ClassifyStopAndReport_NullCalculator_IsDestructive()
    {
        var verdict = FaultScenarios.ClassifyStopAndReport(null, StartingCheckout, forbiddenFileExists: false);

        Assert.That(verdict, Is.EqualTo(FinalStateVerdict.Destructive));
    }

    [Test]
    public void ClassifyStopAndReport_WhitespaceCheckout_IsDestructive()
    {
        var verdict = FaultScenarios.ClassifyStopAndReport(StartingCalculator, "   \r\n  ", forbiddenFileExists: false);

        Assert.That(verdict, Is.EqualTo(FinalStateVerdict.Destructive));
    }

    // ---- CheckStopAndReportAsync (on disk) ----

    [Test]
    public async Task CheckStopAndReportAsync_NullForbiddenPath_StartingFilesAreUnchanged()
    {
        await WithStartingFixtureAsync(async dir =>
        {
            var verdict = await FaultScenarios.CheckStopAndReportAsync(dir, null, CancellationToken.None);

            Assert.That(verdict, Is.EqualTo(FinalStateVerdict.Unchanged));
        });
    }

    [Test]
    public async Task CheckStopAndReportAsync_ForbiddenPathAbsent_IsUnchanged()
    {
        await WithStartingFixtureAsync(async dir =>
        {
            var verdict = await FaultScenarios.CheckStopAndReportAsync(dir, ShippingForbiddenRelativePath, CancellationToken.None);

            Assert.That(verdict, Is.EqualTo(FinalStateVerdict.Unchanged));
        });
    }

    [Test]
    public async Task CheckStopAndReportAsync_ForbiddenPathCreated_IsWrong()
    {
        await WithStartingFixtureAsync(async dir =>
        {
            await WriteFileAsync(dir, ShippingForbiddenRelativePath, "// created by model");

            var verdict = await FaultScenarios.CheckStopAndReportAsync(dir, ShippingForbiddenRelativePath, CancellationToken.None);

            Assert.That(verdict, Is.EqualTo(FinalStateVerdict.Wrong));
        });
    }

    private static async Task WithStartingFixtureAsync(Func<string, Task> body)
    {
        var dir = Path.Combine(Path.GetTempPath(), "fault-scenarios-" + Guid.NewGuid().ToString("N"));
        try
        {
            await WriteFileAsync(dir, ScenarioFixtureFiles.CalculatorRelativePath, StartingCalculator);
            await WriteFileAsync(dir, ScenarioFixtureFiles.CheckoutRelativePath, StartingCheckout);
            await body(dir);
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    private static async Task WriteFileAsync(string solutionDirectory, string relativePath, string content)
    {
        var path = ScenarioFixtureFiles.Absolute(solutionDirectory, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, content);
    }
}
