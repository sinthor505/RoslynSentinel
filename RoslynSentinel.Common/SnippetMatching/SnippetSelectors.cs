using Microsoft.CodeAnalysis.Text;

namespace RoslynSentinel.Common.SnippetMatching;

/// <summary>
/// Optional selectors a caller uses to narrow a multi-match result to the one intended match, applied by
/// <see cref="SnippetResolver.Resolve(SourceText, string, WhitespaceTolerance, SnippetCaseMode, SnippetSelectors?, Func{int, string?}?)"/>
/// after tier resolution, to the matches at the winning tier. A blank selector means "not supplied": a null, empty or
/// whitespace <see cref="WithinMember"/> and a <see cref="MatchLine"/> of 0 or less are ignored, because weak models send
/// "" or 0 for an unused parameter and that must not filter out every match (the same rule as
/// the exact ReplaceSnippet matcher this replaced). <see cref="LineBefore"/> and <see cref="LineAfter"/> are different: any
/// non-null value, including an empty string, is a supplied selector.
/// <para>Precedence rule: <see cref="WithinMember"/> and <see cref="MatchLine"/> act first and are intersected; only the
/// matches that survive them are then filtered by <see cref="LineBefore"/> and <see cref="LineAfter"/>.</para>
/// </summary>
/// <param name="WithinMember">Ordinal name of the member enclosing the match, as reported by the memberNameAt delegate.</param>
/// <param name="MatchLine">1-based line of the match's first non-whitespace character.</param>
/// <param name="LineBefore">The single line before the match's first line (compared trimmed, contains-match).</param>
/// <param name="LineAfter">The single line after the match's last line (compared trimmed, contains-match).</param>
public sealed record SnippetSelectors(
    string? WithinMember = null, int? MatchLine = null, string? LineBefore = null, string? LineAfter = null);
/// <summary>
/// The selector post-filter behind <see cref="SnippetResolver"/>'s selector overload: the selector logic of the
/// exact ReplaceSnippet matcher it replaced, reporting every outcome as data instead of throwing.
/// </summary>
internal static class SnippetSelectorFilter
{
    /// <summary>
    /// Applies <paramref name="selectors"/> to the matches of <paramref name="tierResult"/> (the result of tier
    /// resolution for <paramref name="snippet"/>). Returns <paramref name="tierResult"/> unchanged when the snippet is
    /// blank, no selector is supplied, or nothing matched at any accepted tier. Otherwise: a malformed selector is
    /// <see cref="SnippetMatchOutcome.InvalidDisambiguator"/>; withinMember/matchLine removing every match is
    /// <see cref="SnippetMatchOutcome.SelectorMismatch"/>; lineBefore/lineAfter removing every survivor is
    /// <see cref="SnippetMatchOutcome.AdjacentLineMismatch"/>; one survivor is <see cref="SnippetMatchOutcome.Matched"/>
    /// and several are <see cref="SnippetMatchOutcome.StillAmbiguous"/>.
    /// </summary>
    internal static SnippetMatchResult Apply(
        SourceText sourceText, string snippet, SnippetMatchResult tierResult, SnippetSelectors selectors,
        Func<int, string?>? memberNameAt)
    {
        if (string.IsNullOrWhiteSpace(snippet))
        {
            return tierResult;
        }

        var withinMember = string.IsNullOrWhiteSpace(selectors.WithinMember) ? null : selectors.WithinMember;
        int? matchLine = selectors.MatchLine is > 0 ? selectors.MatchLine : null;
        var lineBefore = selectors.LineBefore;
        var lineAfter = selectors.LineAfter;

        // Validation runs before matching in ContextHelper too, so a malformed selector wins over NoMatch.
        if (withinMember != null && memberNameAt == null)
        {
            return Invalid(tierResult, "withinMember", withinMember);
        }

        if (lineBefore != null && lineBefore.Contains('\n'))
        {
            return Invalid(tierResult, "lineBefore", lineBefore);
        }

        if (lineAfter != null && lineAfter.Contains('\n'))
        {
            return Invalid(tierResult, "lineAfter", lineAfter);
        }

        var anySelector = withinMember != null || matchLine != null || lineBefore != null || lineAfter != null;
        if (!anySelector || tierResult.Matches.Count == 0)
        {
            return tierResult;
        }

        var tierMatches = tierResult.Matches;
        var trail = new List<SnippetFilterStep>();
        var current = tierMatches.ToList();

        if (matchLine != null)
        {
            current = Narrow("matchLine", current, trail,
                m => sourceText.Lines.GetLinePosition(FirstNonWhitespacePosition(sourceText, m)).Line + 1 == matchLine);
        }

        if (withinMember != null)
        {
            current = Narrow("withinMember", current, trail,
                m => string.Equals(memberNameAt!(FirstNonWhitespacePosition(sourceText, m)), withinMember, StringComparison.Ordinal));
        }

        var afterSelectors = current;
        var selectorsSupplied = withinMember != null || matchLine != null;
        if (selectorsSupplied && afterSelectors.Count == 0)
        {
            return Build(tierResult, SnippetMatchOutcome.SelectorMismatch, afterSelectors, tierMatches, afterSelectors, trail);
        }

        if (lineBefore != null)
        {
            var lbTrimmed = lineBefore.Trim();
            current = Narrow("lineBefore", current, trail, m => BeforeLineMatches(sourceText, m, lbTrimmed));
        }

        if (lineAfter != null)
        {
            var laTrimmed = lineAfter.Trim();
            current = Narrow("lineAfter", current, trail, m => AfterLineMatches(sourceText, m, laTrimmed));
        }

        if (current.Count == 0)
        {
            return Build(tierResult, SnippetMatchOutcome.AdjacentLineMismatch, current, tierMatches, afterSelectors, trail);
        }

        var outcome = current.Count == 1 ? SnippetMatchOutcome.Matched : SnippetMatchOutcome.StillAmbiguous;
        return Build(tierResult, outcome, current, tierMatches, afterSelectors, trail);
    }

