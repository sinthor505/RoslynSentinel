using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using RoslynSentinel.Common;

namespace RoslynSentinel.Engines.Basic;

/// <summary>
/// Plans the <c>replaceConstruction</c> operation of SemanticFindReplace: rewrites every object-creation expression
/// (<c>new X(...)</c> and target-typed <c>new(...)</c>) whose resolved constructor belongs to a type (or to one given
/// constructor) into a call to a static factory method, keeping the argument list text untouched.
/// </summary>
/// <remarks>
/// The edit replaces only the span from the start of the creation expression up to its argument list, so nested
/// matching sites (<c>new X(new X(1))</c>) never produce overlapping edits and every argument keeps its exact text.
/// Whether the factory accepts a site's arguments is decided by speculatively binding the rewritten call at the site
/// (full overload resolution: named arguments, conversions, optional and params parameters), not by comparing counts.
/// Sites that cannot be rewritten safely are reported as skipped with a reason; they never block the other sites.
/// </remarks>
public static class ConstructionReplacer
{
    private static string SymbolHint => $"Pass a type id such as 'T:Ns.Type' or a constructor id such as 'M:Ns.Type.#ctor(System.Int32)' (from {ToolCallHint.Call("Search", ("mode", "symbol"))} or {ToolCallHint.Call("Search", ("mode", "constructor"))}).";

    private static string FactoryHint => $"Pass the id of a public or internal static, non-generic method such as 'M:Ns.Factories.CreateType(System.Int32)' (from {ToolCallHint.Call("Search", ("mode", "symbol"))}).";

    /// <summary>Plans the rewrite. Sites that were rewritten have Role Construction; skipped sites have Role Unsupported and an UnsupportedReason.</summary>
    /// <returns>Sites, a per-file full new text dictionary (empty when nothing can be rewritten) and an error for a refused operation.</returns>
    public static async Task<SemanticReplaceOutcome> PlanAsync(Solution solution, string symbolId, string factoryId, string? projectFilter, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(symbolId))
        {
            return Fail(ToolErrorCode.InvalidArgument, $"symbol cannot be empty. {SymbolHint}");
        }

        if (string.IsNullOrWhiteSpace(factoryId))
        {
            return Fail(ToolErrorCode.InvalidArgument, $"factory cannot be empty for operation replaceConstruction. {FactoryHint}");
        }

        var (targetSymbol, _) = await ResolveAsync(solution, symbolId, ct);
        if (targetSymbol is null)
        {
            return Fail(ToolErrorCode.NotFound, $"symbol: no symbol found for docCommentId '{symbolId}'. {SymbolHint}");
        }

        INamedTypeSymbol targetType;
        string? targetCtorId = null;
        if (targetSymbol is INamedTypeSymbol namedType)
        {
            targetType = namedType;
        }
        else if (targetSymbol is IMethodSymbol { MethodKind: MethodKind.Constructor } ctorSymbol)
        {
            targetType = ctorSymbol.ContainingType;
            targetCtorId = ctorSymbol.OriginalDefinition.GetDocumentationCommentId();
        }
        else
        {
            return Fail(ToolErrorCode.InvalidArgument, $"symbol: '{symbolId}' resolves to a {DescribeKind(targetSymbol)}, not a type or an instance constructor. {SymbolHint}");
        }

        if (targetType.TypeKind is not (TypeKind.Class or TypeKind.Struct))
        {
            return Fail(ToolErrorCode.InvalidArgument, $"symbol: '{targetType.Name}' is a {targetType.TypeKind.ToString().ToLowerInvariant()}, which cannot be constructed with new. {SymbolHint}");
        }

        if (targetType.IsGenericType)
        {
            return Fail(ToolErrorCode.TargetIneligible, $"symbol: '{targetType.ToDisplayString()}' is a generic type; replaceConstruction supports only non-generic types.");
        }

        var targetTypeId = targetType.GetDocumentationCommentId();
        if (targetTypeId is null)
        {
            return Fail(ToolErrorCode.InvalidArgument, $"symbol: '{symbolId}' has no documentation comment id. {SymbolHint}");
        }

