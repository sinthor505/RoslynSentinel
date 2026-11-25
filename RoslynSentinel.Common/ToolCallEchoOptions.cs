namespace RoslynSentinel.Common;

/// <summary>
/// Startup switch for the <c>toolCall</c> echo that the outermost call-tool filter stamps onto
/// every tool response (tool name, a short call id, and the truncated arguments the caller sent).
/// Populated once at startup via <see cref="Configure"/>: <c>--echo-tool-args</c> first, then the
/// <c>ROSLYNSENTINEL_ECHO_TOOL_ARGS</c> environment variable. Default is off. The bool literals
/// <c>true</c>, <c>false</c>, <c>1</c>, <c>0</c>, <c>on</c>, <c>off</c>, <c>yes</c> and <c>no</c>
/// (case-insensitive) are accepted; an unrecognised value is reported as an invalid setting and leaves it off.
/// Read-only after startup; the filter reads <see cref="Enabled"/> at call time and is a pure pass-through
/// when it is false.
/// </summary>
[StartupSettingGroup("Tool-call echo")]
public static class ToolCallEchoOptions
{
    public static readonly StartupSetting<bool> EnabledSetting = StartupSetting.Bool("--echo-tool-args", "ROSLYNSENTINEL_ECHO_TOOL_ARGS", false, "Stamp a toolCall echo (tool name, call id, truncated arguments) onto every tool response.", SettingScope.Server);

    public static bool Enabled => EnabledSetting.Value;

    /// <summary>Populates the settings from args and environment. Call once at process startup, before DI is built.</summary>
    public static void Configure(string[] args)
    {
        StartupSettings.ConfigureGroup(typeof(ToolCallEchoOptions), args);
    }

    /// <summary>
    /// Parses a raw setting value into <see cref="Enabled"/>'s meaning. The bool literals true, false, 1, 0, on, off,
    /// yes and no (case-insensitive, surrounding whitespace ignored) are accepted; null and blank are off without a
    /// problem; an unrecognised value is off and is also reported as an invalid setting by the registry.
    /// </summary>
    public static bool ParseEnabled(string? raw)
    {
        return EnabledSetting.Read(StartupArgs.Empty, _ => raw).Value;
    }
}
