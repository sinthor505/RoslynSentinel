using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;

namespace RoslynSentinel.Common;

/// <summary>
/// Adds the startup tools to every <see cref="McpServerOptions"/> the options system builds (stateless HTTP builds one
/// per request), creating the collection when it is null. The tools are built once, on first use, by
/// <see cref="ToolSurfaceController.BuildStartupTools"/>.
/// </summary>
public sealed class ToolSurfaceStartupPopulation : IPostConfigureOptions<McpServerOptions>
{
    private readonly IServiceProvider _services;
    private readonly IEnumerable<ToolSurfaceStartupRequest> _requests;
    private readonly ToolAllowList? _allowList;
    private readonly Lazy<IReadOnlyList<McpServerTool>> _tools;

    /// <summary>Creates the population service from the startup requests.</summary>
    /// <param name="services">Service provider handed to <see cref="ToolSurfaceController.BuildStartupTools"/>.</param>
    /// <param name="requests">The startup requests, one per registration method, in tools/list order.</param>
    /// <param name="allowList">Optional allow list; null means no tool name is filtered out.</param>
    public ToolSurfaceStartupPopulation(IServiceProvider services, IEnumerable<ToolSurfaceStartupRequest> requests, ToolAllowList? allowList = null)
    {
        _services = services;
        _requests = requests;
        _allowList = allowList;
        _tools = new Lazy<IReadOnlyList<McpServerTool>>(
            () => ToolSurfaceController.BuildStartupTools(_requests.ToList(), _allowList, _services),
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>The startup tools in tools/list order, built on first read.</summary>
    public IReadOnlyList<McpServerTool> Tools => _tools.Value;

    /// <summary>
    /// Adds the startup tools to the given options, creating its tool collection when it is null, even when there are no tools.
    /// Runs inside options construction, so it never reads <c>IOptions&lt;McpServerOptions&gt;.Value</c> and never resolves
    /// <see cref="ToolSurfaceController"/> from the provider.
    /// </summary>
    /// <param name="name">The options instance name, or null for the default instance.</param>
    /// <param name="options">The options instance to populate.</param>
    public void PostConfigure(string? name, McpServerOptions options)
    {
        options.ToolCollection ??= new McpServerPrimitiveCollection<McpServerTool>();
        var tools = Tools;
        if (tools.Count == 0)
        {
            return;
        }

        foreach (var tool in tools)
        {
            // TryAdd keeps the first tool of a duplicate name, as the SDK's own registration does.
            options.ToolCollection.TryAdd(tool);
        }
    }
}
