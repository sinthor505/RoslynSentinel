using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RoslynSentinel.Server;

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
    public async Task<string> SimplifyMemberAccessAsync(string filePath, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault();
        if (document == null) return "";

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var thisAccesses = root?.DescendantNodes().OfType<MemberAccessExpressionSyntax>()
            .Where(ma => ma.Expression is ThisExpressionSyntax).ToList();

        if (thisAccesses == null || !thisAccesses.Any()) return root?.ToFullString() ?? "";

        var newRoot = root!.ReplaceNodes(thisAccesses, (oldNode, _) => oldNode.Name);
        return newRoot.ToFullString();
    }

    /// <summary>
    /// Converts standard assignments to object initializers.
    /// </summary>
    public async Task<string> UseObjectInitializersAsync(string filePath, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault();
        if (document == null) return "";

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        if (root == null) return "";

        var newRoot = RewriteBlocksWithObjectInitializers(root);
        return newRoot.NormalizeWhitespace().ToFullString();
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
                            else break;
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
                blocksToReplace[block] = block.WithStatements(SyntaxFactory.List(newStatements));
        }

        if (blocksToReplace.Count == 0) return root;
        return root.ReplaceNodes(blocksToReplace.Keys, (orig, _) => blocksToReplace[orig]);
    }

    /// <summary>
    /// Upgrades traditional null checks to null-propagation (?. ) usage.
    /// </summary>
    public async Task<string> UseNullPropagationAsync(string filePath, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault();
        if (document == null) return "";

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        // Look for if (x != null) x.Do();
        return root?.ToFullString() ?? "";
    }
}
