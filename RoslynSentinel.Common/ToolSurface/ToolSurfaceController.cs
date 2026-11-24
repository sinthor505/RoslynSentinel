using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;

namespace RoslynSentinel.Common;

/// <summary>
/// Builds and holds the current <see cref="ToolSurfaceSnapshot"/> of the tools exposed to clients.
/// </summary>
/// <remarks>
/// The snapshot is rebuilt from the live <c>McpServerOptions.ToolCollection</c>. The controller subscribes once to
/// the collection's <c>Changed</c> event (on the first <see cref="Refresh"/> that finds a collection) and refreshes
/// itself whenever the collection changes. A refresh that finds the same fingerprint and enabled toolsets keeps the
/// existing snapshot, so <see cref="ToolSurfaceSnapshot.Version"/> only moves when the surface really changed.
/// </remarks>
public sealed class ToolSurfaceController
{
    private readonly IOptions<McpServerOptions> _serverOptions;
    private readonly IReadOnlyList<string> _schemaPatchNames;

    // Serializes ApplyToolset calls. Held across the createTool callback and the SDK collection mutation; the
    // collection's Changed handler (Refresh) runs on the applying thread while this gate is held.
    private readonly object _applyGate = new();

    // Guards _current/_built/_subscribed only. Lock order is _applyGate -> _lock, never the reverse (an apply's own
    // Changed handler calls Refresh, which takes _lock while the apply holds _applyGate). _lock is never held
    // while calling into the SDK collection or ToolsetService. Refresh reads _runtimeLayer without a lock: the
    // layer is an immutable dictionary replaced wholesale by reference.
    private readonly object _lock = new();

    // The runtime toolsets currently on and the tool instances each apply added. Replaced, never mutated.
    private volatile IReadOnlyDictionary<ToolSetName, IReadOnlyList<McpServerTool>> _runtimeLayer =
        new Dictionary<ToolSetName, IReadOnlyList<McpServerTool>>();

    private volatile ToolSurfaceSnapshot _current = ToolSurfaceSnapshot.Empty;
    private volatile bool _built;
    private bool _subscribed;

    /// <summary>Creates the controller from the server options and optional schema patches.</summary>
    /// <param name="serverOptions">Startup MCP server options that list the registered tools.</param>
    /// <param name="patches">Schema patches registered with the host; null or empty means the default pipeline.</param>
    // Changed in place rather than migration-first: with an old and a new constructor side by side,
    // new ToolSurfaceController(options) is CS0121 (ambiguous), and MS.DI would pick the longer one and form a
    // cycle controller -> ToolsetService -> controller.
    public ToolSurfaceController(IOptions<McpServerOptions> serverOptions, IEnumerable<IToolSchemaPatch>? patches = null)
    {
        _serverOptions = serverOptions;

        // Mirrors the fallback in McpToolSchemaPatcher.CreateSentinelTool (an empty registration falls back to the
        // defaults), so the snapshot names the pipeline that built the schemas.
        var registered = patches?.OrderBy(p => p.Order).ThenBy(p => p.Name, StringComparer.Ordinal).Select(p => p.Name).ToList()
            ?? new List<string>();
        _schemaPatchNames = registered.Count > 0
            ? registered
            : DefaultToolSchemaPatches.All.Select(p => p.Name).ToList();
    }

    /// <summary>
    /// The most recently built snapshot. While no refresh has yet found a tool collection (a host that never
    /// started), each read retries the build with trigger <c>firstRead</c>.
    /// </summary>
    public ToolSurfaceSnapshot Current
    {
        get
        {
            if (!_built)
            {
                Refresh("firstRead");
            }

            return _current;
        }
    }

