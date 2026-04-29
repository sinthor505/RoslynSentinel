using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RoslynSentinel.Server;

public class LogicOptimizationEngine
{
    private readonly PersistentWorkspaceManager _workspaceManager;

    public LogicOptimizationEngine(PersistentWorkspaceManager workspaceManager)
    {
        _workspaceManager = workspaceManager;
    }

    /// <summary>
    /// Simplifies redundant logic like 'if (x == true)' to 'if (x)'.
    /// </summary>
    public async Task<string> SimplifyBooleanExpressionsAsync(string filePath, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault();
        if (document == null) return "";

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        if (root == null) return string.Empty;

        var rewriter = new BooleanSimplifierRewriter();
        var newRoot = rewriter.Visit(root);
        return newRoot.NormalizeWhitespace().ToFullString();
    }

    /// <summary>
    /// Adds ArgumentNullException.ThrowIfNull checks to all reference type parameters in a method.
    /// </summary>
    public async Task<string> AddGuardClausesAsync(string filePath, string methodName, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault();
        if (document == null) return "";

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
        var method = root?.DescendantNodes().OfType<MethodDeclarationSyntax>().FirstOrDefault(m => m.Identifier.Text == methodName);

        if (method == null || method.Body == null || semanticModel == null) return root?.ToFullString() ?? "";

        var guards = new List<StatementSyntax>();
        foreach (var parameter in method.ParameterList.Parameters)
        {
            var symbol = semanticModel.GetDeclaredSymbol(parameter, cancellationToken);
            if (symbol != null && symbol.Type.IsReferenceType)
            {
                var guard = SyntaxFactory.ExpressionStatement(
                    SyntaxFactory.InvocationExpression(
                        SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, SyntaxFactory.IdentifierName("ArgumentNullException"), SyntaxFactory.IdentifierName("ThrowIfNull")),
                        SyntaxFactory.ArgumentList(SyntaxFactory.SingletonSeparatedList(SyntaxFactory.Argument(SyntaxFactory.IdentifierName(parameter.Identifier))))));
                guards.Add(guard);
            }
        }

        if (guards.Any())
        {
            var newBody = method.Body.WithStatements(method.Body.Statements.InsertRange(0, guards));
            var newRoot = root!.ReplaceNode(method, method.WithBody(newBody));
            return newRoot.NormalizeWhitespace().ToFullString();
        }

        return root!.ToFullString();
    }

    private class BooleanSimplifierRewriter : CSharpSyntaxRewriter
    {
        public override SyntaxNode? VisitBinaryExpression(BinaryExpressionSyntax node)
        {
            if (node.IsKind(SyntaxKind.EqualsExpression) || node.IsKind(SyntaxKind.NotEqualsExpression))
            {
                var isTrue = node.Right.IsKind(SyntaxKind.TrueLiteralExpression) || node.Left.IsKind(SyntaxKind.TrueLiteralExpression);
                var isFalse = node.Right.IsKind(SyntaxKind.FalseLiteralExpression) || node.Left.IsKind(SyntaxKind.FalseLiteralExpression);

                if (isTrue)
                {
                    var expr = node.Right.IsKind(SyntaxKind.TrueLiteralExpression) ? node.Left : node.Right;
                    if (node.IsKind(SyntaxKind.EqualsExpression)) return expr;
                    return SyntaxFactory.PrefixUnaryExpression(SyntaxKind.LogicalNotExpression, SyntaxFactory.ParenthesizedExpression(expr));
                }
                
                if (isFalse)
                {
                    var expr = node.Right.IsKind(SyntaxKind.FalseLiteralExpression) ? node.Left : node.Right;
                    if (node.IsKind(SyntaxKind.EqualsExpression)) return SyntaxFactory.PrefixUnaryExpression(SyntaxKind.LogicalNotExpression, SyntaxFactory.ParenthesizedExpression(expr));
                    return expr;
                }
            }
            return base.VisitBinaryExpression(node);
        }
    }
}
