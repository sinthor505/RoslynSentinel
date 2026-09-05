namespace RoslynSentinel.Common;

/// <summary>
/// Result of an attempt to apply multiple file changes to disk.
/// <para><see cref="WorkspaceInSync"/> indicates whether the in-memory workspace was
/// successfully refreshed after the write. If <c>false</c>, call <c>load_solution</c>
/// to resync before making further semantic queries.</para>
/// <para><see cref="PreImages"/> maps each file path to its content immediately before
/// the write (null if the file did not exist). Callers use this to populate
/// <see cref="OperationItemRecord.BeforeSource"/> in forensic blobs, enabling undo.</para>
/// <para><see cref="NoOpFiles"/> lists files included in <see cref="SucceededFiles"/> whose
/// proposed content was skipped rather than re-written — either byte-for-byte identical to what
/// was already on disk, or (for .cs files) semantically identical after whitespace
/// normalization. The write still succeeded: the file on disk already matches what the caller
/// proposed, so skipping the physical write avoids no-op churn/watcher noise without changing
/// the outcome. <see cref="Summary"/> reports this the same way — as a success, not a failure or
/// partial write — to avoid misleading callers into thinking their change didn't take effect.</para>
/// </summary>
public record ApplyChangesResult(
    bool Success,
    List<string> SucceededFiles,
    Dictionary<FilePath, string> FailedFiles,
    string Summary,
    bool WorkspaceInSync = false,
    int WorkspaceVersion = 0,
    IReadOnlyDictionary<string, string?>? PreImages = null,
    DiagnosticReport? ValidationResult = null,
    List<string>? RolledBackFiles = null,
    List<string>? NoOpFiles = null
);
