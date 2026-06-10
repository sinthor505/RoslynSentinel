namespace RoslynSentinel.Common;

/// <summary>
/// Return type for <c>get_async_migration_progress</c>.
/// Aggregated async-migration statistics for the solution or a single project.
/// </summary>
/// <param name="TotalAsyncMethods">All async or Task/ValueTask-returning methods found.</param>
/// <param name="WithCancellationToken">Subset that already have a CancellationToken parameter.</param>
/// <param name="WithoutCancellationToken">Subset that are still missing a CancellationToken parameter.</param>
/// <param name="CancellationTokenPct">Percentage of async methods that carry a CancellationToken (0–100).</param>
/// <param name="BridgeWrappers">Methods decorated with [Obsolete] where the message contains "Asyncify-bridge".</param>
/// <param name="PendingObsoleteCallers">Call sites of those bridge wrappers that still need to be migrated.</param>
/// <param name="AsyncVoidEventHandlers">Count of <c>async void</c> methods (informational — signatures are fixed).</param>
public record AsyncMigrationProgressReport(
    int TotalAsyncMethods,
    int WithCancellationToken,
    int WithoutCancellationToken,
    double CancellationTokenPct,
    int BridgeWrappers,
    int PendingObsoleteCallers,
    int AsyncVoidEventHandlers
);
// v2 — ScanOptions() now derived from SentinelScanTools.scan_descriptors (single source of truth)