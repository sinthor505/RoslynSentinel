using ModelContextProtocol;
namespace RoslynSentinel.Common;

/// <summary>The sanctioned path for loading a solution and writing changes back to disk.</summary>
public interface IWorkspaceMutator
{
    /// <summary>Writes the given file changes to disk through the shared write-path chokepoint (drift-checked, undo-tracked, retried on lock).</summary>
    Task<ApplyChangesResult> ApplyProposedChangesAsync(Dictionary<FilePath, string> changes, int retryCount = 3, bool validateChanges = false, bool rollbackOnPartialFailure = false, IProgress<ProgressNotificationValue>? progress = null, CancellationToken cancellationToken = default);
    /// <summary>Loads a solution from the given path into the workspace.</summary>
    Task LoadSolutionAsync(string solutionPath, CancellationToken cancellationToken = default);
    /// <summary>Loads a solution from the given path, resolving relative paths against baseRepoDir.</summary>
    Task LoadSolutionAsync(string solutionPath, string? baseRepoDir, CancellationToken cancellationToken = default);
    /// <summary>Removes a document from the workspace and deletes its backing file.</summary>
    Task RemoveDocumentByPathAsync(FilePath filePath, CancellationToken cancellationToken = default);
    /// <summary>Retries previously failed writes, optionally scoped to specific files.</summary>
    Task<ApplyChangesResult> RetryFailedChangesAsync(List<string>? specificFiles = null, int retryCount = 3);
}
