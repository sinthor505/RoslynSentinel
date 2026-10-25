namespace RoslynSentinel.Common;

public static class EditOutcomeExtensions
{
    /// <summary>
    /// The one place that says how a document-edit outcome reads as a canonical <see cref="ResultStatus"/>.
    /// An undefined value (for example a bare cast of 0) is <see cref="ResultStatus.Failed"/>:
    /// an error, never a success.
    /// </summary>
    public static ResultStatus ToResultStatus(this EditOutcome outcome) => outcome switch
    {
        EditOutcome.Modified => ResultStatus.Success,
        EditOutcome.AlreadyInTargetState => ResultStatus.AlreadyInState,
        EditOutcome.NoCandidatesFound => ResultStatus.NotFound,
        _ => ResultStatus.Failed
    };

    /// <summary>
    /// The <see cref="ToolErrorCode"/> an outcome reports on the wire; null for the non-error statuses
    /// (<see cref="ResultStatus.Success"/>, <see cref="ResultStatus.AlreadyInState"/>).
    /// </summary>
    public static string? ToErrorCode(this EditOutcome outcome) => outcome.ToResultStatus() switch
    {
        ResultStatus.Success or ResultStatus.AlreadyInState => null,
        ResultStatus.NotFound => ToolErrorCode.NotFound,
        ResultStatus.InvalidInput => ToolErrorCode.InvalidArgument,
        ResultStatus.NotApplicable => ToolErrorCode.TargetIneligible,
        ResultStatus.FeatureDisabled => ToolErrorCode.FeatureDisabled,
        ResultStatus.Failed or ResultStatus.PartialFailure => ToolErrorCode.Exception,
        _ => ToolErrorCode.Exception
    };
}
