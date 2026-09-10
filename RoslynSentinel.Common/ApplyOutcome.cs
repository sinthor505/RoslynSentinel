namespace RoslynSentinel.Common;

/// <summary>
/// Result of a validate-then-write-through call (see SentinelRefactoringTools/
/// SentinelAdvancedRefactoringTools ValidateAndApplyAsync). Exactly one of
/// <see cref="Error"/> or a written change (<see cref="ChangeId"/> non-null, when not a dry run)
/// is populated on success.
/// </summary>
/// <param name="ChangeId">
/// The undo handle. Null on error, on a dry run, and — added for the run-398 A3 defect — on a
/// successful apply whose operation blob could not be written, in which case
/// <paramref name="NotReversibleReason"/> is set. Withholding the handle in that case is
/// deliberate: previously one was issued regardless, so five tools returned handles
/// <c>UndoLastApply</c> could never resolve, which is strictly worse than returning none.
/// </param>
/// <param name="NotReversibleReason">
/// Non-null only when the change landed on disk but its undo record did not. Callers should
/// surface this in place of the usual "call UndoLastApply" note. The server also trips its
/// unrecoverable breaker in this case, so no further mutation is accepted this session.
/// </param>
public record ApplyOutcome(
    string? ChangeId,
    ResultError? Error,
    bool DryRun,
    string? Diff = null,
    string? NotReversibleReason = null
);
