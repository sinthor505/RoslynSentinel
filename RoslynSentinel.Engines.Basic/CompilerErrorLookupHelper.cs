using System.Text.RegularExpressions;

namespace RoslynSentinel.Engines.Basic;

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
/// leaving the model to infer accessibility from the absence of an error. CS0101/CS0111 (duplicate
/// namespace member / duplicate member signature) name the real file the colliding symbol is
/// already declared in -> these fire when CreateFile/ApplyDiff targets a wrong-but-plausible path
/// for a file that already exists elsewhere in the same project, and without the real path the
/// model has no way to distinguish "you introduced a genuine duplicate" from "you guessed the
/// wrong path for an existing file" (see docs/current/project_readfile_createfile_path_inconsistency_bug.md).
/// </summary>
public static class CompilerErrorLookupHelper
{
    private static readonly Regex Cs0103NameRegex = new(@"The name '([^']+)' does not exist in the current context", RegexOptions.Compiled);
    private static readonly Regex MissingMemberRegex = new(@"'([^']+)' does not contain a definition for '([^']+)'", RegexOptions.Compiled);
    private static readonly Regex Cs0122InaccessibleRegex = new(@"'([^']+)' is inaccessible due to its protection level", RegexOptions.Compiled);
    private static readonly Regex Cs0101DuplicateNamespaceMemberRegex = new(@"The namespace '([^']+)' already contains a definition for '([^']+)'", RegexOptions.Compiled);
    private static readonly Regex Cs0111DuplicateMemberRegex = new(@"Type '([^']+)' already defines a member called '([^']+)'", RegexOptions.Compiled);
    private static readonly Regex Cs0102DuplicateTypeMemberRegex = new(@"The type '([^']+)' already contains a definition for '([^']+)'", RegexOptions.Compiled);
    private static readonly Regex Cs0266ConvertRegex = new(@"Cannot implicitly convert type '([^']+)' to '([^']+)'", RegexOptions.Compiled);
    private static readonly Regex AccessModifierRegex = new(@"\b(public|private|protected|internal)\b", RegexOptions.Compiled);
    private static readonly Regex Cs0737NotPublicRegex = new(@"'([^']+)' cannot implement an interface member because it is not public", RegexOptions.Compiled);

    // TODO pass editedFilePaths to DescribeAsync at the remaining callers
    // isToolActive: whether a tool may be named to the caller (ActiveToolSurface.IsCallable); null names no tool, so existing callers keep today's text.
    public static async Task<string> DescribeAsync(
        DiagnosticReport report,
        SymbolNavigationEngine symbolNavigationEngine,
        CancellationToken cancellationToken = default,
        IReadOnlyCollection<string>? editedFilePaths = null,
        Func<string, bool>? isToolActive = null)
    {
        // Identical diagnostics (same Id AND same Message text) collapse into one line: a single
        // root cause repeated at N sites (e.g. 86 identical CS7036s from one bad MoveMember
        // callSiteFixups value) otherwise reads as N separate problems. GroupBy preserves the
        // first-occurrence order of keys, so output order stays stable relative to the report.
        var parts = new List<string>();

        // A CS0111 inside a type that a CS0101 in the same report says is declared twice follows from that duplicate type; the
        // wrong-path advice of DescribeDuplicateDefinitionAsync misleads there (eval run 20261111-061005-246 T12), so point at
        // the duplicate type instead.
        var duplicatedTypes = report.Diagnostics
            .Where(d => d.Id == "CS0101")
            .Select(d => Cs0101DuplicateNamespaceMemberRegex.Match(d.Message))
            .Where(m => m.Success)
            .Select(m => m.Groups[2].Value)
            .ToHashSet(StringComparer.Ordinal);

        async Task<string?> HintAsync(DiagnosticInfo diagnostic)
        {
            if (diagnostic.Id == "CS0111"
                && Cs0111DuplicateMemberRegex.Match(diagnostic.Message) is { Success: true } cs0111
                && cs0111.Groups[1].Value.Split('.').Last() is var typeName
                && duplicatedTypes.Contains(typeName))
            {
                return $"  This follows from '{typeName}' being declared twice (see its CS0101); removing the extra '{typeName}' declaration fixes this too.";
            }

            return await DescribeHintAsync(diagnostic, symbolNavigationEngine, cancellationToken, editedFilePaths, isToolActive);
        }
        foreach (var group in report.Diagnostics.GroupBy(d => (d.Id, d.Message)))
        {
            var items = group.ToList();
            var first = items[0];
            if (items.Count == 1)
            {
                var single = $"{first.Id} at {first.FilePath}:{first.StartLine}: {first.Message}" + (string.IsNullOrEmpty(first.SourceLine) ? string.Empty : "\n    > " + first.SourceLine);
                var singleHint = await HintAsync(first);
                parts.Add(singleHint == null ? single : single + "\n" + singleHint);
                continue;
            }

            var locations = items.Select(d => $"{d.FilePath}:{d.StartLine}").Distinct().ToList();
            var shown = string.Join(", ", locations.Take(MaxGroupedLocations));
            var more = locations.Count > MaxGroupedLocations ? $" (+{locations.Count - MaxGroupedLocations} more)" : string.Empty;
            var header = $"{first.Id} (x{items.Count}): {first.Message} at: {shown}{more}" + (string.IsNullOrEmpty(first.SourceLine) ? string.Empty : "\n    > " + first.SourceLine);
            var hint = await HintAsync(first);
            parts.Add(hint == null ? header : header + "\n" + hint);
        }

        return string.Join("\n", parts);
    }

