namespace RoslynSentinel.Common;

/// <summary>Selects whether ApplyProposedChangesAsync checks writes against the calling agent's observed-content ledger.</summary>
public enum StaleReadPolicy
{
    /// <summary>No stale-read check (default; every existing caller).</summary>
    None,
    /// <summary>
    /// Refuses the whole batch, non-latching, with <c>ToolErrorCode.StaleRead</c> when a write or delete target has an
    /// observed-content-ledger entry for the calling agent whose hash differs from the current in-memory text or the disk text.
    /// </summary>
    RefuseIfObservedStale,
    /// <summary>Same refusal as RefuseIfObservedStale; does not report unobserved targets as a finding. For engine tools that edit many files.</summary>
    RefuseIfObservedStaleQuiet,
}

/// <summary>The sanctioned path for loading a solution and writing changes back to disk.</summary>
public interface IWorkspaceMutator : IEolResolver
{
    /// <summary>
    /// Writes the given file changes to disk through the shared write-path chokepoint (drift-checked, undo-tracked, retried on lock).
    /// <paramref name="exactRestore"/> is for restoring a file to a pre-image the server itself captured earlier (UndoLastApply): the
    /// content is written byte for byte, bypassing the EOL-change guard, EOL normalization and the whitespace-only no-op skip, because
    /// a restore to the captured pre-image is intended by definition. Normal edits must leave it false. <paramref name="staleReadPolicy"/> follows operationLabel; <see cref="StaleReadPolicy.None"/> (default) skips the stale-read check.
    /// </summary>
    Task<ApplyChangesResult> ApplyProposedChangesAsync(Dictionary<FilePathWrapper, string> changes, int retryCount = 3, bool validateChanges = false, bool rollbackOnPartialFailure = false, IProgress<EngineProgress>? progress = null, CancellationToken cancellationToken = default, IReadOnlyCollection<FilePathWrapper>? deletePaths = null, bool exactRestore = false, string? operationLabel = null, StaleReadPolicy staleReadPolicy = StaleReadPolicy.None); // operationLabel follows exactRestore, not the CancellationToken: the token is already not last here and moving it would break callers. staleReadPolicy follows operationLabel for the same reason.
    /// <summary>Loads a solution from the given path into the workspace.</summary>
    Task LoadSolutionAsync(string solutionPath, CancellationToken cancellationToken = default);
    /// <summary>Loads a solution from the given path, resolving relative paths against baseRepoDir.</summary>
    Task LoadSolutionAsync(string solutionPath, string? baseRepoDir, CancellationToken cancellationToken = default);
    /// <summary>
    /// Loads a solution from the given path, resolving relative paths against baseRepoDir. <paramref name="timeout"/> covers waiting
    /// for the workspace lock and the load itself; on expiry the load is cancelled and <see cref="SolutionLoadTimeoutException"/> is thrown.
    /// </summary>
    Task LoadSolutionAsync(string solutionPath, string? baseRepoDir, TimeSpan timeout, CancellationToken cancellationToken = default);
    /// <summary>Removes a document from the workspace and deletes its backing file.</summary>
    Task RemoveDocumentByPathAsync(FilePathWrapper filePath, CancellationToken cancellationToken = default);
    /// <summary>Retries previously failed writes, optionally scoped to specific files.</summary>
    Task<ApplyChangesResult> RetryFailedChangesAsync(List<string>? specificFiles = null, int retryCount = 3, CancellationToken cancellationToken = default);

    /// <summary>
    /// Read-path fold for one file: when its in-memory text differs from disk, folds the disk text in under _solutionLock and the
    /// cross-process write lock (FoldOnReadWait, fails open when busy). Never throws on IO errors. Lock rules: (a) _solutionLock first,
    /// then the OS write lock, never the reverse; (b) the OS lock is not re-entrant, so it is taken only at this entry point, never inside
    /// ApplyInMemoryDocumentUpdatesAsync and never from a region already holding it; (c) never held across compile validation or a full MSBuild reload.
    /// </summary>
    Task SyncFileFromDiskIfStaleAsync(FilePathWrapper path, CancellationToken ct);

    /// <summary>
    /// Folds every queued external change into the in-memory workspace, with the same lock rules (a)-(c) as SyncFileFromDiskIfStaleAsync.
    /// Anything needing a full reload, a watcher overflow, a busy lock or a missing solution stays queued for the debounce handler. Never throws on IO errors.
    /// </summary>
    Task FlushPendingFoldsAsync(CancellationToken ct);

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
