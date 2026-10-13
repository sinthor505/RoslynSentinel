using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RoslynSentinel.Tools.Basic;

/// <summary>
/// Resolves the member declaration that encloses a source position. Shared by the read-side
/// text search (match annotation) and the write-side snippet selectors.
/// </summary>
internal static class EnclosingMemberResolver
{
    /// <summary>
    /// Walks up from <paramref name = "position"/> to the nearest named member declaration
    /// (method, property, constructor, field/event, indexer, or operator) and returns its name.
    /// Returns null if the position isn't inside any member -> e.g. a using directive, a
    /// namespace-level comment, or a type declaration's own header.
    /// </summary>
    internal static string? GetEnclosingMemberName(SyntaxNode root, int position)
    {
        if (position < root.FullSpan.Start || position > root.FullSpan.End)
        {
            return null;
        }

        var token = root.FindToken(position);
        foreach (var node in token.Parent?.AncestorsAndSelf() ?? [])
        {
            switch (node)
            {
                case MethodDeclarationSyntax method:
                    return method.Identifier.Text;
                case ConstructorDeclarationSyntax ctor:
                    return ctor.Identifier.Text;
                case PropertyDeclarationSyntax prop:
                    return prop.Identifier.Text;
                case IndexerDeclarationSyntax:
                    return "this[]";
                case OperatorDeclarationSyntax op:
                    return $"operator {op.OperatorToken.Text}";
                case EventDeclarationSyntax evt:
                    return evt.Identifier.Text;
                case FieldDeclarationSyntax field:
                    return string.Join(", ", field.Declaration.Variables.Select(v => v.Identifier.Text));
                case EventFieldDeclarationSyntax eventField:
                    return string.Join(", ", eventField.Declaration.Variables.Select(v => v.Identifier.Text));
                case BaseTypeDeclarationSyntax:
                    // Reached a type declaration without finding a member first -> e.g. the match
                    // was on the class header itself, not inside any member body.
                    return null;
            }
        }

        return null;
    }
}
