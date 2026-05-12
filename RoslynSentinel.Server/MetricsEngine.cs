using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RoslynSentinel.Server;

public record SolutionMetrics(
    int ProjectCount,
    int TotalFiles,
    long TotalLines,
    int TotalTypes,
    int TotalMethods,
    List<ProjectMetric> Projects
);

public record ProjectMetric(
    string Name,
    int FileCount,
    long LineCount,
    int TypeCount,
    int MethodCount
);

public record MethodFieldUsage(
    string MethodName,
    List<string> FieldsUsed
);

public record CohesionAnalysis(
    string TypeName,
    string FilePath,
    int Line,
    int FieldCount,
    int MethodCount,
    double LcomScore,
    string CohesionRating,
    List<MethodFieldUsage> MethodUsages,
    List<string> SuggestedSplits
);

public class MetricsEngine
{
    private readonly PersistentWorkspaceManager _workspaceManager;

    public MetricsEngine(PersistentWorkspaceManager workspaceManager)
    {
        _workspaceManager = workspaceManager;
    }

    public async Task<SolutionMetrics> GetSolutionMetricsAsync(string? projectName = null, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var projectMetrics = new List<ProjectMetric>();

        int totalTypes = 0;
        int totalMethods = 0;
        long totalLines = 0;
        int totalFiles = 0;

        var projects = solution.Projects;
        if (!string.IsNullOrEmpty(projectName))
        {
            projects = projects.Where(p => p.Name.Contains(projectName, StringComparison.OrdinalIgnoreCase));
        }

        foreach (var project in projects)
        {
            int pTypes = 0;
            int pMethods = 0;
            long pLines = 0;
            int pFiles = 0;

            foreach (var document in project.Documents)
            {
                pFiles++;
                var text = await document.GetTextAsync(cancellationToken);
                pLines += text.Lines.Count;

                var root = await document.GetSyntaxRootAsync(cancellationToken);
                if (root != null)
                {
                    pTypes += root.DescendantNodes().OfType<BaseTypeDeclarationSyntax>().Count();
                    pMethods += root.DescendantNodes().OfType<MethodDeclarationSyntax>().Count();
                }
            }

            projectMetrics.Add(new ProjectMetric(project.Name, pFiles, pLines, pTypes, pMethods));
            totalTypes += pTypes;
            totalMethods += pMethods;
            totalLines += pLines;
            totalFiles += pFiles;
        }

        return new SolutionMetrics(
            solution.ProjectIds.Count,
            totalFiles,
            totalLines,
            totalTypes,
            totalMethods,
            projectMetrics
        );
    }

