using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using ModelContextProtocol.Server;

namespace RoslynSentinel.Common;

/// <summary>
/// Resolves parameter visibility rules against tools. Constructed from immutable <see cref="ToolParameterSurfaceOptions"/>.
/// </summary>
public sealed class ParameterSurface
{
    /// <summary>Creates a surface over the given options.</summary>
    /// <param name="options">The immutable rules to resolve against.</param>
    public ParameterSurface(ToolParameterSurfaceOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        Options = options;
        _ordered = options.Visibility
            .OrderBy(rule => rule.Source)
            .ThenBy(rule => rule.Tool is null ? 0 : 1)
            .ToList();
        HidesAnything = _ordered.Any(rule => rule.Action == ParameterVisibility.Hide);
    }

    /// <summary>Creates a surface from startup state.</summary>
    /// <returns>A surface over <see cref="ToolParameterSurfaceOptions.FromStartup"/>.</returns>
    public static ParameterSurface FromStartup() => new ParameterSurface(ToolParameterSurfaceOptions.FromStartup());

    /// <summary>The options this surface was built from.</summary>
    public ToolParameterSurfaceOptions Options { get; }

    private readonly IReadOnlyList<ParameterVisibilityRule> _ordered;

    /// <summary>True when any Hide rule exists.</summary>
    public bool HidesAnything { get; }

    /// <summary>
    /// Returns the winning Hide rule for a tool's parameter, or null when the parameter is not hidden.
    /// </summary>
    /// <param name="toolName">The MCP tool name.</param>
    /// <param name="parameterName">The parameter name on that tool.</param>
    /// <returns>The winning Hide rule, or null.</returns>
    public ParameterVisibilityRule? ResolveHidden(string toolName, string parameterName)
    {
        ParameterVisibilityRule? winner = null;
        foreach (var rule in _ordered)
        {
            var toolMatches = rule.Tool is null || string.Equals(rule.Tool, toolName, StringComparison.OrdinalIgnoreCase);
            var parameterMatches = string.Equals(rule.Parameter, parameterName, StringComparison.Ordinal)
                || (rule.Action == ParameterVisibility.Show && rule.Parameter == "*");
            if (toolMatches && parameterMatches)
            {
                winner = rule;
            }
        }

        return winner is { Action: ParameterVisibility.Hide } ? winner : null;
    }

    /// <summary>
    /// Groups the Hide rules that strip parameters from the given tools, one group per winning Hide rule.
    /// Rules are grouped by reference, so two equal rules from different layers or origins stay separate.
    /// </summary>
    /// <param name="tools">The tools to attribute against.</param>
    /// <returns>The hidden parameter groups, ordered by parameter then source.</returns>
    public IReadOnlyList<HiddenParameterGroup> AttributeHidden(IReadOnlyList<ToolSurfaceTool> tools)
    {
        ArgumentNullException.ThrowIfNull(tools);
        var toolsByRule = new Dictionary<ParameterVisibilityRule, List<string>>(ReferenceEqualityComparer.Instance);
        foreach (var (tool, rule) in WinningHideRules(tools))
        {
            if (!toolsByRule.TryGetValue(rule, out var toolNames))
            {
                toolNames = new List<string>();
                toolsByRule[rule] = toolNames;
            }

            toolNames.Add(tool.Name);
        }

        return toolsByRule
            .Select(pair => new HiddenParameterGroup(
                pair.Key.Parameter,
                pair.Key.Source,
                pair.Key.Origin,
                pair.Value.Distinct(StringComparer.Ordinal).OrderBy(name => name, StringComparer.Ordinal).ToList()))
            .OrderBy(group => group.Parameter, StringComparer.Ordinal)
            .ThenBy(group => group.Source)
            .ToList();
    }

    /// <summary>
    /// Returns the Hide rules that are the winning rule for no parameter of the given tools, in list order.
    /// </summary>
    /// <param name="tools">The tools to check rules against.</param>
    /// <returns>The unmatched Hide rules.</returns>
    public IReadOnlyList<ParameterVisibilityRule> UnmatchedHideRules(IReadOnlyList<ToolSurfaceTool> tools)
    {
        ArgumentNullException.ThrowIfNull(tools);
        var winners = new HashSet<ParameterVisibilityRule>(ReferenceEqualityComparer.Instance);
        foreach (var (_, rule) in WinningHideRules(tools))
        {
            winners.Add(rule);
        }

        return _ordered
            .Where(rule => rule.Action == ParameterVisibility.Hide && !winners.Contains(rule))
            .ToList();
    }

    /// <summary>
    /// Yields each (tool, winning Hide rule) pair for the hidden parameter names of the given tools.
    /// Names whose ResolveHidden returns null are skipped; that happens only when a tool was built by another surface than this one.
    /// </summary>
    /// <param name="tools">The tools whose hidden parameter names are resolved.</param>
    /// <returns>The winning Hide rule per hidden parameter name.</returns>
    private IEnumerable<(ToolSurfaceTool Tool, ParameterVisibilityRule Rule)> WinningHideRules(IReadOnlyList<ToolSurfaceTool> tools)
    {
        foreach (var tool in tools)
        {
            foreach (var name in tool.HiddenParameters)
            {
                var rule = ResolveHidden(tool.Name, name);
                if (rule is not null)
                {
                    yield return (tool, rule);
                }
            }
        }
    }
}

/// <summary>
/// Records which parameters were stripped from an MCP tool's schema, keyed by tool instance.
/// </summary>
public static class StrippedToolParameters
{
    // Keyed by instance identity, so a host never sees another host's strips and nothing needs clearing.
    private static readonly ConditionalWeakTable<McpServerTool, string[]> Table = new();

    /// <summary>Records the parameter names stripped from a tool.</summary>
    /// <param name="tool">The tool whose schema was patched.</param>
    /// <param name="parameterNames">The parameter names removed from its schema.</param>
    public static void Record(McpServerTool tool, IReadOnlyList<string> parameterNames)
    {
        Table.AddOrUpdate(tool, parameterNames.OrderBy(name => name, StringComparer.Ordinal).ToArray());
    }

    /// <summary>Returns the parameter names recorded as stripped from a tool.</summary>
    /// <param name="tool">The tool to look up.</param>
    /// <returns>The stripped parameter names, or an empty list.</returns>
    public static IReadOnlyList<string> For(McpServerTool tool)
    {
        if (Table.TryGetValue(tool, out var names))
        {
            return names;
        }

        return [];
    }
}
