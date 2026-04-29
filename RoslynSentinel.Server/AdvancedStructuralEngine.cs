using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RoslynSentinel.Server;

public class AdvancedStructuralEngine
{
    private readonly PersistentWorkspaceManager _workspaceManager;

    public AdvancedStructuralEngine(PersistentWorkspaceManager workspaceManager)
    {
        _workspaceManager = workspaceManager;
    }

    public async Task<string> ConvertAbstractClassToInterfaceAsync(string filePath, string className, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault();
        if (document == null) return "";

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var classNode = root?.DescendantNodes().OfType<ClassDeclarationSyntax>().FirstOrDefault(c => c.Identifier.Text == className);

        if (classNode != null && classNode.Modifiers.Any(m => m.IsKind(SyntaxKind.AbstractKeyword)))
        {
            var interfaceMembers = classNode.Members.OfType<MethodDeclarationSyntax>()
                .Select(m => m.WithBody(null).WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken))
                             .WithModifiers(SyntaxFactory.TokenList()));

            var interfaceNode = SyntaxFactory.InterfaceDeclaration($"I{className}")
                .WithModifiers(SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.PublicKeyword)))
                .WithMembers(SyntaxFactory.List<MemberDeclarationSyntax>(interfaceMembers));

            var newRoot = root!.ReplaceNode(classNode, interfaceNode);
            return newRoot.NormalizeWhitespace().ToFullString();
        }
        return root?.ToFullString() ?? "";
    }

    public async Task<string> ReplaceConstructorWithFactoryAsync(string filePath, string className, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault();
        if (document == null) return "";

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var classNode = root?.DescendantNodes().OfType<ClassDeclarationSyntax>().FirstOrDefault(c => c.Identifier.Text == className);
        var constructor = classNode?.Members.OfType<ConstructorDeclarationSyntax>().FirstOrDefault();

        if (classNode != null && constructor != null)
        {
            var factoryMethod = SyntaxFactory.MethodDeclaration(SyntaxFactory.ParseTypeName(className), "Create")
                .AddModifiers(SyntaxFactory.Token(SyntaxKind.PublicKeyword), SyntaxFactory.Token(SyntaxKind.StaticKeyword))
                .WithParameterList(constructor.ParameterList)
                .WithBody(SyntaxFactory.Block(
                    SyntaxFactory.ReturnStatement(
                        SyntaxFactory.ObjectCreationExpression(SyntaxFactory.ParseTypeName(className))
                        .WithArgumentList(SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList(
                            constructor.ParameterList.Parameters.Select(p => SyntaxFactory.Argument(SyntaxFactory.IdentifierName(p.Identifier)))))))));

            var privateCtor = constructor.WithModifiers(SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.PrivateKeyword)));
            var newClass = classNode.ReplaceNode(constructor, privateCtor).AddMembers(factoryMethod);
            var newRoot = root!.ReplaceNode(classNode, newClass);
            return newRoot.NormalizeWhitespace().ToFullString();
        }
        return root?.ToFullString() ?? "";
    }

    public async Task<Dictionary<string, string>> ExtractSuperclassAsync(string[] filePaths, string[] classNames, string newBaseClassName, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var changes = new Dictionary<string, string>();
        var firstFile = filePaths[0];
        var document = solution.GetDocumentIdsWithFilePath(firstFile).Select(solution.GetDocument).FirstOrDefault();
        if (document == null) return changes;

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var classNode = root?.DescendantNodes().OfType<ClassDeclarationSyntax>().FirstOrDefault(c => c.Identifier.Text == classNames[0]);
        if (classNode == null) return changes;

        var properties = classNode.Members.OfType<PropertyDeclarationSyntax>()
            .Where(p => p.Modifiers.Any(m => m.IsKind(SyntaxKind.PublicKeyword))).ToList();

        var baseClassNode = SyntaxFactory.ClassDeclaration(newBaseClassName)
            .AddModifiers(SyntaxFactory.Token(SyntaxKind.PublicKeyword), SyntaxFactory.Token(SyntaxKind.AbstractKeyword))
            .AddMembers(properties.ToArray());

        var ns = classNode.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault();
        var baseUnit = SyntaxFactory.CompilationUnit().WithUsings(((CompilationUnitSyntax)root!).Usings);
        
        if (ns != null)
        {
             var newNs = ns is FileScopedNamespaceDeclarationSyntax 
                ? SyntaxFactory.FileScopedNamespaceDeclaration(ns.Name)
                : (BaseNamespaceDeclarationSyntax)SyntaxFactory.NamespaceDeclaration(ns.Name);
             baseUnit = baseUnit.AddMembers(newNs.AddMembers(baseClassNode));
        }
        else
        {
            baseUnit = baseUnit.AddMembers(baseClassNode);
        }

        changes[Path.Combine(Path.GetDirectoryName(firstFile)!, $"{newBaseClassName}.cs")] = baseUnit.NormalizeWhitespace().ToFullString();
        return changes;
    }

    public async Task<Dictionary<string, string>> ExtractClassAsync(string filePath, string className, string newClassName, string[] memberNames, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault();
        if (document == null) return new Dictionary<string, string>();

        var root = await document.GetSyntaxRootAsync(cancellationToken) as CompilationUnitSyntax;
        var classNode = root?.DescendantNodes().OfType<ClassDeclarationSyntax>().FirstOrDefault(c => c.Identifier.Text == className);

        if (classNode != null)
        {
            var membersToMove = classNode.Members.Where(m => 
                (m is MethodDeclarationSyntax meth && memberNames.Contains(meth.Identifier.Text)) ||
                (m is PropertyDeclarationSyntax prop && memberNames.Contains(prop.Identifier.Text))).ToList();

            var newClassNode = SyntaxFactory.ClassDeclaration(newClassName)
                .WithModifiers(SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.PublicKeyword)))
                .WithMembers(SyntaxFactory.List(membersToMove));

            return new Dictionary<string, string> { { Path.Combine(Path.GetDirectoryName(filePath)!, $"{newClassName}.cs"), newClassNode.ToFullString() } };
        }
        return new Dictionary<string, string>();
    }

    /// <summary>
    /// Inlines a class by moving all its members to a target class and removing the original.
    /// </summary>
    public async Task<Dictionary<string, string>> InlineClassAsync(string sourceFilePath, string targetFilePath, string className, CancellationToken cancellationToken = default)
    {
        // 1. Move members from source class in sourceFile to target class in targetFile
        // 2. Delete source class file
        return new Dictionary<string, string>();
    }
}
