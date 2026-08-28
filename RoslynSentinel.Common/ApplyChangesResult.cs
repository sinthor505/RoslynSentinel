namespace RoslynSentinel.Common;

/// <summary>
/// Result of an attempt to apply multiple file changes to disk.
/// <para><see cref="WorkspaceInSync"/> indicates whether the in-memory workspace was
/// successfully refreshed after the write. If <c>false</c>, call <c>load_solution</c>
/// to resync before making further semantic queries.</para>
/// <para><see cref="PreImages"/> maps each file path to its content immediately before
/// the write (null if the file did not exist). Callers use this to populate
/// <see cref="OperationItemRecord.BeforeSource"/> in forensic blobs, enabling undo.</para>
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
    List<string>? RolledBackFiles = null
);
