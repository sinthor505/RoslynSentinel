using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

namespace RoslynSentinel.Engines.Core.SnippetMatching;

/// <summary>
/// Second chance for a ReplaceSnippet oldContent that the whitespace ladder matched only at a looser tier: a looser-tier
/// candidate is accepted as an edit target when its C# tokens equal the snippet's (only whitespace and end-of-line trivia
/// differences are forgiven; comments and directives are compared per line with each line trimmed).
/// </summary>
public static class SnippetTokenEquivalence
{
    /// <summary>Matcher name reported in match results when a token-equivalent candidate is accepted.</summary>
    public const string MatcherName = "TokenEquivalence";

    /// <summary>
    /// Resolves <paramref name="snippet"/> through the token-equivalence second chance. Returns null when no looser-tier
    /// candidate is accepted, so the caller keeps its existing outcome.
    /// </summary>
    public static SnippetMatchResult? Resolve(
        SourceText sourceText, SyntaxNode fileRoot, string snippet, SnippetSelectors? selectors, Func<int, string?>? memberNameAt)
    {
        var accepted = new List<SnippetMatch>();
        var seen = new HashSet<(int Start, int Length)>();
        foreach (var candidate in FindLooserCandidates(sourceText, snippet))
        {
            var match = TryAccept(sourceText, fileRoot, snippet, candidate);
            if (match != null && seen.Add((match.Start, match.Length)))
            {
                accepted.Add(match);
            }
        }

        if (accepted.Count == 0)
        {
            return null;
        }

        accepted.Sort((left, right) => left.Start.CompareTo(right.Start));
        var count = accepted.Count;
        var result = SnippetMatchResult.Create(
            WhitespaceTolerance.IgnoreLineEndings, SnippetCaseMode.CaseSensitive,
            count == 1 ? SnippetMatchOutcome.Matched : SnippetMatchOutcome.Ambiguous,
            accepted, [], 0, [], count, count, count,
            tiersProbed: [WhitespaceTolerance.IgnoreLineEndings, WhitespaceTolerance.IgnoreIndentation, WhitespaceTolerance.IgnoreAllWhitespace]);
        if (selectors != null)
        {
            result = SnippetSelectorFilter.Apply(sourceText, snippet, result, selectors, memberNameAt);
        }

        if (result.Outcome is SnippetMatchOutcome.Ambiguous or SnippetMatchOutcome.StillAmbiguous)
        {
            var winners = result.Matches.Where(match => HasIdenticalLeadingWhitespace(sourceText, match, snippet)).ToList();
            if (winners.Count == 1)
            {
                result = SnippetMatchResult.WithAmbiguity(result, SnippetMatchOutcome.Matched, [winners[0]]);
            }
        }

        return result;
    }

    /// <summary>Looser-tier candidates for <paramref name="snippet"/> in <paramref name="sourceText"/>.</summary>
    internal static IReadOnlyList<SnippetMatch> FindLooserCandidates(SourceText sourceText, string snippet)
    {
        // Union of both looser matchers, all spans, uncapped; each candidate keeps its own matcher's Tier. A raw span
        // reported by both matchers is kept once (the IgnoreIndentation one, which is probed first).
        var candidates = new List<SnippetMatch>();
        var seen = new HashSet<(int Start, int Length)>();
        foreach (var found in new IndentationMatcher().FindAll(sourceText, snippet, SnippetCaseMode.CaseSensitive)
                     .Concat(new CollapsedWhitespaceMatcher().FindAll(sourceText, snippet, SnippetCaseMode.CaseSensitive)))
        {
            if (seen.Add((found.Start, found.Length)))
            {
                candidates.Add(found);
            }
        }

        return candidates;
    }