        var (factorySymbol, factoryCompilation) = await ResolveAsync(solution, factoryId, ct);
        if (factorySymbol is null)
        {
            return Fail(ToolErrorCode.NotFound, $"factory: no symbol found for docCommentId '{factoryId}'. {FactoryHint}");
        }

        if (factorySymbol is not IMethodSymbol { MethodKind: MethodKind.Ordinary } factory)
        {
            return Fail(ToolErrorCode.InvalidArgument, $"factory: '{factoryId}' resolves to a {DescribeKind(factorySymbol)}, not a method. {FactoryHint}");
        }

        if (!factory.IsStatic || factory.IsGenericMethod || IsGenericContainer(factory.ContainingType))
        {
            return Fail(ToolErrorCode.TargetIneligible, $"factory: '{factory.ToDisplayString()}' must be a static, non-generic method on a non-generic type. {FactoryHint}");
        }

        var typeInFactoryCompilation = DocumentationCommentId.GetFirstSymbolForDeclarationId(targetTypeId, factoryCompilation!) as INamedTypeSymbol;
        if (typeInFactoryCompilation is null)
        {
            return Fail(ToolErrorCode.TargetIneligible, $"factory: the project that declares '{factory.ToDisplayString()}' cannot see type '{targetType.ToDisplayString()}'. Pass a factory declared in a project that references it.");
        }

        if (!IsAssignable(factoryCompilation!, factory.ReturnType, typeInFactoryCompilation))
        {
            return Fail(ToolErrorCode.TargetIneligible, BuildNotAssignableMessage(factoryCompilation!, factory, typeInFactoryCompilation));
        }

