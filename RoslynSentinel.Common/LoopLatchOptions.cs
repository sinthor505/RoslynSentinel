namespace RoslynSentinel.Common;

/// <summary>
/// Startup switch and constants for the server loop latch (<see cref="ToolLoopLatch"/>; proposal_loop_latch.md).
/// Populated once at startup via <see cref="Configure"/>: <c>--loop-latch</c> first, then the
/// <c>ROSLYNSENTINEL_LOOP_LATCH</c> environment variable. Off by default: with the flag off the latch filter is a pure
/// pass-through and the server behaves exactly as before. The bare flag (<c>--loop-latch</c>) and <c>--loop-latch=true</c>,
/// <c>1</c>, <c>on</c> or <c>yes</c> turn it on; anything else leaves it off.
/// </summary>
public static class LoopLatchOptions
{
    /// <summary>The same tool failing the same way this many times, with no successful call of a different tool in between, latches it.</summary>
    public const int TripThreshold = 2;

    /// <summary>
    /// Harness side: after the first latch refusal of a tool, this many further calls of the same tool (each refused too) end the run
    /// with <c>AgentStopReason.LoopLatchIgnored</c>.
    /// </summary>
    public const int IgnoredRefusalLimit = 2;

    public static bool Enabled { get; private set; }

    /// <summary>Parses --loop-latch (falling back to ROSLYNSENTINEL_LOOP_LATCH) into <see cref="Enabled"/>. Call once at process startup, before DI is built.</summary>
    public static void Configure(string[] args)
    {
        var raw = GetFlagValue(args, "--loop-latch")
            ?? Environment.GetEnvironmentVariable("ROSLYNSENTINEL_LOOP_LATCH");
        Enabled = ParseEnabled(raw);
    }

    /// <summary>True for "true", "1", "on" or "yes" (case-insensitive, whitespace-trimmed); false for null, empty and anything else.</summary>
    public static bool ParseEnabled(string? raw)
    {
        var trimmed = raw?.Trim();
        return string.Equals(trimmed, "true", StringComparison.OrdinalIgnoreCase)
            || string.Equals(trimmed, "on", StringComparison.OrdinalIgnoreCase)
            || string.Equals(trimmed, "yes", StringComparison.OrdinalIgnoreCase)
            || trimmed == "1";
    }

    /// <summary>
    /// "--flag=value" gives the value; a bare "--flag" gives "true", except that a following boolean-looking token
    /// (true/false/on/off/yes/no/0/1) is taken as its value. Null when the flag is absent.
    /// </summary>
    private static string? GetFlagValue(string[] args, string flag)
    {
        var inlinePrefix = flag + "=";
        var inline = args.FirstOrDefault(a => a.StartsWith(inlinePrefix, StringComparison.Ordinal));
        if (inline is not null)
        {
            return inline[inlinePrefix.Length..];
        }

        var index = Array.FindIndex(args, a => a.Equals(flag, StringComparison.Ordinal));
        if (index < 0)
        {
            return null;
        }

        if (index + 1 < args.Length
            && args[index + 1].Trim().ToLowerInvariant() is "true" or "false" or "on" or "off" or "yes" or "no" or "0" or "1")
        {
            return args[index + 1];
        }

        return "true";
    }
}
