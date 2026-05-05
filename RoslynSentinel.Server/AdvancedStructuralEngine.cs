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
        
        // Search for the class anywhere in the tree (handles both nested and file-scope types)
        var classNode = root?.DescendantNodes().OfType<ClassDeclarationSyntax>().FirstOrDefault(c => c.Identifier.Text == className);

        if (classNode != null)
        {
            // Find members to move - be more flexible with matching
            var membersToMove = classNode.Members.Where(m => 
            {
                if (m is MethodDeclarationSyntax meth)
                    return memberNames.Contains(meth.Identifier.Text);
                if (m is PropertyDeclarationSyntax prop)
                    return memberNames.Contains(prop.Identifier.Text);
                if (m is FieldDeclarationSyntax field)
                    return field.Declaration.Variables.Any(v => memberNames.Contains(v.Identifier.Text));
                return false;
            }).ToList();

            // Only create the new class if we found members to move
            if (membersToMove.Count > 0)
            {
                var newClassNode = SyntaxFactory.ClassDeclaration(newClassName)
                    .WithModifiers(SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.PublicKeyword)))
                    .WithMembers(SyntaxFactory.List(membersToMove));

                return new Dictionary<string, string> { { Path.Combine(Path.GetDirectoryName(filePath)!, $"{newClassName}.cs"), newClassNode.NormalizeWhitespace().ToFullString() } };
            }
        }
        return new Dictionary<string, string>();
    }

    /// <summary>
    /// Inlines a class by moving all its members into the first class of the target file,
    /// then removes the source class declaration.
    /// </summary>
    public async Task<Dictionary<string, string>> InlineClassAsync(string sourceFilePath, string targetFilePath, string className, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        bool sameFile = string.Equals(
            Path.GetFullPath(sourceFilePath),
            Path.GetFullPath(targetFilePath),
            StringComparison.OrdinalIgnoreCase);

        // Load source document
        var sourceDoc = solution.GetDocumentIdsWithFilePath(sourceFilePath)
            .Select(solution.GetDocument).FirstOrDefault();
        if (sourceDoc == null)
            return new Dictionary<string, string>
            {
                { "__error__", $"Source file '{Path.GetFileName(sourceFilePath)}' not found in solution." }
            };

        var sourceRoot = await sourceDoc.GetSyntaxRootAsync(cancellationToken) as CompilationUnitSyntax;
        if (sourceRoot == null) return new Dictionary<string, string>();

        var sourceClass = sourceRoot.DescendantNodes()
            .OfType<ClassDeclarationSyntax>()
            .FirstOrDefault(c => c.Identifier.Text == className);
        if (sourceClass == null)
            return new Dictionary<string, string>
            {
                { "__error__", $"Class '{className}' not found in '{Path.GetFileName(sourceFilePath)}'." }
            };

        var membersToInline = sourceClass.Members;

        if (sameFile)
        {
            // Find the first class in the file that is NOT the class being inlined
            var targetClass = sourceRoot.DescendantNodes()
                .OfType<ClassDeclarationSyntax>()
                .FirstOrDefault(c => c.Identifier.Text != className);
            if (targetClass == null)
                return new Dictionary<string, string>
                {
                    { "__error__", $"No target class found in '{Path.GetFileName(sourceFilePath)}' to inline '{className}' into." }
                };

            // Add members to target class
            var expandedTarget = targetClass.AddMembers(membersToInline.ToArray());
            var intermediate = (CompilationUnitSyntax)sourceRoot.ReplaceNode(targetClass, expandedTarget);

            // Remove source class from the updated tree (re-locate by name since node identity changed)
            var classToRemove = intermediate.DescendantNodes()
                .OfType<ClassDeclarationSyntax>()
                .FirstOrDefault(c => c.Identifier.Text == className);
            var newRoot = classToRemove != null
                ? (CompilationUnitSyntax)intermediate.RemoveNode(classToRemove, SyntaxRemoveOptions.KeepExteriorTrivia)!
                : intermediate;

            return new Dictionary<string, string>
            {
                { sourceFilePath, newRoot.NormalizeWhitespace().ToFullString() }
            };
        }
        else
        {
            // Cross-file: load target document
            var targetDoc = solution.GetDocumentIdsWithFilePath(targetFilePath)
                .Select(solution.GetDocument).FirstOrDefault();
            if (targetDoc == null)
                return new Dictionary<string, string>
                {
                    { "__error__", $"Target file '{Path.GetFileName(targetFilePath)}' not found in solution." }
                };

            var targetRoot = await targetDoc.GetSyntaxRootAsync(cancellationToken) as CompilationUnitSyntax;
            var targetClass = targetRoot?.DescendantNodes()
                .OfType<ClassDeclarationSyntax>()
                .FirstOrDefault();
            if (targetClass == null)
                return new Dictionary<string, string>
                {
                    { "__error__", $"No class found in target file '{Path.GetFileName(targetFilePath)}'." }
                };

            // Add members to target class
            var expandedTarget = targetClass.AddMembers(membersToInline.ToArray());
            var newTargetRoot = (CompilationUnitSyntax)targetRoot!.ReplaceNode(targetClass, expandedTarget);

            // Remove source class from source file
            var newSourceRoot = (CompilationUnitSyntax)sourceRoot.RemoveNode(sourceClass, SyntaxRemoveOptions.KeepExteriorTrivia)!;

            return new Dictionary<string, string>
            {
                { targetFilePath, newTargetRoot.NormalizeWhitespace().ToFullString() },
                { sourceFilePath, newSourceRoot.NormalizeWhitespace().ToFullString() }
            };
        }
    }
}
