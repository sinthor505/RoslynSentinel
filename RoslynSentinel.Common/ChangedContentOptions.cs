namespace RoslynSentinel.Common;

[StartupSettingGroup("Changed content")]
public static class ChangedContentOptions
{
    public static readonly StartupSetting<bool> InlineOnApplySetting = StartupSetting.Bool(null, "ROSLYNSENTINEL_INLINE_CHANGED_CONTENT", false, "Echo changed file contents on real applies.", SettingScope.Server, SettingKind.Startup);

    /// <summary>
    /// Server-wide switch (not a tool parameter) to keep tool schemas small; default false = real applies do not echo changed file contents; dry runs/no-stage results always do.
    /// </summary>
    public static bool InlineOnApply { get; set; } = InlineOnApplySetting.Read(StartupArgs.Empty, StartupSettings.EnvironmentReader).Value;
}
