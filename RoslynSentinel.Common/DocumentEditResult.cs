namespace RoslynSentinel.Common
{
    public enum EditOutcome
    {
        Unset = 0,          // default value, should be treated as error if returned
        Modified = 1,
        // 2-12 are retired failure values (engines throw instead); do not reuse them.
        AlreadyInTargetState = 13, // already in target state
        NoCandidatesFound = 14   // e.g. no matching methods for a signature change
    }

    public sealed record DocumentEditResult : EngineResultBase
    {

        public Dictionary<FilePathWrapper, string> Changes { get; init; } = new Dictionary<FilePathWrapper, string>();

        // DEFERRED (accepted 2026-10-22, finding #22): UpdatedText is excluded from JSON summaries, so generate-style tools do not show the generated text.
        // Left as-is on purpose until scheduled; do not re-raise in reviews.
        // required was tried and rejected because System.Text.Json refuses a required member that is [JsonIgnore]; omissions are caught by RefactoringToolHelpers.RequireUpdatedText's empty-text guard instead (plan_document_edit_result_non_nullable_updated_text, step 7).
        [System.Text.Json.Serialization.JsonIgnore]
        public string UpdatedText { get; init; } = string.Empty;

        /// <summary>
        /// Batch engines only: the caller-supplied edit indexes that actually changed this file's text. An index that
        /// was requested but is absent here had no effect (e.g. removing an attribute that was not present), so a
        /// caller must derive "applied" counts from this list and never from the number of edits it submitted.
        /// Null for single-edit operations.
        /// </summary>
        public IReadOnlyList<int>? AppliedEditIndexes
        {
            get; init;
        }

        /// <summary>
        /// Non-fatal notes about a successful edit (for example a contextSnippet that was ignored because exactly one
        /// candidate existed). Null when there are none. Not serialized: ToJsonSummary has 13 callers whose output shape
        /// must not change; the tool layer surfaces these through the envelope's WarningDetails instead.
        /// </summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public IReadOnlyList<string>? Warnings
        {
            get; init;
        }

        public DocumentEditResult()
        {
        }

        [System.Diagnostics.CodeAnalysis.SetsRequiredMembers]
        public DocumentEditResult(EditOutcome outcome, FilePathWrapper filePath)
        {
            this.Outcome = outcome;
            this.FilePath = filePath;
            this.UpdatedText = string.Empty;
        }

        [System.Diagnostics.CodeAnalysis.SetsRequiredMembers]
        public DocumentEditResult(EditOutcome outcome, FilePathWrapper filePath, string changeId, bool isCommitted = false, string updatedText = "")
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
    }
}