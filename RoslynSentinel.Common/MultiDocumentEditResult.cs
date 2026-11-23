namespace RoslynSentinel.Common
{
    /// <summary>
    /// One <see cref="DocumentEditResult"/> per changed document of a multi-document edit.
    /// </summary>
    // Serialized as a bare JSON array: System.Text.Json writes List<T> subclasses as arrays and ignores their other members, so a property added here never reaches the wire.
    public sealed class MultiDocumentEditResult : List<DocumentEditResult>, IOperationResult
    {
        public MultiDocumentEditResult()
        {
        }

        public MultiDocumentEditResult(IEnumerable<DocumentEditResult> documents) : base(documents)
        {
        }

        ResultStatus IOperationResult.Status => ResultStatus.Success;
    }
}
