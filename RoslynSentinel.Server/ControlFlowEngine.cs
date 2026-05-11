using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace RoslynSentinel.Server;

public record PathCoverageReport(string MethodName, List<string> BranchesToTest);

public record ControlFlowAnalysisResult(
    string MethodName,
    bool EndPointIsReachable,
    List<string> ReturnStatements,
    List<string> ThrowStatements,
    List<string> BreakStatements,
    List<string> ContinueStatements,
    string? Error = null);

public record DataFlowAnalysisResult(
    string MethodName,
    List<string> DataFlowsIn,
    List<string> DataFlowsOut,
    List<string> VariablesDeclared,
    List<string> AlwaysAssigned,
    List<string> ReadInside,
    List<string> WrittenInside,
    string? Error = null);

public class ControlFlowEngine
{
    private readonly PersistentWorkspaceManager _workspaceManager;

    public ControlFlowEngine(PersistentWorkspaceManager workspaceManager)
    {
        _workspaceManager = workspaceManager;
    }

    /// <summary>
    /// Analyzes a method and returns a list of all logic paths that need test coverage.
    /// </summary>
    public async Task<PathCoverageReport> AnalyzePathCoverageAsync(string filePath, string methodName, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault();
        if (document == null) return new PathCoverageReport(methodName, new List<string>());

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        // Get ALL overloads, not just the first one
        var methods = root?.DescendantNodes().OfType<MethodDeclarationSyntax>()
            .Where(m => m.Identifier.Text == methodName)
            .ToList() ?? [];

        var branches = new List<string>();
        
        // Analyze all overloads
        foreach (var method in methods)
        {
            if (method == null) continue;
            
            var ifs = method.DescendantNodes().OfType<IfStatementSyntax>();
            foreach (var ifNode in ifs)
            {
                branches.Add($"Condition: {ifNode.Condition} (True Path)");
                if (ifNode.Else != null) branches.Add($"Condition: {ifNode.Condition} (False Path)");
            }

            var switches = method.DescendantNodes().OfType<SwitchStatementSyntax>();
            foreach (var sw in switches)
            {
                foreach (var section in sw.Sections)
                {
                    branches.Add($"Switch Case: {string.Join(", ", section.Labels)}");
                }
            }

            // Also handle ternary operators (ConditionalExpressionSyntax) for expression-bodied methods
            var ternaries = method.DescendantNodes().OfType<ConditionalExpressionSyntax>();
            foreach (var ternary in ternaries)
            {
                branches.Add($"Ternary: {ternary.Condition} (True Path)");
                branches.Add($"Ternary: {ternary.Condition} (False Path)");
            }
        }

        return new PathCoverageReport(methodName, branches);
    }