    /// <summary>
    /// Applies one runtime toolset: enable adds the tools <paramref name="createTool"/> builds for
    /// <paramref name="toolNames"/>, disable removes exactly the instances this controller added for the set.
    /// The only writer of runtime tools to the live collection.
    /// </summary>
    /// <param name="toolSet">The toolset being switched.</param>
    /// <param name="enabled">True to enable the set, false to disable it.</param>
    /// <param name="toolNames">Tool names the set declares for this server flavor.</param>
    /// <param name="createTool">
    /// Builds the tool for a name, or returns null when it cannot be built. Not called for a name already present
    /// in the collection. It runs under the apply gate and must not call back into this controller.
    /// </param>
    /// <returns>What the switch did.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="toolSet"/> is not a defined toolset.</exception>
    /// <exception cref="InvalidOperationException">The server has no tool collection to mutate.</exception>
    public ToolsetChange ApplyToolset(ToolSetName toolSet, bool enabled, IReadOnlyList<string> toolNames,
        Func<string, McpServerTool?> createTool)
    {
        if (!Enum.IsDefined(toolSet))
        {
            throw new ArgumentOutOfRangeException(nameof(toolSet), toolSet, $"Unknown toolset. Valid values: {string.Join(", ", Enum.GetNames<ToolSetName>())}.");
        }

        var collection = _serverOptions.Value.ToolCollection
            ?? throw new InvalidOperationException("The server exposes no mutable tool collection, so toolsets cannot be switched at runtime.");

        lock (_applyGate)
        {
            var layer = _runtimeLayer;
            bool isOn = layer.ContainsKey(toolSet);
            var added = new List<string>();
            var removed = new List<string>();
            var unavailable = new List<string>();

            if (enabled && !isOn)
            {
                var built = new List<McpServerTool>();
                foreach (var name in toolNames)
                {
                    if (collection.TryGetPrimitive(name, out _))
                    {
                        unavailable.Add(name);
                        continue;
                    }

                    var tool = createTool(name);
                    if (tool is null)
                    {
                        unavailable.Add(name);
                        continue;
                    }

                    built.Add(tool);
                }

                // Built first, added second: a creation failure above leaves the surface and layer untouched.
                using (collection.DeferChangedEvents())
                {
                    var addedTools = new List<McpServerTool>();
                    foreach (var tool in built)
                    {
                        if (collection.TryAdd(tool))
                        {
                            added.Add(tool.ProtocolTool.Name);
                            addedTools.Add(tool);
                        }
                        else
                        {
                            unavailable.Add(tool.ProtocolTool.Name);
                        }
                    }

                    // The layer is replaced before the scope is disposed: disposing raises Changed, and the
                    // handler's Refresh reads the layer, so it must already be consistent (A2 ordering guarantee).
                    var next = new Dictionary<ToolSetName, IReadOnlyList<McpServerTool>>(layer)
                    {
                        [toolSet] = addedTools,
                    };
                    _runtimeLayer = next;
                }
            }
            else if (!enabled && isOn)
            {
                using (collection.DeferChangedEvents())
                {
                    foreach (var tool in layer[toolSet])
                    {
                        if (collection.Remove(tool))
                        {
                            removed.Add(tool.ProtocolTool.Name);
                        }
                    }

                    // Same ordering as the enable branch: the layer is replaced before the scope is disposed.
                    var next = new Dictionary<ToolSetName, IReadOnlyList<McpServerTool>>(layer);
                    next.Remove(toolSet);
                    _runtimeLayer = next;
                }
            }

            var current = _runtimeLayer;
            return new ToolsetChange(
                ToolSet: toolSet,
                Enabled: current.ContainsKey(toolSet),
                Changed: enabled != isOn,
                AddedTools: added,
                RemovedTools: removed,
                UnavailableTools: unavailable,
                EnabledToolSets: current.Keys.OrderBy(s => s).ToArray(),
                ActiveTools: collection.PrimitiveNames.OrderBy(n => n, StringComparer.Ordinal).ToArray());
        }
    }

    /// <summary>The runtime toolsets currently on, in enum order.</summary>
    public IReadOnlyList<ToolSetName> RuntimeToolSets => _runtimeLayer.Keys.OrderBy(s => s).ToArray();

    /// <summary>The tool names runtime toolsets currently have on the live surface, ordinal order.</summary>
    public IReadOnlyCollection<string> RuntimeToolNames => _runtimeLayer.Values
        .SelectMany(tools => tools.Select(t => t.ProtocolTool.Name))
        .Distinct(StringComparer.Ordinal)
        .OrderBy(n => n, StringComparer.Ordinal)
        .ToArray();

