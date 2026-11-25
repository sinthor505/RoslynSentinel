namespace RoslynSentinel.Common;

/// <summary>
/// Resolved settings for the idle stale-restart monitor. Populated once at startup via <see cref="Configure"/>: each
/// setting prefers its <c>--flag</c> command-line argument, falling back to the matching <c>ROSLYNSENTINEL_*</c>
/// environment variable, then a built-in default. Read-only after startup.
/// </summary>
[StartupSettingGroup("Idle restart")]
public static class IdleRestartOptions
{
    public static readonly StartupSetting<int> MinutesSetting = StartupSetting.Int("--idle-restart-minutes", "ROSLYNSENTINEL_IDLE_RESTART_MINUTES", 15, "Idle minutes before a stdio server may exit for a newer build; zero disables it.", SettingScope.ServerStdio);

    public static readonly StartupSetting<string?> BuildsRootSetting = StartupSetting.String("--builds-root", "ROSLYNSENTINEL_BUILDS_ROOT", null, "Folder holding the launcher build folders and latest.json.", SettingScope.ServerStdio);

    /// <summary>Idle minutes before a stdio server may exit for a newer prebuilt build; 0 or less disables the monitor.</summary>
    public static int Minutes => MinutesSetting.Value;

    /// <summary>Folder holding the launcher's build folders and <c>latest.json</c>; null (the default) keeps the monitor off.</summary>
    public static string? BuildsRoot => BuildsRootSetting.Value;

    /// <summary>True when a positive idle minutes value and a builds root are both configured.</summary>
    public static bool Enabled => Minutes > 0 && !string.IsNullOrWhiteSpace(BuildsRoot);

    /// <summary>Parses --idle-restart-minutes and --builds-root into the properties above. Call once at process startup.</summary>
    public static void Configure(string[] args)
    {
        StartupSettings.ConfigureGroup(typeof(IdleRestartOptions), args);
    }

    /// <summary>Restores the built-in defaults. Tests only.</summary>
    internal static void ResetForTests()
    {
        MinutesSetting.Reset();
        BuildsRootSetting.Reset();
    }
}
