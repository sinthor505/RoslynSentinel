namespace RoslynSentinel.Common;

/// <summary>
/// Result of a validate-then-write-through call (see SentinelRefactoringTools/
/// SentinelAdvancedRefactoringTools ValidateAndApplyAsync). Exactly one of
/// <see cref="Error"/> or a written change (<see cref="ChangeId"/> non-null, when not a dry run)
/// is populated on success.
/// </summary>
public record ApplyOutcome(
    string? ChangeId,
    ResultError? Error,
    bool DryRun,
    string? Diff = null
);
