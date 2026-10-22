namespace RoslynSentinel.Common;

public static class EditOutcomeExtensions
{
    /// <summary>
    /// The one place that says how a document-edit outcome reads as a canonical <see cref="ResultStatus"/>.
    /// <see cref="EditOutcome.Unset"/>, <see cref="EditOutcome.Error"/> and the obsolete <see cref="EditOutcome.NoChange"/>
    /// are <see cref="ResultStatus.Failed"/>: an engine that fell through without classifying itself is an error, never a success.
    /// </summary>
    public static ResultStatus ToResultStatus(this EditOutcome outcome) => outcome switch
    {
        EditOutcome.Modified => ResultStatus.Success,
        EditOutcome.AlreadyInTargetState => ResultStatus.AlreadyInState,
        EditOutcome.TargetNotFound or EditOutcome.DocumentNotFound or EditOutcome.NoCandidatesFound => ResultStatus.NotFound,
        EditOutcome.SourceInvalid => ResultStatus.InvalidInput,
        EditOutcome.FeatureDisabled => ResultStatus.FeatureDisabled,
        EditOutcome.CannotOptimize or EditOutcome.CannotEdit or EditOutcome.CannotMove
            or EditOutcome.CannotRemove or EditOutcome.CannotConvert => ResultStatus.NotApplicable,
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
