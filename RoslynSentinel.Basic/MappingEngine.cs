using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RoslynSentinel.Basic;

public class MappingEngine
{
    private readonly PersistentWorkspaceManager _workspaceManager;

    public MappingEngine(PersistentWorkspaceManager workspaceManager)
    {
        _workspaceManager = workspaceManager;
    }

    /// <summary>
    /// Generates a mapping method between two types based on property names.
    /// </summary>
    public async Task<DocumentEditResult> GenerateMappingAsync(FilePath filePath, string fromType, string toType, CancellationToken cancellationToken = default)
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

        var compilation = await document.Project.GetCompilationAsync(cancellationToken);

        // Try fully-qualified name first, then fall back to simple name search
        var fromSymbol = compilation?.GetTypeByMetadataName(fromType)
            ?? compilation?.GetSymbolsWithName(n => n == fromType || n == fromType.Split('.').Last(), SymbolFilter.Type, cancellationToken)
                .OfType<INamedTypeSymbol>().FirstOrDefault();
        var toSymbol = compilation?.GetTypeByMetadataName(toType)
            ?? compilation?.GetSymbolsWithName(n => n == toType || n == toType.Split('.').Last(), SymbolFilter.Type, cancellationToken)
                .OfType<INamedTypeSymbol>().FirstOrDefault();

        if (fromSymbol == null || toSymbol == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.TargetNotFound,
                FilePath = filePath,
                Message = $"// Could not resolve symbols for '{fromType}' or '{toType}'.\n" +
                          $"// Tip: pass the simple class name (e.g. 'CreateRecipeCommand') or the fully-qualified metadata name\n" +
                          $"// (e.g. 'MyApp.Commands.CreateRecipeCommand').  Ensure the types are in the loaded solution."
            };
        }

        var fromProps = fromSymbol.GetMembers().OfType<IPropertySymbol>().ToList();
        var toProps = toSymbol.GetMembers().OfType<IPropertySymbol>().ToList();

        var assignments = new List<StatementSyntax>();
        foreach (var toProp in toProps)
        {
            var match = fromProps.FirstOrDefault(p => p.Name == toProp.Name && p.Type.Equals(toProp.Type, SymbolEqualityComparer.Default));
            if (match != null)
            {
                assignments.Add(SyntaxFactory.ExpressionStatement(
                    SyntaxFactory.AssignmentExpression(
                        SyntaxKind.SimpleAssignmentExpression,
                        SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, SyntaxFactory.IdentifierName("dest"), SyntaxFactory.IdentifierName(toProp.Name)),
                        SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, SyntaxFactory.IdentifierName("source"), SyntaxFactory.IdentifierName(match.Name)))));
            }
        }

        var mappingMethod = SyntaxFactory.MethodDeclaration(SyntaxFactory.ParseTypeName("void"), $"Map{fromSymbol.Name}To{toSymbol.Name}")
            .AddModifiers(SyntaxFactory.Token(SyntaxKind.PublicKeyword), SyntaxFactory.Token(SyntaxKind.StaticKeyword))
            .AddParameterListParameters(
                SyntaxFactory.Parameter(SyntaxFactory.Identifier("source")).WithType(SyntaxFactory.ParseTypeName(fromSymbol.Name)),
                SyntaxFactory.Parameter(SyntaxFactory.Identifier("dest")).WithType(SyntaxFactory.ParseTypeName(toSymbol.Name)))
            .WithBody(SyntaxFactory.Block(assignments));

        return new DocumentEditResult
        {
            Outcome = EditOutcome.Modified,
            FilePath = filePath,
            Message = $"// Mapping method generated for {fromSymbol.Name} to {toSymbol.Name}",
            UpdatedText = mappingMethod.NormalizeWhitespace().ToFullString()
        };
    }

    /// <summary>
    /// Inverts the direction of all assignments in a selected block of code.
    /// </summary>
    public async Task<DocumentEditResult> InvertAssignmentsAsync(FilePath filePath, int startLine, int endLine, CancellationToken cancellationToken = default)
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
        var sourceText = await document.GetTextAsync(cancellationToken);
        var span = Microsoft.CodeAnalysis.Text.TextSpan.FromBounds(sourceText.Lines[startLine - 1].Start, sourceText.Lines[endLine - 1].End);

        var nodes = root?.DescendantNodes(span).OfType<AssignmentExpressionSyntax>().ToList();
        if (nodes == null || nodes.Count == 0)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.TargetNotFound,
                FilePath = filePath,
                Message = $"// No assignment expressions found in the specified range: {startLine}-{endLine}"
            };
        }

        var newRoot = root!.ReplaceNodes(nodes, (oldNode, newNode) =>
            newNode.WithLeft(oldNode.Right).WithRight(oldNode.Left));

        return new DocumentEditResult
        {
            Outcome = EditOutcome.Modified,
            FilePath = filePath,
            Message = $"// Assignment expressions inverted in the specified range: {startLine}-{endLine}",
            UpdatedText = newRoot.NormalizeWhitespace().ToFullString()
        };
    }
}
