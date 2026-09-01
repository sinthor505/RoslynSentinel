namespace RoslynSentinel.Common;

/// <summary>
/// One bucket in a capped-list's full-population summary — every item that groups under the same
/// signature, so an agent sees "45 of 50 failures share one cause" in one line instead of
/// paginating hundreds of results to notice the pattern itself. Used by <c>RunTest</c>'s
/// <c>FailureSummary</c>, where the grouping key is a message-derived signature rather than a
/// stable code (compare <see cref="DiagnosticGroupSummary"/>, which <c>Build</c> reuses instead of
/// this type since <see cref="DiagnosticInfo.Id"/> is already a stable grouping key).
/// </summary>
public record GroupedCountSummary(
    string Signature,
    int Count,
    string ExampleRef
);
