using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace RoslynSentinel.Server;

/// <summary>
/// Locates a position in source code using a contextSnippet (verbatim substring) instead of line/column.
/// An AI can extract this snippet from the code it already sees, requiring zero coordinate calculation.
/// </summary>
public static class ContextHelper
{
    /// <summary>
    /// Finds the unique character offset of contextSnippet within sourceText.
    /// Throws InvalidOperationException if not found or ambiguous.
    /// </summary>
    public static int FindSnippetPosition(SourceText sourceText, string contextSnippet)
        => FindSnippetPosition(sourceText.ToString(), contextSnippet);

    public static int FindSnippetPosition(string fullSource, string contextSnippet)
    {
        if (string.IsNullOrWhiteSpace(contextSnippet))
            throw new InvalidOperationException("contextSnippet must not be empty.");
        
        var indices = new List<int>();
        int idx = 0;
        while ((idx = fullSource.IndexOf(contextSnippet, idx, StringComparison.Ordinal)) >= 0)
        {
            indices.Add(idx);
            idx++;
        }
        
        return indices.Count switch
        {
            0 => throw new InvalidOperationException($"contextSnippet not found in file: \"{contextSnippet.Trim()}\""),
            1 => indices[0],
            _ => throw new InvalidOperationException(
                $"contextSnippet is ambiguous ({indices.Count} matches): \"{contextSnippet.Trim()}\". " +
                "Provide a longer or more specific snippet from the surrounding code.")
        };
    }

    /// <summary>
    /// Finds the SyntaxNode at the position identified by contextSnippet.
    /// </summary>
    public static SyntaxNode FindNodeAtSnippet(SyntaxNode root, SourceText text, string contextSnippet)
    {
        var pos = FindSnippetPosition(text, contextSnippet);
        return root.FindNode(new TextSpan(pos, contextSnippet.Length));
    }

    /// <summary>
    /// Gets the ISymbol at the contextSnippet's position.
    /// Walks up ancestors to find the nearest declaration, falling back to reference resolution.
    /// </summary>
    public static async Task<ISymbol?> FindSymbolAtSnippetAsync(
        Document document, string contextSnippet, CancellationToken ct = default)
    {
        var root = await document.GetSyntaxRootAsync(ct);
        var model = await document.GetSemanticModelAsync(ct);
        var text = await document.GetTextAsync(ct);
        if (root == null || model == null) return null;

        var pos = FindSnippetPosition(text, contextSnippet);
        var node = root.FindNode(new TextSpan(pos, 0));

        return node.AncestorsAndSelf()
                   .Select(n => model.GetDeclaredSymbol(n, ct))
                   .FirstOrDefault(s => s != null)
               ?? model.GetSymbolInfo(node, ct).Symbol;
    }
}
