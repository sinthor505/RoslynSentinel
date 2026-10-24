using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

using RoslynSentinel.Common.SnippetMatching;

namespace RoslynSentinel.Engines.Basic;

/// <summary>
/// Resolves the symbol a contextSnippet points at. The Roslyn-bound successor of
/// the removed legacy ContextHelper symbol locator (slice 4b of the whitespace-tolerance proposal): the text lookup goes
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

        // TODO a snippet on a positional record parameter (e.g. "string? Detail = null,") resolves to null
        // (suspected: FindNode lands on the ParameterList when the snippet has leading indentation) and, even when
        // it hits the ParameterSyntax, returns the IParameterSymbol, not the synthesized property that references
        // bind to. The failure message also omits record parameters from its candidate list
        // (SymbolNavigationEngine.DescribeNameOnlyCandidates filters to MemberDeclarationSyntax).
        return model.GetDeclaredSymbol(node, cancellationToken)
               ?? model.GetSymbolInfo(node, cancellationToken).Symbol
               ?? node.DescendantNodes().OfType<VariableDeclaratorSyntax>()
                      .Select(v => model.GetDeclaredSymbol(v, cancellationToken))
                      .FirstOrDefault(s => s != null);
    }

    /// <summary>
    /// Walks from node up through its ancestors and returns the first member, type, variable declarator or parameter
    /// whose header (everything before its body, arrow expression, accessor list or type member list) contains pos.
    /// Returns null when no such declaration encloses the position.
    /// </summary>
    private static IEnumerable<SyntaxNode> FindHeaderDeclarations(SyntaxNode node, int pos)
    {
        foreach (var candidate in node.AncestorsAndSelf())
        {
            if (candidate is not (MemberDeclarationSyntax or BaseTypeDeclarationSyntax or VariableDeclaratorSyntax or ParameterSyntax))
            {
                continue;
            }

            int headerEnd = candidate is BaseTypeDeclarationSyntax type
                ? (type.OpenBraceToken.Span.IsEmpty ? candidate.Span.End : type.OpenBraceToken.SpanStart)
                : (candidate.ChildNodes().FirstOrDefault(c => c is BlockSyntax or ArrowExpressionClauseSyntax or AccessorListSyntax)?.SpanStart
                   ?? candidate.Span.End);

            if (pos >= candidate.SpanStart && pos <= headerEnd)
            {
                yield return candidate;
            }
        }
    }

    /// <summary>
    /// Resolves the symbol at the contextSnippet like the overload above, but accepts it only when its name matches
    /// expectedName (compared on the last dotted segment of expectedName; a constructor matches on its containing
    /// type's name). When the first resolution does not match (for example snippet <c>void Target();</c> resolving to
    /// System.Void), climbs from the snippet position to the enclosing declaration whose header contains it and returns
    /// that declaration's symbol if it matches. Returns null when nothing matches; never returns a symbol whose name
    /// differs from expectedName.
    /// </summary>
    public static async Task<ISymbol?> FindSymbolAtSnippetAsync(
        Document document, string contextSnippet, string expectedName,
        string? lineBefore, string? lineAfter, CancellationToken cancellationToken)
    {
        static bool Matches(ISymbol symbol, string segment) =>
            symbol is IMethodSymbol { MethodKind: MethodKind.Constructor } ctor
                ? ctor.ContainingType?.Name == segment
                : symbol.Name == segment;

        var segment = expectedName.Split('.')[^1];

        var resolved = await FindSymbolAtSnippetAsync(document, contextSnippet, lineBefore, lineAfter, cancellationToken);
        if (resolved != null && Matches(resolved, segment))
        {
            return resolved;
        }

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var model = await document.GetSemanticModelAsync(cancellationToken);
        var text = await document.GetTextAsync(cancellationToken);
        if (root == null || model == null)
        {
            return null;
        }

        var pos = SnippetLookup.ResolveOrThrow(text, contextSnippet, lineBefore, lineAfter).Start;
        var node = root.FindNode(new TextSpan(pos, 0));
        foreach (var declaration in FindHeaderDeclarations(node, pos))
        {
            ISymbol? declared = declaration switch
            {
                BaseFieldDeclarationSyntax field => field.Declaration.Variables.Count > 0
                    ? model.GetDeclaredSymbol(field.Declaration.Variables[0], cancellationToken)
                    : null,
                _ => model.GetDeclaredSymbol(declaration, cancellationToken),
            };

            if (declared != null && Matches(declared, segment))
            {
                return declared;
            }
        }

        return null;
    }
}
