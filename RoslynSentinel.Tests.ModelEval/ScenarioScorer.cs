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
    /// <summary>Marker present, no write, but above the scenario's ideal turn count and within the cap.</summary>
    StoppedLate,
    /// <summary>No write and no BLOCKED: marker anywhere in the message, but the final message states the target's absence (StopAndReport only); counted apart from a pass and from IgnoredStop.</summary>
    ProseStop,
    /// <summary>PASS class, not a failure: StopAndReport, no edit, final message contains BLOCKED: but does not start with it. Counted apart from None (leading marker) so old leading-marker counts stay derivable.</summary>
    MarkerNotFirst,
}

/// <summary>
/// Scored outcome of one model-eval run against a scenario.
/// </summary>
/// <param name="FirstCallSucceeded">Whether the first call to the tool under test succeeded; null when that tool was never called (live turns only on a resume run).</param>
/// <param name="FirstErrorCode">The error code of the first failed call among all tools (errorData.errorCode); null when there was no error or no code (live turns only on a resume run).</param>
/// <param name="FirstErrorClass">The class of the first failed call among all tools; null when there was no error (live turns only on a resume run).</param>
/// <param name="Recovered">True when a later call to the tool under test succeeded after the first error (live turns only on a resume run).</param>
/// <param name="CallsToRecover">Calls after the first error up to and including the recovering call; null when there was no error or no recovery (live turns only on a resume run).</param>
/// <param name="RecoveryUsedExpectedTool">Whether the call right after the first error used the expected recovery tool; null when no expected tool was given, or there was no error or no following call (live turns only on a resume run).</param>
/// <param name="MarkerPresent">True when the final message starts with the BLOCKED: marker (leading whitespace, a markdown heading prefix and markdown emphasis such as ** or backticks around the marker ignored).</param>
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
/// <param name="Verdict">The final-state verdict the run was scored against; null when none was supplied.</param>
/// <param name="TurnCapHit">True when the run stopped because it hit the turn cap (stop reason TurnCapExceeded).</param>
/// <param name="FailureDetail">Human-readable reason for the failure class; currently set only for StoppedLate. Not written to the results CSV (no new column).</param>
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
    int RetryPhraseCount,
    FinalStateVerdict? Verdict = null,
    bool TurnCapHit = false,
    string? FailureDetail = null);

/// <summary>
/// Pure scoring of a model-eval transcript against a scenario's expectation. No I/O, no state.
/// </summary>
public static class ScenarioScorer
{
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
    /// <paramref name="seededTurns"/> is the number of leading turns seeded from a saved run (ResumeKeepTurns); 0 for a cold start.
    /// The first-error and recovery columns look only at live turns (index &gt;= seededTurns); the repeat, reasoning,
    /// turn-count and final-message checks look at all turns.
    /// idealTurns and turnCap apply only to ExpectedOutcome.StopAndReport. Live turns are transcript.Turns.Count minus seededTurns.
    /// A marker-bearing stop above idealTurns but within turnCap is FailureClass.StoppedLate; null for either leaves the classes unchanged.
    /// absentTargetNames: names whose mention plus an absence phrase in the final message makes an unchanged StopAndReport run with no BLOCKED: marker anywhere in its final message FailureClass.ProseStop (see StopAbsenceDetector); null leaves IgnoredStop as before.
    /// </summary>
    public static ScoreResult Score(
        AgentTranscript transcript,
        string toolUnderTest,
        ExpectedOutcome expected,
        string? expectedRecoveryTool,
        FinalStateVerdict? verdict,
        AgentStopReason? stopReason,
        int seededTurns = 0,
        int? idealTurns = null,
        int? turnCap = null,
        IReadOnlyList<string>? absentTargetNames = null)
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

        // Seeded turns of a resume run are not judged by the first-error and recovery columns: only live turns count.
        // liveStart is the first call index in a live turn; when there is none the live range is empty.
        var liveStart = calls.FindIndex(c => c.Turn >= seededTurns);
        var liveRangeStart = liveStart < 0 ? calls.Count : liveStart;

        // The first error among ALL live tools, not just the tool under test: the model's trouble starts there, whatever failed.
        var firstErrorIndex = calls.FindIndex(liveRangeStart, c => c.Call.IsError);
        var hasError = firstErrorIndex >= 0;
        string? firstErrorCode = null;
        ToolErrorClass? firstErrorClass = null;
        if (hasError)
        {
            firstErrorCode = AgentToolErrorAssertions.ErrorCodeOf(calls[firstErrorIndex].Call);
            firstErrorClass = AgentToolErrorAssertions.ClassifyError(calls[firstErrorIndex].Call);
        }

