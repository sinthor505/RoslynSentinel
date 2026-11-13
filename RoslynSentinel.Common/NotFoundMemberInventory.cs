using Microsoft.CodeAnalysis;

namespace RoslynSentinel.Common;

// TODO plan_error_policy_pointer arm I: temporary A/B switch; resolve at plan close
/// <summary>
/// Arm I of plan_error_policy_pointer (Step 11g): the member inventory appended to a no-candidates symbol-not-found text by
/// <see cref="SymbolNotFoundDetail.BuildAsync"/>. <see cref="BuildAsync"/> returns null (nothing is appended) unless
/// <see cref="NotFoundInventoryOptions.Enabled"/> is on, the call's workspace is current and a container is known. Member names only: ordinary methods,
/// properties (not indexers), fields (including constants and enum members), events and nested types; implicit members, accessors, constructors
/// and operators are skipped; overloads share one name; names are distinct and sorted ordinal-ignoring-case; at most <see cref="MaxNames"/> are listed
/// and a longer list says "first 30 of N". Syntax-level facts from the compilation only; no tool is run here.
/// </summary>
internal static class NotFoundMemberInventory
{
    /// <summary>The most member names one inventory lists.</summary>
    internal const int MaxNames = 30;

    /// <summary>
    /// The inventory of the container the symbol was looked up in, or null. Container precedence: an explicit <paramref name="containingType"/>, then a
    /// pinned <paramref name="containerFilePath"/> (absolute), then <paramref name="queryContainer"/> (the part before the last dot of a qualified query).
    /// An explicit containingType that resolves to no single type is stated as a fact; an unresolvable qualified-query container says nothing (it may be a
    /// namespace); a pinned file with no types in the searched projects is stated as a fact.
    /// </summary>
    internal static async Task<string?> BuildAsync(
        IReadOnlyCollection<Project> searchedProjects, string? queryContainer, string? containingType, string? containerFilePath, CancellationToken cancellationToken)
    {
        var context = ToolCallContext.Current;
        if (!NotFoundInventoryOptions.Enabled || context?.WorkspaceCurrent != true)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(containingType))
        {
            var (type, count) = await FindContainerTypeAsync(searchedProjects, containingType, cancellationToken).ConfigureAwait(false);
            if (type is not null)
            {
                return TypeInventoryText(type);
            }

            return count == 0
                ? $"No type named `{containingType}` is declared in the searched projects."
                : $"{count} types named `{containingType}` are declared in the searched projects, so no single member list applies.";
        }

        if (!string.IsNullOrWhiteSpace(containerFilePath))
        {
            return await FileInventoryTextAsync(searchedProjects, containerFilePath, context.SolutionRoot, cancellationToken).ConfigureAwait(false);
        }

        if (!string.IsNullOrWhiteSpace(queryContainer))
        {
            var (type, _) = await FindContainerTypeAsync(searchedProjects, queryContainer, cancellationToken).ConfigureAwait(false);
            return type is null ? null : TypeInventoryText(type);
        }

