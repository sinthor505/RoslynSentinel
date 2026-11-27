namespace RoslynSentinel.Common;

/// <summary>
/// One entry of a result's filter trail: a selector that was applied after tier resolution and how many matches it
/// removed. Data only. Entries are in application order: matchLine, withinMember, lineBefore, lineAfter (a selector
/// that was not supplied has no entry).
/// </summary>
/// <param name="Selector">The selector's parameter name: <c>matchLine</c>, <c>withinMember</c>, <c>lineBefore</c> or <c>lineAfter</c>.</param>
/// <param name="Removed">How many of the then-remaining matches this selector removed.</param>
public sealed record SnippetFilterStep(string Selector, int Removed);
