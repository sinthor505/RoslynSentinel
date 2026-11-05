namespace RoslynSentinel.Common;

// TODO [ResultRecords] marks only LiteralResults; for a regex query the hits are in RegexResults, LiteralResults is empty and the pager falls back to inference (inferred: true) - both lists are records.
/// <summary>
/// Return payload for <c>SearchSolutionText</c>: the literal substring matches (always the
/// complete set) and the regex matches not already present in <see cref="LiteralResults"/> (empty
/// when the pattern has no regex metacharacters, since every regex match is then also a literal
/// match). <see cref="RegexOverlapCount"/> is how many regex matches were suppressed as duplicates
/// of a literal match -> lets a caller tell "regex found nothing extra" apart from "regex found
/// nothing at all." <see cref="RegexPatternValid"/> is false when <c>pattern</c> doesn't compile
/// as a regex; <see cref="RegexResults"/> is empty and literal search is unaffected in that case.
/// </summary>
public record TextSearchResult([property: ResultRecords] List<TextSearchMatch> LiteralResults, List<TextSearchMatch> RegexResults, int RegexOverlapCount, bool RegexPatternValid);
