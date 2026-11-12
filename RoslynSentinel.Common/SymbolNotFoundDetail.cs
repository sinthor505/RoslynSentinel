using System.Text;

using Microsoft.CodeAnalysis;

namespace RoslynSentinel.Common;

/// <summary>
/// One declared symbol offered as a "did you mean" candidate in a symbol-not-found error. <paramref name="Kind"/> uses
/// Search's symbolKind vocabulary (type, method, property, field, event); <paramref name="Container"/> is the containing
/// type's simple name, or the namespace for a top-level type; <paramref name="Distance"/> is the edit distance between
/// the wanted name and <paramref name="Name"/> (see <see cref="NameCandidates.Compare"/>).
/// </summary>
public sealed record SymbolCandidate(string Name, string Kind, string? Container, string? FilePath, int? Line, string? DocCommentId, int Distance);

/// <summary>
/// The shared text builder for "symbol not found" errors. <see cref="BuildAsync"/> is the one entry point: it looks for
/// similarly named declared symbols in the projects the caller already filtered (this layer cannot see
/// SymbolLocation or TestProjectDetector), lists them with a prefilled next call, or, when none is similar, names
/// exactly one orientation call and says the symbol does not exist. Syntax and symbols only; no tool is run here.
/// </summary>
public static class SymbolNotFoundDetail
{
    private const string DoesNotExistSentence = "the symbol does not exist in this solution.";

    /// <summary>
    /// Collects declared named types, ordinary methods, properties (not indexers), fields (including constants and enum
    /// members) and events of the source symbols of <paramref name="searchedProjects"/> whose name is similar to
    /// <paramref name="wantedName"/> (<see cref="NameCandidates.Rank{T}"/>), same kind as <paramref name="symbolKind"/> first
    /// (when it is not "any"), then nearest, then in declaration walk order. Implicitly declared symbols and accessors are
    /// skipped; the same name, container and kind (overloads, partials) is listed once. Returns at most
    /// <paramref name="cap"/> candidates; empty for a wanted name shorter than 3 characters.
    /// </summary>
    public static async Task<IReadOnlyList<SymbolCandidate>> FindCandidatesAsync(
        IEnumerable<Project> searchedProjects, string wantedName, string symbolKind = "any", int cap = 3, CancellationToken cancellationToken = default)
    {
        var declared = new List<SymbolCandidate>();
        foreach (var project in searchedProjects)
        {
            var compilation = await project.GetCompilationAsync(cancellationToken).ConfigureAwait(false);
            if (compilation is null)
            {
                continue;
            }

            CollectNamespace(compilation.Assembly.GlobalNamespace, declared, cancellationToken);
        }

        // cap: int.MaxValue so the same-kind ordering below happens before the cap.
        var ranked = NameCandidates.Rank(wantedName, declared, c => c.Name, int.MaxValue);
        var wantKind = !string.IsNullOrWhiteSpace(symbolKind) && !string.Equals(symbolKind, "any", StringComparison.OrdinalIgnoreCase);
        return ranked
            .OrderBy(r => wantKind && !string.Equals(r.Item.Kind, symbolKind, StringComparison.OrdinalIgnoreCase) ? 1 : 0)
            .ThenBy(r => r.Distance)
            .Select(r => r.Item with { Distance = r.Distance })
            .DistinctBy(c => (c.Name, c.Container, c.Kind))
            .Take(Math.Max(cap, 0))
            .ToList();
    }

    /// <summary>
    /// Returns the declaring file of the type named by the last dotted segment of <paramref name="containerName"/>
    /// (case-sensitive, generic arity ignored) when exactly one type of that name is declared in
    /// <paramref name="searchedProjects"/>; null when none or several are. A partial type reports its first file.
    /// </summary>
    public static async Task<string?> FindContainerFileAsync(
        IEnumerable<Project> searchedProjects, string containerName, CancellationToken cancellationToken = default)
    {
        var wanted = LastSegmentWithoutArity(containerName);
        if (wanted.Length == 0)
        {
            return null;
        }

        var matches = new List<string?>();
        foreach (var project in searchedProjects)
        {
            var compilation = await project.GetCompilationAsync(cancellationToken).ConfigureAwait(false);
            if (compilation is null)
            {
                continue;
            }

            CollectTypesNamed(compilation.Assembly.GlobalNamespace, wanted, matches, cancellationToken);
        }

        return matches.Count == 1 ? matches[0] : null;
    }

