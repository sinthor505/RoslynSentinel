using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;

namespace RoslynSentinel.Common;

/// <summary>
/// Builds and holds the current <see cref="ToolSurfaceSnapshot"/> of the tools exposed to clients.
/// </summary>
public sealed class ToolSurfaceController
{
    /// <summary>Creates the controller from the server options and optional toolset service.</summary>
    /// <param name="serverOptions">Startup MCP server options that list the registered tools.</param>
    /// <param name="toolsets">Toolset service used for runtime enablement, or null when none is wired.</param>
    public ToolSurfaceController(IOptions<McpServerOptions> serverOptions, ToolsetService? toolsets = null)
        => throw new NotImplementedException(); // TODO implement (Step A2)

    /// <summary>The most recently built snapshot.</summary>
    public ToolSurfaceSnapshot Current => throw new NotImplementedException(); // TODO implement (Step A2)

    /// <summary>Rebuilds the snapshot and returns it; <paramref name="trigger"/> records the cause.</summary>
    /// <param name="trigger">Short label describing why the surface was rebuilt.</param>
    public ToolSurfaceSnapshot Refresh(string trigger) => throw new NotImplementedException(); // TODO implement (Step A2)
}
