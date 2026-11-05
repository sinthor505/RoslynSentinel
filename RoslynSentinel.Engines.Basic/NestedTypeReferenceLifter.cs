using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Text;

namespace RoslynSentinel.Engines.Basic;

/// <summary>
/// Support for lifting a nested type to top level (MoveType destination ownFile): finds every qualified
/// <c>Outer.Nested</c> reference solution-wide (semantically, via SymbolFinder) and rewrites it so it still binds
/// once <c>Nested</c> is a namespace-level type. Sites are plain tuples (document, span, kind, flags) so they can be
/// re-located in any root of the same document.
/// </summary>
internal static class NestedTypeReferenceLifter
{
    /// <summary>Name of the namespace the lifted type will live in, or null for the global namespace.</summary>
    public static string? NamespaceNameOrNull(INamedTypeSymbol nested) =>
        nested.ContainingNamespace is { IsGlobalNamespace: false } ns ? ns.ToDisplayString() : null;

    /// <summary>
    /// Finds qualified references <c>X.Nested</c> where X binds to the containing type. Generic containing types and
    /// references reached through a derived type (<c>Derived.Nested</c>) or an alias-qualified name are not handled.
    /// NeedsUsing: the rewritten simple name needs <c>using Namespace;</c>. QualifyFully: a using-alias target that must
    /// become <c>Namespace.Nested</c>. KeepQualifier: the left side is <c>NamespaceQualifier.Outer</c>; only the Outer segment is dropped.
    /// </summary>
    public static async Task<List<(DocumentId DocumentId, TextSpan Span, SyntaxKind Kind, bool NeedsUsing, bool QualifyFully, bool KeepQualifier)>> FindSitesAsync(
        Solution solution, INamedTypeSymbol nested, CancellationToken cancellationToken)
    {
        var sites = new List<(DocumentId DocumentId, TextSpan Span, SyntaxKind Kind, bool NeedsUsing, bool QualifyFully, bool KeepQualifier)>();
        var containing = nested.ContainingType;
        if (containing == null || containing.IsGenericType)
        {
            return sites;
        }

        var namespaceName = NamespaceNameOrNull(nested);
        var globalUsingCache = new Dictionary<ProjectId, bool>();
        var seen = new HashSet<(DocumentId, TextSpan)>();
        var references = await SymbolFinder.FindReferencesAsync(nested, solution, cancellationToken);
        foreach (var reference in references)
        {
            foreach (var location in reference.Locations)
            {
                if (location.IsImplicit || location.IsCandidateLocation || !location.Location.IsInSource)
                {
                    continue;
                }

                var document = location.Document;
                var root = await document.GetSyntaxRootAsync(cancellationToken);
                var model = await document.GetSemanticModelAsync(cancellationToken);
                if (root == null || model == null)
                {
                    continue;
                }

                if (root.FindToken(location.Location.SourceSpan.Start, findInsideTrivia: true).Parent is not SimpleNameSyntax name)
                {
                    continue;
                }

                SyntaxNode site;
                SyntaxNode left;
                switch (name.Parent)
                {
                    case MemberAccessExpressionSyntax ma when ma.IsKind(SyntaxKind.SimpleMemberAccessExpression) && ma.Name == name:
                        site = ma;
                        left = ma.Expression;
                        break;
                    case QualifiedNameSyntax q when q.Right == name:
                        site = q;
                        left = q.Left;
                        break;
                    case NameMemberCrefSyntax nm when nm.Name == name && nm.Parent is QualifiedCrefSyntax qc && qc.Member == nm:
                        site = qc;
                        left = qc.Container;
                        break;
                    default:
                        continue;
                }

                // The left side must itself be written as a simple name or a dotted name that binds to the containing type.
                if (left is not (SimpleNameSyntax or QualifiedNameSyntax) && !(left is MemberAccessExpressionSyntax lma && lma.IsKind(SyntaxKind.SimpleMemberAccessExpression)))
                {
                    continue;
                }

                if (model.GetSymbolInfo(left, cancellationToken).Symbol is not INamedTypeSymbol leftType || !SymbolEqualityComparer.Default.Equals(leftType, containing))
                {
                    continue;
                }

                SyntaxNode? qualifier = left switch
                {
                    MemberAccessExpressionSyntax m => m.Expression,
                    QualifiedNameSyntax qn => qn.Left,
                    _ => null,
                };
                var keepQualifier = qualifier != null && model.GetSymbolInfo(qualifier, cancellationToken).Symbol is INamespaceSymbol;
                var inUsingDirective = site.Ancestors().OfType<UsingDirectiveSyntax>().Any();
                var qualifyFully = false;
                var needsUsing = false;
                if (!keepQualifier && namespaceName != null)
                {
                    if (inUsingDirective)
                    {
                        // A using-alias target is resolved without the file's other usings, so it must be fully qualified.
                        qualifyFully = site is QualifiedNameSyntax;
                    }
                    else
                    {
                        needsUsing = !await IsNamespaceVisibleAsync(site, root, document.Project, namespaceName, globalUsingCache, cancellationToken);
                    }
                }

                if (seen.Add((document.Id, site.Span)))
                {
                    sites.Add((document.Id, site.Span, site.Kind(), needsUsing, qualifyFully, keepQualifier));
                }
            }
        }

        return sites;
    }

