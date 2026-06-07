using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;

namespace RoslynSentinel.Server;

public class AdvancedStructuralEngine
{
    private readonly PersistentWorkspaceManager _workspaceManager;

    public AdvancedStructuralEngine(PersistentWorkspaceManager workspaceManager)
    {
        _workspaceManager = workspaceManager;
    }

    public async Task<DocumentEditResult> ConvertAbstractClassToInterfaceAsync(FilePath filePath, string className, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault();
        if (document == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.DocumentNotFound,
                UpdatedText = null,
                FilePath = filePath
            };
        }

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
            return new DocumentEditResult
            {
                Outcome = EditOutcome.Modified,
                UpdatedText = newRoot.NormalizeWhitespace().ToFullString(),
                FilePath = filePath
            };
        }
        return new DocumentEditResult
        {
            Outcome = EditOutcome.TargetNotFound,
            UpdatedText = null,
            FilePath = filePath
        };
    }

    public async Task<DocumentEditResult> ReplaceConstructorWithFactoryAsync(FilePath filePath, string className, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault();
        if (document == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.DocumentNotFound,
                UpdatedText = null,
                FilePath = filePath
            };
        }

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
            return new DocumentEditResult
            {
                Outcome = EditOutcome.Modified,
                UpdatedText = newRoot.NormalizeWhitespace().ToFullString(),
                FilePath = filePath
            };
        }
        return new DocumentEditResult
        {
            Outcome = EditOutcome.TargetNotFound,
            UpdatedText = null,
            FilePath = filePath
        };
    }

    public async Task<Dictionary<FilePath, string>> ExtractSuperclassAsync(FilePath[] filePaths, string[] classNames, string newBaseClassName, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var changes = new Dictionary<FilePath, string>();
        var firstFile = filePaths[0];
        var document = solution.GetDocumentIdsWithFilePath(firstFile).Select(solution.GetDocument).FirstOrDefault();
        if (document == null)
        {
            return changes;
        }

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var classNode = root?.DescendantNodes().OfType<ClassDeclarationSyntax>().FirstOrDefault(c => c.Identifier.Text == classNames[0]);
        if (classNode == null)
        {
            return changes;
        }

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

    public async Task<Dictionary<FilePath, string>> ExtractClassAsync(FilePath filePath, string className, string newClassName, string[] memberNames, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault();
        if (document == null)
        {
            return new Dictionary<FilePath, string>();
        }

        var root = await document.GetSyntaxRootAsync(cancellationToken) as CompilationUnitSyntax;
        var classNode = root?.DescendantNodes().OfType<ClassDeclarationSyntax>().FirstOrDefault(c => c.Identifier.Text == className);

        if (classNode == null)
        {
            return new Dictionary<FilePath, string>();
        }

        var membersToMove = classNode.Members.Where(m =>
        {
            if (m is MethodDeclarationSyntax meth)
            {
                return memberNames.Contains(meth.Identifier.Text);
            }

            if (m is PropertyDeclarationSyntax prop)
            {
                return memberNames.Contains(prop.Identifier.Text);
            }

            if (m is FieldDeclarationSyntax field)
            {
                return field.Declaration.Variables.Any(v => memberNames.Contains(v.Identifier.Text));
            }

            return false;
        }).ToList();

        if (membersToMove.Count == 0)
        {
            return new Dictionary<FilePath, string>();
        }

        // Capture member symbols BEFORE modifying the syntax tree so SymbolFinder can locate references
        var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
        var memberSymbols = membersToMove
            .Select(m => semanticModel?.GetDeclaredSymbol(m))
            .Where(s => s is IMethodSymbol or IPropertySymbol)
            .ToList();

        // Build extracted class with same namespace + usings as source
        var ns = classNode.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault();
        var newClassNode = SyntaxFactory.ClassDeclaration(newClassName)
            .WithModifiers(SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.PublicKeyword)))
            .WithMembers(SyntaxFactory.List(membersToMove));
        var cleanUsings = SyntaxFactory.List(root!.Usings.Select(u =>
            u.WithoutTrailingTrivia().WithTrailingTrivia(SyntaxFactory.ElasticCarriageReturnLineFeed)));
        CompilationUnitSyntax newFileRoot;
        if (ns != null)
        {
            BaseNamespaceDeclarationSyntax newNs = ns is FileScopedNamespaceDeclarationSyntax
                ? (BaseNamespaceDeclarationSyntax)SyntaxFactory.FileScopedNamespaceDeclaration(ns.Name).AddMembers(newClassNode)
                : SyntaxFactory.NamespaceDeclaration(ns.Name).AddMembers(newClassNode);
            newFileRoot = SyntaxFactory.CompilationUnit().WithUsings(cleanUsings).AddMembers(newNs);
        }
        else
        {
            newFileRoot = SyntaxFactory.CompilationUnit().WithUsings(cleanUsings).AddMembers(newClassNode);
        }

        // Update source class: remove extracted members and expose the new class via a public property
        // (public so external callers can update call sites from sourceObj.Method() to sourceObj.NewClass.Method())
        var propDecl = SyntaxFactory.PropertyDeclaration(
            SyntaxFactory.ParseTypeName(newClassName),
            SyntaxFactory.Identifier(newClassName))
            .AddModifiers(SyntaxFactory.Token(SyntaxKind.PublicKeyword))
            .WithAccessorList(SyntaxFactory.AccessorList(SyntaxFactory.List(new[] {
                SyntaxFactory.AccessorDeclaration(SyntaxKind.GetAccessorDeclaration)
                    .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken))
            })))
            .WithInitializer(SyntaxFactory.EqualsValueClause(
                SyntaxFactory.ObjectCreationExpression(SyntaxFactory.ParseTypeName(newClassName))
                    .WithArgumentList(SyntaxFactory.ArgumentList())))
            .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken));

        var updatedSourceClass = classNode
            .RemoveNodes(membersToMove, SyntaxRemoveOptions.KeepNoTrivia)!
            .AddMembers(propDecl);

        // Fix internal callers within the remaining class body so they route through the new property:
        //   this.Method()  →  NewClass.Method()
        //   Method()       →  NewClass.Method()
        var memberNameSet = new HashSet<string>(memberNames, StringComparer.Ordinal);

        var thisAccesses = updatedSourceClass.DescendantNodes()
            .OfType<MemberAccessExpressionSyntax>()
            .Where(ma => ma.Expression is ThisExpressionSyntax && memberNameSet.Contains(ma.Name.Identifier.Text))
            .ToList();
        if (thisAccesses.Count > 0)
        {
            updatedSourceClass = updatedSourceClass.ReplaceNodes(thisAccesses, (original, _) =>
                original.WithExpression(SyntaxFactory.IdentifierName(newClassName)));
        }

        var bareIdentifiers = updatedSourceClass.DescendantNodes()
            .OfType<IdentifierNameSyntax>()
            .Where(id =>
                memberNameSet.Contains(id.Identifier.Text) &&
                id.Parent is not MemberAccessExpressionSyntax &&
                id.Parent is not QualifiedNameSyntax)
            .ToList();
        if (bareIdentifiers.Count > 0)
        {
            updatedSourceClass = updatedSourceClass.ReplaceNodes(bareIdentifiers, (original, _) =>
                SyntaxFactory.MemberAccessExpression(
                    SyntaxKind.SimpleMemberAccessExpression,
                    SyntaxFactory.IdentifierName(newClassName),
                    (SimpleNameSyntax)original));
        }

        var updatedRoot = root!.ReplaceNode(classNode, updatedSourceClass);

        var newFilePath = Path.Combine(Path.GetDirectoryName(filePath)!, $"{newClassName}.cs");
        var result = new Dictionary<FilePath, string>
        {
            { newFilePath, newFileRoot.NormalizeWhitespace().ToFullString() },
            { filePath, updatedRoot.NormalizeWhitespace().ToFullString() }
        };

        // Update cross-file call sites: expr.Method() → expr.NewClassName.Method()
        var skipPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { filePath, newFilePath };
        foreach (var symbol in memberSymbols)
        {
            if (symbol == null)
            {
                continue;
            }

            var references = await SymbolFinder.FindReferencesAsync(symbol, solution, cancellationToken);
            var byDocument = references.SelectMany(r => r.Locations)
                .Where(l => l.Document.FilePath != null && !skipPaths.Contains(l.Document.FilePath))
                .GroupBy(l => l.Document.Id)
                .ToDictionary(g => g.Key, g => g.ToList());

            foreach (var (docId, locations) in byDocument)
            {
                var doc = solution.GetDocument(docId);
                if (doc?.FilePath == null)
                {
                    continue;
                }

                // Use already-updated root if this file has been modified in a previous symbol's iteration
                SyntaxNode? docRoot;
                if (result.TryGetValue(doc.FilePath, out var alreadyModified))
                {
                    docRoot = CSharpSyntaxTree.ParseText(alreadyModified, cancellationToken: cancellationToken).GetRoot(cancellationToken);
                }
                else
                {
                    docRoot = await doc.GetSyntaxRootAsync(cancellationToken);
                }

                if (docRoot == null)
                {
                    continue;
                }

                var spans = locations.Select(l => l.Location.SourceSpan).ToHashSet();

                // Find the identifier nodes at the reference spans; their parent MemberAccessExpression
                // is what we need to insert the new property name into.
                var identifiers = docRoot.DescendantNodes()
                    .OfType<SimpleNameSyntax>()
                    .Where(n => spans.Contains(n.Span))
                    .ToList();
                var memberAccesses = identifiers
                    .Select(id => id.Parent as MemberAccessExpressionSyntax)
                    .Where(ma => ma != null)
                    .Cast<MemberAccessExpressionSyntax>()
                    .Distinct()
                    .ToList();

                if (memberAccesses.Count == 0)
                {
                    continue;
                }

                var updatedDocRoot = docRoot.ReplaceNodes(memberAccesses, (original, _) =>
                {
                    var intermedReceiver = SyntaxFactory.MemberAccessExpression(
                        SyntaxKind.SimpleMemberAccessExpression,
                        original.Expression,
                        SyntaxFactory.IdentifierName(newClassName));
                    return original.WithExpression(intermedReceiver);
                });

                result[doc.FilePath] = updatedDocRoot.NormalizeWhitespace().ToFullString();
            }
        }

        return result;
    }

    /// <summary>
    /// Inlines a class by moving all its members into the first class of the target file,
    /// then removes the source class declaration. Also renames all type references to the
    /// inlined class across the solution to point to the target class name.
    /// </summary>
    public async Task<Dictionary<FilePath, string>> InlineClassAsync(string sourceFilePath, string targetFilePath, string className, CancellationToken cancellationToken = default)
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
        {
            return new Dictionary<FilePath, string>
            {
                { "__error__", $"Source file '{Path.GetFileName(sourceFilePath)}' not found in solution." }
            };
        }

        var sourceRoot = await sourceDoc.GetSyntaxRootAsync(cancellationToken) as CompilationUnitSyntax;
        if (sourceRoot == null)
        {
            return new Dictionary<FilePath, string>();
        }

        var sourceClass = sourceRoot.DescendantNodes()
            .OfType<ClassDeclarationSyntax>()
            .FirstOrDefault(c => c.Identifier.Text == className);
        if (sourceClass == null)
        {
            return new Dictionary<FilePath, string>
            {
                { "__error__", $"Class '{className}' not found in '{Path.GetFileName(sourceFilePath)}'." }
            };
        }

        // Capture class symbol BEFORE modification so SymbolFinder can resolve all references
        var semanticModel = await sourceDoc.GetSemanticModelAsync(cancellationToken);
        var classSymbol = semanticModel?.GetDeclaredSymbol(sourceClass, cancellationToken) as INamedTypeSymbol;

        var membersToInline = sourceClass.Members;
        var result = new Dictionary<FilePath, string>();
        string targetClassName;

        if (sameFile)
        {
            // Find the first class in the file that is NOT the class being inlined
            var targetClass = sourceRoot.DescendantNodes()
                .OfType<ClassDeclarationSyntax>()
                .FirstOrDefault(c => c.Identifier.Text != className);
            if (targetClass == null)
            {
                return new Dictionary<FilePath, string>
                {
                    { "__error__", $"No target class found in '{Path.GetFileName(sourceFilePath)}' to inline '{className}' into." }
                };
            }

            targetClassName = targetClass.Identifier.Text;

            var expandedTarget = targetClass.AddMembers(membersToInline.ToArray());
            var intermediate = (CompilationUnitSyntax)sourceRoot.ReplaceNode(targetClass, expandedTarget);

            var classToRemove = intermediate.DescendantNodes()
                .OfType<ClassDeclarationSyntax>()
                .FirstOrDefault(c => c.Identifier.Text == className);
            var newRoot = classToRemove != null
                ? (CompilationUnitSyntax)intermediate.RemoveNode(classToRemove, SyntaxRemoveOptions.KeepExteriorTrivia)!
                : intermediate;

            result[sourceFilePath] = newRoot.NormalizeWhitespace().ToFullString();

            // Update type references in all other files
            if (classSymbol != null)
            {
                await UpdateTypeReferencesAsync(solution, classSymbol, className, targetClassName,
                    new HashSet<string>(StringComparer.OrdinalIgnoreCase) { sourceFilePath }, result, cancellationToken);
            }
        }
        else
        {
            var targetDoc = solution.GetDocumentIdsWithFilePath(targetFilePath)
                .Select(solution.GetDocument).FirstOrDefault();
            if (targetDoc == null)
            {
                return new Dictionary<FilePath, string>
                {
                    { "__error__", $"Target file '{Path.GetFileName(targetFilePath)}' not found in solution." }
                };
            }

            var targetRoot = await targetDoc.GetSyntaxRootAsync(cancellationToken) as CompilationUnitSyntax;
            var targetClass = targetRoot?.DescendantNodes()
                .OfType<ClassDeclarationSyntax>()
                .FirstOrDefault();
            if (targetClass == null)
            {
                return new Dictionary<FilePath, string>
                {
                    { "__error__", $"No class found in target file '{Path.GetFileName(targetFilePath)}'." }
                };
            }

            targetClassName = targetClass.Identifier.Text;

            var expandedTarget = targetClass.AddMembers(membersToInline.ToArray());
            var newTargetRoot = (CompilationUnitSyntax)targetRoot!.ReplaceNode(targetClass, expandedTarget);
            var newSourceRoot = (CompilationUnitSyntax)sourceRoot.RemoveNode(sourceClass, SyntaxRemoveOptions.KeepExteriorTrivia)!;

            result[targetFilePath] = newTargetRoot.NormalizeWhitespace().ToFullString();
            result[sourceFilePath] = newSourceRoot.NormalizeWhitespace().ToFullString();

            // Update type references in all other files
            if (classSymbol != null)
            {
                await UpdateTypeReferencesAsync(solution, classSymbol, className, targetClassName,
                    new HashSet<string>(StringComparer.OrdinalIgnoreCase) { sourceFilePath, targetFilePath }, result, cancellationToken);
            }
        }

        return result;
    }

    private static async Task UpdateTypeReferencesAsync(
        Solution solution,
        INamedTypeSymbol classSymbol,
        string oldName,
        string newName,
        HashSet<string> skipPaths,
        Dictionary<FilePath, string> result,
        CancellationToken cancellationToken)
    {
        var references = await SymbolFinder.FindReferencesAsync(classSymbol, solution, cancellationToken);
        var byDocument = references.SelectMany(r => r.Locations)
            .Where(l => l.Document.FilePath != null && !skipPaths.Contains(l.Document.FilePath))
            .GroupBy(l => l.Document.Id)
            .ToDictionary(g => g.Key, g => g.ToList());

        foreach (var (docId, locations) in byDocument)
        {
            var doc = solution.GetDocument(docId);
            if (doc?.FilePath == null)
            {
                continue;
            }

            var docRoot = await doc.GetSyntaxRootAsync(cancellationToken);
            if (docRoot == null)
            {
                continue;
            }

            var spans = locations.Select(l => l.Location.SourceSpan).ToHashSet();

            var nodesToRename = docRoot.DescendantNodes()
                .Where(n => spans.Contains(n.Span))
                .ToList();

            if (nodesToRename.Count == 0)
            {
                continue;
            }

            var updatedRoot = docRoot.ReplaceNodes(nodesToRename, (original, _) =>
            {
                if (original is IdentifierNameSyntax id && id.Identifier.Text == oldName)
                {
                    return SyntaxFactory.IdentifierName(newName).WithTriviaFrom(id);
                }

                if (original is GenericNameSyntax gen && gen.Identifier.Text == oldName)
                {
                    return gen.WithIdentifier(SyntaxFactory.Identifier(newName));
                }

                return original;
            });

            result[doc.FilePath] = updatedRoot.NormalizeWhitespace().ToFullString();
        }
    }
}
