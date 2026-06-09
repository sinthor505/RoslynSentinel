namespace RoslynSentinel.Server
{
    public enum EditOutcome
    {
        Unset,          // default value, should be treated as error if returned
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

    public sealed record DocumentEditResult : EngineResultBase
    {

        public Dictionary<FilePath, string> Changes { get; init; } = new Dictionary<FilePath, string>();

        public DocumentEditResult()
        {
        }

        public DocumentEditResult(EditOutcome outcome, FilePath filePath)
        {
            this.Outcome = outcome;
            this.FilePath = filePath;
        }

        public DocumentEditResult(EditOutcome outcome, FilePath filePath, int changeId, bool isCommitted = false, string? updatedText = null)
        {
            this.Outcome = outcome;
            this.FilePath = filePath;
            this.UpdatedText = updatedText;
            this.IsCommitted = isCommitted;
            this.ChangeId = changeId;
        }

        public string ToJsonSummary()
        {
            return System.Text.Json.JsonSerializer.Serialize(this, _jsonOptions);
        }

        public static DocumentEditResult DocumentNotFound(FilePath filePath)
        {
            return new DocumentEditResult(EditOutcome.DocumentNotFound, filePath) { Message = $"File not found: {filePath}" };
        }

        public static DocumentEditResult TargetNotFound(FilePath filePath)
        {
            return new DocumentEditResult(EditOutcome.TargetNotFound, filePath) { Message = $"Target not found in file: {filePath}" };
        }

        public static DocumentEditResult FeatureDisabled(FilePath filePath)
        {
            return new DocumentEditResult(EditOutcome.FeatureDisabled, filePath) { Message = $"Feature is disabled." };
        }
    }
}