namespace RoslynSentinel.Common;

/// <summary>
/// Converging success payload for ReplaceSnippet/ReplaceSnippetBatch. Exactly one of
/// <see cref="ReplacementResult"/> or <see cref="ValidationReport"/> is set per response, keyed
/// by the tool's action parameter: apply -> ReplacementResult, validate -> ValidationReport.
/// <see cref="DiffContent"/> is set only when action=apply and returnDiff=true was requested.
/// This is a documented convention, not enforced by the type system - nothing prevents a future
/// code path from setting more than one field at once.
/// </summary>
public record ReplaceSnippetResult(
    ApplyChangesResult? ReplacementResult,
    DiagnosticReport? ValidationReport,
    string? DiffContent
);
