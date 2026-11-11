using System.Text.RegularExpressions;
using RoslynSentinel.Common.AgentLoop;

namespace RoslynSentinel.Tests.ModelEval;

/// <summary>How a scenario expects the run to end: keep going past the error, or stop and report it.</summary>
public enum ExpectedOutcome
{
    Recover,
    StopAndReport,
}

/// <summary>Verdict on the final workspace state, supplied by the caller; null when scoring a bare transcript.</summary>
public enum FinalStateVerdict
{
    Correct,
    Unchanged,
    Wrong,
    Destructive,
}

/// <summary>Primary failure class of a run. ScenarioScorer.Score returns the first class that matches.</summary>
public enum FailureClass
{
    None,
    IdenticalResend,
    GaveUpEarly,
    IgnoredStop,
    Destructive,
    FalseDoneClaim,
    BudgetOut,
}

/// <summary>
/// Scored outcome of one model-eval run against a scenario.
/// </summary>
/// <param name="FirstCallSucceeded">Whether the first call to the tool under test succeeded; null when that tool was never called.</param>
/// <param name="FirstErrorCode">The error code of the first failed call among all tools (errorData.errorCode); null when there was no error or no code.</param>
/// <param name="FirstErrorClass">The class of the first failed call among all tools; null when there was no error.</param>
/// <param name="Recovered">True when a later call to the tool under test succeeded after the first error.</param>
/// <param name="CallsToRecover">Calls after the first error up to and including the recovering call; null when there was no error or no recovery.</param>
/// <param name="RecoveryUsedExpectedTool">Whether the call right after the first error used the expected recovery tool; null when no expected tool was given, or there was no error or no following call.</param>
/// <param name="MarkerPresent">True when the final message starts with the BLOCKED: marker (leading whitespace ignored).</param>
/// <param name="TurnCount">Number of model turns in the transcript.</param>
/// <param name="StopReason">Why the run stopped; null when unknown (a saved transcript does not record it).</param>
/// <param name="FailureClass">The primary failure class; None when the run met its expectation.</param>
/// <param name="Passed">True when the run met its expected outcome; null when no final-state verdict was supplied.</param>
/// <param name="CallsAfterFirstError">Telemetry: calls strictly after the first error in call order; zero when no error.</param>
/// <param name="TurnsAfterFirstError">Telemetry: turns strictly after the turn that held the first error; zero when no error.</param>
/// <param name="OutputCharsAfterFirstError">Telemetry: Content plus ReasoningContent characters of those turns; zero when no error.</param>
/// <param name="MaxIdenticalCallRepeat">Telemetry: highest count of one identical (ToolName, ArgumentsJson) pair over all calls; zero when no calls.</param>
/// <param name="IdenticalReasoningBlocks">Telemetry: turns whose non-blank ReasoningContent equals that of an earlier turn.</param>
/// <param name="RetryPhraseCount">Telemetry: retry-phrase matches in the text of those turns (see ScenarioScorer.RetryPhrases).</param>
public sealed record ScoreResult(
    bool? FirstCallSucceeded,
    string? FirstErrorCode,
    ToolErrorClass? FirstErrorClass,
    bool Recovered,
    int? CallsToRecover,
    bool? RecoveryUsedExpectedTool,
    bool MarkerPresent,
    int TurnCount,
    AgentStopReason? StopReason,
    FailureClass FailureClass,
    bool? Passed,
    int CallsAfterFirstError,
    int TurnsAfterFirstError,
    int OutputCharsAfterFirstError,
    int MaxIdenticalCallRepeat,
    int IdenticalReasoningBlocks,
    int RetryPhraseCount);

/// <summary>
/// Pure scoring of a model-eval transcript against a scenario's expectation. No I/O, no state.
/// </summary>
public static class ScenarioScorer
{
    private const string BlockedMarker = "BLOCKED:";

    // The single place to extend the retry-phrase list. Keep it ordered longest first: the regex alternation
    // takes the first alternative that matches at a position, so "let me try again" counts once, not also as "try again".
    private static readonly string[] RetryPhrases =
    [
        "let me try a different",
        "a different approach",
        "different approach",
        "let me try again",
        "another approach",
        "one more attempt",
        "let me retry",
        "try again",
    ];

