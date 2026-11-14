namespace RoslynSentinel.Common;

/// <summary>
/// Decides which assistant turns replay their reasoning on the next request. Block mode replays a fixed
/// window of recent turns. Pressure mode replays everything until the projected prompt reaches
/// <see cref="PressureFraction"/> of the context; then it drops the oldest half of the unpinned turns.
/// The first <see cref="PinnedTurns"/> turns are never dropped. One instance per run.
/// </summary>
public sealed class ReasoningReplayPolicy
{
    /// <summary>Number of leading assistant turns that are never dropped in pressure mode.</summary>
    public const int PinnedTurns = 3;

    /// <summary>Fraction of the context length at which pressure mode drops turns.</summary>
    public const double PressureFraction = 0.7;

    /// <summary>
    /// Creates a policy for a model with the given context length. A non-null fixed window selects block mode;
    /// null selects pressure mode.
    /// </summary>
    /// <param name="contextLength">Loaded context length of the model, in tokens.</param>
    /// <param name="fixedWindow">Number of recent turns to replay in block mode, or null for pressure mode.</param>
    public ReasoningReplayPolicy(int contextLength, int? fixedWindow)
    {
        ContextLength = contextLength;
        if ((fixedWindow is null or < 0) && contextLength <= 0)
        {
            FixedWindow = LmStudioAgentClient.ReasoningReplayTurns;
            Mode = $"window:{FixedWindow} (context length unknown)";
        }
        else if (fixedWindow == 0)
        {
            FixedWindow = 0;
            Mode = "all";
        }
        else if (fixedWindow > 0)
        {
            FixedWindow = fixedWindow;
            Mode = $"window:{fixedWindow}";
        }
        else
        {
            FixedWindow = null;
            Mode = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"pressure:{PressureFraction},pinned:{PinnedTurns}");
        }
    }

    /// <summary>
    /// Builds the policy from the environment: a block window override selects block mode, otherwise pressure mode.
    /// </summary>
    /// <param name="contextLength">Loaded context length of the model, in tokens.</param>
    /// <returns>The policy for this run.</returns>
    public static ReasoningReplayPolicy FromEnvironment(int contextLength)
    {
        return new ReasoningReplayPolicy(contextLength, LmStudioAgentClient.ParseReasoningWindowOverride(Environment.GetEnvironmentVariable(LmStudioAgentClient.ReasoningWindowVariable)));
    }

    /// <summary>Context length of the model, in tokens, that the policy was created for.</summary>
    public int ContextLength { get; }

    /// <summary>Effective block window in turns; null only in pressure mode.</summary>
    public int? FixedWindow { get; }

    /// <summary>Number of assistant turns dropped so far in pressure mode.</summary>
    public int DroppedTurns { get; private set; }

    /// <summary>Mode text recorded in the transcript, for example "pressure:0.7,pinned:3", "all" or "window:10".</summary>
    public string Mode { get; } = "";

    /// <summary>
    /// Observes the projected prompt size for the next request and drops turns when pressure mode requires it.
    /// </summary>
    /// <param name="assistantTurns">Number of non-note assistant turns so far.</param>
    /// <param name="projectedPromptTokens">Projected prompt size of the next request, in tokens.</param>
    /// <returns>True when this call dropped turns.</returns>
    public bool Observe(int assistantTurns, int projectedPromptTokens)
    {
        if (FixedWindow is not null)
        {
            return false;
        }

        if (projectedPromptTokens >= ContextLength * PressureFraction)
        {
            // The dropped set only grows, so the prompt prefix stays stable between drops.
            var replayable = assistantTurns - PinnedTurns - DroppedTurns;
            if (replayable >= 2)
            {
                DroppedTurns += replayable / 2;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Reports whether the assistant turn at the given index replays its reasoning on the next request.
    /// </summary>
    /// <param name="assistantTurnIndex">0-based index among non-note assistant turns.</param>
    /// <param name="assistantTurns">Number of non-note assistant turns so far.</param>
    /// <returns>True when the turn's reasoning is replayed.</returns>
    public bool ReplaysReasoning(int assistantTurnIndex, int assistantTurns)
    {
        if (FixedWindow is null)
        {
            return assistantTurnIndex < PinnedTurns || assistantTurnIndex >= PinnedTurns + DroppedTurns;
        }

        if (FixedWindow == 0)
        {
            return true;
        }

        return assistantTurnIndex >= LmStudioAgentClient.ReasoningDroppedTurns(assistantTurns, FixedWindow.Value);
    }
}
