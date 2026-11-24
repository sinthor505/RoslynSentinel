using System.Diagnostics.CodeAnalysis;
using System.Reflection;

using Microsoft.Extensions.Options;

using ModelContextProtocol.Server;

namespace RoslynSentinel.Common;

/// <summary>What one <see cref="ToolsetService.SetEnabled"/> call did.</summary>
/// <param name="ToolSet">The set that was switched.</param>
/// <param name="Enabled">Whether the set is on after the call.</param>
/// <param name="Changed">False when the call was a no-op (already in the requested state).</param>
/// <param name="AddedTools">Tools added to the live surface by this call.</param>
/// <param name="RemovedTools">Tools removed from the live surface by this call.</param>
/// <param name="UnavailableTools">Set members this server flavor does not declare (or a startup tool already owns the name).</param>
/// <param name="EnabledToolSets">Every set that is on after the call.</param>
/// <param name="ActiveTools">Every tool on the live surface after the call, sorted.</param>
public sealed record ToolsetChange(
    ToolSetName ToolSet,
    bool Enabled,
    bool Changed,
    IReadOnlyList<string> AddedTools,
    IReadOnlyList<string> RemovedTools,
    IReadOnlyList<string> UnavailableTools,
    IReadOnlyList<ToolSetName> EnabledToolSets,
    IReadOnlyList<string> ActiveTools) : IOperationResult
{
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsError => false;

    [System.Text.Json.Serialization.JsonIgnore]
    public ResultStatus Status => Changed ? ResultStatus.Success : ResultStatus.AlreadyInState;

    [System.Text.Json.Serialization.JsonIgnore]
    public string? Code => null;

    [System.Text.Json.Serialization.JsonIgnore]
    public string? Message => null;
}

/// <summary>
/// Owns the runtime state behind <c>McpToolsetControl</c>: which on-demand toolsets are on, and the live
/// <see cref="McpServerTool"/> instances it added to the server's tool collection. A DI singleton (no statics),
/// with every state change under one lock. The SDK raises <c>notifications/tools/list_changed</c> itself when
/// the collection changes on a stateful transport (McpServerImpl subscribes ToolCollection.Changed), so this
/// service only mutates the collection - inside one <c>DeferChangedEvents</c> scope so a set of N tools is a
/// single notification, not N.
/// </summary>
/// <remarks>
/// Tools are built by <see cref="McpToolSchemaPatcher.CreateSentinelTool"/>, the same path startup registration
/// uses, so a dynamically added tool has an identical schema (x-tags, ReplaceSnippet limits, lean profile).
/// <c>ToolArgumentValidator</c>'s schema cache needs no invalidation: it is keyed by tool name and a re-created
/// tool of the same name has the same schema.
/// </remarks>
public sealed class ToolsetService
{
    private readonly object _gate = new();
    private readonly ToolSurfaceController _surface; // unused until Step 2
    private readonly McpServerOptions _serverOptions;
    private readonly IServiceProvider _services;
    private readonly IReadOnlySet<string> _supportedClassNames;
    private readonly Func<IEnumerable<Assembly>> _assemblySource;
    private readonly Dictionary<ToolSetName, List<McpServerTool>> _enabled = new();
    private int _toolListChangedRaised; // guarded by _gate
    private int _toolListSubscriptionRequests; // guarded by _gate

    /// <summary>
    /// Raised after a real enable or disable (not a no-op), outside the lock, with every set that is on after the call.
    /// A subscriber that throws is swallowed by <see cref="SetEnabled"/>.
    /// </summary>
    public event Action<IReadOnlyList<ToolSetName>>? EnabledToolSetsChanged;

