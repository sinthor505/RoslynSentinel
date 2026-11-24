using System.Reflection;
using ModelContextProtocol.Server;

namespace RoslynSentinel.Common;

/// <summary>
/// A single schema adjustment applied to a tool's MCP schema during discovery.
/// Patches run in ascending <see cref="Order"/>.
/// </summary>
public interface IToolSchemaPatch
{
    /// <summary>Stable patch name, recorded in the tool surface snapshot.</summary>
    string Name { get; }

    /// <summary>Position in the patch pipeline; lower values run first.</summary>
    int Order { get; }

    /// <summary>Adjusts the tool's schema using its declaring method.</summary>
    /// <param name="tool">The MCP tool whose schema is being patched.</param>
    /// <param name="toolMethod">The method that declares the tool.</param>
    void Apply(McpServerTool tool, MethodInfo toolMethod);
}
