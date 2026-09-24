using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace RoslynSentinel.Advanced;

public class CodeFlowEngine
{
    private readonly ISolutionProvider _workspaceManager;
    private readonly ILogger<CodeFlowEngine> _logger;

    public CodeFlowEngine(ISolutionProvider workspaceManager)
    {
        _workspaceManager = workspaceManager;
        _logger = new NullLogger<CodeFlowEngine>();
    }

    public CodeFlowEngine(ISolutionProvider workspaceManager, ILogger<CodeFlowEngine> logger)
    {
        _workspaceManager = workspaceManager;
        _logger = logger;
    }

    /// <summary>
    /// Reduces block depth by finding if statements that encompass the whole method body and inverting them to return early.
    /// </summary>
    public async Task<DocumentEditResult> ReduceBlockDepthAsync(FilePathWrapper filePath, string methodName, CancellationToken cancellationToken = default)
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
                    Message = $"// ErrorDetails: File '{filePath}' not found."
                };
            }

            var root = await document.GetSyntaxRootAsync(cancellationToken);
            if (root == null)
            {
                return new DocumentEditResult
                {
                    Outcome = EditOutcome.CannotEdit,
                    FilePath = filePath,
                    Message = $"// ErrorDetails: Failed to get syntax root for '{filePath}'."
                };
            }

            var methodNode = root.DescendantNodes().OfType<MethodDeclarationSyntax>().FirstOrDefault(m => m.Identifier.Text == methodName);

            if (methodNode == null || methodNode.Body == null)
            {
                return new DocumentEditResult
                {
                    Outcome = EditOutcome.TargetNotFound,
                    FilePath = filePath,
                    Message = $"// ErrorDetails: Method '{methodName}' not found or has no body."
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
                        UpdatedText = await RoslynFormattingHelper.ReplaceNodeFormattedAsync(document, root, methodNode, newMethodNode, cancellationToken),
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
                Message = $"// ErrorDetails: {ex.Message}"
            };
        }
    }
}
