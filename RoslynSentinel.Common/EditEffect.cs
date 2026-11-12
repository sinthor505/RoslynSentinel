using System.Text.Json;
using System.Text.Json.Serialization;

namespace RoslynSentinel.Common;

/// <summary>What an edit did to a symbol or file, as stated by the operation that did it (never inferred from a text diff).</summary>
public enum EditEffectKind
{
    /// <summary>The symbol kept its place and took <see cref="EditEffect.NewName"/>.</summary>
    Renamed,

    /// <summary>The symbol's declaration was removed.</summary>
    Removed,

    /// <summary>The symbol moved to <see cref="EditEffect.NewContainer"/> / <see cref="EditEffect.NewFilePath"/>.</summary>
    Moved,

    /// <summary>A symbol with this name was declared (cancels an earlier Removed or Renamed-from note for the name).</summary>
    Added,

    /// <summary>The symbol's parameter list or return type changed.</summary>
    SignatureChanged,

    /// <summary>A whole file was deleted.</summary>
    FileDeleted,
}

/// <summary>
/// One symbol-level fact recorded by a tool call: "call X renamed/removed/moved/added Y". Recorded as pending on the
/// ambient <see cref="ToolCallContext"/>, committed to the <see cref="EditEffectIndex"/> by the write chokepoint only
/// when the write stands, and persisted in the operation blob. Serialized with <see cref="JsonOptions"/> (camelCase,
/// string enums); a blob missing any field still reads (absent fields take their defaults).
/// </summary>
/// <param name="Kind">What happened.</param>
/// <param name="SymbolDisplay">Display form of the symbol before the edit, for example <c>Foo.Bar</c>.</param>
/// <param name="SymbolDocId">Documentation comment id of the symbol before the edit; null when not known.</param>
/// <param name="SimpleName">Name without container, generic arity or parameter list; the index key.</param>
/// <param name="NewName">New simple name for Renamed; null otherwise.</param>
/// <param name="NewContainer">New containing type or namespace for Moved; null otherwise.</param>
/// <param name="NewFilePath">New declaring file for Moved, Renamed or Added; null when not known.</param>
/// <param name="ToolCallId">The call id the response echoes as <c>toolCall.toolCallId</c>; null when the host assigns none.</param>
/// <param name="ToolName">The MCP tool that made the edit.</param>
/// <param name="ChangeId">The operation blob's change id; null until the blob is written, and for writes that produce no blob.</param>
/// <param name="SessionId">Host session id of the caller; null when the host sends none.</param>
/// <param name="ServerInstanceId">Random per-process id of the server that made the edit (<see cref="ServerInstanceIdentity.Id"/>).</param>
/// <param name="AgentId">Agent id of the caller; null when the host sends none.</param>
/// <param name="UtcTime">When the effect was recorded.</param>
/// <param name="Reverted">True once the change was undone; a reverted effect is excluded from lookups.</param>
public sealed record EditEffect(
    EditEffectKind Kind,
    string SymbolDisplay,
    string? SymbolDocId,
    string SimpleName,
    string? NewName,
    string? NewContainer,
    string? NewFilePath,
    string? ToolCallId,
    string ToolName,
    string? ChangeId,
    string? SessionId,
    string ServerInstanceId,
    string? AgentId,
    DateTime UtcTime,
    bool Reverted = false)
{
    /// <summary>The serializer settings for the blob's <c>effects</c> array: camelCase names, string enums, case-insensitive reads.</summary>
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };
}
