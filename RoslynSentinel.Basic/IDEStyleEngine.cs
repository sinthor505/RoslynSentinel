using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

using RoslynSentinel.Common;

namespace RoslynSentinel.Basic;

public class IDEStyleEngine
{
    private readonly PersistentWorkspaceManager _workspaceManager;

    public IDEStyleEngine(PersistentWorkspaceManager workspaceManager)
    {
        _workspaceManager = workspaceManager;
    }

    /// <summary>
    /// Simplifies member access by removing unnecessary 'this.' or base qualifiers.
    /// </summary>
    public async Task<DocumentEditResult> SimplifyMemberAccessAsync(FilePath filePath, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault();
        if (document == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.DocumentNotFound,
                FilePath = filePath,
                Message = "// Document not found."
            };
        }

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var thisAccesses = root?.DescendantNodes().OfType<MemberAccessExpressionSyntax>()
            .Where(ma => ma.Expression is ThisExpressionSyntax).ToList();

        if (thisAccesses == null || thisAccesses.Count == 0)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.TargetNotFound,
                FilePath = filePath,
                Message = "// No member accesses to simplify."
            };
        }

        var newRoot = root!.ReplaceNodes(thisAccesses, (oldNode, _) => oldNode.Name);
        return new DocumentEditResult
        {
            Outcome = EditOutcome.Modified,
            FilePath = filePath,
            Message = newRoot.NormalizeWhitespace().ToFullString()
        };
    }

    /// <summary>
    /// Converts standard assignments to object initializers.
    /// </summary>
    public async Task<DocumentEditResult> UseObjectInitializersAsync(FilePath filePath, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault();
        if (document == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.DocumentNotFound,
                FilePath = filePath,
                Message = "// Document not found."
            };
        }

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        if (root == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.SourceInvalid,
                FilePath = filePath,
                Message = "// Source invalid."
            };
        }

        var newRoot = RewriteBlocksWithObjectInitializers(root);
        return new DocumentEditResult
        {
            Outcome = EditOutcome.Modified,
            FilePath = filePath,
            Message = newRoot.NormalizeWhitespace().ToFullString()
        };
    }

    private static SyntaxNode RewriteBlocksWithObjectInitializers(SyntaxNode root)
    {
        var blocksToReplace = new Dictionary<BlockSyntax, BlockSyntax>();

        foreach (var block in root.DescendantNodesAndSelf().OfType<BlockSyntax>())
        {
            var statements = block.Statements.ToList();
            bool changed = false;
            var newStatements = new List<StatementSyntax>();
            int i = 0;

            while (i < statements.Count)
            {
                var stmt = statements[i];

                // Look for: var x = new T(); where T has no initializer
                if (stmt is LocalDeclarationStatementSyntax localDecl &&
                    localDecl.Declaration.Variables.Count == 1)
                {
                    var variable = localDecl.Declaration.Variables[0];
                    var varName = variable.Identifier.Text;

                    if (variable.Initializer?.Value is ObjectCreationExpressionSyntax objCreation &&
                        objCreation.Initializer == null)
                    {
                        // Collect consecutive: x.Prop = value;
                        var assignments = new List<(string PropName, ExpressionSyntax Value)>();
                        int j = i + 1;

                        while (j < statements.Count)
                        {
                            if (statements[j] is ExpressionStatementSyntax exprStmt &&
                                exprStmt.Expression is AssignmentExpressionSyntax assignment &&
                                assignment.Left is MemberAccessExpressionSyntax memberAccess &&
                                memberAccess.Expression is IdentifierNameSyntax id &&
                                id.Identifier.Text == varName)
                            {
                                assignments.Add((memberAccess.Name.Identifier.Text, assignment.Right));
                                j++;
                            }
                            else
                            {
                                break;
                            }
                        }

                        if (assignments.Count >= 1)
                        {
                            var initAssignments = assignments.Select(a =>
                                (ExpressionSyntax)SyntaxFactory.AssignmentExpression(
                                    SyntaxKind.SimpleAssignmentExpression,
                                    SyntaxFactory.IdentifierName(a.PropName),
                                    a.Value));

                            var initializer = SyntaxFactory.InitializerExpression(
                                SyntaxKind.ObjectInitializerExpression,
                                SyntaxFactory.SeparatedList(initAssignments));

                            var newObjCreation = objCreation.WithInitializer(initializer);
                            var newVariable = variable.WithInitializer(
                                variable.Initializer!.WithValue(newObjCreation));
                            var newLocalDecl = localDecl.WithDeclaration(
                                localDecl.Declaration.WithVariables(
                                    SyntaxFactory.SingletonSeparatedList(newVariable)));

                            newStatements.Add(newLocalDecl);
                            i = j;
                            changed = true;
                            continue;
                        }
                    }
                }

                newStatements.Add(stmt);
                i++;
            }

            if (changed)
            {
                blocksToReplace[block] = block.WithStatements(SyntaxFactory.List(newStatements));
            }
        }

        if (blocksToReplace.Count == 0)
        {
            return root;
        }

        return root.ReplaceNodes(blocksToReplace.Keys, (orig, _) => blocksToReplace[orig]);
    }

    /// <summary>
    /// Upgrades traditional null checks to null-propagation (?.) usage.
    /// Converts:  if (x != null) x.Method(args);
    /// To:        x?.Method(args);
    /// Only transforms standalone expression-statement bodies with no else clause.
    /// </summary>
    public async Task<DocumentEditResult> UseNullPropagationAsync(FilePath filePath, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault();
        if (document == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.DocumentNotFound,
                FilePath = filePath,
                Message = "// Document not found."
            };
        }

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        if (root == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.SourceInvalid,
                FilePath = filePath,
                Message = "// Source invalid."
            };
        }

        var rewriter = new NullPropagationRewriter();
        var newRoot = rewriter.Visit(root);
        return new DocumentEditResult
        {
            Outcome = EditOutcome.Modified,
            FilePath = filePath,
            Message = newRoot.NormalizeWhitespace().ToFullString()
        };
    }

    private class NullPropagationRewriter : CSharpSyntaxRewriter
    {
        public override SyntaxNode? VisitIfStatement(IfStatementSyntax node)
        {
            // Only handle: if (x != null) <single-expr-stmt>  with no else
            if (node.Else != null)
            {
                return base.VisitIfStatement(node);
            }

            if (node.Condition is not BinaryExpressionSyntax bin ||
                !bin.IsKind(SyntaxKind.NotEqualsExpression))
            {
                return base.VisitIfStatement(node);
            }

            bool rightIsNull = bin.Right.IsKind(SyntaxKind.NullLiteralExpression);
            bool leftIsNull = bin.Left.IsKind(SyntaxKind.NullLiteralExpression);
            if (!rightIsNull && !leftIsNull)
            {
                return base.VisitIfStatement(node);
            }

            var checkedExpr = rightIsNull ? bin.Left : bin.Right;
            var checkedStr = checkedExpr.ToString();

            // Unwrap single-statement block
            StatementSyntax body = node.Statement;
            if (body is BlockSyntax block && block.Statements.Count == 1)
            {
                body = block.Statements[0];
            }

            if (body is not ExpressionStatementSyntax exprStmt)
            {
                return base.VisitIfStatement(node);
            }

            ExpressionSyntax? nullConditional = null;

            // Pattern: if (x != null) x.Method(args)
            if (exprStmt.Expression is InvocationExpressionSyntax invoc &&
                invoc.Expression is MemberAccessExpressionSyntax ma &&
                ma.Expression.ToString() == checkedStr)
            {
                nullConditional = SyntaxFactory.ConditionalAccessExpression(
                    checkedExpr,
                    SyntaxFactory.InvocationExpression(
                        SyntaxFactory.MemberBindingExpression(ma.Name),
                        invoc.ArgumentList));
            }
            // Pattern: if (x != null) x.Property  (bare member access)
            else if (exprStmt.Expression is MemberAccessExpressionSyntax ma2 &&
                     ma2.Expression.ToString() == checkedStr)
            {
                nullConditional = SyntaxFactory.ConditionalAccessExpression(
                    checkedExpr,
                    SyntaxFactory.MemberBindingExpression(ma2.Name));
            }

            if (nullConditional == null)
            {
                return base.VisitIfStatement(node);
            }

            return SyntaxFactory.ExpressionStatement(nullConditional)
                .WithLeadingTrivia(node.GetLeadingTrivia())
                .WithTrailingTrivia(node.GetTrailingTrivia());
        }
    }
}
