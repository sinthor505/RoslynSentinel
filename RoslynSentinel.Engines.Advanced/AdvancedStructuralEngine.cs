using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Formatting;

using RoslynSentinel.Engines.Basic;
using RoslynSentinel.Common.SnippetMatching;
// Deliberate exception to the explicit RoslynFormattingHelper.X call style: the shared FormatAnnotatedTextAsync
// (whitespace slice D2) replaced a private copy this file calls many times; the static import keeps those call sites.
using static RoslynSentinel.Common.RoslynFormattingHelper;

namespace RoslynSentinel.Engines.Advanced;

public class StructuralRefactoringEngine
{
    private readonly IWorkspaceManager _workspaceManager;
    private readonly ValidationEngine? _validationEngine;
    public StructuralRefactoringEngine(IWorkspaceManager workspaceManager, ValidationEngine? validationEngine = null)
    {
        _workspaceManager = workspaceManager;
        _validationEngine = validationEngine;
    }

    /// <summary>
    /// Returns <paramref name="leading"/> with a line break in front, so a member that follows another member is separated
    /// from it by a blank line; leading trivia that already starts with a line break is returned unchanged.
    /// </summary>
    private static SyntaxTriviaList WithBlankLineSeparator(SyntaxTriviaList leading) =>
        leading.Count > 0 && leading[0].IsKind(SyntaxKind.EndOfLineTrivia)
            ? leading
            : leading.Insert(0, SyntaxFactory.CarriageReturnLineFeed);

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
            // Each member keeps its own outer trivia (blank lines, comments, line break), which dropping the modifiers
            // would otherwise take with it. The interface is a new node: it takes the class's outer trivia and is
            // formatted whole.
            var interfaceMembers = classNode.Members.OfType<MethodDeclarationSyntax>().Select(m => m.WithBody(null).WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken)).WithModifiers(SyntaxFactory.TokenList()).WithTriviaFrom(m));
            var interfaceNode = SyntaxFactory.InterfaceDeclaration($"I{className}").WithModifiers(SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.PublicKeyword))).WithMembers(SyntaxFactory.List<MemberDeclarationSyntax>(interfaceMembers))
                .WithTriviaFrom(classNode)
                .WithAdditionalAnnotations(Formatter.Annotation);
            var newRoot = root!.ReplaceNode(classNode, interfaceNode);
            return new DocumentEditResult
            {
                Outcome = EditOutcome.Modified,
                UpdatedText = await FormatAnnotatedTextAsync(document, newRoot, cancellationToken),
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
            // The new modifier takes over the leading trivia of the first modifier it replaces (or of the identifier when
            // there was none), so the constructor's indentation and comments stay in place.
            var ctorFirstToken = constructor.Modifiers.Count > 0 ? constructor.Modifiers[0] : constructor.Identifier;
            var privateToken = SyntaxFactory.Token(ctorFirstToken.LeadingTrivia, SyntaxKind.PrivateKeyword, SyntaxFactory.TriviaList(SyntaxFactory.Space));
            var ctorWithoutLeadingTrivia = constructor.Modifiers.Count > 0 ? constructor : constructor.WithIdentifier(constructor.Identifier.WithLeadingTrivia());
            var privateCtor = ctorWithoutLeadingTrivia.WithModifiers(SyntaxFactory.TokenList(privateToken));
            // The factory is a whole new member: a blank line before it, a line break after it, and the formatter annotation.
            var annotatedFactory = factoryMethod
                .WithLeadingTrivia(SyntaxFactory.CarriageReturnLineFeed)
                .WithTrailingTrivia(SyntaxFactory.CarriageReturnLineFeed)
                .WithAdditionalAnnotations(Formatter.Annotation);
            var newClass = classNode.ReplaceNode(constructor, privateCtor).AddMembers(annotatedFactory);
            var newRoot = root!.ReplaceNode(classNode, newClass);
            return new DocumentEditResult
            {
                Outcome = EditOutcome.Modified,
                UpdatedText = await FormatAnnotatedTextAsync(document, newRoot, cancellationToken),
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

        // A new file that embeds existing code: only the synthesized structure is formatted; the usings and the copied
        // properties keep their comments, blank lines and spacing.
        changes[Path.Combine(Path.GetDirectoryName(firstFile)!, $"{newBaseClassName}.cs")] = await RoslynFormattingHelper.FormatNewRootKeepingTriviaAsync(document, baseUnit, n => n is not (UsingDirectiveSyntax or PropertyDeclarationSyntax), cancellationToken);
        return changes;
    }

    /// <summary>
    /// The members of an inlined class, ready to append to <paramref name="targetClass"/>: each is new in the target and
    /// carries <see cref="Formatter.Annotation"/> (it may sit at another nesting depth there), and the first one is
    /// separated from the target's existing members by a blank line.
    /// </summary>
    private static MemberDeclarationSyntax[] PrepareInlinedMembers(ClassDeclarationSyntax targetClass, SyntaxList<MemberDeclarationSyntax> members) =>
        members.Select((m, i) => (i == 0 && targetClass.Members.Count > 0 ? m.WithLeadingTrivia(WithBlankLineSeparator(m.GetLeadingTrivia())) : m)
            .WithAdditionalAnnotations(Formatter.Annotation)).ToArray();

    /// <summary>
    /// Removal options for a whole declaration: when its outer trivia is only whitespace and line breaks it is removed with
    /// that trivia (so the blank line in front of it does not pile up on its neighbours); when it carries comments or
    /// directives they are kept, as before.
    /// </summary>
    private static SyntaxRemoveOptions RemoveOptionsKeepingComments(SyntaxNode node) =>
        node.GetLeadingTrivia().Concat(node.GetTrailingTrivia()).All(t => t.IsKind(SyntaxKind.WhitespaceTrivia) || t.IsKind(SyntaxKind.EndOfLineTrivia))
            ? SyntaxRemoveOptions.KeepNoTrivia
            : SyntaxRemoveOptions.KeepExteriorTrivia;

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
            var expandedTarget = targetClass.AddMembers(PrepareInlinedMembers(targetClass, membersToInline));
            var intermediate = (CompilationUnitSyntax)sourceRoot.ReplaceNode(targetClass, expandedTarget);
            var classToRemove = intermediate.DescendantNodes().OfType<ClassDeclarationSyntax>().FirstOrDefault(c => c.Identifier.Text == className);
            var newRoot = classToRemove != null ? (CompilationUnitSyntax)intermediate.RemoveNode(classToRemove, RemoveOptionsKeepingComments(classToRemove))! : intermediate;
            result[sourceFilePath] = await FormatAnnotatedTextAsync(sourceDoc, newRoot, cancellationToken);
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
            var expandedTarget = targetClass.AddMembers(PrepareInlinedMembers(targetClass, membersToInline));
            var newTargetRoot = (CompilationUnitSyntax)targetRoot!.ReplaceNode(targetClass, expandedTarget);
            var newSourceRoot = (CompilationUnitSyntax)sourceRoot.RemoveNode(sourceClass, RemoveOptionsKeepingComments(sourceClass))!;
            result[targetFilePath] = await FormatAnnotatedTextAsync(targetDoc, newTargetRoot, cancellationToken);
            result[sourceFilePath] = await FormatAnnotatedTextAsync(sourceDoc, newSourceRoot, cancellationToken);
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
                    return gen.WithIdentifier(SyntaxFactory.Identifier(newName).WithTriviaFrom(gen.Identifier));
                }

                return original;
            });
            // A token-for-token rename in which every new token keeps the old one's trivia: the text needs no formatting.
            result[doc.FilePath] = updatedRoot.ToFullString();
        }
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
            _ => throw new ToolInvalidArgumentException($"Unknown micro-refactoring '{refactoringId}'. " + "Known IDs: type-to-var, remove-unused-local, add-braces, remove-braces, extract-constant.")
        };
        return new DocumentEditResult
        {
            Outcome = EditOutcome.Modified,
            FilePath = filePath,
            // The Apply* helpers annotate only the nodes they create; a refactoring that finds nothing returns the text as is.
            UpdatedText = await FormatAnnotatedTextAsync(document, newRoot ?? root, cancellationToken)
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

    /// <summary>
    /// Wraps a braceless body in a block annotated with <see cref="Formatter.Annotation"/>, which places the braces. The
    /// body's trailing trivia (normally the line break) moves to the closing brace, so the next statement keeps its line.
    /// </summary>
    private static BlockSyntax BlockAround(StatementSyntax body) =>
        SyntaxFactory.Block(body.WithoutTrailingTrivia())
            .WithTrailingTrivia(body.GetTrailingTrivia())
            .WithAdditionalAnnotations(Formatter.Annotation);

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
            IfStatementSyntax ifs when ifs.Statement is not BlockSyntax => ifs.WithStatement(BlockAround(ifs.Statement)),
            WhileStatementSyntax ws when ws.Statement is not BlockSyntax => ws.WithStatement(BlockAround(ws.Statement)),
            ForStatementSyntax fs when fs.Statement is not BlockSyntax => fs.WithStatement(BlockAround(fs.Statement)),
            ForEachStatementSyntax fes when fes.Statement is not BlockSyntax => fes.WithStatement(BlockAround(fes.Statement)),
            _ => target
        };
        return root.ReplaceNode(target, replacement);
    }

    /// <summary>
    /// The single statement of <paramref name="block"/>, standing in for the block: it takes the closing brace's trailing
    /// trivia (normally the line break) so the next statement keeps its line, and carries <see cref="Formatter.Annotation"/>
    /// so it is indented as an embedded statement.
    /// </summary>
    private static StatementSyntax UnwrapBlock(BlockSyntax block) =>
        block.Statements[0]
            .WithTrailingTrivia(block.GetTrailingTrivia())
            .WithAdditionalAnnotations(Formatter.Annotation);

    /// <summary>
    /// Removes a member declaration with its own trivia (its comments go with it; unbalanced preprocessor directives
    /// stay). When it was the first member of its type, the blank line that separated it from the next member would end up
    /// directly after the opening brace, so that blank line is dropped as well. Everything else keeps its text.
    /// </summary>
    private static SyntaxNode RemoveMemberDeclaration(SyntaxNode root, MemberDeclarationSyntax member)
    {
        if (member.Parent is TypeDeclarationSyntax type && type.Members.Count > 1 && type.Members[0] == member)
        {
            var withoutMember = type.RemoveNode(member, SyntaxRemoveOptions.KeepUnbalancedDirectives)!;
            var first = withoutMember.Members[0];
            var leading = first.GetLeadingTrivia();
            var index = 0;
            while (index < leading.Count && leading[index].IsKind(SyntaxKind.WhitespaceTrivia))
            {
                index++;
            }

            if (index < leading.Count && leading[index].IsKind(SyntaxKind.EndOfLineTrivia))
            {
                withoutMember = withoutMember.ReplaceNode(first, first.WithLeadingTrivia(leading.Skip(index + 1)));
            }

            return root.ReplaceNode(type, withoutMember);
        }

        return root.RemoveNode(member, SyntaxRemoveOptions.KeepUnbalancedDirectives) ?? root;
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
            IfStatementSyntax ifs when ifs.Statement is BlockSyntax blk && blk.Statements.Count == 1 => ifs.WithStatement(UnwrapBlock(blk)),
            WhileStatementSyntax ws when ws.Statement is BlockSyntax blk && blk.Statements.Count == 1 => ws.WithStatement(UnwrapBlock(blk)),
            ForStatementSyntax fs when fs.Statement is BlockSyntax blk && blk.Statements.Count == 1 => fs.WithStatement(UnwrapBlock(blk)),
            ForEachStatementSyntax fes when fes.Statement is BlockSyntax blk && blk.Statements.Count == 1 => fes.WithStatement(UnwrapBlock(blk)),
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
        var newRoot = root.ReplaceNode(literal, SyntaxFactory.IdentifierName(constName).WithTriviaFrom(literal));
        var newType = newRoot.DescendantNodes().OfType<TypeDeclarationSyntax>().FirstOrDefault(t => t.Identifier.Text == enclosingType.Identifier.Text);
        if (newType == null)
        {
            return newRoot;
        }

        // The constant is a new first member: a line break after it, the formatter annotation, and a blank line between it
        // and the member that used to come first.
        var members = newType.Members;
        if (members.Count > 0)
        {
            members = members.Replace(members[0], members[0].WithLeadingTrivia(WithBlankLineSeparator(members[0].GetLeadingTrivia())));
        }

        var annotatedConst = constDecl.WithTrailingTrivia(SyntaxFactory.CarriageReturnLineFeed).WithAdditionalAnnotations(Formatter.Annotation);
        var updatedType = newType.WithMembers(members.Insert(0, annotatedConst));
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
                UpdatedText = await RemoveNodeAndFormatContainerAsync(document, newRoot, newField, SyntaxRemoveOptions.KeepUnbalancedDirectives, cancellationToken)
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

        // TODO implement parameter inlining (bind the constant argument into the body and drop the parameter plus every call-site argument); until then refuse
        throw new ToolNotImplementedException($"Inline/parameter is not implemented: parameter '{parameterName}' of '{methodName}' was found but the tool cannot inline it yet; the file was not changed.");
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
            UpdatedText = await RoslynFormattingHelper.ReplaceNodeAndFormatReplacementAsync(document, root!, method, indexer, cancellationToken)
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
        var position = SnippetLookup.ResolveOrThrow(sourceText, contextSnippet, lineBefore, lineAfter).Start;
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
            UpdatedText = await RoslynFormattingHelper.ReplaceNodeAndFormatReplacementAsync(document, newRoot, currentClass, newClass, cancellationToken)
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
        var position = SnippetLookup.TryResolve(sourceText, contextSnippet, lineBefore, lineAfter, out var paramMatch, out var paramSnippetError) ? paramMatch.Start : -1;
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
            UpdatedText = await RoslynFormattingHelper.ReplaceNodeAndFormatReplacementAsync(document, newRoot, currentMethod, updatedMethod, cancellationToken)
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
        var position = SnippetLookup.ResolveOrThrow(sourceText, contextSnippet, lineBefore, lineAfter).Start;
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
            UpdatedText = await RoslynFormattingHelper.ReplaceNodeAndFormatReplacementAsync(document, newRoot, currentBlock, newBlock, cancellationToken)
        };
    }

    public async Task<DocumentEditResult> MoveTypeToOuterScopeAsync(FilePathWrapper filePath, string nestedTypeName, bool allowWidenToInternal = false, CancellationToken cancellationToken = default)
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

        // A private or protected nested type cannot be declared at namespace level (CS1527). It is refused by default;
        // with allowWidenToInternal it is widened to internal. A type with no explicit accessibility is left as it is.
        var accessModifiers = nestedType.Modifiers.Where(m => m.IsKind(SyntaxKind.PrivateKeyword) || m.IsKind(SyntaxKind.ProtectedKeyword)).ToList();
        var typeToReAdd = nestedType;
        string? widenedFrom = null;
        if (accessModifiers.Count > 0)
        {
            var originalModifiers = string.Join(" ", accessModifiers.Select(m => m.ValueText));
            if (!allowWidenToInternal)
            {
                return new DocumentEditResult
                {
                    Outcome = EditOutcome.CannotMove,
                    FilePath = filePath,
                    ErrorCode = ToolErrorCode.TargetIneligible,
                    Message = $"// ErrorDetails: Nested type '{nestedTypeName}' is declared '{originalModifiers}'; moving it to namespace level would not compile (CS1527). Retry with allowWidenToInternal: true to widen it to 'internal'."
                };
            }

            // The first access modifier's trivia is kept so the declaration keeps its indentation; the others are dropped.
            // protected internal already carries internal, so no second one is added.
            var needsInternal = !nestedType.Modifiers.Any(m => m.IsKind(SyntaxKind.InternalKeyword));
            var widenedModifiers = new List<SyntaxToken>();
            foreach (var modifier in nestedType.Modifiers)
            {
                if (!accessModifiers.Contains(modifier))
                {
                    widenedModifiers.Add(modifier);
                }
                else if (needsInternal)
                {
                    widenedModifiers.Add(SyntaxFactory.Token(modifier.LeadingTrivia, SyntaxKind.InternalKeyword, modifier.TrailingTrivia));
                    needsInternal = false;
                }
            }
            typeToReAdd = nestedType.WithModifiers(SyntaxFactory.TokenList(widenedModifiers));
            widenedFrom = originalModifiers;
        }

        // Type is nested, move it out: to the nearest enclosing namespace, or the compilation unit when there is none.
        // The removal and the re-add must act on the same tree. The container and the type are tracked in the ORIGINAL
        // root so the container can be found again after the removal (a node taken from the original root is not in the
        // trimmed tree, so replacing it there would silently do nothing and the type would be lost).
        SyntaxNode container = nestedType.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault() ?? (SyntaxNode)root;
        var trackedRoot = root.TrackNodes(nestedType, container);
        var trackedNestedType = trackedRoot.GetCurrentNode(nestedType);
        var newRoot = trackedNestedType == null
            ? null
            : trackedRoot.RemoveNode(trackedNestedType, SyntaxRemoveOptions.KeepUnbalancedDirectives);
        var currentContainer = newRoot?.GetCurrentNode(container);
        if (newRoot == null || currentContainer == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.TargetNotFound,
                FilePath = filePath,
                Message = $"// ErrorDetails: Failed to remove nested type '{nestedTypeName}'."
            };
        }

        // Only the re-added type is annotated for formatting.
        var annotatedNestedType = typeToReAdd.WithAdditionalAnnotations(Formatter.Annotation);
        SyntaxNode newContainer = currentContainer switch
        {
            BaseNamespaceDeclarationSyntax baseNs => baseNs.AddMembers(annotatedNestedType),
            CompilationUnitSyntax unit => unit.AddMembers(annotatedNestedType),
            // Intentional deviation: the container is always a namespace or the compilation unit (see above), so this arm is unreachable.
            _ => throw new InvalidOperationException("Unexpected container kind: " + currentContainer.Kind()),
        };

        newRoot = newRoot.ReplaceNode(currentContainer, newContainer);

        var formattedDoc = await RoslynFormattingHelper.FormatAnnotatedSpansAsync(document, newRoot, cancellationToken);
        return new DocumentEditResult
        {
            Outcome = EditOutcome.Modified,
            FilePath = filePath,
            Message = widenedFrom is null ? "// Nested type moved to outer scope." : $"// Nested type moved to outer scope (widened '{widenedFrom}' to 'internal').",
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
            var membersToMove = SelectMembersToMove(classNode, memberNames);
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
                // The namespace kind follows the source file: the moved members keep their own indentation, which only
                // fits the new file when the class sits at the same nesting depth as in the source.
                var namespaceDecl = fileScopedNamespaceOpt != null && namespaceDeclOpt == null
                    ? (BaseNamespaceDeclarationSyntax)SyntaxFactory.FileScopedNamespaceDeclaration(SyntaxFactory.ParseName(namespaceName)).WithMembers(SyntaxFactory.SingletonList<MemberDeclarationSyntax>(newClassNode))
                    : SyntaxFactory.NamespaceDeclaration(SyntaxFactory.ParseName(namespaceName)).WithMembers(SyntaxFactory.SingletonList<MemberDeclarationSyntax>(newClassNode));
                newCompilationUnit = newCompilationUnit.WithMembers(SyntaxFactory.SingletonList<MemberDeclarationSyntax>(namespaceDecl));
            }
            else
            {
                newCompilationUnit = newCompilationUnit.WithMembers(SyntaxFactory.SingletonList<MemberDeclarationSyntax>(newClassNode));
            }

            // A new file that embeds existing code: the using directives and the moved members (with their comments) are
            // left byte-exact, only the synthesized namespace and class structure is formatted.
            var formattedCode = await RoslynFormattingHelper.FormatNewRootKeepingTriviaAsync(document, newCompilationUnit, n => n is not (UsingDirectiveSyntax or MemberDeclarationSyntax { Parent: ClassDeclarationSyntax }), cancellationToken);

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

    /// <summary>
    /// The complete form of ExtractMembers newType=partial: returns TWO files, the new '{Class}.Partial.cs' and the source file
    /// rewritten with the moved members removed and 'partial' added to the class (an already-partial class is left as is).
    /// The new file is the one <see cref="ExtractMembersToPartialAsync"/> builds (same member selection, usings, namespace and
    /// formatting), with its declaration aligned to the original class (modifiers, type parameters, constraints) so the two
    /// parts agree. Returns an empty dictionary when the file or class is not found, like the single-file method.
    /// </summary>
    public async Task<Dictionary<FilePathWrapper, string>> ExtractMembersToPartialWithSourceAsync(FilePathWrapper filePath, string className, string[] memberNames, CancellationToken cancellationToken = default)
    {
        var partialFiles = await ExtractMembersToPartialAsync(filePath, className, memberNames, cancellationToken);
        if (partialFiles.Count == 0)
        {
            return partialFiles;
        }

        var solution = await _workspaceManager.GetSolutionAsync(ReadSource.Committed, cancellationToken);
        var document = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault();
        var root = document == null ? null : await document.GetSyntaxRootAsync(cancellationToken) as CompilationUnitSyntax;
        var classNode = root?.DescendantNodes().OfType<ClassDeclarationSyntax>().FirstOrDefault(c => c.Identifier.Text == className);
        if (root == null || classNode == null)
        {
            return new Dictionary<FilePathWrapper, string>();
        }

        // Source file: the moved members leave (their comments travelled with them into the new file) and the class becomes partial.
        var membersToMove = SelectMembersToMove(classNode, memberNames);
        var rewrittenClass = WithPartialModifier(classNode.RemoveNodes(membersToMove, SyntaxRemoveOptions.KeepNoTrivia)!);
        var rewrittenSource = root.ReplaceNode(classNode, rewrittenClass).ToFullString();

        // New file: align the generated declaration with the original.
        var partialPath = partialFiles.Keys.First();
        var partialRoot = SyntaxFactory.ParseCompilationUnit(partialFiles[partialPath]);
        var generatedPart = partialRoot.DescendantNodes().OfType<ClassDeclarationSyntax>().First(c => c.Identifier.Text == className);
        var alignedPartialText = partialRoot.ReplaceNode(generatedPart, AlignPartialDeclaration(generatedPart, classNode)).ToFullString();

        return new Dictionary<FilePathWrapper, string>
        {
            { partialPath, alignedPartialText },
            { filePath, rewrittenSource }
        };
    }

    // The members ExtractMembers newType=partial moves: the methods and properties named in memberNames. Shared by
    // ExtractMembersToPartialAsync (builds the new file) and ExtractMembersToPartialWithSourceAsync (removes them from the source).
    private static List<MemberDeclarationSyntax> SelectMembersToMove(ClassDeclarationSyntax classNode, string[] memberNames)
    {
        return classNode.Members.Where(m => (m is MethodDeclarationSyntax meth && memberNames.Contains(meth.Identifier.Text)) || (m is PropertyDeclarationSyntax prop && memberNames.Contains(prop.Identifier.Text))).ToList();
    }

    // Returns the class with the 'partial' modifier added directly before the 'class' keyword (the only position C# accepts);
    // an already-partial class is returned unchanged, and every existing modifier is kept.
    private static ClassDeclarationSyntax WithPartialModifier(ClassDeclarationSyntax classNode)
    {
        if (classNode.Modifiers.Any(SyntaxKind.PartialKeyword))
        {
            return classNode;
        }

        if (classNode.Modifiers.Count > 0)
        {
            return classNode.AddModifiers(SyntaxFactory.Token(SyntaxKind.PartialKeyword).WithTrailingTrivia(SyntaxFactory.Space));
        }

        // No modifiers yet: the indentation (leading trivia) of the 'class' keyword moves onto the new first token.
        var classKeyword = classNode.Keyword;
        return classNode
            .WithModifiers(SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.PartialKeyword).WithLeadingTrivia(classKeyword.LeadingTrivia).WithTrailingTrivia(SyntaxFactory.Space)))
            .WithKeyword(classKeyword.WithLeadingTrivia(SyntaxFactory.TriviaList()));
    }

    // Makes the generated 'public partial class X' part agree with the original declaration: the modifiers (accessibility,
    // static/abstract/sealed, plus 'partial'), the type parameter list and the constraint clauses. Without this a generic or
    // non-public class gets a mismatching second part (CS0260/CS0262/CS0264). The base list is deliberately not copied: the
    // original part keeps it. The copied header tokens are normalised on their own, the generated trivia around them is kept.
    // TODO type-parameter and constraint attributes/comments are normalised away, not carried to the partial part
    private static ClassDeclarationSyntax AlignPartialDeclaration(ClassDeclarationSyntax generatedPart, ClassDeclarationSyntax originalClass)
    {
        var originalModifiers = WithPartialModifier(originalClass).Modifiers;
        var firstLeading = generatedPart.Modifiers.Count > 0 ? generatedPart.Modifiers[0].LeadingTrivia : generatedPart.Keyword.LeadingTrivia;
        var modifierTokens = originalModifiers
            .Select((m, i) => m.WithoutTrivia().WithLeadingTrivia(i == 0 ? firstLeading : SyntaxFactory.TriviaList()).WithTrailingTrivia(SyntaxFactory.Space))
            .ToList();
        var aligned = generatedPart
            .WithModifiers(SyntaxFactory.TokenList(modifierTokens))
            .WithKeyword(generatedPart.Keyword.WithLeadingTrivia(SyntaxFactory.TriviaList()));

        if (originalClass.TypeParameterList is not { } typeParameters)
        {
            return aligned;
        }

        // Whatever followed the identifier in the generated part (normally the line break before '{') now follows the header's last token.
        var headerEnd = generatedPart.Identifier.TrailingTrivia;
        var constraintClauses = originalClass.ConstraintClauses
            .Select((c, i) => c.NormalizeWhitespace().WithTrailingTrivia(i == originalClass.ConstraintClauses.Count - 1 ? headerEnd : SyntaxFactory.TriviaList(SyntaxFactory.Space)))
            .ToList();
        var typeParameterList = typeParameters.NormalizeWhitespace().WithTrailingTrivia(constraintClauses.Count == 0 ? headerEnd : SyntaxFactory.TriviaList(SyntaxFactory.Space));
        return aligned
            .WithIdentifier(aligned.Identifier.WithTrailingTrivia(SyntaxFactory.TriviaList()))
            .WithTypeParameterList(typeParameterList)
            .WithConstraintClauses(SyntaxFactory.List(constraintClauses));
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

        var methodNode = root.DescendantNodes().OfType<MethodDeclarationSyntax>().Where(m => m.Identifier.Text == methodName).OrderBy(m => m.Parent is InterfaceDeclarationSyntax ? 1 : 0).FirstOrDefault();
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
            // The TODO rides as leading trivia of the first statement (or of the closing brace when the body is
            // empty): replacing a statement or token would discard the comment trivia.
            var todoLine = SyntaxFactory.Comment($"// TODO: Update call sites to use {recordName}");
            var todoEol = methodNode.DescendantTrivia().FirstOrDefault(t => t.IsKind(SyntaxKind.EndOfLineTrivia));
            var todoNewLine = todoEol.IsKind(SyntaxKind.EndOfLineTrivia) ? todoEol : SyntaxFactory.EndOfLine(Environment.NewLine);
            if (block.Statements.Count > 0)
            {
                var firstStmt = block.Statements[0];
                var stmtLead = firstStmt.GetLeadingTrivia();
                var todoLead = new List<SyntaxTrivia>();
                if (stmtLead.Count > 0 && stmtLead.Last().IsKind(SyntaxKind.WhitespaceTrivia))
                {
                    todoLead.Add(stmtLead.Last());
                }
                todoLead.Add(todoLine);
                todoLead.Add(todoNewLine);
                var newFirst = firstStmt.WithLeadingTrivia(SyntaxFactory.TriviaList(todoLead).AddRange(stmtLead));
                block = block.WithStatements(block.Statements.Replace(firstStmt, newFirst));
            }
            else
            {
                var closeLead = block.CloseBraceToken.LeadingTrivia;
                var closeIndent = closeLead.Count > 0 && closeLead.Last().IsKind(SyntaxKind.WhitespaceTrivia) ? closeLead.Last().ToString() : string.Empty;
                var closeTodoLead = SyntaxFactory.TriviaList(SyntaxFactory.Whitespace(closeIndent + "    "), todoLine, todoNewLine);
                block = block.WithCloseBraceToken(block.CloseBraceToken.WithLeadingTrivia(closeTodoLead.AddRange(closeLead)));
            }
            newMethodNode = newMethodNode.WithBody(block);
        }

        // If method is on an interface, also update interface definition
        var classNode = methodNode.Parent as ClassDeclarationSyntax;
        var interfaceNode = methodNode.Parent as InterfaceDeclarationSyntax;
        // Shared annotation for every node introduced/replaced below (the rewritten method and,
        // further down, the newly-appended record declaration) -> lets the final
        // RoslynFormattingHelper.FormatAnnotatedSpansAsync pass reformat just these edited spans instead of
        // reflowing the whole file, in the document's own line ending.
        var editAnnotation = Formatter.Annotation;
        var annotatedNewMethodNode = newMethodNode.WithAdditionalAnnotations(editAnnotation);
        string? warning = null;
        SyntaxNode newRoot = root;
        if (interfaceNode != null && methodSymbol?.ExplicitInterfaceImplementations.Length == 0)
        {
            // Update interface method
            var newInterface = interfaceNode.ReplaceNode(methodNode, annotatedNewMethodNode);
            newRoot = root.ReplaceNode(interfaceNode, newInterface);
            warning = "WARNING: The interface method was updated, but its implementations were not. Update the implementations manually.";
        }
        else if (classNode != null && methodSymbol?.ExplicitInterfaceImplementations.Length == 0)
        {
            // Check if method implements an interface
            if (methodSymbol?.ContainingType?.Interfaces.Length > 0)
            {
                // Method implements interface -> warn, but still update the implementation and fall through to
                // the record append below (returning early left the rewritten method referencing an undeclared record).
                warning = "WARNING: This method implements an interface. Update the interface signature in the corresponding interface file.";
            }
            newRoot = root.ReplaceNode(methodNode, annotatedNewMethodNode);
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

        var formattedDoc = await RoslynFormattingHelper.FormatAnnotatedSpansAsync(document, newRoot, cancellationToken);
        return new DocumentEditResult
        {
            Outcome = EditOutcome.Modified,
            FilePath = filePath,
            Message = warning is null ? $"Introduced parameter object '{recordName}'." : $"Introduced parameter object '{recordName}'. {warning}",
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
        var candidates = SyntaxTargetResolver.ResolveCandidates(root, text, memberName, cancellationToken).Where(c => c.Kind is CandidateKind.Method or CandidateKind.Property or CandidateKind.Constructor).ToList();
        MemberDeclarationSyntax? target;
        string? snippetWarning;
        try
        {
            var resolved = SyntaxTargetResolver.ResolveBySnippetWithWarning(candidates, text, contextSnippet, lineBefore, lineAfter, (c, m, mode) => SyntaxTargetResolver.BuildHintForCandidates(c, m, mode), trustSingleCandidate: true, filePath: document.FilePath ?? string.Empty);
            target = resolved.Candidate?.Node as MemberDeclarationSyntax;
            snippetWarning = resolved.Warning;
        }
        catch (ToolException ex)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.CannotEdit,
                FilePath = filePath,
                Message = ex.Message,
                ErrorCode = ex.ErrorCode,
                Candidates = (ex as ToolAmbiguousMatchException)?.Candidates
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

        var newRoot = root.ReplaceNode(target, newTarget.WithAdditionalAnnotations(Formatter.Annotation));
        var formatted = await RoslynFormattingHelper.FormatAnnotatedSpansAsync(document, newRoot, cancellationToken);
        return new DocumentEditResult
        {
            Outcome = EditOutcome.Modified,
            FilePath = filePath,
            UpdatedText = (await formatted.GetTextAsync(cancellationToken)).ToString(),
            Warnings = snippetWarning == null ? null : new[] { snippetWarning }
        };
    }

    /// <summary>
    /// Refuses (before any change set is built) the method shapes whose call sites cannot be inlined by plain
    /// argument substitution: extension, generic and async methods, and ref/out/in, params or optional parameters.
    /// </summary>
    private static void ThrowIfInlineMethodIneligible(IMethodSymbol methodSymbol)
    {
        var name = methodSymbol.Name;
        if (methodSymbol.IsExtensionMethod)
        {
            throw new ToolTargetIneligibleException($"Cannot inline '{name}': extension methods are not supported (the receiver is bound to the first parameter).");
        }

        if (methodSymbol.IsGenericMethod)
        {
            throw new ToolTargetIneligibleException($"Cannot inline '{name}': generic methods are not supported (type arguments are not substituted).");
        }

        if (methodSymbol.IsAsync)
        {
            throw new ToolTargetIneligibleException($"Cannot inline '{name}': async methods are not supported.");
        }

        foreach (var parameter in methodSymbol.Parameters)
        {
            if (parameter.RefKind != RefKind.None)
            {
                throw new ToolTargetIneligibleException($"Cannot inline '{name}': parameter '{parameter.Name}' is a {parameter.RefKind} parameter, which cannot be replaced by an argument expression.");
            }

            if (parameter.IsParams)
            {
                throw new ToolTargetIneligibleException($"Cannot inline '{name}': parameter '{parameter.Name}' is a params array.");
            }

            if (parameter.IsOptional)
            {
                throw new ToolTargetIneligibleException($"Cannot inline '{name}': parameter '{parameter.Name}' is optional (its default value would have to be substituted).");
            }
        }
    }

    /// <summary>
    /// Builds the per-call-site expression: the method's expression with each parameter reference replaced by the
    /// call's argument (parenthesized unless it is already a primary expression). Arguments are read from the
    /// rewritten invocation so a nested inlined call inside an argument is carried over.
    /// </summary>
    private static ExpressionSyntax SubstituteInlinedArguments(
        ExpressionSyntax template,
        IReadOnlyDictionary<IdentifierNameSyntax, int> parameterOrdinalById,
        IReadOnlyDictionary<int, int> argumentIndexByOrdinal,
        InvocationExpressionSyntax rewrittenInvocation,
        IReadOnlyDictionary<SimpleNameSyntax, SyntaxAnnotation>? nameAnnotations = null)
    {
        if (parameterOrdinalById.Count == 0 && (nameAnnotations == null || nameAnnotations.Count == 0))
        {
            return template;
        }

        var nodesToReplace = parameterOrdinalById.Keys.Cast<SimpleNameSyntax>()
            .Concat(nameAnnotations?.Keys ?? Enumerable.Empty<SimpleNameSyntax>())
            .ToList();
        return template.ReplaceNodes(nodesToReplace, (reference, rewritten) =>
        {
            if (reference is not IdentifierNameSyntax parameterReference || !parameterOrdinalById.TryGetValue(parameterReference, out var ordinal))
            {
                return rewritten.WithAdditionalAnnotations(nameAnnotations![reference]);
            }

            var argument = rewrittenInvocation.ArgumentList.Arguments[argumentIndexByOrdinal[ordinal]].Expression;
            var needsParentheses = argument is not (IdentifierNameSyntax or LiteralExpressionSyntax or MemberAccessExpressionSyntax
                or InvocationExpressionSyntax or ElementAccessExpressionSyntax or ThisExpressionSyntax or ParenthesizedExpressionSyntax);
            return needsParentheses
                ? SyntaxFactory.ParenthesizedExpression(argument.WithoutTrivia()).WithTriviaFrom(parameterReference)
                : argument.WithTriviaFrom(parameterReference);
        });
    }

    /// <summary>
    /// Analyses the inlined method's expression (bound in the DEFINING document) for the names that are not the
    /// method's own parameters. <c>Names</c> are the names that must keep their meaning at every call site (the
    /// leftmost name of a chain, an unqualified name, <c>this.X</c> and reduced extension-method names).
    /// <c>QualifiedMembers</c> are members reached through a qualifier or initializer: they bind through that
    /// qualifier, so only their accessibility is checked. <c>ThisDependency</c> names the first thing that needs
    /// the defining instance (an instance member by implicit <c>this</c>, or <c>this</c>/<c>base</c> itself), else null.
    /// Locals, range variables, lambda parameters and discards declared inside the template are skipped.
    /// </summary>
    private static (Dictionary<SimpleNameSyntax, ISymbol> Names, List<ISymbol> QualifiedMembers, string? ThisDependency) CollectInlineTemplateNames(
        ExpressionSyntax template,
        SemanticModel model,
        IMethodSymbol methodSymbol,
        IReadOnlyDictionary<IdentifierNameSyntax, int> parameterOrdinalById,
        CancellationToken cancellationToken)
    {
        var names = new Dictionary<SimpleNameSyntax, ISymbol>();
        var qualifiedMembers = new List<ISymbol>();
        var thisDependency = template.DescendantNodesAndSelf().Any(n => n is ThisExpressionSyntax or BaseExpressionSyntax) ? "this" : null;

        foreach (var name in template.DescendantNodesAndSelf().OfType<SimpleNameSyntax>())
        {
            if (name is IdentifierNameSyntax identifier && (identifier.IsVar || parameterOrdinalById.ContainsKey(identifier)))
            {
                continue;
            }

            var symbol = model.GetSymbolInfo(name, cancellationToken).Symbol;
            if (symbol == null || symbol is ILocalSymbol or IRangeVariableSymbol or ILabelSymbol or IDiscardSymbol or IAliasSymbol)
            {
                continue;
            }

            if (symbol is IParameterSymbol parameterSymbol
                && (SymbolEqualityComparer.Default.Equals(parameterSymbol.ContainingSymbol, methodSymbol)
                    || parameterSymbol.DeclaringSyntaxReferences.Any(r => r.SyntaxTree == template.SyntaxTree && template.Span.Contains(r.Span))
                    || name.Parent is NameColonSyntax))
            {
                continue; // the method's own parameter, a lambda parameter, or the parameter name of a named argument
            }

            var isReducedExtension = symbol is IMethodSymbol { ReducedFrom: not null };
            var isQualified = !isReducedExtension && name.Parent switch
            {
                MemberAccessExpressionSyntax access => access.Name == name && access.Expression is not (ThisExpressionSyntax or BaseExpressionSyntax),
                MemberBindingExpressionSyntax => true,
                QualifiedNameSyntax qualifiedName => qualifiedName.Right == name,
                AliasQualifiedNameSyntax aliasQualified => aliasQualified.Name == name,
                NameColonSyntax or NameEqualsSyntax => true,
                AssignmentExpressionSyntax assignment => assignment.Left == name
                    && assignment.Parent is InitializerExpressionSyntax initializer && initializer.IsKind(SyntaxKind.ObjectInitializerExpression),
                _ => false,
            };

            if (isQualified)
            {
                qualifiedMembers.Add(symbol);
                continue;
            }

            names[name] = symbol;
            var isInstanceMember = symbol switch
            {
                IFieldSymbol field => !field.IsStatic && !field.IsConst,
                IPropertySymbol property => !property.IsStatic,
                IMethodSymbol method => !method.IsStatic && method.ReducedFrom == null && method.MethodKind == MethodKind.Ordinary,
                IEventSymbol @event => !@event.IsStatic,
                IParameterSymbol => true, // a captured parameter of a primary constructor
                _ => false,
            };
            if (thisDependency == null && isInstanceMember)
            {
                thisDependency = symbol.Name;
            }
        }

        return (names, qualifiedMembers, thisDependency);
    }

    /// <summary>
    /// Refuses a call site where inlining would silently change what the expression means. The call's receiver is
    /// dropped by inlining, so (a) a template that needs the defining instance requires an implicit or <c>this</c>
    /// receiver, and any other receiver must be a plain name or member chain (no side effects to drop); (b) every
    /// member the template uses must be accessible at the call site; (c) every name must re-bind to the same symbol
    /// there (speculative binding of the substituted expression at the call's position; if that is unavailable the
    /// site is refused rather than guessed).
    /// </summary>
    private static void ThrowIfInlinedNamesDoNotRebind(
        string methodName,
        ExpressionSyntax template,
        IReadOnlyDictionary<IdentifierNameSyntax, int> parameterOrdinalById,
        IReadOnlyDictionary<SimpleNameSyntax, ISymbol> names,
        IReadOnlyList<ISymbol> qualifiedMembers,
        string? thisDependency,
        InvocationExpressionSyntax invocation,
        IReadOnlyDictionary<int, int> argumentIndexByOrdinal,
        SemanticModel callSiteModel,
        string callSiteFileName)
    {
        ToolTargetIneligibleException Refuse(string referredName, string reason) =>
            new($"Cannot inline '{methodName}': in '{callSiteFileName}' the expression refers to '{referredName}', which does not mean the same thing at this call site ({reason}); inlining would silently change what the code does, so make the member accessible or inline this call by hand.");

        static bool IsPlainReceiver(ExpressionSyntax expression) => expression switch
        {
            SimpleNameSyntax or ThisExpressionSyntax or BaseExpressionSyntax or PredefinedTypeSyntax => true,
            MemberAccessExpressionSyntax access when access.IsKind(SyntaxKind.SimpleMemberAccessExpression) => IsPlainReceiver(access.Expression),
            _ => false,
        };

        static ISymbol Canonical(ISymbol symbol) => (symbol is IMethodSymbol { ReducedFrom: { } reduced } ? reduced : symbol).OriginalDefinition;

        var receiver = (invocation.Expression as MemberAccessExpressionSyntax)?.Expression;
        if (receiver != null && receiver is not ThisExpressionSyntax)
        {
            if (thisDependency != null)
            {
                throw Refuse(thisDependency, "instance member of another instance");
            }

            if (!IsPlainReceiver(receiver))
            {
                throw new ToolTargetIneligibleException($"Cannot inline '{methodName}': a call in '{callSiteFileName}' has the receiver '{receiver}', which is not a plain name or member chain; inlining drops the receiver, and with it any side effects it has.");
            }
        }

        var position = invocation.SpanStart;
        foreach (var symbol in names.Values.Concat(qualifiedMembers))
        {
            var target = symbol is IMethodSymbol { ReducedFrom: { } reducedFrom } ? reducedFrom : symbol;
            if (target.Kind is SymbolKind.NamedType or SymbolKind.Field or SymbolKind.Property or SymbolKind.Method or SymbolKind.Event
                && !callSiteModel.IsAccessible(position, target))
            {
                throw Refuse(symbol.Name, "inaccessible");
            }
        }

        if (names.Count == 0)
        {
            return;
        }

        const string annotationKind = "RoslynSentinel.InlineMethod.TemplateName";
        var ordered = names.Keys.ToList();
        var annotations = new Dictionary<SimpleNameSyntax, SyntaxAnnotation>();
        for (var i = 0; i < ordered.Count; i++)
        {
            annotations[ordered[i]] = new SyntaxAnnotation(annotationKind, i.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        // Which speculative root can be bound depends on where the call sits: a statement inside a body, an arrow
        // body, or a field/property initializer. Try each; if none binds, refuse rather than guess.
        var substituted = SubstituteInlinedArguments(template, parameterOrdinalById, argumentIndexByOrdinal, invocation, annotations);
        var statement = SyntaxFactory.ExpressionStatement(SyntaxFactory.AssignmentExpression(SyntaxKind.SimpleAssignmentExpression, SyntaxFactory.IdentifierName("_"), substituted));
        var arrowBody = SyntaxFactory.ArrowExpressionClause(substituted);
        var initializer = SyntaxFactory.EqualsValueClause(substituted);
        SyntaxNode? boundRoot = null;
        SemanticModel? speculativeModel;
        if (callSiteModel.TryGetSpeculativeSemanticModel(position, statement, out speculativeModel) && speculativeModel != null)
        {
            boundRoot = statement;
        }
        else if (callSiteModel.TryGetSpeculativeSemanticModel(position, arrowBody, out speculativeModel) && speculativeModel != null)
        {
            boundRoot = arrowBody;
        }
        else if (callSiteModel.TryGetSpeculativeSemanticModel(position, initializer, out speculativeModel) && speculativeModel != null)
        {
            boundRoot = initializer;
        }

        if (boundRoot == null || speculativeModel == null)
        {
            throw Refuse(names.Values.First().Name, "its binding could not be verified at this call site");
        }

        foreach (var node in boundRoot.DescendantNodes().OfType<SimpleNameSyntax>())
        {
            var annotation = node.GetAnnotations(annotationKind).FirstOrDefault();
            if (annotation?.Data == null)
            {
                continue;
            }

            var expected = names[ordered[int.Parse(annotation.Data, System.Globalization.CultureInfo.InvariantCulture)]];
            var info = speculativeModel.GetSymbolInfo(node);
            if (info.Symbol == null)
            {
                throw Refuse(expected.Name, info.CandidateReason == CandidateReason.Inaccessible ? "inaccessible" : "not in scope");
            }

            if (!SymbolEqualityComparer.Default.Equals(Canonical(expected), Canonical(info.Symbol)))
            {
                throw Refuse(expected.Name, "binds to a different member");
            }
        }
    }

    /// <summary>
    /// Wraps the inlined expression in parentheses (outer trivia is applied by the caller) when it is not primary
    /// and the call it replaces sits where precedence could change its meaning. No wrap in statement-like slots
    /// (argument, initializer, arrow body, return, expression statement, assignment right-hand side, interpolation,
    /// parentheses) nor for a same-operator binary expression that is the LEFT operand of a binary parent
    /// (left associativity keeps <c>a + b + c</c> meaning <c>(a + b) + c</c>).
    /// </summary>
    private static ExpressionSyntax ParenthesizeInlinedExpressionForContext(ExpressionSyntax inlined, InvocationExpressionSyntax original)
    {
        if (inlined is IdentifierNameSyntax or LiteralExpressionSyntax or InvocationExpressionSyntax or MemberAccessExpressionSyntax
            or ElementAccessExpressionSyntax or ThisExpressionSyntax or ParenthesizedExpressionSyntax)
        {
            return inlined;
        }

        var parent = original.Parent;
        var noWrap = parent is ArgumentSyntax or EqualsValueClauseSyntax or ArrowExpressionClauseSyntax or ReturnStatementSyntax
                or ExpressionStatementSyntax or InterpolationSyntax or ParenthesizedExpressionSyntax
            || (parent is AssignmentExpressionSyntax assignment && assignment.Right == original)
            || (parent is BinaryExpressionSyntax binary && binary.Left == original && inlined is BinaryExpressionSyntax inlinedBinary && inlinedBinary.Kind() == binary.Kind());
        return noWrap ? inlined : SyntaxFactory.ParenthesizedExpression(inlined.WithoutTrivia());
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
        var candidates = root.DescendantNodes().OfType<MethodDeclarationSyntax>().Where(m => m.Identifier.Text == methodName).ToList();
        if (candidates.Count == 0 || semanticModel == null)
        {
            throw new ToolNotFoundException($"Method '{methodName}' not found in '{filePath}'.");
        }

        if (candidates.Count > 1)
        {
            var declarations = string.Join(", ", candidates.Select(c =>
                (c.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault()?.Identifier.Text ?? "<global>") + "." + methodName
                + "(" + string.Join(", ", c.ParameterList.Parameters.Select(p => p.Type?.ToString() ?? "?")) + ")"));
            throw new ToolTargetIneligibleException($"Cannot inline '{methodName}': it is declared {candidates.Count} times in '{Path.GetFileName(filePath)}' ({declarations}); Inline cannot tell which one to inline, so rename or move the others first.");
        }

        var method = candidates[0];

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

        ThrowIfInlineMethodIneligible(methodSymbol);

        // Parameter references inside the template, bound with the DEFINING document's model (ordinal of the parameter they name).
        var expressionTemplate = expressionToInline; // capture once
        var parameterOrdinalById = new Dictionary<IdentifierNameSyntax, int>();
        foreach (var identifier in expressionTemplate.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>())
        {
            if (semanticModel.GetSymbolInfo(identifier, cancellationToken).Symbol is IParameterSymbol parameter
                && SymbolEqualityComparer.Default.Equals(parameter.ContainingSymbol, methodSymbol))
            {
                parameterOrdinalById[identifier] = parameter.Ordinal;
            }
        }

        // The template's other names (fields, statics, types needing a using...) as bound in the defining document; every call site must see them mean the same thing.
        var (templateNames, qualifiedTemplateMembers, thisDependency) = CollectInlineTemplateNames(expressionTemplate, semanticModel, methodSymbol, parameterOrdinalById, cancellationToken);

        // Find ALL references across the solution grouped by document
        var references = await SymbolFinder.FindReferencesAsync(methodSymbol, solution, cancellationToken);
        var byDocument = references.SelectMany(r => r.Locations).Where(l => l.Document.FilePath != null).GroupBy(l => l.Document.Id).ToDictionary(g => g.Key, g => g.ToList());
        var updatedRoots = new Dictionary<DocumentId, SyntaxNode>();

        // Pass 1: plan every call site (invocation -> argument index per parameter ordinal) and refuse before any change set exists.
        var plannedSites = new Dictionary<DocumentId, List<(InvocationExpressionSyntax Invocation, Dictionary<int, int> ArgumentIndexByOrdinal)>>();
        foreach (var (docId, locations) in byDocument)
        {
            var doc = solution.GetDocument(docId);
            if (doc?.FilePath == null)
            {
                continue;
            }

            var docRoot = await doc.GetSyntaxRootAsync(cancellationToken);
            var docModel = await doc.GetSemanticModelAsync(cancellationToken);
            if (docRoot == null || docModel == null)
            {
                continue;
            }

            var sites = new List<(InvocationExpressionSyntax Invocation, Dictionary<int, int> ArgumentIndexByOrdinal)>();
            foreach (var location in locations)
            {
                var token = docRoot.FindToken(location.Location.SourceSpan.Start);
                var referenceNode = token.Parent;
                if (referenceNode == null || referenceNode.IsPartOfStructuredTrivia())
                {
                    continue; // e.g. a cref in a doc comment: nothing to replace
                }

                // A reference must be the callee of an invocation of this very method; method groups, nameof and
                // conditional access (a?.M(x)) cannot be replaced by the expression.
                var invocation = referenceNode.AncestorsAndSelf().OfType<InvocationExpressionSyntax>().FirstOrDefault();
                if (invocation == null
                    || !invocation.Expression.Span.Contains(token.Span)
                    || invocation.Expression is MemberBindingExpressionSyntax
                    || docModel.GetOperation(invocation, cancellationToken) is not Microsoft.CodeAnalysis.Operations.IInvocationOperation invocationOperation
                    || !SymbolEqualityComparer.Default.Equals(invocationOperation.TargetMethod.OriginalDefinition, methodSymbol))
                {
                    throw new ToolTargetIneligibleException($"Cannot inline '{methodName}': a reference to it in '{Path.GetFileName(doc.FilePath)}' is not a plain call (method group, nameof or conditional access).");
                }

                var argumentIndexByOrdinal = new Dictionary<int, int>();
                foreach (var argument in invocationOperation.Arguments)
                {
                    if (argument.ArgumentKind != Microsoft.CodeAnalysis.Operations.ArgumentKind.Explicit
                        || argument.Parameter == null
                        || argument.Syntax is not ArgumentSyntax argumentSyntax)
                    {
                        throw new ToolTargetIneligibleException($"Cannot inline '{methodName}': a call in '{Path.GetFileName(doc.FilePath)}' passes an argument that is not an explicit argument.");
                    }

                    argumentIndexByOrdinal[argument.Parameter.Ordinal] = invocation.ArgumentList.Arguments.IndexOf(argumentSyntax);
                }

                // Only identifiers and literals may be duplicated or dropped; any other argument could change side effects or cost.
                foreach (var parameter in methodSymbol.Parameters)
                {
                    if (!argumentIndexByOrdinal.TryGetValue(parameter.Ordinal, out var argumentIndex) || argumentIndex < 0)
                    {
                        throw new ToolTargetIneligibleException($"Cannot inline '{methodName}': a call in '{Path.GetFileName(doc.FilePath)}' has no argument for parameter '{parameter.Name}'.");
                    }

                    var useCount = parameterOrdinalById.Values.Count(ordinal => ordinal == parameter.Ordinal);
                    var argumentExpression = invocation.ArgumentList.Arguments[argumentIndex].Expression;
                    if (useCount != 1 && argumentExpression is not (IdentifierNameSyntax or LiteralExpressionSyntax))
                    {
                        throw new ToolTargetIneligibleException($"Cannot inline '{methodName}': parameter '{parameter.Name}' is used {useCount} times in the method but a call in '{Path.GetFileName(doc.FilePath)}' passes the non-trivial argument '{argumentExpression}'; only an identifier or literal argument can be duplicated or dropped.");
                    }
                }

                if (sites.All(s => s.Invocation != invocation))
                {
                    ThrowIfInlinedNamesDoNotRebind(methodName, expressionTemplate, parameterOrdinalById, templateNames, qualifiedTemplateMembers, thisDependency, invocation, argumentIndexByOrdinal, docModel, Path.GetFileName(doc.FilePath));
                    sites.Add((invocation, argumentIndexByOrdinal));
                }
            }

            if (sites.Count > 0)
            {
                plannedSites[docId] = sites;
            }
        }

        // Pass 2: replace each call with the per-site expression (Pass 1 already refused any site where the template's names would not keep their meaning).
        foreach (var (docId, sites) in plannedSites)
        {
            var docRoot = await solution.GetDocument(docId)!.GetSyntaxRootAsync(cancellationToken);
            var siteByInvocation = sites.ToDictionary(s => s.Invocation, s => s.ArgumentIndexByOrdinal);

            // The inlined expression is the only changed node and the only one formatted; the rest of the file keeps its text.
            updatedRoots[docId] = docRoot!.ReplaceNodes(
                sites.Select(s => s.Invocation),
                (original, rewritten) => ParenthesizeInlinedExpressionForContext(
                        SubstituteInlinedArguments(expressionTemplate, parameterOrdinalById, siteByInvocation[original], rewritten), original)
                    .WithTriviaFrom(original).WithAdditionalAnnotations(Formatter.Annotation));
        }

        // Remove the method declaration from the defining document, on top of its inlined call sites when it has any.
        var definingRoot = updatedRoots.TryGetValue(document.Id, out var inlinedDefiningRoot) ? inlinedDefiningRoot : root;
        var methodToRemove = definingRoot.DescendantNodes().OfType<MethodDeclarationSyntax>().FirstOrDefault(m => m.Identifier.Text == methodName);
        if (methodToRemove != null)
        {
            updatedRoots[document.Id] = RemoveMemberDeclaration(definingRoot, methodToRemove);
        }

        var result = new Dictionary<FilePathWrapper, string>();
        foreach (var (docId, updatedRoot) in updatedRoots)
        {
            var doc = solution.GetDocument(docId)!;
            result[doc.FilePath!] = await FormatAnnotatedTextAsync(doc, updatedRoot, cancellationToken);
        }

        return result;
    }

    public async Task<Dictionary<FilePathWrapper, string>> ConvertTupleToClassAsync(FilePathWrapper filePath, string methodName, string newClassName, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetSolutionAsync(ReadSource.Committed, cancellationToken);
        var document = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault() ?? throw new ToolNotFoundException($"File not found: {filePath}");
        var root = await document.GetSyntaxRootAsync(cancellationToken) as CompilationUnitSyntax;
        var methodNode = (root?.DescendantNodes().OfType<MethodDeclarationSyntax>().FirstOrDefault(m => m.Identifier.Text == methodName)) ?? throw new ToolNotFoundException($"Method '{methodName}' not found in type.");
        if (methodNode.ReturnType is not TupleTypeSyntax tupleType)
        {
            throw new ToolTargetIneligibleException("Method does not return a named tuple.");
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

        // The method keeps its formatting; only the replaced return type is formatted. classRoot is a new file that embeds
        // existing code (the source's using directives and the tuple element types, comments included, such as a trailing
        // comment on a using or "int /* id */ Id"): those nodes are left byte-exact and only the synthesized structure is
        // formatted.
        var newMethodNode = methodNode.WithReturnType(SyntaxFactory.ParseTypeName(newClassName).WithTriviaFrom(methodNode.ReturnType).WithAdditionalAnnotations(Formatter.Annotation));
        var updatedRoot = root.ReplaceNode(methodNode, newMethodNode);
        return new Dictionary<FilePathWrapper, string>
        {
            {
                filePath,
                await FormatAnnotatedTextAsync(document, updatedRoot, cancellationToken)
            },
            {
                Path.Combine(Path.GetDirectoryName(filePath)!, $"{newClassName}.cs"),
                await RoslynFormattingHelper.FormatNewRootKeepingTriviaAsync(document, classRoot, n => n is not (UsingDirectiveSyntax or TypeSyntax { Parent: PropertyDeclarationSyntax }), cancellationToken)
            }
        };
    }

    public async Task<Dictionary<FilePathWrapper, string>> ChangePropertyTypeAsync(FilePathWrapper filePath, string className, string propertyName, string newType, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetSolutionAsync(ReadSource.Committed, cancellationToken);
        var document = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault() ?? throw new ToolNotFoundException($"File not found: {filePath}");
        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var classNode = root?.DescendantNodes().OfType<ClassDeclarationSyntax>().FirstOrDefault(c => c.Identifier.Text == className);
        var propNode = classNode?.Members.OfType<PropertyDeclarationSyntax>().FirstOrDefault(p => p.Identifier.Text == propertyName);
        if (classNode == null || propNode == null)
        {
            throw new ToolNotFoundException("Class or property not found.");
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
        var document = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault() ?? throw new ToolNotFoundException($"File not found: {filePath}");
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
            // The class is built from scratch, so every node is new and formatted; the file takes the dominant line ending
            // of the document it was extracted from.
            return new Dictionary<FilePathWrapper, string>
            {
                {
                    Path.Combine(Path.GetDirectoryName(filePath)!, $"{newClassName}.cs"),
                    await RoslynFormattingHelper.FormatNewRootKeepingTriviaAsync(document, SyntaxFactory.CompilationUnit().AddMembers(newClass), _ => true, cancellationToken)
                }
            };
        }

        throw new ToolNotFoundException("Anonymous type not found.");
    }
}