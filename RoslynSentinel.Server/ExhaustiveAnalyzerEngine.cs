using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RoslynSentinel.Server;

public class ExhaustiveAnalyzerEngine
{
    private readonly PersistentWorkspaceManager _workspaceManager;

    public ExhaustiveAnalyzerEngine(PersistentWorkspaceManager workspaceManager)
    {
        _workspaceManager = workspaceManager;
    }

    /// <summary>
    /// Runs the Roslyn compiler diagnostic pipeline on <paramref name="filePath"/>
    /// and returns any diagnostics whose ID matches <paramref name="ruleId"/>.
    /// For compiler rules (CS*) the semantic model diagnostics are used directly.
    /// Pass an empty or null <paramref name="ruleId"/> to return ALL diagnostics.
    /// </summary>
    public async Task<List<AnalyzerIssue>> RunDiagnosticRuleAsync(string filePath, string ruleId, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault();
        if (document == null)
        {
            return new List<AnalyzerIssue>();
        }

        var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
        if (semanticModel == null)
        {
            return new List<AnalyzerIssue>();
        }

        var rawDiagnostics = semanticModel.GetDiagnostics(null, cancellationToken);

        bool allRules = string.IsNullOrEmpty(ruleId);

        var results = new List<AnalyzerIssue>();
        foreach (var d in rawDiagnostics)
        {
            if (!allRules && !string.Equals(d.Id, ruleId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var line = d.Location.GetLineSpan().StartLinePosition.Line + 1;
            results.Add(new AnalyzerIssue(d.Id, $"[{d.Severity}] {d.GetMessage()}", line));
        }
        return results;
    }
}
