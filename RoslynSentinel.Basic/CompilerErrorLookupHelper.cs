using System.Text.RegularExpressions;

using RoslynSentinel.Common;

namespace RoslynSentinel.Basic;

/// <summary>
/// Turns a post-write <see cref="DiagnosticReport"/> into guidance a model can act on directly,
/// instead of a raw diagnostics dump. Models that hit a generic "here are the new compiler errors"
/// message have been observed pattern-matching the wrong fix category (e.g. adding a `using` for a
/// CS0103 that's actually a missing `ClassName.` qualifier) and then repeating that wrong fix under
/// slightly different framing rather than re-reading the diagnostic. For known diagnostic IDs this
/// adds a targeted hint and symbol-search candidates (CS0103 unresolved names, CS0117/CS1061
/// missing members); anything unrecognized falls through to the plain diagnostics text so nothing
/// regresses for codes not taught yet. CS0122 (inaccessible due to protection level) additionally
/// states the member's current accessibility and the caller's enclosing type directly, rather than
/// leaving the model to infer accessibility from the absence of an error.
/// </summary>
public static class CompilerErrorLookupHelper
{
    private static readonly Regex Cs0103NameRegex = new(@"The name '([^']+)' does not exist in the current context", RegexOptions.Compiled);
    private static readonly Regex MissingMemberRegex = new(@"'([^']+)' does not contain a definition for '([^']+)'", RegexOptions.Compiled);
    private static readonly Regex Cs0122InaccessibleRegex = new(@"'([^']+)' is inaccessible due to its protection level", RegexOptions.Compiled);

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

        if (diagnostic.Id is "CS0117" or "CS1061")
        {
            return baseText + "\n" + await DescribeMissingMemberAsync(diagnostic, symbolNavigationEngine, cancellationToken);
        }

        if (diagnostic.Id == "CS0122")
        {
            return baseText + "\n" + await DescribeCs0122Async(diagnostic, symbolNavigationEngine, cancellationToken);
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

        FileUsingContext? usingContext = null;
        try
        {
            usingContext = await symbolNavigationEngine.GetFileUsingContextAsync(diagnostic.FilePath, cancellationToken);
        }
        catch (Exception)
        {
            // Fall through without namespace triage below — the plain candidate list still helps.
        }

        var missingUsingCandidates = usingContext != null
            ? candidates.Where(c => !usingContext.IsNamespaceInScope(c.ContainingNamespace)).ToList()
            : [];

        var suggestions = candidates
            .Take(5)
            .Select(c => c.ContainingType != null
                ? $"    - {c.ContainingType}.{c.SymbolName} ({c.Signature}) — {c.FilePath}:{c.Line}"
                : $"    - {c.SymbolName} ({c.Signature}) — {c.FilePath}:{c.Line}");

        if (usingContext != null && missingUsingCandidates.Count > 0)
        {
            var missingNamespaces = missingUsingCandidates
                .Select(c => c.ContainingNamespace)
                .Where(ns => ns != null)
                .Distinct()
                .Select(ns => $"using {ns};");

            return $"  '{name}' exists elsewhere in the solution in a namespace not currently imported here — add one of the following `using` directives:\n"
                + string.Join("\n", missingNamespaces.Select(u => "    " + u))
                + "\n  Candidates found:\n"
                + string.Join("\n", suggestions);
        }

        return $"  '{name}' exists elsewhere in the solution but is not in scope here — most likely it needs to be called with its containing type as a qualifier (a `using` directive does not import another class's static members" + (usingContext != null ? "; its namespace is already imported or is this file's own namespace" : "") + "). Candidates found:\n"
            + string.Join("\n", suggestions);
    }

