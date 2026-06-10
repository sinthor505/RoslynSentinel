namespace RoslynSentinel.Common;

/// <summary>
/// Return type for <c>add_cancellation_token_to_method</c> when called with a known <c>autoStage</c> setting.
/// Exactly one of <see cref="ChangeId"/> or <see cref="Source"/> is non-null.
/// </summary>
/// <param name="ChangeId">
/// Set when <c>autoStage=true</c> (default). Pass to <c>apply_staged_changes</c> to write to disk,
/// or <c>get_staged_changes</c> to preview.
/// </param>
/// <param name="Source">
/// Set when <c>autoStage=false</c>. Contains the full updated source; pass to <c>apply_proposed_changes</c> to save.
/// </param>
public record CancellationTokenResult : EngineResultBase
{
    public SourceTransformResult? Source
    {
        get; init;
    }
}
// v2 — ScanOptions() now derived from SentinelScanTools.scan_descriptors (single source of truth)