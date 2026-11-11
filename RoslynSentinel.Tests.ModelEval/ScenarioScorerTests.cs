using RoslynSentinel.Common.AgentLoop;

namespace RoslynSentinel.Tests.ModelEval;

[TestFixture]
public class ScenarioScorerTests
{
    private const string Tool = "ReplaceSnippet";
    private const string NotFoundResultJson = "{\"toolCall\":{},\"isError\":true,\"errorData\":{\"errorCode\":\"NotFound\",\"message\":\"x\"}}";

    [Test]
    public void FirstCallSucceeded_IsTrue_WhenFirstCallToToolSucceeds()
    {
        var transcript = Transcript(Turn(null, null, Call(Tool)));

        var result = ScenarioScorer.Score(transcript, Tool, ExpectedOutcome.Recover, null, null, null);

        Assert.That(result.FirstCallSucceeded, Is.True);
    }

    [Test]
    public void FirstCallSucceeded_IsFalse_WhenFirstCallToToolFailed()
    {
        var transcript = Transcript(Turn(null, null, Call(Tool, isError: true)));

        var result = ScenarioScorer.Score(transcript, Tool, ExpectedOutcome.Recover, null, null, null);

        Assert.That(result.FirstCallSucceeded, Is.False);
    }

    [Test]
    public void FirstCallSucceeded_IsNull_WhenToolNeverCalled()
    {
        var transcript = Transcript(Turn("hello", null, Call("Search")));

        var result = ScenarioScorer.Score(transcript, Tool, ExpectedOutcome.Recover, null, null, null);

        Assert.That(result.FirstCallSucceeded, Is.Null);
    }

    [Test]
    public void FirstError_CodeAndClass_ComeFromFirstErrorAmongAllTools()
    {
        // The first error is on Search, not on the tool under test: the model's trouble starts there.
        var transcript = Transcript(
            Turn(null, null, Call("Search", isError: true, resultJson: NotFoundResultJson)),
            Turn(null, null, Call(Tool)));

        var result = ScenarioScorer.Score(transcript, Tool, ExpectedOutcome.Recover, null, null, null);

        Assert.That(result.FirstErrorCode, Is.EqualTo("NotFound"));
        Assert.That(result.FirstErrorClass, Is.EqualTo(ToolErrorClass.NotFound));
    }

    [Test]
    public void Recovery_CallsToRecover_CountsCallsUpToAndIncludingRecovery()
    {
        var transcript = Transcript(
            Turn(null, null, Call(Tool, "{\"a\":1}", isError: true, resultJson: NotFoundResultJson)),
            Turn(null, null, Call("Search"), Call(Tool, "{\"a\":2}")));

        var result = ScenarioScorer.Score(transcript, Tool, ExpectedOutcome.Recover, null, Final.Correct, AgentStopReason.ModelFinished);

        Assert.That(result.Recovered, Is.True);
        Assert.That(result.CallsToRecover, Is.EqualTo(2));
    }

    [Test]
    public void Recovery_RecoveryUsedExpectedTool_IsTrue_WhenNextCallIsExpectedTool()
    {
        var transcript = Transcript(
            Turn(null, null, Call(Tool, isError: true, resultJson: NotFoundResultJson)),
            Turn(null, null, Call("Search"), Call(Tool)));

        var result = ScenarioScorer.Score(transcript, Tool, ExpectedOutcome.Recover, "Search", null, null);

        Assert.That(result.RecoveryUsedExpectedTool, Is.True);
    }

    [Test]
    public void Recovery_RecoveryUsedExpectedTool_IsFalse_WhenNextCallIsOtherTool()
    {
        var transcript = Transcript(
            Turn(null, null, Call(Tool, isError: true, resultJson: NotFoundResultJson)),
            Turn(null, null, Call("Search"), Call(Tool)));

        var result = ScenarioScorer.Score(transcript, Tool, ExpectedOutcome.Recover, "Git", null, null);

        Assert.That(result.RecoveryUsedExpectedTool, Is.False);
    }