    /// <summary>
    /// Applies the rewrites to <paramref name="root"/> (sites are re-located by span and kind) and adds the using directive
    /// when any site needs it. <paramref name="tracked"/> is a node of <paramref name="root"/> whose rewritten counterpart is returned.
    /// </summary>
    public static (CompilationUnitSyntax Root, SyntaxNode? Tracked) ApplyRewrites(
        CompilationUnitSyntax root,
        IEnumerable<(TextSpan Span, SyntaxKind Kind, bool NeedsUsing, bool QualifyFully, bool KeepQualifier)> sites,
        SyntaxNode? tracked,
        string? namespaceName,
        string eol)
    {
        var annotation = new SyntaxAnnotation();
        if (tracked != null)
        {
            root = root.ReplaceNode(tracked, tracked.WithAdditionalAnnotations(annotation));
        }

        var flagsByNode = new Dictionary<SyntaxNode, (bool QualifyFully, bool KeepQualifier)>();
        var needsUsing = false;
        foreach (var site in sites)
        {
            var node = root.DescendantNodes(site.Span, descendIntoChildren: null, descendIntoTrivia: true)
                .FirstOrDefault(n => n.Span == site.Span && n.IsKind(site.Kind));
            if (node == null)
            {
                continue;
            }

            flagsByNode[node] = (site.QualifyFully, site.KeepQualifier);
            needsUsing |= site.NeedsUsing;
        }

        var rewrittenRoot = flagsByNode.Count == 0
            ? root
            : root.ReplaceNodes(flagsByNode.Keys, (original, rewritten) => RewriteSite(rewritten, flagsByNode[original].QualifyFully, flagsByNode[original].KeepQualifier, namespaceName));
        if (needsUsing && namespaceName != null)
        {
            rewrittenRoot = AddUsing(rewrittenRoot, namespaceName, eol);
        }

        var newTracked = tracked == null ? null : rewrittenRoot.GetAnnotatedNodes(annotation).FirstOrDefault();
        return (rewrittenRoot, newTracked);
    }

    private static SyntaxNode RewriteSite(SyntaxNode rewritten, bool qualifyFully, bool keepQualifier, string? namespaceName)
    {
        switch (rewritten)
        {
            case MemberAccessExpressionSyntax ma:
                if (keepQualifier && ma.Expression is MemberAccessExpressionSyntax lm)
                {
                    return lm.WithName(ma.Name);
                }

                return ma.Name.WithLeadingTrivia(ma.GetLeadingTrivia());
            case QualifiedNameSyntax q:
                if (keepQualifier && q.Left is QualifiedNameSyntax lq)
                {
                    return lq.WithRight(q.Right);
                }

                if (qualifyFully && namespaceName != null)
                {
                    return SyntaxFactory.QualifiedName(SyntaxFactory.ParseName(namespaceName), q.Right).WithLeadingTrivia(q.GetLeadingTrivia());
                }

                return q.Right.WithLeadingTrivia(q.GetLeadingTrivia());
            case QualifiedCrefSyntax qc:
                if (keepQualifier && qc.Container is QualifiedNameSyntax cq)
                {
                    return SyntaxFactory.QualifiedCref(cq.Left, qc.Member);
                }

                return qc.Member.WithLeadingTrivia(qc.GetLeadingTrivia());
            default:
                return rewritten;
        }
    }

    private static bool HasUsing(SyntaxList<UsingDirectiveSyntax> usings, string namespaceName) =>
        usings.Any(u => u.Alias == null && u.StaticKeyword.IsKind(SyntaxKind.None) && u.Name?.ToString() == namespaceName);

