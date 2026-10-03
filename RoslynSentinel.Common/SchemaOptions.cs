namespace RoslynSentinel.Common;

/// <summary>
/// Startup switch for the optional DataTag vendor extensions in the <c>tools/list</c> input
/// schemas: the <c>x-consumes-tag</c> / <c>x-produces-tag</c> keys and <c>"default": null</c>
/// entries. They cost prompt tokens on every session and carry no information a model acts on,
/// so they are omitted by default (proposal_reduce_tool_schema_token_cost.md, Step 1).
/// Populated once at startup via <see cref="Configure"/>: the <c>--emit-datatags</c> flag first,
/// then the <c>ROSLYNSENTINEL_EMIT_DATATAGS</c> environment variable. Default is off; the flag
/// alone, or a value other than <c>false</c>/<c>0</c> (case-insensitive), turns it on.
/// Read-only after startup; it must be configured before the tool schemas are built.
/// </summary>
public static class SchemaOptions
{
    public static bool EmitDataTags { get; private set; }

    /// <summary>Parses --emit-datatags (falling back to ROSLYNSENTINEL_EMIT_DATATAGS) into <see cref="EmitDataTags"/>. Call once at process startup, before DI is built.</summary>
    public static void Configure(string[] args)
    {
        var raw = GetArgValue(args, "--emit-datatags")
            ?? Environment.GetEnvironmentVariable("ROSLYNSENTINEL_EMIT_DATATAGS");
        EmitDataTags = ParseEnabled(raw);
    }

    /// <summary>True when <paramref name="raw"/> is non-null and not "false" or "0" (case-insensitive, whitespace-trimmed); a bare flag with no value counts as on.</summary>
    public static bool ParseEnabled(string? raw)
    {
        if (raw is null)
        {
            return false;
        }

        var trimmed = raw.Trim();
        return !(string.Equals(trimmed, "false", StringComparison.OrdinalIgnoreCase) || trimmed == "0");
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
