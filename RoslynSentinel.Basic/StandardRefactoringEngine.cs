using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Formatting;

namespace RoslynSentinel.Basic;

public class StandardRefactoringEngine
{
    private readonly ISolutionProvider _workspaceManager;

    public StandardRefactoringEngine(ISolutionProvider workspaceManager)
    {
        _workspaceManager = workspaceManager;
    }

    /// <summary>
    /// Replaces <paramref name="oldNode"/> with <paramref name="newNode"/> and formats only the
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
    /// Converts a method with no parameters to a property.
    /// </summary>
    public async Task<DocumentEditResult> ConvertMethodToPropertyAsync(FilePath filePath, string methodName, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetCurrentSolutionAsync(cancellationToken);
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

                return new DocumentEditResult
                {
                    Outcome = EditOutcome.Modified,
                    FilePath = filePath,
                    UpdatedText = await ReplaceNodeFormattedAsync(document, root!, methodNode, propertyNode, cancellationToken)
                };
            }
        }

        if (methodNode != null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.CannotConvert,
                FilePath = filePath,
                Message = "// Could not convert method to property: methods with parameters cannot become properties.",
                UpdatedText = root!.ToFullString()
            };
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
        var solution = await _workspaceManager.GetCurrentSolutionAsync(cancellationToken);
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
            return new DocumentEditResult
            {
                Outcome = EditOutcome.Modified,
                FilePath = filePath,
                UpdatedText = await ReplaceNodeFormattedAsync(document, root!, methodNode, newMethodNode, cancellationToken)
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
        _ = cancellationToken;

        // Requires solution-wide reference tracking, logic implemented in AdvancedLogicEngine.
        return new DocumentEditResult
        {
            Outcome = EditOutcome.CannotEdit,
            FilePath = filePath,
            Message = "// InvertBooleanAsync is not implemented."
        };
    }
}
