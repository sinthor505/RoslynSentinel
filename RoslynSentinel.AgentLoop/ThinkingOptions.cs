using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace RoslynSentinel.Common;

// Intentional deviation: the other transcript enums (for example AgentStopReason) serialize as numbers.
// These values are read by humans and by Get-ThinkingArmTable.ps1, so they serialize as names.
[JsonConverter(typeof(JsonStringEnumConverter<ThinkingMode>))]
public enum ThinkingMode
{
    Off,
    Triggered,
    Always,
}

// Intentional deviation: serialized as names, see ThinkingMode.
[Flags]
[JsonConverter(typeof(JsonStringEnumConverter<ThinkingTriggers>))]
public enum ThinkingTriggers
{
    None = 0,
    Error = 1,
    LatchRefusal = 2,
    FirstTurn = 4,
}

public sealed record ThinkingOptions(ThinkingMode Mode, ThinkingTriggers Triggers, string Effort, int? ReasoningBudget)
{
    public const string ModeVariable = "ROSLYNSENTINEL_MODELEVAL_THINKING";
    public const string TriggersVariable = "ROSLYNSENTINEL_MODELEVAL_THINKING_TRIGGERS";
    public const string EffortVariable = "ROSLYNSENTINEL_MODELEVAL_THINKING_EFFORT";
    public const string BudgetVariable = "ROSLYNSENTINEL_MODELEVAL_REASONING_BUDGET";
    public const string DefaultEffort = "low";
    public const string CustomEffortWarningFormat = "Warning: Custom thinking_effort value {0} was specified - ensure it is valid for the active model {1}";

    public static IReadOnlyList<string> KnownEfforts { get; } = ["none", "minimal", "low", "medium", "high", "xhigh"];

    public static ThinkingOptions Off { get; } = new(ThinkingMode.Off, ThinkingTriggers.Error, DefaultEffort, null);

    public static ThinkingOptions FromEnvironment(Func<string, string?>? readVariable = null)
    {
        readVariable ??= name => Environment.GetEnvironmentVariable(name);
        var rawMode = readVariable(ModeVariable);
        var rawTriggers = readVariable(TriggersVariable);
        var rawEffort = readVariable(EffortVariable);
        var rawBudget = readVariable(BudgetVariable);
        if (string.IsNullOrWhiteSpace(rawMode) && string.IsNullOrWhiteSpace(rawTriggers)
            && rawEffort is null && string.IsNullOrWhiteSpace(rawBudget))
        {
            return Off;
        }

        var modeNames = Enum.GetNames<ThinkingMode>();
        var mode = ThinkingMode.Off;
        if (!string.IsNullOrWhiteSpace(rawMode))
        {
            var modeName = MatchName(modeNames, rawMode.Trim());
            if (modeName is null)
            {
                throw new InvalidThinkingSettingException(ModeVariable, rawMode, modeNames);
            }

            mode = Enum.Parse<ThinkingMode>(modeName);
        }

        var triggerNames = Array.FindAll(Enum.GetNames<ThinkingTriggers>(), n => n != nameof(ThinkingTriggers.None));
        var triggers = Off.Triggers;
        if (!string.IsNullOrWhiteSpace(rawTriggers))
        {
            triggers = ThinkingTriggers.None;
            foreach (var part in rawTriggers.Split(','))
            {
                var triggerName = MatchName(triggerNames, part.Trim());
                if (triggerName is null)
                {
                    throw new InvalidThinkingSettingException(TriggersVariable, rawTriggers, triggerNames);
                }

                triggers |= Enum.Parse<ThinkingTriggers>(triggerName);
            }
        }

        var effort = Off.Effort;
        if (rawEffort is not null)
        {
            if (string.IsNullOrWhiteSpace(rawEffort))
            {
                throw new InvalidThinkingSettingException(EffortVariable, rawEffort, KnownEfforts);
            }

            effort = rawEffort;
        }

        int? budget = null;
        if (!string.IsNullOrWhiteSpace(rawBudget))
        {
            if (!int.TryParse(rawBudget.Trim(), out var parsedBudget) || parsedBudget <= 0)
            {
                throw new InvalidThinkingSettingException(BudgetVariable, rawBudget, ["a positive integer"]);
            }

            budget = parsedBudget;
        }

        return new ThinkingOptions(mode, triggers, effort, budget);

        static string? MatchName(string[] names, string token) =>
            Array.Find(names, n => string.Equals(n, token, StringComparison.OrdinalIgnoreCase));
    }

    [JsonIgnore]
    public bool IsKnownEffort
    {
        get
        {
            foreach (var known in KnownEfforts)
            {
                if (string.Equals(known, Effort, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>signals = the trigger conditions that hold for this request.</summary>
    public TurnThinking Decide(ThinkingTriggers signals)
    {
        if (Mode == ThinkingMode.Always)
        {
            return new TurnThinking(true, ThinkingTriggers.None, null, null);
        }

        if (Mode == ThinkingMode.Triggered)
        {
            var fired = Triggers & signals;
            return new TurnThinking(fired != ThinkingTriggers.None, fired, null, null);
        }

        return new TurnThinking(false, ThinkingTriggers.None, null, null);
    }
}

public sealed record TurnThinking(bool Requested, ThinkingTriggers FiredTriggers, int? ReasoningDeltas, bool? OverBudget);

// DTO-EXEMPT: exception
public sealed class InvalidThinkingSettingException(string variable, string value, IReadOnlyList<string> allowed)
    : Exception($"Invalid value '{value}' for {variable}; allowed values: {string.Join(", ", allowed)}.")
{
    public string Variable { get; } = variable;

    public string Value { get; } = value;

    public IReadOnlyList<string> Allowed { get; } = allowed;
}
