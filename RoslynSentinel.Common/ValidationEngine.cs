using System.Diagnostics;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging;

namespace RoslynSentinel.Common;

public class ValidationEngine
{
    private readonly ILogger<ValidationEngine> _logger;
    private readonly ISolutionProvider _workspaceManager;
    private readonly DiffEngine _diffEngine;

    public ValidationEngine(ILogger<ValidationEngine> logger, ISolutionProvider workspaceManager, DiffEngine diffEngine)
    {
        _logger = logger;
        _workspaceManager = workspaceManager;
        _diffEngine = diffEngine;
    }

    public async Task<DiagnosticReport> ValidateDiffAsync(FilePath filePath, string unifiedDiff, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync(cancellationToken);
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
        CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync(cancellationToken);
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
    /// A new file (no existing Document for its path) is added into the candidate solution
    /// so it participates in compilation like any edit — this is what lets brand-new files
    /// with compile errors get caught here instead of being written to disk unvalidated.
    /// Only a new file whose containing project can't be inferred (no existing project's
    /// directory is an ancestor of its path) stays pass-through: there is no compilation to
    /// check it against.
    /// </summary>
    public static async Task<DiagnosticReport> ValidateChangesAsync(
        Solution baseline, Dictionary<FilePath, string> fileChanges, CancellationToken cancellationToken = default)
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
                var project = SolutionProjectLocator.FindContainingProject(baseline, filePath);
                if (project == null)
                {
                    Debug.WriteLine($"New file does not belong to any project, skipping in-memory validation: {filePath}");
                    continue;
                }

                var newDocumentId = DocumentId.CreateNewId(project.Id);
                candidate = candidate.AddDocument(newDocumentId, Path.GetFileName(filePath),
                    SourceText.From(newContent), filePath: filePath);
                affectedProjectIds.Add(project.Id);
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

            var baselineCompilation = await baselineProject.GetCompilationAsync(cancellationToken);
            if (baselineCompilation == null)
            {
                introducedDiagnostics.Add(new DiagnosticInfo("RS002", "Error",
                    $"Failed to create baseline compilation for project {baselineProject.Name}.", "", 0, 0, 0, 0));
                continue;
            }

            var baselineErrors = baselineCompilation
                .GetDiagnostics(cancellationToken)
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .Select(DiagnosticKey)
                .ToHashSet();

            var candidateCompilation = await candidateProject.GetCompilationAsync(cancellationToken);
            if (candidateCompilation == null)
            {
                introducedDiagnostics.Add(new DiagnosticInfo("RS002", "Error",
                    $"Failed to create candidate compilation for project {candidateProject.Name}.", "", 0, 0, 0, 0));
                continue;
            }

            foreach (var diagnostic in candidateCompilation.GetDiagnostics(cancellationToken))
            {
                if (diagnostic.Severity != DiagnosticSeverity.Error)
                {
                    continue;
                }

                Debug.WriteLine($"Found new error in project {candidateProject.Name}: {diagnostic.GetMessage()}");
                if (!baselineErrors.Contains(DiagnosticKey(diagnostic)))
                {
                    introducedDiagnostics.Add(diagnostic.ToInfo());
                }
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
