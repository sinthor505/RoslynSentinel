namespace RoslynSentinel.Common;

/// <summary>
/// Resolved settings for the idle stale-restart monitor. Populated once at startup via <see cref="Configure"/>: each
/// setting prefers its <c>--flag</c> command-line argument, falling back to the matching <c>ROSLYNSENTINEL_*</c>
/// environment variable, then a built-in default. Read-only after startup.
/// </summary>
public static class IdleRestartOptions
{
    /// <summary>Idle minutes before a stdio server may exit for a newer prebuilt build; 0 or less disables the monitor.</summary>
    public static int Minutes { get; private set; } = 15;

    /// <summary>Folder holding the launcher's build folders and <c>latest.json</c>; null (the default) keeps the monitor off.</summary>
    public static string? BuildsRoot { get; private set; }

    /// <summary>True when a positive idle minutes value and a builds root are both configured.</summary>
    public static bool Enabled => Minutes > 0 && !string.IsNullOrWhiteSpace(BuildsRoot);

    /// <summary>Parses --idle-restart-minutes and --builds-root into the properties above. Call once at process startup.</summary>
    public static void Configure(string[] args)
    {
        var minutesRaw = GetArgValue(args, "--idle-restart-minutes")
            ?? Environment.GetEnvironmentVariable("ROSLYNSENTINEL_IDLE_RESTART_MINUTES");
        Minutes = int.TryParse(minutesRaw, out var parsedMinutes) ? parsedMinutes : 15;

        BuildsRoot = GetArgValue(args, "--builds-root")
            ?? Environment.GetEnvironmentVariable("ROSLYNSENTINEL_BUILDS_ROOT");
    }

    /// <summary>Restores the built-in defaults. Tests only.</summary>
    internal static void ResetForTests()
    {
        Minutes = 15;
        BuildsRoot = null;
    }

    // TODO share this helper with LlmOptions.GetArgValue (currently a private copy with the same behaviour).
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
