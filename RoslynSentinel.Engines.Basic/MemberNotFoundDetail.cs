using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

using RoslynSentinel.Common;

namespace RoslynSentinel.Engines.Basic;

/// <summary>
/// Builds the text of a member-not-found error for the shared member read (<c>SymbolNavigationEngine.GetMemberSourceAsync</c>,
/// behind ReadFile(memberName) and Member(view)). A bare "Member 'X' not found" left a model to guess: this states what was
/// searched (the file, and the containerName when given), lists the file's own member names by kind (up to
/// <see cref="MaxListed"/>, a similar name first), says when the name is declared in another file of the same project
/// (syntax only), and names the next call. Syntax only; never throws for a bad name.
/// </summary>
public static class MemberNotFoundDetail
{
    private const int MaxListed = 15;
    private const int MaxClosest = 3;
    private const int MaxOtherFiles = 3;
    private const int MaxTypesListed = 10;

    private static readonly string[] KindOrder = ["methods", "constructors", "properties", "fields", "events", "indexers", "enum members"];

    private static readonly Dictionary<string, string> SingularKind = new()
    {
        ["methods"] = "method",
        ["constructors"] = "constructor",
        ["properties"] = "property",
        ["fields"] = "field",
        ["events"] = "event",
        ["indexers"] = "indexer",
        ["enum members"] = "enum member",
    };

    private sealed record Entry(string Kind, string Name, string? Container, int Order);

    /// <summary>
    /// The full not-found text. <paramref name="includeHead"/> false returns only the detail sentences, for a caller whose own
    /// message already says the member was not found (the strict container-miss message).
    /// </summary>
    public static async Task<string> BuildAsync(
        Document document, SyntaxNode root, string memberName, string? containerName, bool includeHead, CancellationToken cancellationToken = default)
    {
        var path = document.FilePath ?? document.Name;
        var fileName = Path.GetFileName(path);
        var parts = new List<string>();
        if (includeHead)
        {
            var containerClause = string.IsNullOrWhiteSpace(containerName) ? string.Empty : $", with containerName '{containerName}'";
            parts.Add($"Member '{memberName}' not found in '{fileName}' (searched every method, constructor, property, field, event, indexer and enum member declared in the file{containerClause}; the name must match exactly, case-sensitive).");
        }

        var entries = Collect(root);
        var scope = entries;
        var scopeText = $"'{fileName}'";
        if (!string.IsNullOrWhiteSpace(containerName))
        {
            var types = root.DescendantNodes().OfType<BaseTypeDeclarationSyntax>().Select(t => t.Identifier.Text).Distinct().ToList();
            var matching = types.FirstOrDefault(t => SyntaxTargetResolver.ContainingTypeMatches(t, containerName));
            if (matching is null)
            {
                var listedTypes = types.Count == 0 ? "none" : string.Join(", ", types.Take(MaxTypesListed)) + (types.Count > MaxTypesListed ? $" (+{types.Count - MaxTypesListed} more)" : string.Empty);
                parts.Add($"Type '{containerName}' is not declared in this file (types declared here: {listedTypes}).");
            }
            else
            {
                scope = entries.Where(e => SyntaxTargetResolver.ContainingTypeMatches(e.Container, containerName)).ToList();
                scopeText = $"type '{matching}' of '{fileName}'";
            }
        }

        var ranked = scope
            .Select(e => (Entry: e, Cmp: Compare(memberName, e.Name)))
            .OrderBy(x => x.Cmp.Similar ? 0 : 1)
            .ThenBy(x => x.Cmp.Similar ? x.Cmp.Distance : 0)
            .ThenBy(x => x.Entry.Order)
            .ToList();

        var closest = ranked.Where(x => x.Cmp.Similar).Select(x => x.Entry).DistinctBy(e => e.Name).Take(MaxClosest).ToList();
        if (closest.Count > 0)
        {
            parts.Add("Closest names: " + string.Join(", ", closest.Select(e => $"'{e.Name}' ({SingularKind[e.Kind]})")) + ".");
        }

        if (ranked.Count == 0)
        {
            parts.Add($"{scopeText} declares no methods, constructors, properties, fields, events, indexers or enum members.");
        }
        else
        {
            var qualify = entries.Select(e => e.Container).Distinct().Count() > 1;
            var distinct = ranked.Select(x => x.Entry).DistinctBy(e => (e.Kind, e.Name, e.Container)).ToList();
            var shown = distinct.Take(MaxListed).ToList();
            var groups = KindOrder
                .Select(kind => (Kind: kind, Names: shown.Where(e => e.Kind == kind)
                    .Select(e => qualify && e.Container is not null ? $"{e.Container}.{e.Name}" : e.Name).Distinct().ToList()))
                .Where(g => g.Names.Count > 0)
                .Select(g => $"{g.Kind}: {string.Join(", ", g.Names)}");
            var more = distinct.Count > shown.Count ? $" (+{distinct.Count - shown.Count} more)" : string.Empty;
            parts.Add($"Members declared in {scopeText}{(closest.Count > 0 ? " (a similar name first)" : string.Empty)}: {string.Join("; ", groups)}{more}.");
        }

        var otherFiles = await FindDeclaringFilesAsync(document, memberName, cancellationToken);
        if (otherFiles.Count > 0)
        {
            parts.Add(string.Join("; ", otherFiles.Select(o => $"'{memberName}' is declared in {o.Path} ({SingularKind[o.Kind]}{(o.Container is null ? string.Empty : " of " + o.Container)})")) + ".");
        }

        var next = $"Next: {ToolCallHint.Call("ReadFile", ("filePath", path))} without memberName reads the whole file, or {ToolCallHint.Call("GetFileOutline", ("filePath", path))} lists every member";
        if (otherFiles.Count > 0)
        {
            next += $"; to read the one in the other file call {ToolCallHint.Call("ReadFile", ("filePath", otherFiles[0].Path), ("memberName", memberName))}";
        }

        parts.Add(next + ".");
        return string.Join(" ", parts);
    }

