using Microsoft.CodeAnalysis;

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

    public async Task<EngineResult<DiagnosticSummary>> GetFileDiagnosticsAsync(FilePath filePath, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault();
        if (document == null)
        {
            throw new FileNotFoundException($"File not found: {filePath}");
        }

        var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
        var diagnostics = semanticModel?.GetDiagnostics(null, cancellationToken) ?? Enumerable.Empty<Diagnostic>();

        var list = diagnostics.Select(d => d.ToInfo()).ToList();
        return new EngineResult<DiagnosticSummary>(EngineOutcome.Success, new DiagnosticSummary(
            list.Count(d => d.Severity == "Error"),
            list.Count(d => d.Severity == "Warning"),
            list
        ));
    }

    public async Task<EngineResult<DiagnosticSummary>> GetProjectDiagnosticsAsync(string projectName, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var project = solution.Projects.FirstOrDefault(p => p.Name == projectName);
        if (project == null)
        {
            throw new InvalidOperationException("Project not found.");
        }

        var compilation = await project.GetCompilationAsync(cancellationToken);
        var diagnostics = compilation?.GetDiagnostics(cancellationToken) ?? Enumerable.Empty<Diagnostic>();

        var list = diagnostics.Select(d => d.ToInfo()).ToList();
        return new EngineResult<DiagnosticSummary>(EngineOutcome.Success, new DiagnosticSummary(
            list.Count(d => d.Severity == "Error"),
            list.Count(d => d.Severity == "Warning"),
            list
        ));
    }

    // Diagnostic IDs that are false positives in Blazor/Razor projects because Roslyn's
    // static workspace doesn't run the Razor source generator (which produces App, Routes, etc.)
    private static readonly HashSet<string> _blazorGeneratorFalsePositiveIds =
        new(StringComparer.OrdinalIgnoreCase) { "CS0234", "CS0246", "CS0103" };

    private static bool IsBlazorProject(Project project) =>
        project.Name.Contains("Blazor", StringComparison.OrdinalIgnoreCase) ||
        project.Name.Contains("Razor", StringComparison.OrdinalIgnoreCase) ||
        project.Documents.Any(d =>
            d.FilePath.EndsWith(".razor.cs", StringComparison.OrdinalIgnoreCase) == true);

    private static int SeverityRank(DiagnosticInfo d) => d.Severity switch
    {
        "Error" => 0,
        "Warning" => 1,
        _ => 2
    };

    /// <summary>
    /// Returns solution-wide diagnostics, capped at <paramref name="maxDetails"/> items in the
    /// detail list (counts are always exact). Errors always appear before warnings regardless of
    /// the cap. File paths are made relative to the solution root for compact output.
    /// Filters Blazor source-generator false positives (CS0234/CS0246/CS0103).
    /// </summary>
    public async Task<EngineResult<DiagnosticSummary>> GetSolutionDiagnosticsAsync(
        int maxDetails = 50,
        CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var solutionDir = Path.GetDirectoryName(solution.FilePath ?? "") ?? "";
        var allDiagnostics = new List<DiagnosticInfo>();

        foreach (var project in solution.Projects)
        {
            var compilation = await project.GetCompilationAsync(cancellationToken);
            if (compilation == null)
            {
                continue;
            }

            bool isBlazor = IsBlazorProject(project);
            var diagnostics = compilation.GetDiagnostics(cancellationToken)
                .Where(d => d.Severity != DiagnosticSeverity.Hidden)
                .Where(d => !(isBlazor && _blazorGeneratorFalsePositiveIds.Contains(d.Id)));

            allDiagnostics.AddRange(diagnostics.Select(d => d.ToInfo().WithRelativePath(solutionDir)));
        }

        int totalErrors = allDiagnostics.Count(d => d.Severity == "Error");
        int totalWarnings = allDiagnostics.Count(d => d.Severity == "Warning");

        // Sort errors first so the cap never hides an error behind warnings.
        var sorted = allDiagnostics.OrderBy(SeverityRank).ToList();

        var details = sorted.Count > maxDetails
            ? sorted.Take(maxDetails).ToList()
            : sorted;

        if (sorted.Count > maxDetails)
        {
            details.Add(new DiagnosticInfo(
                "SENTINEL-TRUNCATED", "Info",
                $"Showing {maxDetails} of {sorted.Count} diagnostics (errors first). " +
                $"Call with maxDetails={sorted.Count} or use get_project_diagnostics for full detail.",
                "", 0, 0, 0, 0));
        }

        return new EngineResult<DiagnosticSummary>(EngineOutcome.Success, new DiagnosticSummary(totalErrors, totalWarnings, details));
    }
}
