using Microsoft.CodeAnalysis.Text;

namespace RoslynSentinel.Common.SnippetMatching;

/// <summary>
/// Shared plumbing for the matchers: argument checks and conversion from the legacy
/// <see cref="ContextHelper.SnippetMatch"/> (start and length only) to the new
/// <see cref="SnippetMatch"/> (adds the 1-based line, tier and matcher name).
/// </summary>
internal static class SnippetMatcherSupport
{
    /// <summary>True when the snippet is empty or whitespace-only, which no matcher accepts as a pattern.</summary>
    public static bool IsBlank(string snippet) => string.IsNullOrWhiteSpace(snippet);

    /// <summary>
    /// Converts legacy matches to the new type. The legacy span is already the true span in the file, so
    /// Start and Length are copied as they are; the line is derived from Start.
    /// </summary>
    public static IReadOnlyList<SnippetMatch> Convert(
        SourceText sourceText, IEnumerable<ContextHelper.SnippetMatch> legacyMatches,
        WhitespaceTolerance tier, string matcherName)
    {
        var converted = new List<SnippetMatch>();
        foreach (var legacy in legacyMatches)
        {
            var line = sourceText.Lines.GetLinePosition(legacy.Start).Line + 1;
            converted.Add(new SnippetMatch(legacy.Start, line, legacy.Length, tier, matcherName));
        }

        return converted;
    }

    /// <summary>
    /// The ordinal and line-ending stages compare case-sensitively today and have no case-insensitive
    /// form, so a CaseInsensitive request to them is a programming error rather than a silent downgrade.
    /// </summary>
    public static void RequireCaseSensitive(SnippetCaseMode caseMode, string matcherName)
    {
        if (caseMode == SnippetCaseMode.CaseInsensitive)
        {
            throw new ArgumentException(
                $"{matcherName} compares case-sensitively and has no case-insensitive form. " +
                "Only the whitespace-collapsing matcher (IgnoreAllWhitespace) is case-insensitive today.",
                nameof(caseMode));
        }
    }

    public static StringComparison ToComparison(SnippetCaseMode caseMode)
        => caseMode == SnippetCaseMode.CaseInsensitive ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}

/// <summary>
/// Tier <see cref="WhitespaceTolerance.Exact"/>: the ordinal stage only. Case-sensitive; the span length is
/// the snippet's own length, which is the true length because the comparison is ordinal.
/// </summary>
internal sealed class ExactMatcher : ISnippetMatcher
{
    public WhitespaceTolerance Tier => WhitespaceTolerance.Exact;

    public string Name => nameof(ExactMatcher);

    public IReadOnlyList<SnippetMatch> FindAll(SourceText sourceText, string snippet, SnippetCaseMode caseMode)
    {
        ArgumentNullException.ThrowIfNull(sourceText);
        SnippetMatcherSupport.RequireCaseSensitive(caseMode, Name);
        if (SnippetMatcherSupport.IsBlank(snippet))
        {
            return Array.Empty<SnippetMatch>();
        }

        var legacy = ContextHelper.FindOrdinalStage(sourceText.ToString(), snippet);
        return SnippetMatcherSupport.Convert(sourceText, legacy, Tier, Name);
    }
}

/// <summary>
/// Tier <see cref="WhitespaceTolerance.IgnoreLineEndings"/>: the CR/LF/CRLF-interchangeable regex stage only
/// (it applies to a multi-line snippet; a single-line snippet yields nothing). Case-sensitive. Run alone it also
/// reports occurrences the ordinal stage would find, because the regex accepts them too; the dispatcher only
/// runs it after the ordinal stage found nothing, as today.
/// </summary>
internal sealed class LineEndingMatcher : ISnippetMatcher
{
    public WhitespaceTolerance Tier => WhitespaceTolerance.IgnoreLineEndings;

    public string Name => nameof(LineEndingMatcher);

    public IReadOnlyList<SnippetMatch> FindAll(SourceText sourceText, string snippet, SnippetCaseMode caseMode)
    {
        ArgumentNullException.ThrowIfNull(sourceText);
        SnippetMatcherSupport.RequireCaseSensitive(caseMode, Name);
        if (SnippetMatcherSupport.IsBlank(snippet))
        {
            return Array.Empty<SnippetMatch>();
        }

        var legacy = ContextHelper.FindLineEndingStage(sourceText.ToString(), snippet);
        return SnippetMatcherSupport.Convert(sourceText, legacy, Tier, Name);
    }
}

/// <summary>
/// Tier <see cref="WhitespaceTolerance.IgnoreAllWhitespace"/>: the whitespace-collapsing single-line stage, then,
/// only if that found nothing and the snippet is multi-line, the N-line window stage (today's stop condition).
/// Honours <see cref="SnippetCaseMode"/> through the stages' comparison parameter. Each span is the real span in
/// the file, never the snippet's length.
/// </summary>
internal sealed class CollapsedWhitespaceMatcher : ISnippetMatcher
{
    public WhitespaceTolerance Tier => WhitespaceTolerance.IgnoreAllWhitespace;

    public string Name => nameof(CollapsedWhitespaceMatcher);

    public IReadOnlyList<SnippetMatch> FindAll(SourceText sourceText, string snippet, SnippetCaseMode caseMode)
    {
        ArgumentNullException.ThrowIfNull(sourceText);
        if (SnippetMatcherSupport.IsBlank(snippet))
        {
            return Array.Empty<SnippetMatch>();
        }

        var comparison = SnippetMatcherSupport.ToComparison(caseMode);
        var legacy = ContextHelper.FindCollapsedSingleLineStage(sourceText, snippet, comparison);
        if (legacy.Count == 0)
        {
            legacy = ContextHelper.FindCollapsedWindowStage(sourceText, snippet, comparison);
        }

        return SnippetMatcherSupport.Convert(sourceText, legacy, Tier, Name);
    }
}
