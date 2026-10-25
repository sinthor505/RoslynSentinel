using System.Collections.Frozen;
using System.Text.Json;

namespace RoslynSentinel.Common;

/// <summary>One parameter of a tool's input schema, as read by <see cref="ToolSignatureCatalog"/>.</summary>
public sealed record ToolParameterSpec(string Name, bool Required, string JsonType, IReadOnlyList<string>? EnumValues);

/// <summary>
/// Read-only lookup of tool names and their input-schema parameters. Pure model with no MCP types:
/// built once from tool InputSchema objects and immutable afterwards, so reads are thread-safe.
/// Tool and parameter names are matched ordinally (case-sensitive).
/// </summary>
public sealed class ToolSignatureCatalog
{
    private readonly IReadOnlyDictionary<string, IReadOnlyList<ToolParameterSpec>> _tools;
    private readonly IReadOnlyCollection<string> _toolNames;

    private ToolSignatureCatalog(IReadOnlyDictionary<string, IReadOnlyList<ToolParameterSpec>> tools, IReadOnlyCollection<string> toolNames)
    {
        _tools = tools;
        _toolNames = toolNames;
    }

    /// <summary>A catalog with no tools.</summary>
    public static ToolSignatureCatalog Empty { get; } = new(
        new Dictionary<string, IReadOnlyList<ToolParameterSpec>>(StringComparer.Ordinal).ToFrozenDictionary(StringComparer.Ordinal),
        Array.Empty<string>());

    /// <summary>Tool names in input order.</summary>
    public IReadOnlyCollection<string> ToolNames => _toolNames;

    /// <summary>
    /// Builds a catalog from (tool name, InputSchema) pairs. Malformed schema shapes are skipped, never thrown.
    /// </summary>
    public static ToolSignatureCatalog FromSchemas(IEnumerable<KeyValuePair<string, JsonElement>> inputSchemasByToolName)
    {
        ArgumentNullException.ThrowIfNull(inputSchemasByToolName);

        var tools = new Dictionary<string, IReadOnlyList<ToolParameterSpec>>(StringComparer.Ordinal);
        var names = new List<string>();
        foreach (var pair in inputSchemasByToolName)
        {
            if (string.IsNullOrEmpty(pair.Key) || tools.ContainsKey(pair.Key))
            {
                continue;
            }

            tools[pair.Key] = ReadParameters(pair.Value);
            names.Add(pair.Key);
        }

        return new ToolSignatureCatalog(tools.ToFrozenDictionary(StringComparer.Ordinal), names.AsReadOnly());
    }

    public bool Contains(string toolName) => toolName is not null && _tools.ContainsKey(toolName);

    public bool TryGetParameters(string toolName, out IReadOnlyList<ToolParameterSpec> parameters)
    {
        if (toolName is not null && _tools.TryGetValue(toolName, out var found))
        {
            parameters = found;
            return true;
        }

        parameters = Array.Empty<ToolParameterSpec>();
        return false;
    }

    private static IReadOnlyList<ToolParameterSpec> ReadParameters(JsonElement schema)
    {
        if (schema.ValueKind != JsonValueKind.Object
            || !schema.TryGetProperty("properties", out var properties)
            || properties.ValueKind != JsonValueKind.Object)
        {
            return Array.Empty<ToolParameterSpec>();
        }

        var required = ReadRequired(schema);
        var result = new List<ToolParameterSpec>();
        foreach (var property in properties.EnumerateObject())
        {
            result.Add(new ToolParameterSpec(
                property.Name,
                required.Contains(property.Name),
                ReadJsonType(property.Value),
                ReadEnumValues(property.Value)));
        }

        return result;
    }

    private static HashSet<string> ReadRequired(JsonElement schema)
    {
        var required = new HashSet<string>(StringComparer.Ordinal);
        if (schema.TryGetProperty("required", out var array) && array.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in array.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String && item.GetString() is { } name)
                {
                    required.Add(name);
                }
            }
        }

        return required;
    }

    private static string ReadJsonType(JsonElement property)
    {
        if (property.ValueKind != JsonValueKind.Object || !property.TryGetProperty("type", out var type))
        {
            return "string";
        }

        if (type.ValueKind == JsonValueKind.String)
        {
            return type.GetString() is { Length: > 0 } single ? single : "string";
        }

        if (type.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in type.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } candidate && candidate != "null")
                {
                    return candidate;
                }
            }
        }

        return "string";
    }

    private static IReadOnlyList<string>? ReadEnumValues(JsonElement property)
    {
        if (property.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (TryReadStringEnum(property, out var direct))
        {
            return direct;
        }

        foreach (var keyword in new[] { "anyOf", "oneOf" })
        {
            if (property.TryGetProperty(keyword, out var options) && options.ValueKind == JsonValueKind.Array)
            {
                foreach (var option in options.EnumerateArray())
                {
                    if (option.ValueKind == JsonValueKind.Object && TryReadStringEnum(option, out var nested))
                    {
                        return nested;
                    }
                }
            }
        }

        return null;
    }

    private static bool TryReadStringEnum(JsonElement schema, out IReadOnlyList<string>? values)
    {
        values = null;
        if (!schema.TryGetProperty("enum", out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var list = new List<string>();
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String && item.GetString() is { } value)
            {
                list.Add(value);
            }
        }

        if (list.Count == 0)
        {
            return false;
        }

        values = list;
        return true;
    }
}
