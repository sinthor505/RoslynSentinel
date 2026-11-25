namespace RoslynSentinel.Common;

/// <summary>Mode of the wire result marshaller hook (plan_wire_result_marshaller.md).</summary>
public enum ResultMarshallerMode
{
    /// <summary>Tools are built by the SDK's own <c>McpServerTool.Create(MethodInfo, ...)</c>; no hook. Opt-in escape hatch.</summary>
    Off,

    /// <summary>Tools are built through <see cref="SentinelToolFactory"/>; results are not changed.</summary>
    Passthrough,

    /// <summary>Tools are built through <see cref="SentinelToolFactory"/> and the marshaller owns the wire result. Default.</summary>
    On,
}

/// <summary>
/// Switch for the wire result marshaller. Environment variable only: the value is read on every access so tests can
/// flip it at runtime. Default is <see cref="ResultMarshallerMode.On"/> (plan_wire_result_marshaller.md Step 6); <c>off</c> and
/// <c>passthrough</c> stay selectable as escape hatches.
/// </summary>
[StartupSettingGroup("Result marshaller")]
public static class ResultMarshallerOptions
{
    /// <summary>Name of the environment variable that controls the feature.</summary>
    public const string EnvVarName = "ROSLYNSENTINEL_RESULT_MARSHALLER";

    public static readonly StartupSetting<ResultMarshallerMode> ModeSetting = StartupSetting.Enum<ResultMarshallerMode>(null, EnvVarName, ResultMarshallerMode.On, "Wire result marshaller mode: on (default), passthrough or off.", SettingScope.Server, SettingKind.Live);

    /// <summary>The mode named by <see cref="EnvVarName"/>, re-read from the environment on each access.</summary>
    public static ResultMarshallerMode Mode => ModeSetting.Value;

    /// <summary>
    /// <c>off</c>, <c>passthrough</c> or <c>on</c> (case-insensitive, surrounding whitespace ignored) select that mode;
    /// unset, empty and any unrecognized value select <see cref="ResultMarshallerMode.On"/> (a typo must not silently
    /// disable offloading, so the default wins over the unknown value). An unrecognized value is also reported as an
    /// invalid setting by the registry.
    /// </summary>
    public static ResultMarshallerMode Parse(string? raw)
    {
        return ModeSetting.Read(StartupArgs.Empty, _ => raw).Value;
    }
}
