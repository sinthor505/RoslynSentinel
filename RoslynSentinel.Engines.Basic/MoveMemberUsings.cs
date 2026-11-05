using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace RoslynSentinel.Engines.Basic;

/// <summary>
/// Carries the using directives a moved member's code needs to the file it moves into. Works from the SOURCE
/// document's semantic model (so it sees what each name in the moved syntax really binds to), and returns one
/// zero-length insertion against the target's ORIGINAL text, like every other MoveMemberTextEdits helper.
/// </summary>
public static class MoveMemberUsings
{
    private static readonly SymbolDisplayFormat NamespaceFormat = SymbolDisplayFormat.CSharpErrorMessageFormat;

    private static readonly SymbolDisplayFormat TypeNoGenericsFormat = SymbolDisplayFormat.CSharpErrorMessageFormat.WithGenericsOptions(SymbolDisplayGenericsOptions.None);

    /// <summary>
    /// Builds the insertion of <c>using X;</c> lines for the namespaces <paramref name = "movedMembers"/> need that the
    /// target file lacks, or null when nothing is missing. A namespace is carried only when the SOURCE file (or its
    /// enclosing namespaces) has a plain, non-global using for it, the target has no equivalent using in scope, and the
    /// target class is not itself inside that namespace. <c>using static</c> and alias directives are carried verbatim
    /// when a bare name in the moved code binds through them. Same-file moves return null. The insertion goes after the
    /// target's last using, or above the first declaration (or its doc comment) with a blank line when it has none.
    /// Limitation: a using already supplied to the target by a global using elsewhere in its project is still added
    /// (harmless duplicate).
    /// </summary>
    public static TextChange? BuildMissingUsingsEdit(SemanticModel sourceModel, IReadOnlyList<MemberDeclarationSyntax> movedMembers, ClassDeclarationSyntax targetClass, SourceText targetText, string eol)
    {
        if (movedMembers.Count == 0 || movedMembers.Any(m => m.SyntaxTree != sourceModel.SyntaxTree) || sourceModel.SyntaxTree == targetClass.SyntaxTree)
        {
            return null;
        }

        if (sourceModel.SyntaxTree.GetRoot() is not CompilationUnitSyntax sourceRoot || targetClass.SyntaxTree.GetRoot() is not CompilationUnitSyntax targetRoot)
        {
            return null;
        }

        var sourceByKey = new Dictionary<string, UsingDirectiveSyntax>();
        foreach (var directive in ScopedUsings(movedMembers[0], sourceRoot))
        {
            sourceByKey.TryAdd(Key(directive), directive);
        }

        var targetKeys = ScopedUsings(targetClass, targetRoot).Select(Key).ToHashSet();
        var targetNamespaces = EnclosingNamespaceNames(targetClass);
        var sourceType = movedMembers[0].Parent is TypeDeclarationSyntax typeSyntax ? sourceModel.GetDeclaredSymbol(typeSyntax) as INamedTypeSymbol : null;
        var needed = new List<UsingDirectiveSyntax>();

        void Consider(string key)
        {
            if (sourceByKey.TryGetValue(key, out var directive) && !targetKeys.Contains(key) && !needed.Contains(directive))
            {
                needed.Add(directive);
            }
        }

        void AddNamespace(INamespaceSymbol? ns)
        {
            if (ns == null || ns.IsGlobalNamespace)
            {
                return;
            }

            var name = Normalize(ns.ToDisplayString(NamespaceFormat));
            if (!targetNamespaces.Contains(name))
            {
                Consider("ns:" + name);
            }
        }

        void AddStaticContainer(INamedTypeSymbol? container)
        {
            if (container == null)
            {
                return;
            }

            // A member of the source class itself, an outer class or a base type binds without any using.
            for (var outer = sourceType; outer != null; outer = outer.ContainingType)
            {
                for (var type = outer; type != null; type = type.BaseType)
                {
                    if (SymbolEqualityComparer.Default.Equals(type.OriginalDefinition, container.OriginalDefinition))
                    {
                        return;
                    }
                }
            }

            Consider("static:" + Normalize(container.ToDisplayString(TypeNoGenericsFormat)));
        }

        foreach (var name in movedMembers.SelectMany(m => m.DescendantNodesAndSelf().OfType<SimpleNameSyntax>()))
        {
            if (name is IdentifierNameSyntax { IsVar: true })
            {
                continue;
            }

            var alias = sourceModel.GetAliasInfo(name);
            if (alias != null)
            {
                Consider("alias:" + alias.Name);
                continue;
            }

            var info = sourceModel.GetSymbolInfo(name);
            var symbol = info.Symbol ?? info.CandidateSymbols.FirstOrDefault();
            var qualifiedRight = IsRightOfQualifier(name);
            switch (symbol)
            {
                case IMethodSymbol { ReducedFrom: not null } reduced:
                    // Extension method called as instance.Method(): it binds through the using of its static class's namespace.
                    AddNamespace(reduced.ContainingNamespace);
                    break;
                case IMethodSymbol { MethodKind: MethodKind.Constructor } ctor when name.Parent is AttributeSyntax || name.Parent is QualifiedNameSyntax { Parent: AttributeSyntax }:
                    if (!qualifiedRight)
                    {
                        AddNamespace(ctor.ContainingNamespace);
                    }

                    break;
                case INamedTypeSymbol type when !qualifiedRight:
                    if (type.ContainingType == null)
                    {
                        AddNamespace(type.ContainingNamespace);
                    }
                    else
                    {
                        AddStaticContainer(type.ContainingType);
                    }

                    break;
                case IMethodSymbol or IFieldSymbol or IPropertySymbol or IEventSymbol when !qualifiedRight && symbol.IsStatic:
                    AddStaticContainer(symbol.ContainingType);
                    break;
            }
        }

        if (needed.Count == 0)
        {
            return null;
        }

        var lines = needed
            .OrderBy(d => d.Alias != null ? 2 : d.StaticKeyword.IsKind(SyntaxKind.None) ? 0 : 1)
            .ThenBy(d => d.ToString().StartsWith("using System", StringComparison.Ordinal) ? 0 : 1)
            .ThenBy(d => d.ToString().Trim(), StringComparer.Ordinal)
            .Select(d => d.ToString().Trim())
            .ToList();
        var block = string.Join(eol, lines);

        if (targetRoot.Usings.Count > 0)
        {
            var position = targetRoot.Usings[^1].FullSpan.End;
            var prefix = position > 0 && targetText[position - 1] != '\n' ? eol : string.Empty;
            return new TextChange(new TextSpan(position, 0), prefix + block + eol);
        }

        // No usings yet: go above the first declaration's doc comment (or the declaration itself), below any header comment.
        var firstToken = targetRoot.GetFirstToken();
        var docComment = firstToken.LeadingTrivia.FirstOrDefault(t => t.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia) || t.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia));
        var anchor = docComment.IsKind(SyntaxKind.None) ? firstToken.SpanStart : docComment.SpanStart;
        var lineStart = targetText.Lines.GetLineFromPosition(anchor).Start;
        return new TextChange(new TextSpan(lineStart, 0), block + eol + eol);
    }

    // The using directives in scope at a node: those of the compilation unit and of every enclosing namespace, excluding global usings.
    private static IEnumerable<UsingDirectiveSyntax> ScopedUsings(SyntaxNode node, CompilationUnitSyntax root)
    {
        return node.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().SelectMany(n => n.Usings).Concat(root.Usings).Where(u => u.GlobalKeyword.IsKind(SyntaxKind.None));
    }

    // Comparison key: "alias:Name", "static:Full.Type.Name" or "ns:Full.Namespace.Name", whitespace and "global::" removed.
    private static string Key(UsingDirectiveSyntax directive)
    {
        if (directive.Alias != null)
        {
            return "alias:" + directive.Alias.Name.Identifier.Text;
        }

        var text = Normalize(directive.ToString());
        if (text.StartsWith("using", StringComparison.Ordinal))
        {
            text = text.Substring("using".Length);
        }

        text = text.TrimEnd(';');
        var isStatic = !directive.StaticKeyword.IsKind(SyntaxKind.None);
        if (isStatic && text.StartsWith("static", StringComparison.Ordinal))
        {
            text = text.Substring("static".Length);
        }

        return (isStatic ? "static:" : "ns:") + text;
    }

    private static string Normalize(string text) => new string(text.Where(c => !char.IsWhiteSpace(c)).ToArray()).Replace("global::", string.Empty);

    // Every namespace name the target class is declared in, plus each parent prefix (A.B.C also yields A.B and A).
    private static HashSet<string> EnclosingNamespaceNames(ClassDeclarationSyntax targetClass)
    {
        var names = new HashSet<string>();
        var cumulative = string.Empty;
        foreach (var ns in targetClass.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().Reverse())
        {
            var declared = Normalize(ns.Name.ToString());
            cumulative = cumulative.Length == 0 ? declared : cumulative + "." + declared;
            var segments = cumulative.Split('.');
            for (var i = 1; i <= segments.Length; i++)
            {
                names.Add(string.Join(".", segments.Take(i)));
            }
        }

        return names;
    }

    // True when the name is the right-hand part of a dotted name (Outer.Inner, a.Method, System.Xml.Linq.XDocument): something else qualifies it.
    private static bool IsRightOfQualifier(SimpleNameSyntax name)
    {
        return name.Parent switch
        {
            MemberAccessExpressionSyntax memberAccess => memberAccess.Name == name,
            QualifiedNameSyntax qualified => qualified.Right == name,
            AliasQualifiedNameSyntax aliasQualified => aliasQualified.Name == name,
            MemberBindingExpressionSyntax => true,
            _ => false,
        };
    }
}
