using System.Diagnostics;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging;

namespace RoslynSentinel.Common;

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

    public async Task<DiagnosticReport> ValidateDiffAsync(FilePath filePath, string unifiedDiff, CancellationToken cancellationToken = default)
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
            return await ValidateChangesAsync(solution, new Dictionary<FilePath, string> { { filePath, newText.ToString() } }, cancellationToken);
        }
        catch (Exception ex)
        {
            return new DiagnosticReport(false, new List<DiagnosticInfo>
            {
                new DiagnosticInfo("RS003", "Error", $"Failed to apply diff: {ex.Message}", filePath, 0, 0, 0, 0)
            });
        }
    }

    /// <summary>
    /// Validates proposed file changes using the current workspace snapshot.
    /// Returns only NEWLY INTRODUCED errors — errors present after the change that were
    /// not already present before it (delta approach).
    /// When errors are found, writes a blob to .roslynsentinel/validation/ for manual review.
    /// </summary>
    public async Task<DiagnosticReport> ValidateChangesAsync(Dictionary<FilePath, string> fileChanges,
        IProgress<string>? progress = default,
        CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var report = await ValidateChangesAsync(solution, fileChanges, cancellationToken);

        if (!report.Success && report.Diagnostics.Count > 0)
        {
            _ = OperationBlobWriter.WriteValidationFailureAsync(
                fileChanges.Keys.Select(p => p.ToString()),
                report.Diagnostics,
                _workspaceManager.GetSolutionRoot());
        }

        return report;
    }

    /// <summary>
    /// Static core — takes a Solution snapshot directly so it can be called without a
    /// workspace manager instance (e.g. from inside ApplyProposedChangesAsync using
    /// CurrentSolution, avoiding re-acquiring the solution lock).
    ///
    /// Files not found in the solution (RS001) are treated as pass-through: the tool
    /// cannot validate new files in-memory, so it allows them rather than blocking.
    /// </summary>
    public static async Task<DiagnosticReport> ValidateChangesAsync(
        Solution baseline, Dictionary<FilePath, string> fileChanges, CancellationToken ct = default)
    {
        Debug.WriteLine("Starting validation of proposed changes...");
        var candidate = baseline;
        var affectedProjectIds = new HashSet<ProjectId>();

        foreach (var change in fileChanges)
        {
            var filePath = change.Key;
            var newContent = change.Value;
            var documentId = baseline.GetDocumentIdsWithFilePath(filePath).FirstOrDefault();

            Debug.WriteLine($"Processing change for {filePath} (mapped to DocumentId: {documentId})...");

            if (documentId == null)
            {
                Debug.WriteLine($"File not found in solution, skipping in-memory validation: {filePath}");
                continue;
            }

            candidate = candidate.WithDocumentText(documentId, SourceText.From(newContent));
            affectedProjectIds.Add(documentId.ProjectId);
        }

        if (affectedProjectIds.Count == 0)
        {
            Debug.WriteLine("No files could be mapped to solution documents, nothing to validate.");
            return new DiagnosticReport(true, new List<DiagnosticInfo>());
        }

        var introducedDiagnostics = new List<DiagnosticInfo>();

        foreach (var projectId in affectedProjectIds)
        {
            var baselineProject = baseline.GetProject(projectId)!;
            var candidateProject = candidate.GetProject(projectId)!;

            Debug.WriteLine($"Compiling project {baselineProject.Name} (baseline + candidate)...");

            var baselineCompilation = await baselineProject.GetCompilationAsync(ct);
            if (baselineCompilation == null)
            {
                introducedDiagnostics.Add(new DiagnosticInfo("RS002", "Error",
                    $"Failed to create baseline compilation for project {baselineProject.Name}.", "", 0, 0, 0, 0));
                continue;
            }

            var baselineErrors = baselineCompilation
                .GetDiagnostics(ct)
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .Select(DiagnosticKey)
                .ToHashSet();

            var candidateCompilation = await candidateProject.GetCompilationAsync(ct);
            if (candidateCompilation == null)
            {
                introducedDiagnostics.Add(new DiagnosticInfo("RS002", "Error",
                    $"Failed to create candidate compilation for project {candidateProject.Name}.", "", 0, 0, 0, 0));
                continue;
            }

            foreach (var diagnostic in candidateCompilation.GetDiagnostics(ct))
            {
                if (diagnostic.Severity != DiagnosticSeverity.Error)
                    continue;

                Debug.WriteLine($"Found new error in project {candidateProject.Name}: {diagnostic.GetMessage()}");
                if (!baselineErrors.Contains(DiagnosticKey(diagnostic)))
                    introducedDiagnostics.Add(diagnostic.ToInfo());
            }
        }

        Debug.WriteLine($"Validation complete. Introduced errors: {introducedDiagnostics.Count}");
        return new DiagnosticReport(introducedDiagnostics.Count == 0, introducedDiagnostics);
    }

    private static string DiagnosticKey(Diagnostic d)
    {
        var location = d.Location.GetLineSpan();
        return $"{d.Id}|{d.GetMessage()}|{location.Path}|{location.StartLinePosition.Line}";
    }
}
