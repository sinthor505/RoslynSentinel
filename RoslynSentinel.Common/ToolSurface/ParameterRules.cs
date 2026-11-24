using System.Collections.Generic;
using System.Linq;

namespace RoslynSentinel.Common;

/// <summary>Where a parameter visibility rule came from. Later layers win over earlier ones.</summary>
public enum ParameterRuleSource { Profile, ToolsetFile, EnvOrFlag, Harness }

/// <summary>Whether a rule hides a parameter from a tool's schema or explicitly keeps it visible.</summary>
public enum ParameterVisibility { Hide, Show }

/// <summary>
/// One parameter visibility rule. A null <paramref name="Tool"/> applies to every tool.
/// The parameter "*" is only valid with <see cref="ParameterVisibility.Show"/>.
/// </summary>
/// <param name="Tool">Tool the rule applies to, or null for every tool.</param>
/// <param name="Parameter">Parameter name the rule applies to, or "*" for all parameters (Show only).</param>
/// <param name="Action">Whether the parameter is hidden or shown.</param>
/// <param name="Source">The layer that supplied the rule.</param>
/// <param name="Origin">Human-readable pointer to the rule's origin, for diagnostics.</param>
public sealed record ParameterVisibilityRule(string? Tool, string Parameter, ParameterVisibility Action,
    ParameterRuleSource Source, string Origin);

/// <summary>
/// One group per winning Hide rule: the parameter, the rule's source and origin, and the tools it was stripped from (ordinal order).
/// </summary>
/// <param name="Parameter">The hidden parameter name.</param>
/// <param name="Source">The layer of the winning Hide rule.</param>
/// <param name="Origin">Origin of the winning Hide rule, for diagnostics.</param>
/// <param name="Tools">The tools the parameter was stripped from.</param>
public sealed record HiddenParameterGroup(string Parameter, ParameterRuleSource Source, string Origin, IReadOnlyList<string> Tools);

/// <summary>Immutable set of parameter visibility rules that drives the tool surface.</summary>
/// <param name="Visibility">The rules, in the order they were supplied.</param>
public sealed record ToolParameterSurfaceOptions(IReadOnlyList<ParameterVisibilityRule> Visibility)
{
    /// <summary>Options with no rules.</summary>
    public static ToolParameterSurfaceOptions Empty { get; } = new([]);

    /// <summary>
    /// Builds options from startup state: the lean list as Profile rules when SchemaOptions.Profile is Lean, then each
    /// SchemaOptions.HiddenParameters name as an unscoped EnvOrFlag rule (ordinal order). Reads the statics when called.
    /// Implemented in Step 3a.
    /// </summary>
    /// <returns>The startup parameter surface options.</returns>
    public static ToolParameterSurfaceOptions FromStartup()
    {
        var rules = new List<ParameterVisibilityRule>();
        if (SchemaOptions.Profile == SchemaProfile.Lean)
        {
            foreach (var name in SchemaOptions.LeanHiddenParameters)
            {
                rules.Add(new ParameterVisibilityRule(null, name, ParameterVisibility.Hide, ParameterRuleSource.Profile, "--schema-profile=lean"));
            }
        }

        // Step 12 changes this (Tool.param scoping): entries stay unscoped literals until then.
        foreach (var name in SchemaOptions.HiddenParameters.OrderBy(n => n, StringComparer.Ordinal))
        {
            rules.Add(new ParameterVisibilityRule(null, name, ParameterVisibility.Hide, ParameterRuleSource.EnvOrFlag, "--hide-params / ROSLYNSENTINEL_HIDE_PARAMS"));
        }

        return new ToolParameterSurfaceOptions(rules);
    }

    /// <summary>
    /// Parses a hide list. Splits on ',' and ';', trims, and drops empties. "Tool.param" (exactly one '.', both sides
    /// non-empty) scopes the rule to a tool; anything else is an unscoped Hide rule for the literal text. Never throws.
    /// Implemented in Step 3a.
    /// </summary>
    /// <param name="raw">The raw hide list text, or null.</param>
    /// <param name="source">The layer the parsed rules belong to.</param>
    /// <param name="origin">Human-readable origin recorded on each rule.</param>
    /// <returns>The parsed Hide rules.</returns>
    public static IReadOnlyList<ParameterVisibilityRule> ParseHideList(string? raw, ParameterRuleSource source, string origin)
        {
        var rules = new List<ParameterVisibilityRule>();
        if (string.IsNullOrWhiteSpace(raw))
        {
            return rules;
        }

        foreach (var part in raw.Split(',', ';'))
        {
            var entry = part.Trim();
            if (entry.Length == 0)
            {
                continue;
            }

            var sides = entry.Split('.');
            if (sides.Length == 2 && sides[0].Trim().Length > 0 && sides[1].Trim().Length > 0)
            {
                rules.Add(new ParameterVisibilityRule(sides[0].Trim(), sides[1].Trim(), ParameterVisibility.Hide, source, origin));
            }
            else
            {
                rules.Add(new ParameterVisibilityRule(null, entry, ParameterVisibility.Hide, source, origin));
            }
        }

        return rules;
    }
}
