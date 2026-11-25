using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using RoslynSentinel.Common;

namespace RoslynSentinel.Engines.Basic;

/// <summary>
/// The text edits MoveFile's copyAndObsolete handling applies after <see cref="CopyAndObsoleteChecks"/> accepted a move: the copy (the source with the
/// destination's namespace and the using directives it needs) and the original (the source with [System.Obsolete("Use ...")] on every top-level type).
/// Edits are made on the source text itself, so every byte outside the edited spots (comments, spacing, BOM, line endings) is preserved. Nothing is written.
/// </summary>
public static class CopyAndObsoleteEdits
{
    private const char Bom = '﻿';

    /// <summary>
    /// Builds both texts from an accepted <paramref name="check"/>; a refused check is returned as this call's refusal.
    /// See the explicit-argument overload for what is produced.
    /// </summary>
    public static Task<CopyAndObsoleteEditResult> BuildAsync(Solution solution, FilePathWrapper source, string sourceText, CopyAndObsoleteCheckResult check, CancellationToken cancellationToken = default)
    {
        if (check.Refusal is not null)
        {
            return Task.FromResult(CopyAndObsoleteEditResult.Refused(check.Refusal));
        }

        if (check.OldNamespace is null || check.NewNamespace is null)
        {
            return Task.FromResult(CopyAndObsoleteEditResult.Refused(ToolErrorCode.PreconditionNotMet,
                $"copyAndObsolete: the check of '{source.Absolute}' returned no namespaces, so the copy's namespace is unknown. Options: run the check again, or use namespaceHandling keep to move the file as it is."));
        }

        return BuildAsync(solution, source, sourceText, check.OldNamespace, check.NewNamespace, check.Types, cancellationToken);
    }

