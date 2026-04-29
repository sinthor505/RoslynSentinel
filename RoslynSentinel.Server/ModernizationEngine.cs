using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RoslynSentinel.Server;

public class ModernizationEngine
{
    private readonly PersistentWorkspaceManager _workspaceManager;

    public ModernizationEngine(PersistentWorkspaceManager workspaceManager)
    {
        _workspaceManager = workspaceManager;
    }

    /// <summary>
    /// Converts a class to a record where possible.
    /// </summary>
    public async Task<string> ClassToRecordAsync(string filePath, string className, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null) throw new Exception("File not found.");

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var classNode = root?.DescendantNodes().OfType<ClassDeclarationSyntax>().FirstOrDefault(c => c.Identifier.Text == className);
        if (classNode == null) throw new Exception("Class not found.");

        // Create a Record declaration
        var recordNode = SyntaxFactory.RecordDeclaration(SyntaxFactory.Token(SyntaxKind.RecordKeyword), classNode.Identifier)
            .WithModifiers(classNode.Modifiers)
            .WithParameterList(SyntaxFactory.ParameterList(SyntaxFactory.SeparatedList(
                classNode.Members.OfType<PropertyDeclarationSyntax>().Select(p => 
                    SyntaxFactory.Parameter(p.Identifier).WithType(p.Type)))))
            .WithMembers(SyntaxFactory.List(classNode.Members.Where(m => m is not PropertyDeclarationSyntax)))
            .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken));

        if (recordNode.Members.Count > 0)
        {
            // If it has members, it shouldn't have a trailing semicolon on the header
            recordNode = recordNode.WithSemicolonToken(default);
        }

        var newRoot = root!.ReplaceNode(classNode, recordNode);
        return newRoot.NormalizeWhitespace().ToFullString();
    }

    /// <summary>
    /// Converts traditional namespaces to file-scoped namespaces.
    /// </summary>
    public async Task<string> UseFileScopedNamespaceAsync(string filePath, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault();
        if (document == null) throw new Exception("File not found.");

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var ns = root?.DescendantNodes().OfType<NamespaceDeclarationSyntax>().FirstOrDefault();
        if (ns == null) return root?.ToFullString() ?? "";

        var fileScopedNs = SyntaxFactory.FileScopedNamespaceDeclaration(ns.Name)
            .WithLeadingTrivia(ns.GetLeadingTrivia())
            .WithMembers(ns.Members);

        var newRoot = root!.ReplaceNode(ns, fileScopedNs);
        return newRoot.ToFullString();
    }

    /// <summary>
    /// Converts a simple method body to an expression body.
    /// </summary>
    public async Task<string> BlockToExpressionBodyAsync(string filePath, string methodName, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault();
        if (document == null) throw new Exception("File not found.");

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var method = root?.DescendantNodes().OfType<MethodDeclarationSyntax>()
            .FirstOrDefault(m => m.Identifier.Text == methodName);

        if (method?.Body?.Statements.Count == 1)
        {
            var statement = method.Body.Statements[0];
            ExpressionSyntax? expr = null;
            if (statement is ReturnStatementSyntax ret) expr = ret.Expression;
            else if (statement is ExpressionStatementSyntax ess) expr = expr = ess.Expression;

            if (expr != null)
            {
                var newMethod = method.WithBody(null)
                    .WithExpressionBody(SyntaxFactory.ArrowExpressionClause(expr))
                    .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken));
                
                var newRoot = root!.ReplaceNode(method, newMethod);
                return newRoot.ToFullString();
            }
        }
        return root?.ToFullString() ?? "";
    }

    /// <summary>
    /// Converts a record back to a standard class.
    /// </summary>
    public async Task<string> RecordToClassAsync(string filePath, string recordName, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null) return string.Empty;

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var recordNode = root?.DescendantNodes().OfType<RecordDeclarationSyntax>().FirstOrDefault(r => r.Identifier.Text == recordName);
        if (recordNode == null) return root?.ToFullString() ?? "";

        // 1. Create a class declaration
        var classNode = SyntaxFactory.ClassDeclaration(recordNode.Identifier)
            .WithModifiers(recordNode.Modifiers);

        var properties = new List<MemberDeclarationSyntax>();

        // 2. Convert positional parameters to properties
        if (recordNode.ParameterList != null)
        {
            foreach (var parameter in recordNode.ParameterList.Parameters)
            {
                var property = SyntaxFactory.PropertyDeclaration(parameter.Type!, parameter.Identifier)
                    .WithModifiers(SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.PublicKeyword)))
                    .WithAccessorList(SyntaxFactory.AccessorList(SyntaxFactory.List(new[] {
                        SyntaxFactory.AccessorDeclaration(SyntaxKind.GetAccessorDeclaration).WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken)),
                        SyntaxFactory.AccessorDeclaration(SyntaxKind.InitAccessorDeclaration).WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken))
                    })));
                properties.Add(property);
            }
        }

        // 3. Keep existing members
        properties.AddRange(recordNode.Members);
        classNode = classNode.WithMembers(SyntaxFactory.List(properties));

        var newRoot = root!.ReplaceNode(recordNode, classNode);
        return newRoot.NormalizeWhitespace().ToFullString();
    }
}
