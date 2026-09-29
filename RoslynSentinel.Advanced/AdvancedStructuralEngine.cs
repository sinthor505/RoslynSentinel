using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Text;
using RoslynSentinel.Common;
using Microsoft.CodeAnalysis.Formatting;

namespace RoslynSentinel.Advanced;
public record MoveMemberResult(Dictionary<FilePathWrapper, string> Changes, List<SkippedCallSite> SkippedCallSites, List<CallSiteLedgerEntry>? PendingLedgerEntries = null, string? PendingLedgerOperationName = null, List<AppliedCallSiteFixup>? AppliedFixups = null);
/// <summary>
/// One call site rewritten from a caller-supplied callSiteFixups entry (not an auto-resolved one).
/// <paramref name = "Line"/> is the 1-based line in the PROPOSED (post-rewrite, post-normalization)
/// content, so it lines up with the compile gate's diagnostics - the MoveMember tool uses it to tell
/// the caller when a rejection's errors sit on lines their own fixup value produced.
/// </summary>
public record AppliedCallSiteFixup(string FilePath, int Line, string FixupKey, string FixupValue);
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

            if (m is BaseTypeDeclarationSyntax nestedType)
            {
                return memberNames.Contains(nestedType.Identifier.Text);
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
        // Resolved BEFORE RequireNoUnmovedDependencies so that guardrail can tell "must move with
        // this member" apart from "already satisfied by an existing same-name/compatible-type field
        // on the target" -> see RequireNoUnmovedDependencies's own doc comment for why this ordering
        // matters (moving it later reproduces the duplicate-field collision this exists to prevent).
        INamedTypeSymbol? targetClassSymbol = null;
        if (!targetIsBaseType)
        {
            var targetSearchDocs = targetFilePath.HasValue ? solution.GetDocumentIdsWithFilePath(targetFilePath.Value).Select(solution.GetDocument).Where(d => d != null) : solution.Projects.SelectMany(p => p.Documents);
            foreach (var doc in targetSearchDocs)
            {
                if (doc?.FilePath == null)
                {
                    continue;
                }

                var docRoot = await doc.GetSyntaxRootAsync(cancellationToken);
                var candidateNode = docRoot?.DescendantNodes().OfType<ClassDeclarationSyntax>().FirstOrDefault(c => c.Identifier.Text == targetClassName);
                if (candidateNode == null)
                {
                    continue;
                }

                var candidateModel = await doc.GetSemanticModelAsync(cancellationToken);
                targetClassSymbol = candidateModel?.GetDeclaredSymbol(candidateNode, cancellationToken) as INamedTypeSymbol;
                break;
            }
        }

        HashSet<string> alreadySatisfiedFields = new(StringComparer.Ordinal);
        if (classSymbol != null && semanticModel != null)
        {
            alreadySatisfiedFields = RequireNoUnmovedDependencies(classSymbol, semanticModel, membersToMove, memberNames, className, targetClassName, targetClassSymbol);
        }

        if (alreadySatisfiedFields.Count > 0)
        {
            // The dependency is satisfied by the target's own existing field of the same name/
            // compatible type -> exclude its declaration from the move so it is never duplicated
            // onto the target. The moved bodies already reference it by bare name, which resolves
            // correctly post-move against the target's pre-existing field - no rewrite needed.
            membersToMove = membersToMove.Where(m => !(m is FieldDeclarationSyntax fd && fd.Declaration.Variables.Any(v => alreadySatisfiedFields.Contains(v.Identifier.Text)))).ToList();
        }

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
            if (m is BaseTypeDeclarationSyntax)
            {
                return false;
            }

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
        var candidateDocs = targetFilePath.HasValue ? solution.GetDocumentIdsWithFilePath(targetFilePath.Value).Select(solution.GetDocument).Where(d => d != null) : solution.Projects.SelectMany(p => p.Documents);
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
                throw new ToolNotFoundException($"Cannot move instance member(s) [{names}] to '{targetClassName}': it is not an existing base class of '{className}'. " + "Moving an instance member elsewhere requires rewriting every call site's receiver expression. Pass " + "autoResolveCallSites:true (the default) to have this resolved automatically where unambiguous, or " + "make the member(s) static first, or move to an existing base class of the source type.");
            }

            return await MoveInstanceMembersAsync(solution, filePath, className, membersToMove, targetClassName, memberNames, targetFilePath, targetDoc, targetClassNode, callSiteFixups, cancellationToken);
        }

        if (targetDoc?.FilePath != null && targetClassNode != null)
        {
            var existingClassMemberSymbols = membersToMove.Select(m => semanticModel?.GetDeclaredSymbol(m is FieldDeclarationSyntax f ? f.Declaration.Variables.First() : m)).Where(s => s != null).Cast<ISymbol>().ToList();
            return await MoveMembersToExistingClassAsync(solution, filePath, root, classNode, membersToMove, targetDoc.FilePath, targetClassNode, targetClassName, existingClassMemberSymbols, cancellationToken);
        }

        var newClassMemberSymbols = membersToMove.Select(m => semanticModel?.GetDeclaredSymbol(m is FieldDeclarationSyntax f ? f.Declaration.Variables.First() : m)).Where(s => s != null).Cast<ISymbol>().ToList();
        return await MoveMembersToNewClassAsync(solution, filePath, root, classNode, membersToMove, targetClassName, newClassMemberSymbols, cancellationToken);
    }

    /// <summary>
    /// Refuses up front when a moved member's body references a private or internal sibling member
    /// of the source class (including nested types) that is not itself part of this move -&gt; without
    /// this check, MoveMemberAsync pastes the body into the target file verbatim and the reference
    /// only fails to compile afterward, as an unexplained CS0246/CS0103 pointing at the target file
    /// rather than at the real cause (an un-moved dependency back in the source class).
    /// </summary>
    private static HashSet<string> RequireNoUnmovedDependencies(INamedTypeSymbol classSymbol, SemanticModel semanticModel, List<MemberDeclarationSyntax> membersToMove, string[] memberNames, string className, string targetClassName, INamedTypeSymbol? targetClassSymbol)
    {
        var alreadySatisfiedFields = new HashSet<string>(StringComparer.Ordinal);
        var siblingsByName = classSymbol.GetMembers().Where(s => s.DeclaredAccessibility is Accessibility.Private or Accessibility.Internal or Accessibility.NotApplicable).Where(s => !memberNames.Contains(s.Name)).ToLookup(s => s.Name, StringComparer.Ordinal);
        if (siblingsByName.Count == 0)
        {
            return alreadySatisfiedFields;
        }

        var missingDependencies = new SortedSet<string>(StringComparer.Ordinal);
        string? incompatibleFieldName = null;
        ITypeSymbol? incompatibleSourceType = null;
        ITypeSymbol? incompatibleTargetType = null;
        foreach (var member in membersToMove)
        {
            var identifiers = member.DescendantNodes().OfType<SimpleNameSyntax>();
            foreach (var identifier in identifiers)
            {
                if (!siblingsByName.Contains(identifier.Identifier.Text))
                {
                    continue;
                }

                var symbolInfo = semanticModel.GetSymbolInfo(identifier);
                var resolved = symbolInfo.Symbol ?? symbolInfo.CandidateSymbols.FirstOrDefault();
                if (resolved == null || !SymbolEqualityComparer.Default.Equals(resolved.ContainingType, classSymbol))
                {
                    continue;
                }

                if (resolved is IFieldSymbol sourceField && targetClassSymbol != null)
                {
                    var targetField = targetClassSymbol.GetMembers(sourceField.Name).OfType<IFieldSymbol>().FirstOrDefault();
                    if (targetField != null)
                    {
                        bool sameType = SymbolEqualityComparer.Default.Equals(sourceField.Type, targetField.Type);
                        bool implicitlyConvertible = !sameType && semanticModel.Compilation.ClassifyConversion(sourceField.Type, targetField.Type).IsImplicit;
                        if (sameType || implicitlyConvertible)
                        {
                            alreadySatisfiedFields.Add(sourceField.Name);
                        }
                        else
                        {
                            incompatibleFieldName = sourceField.Name;
                            incompatibleSourceType = sourceField.Type;
                            incompatibleTargetType = targetField.Type;
                        }

                        continue;
                    }
                }

                missingDependencies.Add(identifier.Identifier.Text);
            }
        }

        if (incompatibleFieldName != null)
        {
            throw new ToolInvalidArgumentException($"Cannot move the requested member(s) out of '{className}': their bodies reference field " + $"'{incompatibleFieldName}' of '{className}' (type '{incompatibleSourceType}'), but '{targetClassName}' " + $"already declares its own field named '{incompatibleFieldName}' of an incompatible type " + $"('{incompatibleTargetType}'). Rename one of the two fields first so they no longer collide, then retry.");
        }

        if (missingDependencies.Count > 0)
        {
            throw new ToolInvalidArgumentException($"Cannot move the requested member(s) out of '{className}': their bodies reference private/internal " + $"member(s) [{string.Join(", ", missingDependencies)}] of '{className}' that are not included in this move " + $"and would no longer be reachable from '{targetClassName}'. Add the missing name(s) to memberNames so they " + "move together, or leave the referencing member(s) behind.");
        }

        return alreadySatisfiedFields;
    }

    private static async Task<MoveMemberResult> MoveMembersToBaseTypeAsync(Solution solution, FilePathWrapper filePath, CompilationUnitSyntax root, ClassDeclarationSyntax classNode, List<MemberDeclarationSyntax> membersToMove, INamedTypeSymbol baseType, CancellationToken cancellationToken)
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

            var baseClassAfterRemoval = combinedRoot.DescendantNodes().OfType<ClassDeclarationSyntax>().FirstOrDefault(c => c.Identifier.Text == baseType.Name);
            if (baseClassAfterRemoval == null)
            {
                throw new ToolNotFoundException($"Base class '{baseType.Name}' not found in '{baseFile}' after removing member(s).");
            }

            var combinedBaseClassNode = baseClassAfterRemoval.AddMembers(membersForBase);
            var finalRoot = combinedRoot.ReplaceNode(baseClassAfterRemoval, combinedBaseClassNode);
            return new MoveMemberResult(new Dictionary<FilePathWrapper, string> { { filePath, RoslynFormattingHelper.NormalizeWholeSubtreeWhitespace(finalRoot).ToFullString() } }, new List<SkippedCallSite>());
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
        return new MoveMemberResult(new Dictionary<FilePathWrapper, string> { { filePath, RoslynFormattingHelper.NormalizeWholeSubtreeWhitespace(newDerivedRoot).ToFullString() }, { baseFile, RoslynFormattingHelper.NormalizeWholeSubtreeWhitespace(newBaseRoot).ToFullString() } }, new List<SkippedCallSite>());
    }

    /// <summary>
    /// Moves STATIC members into an existing, unrelated class. Static-only because the call-site
    /// rewrite is then unambiguous everywhere (ClassA.Foo() -> TargetClassName.Foo(), no receiver
    /// instance involved) -> MoveMemberAsync's caller already guarantees every member here is static.
    /// </summary>
    private static async Task<MoveMemberResult> MoveMembersToExistingClassAsync(Solution solution, FilePathWrapper filePath, CompilationUnitSyntax root, ClassDeclarationSyntax classNode, List<MemberDeclarationSyntax> membersToMove, FilePathWrapper targetFilePath, ClassDeclarationSyntax targetClassNode, string targetClassName, List<ISymbol> memberSymbols, CancellationToken cancellationToken)
    {
        bool sameFile = string.Equals(Path.GetFullPath(filePath), Path.GetFullPath(targetFilePath), StringComparison.OrdinalIgnoreCase);
        var movedNames = new HashSet<string>(membersToMove.SelectMany(m => m switch
        {
            MethodDeclarationSyntax meth => new[] { meth.Identifier.Text },
            PropertyDeclarationSyntax prop => new[] { prop.Identifier.Text },
            FieldDeclarationSyntax field => field.Declaration.Variables.Select(v => v.Identifier.Text).ToArray(),
            _ => Array.Empty<string>()}), StringComparer.Ordinal);
        var newTargetClassNode = targetClassNode.AddMembers(membersToMove.ToArray());
        var updatedSourceClass = classNode.RemoveNodes(membersToMove, SyntaxRemoveOptions.KeepNoTrivia)!;
        // Rewrite bare Member()/ClassName.Member() within the remaining source class to TargetClassName.Member().
        // (No `this.Member()` case: static members can't be accessed via `this`.)
        var bareIdentifiers = updatedSourceClass.DescendantNodes().OfType<IdentifierNameSyntax>().Where(id => movedNames.Contains(id.Identifier.Text) && id.Parent is not MemberAccessExpressionSyntax && id.Parent is not QualifiedNameSyntax).ToList();
        if (bareIdentifiers.Count > 0)
        {
            updatedSourceClass = updatedSourceClass.ReplaceNodes(bareIdentifiers, (original, _) => SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, SyntaxFactory.IdentifierName(targetClassName), (SimpleNameSyntax)original));
        }

        var result = new Dictionary<FilePathWrapper, string>();
        if (sameFile)
        {
            var afterSourceEdit = root.ReplaceNode(classNode, updatedSourceClass);
            var targetAfterSourceEdit = afterSourceEdit.DescendantNodes().OfType<ClassDeclarationSyntax>().FirstOrDefault(c => c.Identifier.Text == targetClassName);
            var finalRoot = targetAfterSourceEdit != null ? afterSourceEdit.ReplaceNode(targetAfterSourceEdit, targetAfterSourceEdit.AddMembers(membersToMove.ToArray())) : afterSourceEdit;
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
        var skipPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            filePath,
            targetFilePath
        };
        foreach (var symbol in memberSymbols)
        {
            var references = await SymbolFinder.FindReferencesAsync(symbol, solution, cancellationToken);
            var byDocument = references.SelectMany(r => r.Locations).Where(l => l.Document.FilePath != null && !skipPaths.Contains(l.Document.FilePath)).GroupBy(l => l.Document.Id).ToDictionary(g => g.Key, g => g.ToList());
            foreach (var(docId, locs)in byDocument)
            {
                var doc = solution.GetDocument(docId);
                if (doc?.FilePath == null)
                {
                    continue;
                }

                SyntaxNode? docRoot = result.TryGetValue(doc.FilePath, out var already) ? CSharpSyntaxTree.ParseText(already, cancellationToken: cancellationToken).GetRoot(cancellationToken) : await doc.GetSyntaxRootAsync(cancellationToken);
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

                var updatedDocRoot = docRoot.ReplaceNodes(memberAccesses, (original, _) => original.WithExpression(SyntaxFactory.IdentifierName(targetClassName)));
                result[doc.FilePath] = RoslynFormattingHelper.NormalizeWholeSubtreeWhitespace(updatedDocRoot).ToFullString();
            }
        }

        return new MoveMemberResult(result, new List<SkippedCallSite>());
    }

    private static async Task<MoveMemberResult> MoveMembersToNewClassAsync(Solution solution, FilePathWrapper filePath, CompilationUnitSyntax root, ClassDeclarationSyntax classNode, List<MemberDeclarationSyntax> membersToMove, string newClassName, List<ISymbol> memberSymbols, CancellationToken cancellationToken)
    {
        var ns = classNode.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault();
        var newClassNode = SyntaxFactory.ClassDeclaration(newClassName).WithModifiers(SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.PublicKeyword))).WithMembers(SyntaxFactory.List(membersToMove));
        var cleanUsings = SyntaxFactory.List(root.Usings.Select(u => u.WithoutTrailingTrivia().WithTrailingTrivia(SyntaxFactory.ElasticCarriageReturnLineFeed)));
        CompilationUnitSyntax newFileRoot;
        if (ns != null)
        {
            BaseNamespaceDeclarationSyntax newNs = ns is FileScopedNamespaceDeclarationSyntax ? SyntaxFactory.FileScopedNamespaceDeclaration(ns.Name).AddMembers(newClassNode) : (BaseNamespaceDeclarationSyntax)SyntaxFactory.NamespaceDeclaration(ns.Name).AddMembers(newClassNode);
            newFileRoot = SyntaxFactory.CompilationUnit().WithUsings(cleanUsings).AddMembers(newNs);
        }
        else
        {
            newFileRoot = SyntaxFactory.CompilationUnit().WithUsings(cleanUsings).AddMembers(newClassNode);
        }

        var memberNameSet = new HashSet<string>(membersToMove.SelectMany(m => m switch
        {
            MethodDeclarationSyntax meth => new[] { meth.Identifier.Text },
            PropertyDeclarationSyntax prop => new[] { prop.Identifier.Text },
            FieldDeclarationSyntax field => field.Declaration.Variables.Select(v => v.Identifier.Text).ToArray(),
            _ => Array.Empty<string>()}), StringComparer.Ordinal);
        // Static members only (guaranteed by MoveMemberAsync's caller) -> no `this.Member()` case to
        // rewrite, and no accessor property needed; bare Member() becomes NewClassName.Member() directly.
        var updatedSourceClass = classNode.RemoveNodes(membersToMove, SyntaxRemoveOptions.KeepNoTrivia)!;
        var bareIdentifiers = updatedSourceClass.DescendantNodes().OfType<IdentifierNameSyntax>().Where(id => memberNameSet.Contains(id.Identifier.Text) && id.Parent is not MemberAccessExpressionSyntax && id.Parent is not QualifiedNameSyntax).ToList();
        if (bareIdentifiers.Count > 0)
        {
            updatedSourceClass = updatedSourceClass.ReplaceNodes(bareIdentifiers, (original, _) => SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, SyntaxFactory.IdentifierName(newClassName), (SimpleNameSyntax)original));
        }

        var updatedRoot = root.ReplaceNode(classNode, updatedSourceClass);
        var newFilePath = Path.Combine(Path.GetDirectoryName(filePath)!, $"{newClassName}.cs");
        var result = new Dictionary<FilePathWrapper, string>
        {
            {
                newFilePath,
                RoslynFormattingHelper.NormalizeWholeSubtreeWhitespace(newFileRoot).ToFullString()
            },
            {
                filePath,
                RoslynFormattingHelper.NormalizeWholeSubtreeWhitespace(updatedRoot).ToFullString()
            }
        };
        // Cross-file call sites: ClassA.Foo() -> NewClassName.Foo() -> unambiguous since Foo is static.
        var skipPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            filePath,
            newFilePath
        };
        foreach (var symbol in memberSymbols)
        {
            var references = await SymbolFinder.FindReferencesAsync(symbol, solution, cancellationToken);
            var byDocument = references.SelectMany(r => r.Locations).Where(l => l.Document.FilePath != null && !skipPaths.Contains(l.Document.FilePath)).GroupBy(l => l.Document.Id).ToDictionary(g => g.Key, g => g.ToList());
            foreach (var(docId, locations)in byDocument)
            {
                var doc = solution.GetDocument(docId);
                if (doc?.FilePath == null)
                {
                    continue;
                }

                SyntaxNode? docRoot = result.TryGetValue(doc.FilePath, out var alreadyModified) ? CSharpSyntaxTree.ParseText(alreadyModified, cancellationToken: cancellationToken).GetRoot(cancellationToken) : await doc.GetSyntaxRootAsync(cancellationToken);
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

                var updatedDocRoot = docRoot.ReplaceNodes(memberAccesses, (original, _) => original.WithExpression(SyntaxFactory.IdentifierName(newClassName)));
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
        foreach (var(docId, locations)in byDocument)
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
            throw new InvalidOperationException("PreviewInstanceMoveCallSitesAsync requires a ValidationEngine - this StructuralRefactoringEngine instance was constructed without one.");
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

        var memberSymbols = membersToMove.Select(m => semanticModel.GetDeclaredSymbol(m is FieldDeclarationSyntax f ? f.Declaration.Variables.First() : m)).Where(s => s != null).Cast<ISymbol>().ToList();
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
                var finalRoot = targetAfterSourceEdit != null ? afterSourceEdit.ReplaceNode(targetAfterSourceEdit, targetAfterSourceEdit.AddMembers(membersToMove.ToArray())) : afterSourceEdit;
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
            var locations = references.SelectMany(r => r.Locations).Where(l => l.Document.FilePath != null).ToList();
            foreach (var refLocation in locations)
            {
                var refDoc = refLocation.Document;
                var refRoot = await refDoc.GetSyntaxRootAsync(cancellationToken);
                var refNode = refRoot?.FindNode(refLocation.Location.SourceSpan);
                var memberAccess = refNode?.Ancestors().OfType<MemberAccessExpressionSyntax>().FirstOrDefault(ma => ma.Name.Span.Contains(refLocation.Location.SourceSpan)) ?? refNode?.Parent as MemberAccessExpressionSyntax;
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
                    results.Add(new PreviewCallSite(refDoc.FilePath!, lineSpan.StartLinePosition.Line + 1, callExpression, CallSiteStatus.MoveOrderDependent, "This call site is inside a method that is itself being moved in the same batch - whether it can be resolved depends on move order.", null, []));
                    continue;
                }

                if (destinationType == null)
                {
                    results.Add(new PreviewCallSite(refDoc.FilePath!, lineSpan.StartLinePosition.Line + 1, callExpression, CallSiteStatus.NoCandidateBlocked, $"Destination type '{targetClassName}' could not be resolved.", null, []));
                    continue;
                }

                var refSemanticModel = await refDoc.GetSemanticModelAsync(cancellationToken);
                var refCompilation = refSemanticModel?.Compilation;
                bool sameAssembly = refCompilation != null && SymbolEqualityComparer.Default.Equals(refCompilation.Assembly, destinationType.ContainingAssembly);
                bool accessible = destinationType.DeclaredAccessibility == Accessibility.Public || sameAssembly;
                if (!accessible)
                {
                    results.Add(new PreviewCallSite(refDoc.FilePath!, lineSpan.StartLinePosition.Line + 1, callExpression, CallSiteStatus.NoCandidateBlocked, $"Destination type '{targetClassName}' is not accessible from this call site's compilation context.", null, []));
                    continue;
                }

                // destinationType was resolved from the semantic model of whichever document first
                // matched targetClassName - almost always a DIFFERENT Project/Compilation than
                // refCompilation whenever the destination class lives in a different project from the
                // call site (e.g. AntiPatternEngine in RoslynSentinel.Advanced vs. a call site in
                // RoslynSentinel.Tests.Advanced). INamedTypeSymbol instances are compilation-scoped:
                // SymbolEqualityComparer.Default.Equals(typeFromCompilationA, typeFromCompilationB)
                // is false for "the same" type seen through two different compilations, even when one
                // references the other's assembly directly, because they are genuinely distinct symbol
                // objects. Every sibling field/property/parameter/local of the destination type at the
                // call site therefore compared unequal and the site was misclassified as
                // NoCandidateIntroducible despite an in-scope, correctly-typed, correctly-initialized
                // candidate existing. Resolving destinationType's counterpart symbol IN refCompilation
                // (via its fully-qualified metadata name) before comparing fixes this: same-compilation
                // moves are unaffected (the round-trip is a no-op), and cross-project moves now compare
                // two symbols that both belong to refCompilation. See
                // docs/current/blockers/resolved/blocking_error_movemember_candidate_lookup_misses_sibling_field.md.
                var destinationTypeMetadataName = destinationType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat.WithGlobalNamespaceStyle(SymbolDisplayGlobalNamespaceStyle.Omitted));
                var destinationTypeInRefCompilation = (ITypeSymbol? )refCompilation?.GetTypeByMetadataName(destinationTypeMetadataName) ?? destinationType;
                // LookupSymbols must be called at a position that reflects general lexical/member
                // scope, not refNode.SpanStart itself: refNode is the referenced member's own
                // identifier token (e.g. "AnalyzeSemaphoreUsageAsync" in "_engine.AnalyzeSemaphoreUsageAsync(...)"),
                // which sits inside a MemberAccessExpressionSyntax's right-hand .Name. Roslyn's
                // LookupSymbols treats a position there as a member lookup scoped to the receiver's
                // OWN type (here, the source class being moved FROM), not the enclosing lexical
                // scope - so sibling fields/locals of the destination type declared in the
                // surrounding class/method were structurally invisible from that position, even
                // when correctly typed and in scope everywhere else. Using the receiver expression's
                // own SpanStart (falling back to refNode's when there is no member-access receiver,
                // e.g. a bare identifier call) evaluates lexical scope correctly.
                var lookupPosition = memberAccess?.Expression.SpanStart ?? refNode?.SpanStart;
                // The type-identity comparison below must not rely solely on SymbolEqualityComparer
                // across a compilation boundary: destinationType was bound in the destination class's
                // OWN document/project compilation, while every field/property/parameter/local found
                // by LookupSymbols here is bound in refSemanticModel's compilation (a DIFFERENT
                // Project/Compilation whenever the destination class lives in another project, e.g.
                // AntiPatternEngine in RoslynSentinel.Advanced vs. a call site in
                // RoslynSentinel.Tests.Advanced). destinationTypeInRefCompilation above re-resolves it
                // by metadata name so the SymbolEqualityComparer path can succeed, but as a
                // compilation-independent guarantee this also falls back to comparing fully-qualified
                // display names directly - a check that cannot be defeated by any cross-compilation
                // symbol-identity subtlety, since it never compares ITypeSymbol instances against each
                // other at all. See
                // docs/current/blockers/resolved/blocking_error_movemember_candidate_lookup_misses_sibling_field.md.
                bool IsDestinationType(ISymbol s)
                {
                    var candidateType = GetSymbolType(s);
                    if (candidateType == null)
                    {
                        return false;
                    }

                    if (SymbolEqualityComparer.Default.Equals(candidateType, destinationTypeInRefCompilation))
                    {
                        return true;
                    }

                    var candidateTypeName = candidateType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat.WithGlobalNamespaceStyle(SymbolDisplayGlobalNamespaceStyle.Omitted));
                    return string.Equals(candidateTypeName, destinationTypeMetadataName, StringComparison.Ordinal);
                }

                // LookupSymbols surfaces a field/property by NAME as soon as it is lexically visible,
                // which for a nested class includes every instance member declared on its enclosing
                // type(s) - C# nested classes see enclosing-type member names in scope for lookup
                // purposes, but (unlike VB.NET/Java inner classes) hold no implicit outer-instance
                // reference, so referencing an outer instance field/property unqualified is always
                // CS0120 ("An object reference is required..."), never a valid rewrite target. Without
                // this check, a same-named/same-typed field declared on an outer or unrelated type
                // (reachable only by name, not by instance) was picked as the "resolved" receiver for
                // an already-correctly-compiling call site, corrupting it. See
                // docs/current/blockers/blocking_error_movemember_batch_false_cs0120_cross_nested_class.md.
                // A static candidate member needs no instance and is always fine; an instance candidate
                // is only usable unqualified if it is declared on the call site's own enclosing type or
                // one of that type's base types.
                var enclosingTypeAtCallSite = lookupPosition == null ? null : refSemanticModel?.GetEnclosingSymbol(lookupPosition.Value, cancellationToken)?.ContainingType;
                bool IsReachableUnqualified(ISymbol s)
                {
                    if (s.IsStatic)
                    {
                        return true;
                    }

                    if (enclosingTypeAtCallSite == null)
                    {
                        return false;
                    }

                    for (var t = enclosingTypeAtCallSite; t != null; t = t.BaseType)
                    {
                        if (SymbolEqualityComparer.Default.Equals(t, s.ContainingType))
                        {
                            return true;
                        }
                    }

                    return false;
                }

                var candidates = refSemanticModel == null || lookupPosition == null ? new List<string>() : refSemanticModel.LookupSymbols(lookupPosition.Value).Where(s => (s is IFieldSymbol || s is IPropertySymbol || s is IParameterSymbol || s is ILocalSymbol) && IsDestinationType(s) && IsReachableUnqualified(s)).Select(s => s.Name).Distinct().ToList();
                if (candidates.Count == 0)
                {
                    // SuggestedFix on a non-Valid row is human-facing prep-step prose, never a receiver
                    // expression: MoveInstanceMembersAsync only reads SuggestedFix as a receiver for
                    // Valid rows, and ledger entries merely echo it back to the caller.
                    results.Add(new PreviewCallSite(refDoc.FilePath!, lineSpan.StartLinePosition.Line + 1, callExpression, CallSiteStatus.NoCandidateIntroducible, $"No in-scope reference of type '{targetClassName}' found at this call site. Add a 'using' directive or introduce a field/parameter of that type.", BuildIntroduceFieldSuggestion(targetClassName, refNode, memberAccess), []));
                    continue;
                }

                if (candidates.Count == 1)
                {
                    // SuggestedFix (like callSiteFixups) is a receiver-only expression: the rewrite in
                    // MoveInstanceMembersAsync calls original.WithExpression(...) on the existing
                    // member-access node, which keeps its own .Name segment. A value that already
                    // includes ".{symbol.Name}" here produced a doubled method name on apply (e.g.
                    // "_apiGenerationEngine.AddValidationToPocoAsync.AddValidationToPocoAsync").
                    results.Add(new PreviewCallSite(refDoc.FilePath!, lineSpan.StartLinePosition.Line + 1, callExpression, CallSiteStatus.Valid, null, candidates[0], candidates));
                    continue;
                }

                results.Add(new PreviewCallSite(refDoc.FilePath!, lineSpan.StartLinePosition.Line + 1, callExpression, CallSiteStatus.Ambiguous, $"Multiple in-scope references of type '{targetClassName}' found; specify which one via callSiteFixups.", null, candidates));
            }
        }

        return results;
    }

    // Added by AddMember (expected - used for diagnostics)
    private async Task<MoveMemberResult> MoveInstanceMembersAsync(Solution solution, FilePathWrapper filePath, string className, List<MemberDeclarationSyntax> membersToMove, string targetClassName, string[] memberNames, FilePathWrapper? targetFilePath, Document? existingTargetDoc, ClassDeclarationSyntax? existingTargetClassNode, Dictionary<string, string>? callSiteFixups, CancellationToken cancellationToken)
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

            if (fixups.Match(row.FilePath, row.Line)is { } fixup)
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
            pendingLedgerEntries = unresolvedRows.Select(r => new CallSiteLedgerEntry { EntryId = Guid.NewGuid().ToString("n")[..8], FilePath = r.FilePath, Line = r.Line, BrokenExpression = r.CallExpression, OldStaticType = className, Status = r.Status, BlockReason = r.BlockReason, SuggestedFix = r.SuggestedFix, CandidatesInScope = r.Candidates, }).ToList();
            pendingLedgerOperationName = $"MoveMember [{string.Join(", ", memberNames)}] '{className}' -> '{targetClassName}'";
            skippedCallSites = pendingLedgerEntries.Select(e => new SkippedCallSite(e.FilePath, e.Line, $"{e.Status}: {e.BlockReason} (ledger entry {e.EntryId})")).ToList();
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
                var finalRoot = targetAfterSourceEdit != null ? afterSourceEdit.ReplaceNode(targetAfterSourceEdit, targetAfterSourceEdit.AddMembers(membersToMove.ToArray())) : afterSourceEdit;
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
            var newClassNode = SyntaxFactory.ClassDeclaration(targetClassName).WithModifiers(SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.PublicKeyword))).WithMembers(SyntaxFactory.List(membersToMove));
            var cleanUsings = SyntaxFactory.List(root.Usings.Select(u => u.WithoutTrailingTrivia().WithTrailingTrivia(SyntaxFactory.ElasticCarriageReturnLineFeed)));
            CompilationUnitSyntax newFileRoot;
            if (ns != null)
            {
                BaseNamespaceDeclarationSyntax newNs = ns is FileScopedNamespaceDeclarationSyntax ? SyntaxFactory.FileScopedNamespaceDeclaration(ns.Name).AddMembers(newClassNode) : (BaseNamespaceDeclarationSyntax)SyntaxFactory.NamespaceDeclaration(ns.Name).AddMembers(newClassNode);
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

            SyntaxNode? docRoot = result.TryGetValue(docFilePath, out var already) ? CSharpSyntaxTree.ParseText(already, cancellationToken: cancellationToken).GetRoot(cancellationToken) : await doc.GetSyntaxRootAsync(cancellationToken);
            if (docRoot == null)
            {
                continue;
            }

            var linesToFix = group.ToDictionary(kv => kv.Key.Line, kv => kv.Value);
            var memberAccesses = docRoot.DescendantNodes().OfType<MemberAccessExpressionSyntax>().Where(ma => memberNames.Contains(ma.Name.Identifier.Text) && linesToFix.ContainsKey(ma.GetLocation().GetLineSpan().StartLinePosition.Line + 1)).ToList();
            SyntaxNode updatedDocRoot = docRoot;
            if (memberAccesses.Count > 0)
            {
                updatedDocRoot = updatedDocRoot.ReplaceNodes(memberAccesses, (original, _) =>
                {
                    var line = original.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                    var receiverExpr = linesToFix[line];
                    var newReceiver = receiverExpr == CallSiteFixupMap.NewKeyword ? (ExpressionSyntax)SyntaxFactory.ObjectCreationExpression(SyntaxFactory.IdentifierName(targetClassName)).WithArgumentList(SyntaxFactory.ArgumentList()) : SyntaxFactory.ParseExpression(receiverExpr);
                    var rewritten = original.WithExpression(newReceiver);
                    var siteKey = CallerFixupSiteKey(docFilePath, line);
                    return callerFixupSources.ContainsKey(siteKey) ? rewritten.WithAdditionalAnnotations(new SyntaxAnnotation(CallerFixupAnnotationKind, siteKey)) : rewritten;
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
        var initialization = receiver == null || receiver is ThisExpressionSyntax ? "initialize it with the same constructor arguments the current instance was created with" : $"initialize it where the current receiver '{receiver}' is initialized, with the same constructor arguments";
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
    private static async Task EnsureNewFixupsAreConstructibleAsync(IEnumerable<(string Key, string Value)> callerFixups, Document? existingTargetDoc, ClassDeclarationSyntax? existingTargetClassNode, CancellationToken cancellationToken)
    {
        var newKeys = callerFixups.Where(f => f.Value == CallSiteFixupMap.NewKeyword).Select(f => f.Key).Distinct(StringComparer.Ordinal).ToList();
        if (newKeys.Count == 0 || existingTargetDoc == null || existingTargetClassNode == null)
        {
            return;
        }

        var model = await existingTargetDoc.GetSemanticModelAsync(cancellationToken);
        if (model?.GetDeclaredSymbol(existingTargetClassNode, cancellationToken)is not INamedTypeSymbol targetType)
        {
            // Symbol unresolvable - leave it to the compile gate rather than refusing on a guess.
            return;
        }

        bool zeroArgCallable = !targetType.IsAbstract && !targetType.IsStatic && targetType.InstanceConstructors.Any(c => c.DeclaredAccessibility is Accessibility.Public or Accessibility.Internal or Accessibility.ProtectedOrInternal && c.Parameters.All(p => p.IsOptional || p.IsParams));
        if (zeroArgCallable)
        {
            return;
        }

        const int maxKeysShown = 3;
        var name = targetType.Name;
        var keyList = string.Join(", ", newKeys.Take(maxKeysShown).Select(k => $"\"{k}\"")) + (newKeys.Count > maxKeysShown ? $" (+{newKeys.Count - maxKeysShown} more)" : "");
        var signatures = targetType.InstanceConstructors.Length == 0 ? "(none)" : string.Join("; ", targetType.InstanceConstructors.Select(c => FormatConstructorSignature(c, name)));
        var widest = targetType.InstanceConstructors.OrderByDescending(c => c.Parameters.Length).FirstOrDefault();
        var example = widest is { Parameters.Length: > 0 } ? $"new {name}({string.Join(", ", widest.Parameters.Select(p => $"<{p.Name}>"))})" : $"new {name}(<args>)";
        var kindNote = targetType.IsStatic ? " (it is static)" : targetType.IsAbstract ? " (it is abstract)" : "";
        throw new ToolInvalidArgumentException($"{newKeys.Count} callSiteFixups key(s) use the value \"new\": {keyList}. \"new\" means a zero-argument constructor call only ('new {name}()'), " + $"but '{name}' has no accessible constructor callable with zero arguments{kindNote}. Its constructor(s): {signatures}. " + $"Pass a full receiver expression instead, e.g. \"{example}\", or the name of an existing in-scope field/property of type '{name}'. " + "Supply ALL constructor arguments explicitly, including configuration/options arguments: omitting an optional one can compile but silently change behavior. " + "No changes were made.");
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
            foreach (var(rawKey, rawValue)in fixups)
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
                throw new ToolInvalidArgumentException($"{unusable.Count} callSiteFixups entr{(unusable.Count == 1 ? "y is" : "ies are")} unusable: {string.Join(", ", unusable.Take(maxShown))}" + (unusable.Count > maxShown ? $" (+{unusable.Count - maxShown} more)" : "") + ". Each key must be \"FilePath:Line\" (1-based line), \"FilePath:*\" (every unresolved call site in that file) or \"*\" (every unresolved call site), " + "and each value a non-empty receiver expression or \"new\". No changes were made.");
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
            catch (Exception ex)when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return null;
            }
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
            _ => throw new ArgumentException($"Unknown micro-refactoring '{refactoringId}'. " + "Known IDs: type-to-var, remove-unused-local, add-braces, remove-braces, extract-constant.")};
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
                Message = $"// ERROR: Field '{fieldName}' not found."};
        }

        if (field.Declaration.Variables[0].Initializer == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.TargetNotFound,
                FilePath = filePath,
                Message = $"// ERROR: Cannot inline field '{fieldName}' without initializer. Field must have a static initializer or initial assignment."};
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
                Message = $"// ERROR: Parameter '{parameterName}' not found in method '{methodName}'."};
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
                Message = $"// ERROR: Cannot convert '{methodName}' to an indexer - it must have exactly one parameter (has {method.ParameterList.Parameters.Count})."};
        }

        // C# does not support static indexers
        if (method.Modifiers.Any(m => m.IsKind(SyntaxKind.StaticKeyword)))
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.TargetNotFound,
                FilePath = filePath,
                Message = $"// ERROR: Cannot convert static method '{methodName}' to an indexer - C# does not support static indexers."};
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
                Message = $"// ERROR: Cannot convert '{methodName}' to an indexer - method has no body."};
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
                Message = $"// ErrorDetails: {paramSnippetError}"};
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
                Message = $"// '{existingName}' is already a local variable - nothing to introduce."};
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
                Message = $"// ErrorDetails: File '{filePath}' not found."};
        }

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        if (root == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.TargetNotFound,
                FilePath = filePath,
                Message = $"// ErrorDetails: Failed to get syntax root."};
        }

        // Find the type
        var nestedType = root.DescendantNodes().OfType<TypeDeclarationSyntax>().FirstOrDefault(t => t.Identifier.Text == nestedTypeName);
        if (nestedType == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.TargetNotFound,
                FilePath = filePath,
                Message = $"// ErrorDetails: Type '{nestedTypeName}' not found."};
        }

        // Check if type is actually nested (parent is a type, not namespace/file scope)
        var parentType = nestedType.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault();
        if (parentType == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.TargetNotFound,
                FilePath = filePath,
                Message = $"// ErrorDetails: Type '{nestedTypeName}' is already at outer scope. Cannot move to outer scope."};
        }

        // Type is nested, move it out
        var newRoot = root.RemoveNode(nestedType, SyntaxRemoveOptions.KeepUnbalancedDirectives);
        if (newRoot == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.TargetNotFound,
                FilePath = filePath,
                Message = $"// ErrorDetails: Failed to remove nested type '{nestedTypeName}'."};
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
            target = SymbolNavigationEngine.ResolveBySnippetOrThrow(candidates, text, contextSnippet, lineBefore, lineAfter, (c, m, mode) => _symbolNavigationEngine.BuildMemberHint(c.Select(x => x.Node).ToList(), m, mode))?.Node as MemberDeclarationSyntax;
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
                Message = $"// Member '{memberName}' not found in '{Path.GetFileName(filePath)}'."};
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
                        Message = $"// Cannot convert '{memberName}' to expression body: method body has {stmts.Count} statement(s); only single-return methods can be converted."};
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
                        Message = $"// Cannot convert '{memberName}' to expression body: property getter does not contain a simple return statement."};
                }
            }
            else
            {
                return new DocumentEditResult
                {
                    Outcome = EditOutcome.CannotConvert,
                    FilePath = filePath,
                    Message = $"// Cannot convert '{memberName}' to expression body: member has no block body or is already an expression body."};
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
                    Message = $"// Cannot convert '{memberName}' to block body: member has no expression body (already a block body or not a method/property)."};
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
        foreach (var(docId, locations)in byDocument)
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