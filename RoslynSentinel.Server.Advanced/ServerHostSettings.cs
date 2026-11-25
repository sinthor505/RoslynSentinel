namespace RoslynSentinel.Server.Advanced;

/// <summary>
/// The server host's command-line flags: tool selection, solution location, HTTP host shape, log identity
/// and the fast-exit switches. All are flag-only (no environment variable). <see cref="ServerStartupHelpers"/>
/// and <c>ServerStdio</c> read them statelessly with <c>Read(StartupArgs.Parse(args), StartupSettings.EnvironmentReader)</c>.
/// </summary>
[StartupSettingGroup("Server host")]
public static class ServerHostSettings
{
    /// <summary>The transport used when <c>--transport</c> is absent.</summary>
    public const string DefaultTransport = "stdio";

    public static readonly StartupSetting<bool> TestingSetting = StartupSetting.Bool("--testing", null, false, "Testing operating mode, separate from tool activation.", SettingScope.Server);

    public static readonly StartupSetting<string?> ModeSetting = StartupSetting.String("--mode", null, null, "Tool mode names to activate, comma separated; all selects every mode.", SettingScope.Server);

    public static readonly StartupSetting<string?> ModesSetting = StartupSetting.String("--modes", null, null, "Alias of the mode list, read when the mode flag is absent.", SettingScope.Server);

    public static readonly StartupSetting<string?> SolutionSetting = StartupSetting.String("--solution", null, null, "Solution file loaded at startup.", SettingScope.Server);

    public static readonly StartupSetting<string?> BaseRepoDirSetting = StartupSetting.String("--base-repo-dir", null, null, "Base directory used to resolve relative solution paths.", SettingScope.Server);

    public static readonly StartupSetting<string?> IncludeToolsSetting = StartupSetting.String("--include-tools", null, null, "Tool class names to activate in addition to the selected modes, comma separated.", SettingScope.Server);

    public static readonly StartupSetting<string?> ExcludeToolsSetting = StartupSetting.String("--exclude-tools", null, null, "Tool class names to deactivate even when a mode selects them, comma separated.", SettingScope.Server);

    public static readonly StartupSetting<string?> HideToolsSetting = StartupSetting.String("--hide-tools", null, null, "Tool names dropped from the claude-lean allow-list, comma separated.", SettingScope.Server);

    public static readonly StartupSetting<string?> ToolsetsSetting = StartupSetting.String("--toolsets", null, null, "On-demand toolset names switched on at startup, comma separated.", SettingScope.Server);

    public static readonly StartupSetting<int?> PortSetting = StartupSetting.NullableInt("--port", null, "HTTP host port; zero lets the operating system pick one.", SettingScope.Server);

    public static readonly StartupSetting<string?> TransportSetting = StartupSetting.String("--transport", null, DefaultTransport, "Transport served by the process; stdio when absent.", SettingScope.Server);

    public static readonly StartupSetting<HttpBindScope> BindSetting = StartupSetting.Enum<HttpBindScope>("--bind", null, HttpBindScope.Loopback, "Interfaces the HTTP host listens on, loopback by default.", SettingScope.Server);

    // Null default: the effective policy follows the transport (http -> None, stdio -> Auto), so ParseRespawn derives it.
    public static readonly StartupSetting<RespawnPolicy?> RespawnSetting = StartupSetting.Custom<RespawnPolicy?>(
        "--respawn",
        null,
        null,
        "(transport default)",
        (string raw, out RespawnPolicy? value) =>
        {
            var normalized = raw.Trim().Replace("-", string.Empty).Replace("_", string.Empty);
            if (string.Equals(normalized, nameof(RespawnPolicy.Auto), StringComparison.OrdinalIgnoreCase))
            {
                value = RespawnPolicy.Auto;
                return true;
            }

            if (string.Equals(normalized, nameof(RespawnPolicy.None), StringComparison.OrdinalIgnoreCase))
            {
                value = RespawnPolicy.None;
                return true;
            }

            value = null;
            return false;
        },
        "auto or none",
        "Restart policy after the server stops; the default follows the transport.",
        SettingScope.Server);

    public static readonly StartupSetting<bool> ListToolsSetting = StartupSetting.Bool("--list-tools", null, false, "Tool surface listing written instead of serving requests.", SettingScope.Server);

    public static readonly StartupSetting<bool> FullSetting = StartupSetting.Bool("--full", null, false, "Full detail in the tool surface listing.", SettingScope.Server);

    public static readonly StartupSetting<string?> OutputSetting = StartupSetting.String("--output", null, null, "File path that receives the tool surface listing.", SettingScope.Server);

    public static readonly StartupSetting<string?> LogDirSetting = StartupSetting.String("--log-dir", null, null, "Directory that receives the server log files.", SettingScope.Server);

    public static readonly StartupSetting<string?> RunIdSetting = StartupSetting.String("--run-id", null, null, "Run identifier stamped onto every log line.", SettingScope.Server);

    public static readonly StartupSetting<string?> StepIdSetting = StartupSetting.String("--step-id", null, null, "Step identifier stamped onto every log line.", SettingScope.Server);

    public static readonly StartupSetting<bool> InteractiveSetting = StartupSetting.Bool("--interactive", null, false, "Interactive console session attached to the stdio server.", SettingScope.ServerStdio);

    // Declared here; the early exit that prints the registry is wired in the renderer step.
    public static readonly StartupSetting<bool> ListOptionsSetting = StartupSetting.Bool("--list-options", null, false, "Configuration reference listing printed instead of serving requests.", SettingScope.Server);
}
