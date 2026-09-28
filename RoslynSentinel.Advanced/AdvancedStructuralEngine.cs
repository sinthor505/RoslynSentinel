using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Text;

using RoslynSentinel.Common;

namespace RoslynSentinel.Advanced;

public record MoveMemberResult(Dictionary<FilePathWrapper, string> Changes, List<SkippedCallSite> SkippedCallSites, List<CallSiteLedgerEntry>? PendingLedgerEntries = null, string? PendingLedgerOperationName = null, List<AppliedCallSiteFixup>? AppliedFixups = null);

/// <summary>
/// One call site rewritten from a caller-supplied callSiteFixups entry (not an auto-resolved one).
/// <paramref name="Line"/> is the 1-based line in the PROPOSED (post-rewrite, post-normalization)
/// content, so it lines up with the compile gate's diagnostics - the MoveMember tool uses it to tell
/// the caller when a rejection's errors sit on lines their own fixup value produced.
/// </summary>
public record AppliedCallSiteFixup(string FilePath, int Line, string FixupKey, string FixupValue);

public class AdvancedStructuralEngine
{
    private readonly IWorkspaceManager _workspaceManager;

    private readonly ValidationEngine? _validationEngine;

    public AdvancedStructuralEngine(IWorkspaceManager workspaceManager, ValidationEngine? validationEngine = null)
    {
        _workspaceManager = workspaceManager;
        _validationEngine = validationEngine;
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

        changes[Path.Combine(Path.GetDirectoryName(firstFile)!, $"{newBaseClassName}.cs")] = RoslynFormattingHelper.NormalizeWholeSubtreeWhitespace(baseUnit).ToFullString();
        return changes;
    }

    /// <summary>
    /// Moves named members from a class into a target class atomically -> one combined change set,
    /// so it validates and writes as a single unit instead of the remove-then-add sequence that
    /// otherwise fails the write-path chokepoint's per-write compiler check (source ends up with
    /// dangling references before the add lands).
    ///
    /// Three destination modes, chosen automatically from targetClassName:
    ///  - Existing base type of the source class: reuses PullUpMember's modifier adjustment
    ///    (removes override, adds virtual) and skips call-site rewriting -> virtual dispatch means
    ///    existing call sites keep working unchanged. Instance OR static members are both fine here.
    ///  - Existing unrelated class: moves the declaration as-is and rewrites call sites solution-wide
    ///    (ClassA.Foo() -> ClassB.Foo()). STATIC MEMBERS ONLY -> an instance member has no such
    ///    unambiguous rewrite (a call site may use the same source-class variable for other members
    ///    that stay behind, so there's no single correct receiver substitution); those are rejected
    ///    up front with a ToolNotFoundException rather than attempting a partial/guessed rewrite.
    ///  - No existing class named targetClassName: synthesizes a new class (same behavior as the
    ///    former ExtractMembers as=class). STATIC MEMBERS ONLY, same reasoning as above.
    /// </summary>
    public async Task<MoveMemberResult> MoveMemberAsync(FilePathWrapper filePath, string className, string[] memberNames, string targetClassName, FilePathWrapper? targetFilePath = null, CancellationToken cancellationToken = default, bool autoResolveCallSites = true, Dictionary<string, string>? callSiteFixups = null)
    {
        var solution = await _workspaceManager.GetSolutionAsync(ReadSource.Committed, cancellationToken);
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
        // every call site's receiver expression. autoResolveCallSites (default true) drives that
        // rewrite through MoveInstanceMembersAsync: unambiguous sites are fixed automatically, and
        // anything left over must be named in callSiteFixups or the whole call is rejected -> see
        // Decision 4 of docs/current/plans/plan_scoped_operation_ledger.md. STATIC members have no
        // such ambiguity (ClassA.Foo() -> ClassB.Foo() is unambiguous everywhere) and always use the
        // direct rewrite path below.
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

        // Look for an existing (non-base) class named targetClassName, optionally narrowed by targetFilePath.
        var candidateDocs = targetFilePath.HasValue
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

        if (nonStaticMembers.Count > 0)
        {
            if (!autoResolveCallSites)
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
                    "Moving an instance member elsewhere requires rewriting every call site's receiver expression. Pass " +
                    "autoResolveCallSites:true (the default) to have this resolved automatically where unambiguous, or " +
                    "make the member(s) static first, or move to an existing base class of the source type.");
            }

            return await MoveInstanceMembersAsync(solution, filePath, className, membersToMove, targetClassName, memberNames, targetFilePath, targetDoc, targetClassNode, callSiteFixups, cancellationToken);
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
        FilePathWrapper filePath,
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

