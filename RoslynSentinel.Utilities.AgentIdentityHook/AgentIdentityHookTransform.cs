using System.Text.Json;

namespace RoslynSentinel.Utilities.AgentIdentityHook;

/// <summary>
/// Rewrites a Claude Code PreToolUse hook payload so an RoslynSentinel MCP call carries the caller identity
/// in tool_input (_agentId and _agentType). A null result means the call runs with no identity (fail open),
/// so a hook problem never blocks a tool call.
/// </summary>
public static class AgentIdentityHookTransform
{
    /// <summary>
    /// Returns the PreToolUse hook output JSON for one hook input payload, or null to pass the call through unchanged.
    /// </summary>
    public static byte[]? Transform(byte[] input)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(input);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("tool_input", out JsonElement toolInput)
                || toolInput.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            string agentId = "main";
            if (root.TryGetProperty("agent_id", out JsonElement idElement)
                && idElement.ValueKind == JsonValueKind.String
                && idElement.GetString() is { Length: > 0 } id)
            {
                agentId = id;
            }

            string? agentType = null;
            if (root.TryGetProperty("agent_type", out JsonElement typeElement)
                && typeElement.ValueKind == JsonValueKind.String
                && typeElement.GetString() is { Length: > 0 } type)
            {
                agentType = type;
            }

            using MemoryStream stream = new();
            using (Utf8JsonWriter writer = new(stream))
            {
                writer.WriteStartObject();
                writer.WriteStartObject("hookSpecificOutput");
                writer.WriteString("hookEventName", "PreToolUse");
                writer.WriteStartObject("updatedInput");
                foreach (JsonProperty property in toolInput.EnumerateObject())
                {
                    if (property.NameEquals("_agentId") || property.NameEquals("_agentType"))
                    {
                        continue;
                    }

                    property.WriteTo(writer);
                }

                writer.WriteString("_agentId", agentId);
                if (agentType is not null)
                {
                    writer.WriteString("_agentType", agentType);
                }

                writer.WriteEndObject();
                writer.WriteEndObject();
                writer.WriteEndObject();
            }

            return stream.ToArray();
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
