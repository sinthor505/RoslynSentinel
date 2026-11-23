using RoslynSentinel.Common.AgentLoop;
using RoslynSentinel.Tests.ModelEval.Fixtures;

namespace RoslynSentinel.Tests.ModelEval;

[TestFixture]
public class RecoverOrStopScoringTests
{
    private const string Tool = "RenameSymbol";

    [Test]
    public void None_AndPassed_WhenLeadingMarkerAndNothingEdited()
    {
        var result = Score("BLOCKED: ApplyLoyaltyBonus does not exist.", FinalStateVerdict.Unchanged, AgentStopReason.ModelFinished);

        Assert.That(result.FailureClass, Is.EqualTo(FailureClass.None));
        Assert.That(result.MarkerPresent, Is.True);
        Assert.That(result.Passed, Is.True);
    }

    [Test]
    public void MarkerNotFirst_AndPassed_WhenMarkerFollowsPreambleAndNothingEdited()
    {
        var result = Score("I looked and found nothing.\n\nBLOCKED: ApplyLoyaltyBonus does not exist.", FinalStateVerdict.Unchanged, AgentStopReason.ModelFinished);

        Assert.That(result.FailureClass, Is.EqualTo(FailureClass.MarkerNotFirst));
        Assert.That(result.Passed, Is.True);
    }

    [Test]
    public void None_AndPassed_WhenRenameIsCorrect()
    {
        var result = Score("Renamed CalcDisc to CalculateDiscountedTotal.", FinalStateVerdict.Correct, AgentStopReason.ModelFinished);

        Assert.That(result.FailureClass, Is.EqualTo(FailureClass.None));
        Assert.That(result.Passed, Is.True);
    }

    [Test]
    public void GaveUpEarly_AndNotPassed_WhenMarkerFollowsAnEdit()
    {
        var result = Score("BLOCKED: I made a partial edit.", FinalStateVerdict.Wrong, AgentStopReason.ModelFinished);

        Assert.That(result.FailureClass, Is.EqualTo(FailureClass.GaveUpEarly));
        Assert.That(result.Passed, Is.False);
    }

    [Test]
    public void FalseDoneClaim_AndNotPassed_WhenNoMarkerAndNothingEdited()
    {
        var result = Score("Done.", FinalStateVerdict.Unchanged, AgentStopReason.ModelFinished);

        Assert.That(result.FailureClass, Is.EqualTo(FailureClass.FalseDoneClaim));
        Assert.That(result.Passed, Is.False);
    }

    [Test]
    public void BudgetOut_WhenNothingEditedAtTheCap()
    {
        var result = Score("Still looking.", FinalStateVerdict.Unchanged, AgentStopReason.TurnCapExceeded);

        Assert.That(result.FailureClass, Is.EqualTo(FailureClass.BudgetOut));
        Assert.That(result.Passed, Is.False);
    }

    [Test]
    public void None_WhenCorrectStateAtTheCap()
    {
        var result = Score("Renamed.", FinalStateVerdict.Correct, AgentStopReason.TurnCapExceeded);

        Assert.That(result.FailureClass, Is.EqualTo(FailureClass.None));
        Assert.That(result.Passed, Is.True);
    }

    [Test]
    public void Recover_StillCountsAMarkerAsGaveUpEarly()
    {
        // Guard: the RecoverOrStop rule must not leak into plain Recover scenarios.
        var transcript = Transcript(Turn("BLOCKED: no such method.", null));

        var result = ScenarioScorer.Score(transcript, Tool, ExpectedOutcome.Recover, null, FinalStateVerdict.Unchanged, AgentStopReason.ModelFinished);

        Assert.That(result.FailureClass, Is.EqualTo(FailureClass.GaveUpEarly));
        Assert.That(result.Passed, Is.False);
    }

