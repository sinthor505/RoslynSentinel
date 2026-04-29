using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RoslynSentinel.Server;

public class AdvancedLogicEngine
{
    private readonly PersistentWorkspaceManager _workspaceManager;

    public AdvancedLogicEngine(PersistentWorkspaceManager workspaceManager)
    {
        _workspaceManager = workspaceManager;
    }

    public async Task<Dictionary<string, string>> InvertBooleanLogicAsync(string filePath, string boolName, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault();
        if (document == null) return new Dictionary<string, string>();

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
        var variable = root?.DescendantNodes().OfType<VariableDeclaratorSyntax>().FirstOrDefault(v => v.Identifier.Text == boolName);
        if (variable == null) return new Dictionary<string, string>();

        var symbol = semanticModel?.GetDeclaredSymbol(variable, cancellationToken);
        if (symbol == null) return new Dictionary<string, string>();

        var references = await Microsoft.CodeAnalysis.FindSymbols.SymbolFinder.FindReferencesAsync(symbol, solution, cancellationToken);
        var updatedSolution = solution;

        foreach (var referencedSymbol in references)
        {
            foreach (var location in referencedSymbol.Locations)
            {
                var refDocument = updatedSolution.GetDocument(location.Document.Id)!;
                var refRoot = await refDocument.GetSyntaxRootAsync(cancellationToken);
                var identifier = refRoot?.FindNode(location.Location.SourceSpan) as IdentifierNameSyntax;
                
                if (identifier != null)
                {
                    var parent = identifier.Parent;
                    if (parent is PrefixUnaryExpressionSyntax p && p.IsKind(SyntaxKind.LogicalNotExpression))
                        refRoot = refRoot!.ReplaceNode(p, identifier);
                    else
                        refRoot = refRoot!.ReplaceNode(identifier, SyntaxFactory.PrefixUnaryExpression(SyntaxKind.LogicalNotExpression, identifier));
                    
                    updatedSolution = updatedSolution.WithDocumentSyntaxRoot(refDocument.Id, refRoot!);
                }
            }
        }
        
        var changes = new Dictionary<string, string>();
        foreach (var docId in updatedSolution.GetChanges(solution).GetProjectChanges().SelectMany(pc => pc.GetChangedDocuments()))
        {
            var doc = updatedSolution.GetDocument(docId)!;
            var text = await doc.GetTextAsync(cancellationToken);
            changes[doc.FilePath!] = text.ToString();
        }
        return changes;
    }

    public async Task<string> ConvertIfToSwitchExpressionAsync(string filePath, string methodName, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault();
        if (document == null) return "";

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var method = root?.DescendantNodes().OfType<MethodDeclarationSyntax>().FirstOrDefault(m => m.Identifier.Text == methodName);
        return root?.ToFullString() ?? "";
    }

    public async Task<string> ConvertIfToSwitchStatementAsync(string filePath, string methodName, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault();
        if (document == null) return "";

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var method = root?.DescendantNodes().OfType<MethodDeclarationSyntax>().FirstOrDefault(m => m.Identifier.Text == methodName);
        return root?.ToFullString() ?? "";
    }

    public async Task<string> ExtensionToStaticAsync(string filePath, string methodName, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault();
        if (document == null) return "";

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var methodNode = root?.DescendantNodes().OfType<MethodDeclarationSyntax>().FirstOrDefault(m => m.Identifier.Text == methodName);

        if (methodNode != null && methodNode.ParameterList.Parameters.Any())
        {
            var firstParam = methodNode.ParameterList.Parameters[0];
            if (firstParam.Modifiers.Any(m => m.IsKind(SyntaxKind.ThisKeyword)))
            {
                var newParam = firstParam.WithModifiers(firstParam.Modifiers.Remove(firstParam.Modifiers.First(m => m.IsKind(SyntaxKind.ThisKeyword))));
                var newMethod = methodNode.WithParameterList(methodNode.ParameterList.WithParameters(methodNode.ParameterList.Parameters.Replace(firstParam, newParam)));
                var newRoot = root!.ReplaceNode(methodNode, newMethod);
                return newRoot.NormalizeWhitespace().ToFullString();
            }
        }
        return root?.ToFullString() ?? "";
    }

    public async Task<string> ConvertStaticToExtensionAsync(string filePath, string methodName, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault();
        if (document == null) return "";

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var methodNode = root?.DescendantNodes().OfType<MethodDeclarationSyntax>().FirstOrDefault(m => m.Identifier.Text == methodName);

        if (methodNode != null && methodNode.ParameterList.Parameters.Any() && methodNode.Modifiers.Any(m => m.IsKind(SyntaxKind.StaticKeyword)))
        {
            var firstParam = methodNode.ParameterList.Parameters[0];
            if (!firstParam.Modifiers.Any(m => m.IsKind(SyntaxKind.ThisKeyword)))
            {
                var newParam = firstParam.WithModifiers(firstParam.Modifiers.Insert(0, SyntaxFactory.Token(SyntaxKind.ThisKeyword)));
                var newMethod = methodNode.WithParameterList(methodNode.ParameterList.WithParameters(methodNode.ParameterList.Parameters.Replace(firstParam, newParam)));
                var newRoot = root!.ReplaceNode(methodNode, newMethod);
                return newRoot.NormalizeWhitespace().ToFullString();
            }
        }
        return root?.ToFullString() ?? "";
    }

    public async Task<string> ConvertForEachToForAsync(string filePath, int line, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault();
        if (document == null) return "";

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var sourceText = await document.GetTextAsync(cancellationToken);
        var position = sourceText.Lines[line - 1].Start;
        var node = root?.FindNode(new Microsoft.CodeAnalysis.Text.TextSpan(position, 0))?.AncestorsAndSelf().OfType<ForEachStatementSyntax>().FirstOrDefault();

        if (node != null)
        {
            // logic to build for(int i=0; i<col.Count; i++) ...
            return root!.ToFullString();
        }
        return root?.ToFullString() ?? "";
    }

    public async Task<string> ConvertForToForEachAsync(string filePath, int line, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault();
        if (document == null) return "";

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var sourceText = await document.GetTextAsync(cancellationToken);
        var position = sourceText.Lines[line - 1].Start;
        var node = root?.FindNode(new Microsoft.CodeAnalysis.Text.TextSpan(position, 0))?.AncestorsAndSelf().OfType<ForStatementSyntax>().FirstOrDefault();

        if (node != null)
        {
            // logic to build foreach(var item in col) ...
            return root!.ToFullString();
        }
        return root?.ToFullString() ?? "";
    }

    public async Task<string> ConvertWhileToForAsync(string filePath, int line, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault();
        if (document == null) return "";

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var sourceText = await document.GetTextAsync(cancellationToken);
        var position = sourceText.Lines[line - 1].Start;
        var node = root?.FindNode(new Microsoft.CodeAnalysis.Text.TextSpan(position, 0))?.AncestorsAndSelf().OfType<WhileStatementSyntax>().FirstOrDefault();

        if (node != null)
        {
            // logic to build for(...) ...
            return root!.ToFullString();
        }
        return root?.ToFullString() ?? "";
    }
}
