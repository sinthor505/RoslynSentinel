using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RoslynSentinel.Server;

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
    public async Task<string> GenerateMappingAsync(string filePath, string fromType, string toType, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault();
        if (document == null) throw new Exception("File not found.");

        var compilation = await document.Project.GetCompilationAsync(cancellationToken);
        var fromSymbol = compilation?.GetTypeByMetadataName(fromType);
        var toSymbol = compilation?.GetTypeByMetadataName(toType);

        if (fromSymbol == null || toSymbol == null)
            throw new Exception($"Could not resolve symbols for {fromType} or {toType}. Ensure they are in the solution.");

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

        return mappingMethod.NormalizeWhitespace().ToFullString();
    }

    /// <summary>
    /// Inverts the direction of all assignments in a selected block of code.
    /// </summary>
    public async Task<string> InvertAssignmentsAsync(string filePath, int startLine, int endLine, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault();
        if (document == null) return "";

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var sourceText = await document.GetTextAsync(cancellationToken);
        var span = Microsoft.CodeAnalysis.Text.TextSpan.FromBounds(sourceText.Lines[startLine - 1].Start, sourceText.Lines[endLine - 1].End);
        
        var nodes = root?.DescendantNodes(span).OfType<AssignmentExpressionSyntax>().ToList();
        if (nodes == null || !nodes.Any()) return root?.ToFullString() ?? "";

        var newRoot = root!.ReplaceNodes(nodes, (oldNode, newNode) => 
            newNode.WithLeft(oldNode.Right).WithRight(oldNode.Left));

        return newRoot.NormalizeWhitespace().ToFullString();
    }
}
