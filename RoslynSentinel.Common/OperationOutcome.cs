using System.Text.Json.Serialization;

namespace RoslynSentinel.Common;

/// <summary>Per-item outcome used in operation blob records and legacy BatchResultSummary.</summary>
public enum ItemRecordOutcome
{
    Unset,
    Succeeded,
    Skipped,
    Failed,
    RolledBack,
    NeedsManualReview
}

/// <summary>Item-level outcome for the structured routing system (spec §3.1).</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ItemOutcome
{
    Succeeded,
    AlreadySatisfied,
    Skipped,
    Failed,
    Blocked
}

/// <summary>Operation-level completion verdict derived by the substrate (spec §3.2).</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum OperationOutcome
{
    CompletedFully,
    CompletedWithNoOps,
    PartialProgress,
    NoProgress,
    NothingToDo
}
