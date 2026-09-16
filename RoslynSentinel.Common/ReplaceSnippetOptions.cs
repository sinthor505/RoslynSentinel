namespace RoslynSentinel.Common;

/// <summary>
/// Resolved size limits for <c>ReplaceSnippet</c>'s oldContent/newContent guard.
/// Populated once at startup via <see cref="Configure"/>: each setting prefers its
/// <c>--replace-snippet-*</c> command-line argument, falling back to the matching
/// <c>ROSLYNSENTINEL_REPLACE_SNIPPET_*</c> environment variable, then a built-in default.
/// Read-only after startup. Exists as a startup-tunable knob specifically so model-eval runs
/// can experiment with looser/tighter limits without a code change and rebuild.
/// </summary>
public static class ReplaceSnippetOptions
{
    public static int MaxOldContentLines { get; private set; } = 100;
    public static int MaxNewContentLines { get; private set; } = 100;
    public static int MaxOldContentChars { get; private set; } = 2000;
    public static int MaxNewContentChars { get; private set; } = 2000;

    /// <summary>Parses --replace-snippet-* args (falling back to ROSLYNSENTINEL_REPLACE_SNIPPET_* env vars) into the static properties above. Call once at process startup, before DI is built.</summary>
    public static void Configure(string[] args)
    {
        var maxOldLinesRaw = GetArgValue(args, "--replace-snippet-max-old-lines")
            ?? Environment.GetEnvironmentVariable("ROSLYNSENTINEL_REPLACE_SNIPPET_MAX_OLD_LINES");
        MaxOldContentLines = int.TryParse(maxOldLinesRaw, out var parsedMaxOldLines) && parsedMaxOldLines > 0 ? parsedMaxOldLines : 100;

        var maxNewLinesRaw = GetArgValue(args, "--replace-snippet-max-new-lines")
            ?? Environment.GetEnvironmentVariable("ROSLYNSENTINEL_REPLACE_SNIPPET_MAX_NEW_LINES");
        MaxNewContentLines = int.TryParse(maxNewLinesRaw, out var parsedMaxNewLines) && parsedMaxNewLines > 0 ? parsedMaxNewLines : 100;

        var maxOldCharsRaw = GetArgValue(args, "--replace-snippet-max-old-chars")
            ?? Environment.GetEnvironmentVariable("ROSLYNSENTINEL_REPLACE_SNIPPET_MAX_OLD_CHARS");
        MaxOldContentChars = int.TryParse(maxOldCharsRaw, out var parsedMaxOldChars) && parsedMaxOldChars > 0 ? parsedMaxOldChars : 2000;

        var maxNewCharsRaw = GetArgValue(args, "--replace-snippet-max-new-chars")
            ?? Environment.GetEnvironmentVariable("ROSLYNSENTINEL_REPLACE_SNIPPET_MAX_NEW_CHARS");
        MaxNewContentChars = int.TryParse(maxNewCharsRaw, out var parsedMaxNewChars) && parsedMaxNewChars > 0 ? parsedMaxNewChars : 2000;
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
