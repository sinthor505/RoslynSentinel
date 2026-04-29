using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Editing;

namespace RoslynSentinel.Server;

public record DependencyReport(string TypeName, string Status, string RecommendedLifetime);

public class DependencyInjectionEngine
{
    private readonly PersistentWorkspaceManager _workspaceManager;

    public DependencyInjectionEngine(PersistentWorkspaceManager workspaceManager)
    {
        _workspaceManager = workspaceManager;
    }

    /// <summary>
    /// Analyzes a class to find what it needs in its constructor and checks if those are likely registered.
    /// </summary>
    public async Task<List<DependencyReport>> AnalyzeDependenciesAsync(string filePath, string className, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault();
        if (document == null) throw new Exception("File not found.");

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
        var classNode = root?.DescendantNodes().OfType<ClassDeclarationSyntax>().FirstOrDefault(c => c.Identifier.Text == className);

        if (classNode == null) throw new Exception("Class not found.");

        var reports = new List<DependencyReport>();
        var constructor = classNode.Members.OfType<ConstructorDeclarationSyntax>().FirstOrDefault();

        if (constructor != null)
        {
            foreach (var parameter in constructor.ParameterList.Parameters)
            {
                var typeInfo = semanticModel!.GetTypeInfo(parameter.Type!);
                var typeSymbol = typeInfo.Type;

                if (typeSymbol != null)
                {
                    // Logic: Interfaces are usually Scoped/Singleton, DALs are Scoped, etc.
                    string lifetime = "Scoped";
                    if (typeSymbol.Name.Contains("Client") || typeSymbol.Name.Contains("Factory")) lifetime = "Singleton";
                    if (typeSymbol.Name.Contains("Context")) lifetime = "Scoped";

                    reports.Add(new DependencyReport(typeSymbol.ToDisplayString(), "Detected in Constructor", lifetime));
                }
            }
        }

        return reports;
    }

    /// <summary>
    /// Injects a new dependency into a class constructor and adds the corresponding private field.
    /// </summary>
    public async Task<string> AddDependencyAsync(string filePath, string className, string dependencyType, string dependencyName, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault();
        if (document == null) throw new Exception("File not found.");

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var classNode = root?.DescendantNodes().OfType<ClassDeclarationSyntax>().FirstOrDefault(c => c.Identifier.Text == className);
        if (classNode == null) throw new Exception("Class not found.");

        var fieldName = $"_{char.ToLower(dependencyName[0])}{dependencyName.Substring(1)}";
        
        // 1. Create the field
        var fieldDecl = SyntaxFactory.FieldDeclaration(
            SyntaxFactory.VariableDeclaration(SyntaxFactory.ParseTypeName(dependencyType))
            .WithVariables(SyntaxFactory.SingletonSeparatedList(SyntaxFactory.VariableDeclarator(fieldName))))
            .AddModifiers(SyntaxFactory.Token(SyntaxKind.PrivateKeyword), SyntaxFactory.Token(SyntaxKind.ReadOnlyKeyword));

        // 2. Update/Create constructor
        var constructor = classNode.Members.OfType<ConstructorDeclarationSyntax>().FirstOrDefault();
        var newParameter = SyntaxFactory.Parameter(SyntaxFactory.Identifier(dependencyName)).WithType(SyntaxFactory.ParseTypeName(dependencyType));
        var assignment = SyntaxFactory.ExpressionStatement(
            SyntaxFactory.AssignmentExpression(
                SyntaxKind.SimpleAssignmentExpression,
                SyntaxFactory.IdentifierName(fieldName),
                SyntaxFactory.IdentifierName(dependencyName)));

        ClassDeclarationSyntax newClassNode;
        if (constructor != null)
        {
            var newConstructor = constructor.AddParameterListParameters(newParameter)
                .AddBodyStatements(assignment);
            newClassNode = classNode.ReplaceNode(constructor, newConstructor);
        }
        else
        {
            var newConstructor = SyntaxFactory.ConstructorDeclaration(className)
                .AddModifiers(SyntaxFactory.Token(SyntaxKind.PublicKeyword))
                .WithParameterList(SyntaxFactory.ParameterList(SyntaxFactory.SingletonSeparatedList(newParameter)))
                .WithBody(SyntaxFactory.Block(assignment));
            newClassNode = classNode.AddMembers(newConstructor);
        }

        newClassNode = newClassNode.InsertNodesBefore(newClassNode.Members.First(), new[] { fieldDecl });

        var newRoot = root!.ReplaceNode(classNode, newClassNode);
        return newRoot.NormalizeWhitespace().ToFullString();
    }
}