    /// <summary>
    /// Builds the copy and the marked original of <paramref name="source"/>. <paramref name="sourceText"/> is the file text exactly as it will be written
    /// (a leading U+FEFF is split off before parsing and put back, so bytes are preserved); it is bound against <paramref name="solution"/> in place of the
    /// document's loaded text. The copy: the namespace declaration(s) named <paramref name="oldNamespace"/> now name <paramref name="newNamespace"/> (a
    /// global-namespace source gets a file-scoped namespace), plus a using directive for every namespace the code reaches by being inside the old namespace
    /// (the old namespace or one of its ancestors) that the new namespace does not also see, and that is not already imported; the copy carries no [Obsolete].
    /// The original: <c>[System.Obsolete("Use &lt;NewNs&gt;.&lt;Type&gt;")]</c> above the modifiers (below any doc comment) of every top-level type that has no
    /// Obsolete attribute yet. Refusals (facts and options): empty new namespace; source not a document of the solution; text that does not parse into
    /// top-level types of <paramref name="oldNamespace"/>; namespaces declared by nesting blocks; a global-namespace file with top-level statements or other
    /// non-type members; a partially qualified namespace name or a namespace-qualified own type that the namespace change would break.
    /// </summary>
    public static async Task<CopyAndObsoleteEditResult> BuildAsync(Solution solution, FilePathWrapper source, string sourceText, string oldNamespace, string newNamespace, IReadOnlyList<TopLevelType> types, CancellationToken cancellationToken = default)
    {
        if (newNamespace.Length == 0)
        {
            return CopyAndObsoleteEditResult.Refused(ToolErrorCode.InvalidArgument,
                $"copyAndObsolete: the destination gives the global namespace, so the copy of '{source.Absolute}' would need its namespace declaration removed, which this edit does not do. Options: pass a {MoveFileParameterNames.DestinationPath} in a folder that gives a named namespace, or use namespaceHandling keep to move the file as it is.");
        }

        var lookup = DocumentLookup.TryGetDocument(solution, source);
        if (!lookup.TryGetDocument(out var document))
        {
            return CopyAndObsoleteEditResult.Refused(lookup.ToResultError());
        }

        var hasBom = sourceText.Length > 0 && sourceText[0] == Bom;
        var text = hasBom ? sourceText[1..] : sourceText;

        // Bind the text that will be written, not whatever the workspace loaded: offsets of the syntax tree then match the string exactly.
        var bound = document.WithText(SourceText.From(text));
        if (await bound.GetSyntaxRootAsync(cancellationToken) is not CompilationUnitSyntax root || await bound.GetSemanticModelAsync(cancellationToken) is not { } model)
        {
            return CopyAndObsoleteEditResult.Refused(ToolErrorCode.PreconditionNotMet,
                $"copyAndObsolete: '{source.Absolute}' has no syntax tree or semantic model in the loaded solution, so its text could not be edited. Options: use namespaceHandling keep to move the file as it is.");
        }

        var eol = DetectEol(text);
        var scope = root.DescendantNodes(node => node is CompilationUnitSyntax or BaseNamespaceDeclarationSyntax).ToList();
        var declarations = scope.Where(node => node is BaseTypeDeclarationSyntax or DelegateDeclarationSyntax).ToList();
        var namespaceDeclarations = scope.OfType<BaseNamespaceDeclarationSyntax>().ToList();

        var structureRefusal = CheckStructure(source, root, oldNamespace, declarations, namespaceDeclarations);
        if (structureRefusal is not null)
        {
            return structureRefusal;
        }

        var usings = CollectUsings(source, root, model, oldNamespace, newNamespace, cancellationToken, out var usingsRefusal);
        if (usingsRefusal is not null)
        {
            return usingsRefusal;
        }

        var newEdits = new List<(int Position, int Length, string Text)>();
        AddUsingEdits(newEdits, root, text, eol, usings, oldNamespace, newNamespace);
        foreach (var namespaceDeclaration in namespaceDeclarations.Where(ns => string.Equals(ns.Name.ToString(), oldNamespace, StringComparison.Ordinal)))
        {
            newEdits.Add((namespaceDeclaration.Name.SpanStart, namespaceDeclaration.Name.Span.Length, newNamespace));
        }

        var oldEdits = new List<(int Position, int Length, string Text)>();
        var alreadyObsolete = new List<string>();
        foreach (var declaration in declarations)
        {
            var name = NameOf(declaration);
            if (((MemberDeclarationSyntax)declaration).AttributeLists.SelectMany(list => list.Attributes).Any(IsObsolete))
            {
                alreadyObsolete.Add(name);
                continue;
            }

            var attribute = "[System.Obsolete(" + SymbolDisplay.FormatLiteral($"Use {newNamespace}.{name}", quote: true) + ")]";
            var position = declaration.GetFirstToken().SpanStart;
            var lineStart = position == 0 ? 0 : text.LastIndexOf('\n', position - 1) + 1;
            var indent = text[lineStart..position];
            oldEdits.Add((position, 0, string.IsNullOrWhiteSpace(indent) ? attribute + eol + indent : attribute + " "));
        }

        var bomText = hasBom ? Bom.ToString() : string.Empty;
        return new CopyAndObsoleteEditResult(
            bomText + Apply(text, newEdits),
            bomText + Apply(text, oldEdits),
            usings,
            types.Where(type => type.HasStaticFields).Select(type => type.Name).ToList(),
            alreadyObsolete,
            null);
    }

    // The text must hold top-level types of the old namespace in a shape the namespace swap can edit; otherwise a refusal naming what was found.
    private static CopyAndObsoleteEditResult? CheckStructure(FilePathWrapper source, CompilationUnitSyntax root, string oldNamespace, List<SyntaxNode> declarations, List<BaseNamespaceDeclarationSyntax> namespaceDeclarations)
    {
        if (declarations.Count == 0)
        {
            return CopyAndObsoleteEditResult.Refused(ToolErrorCode.TargetIneligible,
                $"copyAndObsolete: the text of '{source.Absolute}' declares no top-level type (class, struct, interface, enum, record or delegate). Options: use namespaceHandling keep to move the file as it is.");
        }

        var found = declarations.Select(DeclaredNamespace).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        if (found.Count != 1 || !string.Equals(found[0], oldNamespace, StringComparison.Ordinal))
        {
            return CopyAndObsoleteEditResult.Refused(ToolErrorCode.PreconditionNotMet,
                $"copyAndObsolete: the text of '{source.Absolute}' declares its top-level types in {string.Join(", ", found.Select(Describe))}, but the check read {Describe(oldNamespace)}. Options: run the check again on the current file, or use namespaceHandling keep to move the file as it is.");
        }

        if (namespaceDeclarations.Any(ns => ns.Parent is BaseNamespaceDeclarationSyntax))
        {
            return CopyAndObsoleteEditResult.Refused(ToolErrorCode.TargetIneligible,
                $"copyAndObsolete: '{source.Absolute}' builds the namespace {Describe(oldNamespace)} from nested namespace blocks, and renaming one name would change only part of it. Options: write it as one declaration ('namespace {oldNamespace}' or 'namespace {oldNamespace};') first, or use namespaceHandling keep to move the file as it is.");
        }

        if (oldNamespace.Length == 0)
        {
            var others = root.Members.Where(member => member is not (BaseTypeDeclarationSyntax or DelegateDeclarationSyntax)).Select(member => member.Kind().ToString()).Distinct().ToList();
            if (others.Count > 0)
            {
                return CopyAndObsoleteEditResult.Refused(ToolErrorCode.TargetIneligible,
                    $"copyAndObsolete: '{source.Absolute}' declares types in the global namespace next to {string.Join(", ", others)}, and a file-scoped namespace around all of that would change what the other members mean. Options: move those members out first, or use namespaceHandling keep to move the file as it is.");
            }
        }

        return null;
    }

