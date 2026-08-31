using System.Text.RegularExpressions;

using RoslynSentinel.Common;

namespace RoslynSentinel.Basic;

/// <summary>
/// Turns a post-write <see cref="DiagnosticReport"/> into guidance a model can act on directly,
/// instead of a raw diagnostics dump. Models that hit a generic "here are the new compiler errors"
/// message have been observed pattern-matching the wrong fix category (e.g. adding a `using` for a
/// CS0103 that's actually a missing `ClassName.` qualifier) and then repeating that wrong fix under
/// slightly different framing rather than re-reading the diagnostic. For known diagnostic IDs this
/// adds a targeted hint (and, for CS0103, symbol-search candidates); anything unrecognized falls
/// through to the plain diagnostics text so nothing regresses for codes not taught yet.
/// </summary>
public static class CompilerErrorLookupHelper
{
    private static readonly Regex Cs0103NameRegex = new(@"The name '([^']+)' does not exist in the current context", RegexOptions.Compiled);

    public static async Task<string> DescribeAsync(
        DiagnosticReport report,
        SymbolNavigationEngine symbolNavigationEngine,
        CancellationToken cancellationToken = default)
    {
        var parts = new List<string>();
        foreach (var diagnostic in report.Diagnostics)
        {
            parts.Add(await DescribeOneAsync(diagnostic, symbolNavigationEngine, cancellationToken));
        }

        return string.Join("\n", parts);
    }

    private static async Task<string> DescribeOneAsync(
        DiagnosticInfo diagnostic,
        SymbolNavigationEngine symbolNavigationEngine,
        CancellationToken cancellationToken)
    {
        var location = $"{diagnostic.FilePath}:{diagnostic.StartLine}";
        var baseText = $"{diagnostic.Id} at {location}: {diagnostic.Message}";

        if (diagnostic.Id == "CS0103")
        {
            return baseText + "\n" + await DescribeCs0103Async(diagnostic, symbolNavigationEngine, cancellationToken);
        }

        return baseText;
    }

    private static async Task<string> DescribeCs0103Async(
        DiagnosticInfo diagnostic,
        SymbolNavigationEngine symbolNavigationEngine,
        CancellationToken cancellationToken)
    {
        var match = Cs0103NameRegex.Match(diagnostic.Message);
        if (!match.Success)
        {
            return "  This is an unresolved-name error, but the name could not be extracted from the diagnostic text to search for it.";
        }

        var name = match.Groups[1].Value;

        List<SymbolLocation> candidates;
        try
        {
            candidates = await symbolNavigationEngine.LocateSymbolAsync(name, exactMatch: true, cancellationToken: cancellationToken);
        }
        catch (Exception)
        {
            return $"  '{name}' is not a known symbol at this location. Common causes: a typo, a missing `using` for its namespace, or (if it's a static member of another class) calling it unqualified — C# does not resolve another class's static members just because you share a namespace or `using` it; qualify the call as ClassName.{name}(...).";
        }

        if (candidates.Count == 0)
        {
            return $"  No symbol named '{name}' was found anywhere in the solution. This is likely a typo, or the member genuinely doesn't exist yet and needs to be added.";
        }

        var suggestions = candidates
            .Take(5)
            .Select(c => c.ContainingType != null
                ? $"    - {c.ContainingType}.{c.SymbolName} ({c.Signature}) — {c.FilePath}:{c.Line}"
                : $"    - {c.SymbolName} ({c.Signature}) — {c.FilePath}:{c.Line}");

        return $"  '{name}' exists elsewhere in the solution but is not in scope here — most likely it needs to be called with its containing type as a qualifier (a `using` directive does not import another class's static members). Candidates found:\n"
            + string.Join("\n", suggestions);
    }
}
