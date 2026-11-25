namespace RoslynSentinel.Common;

/// <summary>
/// Startup switch for the optional DataTag vendor extensions in the <c>tools/list</c> input
/// schemas: the <c>x-consumes-tag</c> / <c>x-produces-tag</c> keys and <c>"default": null</c>
/// entries. They cost prompt tokens on every session and carry no information a model acts on,
/// so they are omitted by default (proposal_reduce_tool_schema_token_cost.md, Step 1).
/// Populated once at startup via <see cref="Configure"/>: the <c>--emit-datatags</c> flag first,
/// then the <c>ROSLYNSENTINEL_EMIT_DATATAGS</c> environment variable. Default is off; the flag
/// alone, or a boolean literal (<c>true</c>/<c>1</c>/<c>on</c>/<c>yes</c>, case-insensitive), turns it on.
/// A value that is not a boolean literal is reported as invalid and keeps the default.
/// Read-only after startup; it must be configured before the tool schemas are built.
/// </summary>
[StartupSettingGroup("Tool schema", PostConfigure = "ApplyHiddenParameters")]
public static class SchemaOptions
{
    public static readonly StartupSetting<bool> EmitDataTagsSetting = StartupSetting.Bool("--emit-datatags", "ROSLYNSENTINEL_EMIT_DATATAGS", false, "Emit the optional DataTag vendor extensions (x-consumes-tag, x-produces-tag, default: null) in tools/list schemas.", SettingScope.Server);

    public static readonly StartupSetting<SchemaProfile> ProfileSetting = StartupSetting.Enum<SchemaProfile>("--schema-profile", "ROSLYNSENTINEL_SCHEMA_PROFILE", SchemaProfile.Full, "Input-schema profile emitted by tools/list (Full keeps every parameter; Lean hides boilerplate parameters).", SettingScope.Server);

    public static readonly StartupSetting<IReadOnlySet<string>> HiddenParametersSetting = StartupSetting.Custom<IReadOnlySet<string>>(
        "--hide-params",
        "ROSLYNSENTINEL_HIDE_PARAMS",
        new HashSet<string>(StringComparer.Ordinal),
        "(none)",
        (string raw, out IReadOnlySet<string> value) =>
        {
            value = ParseHiddenParameters(raw);
            return true;
        },
        "parameter names (or Tool.param entries) separated by ',' or ';'",
        "Parameter names hidden from tool schemas; a Tool.param entry scopes the hide to that tool.",
        SettingScope.Server);

    public static bool EmitDataTags => EmitDataTagsSetting.Value;

    /// <summary>
    /// Which input-schema profile <c>tools/list</c> emits. <see cref="SchemaProfile.Full"/> (default) keeps
    /// every parameter; <see cref="SchemaProfile.Lean"/> hides boilerplate parameters (see
    /// McpToolSchemaPatcher.ApplyHiddenParameters). Set via <c>--schema-profile=lean|full</c> or
    /// <c>ROSLYNSENTINEL_SCHEMA_PROFILE</c>; an unrecognised value is reported as invalid and keeps Full.
    /// </summary>
    public static SchemaProfile Profile => ProfileSetting.Value;

    /// <summary>The Lean profile's hidden parameter list, in the order it is applied. Single source of truth for the Lean list.</summary>
    public static IReadOnlyList<string> LeanHiddenParameters { get; } = ["autoStage", "returnDiff", "validateOnApply", "lineBefore", "lineAfter"];

    /// <summary>
    /// Explicit parameter names hidden via <c>--hide-params</c> (or <c>ROSLYNSENTINEL_HIDE_PARAMS</c>), ordinal-compared.
    /// A plain name hides that parameter on every tool schema; a <c>Tool.param</c> entry scopes the hide to that one tool.
    /// </summary>
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

    /// <summary>
    /// Replaces the explicit hidden set (ordinal). Does not touch <see cref="Profile"/> or <see cref="EmitDataTags"/>.
    /// Obsolete for production use: register a ParameterSurface in DI. The remaining callers are test resets.
    /// </summary>
    [Obsolete("Register a ParameterSurface in DI")]
    public static void ConfigureHiddenParameters(IEnumerable<string> names)
    {
        HiddenParameters = new HashSet<string>(names, StringComparer.Ordinal);
    }

    /// <summary>
    /// The names to hide from tool schemas: the Lean list when <see cref="Profile"/> is Lean, followed by
    /// the explicit <see cref="HiddenParameters"/> names not already in it, sorted ordinally.
    /// </summary>
    /// <remarks>
    /// The production caller moved to <see cref="ParameterSurface"/> (the hidden-parameters schema patch).
    /// Kept only for tests until they are migrated.
    /// </remarks>
    [Obsolete("Use ParameterSurface.ResolveHidden")]
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
    /// A <c>Tool.param</c> entry in --hide-params scopes the hide to that one tool; any other entry hides the name on every tool.
    /// A flag wins over its environment variable. Call once at process startup, before DI is built.
    /// </summary>
    public static void Configure(string[] args)
    {
        StartupSettings.ConfigureGroup(typeof(SchemaOptions), args);
    }

    /// <summary>PostConfigure hook for the schema group: publishes the parsed --hide-params set to <see cref="HiddenParameters"/>.</summary>
    private static IReadOnlyList<ConfigurationProblem> ApplyHiddenParameters()
    {
        HiddenParameters = HiddenParametersSetting.Value;
        return Array.Empty<ConfigurationProblem>();
    }

    /// <summary>"lean" or "full" (case-insensitive, whitespace-trimmed) maps to the matching profile; null, empty or anything else keeps <see cref="SchemaProfile.Full"/> and never throws.</summary>
    public static SchemaProfile ParseProfile(string? raw)
    {
        return ProfileSetting.Read(StartupArgs.Empty, _ => raw).Value;
    }

    /// <summary>True when <paramref name="raw"/> is a boolean literal for on (true, 1, on, yes; case-insensitive, whitespace-trimmed); null, blank, off literals and anything unrecognised give false and never throw.</summary>
    public static bool ParseEnabled(string? raw)
    {
        return EmitDataTagsSetting.Read(StartupArgs.Empty, _ => raw).Value;
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
