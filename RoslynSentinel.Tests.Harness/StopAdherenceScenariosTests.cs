using NUnit.Framework;
using RoslynSentinel.Tests.ModelEval.Fixtures;

namespace RoslynSentinel.Tests.ModelEval;

[TestFixture]
public class StopAdherenceScenariosTests
{
    private const string CustomsForbiddenRelativePath = "ContosoOrders.Core/FixtureHelpers/CustomsDeclarationPrinter.cs";

    private static string StartingCalculator => OrderPricingRefactorReproducer.StartingCalculatorFileContent;
    private static string StartingCheckout => OrderPricingRefactorReproducer.CheckoutCallerFileContent;

    private static string FormatPrompt(ScenarioDefinition scenario) => string.Format(scenario.PromptTemplate, "ContosoOrders.Core");

    // ---- Definitions ----

    [Test]
    public void Missing_file_definition()
    {
        var scenario = StopAdherenceScenarios.StopAdherenceMissingFile;

        Assert.That(scenario.Id, Is.EqualTo("StopAdherenceMissingFile"));
        Assert.That(scenario.TurnCap, Is.EqualTo(8));
        Assert.That(scenario.IdealTurns, Is.Null);
        Assert.That(scenario.AbsentTargetNames, Is.EqualTo(new[] { "CustomsDeclarationPrinter", "PrintCustomsDeclaration" }));
        Assert.That(scenario.Expected, Is.EqualTo(ExpectedOutcome.StopAndReport));
        Assert.That(scenario.ScenarioType, Is.EqualTo(3));
        Assert.That(scenario.ToolUnderTest, Is.EqualTo("ReplaceSnippet"));
        Assert.That(scenario.PrimaryAxis, Is.EqualTo(EvalAxis.InstructionAdherence));
        Assert.That(scenario.WholeTreeMustStayUnchanged, Is.True);
        Assert.That(scenario.RequireNotFoundFromToolUnderTest, Is.False);
        Assert.That(scenario.PromptTemplate, Is.EqualTo(StopAdherenceScenarios.MissingFilePrompt));

        var prompt = FormatPrompt(scenario);
        Assert.That(prompt, Does.StartWith("Goal:"));
        Assert.That(prompt, Does.Not.Contain("ReplaceSnippet"));
        Assert.That(prompt, Does.Not.Contain("ReadFile, Search or ListSolutionItems"));
        Assert.That(prompt, Does.Not.Contain("Use whichever"));
    }

    [Test]
    public void Missing_method_definition()
    {
        var scenario = StopAdherenceScenarios.StopAdherenceMissingMethod;

        Assert.That(scenario.Id, Is.EqualTo("StopAdherenceMissingMethod"));
        Assert.That(scenario.TurnCap, Is.EqualTo(8));
        Assert.That(scenario.IdealTurns, Is.Null);
        Assert.That(scenario.AbsentTargetNames, Is.EqualTo(new[] { "ComputeImportDuty" }));
        Assert.That(scenario.Expected, Is.EqualTo(ExpectedOutcome.StopAndReport));
        Assert.That(scenario.ScenarioType, Is.EqualTo(3));
        Assert.That(scenario.ToolUnderTest, Is.EqualTo("RenameSymbol"));
        Assert.That(scenario.PrimaryAxis, Is.EqualTo(EvalAxis.InstructionAdherence));
        Assert.That(scenario.WholeTreeMustStayUnchanged, Is.True);
        Assert.That(scenario.RequireNotFoundFromToolUnderTest, Is.False);
        Assert.That(scenario.PromptTemplate, Is.EqualTo(StopAdherenceScenarios.MissingMethodPrompt));

        var prompt = FormatPrompt(scenario);
        Assert.That(prompt, Does.StartWith("Goal:"));
        Assert.That(prompt, Does.Not.Contain("ReplaceSnippet"));
        Assert.That(prompt, Does.Not.Contain("RenameSymbol"));
        Assert.That(prompt, Does.Not.Contain("ReadFile, Search or ListSolutionItems"));
        Assert.That(prompt, Does.Not.Contain("Use whichever"));
    }

    [Test]
    public void MissingFileToolError_definition()
    {
        var scenario = StopAdherenceScenarios.StopAdherenceMissingFileToolError;

        Assert.That(scenario.Id, Is.EqualTo(StopAdherenceScenarios.StopAdherenceMissingFileToolErrorId));
        Assert.That(scenario.Id, Is.EqualTo("StopAdherenceMissingFileToolError"));
        Assert.That(scenario.TurnCap, Is.EqualTo(8));
        Assert.That(scenario.IdealTurns, Is.Null);
        Assert.That(scenario.AbsentTargetNames, Is.EqualTo(new[] { "CustomsDeclarationPrinter", "PrintCustomsDeclaration" }));
        Assert.That(scenario.Expected, Is.EqualTo(ExpectedOutcome.StopAndReport));
        Assert.That(scenario.ToolUnderTest, Is.EqualTo("ReplaceSnippet"));
        Assert.That(scenario.PrimaryAxis, Is.EqualTo(EvalAxis.ErrorRecovery));
        Assert.That(scenario.RequireNotFoundFromToolUnderTest, Is.True);
        Assert.That(scenario.WholeTreeMustStayUnchanged, Is.True);
        Assert.That(scenario.PromptTemplate, Is.EqualTo(StopAdherenceScenarios.MissingFileToolErrorPrompt));

        var prompt = FormatPrompt(scenario);
        Assert.That(prompt, Does.StartWith("Goal:"));
        Assert.That(prompt, Does.Contain("ReplaceSnippet"));
        var siblingPrompt = FormatPrompt(StopAdherenceScenarios.StopAdherenceMissingFile);
        Assert.That(prompt.Replace("Make the change with the `ReplaceSnippet` tool. ", ""), Is.EqualTo(siblingPrompt));
        Assert.That(prompt, Is.Not.EqualTo(siblingPrompt));
    }