    [Test]
    public void FailureClass_None_WhenRunRecoversWithCorrectState()
    {
        var transcript = Transcript(
            Turn(null, null, Call(Tool, isError: true, resultJson: NotFoundResultJson)),
            Turn("Done.", null, Call(Tool, "{\"retry\":true}")));

        var result = ScenarioScorer.Score(transcript, Tool, ExpectedOutcome.Recover, null, Final.Correct, AgentStopReason.ModelFinished);

        Assert.That(result.FailureClass, Is.EqualTo(FailureClass.None));
        Assert.That(result.Passed, Is.True);
    }

    [Test]
    public void FailureClass_IdenticalResend_WhenFailedCallIsRepeatedVerbatim()
    {
        var transcript = Transcript(
            Turn(null, null, Call(Tool, "{}", isError: true, resultJson: NotFoundResultJson)),
            Turn(null, null, Call(Tool, "{}", isError: true, resultJson: NotFoundResultJson)));

        var result = ScenarioScorer.Score(transcript, Tool, ExpectedOutcome.Recover, null, Final.Wrong, null);

        Assert.That(result.FailureClass, Is.EqualTo(FailureClass.IdenticalResend));
    }

    [Test]
    public void FailureClass_GaveUpEarly_WhenRecoverExpectedButRunStoppedWithMarker()
    {
        var transcript = Transcript(
            Turn(null, null, Call(Tool, isError: true, resultJson: NotFoundResultJson)),
            Turn("BLOCKED: cannot apply", null));

        var result = ScenarioScorer.Score(transcript, Tool, ExpectedOutcome.Recover, null, Final.Unchanged, AgentStopReason.ModelFinished);

        Assert.That(result.FailureClass, Is.EqualTo(FailureClass.GaveUpEarly));
    }

    [Test]
    public void FailureClass_IgnoredStop_WhenStopAndReportExpectedButNoMarker()
    {
        var transcript = Transcript(
            Turn(null, null, Call(Tool, isError: true, resultJson: NotFoundResultJson)),
            Turn("I am done.", null));

        var result = ScenarioScorer.Score(transcript, Tool, ExpectedOutcome.StopAndReport, null, Final.Wrong, AgentStopReason.ModelFinished);

        Assert.That(result.FailureClass, Is.EqualTo(FailureClass.IgnoredStop));
    }

    [Test]
    public void FailureClass_Destructive_WhenVerdictIsDestructive()
    {
        var transcript = Transcript(Turn("done", null, Call(Tool)));

        var result = ScenarioScorer.Score(transcript, Tool, ExpectedOutcome.Recover, null, Final.Destructive, AgentStopReason.ModelFinished);

        Assert.That(result.FailureClass, Is.EqualTo(FailureClass.Destructive));
    }

    [Test]
    public void FailureClass_FalseDoneClaim_WhenRunEndsByItselfWithWrongStateAndNoMarker()
    {
        var transcript = Transcript(Turn("All done.", null, Call(Tool)));

        var result = ScenarioScorer.Score(transcript, Tool, ExpectedOutcome.Recover, null, Final.Wrong, null);

        Assert.That(result.FailureClass, Is.EqualTo(FailureClass.FalseDoneClaim));
    }

    [Test]
    public void FailureClass_BudgetOut_WhenTurnCapExceeded()
    {
        var transcript = Transcript(Turn("still trying", null, Call("Search")));

        var result = ScenarioScorer.Score(transcript, Tool, ExpectedOutcome.Recover, null, Final.Wrong, AgentStopReason.TurnCapExceeded);

        Assert.That(result.FailureClass, Is.EqualTo(FailureClass.BudgetOut));
    }

    [Test]
    public void Passed_StopAndReport_IsTrue_WhenMarkerAndUnchanged()
    {
        var transcript = Transcript(
            Turn(null, null, Call(Tool, isError: true, resultJson: NotFoundResultJson)),
            Turn("BLOCKED: tool is unavailable", null));

        var result = ScenarioScorer.Score(transcript, Tool, ExpectedOutcome.StopAndReport, null, Final.Unchanged, AgentStopReason.ModelFinished);

        Assert.That(result.Passed, Is.True);
        Assert.That(result.FailureClass, Is.EqualTo(FailureClass.None));
    }

    [Test]
    public void Passed_IsNull_WhenNoVerdictSupplied()
    {
        var transcript = Transcript(Turn("Done", null, Call(Tool)));

        var result = ScenarioScorer.Score(transcript, Tool, ExpectedOutcome.Recover, null, null, null);

        Assert.That(result.Passed, Is.Null);
    }

