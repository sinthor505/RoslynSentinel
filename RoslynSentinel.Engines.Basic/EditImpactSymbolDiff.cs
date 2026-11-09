using System.Text.RegularExpressions;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RoslynSentinel.Engines.Basic;

/// <summary>
/// Syntax-only diff of the declarations in the edited files, baseline text against candidate text. No compilation is built:
/// the gate already paid for one, and a declaration's name, parameter types, return type and accessibility are all
/// visible in the syntax. Callers go through <see cref="EditImpactAnalyzer.DiffSymbols"/>.
/// </summary>
internal static class EditImpactSymbolDiff
{
    // A working row of the collector; Params and ReturnType are whitespace-free text, not bound symbols.
    private sealed record Declaration(
        string Container,
        string Kind,
        string Name,
        string Params,
        string ReturnType,
        string Accessibility,
        string File,
        int Line,
        bool IsType);

    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);

    /// <summary>Rank used to decide whether an accessibility change narrows access. Equal ranks never count as narrowing.</summary>
    private static readonly Dictionary<string, int> AccessibilityRank = new(StringComparer.Ordinal)
    {
        ["public"] = 5,
        ["protected internal"] = 4,
        ["internal"] = 3,
        ["protected"] = 3,
        ["private protected"] = 2,
        ["private"] = 1,
    };

    public static IReadOnlyList<SymbolChange> Diff(
        IReadOnlyDictionary<string, string?> baselineTexts,
        IReadOnlyDictionary<string, string?> candidateTexts)
    {
        var oldDecls = Collect(baselineTexts);
        var newDecls = Collect(candidateTexts);
        var changes = new List<SymbolChange>();

        // Types first, so a renamed type can be mapped before its members are compared.
        var typeDiff = DiffGroup(oldDecls.Where(d => d.IsType), newDecls.Where(d => d.IsType));
        changes.AddRange(typeDiff.Changes);
        var renamedTypes = new List<(string OldFull, string NewFull)>();
        var renamedTypeChanges = PairRenames(typeDiff.Removed, typeDiff.Added);
        foreach (var (removed, added) in renamedTypeChanges)
        {
            changes.Add(RenameChange(removed, added));
            renamedTypes.Add((Join(removed.Container, removed.Name), Join(added.Container, added.Name)));
        }

        var removedTypeNames = typeDiff.Removed.Where(r => renamedTypeChanges.All(p => p.Removed != r)).ToList();
        var addedTypeNames = typeDiff.Added.Where(a => renamedTypeChanges.All(p => p.Added != a)).ToList();
        foreach (var removed in removedTypeNames)
        {
            changes.Add(Change(SymbolChangeKind.Removed, removed, null));
        }

        foreach (var added in addedTypeNames)
        {
            changes.Add(Change(SymbolChangeKind.Added, null, added));
        }

        // Members: the new side is re-keyed to the old type name when the type was renamed, so only real member
        // differences remain; members inside a removed or added type are subsumed by the type entry.
        var oldFullRemoved = removedTypeNames.Select(t => Join(t.Container, t.Name)).ToList();
        var newFullAdded = addedTypeNames.Select(t => Join(t.Container, t.Name)).ToList();
        var oldMembers = oldDecls.Where(d => !d.IsType && !InsideAny(d.Container, oldFullRemoved)).ToList();
        var newMembers = newDecls
            .Where(d => !d.IsType && !InsideAny(d.Container, newFullAdded))
            .Select(d => d with { Container = RemapContainer(d.Container, renamedTypes) })
            .ToList();

        var memberDiff = DiffGroup(oldMembers, newMembers);
        changes.AddRange(memberDiff.Changes);
        var memberRenames = PairRenames(memberDiff.Removed, memberDiff.Added);
        foreach (var (removed, added) in memberRenames)
        {
            changes.Add(RenameChange(removed, added));
        }

        foreach (var removed in memberDiff.Removed.Where(r => memberRenames.All(p => p.Removed != r)))
        {
            changes.Add(Change(SymbolChangeKind.Removed, removed, null));
        }

        foreach (var added in memberDiff.Added.Where(a => memberRenames.All(p => p.Added != a)))
        {
            changes.Add(Change(SymbolChangeKind.Added, null, added));
        }

        return changes;
    }

    private static bool InsideAny(string container, List<string> typeNames)
        => typeNames.Any(t => container == t || container.StartsWith(t + ".", StringComparison.Ordinal));

    private static string RemapContainer(string container, List<(string OldFull, string NewFull)> renames)
    {
        foreach (var (oldFull, newFull) in renames)
        {
            if (container == newFull)
            {
                return oldFull;
            }

            if (container.StartsWith(newFull + ".", StringComparison.Ordinal))
            {
                return oldFull + container[newFull.Length..];
            }
        }

        return container;
    }

    private sealed record GroupDiff(List<SymbolChange> Changes, List<Declaration> Removed, List<Declaration> Added);

    /// <summary>
    /// Buckets both sides by (container, kind, name). Within a bucket: exact matches vanish, then a match on parameters and
    /// return type with a different accessibility is an accessibility change, then one leftover on each side is a signature
    /// change (same name, different parameters or return type); anything else is removed or added.
    /// </summary>
    private static GroupDiff DiffGroup(IEnumerable<Declaration> oldItems, IEnumerable<Declaration> newItems)
    {
        var changes = new List<SymbolChange>();
        var removed = new List<Declaration>();
        var added = new List<Declaration>();
        var oldBuckets = oldItems.GroupBy(d => (d.Container, d.Kind, d.Name)).ToDictionary(g => g.Key, g => g.ToList());
        var newBuckets = newItems.GroupBy(d => (d.Container, d.Kind, d.Name)).ToDictionary(g => g.Key, g => g.ToList());
        foreach (var key in oldBuckets.Keys.Union(newBuckets.Keys))
        {
            var olds = oldBuckets.GetValueOrDefault(key) ?? [];
            var news = newBuckets.GetValueOrDefault(key) ?? [];
            olds = [.. olds];
            news = [.. news];

            foreach (var o in olds.ToList())
            {
                var exact = news.FirstOrDefault(n => n.Params == o.Params && n.ReturnType == o.ReturnType && n.Accessibility == o.Accessibility);
                if (exact is not null)
                {
                    olds.Remove(o);
                    news.Remove(exact);
                }
            }

            foreach (var o in olds.ToList())
            {
                var sameShape = news.FirstOrDefault(n => n.Params == o.Params && n.ReturnType == o.ReturnType);
                if (sameShape is null)
                {
                    continue;
                }

                olds.Remove(o);
                news.Remove(sameShape);
                bool narrowed = AccessibilityRank.GetValueOrDefault(sameShape.Accessibility) < AccessibilityRank.GetValueOrDefault(o.Accessibility);
                changes.Add(new SymbolChange(SymbolChangeKind.AccessibilityChanged, o.Kind, o.Container, o.Name, null, sameShape.File, sameShape.Line,
                    $"{o.Accessibility}", $"{sameShape.Accessibility}", narrowed));
            }

            if (olds.Count == 1 && news.Count == 1)
            {
                var o = olds[0];
                var n = news[0];
                changes.Add(new SymbolChange(SymbolChangeKind.SignatureChanged, o.Kind, o.Container, o.Name, null, n.File, n.Line, Render(o), Render(n)));
                continue;
            }

            removed.AddRange(olds);
            added.AddRange(news);
        }

        return new GroupDiff(changes, removed, added);
    }

    /// <summary>
    /// The conservative rename rule: within one (container, kind), exactly one removed and exactly one added declaration
    /// with equal parameter types and equal return type. Anything looser is left as removed plus added.
    /// </summary>
    private static List<(Declaration Removed, Declaration Added)> PairRenames(List<Declaration> removed, List<Declaration> added)
    {
        var pairs = new List<(Declaration, Declaration)>();
        foreach (var group in removed.GroupBy(r => (r.Container, r.Kind)))
        {
            var removedHere = group.ToList();
            var addedHere = added.Where(a => a.Container == group.Key.Container && a.Kind == group.Key.Kind).ToList();
            if (removedHere.Count == 1 && addedHere.Count == 1
                && removedHere[0].Params == addedHere[0].Params
                && removedHere[0].ReturnType == addedHere[0].ReturnType
                && removedHere[0].Name != addedHere[0].Name)
            {
                pairs.Add((removedHere[0], addedHere[0]));
            }
        }

        return pairs;
    }

    private static SymbolChange RenameChange(Declaration removed, Declaration added)
        => new(SymbolChangeKind.Renamed, removed.Kind, removed.Container, removed.Name, added.Name, added.File, added.Line, Render(removed), Render(added));

    private static SymbolChange Change(SymbolChangeKind kind, Declaration? removed, Declaration? added)
    {
        var d = removed ?? added!;
        return new SymbolChange(kind, d.Kind, d.Container, d.Name, null, d.File, d.Line,
            removed is null ? null : Render(removed), added is null ? null : Render(added));
    }

    private static string Render(Declaration d)
    {
        if (d.IsType)
        {
            return d.Accessibility;
        }

        // A method's Params starts with its generic arity, "<n>|"; it is a comparison key, not display text.
        var shownParams = Regex.Replace(d.Params, @"^<\d+>\|", string.Empty);
        return (d.Kind is "method" or "ctor" or "indexer" ? $"({shownParams})" : string.Empty)
            + (d.ReturnType.Length > 0 ? $" : {d.ReturnType}" : string.Empty);
    }

    private static string Join(string container, string name) => container.Length == 0 ? name : container + "." + name;

    private static List<Declaration> Collect(IReadOnlyDictionary<string, string?> texts)
    {
        var all = new List<Declaration>();
        foreach (var (path, text) in texts)
        {
            if (string.IsNullOrEmpty(text))
            {
                continue;
            }

            var tree = CSharpSyntaxTree.ParseText(text);
            CollectFrom(tree.GetRoot(), string.Empty, Path.GetFileName(path), tree, all);
        }

        return all;
    }

    private static void CollectFrom(SyntaxNode node, string container, string file, SyntaxTree tree, List<Declaration> output)
    {
        foreach (var child in node.ChildNodes())
        {
            switch (child)
            {
                case BaseNamespaceDeclarationSyntax ns:
                    CollectFrom(ns, Join(container, ns.Name.ToString()), file, tree, output);
                    break;
                case BaseTypeDeclarationSyntax type:
                    {
                        var kind = type switch
                        {
                            RecordDeclarationSyntax r => r.ClassOrStructKeyword.IsKind(SyntaxKind.StructKeyword) ? "record struct" : "record",
                            ClassDeclarationSyntax => "class",
                            StructDeclarationSyntax => "struct",
                            InterfaceDeclarationSyntax => "interface",
                            _ => "enum",
                        };
                        int arity = (type as TypeDeclarationSyntax)?.TypeParameterList?.Parameters.Count ?? 0;
                        output.Add(new Declaration(container, kind, type.Identifier.Text, arity > 0 ? $"<{arity}>" : string.Empty, string.Empty,
                            AccessibilityOf(type.Modifiers, type), file, LineOf(tree, type.Identifier), true));
                        CollectFrom(type, Join(container, type.Identifier.Text), file, tree, output);
                        break;
                    }

                case DelegateDeclarationSyntax del:
                    output.Add(new Declaration(container, "delegate", del.Identifier.Text, ParamsOf(del.ParameterList), Norm(del.ReturnType),
                        AccessibilityOf(del.Modifiers, del), file, LineOf(tree, del.Identifier), true));
                    break;
                case MethodDeclarationSyntax m:
                    output.Add(new Declaration(container, "method", (m.ExplicitInterfaceSpecifier?.ToString() ?? string.Empty) + m.Identifier.Text,
                        $"<{m.TypeParameterList?.Parameters.Count ?? 0}>|" + ParamsOf(m.ParameterList), Norm(m.ReturnType),
                        AccessibilityOf(m.Modifiers, m), file, LineOf(tree, m.Identifier), false));
                    break;
                case ConstructorDeclarationSyntax c when !c.Modifiers.Any(SyntaxKind.StaticKeyword):
                    // Named ".ctor", not by the type name, so a type rename does not look like a removed constructor.
                    output.Add(new Declaration(container, "ctor", ".ctor", ParamsOf(c.ParameterList), string.Empty,
                        AccessibilityOf(c.Modifiers, c), file, LineOf(tree, c.Identifier), false));
                    break;
                case PropertyDeclarationSyntax p:
                    output.Add(new Declaration(container, "property", p.Identifier.Text, string.Empty, Norm(p.Type),
                        AccessibilityOf(p.Modifiers, p), file, LineOf(tree, p.Identifier), false));
                    break;
                case IndexerDeclarationSyntax ix:
                    output.Add(new Declaration(container, "indexer", "this[]", ParamsOf(ix.ParameterList), Norm(ix.Type),
                        AccessibilityOf(ix.Modifiers, ix), file, LineOf(tree, ix.ThisKeyword), false));
                    break;
                case FieldDeclarationSyntax f:
                    foreach (var v in f.Declaration.Variables)
                    {
                        output.Add(new Declaration(container, "field", v.Identifier.Text, string.Empty, Norm(f.Declaration.Type),
                            AccessibilityOf(f.Modifiers, f), file, LineOf(tree, v.Identifier), false));
                    }

                    break;
                case EventFieldDeclarationSyntax ef:
                    foreach (var v in ef.Declaration.Variables)
                    {
                        output.Add(new Declaration(container, "event", v.Identifier.Text, string.Empty, Norm(ef.Declaration.Type),
                            AccessibilityOf(ef.Modifiers, ef), file, LineOf(tree, v.Identifier), false));
                    }

                    break;
                case EventDeclarationSyntax ev:
                    output.Add(new Declaration(container, "event", ev.Identifier.Text, string.Empty, Norm(ev.Type),
                        AccessibilityOf(ev.Modifiers, ev), file, LineOf(tree, ev.Identifier), false));
                    break;
                case EnumMemberDeclarationSyntax em:
                    output.Add(new Declaration(container, "enum member", em.Identifier.Text, string.Empty, string.Empty, "public", file, LineOf(tree, em.Identifier), false));
                    break;
                    // TODO operators, conversions, finalizers and record primary-constructor parameters are not diffed yet
            }
        }
    }

    private static int LineOf(SyntaxTree tree, SyntaxToken token) => tree.GetLineSpan(token.Span).StartLinePosition.Line + 1;

    private static string Norm(TypeSyntax? type) => type is null ? string.Empty : Whitespace.Replace(type.ToString(), string.Empty);

    private static string ParamsOf(BaseParameterListSyntax? list)
        => list is null
            ? string.Empty
            : string.Join(",", list.Parameters.Select(p =>
                string.Join(string.Empty, p.Modifiers.Select(m => m.Text + " ")) + Norm(p.Type)));

    private static string AccessibilityOf(SyntaxTokenList modifiers, SyntaxNode node)
    {
        bool isPublic = modifiers.Any(SyntaxKind.PublicKeyword);
        bool isProtected = modifiers.Any(SyntaxKind.ProtectedKeyword);
        bool isInternal = modifiers.Any(SyntaxKind.InternalKeyword);
        bool isPrivate = modifiers.Any(SyntaxKind.PrivateKeyword);
        if (isPublic)
        {
            return "public";
        }

        if (isProtected && isInternal)
        {
            return "protected internal";
        }

        if (isPrivate && isProtected)
        {
            return "private protected";
        }

        if (isProtected)
        {
            return "protected";
        }

        if (isInternal)
        {
            return "internal";
        }

        if (isPrivate)
        {
            return "private";
        }

        return node.Parent switch
        {
            CompilationUnitSyntax or BaseNamespaceDeclarationSyntax => "internal",
            InterfaceDeclarationSyntax or EnumDeclarationSyntax => "public",
            _ => "private",
        };
    }
}
