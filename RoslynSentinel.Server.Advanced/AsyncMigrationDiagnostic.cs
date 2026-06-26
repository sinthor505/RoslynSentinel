using RoslynSentinel.Common;

namespace RoslynSentinel.Server.Advanced;

/// <summary>
/// Analyses the item-level results of any async-migration batch tool and produces plain-English
/// diagnostic suggestions. Intended to be called whenever succeeded==0 or failed&gt;0 so that
/// agents receive actionable guidance without having to reason over raw counts themselves.
///
/// Design: all batch tools write phase-prefixed Reason strings to their items list
/// ("phase:flag — …", "phase:bridge — …", "phase:uplift — …", "phase:propagate_ct — …").
/// The analyser uses these prefixes to route items to phase-specific checks, making the same
/// method safe to call from any tool regardless of which phases ran.
/// </summary>
internal static class AsyncMigrationDiagnostic
{
    /// <summary>
    /// Analyses operation items produced by an async-migration tool and returns a list of
    /// actionable suggestions, or null when nothing noteworthy was detected.
    /// </summary>
    /// <param name="succeeded">Succeeded count from the tool run.</param>
    /// <param name="failed">Failed count from the tool run.</param>
    /// <param name="changeId">ChangeId for the operation blob (embedded in suggestions).</param>
    /// <param name="items">Full item list written to the blob — may be large, scanning is cheap.</param>
    internal static List<string>? Analyse(
        int succeeded,
        int failed,
        string changeId,
        IReadOnlyList<OperationItemRecord> items)
    {
        if (succeeded > 0 && failed == 0)
            return null;

        var suggestions = new List<string>();

        AnalyseFlagPhase(items, suggestions);
        AnalyseBridgePhase(items, changeId, suggestions);
        AnalyseUpliftPhase(items, changeId, suggestions);
        AnalyseCtPhase(items, changeId, suggestions);

        // Generic fallback: unclassified failures not already mentioned.
        if (failed > 0 && !suggestions.Exists(s => s.Contains("failure", StringComparison.OrdinalIgnoreCase)
                                                  || s.Contains("error", StringComparison.OrdinalIgnoreCase)))
        {
            suggestions.Add(
                $"{failed} failure(s) recorded. " +
                $"Use GetOperationDetail(changeId=\"{changeId}\", filter=\"failures\") for details.");
        }

        return suggestions.Count > 0 ? suggestions : null;
    }

    // ── Phase analysers ────────────────────────────────────────────────────────

    private static void AnalyseFlagPhase(IReadOnlyList<OperationItemRecord> items, List<string> suggestions)
    {
        var flagItems = items
            .Where(i => i.Reason?.StartsWith("phase:flag", StringComparison.OrdinalIgnoreCase) == true)
            .ToList();

        if (flagItems.Count == 0) return;

        int belowThreshold = flagItems.Count(i =>
            i.Reason!.Contains("below minScore", StringComparison.OrdinalIgnoreCase));
        int alreadyFlagged = flagItems.Count(i =>
            i.Reason!.Contains("already flagged", StringComparison.OrdinalIgnoreCase));

        if (belowThreshold > 0 && alreadyFlagged == 0)
        {
            suggestions.Add(
                $"{belowThreshold} method(s) were scanned but scored below the minScore threshold — " +
                "they were not flagged. Lower minScore or run ScanAsyncMigrationCandidates with a " +
                "higher minScore to find additional candidates.");
        }

        if (alreadyFlagged > 0)
        {
            suggestions.Add(
                $"{alreadyFlagged} method(s) were skipped in the flag phase because they already " +
                "carry a [MigrationCandidate] attribute. Run Asyncify to process them.");
        }
    }

