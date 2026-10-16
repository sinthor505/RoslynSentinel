using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace RoslynSentinel.Engines.Basic;

/// <summary>
/// Outcome of qualifying the crefs in one document. Edits are pure insertions in the original text, sorted by
/// position and re-bound in the edited text. DroppedByRebind counts edits discarded because their cref no longer
/// binds to the same symbol after the edits are applied.
/// </summary>
public sealed record CrefQualifierResult(
    IReadOnlyList<TextChange> Edits,
    int Qualified,
    int SkippedAlreadyQualified,
    int SkippedTypeOrPredefined,
    int SkippedUnresolved,
    int DroppedByRebind = 0);

/// <summary>
/// Adds the containing type to bare member crefs (for example, the cref in a doc comment that names Foo(int))
/// by inserting "Type." before the cref text. Works on the document's ORIGINAL text, so nothing outside the
/// inserted prefixes is re-serialised.
/// A document whose DocumentationMode is None yields no crefs, so the result is all zeros.
/// </summary>
public static class CrefQualifier
{
    /// <summary>
    /// Computes the qualifying insertions for every outermost cref in the document. Crefs that are already
    /// qualified, name a type, or do not bind are counted but not edited. Only methods, properties, fields and
    /// events are qualified; every other bound symbol kind (types, type parameters, namespaces, aliases) is counted
    /// as SkippedTypeOrPredefined. For an overload group that does not bind to one symbol, the cref is qualified
    /// when any candidate passes symbolFilter; the prefix comes from the first candidate, and all candidates must
    /// share a containing type. When symbolFilter is non-null, members it rejects are skipped silently.
    /// Every edit is then re-bound: the document is re-parsed with all edits applied, and each cref at its shifted
    /// position must bind to the same symbol (compared by the DocumentationCommentId of OriginalDefinition) as before. An edit that no longer
    /// binds to that symbol is dropped, counted in DroppedByRebind and removed from Qualified. If the edited document
    /// or its semantic model is unavailable, the edits are returned unverified and nothing is dropped.
    /// </summary>
    public static async Task<CrefQualifierResult> QualifyDocumentAsync(
        Document document,
        Func<ISymbol, bool>? symbolFilter,
        CancellationToken cancellationToken)
    {
        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var model = await document.GetSemanticModelAsync(cancellationToken);
        if (root is null || model is null)
        {
            return new CrefQualifierResult(Array.Empty<TextChange>(), 0, 0, 0, 0);
        }

        var pending = new List<(TextChange Change, int OriginalStart, string? DocId)>();
        var qualified = 0;
        var alreadyQualified = 0;
        var typeOrPredefined = 0;
        var unresolved = 0;

        // Only outermost crefs: a qualified cref such as A.Count also yields its inner NameMemberCref.
        var crefs = root.DescendantNodes(descendIntoTrivia: true)
            .OfType<CrefSyntax>()
            .Where(c => c.Parent is XmlCrefAttributeSyntax);

        foreach (var cref in crefs)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (cref is QualifiedCrefSyntax)
            {
                alreadyQualified++;
                continue;
            }

            if (cref is TypeCrefSyntax)
            {
                typeOrPredefined++;
                continue;
            }

            if (cref is not (NameMemberCrefSyntax or IndexerMemberCrefSyntax or OperatorMemberCrefSyntax or ConversionOperatorMemberCrefSyntax))
            {
                unresolved++;
                continue;
            }

            var info = model.GetSymbolInfo(cref, cancellationToken);
            var symbol = info.Symbol;
            var fromCandidates = symbol is null && info.CandidateSymbols.Length > 0;
            if (fromCandidates)
            {
                // Overload group: usable only when every candidate lives on the same type.
                var first = info.CandidateSymbols[0];
                if (info.CandidateSymbols.All(c => SymbolEqualityComparer.Default.Equals(c.ContainingType, first.ContainingType)))
                {
                    symbol = first;
                }
            }

            if (symbol is null)
            {
                unresolved++;
                continue;
            }

            // Allowlist: only members are qualified. Types, type parameters, namespaces, aliases and the like are skipped.
            if (symbol is not (IMethodSymbol or IPropertySymbol or IFieldSymbol or IEventSymbol))
            {
                typeOrPredefined++;
                continue;
            }

            // Filter: an overload group qualifies when any candidate passes; a bound symbol must pass itself.
            var passesFilter = symbolFilter is null
                || (fromCandidates ? info.CandidateSymbols.Any(symbolFilter) : symbolFilter(symbol));
            if (!passesFilter)
            {
                continue;
            }

            if (symbol.ContainingType is null)
            {
                unresolved++;
                continue;
            }

            // Generic containing types: cref syntax uses braces, so '<' and '>' become '{' and '}'.
            var prefix = symbol.ContainingType
                .ToMinimalDisplayString(model, cref.SpanStart)
                .Replace('<', '{')
                .Replace('>', '}');

            pending.Add((new TextChange(new TextSpan(cref.SpanStart, 0), prefix + "."), cref.SpanStart, symbol.OriginalDefinition.GetDocumentationCommentId()));
            qualified++;
        }

        var ordered = pending.OrderBy(p => p.Change.Span.Start).ToList();
        var edits = ordered.Select(p => p.Change).ToList();
        var dropped = 0;
        if (ordered.Count > 0)
        {
            var originalText = await document.GetTextAsync(cancellationToken);
            var editedDocument = document.WithText(originalText.WithChanges(edits));
            var editedRoot = await editedDocument.GetSyntaxRootAsync(cancellationToken);
            var editedModel = await editedDocument.GetSemanticModelAsync(cancellationToken);
            if (editedRoot is null || editedModel is null)
            {
                // Re-bind could not run: return the edits unverified rather than dropping them silently.
                return new CrefQualifierResult(edits, qualified, alreadyQualified, typeOrPredefined, unresolved);
            }

            // Re-binds the cref starting at newStart in the edited tree and compares its DocumentationCommentId with the pre-edit one.
            // Symbols from different compilations never compare equal, so the id string is the identity. A null id on either side fails.
            static bool ResolvesToSameSymbol(SyntaxNode root, SemanticModel model, int newStart, string? expectedDocId, CancellationToken ct)
            {
                var cref = root.FindToken(newStart, findInsideTrivia: true).Parent?
                    .AncestorsAndSelf()
                    .OfType<CrefSyntax>()
                    .FirstOrDefault(c => c.Parent is XmlCrefAttributeSyntax);
                if (cref is null || cref.SpanStart != newStart)
                {
                    return false;
                }

                var info = model.GetSymbolInfo(cref, ct);
                var bound = info.Symbol ?? (info.CandidateSymbols.Length > 0 ? info.CandidateSymbols[0] : null);
                var boundId = bound?.OriginalDefinition.GetDocumentationCommentId();
                return expectedDocId is not null && boundId is not null && boundId == expectedDocId;
            }

            var verified = new List<TextChange>();
            var shift = 0;
            foreach (var p in ordered)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Every earlier edit (survivor or not) is applied in the edited text, so it shifts this cref.
                var newStart = p.OriginalStart + shift;
                shift += p.Change.NewText?.Length ?? 0;
                if (ResolvesToSameSymbol(editedRoot, editedModel, newStart, p.DocId, cancellationToken))
                {
                    verified.Add(p.Change);
                }
                else
                {
                    dropped++;
                }
            }

            edits = verified;
            qualified -= dropped;
        }

        return new CrefQualifierResult(
            edits,
            qualified,
            alreadyQualified,
            typeOrPredefined,
            unresolved,
            dropped);
    }
}
