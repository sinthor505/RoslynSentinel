using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Text;

namespace RoslynSentinel.Engines.Basic;

/// <summary>
/// The cref retarget edits collected for an instance move. <paramref name = "EditsByDocument"/> holds, per document,
/// the edits sorted by span start, each computed against that document's ORIGINAL text. <paramref name = "Retargeted"/>
/// is the total number of edits; <paramref name = "SkippedCandidates"/> counts distinct crefs that match only as
/// candidates (ambiguous) and were therefore not edited.
/// </summary>
public sealed record InstanceMoveCrefEditsResult(IReadOnlyDictionary<DocumentId, IReadOnlyList<TextChange>> EditsByDocument, int Retargeted, int SkippedCandidates);

/// <summary>
/// Collects the cref retarget edits for an instance move: every reference to a moved symbol that sits inside a
/// documentation comment (cref) gets its qualifier replaced or inserted with the target class name, as
/// MoveMemberTextEdits.BuildReferenceEdit describes. Non-cref references (real call sites) are not handled here.
/// </summary>
public static class InstanceMoveCrefEdits
{
    // TODO: share the reference loop with MemberRefactoringEngine.CollectStaticMoveReferenceEditsAsync
    /// <summary>
    /// Collects one minimal cref edit per reference location of the moved symbols that lies inside a cref, grouped by
    /// document. Bare-name insertions inside the moved members themselves are skipped: those members move together,
    /// so their bare crefs still resolve at the destination (the qualify step handles them).
    /// </summary>
    /// <remarks>
    /// <paramref name = "membersToMove"/> and <paramref name = "movedSymbols"/> must come from the same solution snapshot
    /// as <paramref name = "solution"/>: spans are compared by value. The source document's entry in EditsByDocument
    /// includes edits inside the moved members; the consumer must route those into the moved block text, not the source remainder.
    /// </remarks>
    public static async Task<InstanceMoveCrefEditsResult> CollectRetargetEditsAsync(Solution solution, DocumentId sourceDocumentId, IReadOnlyList<MemberDeclarationSyntax> membersToMove, IReadOnlyList<ISymbol> movedSymbols, string targetClassName, CancellationToken cancellationToken)
    {
        var movedSpans = membersToMove.Select(m => m.FullSpan).ToList();
        var sourceFilePath = solution.GetDocument(sourceDocumentId)?.FilePath;
        var editsByDocument = new Dictionary<DocumentId, List<TextChange>>();
        var rootsByDocument = new Dictionary<DocumentId, SyntaxNode?>();
        var skippedCandidates = 0;
        var countedCandidateCrefs = new HashSet<(DocumentId, int)>();
        foreach (var symbol in movedSymbols)
        {
            var references = await SymbolFinder.FindReferencesAsync(symbol, solution, cancellationToken);
            foreach (var location in references.SelectMany(r => r.Locations))
            {
                var document = location.Document;
                if (document.FilePath == null)
                {
                    continue;
                }

                if (!rootsByDocument.TryGetValue(document.Id, out var documentRoot))
                {
                    documentRoot = await document.GetSyntaxRootAsync(cancellationToken);
                    rootsByDocument[document.Id] = documentRoot;
                }

                if (documentRoot == null)
                {
                    continue;
                }

                var span = location.Location.SourceSpan;
                var refToken = documentRoot.FindToken(span.Start, findInsideTrivia: true);
                var isCref = refToken.Parent?.AncestorsAndSelf().Any(n => n is CrefSyntax) == true;
                TextChange? edit = null;
                var insideMovedMember = false;
                if (isCref)
                {
                    edit = MoveMemberTextEdits.BuildReferenceEdit(documentRoot, span, targetClassName);
                    insideMovedMember = sourceFilePath != null
                        && string.Equals(document.FilePath, sourceFilePath, StringComparison.OrdinalIgnoreCase)
                        && movedSpans.Any(s => s.Contains(span));
                }

                if (location.IsCandidateLocation)
                {
                    // Bare crefs inside moved members move with them and still resolve; not a skip.
                    var bareInsideMovedMember = insideMovedMember && edit != null && edit.Value.Span.Length == 0;
                    if (isCref && !bareInsideMovedMember && countedCandidateCrefs.Add((document.Id, span.Start)))
                    {
                        skippedCandidates++;
                    }

                    continue;
                }

                if (edit == null)
                {
                    continue;
                }

                if (insideMovedMember && edit.Value.Span.Length == 0)
                {
                    continue;
                }

                if (!editsByDocument.TryGetValue(document.Id, out var documentEdits))
                {
                    documentEdits = new List<TextChange>();
                    editsByDocument[document.Id] = documentEdits;
                }

                if (!documentEdits.Any(e => e.Span == edit.Value.Span))
                {
                    documentEdits.Add(edit.Value);
                }
            }
        }

        var result = new Dictionary<DocumentId, IReadOnlyList<TextChange>>();
        var retargeted = 0;
        foreach (var (documentId, edits) in editsByDocument)
        {
            var sorted = edits.OrderBy(e => e.Span.Start).ToList();
            retargeted += sorted.Count;
            result[documentId] = sorted;
        }

        return new InstanceMoveCrefEditsResult(result, retargeted, skippedCandidates);
    }
}
