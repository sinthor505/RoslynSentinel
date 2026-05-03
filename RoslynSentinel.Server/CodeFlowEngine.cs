using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RoslynSentinel.Server;

public class CodeFlowEngine
{
    private readonly PersistentWorkspaceManager _workspaceManager;

    public CodeFlowEngine(PersistentWorkspaceManager workspaceManager)
    {
        _workspaceManager = workspaceManager;
    }

    /// <summary>
    /// Reduces block depth by finding if statements that encompass the whole method body and inverting them to return early.
    /// </summary>
    public async Task<string> ReduceBlockDepthAsync(string filePath, string methodName, CancellationToken cancellationToken = default)
    {
        try
        {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault();
        if (document == null) return $"// Error: File '{filePath}' not found.";

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var methodNode = root?.DescendantNodes().OfType<MethodDeclarationSyntax>().FirstOrDefault(m => m.Identifier.Text == methodName);
        
        if (methodNode == null || methodNode.Body == null) return $"// Error: Method '{methodName}' not found or has no body.";

        // Look for: 
        // void Method() { 
        //     if (condition) { 
        //         /* logic */ 
        //     } 
        // }
        // To convert to:
        // void Method() {
        //     if (!condition) return;
        //     /* logic */
        // }

        if (methodNode.Body.Statements.Count == 1 && methodNode.Body.Statements[0] is IfStatementSyntax ifStmt)
        {
            if (ifStmt.Else == null) // Must not have an else
            {
                var invertedCondition = SyntaxFactory.PrefixUnaryExpression(SyntaxKind.LogicalNotExpression, SyntaxFactory.ParenthesizedExpression(ifStmt.Condition));
                var earlyReturn = SyntaxFactory.IfStatement(
                    invertedCondition,
                    SyntaxFactory.ReturnStatement()
                );

                var newStatements = new List<StatementSyntax> { earlyReturn };
                
                if (ifStmt.Statement is BlockSyntax block)
                {
                    newStatements.AddRange(block.Statements);
                }
                else
                {
                    newStatements.Add(ifStmt.Statement);
                }

                var newBody = SyntaxFactory.Block(newStatements);
                var newMethodNode = methodNode.WithBody(newBody);
                var newRoot = root!.ReplaceNode(methodNode, newMethodNode);
                return newRoot.NormalizeWhitespace().ToFullString();
            }
        }

        return root!.ToFullString(); // No optimization could be safely applied
        }
        catch (Exception ex)
        {
            return $"// Error: {ex.Message}";
        }
    }
}
