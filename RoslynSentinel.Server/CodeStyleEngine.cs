using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RoslynSentinel.Server;

public class CodeStyleEngine
{
    private readonly PersistentWorkspaceManager _workspaceManager;

    public CodeStyleEngine(PersistentWorkspaceManager workspaceManager)
    {
        _workspaceManager = workspaceManager;
    }

    public async Task<string> SimplifyAllNamesAsync(string filePath, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault();
        if (document == null) return "";

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        if (root == null) return string.Empty;

        var rewriter = new NameSimplifierRewriter();
        var newRoot = rewriter.Visit(root);
        return newRoot.NormalizeWhitespace().ToFullString();
    }

    private class NameSimplifierRewriter : CSharpSyntaxRewriter
    {
        public override SyntaxNode? VisitAliasQualifiedName(AliasQualifiedNameSyntax node)
        {
            if (node.Alias.Identifier.Text == "global") return node.Name;
            return base.VisitAliasQualifiedName(node);
        }

        public override SyntaxNode? VisitMemberAccessExpression(MemberAccessExpressionSyntax node)
        {
            if (node.Expression is ThisExpressionSyntax) return node.Name;
            return base.VisitMemberAccessExpression(node);
        }
    }

    public async Task<string> UseThrowExpressionsAsync(string filePath, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault();
        if (document == null) return "";

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        return root?.ToFullString() ?? "";
    }

    public async Task<string> UseCollectionExpressionsAsync(string filePath, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null) return string.Empty;

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        if (root == null) return string.Empty;