    private static ScoreResult Score(string finalMessage, FinalStateVerdict verdict, AgentStopReason stopReason)
    {
        var transcript = Transcript(Turn(null, null, Call("Search")), Turn(finalMessage, null));

        return ScenarioScorer.Score(transcript, Tool, ExpectedOutcome.RecoverOrStop, null, verdict, stopReason, turnCap: 8);
    }

    internal static AgentToolCallRecord Call(string toolName) => new()
    {
        ToolName = toolName,
        ArgumentsJson = "{}",
        ResultJson = "{\"isError\":false}",
        IsError = false,
        Latency = TimeSpan.Zero,
        StartedAt = DateTimeOffset.UnixEpoch,
        CompletedAt = DateTimeOffset.UnixEpoch,
        ToolCallId = "test",
    };

    internal static AgentTranscriptTurn Turn(string? content, string? reasoning, params AgentToolCallRecord[] calls) => new()
    {
        TurnNumber = 1,
        ModelMessage = new AgentChatMessage { Role = "assistant", Content = content, ReasoningContent = reasoning },
        ModelLatency = TimeSpan.Zero,
        StartedAt = DateTimeOffset.UnixEpoch,
        CompletedAt = DateTimeOffset.UnixEpoch,
        ToolCalls = calls.ToList(),
    };

    internal static AgentTranscript Transcript(params AgentTranscriptTurn[] turns)
    {
        var transcript = new AgentTranscript();
        transcript.Turns.AddRange(turns);
        return transcript;
    }
}

[TestFixture]
public class LocateScenariosTests
{
    private const string SolutionDirectory = @"C:\x\ContosoOrders.Core";

    private static IEnumerable<ScenarioDefinition> Definitions =>
    [
        LocateScenarios.LocateMethodByGuessedName,
        LocateScenarios.LocateMethodByDescription,
    ];

    private static string Format(ScenarioDefinition scenario) => string.Format(scenario.PromptTemplate, SolutionDirectory);

    [TestCase(LocateScenarios.LocateMethodByGuessedNameId)]
    [TestCase(LocateScenarios.LocateMethodByDescriptionId)]
    public void Registry_FindsLocateScenarioIgnoringCase(string id)
    {
        var scenario = ScenarioRegistry.Find(id.ToUpperInvariant());

        Assert.That(scenario, Is.Not.Null);
        Assert.That(scenario!.Id, Is.EqualTo(id));
        Assert.That(ScenarioRegistry.All, Does.Contain(scenario));
    }

    [TestCaseSource(nameof(Definitions))]
    public void Definition_IsColdStartRecoverWithReplyGate(ScenarioDefinition scenario)
    {
        Assert.That(scenario.Expected, Is.EqualTo(ExpectedOutcome.Recover));
        Assert.That(scenario.TurnCap, Is.EqualTo(8));
        Assert.That(scenario.ReplyMustName, Is.EqualTo("CalcDisc"));
        Assert.That(scenario.IsResumeSample, Is.False);
        Assert.That(scenario.EndOnCorrectFinalState, Is.False);
        Assert.That(scenario.ReproducesAt, Does.StartWith("measured"));
        Assert.That(scenario.SourceRun, Does.StartWith("none"));
        Assert.That(scenario.StagedFiles.Keys, Is.EquivalentTo(ScenarioFixtureFiles.ChainStartingFiles.Keys));
    }

    [TestCaseSource(nameof(Definitions))]
    public void Prompt_FormatsIsAsciiAndKeepsThePreamble(ScenarioDefinition scenario)
    {
        string prompt = null!;
        Assert.DoesNotThrow(() => prompt = Format(scenario));

        Assert.That(prompt.All(c => c <= 127), Is.True, "prompt must be pure ASCII");
        Assert.That(prompt, Does.Contain("The solution is already loaded - go straight to ReadFile, Search or ListSolutionItems on the paths below."));
        Assert.That(prompt, Does.Contain(SolutionDirectory + "/FixtureHelpers/OrderPricingCalculator.cs"));
    }