    private static void AnalyseBridgePhase(
        IReadOnlyList<OperationItemRecord> items,
        string changeId,
        List<string> suggestions)
    {
        var bridgeItems = items
            .Where(i => i.Reason?.StartsWith("phase:bridge", StringComparison.OrdinalIgnoreCase) == true)
            .ToList();

        if (bridgeItems.Count == 0) return;

        // Already bridged — async overload with CT exists, sync wrapper is stale.
        var alreadyBridged = bridgeItems
            .Where(i => i.Reason!.Contains("already has CancellationToken", StringComparison.OrdinalIgnoreCase))
            .ToList();

        // Methods flagged NeedsManualReview by a prior Asyncify run.
        var priorManualReview = bridgeItems
            .Where(i => i.Outcome == OperationOutcome.Skipped
                     && i.Reason!.Contains("NeedsManualReview", StringComparison.OrdinalIgnoreCase)
                     && !i.Reason!.Contains("already has CancellationToken", StringComparison.OrdinalIgnoreCase))
            .ToList();

        // Methods newly flagged NeedsManualReview this run — bridge produced compiler errors.
        var newCompilerErrors = bridgeItems
            .Where(i => i.Outcome == OperationOutcome.Skipped
                     && i.Reason!.Contains("Validation produced", StringComparison.OrdinalIgnoreCase))
            .ToList();

        // Event handlers — structurally cannot be auto-converted.
        var eventHandlers = bridgeItems
            .Where(i => i.Reason!.Contains("event handler", StringComparison.OrdinalIgnoreCase))
            .ToList();

        // Unexpected bridge failures (pre-condition exceptions, etc.).
        var unexpectedFailures = bridgeItems
            .Where(i => i.Outcome == OperationOutcome.Failed)
            .ToList();

        if (alreadyBridged.Count > 0)
        {
            suggestions.Add(
                $"{alreadyBridged.Count} method(s) skipped — async overload already exists with CancellationToken. " +
                "These sync wrappers have already been bridged and the [MigrationCandidate(\"AsyncBridgeCandidate\")] " +
                "attribute is stale. Run ScanAsyncMigrationCandidates to refresh the candidate list.");
        }

        if (priorManualReview.Count > 0)
        {
            var sample = MethodSample(priorManualReview);
            suggestions.Add(
                $"{priorManualReview.Count} method(s) were previously marked NeedsManualReview — " +
                "a prior Asyncify run attempted bridging but encountered compiler errors, meaning no " +
                $"async API equivalent was found. Sample: {sample}. " +
                "Use GetMethodSource on these methods to review the synchronous calls that block " +
                "automatic conversion.");
        }

        if (newCompilerErrors.Count > 0)
        {
            var sample = MethodSample(newCompilerErrors);
            suggestions.Add(
                $"{newCompilerErrors.Count} method(s) flagged NeedsManualReview this run — " +
                "the bridge produced compiler errors (likely no async API equivalent for the sync " +
                $"calls they make). Sample: {sample}. " +
                $"Use GetOperationDetail(changeId=\"{changeId}\", filter=\"skipped\") for the full " +
                "per-method compiler diagnostic list.");
        }

        if (eventHandlers.Count > 0)
        {
            suggestions.Add(
                $"{eventHandlers.Count} event handler(s) skipped — event handlers cannot be converted " +
                "automatically because their signature is fixed by the delegate contract. " +
                "Use ExtractEventHandlers to separate the business logic into an async method, " +
                "then bridge that method instead.");
        }

        if (unexpectedFailures.Count > 0)
        {
            var sample = MethodSample(unexpectedFailures);
            suggestions.Add(
                $"{unexpectedFailures.Count} bridge item(s) failed with unexpected errors (pre-condition " +
                $"or runtime exceptions). Sample: {sample}. " +
                $"Use GetOperationDetail(changeId=\"{changeId}\", filter=\"failures\") to see details.");
        }
    }

    private static void AnalyseUpliftPhase(
        IReadOnlyList<OperationItemRecord> items,
        string changeId,
        List<string> suggestions)
    {
        var upliftItems = items
            .Where(i => i.Reason?.StartsWith("phase:uplift", StringComparison.OrdinalIgnoreCase) == true)
            .ToList();

        if (upliftItems.Count == 0) return;

        var upliftErrors = upliftItems
            .Where(i => i.Outcome is OperationOutcome.Skipped or OperationOutcome.Failed
                     && i.Reason!.Contains("NeedsManualReview", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var noCallSites = upliftItems
            .Where(i => i.Reason!.Contains("no eligible call sites", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (upliftErrors.Count > 0)
        {
            var sample = MethodSample(upliftErrors);
            suggestions.Add(
                $"{upliftErrors.Count} caller(s) could not be uplifted — compiler errors during " +
                $"caller async conversion. Sample: {sample}. " +
                $"Use GetOperationDetail(changeId=\"{changeId}\", filter=\"skipped\") for compiler diagnostics.");
        }

        if (noCallSites.Count > 0)
        {
            suggestions.Add(
                $"{noCallSites.Count} file(s) had no eligible call sites for uplift — the bridged " +
                "method may not be called from these files, or the callers are already async.");
        }
    }

    private static void AnalyseCtPhase(
        IReadOnlyList<OperationItemRecord> items,
        string changeId,
        List<string> suggestions)
    {
        var ctItems = items
            .Where(i => i.Reason?.StartsWith("phase:propagate_ct", StringComparison.OrdinalIgnoreCase) == true)
            .ToList();

        if (ctItems.Count == 0) return;

        var ctFailures = ctItems
            .Where(i => i.Outcome is OperationOutcome.Failed)
            .ToList();

        if (ctFailures.Count > 0)
        {
            suggestions.Add(
                $"{ctFailures.Count} CancellationToken propagation failure(s). " +
                $"Use GetOperationDetail(changeId=\"{changeId}\", filter=\"failures\") for details.");
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string MethodSample(List<OperationItemRecord> items, int take = 3)
    {
        var names = items
            .Take(take)
            .Select(i => i.MethodName is { Length: > 0 } m
                ? $"{System.IO.Path.GetFileName(i.FilePath)}/{m}"
                : System.IO.Path.GetFileName(i.FilePath))
            .ToList();
        return names.Count == 0 ? "(none)" : string.Join(", ", names);
    }
}
