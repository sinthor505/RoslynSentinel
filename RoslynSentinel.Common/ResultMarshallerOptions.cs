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
public static class ResultMarshallerOptions
{
    /// <summary>Name of the environment variable that controls the feature.</summary>
    public const string EnvVarName = "ROSLYNSENTINEL_RESULT_MARSHALLER";

    /// <summary>The mode named by <see cref="EnvVarName"/>, re-read from the environment on each access.</summary>
    public static ResultMarshallerMode Mode => Parse(Environment.GetEnvironmentVariable(EnvVarName));

    /// <summary>
    /// <c>off</c>, <c>passthrough</c> or <c>on</c> (case-insensitive, surrounding whitespace ignored) select that mode;
    /// unset, empty and any unrecognized value select <see cref="ResultMarshallerMode.On"/> (a typo must not silently
    /// disable offloading, so the default wins over the unknown value).
    /// </summary>
    public static ResultMarshallerMode Parse(string? raw)
    {
        var value = raw?.Trim();
        if (string.IsNullOrEmpty(value))
            return ResultMarshallerMode.On;

        if (value.Equals("off", StringComparison.OrdinalIgnoreCase))
            return ResultMarshallerMode.Off;

        if (value.Equals("passthrough", StringComparison.OrdinalIgnoreCase))
            return ResultMarshallerMode.Passthrough;

        return ResultMarshallerMode.On;
    }
}
