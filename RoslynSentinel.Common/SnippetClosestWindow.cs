namespace RoslynSentinel.Common;

/// <summary>
/// One of the file's windows that scored closest to a snippet that matched at no tier. Never
/// usable for an edit: it shares no base type or interface with <c>SnippetMatch</c>. The dispatcher lists
/// these in <c>SnippetMatchResult.ClosestWindows</c> only when nothing matched at any tier (see
/// <c>SnippetCandidateFinder</c> for the scoring, threshold and cap).
/// </summary>
/// <param name="StartLine">1-based line in the file where the window starts.</param>
/// <param name="ActualLines">The file's actual lines, verbatim.</param>
/// <param name="Score">Similarity score in 0..1; higher is closer. Never 1 (a full match would have been found).</param>
public sealed record SnippetClosestWindow(int StartLine, IReadOnlyList<string> ActualLines, double Score);
