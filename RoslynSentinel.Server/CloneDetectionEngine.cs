using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RoslynSentinel.Server;

public record DuplicateBlockLocation(
    FilePath filePath,
    string MethodName,
    string ContainingType,
    int StartLine,
    int EndLine,
    int StatementCount
);

public record DuplicateBlockGroup(
    int StatementCount,
    bool HasControlFlowExit,
    string SnippetPreview,
    List<string> CapturedVariables,
    List<string> ProducedVariables,
    List<DuplicateBlockLocation> Occurrences
);

public class CloneDetectionEngine
{
    private readonly PersistentWorkspaceManager _workspaceManager;

    public CloneDetectionEngine(PersistentWorkspaceManager workspaceManager)
    {
        _workspaceManager = workspaceManager;
    }

    // ── Within a single class ─────────────────────────────────────────────────

    public async Task<List<DuplicateBlockGroup>> FindDuplicateBlocksInClassAsync(
        FilePath filePath, string className, int minStatements = 4, CancellationToken ct = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();

        Document? document = null;
        foreach (var project in solution.Projects)
        {
            document = project.Documents.FirstOrDefault(d =>
                string.Equals(d.FilePath, filePath, StringComparison.OrdinalIgnoreCase));
            if (document != null)
            {
                break;
            }
        }
        if (document == null)
        {
            return [];
        }

        var semanticModel = await document.GetSemanticModelAsync(ct);
        if (semanticModel == null)
        {
            return [];
        }

        var root = await document.GetSyntaxRootAsync(ct);
        if (root == null)
        {
            return [];
        }

        var classDecl = root.DescendantNodes()
            .OfType<ClassDeclarationSyntax>()
            .FirstOrDefault(c => c.Identifier.Text == className);
        if (classDecl == null)
        {
            return [];
        }

        var methods = classDecl.Members.OfType<MethodDeclarationSyntax>().ToList();
        var windows = CollectWindows(methods, filePath, className, minStatements);

        return GroupDuplicates(windows, semanticModel, minStatements);
    }

    // ── Within an inheritance / interface hierarchy ───────────────────────────

    public async Task<List<DuplicateBlockGroup>> FindDuplicateBlocksInHierarchyAsync(
        string typeName, string? projectName = null, int minStatements = 4, CancellationToken ct = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();

        var projects = string.IsNullOrEmpty(projectName)
            ? solution.Projects
            : solution.Projects.Where(p => p.Name.Contains(projectName, StringComparison.OrdinalIgnoreCase));

        // Find the root type symbol
        INamedTypeSymbol? rootSymbol = null;
        Compilation? rootCompilation = null;

        foreach (var project in projects)
        {
            var compilation = await project.GetCompilationAsync(ct);
            if (compilation == null)
            {
                continue;
            }

            var candidate = compilation.GetSymbolsWithName(typeName, SymbolFilter.Type, ct)
                .OfType<INamedTypeSymbol>()
                .FirstOrDefault();

            if (candidate != null)
            {
                rootSymbol = candidate;
                rootCompilation = compilation;
                break;
            }
        }

        if (rootSymbol == null)
        {
            return [];
        }

        // Build the set of related type names (root + all derived/implementing types)
        var relatedTypeNames = new HashSet<string>(StringComparer.Ordinal);
        relatedTypeNames.Add(rootSymbol.Name);

        // Walk interfaces and base classes upward
        foreach (var iface in rootSymbol.AllInterfaces)
        {
            relatedTypeNames.Add(iface.Name);
        }

        var current = rootSymbol.BaseType;
        while (current != null && current.SpecialType == SpecialType.None)
        {
            relatedTypeNames.Add(current.Name);
            current = current.BaseType;
        }

        // Find all concrete types that implement / inherit from the root
        foreach (var project in solution.Projects)
        {
            var compilation = await project.GetCompilationAsync(ct);
            if (compilation == null)
            {
                continue;
            }

            foreach (var tree in compilation.SyntaxTrees)
            {
                var semanticModel = compilation.GetSemanticModel(tree);
                var treeRoot = await tree.GetRootAsync(ct);

                foreach (var classDecl in treeRoot.DescendantNodes().OfType<ClassDeclarationSyntax>())
                {
                    if (semanticModel.GetDeclaredSymbol(classDecl, ct) is not INamedTypeSymbol typeSymbol)
                    {
                        continue;
                    }

                    // Is this type in the hierarchy?
                    bool related = typeSymbol.AllInterfaces.Any(i => i.Name == rootSymbol.Name)
                        || IsBaseTypeInHierarchy(typeSymbol, rootSymbol)
                        || relatedTypeNames.Contains(typeSymbol.Name);

                    if (related)
                    {
                        relatedTypeNames.Add(typeSymbol.Name);
                    }
                }
            }
        }

        // Collect windows from all related types across all documents
        var allWindows = new List<(string Hash, StatementWindow Window)>();

        foreach (var project in solution.Projects)
        {
            foreach (var document in project.Documents)
            {
                var root = await document.GetSyntaxRootAsync(ct);
                if (root == null)
                {
                    continue;
                }

                var semanticModel = await document.GetSemanticModelAsync(ct);
                if (semanticModel == null)
                {
                    continue;
                }

                var FilePath = document.FilePath ?? document.Name;

                foreach (var classDecl in root.DescendantNodes().OfType<ClassDeclarationSyntax>())
                {
                    if (!relatedTypeNames.Contains(classDecl.Identifier.Text))
                    {
                        continue;
                    }

                    var methods = classDecl.Members.OfType<MethodDeclarationSyntax>().ToList();
                    var windows = CollectWindows(methods, FilePath, classDecl.Identifier.Text, minStatements);
                    allWindows.AddRange(windows);
                }
            }
        }

        if (allWindows.Count == 0)
        {
            return [];
        }

        // We need a SemanticModel for dataflow — use the root type's compilation for best results
        // For hierarchy mode, dataflow uses the first model that compiled the block
        return GroupDuplicatesFromWindows(allWindows, minStatements);
    }

