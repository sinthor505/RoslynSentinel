namespace RoslynSentinel.Common;

/// <summary>
/// One written file of a real apply: its wire path and its updated text. A list of these is detached as
/// <c>successData.changedFiles</c> (see <see cref="ValidateAndApplyHelper"/>), so <c>GetLargeResult</c> can page it by
/// <c>path</c>; the inline <c>ChangedContent</c> dictionary cannot be paged that way. <c>Text</c> is a verbatim key in
/// <see cref="WirePathRewriter.VerbatimKeys"/>, so the file text is never path-relativized.
/// </summary>
/// <param name="Path">The file path in the same form the <c>ChangedContent</c> dictionary key had.</param>
/// <param name="Text">The full updated text of the file.</param>
public record ChangedFile(string Path, string Text);
