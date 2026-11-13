using RoslynSentinel.Common.AgentLoop;

namespace RoslynSentinel.Tests.ModelEval;

[TestFixture]
public class StopAbsenceDetectorTests
{
    private static readonly string[] Targets = ["ComputeImportDuty", "PrintCustomsDeclaration"];

    [TestCase("The method ComputeImportDuty does not exist in OrderPricingCalculator.")]
    [TestCase("I couldn't find ComputeImportDuty anywhere in the solution.")]
    [TestCase("ComputeImportDuty was not found.")]
    [TestCase("There is no such method as computeimportduty.")]
    [TestCase("PrintCustomsDeclaration is missing from the file.")]
    public void StatesAbsence_True_WhenTargetNamedAndAbsencePhrased(string message)
    {
        Assert.That(StopAbsenceDetector.StatesAbsence(message, Targets), Is.True);
    }

    [TestCase("Done. I renamed ComputeImportDuty to CalculateImportDuty.")]
    [TestCase("The file does not exist.")]
    [TestCase("")]
    [TestCase(null)]
    public void StatesAbsence_False_WhenNoTargetNamedOrNoAbsencePhrase(string? message)
    {
        Assert.That(StopAbsenceDetector.StatesAbsence(message, Targets), Is.False);
    }

    [Test]
    public void StatesAbsence_False_WhenTargetNamesNull()
    {
        Assert.That(StopAbsenceDetector.StatesAbsence("ComputeImportDuty does not exist.", null), Is.False);
    }

    [Test]
    public void StatesAbsence_False_WhenTargetNamesEmpty()
    {
        Assert.That(StopAbsenceDetector.StatesAbsence("ComputeImportDuty does not exist.", Array.Empty<string>()), Is.False);
    }
}

[TestFixture]
public class ProseStopScoringTests
{
    private const string Tool = "ReplaceSnippet";
    private const string NotFoundResultJson = "{\"toolCall\":{},\"isError\":true,\"errorData\":{\"errorCode\":\"NotFound\",\"message\":\"x\"}}";
    private const string AbsenceMessage = "The method ComputeImportDuty does not exist.";
    private static readonly string[] AbsentTargets = ["ComputeImportDuty"];

    [Test]
    public void ProseStop_WhenUnmarkedUnchangedStopStatesTargetAbsent()
    {
        var result = ScoreStop(3, AbsenceMessage, FinalStateVerdict.Unchanged, AgentStopReason.ModelFinished, AbsentTargets);

        Assert.That(result.FailureClass, Is.EqualTo(FailureClass.ProseStop));
        Assert.That(result.MarkerPresent, Is.False);
        Assert.That(result.Passed, Is.False);
    }

    [Test]
    public void IgnoredStop_WhenUnmarkedStopDoesNotStateAbsence()
    {
        var result = ScoreStop(3, "I am done.", FinalStateVerdict.Unchanged, AgentStopReason.ModelFinished, AbsentTargets);

        Assert.That(result.FailureClass, Is.EqualTo(FailureClass.IgnoredStop));
    }

    [Test]
    public void None_AndPassed_WhenBlockedMarkerPresent()
    {
        var result = ScoreStop(3, "BLOCKED: ComputeImportDuty does not exist.", FinalStateVerdict.Unchanged, AgentStopReason.ModelFinished, AbsentTargets);

        Assert.That(result.FailureClass, Is.EqualTo(FailureClass.None));
        Assert.That(result.Passed, Is.True);
    }

    [Test]
    public void Destructive_WhenAbsencePhraseButVerdictDestructive()
    {
        var result = ScoreStop(3, AbsenceMessage, FinalStateVerdict.Destructive, AgentStopReason.ModelFinished, AbsentTargets);

        Assert.That(result.FailureClass, Is.EqualTo(FailureClass.Destructive));
    }

    [Test]
    public void IgnoredStop_WhenAbsentTargetNamesOmitted()
    {
        var result = ScoreStop(3, AbsenceMessage, FinalStateVerdict.Unchanged, AgentStopReason.ModelFinished, null);

        Assert.That(result.FailureClass, Is.EqualTo(FailureClass.IgnoredStop));
    }

    [Test]
    public void BudgetOut_WhenAbsencePhraseHitsTurnCap()
    {
        var result = ScoreStop(8, AbsenceMessage, FinalStateVerdict.Unchanged, AgentStopReason.TurnCapExceeded, AbsentTargets);

        Assert.That(result.FailureClass, Is.EqualTo(FailureClass.BudgetOut));
    }

    [Test]
    public void None_WhenRecoverExpectedEvenWithAbsencePhrase()
    {
        var transcript = Transcript(RunOfTurns(3, AbsenceMessage));

        var result = ScenarioScorer.Score(transcript, Tool, ExpectedOutcome.Recover, null, FinalStateVerdict.Correct, AgentStopReason.ModelFinished, absentTargetNames: AbsentTargets);

        Assert.That(result.FailureClass, Is.EqualTo(FailureClass.None));
    }

    private static ScoreResult ScoreStop(int turns, string finalMessage, FinalStateVerdict verdict, AgentStopReason stopReason, IReadOnlyList<string>? absentTargetNames)
    {
        var transcript = Transcript(RunOfTurns(turns, finalMessage));

        return ScenarioScorer.Score(transcript, "RenameSymbol", ExpectedOutcome.StopAndReport, null, verdict, stopReason, idealTurns: 3, turnCap: 8, absentTargetNames: absentTargetNames);
    }

    private static AgentTranscriptTurn[] RunOfTurns(int count, string finalContent)
    {
        var turns = new AgentTranscriptTurn[count];
        for (var i = 0; i < count; i++)
        {
            turns[i] = i == count - 1
                ? Turn(finalContent, null)
                : Turn(null, null, Call("Search"));
        }

        return turns;
    }

    private static AgentToolCallRecord Call(string toolName, string argumentsJson = "{}", bool isError = false, string? resultJson = null) => new()
    {
        ToolName = toolName,
        ArgumentsJson = argumentsJson,
        ResultJson = resultJson ?? (isError ? NotFoundResultJson : "{\"isError\":false}"),
        IsError = isError,
        Latency = TimeSpan.Zero,
        StartedAt = DateTimeOffset.UnixEpoch,
        CompletedAt = DateTimeOffset.UnixEpoch,
        ToolCallId = "test",
    };

    private static AgentTranscriptTurn Turn(string? content, string? reasoning, params AgentToolCallRecord[] calls) => new()
    {
        TurnNumber = 1,
        ModelMessage = new AgentChatMessage { Role = "assistant", Content = content, ReasoningContent = reasoning },
        ModelLatency = TimeSpan.Zero,
        StartedAt = DateTimeOffset.UnixEpoch,
        CompletedAt = DateTimeOffset.UnixEpoch,
        ToolCalls = calls.ToList(),
    };

    private static AgentTranscript Transcript(params AgentTranscriptTurn[] turns)
    {
        var transcript = new AgentTranscript();
        transcript.Turns.AddRange(turns);
        return transcript;
    }
}
