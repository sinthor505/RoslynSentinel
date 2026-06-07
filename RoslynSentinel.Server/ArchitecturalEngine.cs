using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RoslynSentinel.Server;

public record CircularDependencyChain(
    List<string> Cycle,
    string CycleType,
    List<string?> FilePaths
);

public class ArchitecturalEngine
{
    private readonly PersistentWorkspaceManager _workspaceManager;

    public ArchitecturalEngine(PersistentWorkspaceManager workspaceManager)
    {
        _workspaceManager = workspaceManager;
    }

    /// <summary>
    /// Converts a class into a .NET BackgroundService.
    /// </summary>
    public async Task<DocumentEditResult> ConvertToBackgroundServiceAsync(FilePath filePath, string className, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault();
        if (document == null)
        {
            throw new FileNotFoundException($"File not found: {filePath}");
        }

        var root = await document.GetSyntaxRootAsync(cancellationToken) as CompilationUnitSyntax;
        if (root == null)
        {
            throw new InvalidOperationException("Could not parse syntax root.");
        }

        var classNode = root.DescendantNodes().OfType<ClassDeclarationSyntax>().FirstOrDefault(c => c.Identifier.Text == className);
        if (classNode == null)
        {
            throw new InvalidOperationException("Class not found.");
        }

        // 1. Add using
        if (!root.Usings.Any(u => u.Name?.ToString() == "Microsoft.Extensions.Hosting"))
        {
            root = root.AddUsings(SyntaxFactory.UsingDirective(SyntaxFactory.ParseName("Microsoft.Extensions.Hosting")));
            // Re-find class node after root modification (stale reference otherwise)
            classNode = root.DescendantNodes().OfType<ClassDeclarationSyntax>().FirstOrDefault(c => c.Identifier.Text == className);
            if (classNode == null)
            {
                throw new InvalidOperationException("Class not found after root modification.");
            }
        }

        // 2. Change base class
        var baseType = SyntaxFactory.SimpleBaseType(SyntaxFactory.ParseTypeName("BackgroundService"));
        var newClass = classNode.WithBaseList(SyntaxFactory.BaseList(SyntaxFactory.SingletonSeparatedList<BaseTypeSyntax>(baseType)));

        // 3. Add ExecuteAsync override
        var executeAsync = SyntaxFactory.MethodDeclaration(SyntaxFactory.ParseTypeName("Task"), "ExecuteAsync")
            .AddModifiers(SyntaxFactory.Token(SyntaxKind.ProtectedKeyword), SyntaxFactory.Token(SyntaxKind.OverrideKeyword), SyntaxFactory.Token(SyntaxKind.AsyncKeyword))
            .AddParameterListParameters(SyntaxFactory.Parameter(SyntaxFactory.Identifier("stoppingToken")).WithType(SyntaxFactory.ParseTypeName("CancellationToken")))
            .WithBody(SyntaxFactory.Block(
                SyntaxFactory.WhileStatement(
                    SyntaxFactory.PrefixUnaryExpression(SyntaxKind.LogicalNotExpression,
                        SyntaxFactory.MemberAccessExpression(
                            SyntaxKind.SimpleMemberAccessExpression,
                            SyntaxFactory.IdentifierName("stoppingToken"),
                            SyntaxFactory.IdentifierName("IsCancellationRequested"))),
                    SyntaxFactory.Block(
                        SyntaxFactory.ExpressionStatement(SyntaxFactory.ParseExpression("await Task.Delay(1000, stoppingToken)"))
                    ))));

        newClass = newClass.AddMembers(executeAsync);

        var newRoot = root.ReplaceNode(classNode, newClass);
        return new DocumentEditResult
        {
            Outcome = EditOutcome.Modified,
            UpdatedText = newRoot.NormalizeWhitespace().ToFullString(),
            FilePath = filePath
        };
    }

