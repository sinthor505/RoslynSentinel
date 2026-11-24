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
    public ToolSurfaceController(IOptions<McpServerOptions> serverOptions, ToolsetService? toolsets = null)
    {
        _serverOptions = serverOptions;
        _toolsets = toolsets;
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
                [
                    "ConsumesTags",
                    "BatchItemFields",
                    "ReplaceSnippetLimits",
                    "HiddenParameters",
                ]); // TODO read from the IToolSchemaPatch pipeline (Step B3)
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
