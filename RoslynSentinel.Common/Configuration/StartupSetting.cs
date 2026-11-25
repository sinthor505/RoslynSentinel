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
    public static StartupArgs Empty => throw new NotImplementedException(); // TODO implement (Step 3)

    public static StartupArgs Parse(IReadOnlyList<string> args) => throw new NotImplementedException(); // TODO implement (Step 3)

    public IReadOnlyList<string> Tokens => throw new NotImplementedException(); // TODO implement (Step 3)
}

public sealed record SettingRead<T>(T Value, SettingSource Source, ConfigurationProblem? Problem);

/// <summary>Type-erased base of a declared startup setting.</summary>
public abstract class StartupSetting
{
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

    // Factories: flag and env may each be null, not both; a violation throws StartupSettingDeclarationException.

    public static StartupSetting<bool> Bool(string? flag, string? env, bool defaultValue, string description, SettingScope scope, SettingKind kind = SettingKind.Startup)
        => throw new NotImplementedException(); // TODO implement (Step 3)

    public static StartupSetting<int> Int(string? flag, string? env, int defaultValue, string description, SettingScope scope, SettingKind kind = SettingKind.Startup, int? minValue = null)
        => throw new NotImplementedException(); // TODO implement (Step 3)

    public static StartupSetting<int?> NullableInt(string? flag, string? env, string description, SettingScope scope, SettingKind kind = SettingKind.Startup, int? minValue = null)
        => throw new NotImplementedException(); // TODO implement (Step 3)

    public static StartupSetting<double> Double(string? flag, string? env, double defaultValue, string description, SettingScope scope, SettingKind kind = SettingKind.Startup)
        => throw new NotImplementedException(); // TODO implement (Step 3)

    public static StartupSetting<double?> NullableDouble(string? flag, string? env, string description, SettingScope scope, SettingKind kind = SettingKind.Startup)
        => throw new NotImplementedException(); // TODO implement (Step 3)

    public static StartupSetting<string?> String(string? flag, string? env, string? defaultValue, string description, SettingScope scope, SettingKind kind = SettingKind.Startup)
        => throw new NotImplementedException(); // TODO implement (Step 3)

    public static StartupSetting<TEnum> Enum<TEnum>(string? flag, string? env, TEnum defaultValue, string description, SettingScope scope, SettingKind kind = SettingKind.Startup) where TEnum : struct, System.Enum
        => throw new NotImplementedException(); // TODO implement (Step 3)

    public static StartupSetting<T> Custom<T>(string? flag, string? env, T defaultValue, string defaultText, TryParseSetting<T> parse, string expected, string description, SettingScope scope, SettingKind kind = SettingKind.Startup)
        => throw new NotImplementedException(); // TODO implement (Step 3)
}

/// <summary>A declared setting of value type <typeparamref name="T"/>.</summary>
public sealed class StartupSetting<T> : StartupSetting
{
    internal StartupSetting(string? flag, string? env, T defaultValue, string defaultText, TryParseSetting<T> parse, string expected, string description, SettingScope scope, SettingKind kind)
        : base(flag, env, description, scope, kind)
    {
        // TODO implement (Step 3): store defaultText, parse and expected; only defaultValue is stored in the skeleton.
        DefaultValue = defaultValue;
    }

    public T DefaultValue { get; }

    /// <summary>Startup: last applied value (default before Apply). Live/External: Read(StartupArgs.Empty, StartupSettings.EnvironmentReader).Value unless a flag was applied.</summary>
    public T Value => throw new NotImplementedException(); // TODO implement (Step 3)

    /// <summary>Stateless.</summary>
    public SettingRead<T> Read(StartupArgs args, Func<string, string?> getEnv) => throw new NotImplementedException(); // TODO implement (Step 3)

    public override Type ValueType => throw new NotImplementedException(); // TODO implement (Step 3)

    public override bool IsBool => throw new NotImplementedException(); // TODO implement (Step 3)

    public override string DefaultText => throw new NotImplementedException(); // TODO implement (Step 3)

    public override string EffectiveText => throw new NotImplementedException(); // TODO implement (Step 3)

    public override SettingSource Source => throw new NotImplementedException(); // TODO implement (Step 3)

    internal override ConfigurationProblem? Apply(StartupArgs args, Func<string, string?> getEnv) => throw new NotImplementedException(); // TODO implement (Step 3)

    internal override void Reset() => throw new NotImplementedException(); // TODO implement (Step 3)
}
