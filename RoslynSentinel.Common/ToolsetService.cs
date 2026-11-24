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
/// Runtime-toolset layer source behind <c>McpToolsetControl</c>: which classes are supported, how a set's tools are
/// built, the <see cref="EnabledToolSetsChanged"/> persistence event and the tools/list_changed counters. The
/// collection mutation and the runtime layer live in <see cref="ToolSurfaceController.ApplyToolset"/>, the single
/// writer; this service is a facade over it. The SDK raises <c>notifications/tools/list_changed</c> when the collection
/// changes on a stateful transport, and each apply runs inside one <c>DeferChangedEvents</c> scope, so N tools are one notification.
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
    private readonly ToolSurfaceController _surface;
    private readonly IServiceProvider _services;
    private readonly IReadOnlySet<string> _supportedClassNames;
    private readonly Func<IEnumerable<Assembly>> _assemblySource;
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

    /// <summary>The sets currently on, in enum order. Read from the controller's runtime layer.</summary>
    public IReadOnlyList<ToolSetName> EnabledToolSets => _surface.RuntimeToolSets;

    /// <summary>The tool names runtime toolsets currently have on the live surface. Read from the controller.</summary>
    public IReadOnlyCollection<string> DynamicToolNames => _surface.RuntimeToolNames;

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
    /// <remarks>
    /// The change is applied by <see cref="ToolSurfaceController.ApplyToolset"/>, which owns the collection and the
    /// runtime layer. This method supplies the tool factory, the tools/list_changed counter and the persistence event.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The server has no tool collection to mutate.</exception>
    public ToolsetChange SetEnabled(ToolSetName toolSet, bool enabled)
    {
        if (!Enum.IsDefined(toolSet))
        {
            throw new ArgumentOutOfRangeException(nameof(toolSet), toolSet, $"Unknown toolset. Valid values: {string.Join(", ", Enum.GetNames<ToolSetName>())}.");
        }

        // Candidates are resolved on the first tool the apply asks for, once per call; no-op and disable calls skip the scan.
        Dictionary<string, (Type ToolType, MethodInfo Method)>? candidates = null;
        Func<string, McpServerTool?> createTool = name =>
        {
            candidates ??= ResolveCandidates();
            return candidates.TryGetValue(name, out var candidate)
                ? McpToolSchemaPatcher.CreateSentinelTool(candidate.Method, candidate.ToolType, _services)
                : null;
        };

        var result = _surface.ApplyToolset(toolSet, enabled, ToolsetCatalog.GetToolNames(toolSet), createTool);
        lock (_gate)
        {
            if (result.AddedTools.Count + result.RemovedTools.Count > 0)
            {
                _toolListChangedRaised++;
            }
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
