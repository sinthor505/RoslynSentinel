using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RoslynSentinel.Server;

public record PathCoverageReport(string MethodName, List<string> BranchesToTest);

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
        var method = root?.DescendantNodes().OfType<MethodDeclarationSyntax>().FirstOrDefault(m => m.Identifier.Text == methodName);

        var branches = new List<string>();
        if (method != null)
        {
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
        }

        return new PathCoverageReport(methodName, branches);
    }
}
