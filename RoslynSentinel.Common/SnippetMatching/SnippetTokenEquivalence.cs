using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

namespace RoslynSentinel.Common.SnippetMatching;

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
        throw new NotImplementedException(); // TODO implement (Step 3)
    }

    /// <summary>Looser-tier candidates for <paramref name="snippet"/> in <paramref name="sourceText"/>.</summary>
    internal static IReadOnlyList<SnippetMatch> FindLooserCandidates(SourceText sourceText, string snippet)
    {
        throw new NotImplementedException(); // TODO implement (Step 3)
    }

    /// <summary>
    /// Accepts a looser-tier <paramref name="candidate"/> when its tokens align with the tree and are token-equivalent to
    /// <paramref name="snippet"/>; returns the accepted match with its span, or null when it is rejected.
    /// </summary>
    internal static SnippetMatch? TryAccept(SourceText sourceText, SyntaxNode fileRoot, string snippet, SnippetMatch candidate)
    {
        throw new NotImplementedException(); // TODO implement (Step 3)
    }

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
            if (snippetToken.Kind() != candidateToken.Kind()
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
            if (snippetTrivia[i].Kind() != candidateTrivia[i].Kind()
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
        throw new NotImplementedException(); // TODO implement (Step 3)
    }
}
