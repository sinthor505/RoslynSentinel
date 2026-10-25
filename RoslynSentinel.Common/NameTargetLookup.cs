namespace RoslynSentinel.Common;

public static class NameTargetLookup
{
    /// <summary>
    /// Resolves a name lookup to one declaration: null when there are no matches (the caller keeps its own not-found message), the match when there is exactly one, and ToolAmbiguousMatchException listing each candidate's file and line when there are several.
    /// </summary>
    public static T? RequireSingle<T>(System.Collections.Generic.IReadOnlyList<T> matches, string kind, string name) where T : Microsoft.CodeAnalysis.SyntaxNode
    {
        // A multi-target build loads one file into several projects, so one declaration can appear once per project; keep one copy of each.
        var distinct = new System.Collections.Generic.List<T>();
        foreach (var candidate in matches)
        {
            var isCopy = distinct.Any(kept =>
                !string.IsNullOrEmpty(kept.SyntaxTree.FilePath)
                && string.Equals(kept.SyntaxTree.FilePath, candidate.SyntaxTree.FilePath, System.StringComparison.OrdinalIgnoreCase)
                && kept.Span.Equals(candidate.Span));
            if (!isCopy) { distinct.Add(candidate); }
        }
        matches = distinct;
        if (matches.Count == 0) { return null; }
        if (matches.Count == 1) { return matches[0]; }
        // An interface declaration plus its one implementation is one logical member, not ambiguity.
        var interfaceMembers = matches.Where(m => m.Parent is Microsoft.CodeAnalysis.CSharp.Syntax.InterfaceDeclarationSyntax).ToList();
        if (interfaceMembers.Count <= 1 && matches.Count - interfaceMembers.Count <= 1) { return matches[0]; }
        var descriptions = matches.Select(m =>
        {
            var path = string.IsNullOrEmpty(m.SyntaxTree.FilePath) ? "<unknown>" : m.SyntaxTree.FilePath;
            var line = m.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
            var firstLine = m.ToString().Split('\n')[0].Trim();
            var snippet = firstLine.Length > 80 ? firstLine.Substring(0, 80) + "..." : firstLine;
            return $"{path}:{line} `{snippet}`";
        });
        throw new ToolAmbiguousMatchException(
            $"{kind} '{name}' matches {matches.Count} declarations: {string.Join("; ", descriptions)}. Next: rename one of them, or narrow the target with contextSnippet or a containing type when the tool accepts one.");
    }
}
