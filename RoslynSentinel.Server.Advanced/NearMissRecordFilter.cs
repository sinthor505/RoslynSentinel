using System.Text.Json;
using System.Text.Json.Nodes;

using Microsoft.Extensions.Logging;

using ModelContextProtocol;
using ModelContextProtocol.Protocol;

using RoslynSentinel.Common;

namespace RoslynSentinel.Server.Advanced;

/// <summary>
/// The logic of the near-miss record call-tool filter (<c>AddNearMissRecordFilter</c> in <see cref="RoslynSentinelServiceExtensionsBasic"/>),
/// kept out of the filter lambda so tests can drive it without an McpRequestContext (same split as <see cref="ErrorPolicyFilter"/>).
/// It opens a <see cref="NearMissRecorder.CallScope"/> before the call and, after it, writes the scope's records into
/// <c>result._meta.nearMissSubstitutions</c> for a success and an error result alike. The result's content is never touched, so the records stay out of the
/// model-facing text. Public because Server.Advanced has no InternalsVisibleTo for its tests.
/// </summary>
/// <remarks>
/// The filter is registered directly inside the telemetry filter, outside every filter that can rebuild a result (the loop-latch refusal and
/// <see cref="CallToolFailureHandler"/>), so it sees the final result object of each path and Meta needs no support from those builders. The
/// filters outside it (identity, echo, telemetry) change a result in place.
/// </remarks>
public static class NearMissRecordFilter
{
    /// <summary>
    /// Runs <paramref name="invoke"/> inside a record scope. A pass-through, with no scope, when <see cref="NearMissRecorder.IsActive"/> is false.
    /// </summary>
    public static async ValueTask<CallToolResult> InvokeAsync(ILogger? logger, Func<ValueTask<CallToolResult>> invoke)
    {
        if (!NearMissRecorder.IsActive)
        {
            return await invoke();
        }

        using var scope = NearMissRecorder.Begin(logger);
        var result = await invoke();
        try
        {
            Attach(result, scope.Records);
        }
        catch (Exception ex)
        {
            // The records are an evaluation aid: failing to attach them must never break the call.
            logger?.LogWarning(ex, "Could not attach the near-miss records to the tool result.");
        }

        return result;
    }

    /// <summary>
    /// Writes <paramref name="records"/> as the <see cref="NearMissRecorder.MetaKey"/> array of <paramref name="result"/>'s <c>_meta</c>,
    /// keeping any other <c>_meta</c> entry. Does nothing when there are no records.
    /// </summary>
    public static void Attach(CallToolResult result, IReadOnlyList<NearMissSubstitution> records)
    {
        if (records.Count == 0)
        {
            return;
        }

        var array = new JsonArray();
        foreach (var record in records)
        {
            array.Add(JsonSerializer.SerializeToNode(record, McpJsonUtilities.DefaultOptions));
        }

        result.Meta ??= new JsonObject();
        result.Meta[NearMissRecorder.MetaKey] = array;
    }
}