    private static async Task<bool> IsNamespaceVisibleAsync(SyntaxNode site, SyntaxNode root, Project project, string namespaceName, Dictionary<ProjectId, bool> globalUsingCache, CancellationToken cancellationToken)
    {
        // Inside the namespace itself, or in a child namespace of it, the type resolves unqualified.
        var enclosing = site.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().ToList();
        if (enclosing.Count > 0)
        {
            var fullName = string.Join(".", Enumerable.Reverse(enclosing).Select(n => n.Name.ToString()));
            if (fullName == namespaceName || fullName.StartsWith(namespaceName + ".", StringComparison.Ordinal))
            {
                return true;
            }
        }

        if (enclosing.Any(n => HasUsing(n.Usings, namespaceName)) || (root is CompilationUnitSyntax cu && HasUsing(cu.Usings, namespaceName)))
        {
            return true;
        }

        if (!globalUsingCache.TryGetValue(project.Id, out var hasGlobalUsing))
        {
            hasGlobalUsing = false;
            foreach (var projectDocument in project.Documents)
            {
                if (await projectDocument.GetSyntaxRootAsync(cancellationToken) is CompilationUnitSyntax projectRoot
                    && projectRoot.Usings.Any(u => !u.GlobalKeyword.IsKind(SyntaxKind.None) && u.Alias == null && u.StaticKeyword.IsKind(SyntaxKind.None) && u.Name?.ToString() == namespaceName))
                {
                    hasGlobalUsing = true;
                    break;
                }
            }

            globalUsingCache[project.Id] = hasGlobalUsing;
        }

        return hasGlobalUsing;
    }

    // System namespaces first, then ordinal - the usual dotnet using order.
    private static int CompareUsingNames(string a, string b)
    {
        var aSystem = a == "System" || a.StartsWith("System.", StringComparison.Ordinal);
        var bSystem = b == "System" || b.StartsWith("System.", StringComparison.Ordinal);
        return aSystem != bSystem ? (aSystem ? -1 : 1) : string.CompareOrdinal(a, b);
    }

    private static CompilationUnitSyntax AddUsing(CompilationUnitSyntax root, string namespaceName, string eol)
    {
        if (HasUsing(root.Usings, namespaceName))
        {
            return root;
        }

        // Parsed with two line endings in the resolved eol: the using carries the first, the second (the EOF token's leading trivia) is the optional blank line.
        var parsedUsing = SyntaxFactory.ParseCompilationUnit("using " + namespaceName + ";" + eol + eol);
        var newUsing = parsedUsing.Usings[0];
        var usings = root.Usings;
        if (usings.Count == 0)
        {
            if (root.Externs.Count > 0)
            {
                return root.AddUsings(newUsing);
            }

            // First using in the file: it takes over the file's leading trivia (header comment, blank lines) and is followed by a blank line.
            var first = root.GetFirstToken();
            newUsing = newUsing.WithLeadingTrivia(first.LeadingTrivia).WithTrailingTrivia(newUsing.GetTrailingTrivia().AddRange(parsedUsing.EndOfFileToken.LeadingTrivia));
            root = root.ReplaceToken(first, first.WithLeadingTrivia(SyntaxFactory.TriviaList()));
            return root.WithUsings(SyntaxFactory.SingletonList(newUsing));
        }

        var regular = Enumerable.Range(0, usings.Count)
            .Where(i => usings[i].Alias == null && usings[i].StaticKeyword.IsKind(SyntaxKind.None) && usings[i].GlobalKeyword.IsKind(SyntaxKind.None))
            .ToList();
        var insertAt = usings.Count;
        if (regular.Count > 0)
        {
            var names = regular.Select(i => usings[i].Name?.ToString() ?? "").ToList();
            var sorted = Enumerable.Range(0, names.Count - 1).All(k => CompareUsingNames(names[k], names[k + 1]) <= 0);
            insertAt = regular[^1] + 1;
            if (sorted)
            {
                for (var k = 0; k < names.Count; k++)
                {
                    if (CompareUsingNames(namespaceName, names[k]) < 0)
                    {
                        insertAt = regular[k];
                        break;
                    }
                }
            }
        }

        if (insertAt == 0)
        {
            newUsing = newUsing.WithLeadingTrivia(usings[0].GetLeadingTrivia());
            usings = usings.Replace(usings[0], usings[0].WithLeadingTrivia(SyntaxFactory.TriviaList()));
        }

        return root.WithUsings(usings.Insert(insertAt, newUsing));
    }
}
