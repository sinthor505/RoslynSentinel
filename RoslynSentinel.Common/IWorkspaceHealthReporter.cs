namespace RoslynSentinel.Common;

/// <summary>Reports workspace drift, load errors, and overall health/status.</summary>
public interface IWorkspaceHealthReporter
{
    /// <summary>Clears any recorded external-file-change state, acknowledging out-of-band file changes.</summary>
    void ClearExternalFileChanges();
    /// <summary>Returns files whose on-disk content diverges from the in-memory workspace.</summary>
    Task<List<string>> GetContentExternalFileChangesAsync(CancellationToken cancellationToken = default);
    /// <summary>Returns free-form diagnostic messages about the workspace's internal state.</summary>
    [Obsolete("No production caller. Reserved for external consumers; do not add new usages.")]
    IEnumerable<string> GetDiagnostics();
    /// <summary>Returns paths detected as modified outside the sanctioned write path.</summary>
    List<string> GetExternalFileChanges();
    /// <summary>Returns structured health-component data used for status reporting.</summary>
    HealthComponents GetHealthComponents();
    /// <summary>Returns errors accumulated while loading the solution from disk.</summary>
    List<string> GetWorkspaceLoadErrors();
    /// <summary>Returns a summary of whether a solution is loaded and how large it is.</summary>
    WorkspaceStatus GetWorkspaceStatus();
    /// <summary>Returns true if a confirmed drift hit has tripped the session-wide halt latch (see
    /// docs/current/ideas/external-drift-hard-blocker.md) — every mutating call fails while true.</summary>
    bool IsSessionHalted();
    /// <summary>Out-of-band recovery: clears the session-wide halt latch after a human/operator has
    /// reviewed the drift that tripped it. Not reachable from the model's normal tool surface.</summary>
    void ClearSessionHalt();
}
