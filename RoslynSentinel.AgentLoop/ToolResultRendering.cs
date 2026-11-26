using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace RoslynSentinel.Common.AgentLoop;

/// <summary>
/// How the agent loop shows an MCP tool result to the model. <see cref="Json"/> is the wire text unchanged;
/// <see cref="Plain"/> and <see cref="Xml"/> re-render the same JSON with no escaping inside values, so code the
/// model copies back into a tool-call argument needs no un-escaping. Selected by
/// <see cref="ToolResultFormatOptions"/>.
/// </summary>
public enum ToolResultFormat
{
    Json,
    Plain,
    Xml,
}

/// <summary>Turns the final tool-result JSON into the text the model reads.</summary>
public interface IToolResultRenderer
{
    /// <summary>
    /// The model-facing text for <paramref name="resultJson"/>. A body that does not parse as a JSON object or array
    /// is returned unchanged.
    /// </summary>
    string Render(string resultJson);
}

/// <summary>The one factory for <see cref="IToolResultRenderer"/>s (no DI container).</summary>
public static class ToolResultRenderers
{
    private static readonly IToolResultRenderer JsonRenderer = new IdentityToolResultRenderer();
    private static readonly IToolResultRenderer PlainRenderer = new PlainToolResultRenderer();
    private static readonly IToolResultRenderer XmlRenderer = new XmlToolResultRenderer();

    public static IToolResultRenderer For(ToolResultFormat format) => format switch
    {
        ToolResultFormat.Json => JsonRenderer,
        ToolResultFormat.Plain => PlainRenderer,
        ToolResultFormat.Xml => XmlRenderer,
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Unknown tool result format."),
    };
}

/// <summary>The <see cref="ToolResultFormat.Json"/> renderer: the result text exactly as received.</summary>
internal sealed class IdentityToolResultRenderer : IToolResultRenderer
{
    public string Render(string resultJson) => resultJson;
}

/// <summary>
/// Shared walk and shared rules of the text renderers. Rules every renderer inherits: a body that does not parse as a
/// non-empty JSON object or array is returned unchanged; member order is kept; nothing inside a value is escaped (no
/// JSON escapes, no XML entities); numbers and booleans are written as their JSON text; the root object's members (or
/// the root array's items) are written at depth 0 with no wrapper; array items always carry their index. A subclass
/// supplies only the layout hooks. Lines end with <c>\n</c>; the final newline is removed.
/// </summary>
internal abstract class JsonTreeRenderer : IToolResultRenderer
{
    private static readonly JsonSerializerOptions QuoteOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public string Render(string resultJson)
    {
        ArgumentNullException.ThrowIfNull(resultJson);
        if (string.IsNullOrWhiteSpace(resultJson))
        {
            return resultJson;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(resultJson);
        }
        catch (JsonException)
        {
            return resultJson;
        }

        using (document)
        {
            var root = document.RootElement;
            var isObject = root.ValueKind == JsonValueKind.Object;
            var isArray = root.ValueKind == JsonValueKind.Array;
            if ((!isObject && !isArray) || (isObject && root.GetPropertyCount() == 0) || (isArray && root.GetArrayLength() == 0))
            {
                return resultJson;
            }

            var builder = new StringBuilder(resultJson.Length + 256);
            if (isObject)
            {
                foreach (var property in root.EnumerateObject())
                {
                    WriteMember(builder, property.Name, -1, property.Value, property.Name, 0);
                }
            }
            else
            {
                var index = 0;
                foreach (var item in root.EnumerateArray())
                {
                    WriteMember(builder, null, index, item, ItemPath(string.Empty, index), 0);
                    index++;
                }
            }

            if (builder.Length > 0 && builder[builder.Length - 1] == '\n')
            {
                builder.Length--;
            }

            return builder.ToString();
        }
    }

    // key is the member name; a null key means an array item and index is its position.
    private void WriteMember(StringBuilder builder, string? key, int index, JsonElement value, string path, int depth)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                if (value.GetPropertyCount() == 0)
                {
                    WriteEmptyContainer(builder, key, index, depth, "{}");
                    return;
                }

                OpenContainer(builder, key, index, depth);
                foreach (var property in value.EnumerateObject())
                {
                    WriteMember(builder, property.Name, -1, property.Value, ChildPath(path, property.Name), depth + 1);
                }

                CloseContainer(builder, key, index, depth);
                return;
            case JsonValueKind.Array:
                if (value.GetArrayLength() == 0)
                {
                    WriteEmptyContainer(builder, key, index, depth, "[]");
                    return;
                }

                OpenContainer(builder, key, index, depth);
                var itemIndex = 0;
                foreach (var item in value.EnumerateArray())
                {
                    WriteMember(builder, null, itemIndex, item, ItemPath(path, itemIndex), depth + 1);
                    itemIndex++;
                }

                CloseContainer(builder, key, index, depth);
                return;
            case JsonValueKind.String:
                WriteString(builder, key, index, value.GetString() ?? string.Empty, path, depth);
                return;
            default:
                WriteScalar(builder, key, index, value, depth);
                return;
        }
    }

    /// <summary>Starts a non-empty object or array member.</summary>
    protected abstract void OpenContainer(StringBuilder builder, string? key, int index, int depth);

    protected abstract void CloseContainer(StringBuilder builder, string? key, int index, int depth);

    /// <summary>An object or array with no members; <paramref name="emptyText"/> is <c>{}</c> or <c>[]</c>.</summary>
    protected abstract void WriteEmptyContainer(StringBuilder builder, string? key, int index, int depth, string emptyText);

    /// <summary>A string member; <paramref name="text"/> is the decoded text, <paramref name="path"/> its JSON path in ResultPager syntax.</summary>
    protected abstract void WriteString(StringBuilder builder, string? key, int index, string text, string path, int depth);

    /// <summary>A number, boolean or null member; <see cref="JsonElement.GetRawText"/> is its JSON text.</summary>
    protected abstract void WriteScalar(StringBuilder builder, string? key, int index, JsonElement value, int depth);

    protected static void Line(StringBuilder builder, string text) => builder.Append(text).Append('\n');

    /// <summary>Two spaces per depth level.</summary>
    protected static string Indent(int depth) => new(' ', depth * 2);

    /// <summary>True when <paramref name="text"/> contains a line break (<c>\n</c>; a CR before it is part of the line).</summary>
    protected static bool IsMultiLine(string text) => text.Contains('\n', StringComparison.Ordinal);

    /// <summary>True when one of the lines of <paramref name="text"/> (split on <c>\n</c>, trailing CR ignored) equals <paramref name="line"/>.</summary>
    protected static bool HasLine(string text, string line)
    {
        foreach (var candidate in text.Split('\n'))
        {
            if (string.Equals(candidate.TrimEnd('\r'), line, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The text as one JSON string literal (relaxed escaping: only quote, backslash and control characters are escaped).</summary>
    protected static string JsonQuote(string text) => JsonSerializer.Serialize(text, QuoteOptions);

    // ResultPager path syntax: property names separated by dots, array positions as [i].
    private static string ChildPath(string path, string key) => path.Length == 0 ? key : path + "." + key;

    private static string ItemPath(string path, int index) => path + "[" + index + "]";
}
