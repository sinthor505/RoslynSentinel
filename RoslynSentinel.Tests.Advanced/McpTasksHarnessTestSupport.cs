using System.Text.Json;
using System.Text.Json.Nodes;

using ModelContextProtocol.Protocol;

namespace RoslynSentinel.Tests.Advanced;

public static class McpTasksHarnessTestSupport
{
       /// <summary>
    /// Serializes a tool result's content for equality comparison, with <c>toolCall.toolCallId</c>
    /// stripped from each text block's JSON - it's a fresh random id per call (stamped by the
    /// tool-call echo filter, see <c>RoslynSentinel.Server.Basic.ToolCallEcho</c>), so a synchronous
    /// call and its task-polled counterpart never carry the same value even when everything else
    /// matches. The rest of <c>toolCall</c> (name, echoed arguments) is kept and compared.
    /// </summary>
    public static string SerializeContent(CallToolResult result)
    {
        var normalizedBlocks = result.Content.Select(block =>
        {
            if (block is TextContentBlock textBlock && JsonNode.Parse(textBlock.Text) is JsonObject textJson)
            {
                if (textJson["toolCall"] is JsonObject toolCall)
                {
                    toolCall.Remove("toolCallId");
                }

                return JsonSerializer.SerializeToNode(new TextContentBlock { Text = textJson.ToJsonString(), Annotations = textBlock.Annotations, Meta = textBlock.Meta });
            }

            return JsonSerializer.SerializeToNode(block);
        });

        return new JsonArray(normalizedBlocks.ToArray()).ToJsonString();
    }
}