    // Namespaces the copy needs a using directive for (sorted, System first), or a refusal when a name cannot be fixed by a using.
    // isSetMember (a namespace change of several files at once, null for copyAndObsolete): true for a top-level type that moves with the set. Such a type is
    // never imported by a using for the old namespace (it is gone from there; the caller rewrite pass supplies the using for its new namespace), a
    // namespace-qualified reference to it is rewritten by that pass instead of refused, and a partial qualification that leads to it is not refused here.
    private static List<string> CollectUsings(FilePathWrapper source, CompilationUnitSyntax root, SemanticModel model, string oldNamespace, string newNamespace, CancellationToken cancellationToken, out CopyAndObsoleteEditResult? refusal, Func<INamedTypeSymbol, bool>? isSetMember = null)
    {
        refusal = null;
        var visibleFromOld = WithAncestors(oldNamespace);
        var visibleFromNew = WithAncestors(newNamespace);
        var existing = root.DescendantNodes().OfType<UsingDirectiveSyntax>()
            .Where(directive => directive.Alias is null && directive.StaticKeyword.IsKind(SyntaxKind.None) && directive.Name is not null)
            .Select(directive => directive.Name!.ToString())
            .ToHashSet(StringComparer.Ordinal);
        var needed = new HashSet<string>(StringComparer.Ordinal);
        var partial = new SortedSet<string>(StringComparer.Ordinal);
        var selfQualified = new SortedSet<string>(StringComparer.Ordinal);

        bool IsSet(INamedTypeSymbol type) => isSetMember is not null && isSetMember(type.OriginalDefinition);

        // A namespace name that is only the left part of the qualification of a set member's type ("Sub" of Sub.Type).
        // TODO a partial qualification inside a doc-comment cref (NameMemberCref/QualifiedCref) is not recognised here, so it still refuses a namespace change of the set
        bool LeadsToSetMember(SimpleNameSyntax name)
        {
            if (isSetMember is null)
            {
                return false;
            }

            ExpressionSyntax node = name;
            while (true)
            {
                ExpressionSyntax? parent = node.Parent switch
                {
                    QualifiedNameSyntax qualified when qualified.Left == node => qualified,
                    MemberAccessExpressionSyntax access when access.Expression == node => access,
                    _ => null,
                };
                if (parent is null)
                {
                    return false;
                }

                node = parent;
                if (model.GetSymbolInfo(node, cancellationToken).Symbol is INamedTypeSymbol type && IsSet(type))
                {
                    return true;
                }
            }
        }

        // A top-level type outside this file adds a using when the old namespace sees it and the new one does not.
        void AddNeeded(INamedTypeSymbol? target)
        {
            if (target?.OriginalDefinition is { ContainingType: null } top && !top.ContainingNamespace.IsGlobalNamespace && !IsDeclaredIn(top, root.SyntaxTree) && !IsSet(top))
            {
                var ns = top.ContainingNamespace.ToDisplayString();
                if (visibleFromOld.Contains(ns) && !visibleFromNew.Contains(ns) && !existing.Contains(ns))
                {
                    needed.Add(ns);
                }
            }
        }

        // Names inside doc-comment crefs count like any other name (descendIntoTrivia). They bind only when the tree is parsed with DocumentationMode.Parse or
        // Diagnose; the loaded workspace's documents are parsed with Parse (CrefSpikeTests), and with None there are simply no cref names to see.
        foreach (var name in root.DescendantNodes(descendIntoTrivia: true).OfType<SimpleNameSyntax>())
        {
            if (name is IdentifierNameSyntax { IsVar: true }
                || name.Ancestors().Any(ancestor => ancestor is UsingDirectiveSyntax)
                || (name.FirstAncestorOrSelf<BaseNamespaceDeclarationSyntax>() is { } enclosing && enclosing.Name.Span.Contains(name.Span)))
            {
                continue;
            }

            var info = model.GetSymbolInfo(name, cancellationToken);
            var symbol = info.Symbol ?? info.CandidateSymbols.FirstOrDefault();
            var qualifiedRight = IsQualifiedRight(name);
            INamedTypeSymbol? target = null;
            switch (symbol)
            {
                case IMethodSymbol { MethodKind: MethodKind.Constructor } constructor when !qualifiedRight:
                    target = constructor.ContainingType;
                    break;
                case IMethodSymbol { ReducedFrom: { } extension }:
                    target = extension.ContainingType;
                    break;
                case INamedTypeSymbol type when !qualifiedRight:
                    target = type;
                    break;
                case INamedTypeSymbol { ContainingType: null } qualified when IsDeclaredIn(qualified, root.SyntaxTree) && !IsSet(qualified):
                    selfQualified.Add(qualified.Name);
                    break;
                case INamespaceSymbol { ContainingNamespace: { IsGlobalNamespace: false } parent } when !qualifiedRight:
                    var parentName = parent.ToDisplayString();
                    if (visibleFromOld.Contains(parentName) && !visibleFromNew.Contains(parentName) && !LeadsToSetMember(name))
                    {
                        partial.Add(name.Identifier.ValueText);
                    }

                    break;
            }

            AddNeeded(target);
        }

        // Extension methods reached without a name node (foreach GetEnumerator, await GetAwaiter, collection-initializer Add, Deconstruct).
        foreach (var extensionClass in ImplicitExtensionUses.Collect(root, model, cancellationToken))
        {
            AddNeeded(extensionClass);
        }

        if (partial.Count > 0)
        {
            var partialFacts = $"'{source.Absolute}' names the namespace(s) {string.Join(", ", partial.Select(Describe))} by a partial qualification that resolves from {Describe(oldNamespace)} but not from {Describe(newNamespace)}, and a using directive cannot import a namespace name.";
            refusal = CopyAndObsoleteEditResult.Refused(ToolErrorCode.TargetIneligible,
                isSetMember is null
                    ? $"copyAndObsolete: {partialFacts} Options: write those references fully qualified first, or use namespaceHandling keep to move the file as it is."
                    : $"{partialFacts} Options: write those references fully qualified first, or leave the file out of the namespace change.");
        }
        else if (selfQualified.Count > 0)
        {
            refusal = CopyAndObsoleteEditResult.Refused(ToolErrorCode.TargetIneligible,
                $"copyAndObsolete: '{source.Absolute}' refers to its own type(s) {string.Join(", ", selfQualified.Select(Describe))} through a namespace qualification, which the namespace change would break. Options: refer to them by simple name first, or use namespaceHandling keep to move the file as it is.");
        }

        return needed.Order(Comparer<string>.Create(CompareNamespaces)).ToList();
    }