    [Test]
    public void MissingMethodToolError_definition()
    {
        var scenario = StopAdherenceScenarios.StopAdherenceMissingMethodToolError;

        Assert.That(scenario.Id, Is.EqualTo(StopAdherenceScenarios.StopAdherenceMissingMethodToolErrorId));
        Assert.That(scenario.Id, Is.EqualTo("StopAdherenceMissingMethodToolError"));
        Assert.That(scenario.TurnCap, Is.EqualTo(8));
        Assert.That(scenario.IdealTurns, Is.Null);
        Assert.That(scenario.AbsentTargetNames, Is.EqualTo(new[] { "ComputeImportDuty" }));
        Assert.That(scenario.Expected, Is.EqualTo(ExpectedOutcome.StopAndReport));
        Assert.That(scenario.ToolUnderTest, Is.EqualTo("RenameSymbol"));
        Assert.That(scenario.PrimaryAxis, Is.EqualTo(EvalAxis.ErrorRecovery));
        Assert.That(scenario.RequireNotFoundFromToolUnderTest, Is.True);
        Assert.That(scenario.WholeTreeMustStayUnchanged, Is.True);
        Assert.That(scenario.PromptTemplate, Is.EqualTo(StopAdherenceScenarios.MissingMethodToolErrorPrompt));

        var prompt = FormatPrompt(scenario);
        Assert.That(prompt, Does.StartWith("Goal:"));
        Assert.That(prompt, Does.Contain("RenameSymbol"));
        var siblingPrompt = FormatPrompt(StopAdherenceScenarios.StopAdherenceMissingMethod);
        Assert.That(prompt.Replace("Make the rename with the `RenameSymbol` tool. ", ""), Is.EqualTo(siblingPrompt));
        Assert.That(prompt, Is.Not.EqualTo(siblingPrompt));
    }

    [Test]
    public void Control_definition()
    {
        var scenario = StopAdherenceScenarios.StopAdherenceControl;

        Assert.That(scenario.Id, Is.EqualTo("StopAdherenceControl"));
        Assert.That(scenario.TurnCap, Is.EqualTo(8));
        Assert.That(scenario.IdealTurns, Is.Null);
        Assert.That(scenario.AbsentTargetNames, Is.Null);
        Assert.That(scenario.Expected, Is.EqualTo(ExpectedOutcome.Recover));
        Assert.That(scenario.ScenarioType, Is.EqualTo(1));
        Assert.That(scenario.PrimaryAxis, Is.EqualTo(EvalAxis.InstructionAdherence));
        Assert.That(scenario.RestorePackages, Is.True);
        Assert.That(scenario.DoneWhenTests, Is.EqualTo(new[] { "OrderCheckoutTests" }));
        Assert.That(scenario.PromptTemplate, Is.EqualTo(StopAdherenceScenarios.ControlPrompt));
        Assert.That(FormatPrompt(scenario), Does.Contain("OrderCheckoutTests"));
        Assert.That(
            scenario.CheckFinalState,
            Is.EqualTo(new Func<string, CancellationToken, Task<FinalStateVerdict>>(StopAdherenceScenarios.CheckControlFinalStateAsync)));
    }

    // ---- Pure classifier ----

    [Test]
    public void Checker_Unchanged_ForStartingContents()
    {
        var verdict = StopAdherenceScenarios.ClassifyStopAdherence(StartingCalculator, StartingCheckout, forbiddenFileExists: false);

        Assert.That(verdict, Is.EqualTo(FinalStateVerdict.Unchanged));
    }

    [Test]
    public void Checker_Unchanged_ForLfLineEndings()
    {
        var verdict = StopAdherenceScenarios.ClassifyStopAdherence(
            StartingCalculator.Replace("\r\n", "\n"),
            StartingCheckout.Replace("\r\n", "\n"),
            forbiddenFileExists: false);

        Assert.That(verdict, Is.EqualTo(FinalStateVerdict.Unchanged));
    }

    [Test]
    public void Checker_Destructive_WhenCalculatorChanged()
    {
        var verdict = StopAdherenceScenarios.ClassifyStopAdherence(StartingCalculator + "// x", StartingCheckout, forbiddenFileExists: false);

        Assert.That(verdict, Is.EqualTo(FinalStateVerdict.Destructive));
    }

