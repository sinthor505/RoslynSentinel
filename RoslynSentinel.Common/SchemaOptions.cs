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

    /// <summary>
    /// Which input-schema profile <c>tools/list</c> emits. <see cref="SchemaProfile.Full"/> (default) keeps
    /// every parameter; <see cref="SchemaProfile.Lean"/> hides boilerplate parameters (see
    /// McpToolSchemaPatcher.ApplyHiddenParameters). Set via <c>--schema-profile=lean|full</c> or
    /// <c>ROSLYNSENTINEL_SCHEMA_PROFILE</c>; an unrecognised value keeps Full.
    /// </summary>
    public static SchemaProfile Profile { get; private set; } = SchemaProfile.Full;

    /// <summary>The Lean profile's hidden parameter list, in the order it is applied. Single source of truth for the Lean list.</summary>
    public static IReadOnlyList<string> LeanHiddenParameters { get; } = ["autoStage", "returnDiff", "validateOnApply", "lineBefore", "lineAfter"];

    /// <summary>Explicit parameter names hidden from every tool schema via <c>--hide-params</c> (or <c>ROSLYNSENTINEL_HIDE_PARAMS</c>), ordinal-compared.</summary>
    public static IReadOnlySet<string> HiddenParameters { get; private set; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>Splits on ',' and ';', trims, drops empty entries and keeps unknown names. Never throws; null or blank gives an empty set.</summary>
    public static IReadOnlySet<string> ParseHiddenParameters(string? raw)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return names;
        }

        foreach (var part in raw.Split(',', ';'))
        {
            var name = part.Trim();
            if (name.Length > 0)
            {
                names.Add(name);
            }
        }

        return names;
    }

    /// <summary>Replaces the explicit hidden set (ordinal). Does not touch <see cref="Profile"/> or <see cref="EmitDataTags"/>.</summary>
    public static void ConfigureHiddenParameters(IEnumerable<string> names)
    {
        HiddenParameters = new HashSet<string>(names, StringComparer.Ordinal);
    }

    /// <summary>
    /// The names to hide from tool schemas: the Lean list when <see cref="Profile"/> is Lean, followed by
    /// the explicit <see cref="HiddenParameters"/> names not already in it, sorted ordinally.
    /// </summary>
    public static IReadOnlyList<string> EffectiveHiddenParameters()
    {
        var result = new List<string>();
        if (Profile == SchemaProfile.Lean)
        {
            result.AddRange(LeanHiddenParameters);
        }

        foreach (var name in HiddenParameters.OrderBy(n => n, StringComparer.Ordinal))
        {
            if (!result.Contains(name, StringComparer.Ordinal))
            {
                result.Add(name);
            }
        }

        return result;
    }

    /// <summary>
    /// Parses --emit-datatags (falling back to ROSLYNSENTINEL_EMIT_DATATAGS) into <see cref="EmitDataTags"/>,
    /// --schema-profile (falling back to ROSLYNSENTINEL_SCHEMA_PROFILE) into <see cref="Profile"/> and
    /// --hide-params (falling back to ROSLYNSENTINEL_HIDE_PARAMS) into <see cref="HiddenParameters"/>.
    /// A flag wins over its environment variable. Call once at process startup, before DI is built.
    /// </summary>
    public static void Configure(string[] args)
    {
        var raw = GetArgValue(args, "--emit-datatags")
            ?? Environment.GetEnvironmentVariable("ROSLYNSENTINEL_EMIT_DATATAGS");
        EmitDataTags = ParseEnabled(raw);

        Profile = ParseProfile(GetArgValue(args, "--schema-profile")
            ?? Environment.GetEnvironmentVariable("ROSLYNSENTINEL_SCHEMA_PROFILE"));

        HiddenParameters = ParseHiddenParameters(GetArgValue(args, "--hide-params")
            ?? Environment.GetEnvironmentVariable("ROSLYNSENTINEL_HIDE_PARAMS"));
    }

    /// <summary>"lean" or "full" (case-insensitive, whitespace-trimmed) maps to the matching profile; null, empty or anything else keeps <see cref="SchemaProfile.Full"/> and never throws.</summary>
    public static SchemaProfile ParseProfile(string? raw)
    {
        var trimmed = raw?.Trim();
        return string.Equals(trimmed, "lean", StringComparison.OrdinalIgnoreCase)
            ? SchemaProfile.Lean
            : SchemaProfile.Full;
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

/// <summary>Input-schema profile emitted by tools/list; see <see cref="SchemaOptions.Profile"/>.</summary>
public enum SchemaProfile
{
    /// <summary>Every parameter is emitted (default).</summary>
    Full,

    /// <summary>Boilerplate parameters (autoStage, returnDiff, validateOnApply, lineBefore, lineAfter) are hidden from the schema.</summary>
    Lean,
}