    /// <summary>
    /// Accepts a looser-tier <paramref name="candidate"/> when its tokens align with the tree and are token-equivalent to
    /// <paramref name="snippet"/>; returns the accepted match with its span, or null when it is rejected.
    /// </summary>
    internal static SnippetMatch? TryAccept(SourceText sourceText, SyntaxNode fileRoot, string snippet, SnippetMatch candidate)
    {
        // candidateText: the file text from the first to the last non-whitespace character inside the candidate span.
        var first = Math.Max(candidate.Start, 0);
        var spanEnd = Math.Min(candidate.Start + candidate.Length, sourceText.Length);
        while (first < spanEnd && char.IsWhiteSpace(sourceText[first]))
        {
            first++;
        }

        var last = spanEnd;
        while (last > first && char.IsWhiteSpace(sourceText[last - 1]))
        {
            last--;
        }

        if (first >= last)
        {
            return null;
        }

        var candidateText = sourceText.ToString(TextSpan.FromBounds(first, last));
        var options = fileRoot.SyntaxTree.Options as CSharpParseOptions ?? CSharpParseOptions.Default;
        if (!TokensAlignWithTree(fileRoot, first, candidateText, options) || !AreTokenEquivalent(snippet, candidateText, options))
        {
            return null;
        }

        // Owner span convention: a candidate that begins its line (first token or comment is the first non-whitespace
        // text on the line) owns that line's indentation, so the span starts at the line start; a mid-line start keeps
        // the file's text before it.
        var firstLine = sourceText.Lines.GetLineFromPosition(first);
        var start = IsWhitespaceOnly(sourceText, firstLine.Start, first) ? firstLine.Start : first;

        var end = last;
        if (EndsWithLineBreak(snippet))
        {
            // The snippet's final line break maps to the file line's own break. When other text follows the match on
            // that line the line structure differs, so the candidate is not an edit target.
            var lastLine = sourceText.Lines.GetLineFromPosition(last - 1);
            if (!IsWhitespaceOnly(sourceText, last, lastLine.End))
            {
                return null;
            }

            end = lastLine.EndIncludingLineBreak;
        }

        var line = sourceText.Lines.GetLinePosition(start).Line + 1;
        return new SnippetMatch(start, line, end - start, candidate.Tier, MatcherName);
    }

    private static bool IsWhitespaceOnly(SourceText sourceText, int start, int end)
    {
        for (var position = start; position < end; position++)
        {
            if (!char.IsWhiteSpace(sourceText[position]))
            {
                return false;
            }
        }

        return true;
    }

    private static bool EndsWithLineBreak(string snippet) =>
    snippet.Length > 0 && (snippet[^1] == '\n' || snippet[^1] == '\r');

    /// <summary>
    /// True when <paramref name="candidateText"/> and <paramref name="snippet"/> lex to the same tokens: same kinds and
    /// texts, and the same non-whitespace trivia. Whitespace and end-of-line trivia differences are ignored.
    /// </summary>
    internal static bool AreTokenEquivalent(string snippet, string candidateText, CSharpParseOptions options)
    {
        var snippetTokens = SyntaxFactory.ParseTokens(snippet, options: options).ToList();
        var candidateTokens = SyntaxFactory.ParseTokens(candidateText, options: options).ToList();
        if (snippetTokens.Count != candidateTokens.Count)
        {
            return false;
        }

        for (var i = 0; i < snippetTokens.Count; i++)
        {
            var snippetToken = snippetTokens[i];
            var candidateToken = candidateTokens[i];
            // Line breaks inside a token (verbatim or raw string) are compared normalized: the edit path matches with
            // IgnoreLineEndings and normalizes the written file's line endings afterwards.
            if (!snippetToken.IsKind(candidateToken.Kind())
                || NormalizeLineBreaks(snippetToken.Text) != NormalizeLineBreaks(candidateToken.Text)
                || !SignificantTriviaEquivalent(snippetToken, candidateToken))
            {
                return false;
            }
        }

        return true;
    }

    private static string NormalizeLineBreaks(string text) =>
        text.Replace("\r\n", "\n").Replace('\r', '\n');

    // Non-whitespace trivia (leading then trailing) of both tokens must have the same count, kinds and per-line trimmed text.
    private static bool SignificantTriviaEquivalent(SyntaxToken snippetToken, SyntaxToken candidateToken)
    {
        var snippetTrivia = SignificantTrivia(snippetToken);
        var candidateTrivia = SignificantTrivia(candidateToken);
        if (snippetTrivia.Count != candidateTrivia.Count)
        {
            return false;
        }

        for (var i = 0; i < snippetTrivia.Count; i++)
        {
            if (!snippetTrivia[i].IsKind(candidateTrivia[i].Kind())
                || !TrimmedLines(snippetTrivia[i].ToFullString()).SequenceEqual(TrimmedLines(candidateTrivia[i].ToFullString())))
            {
                return false;
            }
        }

        return true;
    }

