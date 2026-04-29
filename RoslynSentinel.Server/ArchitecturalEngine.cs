using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RoslynSentinel.Server;

public class ArchitecturalEngine
{
    private readonly PersistentWorkspaceManager _workspaceManager;

    public ArchitecturalEngine(PersistentWorkspaceManager workspaceManager)
    {
        _workspaceManager = workspaceManager;
    }

    /// <summary>
    /// Converts a class into a .NET BackgroundService.
    /// </summary>
    public async Task<string> ConvertToBackgroundServiceAsync(string filePath, string className, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault();
        if (document == null) throw new Exception("File not found.");

        var root = await document.GetSyntaxRootAsync(cancellationToken) as CompilationUnitSyntax;
        if (root == null) throw new Exception("Could not parse syntax root.");

        var classNode = root.DescendantNodes().OfType<ClassDeclarationSyntax>().FirstOrDefault(c => c.Identifier.Text == className);
        if (classNode == null) throw new Exception("Class not found.");

        // 1. Add using
        if (!root.Usings.Any(u => u.Name.ToString() == "Microsoft.Extensions.Hosting"))
        {
            root = root.AddUsings(SyntaxFactory.UsingDirective(SyntaxFactory.ParseName("Microsoft.Extensions.Hosting")));
        }

        // 2. Change base class
        var baseType = SyntaxFactory.SimpleBaseType(SyntaxFactory.ParseTypeName("BackgroundService"));
        var newClass = classNode.WithBaseList(SyntaxFactory.BaseList(SyntaxFactory.SingletonSeparatedList<BaseTypeSyntax>(baseType)));

        // 3. Add ExecuteAsync override
        var executeAsync = SyntaxFactory.MethodDeclaration(SyntaxFactory.ParseTypeName("Task"), "ExecuteAsync")
            .AddModifiers(SyntaxFactory.Token(SyntaxKind.ProtectedKeyword), SyntaxFactory.Token(SyntaxKind.OverrideKeyword), SyntaxFactory.Token(SyntaxKind.AsyncKeyword))
            .AddParameterListParameters(SyntaxFactory.Parameter(SyntaxFactory.Identifier("stoppingToken")).WithType(SyntaxFactory.ParseTypeName("CancellationToken")))
            .WithBody(SyntaxFactory.Block(
                SyntaxFactory.WhileStatement(
                    SyntaxFactory.PrefixUnaryExpression(SyntaxKind.LogicalNotExpression, SyntaxFactory.IdentifierName("stoppingToken.IsCancellationRequested")),
                    SyntaxFactory.Block(
                        SyntaxFactory.ExpressionStatement(SyntaxFactory.ParseExpression("await Task.Delay(1000, stoppingToken)"))
                    ))));

        newClass = newClass.AddMembers(executeAsync);

        var newRoot = root.ReplaceNode(classNode, newClass);
        return newRoot.NormalizeWhitespace().ToFullString();
    }
}
