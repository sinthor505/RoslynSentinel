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
        throw new NotImplementedException(); // TODO implement (Step 2)
    }

    /// <summary>
    /// True when every token of <paramref name="candidateText"/>, mapped to its file position at
    /// <paramref name="candidateTextStart"/>, starts and ends on real tree token boundaries. A candidate that starts or
    /// ends inside a token, string literal or comment is rejected.
    /// </summary>
    internal static bool TokensAlignWithTree(
        SyntaxNode fileRoot, int candidateTextStart, string candidateText, CSharpParseOptions options)
    {
        throw new NotImplementedException(); // TODO implement (Step 2)
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
