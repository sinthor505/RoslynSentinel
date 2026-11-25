namespace RoslynSentinel.Common;

/// <summary>
/// Kill switch for the wire-path relativizing response filter. Environment variable only: the value
/// is read on every access so tests can flip it at runtime. Default is enabled.
/// </summary>
[StartupSettingGroup("Wire paths")]
public static class WirePathOptions
{
    /// <summary>Name of the environment variable that controls the feature.</summary>
    public const string EnvVarName = "ROSLYNSENTINEL_RELATIVE_PATHS";

    /// <summary>Live setting behind <see cref="Enabled"/>; environment variable only, read on each access.</summary>
    public static readonly StartupSetting<bool> EnabledSetting = StartupSetting.Bool(null, EnvVarName, true, "Relativize wire paths in tool responses; read on each access", SettingScope.Server, SettingKind.Live);

    /// <summary>
    /// True unless <see cref="EnvVarName"/> is set to a false value. Re-read from the environment on each access.
    /// The bool literals true, false, 1, 0, on, off, yes and no are accepted (case-insensitive). Unset, blank or
    /// an unrecognised value (also reported as an invalid setting) leaves it enabled.
    /// </summary>
    public static bool Enabled => EnabledSetting.Value;
}