    /// <summary>Creates the service over a tool surface controller, which supplies the server options it mutates.</summary>
    /// <param name="surface">The controller whose server options' <c>ToolCollection</c> is mutated.</param>
    /// <param name="services">The root provider tools are built against (and construct their targets from).</param>
    /// <param name="supportedClassNames">
    /// Tool classes whose dependencies the server registered for dynamic use. A tool declared by a class
    /// outside this set is reported unavailable rather than added in a state that would fail when called.
    /// </param>
    /// <param name="assemblySource">Assemblies to scan for tool methods; defaults to the loaded RoslynSentinel.Tools.* assemblies.</param>
    public ToolsetService(
        ToolSurfaceController surface,
        IServiceProvider services,
        IReadOnlySet<string> supportedClassNames,
        Func<IEnumerable<Assembly>>? assemblySource = null)
    {
        _surface = surface;
        _serverOptions = surface.ServerOptions;
        _services = services;
        _supportedClassNames = supportedClassNames;
        _assemblySource = assemblySource ?? LoadedToolAssemblies;
    }

    /// <param name="serverOptions">The server options whose <c>ToolCollection</c> is mutated.</param>
    /// <param name="services">The root provider tools are built against (and construct their targets from).</param>
    /// <param name="supportedClassNames">
    /// Tool classes whose dependencies the server registered for dynamic use. A tool declared by a class
    /// outside this set is reported unavailable rather than added in a state that would fail when called.
    /// </param>
    /// <param name="assemblySource">Assemblies to scan for tool methods; defaults to the loaded RoslynSentinel.Tools.* assemblies.</param>
    [Obsolete("Use the constructor that takes the ToolSurfaceController; this one builds a private controller.")]
    public ToolsetService(
        IOptions<McpServerOptions> serverOptions,
        IServiceProvider services,
        IReadOnlySet<string> supportedClassNames,
        Func<IEnumerable<Assembly>>? assemblySource = null)
        : this(new ToolSurfaceController(serverOptions), services, supportedClassNames, assemblySource)
    {
    }

    /// <summary>The sets currently on, in enum order.</summary>
    public IReadOnlyList<ToolSetName> EnabledToolSets
    {
        get
        {
            lock (_gate)
            {
                return _enabled.Keys.OrderBy(s => s).ToArray();
            }
        }
    }