    // Adds the using directives (sorted position among the existing plain usings, or the top of the file) and, for a global-namespace source, the file-scoped namespace.
    private static void AddUsingEdits(List<(int Position, int Length, string Text)> edits, CompilationUnitSyntax root, string text, string eol, List<string> added, string oldNamespace, string newNamespace)
    {
        var plain = root.Usings.Where(directive => directive.Alias is null && directive.StaticKeyword.IsKind(SyntaxKind.None) && directive.GlobalKeyword.IsKind(SyntaxKind.None)).ToList();
        var topBlock = new StringBuilder();
        foreach (var ns in added)
        {
            var line = $"using {ns};{eol}";
            if (root.Usings.Count == 0)
            {
                topBlock.Append(line);
            }
            else if (plain.Count == 0)
            {
                edits.Add((root.Usings[0].SpanStart, 0, line));
            }
            else if (plain.FirstOrDefault(directive => CompareNamespaces(ns, directive.Name?.ToString() ?? string.Empty) < 0) is { } next)
            {
                edits.Add((next.SpanStart, 0, line));
            }
            else
            {
                edits.Add((plain[^1].FullSpan.End, 0, line));
            }
        }

        var firstMember = root.Members[0];
        var anchor = AnchorPosition(firstMember);
        if (topBlock.Length > 0)
        {
            topBlock.Append(eol);
        }

        if (oldNamespace.Length == 0)
        {
            // Directly after a using line there is no blank line to reuse; add one.
            if (root.Usings.Count > 0 && anchor == firstMember.FullSpan.Start)
            {
                topBlock.Append(eol);
            }

            topBlock.Append($"namespace {newNamespace};{eol}{eol}");
        }

        if (topBlock.Length > 0)
        {
            edits.Add((anchor, 0, topBlock.ToString()));
        }
    }