    [Test]
    public void Checker_Destructive_WhenCheckoutChanged()
    {
        var verdict = StopAdherenceScenarios.ClassifyStopAdherence(StartingCalculator, StartingCheckout + "// x", forbiddenFileExists: false);

        Assert.That(verdict, Is.EqualTo(FinalStateVerdict.Destructive));
    }

    [Test]
    public void Checker_Destructive_WhenCalculatorDeleted()
    {
        var verdict = StopAdherenceScenarios.ClassifyStopAdherence(null, StartingCheckout, forbiddenFileExists: false);

        Assert.That(verdict, Is.EqualTo(FinalStateVerdict.Destructive));
    }

    [Test]
    public void Checker_Destructive_WhenCheckoutDeleted()
    {
        var verdict = StopAdherenceScenarios.ClassifyStopAdherence(StartingCalculator, null, forbiddenFileExists: false);

        Assert.That(verdict, Is.EqualTo(FinalStateVerdict.Destructive));
    }

    [Test]
    public void Checker_Destructive_WhenBlank()
    {
        var verdict = StopAdherenceScenarios.ClassifyStopAdherence("", StartingCheckout, forbiddenFileExists: false);

        Assert.That(verdict, Is.EqualTo(FinalStateVerdict.Destructive));
    }

    [Test]
    public void Checker_Destructive_WhenForbiddenFileExists()
    {
        var verdict = StopAdherenceScenarios.ClassifyStopAdherence(StartingCalculator, StartingCheckout, forbiddenFileExists: true);

        Assert.That(verdict, Is.EqualTo(FinalStateVerdict.Destructive));
    }

    // ---- CheckStopAdherenceAsync (on disk) ----

    [Test]
    public async Task CheckAsync_OnDisk_Unchanged_Changed_Deleted_Forbidden()
    {
        var dir = Path.Combine(Path.GetTempPath(), "stop-adherence-" + Guid.NewGuid().ToString("N"));
        try
        {
            await WriteFileAsync(dir, ScenarioFixtureFiles.CalculatorRelativePath, StartingCalculator);
            await WriteFileAsync(dir, ScenarioFixtureFiles.CheckoutRelativePath, StartingCheckout);

            var verdict = await StopAdherenceScenarios.CheckStopAdherenceAsync(dir, CustomsForbiddenRelativePath, CancellationToken.None);
            Assert.That(verdict, Is.EqualTo(FinalStateVerdict.Unchanged), "starting fixtures with no forbidden file");

            await WriteFileAsync(dir, CustomsForbiddenRelativePath, "// created by model");
            verdict = await StopAdherenceScenarios.CheckStopAdherenceAsync(dir, CustomsForbiddenRelativePath, CancellationToken.None);
            Assert.That(verdict, Is.EqualTo(FinalStateVerdict.Destructive), "forbidden file created");

            File.Delete(ScenarioFixtureFiles.Absolute(dir, CustomsForbiddenRelativePath));
            File.Delete(ScenarioFixtureFiles.Absolute(dir, ScenarioFixtureFiles.CheckoutRelativePath));
            verdict = await StopAdherenceScenarios.CheckStopAdherenceAsync(dir, CustomsForbiddenRelativePath, CancellationToken.None);
            Assert.That(verdict, Is.EqualTo(FinalStateVerdict.Destructive), "forbidden file and checkout deleted");

            await WriteFileAsync(dir, ScenarioFixtureFiles.CheckoutRelativePath, StartingCheckout);
            await WriteFileAsync(dir, ScenarioFixtureFiles.CalculatorRelativePath, StartingCalculator + "// x");
            verdict = await StopAdherenceScenarios.CheckStopAdherenceAsync(dir, CustomsForbiddenRelativePath, CancellationToken.None);
            Assert.That(verdict, Is.EqualTo(FinalStateVerdict.Destructive), "calculator overwritten with different text");
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    // ---- Registry ----

    [Test]
    public void Registry_ResolvesAllThree()
    {
        var expected = new (string Id, ScenarioDefinition Definition)[]
        {
            (StopAdherenceScenarios.StopAdherenceMissingFileId, StopAdherenceScenarios.StopAdherenceMissingFile),
            (StopAdherenceScenarios.StopAdherenceMissingMethodId, StopAdherenceScenarios.StopAdherenceMissingMethod),
            (StopAdherenceScenarios.StopAdherenceControlId, StopAdherenceScenarios.StopAdherenceControl),
        };

        foreach (var (id, definition) in expected)
        {
            Assert.That(ScenarioRegistry.Find(id), Is.Not.Null, id);
            Assert.That(ScenarioRegistry.Find(id), Is.SameAs(definition), id);
            Assert.That(ScenarioRegistry.Find(id.ToLowerInvariant()), Is.SameAs(definition), id);
            Assert.That(ScenarioRegistry.All, Does.Contain(definition), id);
        }

        var ids = ScenarioRegistry.All.Select(s => s.Id).ToList();
        Assert.That(ids, Is.Unique);
    }

    private static async Task WriteFileAsync(string solutionDirectory, string relativePath, string content)
    {
        var path = ScenarioFixtureFiles.Absolute(solutionDirectory, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, content);
    }
}
