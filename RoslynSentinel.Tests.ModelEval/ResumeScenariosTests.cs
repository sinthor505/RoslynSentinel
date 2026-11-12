using System.IO;
using NUnit.Framework;
using RoslynSentinel.Tests.ModelEval.Fixtures;

namespace RoslynSentinel.Tests.ModelEval;

[TestFixture]
public class ResumeScenariosTests
{
    private const string CaptureFileName = "stale-caller-edit-after-rename.json";

    private static string StartingCheckout => OrderPricingRefactorReproducer.CheckoutCallerFileContent;

    private static string RenamedCheckout => StartingCheckout.Replace("CalcDisc", "CalculateDiscountedTotal");

    private static string RenamedAndInterfaceTypedCheckout =>
        RenamedCheckout.Replace("private readonly OrderPricingCalculator _calculator", "private readonly IOrderPricingCalculator _calculator");

    // ---- Definition shape and registry ----

    [Test]
    public void StaleCallerEditAfterRename_DefinitionIsResumeRecoverScenario()
    {
        var scenario = ResumeScenarios.StaleCallerEditAfterRename;

        Assert.Multiple(() =>
        {
            Assert.That(scenario.Id, Is.EqualTo(ResumeScenarios.StaleCallerEditAfterRenameId));
            Assert.That(scenario.ScenarioType, Is.EqualTo(2));
            Assert.That(scenario.Expected, Is.EqualTo(ExpectedOutcome.Recover));
            Assert.That(scenario.ExpectedRecoveryTool, Is.EqualTo("ReadFile"));
            Assert.That(scenario.ResumeKeepTurns, Is.EqualTo(11));
            Assert.That(scenario.ResumeTranscript, Is.EqualTo(CaptureFileName));
            Assert.That(scenario.TurnCap, Is.EqualTo(8));
            Assert.That(scenario.SourceRun, Is.Not.Empty);
        });
    }

    [Test]
    public void Registry_FindsStaleCallerEditAfterRename()
    {
        var scenario = ScenarioRegistry.Find(ResumeScenarios.StaleCallerEditAfterRenameId.ToUpperInvariant());

        Assert.That(scenario, Is.SameAs(ResumeScenarios.StaleCallerEditAfterRename));
        Assert.That(ScenarioRegistry.All, Does.Contain(ResumeScenarios.StaleCallerEditAfterRename));
    }

    // ---- Capture file ----

    [Test]
    public void CapturePath_PointsIntoTheTrackedFolderAndFileExists()
    {
        var path = ResumeScenarios.CapturePath(CaptureFileName);

        Assert.That(path, Does.EndWith(Path.Combine("RoslynSentinel.Tests.ModelEval", "ResumeTranscripts", CaptureFileName)));
        Assert.That(File.Exists(path), Is.True, $"capture not found at '{path}'");
    }

    [Test]
    public void Capture_LoadsWithElevenTurnsSchemasAndCommit()
    {
        var source = TranscriptResume.Load(ResumeScenarios.CapturePath(CaptureFileName));

        Assert.Multiple(() =>
        {
            Assert.That(source.Turns, Has.Count.EqualTo(11));
            Assert.That(source.Turns[3].ToolCalls[0].ToolName, Is.EqualTo("RenameSymbol"));
            Assert.That(source.Turns[10].ToolCalls[0].ToolName, Is.EqualTo("CreateFile"));
            Assert.That(source.ToolSchemas, Is.Not.Null);
            Assert.That(source.ToolSchemas!.Keys, Does.Contain("ReplaceSnippet"));
            Assert.That(string.IsNullOrEmpty(source.CommitHash), Is.False);
            Assert.That(source.SystemPrompt, Does.Contain("BLOCKED:"));
        });
    }

    // ---- ClassifyCheckout ----

    [Test]
    public void ClassifyCheckout_CallRenamedFieldUntouched_IsUnchanged()
    {
        var verdict = ResumeScenarios.ClassifyCheckout(RenamedCheckout);

        Assert.That(verdict, Is.EqualTo(FinalStateVerdict.Unchanged));
    }

    [Test]
    public void ClassifyCheckout_CallRenamedAndFieldTypedAsInterface_IsCorrect()
    {
        var verdict = ResumeScenarios.ClassifyCheckout(RenamedAndInterfaceTypedCheckout);

        Assert.That(verdict, Is.EqualTo(FinalStateVerdict.Correct));
    }

    [Test]
    public void ClassifyCheckout_StartingText_IsWrong()
    {
        var verdict = ResumeScenarios.ClassifyCheckout(StartingCheckout);

        Assert.That(verdict, Is.EqualTo(FinalStateVerdict.Wrong));
    }

    [Test]
    public void ClassifyCheckout_NullText_IsDestructive()
    {
        var verdict = ResumeScenarios.ClassifyCheckout(null);

        Assert.That(verdict, Is.EqualTo(FinalStateVerdict.Destructive));
    }

    [Test]
    public void ClassifyCheckout_CorrectTextWithoutGetFinalPrice_IsDestructive()
    {
        var verdict = ResumeScenarios.ClassifyCheckout(RenamedAndInterfaceTypedCheckout.Replace("GetFinalPrice", "FinalPrice"));

        Assert.That(verdict, Is.EqualTo(FinalStateVerdict.Destructive));
    }
}