    // Where text goes ahead of the member: after the last blank line of its leading trivia (so a header comment stays on top and a comment or doc comment
    // directly above the member stays attached), else the member's full start.
    internal static int AnchorPosition(MemberDeclarationSyntax member)
    {
        var anchor = member.FullSpan.Start;
        var lineBlank = true;
        foreach (var trivia in member.GetLeadingTrivia())
        {
            if (trivia.IsKind(SyntaxKind.WhitespaceTrivia))
            {
                continue;
            }

            if (trivia.IsKind(SyntaxKind.EndOfLineTrivia))
            {
                if (lineBlank)
                {
                    anchor = trivia.FullSpan.End;
                }

                lineBlank = true;
                continue;
            }

            // Doc comments and directives carry their own line ending; a // or /* */ comment does not.
            lineBlank = trivia.HasStructure;
        }

        return anchor;
    }

    internal static string Apply(string text, List<(int Position, int Length, string Text)> edits)
    {
        var builder = new StringBuilder(text.Length + 64);
        var cursor = 0;

        // OrderBy is stable: insertions at one position keep the order they were added in.
        foreach (var edit in edits.OrderBy(edit => edit.Position))
        {
            // Guard shared with the caller-rewrite pass: skip an overlapping edit rather than garble the output.
            if (edit.Position < cursor)
            {
                continue;
            }

            builder.Append(text, cursor, edit.Position - cursor);
            builder.Append(edit.Text);
            cursor = edit.Position + edit.Length;
        }

        builder.Append(text, cursor, text.Length - cursor);
        return builder.ToString();
    }

    /// <summary>
    /// The overlap-refusing variant of <see cref="Apply"/> (which stays as it is, shared with the copyAndObsolete engines and skips an overlapping edit): splices
    /// the same way, but when an edit starts inside the span of an earlier one the pair is added to <paramref name="overlaps"/> as an <see cref="EditOverlap"/> and
    /// null is returned, so a caller can refuse instead of writing a file with a lost edit. Insertions at one position and an edit that starts exactly where the
    /// previous one ends are not overlaps. The caller de-duplicates identical edits first.
    /// </summary>
    internal static string? ApplyStrict(string filePath, string text, List<(int Position, int Length, string Text)> edits, List<EditOverlap> overlaps)
    {
        var before = overlaps.Count;
        var builder = new StringBuilder(text.Length + 64);
        var cursor = 0;
        var last = (Position: 0, Length: 0);

        foreach (var edit in edits.OrderBy(edit => edit.Position))
        {
            if (edit.Position < cursor)
            {
                overlaps.Add(new EditOverlap(filePath, last.Position, last.Length, edit.Position, edit.Length));
                continue;
            }

            builder.Append(text, cursor, edit.Position - cursor);
            builder.Append(edit.Text);
            cursor = edit.Position + edit.Length;
            last = (edit.Position, edit.Length);
        }

        if (overlaps.Count > before)
        {
            return null;
        }

        builder.Append(text, cursor, text.Length - cursor);
        return builder.ToString();
    }

