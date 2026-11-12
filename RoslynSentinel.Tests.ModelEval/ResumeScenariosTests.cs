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

    [Test]
    public void StaleWorkspaceAfterRename_DefinitionIsResumeFaultedRecoverScenario()
    {
        var scenario = ResumeScenarios.StaleWorkspaceAfterRename;

        Assert.Multiple(() =>
        {
            Assert.That(scenario.Id, Is.EqualTo(ResumeScenarios.StaleWorkspaceAfterRenameId));
            Assert.That(scenario.ScenarioType, Is.EqualTo(4));
            Assert.That(scenario.Expected, Is.EqualTo(ExpectedOutcome.Recover));
            Assert.That(scenario.ResumeTranscript, Is.EqualTo(CaptureFileName));
            Assert.That(scenario.ResumeKeepTurns, Is.EqualTo(11));
            Assert.That(scenario.ResumeFaultTool, Is.EqualTo("ReplaceSnippet"));
            Assert.That(scenario.ResumeFaultSeeded, Is.False);
            Assert.That(scenario.ResumeFaultArguments, Does.Contain("{SOLUTION}"));
        });

        using var arguments = System.Text.Json.JsonDocument.Parse(scenario.ResumeFaultArguments!);
        Assert.Multiple(() =>
        {
            Assert.That(arguments.RootElement.GetProperty("filePath").ValueKind, Is.EqualTo(System.Text.Json.JsonValueKind.String));
            Assert.That(arguments.RootElement.GetProperty("oldContent").ValueKind, Is.EqualTo(System.Text.Json.JsonValueKind.String));
            Assert.That(arguments.RootElement.GetProperty("newContent").ValueKind, Is.EqualTo(System.Text.Json.JsonValueKind.String));
        });
    }

    [Test]
    public void Registry_FindsStaleWorkspaceAfterRename()
    {
        var scenario = ScenarioRegistry.Find(ResumeScenarios.StaleWorkspaceAfterRenameId.ToUpperInvariant());

        Assert.That(scenario, Is.SameAs(ResumeScenarios.StaleWorkspaceAfterRename));
        Assert.That(ScenarioRegistry.All, Does.Contain(ResumeScenarios.StaleWorkspaceAfterRename));
    }

    [Test]
    public void FailedLastCallStaleOldContent_DefinitionIsSeededFaultRecoverScenario()
    {
        var scenario = ResumeScenarios.FailedLastCallStaleOldContent;

        Assert.Multiple(() =>
        {
            Assert.That(scenario.Id, Is.EqualTo(ResumeScenarios.FailedLastCallStaleOldContentId));
            Assert.That(scenario.ScenarioType, Is.EqualTo(4));
            Assert.That(scenario.Expected, Is.EqualTo(ExpectedOutcome.Recover));
            Assert.That(scenario.ResumeTranscript, Is.EqualTo(CaptureFileName));
            Assert.That(scenario.ResumeKeepTurns, Is.EqualTo(11));
            Assert.That(scenario.ResumeFaultTool, Is.EqualTo("ReplaceSnippet"));
            Assert.That(scenario.ResumeFaultSeeded, Is.True);
            Assert.That(scenario.ResumeFaultArguments, Does.Contain("{SOLUTION}"));
        });

        using var arguments = System.Text.Json.JsonDocument.Parse(scenario.ResumeFaultArguments!);
        Assert.Multiple(() =>
        {
            Assert.That(arguments.RootElement.GetProperty("filePath").ValueKind, Is.EqualTo(System.Text.Json.JsonValueKind.String));
            Assert.That(arguments.RootElement.GetProperty("oldContent").ValueKind, Is.EqualTo(System.Text.Json.JsonValueKind.String));
            Assert.That(arguments.RootElement.GetProperty("newContent").ValueKind, Is.EqualTo(System.Text.Json.JsonValueKind.String));
            Assert.That(arguments.RootElement.GetProperty("oldContent").GetString(), Does.Contain("CalcDisc"));
        });
    }

    [Test]
    public void Registry_FindsFailedLastCallStaleOldContent()
    {
        var scenario = ScenarioRegistry.Find(ResumeScenarios.FailedLastCallStaleOldContentId.ToUpperInvariant());

        Assert.That(scenario, Is.SameAs(ResumeScenarios.FailedLastCallStaleOldContent));
        Assert.That(ScenarioRegistry.All, Does.Contain(ResumeScenarios.FailedLastCallStaleOldContent));
    }

    // ---- EndOnCorrectFinalState and BuildTargetReached ----

    [Test]
    public void ResumeRecoverScenarios_EndOnCorrectFinalState()
    {
        Assert.Multiple(() =>
        {
            Assert.That(ResumeScenarios.StaleCallerEditAfterRename.EndOnCorrectFinalState, Is.True);
            Assert.That(ResumeScenarios.StaleWorkspaceAfterRename.EndOnCorrectFinalState, Is.True);
            Assert.That(ResumeScenarios.FailedLastCallStaleOldContent.EndOnCorrectFinalState, Is.True);
        });
    }

    [Test]
    public void ColdStartScenario_DoesNotEndOnCorrectFinalState()
    {
        var scenario = FaultScenarios.RenameSymbolNoSuchMethod;

        Assert.Multiple(() =>
        {
            Assert.That(scenario.ResumeTranscript, Is.Null);
            Assert.That(scenario.EndOnCorrectFinalState, Is.False);
        });
    }

    [Test]
    public void BuildTargetReached_NullWhenFlagIsFalse()
    {
        var target = FaultScenarios.RenameSymbolNoSuchMethod.BuildTargetReached(Path.GetTempPath());

        Assert.That(target, Is.Null);
    }

    [Test]
    public async Task StaleCallerEditAfterRename_BuildTargetReached_IsTrueOnlyForCorrectCheckout()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            var checkoutPath = Path.Combine(tempDir, ScenarioFixtureFiles.CheckoutRelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(checkoutPath)!);
            var target = ResumeScenarios.StaleCallerEditAfterRename.BuildTargetReached(tempDir);

            Assert.That(target, Is.Not.Null);

            await File.WriteAllTextAsync(checkoutPath, RenamedCheckout);
            Assert.That(await target!(CancellationToken.None), Is.False);

            await File.WriteAllTextAsync(checkoutPath, RenamedAndInterfaceTypedCheckout);
            Assert.That(await target!(CancellationToken.None), Is.True);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
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

    // ---- IsResumeSample and ResumeTurnCap ----

    [Test]
    public void IsResumeSample_TrueForTranscriptAndAuthoredOnlySeed_FalseForColdStart()
    {
        var authoredOnly = FaultScenarios.RenameSymbolTypo;

        Assert.Multiple(() =>
        {
            Assert.That(ResumeScenarios.StaleCallerEditAfterRename.IsResumeSample, Is.True);
            Assert.That(ResumeScenarios.StaleWorkspaceAfterRename.IsResumeSample, Is.True);
            Assert.That(ResumeScenarios.FailedLastCallStaleOldContent.IsResumeSample, Is.True);
            Assert.That(FaultScenarios.RenameSymbolNoSuchMethod.IsResumeSample, Is.False);
            Assert.That(authoredOnly.IsResumeSample, Is.True);
        });
    }

    [Test]
    public void ResumeTurnCap_CountsKeptTurnsAuthoredFaultTurnAndLiveTurns()
    {
        var staleCaller = ResumeScenarios.StaleCallerEditAfterRename;
        var seededTranscript = ResumeScenarios.FailedLastCallStaleOldContent;
        var authoredOnly = FaultScenarios.RenameSymbolTypo;

        Assert.Multiple(() =>
        {
            Assert.That(staleCaller.ResumeTurnCap, Is.EqualTo(staleCaller.ResumeKeepTurns + staleCaller.TurnCap));
            Assert.That(seededTranscript.ResumeTurnCap, Is.EqualTo(seededTranscript.ResumeKeepTurns + 1 + seededTranscript.TurnCap));
            Assert.That(authoredOnly.ResumeTurnCap, Is.EqualTo(1 + authoredOnly.TurnCap));
        });
    }

    [Test]
    public void NextSeedTurnNumber_EmptySeedStartsAtTurnOne()
    {
        Assert.That(RoslynSentinel.Tests.ModelEval.LiveModel.ScenarioAgentTests.NextSeedTurnNumber([]), Is.EqualTo(1));
    }

    [Test]
    public void NextSeedTurnNumber_FollowsTheLastSeededTurn_AndAuthoredFaultOnEmptySeedIsTurnOne()
    {
        var seeded = TranscriptResume.BuildAuthoredFaultTurn(1, "RenameSymbol", "{}", "x", true);
        var authored = TranscriptResume.BuildAuthoredFaultTurn(
            RoslynSentinel.Tests.ModelEval.LiveModel.ScenarioAgentTests.NextSeedTurnNumber([]), "RenameSymbol", "{}", "x", true);

        Assert.Multiple(() =>
        {
            Assert.That(RoslynSentinel.Tests.ModelEval.LiveModel.ScenarioAgentTests.NextSeedTurnNumber([seeded]), Is.EqualTo(2));
            Assert.That(authored.TurnNumber, Is.EqualTo(1));
        });
    }
}