        return null;
    }

    /// <summary>
    /// Like <see cref="SymbolNotFoundDetail.FindContainerFileAsync"/>, but returns the type: the declared type named by the last dotted segment of
    /// <paramref name="containerName"/> (case-sensitive, generic arity ignored) with the number of distinct declarations of that name. <c>Type</c> is set only when
    /// exactly one is declared (a qualified name narrows several by display-name suffix first); the same declaration seen from several projects counts once.
    /// </summary>
    internal static async Task<(INamedTypeSymbol? Type, int Count)> FindContainerTypeAsync(
        IEnumerable<Project> searchedProjects, string containerName, CancellationToken cancellationToken = default)
    {
        var wanted = LastSegmentWithoutArity(containerName);
        if (wanted.Length == 0)
        {
            return (null, 0);
        }

        var matches = new List<INamedTypeSymbol>();
        foreach (var project in searchedProjects)
        {
            var compilation = await project.GetCompilationAsync(cancellationToken).ConfigureAwait(false);
            if (compilation is null)
            {
                continue;
            }

            CollectTypesNamed(compilation.Assembly.GlobalNamespace, wanted, matches, cancellationToken);
        }

        var distinct = matches
            .DistinctBy(t => (FirstSourceLocation(t)?.SourceTree?.FilePath, FirstSourceLocation(t)?.SourceSpan.Start))
            .ToList();
        if (distinct.Count > 1 && containerName.Contains('.'))
        {
            var qualified = StripGenericArguments(containerName).Trim();
            var narrowed = distinct.Where(t => StripGenericArguments(t.ToDisplayString()).EndsWith(qualified, StringComparison.Ordinal)).ToList();
            if (narrowed.Count > 0)
            {
                distinct = narrowed;
            }
        }

        return (distinct.Count == 1 ? distinct[0] : null, distinct.Count);
    }

    private static string TypeInventoryText(INamedTypeSymbol type)
    {
        var names = SortedDistinct(DeclaredMemberNames(type, null));
        var subject = $"Type `{type.ToDisplayString()}`";
        return names.Count == 0 ? $"{subject} declares no members." : $"{subject} declares {MemberCount(names)}: {string.Join(", ", names.Take(MaxNames))}";
    }

    private static async Task<string> FileInventoryTextAsync(
        IReadOnlyCollection<Project> searchedProjects, string filePath, string? solutionRoot, CancellationToken cancellationToken)
    {
        var types = new List<INamedTypeSymbol>();
        foreach (var project in searchedProjects)
        {
            var compilation = await project.GetCompilationAsync(cancellationToken).ConfigureAwait(false);
            if (compilation is null)
            {
                continue;
            }

            CollectTopLevelTypesInFile(compilation.Assembly.GlobalNamespace, filePath, types, cancellationToken);
        }

        var shownPath = EditEffectNames.DisplayPath(filePath, solutionRoot);
        var typeCount = types.Select(t => t.Name).Distinct(StringComparer.Ordinal).Count();
        if (typeCount == 0)
        {
            return $"File `{shownPath}` declares no types in the searched projects.";
        }

        var names = SortedDistinct(types.SelectMany(t => DeclaredMemberNames(t, filePath)));
        return names.Count == 0
            ? $"File `{shownPath}` declares {typeCount} type(s) and no members."
            : $"File `{shownPath}` declares {MemberCount(names)} across {typeCount} type(s): {string.Join(", ", names.Take(MaxNames))}";
    }

    private static List<string> SortedDistinct(IEnumerable<string> names) =>
        names.Distinct(StringComparer.Ordinal).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ThenBy(n => n, StringComparer.Ordinal).ToList();

    // "12 members", "1 member", or "45 members (first 30 of 45)" for a list the cap shortens.
    private static string MemberCount(IReadOnlyList<string> names)
    {
        var noun = names.Count == 1 ? "member" : "members";
        return names.Count > MaxNames ? $"{names.Count} {noun} (first {MaxNames} of {names.Count})" : $"{names.Count} {noun}";
    }

    // With inFilePath only members with a source location in that file count (a partial type spread over several files).
    private static IEnumerable<string> DeclaredMemberNames(INamedTypeSymbol type, string? inFilePath)
    {
        foreach (var member in type.GetMembers())
        {
            var listed = member switch
            {
                INamedTypeSymbol => true,
                IMethodSymbol { MethodKind: MethodKind.Ordinary } => true,
                IPropertySymbol { IsIndexer: false } => true,
                IFieldSymbol => true,
                IEventSymbol => true,
                _ => false,
            };
            if (!listed || member.IsImplicitlyDeclared || member.Name.Length == 0 || member.Name[0] == '<')
            {
                continue;
            }

            var inSource = member.Locations.Where(l => l.IsInSource).ToList();
            if (inSource.Count == 0 || (inFilePath is not null && !inSource.Any(l => PathComparison.Comparer.Equals(l.SourceTree?.FilePath, inFilePath))))
            {
                continue;
            }

            yield return member.Name;
        }
    }

    private static void CollectTypesNamed(INamespaceSymbol ns, string wanted, List<INamedTypeSymbol> matches, CancellationToken cancellationToken)
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

    private static void CollectTypesNamed(INamedTypeSymbol type, string wanted, List<INamedTypeSymbol> matches)
    {
        if (type.IsImplicitlyDeclared || FirstSourceLocation(type) is null)
        {
            return;
        }

        if (string.Equals(type.Name, wanted, StringComparison.Ordinal))
        {
            matches.Add(type);
        }

        foreach (var nested in type.GetTypeMembers())
        {
            CollectTypesNamed(nested, wanted, matches);
        }
    }

    private static void CollectTopLevelTypesInFile(INamespaceSymbol ns, string filePath, List<INamedTypeSymbol> sink, CancellationToken cancellationToken)
    {
        foreach (var member in ns.GetMembers())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (member is INamespaceSymbol child)
            {
                CollectTopLevelTypesInFile(child, filePath, sink, cancellationToken);
            }
            else if (member is INamedTypeSymbol type
                     && !type.IsImplicitlyDeclared
                     && type.Locations.Any(l => l.IsInSource && PathComparison.Comparer.Equals(l.SourceTree?.FilePath, filePath)))
            {
                sink.Add(type);
            }
        }
    }

    private static Location? FirstSourceLocation(ISymbol symbol) => symbol.Locations.FirstOrDefault(l => l.IsInSource);

    private static string StripGenericArguments(string name)
    {
        var angle = name.IndexOf('<');
        return angle >= 0 ? name[..angle] : name;
    }

    // The last dotted segment of a type name without generic arguments ("Ns.Box<T>" -> "Box", "Box`1" -> "Box"); same rule as SymbolNotFoundDetail.FindContainerFileAsync.
    private static string LastSegmentWithoutArity(string containerName)
    {
        var text = StripGenericArguments(containerName ?? string.Empty);
        var tick = text.IndexOf('`');
        if (tick >= 0)
        {
            text = text[..tick];
        }

        var dot = text.LastIndexOf('.');
        return (dot >= 0 ? text[(dot + 1)..] : text).Trim();
    }
}
