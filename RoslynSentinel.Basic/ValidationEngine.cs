using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging;

using RoslynSentinel.Common;

namespace RoslynSentinel.Basic;

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
            return await ValidateChangesAsync(new Dictionary<FilePath, string> { { filePath, newText.ToString() } }, cancellationToken);
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
    /// Validates proposed file changes by compiling each affected project in-memory and
    /// returning only NEWLY INTRODUCED errors — i.e. errors present in the candidate
    /// compilation that were not already present in the baseline (unmodified) compilation.
    ///
    /// This delta approach means a project with pre-existing errors does not block a
    /// clean edit, and IsValid=false reliably means "your change broke something new."
    ///
    /// Files not found in the solution (RS001) are treated as pass-through: the tool
    /// cannot validate new files in-memory, so it allows them rather than blocking.
    /// </summary>
    public async Task<DiagnosticReport> ValidateChangesAsync(Dictionary<FilePath, string> fileChanges, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var candidateSolution = solution;
        var affectedProjectIds = new HashSet<ProjectId>();

        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation("Applying {Count} file changes to in-memory solution fork...", fileChanges.Count);
        }

        foreach (var change in fileChanges)
        {
            var filePath = change.Key;
            var newContent = change.Value;
            var documentId = solution.GetDocumentIdsWithFilePath(filePath).FirstOrDefault();

            if (documentId == null)
            {
                // File not found in solution — cannot validate in-memory (new file or not in .csproj).
                // Treat as pass-through rather than blocking a legitimate add-file operation.
                if (_logger.IsEnabled(LogLevel.Information))
                {
                    _logger.LogInformation("File not found in solution, skipping in-memory validation: {FilePath}", filePath);
                }
                continue;
            }

            candidateSolution = candidateSolution.WithDocumentText(documentId, SourceText.From(newContent));
            affectedProjectIds.Add(documentId.ProjectId);
        }

        // If no files could be mapped to solution documents, nothing to validate.
        if (affectedProjectIds.Count == 0)
        {
            return new DiagnosticReport(true, new List<DiagnosticInfo>());
        }

        var introducedDiagnostics = new List<DiagnosticInfo>();

        foreach (var projectId in affectedProjectIds)
        {
            var baselineProject = solution.GetProject(projectId)!;
            var candidateProject = candidateSolution.GetProject(projectId)!;

            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation("Compiling project {ProjectName} (baseline + candidate)...", baselineProject.Name);
            }

            // ApiBaseline compile — errors that already exist before our changes.
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
                .Select(d => DiagnosticKey(d))
                .ToHashSet();

            // Candidate compile — errors after applying the proposed changes.
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

                // Only report errors that are NEW — not present in the baseline.
                if (!baselineErrors.Contains(DiagnosticKey(diagnostic)))
                {
                    introducedDiagnostics.Add(diagnostic.ToInfo());
                }
            }
        }

        return new DiagnosticReport(introducedDiagnostics.Count == 0, introducedDiagnostics);
    }

    /// <summary>
    /// Produces a stable key for deduplicating diagnostics across baseline and candidate
    /// compilations. Uses diagnostic ID + message + file + line so that the same logical
    /// error at the same location matches across two independent compiles. Column is
    /// intentionally excluded — reformatting can shift columns without changing the error.
    /// </summary>
    private static string DiagnosticKey(Diagnostic d)
    {
        var location = d.Location.GetLineSpan();
        return $"{d.Id}|{d.GetMessage()}|{location.Path}|{location.StartLinePosition.Line}";
    }
}
