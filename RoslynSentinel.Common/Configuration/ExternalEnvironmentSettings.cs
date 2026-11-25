namespace RoslynSentinel.Common;

/// <summary>
/// Environment variables owned by other tools (kind External). The registry reports only whether
/// each one is set; it never validates or defaults them.
/// </summary>
[StartupSettingGroup("External environment")]
public static class ExternalEnvironmentSettings
{
    public static readonly StartupSetting<string?> NuGetPackagesSetting = StartupSetting.String(null, "NUGET_PACKAGES", null, "NuGet package cache location, read by the commenting engine.", SettingScope.Server, SettingKind.External);

    public static readonly StartupSetting<string?> ClaudeCodeSessionIdSetting = StartupSetting.String(null, "CLAUDE_CODE_SESSION_ID", null, "Session id set by Claude Code, read for tool-call attribution.", SettingScope.Server, SettingKind.External);
}
