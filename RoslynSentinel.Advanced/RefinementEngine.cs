using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;

namespace RoslynSentinel.Advanced;

public class RefinementEngine
{
    private readonly ISolutionProvider _workspaceManager;

    public RefinementEngine(ISolutionProvider workspaceManager)
    {
        _workspaceManager = workspaceManager;
    }

    /// <summary>
    /// Inlines a simple method (expression-body or single-return-statement) by replacing ALL call sites
    /// solution-wide with the method's expression, then removing the method declaration.
    /// Returns a dictionary of filePath→updatedContent for every affected file.
    /// </summary>
    public async Task<Dictionary<FilePathWrapper, string>> InlineMethodAsync(FilePathWrapper filePath, string methodName, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetCurrentSolutionAsync(cancellationToken);
        var document = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault();
        if (document == null)
        {
            throw new ToolNotFoundException($"File '{Path.GetFileName(filePath)}' not found in solution.");
        }

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        if (root == null)
        {
            throw new ToolNotFoundException($"Failed to get syntax root for '{filePath}'.");
        }

        var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
        var method = root.DescendantNodes().OfType<MethodDeclarationSyntax>().FirstOrDefault(m => m.Identifier.Text == methodName);

        if (method == null || semanticModel == null)
        {
            throw new ToolNotFoundException($"Method '{methodName}' not found in '{filePath}'.");
        }

        ExpressionSyntax? expressionToInline = null;
        if (method.ExpressionBody != null)
        {
            expressionToInline = method.ExpressionBody.Expression;
        }
        else if (method.Body?.Statements.Count == 1 && method.Body.Statements[0] is ReturnStatementSyntax ret)
        {
            expressionToInline = ret.Expression;
        }

        if (expressionToInline == null)
        {
            throw new ToolNotFoundException($"Cannot inline '{methodName}': only expression-body or single-return-statement methods are supported. This method has a complex body with multiple statements.");
        }

        var methodSymbol = semanticModel.GetDeclaredSymbol(method, cancellationToken);
        if (methodSymbol == null)
        {
            throw new ToolNotFoundException($"Cannot inline '{methodName}': failed to resolve semantic symbol.");
        }

        // Find ALL references across the solution grouped by document
        var references = await SymbolFinder.FindReferencesAsync(methodSymbol, solution, cancellationToken);
        var byDocument = references
            .SelectMany(r => r.Locations)
            .Where(l => l.Document.FilePath != null)
            .GroupBy(l => l.Document.Id)
            .ToDictionary(g => g.Key, g => g.ToList());

        var result = new Dictionary<FilePathWrapper, string>();
        var expressionTemplate = expressionToInline; // capture once

        // Process each document that has call sites (including the defining document)
        foreach (var (docId, locations) in byDocument)
        {
            var doc = solution.GetDocument(docId);
            if (doc?.FilePath == null)
            {
                continue;
            }

            var docRoot = await doc.GetSyntaxRootAsync(cancellationToken);
            if (docRoot == null)
            {
                continue;
            }

            var callSiteNodes = new List<InvocationExpressionSyntax>();
            foreach (var location in locations)
            {
                var node = docRoot.FindNode(location.Location.SourceSpan)
                    .AncestorsAndSelf().OfType<InvocationExpressionSyntax>().FirstOrDefault();
                if (node != null)
                {
                    callSiteNodes.Add(node);
                }
            }

            if (callSiteNodes.Count == 0)
            {
                continue;
            }

            var updatedDocRoot = docRoot.ReplaceNodes(
                callSiteNodes,
                (original, _) => expressionTemplate.WithTriviaFrom(original));
            result[doc.FilePath] = updatedDocRoot.NormalizeWhitespace().ToFullString();
        }

        // Remove the method declaration from the defining document
        var definingFilePath = document.FilePath!;
        var definingRoot = result.TryGetValue(definingFilePath, out var already)
            ? Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(already, cancellationToken: cancellationToken).GetRoot(cancellationToken)
            : root;
        var methodToRemove = definingRoot.DescendantNodes().OfType<MethodDeclarationSyntax>()
            .FirstOrDefault(m => m.Identifier.Text == methodName);
        if (methodToRemove != null)
        {
            var withoutMethod = definingRoot.RemoveNode(methodToRemove, SyntaxRemoveOptions.KeepUnbalancedDirectives);
            result[definingFilePath] = withoutMethod?.NormalizeWhitespace().ToFullString() ?? definingRoot.ToFullString();
        }
        else if (!result.ContainsKey(definingFilePath))
        {
            // Method had no callers but still needs the declaration removed
            var withoutMethod = root.RemoveNode(method, SyntaxRemoveOptions.KeepUnbalancedDirectives);
            result[definingFilePath] = withoutMethod?.NormalizeWhitespace().ToFullString() ?? root.ToFullString();
        }

        return result;
    }
}
