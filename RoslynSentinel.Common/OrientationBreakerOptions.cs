namespace RoslynSentinel.Common;

public static class OrientationBreakerOptions
{
    // Added by AddMember (expected - used for diagnostics)
    public static int TripThreshold { get; private set; } = 10;


    // Added by InsertMemberAfter (expected - used for diagnostics)
    /// <summary>Parses --orientation-breaker-threshold (falling back to ROSLYNSENTINEL_ORIENTATION_BREAKER_THRESHOLD) into TripThreshold. Call once at process startup, before DI is built.</summary>
    public static void Configure(string[] args)
    {
        var thresholdRaw = GetArgValue(args, "--orientation-breaker-threshold")
            ?? Environment.GetEnvironmentVariable("ROSLYNSENTINEL_ORIENTATION_BREAKER_THRESHOLD");
        TripThreshold = int.TryParse(thresholdRaw, out var parsedThreshold) && parsedThreshold > 0 ? parsedThreshold : 10;
    }


    // Added by AddMember (expected - used for diagnostics)
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
