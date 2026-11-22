namespace RoslynSentinel.Common;

/// <summary>
/// Result of an attempt to apply multiple file changes to disk.
/// <para><see cref="WorkspaceInSync"/> indicates whether the in-memory workspace was
/// successfully refreshed after the write. If <c>false</c>, call <c>load_solution</c>
/// to resync before making further semantic queries.</para>
/// <para><see cref="PreImages"/> maps each file path to its content immediately before
/// the write (null if the file did not exist). Callers use this to populate
/// <see cref="OperationItemRecord.BeforeSource"/> in forensic blobs, enabling undo.</para>
/// <para><see cref="PostImages"/> maps each file the chokepoint actually wrote to disk to the exact text it wrote
/// (after the EOL pre-pass, so it can differ from the proposed text). No-op writes, deleted files and files rolled back
/// after a partial failure have no entry. Null on refusals. In-process only: never serialised to tool output, so
/// callers read it when building operation blobs (<see cref="OperationItemRecord.AfterSource"/>).</para>
/// <para><see cref="CreatedFiles"/> lists files the chokepoint created on this call: no file existed before, the pre-image read did not fail, and the write landed. Null on refusals, empty when none. Keys match <see cref="SucceededFiles"/> entries.</para>
/// <para><see cref="NoOpFiles"/> lists files included in <see cref="SucceededFiles"/> whose
/// proposed content was skipped rather than re-written -> either byte-for-byte identical to what
/// was already on disk, or (for .cs files) semantically identical after whitespace
/// normalization. The write still succeeded: the file on disk already matches what the caller
/// proposed, so skipping the physical write avoids no-op churn/watcher noise without changing
/// the outcome. <see cref="Summary"/> reports this the same way -> as a success, not a failure or
/// partial write -> to avoid misleading callers into thinking their change didn't take effect.</para>
/// <para><see cref="Findings"/> carries non-fatal observations from the write chokepoint (for example a
/// pre-image that was written outside the chokepoint with the wrong line endings). Null when there are none.</para>
/// <para><see cref="NeedsFullReload"/> is <c>true</c> when a structural file in the write set (project, props, targets) queued a
/// full workspace reload instead of the per-document fold; <see cref="WorkspaceInSync"/> is then <c>false</c> until the reload
/// lands. Omitted from the wire when <c>false</c>.</para>
/// </summary>
public record ApplyChangesResult(
    bool IsError,
    List<string> SucceededFiles,
    Dictionary<FilePathWrapper, string> FailedFiles,
    string Summary,
    bool WorkspaceInSync = false,
    int WorkspaceVersion = 0,
    IReadOnlyDictionary<string, string?>? PreImages = null,
    DiagnosticReport? ValidationResult = null,
    List<string>? RolledBackFiles = null,
    List<string>? NoOpFiles = null,
    string? RefusalCode = null,
    List<Finding>? Findings = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)] bool NeedsFullReload = false,
    [property: System.Text.Json.Serialization.JsonIgnore] IReadOnlyDictionary<string, string?>? PostImages = null,
    [property: System.Text.Json.Serialization.JsonIgnore] IReadOnlyList<string>? CreatedFiles = null
) : IOperationResult
{
    [System.Text.Json.Serialization.JsonIgnore]
    public ResultStatus Status => ResultStatusExtensions.FromFlag(IsError, Code);

    /// <summary>Failure code: <see cref="RefusalCode"/> when the write chokepoint refused, <see cref="ToolErrorCode.ValidationFailed"/> when the compile gate rejected the change (ValidationResult set), else Exception. Null on success.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string? Code => IsError ? RefusalCode ?? (ValidationResult is not null ? ToolErrorCode.ValidationFailed : ToolErrorCode.Exception) : null;

    [System.Text.Json.Serialization.JsonIgnore]
    public string? Message => IsError ? Summary : null;
}
