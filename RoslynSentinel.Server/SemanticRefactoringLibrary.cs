using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RoslynSentinel.Server;

public class SemanticRefactoringLibrary
{
    private readonly PersistentWorkspaceManager _workspaceManager;

    public SemanticRefactoringLibrary(PersistentWorkspaceManager workspaceManager)
    {
        _workspaceManager = workspaceManager;
    }

    /// <summary>
    /// Inlines a temporary variable by replacing its usages with its initializer.
    /// </summary>
    public async Task<string> InlineVariableAsync(string filePath, string variableName, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault();
        if (document == null) return "";

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
        
        var variable = root?.DescendantNodes().OfType<VariableDeclaratorSyntax>()
            .FirstOrDefault(v => v.Identifier.Text == variableName);

        if (variable != null && variable.Initializer != null)
        {
            var value = variable.Initializer.Value;
            var usages = root!.DescendantNodes().OfType<IdentifierNameSyntax>()
                .Where(i => i.Identifier.Text == variableName && semanticModel!.GetSymbolInfo(i).Symbol?.Name == variableName);
            
            var newRoot = root.ReplaceNodes(usages, (old, _) => value.WithTriviaFrom(old));
            newRoot = newRoot.RemoveNode(variable.Parent!.Parent!, SyntaxRemoveOptions.KeepUnbalancedDirectives)!;
            return newRoot.NormalizeWhitespace().ToFullString();
        }

        return root?.ToFullString() ?? "";
    }

    /// <summary>
    /// Converts a simple property into a get/set method pair.
    /// </summary>
    public async Task<string> ConvertPropertyToMethodsAsync(string filePath, string className, string propertyName, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault();
        if (document == null) return "";

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var classNode = root?.DescendantNodes().OfType<ClassDeclarationSyntax>().FirstOrDefault(c => c.Identifier.Text == className);
        var propNode = classNode?.Members.OfType<PropertyDeclarationSyntax>().FirstOrDefault(p => p.Identifier.Text == propertyName);

        if (classNode == null || propNode == null) return root?.ToFullString() ?? "";

        var getter = SyntaxFactory.MethodDeclaration(propNode.Type, $"Get{propertyName}")
            .AddModifiers(SyntaxFactory.Token(SyntaxKind.PublicKeyword))
            .WithBody(SyntaxFactory.Block(SyntaxFactory.ReturnStatement(SyntaxFactory.IdentifierName(propertyName.ToLowerInvariant()))));

        var setter = SyntaxFactory.MethodDeclaration(SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.VoidKeyword)), $"Set{propertyName}")
            .AddModifiers(SyntaxFactory.Token(SyntaxKind.PublicKeyword))
            .AddParameterListParameters(SyntaxFactory.Parameter(SyntaxFactory.Identifier("value")).WithType(propNode.Type))
            .WithBody(SyntaxFactory.Block(SyntaxFactory.ExpressionStatement(SyntaxFactory.AssignmentExpression(
                SyntaxKind.SimpleAssignmentExpression, SyntaxFactory.IdentifierName(propertyName.ToLowerInvariant()), SyntaxFactory.IdentifierName("value")))));

        var field = SyntaxFactory.FieldDeclaration(SyntaxFactory.VariableDeclaration(propNode.Type)
            .WithVariables(SyntaxFactory.SingletonSeparatedList(SyntaxFactory.VariableDeclarator(propertyName.ToLowerInvariant()))))
            .AddModifiers(SyntaxFactory.Token(SyntaxKind.PrivateKeyword));

        var newClass = classNode.ReplaceNode(propNode, new MemberDeclarationSyntax[] { field, getter, setter });
        var newRoot = root!.ReplaceNode(classNode, newClass);
        
        return newRoot.NormalizeWhitespace().ToFullString();
    }

    /// <summary>
    /// Wraps a block of code in a using statement for an IDisposable object.
    /// </summary>
    public async Task<string> WrapInUsingAsync(string filePath, int startLine, int endLine, string disposalName, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault();
        if (document == null) return "";

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var sourceText = await document.GetTextAsync(cancellationToken);
        var span = Microsoft.CodeAnalysis.Text.TextSpan.FromBounds(sourceText.Lines[startLine - 1].Start, sourceText.Lines[endLine - 1].End);
        
        var nodes = root?.DescendantNodes(span).Where(n => n is StatementSyntax && n.Parent is BlockSyntax).Cast<StatementSyntax>().ToList();
        if (nodes == null || !nodes.Any()) return "";

        var firstNode = nodes[0];
        var parentBlock = (BlockSyntax)firstNode.Parent!;

        var usingStatement = SyntaxFactory.UsingStatement(
            SyntaxFactory.VariableDeclaration(SyntaxFactory.IdentifierName("var"))
                .WithVariables(SyntaxFactory.SingletonSeparatedList(
                    SyntaxFactory.VariableDeclarator(disposalName))),
            null,
            SyntaxFactory.Block(nodes));

        // Rebuild the parent block's statement list: replace the entire selected range
        // with the single using statement. ReplaceNodes(delegate returning null) would crash Roslyn.
        var origStatements = parentBlock.Statements.ToList();
        int startIdx = origStatements.IndexOf(nodes[0]);
        int endIdx   = origStatements.IndexOf(nodes[^1]);
        var newStatements = new List<StatementSyntax>();
        newStatements.AddRange(origStatements.Take(startIdx));
        newStatements.Add(usingStatement);
        newStatements.AddRange(origStatements.Skip(endIdx + 1));

        var newBlock = parentBlock.WithStatements(SyntaxFactory.List(newStatements));
        var newRoot  = root!.ReplaceNode(parentBlock, newBlock);
        
        return newRoot.NormalizeWhitespace().ToFullString();
    }
}
