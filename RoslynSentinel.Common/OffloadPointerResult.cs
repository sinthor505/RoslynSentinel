using System.Text.Json.Nodes;

namespace RoslynSentinel.Common;

/// <summary>
/// The pointer that replaces an over-threshold tool result body in the offload filter. Serialized with
/// <c>SentinelToolFactory.WireSerializerOptions</c> (camelCase, nulls omitted, relaxed encoder).
/// </summary>
public sealed record OffloadPointerResult
{
    /// <summary>Solution root the offloaded result belongs to.</summary>
    public string? SolutionRoot { get; init; }

    /// <summary>True when the body was moved behind <see cref="ResultId"/>.</summary>
    public bool Offloaded { get; init; }

    /// <summary>Id to fetch the stored body with GetLargeResult.</summary>
    public string? ResultId { get; init; }

    /// <summary>Size of the offloaded body in bytes.</summary>
    public int SizeBytes { get; init; }

    /// <summary>Size of the offloaded body in characters.</summary>
    public int SizeChars { get; init; }

    /// <summary>Number of items in the offloaded body, when it is a list.</summary>
    public int? ItemCount { get; init; }

    /// <summary>Whether the offloaded body reports an error.</summary>
    public bool? IsError { get; init; }

    /// <summary>Status message carried by the offloaded body, when present.</summary>
    public string? StatusMessage { get; init; }

    /// <summary>Compact summary of the list items, when the body is a list.</summary>
    public JsonNode? ListSummary { get; init; }

    /// <summary>Human-readable pointer text for the caller.</summary>
    public string Message { get; init; } = string.Empty;

    /// <summary>Structured error of the offloaded body, when it failed.</summary>
    public ResultError? ErrorData { get; init; }

    /// <summary>Total characters of the error message before any trimming.</summary>
    public int? ErrorMessageTotalChars { get; init; }
}
