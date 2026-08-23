namespace RoslynSentinel.Common;

/// <summary>Reports workspace drift, load errors, and overall health/status.</summary>
public interface IWorkspaceHealthReporter
{
    /// <summary>Clears any recorded external-drift state, acknowledging out-of-band disk changes.</summary>
    void ClearDrift();
    /// <summary>Returns files whose on-disk content diverges from the in-memory workspace.</summary>
    Task<List<string>> GetContentDriftAsync(CancellationToken cancellationToken = default);
    /// <summary>Returns free-form diagnostic messages about the workspace's internal state.</summary>
    [Obsolete("No production caller. Reserved for external consumers; do not add new usages.")]
    IEnumerable<string> GetDiagnostics();
    /// <summary>Returns paths detected as modified outside the sanctioned write path.</summary>
    List<string> GetExternalDrift();
    /// <summary>Returns structured health-component data used for status reporting.</summary>
    HealthComponents GetHealthComponents();
    /// <summary>Returns errors accumulated while loading the solution from disk.</summary>
    List<string> GetWorkspaceLoadErrors();
    /// <summary>Returns a summary of whether a solution is loaded and how large it is.</summary>
    WorkspaceStatus GetWorkspaceStatus();
}
