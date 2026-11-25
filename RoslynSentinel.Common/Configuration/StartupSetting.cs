using System.Globalization;

namespace RoslynSentinel.Common;

/// <summary>Which hosts a setting applies to. Flags: a setting may apply to several.</summary>
[Flags]
public enum SettingScope
{
    None = 0,
    Server = 1,
    ServerStdio = 2,
    PlanStepRunner = 4,
    AgentLoop = 8,
    TestHarness = 16
}

/// <summary>Where a setting's current value came from.</summary>
public enum SettingSource
{
    Default,
    Flag,
    Env,
    Invalid
}

/// <summary>Live and External: the env var is re-read on every Value access.</summary>
public enum SettingKind
{
    Startup,
    Live,
    External
}

public enum ConfigurationProblemKind
{
    InvalidValue,
    MissingValue,
    UnknownFlag,
    PostConfigure
}

/// <summary>Marks a class whose public static readonly <see cref="StartupSetting"/> fields form one setting group.</summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class StartupSettingGroupAttribute(string title) : Attribute
{
    public string Title { get; } = title;

    /// <summary>Name of a <c>static IReadOnlyList&lt;ConfigurationProblem&gt; X()</c> on the group class.</summary>
    public string? PostConfigure { get; set; }
}

public delegate bool TryParseSetting<T>(string raw, out T value);

/// <summary>Command-line tokens handed to the registry.</summary>
public sealed class StartupArgs
{
    private static readonly StartupArgs EmptyInstance = new(Array.Empty<string>());

    private StartupArgs(IReadOnlyList<string> tokens)
    {
        Tokens = tokens;
    }

    public static StartupArgs Empty => EmptyInstance;

    /// <summary>Snapshots the tokens (null tokens become empty strings). An empty or null list yields <see cref="Empty"/>.</summary>
    public static StartupArgs Parse(IReadOnlyList<string> args)
    {
        if (args is null || args.Count == 0)
        {
            return EmptyInstance;
        }

        var copy = new string[args.Count];
        for (var i = 0; i < copy.Length; i++)
        {
            copy[i] = args[i] ?? string.Empty;
        }

        return new StartupArgs(copy);
    }

    public IReadOnlyList<string> Tokens { get; }
}

public sealed record SettingRead<T>(T Value, SettingSource Source, ConfigurationProblem? Problem);

/// <summary>Type-erased base of a declared startup setting.</summary>
public abstract class StartupSetting
{
    private protected const string BoolExpected = "true|false|1|0|on|off|yes|no";

    private protected const string NoneText = "(none)";

    private protected StartupSetting(string? flag, string? env, string description, SettingScope scope, SettingKind kind)
    {
        Flag = flag;
        EnvName = env;
        Description = description;
        Scope = scope;
        Kind = kind;
    }

    /// <summary>"&lt;Class&gt;.&lt;Field&gt;" with a trailing "Setting" removed; set by Discover.</summary>
    public string Name { get; internal set; } = string.Empty;

    /// <summary>"--kebab"; null = env-only.</summary>
    public string? Flag { get; }

    /// <summary>Null = flag-only.</summary>
    public string? EnvName { get; }

    public string Description { get; }

    public SettingScope Scope { get; }

    public SettingKind Kind { get; }

    public abstract Type ValueType { get; }

    public abstract bool IsBool { get; }

    public abstract string DefaultText { get; }

    /// <summary>External kind: "set" or "unset", never the value.</summary>
    public abstract string EffectiveText { get; }

    public abstract SettingSource Source { get; }

    /// <summary>Stores value and source.</summary>
    internal abstract ConfigurationProblem? Apply(StartupArgs args, Func<string, string?> getEnv);

    internal abstract void Reset();

    /// <summary>Bool literals: true/false/1/0/on/off/yes/no, case-insensitive. The caller trims.</summary>
    private protected static bool TryParseBoolLiteral(string raw, out bool value)
    {
        switch (raw.ToLowerInvariant())
        {
            case "true":
            case "1":
            case "on":
            case "yes":
                value = true;
                return true;
            case "false":
            case "0":
            case "off":
            case "no":
                value = false;
                return true;
            default:
                value = false;
                return false;
        }
    }

    private static void RequireFlagOrEnv(string? flag, string? env, string description)
    {
        if (flag is null && env is null)
        {
            throw new StartupSettingDeclarationException(
                $"A startup setting needs a flag, an env name or both; both were null (description: '{description}').");
        }
    }

    // Factories: flag and env may each be null, not both; a violation throws StartupSettingDeclarationException.