        var filter = ParseProjectFilter(projectFilter);
        if (filter is not null)
        {
            var projectNames = solution.Projects.Select(p => p.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var unknown = filter.Where(n => !projectNames.Contains(n, StringComparer.OrdinalIgnoreCase)).ToList();
            if (unknown.Count > 0)
            {
                return Fail(ToolErrorCode.InvalidArgument,
                    $"projectFilter: no project named {string.Join(", ", unknown.Select(n => $"'{n}'"))}. Pass project names separated by commas, for example '{projectNames.FirstOrDefault() ?? "MyProject"}'. Available: {string.Join(", ", projectNames.Take(40))}.");
            }
        }

        var factoryTypeIds = new HashSet<string>(StringComparer.Ordinal);
        for (var container = factory.ContainingType; container is not null; container = container.ContainingType)
        {
            var id = container.OriginalDefinition.GetDocumentationCommentId();
            if (id is not null)
            {
                factoryTypeIds.Add(id);
            }
        }

        var sites = new List<SemanticReplaceSite>();
        var editsByFile = new Dictionary<string, List<(TextSpan Span, string NewText)>>(StringComparer.Ordinal);
        var textByFile = new Dictionary<string, SourceText>(StringComparer.Ordinal);
        var seen = new HashSet<(string Path, int Start)>();

        foreach (var project in solution.Projects)
        {
            ct.ThrowIfCancellationRequested();
            if (filter is not null && !filter.Contains(project.Name, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            var compilation = await project.GetCompilationAsync(ct);
            if (compilation is null || DocumentationCommentId.GetFirstSymbolForDeclarationId(targetTypeId, compilation) is null)
            {
                // The project cannot see the type, so it cannot construct it.
                continue;
            }

            var factoryHere = DocumentationCommentId.GetFirstSymbolForDeclarationId(factoryId, compilation) as IMethodSymbol;

            foreach (var document in project.Documents)
            {
                if (document.FilePath is null)
                {
                    continue;
                }

                var root = await document.GetSyntaxRootAsync(ct);
                if (root is null)
                {
                    continue;
                }

                var creations = root.DescendantNodes().OfType<BaseObjectCreationExpressionSyntax>().ToList();
                if (creations.Count == 0)
                {
                    continue;
                }

                var model = await document.GetSemanticModelAsync(ct);
                if (model is null)
                {
                    continue;
                }

                var text = await document.GetTextAsync(ct);
                foreach (var node in creations)
                {
                    if (model.GetSymbolInfo(node, ct).Symbol is not IMethodSymbol { MethodKind: MethodKind.Constructor } ctor
                        || !Matches(ctor, targetTypeId, targetCtorId))
                    {
                        continue;
                    }

                    if (!seen.Add((document.FilePath, node.SpanStart)))
                    {
                        continue;
                    }

                    var line = text.Lines.GetLineFromPosition(node.SpanStart).LineNumber + 1;
                    var before = node.ToString();
                    var skipReason = GetSkipReason(node, model, factoryHere, factory, factoryTypeIds, project.Name, out var replacementPrefix);
                    if (skipReason is not null)
                    {
                        sites.Add(new SemanticReplaceSite(document.FilePath, line, SemanticReplaceRole.Unsupported, before, before, skipReason));
                        continue;
                    }

                    var argumentStart = node.ArgumentList!.SpanStart;
                    var span = TextSpan.FromBounds(node.SpanStart, argumentStart);
                    var after = replacementPrefix + before[(argumentStart - node.SpanStart)..];
                    sites.Add(new SemanticReplaceSite(document.FilePath, line, SemanticReplaceRole.Construction, before, after, null));
                    if (!editsByFile.TryGetValue(document.FilePath, out var fileEdits))
                    {
                        fileEdits = [];
                        editsByFile[document.FilePath] = fileEdits;
                        textByFile[document.FilePath] = text;
                    }

                    fileEdits.Add((span, replacementPrefix));
                }
            }
        }

        var changes = new Dictionary<FilePathWrapper, string>();
        foreach (var (path, fileEdits) in editsByFile)
        {
            var result = textByFile[path].ToString();
            foreach (var edit in fileEdits.OrderByDescending(e => e.Span.Start))
            {
                result = result.Remove(edit.Span.Start, edit.Span.Length).Insert(edit.Span.Start, edit.NewText);
            }

            changes[path] = result;
        }

        sites = sites.OrderBy(s => s.FilePath, StringComparer.Ordinal).ThenBy(s => s.Line).ToList();
        return new SemanticReplaceOutcome(sites, changes, null);
    }

    private static string? GetSkipReason(
        BaseObjectCreationExpressionSyntax node,
        SemanticModel model,
        IMethodSymbol? factoryHere,
        IMethodSymbol factory,
        HashSet<string> factoryTypeIds,
        string projectName,
        out string replacementPrefix)
    {
        replacementPrefix = string.Empty;

        var enclosing = model.GetEnclosingSymbol(node.SpanStart);
        for (var container = enclosing as INamedTypeSymbol ?? enclosing?.ContainingType; container is not null; container = container.ContainingType)
        {
            var id = container.OriginalDefinition.GetDocumentationCommentId();
            if (id is not null && factoryTypeIds.Contains(id))
            {
                return $"site is inside the factory's containing type '{factory.ContainingType.Name}'; rewriting it would make the factory call itself";
            }
        }

        if (node.Initializer is not null)
        {
            return "site has an object or collection initializer; the factory call would lose it, so the site was left unchanged";
        }

        if (node.ArgumentList is null)
        {
            return "creation expression has no argument list to forward to the factory";
        }

        if (factoryHere is null)
        {
            return $"project '{projectName}' does not reference the assembly that declares the factory";
        }

        if (!model.IsAccessible(node.SpanStart, factoryHere))
        {
            return "factory is not accessible from this site";
        }

        var qualifier = factoryHere.ContainingType.ToMinimalDisplayString(model, node.SpanStart);
        var prefix = $"{qualifier}.{factoryHere.Name}";
        var probe = SyntaxFactory.ParseExpression(prefix + node.ArgumentList.ToString());
        var info = model.GetSpeculativeSymbolInfo(node.SpanStart, probe, SpeculativeBindingOption.BindAsExpression);
        if (info.Symbol is not IMethodSymbol bound
            || !SymbolEqualityComparer.Default.Equals(bound.OriginalDefinition, factoryHere.OriginalDefinition))
        {
            return $"argument shape does not match factory {factory.ContainingType.Name}.{factory.Name}({string.Join(", ", factory.Parameters.Select(p => p.Type.ToMinimalDisplayString(model, node.SpanStart) + " " + p.Name))})";
        }

        replacementPrefix = prefix;
        return null;
    }

    private static bool Matches(IMethodSymbol ctor, string targetTypeId, string? targetCtorId)
    {
        if (ctor.ContainingType.OriginalDefinition.GetDocumentationCommentId() != targetTypeId)
        {
            return false;
        }

        return targetCtorId is null || ctor.OriginalDefinition.GetDocumentationCommentId() == targetCtorId;
    }

    private static bool IsGenericContainer(INamedTypeSymbol type)
    {
        for (var current = type; current is not null; current = current.ContainingType)
        {
            if (current.IsGenericType)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsAssignable(Compilation compilation, ITypeSymbol from, ITypeSymbol to)
    {
        if (from.SpecialType == SpecialType.System_Void)
        {
            return false;
        }

        var conversion = compilation.ClassifyConversion(from, to);
        return conversion.Exists && conversion.IsImplicit && !conversion.IsUserDefined;
    }

    private static string BuildNotAssignableMessage(Compilation compilation, IMethodSymbol factory, INamedTypeSymbol type)
    {
        var candidates = factory.ContainingType.GetMembers()
            .OfType<IMethodSymbol>()
            .Where(m => m is { MethodKind: MethodKind.Ordinary, IsStatic: true, IsGenericMethod: false }
                && !SymbolEqualityComparer.Default.Equals(m, factory)
                && IsAssignable(compilation, m.ReturnType, type))
            .Select(m => m.GetDocumentationCommentId())
            .Where(id => id is not null)
            .Take(5)
            .ToList();
        var suggestion = candidates.Count > 0
            ? $"Methods on '{factory.ContainingType.Name}' that qualify: {string.Join(", ", candidates)}."
            : $"Add a static method on '{factory.ContainingType.Name}' that returns '{type.Name}' (or a subtype), then pass its id.";
        return $"factory: '{factory.ToDisplayString()}' returns '{factory.ReturnType.ToDisplayString()}', which is not implicitly assignable to the constructed type '{type.ToDisplayString()}', so rewritten sites could stop compiling. {suggestion}";
    }

    private static List<string>? ParseProjectFilter(string? projectFilter)
    {
        if (string.IsNullOrWhiteSpace(projectFilter))
        {
            return null;
        }

        var names = projectFilter.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        return names.Count == 0 ? null : names;
    }

    private static string DescribeKind(ISymbol symbol) => symbol switch
    {
        IMethodSymbol { MethodKind: MethodKind.Constructor } => "constructor",
        IMethodSymbol => "method",
        INamedTypeSymbol => "type",
        INamespaceSymbol => "namespace",
        _ => symbol.Kind.ToString().ToLowerInvariant(),
    };

    private static async Task<(ISymbol? Symbol, Compilation? Compilation)> ResolveAsync(Solution solution, string docCommentId, CancellationToken ct)
    {
        foreach (var project in solution.Projects)
        {
            var compilation = await project.GetCompilationAsync(ct);
            if (compilation is null)
            {
                continue;
            }

            var symbol = DocumentationCommentId.GetFirstSymbolForDeclarationId(docCommentId, compilation);
            if (symbol is not null)
            {
                return (symbol, compilation);
            }
        }

        return (null, null);
    }

    private static SemanticReplaceOutcome Fail(string code, string message) =>
        new(new List<SemanticReplaceSite>(), new Dictionary<FilePathWrapper, string>(), new ResultError(code, $"SemanticFindReplace replaceConstruction: {message}"));
}
