namespace RoslynSentinel.Common;

/// <summary>
/// Canonical outcome vocabulary shared by every status-carrying result type. Which members are errors is
/// decided in exactly one place, <see cref="ResultStatusExtensions.IsErrorStatus"/>.
/// See docs/current/proposals/proposal_shared_result_status_interface.md.
/// </summary>
public enum ResultStatus
{
    /// <summary>The operation completed and did what was asked.</summary>
    Success,

    /// <summary>Nothing to do because the target was already in the requested state. Not an error.</summary>
    AlreadyInState,

    /// <summary>The target (symbol, file, member, line) was not located.</summary>
    NotFound,

    /// <summary>More than one target matched where exactly one was required.</summary>
    Ambiguous,

    /// <summary>A parameter or input was invalid.</summary>
    InvalidInput,

    /// <summary>The target exists but the operation cannot be applied to it.</summary>
    NotApplicable,

    /// <summary>The operation is disabled in this server configuration.</summary>
    FeatureDisabled,

    /// <summary>The operation was cancelled before it finished.</summary>
    Cancelled,

    /// <summary>The operation exceeded its time limit.</summary>
    TimedOut,

    /// <summary>The operation failed for a reason none of the other members describes.</summary>
    Failed,

    /// <summary>Some items of an aggregate failed and some succeeded. An error status; set only by <see cref="ResultStatusExtensions.FromFailureCount"/>.</summary>
    PartialFailure
}

/// <summary>Single source of truth for which <see cref="ResultStatus"/> values are errors and how a tool error code maps to one.</summary>
public static class ResultStatusExtensions
{
    /// <summary>True for every status except <see cref="ResultStatus.Success"/> and <see cref="ResultStatus.AlreadyInState"/>.</summary>
    public static bool IsErrorStatus(this ResultStatus status) =>
        status is not (ResultStatus.Success or ResultStatus.AlreadyInState);

    /// <summary>
    /// Maps a <see cref="ToolErrorCode"/> string to its canonical status. A null or empty code means no error was
    /// recorded, so <see cref="ResultStatus.Success"/>; an unrecognized code is <see cref="ResultStatus.Failed"/>.
    /// </summary>
    public static ResultStatus FromErrorCode(string? errorCode) => errorCode switch
    {
        null or "" => ResultStatus.Success,
        ToolErrorCode.NotFound or ToolErrorCode.NoMatches => ResultStatus.NotFound,
        ToolErrorCode.Ambiguous => ResultStatus.Ambiguous,
        ToolErrorCode.InvalidArgument => ResultStatus.InvalidInput,
        ToolErrorCode.TargetIneligible => ResultStatus.NotApplicable,
        ToolErrorCode.FeatureDisabled => ResultStatus.FeatureDisabled,
        ToolErrorCode.Cancelled => ResultStatus.Cancelled,
        ToolErrorCode.TimedOut or ToolErrorCode.SolutionLoadTimeout => ResultStatus.TimedOut,
        _ => ResultStatus.Failed
    };

    /// <summary>
    /// Status for a type that stores a bare <c>IsError</c> flag plus an optional code: <see cref="ResultStatus.Success"/>
    /// when not an error, otherwise the status <paramref name="errorCode"/> maps to, or <see cref="ResultStatus.Failed"/>
    /// when the code is missing or maps to a non-error status. Keeps <c>Status</c> and <c>IsError</c> consistent.
    /// </summary>
    public static ResultStatus FromFlag(bool isError, string? errorCode)
    {
        if (!isError)
        {
            return ResultStatus.Success;
        }

        var mapped = FromErrorCode(errorCode);
        return mapped.IsErrorStatus() ? mapped : ResultStatus.Failed;
    }

    /// <summary>
    /// Status for an aggregate of <paramref name="failed"/> failed and <paramref name="succeeded"/> succeeded items:
    /// <see cref="ResultStatus.Success"/> when none failed, <see cref="ResultStatus.Failed"/> when all failed (none succeeded),
    /// otherwise <see cref="ResultStatus.PartialFailure"/>.
    /// </summary>
    public static ResultStatus FromFailureCount(int failed, int succeeded) =>
        failed == 0 ? ResultStatus.Success
        : succeeded == 0 ? ResultStatus.Failed
        : ResultStatus.PartialFailure;

    /// <summary>Message for an aggregate with failures, for example "2 of 5 failed"; null when none failed.</summary>
    public static string? FailureCountMessage(int succeeded, int failed) =>
        failed > 0 ? $"{failed} of {succeeded + failed} failed" : null;
}
