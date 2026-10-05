namespace RoslynSentinel.Common;

/// <summary>The sanctioned path for loading a solution and writing changes back to disk.</summary>
public interface IWorkspaceMutator
{
    /// <summary>
    /// Writes the given file changes to disk through the shared write-path chokepoint (drift-checked, undo-tracked, retried on lock).
    /// <paramref name="exactRestore"/> is for restoring a file to a pre-image the server itself captured earlier (UndoLastApply): the
    /// content is written byte for byte, bypassing the EOL-change guard, EOL normalization and the whitespace-only no-op skip, because
    /// a restore to the captured pre-image is intended by definition. Normal edits must leave it false.
    /// </summary>
    Task<ApplyChangesResult> ApplyProposedChangesAsync(Dictionary<FilePathWrapper, string> changes, int retryCount = 3, bool validateChanges = false, bool rollbackOnPartialFailure = false, IProgress<EngineProgress>? progress = null, CancellationToken cancellationToken = default, IReadOnlyCollection<FilePathWrapper>? deletePaths = null, bool exactRestore = false);
    /// <summary>Loads a solution from the given path into the workspace.</summary>
    Task LoadSolutionAsync(string solutionPath, CancellationToken cancellationToken = default);
    /// <summary>Loads a solution from the given path, resolving relative paths against baseRepoDir.</summary>
    Task LoadSolutionAsync(string solutionPath, string? baseRepoDir, CancellationToken cancellationToken = default);
    /// <summary>Removes a document from the workspace and deletes its backing file.</summary>
    Task RemoveDocumentByPathAsync(FilePathWrapper filePath, CancellationToken cancellationToken = default);
    /// <summary>Retries previously failed writes, optionally scoped to specific files.</summary>
    Task<ApplyChangesResult> RetryFailedChangesAsync(List<string>? specificFiles = null, int retryCount = 3, CancellationToken cancellationToken = default);

    /// <summary>
    /// Caches a changeset that <c>ApplyDiff</c>'s whole-file-rewrite size guard rejected, under a
    /// fresh confirmation code, so a caller that intended the large rewrite can replay just the
    /// code via <see cref="TakePendingChangeset"/> instead of resending file content.
    /// </summary>
    string CachePendingChangeset(Dictionary<FilePathWrapper, string> changes, int retryCount, bool validateOnApply);

    /// <summary>
    /// Retrieves and removes (one-time use) the changeset cached under <paramref name="confirmationCode"/>
    /// by <see cref="CachePendingChangeset"/>. Returns null if the code is unrecognized or expired.
    /// </summary>
    (Dictionary<FilePathWrapper, string> Changes, int RetryCount, bool ValidateOnApply)? TakePendingChangeset(string confirmationCode);
}
