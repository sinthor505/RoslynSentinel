using System;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

namespace RoslynSentinel.Common.SnippetMatching;

/// <summary>
/// Words the case where a snippet matched, but at a node of the wrong kind (for example an int local where a switch
/// was expected). Lives beside <see cref="SnippetErrorFormatter"/>; the message names the match line, a trimmed first
/// line of the matched text, the expected node and the innermost node at the match span.
/// </summary>
public static class SnippetNodeKindError
{
    /// <summary>Longest first line of the matched text quoted in the message.</summary>
    private const int MaxExcerptChars = 60;

    /// <summary>
    /// Describes a match at the wrong node kind. Output: "{snippetLabel} matched at line N (`first line`), but no
    /// {expectedNode} is there; the innermost node at that span is {SyntaxKind}. Pass a {snippetLabel} that {passInstead}."
    /// The excerpt is omitted when the span lies outside the text, and the kind clause when it cannot be mapped onto
    /// the tree; nothing is thrown for a weird span.
    /// </summary>
    /// <param name="text">The text the match was found in.</param>
    /// <param name="root">The syntax root of that text.</param>
    /// <param name="match">The accepted match.</param>
    /// <param name="expectedNode">The node kind the caller needed, in words (for example "switch statement").</param>
    /// <param name="passInstead">What the replacement snippet must contain, as a clause (for example "contains a switch statement").</param>
    /// <param name="snippetLabel">The caller's parameter name for the snippet, used in the wording.</param>
    public static string Describe(SourceText text, SyntaxNode root, SnippetMatch match, string expectedNode, string passInstead, string snippetLabel = SnippetLookup.DefaultSnippetLabel)
    {
        var excerpt = FirstLineExcerpt(text, match);
        var kind = InnermostKindName(root, match);
        var quoted = excerpt.Length > 0 ? $" (`{excerpt}`)" : "";
        var kindClause = kind is null ? "" : $"; the innermost node at that span is {kind}";
        return $"{snippetLabel} matched at line {match.Line}{quoted}, but no {expectedNode} is there{kindClause}. Pass a {snippetLabel} that {passInstead}.";
    }

    private static string FirstLineExcerpt(SourceText text, SnippetMatch match)
    {
        if (!IsWithin(match, text.Length))
        {
            return "";
        }

        var firstLine = text.ToString(new TextSpan(match.Start, match.Length)).Split('\n')[0].Trim();
        return firstLine.Length > MaxExcerptChars ? firstLine[..MaxExcerptChars] : firstLine;
    }

    private static string? InnermostKindName(SyntaxNode root, SnippetMatch match)
    {
        if (!IsWithin(match, root.FullSpan.End))
        {
            return null;
        }

        try
        {
            return root.FindNode(new TextSpan(match.Start, match.Length), getInnermostNodeForTie: false).Kind().ToString();
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static bool IsWithin(SnippetMatch match, int limit) =>
        match.Start >= 0 && match.Length >= 0 && match.Start <= limit && match.Length <= limit - match.Start;
}