    public static StartupSetting<bool> Bool(string? flag, string? env, bool defaultValue, string description, SettingScope scope, SettingKind kind = SettingKind.Startup)
    {
        RequireFlagOrEnv(flag, env, description);
        return new StartupSetting<bool>(flag, env, defaultValue, defaultValue ? "true" : "false", TryParseBoolLiteral, BoolExpected, description, scope, kind);
    }

    public static StartupSetting<int> Int(string? flag, string? env, int defaultValue, string description, SettingScope scope, SettingKind kind = SettingKind.Startup, int? minValue = null)
    {
        RequireFlagOrEnv(flag, env, description);
        TryParseSetting<int> parse = (string raw, out int value) =>
            int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out value) && (minValue is null || value >= minValue.Value);
        return new StartupSetting<int>(flag, env, defaultValue, defaultValue.ToString(CultureInfo.InvariantCulture), parse, IntExpected(minValue), description, scope, kind);
    }

    public static StartupSetting<int?> NullableInt(string? flag, string? env, string description, SettingScope scope, SettingKind kind = SettingKind.Startup, int? minValue = null)
    {
        RequireFlagOrEnv(flag, env, description);
        TryParseSetting<int?> parse = (string raw, out int? value) =>
        {
            if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) && (minValue is null || number >= minValue.Value))
            {
                value = number;
                return true;
            }

            value = null;
            return false;
        };
        return new StartupSetting<int?>(flag, env, null, NoneText, parse, IntExpected(minValue), description, scope, kind);
    }

    public static StartupSetting<double> Double(string? flag, string? env, double defaultValue, string description, SettingScope scope, SettingKind kind = SettingKind.Startup)
    {
        RequireFlagOrEnv(flag, env, description);
        TryParseSetting<double> parse = (string raw, out double value) =>
            double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        return new StartupSetting<double>(flag, env, defaultValue, defaultValue.ToString(CultureInfo.InvariantCulture), parse, "number", description, scope, kind);
    }

    public static StartupSetting<double?> NullableDouble(string? flag, string? env, string description, SettingScope scope, SettingKind kind = SettingKind.Startup)
    {
        RequireFlagOrEnv(flag, env, description);
        TryParseSetting<double?> parse = (string raw, out double? value) =>
        {
            if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
            {
                value = number;
                return true;
            }

            value = null;
            return false;
        };
        return new StartupSetting<double?>(flag, env, null, NoneText, parse, "number", description, scope, kind);
    }

    public static StartupSetting<string?> String(string? flag, string? env, string? defaultValue, string description, SettingScope scope, SettingKind kind = SettingKind.Startup)
    {
        RequireFlagOrEnv(flag, env, description);
        TryParseSetting<string?> parse = (string raw, out string? value) =>
        {
            value = raw;
            return true;
        };
        return new StartupSetting<string?>(flag, env, defaultValue, defaultValue ?? NoneText, parse, "string", description, scope, kind);
    }

    public static StartupSetting<TEnum> Enum<TEnum>(string? flag, string? env, TEnum defaultValue, string description, SettingScope scope, SettingKind kind = SettingKind.Startup) where TEnum : struct, System.Enum
    {
        RequireFlagOrEnv(flag, env, description);
        var names = System.Enum.GetNames<TEnum>();
        TryParseSetting<TEnum> parse = (string raw, out TEnum value) =>
        {
            var wanted = NormalizeEnumName(raw);
            foreach (var name in names)
            {
                if (string.Equals(NormalizeEnumName(name), wanted, StringComparison.OrdinalIgnoreCase))
                {
                    value = System.Enum.Parse<TEnum>(name);
                    return true;
                }
            }

            value = default;
            return false;
        };
        return new StartupSetting<TEnum>(flag, env, defaultValue, defaultValue.ToString(), parse, string.Join("|", names), description, scope, kind);
    }

    public static StartupSetting<T> Custom<T>(string? flag, string? env, T defaultValue, string defaultText, TryParseSetting<T> parse, string expected, string description, SettingScope scope, SettingKind kind = SettingKind.Startup)
    {
        RequireFlagOrEnv(flag, env, description);
        ArgumentNullException.ThrowIfNull(parse);
        return new StartupSetting<T>(flag, env, defaultValue, defaultText, parse, expected, description, scope, kind);
    }

    private static string IntExpected(int? minValue)
        => minValue is null ? "integer" : $"integer >= {minValue.Value.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>Enum names match case-insensitively ignoring '-' and '_'.</summary>
    private static string NormalizeEnumName(string text)
        => text.Replace("-", string.Empty, StringComparison.Ordinal).Replace("_", string.Empty, StringComparison.Ordinal);
}

/// <summary>A declared setting of value type <typeparamref name="T"/>.</summary>
public sealed class StartupSetting<T> : StartupSetting
{
    private readonly TryParseSetting<T> _parse;
    private readonly string _expected;

