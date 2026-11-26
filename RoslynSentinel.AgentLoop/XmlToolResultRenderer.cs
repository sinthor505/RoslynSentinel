using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RoslynSentinel.Common.AgentLoop;

/// <summary>
/// <see cref="ToolResultFormat.Xml"/>: raw XML-shaped text, deliberately not well-formed because text is never entity
/// escaped (<c>&amp;lt;</c> in C# generics made the model copy entities). <c>&lt;key&gt;value&lt;/key&gt;</c> with
/// nested elements indented two spaces, array items as <c>&lt;item i="N"&gt;</c>, null as <c>&lt;key/&gt;</c>, an empty
/// object, array or string as <c>&lt;key&gt;&lt;/key&gt;</c>. A key that is not a valid XML name becomes
/// <c>&lt;entry key="..."&gt;</c> (only <c>"</c> and <c>&amp;</c> are escaped, in the attribute). A multi-line string
/// starts on the line after the open tag and its close tag sits on its own line, so copied lines carry no tag text. A
/// string holding its own closing tag is written as <c>&lt;key encoding="json"&gt;"..."&lt;/key&gt;</c> (JSON-quoted,
/// with <c>&lt;/</c> written <c>&lt;\/</c> so the closing tag cannot recur inside).
/// </summary>
internal sealed class XmlToolResultRenderer : JsonTreeRenderer
{
    // XML name without a colon: a letter or underscore, then letters, digits, combining marks, '_', '.' or '-'.
    private static readonly Regex XmlName = new(
        @"^[\p{L}_][\p{L}\p{Nd}\p{Mn}_.\-]*\z",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    protected override void OpenContainer(StringBuilder builder, string? key, int index, int depth)
        => Line(builder, $"{Indent(depth)}<{TagName(key)}{Attributes(key, index)}>");

    protected override void CloseContainer(StringBuilder builder, string? key, int index, int depth)
        => Line(builder, $"{Indent(depth)}</{TagName(key)}>");

    protected override void WriteEmptyContainer(StringBuilder builder, string? key, int index, int depth, string emptyText)
        => Line(builder, $"{Indent(depth)}<{TagName(key)}{Attributes(key, index)}></{TagName(key)}>");

    protected override void WriteString(StringBuilder builder, string? key, int index, string text, string path, int depth)
    {
        var name = TagName(key);
        var attributes = Attributes(key, index);
        var indent = Indent(depth);
        var closeTag = $"</{name}>";

        if (text.Contains(closeTag, StringComparison.Ordinal))
        {
            var quoted = JsonQuote(text).Replace("</", "<\\/", StringComparison.Ordinal);
            Line(builder, $"{indent}<{name}{attributes} encoding=\"json\">{quoted}{closeTag}");
            return;
        }

        if (IsMultiLine(text))
        {
            Line(builder, $"{indent}<{name}{attributes}>");
            builder.Append(text).Append('\n');
            Line(builder, $"{indent}{closeTag}");
            return;
        }

        Line(builder, $"{indent}<{name}{attributes}>{text}{closeTag}");
    }

    protected override void WriteScalar(StringBuilder builder, string? key, int index, JsonElement value, int depth)
    {
        var name = TagName(key);
        var attributes = Attributes(key, index);
        Line(
            builder,
            value.ValueKind == JsonValueKind.Null
                ? $"{Indent(depth)}<{name}{attributes}/>"
                : $"{Indent(depth)}<{name}{attributes}>{value.GetRawText()}</{name}>");
    }

    // A null key is an array item. A key that is not an XML name (empty, spaces, a colon, a leading digit) becomes <entry key="...">.
    private static string TagName(string? key)
        => key is null ? "item" : IsXmlName(key) ? key : "entry";

    private static string Attributes(string? key, int index)
    {
        if (key is null)
        {
            return $" i=\"{index}\"";
        }

        return IsXmlName(key)
            ? string.Empty
            : $" key=\"{key.Replace("&", "&amp;", StringComparison.Ordinal).Replace("\"", "&quot;", StringComparison.Ordinal)}\"";
    }

    private static bool IsXmlName(string key) => XmlName.IsMatch(key);
}