    /// <summary>
    /// Renders the "did you mean" text for <paramref name="candidates"/>: one line per candidate (name, kind, container,
    /// file) with a next call, then the retry sentence. <paramref name="isCallable"/> says whether a tool can be called on
    /// this server (null = every tool can). With <paramref name="nextCall"/> null the next call is a prefilled
    /// Search(mode: symbol) when Search is callable; a supplied <paramref name="nextCall"/> is invoked at render time only,
    /// and an empty result leaves the line without a call. Hints are built only here, never speculatively, because
    /// <see cref="ToolCallHint.Call(string, (string Name, string Value)[])"/> records each one as issued.
    /// <paramref name="retryCall"/> is for a tool whose input is a docCommentId: when it is supplied, each candidate that has an
    /// id also states that id as a fact, and the call is <c>retryCall(candidate id)</c> (the calling tool's own call with only the
    /// id corrected) in place of the default Search call; a null result falls back to the default. A supplied
    /// <paramref name="nextCall"/> wins over it. It is the last parameter so existing positional callers compile unchanged.
    /// </summary>
    public static string RenderCandidates(
        string wantedName, string symbolKind, string scopeDescription, IReadOnlyList<SymbolCandidate> candidates,
        Func<string, bool>? isCallable = null, Func<SymbolCandidate, string>? nextCall = null, Func<string, string?>? retryCall = null)
    {
        var text = new StringBuilder();
        text.Append($"Symbol '{wantedName}' ({symbolKind}) was not found in {scopeDescription}. Did you mean one of these?");
        foreach (var candidate in candidates)
        {
            text.Append($"\n  - {candidate.Name} ({candidate.Kind})");
            if (!string.IsNullOrEmpty(candidate.Container))
            {
                text.Append($" in {candidate.Container}");
            }

            if (!string.IsNullOrEmpty(candidate.FilePath))
            {
                text.Append($", {candidate.FilePath}");
            }

            var hasId = retryCall is not null && !string.IsNullOrEmpty(candidate.DocCommentId);
            if (hasId)
            {
                text.Append($", docCommentId '{candidate.DocCommentId}'");
            }

            var call = nextCall is not null
                ? nextCall(candidate)
                : (hasId ? retryCall!(candidate.DocCommentId!) : null) ?? (Callable(isCallable, "Search") ? DefaultSearchCall(candidate) : string.Empty);
            if (!string.IsNullOrEmpty(call))
            {
                text.Append($" -> {call}");
            }
        }

        text.Append("\nRetry with the exact symbol you meant. If none of these is the symbol you meant, it does not exist in this solution.");
        return text.ToString();
    }

    /// <summary>
    /// Renders the text for a symbol with no similar name: it says so and names at most one orientation call. When
    /// <paramref name="orientationFilePath"/> is not null and GetFileOutline is callable the call is GetFileOutline of that
    /// file; otherwise, when ListAll is callable, ListAll with the kind mapped from <paramref name="symbolKind"/>
    /// (method, property and field map to themselves; type, event and any map to "all", ListAll's nearest values) and
    /// <paramref name="projectName"/> when not null; with neither tool callable there is no call. With a call, the text ends
    /// with the fact that a symbol missing from what that call shows does not exist in this solution.
    /// </summary>
    public static string RenderNoCandidates(
        string wantedName, string symbolKind, string scopeDescription, string? orientationFilePath, string? projectName,
        Func<string, bool>? isCallable = null)
    {
        var text = $"Symbol '{wantedName}' ({symbolKind}) was not found in {scopeDescription}. No symbol in the solution has a similar name. ";
        if (orientationFilePath is not null && Callable(isCallable, "GetFileOutline"))
        {
            return text + $"To see what exists in the file you named, call {ToolCallHint.Call("GetFileOutline", ("filePath", orientationFilePath))}. If it is not there, {DoesNotExistSentence}";
        }

        if (Callable(isCallable, "ListAll"))
        {
            var pairs = new List<(string Name, string Value)> { ("kind", ListAllKindFor(symbolKind)) };
            if (projectName is not null)
            {
                pairs.Add(("projectName", projectName));
            }

            return text + $"To see what exists, call {ToolCallHint.Call("ListAll", pairs.ToArray())}. If it is not in that list, {DoesNotExistSentence}";
        }

        // No orientation call is callable: the no-similar-name fact above is all that is true for every caller.
        return text.TrimEnd();
    }