    public async Task<List<CircularDependencyChain>> FindCircularDependenciesAsync(
        string? projectName = null, CancellationToken ct = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var projects = solution.Projects.AsEnumerable();
        if (!string.IsNullOrEmpty(projectName))
        {
            projects = projects.Where(p => p.Name == projectName);
        }

        // Pass 1: Collect all named types defined in the solution
        var allSymbols = new Dictionary<string, (INamedTypeSymbol Symbol, string? FilePath)>();
        foreach (var project in projects)
        {
            ct.ThrowIfCancellationRequested();
            var compilation = await project.GetCompilationAsync(ct);
            if (compilation == null)
            {
                continue;
            }

            foreach (var syntaxTree in compilation.SyntaxTrees)
            {
                var semanticModel = compilation.GetSemanticModel(syntaxTree);
                var root = await syntaxTree.GetRootAsync(ct);
                foreach (var typeDecl in root.DescendantNodes().OfType<BaseTypeDeclarationSyntax>())
                {
                    if (semanticModel.GetDeclaredSymbol(typeDecl, ct) is not INamedTypeSymbol symbol)
                    {
                        continue;
                    }

                    if (symbol.IsImplicitlyDeclared)
                    {
                        continue;
                    }

                    if (symbol.IsGenericType && !symbol.IsDefinition)
                    {
                        continue;
                    }

                    var typeKey = symbol.ToDisplayString();
                    if (!allSymbols.ContainsKey(typeKey))
                    {
                        var filePath = symbol.Locations.FirstOrDefault(l => l.IsInSource)?.SourceTree?.FilePath;
                        allSymbols[typeKey] = (symbol, filePath);
                    }
                }
            }
        }

        // Pass 2: Build dependency graph restricted to solution types
        var graph = new Dictionary<string, HashSet<string>>();
        foreach (var key in allSymbols.Keys)
        {
            graph[key] = new HashSet<string>();
        }

        foreach (var (key, (symbol, _)) in allSymbols)
        {
            ct.ThrowIfCancellationRequested();
            foreach (var dep in GetReferencedNamedTypes(symbol))
            {
                var depKey = dep.ToDisplayString();
                if (allSymbols.ContainsKey(depKey) && depKey != key)
                {
                    graph[key].Add(depKey);
                }
            }
        }

        // Pass 3: Find SCCs via Tarjan's algorithm
        var sccs = ComputeTarjanSCCs(graph);

        // Pass 4: Extract representative cycle from each SCC with size > 1
        var results = new List<CircularDependencyChain>();
        var seen = new HashSet<string>();

        foreach (var scc in sccs.Where(s => s.Count > 1))
        {
            var cycle = FindRepresentativeCycle(scc, graph);
            if (cycle == null || cycle.Count < 3)
            {
                continue;
            }

            // Canonicalize: rotate so the lexicographically smallest name is first
            var nodes = cycle.Take(cycle.Count - 1).ToList();
            var minIdx = 0;
            for (var i = 1; i < nodes.Count; i++)
            {
                if (string.Compare(nodes[i], nodes[minIdx], StringComparison.Ordinal) < 0)
                {
                    minIdx = i;
                }
            }

            var rotated = new List<string>();
            for (var i = 0; i < nodes.Count; i++)
            {
                rotated.Add(nodes[(i + minIdx) % nodes.Count]);
            }

            rotated.Add(rotated[0]);

            var canonicalKey = string.Join("->", rotated);
            if (!seen.Add(canonicalKey))
            {
                continue;
            }

            var filePaths = rotated
                .Select(t => allSymbols.TryGetValue(t, out var info) ? info.FilePath : null)
                .ToList();
            var cycleType = rotated.Count == 3 ? "Direct" : "Transitive";
            results.Add(new CircularDependencyChain(rotated, cycleType, filePaths));
        }

        return results;
    }

    private static IEnumerable<INamedTypeSymbol> GetReferencedNamedTypes(INamedTypeSymbol type)
    {
        if (type.BaseType != null && type.BaseType.SpecialType == SpecialType.None)
        {
            foreach (var t in UnwrapNamedTypes(type.BaseType))
            {
                yield return t;
            }
        }

        foreach (var iface in type.Interfaces)
        {
            foreach (var t in UnwrapNamedTypes(iface))
            {
                yield return t;
            }
        }

        foreach (var member in type.GetMembers())
        {
            switch (member)
            {
                case IFieldSymbol field when field.AssociatedSymbol == null:
                    foreach (var t in UnwrapNamedTypes(field.Type))
                    {
                        yield return t;
                    }

                    break;
                case IPropertySymbol prop:
                    foreach (var t in UnwrapNamedTypes(prop.Type))
                    {
                        yield return t;
                    }

                    break;
                case IMethodSymbol method when
                    method.MethodKind == MethodKind.Ordinary ||
                    method.MethodKind == MethodKind.Constructor:
                    foreach (var t in UnwrapNamedTypes(method.ReturnType))
                    {
                        yield return t;
                    }

                    foreach (var param in method.Parameters)
                    {
                        foreach (var t in UnwrapNamedTypes(param.Type))
                        {
                            yield return t;
                        }
                    }

                    break;
            }
        }
    }

