using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging;

namespace RoslynSentinel.Server;

public class ValidationEngine
{
    private readonly ILogger<ValidationEngine> _logger;
    private readonly PersistentWorkspaceManager _workspaceManager;
    private readonly DiffEngine _diffEngine;

    public ValidationEngine(ILogger<ValidationEngine> logger, PersistentWorkspaceManager workspaceManager, DiffEngine diffEngine)
    {
        _logger = logger;
        _workspaceManager = workspaceManager;
        _diffEngine = diffEngine;
    }

    public async Task<DiagnosticReport> ValidateDiffAsync(string filePath, string unifiedDiff, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var documentId = solution.GetDocumentIdsWithFilePath(filePath).FirstOrDefault();

        if (documentId == null)
        {
            return new DiagnosticReport(false, new List<DiagnosticInfo> 
            { 
                new DiagnosticInfo("RS001", "Error", $"File not found: {filePath}", filePath, 0, 0, 0, 0) 
            });
        }

        var document = solution.GetDocument(documentId)!;
        var oldText = await document.GetTextAsync(cancellationToken);
        
        try
        {
            var newText = _diffEngine.ApplyDiff(oldText, unifiedDiff);
            return await ValidateChangesAsync(new Dictionary<string, string> { { filePath, newText.ToString() } }, cancellationToken);
        }
        catch (Exception ex)
        {
            return new DiagnosticReport(false, new List<DiagnosticInfo> 
            { 
                new DiagnosticInfo("RS003", "Error", $"Failed to apply diff: {ex.Message}", filePath, 0, 0, 0, 0) 
            });
        }
    }

    public async Task<DiagnosticReport> ValidateChangesAsync(Dictionary<string, string> fileChanges, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var updatedSolution = solution;
        var affectedProjectIds = new HashSet<ProjectId>();

        _logger.LogInformation("Applying {Count} file changes to in-memory solution fork...", fileChanges.Count);

        foreach (var change in fileChanges)
        {
            var filePath = change.Key;
            var newContent = change.Value;
            var documentId = solution.GetDocumentIdsWithFilePath(filePath).FirstOrDefault();

            if (documentId == null)
            {
                return new DiagnosticReport(false, new List<DiagnosticInfo> 
                { 
                    new DiagnosticInfo("RS001", "Error", $"File not found in solution: {filePath}", filePath, 0, 0, 0, 0) 
                });
            }

            updatedSolution = updatedSolution.WithDocumentText(documentId, SourceText.From(newContent));
            affectedProjectIds.Add(documentId.ProjectId);
        }

        var allDiagnostics = new List<DiagnosticInfo>();

        foreach (var projectId in affectedProjectIds)
        {
            var project = updatedSolution.GetProject(projectId)!;
            _logger.LogInformation("Compiling project {ProjectName} in-memory...", project.Name);

            var compilation = await project.GetCompilationAsync(cancellationToken);
            if (compilation == null)
            {
                allDiagnostics.Add(new DiagnosticInfo("RS002", "Error", $"Failed to create compilation for project {project.Name}.", "", 0, 0, 0, 0));
                continue;
            }

            var diagnostics = compilation.GetDiagnostics(cancellationToken)
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .Select(d => d.ToInfo());
            
            allDiagnostics.AddRange(diagnostics);
        }

        return new DiagnosticReport(allDiagnostics.Count == 0, allDiagnostics);
    }
}
