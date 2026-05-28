using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RoslynSentinel.Server;

public class ModernizationUpgradeEngine
{
    private readonly PersistentWorkspaceManager _workspaceManager;

    public ModernizationUpgradeEngine(PersistentWorkspaceManager workspaceManager)
    {
        _workspaceManager = workspaceManager;
    }

    /// <summary>
    /// Upgrades legacy string parsing to use Span&lt;char&gt; for zero-allocation performance.
    /// Converts: str.Substring(start, length) → str.AsSpan(start, length).ToString()
    /// and:      str.Substring(start)         → str.AsSpan(start).ToString()
    /// Scoped to the named method when methodName is provided; otherwise transforms entire file.
    /// </summary>
    public async Task<string> UseSpanForParsingAsync(string filePath, string methodName, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault();
        if (document == null)
        {
            return "";
        }

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        if (root == null)
        {
            return "";
        }

        SyntaxNode scope = root;
        if (!string.IsNullOrWhiteSpace(methodName))
        {
            var method = root.DescendantNodes()
                .OfType<MethodDeclarationSyntax>()
                .FirstOrDefault(m => m.Identifier.Text == methodName);
            if (method == null)
            {
                return root.ToFullString();
            }

            scope = method;
        }

        var rewriter = new SpanParsingRewriter();
        var newScope = rewriter.Visit(scope);
        var newRoot = string.IsNullOrWhiteSpace(methodName)
            ? newScope
            : root.ReplaceNode(scope, newScope);

        return newRoot.NormalizeWhitespace().ToFullString();
    }

    private class SpanParsingRewriter : CSharpSyntaxRewriter
    {
        public override SyntaxNode? VisitInvocationExpression(InvocationExpressionSyntax node)
        {
            if (node.Expression is MemberAccessExpressionSyntax memberAccess &&
                memberAccess.Name.Identifier.Text == "Substring" &&
                node.ArgumentList.Arguments.Count is 1 or 2)
            {
                // str.AsSpan(args...)
                var asSpanAccess = SyntaxFactory.MemberAccessExpression(
                    SyntaxKind.SimpleMemberAccessExpression,
                    memberAccess.Expression,
                    SyntaxFactory.IdentifierName("AsSpan"));
                var asSpanCall = SyntaxFactory.InvocationExpression(asSpanAccess, node.ArgumentList);

                // .ToString()
                var toStringAccess = SyntaxFactory.MemberAccessExpression(
                    SyntaxKind.SimpleMemberAccessExpression,
                    asSpanCall,
                    SyntaxFactory.IdentifierName("ToString"));
                var result = SyntaxFactory.InvocationExpression(
                    toStringAccess,
                    SyntaxFactory.ArgumentList())
                    .WithLeadingTrivia(node.GetLeadingTrivia())
                    .WithTrailingTrivia(node.GetTrailingTrivia());
                return result;
            }
            return base.VisitInvocationExpression(node);
        }
    }

    /// <summary>
    /// Upgrades code to use modern pattern matching (is Type t) instead of casts.
    /// </summary>
    public async Task<string> UpgradePatternMatchingAsync(string filePath, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault();
        if (document == null)
        {
            return "";
        }

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        if (root == null)
        {
            return string.Empty;
        }

        var rewriter = new PatternMatchingRewriter();
        var newRoot = rewriter.Visit(root);
        
        return newRoot.NormalizeWhitespace().ToFullString();
    }

    /// <summary>
    /// Converts adjacent null-assign + null-guard patterns to null-coalescing throw expressions (C# 7+).
    /// Before:  var x = GetValue();
    ///          if (x == null) throw new ArgumentNullException(nameof(x));
    /// After:   var x = GetValue() ?? throw new ArgumentNullException(nameof(x));
    /// Only transforms cases where the assignment and null check are consecutive statements in the same block.
    /// </summary>
    public async Task<string> UseThrowExpressionsAsync(string filePath, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault();
        if (document == null)
        {
            return "";
        }

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        if (root == null)
        {
            return string.Empty;
        }

        var rewriter = new ThrowExpressionRewriter();
        var newRoot = rewriter.Visit(root);

        return newRoot.NormalizeWhitespace().ToFullString();
    }

