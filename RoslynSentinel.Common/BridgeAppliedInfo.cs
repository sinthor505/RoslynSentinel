namespace RoslynSentinel.Common;

// ──────────────────────────────────────────────────────────────────────────────
// Result records
// ──────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Describes a single method successfully converted to the Asyncify-bridge pattern
/// during a <c>run_bridge_batch</c> operation.
/// </summary>
/// <param name="FilePath">Absolute path of the modified source file.</param>
/// <param name="MethodName">Name of the original (now bridge-wrapper) method.</param>
/// <param name="AsyncMethodName">Name of the newly created async overload.</param>
public record BridgeAppliedInfo(
    FilePath FilePath,
    string MethodName,
    string AsyncMethodName
)
{
    /// <summary>
    /// Full source text of the file immediately before this operation was written to disk.
    /// Populated by RunBridgeBatchAsync to enable undo_last_apply via BeforeSource on
    /// OperationItemRecord. Null when pre-image capture fails or the file did not previously exist.
    /// </summary>
    public string? BeforeSource
    {
        get; init;
    }
}

