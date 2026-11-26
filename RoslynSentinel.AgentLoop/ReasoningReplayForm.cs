using System.Text.Json.Serialization;

namespace RoslynSentinel.Common;

/// <summary>
/// How earlier turns' reasoning is replayed on the next request.
/// </summary>
// Intentional deviation: serialized as names, see ThinkingMode.
[JsonConverter(typeof(JsonStringEnumConverter<ReasoningReplayForm>))]
public enum ReasoningReplayForm
{
    /// <summary>Earlier reasoning is sent as reasoning input items (today's behavior).</summary>
    Items,

    /// <summary>Earlier reasoning is sent as plain assistant message text.</summary>
    Content,

    /// <summary>Earlier reasoning is not sent.</summary>
    Drop,
}
