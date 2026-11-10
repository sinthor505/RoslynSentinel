using System.Globalization;

namespace RoslynSentinel.Common;

/// <summary>
/// Startup thresholds for the full-suite staleness warning. The full suite counts as stale when the last full run is
/// more than <see cref="MaxAgeDays"/> days old or more than <see cref="MaxFastRuns"/> fast runs ago.
/// Parsed from <c>--full-suite-max-age-days</c> / <c>--full-suite-max-fast-runs</c>, falling back to the environment
/// variables <c>ROSLYNSENTINEL_FULL_SUITE_MAX_AGE_DAYS</c> / <c>ROSLYNSENTINEL_FULL_SUITE_MAX_FAST_RUNS</c>; an
/// unparsable or non-positive value keeps the default. Populated once at startup via <see cref="Configure"/>;
/// static, so tests must reset it with <c>Configure(Array.Empty&lt;string&gt;())</c>.
/// </summary>
public static class FullSuiteStalenessOptions
{
    public const double DefaultMaxAgeDays = 2;
    public const int DefaultMaxFastRuns = 40;

    public static double MaxAgeDays { get; private set; } = DefaultMaxAgeDays;

    public static int MaxFastRuns { get; private set; } = DefaultMaxFastRuns;

    /// <summary>Parses the two options (arg first, then environment variable). Call once at process startup, before DI is built.</summary>
    public static void Configure(string[] args)
    {
        var ageRaw = GetArgValue(args, "--full-suite-max-age-days")
            ?? Environment.GetEnvironmentVariable("ROSLYNSENTINEL_FULL_SUITE_MAX_AGE_DAYS");
        MaxAgeDays = double.TryParse(ageRaw, NumberStyles.Float, CultureInfo.InvariantCulture, out var days) && days > 0
            ? days
            : DefaultMaxAgeDays;

        var runsRaw = GetArgValue(args, "--full-suite-max-fast-runs")
            ?? Environment.GetEnvironmentVariable("ROSLYNSENTINEL_FULL_SUITE_MAX_FAST_RUNS");
        MaxFastRuns = int.TryParse(runsRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var runs) && runs > 0
            ? runs
            : DefaultMaxFastRuns;
    }

    // TODO share this helper with LlmOptions.GetArgValue (every options class carries a private copy with the same behaviour).
    /// <summary>Reads a command-line flag's value, accepting both "--flag=value" and "--flag value" forms.</summary>
    private static string? GetArgValue(string[] args, string flag)
    {
        var inlinePrefix = flag + "=";
        var inline = args.FirstOrDefault(a => a.StartsWith(inlinePrefix, StringComparison.Ordinal));
        if (inline is not null)
        {
            return inline[inlinePrefix.Length..];
        }

        var index = Array.FindIndex(args, a => a.Equals(flag, StringComparison.Ordinal));
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
}
