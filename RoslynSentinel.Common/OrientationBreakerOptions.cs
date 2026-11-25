namespace RoslynSentinel.Common;

[StartupSettingGroup("Orientation breaker")]
public static class OrientationBreakerOptions
{
    public static readonly StartupSetting<int> TripThresholdSetting = StartupSetting.Int("--orientation-breaker-threshold", "ROSLYNSENTINEL_ORIENTATION_BREAKER_THRESHOLD", 10, "Consecutive failures before the orientation circuit breaker trips.", SettingScope.Server, minValue: 1);

    public static int TripThreshold => TripThresholdSetting.Value;

    /// <summary>Parses --orientation-breaker-threshold (falling back to ROSLYNSENTINEL_ORIENTATION_BREAKER_THRESHOLD) into TripThreshold. Call once at process startup, before DI is built.</summary>
    public static void Configure(string[] args)
    {
        StartupSettings.ConfigureGroup(typeof(OrientationBreakerOptions), args);
    }
}
