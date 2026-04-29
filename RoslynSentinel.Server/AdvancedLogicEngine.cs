using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;

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
        var document = solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null) return new Dictionary<string, string>();

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
        
        var variable = root?.DescendantNodes().OfType<VariableDeclaratorSyntax>().FirstOrDefault(v => v.Identifier.Text == boolName);
        ISymbol? symbol = null;
        if (variable != null) symbol = semanticModel?.GetDeclaredSymbol(variable, cancellationToken);
        else 
        {
            var method = root?.DescendantNodes().OfType<MethodDeclarationSyntax>().FirstOrDefault(m => m.Identifier.Text == boolName);
            if (method != null) symbol = semanticModel?.GetDeclaredSymbol(method, cancellationToken);
        }

        if (symbol == null) return new Dictionary<string, string>();

        var references = await SymbolFinder.FindReferencesAsync(symbol, solution, cancellationToken);
        var updatedSolution = solution;

        foreach (var referencedSymbol in references)
        {
            foreach (var location in referencedSymbol.Locations)
            {
                var refDocument = updatedSolution.GetDocument(location.Document.Id)!;
                var refRoot = await refDocument.GetSyntaxRootAsync(cancellationToken);
                var node = refRoot?.FindNode(location.Location.SourceSpan) as ExpressionSyntax;
                
                if (node != null)
                {
                    var parent = node.Parent;
                    if (parent is PrefixUnaryExpressionSyntax p && p.IsKind(SyntaxKind.LogicalNotExpression))
                    {
                        refRoot = refRoot!.ReplaceNode(p, node);
                    }
                    else
                    {
                        var inverted = SyntaxFactory.PrefixUnaryExpression(SyntaxKind.LogicalNotExpression, node);
                        refRoot = refRoot!.ReplaceNode(node, inverted);
                    }
                    updatedSolution = updatedSolution.WithDocumentSyntaxRoot(refDocument.Id, refRoot!);
                }
            }
        }
        
        var changes = new Dictionary<string, string>();
        foreach (var projectChange in updatedSolution.GetChanges(solution).GetProjectChanges())
        {
            foreach (var changedDocId in projectChange.GetChangedDocuments())
            {
                var doc = updatedSolution.GetDocument(changedDocId)!;
                changes[doc.FilePath ?? doc.Name] = (await doc.GetTextAsync(cancellationToken)).ToString();
            }
        }
        return changes;
    }

    public async Task<string> ConvertIfToSwitchExpressionAsync(string filePath, string methodName, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null) return "";
        var root = await document.GetSyntaxRootAsync(cancellationToken);
        return root?.ToFullString() ?? "";
    }

    public async Task<string> ConvertIfToSwitchStatementAsync(string filePath, string methodName, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null) return "";
        var root = await document.GetSyntaxRootAsync(cancellationToken);
        return root?.ToFullString() ?? "";
    }

    public async Task<string> ExtensionToStaticAsync(string filePath, string methodName, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
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
                return root!.ReplaceNode(methodNode, newMethod).NormalizeWhitespace().ToFullString();
            }
        }
        return root?.ToFullString() ?? "";
    }

    public async Task<string> ConvertStaticToExtensionAsync(string filePath, string methodName, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null) return "";
        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var methodNode = root?.DescendantNodes().OfType<MethodDeclarationSyntax>().FirstOrDefault(m => m.Identifier.Text == methodName);
        if (methodNode != null && methodNode.ParameterList.Parameters.Any())
        {
            var firstParam = methodNode.ParameterList.Parameters[0];
            if (!firstParam.Modifiers.Any(m => m.IsKind(SyntaxKind.ThisKeyword)))
            {
                var newParam = firstParam.WithModifiers(firstParam.Modifiers.Insert(0, SyntaxFactory.Token(SyntaxKind.ThisKeyword)));
                var newMethod = methodNode.WithParameterList(methodNode.ParameterList.WithParameters(methodNode.ParameterList.Parameters.Replace(firstParam, newParam)));
                return root!.ReplaceNode(methodNode, newMethod).NormalizeWhitespace().ToFullString();
            }
        }
        return root?.ToFullString() ?? "";
    }

    public async Task<string> ConvertForEachToForAsync(string filePath, int line, CancellationToken ct = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null) return "";
        var root = await document.GetSyntaxRootAsync(ct);
        return root?.ToFullString() ?? "";
    }

    public async Task<string> ConvertForToForEachAsync(string filePath, int line, CancellationToken ct = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null) return "";
        var root = await document.GetSyntaxRootAsync(ct);
        return root?.ToFullString() ?? "";
    }

    public async Task<string> ConvertWhileToForAsync(string filePath, int line, CancellationToken ct = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null) return "";
        var root = await document.GetSyntaxRootAsync(ct);
        return root?.ToFullString() ?? "";
    }
}
