namespace RoslynSentinel.Common;

public static class ProjectLookup
{
    /// <summary>
    /// Finds the project with the given name in the loaded solution. Returns null when the name is null or empty or no project has that name; the caller keeps its own not-found handling. Throws ToolAmbiguousMatchException when several projects share the name but have different file paths. Several projects that share one file path (a multi-target load) count as one project and the first is returned.
    /// </summary>
    public static Microsoft.CodeAnalysis.Project? FindProject(Microsoft.CodeAnalysis.Solution solution, string? name, bool ignoreCase = false)
    {
        if (string.IsNullOrEmpty(name)) { return null; }
        var comparison = ignoreCase ? System.StringComparison.OrdinalIgnoreCase : System.StringComparison.Ordinal;
        var matches = solution.Projects.Where(p => string.Equals(p.Name, name, comparison)).ToList();
        if (matches.Count == 0) { return null; }
        if (matches.Select(p => (p.FilePath ?? "<null>").ToUpperInvariant()).Distinct().Count() > 1)
        {
            var candidates = string.Join("; ", matches.Select(p => $"{p.Name} ({p.FilePath ?? "<null>"})"));
            throw new ToolAmbiguousMatchException(
                $"Project name '{name}' matches {matches.Count} projects in the loaded solution: {candidates}. Next: rename one of the projects or scope by file instead.");
        }
        return matches[0];
    }
}