    private static IEnumerable<INamedTypeSymbol> UnwrapNamedTypes(ITypeSymbol typeSymbol)
    {
        switch (typeSymbol)
        {
            case INamedTypeSymbol named:
                yield return named.OriginalDefinition;
                foreach (var arg in named.TypeArguments)
                {
                    foreach (var t in UnwrapNamedTypes(arg))
                    {
                        yield return t;
                    }
                }

                break;
            case IArrayTypeSymbol array:
                foreach (var t in UnwrapNamedTypes(array.ElementType))
                {
                    yield return t;
                }

                break;
        }
    }

    private static List<List<string>> ComputeTarjanSCCs(Dictionary<string, HashSet<string>> graph)
    {
        var index = 0;
        var stack = new Stack<string>();
        var onStack = new HashSet<string>();
        var indices = new Dictionary<string, int>();
        var lowLinks = new Dictionary<string, int>();
        var sccs = new List<List<string>>();

        void StrongConnect(string v)
        {
            indices[v] = lowLinks[v] = index++;
            stack.Push(v);
            onStack.Add(v);

            foreach (var w in graph[v])
            {
                if (!indices.TryGetValue(w, out int value))
                {
                    StrongConnect(w);
                    lowLinks[v] = Math.Min(lowLinks[v], lowLinks[w]);
                }
                else if (onStack.Contains(w))
                {
                    lowLinks[v] = Math.Min(lowLinks[v], value);
                }
            }

            if (lowLinks[v] == indices[v])
            {
                var scc = new List<string>();
                string w;
                do
                {
                    w = stack.Pop();
                    onStack.Remove(w);
                    scc.Add(w);
                } while (w != v);
                sccs.Add(scc);
            }
        }

        foreach (var v in graph.Keys)
        {
            if (!indices.ContainsKey(v))
            {
                StrongConnect(v);
            }
        }

        return sccs;
    }

    private static List<string>? FindRepresentativeCycle(List<string> scc, Dictionary<string, HashSet<string>> graph)
    {
        var sccSet = new HashSet<string>(scc);
        List<string>? shortest = null;

        foreach (var start in scc)
        {
            var cycle = FindCycleFromStart(start, sccSet, graph);
            if (cycle != null && (shortest == null || cycle.Count < shortest.Count))
            {
                shortest = cycle;
                if (shortest.Count == 3)
                {
                    break;
                }
            }
        }

        return shortest;
    }

    private static List<string>? FindCycleFromStart(string start, HashSet<string> sccSet, Dictionary<string, HashSet<string>> graph)
    {
        var path = new List<string> { start };
        var pathSet = new HashSet<string> { start };

        bool Dfs(string current)
        {
            if (!graph.TryGetValue(current, out var neighbors))
            {
                return false;
            }

            foreach (var next in neighbors)
            {
                if (!sccSet.Contains(next))
                {
                    continue;
                }

                if (next == start)
                {
                    path.Add(start);
                    return true;
                }
                if (pathSet.Contains(next))
                {
                    continue;
                }

                path.Add(next);
                pathSet.Add(next);
                if (Dfs(next))
                {
                    return true;
                }

                path.RemoveAt(path.Count - 1);
                pathSet.Remove(next);
            }
            return false;
        }

        return Dfs(start) ? path : null;
    }

    // ── Layer Architecture Enforcement ───────────────────────────────────────

    public record LayerViolation(
        string ViolationType,
        string Description,
        string SourceLayer,
        string ForbiddenDependency,
        FilePath FilePath,
        int Line
    );

    // Standard layered architecture rule set (namespace segment → layer rank, lower = higher-level)
    private static readonly Dictionary<string, int> LayerRank = new(StringComparer.OrdinalIgnoreCase)
    {
        { "Controllers", 0 },
        { "Endpoints",   0 },
        { "Hubs",        0 },
        { "Workers",     1 },
        { "Services",    2 },
        { "Managers",    2 },
        { "Handlers",    2 },
        { "Queries",     3 },
        { "Commands",    3 },
        { "Domain",      4 },
        { "Models",      4 },
        { "Entities",    4 },
        { "Data",        5 },
        { "Repositories",5 },
        { "Migrations",  6 },
    };