    // Last Apply result; null = never applied (or Reset). Held as one reference so a reader never sees value and source torn apart.
    private SettingRead<T>? _applied;

    internal StartupSetting(string? flag, string? env, T defaultValue, string defaultText, TryParseSetting<T> parse, string expected, string description, SettingScope scope, SettingKind kind)
        : base(flag, env, description, scope, kind)
    {
        DefaultValue = defaultValue;
        DefaultText = defaultText;
        _parse = parse;
        _expected = expected;
    }

    public T DefaultValue { get; }

    /// <summary>Startup: last applied value (default before Apply). Live/External: Read(StartupArgs.Empty, StartupSettings.EnvironmentReader).Value unless a flag was applied.</summary>
    public T Value => Current().Value;

    /// <summary>Stateless.</summary>
    public SettingRead<T> Read(StartupArgs args, Func<string, string?> getEnv)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(getEnv);

        if (Flag is not null)
        {
            var tokens = args.Tokens;
            var found = false;
            string? flagRaw = null;
            for (var i = 0; i < tokens.Count; i++)
            {
                var token = tokens[i];
                string? raw;
                if (string.Equals(token, Flag, StringComparison.Ordinal))
                {
                    var next = i + 1 < tokens.Count ? tokens[i + 1] : null;
                    if (IsBool)
                    {
                        // A bare bool flag is true; the next token is consumed only when it is a bool literal.
                        if (next is not null && TryParseBoolLiteral(next.Trim(), out _))
                        {
                            raw = next;
                            i++;
                        }
                        else
                        {
                            raw = "true";
                        }
                    }
                    else if (next is not null && !next.StartsWith("--", StringComparison.Ordinal))
                    {
                        raw = next;
                        i++;
                    }
                    else
                    {
                        raw = null; // bare value flag: MissingValue
                    }
                }
                else if (token.StartsWith(Flag + "=", StringComparison.Ordinal))
                {
                    raw = token[(Flag.Length + 1)..];
                }
                else
                {
                    continue;
                }

                found = true;
                flagRaw = raw; // repeated flag: last wins
            }

            if (found)
            {
                if (flagRaw is null)
                {
                    return new SettingRead<T>(DefaultValue, SettingSource.Invalid, NewProblem(ConfigurationProblemKind.MissingValue, null));
                }

                var trimmed = flagRaw.Trim();
                if (trimmed.Length > 0)
                {
                    return Resolve(trimmed, SettingSource.Flag);
                }

                // An empty or whitespace flag value is unset: fall through to env.
            }
        }

        if (EnvName is not null)
        {
            var envRaw = getEnv(EnvName)?.Trim();
            if (!string.IsNullOrEmpty(envRaw))
            {
                return Resolve(envRaw, SettingSource.Env);
            }
        }

        return new SettingRead<T>(DefaultValue, SettingSource.Default, null);
    }

    public override Type ValueType => typeof(T);

    public override bool IsBool => typeof(T) == typeof(bool);

    public override string DefaultText { get; }

    public override string EffectiveText
    {
        get
        {
            var current = Current();
            if (Kind == SettingKind.External)
            {
                return current.Source == SettingSource.Default ? "unset" : "set";
            }

            return current.Source is SettingSource.Default or SettingSource.Invalid
                ? DefaultText
                : FormatValue(current.Value);
        }
    }

    public override SettingSource Source => Current().Source;

    internal override ConfigurationProblem? Apply(StartupArgs args, Func<string, string?> getEnv)
    {
        var read = Read(args, getEnv);
        _applied = read;
        return read.Problem;
    }

    internal override void Reset() => _applied = null;

    private SettingRead<T> Current()
    {
        var applied = _applied;
        if (Kind == SettingKind.Startup)
        {
            return applied ?? new SettingRead<T>(DefaultValue, SettingSource.Default, null);
        }

        // Live and External re-read env on every access unless a flag was applied.
        if (applied is { Source: SettingSource.Flag })
        {
            return applied;
        }

        return Read(StartupArgs.Empty, StartupSettings.EnvironmentReader);
    }

    private SettingRead<T> Resolve(string raw, SettingSource source)
        => _parse(raw, out var value)
            ? new SettingRead<T>(value, source, null)
            : new SettingRead<T>(DefaultValue, SettingSource.Invalid, NewProblem(ConfigurationProblemKind.InvalidValue, raw));

    private ConfigurationProblem NewProblem(ConfigurationProblemKind kind, string? rawValue)
        => new(kind, Name, Flag, EnvName, rawValue, _expected);

    private static string FormatValue(T value) => value switch
    {
        null => NoneText,
        bool flag => flag ? "true" : "false",
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty
    };
}
