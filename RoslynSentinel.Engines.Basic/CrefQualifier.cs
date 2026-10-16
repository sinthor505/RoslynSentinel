using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace RoslynSentinel.Engines.Basic;

/// <summary>
/// Outcome of qualifying the crefs in one document. Edits are pure insertions in the original text.
/// </summary>
public sealed record CrefQualifierResult(
    IReadOnlyList<TextChange> Edits,
    int Qualified,
    int SkippedAlreadyQualified,
    int SkippedTypeOrPredefined,
    int SkippedUnresolved);

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

        var edits = new List<TextChange>();
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

            edits.Add(new TextChange(new TextSpan(cref.SpanStart, 0), prefix + "."));
            qualified++;
        }

        return new CrefQualifierResult(
            edits.OrderBy(e => e.Span.Start).ToList(),
            qualified,
            alreadyQualified,
            typeOrPredefined,
            unresolved);
    }
}