    private static List<Entry> Collect(SyntaxNode root)
    {
        var list = new List<Entry>();
        var order = 0;
        foreach (var member in root.DescendantNodes().OfType<MemberDeclarationSyntax>())
        {
            var container = (member.Parent as BaseTypeDeclarationSyntax)?.Identifier.Text;
            switch (member)
            {
                case MethodDeclarationSyntax m:
                    list.Add(new Entry("methods", m.Identifier.Text, container, order++));
                    break;
                case ConstructorDeclarationSyntax c:
                    list.Add(new Entry("constructors", c.Identifier.Text, container, order++));
                    break;
                case PropertyDeclarationSyntax p:
                    list.Add(new Entry("properties", p.Identifier.Text, container, order++));
                    break;
                case FieldDeclarationSyntax f:
                    foreach (var v in f.Declaration.Variables)
                    {
                        list.Add(new Entry("fields", v.Identifier.Text, container, order++));
                    }

                    break;
                case EventDeclarationSyntax e:
                    list.Add(new Entry("events", e.Identifier.Text, container, order++));
                    break;
                case EventFieldDeclarationSyntax ef:
                    foreach (var v in ef.Declaration.Variables)
                    {
                        list.Add(new Entry("events", v.Identifier.Text, container, order++));
                    }

                    break;
                case IndexerDeclarationSyntax:
                    list.Add(new Entry("indexers", "this", container, order++));
                    break;
                case EnumMemberDeclarationSyntax em:
                    list.Add(new Entry("enum members", em.Identifier.Text, container, order++));
                    break;
            }
        }

        return list;
    }

    // Other documents of the same project that declare a member with exactly this name (syntax only; at most MaxOtherFiles).
    private static async Task<List<(string Path, string Kind, string? Container)>> FindDeclaringFilesAsync(
        Document document, string memberName, CancellationToken cancellationToken)
    {
        var found = new List<(string, string, string?)>();
        foreach (var other in document.Project.Documents)
        {
            if (found.Count >= MaxOtherFiles)
            {
                break;
            }

            if (other.Id == document.Id || string.IsNullOrEmpty(other.FilePath))
            {
                continue;
            }

            var otherRoot = await other.GetSyntaxRootAsync(cancellationToken);
            var hit = otherRoot is null ? null : Collect(otherRoot).FirstOrDefault(e => string.Equals(e.Name, memberName, StringComparison.Ordinal));
            if (hit is not null)
            {
                found.Add((other.FilePath!, hit.Kind, hit.Container));
            }
        }

        return found;
    }

    // A name is "similar" when it is a short edit away, shares a prefix of 4+ characters, or (for names of 4+ characters)
    // contains, is contained in, or is a subsequence of the other: CalcDisc is similar to CalculateDiscountedTotal.
    private static (bool Similar, int Distance) Compare(string wanted, string actual)
    {
        var w = wanted.ToLowerInvariant();
        var a = actual.ToLowerInvariant();
        var distance = EditDistance(w, a);
        var prefix = 0;
        while (prefix < w.Length && prefix < a.Length && w[prefix] == a[prefix])
        {
            prefix++;
        }

        var similar = distance <= Math.Max(2, Math.Max(w.Length, a.Length) * 2 / 5)
                      || prefix >= 4
                      || (Math.Min(w.Length, a.Length) >= 4
                          && (w.Contains(a, StringComparison.Ordinal) || a.Contains(w, StringComparison.Ordinal) || IsSubsequence(w, a) || IsSubsequence(a, w)));
        return (similar, distance);
    }

    private static bool IsSubsequence(string needle, string haystack)
    {
        var i = 0;
        foreach (var c in haystack)
        {
            if (i < needle.Length && needle[i] == c)
            {
                i++;
            }
        }

        return i == needle.Length;
    }

    private static int EditDistance(string a, string b)
    {
        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++)
        {
            previous[j] = j;
        }

        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
            }

            (previous, current) = (current, previous);
        }

        return previous[b.Length];
    }
}
