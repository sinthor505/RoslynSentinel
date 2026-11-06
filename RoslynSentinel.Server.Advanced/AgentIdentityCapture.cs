using System.Diagnostics;
using System.Text.Json;

using RoslynSentinel.Common;

namespace RoslynSentinel.Server.Advanced;

/// <summary>
/// The logic of the agent identity call-tool filter (<c>AddAgentIdentityFilter</c> in
/// <see cref="RoslynSentinelServiceExtensionsBasic"/>), kept out of the filter lambda so tests can drive it without
/// building an McpRequestContext. Public rather than internal because Server.Advanced has no InternalsVisibleTo for its tests.
/// </summary>
public static class AgentIdentityCapture
{
    /// <summary>Argument key holding the caller's agent id.</summary>
    public const string AgentIdKey = "_agentId";

    /// <summary>Argument key holding the caller's agent type.</summary>
    public const string AgentTypeKey = "_agentType";

    /// <summary>
    /// Removes the identity keys from <paramref name="arguments"/>, latches the operating mode and sets
    /// <see cref="ToolCallContext.Current"/> for this call. Never throws: this runs outside the echo stamp and the
    /// telemetry count, so a failure here would surface unstamped and uncounted. On failure the call runs with a null context.
    /// </summary>
    public static void Enter(string? toolName, IDictionary<string, JsonElement>? arguments, Func<long> currentSeq)
    {
        try
        {
            // Keys first, so a later failure here still leaves arguments the validation filter accepts.
            var agentId = TakeString(arguments, AgentIdKey);
            var agentType = TakeString(arguments, AgentTypeKey);
            AgentOperatingModeOptions.ResolveOnce(agentId is not null);
            ToolCallContext.Set(new ToolCallContext(agentId, agentType, toolName ?? string.Empty, ReadString(arguments, "reason"), currentSeq()));
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Agent identity capture failed: {ex}");
            ToolCallContext.Set(null);
        }
    }

    private static string? TakeString(IDictionary<string, JsonElement>? arguments, string key)
    {
        var value = ReadString(arguments, key);
        arguments?.Remove(key);
        return value;
    }

    private static string? ReadString(IDictionary<string, JsonElement>? arguments, string key)
    {
        if (arguments is null || !arguments.TryGetValue(key, out var element) || element.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return element.GetString() is { Length: > 0 } text ? text : null;
    }
}