    // Rules: a layer at rank R should NOT directly reference a layer at rank > R+1 (skip-a-layer)
    // and Controllers should never reference Data/Repositories directly.
    private static readonly Dictionary<string, HashSet<string>> ForbiddenDependencies = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Controllers"] = new(StringComparer.OrdinalIgnoreCase) { "Data", "Repositories", "Migrations" },
        ["Endpoints"] = new(StringComparer.OrdinalIgnoreCase) { "Data", "Repositories", "Migrations" },
        ["Hubs"] = new(StringComparer.OrdinalIgnoreCase) { "Data", "Repositories", "Migrations" },
        ["Workers"] = new(StringComparer.OrdinalIgnoreCase) { "Repositories", "Migrations" },
        ["Services"] = new(StringComparer.OrdinalIgnoreCase) { "Migrations" },
        ["Managers"] = new(StringComparer.OrdinalIgnoreCase) { "Migrations" },
        ["Domain"] = new(StringComparer.OrdinalIgnoreCase) { "Data", "Repositories", "Migrations" },
        ["Models"] = new(StringComparer.OrdinalIgnoreCase) { "Data", "Repositories", "Migrations" },
        ["Entities"] = new(StringComparer.OrdinalIgnoreCase) { "Data", "Repositories", "Migrations" },
    };

    /// <summary>
    /// Detects namespace-level layer violations (e.g. Controllers referencing Repositories directly,
    /// domain models importing data-layer types). Operates on using directives — no compilation needed.
    /// </summary>
    public async Task<List<LayerViolation>> DetectLayerViolationsAsync(
        string? projectName = null,
        string? filePath = null,
        CancellationToken ct = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var violations = new List<LayerViolation>();

        IEnumerable<Document?> documents;
        if (!string.IsNullOrEmpty(filePath))
        {
            documents = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument);
        }
        else if (!string.IsNullOrEmpty(projectName))
        {
            var project = solution.Projects.FirstOrDefault(p =>
                string.Equals(p.Name, projectName, StringComparison.OrdinalIgnoreCase));
            documents = project?.Documents.Cast<Document?>() ?? Enumerable.Empty<Document?>();
        }
        else
        {
            documents = solution.Projects.SelectMany(p => p.Documents).Cast<Document?>();
        }

        foreach (var doc in documents)
        {
            if (doc == null)
            {
                continue;
            }

            var root = await doc.GetSyntaxRootAsync(ct);
            if (root == null)
            {
                continue;
            }

            var docPath = doc.FilePath ?? doc.Name;

            // Determine which layer this file belongs to from its own namespace
            var ownNamespace = root.DescendantNodes().OfType<BaseNamespaceDeclarationSyntax>()
                .FirstOrDefault()?.Name.ToString() ?? "";
            var ownLayer = GetLayerSegment(ownNamespace);
            if (ownLayer == null)
            {
                continue;
            }

            if (!ForbiddenDependencies.TryGetValue(ownLayer, out var forbidden))
            {
                continue;
            }

            // Scan all using directives in this file
            var usings = root.DescendantNodes().OfType<UsingDirectiveSyntax>();
            foreach (var usingDirective in usings)
            {
                var importedNs = usingDirective.Name?.ToString() ?? "";
                var importedLayer = GetLayerSegment(importedNs);
                if (importedLayer == null)
                {
                    continue;
                }

                if (!forbidden.Contains(importedLayer))
                {
                    continue;
                }

                var line = usingDirective.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                violations.Add(new LayerViolation(
                    "LayerBypass",
                    $"'{ownLayer}' layer directly references '{importedLayer}' layer ({importedNs}). " +
                    $"Route through an intermediate service/repository interface instead.",
                    ownLayer,
                    importedNs,
                    docPath,
                    line));
            }
        }

        return violations.OrderBy(v => v.FilePath).ThenBy(v => v.Line).ToList();
    }

    private static string? GetLayerSegment(string namespaceName)
    {
        if (string.IsNullOrEmpty(namespaceName))
        {
            return null;
        }

        var segments = namespaceName.Split('.');
        return segments.FirstOrDefault(s => LayerRank.ContainsKey(s));
    }
}
