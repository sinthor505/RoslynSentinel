using System.Text.Json;
using System.Text.Json.Nodes;

using ModelContextProtocol.Protocol;

namespace RoslynSentinel.Server.Basic;

/// <summary>
/// Builds and stamps the <c>toolCall</c> echo (call id, tool name, truncated arguments) that the
/// outermost call-tool filter adds to every tool response, so a transcript, log line or offloaded
/// result can be tied back to the call that produced it without pairing by position. Lives next to
/// <see cref="ToolArgumentValidator"/>, the other filter helper in this project. See
/// docs/current/plans/plan_tool_call_echo.md for the design.
/// </summary>
public static class ToolCallEcho
{
    /// <summary>Length of the hex call id (a prefix of a GUID's "N" form).</summary>
    public const int ToolCallIdLength = 12;

    /// <summary>A string argument without a newline (path, name, mode, reason) is kept up to this many chars.</summary>
    public const int MaxSingleLineStringChars = 260;

    /// <summary>A multi-line string argument (code, diff) longer than this is cut to this many leading chars.</summary>
    public const int MultiLineKeepChars = 100;

    /// <summary>An array argument echoes at most this many elements, then a "+N more" marker.</summary>
    public const int MaxArrayElements = 3;

    /// <summary>If the serialized arguments still exceed this, every object/array-valued top-level argument is replaced by an "omitted" marker.</summary>
    public const int MaxArgumentsChars = 2048;

    /// <summary>Returns a fresh short call id: the first <see cref="ToolCallIdLength"/> hex chars of a GUID.</summary>
    public static string NewToolCallId() => Guid.NewGuid().ToString("N")[..ToolCallIdLength];

    /// <summary>
    /// Builds <c>{toolCallId, name, arguments}</c> with the truncation rules applied. Call this
    /// <em>before</em> the argument-validation filter runs: that filter repairs parameter-name case in
    /// place, and the echo must show what the caller actually sent.
    /// </summary>
    public static JsonObject CreateEcho(string toolCallId, string? toolName, IDictionary<string, JsonElement>? arguments)
    {
        var args = new JsonObject();
        if (arguments is not null)
        {
            foreach (var (key, value) in arguments)
            {
                args[key] = TruncateElement(value);
            }
        }

        if (args.ToJsonString(SharedJsonOptions.Compact).Length > MaxArgumentsChars && arguments is not null)
        {
            // Still too big after per-value truncation: drop the structured values entirely.
            foreach (var (key, value) in arguments)
            {
                if (value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                {
                    args[key] = $"...[omitted, {value.GetRawText().Length} chars]";
                }
            }
        }

        return new JsonObject
        {
            ["toolCallId"] = toolCallId,
            ["name"] = toolName,
            ["arguments"] = args,
        };
    }

    /// <summary>
    /// Stamps <paramref name="echo"/> onto the result's first text block (re-serialized with
    /// <see cref="SharedJsonOptions.Compact"/>: a bare <c>ToJsonString()</c> would HTML-escape
    /// angle brackets and quotes in every response): inserted as the first
    /// property when the text is a JSON object, otherwise the text is wrapped as
    /// <c>{"toolCall": ..., "message": "&lt;original text&gt;"}</c>. A result with no text block is left as-is.
    /// <c>StructuredContent</c> and <c>IsError</c> are never touched.
    /// </summary>
    public static void Stamp(CallToolResult result, JsonObject echo)
    {
        if (result.Content is null)
        {
            return;
        }

        foreach (var block in result.Content)
        {
            if (block is not TextContentBlock textBlock)
            {
                continue;
            }

            textBlock.Text = StampText(textBlock.Text, echo);
            return;
        }
    }

    private static string StampText(string? text, JsonObject echo)
    {
        text ??= string.Empty;

        if (text.AsSpan().TrimStart().StartsWith("{"))
        {
            try
            {
                if (JsonNode.Parse(text) is JsonObject body && !body.ContainsKey("toolCall"))
                {
                    body.Insert(0, "toolCall", echo.DeepClone());
                    return body.ToJsonString(SharedJsonOptions.Compact);
                }
            }
            catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException)
            {
                // Unparseable (or duplicate-key) JSON: fall through to the wrap path.
            }
        }

        return new JsonObject
        {
            ["toolCall"] = echo.DeepClone(),
            ["message"] = text,
        }.ToJsonString(SharedJsonOptions.Compact);
    }

    private static JsonNode? TruncateElement(JsonElement element)
    {
        try
        {
            return Truncate(JsonNode.Parse(element.GetRawText()));
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException)
        {
            return TruncateString(element.GetRawText());
        }
    }

    private static JsonNode? Truncate(JsonNode? node)
    {
        switch (node)
        {
            case null:
                return null;
            case JsonObject obj:
                var copy = new JsonObject();
                foreach (var (key, value) in obj)
                {
                    copy[key] = Truncate(value);
                }
                return copy;
            case JsonArray arr:
                var items = new JsonArray();
                for (var i = 0; i < arr.Count && i < MaxArrayElements; i++)
                {
                    items.Add(Truncate(arr[i]));
                }
                if (arr.Count > MaxArrayElements)
                {
                    items.Add($"...[+{arr.Count - MaxArrayElements} more]");
                }
                return items;
            case JsonValue val when val.TryGetValue<string>(out var s):
                return TruncateString(s);
            default:
                return node.DeepClone();
        }
    }

    /// <summary>Truncates by shape: a no-newline string keeps <see cref="MaxSingleLineStringChars"/>, a multi-line one keeps <see cref="MultiLineKeepChars"/> and reports the omitted chars and total line count.</summary>
    private static string TruncateString(string s)
    {
        var multiLine = s.Contains('\n');
        var keep = multiLine ? MultiLineKeepChars : MaxSingleLineStringChars;
        if (s.Length <= keep)
        {
            return s;
        }

        // Never split a surrogate pair at the cut point.
        var cut = keep;
        if (cut > 0 && char.IsHighSurrogate(s[cut - 1]))
        {
            cut--;
        }

        var omitted = s.Length - cut;
        if (!multiLine)
        {
            return $"{s[..cut]}...[+{omitted} chars]";
        }

        var lines = s.Count(c => c == '\n') + 1;
        return $"{s[..cut]}...[+{omitted} chars, {lines} lines]";
    }
}