    /// <summary>
    /// Analyzes control flow for an entire method body using Roslyn's semantic analysis.
    /// Takes the method name (not raw line ranges) — avoids the "include method signature" trap.
    /// If multiple overloads exist, provide disambiguateLine (any line inside the desired overload).
    /// </summary>
    public async Task<ControlFlowAnalysisResult> AnalyzeMethodControlFlowAsync(
        string filePath,
        string methodName,
        int? disambiguateLine = null,
        CancellationToken ct = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault();
        if (document == null)
            return new ControlFlowAnalysisResult(methodName, false, [], [], [], [], $"File not found: {filePath}");

        var root = await document.GetSyntaxRootAsync(ct);
        var text = await document.GetTextAsync(ct);
        var methods = root?.DescendantNodes().OfType<MethodDeclarationSyntax>()
            .Where(m => m.Identifier.Text == methodName)
            .ToList() ?? [];

        if (methods.Count == 0)
            return new ControlFlowAnalysisResult(methodName, false, [], [], [], [], $"Method '{methodName}' not found in {filePath}.");

        MethodDeclarationSyntax method;
        if (methods.Count == 1)
        {
            method = methods[0];
        }
        else if (disambiguateLine.HasValue)
        {
            method = methods.FirstOrDefault(m =>
            {
                var span = m.GetLocation().GetLineSpan();
                return disambiguateLine.Value >= span.StartLinePosition.Line + 1 &&
                       disambiguateLine.Value <= span.EndLinePosition.Line + 1;
            }) ?? methods[0];
        }
        else
        {
            var locations = methods.Select(m =>
            {
                var span = m.GetLocation().GetLineSpan();
                return $"line {span.StartLinePosition.Line + 1}";
            });
            return new ControlFlowAnalysisResult(methodName, false, [], [], [], [],
                $"Multiple overloads of '{methodName}' found at: {string.Join(", ", locations)}. " +
                $"Provide disambiguateLine with any line number inside the desired overload.");
        }

        if (method.Body == null)
            return new ControlFlowAnalysisResult(methodName, false, [], [], [], [],
                "Method has no block body (expression-bodied member). Control flow analysis requires a block body.");

        var statements = method.Body.Statements;
        if (statements.Count == 0)
            return new ControlFlowAnalysisResult(methodName, true, [], [], [], [], null);

        var model = await document.GetSemanticModelAsync(ct);
        if (model == null)
            return new ControlFlowAnalysisResult(methodName, false, [], [], [], [], "Semantic model unavailable.");
        var analysis = model.AnalyzeControlFlow(statements.First(), statements.Last());

        static string NodeText(SyntaxNode node) =>
            node.ToString().Trim().Replace("\r\n", " ").Replace("\n", " ");

        var returns   = analysis.ReturnStatements.Select(n => NodeText(n)).ToList();
        var throws    = analysis.ExitPoints.Where(n => n.IsKind(SyntaxKind.ThrowStatement)).Select(n => NodeText(n)).ToList();
        var breaks    = analysis.ExitPoints.Where(n => n.IsKind(SyntaxKind.BreakStatement)).Select(n => NodeText(n)).ToList();
        var continues = analysis.ExitPoints.Where(n => n.IsKind(SyntaxKind.ContinueStatement)).Select(n => NodeText(n)).ToList();

        return new ControlFlowAnalysisResult(
            methodName,
            analysis.EndPointIsReachable,
            returns, throws, breaks, continues);
    }

    /// <summary>
    /// Analyzes data flow for an entire method body using Roslyn's semantic analysis.
    /// Takes the method name (not raw line ranges) — avoids the "include method signature" trap.
    /// If multiple overloads exist, provide disambiguateLine (any line inside the desired overload).
    /// </summary>
    public async Task<DataFlowAnalysisResult> AnalyzeMethodDataFlowAsync(
        string filePath,
        string methodName,
        int? disambiguateLine = null,
        CancellationToken ct = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault();
        if (document == null)
            return new DataFlowAnalysisResult(methodName, [], [], [], [], [], [], $"File not found: {filePath}");

        var root = await document.GetSyntaxRootAsync(ct);
        var methods = root?.DescendantNodes().OfType<MethodDeclarationSyntax>()
            .Where(m => m.Identifier.Text == methodName)
            .ToList() ?? [];

        if (methods.Count == 0)
            return new DataFlowAnalysisResult(methodName, [], [], [], [], [], [], $"Method '{methodName}' not found in {filePath}.");

        MethodDeclarationSyntax method;
        if (methods.Count == 1)
        {
            method = methods[0];
        }
        else if (disambiguateLine.HasValue)
        {
            method = methods.FirstOrDefault(m =>
            {
                var span = m.GetLocation().GetLineSpan();
                return disambiguateLine.Value >= span.StartLinePosition.Line + 1 &&
                       disambiguateLine.Value <= span.EndLinePosition.Line + 1;
            }) ?? methods[0];
        }
        else
        {
            var locations = methods.Select(m =>
            {
                var span = m.GetLocation().GetLineSpan();
                return $"line {span.StartLinePosition.Line + 1}";
            });
            return new DataFlowAnalysisResult(methodName, [], [], [], [], [], [],
                $"Multiple overloads of '{methodName}' found at: {string.Join(", ", locations)}. " +
                $"Provide disambiguateLine with any line number inside the desired overload.");
        }

        if (method.Body == null)
            return new DataFlowAnalysisResult(methodName, [], [], [], [], [], [],
                "Method has no block body (expression-bodied member). Data flow analysis requires a block body.");

        var statements = method.Body.Statements;
        if (statements.Count == 0)
            return new DataFlowAnalysisResult(methodName, [], [], [], [], [], [], null);

        var model = await document.GetSemanticModelAsync(ct);
        if (model == null)
            return new DataFlowAnalysisResult(methodName, [], [], [], [], [], [], "Semantic model unavailable.");
        var analysis = model.AnalyzeDataFlow(statements.First(), statements.Last());

        return new DataFlowAnalysisResult(
            methodName,
            analysis.DataFlowsIn.Select(s => s.Name).ToList(),
            analysis.DataFlowsOut.Select(s => s.Name).ToList(),
            analysis.VariablesDeclared.Select(s => s.Name).ToList(),
            analysis.AlwaysAssigned.Select(s => s.Name).ToList(),
            analysis.ReadInside.Select(s => s.Name).ToList(),
            analysis.WrittenInside.Select(s => s.Name).ToList());
    }