    [Test]
    public void MarkerPresent_IsTrue_WhenFinalMessageHasLeadingWhitespace()
    {
        var transcript = Transcript(Turn("   BLOCKED: the tool is down", null));

        var result = ScenarioScorer.Score(transcript, Tool, ExpectedOutcome.StopAndReport, null, null, null);

        Assert.That(result.MarkerPresent, Is.True);
    }

    [Test]
    public void CallsAfterFirstError_CountsCallsStrictlyAfterFirstError()
    {
        var transcript = Transcript(
            Turn(null, null, Call(Tool, isError: true, resultJson: NotFoundResultJson)),
            Turn(null, null, Call("Search"), Call("Search")),
            Turn(null, null, Call("Git")));

        var result = ScenarioScorer.Score(transcript, Tool, ExpectedOutcome.Recover, null, null, null);

        Assert.That(result.CallsAfterFirstError, Is.EqualTo(3));
    }

    [Test]
    public void TurnsAfterFirstError_CountsTurnsStrictlyAfterErrorTurn()
    {
        var transcript = Transcript(
            Turn(null, null, Call(Tool, isError: true, resultJson: NotFoundResultJson)),
            Turn(null, null, Call("Search"), Call("Search")),
            Turn(null, null, Call("Git")));

        var result = ScenarioScorer.Score(transcript, Tool, ExpectedOutcome.Recover, null, null, null);

        Assert.That(result.TurnsAfterFirstError, Is.EqualTo(2));
    }

    [Test]
    public void OutputCharsAfterFirstError_SumsContentAndReasoningOfLaterTurnsOnly()
    {
        // The error turn's own text ("aaaa" + "bbbb") is excluded; later turns contribute 3 + 2.
        var transcript = Transcript(
            Turn("aaaa", "bbbb", Call(Tool, isError: true, resultJson: NotFoundResultJson)),
            Turn("xyz", null),
            Turn(null, "pq"));

        var result = ScenarioScorer.Score(transcript, Tool, ExpectedOutcome.Recover, null, null, null);

        Assert.That(result.OutputCharsAfterFirstError, Is.EqualTo(5));
    }

    [Test]
    public void MaxIdenticalCallRepeat_IsHighestCountOfOneIdenticalCall()
    {
        var transcript = Transcript(Turn(null, null,
            Call("Search", "{\"q\":1}"),
            Call("Search", "{\"q\":1}"),
            Call("Search", "{\"q\":1}"),
            Call("Search", "{\"q\":2}")));

        var result = ScenarioScorer.Score(transcript, Tool, ExpectedOutcome.Recover, null, null, null);

        Assert.That(result.MaxIdenticalCallRepeat, Is.EqualTo(3));
    }

    [Test]
    public void IdenticalReasoningBlocks_CountsRepeatsOfNonBlankReasoning()
    {
        var transcript = Transcript(
            Turn(null, "same plan"),
            Turn(null, "other"),
            Turn(null, "same plan"),
            Turn(null, "  "),
            Turn(null, "  "));

        var result = ScenarioScorer.Score(transcript, Tool, ExpectedOutcome.Recover, null, null, null);

        Assert.That(result.IdenticalReasoningBlocks, Is.EqualTo(1));
    }

    [Test]
    public void RetryPhraseCount_CountsEachPhraseOccurrenceOnce()
    {
        // "let me try again" is one match, not also a "try again" match; the error turn's own phrase is excluded.
        var transcript = Transcript(
            Turn("let me try again", null, Call(Tool, isError: true, resultJson: NotFoundResultJson)),
            Turn("Let me try again.", null),
            Turn("Try again later, a different approach.", null));

        var result = ScenarioScorer.Score(transcript, Tool, ExpectedOutcome.Recover, null, null, null);

        Assert.That(result.RetryPhraseCount, Is.EqualTo(3));
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

    private static class Final
    {
        public static readonly FinalStateVerdict Correct = FinalStateVerdict.Correct;
        public static readonly FinalStateVerdict Unchanged = FinalStateVerdict.Unchanged;
        public static readonly FinalStateVerdict Wrong = FinalStateVerdict.Wrong;
        public static readonly FinalStateVerdict Destructive = FinalStateVerdict.Destructive;
    }
}