    /// <summary>The tool names this service currently has on the live surface.</summary>
    public IReadOnlyCollection<string> DynamicToolNames
    {
        get
        {
            lock (_gate)
            {
                return _enabled.Values.SelectMany(t => t).Select(t => t.ProtocolTool.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();
            }
        }
    }

    /// <summary>
    /// Number of SetEnabled calls that mutated the tool collection (added or removed at least one tool). These are the
    /// changes that make the SDK raise notifications/tools/list_changed on a stateful transport; it is not proof that a
    /// client received the notification.
    /// </summary>
    public int ToolListChangedRaised
    {
        get
        {
            lock (_gate)
            {
                return _toolListChangedRaised;
            }
        }
    }

    /// <summary>
    /// Number of subscriptions/listen requests (2026-07-28 and later clients) that asked for notifications/tools/list_changed. Zero with a client on the initialize handshake (2025-11-25 and earlier), which gets the session-wide broadcast instead.
    /// </summary>
    public int ToolListSubscriptionRequests
    {
        get
        {
            lock (_gate)
            {
                return _toolListSubscriptionRequests;
            }
        }
    }

    /// <summary>
    /// Called by the incoming message filter for each subscriptions/listen request whose notifications.toolsListChanged is true.
    /// </summary>
    public void RecordToolListSubscriptionRequest()
    {
        lock (_gate)
        {
            _toolListSubscriptionRequests++;
        }
    }

    /// <summary>Switches <paramref name="toolSet"/> on or off. Idempotent.</summary>
    /// <exception cref="InvalidOperationException">The server has no tool collection to mutate.</exception>
    public ToolsetChange SetEnabled(ToolSetName toolSet, bool enabled)
    {
        if (!Enum.IsDefined(toolSet))
        {
            throw new ArgumentOutOfRangeException(nameof(toolSet), toolSet, $"Unknown toolset. Valid values: {string.Join(", ", Enum.GetNames<ToolSetName>())}.");
        }

        var collection = _serverOptions.ToolCollection
            ?? throw new InvalidOperationException("The server exposes no mutable tool collection, so toolsets cannot be switched at runtime.");

        ToolsetChange result;
        lock (_gate)
        {
            bool isOn = _enabled.ContainsKey(toolSet);
            var added = new List<string>();
            var removed = new List<string>();
            var unavailable = new List<string>();

            if (enabled && !isOn)
            {
                var candidates = ResolveCandidates();
                var built = new List<McpServerTool>();
                foreach (var name in ToolsetCatalog.GetToolNames(toolSet))
                {
                    if (!candidates.TryGetValue(name, out var candidate) || collection.TryGetPrimitive(name, out _))
                    {
                        unavailable.Add(name);
                        continue;
                    }

                    built.Add(McpToolSchemaPatcher.CreateSentinelTool(candidate.Method, candidate.ToolType, _services));
                }

                // Built first, added second: a creation failure above leaves the surface and state untouched.
                using (collection.DeferChangedEvents())
                {
                    foreach (var tool in built)
                    {
                        if (collection.TryAdd(tool))
                        {
                            added.Add(tool.ProtocolTool.Name);
                        }
                        else
                        {
                            unavailable.Add(tool.ProtocolTool.Name);
                        }
                    }

                    // The state must be updated before the scope is disposed: disposing raises
                    // ToolCollection.Changed, and the ToolSurfaceController's handler reads EnabledToolSets/DynamicToolNames.
                    _enabled[toolSet] = built.Where(t => added.Contains(t.ProtocolTool.Name)).ToList();
                }
            }
            else if (!enabled && isOn)
            {
                using (collection.DeferChangedEvents())
                {
                    foreach (var tool in _enabled[toolSet])
                    {
                        if (collection.Remove(tool))
                        {
                            removed.Add(tool.ProtocolTool.Name);
                        }
                    }

                    // Same ordering as the enable branch: state is updated before the scope is disposed.
                    _enabled.Remove(toolSet);
                }
            }

            if (added.Count + removed.Count > 0)
            {
                _toolListChangedRaised++;
            }

            result = new ToolsetChange(
                ToolSet: toolSet,
                Enabled: _enabled.ContainsKey(toolSet),
                Changed: enabled != isOn,
                AddedTools: added,
                RemovedTools: removed,
                UnavailableTools: unavailable,
                EnabledToolSets: _enabled.Keys.OrderBy(s => s).ToArray(),
                ActiveTools: collection.PrimitiveNames.OrderBy(n => n, StringComparer.Ordinal).ToArray());
        }

        if (result.Changed)
        {
            try
            {
                EnabledToolSetsChanged?.Invoke(result.EnabledToolSets);
            }
            catch
            {
                // Persistence subscribers must never fail a toolset switch.
            }
        }

        return result;
    }

    private Dictionary<string, (Type ToolType, MethodInfo Method)> ResolveCandidates()
    {
        var result = new Dictionary<string, (Type ToolType, MethodInfo Method)>(StringComparer.Ordinal);
        foreach (var (toolName, toolType, method) in McpToolSchemaPatcher.DiscoverToolMethods(_assemblySource())
                     .Where(t => ToolsetCatalog.AllToolNames.Contains(t.ToolName) && _supportedClassNames.Contains(t.ToolType.Name)))
        {
            result[toolName] = (toolType, method);
        }

        return result;
    }

    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "Server is not trimmed; assemblies are scanned reflectively on purpose.")]
    private static IEnumerable<Assembly> LoadedToolAssemblies() =>
        AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => a.GetName().Name?.StartsWith("RoslynSentinel.Tools.", StringComparison.Ordinal) == true)
            .ToArray();
}
