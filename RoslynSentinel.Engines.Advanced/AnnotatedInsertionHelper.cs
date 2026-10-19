using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Formatting;

using RoslynSentinel.Common;

namespace RoslynSentinel.Engines.Advanced;

/// <summary>
/// Inserts using directives and attribute lists with explicit trivia plus <see cref="Formatter.Annotation"/>, so that
/// <see cref="FormatAnnotatedTextAsync"/> lays out only the inserted syntax and every untouched line stays byte-exact.
/// </summary>
internal static class AnnotatedInsertionHelper
{
    // Inserted lines end in "\n": FormatAnnotatedSpansAsync gives every changed line the document's dominant line ending.
    private static readonly SyntaxTrivia NewLine = SyntaxFactory.EndOfLine("\n");

    /// <summary>Appends <c>using <paramref name="namespaceName"/>;</c> after the existing top-level usings, on its own line.</summary>
    internal static CompilationUnitSyntax AddUsingDirective(CompilationUnitSyntax root, string namespaceName)
    {
        var directive = SyntaxFactory.UsingDirective(SyntaxFactory.ParseName(namespaceName))
            .WithTrailingTrivia(NewLine)
            .WithAdditionalAnnotations(Formatter.Annotation);
        return root.AddUsings(directive);
    }

    /// <summary>
    /// Appends <paramref name="newLists"/> after the property's existing attribute lists, one per line at the property's
    /// indentation. When the property has no attribute list yet, its leading trivia (comments, blank lines) moves in front of
    /// the first new list, so the attributes sit directly above the declaration and below its comment.
    /// </summary>
    internal static PropertyDeclarationSyntax AppendAttributeLists(PropertyDeclarationSyntax property, IReadOnlyList<AttributeListSyntax> newLists)
    {
        if (newLists.Count == 0)
        {
            return property;
        }

        // The first token after the attribute lists (a modifier, or the type) carries the declaration line's indentation.
        var declarationToken = property.Modifiers.Count > 0 ? property.Modifiers[0] : property.Type.GetFirstToken();
        var leading = declarationToken.LeadingTrivia;
        var indent = leading.Count > 0 && leading[leading.Count - 1].IsKind(SyntaxKind.WhitespaceTrivia)
            ? SyntaxFactory.TriviaList(leading[leading.Count - 1])
            : SyntaxTriviaList.Empty;

        var lists = newLists
            .Select(list => list.WithLeadingTrivia(indent).WithTrailingTrivia(NewLine).WithAdditionalAnnotations(Formatter.Annotation))
            .ToList();
        if (property.AttributeLists.Count == 0)
        {
            lists[0] = lists[0].WithLeadingTrivia(leading);
            property = property.ReplaceToken(declarationToken, declarationToken.WithLeadingTrivia(indent));
        }

        return property.WithAttributeLists(property.AttributeLists.AddRange(lists));
    }

    /// <summary>
    /// Formats only the nodes of <paramref name="newRoot"/> annotated with <see cref="Formatter.Annotation"/> (through
    /// <see cref="RoslynFormattingHelper.FormatAnnotatedSpansAsync"/>) and returns the full text: untouched lines keep their
    /// text and terminator, and every changed line takes the document's dominant line ending.
    /// </summary>
    internal static async Task<string> FormatAnnotatedTextAsync(Document document, SyntaxNode newRoot, CancellationToken cancellationToken)
    {
        var formattedDoc = await RoslynFormattingHelper.FormatAnnotatedSpansAsync(document, newRoot, cancellationToken);
        return (await formattedDoc.GetTextAsync(cancellationToken)).ToString();
    }
}