    private static List<SyntaxTrivia> SignificantTrivia(SyntaxToken token) =>
        token.LeadingTrivia.Concat(token.TrailingTrivia)
            .Where(trivia => !trivia.IsKind(SyntaxKind.WhitespaceTrivia) && !trivia.IsKind(SyntaxKind.EndOfLineTrivia))
            .ToList();

    // Owner decision R4: per-line trim. Trailing empty lines are dropped so a directive's own line break (present in a
    // mid-file directive, absent at the end of a snippet) does not make equal directives differ.
    private static List<string> TrimmedLines(string triviaText)
    {
        var lines = triviaText.Split(["\r\n", "\n", "\r"], StringSplitOptions.None).Select(line => line.Trim()).ToList();
        while (lines.Count > 0 && lines[^1].Length == 0)
        {
            lines.RemoveAt(lines.Count - 1);
        }

        return lines;
    }

    /// <summary>
    /// True when every token of <paramref name="candidateText"/>, mapped to its file position at
    /// <paramref name="candidateTextStart"/>, starts and ends on real tree token boundaries. A candidate that starts or
    /// ends inside a token, string literal or comment is rejected.
    /// </summary>
    internal static bool TokensAlignWithTree(
        SyntaxNode fileRoot, int candidateTextStart, string candidateText, CSharpParseOptions options)
    {
        var fullSpan = fileRoot.FullSpan;
        foreach (var token in SyntaxFactory.ParseTokens(candidateText, options: options))
        {
            if (token.IsKind(SyntaxKind.EndOfFileToken) || token.Span.Length == 0)
            {
                continue;
            }

            var start = candidateTextStart + token.Span.Start;
            var end = candidateTextStart + token.Span.End;
            if (start < fullSpan.Start || end > fullSpan.End)
            {
                return false;
            }

            // A lexer token that starts or ends inside a tree token, string literal or comment lands on a position whose
            // owning tree token starts earlier / ends later. A whole interpolated string is one lexer token spanning
            // several tree tokens: only its outer start and end are checked, and both sit on tree token boundaries.
            if (fileRoot.FindToken(start).Span.Start != start || fileRoot.FindToken(end - 1).Span.End != end)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Tie-break test: true when every snippet line has the same leading whitespace as the corresponding line of the
    /// accepted match in the file.
    /// </summary>
    internal static bool HasIdenticalLeadingWhitespace(SourceText sourceText, SnippetMatch accepted, string snippet)
    {
        var snippetLines = SnippetMatcherSupport.SplitSnippetIntoTrimmedContentLines(snippet);
        var startLineIndex = sourceText.Lines.GetLinePosition(accepted.Start).Line;
        var startsMidLine = !IsWhitespaceOnly(sourceText, sourceText.Lines[startLineIndex].Start, accepted.Start);
        for (var i = 0; i < snippetLines.Length; i++)
        {
            // Line 1 of a mid-line span has no leading whitespace of its own to compare. A blank snippet line has no
            // meaningful leading whitespace either (it differs from the file's blank line only as trailing whitespace).
            if ((i == 0 && startsMidLine) || string.IsNullOrWhiteSpace(snippetLines[i]))
            {
                continue;
            }

            var fileLineIndex = startLineIndex + i;
            if (fileLineIndex >= sourceText.Lines.Count
                || LeadingWhitespace(snippetLines[i]) != LeadingWhitespace(sourceText.Lines[fileLineIndex].ToString()))
            {
                return false;
            }
        }

        return true;
    }

    private static string LeadingWhitespace(string line)
    {
        var length = 0;
        while (length < line.Length && (line[length] == ' ' || line[length] == '\t'))
        {
            length++;
        }

        return line[..length];
    }
}