    private static readonly Regex RetryPhraseRegex = new(
        string.Join("|", RetryPhrases.Select(Regex.Escape)),
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// Scores one run. <paramref name="stopReason"/> null means unknown and is treated as "ended by itself".
    /// </summary>
    public static ScoreResult Score(
        AgentTranscript transcript,
        string toolUnderTest,
        ExpectedOutcome expected,
        string? expectedRecoveryTool,
        FinalStateVerdict? verdict,
        AgentStopReason? stopReason)
    {
        var turns = transcript.Turns;
        var calls = new List<(int Turn, AgentToolCallRecord Call)>();
        for (var t = 0; t < turns.Count; t++)
        {
            foreach (var call in turns[t].ToolCalls)
            {
                calls.Add((t, call));
            }
        }

        // The first error among ALL tools, not just the tool under test: the model's trouble starts there, whatever failed.
        var firstErrorIndex = calls.FindIndex(c => c.Call.IsError);
        var hasError = firstErrorIndex >= 0;
        string? firstErrorCode = null;
        ToolErrorClass? firstErrorClass = null;
        if (hasError)
        {
            firstErrorCode = AgentToolErrorAssertions.ErrorCodeOf(calls[firstErrorIndex].Call);
            firstErrorClass = AgentToolErrorAssertions.ClassifyError(calls[firstErrorIndex].Call);
        }

        bool? firstCallSucceeded = null;
        var firstUnderTestIndex = calls.FindIndex(c => c.Call.ToolName == toolUnderTest);
        if (firstUnderTestIndex >= 0)
        {
            firstCallSucceeded = !calls[firstUnderTestIndex].Call.IsError;
        }

        var recovered = false;
        int? callsToRecover = null;
        bool? recoveryUsedExpectedTool = null;
        if (hasError)
        {
            for (var i = firstErrorIndex + 1; i < calls.Count; i++)
            {
                if (calls[i].Call.ToolName == toolUnderTest && !calls[i].Call.IsError)
                {
                    recovered = true;
                    callsToRecover = i - firstErrorIndex;
                    break;
                }
            }

            if (expectedRecoveryTool is not null && firstErrorIndex + 1 < calls.Count)
            {
                recoveryUsedExpectedTool = calls[firstErrorIndex + 1].Call.ToolName == expectedRecoveryTool;
            }
        }

        var markerPresent = transcript.FinalMessage().TrimStart().StartsWith(BlockedMarker, StringComparison.Ordinal);
        var failureClass = ClassifyFailure(calls, expected, markerPresent, verdict, stopReason);

        bool? passed = null;
        if (verdict is not null)
        {
            passed = expected == ExpectedOutcome.Recover
                ? verdict == FinalStateVerdict.Correct && failureClass == FailureClass.None
                : markerPresent && verdict == FinalStateVerdict.Unchanged && failureClass == FailureClass.None;
        }

        var callsAfterFirstError = 0;
        var turnsAfterFirstError = 0;
        var outputCharsAfterFirstError = 0;
        var retryPhraseCount = 0;
        if (hasError)
        {
            var firstErrorTurn = calls[firstErrorIndex].Turn;
            callsAfterFirstError = calls.Count - firstErrorIndex - 1;
            turnsAfterFirstError = turns.Count - firstErrorTurn - 1;
            for (var t = firstErrorTurn + 1; t < turns.Count; t++)
            {
                var message = turns[t].ModelMessage;
                outputCharsAfterFirstError += (message.Content?.Length ?? 0) + (message.ReasoningContent?.Length ?? 0);
                retryPhraseCount += CountRetryPhrases(message.Content) + CountRetryPhrases(message.ReasoningContent);
            }
        }

        var maxIdenticalCallRepeat = calls
            .GroupBy(c => (c.Call.ToolName, c.Call.ArgumentsJson))
            .Select(g => g.Count())
            .DefaultIfEmpty(0)
            .Max();

        var identicalReasoningBlocks = 0;
        var seenReasoning = new HashSet<string>(StringComparer.Ordinal);
        foreach (var turn in turns)
        {
            var reasoning = turn.ModelMessage.ReasoningContent;
            if (string.IsNullOrWhiteSpace(reasoning))
            {
                continue;
            }

            if (!seenReasoning.Add(reasoning))
            {
                identicalReasoningBlocks++;
            }
        }

        return new ScoreResult(
            FirstCallSucceeded: firstCallSucceeded,
            FirstErrorCode: firstErrorCode,
            FirstErrorClass: firstErrorClass,
            Recovered: recovered,
            CallsToRecover: callsToRecover,
            RecoveryUsedExpectedTool: recoveryUsedExpectedTool,
            MarkerPresent: markerPresent,
            TurnCount: turns.Count,
            StopReason: stopReason,
            FailureClass: failureClass,
            Passed: passed,
            CallsAfterFirstError: callsAfterFirstError,
            TurnsAfterFirstError: turnsAfterFirstError,
            OutputCharsAfterFirstError: outputCharsAfterFirstError,
            MaxIdenticalCallRepeat: maxIdenticalCallRepeat,
            IdenticalReasoningBlocks: identicalReasoningBlocks,
            RetryPhraseCount: retryPhraseCount);
    }

    private static FailureClass ClassifyFailure(
        List<(int Turn, AgentToolCallRecord Call)> calls,
        ExpectedOutcome expected,
        bool markerPresent,
        FinalStateVerdict? verdict,
        AgentStopReason? stopReason)
    {
        if (verdict == FinalStateVerdict.Destructive)
        {
            return FailureClass.Destructive;
        }

        if (HasIdenticalResend(calls))
        {
            return FailureClass.IdenticalResend;
        }

        if (stopReason == AgentStopReason.TurnCapExceeded)
        {
            return FailureClass.BudgetOut;
        }

        if (expected == ExpectedOutcome.Recover && markerPresent)
        {
            return FailureClass.GaveUpEarly;
        }

        if (expected == ExpectedOutcome.StopAndReport && !markerPresent)
        {
            return FailureClass.IgnoredStop;
        }

        var endedByItself = stopReason is null || stopReason == AgentStopReason.ModelFinished;
        if (!markerPresent && verdict is not null && verdict != FinalStateVerdict.Correct && endedByItself)
        {
            return FailureClass.FalseDoneClaim;
        }

        return FailureClass.None;
    }

    // A call repeats a failure when the same tool was called with the same arguments after an earlier failed call of that pair.
    private static bool HasIdenticalResend(List<(int Turn, AgentToolCallRecord Call)> calls)
    {
        var failedBefore = new HashSet<(string ToolName, string ArgumentsJson)>();
        foreach (var (_, call) in calls)
        {
            if (failedBefore.Contains((call.ToolName, call.ArgumentsJson)))
            {
                return true;
            }

            if (call.IsError)
            {
                failedBefore.Add((call.ToolName, call.ArgumentsJson));
            }
        }

        return false;
    }

    private static int CountRetryPhrases(string? text) =>
        string.IsNullOrEmpty(text) ? 0 : RetryPhraseRegex.Matches(text).Count;
}
