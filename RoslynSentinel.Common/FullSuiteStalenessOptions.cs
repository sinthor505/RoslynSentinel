using System.Globalization;

namespace RoslynSentinel.Common;

/// <summary>
/// Startup thresholds for the full-suite staleness warning. The full suite counts as stale when the last full run is
/// more than <see cref="MaxAgeDays"/> days old or more than <see cref="MaxFastRuns"/> fast runs ago.
/// Parsed from <c>--full-suite-max-age-days</c> / <c>--full-suite-max-fast-runs</c>, falling back to the environment
/// variables <c>ROSLYNSENTINEL_FULL_SUITE_MAX_AGE_DAYS</c> / <c>ROSLYNSENTINEL_FULL_SUITE_MAX_FAST_RUNS</c>; an
/// unparsable or non-positive value keeps the default and is reported as an invalid setting. Populated once at startup via <see cref="Configure"/>;
/// static, so tests must reset it with <c>Configure(Array.Empty&lt;string&gt;())</c>.
/// </summary>
[StartupSettingGroup("Full-suite staleness")]
public static class FullSuiteStalenessOptions
{
    public const double DefaultMaxAgeDays = 2;
    public const int DefaultMaxFastRuns = 40;

    public static readonly StartupSetting<double> MaxAgeDaysSetting = StartupSetting.Custom<double>(
        "--full-suite-max-age-days",
        "ROSLYNSENTINEL_FULL_SUITE_MAX_AGE_DAYS",
        DefaultMaxAgeDays,
        "2",
        (string raw, out double value) => double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && value > 0,
        "number > 0",
        "Full-suite staleness: maximum age of the last full run, in days.",
        SettingScope.Server);

    public static readonly StartupSetting<int> MaxFastRunsSetting = StartupSetting.Int("--full-suite-max-fast-runs", "ROSLYNSENTINEL_FULL_SUITE_MAX_FAST_RUNS", DefaultMaxFastRuns, "Full-suite staleness: maximum number of fast runs since the last full run.", SettingScope.Server, minValue: 1);

    public static double MaxAgeDays => MaxAgeDaysSetting.Value;

    public static int MaxFastRuns => MaxFastRunsSetting.Value;

    /// <summary>Parses the two options (arg first, then environment variable). Call once at process startup, before DI is built.</summary>
    public static void Configure(string[] args)
    {
        StartupSettings.ConfigureGroup(typeof(FullSuiteStalenessOptions), args);
    }
}
