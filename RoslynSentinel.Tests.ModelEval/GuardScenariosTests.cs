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
    private static string StartingInterface => GuardScenarios.EmptyInterfaceRemovalStartingContent;

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
        Assert.That(staged[InterfaceScenarios.InterfaceRelativePath], Is.EqualTo(GuardScenarios.EmptyInterfaceRemovalStartingContent));
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

    private static ScoreResult ScoreWith(int maxIdenticalCallRepeat, FinalStateVerdict? verdict) => new(
    FirstCallSucceeded: true,
    FirstErrorCode: null,
    FirstErrorClass: null,
    Recovered: false,
    CallsToRecover: null,
    RecoveryUsedExpectedTool: null,
    MarkerPresent: false,
    TurnCount: 8,
    StopReason: null,
    FailureClass: FailureClass.None,
    Passed: true,
    CallsAfterFirstError: 0,
    TurnsAfterFirstError: 0,
    OutputCharsAfterFirstError: 0,
    MaxIdenticalCallRepeat: maxIdenticalCallRepeat,
    IdenticalReasoningBlocks: 0,
    RetryPhraseCount: 0,
    Verdict: verdict);

    [Test]
    public void TwinApplyCalls_IsAuthoredOnlySeededScenarioWithTwoIdenticalCalls()
    {
        var scenario = GuardScenarios.TwinApplyCalls;

        Assert.Multiple(() =>
        {
            Assert.That(scenario.ResumeFaultSeeded, Is.True);
            Assert.That(scenario.ResumeTranscript, Is.Null);
            Assert.That(scenario.ScenarioType, Is.EqualTo(2));
            Assert.That(scenario.IsResumeSample, Is.True);
            Assert.That(scenario.ResumeFaultTool, Is.EqualTo("ReplaceSnippet"));
            Assert.That(scenario.ResumeFaultExtraCalls!.Count, Is.EqualTo(1));
            Assert.That(scenario.ResumeFaultExtraCalls![0].Tool, Is.EqualTo("ReplaceSnippet"));
            Assert.That(scenario.ResumeFaultExtraCalls![0].Arguments, Is.EqualTo(scenario.ResumeFaultArguments));
            Assert.That(scenario.ResumeFaultArguments, Does.Contain("{SOLUTION}"));
            Assert.That(scenario.ResumeTurnCap, Is.EqualTo(7));
        });

        using var document = System.Text.Json.JsonDocument.Parse(scenario.ResumeFaultArguments!);
        Assert.That(document.RootElement.GetProperty("action").GetString(), Is.EqualTo("apply"));
    }

    [TestCase(FinalStateVerdict.Correct)]
    [TestCase(FinalStateVerdict.Wrong)]
    public void ClassifyTwinApplyReaction_RepeatAtThreshold_IsResentAgain(FinalStateVerdict verdict)
    {
        Assert.That(GuardScenarios.ClassifyTwinApplyReaction(3, verdict), Is.EqualTo(GuardScenarios.TwinApplyReaction.ResentAgain));
    }

    [Test]
    public void ClassifyTwinApplyReaction_SeedRepeatOnlyAndWrongState_IsReverted()
    {
        Assert.That(GuardScenarios.ClassifyTwinApplyReaction(2, FinalStateVerdict.Wrong), Is.EqualTo(GuardScenarios.TwinApplyReaction.Reverted));
    }

    [TestCase(FinalStateVerdict.Correct)]
    [TestCase(FinalStateVerdict.Unchanged)]
    [TestCase(null)]
    public void ClassifyTwinApplyReaction_SeedRepeatOnlyAndCorrectState_IsMovedOn(FinalStateVerdict? verdict)
    {
        Assert.That(GuardScenarios.ClassifyTwinApplyReaction(2, verdict), Is.EqualTo(GuardScenarios.TwinApplyReaction.MovedOn));
    }

    [Test]
    public void ClassifyTwinApplyReaction_ScoreOverload_ReadsRepeatCountAndVerdict()
    {
        Assert.That(GuardScenarios.ClassifyTwinApplyReaction(ScoreWith(3, FinalStateVerdict.Correct)), Is.EqualTo(GuardScenarios.TwinApplyReaction.ResentAgain));
        Assert.That(GuardScenarios.ClassifyTwinApplyReaction(ScoreWith(2, FinalStateVerdict.Wrong)), Is.EqualTo(GuardScenarios.TwinApplyReaction.Reverted));
    }

    [Test]
    public void ClassifyTwinApplyReaction_InconclusiveSample_IsNull()
    {
        var sample = new ScenarioSample(
            ScenarioId: GuardScenarios.TwinApplyCallsId,
            ScenarioType: 2,
            FaultKind: "",
            SampleIndex: 1,
            Commit: "",
            Dirty: null,
            LiftRenames: "unset",
            Score: null,
            SchemaDrift: true,
            ResultDrift: 0,
            InconclusiveReason: "schema changed, tool ReplaceSnippet: parameter \"oldContent\" removed",
            TranscriptPath: "");

        Assert.That(sample.IsInconclusive, Is.True);
        Assert.That(GuardScenarios.ClassifyTwinApplyReaction(sample), Is.Null);
    }

    [Test]
    public void ClassifyTwinApplyReaction_ScoredSample_ClassifiesItsScore()
    {
        var sample = new ScenarioSample(
            ScenarioId: GuardScenarios.TwinApplyCallsId,
            ScenarioType: 2,
            FaultKind: "",
            SampleIndex: 1,
            Commit: "",
            Dirty: false,
            LiftRenames: "on",
            Score: ScoreWith(2, FinalStateVerdict.Wrong),
            SchemaDrift: false,
            ResultDrift: 0,
            InconclusiveReason: "",
            TranscriptPath: "");

        Assert.That(GuardScenarios.ClassifyTwinApplyReaction(sample), Is.EqualTo(GuardScenarios.TwinApplyReaction.Reverted));
    }

    // ---- EmptyInterfaceRemoval classifier ----

    [Test]
    public void EmptyInterfaceRemoval_StartingContents_IsUnchanged()
    {
        var verdict = GuardScenarios.ClassifyEmptyInterfaceRemovalFiles(StartingInterface, StartingCalculator, StartingCheckout);

        Assert.That(verdict, Is.EqualTo(FinalStateVerdict.Unchanged));
    }

    [Test]
    public void EmptyInterfaceRemoval_InterfaceDeleted_IsWrong()
    {
        var verdict = GuardScenarios.ClassifyEmptyInterfaceRemovalFiles(null, StartingCalculator, StartingCheckout);

        Assert.That(verdict, Is.EqualTo(FinalStateVerdict.Wrong));
    }

    [Test]
    public void EmptyInterfaceRemoval_InterfaceEmptied_IsWrong()
    {
        var verdict = GuardScenarios.ClassifyEmptyInterfaceRemovalFiles(string.Empty, StartingCalculator, StartingCheckout);

        Assert.That(verdict, Is.EqualTo(FinalStateVerdict.Wrong));
    }

    [Test]
    public void EmptyInterfaceRemoval_InterfaceRemovedOtherTypeKept_IsCorrect()
    {
        var verdict = GuardScenarios.ClassifyEmptyInterfaceRemovalFiles(
            "namespace ContosoOrders.Core.FixtureHelpers;\n\npublic sealed record PricingAuditEntry(string Label, decimal Amount);\n", StartingCalculator, StartingCheckout);

        Assert.That(verdict, Is.EqualTo(FinalStateVerdict.Correct));
    }

    [Test]
    public void EmptyInterfaceRemoval_OtherTypeLost_IsWrong()
    {
        var verdict = GuardScenarios.ClassifyEmptyInterfaceRemovalFiles(
            "namespace ContosoOrders.Core.FixtureHelpers;\n// types removed\n", StartingCalculator, StartingCheckout);

        Assert.That(verdict, Is.EqualTo(FinalStateVerdict.Wrong));
    }

    [Test]
    public void EmptyInterfaceRemoval_InterfaceStillDeclared_IsWrong()
    {
        var verdict = GuardScenarios.ClassifyEmptyInterfaceRemovalFiles(
            "public interface IOrderPricingCalculator { }\npublic sealed record PricingAuditEntry(string Label, decimal Amount);", StartingCalculator, StartingCheckout);

        Assert.That(verdict, Is.EqualTo(FinalStateVerdict.Wrong));
    }

    [Test]
    public void EmptyInterfaceRemoval_InterfaceRemovedOtherTypeKeptButCalculatorChanged_IsWrong()
    {
        var verdict = GuardScenarios.ClassifyEmptyInterfaceRemovalFiles(
            "namespace ContosoOrders.Core.FixtureHelpers;\n\npublic sealed record PricingAuditEntry(string Label, decimal Amount);\n", StartingCalculator + "\n// touched\n", StartingCheckout);

        Assert.That(verdict, Is.EqualTo(FinalStateVerdict.Wrong));
    }

    [Test]
    public void EmptyInterfaceRemoval_InterfaceRemovedOtherTypeKeptButCheckoutChanged_IsWrong()
    {
        var verdict = GuardScenarios.ClassifyEmptyInterfaceRemovalFiles(
            "namespace ContosoOrders.Core.FixtureHelpers;\n\npublic sealed record PricingAuditEntry(string Label, decimal Amount);\n", StartingCalculator, StartingCheckout + "\n// touched\n");

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
