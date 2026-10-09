namespace RoslynSentinel.Common;

public static class ChangedContentOptions
{
    /// <summary>
    /// Server-wide switch (not a tool parameter) to keep tool schemas small; default false = real applies do not echo changed file contents; dry runs/no-stage results always do.
    /// </summary>
    public static bool InlineOnApply { get; set; } = TryParseEnvVar();

    private static bool TryParseEnvVar()
    {
        var value = Environment.GetEnvironmentVariable("ROSLYNSENTINEL_INLINE_CHANGED_CONTENT");
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        return value.Equals("1", StringComparison.OrdinalIgnoreCase)
            || value.Equals("true", StringComparison.OrdinalIgnoreCase);
    }
}