    // ── Enum Switch Exhaustiveness ─────────────────────────────────────────

    public record EnumSwitchGap(
        string FilePath,
        int Line,
        string EnumTypeName,
        List<string> MissingMembers,
        string MethodName
    );

    public async Task<List<EnumSwitchGap>> FindNonExhaustiveEnumSwitchesAsync(
        string? filePath = null,
        string? projectName = null,
        CancellationToken ct = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();

        IEnumerable<Document?> documents;
        if (!string.IsNullOrEmpty(filePath))
            documents = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument);
        else if (!string.IsNullOrEmpty(projectName))
        {
            var proj = solution.Projects.FirstOrDefault(p =>
                string.Equals(p.Name, projectName, StringComparison.OrdinalIgnoreCase));
            documents = proj?.Documents.Cast<Document?>() ?? [];
        }
        else
            documents = solution.Projects.SelectMany(p => p.Documents).Cast<Document?>();

        var gaps = new List<EnumSwitchGap>();

        foreach (var doc in documents)
        {
            if (doc == null) continue;
            var root = await doc.GetSyntaxRootAsync(ct);
            if (root == null) continue;
            var model = await doc.GetSemanticModelAsync(ct);
            if (model == null) continue;
            var docPath = doc.FilePath ?? doc.Name;

            foreach (var switchStmt in root.DescendantNodes().OfType<SwitchStatementSyntax>())
            {
                // Only interested in switches on enum types
                var switchType = model.GetTypeInfo(switchStmt.Expression, ct).Type;
                if (switchType == null || switchType.TypeKind != TypeKind.Enum) continue;

                // Has a default label → already handles unrecognized values
                bool hasDefault = switchStmt.Sections
                    .SelectMany(s => s.Labels)
                    .Any(l => l is DefaultSwitchLabelSyntax);
                if (hasDefault) continue;

                // Collect all enum member names handled by the switch
                var handledNames = new HashSet<string>(StringComparer.Ordinal);
                foreach (var section in switchStmt.Sections)
                {
                    foreach (var label in section.Labels.OfType<CaseSwitchLabelSyntax>())
                    {
                        // The case expression might be: MyEnum.Active, or just Active
                        var caseSym = model.GetSymbolInfo(label.Value, ct).Symbol;
                        if (caseSym is IFieldSymbol field && field.ContainingType.Equals(switchType, SymbolEqualityComparer.Default))
                            handledNames.Add(field.Name);
                        else
                        {
                            // Fallback: last identifier in the expression
                            var id = label.Value.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>().LastOrDefault();
                            if (id != null) handledNames.Add(id.Identifier.Text);
                        }
                    }
                }

                // Collect all declared enum members
                var allMembers = switchType.GetMembers()
                    .OfType<IFieldSymbol>()
                    .Where(f => f.IsConst || f.IsStatic)
                    .Select(f => f.Name)
                    .ToList();

                var missing = allMembers.Where(m => !handledNames.Contains(m)).ToList();
                if (missing.Count == 0) continue;

                var containingMethod = switchStmt.Ancestors().OfType<MethodDeclarationSyntax>()
                    .FirstOrDefault()?.Identifier.Text ?? "<top-level>";
                var line = switchStmt.GetLocation().GetLineSpan().StartLinePosition.Line + 1;

                gaps.Add(new EnumSwitchGap(docPath, line, switchType.Name, missing, containingMethod));
            }
        }

        return gaps;
    }
}