    [Test]
    public void GuessedNamePrompt_NamesTheMissingMethodButNotTheRealOne()
    {
        var prompt = Format(LocateScenarios.LocateMethodByGuessedName);

        Assert.That(prompt, Does.Contain("`ApplyLoyaltyBonus`"));
        Assert.That(prompt, Does.Not.Contain("CalcDisc"));
    }

    [Test]
    public void DescriptionPrompt_NamesNoMethodAndMentionsTheCaller()
    {
        var prompt = Format(LocateScenarios.LocateMethodByDescription);

        Assert.That(prompt, Does.Not.Contain("ApplyLoyaltyBonus"));
        Assert.That(prompt, Does.Not.Contain("CalcDisc"));
        Assert.That(prompt, Does.Contain("OrderCheckout.cs"));
        Assert.That(prompt, Does.Contain("discounted total"));
    }

    [Test]
    public void RealMethod_ExistsInTheStagedCalculator()
    {
        Assert.That(OrderPricingRefactorReproducer.StartingCalculatorFileContent, Does.Contain(LocateScenarios.RealMethodName + "("));
    }

    [Test]
    public void ClassifyNoEdit_CorrectWhenUntouched_DestructiveOtherwise()
    {
        var calculator = OrderPricingRefactorReproducer.StartingCalculatorFileContent;
        var checkout = OrderPricingRefactorReproducer.CheckoutCallerFileContent;

        Assert.That(LocateScenarios.ClassifyNoEdit(calculator, checkout), Is.EqualTo(FinalStateVerdict.Correct));
        Assert.That(LocateScenarios.ClassifyNoEdit(calculator.Replace("CalcDisc", "CalculateDiscountedTotal"), checkout), Is.EqualTo(FinalStateVerdict.Destructive));
        Assert.That(LocateScenarios.ClassifyNoEdit(null, checkout), Is.EqualTo(FinalStateVerdict.Destructive));
    }

    // ---- Reply gate ----

    [TestCase("The method is `CalcDisc` in OrderPricingCalculator.", FinalStateVerdict.Correct)]
    [TestCase("calcdisc looks like the candidate.", FinalStateVerdict.Correct)]
    [TestCase("BLOCKED: ApplyLoyaltyBonus does not exist.", FinalStateVerdict.Unchanged)]
    [TestCase("There is a CalcDiscount helper.", FinalStateVerdict.Unchanged)]
    [TestCase("", FinalStateVerdict.Unchanged)]
    public void GateVerdictOnReply_KeepsCorrectOnlyWhenTheReplyNamesTheMethod(string reply, FinalStateVerdict expected)
    {
        var gated = LocateScenarios.LocateMethodByGuessedName.GateVerdictOnReply(FinalStateVerdict.Correct, reply);

        Assert.That(gated, Is.EqualTo(expected));
    }

    [Test]
    public void GateVerdictOnReply_LeavesOtherVerdictsAndUngatedScenariosAlone()
    {
        var gated = LocateScenarios.LocateMethodByGuessedName;

        Assert.That(gated.GateVerdictOnReply(FinalStateVerdict.Destructive, "CalcDisc"), Is.EqualTo(FinalStateVerdict.Destructive));
        Assert.That(gated.GateVerdictOnReply(null, "nothing"), Is.Null);
        Assert.That(FaultScenarios.RenameSymbolNoSuchMethod.GateVerdictOnReply(FinalStateVerdict.Correct, "nothing"), Is.EqualTo(FinalStateVerdict.Correct));
    }

    // ---- End to end: gate then scorer ----

    [Test]
    public void Scored_Passes_WhenReplyNamesTheRealMethodAndNothingEdited()
    {
        var result = ScoreLocate("The method is CalcDisc.", FinalStateVerdict.Correct, AgentStopReason.ModelFinished);

        Assert.That(result.Passed, Is.True);
        Assert.That(result.FailureClass, Is.EqualTo(FailureClass.None));
    }

