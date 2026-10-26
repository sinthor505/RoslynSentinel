namespace RoslynSentinel.Common;

/// <summary>
/// Startup switch for the line-ending policy: <c>--eol=auto|crlf|lf</c> first, then the
/// <c>ROSLYNSENTINEL_EOL</c> environment variable. Default is <see cref="EolMode.Auto"/>.
/// Populated once at startup via <see cref="Configure"/>; read-only after startup, but static,
/// so tests must reset it.
/// </summary>
public static class EolPolicyOptions
{
    public static EolMode Mode { get; private set; } = EolMode.Auto;

    /// <summary>Parses --eol (falling back to ROSLYNSENTINEL_EOL) into <see cref="Mode"/>. Call once at process startup.</summary>
    public static void Configure(string[] args)
    {
        Mode = ParseMode(GetArgValue(args, "--eol")
            ?? Environment.GetEnvironmentVariable("ROSLYNSENTINEL_EOL"));
    }

    /// <summary>"crlf" or "lf" (case-insensitive, whitespace-trimmed) map to the matching mode; "auto", null, empty or anything else maps to Auto and never throws.</summary>
    public static EolMode ParseMode(string? raw)
    {
        var trimmed = raw?.Trim();
        if (string.Equals(trimmed, "crlf", StringComparison.OrdinalIgnoreCase))
        {
            return EolMode.Crlf;
        }

        if (string.Equals(trimmed, "lf", StringComparison.OrdinalIgnoreCase))
        {
            return EolMode.Lf;
        }

        return EolMode.Auto;
    }

    /// <summary>
    /// Reads a command-line flag's value, accepting "--flag=value", "--flag value", and a bare
    /// "--flag" (returned as "true"; a following token starting with "--" is not consumed as a value).
    /// </summary>
    private static string? GetArgValue(string[] args, string flag)
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

        return index + 1 < args.Length && !args[index + 1].StartsWith("--", StringComparison.Ordinal)
            ? args[index + 1]
            : "true";
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
