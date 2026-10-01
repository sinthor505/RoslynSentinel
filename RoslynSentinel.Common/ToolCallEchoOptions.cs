namespace RoslynSentinel.Common;

/// <summary>
/// Startup switch for the <c>toolCall</c> echo that the outermost call-tool filter stamps onto
/// every tool response (tool name, a short call id, and the truncated arguments the caller sent).
/// Populated once at startup via <see cref="Configure"/>: <c>--echo-tool-args</c> first, then the
/// <c>ROSLYNSENTINEL_ECHO_TOOL_ARGS</c> environment variable. Default is on; the values
/// <c>false</c> and <c>0</c> (case-insensitive) turn it off, anything else leaves it on. Read-only
/// after startup; the filter reads <see cref="Enabled"/> at call time and is a pure pass-through
/// when it is false.
/// </summary>
public static class ToolCallEchoOptions
{
    public static bool Enabled { get; private set; } = true;

    /// <summary>Parses --echo-tool-args (falling back to ROSLYNSENTINEL_ECHO_TOOL_ARGS) into <see cref="Enabled"/>. Call once at process startup, before DI is built.</summary>
    public static void Configure(string[] args)
    {
        var raw = GetArgValue(args, "--echo-tool-args")
            ?? Environment.GetEnvironmentVariable("ROSLYNSENTINEL_ECHO_TOOL_ARGS");
        Enabled = ParseEnabled(raw);
    }

    /// <summary>True unless <paramref name="raw"/> is "false" or "0" (case-insensitive, whitespace-trimmed).</summary>
    public static bool ParseEnabled(string? raw)
    {
        var trimmed = raw?.Trim();
        return !(string.Equals(trimmed, "false", StringComparison.OrdinalIgnoreCase) || trimmed == "0");
    }

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