    public async Task<List<CohesionAnalysis>> AnalyzeTypeCohesionAsync(
        string filePath, string? className = null, CancellationToken ct = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();

        Document? document = null;
        foreach (var project in solution.Projects)
        {
            document = project.Documents.FirstOrDefault(d =>
                string.Equals(d.FilePath, filePath, StringComparison.OrdinalIgnoreCase));
            if (document != null) break;
        }
        if (document == null) return new List<CohesionAnalysis>();

        var semanticModel = await document.GetSemanticModelAsync(ct);
        if (semanticModel == null) return new List<CohesionAnalysis>();

        var root = await document.GetSyntaxRootAsync(ct);
        if (root == null) return new List<CohesionAnalysis>();

        var results = new List<CohesionAnalysis>();

        IEnumerable<ClassDeclarationSyntax> classDecls = root.DescendantNodes().OfType<ClassDeclarationSyntax>();
        if (!string.IsNullOrEmpty(className))
            classDecls = classDecls.Where(c => c.Identifier.Text == className);

        foreach (var classDecl in classDecls)
        {
            if (semanticModel.GetDeclaredSymbol(classDecl, ct) is not INamedTypeSymbol typeSymbol)
                continue;

            // Collect non-static fields; map backing fields of auto-properties to their property name
            var instanceFields = typeSymbol.GetMembers()
                .OfType<IFieldSymbol>()
                .Where(f => !f.IsStatic)
                .ToList();

            var fieldDisplayMap = new Dictionary<ISymbol, string>(SymbolEqualityComparer.Default);
            foreach (var f in instanceFields)
            {
                var displayName = f.AssociatedSymbol is IPropertySymbol prop ? prop.Name : f.Name;
                fieldDisplayMap[f] = displayName;
            }

            // Collect ordinary instance methods (excludes constructors, accessors, operators)
            var instanceMethods = typeSymbol.GetMembers()
                .OfType<IMethodSymbol>()
                .Where(m => !m.IsStatic && !m.IsImplicitlyDeclared && m.MethodKind == MethodKind.Ordinary)
                .ToList();

            // For each method, walk its body and resolve which instance fields are used
            var methodUsages = new List<MethodFieldUsage>();
            foreach (var method in instanceMethods)
            {
                var methodSyntax = method.DeclaringSyntaxReferences
                    .Select(r => r.GetSyntax(ct))
                    .OfType<MethodDeclarationSyntax>()
                    .FirstOrDefault();

                var fieldsUsed = new HashSet<string>();

                if (methodSyntax != null)
                {
                    SyntaxNode? body = (SyntaxNode?)methodSyntax.Body ?? methodSyntax.ExpressionBody;
                    if (body != null)
                    {
                        foreach (var id in body.DescendantNodes().OfType<IdentifierNameSyntax>())
                        {
                            var si = semanticModel.GetSymbolInfo(id, ct);
                            var sym = si.Symbol ?? si.CandidateSymbols.FirstOrDefault();
                            if (sym is IFieldSymbol fs && fieldDisplayMap.TryGetValue(fs, out var dn))
                                fieldsUsed.Add(dn);
                        }
                    }
                }

                methodUsages.Add(new MethodFieldUsage(method.Name, fieldsUsed.ToList()));
            }

            // Compute LCOM: P/(P+Q) where P=pairs sharing no fields, Q=pairs sharing ≥1 field
            int p = 0, q = 0;
            var fieldSets = methodUsages.Select(m => new HashSet<string>(m.FieldsUsed)).ToList();
            for (int i = 0; i < fieldSets.Count; i++)
                for (int j = i + 1; j < fieldSets.Count; j++)
                {
                    if (fieldSets[i].Overlaps(fieldSets[j])) q++; else p++;
                }

            double lcomScore = (p + q) > 0 ? Math.Round((double)p / (p + q), 4) : 0.0;

            string cohesionRating = lcomScore switch
            {
                <= 0.2 => "Excellent",
                <= 0.4 => "Good",
                <= 0.7 => "Poor",
                _ => "Very Poor"
            };

            // Union-Find to identify connected method components for split suggestions
            var parent = Enumerable.Range(0, methodUsages.Count).ToArray();

            int Find(int x)
            {
                if (parent[x] != x) parent[x] = Find(parent[x]);
                return parent[x];
            }

            void Union(int x, int y)
            {
                int px = Find(x), py = Find(y);
                if (px != py) parent[px] = py;
            }

            for (int i = 0; i < fieldSets.Count; i++)
                for (int j = i + 1; j < fieldSets.Count; j++)
                    if (fieldSets[i].Overlaps(fieldSets[j]))
                        Union(i, j);

            var components = new Dictionary<int, List<int>>();
            for (int i = 0; i < methodUsages.Count; i++)
            {
                int compRoot = Find(i);
                if (!components.TryGetValue(compRoot, out var members))
                    components[compRoot] = members = new List<int>();
                members.Add(i);
            }

            var suggestedSplits = new List<string>();
            if (components.Count > 1)
            {
                int splitIdx = 1;
                foreach (var component in components.Values.OrderByDescending(c => c.Count))
                {
                    var methodNames = component.Select(i => methodUsages[i].MethodName).ToList();
                    var compFields = component.SelectMany(i => methodUsages[i].FieldsUsed).Distinct().ToList();
                    var suggestedName = SuggestExtractedClassName(typeSymbol.Name, methodNames, splitIdx);

                    if (component.Count >= 2)
                    {
                        var fieldsPart = compFields.Count > 0
                            ? $" with fields [{string.Join(", ", compFields)}]"
                            : " (no shared fields — extract as static helper class)";
                        suggestedSplits.Add(
                            $"Extract '{suggestedName}': [{string.Join(", ", methodNames)}]{fieldsPart}");
                    }
                    else
                    {
                        var onlyMethod = methodNames[0];
                        var fieldsPart = compFields.Count > 0
                            ? $"uses [{string.Join(", ", compFields)}] exclusively — move with the field(s)"
                            : "uses no instance fields — extract as static method or move to a helper class";
                        suggestedSplits.Add($"Isolated: '{onlyMethod}' {fieldsPart}");
                    }
                    splitIdx++;
                }
            }

            int line = classDecl.GetLocation().GetLineSpan().StartLinePosition.Line + 1;

            results.Add(new CohesionAnalysis(
                typeSymbol.Name,
                filePath,
                line,
                instanceFields.Count,
                instanceMethods.Count,
                lcomScore,
                cohesionRating,
                methodUsages,
                suggestedSplits));
        }

        return results;
    }

    private static string SuggestExtractedClassName(string originalName, List<string> methodNames, int index)
    {
        // Look for a common leading word across all method names (e.g. all start with "Parse" → "Parser")
        static string FirstWord(string name)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var ch in name)
            {
                if (sb.Length > 0 && char.IsUpper(ch)) break;
                sb.Append(ch);
            }
            return sb.ToString();
        }

        var firstWords = methodNames.Select(FirstWord).Where(w => w.Length >= 3).ToList();
        var dominant = firstWords.GroupBy(w => w, StringComparer.OrdinalIgnoreCase)
                                 .OrderByDescending(g => g.Count())
                                 .FirstOrDefault();

        if (dominant != null && dominant.Count() == methodNames.Count)
        {
            // All methods share the same leading verb — suggest a nominal form
            var verb = dominant.Key;
            var nominal = verb.EndsWith("e") ? verb + "r" : verb + "er"; // Parse→Parser, Validate→Validator
            return $"{nominal}";
        }

        // Fall back to stripping a common suffix from the original class name and adding an ordinal
        var baseName = originalName.EndsWith("Service") ? originalName[..^7]
                     : originalName.EndsWith("Manager") ? originalName[..^7]
                     : originalName.EndsWith("Handler") ? originalName[..^7]
                     : originalName;

        return $"{baseName}Part{index}";
    }
}
