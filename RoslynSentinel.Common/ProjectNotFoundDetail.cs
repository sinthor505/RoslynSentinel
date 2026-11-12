using System;
using System.Collections.Generic;
using System.Linq;

namespace RoslynSentinel.Common;

/// <summary>
/// Facts and retry options for a project-not-found error: the loaded count, ranked candidates (or the
/// no-similar-name fact), and the loaded project names. Shared by every tool that resolves a project by name.
/// </summary>
public static class ProjectNotFoundDetail
{
    private const int MaxListedProjects = 10;

    /// <summary>
    /// Builds the detail sentences for a project name that did not resolve. Names are de-duplicated (ordinal)
    /// before counting and ranking; the listing puts similar names first, caps at 10 and appends "(+N more)".
    /// </summary>
    public static string Build(string wanted, IReadOnlyList<string> projectNames, string paramName = "projectName")
    {
        var names = (projectNames ?? Array.Empty<string>())
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct()
            .ToList();

        var parts = new List<string>
        {
            $"Project '{wanted}' was not found among the {names.Count} loaded project(s); project names are compared ignoring case."
        };

        var candidates = NameCandidates.Rank(wanted, names, n => n).Select(c => c.Item).ToList();
        foreach (var candidate in candidates)
        {
            parts.Add($"Did you mean '{candidate}'? Retry with {paramName}: '{candidate}'.");
        }

        if (candidates.Count == 0)
        {
            parts.Add($"No loaded project has a similar name to '{wanted}'.");
        }

        var listed = candidates.Concat(names.Where(n => !candidates.Contains(n))).ToList();
        var shown = listed.Take(MaxListedProjects).ToList();
        var more = listed.Count - shown.Count;
        var list = shown.Count == 0
            ? "none"
            : string.Join(", ", shown) + (more > 0 ? $" (+{more} more)" : string.Empty);
        parts.Add($"Loaded projects: {list}.");

        return string.Join(" ", parts);
    }
}
