using System.Text.Json.Nodes;

using ModelContextProtocol.Protocol;

namespace RoslynSentinel.Server.Basic;

/// <summary>
/// Stamps the four <c>isSessionHalted</c> / <c>sessionHaltKind</c> / <c>sessionHaltReason</c> /
/// <c>sessionHaltRecovery</c> properties from a <see cref="HaltInfo"/> onto a tool response's first
/// text block, so a caller learns the exact recovery call without a discovery round trip. Modelled on
/// <see cref="ToolCallEcho.Stamp"/>. See docs/current/plans/plan_external_file_drift_tool_and_halt_stamping.md
/// (Decision 3).
/// </summary>
internal static class HaltStamp
{
    /// <summary>Property name that marks a body as already stamped (and is the skip key).</summary>
    internal const string IsSessionHaltedKey = "isSessionHalted";

    /// <summary>
    /// Stamps <paramref name="info"/> onto the result's first text block: inserted as the first four
    /// properties when the text is a JSON object, otherwise the text is wrapped as
    /// <c>{"isSessionHalted":true,...,"message":"&lt;original text&gt;"}</c>. A body that already has an
    /// <c>isSessionHalted</c> key, or a result with no text block, is left as-is. <c>StructuredContent</c>
    /// and <c>IsError</c> are never touched.
    /// </summary>
    public static void Stamp(CallToolResult result, HaltInfo info)
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

            textBlock.Text = StampText(textBlock.Text, info);
            return;
        }
    }

    private static string StampText(string? text, HaltInfo info)
    {
        text ??= string.Empty;

        if (text.AsSpan().TrimStart().StartsWith("{"))
        {
            try
            {
                if (JsonNode.Parse(text) is JsonObject body)
                {
                    if (body.ContainsKey(IsSessionHaltedKey))
                    {
                        return text;
                    }

                    body.Insert(0, IsSessionHaltedKey, true);
                    body.Insert(1, "sessionHaltKind", info.Kind);
                    body.Insert(2, "sessionHaltReason", info.Reason);
                    body.Insert(3, "sessionHaltRecovery", info.Recovery);
                    return body.ToJsonString(SharedJsonOptions.Compact);
                }
            }
            catch (Exception ex) when (ex is System.Text.Json.JsonException or ArgumentException or InvalidOperationException)
            {
                // Unparseable (or duplicate-key) JSON: fall through to the wrap path.
            }
        }

        return new JsonObject
        {
            [IsSessionHaltedKey] = true,
            ["sessionHaltKind"] = info.Kind,
            ["sessionHaltReason"] = info.Reason,
            ["sessionHaltRecovery"] = info.Recovery,
            ["message"] = text,
        }.ToJsonString(SharedJsonOptions.Compact);
    }
}