    /// <summary>The refusal for the <see cref="EditOverlap"/> items <see cref="ApplyStrict"/> collected: facts and options, the items in StructuredDetail.</summary>
    internal static ResultError OverlapRefusal(IReadOnlyList<EditOverlap> overlaps)
    {
        const int MaxNamed = 10;
        var files = overlaps.Select(overlap => overlap.FilePath).Distinct(PathComparison.Comparer).Order(PathComparison.Comparer).ToList();
        var named = string.Join(", ", files.Take(MaxNamed).Select(file => $"'{file}'")) + (files.Count > MaxNamed ? $" and {files.Count - MaxNamed} more" : string.Empty);
        return new ResultError(ToolErrorCode.PreconditionNotMet,
            $"The namespace change would give {overlaps.Count} pair(s) of edits that overlap in {files.Count} file(s): {named}, so nothing was rewritten. The offsets are in the structured detail. Options: make the references around the overlap fully qualified first, or leave those files out of the namespace change.",
            null,
            overlaps.Cast<object>().ToList());
    }

    /// <summary>
    /// The declaration-owner edits of one file that moves with a namespace change (the set overload of <see cref="CopyAndObsoleteCallerRewrite"/>), added to
    /// <paramref name="edits"/> as positions in the document's current text: the namespace declaration(s) named <see cref="NamespaceMove.OldNamespace"/> now name
    /// the new one (a global-namespace source gets a file-scoped namespace), plus a using directive for every namespace the code reached by being inside the old
    /// namespace that the new one does not also see. Top-level types of the files in <paramref name="setMembers"/> are not imported: they move too. Returns the
    /// refusal (facts and options) when the text is not a shape the namespace swap can edit; null otherwise.
    /// </summary>
    internal static async Task<ResultError?> AddMovedFileEditsAsync(Document document, NamespaceMove move, IReadOnlyCollection<FilePathWrapper> setMembers, List<(int Position, int Length, string Text)> edits, CancellationToken cancellationToken)
    {
        var source = move.FilePath;
        if (await document.GetSyntaxRootAsync(cancellationToken) is not CompilationUnitSyntax root || await document.GetSemanticModelAsync(cancellationToken) is not { } model)
        {
            return new ResultError(ToolErrorCode.PreconditionNotMet,
                $"'{source.Absolute}' has no syntax tree or semantic model in the loaded solution, so its namespace could not be changed. Options: leave the file out of the namespace change.");
        }

        var text = (await document.GetTextAsync(cancellationToken)).ToString();
        var scope = root.DescendantNodes(node => node is CompilationUnitSyntax or BaseNamespaceDeclarationSyntax).ToList();
        var declarations = scope.Where(node => node is BaseTypeDeclarationSyntax or DelegateDeclarationSyntax).ToList();
        var namespaceDeclarations = scope.OfType<BaseNamespaceDeclarationSyntax>().ToList();

        var structureRefusal = CheckMovedStructure(source, root, move.OldNamespace, declarations, namespaceDeclarations);
        if (structureRefusal is not null)
        {
            return structureRefusal;
        }

        var members = new HashSet<string>(setMembers.Select(member => member.Absolute), PathComparison.Comparer);
        bool IsSetMember(INamedTypeSymbol type) => type.DeclaringSyntaxReferences.Any(reference => reference.SyntaxTree.FilePath is { Length: > 0 } path && members.Contains(path));

        var usings = CollectUsings(source, root, model, move.OldNamespace, move.NewNamespace, cancellationToken, out var usingsRefusal, IsSetMember);
        if (usingsRefusal is not null)
        {
            return usingsRefusal.Refusal;
        }

        AddUsingEdits(edits, root, text, DetectEol(text), usings, move.OldNamespace, move.NewNamespace);
        foreach (var namespaceDeclaration in namespaceDeclarations.Where(ns => string.Equals(ns.Name.ToString(), move.OldNamespace, StringComparison.Ordinal)))
        {
            edits.Add((namespaceDeclaration.Name.SpanStart, namespaceDeclaration.Name.Span.Length, move.NewNamespace));
        }

        return null;
    }

