using System.Text.RegularExpressions;
using RoslynSentinel.Common.AgentLoop;

namespace RoslynSentinel.Tests.ErrorVariants;

/// <summary>
/// What the first assistant turn after a cut did, and how the run ended. Built by <see cref="Classify"/>
/// from the turns a run produced after the cut. Pure functions, no state.
/// </summary>
/// <param name="DecisionClass">Class of the first new turn: Malformed, StopLeading, StopMarkerLater, StopProse, NoCallOther, Widen:&lt;tool&gt;, Write:&lt;tool&gt; or Other:&lt;tool&gt;.</param>
/// <param name="FirstTool">Name of the first tool call in the first new turn; null when that turn has no tool calls.</param>
/// <param name="CallCount">Number of tool calls in the first new turn (0 for a no-call turn).</param>
/// <param name="BlockedAnywhere">True when the BLOCKED: marker appears in any new turn's message, not only the first.</param>
/// <param name="StopTurn">TurnNumber of the last new turn.</param>
/// <param name="StopReason">Why the run ended: OffScript:&lt;tool&gt;, the last turn's no-call class, TurnCap, or the AgentStopReason name.</param>
public sealed record NextTurnDecision(
    string DecisionClass,
    string? FirstTool,
    int CallCount,
    bool BlockedAnywhere,
    int? StopTurn,
    string StopReason)
{
    /// <summary>
    /// Classifies the first turn after the cut and describes how the run ended.
    /// </summary>
    /// <param name="newTurns">Turns produced after the cut, in order. The first is classified; the last sets StopTurn and StopReason. Must not be empty.</param>
    /// <param name="absentTargets">Target names the task names (for example a file name). Null or empty disables the StopProse check.</param>
    /// <param name="stopReason">Why the run ended, from the run result.</param>
    /// <param name="offScriptTool">Tool name the fake host went off-script on, or null when the run stayed on script.</param>
    /// <exception cref="ArgumentException"><paramref name="newTurns"/> is empty.</exception>
    public static NextTurnDecision Classify(
        IReadOnlyList<AgentTranscriptTurn> newTurns,
        IReadOnlyList<string>? absentTargets,
        AgentStopReason stopReason,
        string? offScriptTool)
    {
        ArgumentNullException.ThrowIfNull(newTurns);
        if (newTurns.Count == 0)
        {
            throw new ArgumentException(
                "newTurns is empty: the run produced no turn after the cut, so there is no first turn to classify.",
                nameof(newTurns));
        }

        var first = newTurns[0];
        string decisionClass;
        string? firstTool = null;
        if (first.ToolCalls.Count == 0)
        {
            decisionClass = ClassifyNoCallTurn(first, absentTargets);
        }
        else
        {
            firstTool = first.ToolCalls[0].ToolName;
            decisionClass = ToolEffectList.ClassifyCall(firstTool, first.ToolCalls[0].ArgumentsJson) switch
            {
                ToolEffect.Read => "Widen:" + firstTool,
                ToolEffect.Write => "Write:" + firstTool,
                _ => "Other:" + firstTool,
            };
        }

        var last = newTurns[^1];
        string lastStopReason;
        if (offScriptTool is not null)
        {
            lastStopReason = "OffScript:" + offScriptTool;
        }
        else if (last.ToolCalls.Count == 0)
        {
            lastStopReason = ClassifyNoCallTurn(last, absentTargets);
        }
        else if (stopReason is AgentStopReason.TurnCapExceeded or AgentStopReason.TargetReached)
        {
            lastStopReason = "TurnCap";
        }
        else
        {
            lastStopReason = stopReason.ToString();
        }

        return new NextTurnDecision(
            decisionClass,
            firstTool,
            first.ToolCalls.Count,
            newTurns.Any(turn => StopMarker.ContainsAnywhere(MessageText(turn))),
            last.TurnNumber,
            lastStopReason);
    }

    /// <summary>Class of a turn that made no tool call: Malformed, StopLeading, StopMarkerLater, StopProse or NoCallOther.</summary>
    private static string ClassifyNoCallTurn(AgentTranscriptTurn turn, IReadOnlyList<string>? absentTargets)
    {
        var message = MessageText(turn);
        if (string.IsNullOrWhiteSpace(message) || ModelAgentRunner.ContainsToolCallMarkup(message))
        {
            return "Malformed";
        }

        if (StopMarker.IsLeading(message))
        {
            return "StopLeading";
        }

        if (StopMarker.ContainsAnywhere(message))
        {
            return "StopMarkerLater";
        }

        if (AbsenceDetectorCopy.StatesAbsence(message, absentTargets))
        {
            return "StopProse";
        }

        return "NoCallOther";
    }

    // Uses ModelMessage.Content as is. AgentTranscript.FinalMessage() strips think tags and falls back to
    // ReasoningContent, but its WithoutThinkTags helper is private and works on a whole transcript, so it is
    // not reachable per turn here. A reasoning-only turn therefore reads as Malformed.
    private static string MessageText(AgentTranscriptTurn turn) => turn.ModelMessage.Content ?? "";

    /// <summary>
    /// Deliberate copy of RoslynSentinel.Tests.ModelEval/StopAbsenceDetector.cs (same regex and logic).
    /// This project may not reference ModelEval, so the StopProse check cannot call it.
    /// </summary>
    private static class AbsenceDetectorCopy
    {
        // Apostrophes: ASCII ' or U+2019, written as ’ so this file stays ASCII.
        private static readonly Regex AbsencePhraseRegex = new(
            @"does(?:\s+not|n['’]t)\s+(?:appear\s+to\s+)?exist"
            + @"|not\s+found"
            + @"|no\s+such\s+(?:file|method|symbol)"
            + @"|(?:cannot|can['’]t|could\s+not|couldn['’]t|unable\s+to)\s+(?:be\s+)?(?:find|found|locate|located)"
            + @"|(?:is\s+not|isn['’]t)\s+(?:present|there)"
            + @"|is\s+missing"
            + @"|no\s+(?:matching|similar)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>True when the message names at least one target (case-insensitive substring) and states it is absent.</summary>
        public static bool StatesAbsence(string? finalMessage, IReadOnlyList<string>? targetNames)
        {
            // TODO unify with ModelEval StopAbsenceDetector
            if (string.IsNullOrWhiteSpace(finalMessage) || targetNames is null || targetNames.Count == 0)
            {
                return false;
            }

            var namesTarget = targetNames.Any(name =>
                !string.IsNullOrWhiteSpace(name) && finalMessage.Contains(name, StringComparison.OrdinalIgnoreCase));
            return namesTarget && AbsencePhraseRegex.IsMatch(finalMessage);
        }
    }
}
