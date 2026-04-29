using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;

namespace RoslynSentinel.Server;

public record DiagnosticSummary(
    int Errors,
    int Warnings,
    List<DiagnosticInfo> Details
);

public class DiagnosticEngine
{
    private readonly PersistentWorkspaceManager _workspaceManager;

    public DiagnosticEngine(PersistentWorkspaceManager workspaceManager)
    {
        _workspaceManager = workspaceManager;
    }

    public async Task<DiagnosticSummary> GetFileDiagnosticsAsync(string filePath, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault();
        if (document == null) throw new Exception("File not found.");

        var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
        var diagnostics = semanticModel?.GetDiagnostics(null, cancellationToken) ?? Enumerable.Empty<Diagnostic>();

        var list = diagnostics.Select(d => d.ToInfo()).ToList();
        return new DiagnosticSummary(
            list.Count(d => d.Severity == "Error"),
            list.Count(d => d.Severity == "Warning"),
            list
        );
    }

    public async Task<DiagnosticSummary> GetProjectDiagnosticsAsync(string projectName, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var project = solution.Projects.FirstOrDefault(p => p.Name == projectName);
        if (project == null) throw new Exception("Project not found.");

        var compilation = await project.GetCompilationAsync(cancellationToken);
        var diagnostics = compilation?.GetDiagnostics(cancellationToken) ?? Enumerable.Empty<Diagnostic>();

        var list = diagnostics.Select(d => d.ToInfo()).ToList();
        return new DiagnosticSummary(
            list.Count(d => d.Severity == "Error"),
            list.Count(d => d.Severity == "Warning"),
            list
        );
    }

    public async Task<DiagnosticSummary> GetSolutionDiagnosticsAsync(CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var allDiagnostics = new List<DiagnosticInfo>();

        foreach (var project in solution.Projects)
        {
            var compilation = await project.GetCompilationAsync(cancellationToken);
            if (compilation != null)
            {
                allDiagnostics.AddRange(compilation.GetDiagnostics(cancellationToken).Select(d => d.ToInfo()));
            }
        }

        return new DiagnosticSummary(
            allDiagnostics.Count(d => d.Severity == "Error"),
            allDiagnostics.Count(d => d.Severity == "Warning"),
            allDiagnostics
        );
    }
}