    private static async Task<string> DescribeMissingMemberAsync(
        DiagnosticInfo diagnostic,
        SymbolNavigationEngine symbolNavigationEngine,
        CancellationToken cancellationToken)
    {
        var match = MissingMemberRegex.Match(diagnostic.Message);
        if (!match.Success)
        {
            return "  This is a missing-member error, but the type/member names could not be extracted from the diagnostic text to search for them.";
        }

        var typeName = match.Groups[1].Value;
        var memberName = match.Groups[2].Value;
        var isExtensionCandidate = diagnostic.Id == "CS1061";

        // LocateSymbolAsync's containingType filter matches MinimallyQualifiedFormat (simple name,
        // no generic arguments) exactly — the diagnostic's type name may be fully-qualified or
        // generic-decorated (e.g. "System.Collections.Generic.List<int>"), so reduce to a simple
        // name before using it as a scope filter. If reduction still doesn't match, the solution-wide
        // fallback below still finds the member by name alone.
        var simpleTypeName = typeName.Split('.').Last().Split('<').First();

        List<SymbolLocation> candidates;
        try
        {
            // Scope to the named type first — a typo'd member on the right type is the common case.
            candidates = await symbolNavigationEngine.LocateSymbolAsync(
                memberName, exactMatch: true, containingType: simpleTypeName, cancellationToken: cancellationToken);

            if (candidates.Count == 0)
            {
                // Not on that type at all — search the whole solution in case the member exists on a
                // different, similarly-named type (or the model has the wrong receiver entirely).
                candidates = await symbolNavigationEngine.LocateSymbolAsync(
                    memberName, exactMatch: true, cancellationToken: cancellationToken);
            }
        }
        catch (Exception)
        {
            candidates = [];
        }

        if (candidates.Count == 0)
        {
            return isExtensionCandidate
                ? $"  No member or extension method named '{memberName}' was found anywhere in the solution for type '{typeName}'. This is likely a typo, a member that needs to be added to '{typeName}', or an extension method whose defining namespace needs a `using` — check FindExtensionMethods if one is expected to exist."
                : $"  No member named '{memberName}' was found anywhere in the solution. This is likely a typo, or '{memberName}' needs to be added to '{typeName}'.";
        }

        var suggestions = candidates
            .Take(5)
            .Select(c => c.ContainingType != null
                ? $"    - {c.ContainingType}.{c.SymbolName} ({c.Signature}) — {c.FilePath}:{c.Line}"
                : $"    - {c.SymbolName} ({c.Signature}) — {c.FilePath}:{c.Line}");

        var onNamedType = candidates.Any(c => string.Equals(c.ContainingType, simpleTypeName, StringComparison.OrdinalIgnoreCase));
        var explanation = onNamedType
            ? $"  '{memberName}' exists on '{typeName}' but isn't accessible the way it was called (wrong overload, wrong accessibility, or a static/instance mismatch)."
            : $"  '{memberName}' was not found on '{typeName}', but a member with that name exists elsewhere — likely the wrong receiver type, or '{memberName}' needs to be added to '{typeName}' instead. Candidates found:";

        return explanation + "\n" + string.Join("\n", suggestions);
    }

    private static async Task<string> DescribeCs0122Async(
        DiagnosticInfo diagnostic,
        SymbolNavigationEngine symbolNavigationEngine,
        CancellationToken cancellationToken)
    {
        var match = Cs0122InaccessibleRegex.Match(diagnostic.Message);
        if (!match.Success)
        {
            return "  This is an accessibility error, but the member name could not be extracted from the diagnostic text to search for it.";
        }

        var qualifiedName = match.Groups[1].Value;
        var lastDot = qualifiedName.LastIndexOf('.');
        var simpleTypeName = lastDot >= 0 ? qualifiedName[..lastDot] : qualifiedName;
        simpleTypeName = simpleTypeName.Split('.').Last();
        var memberName = lastDot >= 0 ? qualifiedName[(lastDot + 1)..].Split('(').First() : simpleTypeName;

        List<SymbolLocation> candidates;
        try
        {
            candidates = await symbolNavigationEngine.LocateSymbolAsync(
                memberName, exactMatch: true, containingType: simpleTypeName, cancellationToken: cancellationToken);
        }
        catch (Exception)
        {
            candidates = [];
        }

        var symbol = candidates.FirstOrDefault();
        if (symbol == null)
        {
            return $"  '{qualifiedName}' is inaccessible from this call site, but its declaration could not be located to report its current accessibility.";
        }

        string? callerType = null;
        try
        {
            callerType = await symbolNavigationEngine.GetEnclosingTypeNameAsync(
                diagnostic.FilePath, diagnostic.StartLine, diagnostic.StartColumn, cancellationToken);
        }
        catch (Exception)
        {
            // Fall through without a caller type name below — the accessibility guidance still helps.
        }

        var accessibility = symbol.Accessibility.ToLowerInvariant();
        var calledFrom = callerType != null ? $" from {callerType}" : string.Empty;
        return $"  '{qualifiedName}' is currently {accessibility}. It must be changed to a level accessible{calledFrom} ({diagnostic.FilePath}:{diagnostic.StartLine}).\n"
            + $"  Note: accessibility is set per-member, not inherited from the containing type's accessibility — raising {simpleTypeName}'s own accessibility does not change {symbol.SymbolName}'s.";
    }
}
