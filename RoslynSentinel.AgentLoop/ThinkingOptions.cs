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
        throw new NotImplementedException(); // TODO implement (Step 2)
    }

    [JsonIgnore]
    public bool IsKnownEffort
    {
        get
        {
            throw new NotImplementedException(); // TODO implement (Step 2)
        }
    }

    /// <summary>signals = the trigger conditions that hold for this request.</summary>
    public TurnThinking Decide(ThinkingTriggers signals)
    {
        throw new NotImplementedException(); // TODO implement (Step 2)
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
