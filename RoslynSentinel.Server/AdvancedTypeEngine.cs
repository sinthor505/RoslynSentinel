using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RoslynSentinel.Server;

public class AdvancedTypeEngine
{
    private readonly PersistentWorkspaceManager _workspaceManager;

    public AdvancedTypeEngine(PersistentWorkspaceManager workspaceManager)
    {
        _workspaceManager = workspaceManager;
    }

    public async Task<Dictionary<string, string>> ConvertTupleToClassAsync(string filePath, string methodName, string newClassName, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault();
        if (document == null)
        {
            throw new FileNotFoundException($"File not found: {filePath}");
        }

        var root = await document.GetSyntaxRootAsync(cancellationToken) as CompilationUnitSyntax;
        var methodNode = root?.DescendantNodes().OfType<MethodDeclarationSyntax>().FirstOrDefault(m => m.Identifier.Text == methodName);

        if (methodNode == null)
        {
            throw new InvalidOperationException("Method not found.");
        }

        if (methodNode.ReturnType is not TupleTypeSyntax tupleType)
        {
            throw new InvalidOperationException("Method does not return a named tuple.");
        }

        var properties = new List<PropertyDeclarationSyntax>();
        foreach (var element in tupleType.Elements)
        {
            var name = element.Identifier.Text;
            if (string.IsNullOrEmpty(name))
            {
                name = "Item" + (properties.Count + 1);
            }

            var prop = SyntaxFactory.PropertyDeclaration(element.Type, char.ToUpper(name[0]) + name.Substring(1))
                .AddModifiers(SyntaxFactory.Token(SyntaxKind.PublicKeyword))
                .AddAccessorListAccessors(
                    SyntaxFactory.AccessorDeclaration(SyntaxKind.GetAccessorDeclaration).WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken)),
                    SyntaxFactory.AccessorDeclaration(SyntaxKind.SetAccessorDeclaration).WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken)));

            properties.Add(prop);
        }

        var newClass = SyntaxFactory.ClassDeclaration(newClassName)
            .AddModifiers(SyntaxFactory.Token(SyntaxKind.PublicKeyword))
            .AddMembers(properties.ToArray());

        var ns = methodNode.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault();
        var classRoot = SyntaxFactory.CompilationUnit().WithUsings(root!.Usings);

        if (ns != null)
        {
            var newNs = ns is FileScopedNamespaceDeclarationSyntax
               ? SyntaxFactory.FileScopedNamespaceDeclaration(ns.Name)
               : (BaseNamespaceDeclarationSyntax)SyntaxFactory.NamespaceDeclaration(ns.Name);
            classRoot = classRoot.AddMembers(newNs.AddMembers(newClass));
        }
        else
        {
            classRoot = classRoot.AddMembers(newClass);
        }

        var newMethodNode = methodNode.WithReturnType(SyntaxFactory.ParseTypeName(newClassName));
        var updatedRoot = root.ReplaceNode(methodNode, newMethodNode);

        return new Dictionary<string, string>
        {
            { filePath, updatedRoot.NormalizeWhitespace().ToFullString() },
            { Path.Combine(Path.GetDirectoryName(filePath)!, $"{newClassName}.cs"), classRoot.NormalizeWhitespace().ToFullString() }
        };
    }

    public async Task<Dictionary<string, string>> ChangePropertyTypeAsync(string filePath, string className, string propertyName, string newType, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault();
        if (document == null)
        {
            throw new FileNotFoundException($"File not found: {filePath}");
        }

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var classNode = root?.DescendantNodes().OfType<ClassDeclarationSyntax>().FirstOrDefault(c => c.Identifier.Text == className);
        var propNode = classNode?.Members.OfType<PropertyDeclarationSyntax>().FirstOrDefault(p => p.Identifier.Text == propertyName);

        if (classNode == null || propNode == null)
        {
            throw new InvalidOperationException("Class or property not found.");
        }

        var newPropNode = propNode.WithType(SyntaxFactory.ParseTypeName(newType).WithTrailingTrivia(SyntaxFactory.Space));
        var newRoot = root!.ReplaceNode(propNode, newPropNode);

        var updatedSolution = solution.WithDocumentSyntaxRoot(document.Id, newRoot);
        var changes = new Dictionary<string, string>();
        foreach (var docId in updatedSolution.GetChanges(solution).GetProjectChanges().SelectMany(pc => pc.GetChangedDocuments()))
        {
            var doc = updatedSolution.GetDocument(docId)!;
            var text = await doc.GetTextAsync(cancellationToken);
            changes[doc.FilePath!] = text.ToString();
        }

        return changes;
    }

    public async Task<Dictionary<string, string>> ConvertAnonymousToNamedAsync(string filePath, string newClassName, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault();
        if (document == null)
        {
            throw new FileNotFoundException($"File not found: {filePath}");
        }

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var anonType = root?.DescendantNodes().OfType<AnonymousObjectCreationExpressionSyntax>().FirstOrDefault();

        if (anonType != null)
        {
            var properties = anonType.Initializers.Select(init =>
            {
                var name = init.NameEquals?.Name.Identifier.Text ?? "Prop";
                return SyntaxFactory.PropertyDeclaration(SyntaxFactory.ParseTypeName("object"), char.ToUpper(name[0]) + name.Substring(1))
                    .AddModifiers(SyntaxFactory.Token(SyntaxKind.PublicKeyword))
                    .AddAccessorListAccessors(
                        SyntaxFactory.AccessorDeclaration(SyntaxKind.GetAccessorDeclaration).WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken)),
                        SyntaxFactory.AccessorDeclaration(SyntaxKind.SetAccessorDeclaration).WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken)));
            });

            var newClass = SyntaxFactory.ClassDeclaration(newClassName)
                .AddModifiers(SyntaxFactory.Token(SyntaxKind.PublicKeyword))
                .AddMembers(properties.ToArray());

            return new Dictionary<string, string> { { Path.Combine(Path.GetDirectoryName(filePath)!, $"{newClassName}.cs"), newClass.NormalizeWhitespace().ToFullString() } };
        }

        throw new InvalidOperationException("Anonymous type not found.");
    }
}