    /// <summary>
    /// The one entry point for a symbol-not-found error. Splits <paramref name="query"/> into a container (text before the
    /// last '.', ignoring anything from the first '(') and a name, looks for similar symbols by name, and renders either
    /// the candidate list or the no-candidate text. For no candidates a container that resolves to exactly one declared
    /// type gives GetFileOutline of that type's file; otherwise ListAll. <paramref name="scopeDescription"/> states what
    /// was searched (for example "3 project(s), test projects excluded"); <paramref name="projectName"/> is passed to ListAll.
    /// </summary>
    public static Task<string> BuildAsync(
        IReadOnlyCollection<Project> searchedProjects, string query, string symbolKind, string scopeDescription, string? projectName,
        Func<string, bool>? isCallable = null, Func<SymbolCandidate, string>? nextCall = null, CancellationToken cancellationToken = default,
        Func<string, string?>? retryCall = null)
    {
        // TODO optional per-error-code operator policy suffix (idea_error_policy_pointer)
        return BuildAsyncCore(searchedProjects, query, symbolKind, scopeDescription, projectName, isCallable, nextCall, cancellationToken, retryCall);
    }

    private static async Task<string> BuildAsyncCore(
        IReadOnlyCollection<Project> searchedProjects, string query, string symbolKind, string scopeDescription, string? projectName,
        Func<string, bool>? isCallable = null, Func<SymbolCandidate, string>? nextCall = null, CancellationToken cancellationToken = default,
        Func<string, string?>? retryCall = null)
    {
        // TODO split ignores generic arguments: Ns.Box<int>.Foo splits at a dot inside <...>; scan for the last '.' outside angle brackets
        var (container, name) = SplitQuery(query);
        var candidates = await FindCandidatesAsync(searchedProjects, name, symbolKind, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (candidates.Count > 0)
        {
            return RenderCandidates(name, symbolKind, scopeDescription, candidates, isCallable, nextCall, retryCall);
        }

        var containerFile = container is null
            ? null
            : await FindContainerFileAsync(searchedProjects, container, cancellationToken).ConfigureAwait(false);
        return RenderNoCandidates(name, symbolKind, scopeDescription, containerFile, projectName, isCallable);
    }

    private static bool Callable(Func<string, bool>? isCallable, string toolName) => isCallable is null || isCallable(toolName);

    private static string DefaultSearchCall(SymbolCandidate candidate)
    {
        var pairs = new List<(string Name, string Value)> { ("mode", "symbol"), ("query", candidate.Name) };
        if (!string.IsNullOrEmpty(candidate.FilePath))
        {
            pairs.Add(("filePath", candidate.FilePath));
        }

        return ToolCallHint.Call("Search", pairs.ToArray());
    }

    // ListAll's kind values: all, namespace, class, interface, method, property, struct, record, enum, enum member, constructor,
    // field. It has no "type" or "event" kind, so those and "any" map to "all".
    private static string ListAllKindFor(string symbolKind) => symbolKind?.ToLowerInvariant() switch
    {
        "method" => "method",
        "property" => "property",
        "field" => "field",
        _ => "all",
    };

    private static (string? Container, string Name) SplitQuery(string query)
    {
        var text = query ?? string.Empty;
        var paren = text.IndexOf('(');
        if (paren >= 0)
        {
            text = text[..paren];
        }

        text = text.Trim();
        var dot = text.LastIndexOf('.');
        if (dot < 0)
        {
            return (null, text);
        }

        var container = text[..dot].Trim();
        return (container.Length == 0 ? null : container, text[(dot + 1)..].Trim());
    }

    // The last dotted segment of a type name without generic arguments ("Ns.Box<T>" -> "Box", "Box`1" -> "Box").
    private static string LastSegmentWithoutArity(string containerName)
    {
        var text = containerName ?? string.Empty;
        var angle = text.IndexOf('<');
        if (angle >= 0)
        {
            text = text[..angle];
        }

        var tick = text.IndexOf('`');
        if (tick >= 0)
        {
            text = text[..tick];
        }

        var dot = text.LastIndexOf('.');
        return (dot >= 0 ? text[(dot + 1)..] : text).Trim();
    }

    private static void CollectTypesNamed(INamespaceSymbol ns, string wanted, List<string?> matches, CancellationToken cancellationToken)
    {
        foreach (var member in ns.GetMembers())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (member is INamespaceSymbol child)
            {
                CollectTypesNamed(child, wanted, matches, cancellationToken);
            }
            else if (member is INamedTypeSymbol type)
            {
                CollectTypesNamed(type, wanted, matches);
            }
        }
    }

