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
/// <summary>
/// Tier <see cref="WhitespaceTolerance.IgnoreIndentation"/>: per line, leading and trailing whitespace is ignored and
/// interior whitespace is compared as written (no collapse). Case-sensitive. Consumes <c>SourceText.Lines</c>, so line
/// endings are already ignored. Semantics: fragment at the edges, whole-line equality inside, so everything the
/// ordinal and line-ending stages accept is accepted here too.
/// <list type="bullet">
/// <item>Single-line snippet: the trimmed snippet is searched ordinally inside every source line (every occurrence);
/// the span is the matched fragment only.</item>
/// <item>Multi-line snippet (after dropping fully blank leading and trailing snippet lines, as the window stage does):
/// the first line must be a suffix of the source line's trim-end text, each interior line must equal its source line
/// after trimming both ends (a blank interior line matches only a whitespace-only source line), and the last line must
/// be a prefix of its source line's trim-start text. The span starts at the first fragment (after that line's
/// indentation) and ends right after the last fragment (before that line's trailing whitespace and line break), so its
/// length is the real source span, never the snippet's length.</item>
/// </list>
/// A whitespace-only snippet matches nothing. Every start that satisfies the rule is reported, in source order.
/// </summary>
internal sealed class IndentationMatcher : ISnippetMatcher
{
    public WhitespaceTolerance Tier => WhitespaceTolerance.IgnoreIndentation;

    public string Name => nameof(IndentationMatcher);

    public IReadOnlyList<SnippetMatch> FindAll(SourceText sourceText, string snippet, SnippetCaseMode caseMode)
    {
        ArgumentNullException.ThrowIfNull(sourceText);
        SnippetMatcherSupport.RequireCaseSensitive(caseMode, Name);
        if (SnippetMatcherSupport.IsBlank(snippet))
        {
            return Array.Empty<SnippetMatch>();
        }

        var snippetLines = SplitContentLines(snippet).Select(l => l.Trim()).ToArray();
        var lines = sourceText.Lines;
        var matches = new List<SnippetMatch>();

        if (snippetLines.Length == 1)
        {
            // Single-line snippet: every ordinal occurrence of the trimmed snippet inside each source line.
            var needle = snippetLines[0];
            for (var i = 0; i < lines.Count; i++)
            {
                var lineText = lines[i].ToString();
                var idx = 0;
                while ((idx = lineText.IndexOf(needle, idx, StringComparison.Ordinal)) >= 0)
                {
                    matches.Add(new SnippetMatch(lines[i].Start + idx, i + 1, needle.Length, Tier, Name));
                    idx++;
                }
            }

            return matches;
        }

        var first = snippetLines[0];
        var last = snippetLines[^1];
        var windowSize = snippetLines.Length;
        for (var i = 0; i + windowSize <= lines.Count; i++)
        {
            // First line: the snippet line must be a suffix of the source line without its trailing whitespace.
            var firstText = lines[i].ToString().TrimEnd();
            if (!firstText.EndsWith(first, StringComparison.Ordinal))
            {
                continue;
            }

            var interiorEqual = true;
            for (var j = 1; j < windowSize - 1 && interiorEqual; j++)
            {
                interiorEqual = lines[i + j].ToString().Trim() == snippetLines[j];
            }

            if (!interiorEqual)
            {
                continue;
            }

            // Last line: the snippet line must be a prefix of the source line without its leading whitespace.
            var lastLine = lines[i + windowSize - 1];
            var lastRaw = lastLine.ToString();
            var lastTrimmedStart = lastRaw.TrimStart();
            if (!lastTrimmedStart.StartsWith(last, StringComparison.Ordinal))
            {
                continue;
            }

            var start = lines[i].Start + (firstText.Length - first.Length);
            var end = lastLine.Start + (lastRaw.Length - lastTrimmedStart.Length) + last.Length;
            matches.Add(new SnippetMatch(start, i + 1, end - start, Tier, Name));
        }

        return matches;
    }

    // Mirrors ContextHelper.SplitSnippetIntoTrimmedContentLines (private there, and ContextHelper is not to be
    // changed in this slice): line endings normalised, fully blank leading and trailing lines dropped. The caller
    // has already rejected a blank snippet, so at least one non-blank line remains.
    private static string[] SplitContentLines(string snippet)
    {
        var normalized = snippet.Replace("\r\n", "\n").Replace("\r", "\n");
        return normalized.Split('\n')
            .SkipWhile(string.IsNullOrWhiteSpace)
            .Reverse().SkipWhile(string.IsNullOrWhiteSpace).Reverse()
            .ToArray();
    }
}