    /// <summary>Rebuilds the snapshot and returns it; <paramref name="trigger"/> records the cause.</summary>
    /// <param name="trigger">Short label describing why the surface was rebuilt.</param>
    public ToolSurfaceSnapshot Refresh(string trigger)
    {
        var collection = _serverOptions.Value.ToolCollection;
        if (collection is null)
        {
            // No tools to describe yet; leave _built false so a later call retries. Read the field, not Current.
            return _current;
        }

        bool subscribe;
        lock (_lock)
        {
            subscribe = !_subscribed;
            _subscribed = true;
        }

        if (subscribe)
        {
            // Subscribed before gathering so a change between the gather and the subscribe cannot be missed.
            // The SDK raises Changed on the mutating thread (McpServerPrimitiveCollection.cs:92-122), so a handler
            // that threw would fail the caller's SetEnabled/TryAdd; swallow, and log nothing from the hot path.
            collection.Changed += (_, _) =>
            {
                try
                {
                    Refresh("toolCollectionChanged");
                }
                catch
                {
                    // Swallowed on purpose: see above.
                }
            };
        }

        // Gather first, outside the controller lock (see _lock). The layer is read once, without a lock.
        var layer = _runtimeLayer;
        var layerSetByTool = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (set, layerTools) in layer.OrderBy(kv => kv.Key))
        {
            foreach (var layerTool in layerTools)
            {
                // TryAdd, not ToDictionary: two sets sharing a tool name must not throw; the first set wins.
                layerSetByTool.TryAdd(layerTool.ProtocolTool.Name, set.ToString());
            }
        }

        var declaringClasses = ActiveToolSurface.DeclaringClassByTool();

        var tools = new List<(ToolSurfaceTool Row, string Schema)>();
        foreach (var tool in collection)
        {
            var name = tool.ProtocolTool.Name;
            var schemaElement = tool.ProtocolTool.InputSchema;
            var schemaText = schemaElement.ValueKind == JsonValueKind.Undefined ? string.Empty : schemaElement.GetRawText();

            // The layer alone decides origin and set: a tool that a runtime apply added is RuntimeToolset with that set.
            string? toolSetName = layerSetByTool.TryGetValue(name, out var layerSet) ? layerSet : null;

            var row = new ToolSurfaceTool(
                name,
                declaringClasses.TryGetValue(name, out var className) ? className : string.Empty,
                toolSetName is null ? ToolSurfaceOrigin.Startup : ToolSurfaceOrigin.RuntimeToolset,
                toolSetName,
                ListedParameterNames(schemaElement),
                HiddenSchemaParams.HiddenFor(name));
            tools.Add((row, schemaText));
        }

        tools.Sort((a, b) => string.CompareOrdinal(a.Row.Name, b.Row.Name));

        var enabledToolSets = layer.Keys.OrderBy(s => s).Select(s => s.ToString()).ToList();
        var fingerprint = ComputeFingerprint(tools);

        lock (_lock)
        {
            _built = true;
            var current = _current;
            if (current.Version > 0
                && current.Fingerprint == fingerprint
                && current.EnabledToolSets.SequenceEqual(enabledToolSets))
            {
                return current;
            }

            var next = new ToolSurfaceSnapshot(
                current.Version + 1,
                DateTime.UtcNow,
                trigger,
                fingerprint,
                tools.Select(t => t.Row).ToList(),
                enabledToolSets,
                _schemaPatchNames);
            _current = next;
            return next;
        }
    }

    private static List<string> ListedParameterNames(JsonElement schema)
    {
        var names = new List<string>();
        if (schema.ValueKind == JsonValueKind.Object
            && schema.TryGetProperty("properties", out var properties)
            && properties.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in properties.EnumerateObject())
            {
                names.Add(property.Name);
            }
        }

        names.Sort(StringComparer.Ordinal);
        return names;
    }

    private static string ComputeFingerprint(List<(ToolSurfaceTool Row, string Schema)> sortedTools)
    {
        var text = new StringBuilder();
        foreach (var (row, schema) in sortedTools)
        {
            // The attribution is part of the fingerprint so a pure attribution change moves the version.
            text.Append(row.Name).Append('\0').Append(schema).Append('\0').Append(row.ToolSet ?? "").Append('\0');
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString()))).ToLowerInvariant();
    }
}
