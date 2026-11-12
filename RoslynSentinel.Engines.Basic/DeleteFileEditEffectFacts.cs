using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

using RoslynSentinel.Common;

namespace RoslynSentinel.Engines.Basic;

/// <summary>
/// Gathers the facts a DeleteFile <see cref="EditEffect"/> needs before the file is gone: the top-level types the file declares, so a later
/// lookup by type name (not only by file name) explains the miss. Never throws and never blocks the delete; an unresolved file yields no rows.
/// </summary>
public static class DeleteFileEditEffectFacts
{
    /// <summary>
    /// The top-level types and delegates declared in the document at <paramref name="filePath"/>, read from the committed
    /// <paramref name="solution"/>: container-qualified display name, documentation comment id and simple name. A file that is not in the solution
    /// yields none. A <c>partial</c> type is skipped: deleting one part removes the file but not the type, so "removed" would be false.
    /// </summary>
    public static async Task<IReadOnlyList<(string Display, string? DocId, string SimpleName)>> DescribeDeclaredTypesAsync(
        Solution solution,
        FilePathWrapper filePath,
        CancellationToken cancellationToken)
    {
        try
        {
            var document = DocumentLookup.FindDocumentOrNull(solution, filePath);
            if (document is null)
            {
                return [];
            }

            var root = await document.GetSyntaxRootAsync(cancellationToken);
            var model = await document.GetSemanticModelAsync(cancellationToken);
            if (root is null || model is null)
            {
                return [];
            }

            var rows = new List<(string Display, string? DocId, string SimpleName)>();
            foreach (var node in root.DescendantNodes(n => n is CompilationUnitSyntax or BaseNamespaceDeclarationSyntax))
            {
                if (node is TypeDeclarationSyntax type && type.Modifiers.Any(SyntaxKind.PartialKeyword))
                {
                    continue;
                }

                if (node is not (BaseTypeDeclarationSyntax or DelegateDeclarationSyntax))
                {
                    continue;
                }

                if (model.GetDeclaredSymbol(node, cancellationToken) is { } symbol)
                {
                    rows.Add((EditEffectNames.Display(symbol), symbol.GetDocumentationCommentId(), EditEffectNames.SimpleName(symbol.Name)));
                }
            }

            return rows;
        }
        catch (Exception ex) when (ex is ToolException or InvalidOperationException or ArgumentException)
        {
            return [];
        }
    }
}
