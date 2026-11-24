using System.Collections.Generic;
using ModelContextProtocol.Server;

namespace RoslynSentinel.Common;

/// <summary>
/// Resolves parameter visibility rules against tools. Constructed from immutable <see cref="ToolParameterSurfaceOptions"/>.
/// Stub members are implemented in the step named on each member.
/// </summary>
public sealed class ParameterSurface
{
    /// <summary>Creates a surface over the given options. Implemented in Step 3a.</summary>
    /// <param name="options">The immutable rules to resolve against.</param>
    public ParameterSurface(ToolParameterSurfaceOptions options) => throw new NotImplementedException(); // TODO implement (Step 3a)

    /// <summary>Creates a surface from startup state. Implemented in Step 3a.</summary>
    /// <returns>A surface over <see cref="ToolParameterSurfaceOptions.FromStartup"/>.</returns>
    public static ParameterSurface FromStartup() => throw new NotImplementedException(); // TODO implement (Step 3a)

    /// <summary>The options this surface was built from.</summary>
    public ToolParameterSurfaceOptions Options { get; } = ToolParameterSurfaceOptions.Empty;

    /// <summary>True when any Hide rule exists.</summary>
    public bool HidesAnything { get; } = false;

    /// <summary>
    /// Returns the winning Hide rule for a tool's parameter, or null when the parameter is not hidden.
    /// Implemented in Step 3a.
    /// </summary>
    /// <param name="toolName">The MCP tool name.</param>
    /// <param name="parameterName">The parameter name on that tool.</param>
    /// <returns>The winning Hide rule, or null.</returns>
    public ParameterVisibilityRule? ResolveHidden(string toolName, string parameterName) => throw new NotImplementedException(); // TODO implement (Step 3a)

    /// <summary>
    /// Groups the Hide rules that strip parameters from the given tools, one group per winning Hide rule.
    /// Implemented in Step 3b.
    /// </summary>
    /// <param name="tools">The tools to attribute against.</param>
    /// <returns>The hidden parameter groups.</returns>
    public IReadOnlyList<HiddenParameterGroup> AttributeHidden(IReadOnlyList<ToolSurfaceTool> tools) => throw new NotImplementedException(); // TODO implement (Step 3b)

    /// <summary>
    /// Returns the Hide rules that matched no parameter of the given tools.
    /// Implemented in Step 3b.
    /// </summary>
    /// <param name="tools">The tools to check rules against.</param>
    /// <returns>The unmatched Hide rules.</returns>
    public IReadOnlyList<ParameterVisibilityRule> UnmatchedHideRules(IReadOnlyList<ToolSurfaceTool> tools) => throw new NotImplementedException(); // TODO implement (Step 3b)
}

/// <summary>
/// Records which parameters were stripped from an MCP tool's schema, keyed by tool instance.
/// Stub members are implemented in Step 3a.
/// </summary>
public static class StrippedToolParameters
{
    /// <summary>Records the parameter names stripped from a tool. Implemented in Step 3a.</summary>
    /// <param name="tool">The tool whose schema was patched.</param>
    /// <param name="parameterNames">The parameter names removed from its schema.</param>
    public static void Record(McpServerTool tool, IReadOnlyList<string> parameterNames) => throw new NotImplementedException(); // TODO implement (Step 3a)

    /// <summary>Returns the parameter names recorded as stripped from a tool. Implemented in Step 3a.</summary>
    /// <param name="tool">The tool to look up.</param>
    /// <returns>The stripped parameter names, or an empty list.</returns>
    public static IReadOnlyList<string> For(McpServerTool tool) => throw new NotImplementedException(); // TODO implement (Step 3a)
}
