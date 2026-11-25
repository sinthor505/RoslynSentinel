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

[StartupSettingGroup("Thinking (ModelEval)")]
public sealed record ThinkingOptions(ThinkingMode Mode, ThinkingTriggers Triggers, string Effort, int? ReasoningBudget)
{
    public const string ModeVariable = "ROSLYNSENTINEL_MODELEVAL_THINKING";
    public const string TriggersVariable = "ROSLYNSENTINEL_MODELEVAL_THINKING_TRIGGERS";
    public const string EffortVariable = "ROSLYNSENTINEL_MODELEVAL_THINKING_EFFORT";
    public const string BudgetVariable = "ROSLYNSENTINEL_MODELEVAL_REASONING_BUDGET";
    public const string DefaultEffort = "low";
    public const string CustomEffortWarningFormat = "Warning: Custom thinking_effort value {0} was specified - ensure it is valid for the active model {1}";

    /// <summary>Thinking mode (<see cref="ModeVariable"/>), re-read on every access.</summary>
    public static readonly StartupSetting<ThinkingMode> ModeSetting = StartupSetting.Enum<ThinkingMode>(null, ModeVariable, ThinkingMode.Off, "Thinking mode for model-eval requests: off, triggered or always.", SettingScope.AgentLoop, SettingKind.Live);

    /// <summary>Trigger conditions for triggered mode (<see cref="TriggersVariable"/>), re-read on every access.</summary>
    public static readonly StartupSetting<ThinkingTriggers> TriggersSetting = StartupSetting.Custom<ThinkingTriggers>(
        null,
        TriggersVariable,
        ThinkingTriggers.Error,
        "error",
        (string raw, out ThinkingTriggers value) =>
        {
            var triggerNames = Array.FindAll(Enum.GetNames<ThinkingTriggers>(), n => n != nameof(ThinkingTriggers.None));
            var combined = ThinkingTriggers.None;
            foreach (var part in raw.Split(','))
            {
                var token = part.Trim();
                var triggerName = Array.Find(triggerNames, n => string.Equals(n, token, StringComparison.OrdinalIgnoreCase));
                if (triggerName is null)
                {
                    value = default;
                    return false;
                }

                combined |= Enum.Parse<ThinkingTriggers>(triggerName);
            }

            value = combined;
            return true;
        },
        "error,latchRefusal,firstTurn (comma list)",
        "Comma list of the conditions that request thinking in triggered mode.",
        SettingScope.AgentLoop,
        SettingKind.Live);

    /// <summary>Thinking effort (<see cref="EffortVariable"/>), re-read on every access; blank is unset.</summary>
    public static readonly StartupSetting<string?> EffortSetting = StartupSetting.String(null, EffortVariable, DefaultEffort, "Thinking effort sent with thinking requests; any non-blank text (known: none, minimal, low, medium, high, xhigh).", SettingScope.AgentLoop, SettingKind.Live);

    /// <summary>Reasoning token budget (<see cref="BudgetVariable"/>), re-read on every access; null when unset.</summary>
    public static readonly StartupSetting<int?> BudgetSetting = StartupSetting.NullableInt(null, BudgetVariable, "Reasoning token budget per turn (positive integer); unset means none.", SettingScope.AgentLoop, SettingKind.Live, minValue: 1);

    public static IReadOnlyList<string> KnownEfforts { get; } = ["none", "minimal", "low", "medium", "high", "xhigh"];

    public static ThinkingOptions Off { get; } = new(ThinkingMode.Off, ThinkingTriggers.Error, DefaultEffort, null);

    public static ThinkingOptions FromEnvironment(Func<string, string?>? readVariable = null)
    {
        readVariable ??= StartupSettings.EnvironmentReader;
        var mode = ModeSetting.Read(StartupArgs.Empty, readVariable);
        var triggers = TriggersSetting.Read(StartupArgs.Empty, readVariable);
        var effort = EffortSetting.Read(StartupArgs.Empty, readVariable);
        var budget = BudgetSetting.Read(StartupArgs.Empty, readVariable);

        var problems = new List<ConfigurationProblem>();
        foreach (var problem in new[] { mode.Problem, triggers.Problem, effort.Problem, budget.Problem })
        {
            if (problem is not null)
            {
                problems.Add(problem);
            }
        }

        if (problems.Count > 0)
        {
            throw new ConfigurationInvalidException(problems);
        }

        if (mode.Source == SettingSource.Default && triggers.Source == SettingSource.Default
            && effort.Source == SettingSource.Default && budget.Source == SettingSource.Default)
        {
            return Off;
        }

        return new ThinkingOptions(mode.Value, triggers.Value, effort.Value ?? DefaultEffort, budget.Value);
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