    /// <summary>
    /// Maximum number of file:line locations listed on one grouped diagnostic line before the rest
    /// are summarized as "(+N more)".
    /// </summary>
    public const int MaxGroupedLocations = 10;

    /// <summary>
    /// Returns the targeted guidance text for a known diagnostic ID, or null when the ID has no
    /// dedicated hint. For a grouped diagnostic this is computed once from the first occurrence -
    /// every member of a group shares the same Id and Message, so the hint is identical.
    /// </summary>
    private static async Task<string?> DescribeHintAsync(
        DiagnosticInfo diagnostic,
        SymbolNavigationEngine symbolNavigationEngine,
        CancellationToken cancellationToken,
        IReadOnlyCollection<string>? editedFilePaths,
        Func<string, bool>? isToolActive)
    {
        if (diagnostic.Id == "CS0103")
        {
            return await DescribeCs0103Async(diagnostic, symbolNavigationEngine, cancellationToken);
        }

        if (diagnostic.Id is "CS0117" or "CS1061")
        {
            return await DescribeMissingMemberAsync(diagnostic, symbolNavigationEngine, cancellationToken, editedFilePaths);
        }

        if (diagnostic.Id == "CS0122")
        {
            return await DescribeCs0122Async(diagnostic, symbolNavigationEngine, cancellationToken);
        }

        if (diagnostic.Id is "CS0101" or "CS0102")
        {
            return await DescribeDuplicateDeclarationAsync(diagnostic, symbolNavigationEngine, cancellationToken, editedFilePaths, isToolActive);
        }

        if (diagnostic.Id == "CS0111")
        {
            return await DescribeDuplicateDefinitionAsync(diagnostic, symbolNavigationEngine, cancellationToken);
        }

        if (diagnostic.Id == "CS0266")
        {
            return await DescribeCs0266Async(diagnostic, symbolNavigationEngine, cancellationToken);
        }

        if (diagnostic.Id == "CS0737")
        {
            return DescribeCs0737(diagnostic);
        }

        if (diagnostic.Id == "CS0107")
        {
            return DescribeCs0107(diagnostic);
        }

        if (diagnostic.Id == "CS0144")
        {
            return await DescribeCs0144Async(diagnostic, symbolNavigationEngine, cancellationToken);
        }

        return null;
    }

