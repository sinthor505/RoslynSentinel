namespace RoslynSentinel.Common;

/// <summary>
/// Return type for <c>apply_cancellation_token_to_file</c>.
/// Reports which methods were modified and whether the file was written to disk.
/// </summary>
/// <param name="ModifiedMethods">Names of methods that received the new CancellationToken parameter.</param>
/// <param name="SkippedMethods">
/// Names of async/Task-returning methods that were skipped for a specific reason
/// (already had CT, abstract, event handler, or not in the requested <c>methodNames</c> filter).
/// Sync methods are silently excluded and do not appear here.
/// </param>
/// <param name="TotalModified">Count of modified methods.</param>
/// <param name="WroteToFile"><c>true</c> if the updated source was written to disk successfully.</param>
/// <param name="WorkspaceInSync">
/// <c>true</c> if the in-memory workspace was successfully updated after writing.
/// If <c>false</c>, call <c>load_solution</c> before running further semantic analysis.
/// </param>
/// <param name="WorkspaceVersion">Monotonically increasing workspace version counter; increments on every successful workspace refresh.</param>
public record ApplyCancellationTokenToFileResult(
    List<string> ModifiedMethods,
    List<string> SkippedMethods,
    int TotalModified,
    bool WroteToFile,
    bool WorkspaceInSync = false,
    int WorkspaceVersion = 0
);
// v2 — ScanOptions() now derived from SentinelScanTools.scan_descriptors (single source of truth)