            return new MoveMemberResult(new Dictionary<FilePathWrapper, string>
            {
                { filePath, RoslynFormattingHelper.NormalizeWholeSubtreeWhitespace(finalRoot).ToFullString() }
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

        return new MoveMemberResult(new Dictionary<FilePathWrapper, string>
        {
            { filePath, RoslynFormattingHelper.NormalizeWholeSubtreeWhitespace(newDerivedRoot).ToFullString() },
            { baseFile, RoslynFormattingHelper.NormalizeWholeSubtreeWhitespace(newBaseRoot).ToFullString() }
        }, new List<SkippedCallSite>());
    }

    /// <summary>
    /// Moves STATIC members into an existing, unrelated class. Static-only because the call-site
    /// rewrite is then unambiguous everywhere (ClassA.Foo() -> TargetClassName.Foo(), no receiver
    /// instance involved) -> MoveMemberAsync's caller already guarantees every member here is static.
    /// </summary>
    private static async Task<MoveMemberResult> MoveMembersToExistingClassAsync(
        Solution solution,
        FilePathWrapper filePath,
        CompilationUnitSyntax root,
        ClassDeclarationSyntax classNode,
        List<MemberDeclarationSyntax> membersToMove,
        FilePathWrapper targetFilePath,
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

        var result = new Dictionary<FilePathWrapper, string>();

        if (sameFile)
        {
            var afterSourceEdit = root.ReplaceNode(classNode, updatedSourceClass);
            var targetAfterSourceEdit = afterSourceEdit.DescendantNodes().OfType<ClassDeclarationSyntax>().FirstOrDefault(c => c.Identifier.Text == targetClassName);
            var finalRoot = targetAfterSourceEdit != null
                ? afterSourceEdit.ReplaceNode(targetAfterSourceEdit, targetAfterSourceEdit.AddMembers(membersToMove.ToArray()))
                : afterSourceEdit;
            result[filePath] = RoslynFormattingHelper.NormalizeWholeSubtreeWhitespace(finalRoot).ToFullString();
        }
        else
        {
            var targetDocument = solution.GetDocumentIdsWithFilePath(targetFilePath).Select(solution.GetDocument).First()!;
            var targetRoot = await targetDocument.GetSyntaxRootAsync(cancellationToken);
            var newTargetRoot = targetRoot!.ReplaceNode(targetClassNode, newTargetClassNode);

            result[filePath] = RoslynFormattingHelper.NormalizeWholeSubtreeWhitespace(root.ReplaceNode(classNode, updatedSourceClass)).ToFullString();
            result[targetFilePath] = RoslynFormattingHelper.NormalizeWholeSubtreeWhitespace(newTargetRoot).ToFullString();
        }

        // Cross-file call sites: ClassA.Foo() -> TargetClassName.Foo() -> unambiguous since Foo is static.
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
                result[doc.FilePath] = RoslynFormattingHelper.NormalizeWholeSubtreeWhitespace(updatedDocRoot).ToFullString();
            }
        }

