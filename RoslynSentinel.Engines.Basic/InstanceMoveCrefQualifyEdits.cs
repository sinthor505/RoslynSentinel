using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace RoslynSentinel.Engines.Basic;

/// <summary>
/// The cref qualify edits collected for the moved members of an instance move. <paramref name = "Edits"/> are the
/// qualifying insertions, sorted by position, computed against the source document's ORIGINAL text.
/// <paramref name = "DroppedByRebind"/> is document-wide, informational: counts rebind drops anywhere in the source
/// document, including crefs outside the moved members.
/// </summary>
public sealed record MovedDocQualifyResult(IReadOnlyList<TextChange> Edits, int DroppedByRebind);

/// <summary>
/// Collects the cref qualify edits for an instance move: bare crefs inside the moved members' own documentation
/// comments that name other members of the source type are qualified with the source type name, so they still
/// resolve after the members leave it. Crefs belonging to the moved symbols themselves are left to the retarget collector.
/// </summary>
public static class InstanceMoveCrefQualifyEdits
{
    /// <summary>
    /// Qualifies the bare crefs of the source type's other members, then keeps only the edits whose position lies
    /// inside one of the moved members. Edits are returned in position order.
    /// </summary>
    /// <remarks>
    /// <paramref name = "membersToMove"/> and <paramref name = "movedSymbols"/> must come from the same solution snapshot
    /// as <paramref name = "sourceDocument"/>: spans are compared by value. The movedSymbols exclusion needs symbols from
    /// the same compilation as <paramref name = "sourceDocument"/>.
    /// Members inherited from a base type are not qualified (their ContainingType is the base, not the source type).
    /// For a mixed overload group (some candidates moved, some stay), CrefQualifier qualifies the cref when any candidate
    /// passes the filter, so the cref narrows to the remaining overloads after the move. A group where every candidate
    /// moved is left alone.
    /// <paramref name = "DroppedByRebind"/> is document-wide, informational: it counts rebind drops anywhere in the
    /// source document, including crefs outside the moved members.
    /// </remarks>
    public static async Task<MovedDocQualifyResult> CollectMovedDocQualifyEditsAsync(
        Document sourceDocument,
        INamedTypeSymbol sourceType,
        IReadOnlyList<ISymbol> movedSymbols,
        IReadOnlyList<MemberDeclarationSyntax> membersToMove,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sourceDocument);
        ArgumentNullException.ThrowIfNull(sourceType);
        ArgumentNullException.ThrowIfNull(movedSymbols);
        ArgumentNullException.ThrowIfNull(membersToMove);

        if (membersToMove.Count == 0)
        {
            return new MovedDocQualifyResult(Array.Empty<TextChange>(), 0);
        }

        var sourceTypeDefinition = sourceType.OriginalDefinition;
        var result = await CrefQualifier.QualifyDocumentAsync(
            sourceDocument,
            s => SymbolEqualityComparer.Default.Equals(s.ContainingType?.OriginalDefinition, sourceTypeDefinition)
                && !movedSymbols.Any(m => SymbolEqualityComparer.Default.Equals(m, s)),
            cancellationToken);

        var edits = result.Edits
            .Where(e => membersToMove.Any(m => m.FullSpan.Contains(e.Span.Start)))
            .ToList();

        return new MovedDocQualifyResult(edits, result.DroppedByRebind);
    }

    /// <summary>
    /// Returns true when any moved member's documentation comments contain an outermost bare cref: a member cref
    /// (<c>NameMemberCref</c>, <c>IndexerMemberCref</c>, <c>OperatorMemberCref</c> or <c>ConversionOperatorMemberCref</c>)
    /// such as <c>Count</c> or <c>Count(int)</c>. Qualified crefs are not counted. Purely syntactic; no semantic model
    /// is consulted. This over-approximates: a bare cref naming a moved symbol needs no qualify edit, but still counts.
    /// </summary>
    public static bool HasBareCrefInMovedDocs(IReadOnlyList<MemberDeclarationSyntax> membersToMove)
    {
        ArgumentNullException.ThrowIfNull(membersToMove);

        return membersToMove.Any(m => m.DescendantNodes(descendIntoTrivia: true)
            .OfType<CrefSyntax>()
            .Any(c => c.Parent is XmlCrefAttributeSyntax
                && c is NameMemberCrefSyntax or IndexerMemberCrefSyntax or OperatorMemberCrefSyntax or ConversionOperatorMemberCrefSyntax));
    }
}