    // ── Core algorithm ────────────────────────────────────────────────────────

    private record StatementWindow(
        string Hash,
        StatementSyntax[] Statements,
        FilePath FilePath,
        string MethodName,
        string ContainingType,
        SemanticModel? Model
    );

    private List<(string Hash, StatementWindow Window)> CollectWindows(
        List<MethodDeclarationSyntax> methods,
        FilePath FilePath,
        string typeName,
        int minStatements)
    {
        var result = new List<(string Hash, StatementWindow Window)>();

        foreach (var method in methods)
        {
            var body = method.Body;
            if (body == null)
            {
                continue; // expression-bodied, skip
            }

            var stmts = body.Statements.ToArray();
            if (stmts.Length < minStatements)
            {
                continue;
            }

            // Sliding window — use set to avoid overlapping windows from same method
            var coveredRanges = new List<(int Start, int End)>();

            for (int windowSize = stmts.Length; windowSize >= minStatements; windowSize--)
            {
                for (int start = 0; start <= stmts.Length - windowSize; start++)
                {
                    // Skip if overlaps an already-covered range
                    int end = start + windowSize - 1;
                    if (coveredRanges.Any(r => start <= r.End && end >= r.Start))
                    {
                        continue;
                    }

                    var window = stmts[start..(end + 1)];
                    var hash = ComputeStructuralHash(window);
                    result.Add((hash, new StatementWindow(hash, window, FilePath, method.Identifier.Text, typeName, null)));
                }
            }
        }

        return result;
    }

    private static string ComputeStructuralHash(StatementSyntax[] statements)
    {
        var kinds = statements
            .SelectMany(s => s.DescendantNodesAndSelf())
            .Select(n => (int)n.Kind());

        var hashCode = new System.Text.StringBuilder();
        foreach (var k in kinds)
        {
            hashCode.Append(k).Append(',');
        }

        return hashCode.ToString();
    }

    private List<DuplicateBlockGroup> GroupDuplicates(
        List<(string Hash, StatementWindow Window)> windows,
        SemanticModel semanticModel,
        int minStatements)
    {
        var windowsWithModel = windows.Select(w =>
            (w.Hash, Window: w.Window with
            {
                Model = semanticModel
            })).ToList();
        return GroupDuplicatesFromWindows(windowsWithModel, minStatements);
    }

    private static List<DuplicateBlockGroup> GroupDuplicatesFromWindows(
        List<(string Hash, StatementWindow Window)> windows,
        int minStatements)
    {
        var groups = windows
            .GroupBy(w => w.Hash)
            .Where(g => g.Count() >= 2)
            .ToList();

        var result = new List<DuplicateBlockGroup>();

        foreach (var group in groups)
        {
            var first = group.First().Window;
            var stmts = first.Statements;
            int statementCount = stmts.Length;

            bool hasExit = stmts.Any(s =>
                s.DescendantNodesAndSelf().Any(n =>
                    n is ReturnStatementSyntax ||
                    n is BreakStatementSyntax ||
                    n is ContinueStatementSyntax ||
                    n is ThrowStatementSyntax ||
                    n is GotoStatementSyntax));

            string preview = Truncate(stmts[0].ToString(), 120);

            List<string> captured = [];
            List<string> produced = [];

            if (first.Model != null && stmts.Length >= 2)
            {
                try
                {
                    var dataflow = first.Model.AnalyzeDataFlow(stmts[0], stmts[^1]);
                    if (dataflow != null && dataflow.Succeeded)
                    {
                        captured = dataflow.DataFlowsIn.Select(s => $"{s.Name}:{s.Kind}").ToList();
                        produced = dataflow.DataFlowsOut.Select(s => $"{s.Name}:{s.Kind}").ToList();
                    }
                }
                catch { /* dataflow can fail on certain syntax shapes */ }
            }

            var locations = group.Select(w =>
            {
                var win = w.Window;
                int startLine = win.Statements[0].GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                int endLine = win.Statements[^1].GetLocation().GetLineSpan().EndLinePosition.Line + 1;
                return new DuplicateBlockLocation(win.FilePath, win.MethodName, win.ContainingType, startLine, endLine, statementCount);
            }).ToList();

            result.Add(new DuplicateBlockGroup(statementCount, hasExit, preview, captured, produced, locations));
        }

        return result.OrderByDescending(g => g.StatementCount).ToList();
    }

    private static bool IsBaseTypeInHierarchy(INamedTypeSymbol type, INamedTypeSymbol target)
    {
        var b = type.BaseType;
        while (b != null && b.SpecialType == SpecialType.None)
        {
            if (SymbolEqualityComparer.Default.Equals(b, target))
            {
                return true;
            }

            b = b.BaseType;
        }
        return false;
    }

    private static string Truncate(string s, int max = 120)
        => s.Length <= max ? s : s[..max] + "…";
}
