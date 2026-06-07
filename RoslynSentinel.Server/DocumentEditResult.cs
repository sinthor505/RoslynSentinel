namespace RoslynSentinel.Server
{
    public enum EditOutcome
    {
        Modified,
        TargetNotFound,    // method/line/container not located
        DocumentNotFound,
        SourceInvalid,     // parsed fragment failed (the 3 fragment methods)
        NoChange,     // already in target form
        CannotOptimize,        // e.g. already optimized or optimization not applicable
        CannotEdit,          // e.g. unsupported scenario or API
        CannotMove,          // e.g. code can't be moved to target location (for move refactoring)
        CannotRemove,         // e.g. code can't be removed (for remove refactoring)
        CannotConvert,       // e.g. code can't be converted to target form (for convert refactoring)
        FeatureDisabled,        // e.g. optimization disabled by user or policy
        Error                 // for unexpected exceptions or failures
    }

    public sealed class DocumentEditResult
    {
        public EditOutcome Outcome
        {
            get; init;
        }
        public string? UpdatedText
        {
            get; init;
        }   // non-null if Modified
        public FilePath FilePath { get; init; } = "";
        public string Message { get; init; } = ""; // for error details in case of failure

        public DocumentEditResult()
        {
        }

        public DocumentEditResult(EditOutcome outcome, FilePath filePath, string? updatedText = null)
        {
            this.Outcome = outcome;
            this.FilePath = filePath;
            this.UpdatedText = updatedText;
        }
    }
}