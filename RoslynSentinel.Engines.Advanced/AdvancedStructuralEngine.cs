using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Formatting;

using RoslynSentinel.Engines.Basic;

namespace RoslynSentinel.Engines.Advanced;

public class StructuralRefactoringEngine
{
    private readonly IWorkspaceManager _workspaceManager;
    private readonly ValidationEngine? _validationEngine;
    public StructuralRefactoringEngine(IWorkspaceManager workspaceManager, ValidationEngine? validationEngine = null)
    {
        _workspaceManager = workspaceManager;
        _validationEngine = validationEngine;
        _symbolNavigationEngine = new SymbolNavigationEngine(_workspaceManager);
    }

    public async Task<DocumentEditResult> ConvertAbstractClassToInterfaceAsync(FilePathWrapper filePath, string className, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetSolutionAsync(ReadSource.Committed, cancellationToken);
        var document = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault();
        if (document == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.DocumentNotFound,
                FilePath = filePath
            };
        }

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var classNode = root?.DescendantNodes().OfType<ClassDeclarationSyntax>().FirstOrDefault(c => c.Identifier.Text == className);
        if (classNode != null && classNode.Modifiers.Any(m => m.IsKind(SyntaxKind.AbstractKeyword)))
        {
            var interfaceMembers = classNode.Members.OfType<MethodDeclarationSyntax>().Select(m => m.WithBody(null).WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken)).WithModifiers(SyntaxFactory.TokenList()));
            var interfaceNode = SyntaxFactory.InterfaceDeclaration($"I{className}").WithModifiers(SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.PublicKeyword))).WithMembers(SyntaxFactory.List<MemberDeclarationSyntax>(interfaceMembers));
            var newRoot = root!.ReplaceNode(classNode, interfaceNode);
            return new DocumentEditResult
            {
                Outcome = EditOutcome.Modified,
                UpdatedText = RoslynFormattingHelper.NormalizeWholeSubtreeWhitespace(newRoot).ToFullString(),
                FilePath = filePath
            };
        }

        return new DocumentEditResult
        {
            Outcome = EditOutcome.TargetNotFound,
            FilePath = filePath
        };
    }

    public async Task<DocumentEditResult> ReplaceConstructorWithFactoryAsync(FilePathWrapper filePath, string className, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetSolutionAsync(ReadSource.Committed, cancellationToken);
        var document = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault();
        if (document == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.DocumentNotFound,
                FilePath = filePath
            };
        }

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var classNode = root?.DescendantNodes().OfType<ClassDeclarationSyntax>().FirstOrDefault(c => c.Identifier.Text == className);
        var constructor = classNode?.Members.OfType<ConstructorDeclarationSyntax>().FirstOrDefault();
        if (classNode != null && constructor != null)
        {
            var factoryMethod = SyntaxFactory.MethodDeclaration(SyntaxFactory.ParseTypeName(className), "Create").AddModifiers(SyntaxFactory.Token(SyntaxKind.PublicKeyword), SyntaxFactory.Token(SyntaxKind.StaticKeyword)).WithParameterList(constructor.ParameterList).WithBody(SyntaxFactory.Block(SyntaxFactory.ReturnStatement(SyntaxFactory.ObjectCreationExpression(SyntaxFactory.ParseTypeName(className)).WithArgumentList(SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList(constructor.ParameterList.Parameters.Select(p => SyntaxFactory.Argument(SyntaxFactory.IdentifierName(p.Identifier)))))))));
            var privateCtor = constructor.WithModifiers(SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.PrivateKeyword)));
            var newClass = classNode.ReplaceNode(constructor, privateCtor).AddMembers(factoryMethod);
            var newRoot = root!.ReplaceNode(classNode, newClass);
            return new DocumentEditResult
            {
                Outcome = EditOutcome.Modified,
                UpdatedText = RoslynFormattingHelper.NormalizeWholeSubtreeWhitespace(newRoot).ToFullString(),
                FilePath = filePath
            };
        }

        return new DocumentEditResult
        {
            Outcome = EditOutcome.TargetNotFound,
            FilePath = filePath
        };
    }

    public async Task<Dictionary<FilePathWrapper, string>> ExtractSuperclassAsync(FilePathWrapper[] filePaths, string[] classNames, string newBaseClassName, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetSolutionAsync(ReadSource.Committed, cancellationToken);
        var changes = new Dictionary<FilePathWrapper, string>();
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

        var properties = classNode.Members.OfType<PropertyDeclarationSyntax>().Where(p => p.Modifiers.Any(m => m.IsKind(SyntaxKind.PublicKeyword))).ToList();
        var baseClassNode = SyntaxFactory.ClassDeclaration(newBaseClassName).AddModifiers(SyntaxFactory.Token(SyntaxKind.PublicKeyword), SyntaxFactory.Token(SyntaxKind.AbstractKeyword)).AddMembers(properties.ToArray());
        var ns = classNode.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault();
        var baseUnit = SyntaxFactory.CompilationUnit().WithUsings(((CompilationUnitSyntax)root!).Usings);
        if (ns != null)
        {
            var newNs = ns is FileScopedNamespaceDeclarationSyntax ? SyntaxFactory.FileScopedNamespaceDeclaration(ns.Name) : (BaseNamespaceDeclarationSyntax)SyntaxFactory.NamespaceDeclaration(ns.Name);
            baseUnit = baseUnit.AddMembers(newNs.AddMembers(baseClassNode));
        }
        else
        {
            baseUnit = baseUnit.AddMembers(baseClassNode);
        }

        changes[Path.Combine(Path.GetDirectoryName(firstFile)!, $"{newBaseClassName}.cs")] = RoslynFormattingHelper.NormalizeWholeSubtreeWhitespace(baseUnit).ToFullString();
        return changes;
    }


    /// <summary>
    /// Inlines a class by moving all its members into the first class of the target file,
    /// then removes the source class declaration. Also renames all type references to the
    /// inlined class across the solution to point to the target class name.
    /// </summary>
    public async Task<Dictionary<FilePathWrapper, string>> InlineClassAsync(string sourceFilePath, string targetFilePath, string className, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetSolutionAsync(ReadSource.Committed, cancellationToken);
        bool sameFile = string.Equals(Path.GetFullPath(sourceFilePath), Path.GetFullPath(targetFilePath), StringComparison.OrdinalIgnoreCase);
        // Load source document
        var sourceDoc = solution.GetDocumentIdsWithFilePath(sourceFilePath).Select(solution.GetDocument).FirstOrDefault();
        if (sourceDoc == null)
        {
            return new Dictionary<FilePathWrapper, string>
            {
                {
                    "__error__",
                    $"Source file '{Path.GetFileName(sourceFilePath)}' not found in solution."}
            };
        }

        var sourceRoot = await sourceDoc.GetSyntaxRootAsync(cancellationToken) as CompilationUnitSyntax;
        if (sourceRoot == null)
        {
            return new Dictionary<FilePathWrapper, string>();
        }

        var sourceClass = sourceRoot.DescendantNodes().OfType<ClassDeclarationSyntax>().FirstOrDefault(c => c.Identifier.Text == className);
        if (sourceClass == null)
        {
            return new Dictionary<FilePathWrapper, string>
            {
                {
                    "__error__",
                    $"Class '{className}' not found in '{Path.GetFileName(sourceFilePath)}'."}
            };
        }

        // Capture class symbol BEFORE modification so SymbolFinder can resolve all references
        var semanticModel = await sourceDoc.GetSemanticModelAsync(cancellationToken);
        var classSymbol = semanticModel?.GetDeclaredSymbol(sourceClass, cancellationToken) as INamedTypeSymbol;
        var membersToInline = sourceClass.Members;
        var result = new Dictionary<FilePathWrapper, string>();
        string targetClassName;
        if (sameFile)
        {
            // Find the first class in the file that is NOT the class being inlined
            var targetClass = sourceRoot.DescendantNodes().OfType<ClassDeclarationSyntax>().FirstOrDefault(c => c.Identifier.Text != className);
            if (targetClass == null)
            {
                return new Dictionary<FilePathWrapper, string>
                {
                    {
                        "__error__",
                        $"No target class found in '{Path.GetFileName(sourceFilePath)}' to inline '{className}' into."}
                };
            }

            targetClassName = targetClass.Identifier.Text;
            var expandedTarget = targetClass.AddMembers(membersToInline.ToArray());
            var intermediate = (CompilationUnitSyntax)sourceRoot.ReplaceNode(targetClass, expandedTarget);
            var classToRemove = intermediate.DescendantNodes().OfType<ClassDeclarationSyntax>().FirstOrDefault(c => c.Identifier.Text == className);
            var newRoot = classToRemove != null ? (CompilationUnitSyntax)intermediate.RemoveNode(classToRemove, SyntaxRemoveOptions.KeepExteriorTrivia)! : intermediate;
            result[sourceFilePath] = RoslynFormattingHelper.NormalizeWholeSubtreeWhitespace(newRoot).ToFullString();
            // Update type references in all other files
            if (classSymbol != null)
            {
                await UpdateTypeReferencesAsync(solution, classSymbol, className, targetClassName, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { sourceFilePath }, result, cancellationToken);
            }
        }
        else
        {
            var targetDoc = solution.GetDocumentIdsWithFilePath(targetFilePath).Select(solution.GetDocument).FirstOrDefault();
            if (targetDoc == null)
            {
                return new Dictionary<FilePathWrapper, string>
                {
                    {
                        "__error__",
                        $"Target file '{Path.GetFileName(targetFilePath)}' not found in solution."}
                };
            }

            var targetRoot = await targetDoc.GetSyntaxRootAsync(cancellationToken) as CompilationUnitSyntax;
            var targetClass = targetRoot?.DescendantNodes().OfType<ClassDeclarationSyntax>().FirstOrDefault();
            if (targetClass == null)
            {
                return new Dictionary<FilePathWrapper, string>
                {
                    {
                        "__error__",
                        $"No class found in target file '{Path.GetFileName(targetFilePath)}'."}
                };
            }

            targetClassName = targetClass.Identifier.Text;
            var expandedTarget = targetClass.AddMembers(membersToInline.ToArray());
            var newTargetRoot = (CompilationUnitSyntax)targetRoot!.ReplaceNode(targetClass, expandedTarget);
            var newSourceRoot = (CompilationUnitSyntax)sourceRoot.RemoveNode(sourceClass, SyntaxRemoveOptions.KeepExteriorTrivia)!;
            result[targetFilePath] = RoslynFormattingHelper.NormalizeWholeSubtreeWhitespace(newTargetRoot).ToFullString();
            result[sourceFilePath] = RoslynFormattingHelper.NormalizeWholeSubtreeWhitespace(newSourceRoot).ToFullString();
            // Update type references in all other files
            if (classSymbol != null)
            {
                await UpdateTypeReferencesAsync(solution, classSymbol, className, targetClassName, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { sourceFilePath, targetFilePath }, result, cancellationToken);
            }
        }

        return result;
    }

    private static async Task UpdateTypeReferencesAsync(Solution solution, INamedTypeSymbol classSymbol, string oldName, string newName, HashSet<string> skipPaths, Dictionary<FilePathWrapper, string> result, CancellationToken cancellationToken = default)
    {
        var references = await SymbolFinder.FindReferencesAsync(classSymbol, solution, cancellationToken);
        var byDocument = references.SelectMany(r => r.Locations).Where(l => l.Document.FilePath != null && !skipPaths.Contains(l.Document.FilePath)).GroupBy(l => l.Document.Id).ToDictionary(g => g.Key, g => g.ToList());
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
            var nodesToRename = docRoot.DescendantNodes().Where(n => spans.Contains(n.Span)).ToList();
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
            result[doc.FilePath] = RoslynFormattingHelper.NormalizeWholeSubtreeWhitespace(updatedRoot).ToFullString();
        }
    }

    /// <summary>
    /// Removes <paramref name = "nodeToRemove"/> and formats only its former container (the nearest
    /// ancestor whose node identity survives the removal), instead of the whole file.
    /// </summary>
    private static async Task<string> RemoveNodeFormattedAsync(Document document, SyntaxNode root, SyntaxNode nodeToRemove, SyntaxRemoveOptions removeOptions, CancellationToken cancellationToken = default)
    {
        var container = nodeToRemove.Parent;
        if (container == null)
        {
            var bareNewRoot = root.RemoveNode(nodeToRemove, removeOptions)!;
            var bareFormattedDoc = await Formatter.FormatAsync(document.WithSyntaxRoot(bareNewRoot), cancellationToken: cancellationToken);
            return (await bareFormattedDoc.GetTextAsync(cancellationToken)).ToString();
        }

        var annotation = new SyntaxAnnotation();
        var annotatedRoot = root.ReplaceNode(container, container.WithAdditionalAnnotations(annotation));
        var annotatedContainer = annotatedRoot.GetAnnotatedNodes(annotation).Single();
        var trackedNodeToRemove = annotatedContainer.DescendantNodesAndSelf().Single(n => n.IsEquivalentTo(nodeToRemove) && n.Span == nodeToRemove.Span);
        var newRoot = annotatedRoot.RemoveNode(trackedNodeToRemove, removeOptions)!;
        var formattedDoc = await Formatter.FormatAsync(document.WithSyntaxRoot(newRoot), annotation, cancellationToken: cancellationToken);
        return (await formattedDoc.GetTextAsync(cancellationToken)).ToString();
    }

    /// <summary>
    /// Dispatches a named micro-refactoring against a specific line in a file.
    /// </summary>
    /// <param name = "refactoringId">One of: type-to-var, remove-unused-local, add-braces, remove-braces, extract-constant</param>
    public async Task<DocumentEditResult> RunMicroRefactoringAsync(FilePathWrapper filePath, string refactoringId, int line, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetSolutionAsync(ReadSource.Committed, cancellationToken);
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
        if (root == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.SourceInvalid,
                FilePath = filePath,
                Message = "// Source invalid."
            };
        }

        SyntaxNode? newRoot = refactoringId.ToLowerInvariant() switch
        {
            "type-to-var" => ApplyTypeToVar(root, line),
            "remove-unused-local" => ApplyRemoveLocalAtLine(root, line),
            "add-braces" => ApplyAddBraces(root, line),
            "remove-braces" => ApplyRemoveBraces(root, line),
            "extract-constant" => ApplyExtractConstant(root, line),
            _ => throw new ArgumentException($"Unknown micro-refactoring '{refactoringId}'. " + "Known IDs: type-to-var, remove-unused-local, add-braces, remove-braces, extract-constant.")
        };
        return new DocumentEditResult
        {
            Outcome = EditOutcome.Modified,
            FilePath = filePath,
            UpdatedText = (newRoot is null ? null : RoslynFormattingHelper.NormalizeWholeSubtreeWhitespace(newRoot).ToFullString()) ?? RoslynFormattingHelper.NormalizeWholeSubtreeWhitespace(root).ToFullString()
        };
    }

    private static SyntaxNode ApplyTypeToVar(SyntaxNode root, int line)
    {
        var target = root.DescendantNodes().OfType<LocalDeclarationStatementSyntax>().FirstOrDefault(n => n.GetLocation().GetLineSpan().StartLinePosition.Line + 1 == line && !n.IsConst && !n.Declaration.Type.IsVar && n.Declaration.Variables.Count == 1 && n.Declaration.Variables[0].Initializer != null);
        if (target == null)
        {
            return root;
        }

        var varType = SyntaxFactory.IdentifierName("var").WithLeadingTrivia(target.Declaration.Type.GetLeadingTrivia()).WithTrailingTrivia(target.Declaration.Type.GetTrailingTrivia());
        return root.ReplaceNode(target, target.WithDeclaration(target.Declaration.WithType(varType)));
    }

    private static SyntaxNode ApplyRemoveLocalAtLine(SyntaxNode root, int line)
    {
        var target = root.DescendantNodes().OfType<LocalDeclarationStatementSyntax>().FirstOrDefault(n => n.GetLocation().GetLineSpan().StartLinePosition.Line + 1 == line);
        return target == null ? root : (root.RemoveNode(target, SyntaxRemoveOptions.KeepNoTrivia) ?? root);
    }

    private static SyntaxNode ApplyAddBraces(SyntaxNode root, int line)
    {
        // Find an if/while/for at the target line that has a braceless body
        var target = root.DescendantNodes().Where(n => n.GetLocation().GetLineSpan().StartLinePosition.Line + 1 == line).OfType<StatementSyntax>().FirstOrDefault(n => n is IfStatementSyntax || n is WhileStatementSyntax || n is ForStatementSyntax || n is ForEachStatementSyntax);
        if (target == null)
        {
            return root;
        }

        SyntaxNode replacement = target switch
        {
            IfStatementSyntax ifs when ifs.Statement is not BlockSyntax => ifs.WithStatement(SyntaxFactory.Block(ifs.Statement)),
            WhileStatementSyntax ws when ws.Statement is not BlockSyntax => ws.WithStatement(SyntaxFactory.Block(ws.Statement)),
            ForStatementSyntax fs when fs.Statement is not BlockSyntax => fs.WithStatement(SyntaxFactory.Block(fs.Statement)),
            ForEachStatementSyntax fes when fes.Statement is not BlockSyntax => fes.WithStatement(SyntaxFactory.Block(fes.Statement)),
            _ => target
        };
        return root.ReplaceNode(target, replacement);
    }

    private static SyntaxNode ApplyRemoveBraces(SyntaxNode root, int line)
    {
        // Find an if/while/for at the target line with a single-statement block body
        var target = root.DescendantNodes().Where(n => n.GetLocation().GetLineSpan().StartLinePosition.Line + 1 == line).OfType<StatementSyntax>().FirstOrDefault(n => n is IfStatementSyntax || n is WhileStatementSyntax || n is ForStatementSyntax || n is ForEachStatementSyntax);
        if (target == null)
        {
            return root;
        }

        SyntaxNode replacement = target switch
        {
            IfStatementSyntax ifs when ifs.Statement is BlockSyntax blk && blk.Statements.Count == 1 => ifs.WithStatement(blk.Statements[0]),
            WhileStatementSyntax ws when ws.Statement is BlockSyntax blk && blk.Statements.Count == 1 => ws.WithStatement(blk.Statements[0]),
            ForStatementSyntax fs when fs.Statement is BlockSyntax blk && blk.Statements.Count == 1 => fs.WithStatement(blk.Statements[0]),
            ForEachStatementSyntax fes when fes.Statement is BlockSyntax blk && blk.Statements.Count == 1 => fes.WithStatement(blk.Statements[0]),
            _ => target
        };
        return root.ReplaceNode(target, replacement);
    }

    private static SyntaxNode ApplyExtractConstant(SyntaxNode root, int line)
    {
        var literal = root.DescendantNodes().Where(n => n.GetLocation().GetLineSpan().StartLinePosition.Line + 1 == line).OfType<LiteralExpressionSyntax>().FirstOrDefault(l => l.IsKind(SyntaxKind.StringLiteralExpression) || l.IsKind(SyntaxKind.NumericLiteralExpression));
        if (literal == null)
        {
            return root;
        }

        // Find enclosing class or struct to inject const field
        var enclosingType = literal.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault();
        if (enclosingType == null)
        {
            return root;
        }

        var typeName = literal.IsKind(SyntaxKind.StringLiteralExpression) ? "string" : "int";
        const string constName = "ExtractedConstant";
        var constDecl = SyntaxFactory.FieldDeclaration(SyntaxFactory.VariableDeclaration(SyntaxFactory.ParseTypeName(typeName), SyntaxFactory.SingletonSeparatedList(SyntaxFactory.VariableDeclarator(constName).WithInitializer(SyntaxFactory.EqualsValueClause(literal))))).WithModifiers(SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.PrivateKeyword), SyntaxFactory.Token(SyntaxKind.ConstKeyword)));
        var newRoot = root.ReplaceNode(literal, SyntaxFactory.IdentifierName(constName));
        var newType = newRoot.DescendantNodes().OfType<TypeDeclarationSyntax>().FirstOrDefault(t => t.Identifier.Text == enclosingType.Identifier.Text);
        if (newType == null)
        {
            return newRoot;
        }

        var updatedType = newType.WithMembers(newType.Members.Insert(0, constDecl));
        return newRoot.ReplaceNode(newType, updatedType);
    }

    public async Task<DocumentEditResult> InlineFieldAsync(FilePathWrapper filePath, string fieldName, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetSolutionAsync(ReadSource.Committed, cancellationToken);
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
        var field = root?.DescendantNodes().OfType<FieldDeclarationSyntax>().FirstOrDefault(f => f.Declaration.Variables.Any(v => v.Identifier.Text == fieldName));
        // If field not found or has no initializer, return error
        if (field == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.TargetNotFound,
                FilePath = filePath,
                Message = $"// ERROR: Field '{fieldName}' not found."
            };
        }

        if (field.Declaration.Variables[0].Initializer == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.TargetNotFound,
                FilePath = filePath,
                Message = $"// ERROR: Cannot inline field '{fieldName}' without initializer. Field must have a static initializer or initial assignment."
            };
        }

        var value = field.Declaration.Variables[0].Initializer!.Value;
        var usages = root!.DescendantNodes().OfType<IdentifierNameSyntax>().Where(i => i.Identifier.Text == fieldName).ToList();
        var trackedRoot = root.TrackNodes(new SyntaxNode[] { field });
        var usagesInTracked = trackedRoot.DescendantNodes().OfType<IdentifierNameSyntax>().Where(i => i.Identifier.Text == fieldName).ToList();
        var newRoot = trackedRoot.ReplaceNodes(usagesInTracked, (old, _) => value.WithTriviaFrom(old));
        var newField = newRoot.GetCurrentNode(field);
        if (newField != null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.Modified,
                FilePath = filePath,
                UpdatedText = await RemoveNodeFormattedAsync(document, newRoot, newField, SyntaxRemoveOptions.KeepUnbalancedDirectives, cancellationToken)
            };
        }

        return new DocumentEditResult
        {
            Outcome = EditOutcome.Modified,
            FilePath = filePath,
            UpdatedText = newRoot.ToFullString()
        };
    }

    public async Task<DocumentEditResult> InlineParameterAsync(FilePathWrapper filePath, string methodName, string parameterName, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetSolutionAsync(ReadSource.Committed, cancellationToken);
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
        var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
        var method = root?.DescendantNodes().OfType<MethodDeclarationSyntax>().FirstOrDefault(m => m.Identifier.Text == methodName);
        if (method == null || semanticModel == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.SourceInvalid,
                FilePath = filePath,
                Message = "// Source invalid."
            };
        }

        var parameter = method.ParameterList.Parameters.FirstOrDefault(p => p.Identifier.Text == parameterName);
        if (parameter == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.TargetNotFound,
                FilePath = filePath,
                Message = $"// ERROR: Parameter '{parameterName}' not found in method '{methodName}'."
            };
        }

        return new DocumentEditResult
        {
            Outcome = EditOutcome.Modified,
            FilePath = filePath,
            UpdatedText = root!.ToFullString()
        };
    }

    public async Task<DocumentEditResult> ConvertMethodToIndexerAsync(FilePathWrapper filePath, string methodName, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetSolutionAsync(ReadSource.Committed, cancellationToken);
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
        var method = root?.DescendantNodes().OfType<MethodDeclarationSyntax>().FirstOrDefault(m => m.Identifier.Text == methodName);
        if (method == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.TargetNotFound,
                FilePath = filePath,
                Message = $"// ERROR: Method '{methodName}' not found in {Path.GetFileName(filePath)}.\n" + (root?.ToFullString() ?? "")
            };
        }

        if (method.ParameterList.Parameters.Count != 1)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.TargetNotFound,
                FilePath = filePath,
                Message = $"// ERROR: Cannot convert '{methodName}' to an indexer - it must have exactly one parameter (has {method.ParameterList.Parameters.Count})."
            };
        }

        // C# does not support static indexers
        if (method.Modifiers.Any(m => m.IsKind(SyntaxKind.StaticKeyword)))
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.TargetNotFound,
                FilePath = filePath,
                Message = $"// ERROR: Cannot convert static method '{methodName}' to an indexer - C# does not support static indexers."
            };
        }

        // Build getter body from block body or expression body
        BlockSyntax getterBody;
        if (method.Body != null)
        {
            getterBody = method.Body;
        }
        else if (method.ExpressionBody != null)
        {
            getterBody = SyntaxFactory.Block(SyntaxFactory.ReturnStatement(method.ExpressionBody.Expression));
        }
        else
        {
            // Abstract/extern methods have no body -> cannot create indexer
            return new DocumentEditResult
            {
                Outcome = EditOutcome.TargetNotFound,
                FilePath = filePath,
                Message = $"// ERROR: Cannot convert '{methodName}' to an indexer - method has no body."
            };
        }

        // Exclude modifiers that are invalid on indexers (static, abstract, extern, etc.)
        var validModifiers = method.Modifiers.Where(m => !m.IsKind(SyntaxKind.StaticKeyword) && !m.IsKind(SyntaxKind.AbstractKeyword) && !m.IsKind(SyntaxKind.ExternKeyword)).ToArray();
        var parameter = method.ParameterList.Parameters[0];
        var indexer = SyntaxFactory.IndexerDeclaration(method.ReturnType).AddModifiers(validModifiers).WithParameterList(SyntaxFactory.BracketedParameterList(SyntaxFactory.SingletonSeparatedList(parameter))).WithAccessorList(SyntaxFactory.AccessorList(SyntaxFactory.SingletonList(SyntaxFactory.AccessorDeclaration(SyntaxKind.GetAccessorDeclaration).WithBody(getterBody))));
        return new DocumentEditResult
        {
            Outcome = EditOutcome.Modified,
            FilePath = filePath,
            UpdatedText = await RoslynFormattingHelper.ReplaceNodeFormattedAsync(document, root!, method, indexer, cancellationToken)
        };
    }

    public async Task<DocumentEditResult> IntroduceFieldAsync(FilePathWrapper filePath, string contextSnippet, string newFieldName, string? lineBefore = null, string? lineAfter = null, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetSolutionAsync(ReadSource.Committed, cancellationToken);
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
        if (root == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.SourceInvalid,
                FilePath = filePath,
                Message = "// Source invalid."
            };
        }

        var sourceText = await document.GetTextAsync(cancellationToken);
        var position = ContextHelper.FindSnippetPosition(sourceText, contextSnippet, lineBefore, lineAfter);
        var token = root.FindToken(position);
        var expression = token.Parent?.AncestorsAndSelf().OfType<ExpressionSyntax>().FirstOrDefault();
        if (expression == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.TargetNotFound,
                FilePath = filePath,
                Message = "// Expression not found.",
                UpdatedText = root.ToFullString()
            };
        }

        var containingClass = expression.Ancestors().OfType<ClassDeclarationSyntax>().FirstOrDefault();
        if (containingClass == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.TargetNotFound,
                FilePath = filePath,
                Message = "// Containing class not found."
            };
        }

        var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
        TypeSyntax fieldType;
        if (semanticModel != null)
        {
            var typeInfo = semanticModel.GetTypeInfo(expression, cancellationToken);
            fieldType = typeInfo.Type != null ? SyntaxFactory.ParseTypeName(typeInfo.Type.ToDisplayString()) : SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.ObjectKeyword));
        }
        else
        {
            fieldType = SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.ObjectKeyword));
        }

        // Check if expression is safe to use as a class field initializer
        // (doesn't reference method parameters or local variables)
        var isInitializerSafe = IsExpressionClassScopeSafe(expression, semanticModel, cancellationToken);
        var variableDeclarator = isInitializerSafe ? SyntaxFactory.VariableDeclarator(newFieldName).WithInitializer(SyntaxFactory.EqualsValueClause(expression.WithoutTrivia())) : SyntaxFactory.VariableDeclarator(newFieldName);
        var fieldDeclaration = SyntaxFactory.FieldDeclaration(SyntaxFactory.VariableDeclaration(fieldType).WithVariables(SyntaxFactory.SingletonSeparatedList(variableDeclarator))).WithModifiers(SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.PrivateKeyword), SyntaxFactory.Token(SyntaxKind.ReadOnlyKeyword)));
        var fieldRef = SyntaxFactory.IdentifierName(newFieldName).WithTriviaFrom(expression);
        var trackedRoot = root.TrackNodes(new SyntaxNode[] { expression, containingClass });
        var newRoot = trackedRoot.ReplaceNode(trackedRoot.GetCurrentNode(expression)!, fieldRef);
        var currentClass = newRoot.GetCurrentNode(containingClass)!;
        var newClass = currentClass.WithMembers(currentClass.Members.Insert(0, fieldDeclaration));
        return new DocumentEditResult
        {
            Outcome = EditOutcome.Modified,
            FilePath = filePath,
            UpdatedText = await RoslynFormattingHelper.ReplaceNodeFormattedAsync(document, newRoot, currentClass, newClass, cancellationToken)
        };
    }

    private static bool IsExpressionClassScopeSafe(ExpressionSyntax expression, SemanticModel? semanticModel, CancellationToken cancellationToken = default)
    {
        // Check all identifiers in the expression to see if they reference method parameters or local variables
        var identifiers = expression.DescendantNodes().OfType<IdentifierNameSyntax>();
        if (semanticModel == null)
        {
            // If no semantic model, be conservative and assume it's not safe
            return !identifiers.Any();
        }

        foreach (var identifier in identifiers)
        {
            try
            {
                var symbolInfo = semanticModel.GetSymbolInfo(identifier, cancellationToken);
                var symbol = symbolInfo.Symbol;
                // Check if this identifier refers to a method parameter or local variable
                if (symbol is IParameterSymbol or ILocalSymbol)
                {
                    return false;
                }
            }
            catch
            {
                // If we can't determine the symbol, be conservative
                return false;
            }
        }

        return true;
    }

    public async Task<DocumentEditResult> IntroduceParameterAsync(FilePathWrapper filePath, string contextSnippet, string newParamName, string? lineBefore = null, string? lineAfter = null, CancellationToken cancellationToken = default)
    {
        // NOTE: Single-file only -> call sites in other files are not updated.
        var solution = await _workspaceManager.GetSolutionAsync(ReadSource.Committed, cancellationToken);
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
        if (root == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.SourceInvalid,
                FilePath = filePath,
                Message = "// Source invalid."
            };
        }

        var sourceText = await document.GetTextAsync(cancellationToken);
        var position = ContextHelper.TryFindSnippetPosition(sourceText, contextSnippet, out var paramSnippetError, lineBefore, lineAfter);
        if (position < 0)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.TargetNotFound,
                FilePath = filePath,
                Message = $"// ErrorDetails: {paramSnippetError}"
            };
        }

        var token = root.FindToken(position);
        var expression = token.Parent?.AncestorsAndSelf().OfType<ExpressionSyntax>().FirstOrDefault();
        if (expression == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.TargetNotFound,
                FilePath = filePath,
                Message = "// Expression not found.",
                UpdatedText = root.ToFullString()
            };
        }

        var containingMethod = expression.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault();
        if (containingMethod == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.TargetNotFound,
                FilePath = filePath,
                Message = "// Containing method not found."
            };
        }

        var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
        TypeSyntax paramType;
        if (semanticModel != null)
        {
            var typeInfo = semanticModel.GetTypeInfo(expression, cancellationToken);
            paramType = typeInfo.Type != null ? SyntaxFactory.ParseTypeName(typeInfo.Type.ToDisplayString()) : SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.ObjectKeyword));
        }
        else
        {
            paramType = SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.ObjectKeyword));
        }

        var newParameter = SyntaxFactory.Parameter(SyntaxFactory.Identifier(newParamName)).WithType(paramType.WithTrailingTrivia(SyntaxFactory.Space));
        var paramRef = SyntaxFactory.IdentifierName(newParamName).WithTriviaFrom(expression);
        var trackedRoot = root.TrackNodes(new SyntaxNode[] { expression, containingMethod });
        var currentExpression = trackedRoot.GetCurrentNode(expression);
        if (currentExpression == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.TargetNotFound,
                FilePath = filePath,
                Message = "// Expression not found."
            };
        }

        var newRoot = trackedRoot.ReplaceNode(currentExpression, paramRef);
        var currentMethod = newRoot.GetCurrentNode(containingMethod);
        if (currentMethod == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.TargetNotFound,
                FilePath = filePath,
                Message = "// Containing method not found."
            };
        }

        var updatedMethod = currentMethod.WithParameterList(currentMethod.ParameterList.AddParameters(newParameter));
        return new DocumentEditResult
        {
            Outcome = EditOutcome.Modified,
            FilePath = filePath,
            UpdatedText = await RoslynFormattingHelper.ReplaceNodeFormattedAsync(document, newRoot, currentMethod, updatedMethod, cancellationToken)
        };
    }

    public async Task<DocumentEditResult> IntroduceVariableAsync(FilePathWrapper filePath, string contextSnippet, string newVariableName, string? lineBefore = null, string? lineAfter = null, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetSolutionAsync(ReadSource.Committed, cancellationToken);
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
        if (root == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.SourceInvalid,
                FilePath = filePath,
                Message = "// Source invalid."
            };
        }

        var sourceText = await document.GetTextAsync(cancellationToken);
        var position = ContextHelper.FindSnippetPosition(sourceText, contextSnippet, lineBefore, lineAfter);
        // Primary: find an expression whose span starts at position and whose text matches
        // the snippet -> handles compound expressions like "a + b".
        var trimmedSnippet = contextSnippet.Trim();
        var expression = root.DescendantNodes().OfType<ExpressionSyntax>().Where(e => e.SpanStart == position && e.ToString().Trim() == trimmedSnippet).FirstOrDefault() // Fallback: walk from the token at the position up to the first expression.
 ?? root.FindToken(position).Parent?.AncestorsAndSelf().OfType<ExpressionSyntax>().FirstOrDefault();
        if (expression == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.TargetNotFound,
                FilePath = filePath,
                Message = "// Expression not found."
            };
        }

        var containingStatement = expression.Ancestors().OfType<StatementSyntax>().FirstOrDefault();
        if (containingStatement == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.TargetNotFound,
                FilePath = filePath,
                Message = "// Containing statement not found."
            };
        }

        if (containingStatement.Parent is not BlockSyntax block)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.TargetNotFound,
                FilePath = filePath,
                Message = "// Containing block not found."
            };
        }

        // If the expression IS the entire initializer of an existing local var declaration, the
        // variable is already introduced -> extracting it would produce `var x = x;` (a duplicate).
        if (containingStatement is LocalDeclarationStatementSyntax existingDecl && existingDecl.Declaration.Variables.Count == 1 && existingDecl.Declaration.Variables[0].Initializer?.Value?.IsEquivalentTo(expression) == true)
        {
            var existingName = existingDecl.Declaration.Variables[0].Identifier.Text;
            return new DocumentEditResult
            {
                Outcome = EditOutcome.TargetNotFound,
                FilePath = filePath,
                Message = $"// '{existingName}' is already a local variable - nothing to introduce."
            };
        }

        var varDecl = SyntaxFactory.LocalDeclarationStatement(SyntaxFactory.VariableDeclaration(SyntaxFactory.IdentifierName("var")).WithVariables(SyntaxFactory.SingletonSeparatedList(SyntaxFactory.VariableDeclarator(newVariableName).WithInitializer(SyntaxFactory.EqualsValueClause(expression.WithoutTrivia())))));
        // If the extracted expression is the sole content of a parenthesized expression,
        // replace the outer parens too -> avoids spurious "(sum) * c" when extracting "a + b"
        // from "(a + b) * c". A bare identifier never needs parens (highest precedence).
        SyntaxNode nodeToReplace = expression;
        if (expression.Parent is ParenthesizedExpressionSyntax parenParent && parenParent.Expression == expression)
        {
            nodeToReplace = parenParent;
        }

        var varRef = SyntaxFactory.IdentifierName(newVariableName).WithTriviaFrom(nodeToReplace);
        var trackedRoot = root.TrackNodes(new SyntaxNode[] { nodeToReplace, containingStatement, block });
        var newRoot = trackedRoot.ReplaceNode(trackedRoot.GetCurrentNode(nodeToReplace)!, varRef);
        var currentStatement = newRoot.GetCurrentNode(containingStatement)!;
        var currentBlock = newRoot.GetCurrentNode(block)!;
        var idx = currentBlock.Statements.IndexOf(currentStatement);
        if (idx < 0)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.TargetNotFound,
                FilePath = filePath,
                Message = "// Statement not found in block."
            };
        }

        var newBlock = currentBlock.WithStatements(currentBlock.Statements.Insert(idx, varDecl));
        return new DocumentEditResult
        {
            Outcome = EditOutcome.Modified,
            FilePath = filePath,
            Message = "// Local variable introduced.",
            UpdatedText = await RoslynFormattingHelper.ReplaceNodeFormattedAsync(document, newRoot, currentBlock, newBlock, cancellationToken)
        };
    }

    public async Task<DocumentEditResult> MoveTypeToOuterScopeAsync(FilePathWrapper filePath, string nestedTypeName, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetSolutionAsync(ReadSource.Committed, cancellationToken);
        var document = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault();
        if (document == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.TargetNotFound,
                FilePath = filePath,
                Message = $"// ErrorDetails: File '{filePath}' not found."
            };
        }

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        if (root == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.TargetNotFound,
                FilePath = filePath,
                Message = $"// ErrorDetails: Failed to get syntax root."
            };
        }

        // Find the type
        var nestedType = root.DescendantNodes().OfType<TypeDeclarationSyntax>().FirstOrDefault(t => t.Identifier.Text == nestedTypeName);
        if (nestedType == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.TargetNotFound,
                FilePath = filePath,
                Message = $"// ErrorDetails: Type '{nestedTypeName}' not found."
            };
        }

        // Check if type is actually nested (parent is a type, not namespace/file scope)
        var parentType = nestedType.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault();
        if (parentType == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.TargetNotFound,
                FilePath = filePath,
                Message = $"// ErrorDetails: Type '{nestedTypeName}' is already at outer scope. Cannot move to outer scope."
            };
        }

        // Type is nested, move it out
        var newRoot = root.RemoveNode(nestedType, SyntaxRemoveOptions.KeepUnbalancedDirectives);
        if (newRoot == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.TargetNotFound,
                FilePath = filePath,
                Message = $"// ErrorDetails: Failed to remove nested type '{nestedTypeName}'."
            };
        }

        // Find the namespace or file scope and add the type there
        var ns = root.DescendantNodes().OfType<NamespaceDeclarationSyntax>().FirstOrDefault();
        var fileScopedNs = root.DescendantNodes().OfType<FileScopedNamespaceDeclarationSyntax>().FirstOrDefault();
        var annotation = new SyntaxAnnotation();
        var annotatedNestedType = nestedType.WithAdditionalAnnotations(annotation);
        if (ns != null)
        {
            var newNs = ns.AddMembers(annotatedNestedType);
            newRoot = newRoot!.ReplaceNode(ns, newNs);
        }
        else if (fileScopedNs != null)
        {
            var newFileScopedNs = fileScopedNs.AddMembers(annotatedNestedType);
            newRoot = newRoot!.ReplaceNode(fileScopedNs, newFileScopedNs);
        }
        else
        {
            // Add at compilation unit level
            var compilationUnit = root as CompilationUnitSyntax;
            if (compilationUnit != null)
            {
                newRoot = compilationUnit.AddMembers(annotatedNestedType);
            }
        }

        if (newRoot == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.Modified,
                FilePath = filePath,
                Message = "// Nested type moved to outer scope.",
                UpdatedText = root.ToFullString()
            };
        }

        var formattedDoc = await Formatter.FormatAsync(document.WithSyntaxRoot(newRoot), annotation, cancellationToken: cancellationToken);
        return new DocumentEditResult
        {
            Outcome = EditOutcome.Modified,
            FilePath = filePath,
            Message = "// Nested type moved to outer scope.",
            UpdatedText = (await formattedDoc.GetTextAsync(cancellationToken)).ToString()
        };
    }

    public async Task<Dictionary<FilePathWrapper, string>> ExtractMembersToPartialAsync(FilePathWrapper filePath, string className, string[] memberNames, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetSolutionAsync(ReadSource.Committed, cancellationToken);
        var document = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault();
        if (document == null)
        {
            return new Dictionary<FilePathWrapper, string>();
        }

        var root = await document.GetSyntaxRootAsync(cancellationToken) as CompilationUnitSyntax;
        var classNode = root?.DescendantNodes().OfType<ClassDeclarationSyntax>().FirstOrDefault(c => c.Identifier.Text == className);
        if (classNode != null)
        {
            var membersToMove = classNode.Members.Where(m => (m is MethodDeclarationSyntax meth && memberNames.Contains(meth.Identifier.Text)) || (m is PropertyDeclarationSyntax prop && memberNames.Contains(prop.Identifier.Text))).ToList();
            // Extract namespace
            var namespaceDeclOpt = root?.DescendantNodes().OfType<NamespaceDeclarationSyntax>().FirstOrDefault();
            var fileScopedNamespaceOpt = root?.DescendantNodes().OfType<FileScopedNamespaceDeclarationSyntax>().FirstOrDefault();
            var namespaceName = namespaceDeclOpt?.Name.ToString() ?? fileScopedNamespaceOpt?.Name.ToString();
            // Extract usings
            var usings = root?.ChildNodes().OfType<UsingDirectiveSyntax>().ToList() ?? new List<UsingDirectiveSyntax>();
            // Build new partial class
            var newClassNode = SyntaxFactory.ClassDeclaration(className).WithModifiers(SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.PublicKeyword), SyntaxFactory.Token(SyntaxKind.PartialKeyword))).WithMembers(SyntaxFactory.List(membersToMove));
            // Build new compilation unit with usings and namespace
            CompilationUnitSyntax newCompilationUnit = SyntaxFactory.CompilationUnit();
            if (usings.Count != 0)
            {
                newCompilationUnit = newCompilationUnit.WithUsings(SyntaxFactory.List(usings));
            }

            if (!string.IsNullOrEmpty(namespaceName))
            {
                var namespaceDecl = SyntaxFactory.NamespaceDeclaration(SyntaxFactory.ParseName(namespaceName)).WithMembers(SyntaxFactory.SingletonList<MemberDeclarationSyntax>(newClassNode));
                newCompilationUnit = newCompilationUnit.WithMembers(SyntaxFactory.SingletonList<MemberDeclarationSyntax>(namespaceDecl));
            }
            else
            {
                newCompilationUnit = newCompilationUnit.WithMembers(SyntaxFactory.SingletonList<MemberDeclarationSyntax>(newClassNode));
            }

            // Format with proper newlines
            var formattedCode = RoslynFormattingHelper.NormalizeWholeSubtreeWhitespace(newCompilationUnit).ToFullString();
            // Ensure proper spacing after usings before namespace
            if (usings.Count != 0 && !string.IsNullOrEmpty(namespaceName))
            {
                formattedCode = formattedCode.Replace(";namespace", ";\n\nnamespace");
            }

            return new Dictionary<FilePathWrapper, string>
            {
                {
                    Path.Combine(Path.GetDirectoryName(filePath)!, $"{className}.Partial.cs"),
                    formattedCode
                }
            };
        }

        return new Dictionary<FilePathWrapper, string>();
    }

    public async Task<DocumentEditResult> IntroduceParameterObjectAsync(FilePathWrapper filePath, string methodName, string? newTypeName = null, string[]? parameterNames = null, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetSolutionAsync(ReadSource.Committed, cancellationToken);
        var document = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault();
        if (document == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.TargetNotFound,
                FilePath = filePath,
                Message = "// File not found."
            };
        }

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        if (root == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.TargetNotFound,
                FilePath = filePath,
                Message = "// Could not parse file."
            };
        }

        var methodNode = root.DescendantNodes().OfType<MethodDeclarationSyntax>().FirstOrDefault(m => m.Identifier.Text == methodName);
        if (methodNode == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.TargetNotFound,
                FilePath = filePath,
                Message = "// Method not found."
            };
        }

        // Check if method implements an interface
        var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
        IMethodSymbol? methodSymbol = null;
        if (semanticModel != null)
        {
            methodSymbol = semanticModel.GetDeclaredSymbol(methodNode, cancellationToken);
        }

        var allParams = methodNode.ParameterList.Parameters.ToList();
        // Separate CancellationToken params from the rest
        var ctParams = allParams.Where(p => p.Type?.ToString().Contains("CancellationToken") == true).ToList();
        var candidateParams = allParams.Where(p => !ctParams.Contains(p)).ToList();
        // Determine which params to group
        List<ParameterSyntax> groupedParams;
        if (parameterNames != null && parameterNames.Length > 0)
        {
            var nameSet = new HashSet<string>(parameterNames, StringComparer.Ordinal);
            groupedParams = candidateParams.Where(p => nameSet.Contains(p.Identifier.Text)).ToList();
        }
        else
        {
            groupedParams = candidateParams;
        }

        if (groupedParams.Count == 0)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.TargetNotFound,
                FilePath = filePath,
                Message = "// No parameters to group."
            };
        }

        // Generate record name
        var recordName = newTypeName ?? (methodName + "Parameters");
        // Build record properties: capitalize first letter
        static string Capitalize(string s) => s.Length == 0 ? s : char.ToUpper(s[0]) + s.Substring(1);
        var recordParams = groupedParams.Select(p => SyntaxFactory.Parameter(SyntaxFactory.Identifier(Capitalize(p.Identifier.Text))).WithType(p.Type!));
        var recordDecl = SyntaxFactory.RecordDeclaration(SyntaxFactory.Token(SyntaxKind.RecordKeyword), SyntaxFactory.Identifier(recordName)).WithModifiers(SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.PublicKeyword))).WithParameterList(SyntaxFactory.ParameterList(SyntaxFactory.SeparatedList(recordParams))).WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken));
        // Build new parameter list: (RecordName request, [CT params])
        var requestParam = SyntaxFactory.Parameter(SyntaxFactory.Identifier("request")).WithType(SyntaxFactory.ParseTypeName(recordName + " "));
        var remainingParams = candidateParams.Where(p => !groupedParams.Contains(p)).ToList();
        var newParams = new List<ParameterSyntax>
        {
            requestParam
        };
        newParams.AddRange(remainingParams);
        newParams.AddRange(ctParams);
        var newParamList = methodNode.ParameterList.WithParameters(SyntaxFactory.SeparatedList(newParams));
        // Build param->property rewrite map: paramName -> request.ParamName
        var rewriteMap = groupedParams.ToDictionary(p => p.Identifier.Text, p => $"request.{Capitalize(p.Identifier.Text)}");
        // Add TODO comment to method
        var todoComment = SyntaxFactory.Comment($"// TODO: Update call sites to use {recordName}\r\n        ");
        // Rewrite references in method body
        SyntaxNode? newBody = null;
        if (methodNode.Body != null)
        {
            var rewriter = new ParamToPropertyRewriter(rewriteMap);
            newBody = rewriter.Visit(methodNode.Body);
        }

        var newMethodNode = methodNode.WithParameterList(newParamList);
        if (newBody is BlockSyntax block)
        {
            var firstStmt = block.Statements.FirstOrDefault();
            StatementSyntax todoStmt = SyntaxFactory.EmptyStatement().WithLeadingTrivia(SyntaxFactory.TriviaList(SyntaxFactory.Comment($"// TODO: Update call sites to use {recordName}"))).WithSemicolonToken(SyntaxFactory.MissingToken(SyntaxKind.SemicolonToken));
            var newStmts = block.Statements.Insert(0, todoStmt);
            newMethodNode = newMethodNode.WithBody(block.WithStatements(newStmts));
        }

        // If method is on an interface, also update interface definition
        var classNode = methodNode.Parent as ClassDeclarationSyntax;
        var interfaceNode = methodNode.Parent as InterfaceDeclarationSyntax;
        // Shared annotation for every node introduced/replaced below (the rewritten method and,
        // further down, the newly-appended record declaration) -> lets the final Formatter.FormatAsync
        // pass reformat just these edited spans instead of reflowing the whole file.
        var editAnnotation = new SyntaxAnnotation();
        var annotatedNewMethodNode = newMethodNode.WithAdditionalAnnotations(editAnnotation);
        SyntaxNode newRoot = root;
        if (interfaceNode != null && methodSymbol?.ExplicitInterfaceImplementations.Length == 0)
        {
            // Update interface method
            var newInterface = interfaceNode.ReplaceNode(methodNode, annotatedNewMethodNode);
            newRoot = root.ReplaceNode(interfaceNode, newInterface);
        }
        else if (classNode != null && methodSymbol?.ExplicitInterfaceImplementations.Length == 0)
        {
            // Check if method implements an interface
            if (methodSymbol?.ContainingType?.Interfaces.Length > 0)
            {
                // Method implements interface -> add warning but update the implementation
                newRoot = root.ReplaceNode(methodNode, annotatedNewMethodNode);
                // Append warning comment
                var warning = $"// WARNING: This method implements an interface. Update the interface signature in the corresponding interface file.\n";
                var warningFormattedDoc = await Formatter.FormatAsync(document.WithSyntaxRoot(newRoot), editAnnotation, cancellationToken: cancellationToken);
                return new DocumentEditResult
                {
                    Outcome = EditOutcome.Modified,
                    FilePath = filePath,
                    Message = warning,
                    UpdatedText = (await warningFormattedDoc.GetTextAsync(cancellationToken)).ToString()
                };
            }
            else
            {
                newRoot = root.ReplaceNode(methodNode, annotatedNewMethodNode);
            }
        }
        else
        {
            newRoot = root.ReplaceNode(methodNode, annotatedNewMethodNode);
        }

        // Append record declaration to end of file
        var compilationUnit = newRoot as CompilationUnitSyntax;
        if (compilationUnit != null)
        {
            var annotatedRecordDecl = recordDecl.WithAdditionalAnnotations(editAnnotation);
            // Find the namespace or use file-scoped namespace
            var nsNode = compilationUnit.Members.OfType<NamespaceDeclarationSyntax>().LastOrDefault();
            var fileScopeNs = compilationUnit.Members.OfType<FileScopedNamespaceDeclarationSyntax>().LastOrDefault();
            if (nsNode != null)
            {
                var newNs = nsNode.AddMembers(annotatedRecordDecl);
                newRoot = compilationUnit.ReplaceNode(nsNode, newNs);
            }
            else if (fileScopeNs != null)
            {
                var newNs = fileScopeNs.AddMembers(annotatedRecordDecl);
                newRoot = compilationUnit.ReplaceNode(fileScopeNs, newNs);
            }
            else
            {
                // Top-level: add after the last type declaration
                newRoot = compilationUnit.AddMembers(annotatedRecordDecl);
            }
        }

        var formattedDoc = await Formatter.FormatAsync(document.WithSyntaxRoot(newRoot), editAnnotation, cancellationToken: cancellationToken);
        return new DocumentEditResult
        {
            Outcome = EditOutcome.Modified,
            FilePath = filePath,
            Message = "// Local variable introduced.",
            UpdatedText = (await formattedDoc.GetTextAsync(cancellationToken)).ToString()
        };
    }

    private class ParamToPropertyRewriter : CSharpSyntaxRewriter
    {
        private readonly Dictionary<string, string> _map;
        public ParamToPropertyRewriter(Dictionary<string, string> map)
        {
            _map = map;
        }

        public override SyntaxNode? VisitIdentifierName(IdentifierNameSyntax node)
        {
            var name = node.Identifier.Text;
            if (_map.TryGetValue(name, out var replacement))
            {
                return SyntaxFactory.ParseExpression(replacement).WithTriviaFrom(node);
            }

            return base.VisitIdentifierName(node);
        }
    }

    private readonly SentinelConfiguration _config = new SentinelConfiguration();
    private readonly SymbolNavigationEngine _symbolNavigationEngine;
    public async Task<DocumentEditResult> ConvertExpressionBodyAsync(FilePathWrapper filePath, string memberName, string direction, string? contextSnippet = null, string? lineBefore = null, string? lineAfter = null, CancellationToken cancellationToken = default)
    {
        if (!_config.IsFeatureEnabled("ConvertExpressionBody"))
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.FeatureDisabled,
                FilePath = filePath,
                Message = "// Feature 'ConvertExpressionBody' is disabled."
            };
        }

        var solution = await _workspaceManager.GetSolutionAsync(ReadSource.Committed, cancellationToken);
        var document = solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.DocumentNotFound,
                FilePath = filePath,
                Message = "// Document not found."
            };
        }

        var root = (await document.GetSyntaxRootAsync(cancellationToken))!;
        var text = await document.GetTextAsync(cancellationToken);
        var candidates = _symbolNavigationEngine.ResolveCandidates(root, text, memberName, cancellationToken).Where(c => c.Kind is CandidateKind.Method or CandidateKind.Property or CandidateKind.Constructor).ToList();
        MemberDeclarationSyntax? target;
        try
        {
            target = _symbolNavigationEngine.ResolveBySnippetOrThrow(candidates, text, contextSnippet, lineBefore, lineAfter, (c, m, mode) => _symbolNavigationEngine.BuildMemberHint(c.Select(x => x.Node).ToList(), m, mode))?.Node as MemberDeclarationSyntax;
        }
        catch (InvalidOperationException ex)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.CannotEdit,
                FilePath = filePath,
                Message = ex.Message
            };
        }

        if (target == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.TargetNotFound,
                FilePath = filePath,
                Message = $"// Member '{memberName}' not found in '{Path.GetFileName(filePath)}'."
            };
        }

        SyntaxNode newTarget;
        if (direction == "ToExpressionBody")
        {
            if (target is MethodDeclarationSyntax meth && meth.Body != null)
            {
                var stmts = meth.Body.Statements;
                if (stmts.Count == 1 && stmts[0] is ReturnStatementSyntax ret && ret.Expression != null)
                {
                    newTarget = meth.WithBody(null).WithExpressionBody(SyntaxFactory.ArrowExpressionClause(ret.Expression)).WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken));
                }
                else
                {
                    return new DocumentEditResult
                    {
                        Outcome = EditOutcome.CannotConvert,
                        FilePath = filePath,
                        Message = $"// Cannot convert '{memberName}' to expression body: method body has {stmts.Count} statement(s); only single-return methods can be converted."
                    };
                }
            }
            else if (target is PropertyDeclarationSyntax prop && prop.AccessorList != null)
            {
                var getter = prop.AccessorList.Accessors.FirstOrDefault(a => a.IsKind(SyntaxKind.GetAccessorDeclaration));
                if (getter?.Body?.Statements.Count == 1 && getter.Body.Statements[0] is ReturnStatementSyntax pret && pret.Expression != null)
                {
                    newTarget = prop.WithAccessorList(null).WithExpressionBody(SyntaxFactory.ArrowExpressionClause(pret.Expression)).WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken));
                }
                else
                {
                    return new DocumentEditResult
                    {
                        Outcome = EditOutcome.CannotConvert,
                        FilePath = filePath,
                        Message = $"// Cannot convert '{memberName}' to expression body: property getter does not contain a simple return statement."
                    };
                }
            }
            else
            {
                return new DocumentEditResult
                {
                    Outcome = EditOutcome.CannotConvert,
                    FilePath = filePath,
                    Message = $"// Cannot convert '{memberName}' to expression body: member has no block body or is already an expression body."
                };
            }
        }
        else // ToBlockBody
        {
            if (target is MethodDeclarationSyntax methExpr && methExpr.ExpressionBody != null)
            {
                var returnType = methExpr.ReturnType.ToString().Trim();
                StatementSyntax stmt = returnType == "void" ? SyntaxFactory.ExpressionStatement(methExpr.ExpressionBody.Expression) : (StatementSyntax)SyntaxFactory.ReturnStatement(methExpr.ExpressionBody.Expression);
                newTarget = methExpr.WithExpressionBody(null).WithSemicolonToken(default).WithBody(SyntaxFactory.Block(stmt));
            }
            else if (target is PropertyDeclarationSyntax propExpr && propExpr.ExpressionBody != null)
            {
                var getter = SyntaxFactory.AccessorDeclaration(SyntaxKind.GetAccessorDeclaration).WithBody(SyntaxFactory.Block(SyntaxFactory.ReturnStatement(propExpr.ExpressionBody.Expression)));
                newTarget = propExpr.WithExpressionBody(null).WithSemicolonToken(default).WithAccessorList(SyntaxFactory.AccessorList(SyntaxFactory.SingletonList(getter)));
            }
            else
            {
                return new DocumentEditResult
                {
                    Outcome = EditOutcome.CannotConvert,
                    FilePath = filePath,
                    Message = $"// Cannot convert '{memberName}' to block body: member has no expression body (already a block body or not a method/property)."
                };
            }
        }

        var newRoot = root.ReplaceNode(target, RoslynFormattingHelper.NormalizeWholeSubtreeWhitespace(newTarget));
        var doc = document.WithSyntaxRoot(newRoot);
        var formatted = await Formatter.FormatAsync(doc, null, cancellationToken);
        return new DocumentEditResult
        {
            Outcome = EditOutcome.Modified,
            FilePath = filePath,
            UpdatedText = (await formatted.GetTextAsync(cancellationToken)).ToString()
        };
    }

    /// <summary>
    /// Inlines a simple method (expression-body or single-return-statement) by replacing ALL call sites
    /// solution-wide with the method's expression, then removing the method declaration.
    /// Returns a dictionary of filePath->updatedContent for every affected file.
    /// </summary>
    public async Task<Dictionary<FilePathWrapper, string>> InlineMethodAsync(FilePathWrapper filePath, string methodName, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetSolutionAsync(ReadSource.Committed, cancellationToken);
        var document = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault();
        if (document == null)
        {
            throw new ToolNotFoundException($"File '{Path.GetFileName(filePath)}' not found in solution.");
        }

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        if (root == null)
        {
            throw new ToolNotFoundException($"Failed to get syntax root for '{filePath}'.");
        }

        var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
        var method = root.DescendantNodes().OfType<MethodDeclarationSyntax>().FirstOrDefault(m => m.Identifier.Text == methodName);
        if (method == null || semanticModel == null)
        {
            throw new ToolNotFoundException($"Method '{methodName}' not found in '{filePath}'.");
        }

        ExpressionSyntax? expressionToInline = null;
        if (method.ExpressionBody != null)
        {
            expressionToInline = method.ExpressionBody.Expression;
        }
        else if (method.Body?.Statements.Count == 1 && method.Body.Statements[0] is ReturnStatementSyntax ret)
        {
            expressionToInline = ret.Expression;
        }

        if (expressionToInline == null)
        {
            throw new ToolNotFoundException($"Cannot inline '{methodName}': only expression-body or single-return-statement methods are supported. This method has a complex body with multiple statements.");
        }

        var methodSymbol = semanticModel.GetDeclaredSymbol(method, cancellationToken);
        if (methodSymbol == null)
        {
            throw new ToolNotFoundException($"Cannot inline '{methodName}': failed to resolve semantic symbol.");
        }

        // Find ALL references across the solution grouped by document
        var references = await SymbolFinder.FindReferencesAsync(methodSymbol, solution, cancellationToken);
        var byDocument = references.SelectMany(r => r.Locations).Where(l => l.Document.FilePath != null).GroupBy(l => l.Document.Id).ToDictionary(g => g.Key, g => g.ToList());
        var result = new Dictionary<FilePathWrapper, string>();
        var expressionTemplate = expressionToInline; // capture once
        // Process each document that has call sites (including the defining document)
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

            var callSiteNodes = new List<InvocationExpressionSyntax>();
            foreach (var location in locations)
            {
                var node = docRoot.FindNode(location.Location.SourceSpan).AncestorsAndSelf().OfType<InvocationExpressionSyntax>().FirstOrDefault();
                if (node != null)
                {
                    callSiteNodes.Add(node);
                }
            }

            if (callSiteNodes.Count == 0)
            {
                continue;
            }

            var updatedDocRoot = docRoot.ReplaceNodes(callSiteNodes, (original, _) => expressionTemplate.WithTriviaFrom(original));
            result[doc.FilePath] = RoslynFormattingHelper.NormalizeWholeSubtreeWhitespace(updatedDocRoot).ToFullString();
        }

        // Remove the method declaration from the defining document
        var definingFilePath = document.FilePath!;
        var definingRoot = result.TryGetValue(definingFilePath, out var already) ? Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(already, cancellationToken: cancellationToken).GetRoot(cancellationToken) : root;
        var methodToRemove = definingRoot.DescendantNodes().OfType<MethodDeclarationSyntax>().FirstOrDefault(m => m.Identifier.Text == methodName);
        if (methodToRemove != null)
        {
            var withoutMethod = definingRoot.RemoveNode(methodToRemove, SyntaxRemoveOptions.KeepUnbalancedDirectives);
            result[definingFilePath] = (withoutMethod is null ? null : RoslynFormattingHelper.NormalizeWholeSubtreeWhitespace(withoutMethod).ToFullString()) ?? definingRoot.ToFullString();
        }
        else if (!result.ContainsKey(definingFilePath))
        {
            // Method had no callers but still needs the declaration removed
            var withoutMethod = root.RemoveNode(method, SyntaxRemoveOptions.KeepUnbalancedDirectives);
            result[definingFilePath] = (withoutMethod is null ? null : RoslynFormattingHelper.NormalizeWholeSubtreeWhitespace(withoutMethod).ToFullString()) ?? root.ToFullString();
        }

        return result;
    }

    public async Task<Dictionary<FilePathWrapper, string>> ConvertTupleToClassAsync(FilePathWrapper filePath, string methodName, string newClassName, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetSolutionAsync(ReadSource.Committed, cancellationToken);
        var document = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault() ?? throw new FileNotFoundException($"File not found: {filePath}");
        var root = await document.GetSyntaxRootAsync(cancellationToken) as CompilationUnitSyntax;
        var methodNode = (root?.DescendantNodes().OfType<MethodDeclarationSyntax>().FirstOrDefault(m => m.Identifier.Text == methodName)) ?? throw new InvalidOperationException("Method not found.");
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

            var prop = SyntaxFactory.PropertyDeclaration(element.Type, char.ToUpper(name[0]) + name.Substring(1)).AddModifiers(SyntaxFactory.Token(SyntaxKind.PublicKeyword)).AddAccessorListAccessors(SyntaxFactory.AccessorDeclaration(SyntaxKind.GetAccessorDeclaration).WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken)), SyntaxFactory.AccessorDeclaration(SyntaxKind.SetAccessorDeclaration).WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken)));
            properties.Add(prop);
        }

        var newClass = SyntaxFactory.ClassDeclaration(newClassName).AddModifiers(SyntaxFactory.Token(SyntaxKind.PublicKeyword)).AddMembers(properties.ToArray());
        var ns = methodNode.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault();
        var classRoot = SyntaxFactory.CompilationUnit().WithUsings(root!.Usings);
        if (ns != null)
        {
            var newNs = ns is FileScopedNamespaceDeclarationSyntax ? SyntaxFactory.FileScopedNamespaceDeclaration(ns.Name) : (BaseNamespaceDeclarationSyntax)SyntaxFactory.NamespaceDeclaration(ns.Name);
            classRoot = classRoot.AddMembers(newNs.AddMembers(newClass));
        }
        else
        {
            classRoot = classRoot.AddMembers(newClass);
        }

        var newMethodNode = methodNode.WithReturnType(SyntaxFactory.ParseTypeName(newClassName));
        var updatedRoot = root.ReplaceNode(methodNode, newMethodNode);
        return new Dictionary<FilePathWrapper, string>
        {
            {
                filePath,
                RoslynFormattingHelper.NormalizeWholeSubtreeWhitespace(updatedRoot).ToFullString()
            },
            {
                Path.Combine(Path.GetDirectoryName(filePath)!, $"{newClassName}.cs"),
                RoslynFormattingHelper.NormalizeWholeSubtreeWhitespace(classRoot).ToFullString()
            }
        };
    }

    public async Task<Dictionary<FilePathWrapper, string>> ChangePropertyTypeAsync(FilePathWrapper filePath, string className, string propertyName, string newType, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetSolutionAsync(ReadSource.Committed, cancellationToken);
        var document = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault() ?? throw new FileNotFoundException($"File not found: {filePath}");
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
        var changes = new Dictionary<FilePathWrapper, string>();
        foreach (var docId in updatedSolution.GetChanges(solution).GetProjectChanges().SelectMany(pc => pc.GetChangedDocuments()))
        {
            var doc = updatedSolution.GetDocument(docId)!;
            var text = await doc.GetTextAsync(cancellationToken);
            changes[doc.FilePath!] = text.ToString();
        }

        return changes;
    }

    public async Task<Dictionary<FilePathWrapper, string>> ConvertAnonymousToNamedAsync(FilePathWrapper filePath, string newClassName, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetSolutionAsync(ReadSource.Committed, cancellationToken);
        var document = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault() ?? throw new FileNotFoundException($"File not found: {filePath}");
        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var anonType = root?.DescendantNodes().OfType<AnonymousObjectCreationExpressionSyntax>().FirstOrDefault();
        if (anonType != null)
        {
            var properties = anonType.Initializers.Select(init =>
            {
                var name = init.NameEquals?.Name.Identifier.Text ?? "Prop";
                return SyntaxFactory.PropertyDeclaration(SyntaxFactory.ParseTypeName("object"), char.ToUpper(name[0]) + name.Substring(1)).AddModifiers(SyntaxFactory.Token(SyntaxKind.PublicKeyword)).AddAccessorListAccessors(SyntaxFactory.AccessorDeclaration(SyntaxKind.GetAccessorDeclaration).WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken)), SyntaxFactory.AccessorDeclaration(SyntaxKind.SetAccessorDeclaration).WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken)));
            });
            var newClass = SyntaxFactory.ClassDeclaration(newClassName).AddModifiers(SyntaxFactory.Token(SyntaxKind.PublicKeyword)).AddMembers(properties.ToArray());
            return new Dictionary<FilePathWrapper, string>
            {
                {
                    Path.Combine(Path.GetDirectoryName(filePath)!, $"{newClassName}.cs"),
                    RoslynFormattingHelper.NormalizeWholeSubtreeWhitespace(newClass).ToFullString()
                }
            };
        }

        throw new InvalidOperationException("Anonymous type not found.");
    }
}