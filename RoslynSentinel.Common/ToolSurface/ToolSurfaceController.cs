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
/// itself when toolsets are switched. A refresh that finds the same fingerprint and enabled toolsets keeps the
/// existing snapshot, so <see cref="ToolSurfaceSnapshot.Version"/> only moves when the surface really changed.
/// </remarks>
public sealed class ToolSurfaceController
{
    private readonly IOptions<McpServerOptions> _serverOptions;
    private readonly ToolsetService? _toolsets;
    private readonly IReadOnlyList<string> _schemaPatchNames;

    // Guards _current/_built/_subscribed only. It is never held while calling into ToolsetService or the SDK
    // collection: SetEnabled raises Changed on the mutating thread while holding the toolset lock, and our handler
    // calls Refresh, which reads the toolset service (taking that lock). Holding this lock across such a call
    // could deadlock against a thread that holds the toolset lock and is waiting for this one.
    private readonly object _lock = new();

    private volatile ToolSurfaceSnapshot _current = ToolSurfaceSnapshot.Empty;
    private volatile bool _built;
    private bool _subscribed;

    /// <summary>Creates the controller from the server options and optional toolset service.</summary>
    /// <param name="serverOptions">Startup MCP server options that list the registered tools.</param>
    /// <param name="toolsets">Toolset service used for runtime enablement, or null when none is wired.</param>
    /// <param name="patches">Schema patches registered with the host; null or empty means the default pipeline.</param>
    public ToolSurfaceController(IOptions<McpServerOptions> serverOptions, ToolsetService? toolsets = null, IEnumerable<IToolSchemaPatch>? patches = null)
    {
        _serverOptions = serverOptions;
        _toolsets = toolsets;

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
    /// <param name="createTool">Builds the tool for a name, or returns null when it cannot be built.</param>
    /// <returns>What the switch did.</returns>
    public ToolsetChange ApplyToolset(ToolSetName toolSet, bool enabled, IReadOnlyList<string> toolNames,
        Func<string, McpServerTool?> createTool)
        => throw new NotImplementedException(); // TODO implement (Step 2)

    /// <summary>The runtime toolsets currently on, in enum order.</summary>
    public IReadOnlyList<ToolSetName> RuntimeToolSets => throw new NotImplementedException(); // TODO implement (Step 2)

    /// <summary>The tool names runtime toolsets currently have on the live surface, ordinal order.</summary>
    public IReadOnlyCollection<string> RuntimeToolNames => throw new NotImplementedException(); // TODO implement (Step 2)

    /// <summary>The startup MCP server options whose tool collection this controller maintains.</summary>
    internal McpServerOptions ServerOptions => _serverOptions.Value;

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

        // Gather first, outside the controller lock (see _lock).
        var dynamicNames = _toolsets?.DynamicToolNames;
        var declaringClasses = ActiveToolSurface.DeclaringClassByTool();

        var tools = new List<(ToolSurfaceTool Row, string Schema)>();
        foreach (var tool in collection)
        {
            var name = tool.ProtocolTool.Name;
            var schemaElement = tool.ProtocolTool.InputSchema;
            var schemaText = schemaElement.ValueKind == JsonValueKind.Undefined ? string.Empty : schemaElement.GetRawText();

            var isDynamic = dynamicNames is not null && dynamicNames.Contains(name);
            var row = new ToolSurfaceTool(
                name,
                declaringClasses.TryGetValue(name, out var className) ? className : string.Empty,
                isDynamic ? ToolSurfaceOrigin.RuntimeToolset : ToolSurfaceOrigin.Startup,
                isDynamic ? ToolsetCatalog.FindSet(name)?.ToString() : null,
                ListedParameterNames(schemaElement),
                HiddenSchemaParams.HiddenFor(name));
            tools.Add((row, schemaText));
        }

        tools.Sort((a, b) => string.CompareOrdinal(a.Row.Name, b.Row.Name));

        var enabledToolSets = _toolsets?.EnabledToolSets.Select(s => s.ToString()).ToList() ?? [];
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
            text.Append(row.Name).Append('\0').Append(schema).Append('\0');
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString()))).ToLowerInvariant();
    }
}
