using RoslynSentinel.Common;

namespace RoslynSentinel.Engines.Core.SnippetMatching
{
    /// <summary>
    /// A contextSnippet/oldContent matched the file only at a looser whitespace tolerance than the call accepts, so
    /// nothing was located or applied; the message lists the file's own copy-ready lines. Distinct from
    /// <see cref="ToolNotFoundException"/> (nothing close exists) so a caller can tell "retype with the file's text"
    /// from "re-read the file". Maps to <see cref="ToolErrorCode.MatchedLooserThanRequested"/>. Lives beside
    /// <c>SnippetErrorFormatter</c> for now; it can move to ToolException.cs when callers migrate.
    /// </summary>
    public sealed class ToolMatchedLooserTierException : ToolException
    {
        public override string ErrorCode => ToolErrorCode.MatchedLooserThanRequested;

        public ToolMatchedLooserTierException(string message) : base(message)
        {
        }
    }
}