    private static List<SnippetMatch> Narrow(
        string selector, List<SnippetMatch> current, List<SnippetFilterStep> trail, Func<SnippetMatch, bool> keep)
    {
        var kept = current.Where(keep).ToList();
        trail.Add(new SnippetFilterStep(selector, current.Count - kept.Count));
        return kept;
    }

    /// <summary>
    /// Position a match is attributed to for withinMember/matchLine: its first non-whitespace character, so a match that
    /// begins with a newline or indentation is placed on the line (and in the member) a human sees it on.
    /// </summary>
    internal static int FirstNonWhitespacePosition(SourceText sourceText, SnippetMatch match)
    {
        var last = match.Start + match.Length - 1;
        var position = match.Start;
        while (position < last && char.IsWhiteSpace(sourceText[position]))
        {
            position++;
        }

        return position;
    }

    private static bool BeforeLineMatches(SourceText sourceText, SnippetMatch match, string lbTrimmed)
    {
        var firstLineIndex = sourceText.Lines.GetLinePosition(match.Start).Line;
        return firstLineIndex != 0 && LineContains(sourceText.Lines[firstLineIndex - 1].ToString().Trim(), lbTrimmed);
    }

    private static bool AfterLineMatches(SourceText sourceText, SnippetMatch match, string laTrimmed)
    {
        var lastLineIndex = sourceText.Lines.GetLinePosition(match.Start + match.Length - 1).Line;
        return lastLineIndex < sourceText.Lines.Count - 1
            && LineContains(sourceText.Lines[lastLineIndex + 1].ToString().Trim(), laTrimmed);
    }

    /// <summary>
    /// Checks if a source line contains the pattern. Falls back to a quote-normalized comparison to handle snippets
    /// whose escaped quotes were not unescaped (e.g. from JSON context). Same rule as the removed legacy matcher's private MatchLine.
    /// </summary>
    private static bool LineContains(string sourceLine, string pattern)
    {
        if (sourceLine.Contains(pattern, StringComparison.Ordinal))
        {
            return true;
        }

        var normalized = pattern
            .Replace("\\\"", "\"")
            .Replace("\\'", "'")
            .Replace("\\\\", "\\");
        return normalized != pattern && sourceLine.Contains(normalized, StringComparison.Ordinal);
    }

    private static SnippetMatchResult Invalid(SnippetMatchResult tierResult, string parameterName, string value)
        => SnippetMatchResult.Create(
            tierResult.RequestedTolerance, tierResult.RequestedCaseMode, SnippetMatchOutcome.InvalidDisambiguator,
            Array.Empty<SnippetMatch>(), Array.Empty<SnippetNearMatch>(), 0, Array.Empty<SnippetClosestWindow>(),
            candidatesBeforeSelectors: 0, candidatesAfterSelectors: 0, candidatesAfterAdjacentLines: 0,
            invalidParameterName: parameterName, invalidParameterValue: value, tiersProbed: tierResult.TiersProbed);

    private static SnippetMatchResult Build(
        SnippetMatchResult tierResult, SnippetMatchOutcome outcome, IReadOnlyList<SnippetMatch> matches,
        IReadOnlyList<SnippetMatch> tierMatches, IReadOnlyList<SnippetMatch> afterSelectors, IReadOnlyList<SnippetFilterStep> trail)
        => SnippetMatchResult.Create(
            tierResult.RequestedTolerance, tierResult.RequestedCaseMode, outcome,
            matches, Array.Empty<SnippetNearMatch>(), 0, Array.Empty<SnippetClosestWindow>(),
            candidatesBeforeSelectors: tierMatches.Count, candidatesAfterSelectors: afterSelectors.Count,
            candidatesAfterAdjacentLines: matches.Count,
            filterTrail: trail, preFilterMatches: tierMatches, selectorSurvivors: afterSelectors,
            tiersProbed: tierResult.TiersProbed);
}
