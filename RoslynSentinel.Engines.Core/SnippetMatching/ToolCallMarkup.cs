namespace RoslynSentinel.Common.SnippetMatching
{
    /// <summary>
    /// Markers of a tool-call envelope that leaked into a tool argument (a weak model closing its own parameter tag inside
    /// the value). The single definition shared by the NoMatch message for oldContent (<see cref="SnippetErrorFormatter"/>)
    /// and the ReplaceSnippet newContent check, so the two cannot drift.
    /// </summary>
    public static class ToolCallMarkup
    {
        /// <summary>The markers, as literal text.</summary>
        public static readonly IReadOnlyList<string> Markers = ["</parameter>", "<parameter", "</invoke>"];

        /// <summary>
        /// The first marker contained in <paramref name="text"/> that <paramref name="existingText"/> does not contain, or null.
        /// A marker the file itself holds (this repo's own sources mention them) is legitimate content, not a leak.
        /// </summary>
        public static string? FindLeaked(string? text, Func<string, bool> existingTextContains)
        {
            if (string.IsNullOrEmpty(text))
            {
                return null;
            }

            foreach (var marker in Markers)
            {
                if (text.Contains(marker, StringComparison.Ordinal) && !existingTextContains(marker))
                {
                    return marker;
                }
            }

            return null;
        }
    }
}
