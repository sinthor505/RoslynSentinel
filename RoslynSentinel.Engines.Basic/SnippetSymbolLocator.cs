using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

using RoslynSentinel.Common.SnippetMatching;

namespace RoslynSentinel.Engines.Basic;

/// <summary>
/// Resolves the symbol a contextSnippet points at. The Roslyn-bound successor of
/// <c>ContextHelper.FindSymbolAtSnippetAsync</c> (slice 4b of the whitespace-tolerance proposal): the text lookup goes
/// through <see cref="SnippetLookup"/> (the loosest tier, case-insensitive, never a best-guess near-match, failures list
/// the file's candidates) instead of the legacy tolerant helpers.
/// </summary>
public static class SnippetSymbolLocator
{
    /// <summary>
    /// Gets the ISymbol at the contextSnippet's position: the node's own declared symbol if the snippet points at a
    /// declaration, otherwise the symbol the node references. Returns null when the document has no syntax tree or
    /// semantic model, or when nothing at the position has a symbol.
    /// </summary>
    /// <remarks>
    /// Deliberately does NOT walk up <c>AncestorsAndSelf()</c> looking for the first ancestor with a declared symbol
    /// (that silently resolves a plain reference such as <c>_x = x;</c> to its enclosing constructor). Only the node
    /// itself is checked, and a field or event-field declaration (which has no declared symbol of its own) descends to
    /// its first variable declarator.
    /// </remarks>
    /// <exception cref="ToolNotFoundException">The snippet matches nothing, or lineBefore/lineAfter is malformed; the message lists the file's closest candidates.</exception>
    /// <exception cref="ToolAmbiguousMatchException">The snippet matches several places; the message lists each with the values that select it.</exception>
    public static async Task<ISymbol?> FindSymbolAtSnippetAsync(
        Document document, string contextSnippet,
        string? lineBefore = null, string? lineAfter = null,
        CancellationToken cancellationToken = default)
    {
        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var model = await document.GetSemanticModelAsync(cancellationToken);
        var text = await document.GetTextAsync(cancellationToken);
        if (root == null || model == null)
        {
            return null;
        }

        var pos = SnippetLookup.ResolveOrThrow(text, contextSnippet, lineBefore, lineAfter).Start;
        var node = root.FindNode(new TextSpan(pos, 0));

        return model.GetDeclaredSymbol(node, cancellationToken)
               ?? model.GetSymbolInfo(node, cancellationToken).Symbol
               ?? node.DescendantNodes().OfType<VariableDeclaratorSyntax>()
                      .Select(v => model.GetDeclaredSymbol(v, cancellationToken))
                      .FirstOrDefault(s => s != null);
    }
}
