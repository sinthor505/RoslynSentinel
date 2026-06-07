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
    public async Task<DocumentEditResult> ReduceBlockDepthAsync(FilePath filePath, string methodName, CancellationToken cancellationToken = default)
    {
        try
        {
            var solution = await _workspaceManager.GetBranchedSolutionAsync();
            var document = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault();
            if (document == null)
            {
                return new DocumentEditResult
                {
                    Outcome = EditOutcome.DocumentNotFound,
                    FilePath = filePath,
                    Message = $"// Error: File '{filePath}' not found."
                };
            }

            var root = await document.GetSyntaxRootAsync(cancellationToken);
            if (root == null)
            {
                return new DocumentEditResult
                {
                    Outcome = EditOutcome.CannotEdit,
                    FilePath = filePath,
                    Message = $"// Error: Failed to get syntax root for '{filePath}'."
                };
            }

            var methodNode = root.DescendantNodes().OfType<MethodDeclarationSyntax>().FirstOrDefault(m => m.Identifier.Text == methodName);

            if (methodNode == null || methodNode.Body == null)
            {
                return new DocumentEditResult
                {
                    Outcome = EditOutcome.TargetNotFound,
                    FilePath = filePath,
                    Message = $"// Error: Method '{methodName}' not found or has no body."
                };
            }

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
                    var newRoot = root.ReplaceNode(methodNode, newMethodNode);
                    return new DocumentEditResult
                    {
                        Outcome = EditOutcome.Modified,
                        UpdatedText = newRoot.NormalizeWhitespace().ToFullString(),
                        FilePath = filePath
                    };
                }
            }

            return new DocumentEditResult
            {
                Outcome = EditOutcome.NoChange,
                FilePath = filePath,
                Message = "// Info: No optimization could be safely applied."
            };
        }
        catch (Exception ex)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.CannotEdit,
                FilePath = filePath,
                Message = $"// Error: {ex.Message}"
            };
        }
    }
}
