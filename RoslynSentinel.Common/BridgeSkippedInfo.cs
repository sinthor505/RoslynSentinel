namespace RoslynSentinel.Common;

/// <summary>
/// Describes a method that could not be bridged during a <c>run_bridge_batch</c> operation.
/// The method has been flagged with <c>[MigrationCandidate("NeedsManualReview")]</c>.
/// </summary>
/// <param name="FilePath">Absolute path of the source file.</param>
/// <param name="MethodName">Name of the method that was not bridged.</param>
/// <param name="Reason">Human-readable reason for skipping.</param>
/// <param name="Diagnostics">Roslyn compiler diagnostics that caused the skip (may be empty).</param>
public record BridgeSkippedInfo(
    FilePath FilePath,
    string MethodName,
    string Reason,
    List<string> Diagnostics
);