        var rewriter = new CollectionExpressionRewriter();
        var newRoot = rewriter.Visit(root);
        return newRoot.NormalizeWhitespace().ToFullString();
    }

    /// <summary>
    /// Simplifies redundant code patterns (target-typed new, ??=, etc.).
    /// </summary>
    public async Task<string> SimplifyVerbosityAsync(string filePath, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null) return string.Empty;

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        if (root == null) return string.Empty;

        var rewriter = new VerbosityRewriter();
        var newRoot = rewriter.Visit(root);
        return newRoot.NormalizeWhitespace().ToFullString();
    }

    private class CollectionExpressionRewriter : CSharpSyntaxRewriter
    {
        public override SyntaxNode? VisitArrayCreationExpression(ArrayCreationExpressionSyntax node)
        {
            if (node.Initializer == null || node.Initializer.Expressions.Count == 0)
            {
                return SyntaxFactory.CollectionExpression().WithTriviaFrom(node);
            }
            
            var elements = node.Initializer.Expressions;
            return SyntaxFactory.CollectionExpression(SyntaxFactory.SeparatedList<CollectionElementSyntax>(
                elements.Select(e => SyntaxFactory.ExpressionElement(e)))).WithTriviaFrom(node);
        }

        public override SyntaxNode? VisitObjectCreationExpression(ObjectCreationExpressionSyntax node)
        {
            var typeStr = node.Type.ToString();
            if ((typeStr.StartsWith("List<") || typeStr.StartsWith("HashSet<") || typeStr.StartsWith("IEnumerable<")) && (node.Initializer == null || node.Initializer.Expressions.Count >= 0))
            {
                if (node.Initializer == null) return SyntaxFactory.CollectionExpression().WithTriviaFrom(node);

                var elements = node.Initializer.Expressions;
                return SyntaxFactory.CollectionExpression(SyntaxFactory.SeparatedList<CollectionElementSyntax>(
                    elements.Select(e => SyntaxFactory.ExpressionElement(e)))).WithTriviaFrom(node);
            }
            return base.VisitObjectCreationExpression(node);
        }
    }

    private class VerbosityRewriter : CSharpSyntaxRewriter
    {
        public override SyntaxNode? VisitObjectCreationExpression(ObjectCreationExpressionSyntax node)
        {
            if (node.Parent is VariableDeclarationSyntax vds && vds.Type.ToString() == node.Type.ToString())
            {
                return SyntaxFactory.ImplicitObjectCreationExpression(node.ArgumentList ?? SyntaxFactory.ArgumentList(), node.Initializer).WithTriviaFrom(node);
            }
            if (node.Parent is EqualsValueClauseSyntax evc && evc.Parent is VariableDeclaratorSyntax vdr && vdr.Parent is VariableDeclarationSyntax vdsField && vdsField.Type.ToString() == node.Type.ToString())
            {
                return SyntaxFactory.ImplicitObjectCreationExpression(node.ArgumentList ?? SyntaxFactory.ArgumentList(), node.Initializer).WithTriviaFrom(node);
            }
            return base.VisitObjectCreationExpression(node);
        }

        public override SyntaxNode? VisitIfStatement(IfStatementSyntax node)
        {
            if (node.Condition is BinaryExpressionSyntax be && be.IsKind(SyntaxKind.EqualsExpression) && be.Right.IsKind(SyntaxKind.NullLiteralExpression))
            {
                var assignment = node.Statement is ExpressionStatementSyntax es && es.Expression is AssignmentExpressionSyntax asgn ? asgn : null;
                if (assignment != null && assignment.Left.ToString() == be.Left.ToString())
                {
                    var right = assignment.Right;
                    // If the right side is an object creation, try to make it target-typed new()
                    if (right is ObjectCreationExpressionSyntax oce)
                    {
                         right = SyntaxFactory.ImplicitObjectCreationExpression(oce.ArgumentList ?? SyntaxFactory.ArgumentList(), oce.Initializer).WithTriviaFrom(oce);
                    }

                    return SyntaxFactory.ExpressionStatement(
                        SyntaxFactory.AssignmentExpression(SyntaxKind.CoalesceAssignmentExpression, assignment.Left, right)
                    ).WithTriviaFrom(node);
                }
            }
            return base.VisitIfStatement(node);
        }
    }

    public async Task<string> SimplifyCollectionInitializationAsync(string filePath, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault();
        if (document == null) return "";

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        if (root == null) return string.Empty;

        return root.ToFullString();
    }

    public async Task<string> UpgradeThreadSafetyAsync(string filePath, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null) return string.Empty;

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        if (root == null) return string.Empty;

        var rewriter = new ThreadSafetyRewriter();
        var newRoot = rewriter.Visit(root);
        return newRoot.NormalizeWhitespace().ToFullString();
    }

    public async Task<string> UseTimeProviderAsync(string filePath, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null) return string.Empty;

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        if (root == null) return string.Empty;

        var rewriter = new TimeAbstractionRewriter();
        var newRoot = rewriter.Visit(root) as CompilationUnitSyntax;
        if (newRoot == null) return string.Empty;

        // --- Post-Processing: Ensure TimeProvider Field Exists ---
        var classNode = newRoot.DescendantNodes().OfType<ClassDeclarationSyntax>().FirstOrDefault();
        if (classNode != null)
        {
            var hasProvider = classNode.Members.OfType<FieldDeclarationSyntax>()
                .Any(f => f.Declaration.Type.ToString().Contains("TimeProvider"));

            if (!hasProvider)
            {
                var field = SyntaxFactory.FieldDeclaration(
                    SyntaxFactory.VariableDeclaration(SyntaxFactory.ParseTypeName("TimeProvider"))
                        .AddVariables(SyntaxFactory.VariableDeclarator("_timeProvider")))
                    .AddModifiers(SyntaxFactory.Token(SyntaxKind.PrivateKeyword), SyntaxFactory.Token(SyntaxKind.ReadOnlyKeyword));
                
                var newClass = classNode.WithMembers(classNode.Members.Insert(0, field));
                newRoot = newRoot.ReplaceNode(classNode, newClass);
                
                // Add using System; if missing
                if (!newRoot.Usings.Any(u => u.Name.ToString() == "System"))
                {
                    newRoot = newRoot.AddUsings(SyntaxFactory.UsingDirective(SyntaxFactory.ParseName("System")));
                }
            }
        }

        return newRoot.NormalizeWhitespace().ToFullString();
    }

    private class ThreadSafetyRewriter : CSharpSyntaxRewriter
    {
        public override SyntaxNode? VisitLockStatement(LockStatementSyntax node)
        {
            return base.VisitLockStatement(node);
        }
    }

    private class TimeAbstractionRewriter : CSharpSyntaxRewriter
    {
        public override SyntaxNode? VisitMemberAccessExpression(MemberAccessExpressionSyntax node)
        {
            var expr = node.Expression.ToString();
            var isDateTime = expr is "DateTime" or "DateTimeOffset";
            
            if (isDateTime && node.Name.Identifier.Text is "UtcNow" or "Now" or "Today")
            {
                var classNode = node.Ancestors().OfType<ClassDeclarationSyntax>().FirstOrDefault();
                var providerName = "_timeProvider"; // Use the standard name we add if none found

                if (classNode != null)
                {
                    var providerField = classNode.Members.OfType<FieldDeclarationSyntax>()
                        .FirstOrDefault(f => f.Declaration.Type.ToString().Contains("TimeProvider"));
                    if (providerField != null)
                    {
                        providerName = providerField.Declaration.Variables.First().Identifier.Text;
                    }
                }

                var method = node.Name.Identifier.Text == "UtcNow" ? "GetUtcNow()" : "GetLocalNow()";
                return SyntaxFactory.ParseExpression($"{providerName}.{method}").WithTriviaFrom(node);
            }
            return base.VisitMemberAccessExpression(node);
        }
    }
}
