using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Formatting;

using RoslynSentinel.Common;

namespace RoslynSentinel.Basic;

public class CodeFlowEngine
{
    private readonly ISolutionProvider _workspaceManager;

    public CodeFlowEngine(ISolutionProvider workspaceManager)
    {
        _workspaceManager = workspaceManager;
    }

    /// <summary>
    /// Replaces <paramref name = "oldNode"/> with <paramref name = "newNode"/> and formats only the
    /// replaced node (via a tracking annotation), instead of the whole file. Prevents write-back
    /// paths from silently reformatting unrelated code and shifting line numbers below the edit.
    /// </summary>
    private static async Task<string> ReplaceNodeFormattedAsync(Document document, SyntaxNode root, SyntaxNode oldNode, SyntaxNode newNode, CancellationToken cancellationToken = default)
    {
        var annotation = new SyntaxAnnotation();
        var annotatedNewNode = newNode.WithAdditionalAnnotations(annotation);
        var newRoot = root.ReplaceNode(oldNode, annotatedNewNode);
        var formattedDoc = await Formatter.FormatAsync(document.WithSyntaxRoot(newRoot), annotation, cancellationToken: cancellationToken);
        return (await formattedDoc.GetTextAsync(cancellationToken)).ToString();
    }

    /// <summary>
    /// Reduces block depth by finding if statements that encompass the whole method body and inverting them to return early.
    /// </summary>
    public async Task<DocumentEditResult> ReduceBlockDepthAsync(FilePath filePath, string methodName, CancellationToken cancellationToken = default)
    {
        try
        {
            var solution = await _workspaceManager.GetCurrentSolutionAsync(cancellationToken);
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
                    return new DocumentEditResult
                    {
                        Outcome = EditOutcome.Modified,
                        UpdatedText = await ReplaceNodeFormattedAsync(document, root, methodNode, newMethodNode, cancellationToken),
                        FilePath = filePath
                    };
                }
            }

            return new DocumentEditResult
            {
                Outcome = EditOutcome.NoChange,
                FilePath = filePath,
                Message = "// Info: No optimization could be safely applied.",
                UpdatedText = root.ToFullString()
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
