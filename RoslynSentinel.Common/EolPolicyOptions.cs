namespace RoslynSentinel.Common;

/// <summary>
/// Startup switch for the line-ending policy: <c>--eol=auto|crlf|lf</c> first, then the
/// <c>ROSLYNSENTINEL_EOL</c> environment variable. Default is <see cref="EolMode.Auto"/>.
/// Populated once at startup via <see cref="Configure"/>; read-only after startup, but static,
/// so tests must reset it.
/// </summary>
[StartupSettingGroup("Line endings")]
public static class EolPolicyOptions
{
    public static readonly StartupSetting<EolMode> ModeSetting = StartupSetting.Enum<EolMode>("--eol", "ROSLYNSENTINEL_EOL", EolMode.Auto, "Line-ending policy for files the server writes: auto, crlf or lf.", SettingScope.Server);

    public static EolMode Mode => ModeSetting.Value;

    /// <summary>Parses --eol (falling back to ROSLYNSENTINEL_EOL) into <see cref="Mode"/>. Call once at process startup.</summary>
    public static void Configure(string[] args)
    {
        StartupSettings.ConfigureGroup(typeof(EolPolicyOptions), args);
    }

    /// <summary>"crlf" or "lf" (case-insensitive, whitespace-trimmed) map to the matching mode; "auto", null, empty or anything else maps to Auto and never throws (the registry reports an unrecognised value as invalid).</summary>
    public static EolMode ParseMode(string? raw)
    {
        return ModeSetting.Read(StartupArgs.Empty, _ => raw).Value;
    }
}

/// <summary>Line-ending mode for files the server writes; see <see cref="EolPolicyOptions"/>.</summary>
public enum EolMode
{
    /// <summary>Follow the pre-image file, then .editorconfig, then the platform.</summary>
    Auto,

    /// <summary>Always CRLF.</summary>
    Crlf,

    /// <summary>Always LF.</summary>
    Lf,
}