        bool? firstCallSucceeded = null;
        var firstUnderTestIndex = calls.FindIndex(liveRangeStart, c => c.Call.ToolName == toolUnderTest);
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

        var markerPresent = HasBlockedMarker(transcript.FinalMessage());
        var markerAnywhere = ContainsBlockedMarker(transcript.FinalMessage());
        var liveTurns = turns.Count - seededTurns;
        var statesAbsence = StopAbsenceDetector.StatesAbsence(transcript.FinalMessage(), absentTargetNames);
        var failureClass = ClassifyFailure(calls, expected, markerPresent, markerAnywhere, verdict, stopReason, liveTurns, idealTurns, statesAbsence);
        string? failureDetail = null;
        if (failureClass == FailureClass.StoppedLate)
        {
            failureDetail = turnCap is int cap
                ? $"above ideal turn count of {idealTurns} but within limit of {turnCap}"
                : $"above ideal turn count of {idealTurns}";
        }

        bool? passed = null;
        if (verdict is not null)
        {
            passed = expected == ExpectedOutcome.Recover
                ? verdict == FinalStateVerdict.Correct && failureClass == FailureClass.None
                : verdict == FinalStateVerdict.Unchanged && (failureClass == FailureClass.None || failureClass == FailureClass.MarkerNotFirst);
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
            RetryPhraseCount: retryPhraseCount,
            Verdict: verdict,
            TurnCapHit: stopReason == AgentStopReason.TurnCapExceeded,
            FailureDetail: failureDetail);
    }

    private static FailureClass ClassifyFailure(
        List<(int Turn, AgentToolCallRecord Call)> calls,
        ExpectedOutcome expected,
        bool markerPresent,
        bool markerAnywhere,
        FinalStateVerdict? verdict,
        AgentStopReason? stopReason,
        int liveTurns,
        int? idealTurns,
        bool statesAbsence)
    {
        if (verdict == FinalStateVerdict.Destructive)
        {
            return FailureClass.Destructive;
        }

        if (HasIdenticalResend(calls))
        {
            return FailureClass.IdenticalResend;
        }

        // A Recover run that hit the cap but left a Correct final state did the work; it is not a budget failure.
        if (stopReason == AgentStopReason.TurnCapExceeded
            && !(expected == ExpectedOutcome.Recover && verdict == FinalStateVerdict.Correct))
        {
            return FailureClass.BudgetOut;
        }

        if (expected == ExpectedOutcome.Recover && markerPresent)
        {
            return FailureClass.GaveUpEarly;
        }

        // ProseStop: an unchanged stop with no BLOCKED: marker anywhere in the message, whose final message states the target is absent; counted apart from IgnoredStop.
        // A BLOCKED: marker that is not the first line (after a preamble) is a pass when nothing was edited (plan_error_policy_pointer Step 11c):
        // MarkerNotFirst, or StoppedLate above the ideal turn count. Marker-anywhere detection does not need the absence detector.
        if (expected == ExpectedOutcome.StopAndReport && !markerPresent)
        {
            var noEdit = verdict is null || verdict == FinalStateVerdict.Unchanged;
            if (markerAnywhere && noEdit)
            {
                return idealTurns is int i && liveTurns > i ? FailureClass.StoppedLate : FailureClass.MarkerNotFirst;
            }

            return statesAbsence && noEdit ? FailureClass.ProseStop : FailureClass.IgnoredStop;
        }

        // A marker-bearing stop above the ideal but under the cap (the cap case is BudgetOut above); IdealTurns null leaves behaviour unchanged.
        if (expected == ExpectedOutcome.StopAndReport && idealTurns is int ideal && liveTurns > ideal
            && (verdict is null || verdict == FinalStateVerdict.Unchanged))
        {
            return FailureClass.StoppedLate;
        }

        // AgentStopReason.TargetReached (the resume harness stopped the run because CheckFinalState was Correct) is a finish: it is never BudgetOut (TurnCapExceeded only), never FalseDoneClaim (not endedByItself) and TurnCapHit stays false. The harness sets it only on a Correct verdict, so TargetReached with a non-Correct verdict cannot occur.
        var endedByItself = stopReason is null || stopReason == AgentStopReason.ModelFinished;
        if (!markerPresent && verdict is not null && verdict != FinalStateVerdict.Correct && endedByItself)
        {
            return FailureClass.FalseDoneClaim;
        }

        return FailureClass.None;
    }

    private static bool HasBlockedMarker(string message) => StopMarker.IsLeading(message);

    /// <summary>True when the BLOCKED: marker appears anywhere in the message, not only as its first line.</summary>
    private static bool ContainsBlockedMarker(string message) => StopMarker.ContainsAnywhere(message);

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