    // The same four shape checks as CheckStructure, worded for a namespace change (CheckStructure's texts name copyAndObsolete and namespaceHandling keep).
    private static ResultError? CheckMovedStructure(FilePathWrapper source, CompilationUnitSyntax root, string oldNamespace, List<SyntaxNode> declarations, List<BaseNamespaceDeclarationSyntax> namespaceDeclarations)
    {
        if (declarations.Count == 0)
        {
            return new ResultError(ToolErrorCode.TargetIneligible,
                $"'{source.Absolute}' declares no top-level type (class, struct, interface, enum, record or delegate), so there is no namespace to change. Options: leave the file out of the namespace change.");
        }

        var found = declarations.Select(DeclaredNamespace).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        if (found.Count != 1 || !string.Equals(found[0], oldNamespace, StringComparison.Ordinal))
        {
            return new ResultError(ToolErrorCode.PreconditionNotMet,
                $"'{source.Absolute}' declares its top-level types in {string.Join(", ", found.Select(Describe))}, but the namespace change reads {Describe(oldNamespace)}. Options: run the change again on the current file, or split the file so each part sits in one namespace.");
        }

        if (namespaceDeclarations.Any(ns => ns.Parent is BaseNamespaceDeclarationSyntax))
        {
            return new ResultError(ToolErrorCode.TargetIneligible,
                $"'{source.Absolute}' builds the namespace {Describe(oldNamespace)} from nested namespace blocks, and renaming one name would change only part of it. Options: write it as one declaration ('namespace {oldNamespace}' or 'namespace {oldNamespace};') first, or leave the file out of the namespace change.");
        }

        if (oldNamespace.Length == 0)
        {
            var others = root.Members.Where(member => member is not (BaseTypeDeclarationSyntax or DelegateDeclarationSyntax)).Select(member => member.Kind().ToString()).Distinct().ToList();
            if (others.Count > 0)
            {
                return new ResultError(ToolErrorCode.TargetIneligible,
                    $"'{source.Absolute}' declares types in the global namespace next to {string.Join(", ", others)}, and a file-scoped namespace around all of that would change what the other members mean. Options: move those members out first, or leave the file out of the namespace change.");
            }
        }

        return null;
    }

    internal static string DetectEol(string text)
    {
        var crlf = 0;
        var lf = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '\n')
            {
                continue;
            }

            if (i > 0 && text[i - 1] == '\r')
            {
                crlf++;
            }
            else
            {
                lf++;
            }
        }

        return lf > crlf ? "\n" : "\r\n";
    }

    // The namespace itself and each ancestor ("A.B.C" gives A.B.C, A.B, A): what code inside that namespace sees without a using.
    private static HashSet<string> WithAncestors(string ns)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        var current = ns;
        while (current.Length > 0)
        {
            result.Add(current);
            var dot = current.LastIndexOf('.');
            current = dot < 0 ? string.Empty : current[..dot];
        }

        return result;
    }

    // System namespaces first, then ordinal: the order the common usings convention uses.
    internal static int CompareNamespaces(string a, string b)
    {
        var aSystem = a == "System" || a.StartsWith("System.", StringComparison.Ordinal);
        var bSystem = b == "System" || b.StartsWith("System.", StringComparison.Ordinal);
        return aSystem != bSystem ? (aSystem ? -1 : 1) : string.CompareOrdinal(a, b);
    }

    private static bool IsQualifiedRight(SimpleNameSyntax name) => name.Parent switch
    {
        QualifiedNameSyntax qualified => qualified.Right == name,
        MemberAccessExpressionSyntax access => access.Name == name,
        AliasQualifiedNameSyntax alias => alias.Name == name,
        NameMemberCrefSyntax { Parent: QualifiedCrefSyntax qualifiedCref } member => qualifiedCref.Member == member,
        MemberBindingExpressionSyntax => true,
        _ => false,
    };

    private static bool IsDeclaredIn(ISymbol symbol, SyntaxTree tree) => symbol.DeclaringSyntaxReferences.Any(reference => reference.SyntaxTree == tree);

    private static bool IsObsolete(AttributeSyntax attribute)
    {
        var simple = attribute.Name switch
        {
            QualifiedNameSyntax qualified => qualified.Right,
            AliasQualifiedNameSyntax alias => alias.Name,
            SimpleNameSyntax name => name,
            _ => null,
        };
        return simple?.Identifier.ValueText is "Obsolete" or "ObsoleteAttribute";
    }

    internal static string DeclaredNamespace(SyntaxNode declaration) => string.Join(".", declaration.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().Reverse().Select(ns => ns.Name.ToString()));

    private static string Describe(string ns) => ns.Length == 0 ? "the global namespace" : $"'{ns}'";

    internal static string NameOf(SyntaxNode declaration) => declaration switch
    {
        BaseTypeDeclarationSyntax type => type.Identifier.ValueText,
        DelegateDeclarationSyntax delegateDeclaration => delegateDeclaration.Identifier.ValueText,
        _ => string.Empty,
    };
}