    private static void CollectTypesNamed(INamedTypeSymbol type, string wanted, List<string?> matches)
    {
        if (type.IsImplicitlyDeclared || FirstSourceLocation(type) is not { } location)
        {
            return;
        }

        if (string.Equals(type.Name, wanted, StringComparison.Ordinal))
        {
            matches.Add(location.SourceTree?.FilePath);
        }

        foreach (var nested in type.GetTypeMembers())
        {
            CollectTypesNamed(nested, wanted, matches);
        }
    }

    private static void CollectNamespace(INamespaceSymbol ns, List<SymbolCandidate> sink, CancellationToken cancellationToken)
    {
        foreach (var member in ns.GetMembers())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (member is INamespaceSymbol child)
            {
                CollectNamespace(child, sink, cancellationToken);
            }
            else if (member is INamedTypeSymbol type)
            {
                CollectType(type, sink);
            }
        }
    }

    private static void CollectType(INamedTypeSymbol type, List<SymbolCandidate> sink)
    {
        if (!TryDescribe(type, "type", sink))
        {
            return;
        }

        foreach (var member in type.GetMembers())
        {
            switch (member)
            {
                case INamedTypeSymbol nested:
                    CollectType(nested, sink);
                    break;
                case IMethodSymbol { MethodKind: MethodKind.Ordinary } method:
                    TryDescribe(method, "method", sink);
                    break;
                case IPropertySymbol { IsIndexer: false } property:
                    TryDescribe(property, "property", sink);
                    break;
                case IFieldSymbol field:
                    TryDescribe(field, "field", sink);
                    break;
                case IEventSymbol evt:
                    TryDescribe(evt, "event", sink);
                    break;
            }
        }
    }

    // Adds the symbol when it is declared in source and not compiler-generated; false when it was skipped.
    private static bool TryDescribe(ISymbol symbol, string kind, List<SymbolCandidate> sink)
    {
        if (symbol.IsImplicitlyDeclared || symbol.Name.Length == 0 || symbol.Name[0] == '<' || FirstSourceLocation(symbol) is not { } location)
        {
            return false;
        }

        var container = symbol.ContainingType is { } containingType
            ? containingType.Name
            : symbol.ContainingNamespace is { IsGlobalNamespace: false } ns ? ns.ToDisplayString() : null;
        var line = location.SourceTree is null ? (int?)null : location.GetLineSpan().StartLinePosition.Line + 1;
        sink.Add(new SymbolCandidate(symbol.Name, kind, container, location.SourceTree?.FilePath, line, symbol.GetDocumentationCommentId(), 0));
        return true;
    }

    private static Location? FirstSourceLocation(ISymbol symbol) => symbol.Locations.FirstOrDefault(l => l.IsInSource);
}