        return new MoveMemberResult(result, new List<SkippedCallSite>());
    }

    private static async Task<MoveMemberResult> MoveMembersToNewClassAsync(
        Solution solution,
        FilePathWrapper filePath,
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

        // Static members only (guaranteed by MoveMemberAsync's caller) -> no `this.Member()` case to
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

        var result = new Dictionary<FilePathWrapper, string>
        {
            { newFilePath, RoslynFormattingHelper.NormalizeWholeSubtreeWhitespace(newFileRoot).ToFullString() },
            { filePath, RoslynFormattingHelper.NormalizeWholeSubtreeWhitespace(updatedRoot).ToFullString() }
        };

        // Cross-file call sites: ClassA.Foo() -> NewClassName.Foo() -> unambiguous since Foo is static.
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
                result[doc.FilePath] = RoslynFormattingHelper.NormalizeWholeSubtreeWhitespace(updatedDocRoot).ToFullString();
            }
        }

        return new MoveMemberResult(result, new List<SkippedCallSite>());
    }

    /// <summary>
    /// Inlines a class by moving all its members into the first class of the target file,
    /// then removes the source class declaration. Also renames all type references to the
    /// inlined class across the solution to point to the target class name.
    /// </summary>
    public async Task<Dictionary<FilePathWrapper, string>> InlineClassAsync(string sourceFilePath, string targetFilePath, string className, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetSolutionAsync(ReadSource.Committed, cancellationToken);
        bool sameFile = string.Equals(
            Path.GetFullPath(sourceFilePath),
            Path.GetFullPath(targetFilePath),
            StringComparison.OrdinalIgnoreCase);

        // Load source document
        var sourceDoc = solution.GetDocumentIdsWithFilePath(sourceFilePath)
            .Select(solution.GetDocument).FirstOrDefault();
        if (sourceDoc == null)
        {
            return new Dictionary<FilePathWrapper, string>
            {
                { "__error__", $"Source file '{Path.GetFileName(sourceFilePath)}' not found in solution." }
            };
        }

        var sourceRoot = await sourceDoc.GetSyntaxRootAsync(cancellationToken) as CompilationUnitSyntax;
        if (sourceRoot == null)
        {
            return new Dictionary<FilePathWrapper, string>();
        }

        var sourceClass = sourceRoot.DescendantNodes()
            .OfType<ClassDeclarationSyntax>()
            .FirstOrDefault(c => c.Identifier.Text == className);
        if (sourceClass == null)
        {
            return new Dictionary<FilePathWrapper, string>
            {
                { "__error__", $"Class '{className}' not found in '{Path.GetFileName(sourceFilePath)}'." }
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
            var targetClass = sourceRoot.DescendantNodes()
                .OfType<ClassDeclarationSyntax>()
                .FirstOrDefault(c => c.Identifier.Text != className);
            if (targetClass == null)
            {
                return new Dictionary<FilePathWrapper, string>
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

            result[sourceFilePath] = RoslynFormattingHelper.NormalizeWholeSubtreeWhitespace(newRoot).ToFullString();

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
                return new Dictionary<FilePathWrapper, string>
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
                return new Dictionary<FilePathWrapper, string>
                {
                    { "__error__", $"No class found in target file '{Path.GetFileName(targetFilePath)}'." }
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
        Dictionary<FilePathWrapper, string> result,
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

            result[doc.FilePath] = RoslynFormattingHelper.NormalizeWholeSubtreeWhitespace(updatedRoot).ToFullString();
        }
    }

    // Added by AddMember (expected - used for diagnostics)
    private static ITypeSymbol? GetSymbolType(ISymbol symbol) => symbol switch
    {
        IFieldSymbol f => f.Type,
        IPropertySymbol p => p.Type,
        IParameterSymbol pa => pa.Type,
        ILocalSymbol l => l.Type,
        _ => null
    };

    // Added by AddMember (expected - used for diagnostics)
    /// <summary>
    /// Reporting-only preview for moving instance member(s) to a destination that is not an existing
    /// base type (the one case MoveMemberAsync itself still rejects outright). Does not write anything
    /// and does not lift MoveMemberAsync's static-only guard -> see
    /// docs/current/proposal_movemember_instance_callsite_resolution.md sections 1, 5, 6.
    ///
    /// For each call site of the member(s) being moved: builds an in-memory trial change set with the
    /// member relocated but that call site NOT rewritten, runs it through ValidationEngine so the
    /// compiler's own diagnostics decide whether the site breaks (rather than a hand-rolled prediction),
    /// then classifies broken sites by scanning for in-scope fields/properties/parameters of the
    /// destination type via SemanticModel.LookupSymbols (which already respects C# shadowing).
    /// </summary>
    public async Task<List<PreviewCallSite>> PreviewInstanceMoveCallSitesAsync(FilePathWrapper filePath, string className, string[] memberNames, string targetClassName, FilePathWrapper? targetFilePath = null, CancellationToken cancellationToken = default)
    {
        if (_validationEngine == null)
        {
            throw new InvalidOperationException("PreviewInstanceMoveCallSitesAsync requires a ValidationEngine - this AdvancedStructuralEngine instance was constructed without one.");
        }

        var solution = await _workspaceManager.GetSolutionAsync(ReadSource.Committed, cancellationToken);
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
        if (semanticModel == null)
        {
            throw new ToolNotFoundException($"Failed to get semantic model for '{filePath}'.");
        }

        var memberSymbols = membersToMove
            .Select(m => semanticModel.GetDeclaredSymbol(m is FieldDeclarationSyntax f ? f.Declaration.Variables.First() : m))
            .Where(s => s != null)
            .Cast<ISymbol>()
            .ToList();

        INamedTypeSymbol? destinationType = null;
        Document? destinationDoc = null;
        ClassDeclarationSyntax? destinationClassNode = null;
        foreach (var doc in solution.Projects.SelectMany(p => p.Documents))
        {
            var docRoot = await doc.GetSyntaxRootAsync(cancellationToken);
            var candidate = docRoot?.DescendantNodes().OfType<ClassDeclarationSyntax>().FirstOrDefault(c => c.Identifier.Text == targetClassName);
            if (candidate == null)
            {
                continue;
            }

            var candidateModel = await doc.GetSemanticModelAsync(cancellationToken);
            destinationType = candidateModel?.GetDeclaredSymbol(candidate, cancellationToken) as INamedTypeSymbol;
            if (destinationType != null)
            {
                destinationDoc = doc;
                destinationClassNode = candidate;
                break;
            }
        }

        // previewChanges must reflect the COMPLETE post-move state (member removed from the source
        // class AND added to the destination class), not just the removal -> otherwise the trial
        // compile does not reproduce the same diagnostics the real two-file apply would produce, and
        // every call site gets misclassified as already-Valid. See
        // docs/current/blockers/blocking_error_movemember_instance_callsite_not_rewritten.md.
        var updatedSourceClass = classNode.RemoveNodes(membersToMove, SyntaxRemoveOptions.KeepNoTrivia)!;
        var previewChanges = new Dictionary<FilePathWrapper, string>();

        if (destinationDoc?.FilePath != null && destinationClassNode != null)
        {
            bool sameFile = string.Equals(Path.GetFullPath(filePath), Path.GetFullPath(destinationDoc.FilePath), StringComparison.OrdinalIgnoreCase);
            if (sameFile)
            {
                var afterSourceEdit = root.ReplaceNode(classNode, updatedSourceClass);
                var targetAfterSourceEdit = afterSourceEdit.DescendantNodes().OfType<ClassDeclarationSyntax>().FirstOrDefault(c => c.Identifier.Text == targetClassName);
                var finalRoot = targetAfterSourceEdit != null
                    ? afterSourceEdit.ReplaceNode(targetAfterSourceEdit, targetAfterSourceEdit.AddMembers(membersToMove.ToArray()))
                    : afterSourceEdit;
                previewChanges[filePath] = RoslynFormattingHelper.NormalizeWholeSubtreeWhitespace(finalRoot).ToFullString();
            }
            else
            {
                var destinationRoot = await destinationDoc.GetSyntaxRootAsync(cancellationToken);
                var updatedDestinationRoot = destinationRoot!.ReplaceNode(destinationClassNode, destinationClassNode.AddMembers(membersToMove.ToArray()));
                previewChanges[filePath] = RoslynFormattingHelper.NormalizeWholeSubtreeWhitespace(root.ReplaceNode(classNode, updatedSourceClass)).ToFullString();
                previewChanges[destinationDoc.FilePath] = RoslynFormattingHelper.NormalizeWholeSubtreeWhitespace(updatedDestinationRoot).ToFullString();
            }
        }
        else
        {
            previewChanges[filePath] = RoslynFormattingHelper.NormalizeWholeSubtreeWhitespace(root.ReplaceNode(classNode, updatedSourceClass)).ToFullString();
        }

        var validation = await _validationEngine.ValidateChangesAsync(previewChanges, cancellationToken);

        var results = new List<PreviewCallSite>();
        var movingMemberDeclarationSpans = new HashSet<TextSpan>(membersToMove.Select(m => m.Span));

        foreach (var symbol in memberSymbols)
        {
            var references = await SymbolFinder.FindReferencesAsync(symbol, solution, cancellationToken);
            var locations = references.SelectMany(r => r.Locations)
                .Where(l => l.Document.FilePath != null)
                .ToList();

            foreach (var refLocation in locations)
            {
                var refDoc = refLocation.Document;
                var refRoot = await refDoc.GetSyntaxRootAsync(cancellationToken);
                var refNode = refRoot?.FindNode(refLocation.Location.SourceSpan);
                var memberAccess = refNode?.Ancestors().OfType<MemberAccessExpressionSyntax>().FirstOrDefault(ma => ma.Name.Span.Contains(refLocation.Location.SourceSpan))
                    ?? refNode?.Parent as MemberAccessExpressionSyntax;

                var lineSpan = refLocation.Location.GetLineSpan();
                var callExpression = memberAccess?.ToString() ?? refNode?.ToString() ?? symbol.Name;

                // TextSpan carries no document identity, so this must not compare spans across
                // documents: a call site in an unrelated (often short) file can coincidentally
                // fall inside the same byte-offset range as the moved method's declaration span
                // in the SOURCE file, producing a false MoveOrderDependent classification (seen
                // for BatteryFifteenTests.cs/BatteryThirtyTests.cs call sites during MoveMember
                // testing). Only the source document's own span comparisons are meaningful.
                bool isSameDocAsSource = string.Equals(refDoc.FilePath, document.FilePath, StringComparison.OrdinalIgnoreCase);
                var isMoveOrderDependent = isSameDocAsSource && refNode != null && movingMemberDeclarationSpans.Any(span => span.Contains(refNode.Span));

                var brokenHere = validation.Diagnostics.Any(d => string.Equals(d.FilePath, refDoc.FilePath, StringComparison.OrdinalIgnoreCase) && d.StartLine == lineSpan.StartLinePosition.Line + 1);

                if (!brokenHere)
                {
                    results.Add(new PreviewCallSite(refDoc.FilePath!, lineSpan.StartLinePosition.Line + 1, callExpression, CallSiteStatus.Valid, null, null, []));
                    continue;
                }

                if (isMoveOrderDependent)
                {
                    results.Add(new PreviewCallSite(refDoc.FilePath!, lineSpan.StartLinePosition.Line + 1, callExpression, CallSiteStatus.MoveOrderDependent,
                        "This call site is inside a method that is itself being moved in the same batch - whether it can be resolved depends on move order.", null, []));
                    continue;
                }

                if (destinationType == null)
                {
                    results.Add(new PreviewCallSite(refDoc.FilePath!, lineSpan.StartLinePosition.Line + 1, callExpression, CallSiteStatus.NoCandidateBlocked,
                        $"Destination type '{targetClassName}' could not be resolved.", null, []));
                    continue;
                }

                var refSemanticModel = await refDoc.GetSemanticModelAsync(cancellationToken);
                var refCompilation = refSemanticModel?.Compilation;
                bool sameAssembly = refCompilation != null && SymbolEqualityComparer.Default.Equals(refCompilation.Assembly, destinationType.ContainingAssembly);
                bool accessible = destinationType.DeclaredAccessibility == Accessibility.Public || sameAssembly;

                if (!accessible)
                {
                    results.Add(new PreviewCallSite(refDoc.FilePath!, lineSpan.StartLinePosition.Line + 1, callExpression, CallSiteStatus.NoCandidateBlocked,
                        $"Destination type '{targetClassName}' is not accessible from this call site's compilation context.", null, []));
                    continue;
                }

                var candidates = refSemanticModel == null || refNode == null
                    ? new List<string>()
                    : refSemanticModel.LookupSymbols(refNode.SpanStart)
                        .Where(s => (s is IFieldSymbol || s is IPropertySymbol || s is IParameterSymbol || s is ILocalSymbol) && SymbolEqualityComparer.Default.Equals(GetSymbolType(s), destinationType))
                        .Select(s => s.Name)
                        .Distinct()
                        .ToList();

                if (candidates.Count == 0)
                {
                    // SuggestedFix on a non-Valid row is human-facing prep-step prose, never a receiver
                    // expression: MoveInstanceMembersAsync only reads SuggestedFix as a receiver for
                    // Valid rows, and ledger entries merely echo it back to the caller.
                    results.Add(new PreviewCallSite(refDoc.FilePath!, lineSpan.StartLinePosition.Line + 1, callExpression, CallSiteStatus.NoCandidateIntroducible,
                        $"No in-scope reference of type '{targetClassName}' found at this call site. Add a 'using' directive or introduce a field/parameter of that type.",
                        BuildIntroduceFieldSuggestion(targetClassName, refNode, memberAccess), []));
                    continue;
                }

                if (candidates.Count == 1)
                {
                    // SuggestedFix (like callSiteFixups) is a receiver-only expression: the rewrite in
                    // MoveInstanceMembersAsync calls original.WithExpression(...) on the existing
                    // member-access node, which keeps its own .Name segment. A value that already
                    // includes ".{symbol.Name}" here produced a doubled method name on apply (e.g.
                    // "_apiAutomationEngine.AddValidationToPocoAsync.AddValidationToPocoAsync").
                    results.Add(new PreviewCallSite(refDoc.FilePath!, lineSpan.StartLinePosition.Line + 1, callExpression, CallSiteStatus.Valid, null,
                        candidates[0], candidates));
                    continue;
                }

                results.Add(new PreviewCallSite(refDoc.FilePath!, lineSpan.StartLinePosition.Line + 1, callExpression, CallSiteStatus.Ambiguous,
                    $"Multiple in-scope references of type '{targetClassName}' found; specify which one via callSiteFixups.", null, candidates));
            }
        }

        return results;
    }

    // Added by AddMember (expected - used for diagnostics)
    private async Task<MoveMemberResult> MoveInstanceMembersAsync(
        Solution solution,
        FilePathWrapper filePath,
        string className,
        List<MemberDeclarationSyntax> membersToMove,
        string targetClassName,
        string[] memberNames,
        FilePathWrapper? targetFilePath,
        Document? existingTargetDoc,
        ClassDeclarationSyntax? existingTargetClassNode,
        Dictionary<string, string>? callSiteFixups,
        CancellationToken cancellationToken)
    {
        var rows = await PreviewInstanceMoveCallSitesAsync(filePath, className, memberNames, targetClassName, targetFilePath, cancellationToken);
        var fixups = CallSiteFixupMap.Parse(callSiteFixups, _workspaceManager.GetSolutionRoot());

        // Caller-supplied fixups actually applied, keyed by CallerFixupSiteKey(file, original line).
        // Auto-resolved (Valid) rows are deliberately absent: only a caller's own value can be blamed
        // for errors on the line it rewrote.
        var callerFixupSources = new Dictionary<string, (string Key, string Value)>(StringComparer.OrdinalIgnoreCase);
        var appliedFixups = new List<AppliedCallSiteFixup>();

        var resolvedReceivers = new Dictionary<(string FilePath, int Line), string>();

        foreach (var row in rows)
        {
            if (row.Status == CallSiteStatus.Valid)
            {
                if (row.SuggestedFix != null)
                {
                    resolvedReceivers[(row.FilePath, row.Line)] = row.SuggestedFix;
                }

                continue;
            }

            if (fixups.Match(row.FilePath, row.Line) is { } fixup)
            {
                resolvedReceivers[(row.FilePath, row.Line)] = fixup.Value;
                callerFixupSources[CallerFixupSiteKey(row.FilePath, row.Line)] = fixup;
            }
        }

        // Refuse BEFORE building or validating any change set - a bad "new" would otherwise surface
        // only as one undifferentiated CS7036 per fixed-up site from the compile gate.
        await EnsureNewFixupsAreConstructibleAsync(callerFixupSources.Values, existingTargetDoc, existingTargetClassNode, cancellationToken);

        var unresolvedRows = rows.Where(r => r.Status != CallSiteStatus.Valid && !resolvedReceivers.ContainsKey((r.FilePath, r.Line))).ToList();
        var skippedCallSites = new List<SkippedCallSite>();
        List<CallSiteLedgerEntry>? pendingLedgerEntries = null;
        string? pendingLedgerOperationName = null;

        if (unresolvedRows.Count > 0)
        {
            // Deliberately NOT opened here via TryOpen: this method only returns a proposed change
            // set, it does not write anything. The caller (MoveMember MCP tool) applies Changes via
            // ValidateAndApplyAsync first, and only opens the ledger once that atomic write has
            // actually succeeded -> opening it here would make IsBlocked refuse the move's own
            // source/target file write in the same apply, since neither necessarily has an
            // unresolved ledger entry for itself (see Decision 5 plan notes).
            pendingLedgerEntries = unresolvedRows.Select(r => new CallSiteLedgerEntry
            {
                EntryId = Guid.NewGuid().ToString("n")[..8],
                FilePath = r.FilePath,
                Line = r.Line,
                BrokenExpression = r.CallExpression,
                OldStaticType = className,
                Status = r.Status,
                BlockReason = r.BlockReason,
                SuggestedFix = r.SuggestedFix,
                CandidatesInScope = r.Candidates,
            }).ToList();
            pendingLedgerOperationName = $"MoveMember [{string.Join(", ", memberNames)}] '{className}' -> '{targetClassName}'";

            skippedCallSites = pendingLedgerEntries
                .Select(e => new SkippedCallSite(e.FilePath, e.Line, $"{e.Status}: {e.BlockReason} (ledger entry {e.EntryId})"))
                .ToList();
        }

        var document = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).First()!;
        var root = (CompilationUnitSyntax)(await document.GetSyntaxRootAsync(cancellationToken))!;
        var classNode = root.DescendantNodes().OfType<ClassDeclarationSyntax>().First(c => c.Identifier.Text == className);

        var updatedSourceClass = classNode.RemoveNodes(membersToMove, SyntaxRemoveOptions.KeepNoTrivia)!;
        var result = new Dictionary<FilePathWrapper, string>();

        if (existingTargetDoc?.FilePath != null && existingTargetClassNode != null)
        {
            bool sameFile = string.Equals(Path.GetFullPath(filePath), Path.GetFullPath(existingTargetDoc.FilePath), StringComparison.OrdinalIgnoreCase);
            if (sameFile)
            {
                var afterSourceEdit = root.ReplaceNode(classNode, updatedSourceClass);
                var targetAfterSourceEdit = afterSourceEdit.DescendantNodes().OfType<ClassDeclarationSyntax>().FirstOrDefault(c => c.Identifier.Text == targetClassName);
                var finalRoot = targetAfterSourceEdit != null
                    ? afterSourceEdit.ReplaceNode(targetAfterSourceEdit, targetAfterSourceEdit.AddMembers(membersToMove.ToArray()))
                    : afterSourceEdit;
                result[filePath] = RoslynFormattingHelper.NormalizeWholeSubtreeWhitespace(finalRoot).ToFullString();
            }
            else
            {
                var targetRoot = await existingTargetDoc.GetSyntaxRootAsync(cancellationToken);
                var newTargetRoot = targetRoot!.ReplaceNode(existingTargetClassNode, existingTargetClassNode.AddMembers(membersToMove.ToArray()));
                result[filePath] = RoslynFormattingHelper.NormalizeWholeSubtreeWhitespace(root.ReplaceNode(classNode, updatedSourceClass)).ToFullString();
                result[existingTargetDoc.FilePath] = RoslynFormattingHelper.NormalizeWholeSubtreeWhitespace(newTargetRoot).ToFullString();
            }
        }
        else
        {
            var ns = classNode.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault();
            var newClassNode = SyntaxFactory.ClassDeclaration(targetClassName)
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

            var newFilePath = Path.Combine(Path.GetDirectoryName(filePath)!, $"{targetClassName}.cs");
            result[newFilePath] = RoslynFormattingHelper.NormalizeWholeSubtreeWhitespace(newFileRoot).ToFullString();
            result[filePath] = RoslynFormattingHelper.NormalizeWholeSubtreeWhitespace(root.ReplaceNode(classNode, updatedSourceClass)).ToFullString();
        }

        foreach (var group in resolvedReceivers.GroupBy(kv => kv.Key.FilePath, StringComparer.OrdinalIgnoreCase))
        {
            var docFilePath = group.Key;
            var doc = solution.GetDocumentIdsWithFilePath(docFilePath).Select(solution.GetDocument).FirstOrDefault();
            if (doc == null)
            {
                continue;
            }

            SyntaxNode? docRoot = result.TryGetValue(docFilePath, out var already)
                ? CSharpSyntaxTree.ParseText(already, cancellationToken: cancellationToken).GetRoot(cancellationToken)
                : await doc.GetSyntaxRootAsync(cancellationToken);
            if (docRoot == null)
            {
                continue;
            }

            var linesToFix = group.ToDictionary(kv => kv.Key.Line, kv => kv.Value);
            var memberAccesses = docRoot.DescendantNodes()
                .OfType<MemberAccessExpressionSyntax>()
                .Where(ma => memberNames.Contains(ma.Name.Identifier.Text) && linesToFix.ContainsKey(ma.GetLocation().GetLineSpan().StartLinePosition.Line + 1))
                .ToList();

            SyntaxNode updatedDocRoot = docRoot;
            if (memberAccesses.Count > 0)
            {
                updatedDocRoot = updatedDocRoot.ReplaceNodes(memberAccesses, (original, _) =>
                {
                    var line = original.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                    var receiverExpr = linesToFix[line];
                    var newReceiver = receiverExpr == CallSiteFixupMap.NewKeyword
                        ? (ExpressionSyntax)SyntaxFactory.ObjectCreationExpression(SyntaxFactory.IdentifierName(targetClassName)).WithArgumentList(SyntaxFactory.ArgumentList())
                        : SyntaxFactory.ParseExpression(receiverExpr);
                    var rewritten = original.WithExpression(newReceiver);
                    var siteKey = CallerFixupSiteKey(docFilePath, line);
                    return callerFixupSources.ContainsKey(siteKey)
                        ? rewritten.WithAdditionalAnnotations(new SyntaxAnnotation(CallerFixupAnnotationKind, siteKey))
                        : rewritten;
                });
            }

            // Whole-subtree normalization can shift line numbers, so fixup-rewritten sites are located
            // by annotation on the FINAL tree - the same text the compile gate reports against.
            var normalizedDocRoot = RoslynFormattingHelper.NormalizeWholeSubtreeWhitespace(updatedDocRoot);
            foreach (var annotated in normalizedDocRoot.GetAnnotatedNodes(CallerFixupAnnotationKind))
            {
                var finalLine = annotated.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                foreach (var annotation in annotated.GetAnnotations(CallerFixupAnnotationKind))
                {
                    if (annotation.Data != null && callerFixupSources.TryGetValue(annotation.Data, out var source))
                    {
                        appliedFixups.Add(new AppliedCallSiteFixup(docFilePath, finalLine, source.Key, source.Value));
                    }
                }
            }

            result[docFilePath] = normalizedDocRoot.ToFullString();
        }

        return new MoveMemberResult(result, skippedCallSites, pendingLedgerEntries, pendingLedgerOperationName, appliedFixups);
    }

    private const string CallerFixupAnnotationKind = "RoslynSentinel.MoveMember.CallerFixup";

    private static string CallerFixupSiteKey(string filePath, int line) => $"{filePath}|{line}";


    /// <summary>
    /// Prep-step prose for a NoCandidateIntroducible call site: names the destination type, the type
    /// that needs the new field, and the receiver whose construction it should mirror. It points the
    /// caller at the current receiver's constructor arguments on purpose - a bare "new" or an inferred
    /// argument list can compile while silently dropping configuration that receiver was built with.
    /// </summary>
    private static string BuildIntroduceFieldSuggestion(string targetClassName, SyntaxNode? refNode, MemberAccessExpressionSyntax? memberAccess)
    {
        var containingType = refNode?.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault()?.Identifier.Text ?? "the calling type";
        var receiver = memberAccess?.Expression;
        var initialization = receiver == null || receiver is ThisExpressionSyntax
            ? "initialize it with the same constructor arguments the current instance was created with"
            : $"initialize it where the current receiver '{receiver}' is initialized, with the same constructor arguments";
        return $"Add a field of type {targetClassName} to {containingType} ({initialization}), then retry MoveMember - the call site will then auto-resolve.";
    }


    /// <summary>
    /// Enforces the documented "new" contract (docs/current/proposal_movemember_instance_callsite_resolution.md
    /// section 2): "new" emits exactly 'new Target()' and nothing more. If Target has no accessible
    /// instance constructor callable with zero arguments, refuse up front naming the offending keys and
    /// the real constructor signature(s), instead of letting every fixed-up site fail the compile gate
    /// with an undifferentiated CS7036. Deliberately does NOT infer constructor arguments from scope: a
    /// guessed argument list can compile while silently dropping configuration (e.g. a per-test
    /// SentinelConfiguration), which no compile gate can catch. A synthesized destination class (no
    /// existing target) always has an implicit public parameterless constructor, so it needs no check.
    /// </summary>
    private static async Task EnsureNewFixupsAreConstructibleAsync(
        IEnumerable<(string Key, string Value)> callerFixups,
        Document? existingTargetDoc,
        ClassDeclarationSyntax? existingTargetClassNode,
        CancellationToken cancellationToken)
    {
        var newKeys = callerFixups
            .Where(f => f.Value == CallSiteFixupMap.NewKeyword)
            .Select(f => f.Key)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (newKeys.Count == 0 || existingTargetDoc == null || existingTargetClassNode == null)
        {
            return;
        }

        var model = await existingTargetDoc.GetSemanticModelAsync(cancellationToken);
        if (model?.GetDeclaredSymbol(existingTargetClassNode, cancellationToken) is not INamedTypeSymbol targetType)
        {
            // Symbol unresolvable - leave it to the compile gate rather than refusing on a guess.
            return;
        }

        bool zeroArgCallable = !targetType.IsAbstract && !targetType.IsStatic && targetType.InstanceConstructors.Any(c =>
            c.DeclaredAccessibility is Accessibility.Public or Accessibility.Internal or Accessibility.ProtectedOrInternal &&
            c.Parameters.All(p => p.IsOptional || p.IsParams));
        if (zeroArgCallable)
        {
            return;
        }

        const int maxKeysShown = 3;
        var name = targetType.Name;
        var keyList = string.Join(", ", newKeys.Take(maxKeysShown).Select(k => $"\"{k}\"")) +
            (newKeys.Count > maxKeysShown ? $" (+{newKeys.Count - maxKeysShown} more)" : "");
        var signatures = targetType.InstanceConstructors.Length == 0
            ? "(none)"
            : string.Join("; ", targetType.InstanceConstructors.Select(c => FormatConstructorSignature(c, name)));
        var widest = targetType.InstanceConstructors.OrderByDescending(c => c.Parameters.Length).FirstOrDefault();
        var example = widest is { Parameters.Length: > 0 }
            ? $"new {name}({string.Join(", ", widest.Parameters.Select(p => $"<{p.Name}>"))})"
            : $"new {name}(<args>)";
        var kindNote = targetType.IsStatic ? " (it is static)" : targetType.IsAbstract ? " (it is abstract)" : "";

        throw new ToolInvalidArgumentException(
            $"{newKeys.Count} callSiteFixups key(s) use the value \"new\": {keyList}. \"new\" means a zero-argument constructor call only ('new {name}()'), " +
            $"but '{name}' has no accessible constructor callable with zero arguments{kindNote}. Its constructor(s): {signatures}. " +
            $"Pass a full receiver expression instead, e.g. \"{example}\", or the name of an existing in-scope field/property of type '{name}'. " +
            "Supply ALL constructor arguments explicitly, including configuration/options arguments: omitting an optional one can compile but silently change behavior. " +
            "No changes were made.");
    }


    private static string FormatConstructorSignature(IMethodSymbol constructor, string typeName)
    {
        var parameters = constructor.Parameters.Select(p =>
        {
            var text = (p.IsParams ? "params " : "") + p.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat) + " " + p.Name;
            if (p.HasExplicitDefaultValue)
            {
                text += " = " + (p.ExplicitDefaultValue switch
                {
                    null => "null",
                    string s => $"\"{s}\"",
                    bool b => b ? "true" : "false",
                    var v => Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture),
                });
            }

            return text;
        });
        return $"{SyntaxFacts.GetText(constructor.DeclaredAccessibility)} {typeName}({string.Join(", ", parameters)})".TrimStart();
    }


    /// <summary>
    /// Parsed MoveMember callSiteFixups. Per unresolved call site, precedence is: an exact
    /// "FilePath:Line" key, then "FilePath:*" (every unresolved site in that file), then "*" (every
    /// unresolved site anywhere). Paths match case-insensitively after Path.GetFullPath normalization;
    /// solution-relative paths resolve against the solution root. Keys and values are trimmed. The
    /// "new" shorthand is matched case-SENSITIVELY on purpose: "new" is a reserved keyword and can never
    /// be an identifier, whereas "New"/"NEW" are legal C# identifiers that could name a real field.
    /// </summary>
    private sealed class CallSiteFixupMap
    {
        public const string NewKeyword = "new";

        private readonly Dictionary<string, (string Key, string Value)> _exact = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, (string Key, string Value)> _perFile = new(StringComparer.OrdinalIgnoreCase);
        private (string Key, string Value)? _global;

        public static CallSiteFixupMap Parse(Dictionary<string, string>? fixups, string? solutionRoot)
        {
            var map = new CallSiteFixupMap();
            if (fixups == null)
            {
                return map;
            }

            var unusable = new List<string>();
            foreach (var (rawKey, rawValue) in fixups)
            {
                var key = (rawKey ?? string.Empty).Trim();
                var value = (rawValue ?? string.Empty).Trim();
                if (value.Length == 0)
                {
                    unusable.Add($"\"{rawKey}\" (empty value)");
                    continue;
                }

                if (key == "*")
                {
                    map._global = (key, value);
                    continue;
                }

                // Split on the LAST ':' - the first one is usually a drive letter.
                int colon = key.LastIndexOf(':');
                var fullPath = colon > 0 ? NormalizePath(key[..colon], solutionRoot) : null;
                var linePart = colon > 0 ? key[(colon + 1)..].Trim() : string.Empty;
                if (fullPath == null)
                {
                    unusable.Add($"\"{rawKey}\"");
                }
                else if (linePart == "*")
                {
                    map._perFile[fullPath] = (key, value);
                }
                else if (int.TryParse(linePart, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var line) && line > 0)
                {
                    map._exact[$"{fullPath}:{line}"] = (key, value);
                }
                else
                {
                    unusable.Add($"\"{rawKey}\"");
                }
            }

            if (unusable.Count > 0)
            {
                const int maxShown = 5;
                throw new ToolInvalidArgumentException(
                    $"{unusable.Count} callSiteFixups entr{(unusable.Count == 1 ? "y is" : "ies are")} unusable: {string.Join(", ", unusable.Take(maxShown))}" +
                    (unusable.Count > maxShown ? $" (+{unusable.Count - maxShown} more)" : "") +
                    ". Each key must be \"FilePath:Line\" (1-based line), \"FilePath:*\" (every unresolved call site in that file) or \"*\" (every unresolved call site), " +
                    "and each value a non-empty receiver expression or \"new\". No changes were made.");
            }

            return map;
        }

        /// <summary>The fixup for an unresolved call site, by precedence, or null if none applies.</summary>
        public (string Key, string Value)? Match(string filePath, int line)
        {
            var fullPath = NormalizePath(filePath, null) ?? filePath;
            if (_exact.TryGetValue($"{fullPath}:{line}", out var exact))
            {
                return exact;
            }

            if (_perFile.TryGetValue(fullPath, out var perFile))
            {
                return perFile;
            }

            return _global;
        }

        private static string? NormalizePath(string path, string? solutionRoot)
        {
            try
            {
                var wrapped = FilePathWrapper.FromWire(path, solutionRoot);
                return string.IsNullOrEmpty(wrapped.Absolute) ? null : Path.GetFullPath(wrapped.Absolute);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return null;
            }
        }
    }
}
