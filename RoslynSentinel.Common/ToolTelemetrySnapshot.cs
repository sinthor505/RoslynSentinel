using System.Text.Json.Serialization;

namespace RoslynSentinel.Common;

/// <summary>
/// Immutable point-in-time view of <see cref="ToolTelemetry"/>, reported as <c>toolTelemetry</c> on McpServerStatus.
/// Lists only tools with at least one recorded call, most-called first (then by name). Zero and null fields are omitted
/// from the wire to keep it small.
/// </summary>
/// <param name="InstanceId">Which server instance these counters belong to (see <see cref="ToolTelemetry.ResolveInstanceId"/>).</param>
/// <param name="ServerStartedUtc">When the telemetry (and so the counting) started.</param>
/// <param name="BuildSha">Commit the running binary was built from; null for an unstamped build.</param>
/// <param name="Tools">One entry per tool called at least once.</param>
public sealed record ToolTelemetrySnapshot(
    string InstanceId,
    DateTime ServerStartedUtc,
    string? BuildSha,
    IReadOnlyList<ToolCallStats> Tools);

/// <summary>
/// Counters for one tool, read as one consistent unit. <see cref="Successes"/> + <see cref="Errors"/> = <see cref="Calls"/>.
/// The four guidance counters are keyed on the ORIGINATING tool (the one whose hints suggested a next tool): suggestions
/// issued, followed by the very next matching call, followed by a call that succeeded, and ignored (expired unfollowed).
/// They are approximate: hints built but not emitted over-count issued, and concurrent subagents share one stream.
/// </summary>
public sealed record ToolCallStats(
    string ToolName,
    long Calls,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] long Successes,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] long Errors,
    IReadOnlyDictionary<string, int>? ErrorsByCode,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] long TotalDurationMs,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] long MaxDurationMs,
    DateTime FirstCallUtc,
    DateTime LastCallUtc,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] long GuidanceIssued = 0,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] long GuidanceFollowed = 0,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] long GuidanceFollowedSucceeded = 0,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] long GuidanceIgnored = 0);
