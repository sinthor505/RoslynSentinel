using System.Text.Json.Nodes;

using ModelContextProtocol.Protocol;

namespace RoslynSentinel.Server.Advanced;

/// <summary>
/// Stamps the <c>workspaceStale</c> / <c>workspaceStaleReason</c> / <c>workspaceStaleRecovery</c> properties
/// onto a tool response's first text block while the workspace is stale after a failed reload. Modelled on
/// <see cref="HaltStamp"/>, so a caller learns the recovery call without a discovery round trip; the halt
/// stamp covers breakers, this one covers the sticky reload failure that refuses source edits. See
/// docs/current/proposals/proposal_workspace_reload_failure_visibility.md (slice 3).
/// </summary>
internal static class WorkspaceStaleStamp
{
    /// <summary>Property name that marks a body as already stamped (and is the skip key).</summary>
    internal const string IsWorkspaceStaleKey = "workspaceStale";

    /// <summary>
    /// Stamps the reason and recovery onto the result's first text block: inserted as the first three
    /// properties when the text is a JSON object, otherwise the text is wrapped as
    /// <c>{"workspaceStale":true,...,"message":"&lt;original text&gt;"}</c>. A body that already has a
    /// <c>workspaceStale</c> key (for example McpServerStatus, which reports it itself), or a result with no
    /// text block, is left as-is. <c>StructuredContent</c> and <c>IsError</c> are never touched.
    /// </summary>
    public static void Stamp(CallToolResult result, string reason, string recovery)
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

            textBlock.Text = StampText(textBlock.Text, reason, recovery);
            return;
        }
    }

    private static string StampText(string? text, string reason, string recovery)
    {
        text ??= string.Empty;

        if (text.AsSpan().TrimStart().StartsWith("{"))
        {
            try
            {
                if (JsonNode.Parse(text) is JsonObject body)
                {
                    if (body.ContainsKey(IsWorkspaceStaleKey))
                    {
                        return text;
                    }

                    body.Insert(0, IsWorkspaceStaleKey, true);
                    body.Insert(1, "workspaceStaleReason", reason);
                    body.Insert(2, "workspaceStaleRecovery", recovery);
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
            [IsWorkspaceStaleKey] = true,
            ["workspaceStaleReason"] = reason,
            ["workspaceStaleRecovery"] = recovery,
            ["message"] = text,
        }.ToJsonString(SharedJsonOptions.Compact);
    }
}
