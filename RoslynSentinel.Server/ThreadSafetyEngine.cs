using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RoslynSentinel.Server;

public class ThreadSafetyEngine
{
    private readonly PersistentWorkspaceManager _workspaceManager;

    public ThreadSafetyEngine(PersistentWorkspaceManager workspaceManager)
    {
        _workspaceManager = workspaceManager;
    }

    /// <summary>
    /// Adds a private lock object and wraps a method's body in a lock statement.
    /// </summary>
    public async Task<string> MakeMethodThreadSafeAsync(string filePath, string methodName, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault();
        if (document == null) throw new Exception("File not found.");

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var method = root?.DescendantNodes().OfType<MethodDeclarationSyntax>().FirstOrDefault(m => m.Identifier.Text == methodName);
        if (method == null || method.Body == null) throw new Exception("Method or body not found.");

        var typeNode = method.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault();
        if (typeNode == null) throw new Exception("Type not found.");

        var lockFieldName = "_lock";
        var lockField = SyntaxFactory.FieldDeclaration(
            SyntaxFactory.VariableDeclaration(SyntaxFactory.ParseTypeName("object"))
            .WithVariables(SyntaxFactory.SingletonSeparatedList(
                SyntaxFactory.VariableDeclarator(lockFieldName)
                .WithInitializer(SyntaxFactory.EqualsValueClause(SyntaxFactory.ObjectCreationExpression(SyntaxFactory.ParseTypeName("object")).WithArgumentList(SyntaxFactory.ArgumentList()))))))
            .AddModifiers(SyntaxFactory.Token(SyntaxKind.PrivateKeyword), SyntaxFactory.Token(SyntaxKind.ReadOnlyKeyword));

        // Only add lock field if it doesn't exist
        var existingLock = typeNode.Members.OfType<FieldDeclarationSyntax>().Any(f => f.Declaration.Variables.Any(v => v.Identifier.Text == lockFieldName));
        
        var newBody = SyntaxFactory.Block(
            SyntaxFactory.LockStatement(
                SyntaxFactory.IdentifierName(lockFieldName),
                method.Body));

        var newMethod = method.WithBody(newBody);
        var newTypeNode = typeNode.ReplaceNode(method, newMethod);
        
        if (!existingLock)
        {
            newTypeNode = newTypeNode.InsertNodesBefore(newTypeNode.Members.First(), new[] { lockField });
        }

        var newRoot = root!.ReplaceNode(typeNode, newTypeNode);
        return newRoot.NormalizeWhitespace().ToFullString();
    }
}
