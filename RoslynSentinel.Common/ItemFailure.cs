using System.Text.Json.Serialization;

namespace RoslynSentinel.Common;

/// <summary>Machine-routable failure reason for a single item (spec §3.3).</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum FailureReason
{
    /// <summary>An async overload already exists by name but lacks a CancellationToken parameter.</summary>
    OverloadAlreadyExists,
    /// <summary>The caller is already async — normally reclassified to AlreadySatisfied at assembly time.</summary>
    AlreadyAsync,
    /// <summary>No async API equivalent exists for the synchronous call the method makes.</summary>
    NoAsyncEquivalent,
    /// <summary>The transformation was applied but introduced compiler errors.</summary>
    CompilerErrorAfterTransform,
    /// <summary>The symbol, file, or project reference could not be resolved.</summary>
    SymbolNotResolved,
    /// <summary>A required precondition for the operation was not met.</summary>
    PreconditionMissing,
    /// <summary>Failure reason could not be classified.</summary>
    Unknown
}

/// <summary>
/// Pre-filled tool invocation hint routed via <see cref="ToolGraph"/> (spec §3.4).
/// All args the substrate already knows are pre-filled; the model supplies only what it must decide.
/// </summary>
public sealed class ToolHint
{
    public string ToolName { get; init; } = "";
    /// <summary>Parameter name → pre-filled value (as a string the model can pass verbatim).</summary>
    public IReadOnlyDictionary<string, string> PrefilledArgs { get; init; } = new Dictionary<string, string>();
    /// <summary>Required parameter names the substrate could not pre-fill; the model must supply these.</summary>
    public IReadOnlyList<string> RequiresFromModel { get; init; } = Array.Empty<string>();
    public string Rationale { get; init; } = "";
}

/// <summary>
/// Per-item failure with a substrate-routed tool hint (spec §3.5).
/// Only emitted for <see cref="ItemOutcome.Failed"/> or <see cref="ItemOutcome.Blocked"/> items.
/// </summary>
public sealed class ItemFailure
{
    public string FilePath { get; init; } = "";
    public string MethodName { get; init; } = "";
    /// <summary>Must be <see cref="ItemOutcome.Failed"/> or <see cref="ItemOutcome.Blocked"/>.</summary>
    public ItemOutcome Outcome { get; init; }
    public FailureReason Reason { get; init; }
    public string Detail { get; init; } = "";
    /// <summary>Null when the reason has no routable tool (loud null — the absence is itself a diagnostic signal).</summary>
    public ToolHint? SuggestedTool { get; init; }
}

/// <summary>Minimal context the router needs to pre-fill tool arguments (spec §4.2).</summary>
public sealed class ItemContext
{
    public string FilePath { get; init; } = "";
    public string MethodName { get; init; } = "";
    public string ProjectName { get; init; } = "";
    public string ChangeId { get; init; } = "";
}
