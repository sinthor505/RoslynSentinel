namespace RoslynSentinel.Common;

/// <summary>
/// Explicit, never-serialized route for a tool result to say which tools it suggests next when the suggestion is not
/// built through <see cref="ToolCallHint"/> (set on <c>SentinelCallToolResult.GuidanceHint</c>, <c>[JsonIgnore]</c>).
/// <c>ToolResultMarshaller</c> forwards it into the open <see cref="ToolGuidanceCollector"/> (the telemetry filter only
/// sees the already-serialized <c>CallToolResult</c>), where it is merged with the <see cref="ToolCallHint"/> recordings
/// and de-duplicated by tool name. Like those, it feeds guidance-adherence counts only.
/// </summary>
/// <param name="SuggestedToolNames">Tool names the result suggests calling next.</param>
/// <param name="OriginatingToolName">
/// Optional and informational: the originating tool is always the request's tool name as seen by the telemetry
/// filter, which ignores this value.
/// </param>
public sealed record GuidanceHint(IReadOnlyList<string> SuggestedToolNames, string? OriginatingToolName = null);
