using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;

namespace RoslynSentinel.Advanced;

public record MoveMemberResult(Dictionary<FilePath, string> Changes, List<SkippedCallSite> SkippedCallSites);

public class AdvancedStructuralEngine
{
    private readonly ISolutionProvider _workspaceManager;

    public AdvancedStructuralEngine(ISolutionProvider workspaceManager)
    {
        _workspaceManager = workspaceManager;
    }

    public async Task<DocumentEditResult> ConvertAbstractClassToInterfaceAsync(FilePath filePath, string className, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetCurrentSolutionAsync(cancellationToken);
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
            FilePath = filePath
        };
    }

    public async Task<DocumentEditResult> ReplaceConstructorWithFactoryAsync(FilePath filePath, string className, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetCurrentSolutionAsync(cancellationToken);
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
            FilePath = filePath
        };
    }

    public async Task<Dictionary<FilePath, string>> ExtractSuperclassAsync(FilePath[] filePaths, string[] classNames, string newBaseClassName, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetCurrentSolutionAsync(cancellationToken);
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

    /// <summary>
    /// Moves named members from a class into a target class atomically — one combined change set,
    /// so it validates and writes as a single unit instead of the remove-then-add sequence that
    /// otherwise fails the write-path chokepoint's per-write compiler check (source ends up with
    /// dangling references before the add lands).
    ///
    /// Three destination modes, chosen automatically from targetClassName:
    ///  - Existing base type of the source class: reuses PullUpMember's modifier adjustment
    ///    (removes override, adds virtual) and skips call-site rewriting — virtual dispatch means
    ///    existing call sites keep working unchanged. Instance OR static members are both fine here.
    ///  - Existing unrelated class: moves the declaration as-is and rewrites call sites solution-wide
    ///    (ClassA.Foo() → ClassB.Foo()). STATIC MEMBERS ONLY — an instance member has no such
    ///    unambiguous rewrite (a call site may use the same source-class variable for other members
    ///    that stay behind, so there's no single correct receiver substitution); those are rejected
    ///    up front with a ToolNotFoundException rather than attempting a partial/guessed rewrite.
    ///  - No existing class named targetClassName: synthesizes a new class (same behavior as the
    ///    former ExtractMembers as=class). STATIC MEMBERS ONLY, same reasoning as above.
    /// </summary>
    public async Task<MoveMemberResult> MoveMemberAsync(FilePath filePath, string className, string[] memberNames, string targetClassName, FilePath? targetFilePath = null, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetCurrentSolutionAsync(cancellationToken);
        var document = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault();
        if (document == null)
        {
            throw new ToolNotFoundException($"File '{filePath}' not found.");
        }

        var root = await document.GetSyntaxRootAsync(cancellationToken) as CompilationUnitSyntax;
        if (root == null)
        {
            throw new ToolNotFoundException($"Failed to get syntax root for '{filePath}'.");
        }

        var classNode = root.DescendantNodes().OfType<ClassDeclarationSyntax>().FirstOrDefault(c => c.Identifier.Text == className);
        if (classNode == null)
        {
            throw new ToolNotFoundException($"Class '{className}' not found in '{filePath}'.");
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
            throw new ToolNotFoundException($"None of the requested member(s) [{string.Join(", ", memberNames)}] were found in class '{className}'.");
        }

        var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
        var classSymbol = semanticModel?.GetDeclaredSymbol(classNode, cancellationToken) as INamedTypeSymbol;
        var baseType = classSymbol?.BaseType;
        bool targetIsBaseType = baseType != null && baseType.SpecialType != SpecialType.System_Object && baseType.Name == targetClassName;

        if (targetIsBaseType)
        {
            return await MoveMembersToBaseTypeAsync(solution, filePath, root, classNode, membersToMove, baseType!, cancellationToken);
        }

        // Moving an INSTANCE member to anywhere other than an existing base class requires rewriting
        // every call site's receiver expression — and that's not always a safe mechanical substitution.
        // A local like `var x = new ClassA(); x.Foo(); x.Bar();` where only Foo moves to ClassB has no
        // single correct fix: retyping x to ClassB breaks Bar(), leaving it ClassA breaks Foo(). The real
        // fix (splitting into two variables, or adding a second reference) reshapes the caller's method
        // body — a design decision, not something this tool can infer from the move alone. STATIC members
        // have no such ambiguity (ClassA.Foo() → ClassB.Foo() is unambiguous everywhere), so only those
        // are supported for the existing-unrelated-class and new-class destinations.
        var nonStaticMembers = membersToMove.Where(m =>
        {
            var modifiers = m switch
            {
                MethodDeclarationSyntax meth => meth.Modifiers,
                PropertyDeclarationSyntax prop => prop.Modifiers,
                FieldDeclarationSyntax field => field.Modifiers,
                _ => default
            };
            return !modifiers.Any(mod => mod.IsKind(SyntaxKind.StaticKeyword));
        }).ToList();

        if (nonStaticMembers.Count > 0)
        {
            var names = string.Join(", ", nonStaticMembers.Select(m => m switch
            {
                MethodDeclarationSyntax meth => meth.Identifier.Text,
                PropertyDeclarationSyntax prop => prop.Identifier.Text,
                FieldDeclarationSyntax field => string.Join(", ", field.Declaration.Variables.Select(v => v.Identifier.Text)),
                _ => "?"
            }));
            throw new ToolNotFoundException(
                $"Cannot move instance member(s) [{names}] to '{targetClassName}': it is not an existing base class of '{className}'. " +
                "Moving an instance member elsewhere requires rewriting every call site's receiver expression, which isn't always a safe " +
                "mechanical substitution (a caller may use the same variable for other members that stay behind). Either make the member(s) " +
                "static first, or move to an existing base class of the source type.");
        }

        // Look for an existing (non-base) class named targetClassName, optionally narrowed by targetFilePath.
        var candidateDocs = targetFilePath != null
            ? solution.GetDocumentIdsWithFilePath(targetFilePath.Value).Select(solution.GetDocument).Where(d => d != null)
            : solution.Projects.SelectMany(p => p.Documents);

        Document? targetDoc = null;
        ClassDeclarationSyntax? targetClassNode = null;
        foreach (var doc in candidateDocs)
        {
            if (doc?.FilePath == null)
            {
                continue;
            }

            var docRoot = await doc.GetSyntaxRootAsync(cancellationToken);
            var candidate = docRoot?.DescendantNodes().OfType<ClassDeclarationSyntax>().FirstOrDefault(c => c.Identifier.Text == targetClassName);
            if (candidate != null)
            {
                targetDoc = doc;
                targetClassNode = candidate;
                break;
            }
        }

        if (targetDoc?.FilePath != null && targetClassNode != null)
        {
            var existingClassMemberSymbols = membersToMove
                .Select(m => semanticModel?.GetDeclaredSymbol(m is FieldDeclarationSyntax f ? f.Declaration.Variables.First() : m))
                .Where(s => s != null)
                .Cast<ISymbol>()
                .ToList();

            return await MoveMembersToExistingClassAsync(solution, filePath, root, classNode, membersToMove, targetDoc.FilePath, targetClassNode, targetClassName, existingClassMemberSymbols, cancellationToken);
        }

        var newClassMemberSymbols = membersToMove
            .Select(m => semanticModel?.GetDeclaredSymbol(m is FieldDeclarationSyntax f ? f.Declaration.Variables.First() : m))
            .Where(s => s != null)
            .Cast<ISymbol>()
            .ToList();

        return await MoveMembersToNewClassAsync(solution, filePath, root, classNode, membersToMove, targetClassName, newClassMemberSymbols, cancellationToken);
    }

    private static async Task<MoveMemberResult> MoveMembersToBaseTypeAsync(
        Solution solution,
        FilePath filePath,
        CompilationUnitSyntax root,
        ClassDeclarationSyntax classNode,
        List<MemberDeclarationSyntax> membersToMove,
        INamedTypeSymbol baseType,
        CancellationToken cancellationToken)
    {
        if (baseType.DeclaringSyntaxReferences.Length == 0)
        {
            throw new ToolNotFoundException("Base class is in an external assembly and cannot be modified.");
        }

        var baseFile = baseType.DeclaringSyntaxReferences.FirstOrDefault()?.SyntaxTree.FilePath;
        if (baseFile == null)
        {
            throw new ToolNotFoundException("Base class source file not found.");
        }

        var baseDoc = solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => d.FilePath == baseFile);
        if (baseDoc == null)
        {
            throw new ToolNotFoundException($"Base class source document not found at '{baseFile}'.");
        }

        var baseRoot = await baseDoc.GetSyntaxRootAsync(cancellationToken);
        if (baseRoot == null)
        {
            throw new ToolNotFoundException($"Failed to get syntax root for base class file '{baseFile}'.");
        }

        static SyntaxTokenList AdjustModifiers(SyntaxTokenList modifiers)
        {
            var overrideToken = modifiers.FirstOrDefault(m => m.IsKind(SyntaxKind.OverrideKeyword));
            if (overrideToken != default)
            {
                modifiers = modifiers.Remove(overrideToken);
            }

            if (!modifiers.Any(m => m.IsKind(SyntaxKind.VirtualKeyword) || m.IsKind(SyntaxKind.AbstractKeyword)))
            {
                modifiers = modifiers.Add(SyntaxFactory.Token(SyntaxKind.VirtualKeyword).WithLeadingTrivia(SyntaxFactory.Space));
            }

            return modifiers;
        }

        var membersForBase = membersToMove.Select(member => member switch
        {
            MethodDeclarationSyntax m => (MemberDeclarationSyntax)m.WithModifiers(AdjustModifiers(m.Modifiers)),
            PropertyDeclarationSyntax p => p.WithModifiers(AdjustModifiers(p.Modifiers)),
            _ => member
        }).ToArray();

        // Base and derived class routinely live in the same file (a small hierarchy kept together
        // rather than split one-type-per-file). When they do, root and baseRoot are the same tree,
        // so removing the members and adding them to the base class must happen against a single
        // root passed through both edits.
        if (filePath == baseFile)
        {
            var combinedRoot = root.RemoveNodes(membersToMove, SyntaxRemoveOptions.KeepUnbalancedDirectives);
            if (combinedRoot == null)
            {
                throw new ToolNotFoundException("Failed to remove member(s) from derived class.");
            }

            var baseClassAfterRemoval = combinedRoot.DescendantNodes().OfType<ClassDeclarationSyntax>()
                .FirstOrDefault(c => c.Identifier.Text == baseType.Name);
            if (baseClassAfterRemoval == null)
            {
                throw new ToolNotFoundException($"Base class '{baseType.Name}' not found in '{baseFile}' after removing member(s).");
            }

            var combinedBaseClassNode = baseClassAfterRemoval.AddMembers(membersForBase);
            var finalRoot = combinedRoot.ReplaceNode(baseClassAfterRemoval, combinedBaseClassNode);

            return new MoveMemberResult(new Dictionary<FilePath, string>
            {
                { filePath, finalRoot.NormalizeWhitespace().ToFullString() }
            }, new List<SkippedCallSite>());
        }

        var newDerivedRoot = root.RemoveNodes(membersToMove, SyntaxRemoveOptions.KeepUnbalancedDirectives);
        if (newDerivedRoot == null)
        {
            throw new ToolNotFoundException("Failed to remove member(s) from derived class.");
        }

        var baseClassNode = baseRoot.DescendantNodes().OfType<ClassDeclarationSyntax>().FirstOrDefault(c => c.Identifier.Text == baseType.Name);
        if (baseClassNode == null)
        {
            throw new ToolNotFoundException($"Base class '{baseType.Name}' not found in '{baseFile}'.");
        }

        var newBaseClassNode = baseClassNode.AddMembers(membersForBase);
        var newBaseRoot = baseRoot.ReplaceNode(baseClassNode, newBaseClassNode);

        return new MoveMemberResult(new Dictionary<FilePath, string>
        {
            { filePath, newDerivedRoot.NormalizeWhitespace().ToFullString() },
            { baseFile, newBaseRoot.NormalizeWhitespace().ToFullString() }
        }, new List<SkippedCallSite>());
    }

    /// <summary>
    /// Moves STATIC members into an existing, unrelated class. Static-only because the call-site
    /// rewrite is then unambiguous everywhere (ClassA.Foo() → TargetClassName.Foo(), no receiver
    /// instance involved) — MoveMemberAsync's caller already guarantees every member here is static.
    /// </summary>
    private static async Task<MoveMemberResult> MoveMembersToExistingClassAsync(
        Solution solution,
        FilePath filePath,
        CompilationUnitSyntax root,
        ClassDeclarationSyntax classNode,
        List<MemberDeclarationSyntax> membersToMove,
        FilePath targetFilePath,
        ClassDeclarationSyntax targetClassNode,
        string targetClassName,
        List<ISymbol> memberSymbols,
        CancellationToken cancellationToken)
    {
        bool sameFile = string.Equals(Path.GetFullPath(filePath), Path.GetFullPath(targetFilePath), StringComparison.OrdinalIgnoreCase);

        var movedNames = new HashSet<string>(membersToMove.SelectMany(m => m switch
        {
            MethodDeclarationSyntax meth => new[] { meth.Identifier.Text },
            PropertyDeclarationSyntax prop => new[] { prop.Identifier.Text },
            FieldDeclarationSyntax field => field.Declaration.Variables.Select(v => v.Identifier.Text).ToArray(),
            _ => Array.Empty<string>()
        }), StringComparer.Ordinal);

        var newTargetClassNode = targetClassNode.AddMembers(membersToMove.ToArray());
        var updatedSourceClass = classNode.RemoveNodes(membersToMove, SyntaxRemoveOptions.KeepNoTrivia)!;

        // Rewrite bare Member()/ClassName.Member() within the remaining source class to TargetClassName.Member().
        // (No `this.Member()` case: static members can't be accessed via `this`.)
        var bareIdentifiers = updatedSourceClass.DescendantNodes()
            .OfType<IdentifierNameSyntax>()
            .Where(id => movedNames.Contains(id.Identifier.Text) && id.Parent is not MemberAccessExpressionSyntax && id.Parent is not QualifiedNameSyntax)
            .ToList();
        if (bareIdentifiers.Count > 0)
        {
            updatedSourceClass = updatedSourceClass.ReplaceNodes(bareIdentifiers, (original, _) =>
                SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, SyntaxFactory.IdentifierName(targetClassName), (SimpleNameSyntax)original));
        }

        var result = new Dictionary<FilePath, string>();

        if (sameFile)
        {
            var afterSourceEdit = root.ReplaceNode(classNode, updatedSourceClass);
            var targetAfterSourceEdit = afterSourceEdit.DescendantNodes().OfType<ClassDeclarationSyntax>().FirstOrDefault(c => c.Identifier.Text == targetClassName);
            var finalRoot = targetAfterSourceEdit != null
                ? afterSourceEdit.ReplaceNode(targetAfterSourceEdit, targetAfterSourceEdit.AddMembers(membersToMove.ToArray()))
                : afterSourceEdit;
            result[filePath] = finalRoot.NormalizeWhitespace().ToFullString();
        }
        else
        {
            var targetDocument = solution.GetDocumentIdsWithFilePath(targetFilePath).Select(solution.GetDocument).First()!;
            var targetRoot = await targetDocument.GetSyntaxRootAsync(cancellationToken);
            var newTargetRoot = targetRoot!.ReplaceNode(targetClassNode, newTargetClassNode);

            result[filePath] = root.ReplaceNode(classNode, updatedSourceClass).NormalizeWhitespace().ToFullString();
            result[targetFilePath] = newTargetRoot.NormalizeWhitespace().ToFullString();
        }

        // Cross-file call sites: ClassA.Foo() → TargetClassName.Foo() — unambiguous since Foo is static.
        var skipPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { filePath, targetFilePath };
        foreach (var symbol in memberSymbols)
        {
            var references = await SymbolFinder.FindReferencesAsync(symbol, solution, cancellationToken);
            var byDocument = references.SelectMany(r => r.Locations)
                .Where(l => l.Document.FilePath != null && !skipPaths.Contains(l.Document.FilePath))
                .GroupBy(l => l.Document.Id)
                .ToDictionary(g => g.Key, g => g.ToList());

            foreach (var (docId, locs) in byDocument)
            {
                var doc = solution.GetDocument(docId);
                if (doc?.FilePath == null)
                {
                    continue;
                }

                SyntaxNode? docRoot = result.TryGetValue(doc.FilePath, out var already)
                    ? CSharpSyntaxTree.ParseText(already, cancellationToken: cancellationToken).GetRoot(cancellationToken)
                    : await doc.GetSyntaxRootAsync(cancellationToken);
                if (docRoot == null)
                {
                    continue;
                }

                var spans = locs.Select(l => l.Location.SourceSpan).ToHashSet();
                var identifiers = docRoot.DescendantNodes().OfType<SimpleNameSyntax>().Where(n => spans.Contains(n.Span)).ToList();
                var memberAccesses = identifiers.Select(id => id.Parent as MemberAccessExpressionSyntax).Where(ma => ma != null).Cast<MemberAccessExpressionSyntax>().Distinct().ToList();
                if (memberAccesses.Count == 0)
                {
                    continue;
                }

                var updatedDocRoot = docRoot.ReplaceNodes(memberAccesses, (original, _) =>
                    original.WithExpression(SyntaxFactory.IdentifierName(targetClassName)));
                result[doc.FilePath] = updatedDocRoot.NormalizeWhitespace().ToFullString();
            }
        }

        return new MoveMemberResult(result, new List<SkippedCallSite>());
    }

    private static async Task<MoveMemberResult> MoveMembersToNewClassAsync(
        Solution solution,
        FilePath filePath,
        CompilationUnitSyntax root,
        ClassDeclarationSyntax classNode,
        List<MemberDeclarationSyntax> membersToMove,
        string newClassName,
        List<ISymbol> memberSymbols,
        CancellationToken cancellationToken)
    {
        var ns = classNode.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault();
        var newClassNode = SyntaxFactory.ClassDeclaration(newClassName)
            .WithModifiers(SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.PublicKeyword)))
            .WithMembers(SyntaxFactory.List(membersToMove));
        var cleanUsings = SyntaxFactory.List(root.Usings.Select(u =>
            u.WithoutTrailingTrivia().WithTrailingTrivia(SyntaxFactory.ElasticCarriageReturnLineFeed)));
        CompilationUnitSyntax newFileRoot;
        if (ns != null)
        {
            BaseNamespaceDeclarationSyntax newNs = ns is FileScopedNamespaceDeclarationSyntax
                ? SyntaxFactory.FileScopedNamespaceDeclaration(ns.Name).AddMembers(newClassNode)
                : (BaseNamespaceDeclarationSyntax)SyntaxFactory.NamespaceDeclaration(ns.Name).AddMembers(newClassNode);
            newFileRoot = SyntaxFactory.CompilationUnit().WithUsings(cleanUsings).AddMembers(newNs);
        }
        else
        {
            newFileRoot = SyntaxFactory.CompilationUnit().WithUsings(cleanUsings).AddMembers(newClassNode);
        }

        var memberNameSet = new HashSet<string>(
            membersToMove.SelectMany(m => m switch
            {
                MethodDeclarationSyntax meth => new[] { meth.Identifier.Text },
                PropertyDeclarationSyntax prop => new[] { prop.Identifier.Text },
                FieldDeclarationSyntax field => field.Declaration.Variables.Select(v => v.Identifier.Text).ToArray(),
                _ => Array.Empty<string>()
            }), StringComparer.Ordinal);

        // Static members only (guaranteed by MoveMemberAsync's caller) — no `this.Member()` case to
        // rewrite, and no accessor property needed; bare Member() becomes NewClassName.Member() directly.
        var updatedSourceClass = classNode.RemoveNodes(membersToMove, SyntaxRemoveOptions.KeepNoTrivia)!;

        var bareIdentifiers = updatedSourceClass.DescendantNodes()
            .OfType<IdentifierNameSyntax>()
            .Where(id => memberNameSet.Contains(id.Identifier.Text) && id.Parent is not MemberAccessExpressionSyntax && id.Parent is not QualifiedNameSyntax)
            .ToList();
        if (bareIdentifiers.Count > 0)
        {
            updatedSourceClass = updatedSourceClass.ReplaceNodes(bareIdentifiers, (original, _) =>
                SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, SyntaxFactory.IdentifierName(newClassName), (SimpleNameSyntax)original));
        }

        var updatedRoot = root.ReplaceNode(classNode, updatedSourceClass);
        var newFilePath = Path.Combine(Path.GetDirectoryName(filePath)!, $"{newClassName}.cs");

        var result = new Dictionary<FilePath, string>
        {
            { newFilePath, newFileRoot.NormalizeWhitespace().ToFullString() },
            { filePath, updatedRoot.NormalizeWhitespace().ToFullString() }
        };

        // Cross-file call sites: ClassA.Foo() → NewClassName.Foo() — unambiguous since Foo is static.
        var skipPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { filePath, newFilePath };
        foreach (var symbol in memberSymbols)
        {
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

                SyntaxNode? docRoot = result.TryGetValue(doc.FilePath, out var alreadyModified)
                    ? CSharpSyntaxTree.ParseText(alreadyModified, cancellationToken: cancellationToken).GetRoot(cancellationToken)
                    : await doc.GetSyntaxRootAsync(cancellationToken);
                if (docRoot == null)
                {
                    continue;
                }

                var spans = locations.Select(l => l.Location.SourceSpan).ToHashSet();
                var identifiers = docRoot.DescendantNodes().OfType<SimpleNameSyntax>().Where(n => spans.Contains(n.Span)).ToList();
                var memberAccesses = identifiers.Select(id => id.Parent as MemberAccessExpressionSyntax).Where(ma => ma != null).Cast<MemberAccessExpressionSyntax>().Distinct().ToList();
                if (memberAccesses.Count == 0)
                {
                    continue;
                }

                var updatedDocRoot = docRoot.ReplaceNodes(memberAccesses, (original, _) =>
                    original.WithExpression(SyntaxFactory.IdentifierName(newClassName)));
                result[doc.FilePath] = updatedDocRoot.NormalizeWhitespace().ToFullString();
            }
        }

        return new MoveMemberResult(result, new List<SkippedCallSite>());
    }

    /// <summary>
    /// Inlines a class by moving all its members into the first class of the target file,
    /// then removes the source class declaration. Also renames all type references to the
    /// inlined class across the solution to point to the target class name.
    /// </summary>
    public async Task<Dictionary<FilePath, string>> InlineClassAsync(string sourceFilePath, string targetFilePath, string className, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetCurrentSolutionAsync(cancellationToken);
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
        CancellationToken cancellationToken = default)
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
