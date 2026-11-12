using RoslynSentinel.Common;

namespace RoslynSentinel.Engines.Basic;

/// <summary>
/// Builds the tail of a not-found error for a declaration lookup (Decision 2 standard): the nearest declared names
/// with a retry hint, or, when none is similar, a GetFileOutline call and the fact that the name is not declared there.
/// Ranking is delegated to <see cref="NameCandidates.Rank{T}"/>.
/// </summary>
public static class DeclarationNotFoundDetail
{
    /// <summary>
    /// Returns the nearest of <paramref name="declaredNames"/> to <paramref name="wanted"/> as retry hints that name
    /// <paramref name="paramName"/>; with no similar name, says so and points at GetFileOutline for <paramref name="filePath"/>.
    /// </summary>
    public static string Build(string kindLabel, string paramName, string wanted, IEnumerable<string> declaredNames, string filePath)
    {
        var names = declaredNames.Distinct(StringComparer.Ordinal).ToList();
        var candidates = NameCandidates.Rank(wanted, names, name => name);
        if (candidates.Count > 0)
        {
            var hints = candidates.Select(candidate => $"Did you mean '{candidate.Item}'? Retry with {paramName}: '{candidate.Item}'.");
            return "The list shows the nearest names of the file, nothing more. " + string.Join(" ", hints);
        }

        var outline = ToolCallHint.Call("GetFileOutline", ("filePath", filePath));
        return $"No {kindLabel} declared in '{filePath}' has a similar name to '{wanted}'. To see what the file declares, call {outline}. '{wanted}' is not in that outline, so it does not exist in this file.";
    }

    /// <summary>
    /// Returns the text for a lookup where the file declares no <paramref name="kindLabel"/> at all.
    /// </summary>
    public static string BuildNoneDeclared(string kindLabel, string filePath)
    {
        var outline = ToolCallHint.Call("GetFileOutline", ("filePath", filePath));
        return $"No {kindLabel} is declared in '{filePath}'. To see what the file declares, call {outline}.";
    }
}
