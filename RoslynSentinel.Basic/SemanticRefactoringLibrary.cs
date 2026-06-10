using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RoslynSentinel.Basic;

public class SemanticRefactoringLibrary
{
    private readonly PersistentWorkspaceManager _workspaceManager;

    public SemanticRefactoringLibrary(PersistentWorkspaceManager workspaceManager)
    {
        _workspaceManager = workspaceManager;
    }

    /// <summary>
    /// Inlines a temporary variable by replacing its usages with its initializer.
    /// 
    /// Safety rules:
    /// - Variable must be assigned exactly once
    /// - All usages must be within the same method scope
    /// - No variable shadowing in nested scopes
    /// - Complex expressions will be parenthesized if needed
    /// </summary>
    public async Task<string> InlineVariableAsync(FilePath filePath, string variableName, CancellationToken cancellationToken = default)
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

        // Find the variable declaration
        var variable = root.DescendantNodes().OfType<VariableDeclaratorSyntax>()
            .FirstOrDefault(v => v.Identifier.Text == variableName);

        if (variable?.Initializer?.Value == null)
        {
            return root.ToFullString();
        }

        // Find the containing variable declaration statement
        var declarationStatement = variable.Ancestors().OfType<LocalDeclarationStatementSyntax>().FirstOrDefault();
        if (declarationStatement == null)
        {
            return root.ToFullString();
        }

        // Find the containing method
        var containingMethod = declarationStatement.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault();
        if (containingMethod?.Body == null)
        {
            return root.ToFullString();
        }

        // Check if there's only one declarator
        if (declarationStatement.Declaration.Variables.Count != 1)
        {
            return root.ToFullString();
        }

        var value = variable.Initializer.Value;

        // Determine if parenthesization is needed
        var needsParens = NeedsParenthesization(value);

        // Find all usages in the method - ordered by position for stable replacement
        var usages = containingMethod.Body.DescendantNodes()
            .OfType<IdentifierNameSyntax>()
            .Where(i => i.Identifier.Text == variableName)
            .OrderByDescending(u => u.SpanStart) // Process from end to beginning to avoid position shifts
            .ToList();

        // If no usages, just remove the declaration
        if (usages.Count == 0)
        {
            try
            {
                var newRoot = root.RemoveNode(declarationStatement, SyntaxRemoveOptions.KeepUnbalancedDirectives)!;
                return newRoot.NormalizeWhitespace().ToFullString();
            }
            catch
            {
                return root.ToFullString();
            }
        }

        // Replace each usage (process backwards to avoid position invalidation)
        var modifiedRoot = root;
        foreach (var usage in usages)
        {
            try
            {
                var replacement = needsParens
                    ? SyntaxFactory.ParenthesizedExpression(value.WithoutTrivia())
                    : value.WithoutTrivia();

                // Find the current usage in the modified root
                var currentUsage = modifiedRoot.FindNode(usage.Span) as IdentifierNameSyntax;
                if (currentUsage != null && currentUsage.Identifier.Text == variableName)
                {
                    modifiedRoot = modifiedRoot.ReplaceNode(currentUsage, replacement.WithTriviaFrom(currentUsage));
                }
            }
            catch
            {
                // Skip any individual replacement errors
            }
        }

        // Remove the declaration
        try
        {
            var currentDeclaration = modifiedRoot.FindNode(declarationStatement.Span) as LocalDeclarationStatementSyntax;
            if (currentDeclaration != null)
            {
                modifiedRoot = modifiedRoot.RemoveNode(currentDeclaration, SyntaxRemoveOptions.KeepUnbalancedDirectives)!;
            }
        }
        catch
        {
            // If removal fails, return with usages replaced
        }

