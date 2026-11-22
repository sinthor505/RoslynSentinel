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
                $"copyAndObsolete: the destination gives the global namespace, so the copy of '{source.Absolute}' would need its namespace declaration removed, which this edit does not do. Options: pass a destinationPath in a folder that gives a named namespace, or use namespaceHandling keep to move the file as it is.");
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
    private static List<string> CollectUsings(FilePathWrapper source, CompilationUnitSyntax root, SemanticModel model, string oldNamespace, string newNamespace, CancellationToken cancellationToken, out CopyAndObsoleteEditResult? refusal)
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

        // TODO extension methods reached without a name node (foreach GetEnumerator, collection-initializer Add, await GetAwaiter) and names in doc-comment crefs add no using
        foreach (var name in root.DescendantNodes().OfType<SimpleNameSyntax>())
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
                case INamedTypeSymbol { ContainingType: null } qualified when IsDeclaredIn(qualified, root.SyntaxTree):
                    selfQualified.Add(qualified.Name);
                    break;
                case INamespaceSymbol { ContainingNamespace: { IsGlobalNamespace: false } parent } when !qualifiedRight:
                    var parentName = parent.ToDisplayString();
                    if (visibleFromOld.Contains(parentName) && !visibleFromNew.Contains(parentName))
                    {
                        partial.Add(name.Identifier.ValueText);
                    }

                    break;
            }

            if (target?.OriginalDefinition is { ContainingType: null } top && !top.ContainingNamespace.IsGlobalNamespace && !IsDeclaredIn(top, root.SyntaxTree))
            {
                var ns = top.ContainingNamespace.ToDisplayString();
                if (visibleFromOld.Contains(ns) && !visibleFromNew.Contains(ns) && !existing.Contains(ns))
                {
                    needed.Add(ns);
                }
            }
        }

        if (partial.Count > 0)
        {
            refusal = CopyAndObsoleteEditResult.Refused(ToolErrorCode.TargetIneligible,
                $"copyAndObsolete: '{source.Absolute}' names the namespace(s) {string.Join(", ", partial.Select(Describe))} by a partial qualification that resolves from {Describe(oldNamespace)} but not from {Describe(newNamespace)}, and a using directive cannot import a namespace name. Options: write those references fully qualified first, or use namespaceHandling keep to move the file as it is.");
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
    private static int AnchorPosition(MemberDeclarationSyntax member)
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

    private static string Apply(string text, List<(int Position, int Length, string Text)> edits)
    {
        var builder = new StringBuilder(text.Length + 64);
        var cursor = 0;

        // OrderBy is stable: insertions at one position keep the order they were added in.
        foreach (var edit in edits.OrderBy(edit => edit.Position))
        {
            builder.Append(text, cursor, edit.Position - cursor);
            builder.Append(edit.Text);
            cursor = edit.Position + edit.Length;
        }

        builder.Append(text, cursor, text.Length - cursor);
        return builder.ToString();
    }

    private static string DetectEol(string text)
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
    private static int CompareNamespaces(string a, string b)
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

    private static string DeclaredNamespace(SyntaxNode declaration) => string.Join(".", declaration.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().Reverse().Select(ns => ns.Name.ToString()));

    private static string Describe(string ns) => ns.Length == 0 ? "the global namespace" : $"'{ns}'";

    private static string NameOf(SyntaxNode declaration) => declaration switch
    {
        BaseTypeDeclarationSyntax type => type.Identifier.ValueText,
        DelegateDeclarationSyntax delegateDeclaration => delegateDeclaration.Identifier.ValueText,
        _ => string.Empty,
    };
}