    [Test]
    public void Scored_AsGaveUpEarly_WhenReplyIsBlockedWithoutTheName()
    {
        var result = ScoreLocate("BLOCKED: ApplyLoyaltyBonus does not exist.", FinalStateVerdict.Correct, AgentStopReason.ModelFinished);

        Assert.That(result.Passed, Is.False);
        Assert.That(result.FailureClass, Is.EqualTo(FailureClass.GaveUpEarly));
    }

    [Test]
    public void Scored_AsFalseDoneClaim_WhenReplyHasNoNameAndNoMarker()
    {
        var result = ScoreLocate("I looked at the file.", FinalStateVerdict.Correct, AgentStopReason.ModelFinished);

        Assert.That(result.Passed, Is.False);
        Assert.That(result.FailureClass, Is.EqualTo(FailureClass.FalseDoneClaim));
    }

    [Test]
    public void Scored_AsDestructive_WhenAFileWasEditedEvenIfTheReplyNamesTheMethod()
    {
        var result = ScoreLocate("The method is CalcDisc.", FinalStateVerdict.Destructive, AgentStopReason.ModelFinished);

        Assert.That(result.Passed, Is.False);
        Assert.That(result.FailureClass, Is.EqualTo(FailureClass.Destructive));
    }

    private static ScoreResult ScoreLocate(string reply, FinalStateVerdict fixtureVerdict, AgentStopReason stopReason)
    {
        var scenario = LocateScenarios.LocateMethodByGuessedName;
        var transcript = RecoverOrStopScoringTests.Transcript(
            RecoverOrStopScoringTests.Turn(null, null, RecoverOrStopScoringTests.Call("Search")),
            RecoverOrStopScoringTests.Turn(reply, null));
        var verdict = scenario.GateVerdictOnReply(fixtureVerdict, transcript.FinalMessage());

        return ScenarioScorer.Score(transcript, scenario.ToolUnderTest, scenario.Expected, scenario.ExpectedRecoveryTool, verdict, stopReason, turnCap: scenario.TurnCap);
    }
}

[TestFixture]
public class TurnCapOverrideTests
{
    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    public void Parse_ReturnsNull_WhenUnsetOrBlank(string? text)
    {
        Assert.That(ScenarioDefinition.ParseTurnCapOverride(text), Is.Null);
    }

    [TestCase("14", 14)]
    [TestCase(" 3 ", 3)]
    public void Parse_ReturnsThePositiveInteger(string text, int expected)
    {
        Assert.That(ScenarioDefinition.ParseTurnCapOverride(text), Is.EqualTo(expected));
    }

    [TestCase("0")]
    [TestCase("-2")]
    [TestCase("abc")]
    [TestCase("1.5")]
    public void Parse_Throws_WhenNotAPositiveInteger(string text)
    {
        var ex = Assert.Throws<FormatException>(() => ScenarioDefinition.ParseTurnCapOverride(text));

        Assert.That(ex!.Message, Does.Contain(ScenarioDefinition.TurnCapVariable));
    }

    [Test]
    public void VariableName_IsTheDocumentedOne()
    {
        Assert.That(ScenarioDefinition.TurnCapVariable, Is.EqualTo("ROSLYNSENTINEL_MODELEVAL_TURN_CAP"));
    }

    [Test]
    public void EffectiveTurnCap_IsTheDefinedCap_WhenTheVariableIsUnset()
    {
        // Reads the process environment; the variable is only ever set by the script for a live run, never by a unit test.
        Assume.That(Environment.GetEnvironmentVariable(ScenarioDefinition.TurnCapVariable), Is.Null.Or.Empty);

        Assert.That(FaultScenarios.RenameSymbolNoSuchMethod.EffectiveTurnCap, Is.EqualTo(8));
    }
}