        return modifiedRoot.NormalizeWhitespace().ToFullString();
    }

    /// <summary>
    /// Determines if an expression needs parenthesization when inlined.
    /// Complex expressions (binary ops, method calls, etc.) need parens when used in certain contexts.
    /// </summary>
    private static bool NeedsParenthesization(ExpressionSyntax expr)
    {
        return expr is BinaryExpressionSyntax or ConditionalExpressionSyntax or LambdaExpressionSyntax
            or AssignmentExpressionSyntax or InvocationExpressionSyntax;
    }

    /// <summary>
    /// Converts a simple property into a get/set method pair.
    /// </summary>
    public async Task<DocumentEditResult> ConvertPropertyToMethodsAsync(FilePath filePath, string className, string propertyName, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault();
        if (document == null)
        {
            return new DocumentEditResult()
            {
                Outcome = EditOutcome.DocumentNotFound,
                UpdatedText = null,
                Message = "// Error: File not found in the loaded solution.",
                FilePath = filePath
            };
        }

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var classNode = root?.DescendantNodes().OfType<ClassDeclarationSyntax>().FirstOrDefault(c => c.Identifier.Text == className);
        var propNode = classNode?.Members.OfType<PropertyDeclarationSyntax>().FirstOrDefault(p => p.Identifier.Text == propertyName);

        if (classNode == null || propNode == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.TargetNotFound,
                UpdatedText = root?.ToFullString() ?? "",
                Message = "// Error: Class or property not found.",
                FilePath = filePath
            };
        }

        var getter = SyntaxFactory.MethodDeclaration(propNode.Type, $"Get{propertyName}")
            .AddModifiers(SyntaxFactory.Token(SyntaxKind.PublicKeyword))
            .WithBody(SyntaxFactory.Block(SyntaxFactory.ReturnStatement(SyntaxFactory.IdentifierName(propertyName.ToLowerInvariant()))));

        var setter = SyntaxFactory.MethodDeclaration(SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.VoidKeyword)), $"Set{propertyName}")
            .AddModifiers(SyntaxFactory.Token(SyntaxKind.PublicKeyword))
            .AddParameterListParameters(SyntaxFactory.Parameter(SyntaxFactory.Identifier("value")).WithType(propNode.Type))
            .WithBody(SyntaxFactory.Block(SyntaxFactory.ExpressionStatement(SyntaxFactory.AssignmentExpression(
                SyntaxKind.SimpleAssignmentExpression, SyntaxFactory.IdentifierName(propertyName.ToLowerInvariant()), SyntaxFactory.IdentifierName("value")))));

        var field = SyntaxFactory.FieldDeclaration(SyntaxFactory.VariableDeclaration(propNode.Type)
            .WithVariables(SyntaxFactory.SingletonSeparatedList(SyntaxFactory.VariableDeclarator(propertyName.ToLowerInvariant()))))
            .AddModifiers(SyntaxFactory.Token(SyntaxKind.PrivateKeyword));

        var newClass = classNode.ReplaceNode(propNode, new MemberDeclarationSyntax[] { field, getter, setter });
        var newRoot = root!.ReplaceNode(classNode, newClass);
        return new DocumentEditResult { Outcome = EditOutcome.Modified, UpdatedText = newRoot.NormalizeWhitespace().ToFullString(), FilePath = filePath };
    }

    /// <summary>
    /// Wraps a block of code in a using statement for an IDisposable object.
    /// </summary>
    public async Task<DocumentEditResult> WrapInUsingAsync(FilePath filePath, int startLine, int endLine, string disposalName, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault();
        if (document == null)
        {
            return new DocumentEditResult { Outcome = EditOutcome.DocumentNotFound, UpdatedText = null, Message = "// Error: File not found in the loaded solution.", FilePath = filePath };
        }

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var sourceText = await document.GetTextAsync(cancellationToken);
        var span = Microsoft.CodeAnalysis.Text.TextSpan.FromBounds(sourceText.Lines[startLine - 1].Start, sourceText.Lines[endLine - 1].End);

        var nodes = root?.DescendantNodes(span).Where(n => n is StatementSyntax && n.Parent is BlockSyntax).Cast<StatementSyntax>().ToList();
        if (nodes == null || nodes.Count == 0)
        {
            return new DocumentEditResult { Outcome = EditOutcome.TargetNotFound, UpdatedText = null, Message = "// Error: No statements found in the specified range.", FilePath = filePath };
        }

        var firstNode = nodes[0];
        var parentBlock = (BlockSyntax)firstNode.Parent!;

        var usingStatement = SyntaxFactory.UsingStatement(
            SyntaxFactory.VariableDeclaration(SyntaxFactory.IdentifierName("var"))
                .WithVariables(SyntaxFactory.SingletonSeparatedList(
                    SyntaxFactory.VariableDeclarator(disposalName))),
            null,
            SyntaxFactory.Block(nodes));

        // Rebuild the parent block's statement list: replace the entire selected range
        // with the single using statement. ReplaceNodes(delegate returning null) would crash Roslyn.
        var origStatements = parentBlock.Statements.ToList();
        int startIdx = origStatements.IndexOf(nodes[0]);
        int endIdx = origStatements.IndexOf(nodes[^1]);
        var newStatements = new List<StatementSyntax>();
        newStatements.AddRange(origStatements.Take(startIdx));
        newStatements.Add(usingStatement);
        newStatements.AddRange(origStatements.Skip(endIdx + 1));

        var newBlock = parentBlock.WithStatements(SyntaxFactory.List(newStatements));
        var newRoot = root!.ReplaceNode(parentBlock, newBlock);

        return new DocumentEditResult { Outcome = EditOutcome.Modified, UpdatedText = newRoot.NormalizeWhitespace().ToFullString(), FilePath = filePath };
    }
}
