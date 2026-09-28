using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynSentinel.Common;

namespace RoslynSentinel.Advanced;
public record CircularDependencyChain(List<string> Cycle, string CycleType, List<string?> FilePaths);
public class ArchitecturalEngine
{
    private readonly SentinelConfiguration _config;
    private readonly IWorkspaceManager _workspaceManager;
    public ArchitecturalEngine(IWorkspaceManager workspaceManager, SentinelConfiguration config = null)
    {
        _workspaceManager = workspaceManager;
        _config = config;
    }

    /// <summary>
    /// Converts a class into a .NET BackgroundService.
    /// </summary>
    public async Task<DocumentEditResult> ConvertToBackgroundServiceAsync(FilePathWrapper filePath, string className, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetSolutionAsync(ReadSource.Committed, cancellationToken);
        var document = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault() ?? throw new FileNotFoundException($"File not found: {filePath}");
        var root = await document.GetSyntaxRootAsync(cancellationToken) as CompilationUnitSyntax ?? throw new InvalidOperationException("Could not parse syntax root.");
        var classNode = root.DescendantNodes().OfType<ClassDeclarationSyntax>().FirstOrDefault(c => c.Identifier.Text == className) ?? throw new InvalidOperationException("Class not found.");
        // 1. Add using
        if (!root.Usings.Any(u => u.Name?.ToString() == "Microsoft.Extensions.Hosting"))
        {
            root = root.AddUsings(SyntaxFactory.UsingDirective(SyntaxFactory.ParseName("Microsoft.Extensions.Hosting")));
            // Re-find class node after root modification (stale reference otherwise)
            classNode = root.DescendantNodes().OfType<ClassDeclarationSyntax>().FirstOrDefault(c => c.Identifier.Text == className) ?? throw new InvalidOperationException("Class not found after root modification.");
        }

        // 2. Change base class
        var baseType = SyntaxFactory.SimpleBaseType(SyntaxFactory.ParseTypeName("BackgroundService"));
        var newClass = classNode.WithBaseList(SyntaxFactory.BaseList(SyntaxFactory.SingletonSeparatedList<BaseTypeSyntax>(baseType)));
        // 3. Add ExecuteAsync override
        var executeAsync = SyntaxFactory.MethodDeclaration(SyntaxFactory.ParseTypeName("Task"), "ExecuteAsync").AddModifiers(SyntaxFactory.Token(SyntaxKind.ProtectedKeyword), SyntaxFactory.Token(SyntaxKind.OverrideKeyword), SyntaxFactory.Token(SyntaxKind.AsyncKeyword)).AddParameterListParameters(SyntaxFactory.Parameter(SyntaxFactory.Identifier("stoppingToken")).WithType(SyntaxFactory.ParseTypeName("CancellationToken"))).WithBody(SyntaxFactory.Block(SyntaxFactory.WhileStatement(SyntaxFactory.PrefixUnaryExpression(SyntaxKind.LogicalNotExpression, SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, SyntaxFactory.IdentifierName("stoppingToken"), SyntaxFactory.IdentifierName("IsCancellationRequested"))), SyntaxFactory.Block(SyntaxFactory.ExpressionStatement(SyntaxFactory.ParseExpression("await Task.Delay(1000, stoppingToken)"))))));
        newClass = newClass.AddMembers(executeAsync);
        var newRoot = root.ReplaceNode(classNode, newClass);
        return new DocumentEditResult
        {
            Outcome = EditOutcome.Modified,
            UpdatedText = RoslynFormattingHelper.NormalizeWholeSubtreeWhitespace(newRoot).ToFullString(),
            FilePath = filePath
        };
    }

    public async Task<List<CircularDependencyChain>> FindCircularDependenciesAsync(string? projectName = null, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetSolutionAsync(ReadSource.Committed, cancellationToken);
        var projects = solution.Projects.AsEnumerable();
        if (!string.IsNullOrEmpty(projectName))
        {
            projects = projects.Where(p => p.Name == projectName);
        }

        // Pass 1: Collect all named types defined in the solution
        var allSymbols = new Dictionary<string, (INamedTypeSymbol Symbol, string? FilePath)>();
        foreach (var project in projects)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var compilation = await project.GetCompilationAsync(cancellationToken);
            if (compilation == null)
            {
                continue;
            }

            foreach (var syntaxTree in compilation.SyntaxTrees)
            {
                var semanticModel = compilation.GetSemanticModel(syntaxTree);
                var root = await syntaxTree.GetRootAsync(cancellationToken);
                foreach (var typeDecl in root.DescendantNodes().OfType<BaseTypeDeclarationSyntax>())
                {
                    if (semanticModel.GetDeclaredSymbol(typeDecl, cancellationToken)is not INamedTypeSymbol symbol)
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

        foreach (var(key, (symbol, _))in allSymbols)
        {
            cancellationToken.ThrowIfCancellationRequested();
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

            var filePaths = rotated.Select(t => allSymbols.TryGetValue(t, out var info) ? info.FilePath : null).ToList();
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
                case IMethodSymbol method when method.MethodKind == MethodKind.Ordinary || method.MethodKind == MethodKind.Constructor:
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
                }
                while (w != v);
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
        var path = new List<string>
        {
            start
        };
        var pathSet = new HashSet<string>
        {
            start
        };
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
    public record LayerViolation(string ViolationType, string Description, string SourceLayer, string ForbiddenDependency, FilePathWrapper FilePath, int Line);
    // Standard layered architecture rule set (namespace segment -> layer rank, lower = higher-level)
    private static readonly Dictionary<string, int> LayerRank = new(StringComparer.OrdinalIgnoreCase)
    {
        {
            "Controllers",
            0
        },
        {
            "Endpoints",
            0
        },
        {
            "Hubs",
            0
        },
        {
            "Workers",
            1
        },
        {
            "Services",
            2
        },
        {
            "Managers",
            2
        },
        {
            "Handlers",
            2
        },
        {
            "Queries",
            3
        },
        {
            "Commands",
            3
        },
        {
            "Domain",
            4
        },
        {
            "Models",
            4
        },
        {
            "Entities",
            4
        },
        {
            "Data",
            5
        },
        {
            "Repositories",
            5
        },
        {
            "Migrations",
            6
        },
    };
    // Rules: a layer at rank R should NOT directly reference a layer at rank > R+1 (skip-a-layer)
    // and Controllers should never reference Data/Repositories directly.
    private static readonly Dictionary<string, HashSet<string>> ForbiddenDependencies = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Controllers"] = new(StringComparer.OrdinalIgnoreCase)
        {
            "Data",
            "Repositories",
            "Migrations"
        },
        ["Endpoints"] = new(StringComparer.OrdinalIgnoreCase)
        {
            "Data",
            "Repositories",
            "Migrations"
        },
        ["Hubs"] = new(StringComparer.OrdinalIgnoreCase)
        {
            "Data",
            "Repositories",
            "Migrations"
        },
        ["Workers"] = new(StringComparer.OrdinalIgnoreCase)
        {
            "Repositories",
            "Migrations"
        },
        ["Services"] = new(StringComparer.OrdinalIgnoreCase)
        {
            "Migrations"
        },
        ["Managers"] = new(StringComparer.OrdinalIgnoreCase)
        {
            "Migrations"
        },
        ["Domain"] = new(StringComparer.OrdinalIgnoreCase)
        {
            "Data",
            "Repositories",
            "Migrations"
        },
        ["Models"] = new(StringComparer.OrdinalIgnoreCase)
        {
            "Data",
            "Repositories",
            "Migrations"
        },
        ["Entities"] = new(StringComparer.OrdinalIgnoreCase)
        {
            "Data",
            "Repositories",
            "Migrations"
        },
    };
    /// <summary>
    /// Detects namespace-level layer violations (e.g. Controllers referencing Repositories directly,
    /// domain models importing data-layer types). Operates on using directives -> no compilation needed.
    /// </summary>
    public async Task<List<LayerViolation>> DetectLayerViolationsAsync(string? projectName = null, string? filePath = null, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetSolutionAsync(ReadSource.Committed, cancellationToken);
        var violations = new List<LayerViolation>();
        IEnumerable<Document?> documents;
        if (!string.IsNullOrEmpty(filePath))
        {
            documents = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument);
        }
        else if (!string.IsNullOrEmpty(projectName))
        {
            var project = solution.Projects.FirstOrDefault(p => string.Equals(p.Name, projectName, StringComparison.OrdinalIgnoreCase));
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

            var root = await doc.GetSyntaxRootAsync(cancellationToken);
            if (root == null)
            {
                continue;
            }

            var docPath = doc.FilePath ?? doc.Name;
            // Determine which layer this file belongs to from its own namespace
            var ownNamespace = root.DescendantNodes().OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault()?.Name.ToString() ?? "";
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
                violations.Add(new LayerViolation("LayerBypass", $"'{ownLayer}' layer directly references '{importedLayer}' layer ({importedNs}). " + $"Route through an intermediate service/repository interface instead.", ownLayer, importedNs, docPath, line));
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

    private async Task<IEnumerable<(Document Document, SyntaxNode Root, SemanticModel? SemanticModel)>> GetTargetDocumentsAsync(Solution solution, string? projectName, string? filePath, bool includeSemantic = false, CancellationToken cancellationToken = default)
    {
        var projects = solution.Projects.AsEnumerable();
        if (!string.IsNullOrEmpty(projectName))
        {
            projects = projects.Where(p => p.Name.Equals(projectName, StringComparison.OrdinalIgnoreCase) || p.Name.Contains(projectName, StringComparison.OrdinalIgnoreCase));
        }

        var documentList = new List<(Document, SyntaxNode, SemanticModel? )>();
        foreach (var project in projects)
        {
            var docs = project.Documents.AsEnumerable();
            if (!string.IsNullOrEmpty(filePath))
            {
                docs = docs.Where(d => d.Name == filePath || d.FilePath == filePath || (d.FilePath != null && d.FilePath.EndsWith(filePath, StringComparison.OrdinalIgnoreCase)));
            }

            foreach (var doc in docs)
            {
                var root = await doc.GetSyntaxRootAsync(cancellationToken);
                if (root == null)
                {
                    continue;
                }

                var model = includeSemantic ? await doc.GetSemanticModelAsync(cancellationToken) : null;
                documentList.Add((doc, root, model));
            }
        }

        return documentList;
    }

    /// <summary>
    /// Detects circular type dependencies at the class level by analysing constructor parameters.
    /// Unlike FindCircularDependenciesAsync (which checks project references), this method finds
    /// cycles in composition: ClassA's ctor takes ClassB, ClassB's ctor takes ClassA.
    /// Such cycles cause runtime DI failures without a compile error.
    /// </summary>
    public async Task<List<string>> FindCircularTypeReferencesAsync(string? projectName = null, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetSolutionAsync(ReadSource.Committed, cancellationToken);
        var targets = await GetTargetDocumentsAsync(solution, projectName, null, true, cancellationToken);
        // Build map: simpleName -> set of constructor-parameter simple names
        var deps = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var target in targets)
        {
            if (target.SemanticModel == null)
            {
                continue;
            }

            foreach (var classDecl in target.Root.DescendantNodes().OfType<ClassDeclarationSyntax>())
            {
                var className = classDecl.Identifier.Text;
                if (!deps.TryGetValue(className, out HashSet<string>? value))
                {
                    value = new HashSet<string>(StringComparer.Ordinal);
                    deps[className] = value;
                }

                var ctors = classDecl.Members.OfType<ConstructorDeclarationSyntax>();
                foreach (var ctor in ctors)
                {
                    foreach (var param in ctor.ParameterList.Parameters)
                    {
                        if (param.Type == null)
                        {
                            continue;
                        }

                        var typeSymbol = target.SemanticModel.GetTypeInfo(param.Type, cancellationToken).Type;
                        if (typeSymbol == null || typeSymbol.TypeKind == TypeKind.Error)
                        {
                            continue;
                        }

                        // Only track types that are declared in this solution (user types)
                        if (typeSymbol.DeclaringSyntaxReferences.Length == 0)
                        {
                            continue;
                        }

                        value.Add(typeSymbol.Name);
                    }
                }
            }
        }

        var results = new List<string>();
        var reportedCycles = new HashSet<string>(StringComparer.Ordinal);
        foreach (var start in deps.Keys)
        {
            var visited = new HashSet<string>(StringComparer.Ordinal);
            var path = new List<string>();
            DetectTypeCycle(start, deps, visited, path, results, reportedCycles);
        }

        return results;
    }

    private static void DetectTypeCycle(string current, Dictionary<string, HashSet<string>> deps, HashSet<string> visited, List<string> path, List<string> results, HashSet<string> reportedCycles)
    {
        if (!deps.TryGetValue(current, out HashSet<string>? value))
        {
            return;
        }

        if (path.Contains(current))
        {
            // Found a cycle -> normalise the cycle key so A->B->A and B->A->B produce one report
            var cycleStart = path.IndexOf(current);
            var cycle = path.Skip(cycleStart).Concat(new[] { current }).ToList();
            var key = string.Join("->", cycle.OrderBy(x => x));
            if (reportedCycles.Add(key))
            {
                results.Add($"Circular type dependency: {string.Join(" -> ", cycle)}");
            }

            return;
        }

        if (visited.Contains(current))
        {
            return;
        }

        visited.Add(current);
        path.Add(current);
        foreach (var dep in value)
        {
            DetectTypeCycle(dep, deps, visited, path, results, reportedCycles);
        }

        path.RemoveAt(path.Count - 1);
    }

    // ── Namespace / path mismatch detection ───────────────────────────────────
    public async Task<NamespacePathMismatchReport> FindNamespacePathMismatchesAsync(Solution solution, string? projectName, CancellationToken cancellationToken = default)
    {
        var errors = new List<NamespacePathMismatch>();
        var warnings = new List<NamespacePathMismatch>();
        int totalFiles = 0;
        var projects = solution.Projects.AsEnumerable();
        if (!string.IsNullOrEmpty(projectName))
        {
            projects = projects.Where(p => p.Name.Equals(projectName, StringComparison.OrdinalIgnoreCase) || p.Name.Contains(projectName, StringComparison.OrdinalIgnoreCase));
        }

        // Build a cross-project lookup: fully-qualified-type-name -> list of file paths
        // Used to detect duplicate type names across mismatched paths (ErrorData severity).
        var typeToFiles = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        // First pass: collect (document, declaredNamespace, expectedNamespace) for all relevant docs.
        var findings = new List<(Document Doc, string? DeclaredNs, string ExpectedNs, List<string> TypeNames, string ProjectName)>();
        foreach (var project in projects)
        {
            var rootNamespace = GetRootNamespace(project);
            var projectRoot = project.FilePath is not null ? Path.GetDirectoryName(project.FilePath) : null;
            foreach (var doc in project.Documents)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var filePath = doc.FilePath;
                if (filePath is null)
                {
                    continue;
                }

                // Skip generated files.
                var fileName = Path.GetFileName(filePath);
                if (fileName.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase) || fileName.EndsWith(".generated.cs", StringComparison.OrdinalIgnoreCase) || fileName.EndsWith(".Designer.cs", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                totalFiles++;
                var syntaxRoot = await doc.GetSyntaxRootAsync(cancellationToken);
                if (syntaxRoot is null)
                {
                    continue;
                }

                // Extract all namespace declarations (both block and file-scoped).
                var nsDecls = syntaxRoot.DescendantNodes().Where(n => n is NamespaceDeclarationSyntax or FileScopedNamespaceDeclarationSyntax).ToList();
                // Extract top-level type names declared in this file (for duplicate detection).
                var typeNames = syntaxRoot.DescendantNodes().OfType<BaseTypeDeclarationSyntax>().Where(t => t.Parent is NamespaceDeclarationSyntax or FileScopedNamespaceDeclarationSyntax or CompilationUnitSyntax).Select(t => t.Identifier.Text).ToList();
                string? declaredNs;
                if (nsDecls.Count == 0)
                {
                    declaredNs = null; // global namespace
                }
                else if (nsDecls.Count == 1)
                {
                    declaredNs = nsDecls[0] switch
                    {
                        NamespaceDeclarationSyntax ns => ns.Name.ToString().Trim(),
                        FileScopedNamespaceDeclarationSyntax fns => fns.Name.ToString().Trim(),
                        _ => null
                    };
                }
                else
                {
                    // Multiple namespace declarations -> report as a warning, use first for path check.
                    declaredNs = nsDecls[0] switch
                    {
                        NamespaceDeclarationSyntax ns => ns.Name.ToString().Trim(),
                        FileScopedNamespaceDeclarationSyntax fns => fns.Name.ToString().Trim(),
                        _ => null
                    };
                    var expectedNsMulti = projectRoot is not null ? DeriveExpectedNamespace(filePath, projectRoot, rootNamespace) : rootNamespace;
                    warnings.Add(new NamespacePathMismatch { FilePath = filePath, ProjectName = project.Name, DeclaredNamespace = declaredNs ?? "", ExpectedNamespace = expectedNsMulti, Severity = "Warning", Reason = "MultipleNamespacesInFile", ConflictingFiles = [] });
                    continue;
                }

                var expectedNs = projectRoot is not null ? DeriveExpectedNamespace(filePath, projectRoot, rootNamespace) : rootNamespace;
                // Track type -> file for duplicate detection.
                foreach (var typeName in typeNames)
                {
                    var fqn = $"{declaredNs ?? ""}.{typeName}";
                    if (!typeToFiles.TryGetValue(fqn, out var list))
                    {
                        typeToFiles[fqn] = list = [];
                    }

                    list.Add(filePath);
                }

                findings.Add((doc, declaredNs, expectedNs, typeNames, project.Name));
            }
        }

        // Second pass: classify each finding.
        foreach (var(doc, declaredNs, expectedNs, typeNames, projName)in findings)
        {
            var filePath = doc.FilePath!;
            if (declaredNs is null)
            {
                // No namespace declaration -> global namespace is unexpected when a root namespace exists.
                if (!string.IsNullOrEmpty(expectedNs))
                {
                    warnings.Add(new NamespacePathMismatch { FilePath = filePath, ProjectName = projName, DeclaredNamespace = "", ExpectedNamespace = expectedNs, Severity = "Warning", Reason = "GlobalNamespace", ConflictingFiles = [] });
                }

                continue;
            }

            if (string.Equals(declaredNs, expectedNs, StringComparison.OrdinalIgnoreCase))
            {
                continue; // Clean - matches.
            }

            // Mismatch detected. Check for duplicate type names at the conflicting path.
            var conflicting = new List<string>();
            foreach (var typeName in typeNames)
            {
                var fqn = $"{expectedNs}.{typeName}";
                if (typeToFiles.TryGetValue(fqn, out var otherFiles))
                {
                    conflicting.AddRange(otherFiles.Where(f => !f.Equals(filePath, StringComparison.OrdinalIgnoreCase)));
                }
            }

            if (conflicting.Count > 0)
            {
                errors.Add(new NamespacePathMismatch { FilePath = filePath, ProjectName = projName, DeclaredNamespace = declaredNs, ExpectedNamespace = expectedNs, Severity = "Error", Reason = "DuplicateTypeAtMismatchedPath", ConflictingFiles = conflicting.Distinct().ToList() });
            }
            else
            {
                warnings.Add(new NamespacePathMismatch { FilePath = filePath, ProjectName = projName, DeclaredNamespace = declaredNs, ExpectedNamespace = expectedNs, Severity = "Warning", Reason = "NamespaceFolderMismatch", ConflictingFiles = [] });
            }
        }

        // Partial-class-across-namespaces detection.
        // Find partial type names declared in more than one namespace.
        var partialTypeToNamespaces = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var(doc, declaredNs, _, typeNames, _)in findings)
        {
            if (declaredNs is null)
            {
                continue;
            }

            var syntaxRoot = await doc.GetSyntaxRootAsync(cancellationToken);
            if (syntaxRoot is null)
            {
                continue;
            }

            var partials = syntaxRoot.DescendantNodes().OfType<BaseTypeDeclarationSyntax>().Where(t => t.Modifiers.Any(m => m.IsKind(SyntaxKind.PartialKeyword))).Select(t => t.Identifier.Text);
            foreach (var name in partials)
            {
                if (!partialTypeToNamespaces.TryGetValue(name, out var nss))
                {
                    partialTypeToNamespaces[name] = nss = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                }

                nss.Add(declaredNs);
            }
        }

        foreach (var(typeName, namespaces)in partialTypeToNamespaces)
        {
            if (namespaces.Count > 1)
            {
                // Find the files that contain this partial type.
                var involvedFiles = findings.Where(f => f.TypeNames.Contains(typeName, StringComparer.OrdinalIgnoreCase)).Select(f => f.Doc.FilePath!).ToList();
                // Only report once per type (use first file).
                if (involvedFiles.Count > 0)
                {
                    var first = involvedFiles[0];
                    // Avoid double-reporting a file already in errors/warnings.
                    if (!errors.Any(e => e.FilePath == first) && !warnings.Any(w => w.FilePath == first))
                    {
                        warnings.Add(new NamespacePathMismatch { FilePath = first, ProjectName = findings.First(f => f.Doc.FilePath == first).ProjectName, DeclaredNamespace = string.Join(", ", namespaces), ExpectedNamespace = "", Severity = "Warning", Reason = "PartialClassAcrossNamespaces", ConflictingFiles = involvedFiles.Skip(1).ToList() });
                    }
                }
            }
        }

        int mismatchCount = errors.Count + warnings.Count;
        string summary = mismatchCount == 0 ? $"Clean: all {totalFiles} file(s) have matching namespaces and folder paths." : $"{mismatchCount} mismatch(es) found in {totalFiles} file(s): " + $"{errors.Count} error(s), {warnings.Count} warning(s).";
        return new NamespacePathMismatchReport
        {
            Errors = errors,
            Warnings = warnings,
            TotalFiles = totalFiles,
            MismatchCount = mismatchCount,
            IsClean = mismatchCount == 0,
            Summary = summary
        };
    }

    /// <summary>
    /// Derives the expected namespace for a file based on its path relative to the project root.
    /// </summary>
    private static string DeriveExpectedNamespace(FilePathWrapper filePath, string projectRoot, string rootNamespace)
    {
        // Get the directory containing the file, relative to the project root.
        var fileDir = Path.GetDirectoryName(filePath) ?? "";
        var relativeDir = Path.GetRelativePath(projectRoot, fileDir);
        if (relativeDir == ".")
        {
            return rootNamespace;
        }

        // Convert directory separators to namespace separators, strip leading dots.
        var nsPart = relativeDir.Replace('\\', '.').Replace('/', '.').Trim('.');
        return string.IsNullOrEmpty(nsPart) ? rootNamespace : $"{rootNamespace}.{nsPart}";
    }

    /// <summary>
    /// Reads the <c><RootNamespace></c> MSBuild property from the project file,
    /// falling back to the project name (the .csproj stem).
    /// </summary>
    private static string GetRootNamespace(Project project)
    {
        // Try to read <RootNamespace> from the .csproj XML directly -> works for both
        // SDK-style and legacy project formats, and doesn't require a compilation.
        if (project.FilePath is not null)
        {
            try
            {
                var xdoc = System.Xml.Linq.XDocument.Load(project.FilePath);
                var ns = xdoc.Descendants().FirstOrDefault(e => string.Equals(e.Name.LocalName, "RootNamespace", StringComparison.OrdinalIgnoreCase))?.Value;
                if (!string.IsNullOrWhiteSpace(ns))
                {
                    return ns.Trim();
                }
            }
            catch
            { /* best effort - fall through to project name */
            }
        }

        return project.Name;
    }
}