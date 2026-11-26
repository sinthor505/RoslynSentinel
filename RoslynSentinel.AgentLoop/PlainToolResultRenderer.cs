using System.Text;
using System.Text.Json;

namespace RoslynSentinel.Common.AgentLoop;

/// <summary>
/// <see cref="ToolResultFormat.Plain"/>: <c>key: value</c> per scalar, objects nested by two-space indentation, array
/// items as <c>- [i]</c> lines (<c>- [i] value</c> for a scalar; an object or array item is <c>- [i]</c> followed by its
/// content indented two more spaces), <c>null</c>, <c>{}</c> and <c>[]</c> written literally. A string is a block when it is
/// empty, multi-line or has leading or trailing whitespace: a <c>key:</c> line (<c>- [i]:</c> for an array item), the raw
/// text from column 0, then a line <c>(end of &lt;path&gt;)</c> at column 0, where the path uses ResultPager syntax
/// (<c>successData.hits[3].text</c>). A string that holds its own terminator line is written as a JSON-quoted single-line
/// value instead.
/// </summary>
internal sealed class PlainToolResultRenderer : JsonTreeRenderer
{
    protected override void OpenContainer(StringBuilder builder, string? key, int index, int depth)
        => Line(builder, key is null ? $"{Indent(depth)}- [{index}]" : $"{Indent(depth)}{key}:");

    protected override void CloseContainer(StringBuilder builder, string? key, int index, int depth)
    {
        // Indentation carries the structure; nothing closes a container.
    }

    protected override void WriteEmptyContainer(StringBuilder builder, string? key, int index, int depth, string emptyText)
        => WriteInline(builder, key, index, depth, emptyText);

    protected override void WriteString(StringBuilder builder, string? key, int index, string text, string path, int depth)
    {
        if (!IsBlock(text))
        {
            WriteInline(builder, key, index, depth, text);
            return;
        }

        var terminator = $"(end of {path})";
        if (HasLine(text, terminator))
        {
            WriteInline(builder, key, index, depth, JsonQuote(text));
            return;
        }

        Line(builder, key is null ? $"{Indent(depth)}- [{index}]:" : $"{Indent(depth)}{key}:");
        builder.Append(text).Append('\n');
        Line(builder, terminator);
    }

    protected override void WriteScalar(StringBuilder builder, string? key, int index, JsonElement value, int depth)
        => WriteInline(builder, key, index, depth, value.GetRawText());

    private static bool IsBlock(string text)
        => text.Length == 0 || IsMultiLine(text) || char.IsWhiteSpace(text[0]) || char.IsWhiteSpace(text[text.Length - 1]);

    private static void WriteInline(StringBuilder builder, string? key, int index, int depth, string value)
        => Line(builder, key is null ? $"{Indent(depth)}- [{index}] {value}" : $"{Indent(depth)}{key}: {value}");
}