    /// <summary>
    /// CS0266 where the target is an interface the source class does not implement: the field or parameter was retyped to the
    /// interface in an edit that does not also add the interface to the class. Verified from the committed semantic model
    /// (target is an interface, source is a non-interface type that does not list it); any other CS0266 (numeric narrowing,
    /// explicit-cast cases, generics, unresolvable names) gets no hint.
    /// </summary>
    private static async Task<string?> DescribeCs0266Async(
        DiagnosticInfo diagnostic,
        SymbolNavigationEngine symbolNavigationEngine,
        CancellationToken cancellationToken)
    {
        var match = Cs0266ConvertRegex.Match(diagnostic.Message);
        if (!match.Success)
        {
            return null;
        }

        var source = match.Groups[1].Value;
        var target = match.Groups[2].Value;
        if (source.Contains('<') || target.Contains('<') || source.Contains('[') || target.Contains('['))
        {
            return null;
        }

        var sourceName = source.Split('.').Last();
        var targetName = target.Split('.').Last();
        try
        {
            var targetReport = await symbolNavigationEngine.GetTypeHierarchyAsync(targetName, cancellationToken: cancellationToken);
            if (targetReport.IsError || !targetReport.IsInterface)
            {
                return null;
            }

            var sourceReport = await symbolNavigationEngine.GetTypeHierarchyAsync(sourceName, cancellationToken: cancellationToken);
            if (sourceReport.IsError || sourceReport.IsInterface
                || sourceReport.ImplementedInterfaces.Any(i => i.Split('.').Last() == targetName))
            {
                return null;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }

        return $"  '{sourceName}' does not implement '{targetName}'. Add ': {targetName}' to {sourceName}'s declaration in the same atomic edit as this change.";
    }

    /// <summary>
    /// Up to 3 non-abstract classes that implement the named interface, ordered by name.
    /// Empty when the name is not an interface in the committed solution, it has no such implementer, or the lookup fails.
    /// </summary>
    private static async Task<List<string>> FindConcreteImplementersAsync(
        string interfaceName,
        SymbolNavigationEngine symbolNavigationEngine,
        CancellationToken cancellationToken)
    {
        const int maxNamed = 3;
        var concrete = new List<string>();
        try
        {
            var report = await symbolNavigationEngine.GetTypeHierarchyAsync(interfaceName, cancellationToken: cancellationToken);
            if (report.IsError || !report.IsInterface)
            {
                return concrete;
            }

            // TypeHierarchyEntry.Kind is Roslyn's TypeKind name.
            var classNames = report.ImplementingTypes
                .Where(t => t.Kind == "Class" && !t.TypeName.Contains('<'))
                .Select(t => t.TypeName)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(n => n, StringComparer.Ordinal);
            foreach (var className in classNames)
            {
                // The hierarchy entries do not say whether a class is abstract; ask per candidate until enough are found.
                var classReport = await symbolNavigationEngine.GetTypeHierarchyAsync(className.Split('.').Last(), cancellationToken: cancellationToken);
                if (classReport.IsError || classReport.IsAbstract)
                {
                    continue;
                }

                concrete.Add(className);
                if (concrete.Count == maxNamed)
                {
                    break;
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return [];
        }

        return concrete;
    }

    /// <summary>
    /// CS0107 ("More than one protection modifier"): typically a whole-file write that added the new access modifier beside the old one
    /// ("private static internal"). The modifiers found are read from the quoted source line, up to the first '(', '=', '{', '&lt;' or ';',
    /// and left out when the line is not available.
    /// </summary>
    /// <summary>
    /// CS0144 ("Cannot create an instance of the abstract type or interface 'X'"): typically a field retyped to an interface
    /// that kept its target-typed <c>new()</c>. 10-chain runs 214 and 216 (2026-11-10) hit it on step 7 and gave the step up.
    /// </summary>
    private static async Task<string> DescribeCs0144Async(
        DiagnosticInfo diagnostic,
        SymbolNavigationEngine symbolNavigationEngine,
        CancellationToken cancellationToken)
    {
        var quoted = System.Text.RegularExpressions.Regex.Match(diagnostic.Message ?? string.Empty, "'([^']+)'");
        var type = quoted.Success ? quoted.Groups[1].Value : "the interface";
        var implementers = quoted.Success
            ? await FindConcreteImplementersAsync(type.Split('.').Last(), symbolNavigationEngine, cancellationToken)
            : [];
        if (implementers.Count > 0)
        {
            return $"  {type} cannot be created with new() or new {type}(). Keep {type} as the declared type and name a concrete class that implements it on the right-hand side, for example: private readonly {type} _field = new {implementers[0]}();"
                + $" Concrete classes in the solution that implement {type}: {string.Join(", ", implementers)}.";
        }

        var text = $"  {type} cannot be created with new() or new {type}(). Keep {type} as the declared type; the right-hand side can name a concrete class that implements it.";
        if (!quoted.Success)
        {
            return text;
        }

        var shortName = type.Split('.').Last();
        if (!await HasNoImplementingTypesAsync(shortName, symbolNavigationEngine, cancellationToken))
        {
            return text;
        }

        text += $" No class in the solution implements '{type}'.";
        var structural = await FindStructuralMatchesAsync(shortName, symbolNavigationEngine, cancellationToken);
        if (structural.Count > 0)
        {
            text += " " + string.Join(" ", structural.Select(n => $"'{n}' declares all of {type}'s members but does not list {type} as a base type."))
                + (structural.Count == 1
                    ? $" Listing {type} as a base type of {structural[0]} makes new {structural[0]}() valid for a {type}-typed field."
                    : $" Listing {type} as a base type of one of them (for example {structural[0]}) makes new {structural[0]}() valid for a {type}-typed field.");
        }

        return text;
    }

    /// <summary>
    /// True when the name resolves to an interface in the committed solution that no type implements; false when it does not
    /// resolve, is not an interface, has any implementing type (abstract ones included) or the lookup fails.
    /// </summary>
    private static async Task<bool> HasNoImplementingTypesAsync(
        string interfaceName,
        SymbolNavigationEngine symbolNavigationEngine,
        CancellationToken cancellationToken)
    {
        try
        {
            var report = await symbolNavigationEngine.GetTypeHierarchyAsync(interfaceName, cancellationToken: cancellationToken);
            return !report.IsError && report.IsInterface && report.ImplementingTypes.Count == 0;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>
    /// Up to 3 classes that declare every member of the named interface without listing it; empty when none or the scan fails.
    /// </summary>
    private static async Task<List<string>> FindStructuralMatchesAsync(
        string interfaceName,
        SymbolNavigationEngine symbolNavigationEngine,
        CancellationToken cancellationToken)
    {
        try
        {
            return await symbolNavigationEngine.FindStructuralInterfaceMatchesAsync(interfaceName, 3, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return [];
        }
    }

    private static string DescribeCs0107(DiagnosticInfo diagnostic)
    {
        var found = new List<string>();
        if (!string.IsNullOrWhiteSpace(diagnostic.SourceLine))
        {
            var line = diagnostic.SourceLine;
            var header = line[..(line.IndexOfAny(['(', '=', '{', '<', ';']) is var cut and >= 0 ? cut : line.Length)];
            found = AccessModifierRegex.Matches(header).Select(m => m.Value).ToList();
        }

        var foundText = found.Count >= 2 ? $" (found: {string.Join(" ", found)})" : string.Empty;
        return $"  A declaration may have only one access modifier{foundText}. To change accessibility, replace the old modifier with the new one rather than adding a second.";
    }

    /// <summary>CS0737: the member that would implement the interface member is not public; the message already names it.</summary>
    private static string? DescribeCs0737(DiagnosticInfo diagnostic)
    {
        var match = Cs0737NotPublicRegex.Match(diagnostic.Message);
        return match.Success
            ? $"  The implementing member must be public; make '{match.Groups[1].Value}' public (interface members are implemented by public members)."
            : null;
    }

    /// <summary>
    /// True when the quoted source line uses <paramref name="name"/> as an unqualified call (<c>name(</c> or <c>name&lt;T&gt;(</c>).
    /// The line is the only syntax the diagnostic carries: the committed model may not contain the pending edit, so a syntax
    /// lookup by line and column could land on different code. No source line means no claim.
    /// </summary>
    private static bool IsInvocationUse(string? sourceLine, string name)
    {
        if (string.IsNullOrWhiteSpace(sourceLine))
        {
            return false;
        }

        return Regex.IsMatch(sourceLine, $@"(?<![\w.]){Regex.Escape(name)}\s*(<[^<>()]*>)?\s*\(");
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
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return $"  '{name}' is not a known symbol at this location. Common causes: a typo, a missing `using` for its namespace, or (if it's a static member of another class) calling it unqualified - C# does not resolve another class's static members just because you share a namespace or `using` it; qualify the call as ClassName.{name}(...).";
        }

        if (candidates.Count == 0)
        {
            if (IsInvocationUse(diagnostic.SourceLine, name))
            {
                // The committed model may not contain the pending edit, so the enclosing type is best effort; a placeholder keeps the call shape complete.
                string? callerType = null;
                try
                {
                    callerType = await symbolNavigationEngine.GetEnclosingTypeNameAsync(
                        diagnostic.FilePath, diagnostic.StartLine, diagnostic.StartColumn, cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Fall through with the placeholder container name below.
                }

                var addCall = ToolCallHint.Call(
                    "Member",
                    ("operation", "addMember"),
                    ("containerName", callerType ?? "<the type that contains the call>"),
                    ("newMemberSource", $"<declaration of {name}>"));
                return $"  No symbol named '{name}' was found anywhere in the solution, and it is called here, so the method itself is missing. Declare '{name}' in the same newContent as the code that calls it, or add it first with {addCall} and then re-apply this edit.";
            }

            return $"  No symbol named '{name}' was found anywhere in the solution. This is likely a typo, or the member genuinely doesn't exist yet and needs to be added.";
        }

        FileUsingContext? usingContext = null;
        try
        {
            usingContext = await symbolNavigationEngine.GetFileUsingContextAsync(diagnostic.FilePath, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Fall through without namespace triage below -> the plain candidate list still helps.
        }

        var missingUsingCandidates = usingContext != null
            ? candidates.Where(c => !usingContext.IsNamespaceInScope(c.ContainingNamespace)).ToList()
            : [];

        var suggestions = candidates
            .Take(5)
            .Select(c => c.ContainingType != null
                ? $"    - {c.ContainingType}.{c.SymbolName} ({c.Signature}) - {c.FilePath}:{c.Line}"
                : $"    - {c.SymbolName} ({c.Signature}) - {c.FilePath}:{c.Line}");

        if (usingContext != null && missingUsingCandidates.Count > 0)
        {
            var missingNamespaces = missingUsingCandidates
                .Select(c => c.ContainingNamespace)
                .Where(ns => ns != null)
                .Distinct()
                .Select(ns => $"using {ns};");

            return $"  '{name}' is not accessible because its namespace is not imported here; one of the " +
                "following `using` directives is required:\n"
                + string.Join("\n", missingNamespaces.Select(u => "    " + u))
                + "\n  Candidates found:\n"
                + string.Join("\n", suggestions);
        }

        return $"  '{name}' is not accessible unqualified from this file; the fully qualified name shown " +
            "below (ContainingType.MemberName) resolves it, and a `using` directive does not. Candidates found:\n"
            + string.Join("\n", suggestions);
    }

    private static async Task<string> DescribeMissingMemberAsync(
        DiagnosticInfo diagnostic,
        SymbolNavigationEngine symbolNavigationEngine,
        CancellationToken cancellationToken,
        IReadOnlyCollection<string>? editedFilePaths)
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
        // no generic arguments) exactly -> the diagnostic's type name may be fully-qualified or
        // generic-decorated (e.g. "System.Collections.Generic.List<int>"), so reduce to a simple
        // name before using it as a scope filter. If reduction still doesn't match, the solution-wide
        // fallback below still finds the member by name alone.
        var simpleTypeName = typeName.Split('.').Last().Split('<').First();

        List<SymbolLocation> candidates;
        try
        {
            // Scope to the named type first -> a typo'd member on the right type is the common case.
            candidates = await symbolNavigationEngine.LocateSymbolAsync(
                memberName, exactMatch: true, containingType: simpleTypeName, cancellationToken: cancellationToken);

            if (candidates.Count == 0)
            {
                // Not on that type at all -> search the whole solution in case the member exists on a
                // different, similarly-named type (or the model has the wrong receiver entirely).
                candidates = await symbolNavigationEngine.LocateSymbolAsync(
                    memberName, exactMatch: true, cancellationToken: cancellationToken);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            candidates = [];
        }

        if (candidates.Count == 0)
        {
            // TODO FindExtensionMethods is named unconditionally; name it only when active (gated-tool rule)
            var noCandidate = isExtensionCandidate
                ? $"  No member or extension method named '{memberName}' was found anywhere in the solution for type '{typeName}'. This is likely a typo, a member that needs to be added to '{typeName}', or an extension method whose defining namespace needs a `using` - check FindExtensionMethods if one is expected to exist."
                : $"  No member named '{memberName}' was found anywhere in the solution. This is likely a typo, or '{memberName}' needs to be added to '{typeName}'.";
            return noCandidate + (IsEdited(diagnostic.FilePath.ToString()) ? " If this edit renames a use of an existing member, change the member's declaration in the same call." : string.Empty);
        }

        var suggestions = candidates
            .Take(5)
            .Select(c => c.ContainingType != null
                ? $"    - {c.ContainingType}.{c.SymbolName} ({c.Signature}) - {c.FilePath}:{c.Line}"
                : $"    - {c.SymbolName} ({c.Signature}) - {c.FilePath}:{c.Line}");

        var onNamedType = candidates.Any(c => string.Equals(c.ContainingType, simpleTypeName, StringComparison.OrdinalIgnoreCase));
        var editedOnNamedType = candidates.FirstOrDefault(c =>
            string.Equals(c.ContainingType, simpleTypeName, StringComparison.OrdinalIgnoreCase) && IsEdited(c.FilePath?.ToString()));
        if (editedOnNamedType is not null)
        {
            return $"  '{memberName}' is declared in {Path.GetFileName(editedOnNamedType.FilePath?.ToString())}, which this edit changes. If the edit removes or renames '{memberName}', this file still uses the old name: change the declaration and every use in one call.";
        }

        bool IsEdited(string? path) => IsEditedPath(editedFilePaths, path);

        var explanation = onNamedType
            ? $"  '{memberName}' exists on '{typeName}' but isn't accessible the way it was called (wrong overload, wrong accessibility, or a static/instance mismatch)."
            : $"  '{memberName}' was not found on '{typeName}', but a member with that name exists elsewhere - likely the wrong receiver type, or '{memberName}' needs to be added to '{typeName}' instead. Candidates found:";

        return explanation + "\n" + string.Join("\n", suggestions);
    }

    // Duplicates the path rule of ValidateAndApplyHelper.DescribeUntouchedFileErrors, because Engines cannot reuse its local functions.
    // TODO share path matching with ValidateAndApplyHelper.DescribeUntouchedFileErrors
    private static bool IsEditedPath(IReadOnlyCollection<string>? editedFilePaths, string? path)
    {
        if (editedFilePaths is null || string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var target = NormalizePath(path);
        return editedFilePaths.Any(e => SamePath(NormalizePath(e), target));
    }

    private static string NormalizePath(string p) => p.Trim().Replace('/', '\\');

    private static bool SamePath(string edited, string target)
    {
        if (string.Equals(edited, target, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        bool editedRooted = Path.IsPathRooted(edited);
        bool targetRooted = Path.IsPathRooted(target);
        if (editedRooted != targetRooted)
        {
            var (absolute, relative) = editedRooted ? (edited, target) : (target, edited);
            return absolute.EndsWith("\\" + relative, StringComparison.OrdinalIgnoreCase);
        }

        if (!editedRooted)
        {
            return false;
        }

        try
        {
            return string.Equals(Path.GetFullPath(edited), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return false;
        }
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
        catch (Exception ex) when (ex is not OperationCanceledException)
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
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Fall through without a caller type name below -> the accessibility guidance still helps.
        }

        var accessibility = symbol.Accessibility.ToLowerInvariant();
        var calledFrom = callerType != null ? $" from {callerType}" : string.Empty;
        return $"  '{qualifiedName}' is currently {accessibility}. It must be changed to a level accessible{calledFrom} ({diagnostic.FilePath}:{diagnostic.StartLine}).\n"
            + $"  Note: accessibility is set per-member, not inherited from the containing type's accessibility - raising {simpleTypeName}'s own accessibility does not change {symbol.SymbolName}'s.\n" + $"  Fix: change {symbol.SymbolName}'s accessibility first with the ChangeAccessibility tool (in a lean tool list, the Declaration tool), then re-apply this edit; do not hand-edit the modifier with ApplyDiff."; // hint-ok: static advice text, tool name without arguments
    }

    /// <summary>
    /// CS0101 ("namespace already contains a definition for X") and CS0102 ("type already contains a definition for X"): the
    /// same name declared twice. The compiler reports only one of the two declarations (in the eval run 20261110-004337-782 it
    /// was the untouched IOrderPricingCalculator.cs, while the duplicate came from the whole-file edit of another file), so the
    /// hint names both files: the diagnostic's own file, and the other one, taken from the files this edit changes when the
    /// diagnostic is in an unedited file, else from the committed declarations of the name. No tool is named.
    /// </summary>
    private static async Task<string> DescribeDuplicateDeclarationAsync(
        DiagnosticInfo diagnostic,
        SymbolNavigationEngine symbolNavigationEngine,
        CancellationToken cancellationToken,
        IReadOnlyCollection<string>? editedFilePaths,
        Func<string, bool>? isToolActive)
    {
        string? containerName = null;
        string? name = null;
        var cs0102 = Cs0102DuplicateTypeMemberRegex.Match(diagnostic.Message);
        if (cs0102.Success)
        {
            containerName = cs0102.Groups[1].Value.Split('.').Last();
            name = cs0102.Groups[2].Value;
        }
        else if (Cs0101DuplicateNamespaceMemberRegex.Match(diagnostic.Message) is { Success: true } cs0101)
        {
            name = cs0101.Groups[2].Value;
        }

        if (name == null)
        {
            return "  This is a duplicate-definition error, but the duplicated name could not be extracted from the diagnostic text.";
        }

        var diagnosticFile = diagnostic.FilePath.ToString();
        var diagnosticEdited = IsEditedPath(editedFilePaths, diagnosticFile);
        var shortForm = containerName is null
            ? $"  '{name}' is declared more than once in this namespace; keep one declaration."
            : $"  '{name}' is declared more than once in '{containerName}'; keep one declaration.";

        // The other declaration: an edited file when the diagnostic points into an unedited one (the clash is new, so this edit
        // introduced it); otherwise the committed declarations of the name outside the diagnostic's file.
        List<string> others;
        string otherNote;
        if (!diagnosticEdited && editedFilePaths is { Count: > 0 })
        {
            others = editedFilePaths.Where(e => !SamePath(NormalizePath(e), NormalizePath(diagnosticFile))).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            otherNote = " (changed by this edit)";
        }
        else
        {
            List<SymbolLocation> candidates;
            try
            {
                candidates = await symbolNavigationEngine.LocateSymbolAsync(name, exactMatch: true, containingType: containerName, cancellationToken: cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                candidates = [];
            }

            others = candidates.Select(c => c.FilePath?.ToString())
                .OfType<string>()
                .Where(p => !string.IsNullOrEmpty(p) && !SamePath(NormalizePath(p), NormalizePath(diagnosticFile)))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            otherNote = string.Empty;
        }

        if (others.Count == 0 || string.IsNullOrWhiteSpace(diagnosticFile))
        {
            return shortForm;
        }

        // File names; the full path when the names collide.
        string Show(string path) => Path.GetFileName(NormalizePath(path)) is { Length: > 0 } fileName && !others.Concat([diagnosticFile]).Any(o => o != path && Path.GetFileName(NormalizePath(o)) == fileName) ? fileName : path;
        var diagnosticShown = Show(diagnosticFile);
        var othersShown = string.Join(" or ", others.Select(Show));
        var what = containerName is null ? $"'{name}'" : $"'{name}' in '{containerName}'";
        if (!diagnosticEdited && otherNote.Length > 0)
        {
            // The edit's file first, then the unedited file the compiler pointed at.
            return $"  {what} is now declared twice: in {othersShown}{otherNote} and in {diagnosticShown}. Keep one declaration: {diagnosticShown} already declares it, so remove the '{name}' declaration from your edit.{(isToolActive?.Invoke("DeleteFile") == true ? $" Or, if {diagnosticShown} is the superseded copy, delete that file with {ToolCallHint.Call("DeleteFile", ("filePath", diagnosticFile))}." : string.Empty)}";
        }

        var editedClause = diagnosticEdited ? " (changed by this edit)" : string.Empty;
        return $"  {what} is now declared twice: in {diagnosticShown}{editedClause} and in {othersShown}. Keep one declaration"
            + (diagnosticEdited ? $": {othersShown} already declares it, so remove the '{name}' declaration from your edit." + (isToolActive?.Invoke("DeleteFile") == true ? $" Or, if {Show(others[0])} is the superseded copy, delete that file with {ToolCallHint.Call("DeleteFile", ("filePath", others[0]))}." : string.Empty) : ".");
    }

    /// <summary>
    /// CS0101 ("namespace already contains a definition for X") and CS0111 ("type already defines a
    /// member called X with the same parameter types") both fire when the file being validated is a
    /// wrong-but-plausible path for a file that already exists elsewhere in the same project -> see
    /// docs/current/project_readfile_createfile_path_inconsistency_bug.md. Neither diagnostic's raw
    /// message says where the *other* declaration lives, which leaves a model with no way to tell
    /// "I introduced a genuine duplicate" apart from "I guessed the wrong path for an existing file."
    /// This looks up the colliding symbol by name and, if found at a location other than the
    /// diagnostic's own (wrong) path, names that real file directly.
    /// </summary>
    private static async Task<string> DescribeDuplicateDefinitionAsync(
        DiagnosticInfo diagnostic,
        SymbolNavigationEngine symbolNavigationEngine,
        CancellationToken cancellationToken)
    {
        string? typeName = null;
        string? memberName = null;

        var cs0111Match = Cs0111DuplicateMemberRegex.Match(diagnostic.Message);
        if (cs0111Match.Success)
        {
            typeName = cs0111Match.Groups[1].Value.Split('.').Last();
            memberName = cs0111Match.Groups[2].Value;
        }
        else
        {
            var cs0101Match = Cs0101DuplicateNamespaceMemberRegex.Match(diagnostic.Message);
            if (cs0101Match.Success)
            {
                memberName = cs0101Match.Groups[2].Value;
            }
        }

        if (memberName == null)
        {
            return "  This is a duplicate-definition error, but the colliding name could not be extracted from the diagnostic text to search for it.";
        }

        List<SymbolLocation> candidates;
        try
        {
            candidates = await symbolNavigationEngine.LocateSymbolAsync(
                memberName, exactMatch: true, containingType: typeName, cancellationToken: cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            candidates = [];
        }

        var realLocation = candidates.FirstOrDefault(c =>
            !string.IsNullOrEmpty(c.FilePath) &&
            !string.Equals(c.FilePath, diagnostic.FilePath.ToString(), StringComparison.OrdinalIgnoreCase));

        if (realLocation == null)
        {
            // Same-file duplicate: an edit re-declared a member the file already has. Name the existing line, preferring a
            // candidate other than the reported one; without this the hint said "could not be found" and the model re-sent
            // the same edit four times (batch G R4, run 20261111-074907-352, T12-T24).
            var diagnosticFile = diagnostic.FilePath.ToString();
            var sameFile = candidates
                .Where(c => !string.IsNullOrEmpty(c.FilePath) && SamePath(NormalizePath(c.FilePath), NormalizePath(diagnosticFile)))
                .OrderBy(c => c.Line == diagnostic.StartLine ? 1 : 0)
                .FirstOrDefault();
            if (sameFile != null)
            {
                // Worded for both an edit that re-declares the member and a rename onto an existing name (RenameSymbolTests).
                var owner = typeName is null ? "the file" : $"'{typeName}'";
                return $"  '{memberName}' collides with an existing declaration: {owner} already declares it with these parameter types at line {sameFile.Line} of this file, so this change would declare it twice. " +
                    "If the member you wanted is already there, keep it and drop the new one; otherwise use a different name or different parameter types.";
            }

            return $"  '{memberName}' collides with an existing declaration, but its real location could not be found - this may be a genuine duplicate rather than a wrong-path new file.";
        }

        return $"  A declaration of '{memberName}' already exists at {realLocation.FilePath}:{realLocation.Line}. " +
            $"If '{diagnostic.FilePath}' was meant to be that same file, this path is likely wrong - " +
            $"did you mean to ReadFile/ApplyDiff {realLocation.FilePath} instead of creating a new file here? " +
            "If this really is meant to be a new, separate declaration, rename it to avoid the collision.";
    }
}