    private class ThrowExpressionRewriter : CSharpSyntaxRewriter
    {
        public override SyntaxNode? VisitBlock(BlockSyntax node)
        {
            var statements = node.Statements.ToList();
            var replacements = new Dictionary<int, (bool skipNext, StatementSyntax newStmt)>();

            for (int i = 0; i < statements.Count - 1; i++)
            {
                // Look for: var x = someExpr;  followed by  if (x == null) throw ...;
                if (statements[i] is not LocalDeclarationStatementSyntax localDecl)
                {
                    continue;
                }

                if (localDecl.Declaration.Variables.Count != 1)
                {
                    continue;
                }

                var variable = localDecl.Declaration.Variables[0];
                var varName = variable.Identifier.Text;
                var initValue = variable.Initializer?.Value;
                if (initValue == null)
                {
                    continue;
                }

                if (statements[i + 1] is not IfStatementSyntax ifStmt)
                {
                    continue;
                }

                if (ifStmt.Else != null)
                {
                    continue;
                }

                if (ifStmt.Condition is not BinaryExpressionSyntax condBin)
                {
                    continue;
                }

                if (!condBin.IsKind(SyntaxKind.EqualsExpression))
                {
                    continue;
                }

                bool rightIsNull = condBin.Right.IsKind(SyntaxKind.NullLiteralExpression);
                bool leftIsNull  = condBin.Left.IsKind(SyntaxKind.NullLiteralExpression);
                if (!rightIsNull && !leftIsNull)
                {
                    continue;
                }

                var condVar = rightIsNull ? condBin.Left.ToString() : condBin.Right.ToString();
                if (condVar != varName)
                {
                    continue;
                }

                // Get throw statement from if body
                ThrowStatementSyntax? throwStmt = ifStmt.Statement as ThrowStatementSyntax;
                if (throwStmt == null && ifStmt.Statement is BlockSyntax blk && blk.Statements.Count == 1)
                {
                    throwStmt = blk.Statements[0] as ThrowStatementSyntax;
                }

                if (throwStmt?.Expression == null)
                {
                    continue;
                }

                // Build: var x = initValue ?? throw new ...;
                var throwExpr = SyntaxFactory.ThrowExpression(
                    SyntaxFactory.Token(SyntaxKind.ThrowKeyword)
                        .WithTrailingTrivia(SyntaxFactory.Space),
                    throwStmt.Expression);

                var coalescedInit = SyntaxFactory.BinaryExpression(
                    SyntaxKind.CoalesceExpression,
                    initValue,
                    throwExpr);

                var newVarDecl = variable.WithInitializer(
                    variable.Initializer!.WithValue(coalescedInit));
                var newDecl = localDecl.WithDeclaration(
                    localDecl.Declaration.WithVariables(
                        SyntaxFactory.SingletonSeparatedList(newVarDecl)));

                replacements[i] = (skipNext: true, newStmt: newDecl);
            }

            if (replacements.Count == 0)
            {
                return base.VisitBlock(node);
            }

            var newStatements = new List<StatementSyntax>();
            bool skipThisLine = false;
            for (int i = 0; i < statements.Count; i++)
            {
                if (skipThisLine) { skipThisLine = false; continue; }
                if (replacements.TryGetValue(i, out var rep))
                {
                    newStatements.Add(rep.newStmt);
                    skipThisLine = rep.skipNext;
                }
                else
                {
                    newStatements.Add(statements[i]);
                }
            }

            var updated = node.WithStatements(SyntaxFactory.List(newStatements));
            return base.VisitBlock(updated);
        }
    }

    private class PatternMatchingRewriter : CSharpSyntaxRewriter
    {
        public override SyntaxNode? VisitIfStatement(IfStatementSyntax node)
        {
            // Look for: if (x is T) { var t = (T)x; ... }
            if (node.Condition is BinaryExpressionSyntax bin && bin.IsKind(SyntaxKind.IsExpression))
            {
                var block = node.Statement as BlockSyntax;
                var firstStatement = block?.Statements.FirstOrDefault() as LocalDeclarationStatementSyntax;
                
                if (firstStatement?.Declaration.Variables.Count == 1)
                {
                    var variable = firstStatement.Declaration.Variables[0];
                    if (variable.Initializer?.Value is CastExpressionSyntax cast && cast.Type.ToString() == bin.Right.ToString())
                    {
                        var newCondition = SyntaxFactory.IsPatternExpression(bin.Left, 
                            SyntaxFactory.DeclarationPattern(cast.Type, SyntaxFactory.SingleVariableDesignation(variable.Identifier)));
                        
                        var newBlock = block!.WithStatements(block.Statements.RemoveAt(0));
                        return node.WithCondition(newCondition).WithStatement(newBlock);
                    }
                }
            }
            return base.VisitIfStatement(node);
        }
    }
}
