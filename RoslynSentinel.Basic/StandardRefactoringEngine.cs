using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RoslynSentinel.Basic;

public class StandardRefactoringEngine
{
    private readonly PersistentWorkspaceManager _workspaceManager;

    public StandardRefactoringEngine(PersistentWorkspaceManager workspaceManager)
    {
        _workspaceManager = workspaceManager;
    }

    /// <summary>
    /// Converts a method with no parameters to a property.
    /// </summary>
    public async Task<DocumentEditResult> ConvertMethodToPropertyAsync(FilePath filePath, string methodName, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault();
        if (document == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.CannotEdit,
                FilePath = filePath,
                Message = "// Document not found."
            };
        }

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var methodNode = root?.DescendantNodes().OfType<MethodDeclarationSyntax>().FirstOrDefault(m => m.Identifier.Text == methodName);

        if (methodNode != null && !methodNode.ParameterList.Parameters.Any())
        {
            ArrowExpressionClauseSyntax? arrow = null;
            if (methodNode.ExpressionBody != null)
            {
                arrow = methodNode.ExpressionBody;
            }
            else if (methodNode.Body?.Statements.Count == 1 && methodNode.Body.Statements[0] is ReturnStatementSyntax ret)
            {
                arrow = SyntaxFactory.ArrowExpressionClause(ret.Expression!);
            }

            if (arrow != null)
            {
                var propertyNode = SyntaxFactory.PropertyDeclaration(methodNode.ReturnType, methodNode.Identifier)
                    .WithModifiers(methodNode.Modifiers)
                    .WithExpressionBody(arrow)
                    .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken));

                var newRoot = root!.ReplaceNode(methodNode, propertyNode);
                return new DocumentEditResult
                {
                    Outcome = EditOutcome.Modified,
                    FilePath = filePath,
                    UpdatedText = newRoot.NormalizeWhitespace().ToFullString()
                };
            }
        }
        return new DocumentEditResult
        {
            Outcome = EditOutcome.CannotEdit,
            FilePath = filePath,
            Message = "// Could not convert method to property."
        };
    }

    /// <summary>
    /// Makes a method static if it doesn't access any instance members.
    /// </summary>
    public async Task<DocumentEditResult> MakeMethodStaticAsync(FilePath filePath, string methodName, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault();
        if (document == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.CannotEdit,
                FilePath = filePath,
                Message = "// Document not found."
            };
        }

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
        var methodNode = root?.DescendantNodes().OfType<MethodDeclarationSyntax>().FirstOrDefault(m => m.Identifier.Text == methodName);

        if (methodNode == null || methodNode.Modifiers.Any(m => m.IsKind(SyntaxKind.StaticKeyword)))
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.CannotEdit,
                FilePath = filePath,
                Message = "// Method not found or already static."
            };
        }

        // Check for instance access
        var hasInstanceAccess = methodNode.DescendantNodes().Any(node =>
        {
            if (node is ThisExpressionSyntax || node is BaseExpressionSyntax)
            {
                return true;
            }

            var symbol = semanticModel?.GetSymbolInfo(node, cancellationToken).Symbol;
            return symbol != null && !symbol.IsStatic && (symbol.Kind == SymbolKind.Field || symbol.Kind == SymbolKind.Property || symbol.Kind == SymbolKind.Method);
        });

        if (!hasInstanceAccess)
        {
            var newMethodNode = methodNode.AddModifiers(SyntaxFactory.Token(SyntaxKind.StaticKeyword));
            var newRoot = root!.ReplaceNode(methodNode, newMethodNode);
            return new DocumentEditResult
            {
                Outcome = EditOutcome.Modified,
                FilePath = filePath,
                UpdatedText = newRoot.NormalizeWhitespace().ToFullString()
            };
        }

        return new DocumentEditResult
        {
            Outcome = EditOutcome.CannotEdit,
            FilePath = filePath,
            Message = "// Method accesses instance members and cannot be made static."
        };
    }

    /// <summary>
    /// Inverts a boolean variable or parameter name and its usages.
    /// </summary>
    public async Task<DocumentEditResult> InvertBooleanAsync(FilePath filePath, string boolName, CancellationToken cancellationToken = default)
    {
        // Requires solution-wide reference tracking, logic implemented in AdvancedLogicEngine.
        return new DocumentEditResult
        {
            Outcome = EditOutcome.CannotEdit,
            FilePath = filePath,
            Message = "// InvertBooleanAsync is not implemented."
        };
    }
}
