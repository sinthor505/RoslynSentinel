using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

namespace RoslynSentinel.Server;

/// <summary>
/// Locates a position in source code using a contextSnippet (verbatim substring) instead of line/column.
/// An AI can extract this snippet from the code it already sees, requiring zero coordinate calculation.
/// When a snippet could be ambiguous, provide lineBefore and/or lineAfter for disambiguation.
/// </summary>
public static class ContextHelper
{
    /// <summary>
    /// Finds the unique character offset of contextSnippet within sourceText.
    /// Optionally, provide lineBefore/lineAfter (verbatim text from adjacent lines) to disambiguate.
    /// Throws InvalidOperationException if not found or still ambiguous after disambiguation.
    /// </summary>
    public static int FindSnippetPosition(
        SourceText sourceText, string contextSnippet,
        string? lineBefore = null, string? lineAfter = null)
    {
        if (string.IsNullOrWhiteSpace(contextSnippet))
            throw new InvalidOperationException("contextSnippet must not be empty.");

        var source = sourceText.ToString();
        var allMatches = new List<int>();
        int idx = 0;
        while ((idx = source.IndexOf(contextSnippet, idx, StringComparison.Ordinal)) >= 0)
        {
            allMatches.Add(idx);
            idx++;
        }

        if (allMatches.Count == 0)
            throw new InvalidOperationException($"contextSnippet not found: \"{contextSnippet.Trim()}\"");

        if (allMatches.Count == 1)
            return allMatches[0];

        // Multiple matches — try to disambiguate with surrounding lines
        if (lineBefore == null && lineAfter == null)
            throw new InvalidOperationException(
                $"contextSnippet is ambiguous ({allMatches.Count} matches): \"{contextSnippet.Trim()}\". " +
                "Provide lineBefore and/or lineAfter (verbatim text from the lines immediately above/below) to disambiguate.");

        var lbTrimmed = lineBefore?.Trim();
        var laTrimmed = lineAfter?.Trim();

        var filtered = allMatches.Where(offset =>
        {
            var linePos = sourceText.Lines.GetLinePosition(offset);
            var lineIndex = linePos.Line;

            if (lbTrimmed != null)
            {
                if (lineIndex == 0) return false;
                var prevLine = sourceText.Lines[lineIndex - 1].ToString().Trim();
                if (!prevLine.Contains(lbTrimmed, StringComparison.Ordinal)) return false;
            }
            if (laTrimmed != null)
            {
                if (lineIndex >= sourceText.Lines.Count - 1) return false;
                var nextLine = sourceText.Lines[lineIndex + 1].ToString().Trim();
                if (!nextLine.Contains(laTrimmed, StringComparison.Ordinal)) return false;
            }
            return true;
        }).ToList();

        return filtered.Count switch
        {
            0 => throw new InvalidOperationException(
                $"contextSnippet \"{contextSnippet.Trim()}\" found {allMatches.Count} time(s) " +
                $"but none match the provided context. " +
                (lbTrimmed != null ? $"lineBefore=\"{lbTrimmed}\" " : "") +
                (laTrimmed != null ? $"lineAfter=\"{laTrimmed}\"" : "") +
                " — check that the surrounding lines are exact."),
            1 => filtered[0],
            _ => throw new InvalidOperationException(
                $"contextSnippet is still ambiguous ({filtered.Count} of {allMatches.Count} matches remain): \"{contextSnippet.Trim()}\". " +
                "Provide more specific lineBefore and/or lineAfter content.")
        };
    }

    /// <summary>String overload — delegates to SourceText for consistent line handling.</summary>
    public static int FindSnippetPosition(
        string fullSource, string contextSnippet,
        string? lineBefore = null, string? lineAfter = null)
        => FindSnippetPosition(SourceText.From(fullSource), contextSnippet, lineBefore, lineAfter);

    /// <summary>
    /// After <see cref="FindSnippetPosition"/> returns a position, that position may land on a
    /// modifier keyword (e.g., "public") rather than the declared identifier.
    /// This helper scans the snippet span for identifier tokens and returns the position of the
    /// <em>last</em> one (the declared name, not the return type).
    /// Required before calling <c>SymbolFinder.FindSymbolAtPositionAsync</c>, which returns null
    /// when the cursor is on a non-identifier token.
    /// </summary>
    public static int AdvanceToLastIdentifier(SyntaxNode root, int snippetStart, int snippetLength)
    {
        var startToken = root.FindToken(snippetStart);
        if (startToken.IsKind(SyntaxKind.IdentifierToken))
            return snippetStart;

        var snippetEnd = snippetStart + snippetLength;
        var ident = root.DescendantTokens()
            .Where(t => t.SpanStart >= snippetStart && t.SpanStart < snippetEnd &&
                        t.IsKind(SyntaxKind.IdentifierToken))
            .Select(t => (SyntaxToken?)t)
            .LastOrDefault();

        return ident.HasValue ? ident.Value.SpanStart : snippetStart;
    }

    /// <summary>
    /// Finds the SyntaxNode at the position identified by contextSnippet.
    /// </summary>
    public static SyntaxNode FindNodeAtSnippet(
        SyntaxNode root, SourceText text, string contextSnippet,
        string? lineBefore = null, string? lineAfter = null)
    {
        var pos = FindSnippetPosition(text, contextSnippet, lineBefore, lineAfter);
        return root.FindNode(new TextSpan(pos, contextSnippet.Length));
    }

    /// <summary>
    /// Gets the ISymbol at the contextSnippet's position.
    /// Walks up ancestors to find the nearest declaration, falling back to reference resolution.
    /// </summary>
    public static async Task<ISymbol?> FindSymbolAtSnippetAsync(
        Document document, string contextSnippet,
        string? lineBefore = null, string? lineAfter = null,
        CancellationToken ct = default)
    {
        var root = await document.GetSyntaxRootAsync(ct);
        var model = await document.GetSemanticModelAsync(ct);
        var text = await document.GetTextAsync(ct);
        if (root == null || model == null) return null;

        var pos = FindSnippetPosition(text, contextSnippet, lineBefore, lineAfter);
        var node = root.FindNode(new TextSpan(pos, 0));

        return node.AncestorsAndSelf()
                   .Select(n => model.GetDeclaredSymbol(n, ct))
                   .FirstOrDefault(s => s != null)
               ?? model.GetSymbolInfo(node, ct).Symbol;
    }
}
