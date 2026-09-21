using System.Text.Json;
using System.Text.Json.Nodes;

using ModelContextProtocol.Protocol;

namespace RoslynSentinel.Tests.Advanced;

public static class McpTasksHarnessTestSupport
{
    // Added by AddMember (expected - used for diagnostics)
    /// <summary>
    /// Serializes a tool result's content for equality comparison, with <c>responseId</c> stripped
    /// from each text block's JSON - it's a fresh GUID per call (see
    /// <c>RoslynSentinel.Common.SentinelCallToolResult.ResponseId</c>), so a synchronous call and its
    /// task-polled counterpart never carry the same value even when everything else matches.
    /// </summary>
    public static string SerializeContent(CallToolResult result)
    {
        var normalizedBlocks = result.Content.Select(block =>
        {
            if (block is TextContentBlock textBlock && JsonNode.Parse(textBlock.Text) is JsonObject textJson)
            {
                textJson.Remove("responseId");
                return JsonSerializer.SerializeToNode(new TextContentBlock { Text = textJson.ToJsonString(), Annotations = textBlock.Annotations, Meta = textBlock.Meta });
            }

            return JsonSerializer.SerializeToNode(block);
        });

        return new JsonArray(normalizedBlocks.ToArray()).ToJsonString();
    }
}
