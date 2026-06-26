using System.Diagnostics;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.Extensions.Logging;

namespace RoslynSentinel.Advanced;

// ──────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Describes a caller successfully uplifted to use the async overload during a
/// <c>run_uplift_batch</c> operation.
/// </summary>
/// <param name="FilePath">Absolute path of the modified source file.</param>
/// <param name="CallerMethod">Name of the original (now bridge-wrapper) caller method.</param>
/// <param name="CallerAsyncMethod">Name of the new async caller overload.</param>
public record UpliftCallerInfo(
    FilePath FilePath,
    string CallerMethod,
    string CallerAsyncMethod
)
{
    /// <summary>
    /// Full source text of the file immediately before this uplift was written to disk.
    /// Populated by RunUpliftBatchAsync to enable undo_last_apply via BeforeSource on
    /// OperationItemRecord. Null when pre-image capture fails or the file did not previously exist.
    /// </summary>
    public string? BeforeSource
    {
        get; init;
    }
}

/// <summary>
/// Describes a caller that could not be uplifted during a <c>run_uplift_batch</c> operation.
/// </summary>
/// <param name="FilePath">Absolute path of the source file.</param>
/// <param name="CallerMethod">Name of the caller method that was not uplifted.</param>
/// <param name="Reason">Human-readable reason for skipping.</param>
/// <param name="Diagnostics">Roslyn compiler diagnostics that caused the skip (may be empty).</param>
public record UpliftSkippedInfo(
    FilePath FilePath,
    string CallerMethod,
    string Reason,
    List<string> Diagnostics
);

/// <summary>
/// Aggregate result of a <c>run_uplift_batch</c> call.
/// </summary>
/// <param name="Uplifted">Callers that were successfully bridged and written to disk.</param>
/// <param name="Skipped">
/// Callers that were skipped (pre-condition failure or compiler errors after transform).
/// Each skipped caller is flagged <c>[MigrationCandidate("NeedsManualReview")]</c> where possible.
/// </param>
/// <param name="RemainingCallers">
/// Number of callers of the bridged method still pending after this batch.
/// </param>
/// <param name="StopReason">
/// Why the batch ended:
/// <list type="bullet">
///   <item><c>batch_complete</c> — all discovered callers were processed.</item>
///   <item><c>budget_exhausted</c> — <c>maxCallers</c> limit reached before all callers processed.</item>
///   <item><c>no_callers</c> — no callers of the bridged method found.</item>
///   <item><c>dry_run</c> — dry-run mode; no files written.</item>
/// </list>
/// </param>
public record UpliftBatchResult(
    List<UpliftCallerInfo> Uplifted,
    List<UpliftSkippedInfo> Skipped,
    int RemainingCallers,
    string StopReason
);

// ──────────────────────────────────────────────────────────────────────────────
// UpliftBatchMulti types
// ──────────────────────────────────────────────────────────────────────────────

/// <summary>A single bridged-method target for <c>run_uplift_batch_multi</c>.</summary>
public class UpliftBatchMultiTarget
{
    /// <summary>Name of the Asyncify-bridge sync method whose callers should be uplifted.</summary>
    public string BridgedMethodName { get; set; } = "";
    /// <summary>Optional project name to restrict the caller scan. <c>null</c> = entire solution.</summary>
    public string? ProjectName { get; set; }
    /// <summary>Optional Roslyn documentation-comment ID. When set, only callers of this exact overload are uplifted.</summary>
    public string? SymbolId { get; set; }
}

/// <summary>Input for <c>run_uplift_batch_multi</c>.</summary>
public class UpliftBatchMultiInput
{
    /// <summary>List of bridged methods to uplift, each processed as a separate <c>run_uplift_batch</c> call.</summary>
    public List<UpliftBatchMultiTarget> Targets { get; set; } = new();
    /// <summary>Maximum callers to process per bridged method (forwarded to each inner batch). Default 10.</summary>
    public int MaxCallersPerMethod { get; set; } = 10;
    /// <summary>When <c>true</c>, reports what would happen without writing files. Default <c>false</c>.</summary>
    public bool DryRun { get; set; } = false;
    /// <summary>When <c>true</c> (default), propagates CT to other async callees in each uplifted method.</summary>
    public bool PropagateCancellationTokens { get; set; } = true;
}

/// <summary>Per-method result within <c>UpliftBatchMultiResult</c>.</summary>
public class UpliftBatchMultiMethodResult
{
    public string BridgedMethodName { get; set; } = "";
    public string? ProjectName
    {
        get; set;
    }
    public UpliftBatchResult Result { get; set; } = new(new(), new(), 0, "");
    public string? Error
    {
        get; set;
    }
}

/// <summary>Aggregate result of <c>run_uplift_batch_multi</c>.</summary>
public class UpliftBatchMultiResult
{
    public List<UpliftBatchMultiMethodResult> PerMethod { get; set; } = new();
    public int TotalUplifted
    {
        get; set;
    }
    public int TotalSkipped
    {
        get; set;
    }
    public int TotalRemainingCallers
    {
        get; set;
    }
    /// <summary>"batch_complete" | "dry_run"</summary>
    public string StopReason { get; set; } = "";
}

// ──────────────────────────────────────────────────────────────────────────────
// PropagateCtResult types
// ──────────────────────────────────────────────────────────────────────────────

/// <summary>Describes a single call site that was rewritten to forward a CancellationToken.</summary>
public class CallSiteForward
{
    public string CalleeMethod { get; set; } = "";
    public string CalleeType { get; set; } = "";
    public int Line
    {
        get; set;
    }
    public string BeforeSnippet { get; set; } = "";
    public string AfterSnippet { get; set; } = "";
}

/// <summary>Describes a call site that was skipped during CancellationToken propagation.</summary>
public class CallSiteSkip
{
    public string CalleeMethod { get; set; } = "";
    public int Line
    {
        get; set;
    }
    /// <summary>
    /// One of: "AlreadyForwarded", "NoCancellationTokenOverload", "AmbiguousOverload",
    /// "NamedArgumentCollision", "CalleeNotAsync".
    /// </summary>
    public string Reason { get; set; } = "";
}

/// <summary>Result of a single-method CancellationToken propagation operation.</summary>
public class PropagateCtResult
{
    public string? ChangeId
    {
        get; set;
    }
    public string MethodName { get; set; } = "";
    public string TokenParameterName { get; set; } = "";
    public List<CallSiteForward> Forwarded { get; set; } = new();
    public List<CallSiteSkip> Skipped { get; set; } = new();
    public int ForwardedCount
    {
        get; set;
    }
    public int SkippedCount
    {
        get; set;
    }
    public bool MethodFound
    {
        get; set;
    }
    public string? Error
    {
        get; set;
    }
}

/// <summary>Result of a file-level CancellationToken propagation operation.</summary>
public class PropagateCtFileResult
{
    public string? ChangeId
    {
        get; set;
    }
    public string FilePath { get; set; } = "";
    public List<PropagateCtResult> PerMethod { get; set; } = new();
    public int TotalForwarded
    {
        get; set;
    }
    public int TotalSkipped
    {
        get; set;
    }
    public int MethodsProcessed
    {
        get; set;
    }
    public int MethodsSkipped
    {
        get; set;
    }
    /// <summary>
    /// Full source text of the file immediately before this operation was written to disk.
    /// Populated by PropagateCancellationTokenBatchAsync to enable undo_last_apply via
    /// BeforeSource on OperationItemRecord. Null when pre-image capture fails or the file
    /// did not previously exist.
    /// </summary>
    public string? BeforeSource
    {
        get; set;
    }
}

/// <summary>Specifies a file (with optional method filter) for batch CT propagation.</summary>
public class PropagateCtFileTarget
{
    public string FilePath { get; set; } = "";
    public string[]? MethodNames
    {
        get; set;
    }
}

/// <summary>Input for <c>propagate_cancellation_token_batch</c>.</summary>
public class PropagateCtBatchInput
{
    public List<PropagateCtFileTarget> Targets { get; set; } = new();
    public bool DryRun { get; set; } = false;
    public int MaxFiles { get; set; } = 100;
    public bool FlagFailures { get; set; } = true;
}

/// <summary>Describes a file that failed during batch CT propagation.</summary>
public class PropagateCtFileFailure
{
    public string FilePath { get; set; } = "";
    public string Reason { get; set; } = "";
    public List<DiagnosticInfo> Diagnostics { get; set; } = new();
    public List<string> FlaggedMethods { get; set; } = new();
}

/// <summary>Aggregate result of <c>propagate_cancellation_token_batch</c>.</summary>
public class PropagateCtBatchResult
{
    public List<PropagateCtFileResult> Applied { get; set; } = new();
    public List<PropagateCtFileFailure> Failed { get; set; } = new();
    public int TotalForwarded
    {
        get; set;
    }
    public int TotalSkipped
    {
        get; set;
    }
    public int RemainingFiles
    {
        get; set;
    }
    public string StopReason { get; set; } = "";
}

// ──────────────────────────────────────────────────────────────────────────────
// Engine
// ──────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Orchestrates batch async-migration operations using in-memory Roslyn compilation for
/// validation — no MSBuild round-trips required.
/// <list type="bullet">
///   <item><see cref="RunBridgeBatchAsync"/> — applies the Asyncify-bridge transform to a
///         batch of methods flagged with <c>[MigrationCandidate("AsyncBridgeCandidate")]</c>,
///         validates each in-memory, writes successes to disk, and flags failures for manual
///         review.</item>
///   <item><see cref="RunUpliftBatchAsync"/> — finds callers of a previously bridged method,
///         bridges each caller, rewrites the caller's new async body to replace the obsolete
///         sync call with an awaited async call, validates in-memory, and writes successes to
///         disk.</item>
/// </list>
/// </summary>
public class AsyncBatchEngine
{
    private readonly PersistentWorkspaceManager _workspaceManager;
    private readonly AsyncOptimizationEngine _asyncOptimizationEngine;
    private readonly ValidationEngine _validationEngine;
    private readonly AntiPatternEngine _antiPatternEngine;
    private readonly ILogger<AsyncBatchEngine> _logger;

    /// <summary>
    /// Initialises a new <see cref="AsyncBatchEngine"/> with all required dependencies.
    /// </summary>
    public AsyncBatchEngine(
        PersistentWorkspaceManager workspaceManager,
        AsyncOptimizationEngine asyncOptimizationEngine,
        ValidationEngine validationEngine,
        AntiPatternEngine antiPatternEngine,
        ILogger<AsyncBatchEngine> logger)
    {
        _workspaceManager = workspaceManager;
        _asyncOptimizationEngine = asyncOptimizationEngine;
        _validationEngine = validationEngine;
        _antiPatternEngine = antiPatternEngine;
        _logger = logger;
    }

    // ──────────────────────────────────────────────────────────────────────────
    // RunBridgeBatchAsync
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Converts a batch of methods flagged with <c>[MigrationCandidate("AsyncBridgeCandidate")]</c>
    /// to the Asyncify-bridge pattern.  For each candidate:
    /// <list type="number">
    ///   <item>Calls <c>ConvertToAsyncBridgeAsync</c> to generate the transformed source.</item>
    ///   <item>Validates the source with in-memory Roslyn compilation (no MSBuild needed).</item>
    ///   <item>On success: writes the file to disk and refreshes the workspace.</item>
    ///   <item>On error: flags the method with <c>[MigrationCandidate("NeedsManualReview")]</c>
    ///         and continues to the next candidate.</item>
    /// </list>
    /// </summary>
    /// <param name="projectName">
    /// Optional project name to restrict the candidate scan. <c>null</c> = entire solution.
    /// </param>
    /// <param name="maxBridges">
    /// Maximum number of candidates to process in this call. Candidates are ordered by ascending
    /// score (lowest score = easiest first). Default 10.
    /// </param>
    /// <param name="scoreThreshold">
    /// Only candidates with score ≤ this value are eligible. Default 60.
    /// Note: <see cref="flag_migration_candidates_in_project"/> uses minScore 50 by default,
    /// so no candidate is ever flagged with a score below 50. A threshold of 60 captures the
    /// simplest (lowest-scoring) batch first.
    /// </param>
    /// <param name="dryRun">
    /// When <c>true</c>, returns what would be done without writing any files. Default <c>false</c>.
    /// </param>
    /// <param name="cancellationToken">Optional cancellation token.</param>
    /// <returns>
    /// A <see cref="BridgeBatchResult"/> describing applied, skipped, remaining candidate count,
    /// and the reason the batch stopped.
    /// </returns>
    public async Task<BridgeBatchResult> RunBridgeBatchAsync(
        string? projectName = null,
        int maxBridges = 10,
        int scoreThreshold = 50,
        bool dryRun = false,
        bool propagateCancellationTokens = true,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        // 1. Discover eligible candidates.
        var candidates = await _asyncOptimizationEngine.FindMigrationCandidatesAsync(
            filePath: null,
            projectName: projectName,
            pattern: "AsyncBridgeCandidate",
            cancellationToken: cancellationToken);

        var eligible = candidates
            .Where(c => c.Score >= scoreThreshold)
            .OrderByDescending(c => c.Score)  // highest score (highest impact) first
            .DistinctBy(c => (c.FilePath, c.MethodName))
            .ToList();

        var applied = new List<BridgeAppliedInfo>();
        var skipped = new List<BridgeSkippedInfo>();

        if (eligible.Count == 0)
        {
            var allScores = candidates.Select(c => c.Score);
            return new BridgeBatchResult(applied, skipped, 0, "no_candidates")
            {
                MinCandidateScore = CandidateScoreAnalyzer.ComputeMax(allScores),
                AllCandidatesBuckets = candidates.Count > 0
                    ? CandidateScoreAnalyzer.ComputeBuckets(allScores)
                    : null,
            };
        }

        // 2. Process up to maxBridges candidates.
        int budget = Math.Min(eligible.Count, maxBridges);

        for (int i = 0; i < budget; i++)
        {
            if (cancellationToken.IsCancellationRequested) break;

            var candidate = eligible[i];

            _logger.LogInformation(
                "RunBridgeBatch [{Index}/{Budget}]: bridging {Method} in {File}",
                i + 1, budget, candidate.MethodName, candidate.FilePath);

            if (dryRun)
            {
                // Report what would happen without touching the filesystem.
                applied.Add(new BridgeAppliedInfo(
                    candidate.FilePath, candidate.MethodName, candidate.MethodName + "Async"));
                continue;
            }

            // Step A: generate new source via bridge transform.
            string newSource;
            try
            {
                var result = await _asyncOptimizationEngine.ConvertToAsyncBridgeAsync(
                    candidate.FilePath,
                    candidate.MethodName,
                    progress,
                    cancellationToken);
                newSource = result.UpdatedText ?? throw new InvalidOperationException();
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("already exists"))
            {
                // The async overload already exists — try adding CT to it instead of failing
                var asyncMethodName = candidate.MethodName + "Async";
                _logger.LogInformation(
                    "Async overload '{AsyncMethod}' already exists — attempting CT add fallback",
                    asyncMethodName);
                bool fallbackHandled = false;
                try
                {
                    var ctResult = await _asyncOptimizationEngine.AddCancellationTokenToMethodAsync(
                        candidate.FilePath, asyncMethodName, progress, cancellationToken);
                    if (ctResult.Outcome == EditOutcome.Modified && ctResult.UpdatedText != null)
                    {
                        var ctValidation = await _validationEngine.ValidateChangesAsync(
                            new Dictionary<FilePath, string> { { candidate.FilePath, ctResult.UpdatedText } },
                            progress, cancellationToken);
                        if (ctValidation.Success)
                        {
                            string? beforeSource = File.Exists(candidate.FilePath)
                                ? await File.ReadAllTextAsync(candidate.FilePath, cancellationToken) : null;
                            await _workspaceManager.ApplyProposedChangesAsync(
                                new Dictionary<FilePath, string> { { candidate.FilePath, ctResult.UpdatedText } });
                            applied.Add(new BridgeAppliedInfo(candidate.FilePath, candidate.MethodName, asyncMethodName)
                            {
                                BeforeSource = beforeSource,
                            });
                            progress?.Report($"Added CT to existing async overload '{asyncMethodName}' in {candidate.FilePath}");
                            fallbackHandled = true;
                        }
                        else
                        {
                            var diagMessages = ctValidation.Diagnostics
                                .Select(d => $"[{d.Id}] {d.Message}").ToList();
                            skipped.Add(new BridgeSkippedInfo(
                                candidate.FilePath, candidate.MethodName,
                                $"CT-add validation failed for '{asyncMethodName}': {ctValidation.Diagnostics.Count} error(s); flagged NeedsManualReview",
                                diagMessages));
                            fallbackHandled = true;
                        }
                    }
                    else if (ctResult.Outcome == EditOutcome.NoChange)
                    {
                        skipped.Add(new BridgeSkippedInfo(
                            candidate.FilePath, candidate.MethodName,
                            $"Async overload '{asyncMethodName}' already exists and already has CancellationToken",
                            new List<string>()));
                        fallbackHandled = true;
                    }
                }
                catch (Exception ctEx)
                {
                    _logger.LogWarning(ctEx, "CT-add fallback failed for '{AsyncMethod}'", asyncMethodName);
                }
                if (!fallbackHandled)
                {
                    skipped.Add(new BridgeSkippedInfo(
                        candidate.FilePath, candidate.MethodName,
                        $"Pre-condition: {ex.Message}", new List<string>()));
                }
                continue;
            }
            catch (InvalidOperationException ex)
            {
                // Other pre-condition failure (abstract, already async, event handler, etc.)
                // Flag NeedsManualReview so this method is excluded from future bridge batches.
                _logger.LogWarning(
                    "ConvertToAsyncBridge pre-condition failed for {Method}: {Message}",
                    candidate.MethodName, ex.Message);
                try
                {
                    var flagResult = await _asyncOptimizationEngine.FlagMigrationCandidateAsync(
                        candidate.FilePath, candidate.MethodName, "NeedsManualReview",
                        score: 0,
                        reason: $"Bridge pre-condition: {ex.Message}",
                        progress: progress,
                        cancellationToken: cancellationToken);
                    await _workspaceManager.ApplyProposedChangesAsync(flagResult.Changes);
                }
                catch (Exception flagEx)
                {
                    _logger.LogWarning(flagEx,
                        "Could not flag {Method} as NeedsManualReview after pre-condition failure",
                        candidate.MethodName);
                }
                skipped.Add(new BridgeSkippedInfo(
                    candidate.FilePath, candidate.MethodName,
                    $"Pre-condition: {ex.Message}", new List<string>()));
                continue;
            }
            catch (Exception ex)
            {
                // Unexpected error — also flag NeedsManualReview to prevent infinite retries.
                _logger.LogError(ex,
                    "Unexpected error in ConvertToAsyncBridge for {Method}", candidate.MethodName);
                try
                {
                    var flagResult = await _asyncOptimizationEngine.FlagMigrationCandidateAsync(
                        candidate.FilePath, candidate.MethodName, "NeedsManualReview",
                        score: 0,
                        reason: $"Unexpected bridge error: {ex.Message}",
                        progress: progress,
                        cancellationToken: cancellationToken);
                    await _workspaceManager.ApplyProposedChangesAsync(flagResult.Changes);
                }
                catch (Exception flagEx)
                {
                    _logger.LogWarning(flagEx,
                        "Could not flag {Method} as NeedsManualReview after unexpected error",
                        candidate.MethodName);
                }
                skipped.Add(new BridgeSkippedInfo(
                    candidate.FilePath, candidate.MethodName,
                    $"Unexpected error: {ex.Message}", new List<string>()));
                continue;
            }

            // Step B (optional): propagate CT in the new async overload.
            string sourceToValidate = newSource;
            if (propagateCancellationTokens)
            {
                try
                {
                    // Propagate CT in the new async overload only (methodName + "Async").
                    // We must parse the updated source in-memory since it hasn't been written yet.
                    // Use a temporary in-memory approach via the engine helper.
                    var asyncMethodName = candidate.MethodName + "Async";
                    var (propagatedSource, _) = await _asyncOptimizationEngine
                        .PropagateCancellationTokenInSourceAsync(
                            newSource, candidate.FilePath, asyncMethodName, progress, cancellationToken);
                    sourceToValidate = propagatedSource;
                }
                catch (Exception propEx)
                {
                    _logger.LogWarning(propEx,
                        "CT propagation failed for '{Method}' — continuing with unpropagated source",
                        candidate.MethodName + "Async");
                    // Fall through with original bridge source
                }
            }

            // Step C: validate in-memory — no MSBuild required.
            Debug.WriteLine($"Validating in-memory for {candidate.MethodName} in {candidate.FilePath}...");
            var validation = await _validationEngine.ValidateChangesAsync(
                new Dictionary<FilePath, string> { { candidate.FilePath, sourceToValidate } },
                progress,
                cancellationToken);

            if (!validation.Success)
            {
                var diagMessages = validation.Diagnostics
                    .Select(d => $"[{d.Id}] {d.Message}")
                    .ToList();

                _logger.LogWarning(
                    "In-memory validation failed for {Method} ({DiagCount} errors): {FirstDiag}",
                    candidate.MethodName, validation.Diagnostics.Count,
                    diagMessages.FirstOrDefault() ?? "");

                // Flag the method for manual review — best effort, do not abort batch on failure.
                try
                {
                    var flagResult = await _asyncOptimizationEngine.FlagMigrationCandidateAsync(
                        candidate.FilePath, candidate.MethodName, "NeedsManualReview",
                        score: 0,
                        reason: $"Bridge produced {validation.Diagnostics.Count} compiler error(s)",
                        progress: progress,
                        cancellationToken: cancellationToken);
                    await _workspaceManager.ApplyProposedChangesAsync(flagResult.Changes);
                }
                catch (Exception flagEx)
                {
                    _logger.LogWarning(flagEx,
                        "Could not flag {Method} as NeedsManualReview — continuing",
                        candidate.MethodName);
                }

                skipped.Add(new BridgeSkippedInfo(
                    candidate.FilePath, candidate.MethodName,
                    $"Validation produced {validation.Diagnostics.Count} compiler error(s); flagged NeedsManualReview",
                    diagMessages));
                continue;
            }

            // Step D: write to disk and refresh workspace.
            string? bridgeBeforeSource = File.Exists(candidate.FilePath)
                ? await File.ReadAllTextAsync(candidate.FilePath, cancellationToken)
                : null;

            await _workspaceManager.ApplyProposedChangesAsync(
                new Dictionary<FilePath, string> { { candidate.FilePath, sourceToValidate } });

            applied.Add(new BridgeAppliedInfo(
                candidate.FilePath, candidate.MethodName, candidate.MethodName + "Async")
            {
                BeforeSource = bridgeBeforeSource,
            });

            progress?.Report($"{applied.Count} of {eligible.Count}. Bridged {candidate.MethodName} in {candidate.FilePath}");
        }

        // Determine why the batch stopped.
        int totalProcessed = applied.Count + skipped.Count;
        string stopReason;
        if (dryRun)
        {
            stopReason = "dry_run";
        }
        else if (totalProcessed >= eligible.Count)
        {
            stopReason = "batch_complete";
        }
        else
        {
            stopReason = "budget_exhausted";
        }

        // Remaining = total eligible that were not applied this run.
        int remaining = eligible.Count - applied.Count;

        return new BridgeBatchResult(applied, skipped, remaining, stopReason);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // RunUpliftBatchAsync
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Uplifts callers of an Asyncify-bridge sync wrapper to use the async overload directly.
    /// For each caller:
    /// <list type="number">
    ///   <item>Applies <c>ConvertToAsyncBridgeAsync</c> to the caller, creating a
    ///         <c>callerAsync(CancellationToken)</c> overload with the original body.</item>
    ///   <item>Rewrites the <c>callerAsync</c> body to replace
    ///         <c>bridgedMethod(args)</c> → <c>await bridgedMethodAsync(args, cancellationToken)</c>.</item>
    ///   <item>Validates the combined transform in-memory using Roslyn compilation.</item>
    ///   <item>On success: writes the file to disk and refreshes the workspace.</item>
    ///   <item>On error: flags the caller with <c>[MigrationCandidate("NeedsManualReview")]</c>
    ///         and continues.</item>
    /// </list>
    /// </summary>
    /// <param name="bridgedMethodName">
    /// Name of the already-bridged (Obsolete-annotated) sync method whose callers should be
    /// uplifted. For example, if the bridge is <c>search()</c>, pass <c>"search"</c>.
    /// </param>
    /// <param name="projectName">
    /// Optional project name to restrict the caller scan. <c>null</c> = entire solution.
    /// </param>
    /// <param name="maxCallers">Maximum number of caller methods to process. Default 10.</param>
    /// <param name="dryRun">
    /// When <c>true</c>, returns what would be done without writing any files. Default <c>false</c>.
    /// </param>
    /// <param name="cancellationToken">Optional cancellation token.</param>
    /// <returns>
    /// A <see cref="UpliftBatchResult"/> describing uplifted, skipped, remaining caller count,
    /// and the reason the batch stopped.
    /// </returns>
    public async Task<UpliftBatchResult> RunUpliftBatchAsync(
        string bridgedMethodName,
        string? projectName = null,
        int maxCallers = 10,
        bool dryRun = false,
        bool propagateCancellationTokens = true,
        string? symbolId = null,
        IProgress<string> progress = default,
        CancellationToken cancellationToken = default)
    {
        // 1. Find all call sites that invoke the Asyncify-bridge sync wrapper.
        //    Filter by the standard Obsolete message pattern so we only match the
        //    specific bridge and not unrelated [Obsolete] methods.
        var asyncMethodName = bridgedMethodName + "Async";
        var messageFilter = $"Asyncify-bridge: call {asyncMethodName} instead.";

        var callerFindings = await _antiPatternEngine.FindObsoleteCallersAsync(
            messagePattern: messageFilter,
            filePath: null,
            projectName: projectName,
            symbolId: symbolId,
            cancellationToken: cancellationToken);

        int totalFound = callerFindings.Count;

        var uplifted = new List<UpliftCallerInfo>();
        var skipped = new List<UpliftSkippedInfo>();

        if (totalFound == 0)
        {
            return new UpliftBatchResult(uplifted, skipped, 0, "no_callers");
        }

        // 2. Deduplicate to unique (filePath, callerMethod) pairs and take up to maxCallers.
        var callerPairs = callerFindings
            .Select(c => (c.FilePath, c.CallerMethod))
            .Distinct()
            .Take(maxCallers)
            .ToList();

        if (dryRun)
        {
            foreach (var (fp, cm) in callerPairs)
            {
                uplifted.Add(new UpliftCallerInfo(fp, cm, cm + "Async"));
            }
            int dryRemaining = Math.Max(0, totalFound - callerPairs.Count);
            return new UpliftBatchResult(uplifted, skipped, dryRemaining, "dry_run");
        }

        // 3. Process each caller.
        for (int i = 0; i < callerPairs.Count; i++)
        {
            if (cancellationToken.IsCancellationRequested) break;

            var (callerFilePath, callerMethodName) = callerPairs[i];

            _logger.LogInformation(
                "RunUpliftBatch [{Index}/{Total}]: uplifting {Caller} in {File}",
                i + 1, callerPairs.Count, callerMethodName, callerFilePath);

            // Step A: bridge the caller — creates callerAsync(CT) with original body.
            // Pre-check the caller's state from disk so we never send already-async methods through
            // ConvertToAsyncBridgeAsync (which would throw and rely on message-string matching).
            string bridgedCallerSource;
            bool isEventHandlerInPlace = false;
            string? callerAsyncNameOverride = null;

            if (!File.Exists(callerFilePath))
            {
                skipped.Add(new UpliftSkippedInfo(callerFilePath, callerMethodName,
                    "Caller file not found on disk.", new List<string>()));
                continue;
            }

            var preCheckSource = await File.ReadAllTextAsync(callerFilePath, cancellationToken);
            var preCheckTree = CSharpSyntaxTree.ParseText(preCheckSource);
            var preCheckRoot = preCheckTree.GetRoot();
            var preCheckMethods = preCheckRoot.DescendantNodes().OfType<MethodDeclarationSyntax>().ToList();

            var callerMethodNode = preCheckMethods
                .FirstOrDefault(m => m.Identifier.Text == callerMethodName);

            bool callerIsAsync = callerMethodNode?.Modifiers.Any(m => m.IsKind(SyntaxKind.AsyncKeyword)) == true;
            bool asyncOverloadExists = preCheckMethods
                .Any(m => m.Identifier.Text == callerMethodName + "Async");

            if (callerIsAsync)
            {
                // Caller is itself async — rewrite its body directly without creating a new overload.
                _logger.LogInformation(
                    "RunUpliftBatch: '{Method}' is already async — rewriting body in-place",
                    callerMethodName);
                bridgedCallerSource = preCheckSource;
                callerAsyncNameOverride = callerMethodName;
            }
            else if (asyncOverloadExists)
            {
                // An async overload already exists — rewrite its body to call the bridged method.
                _logger.LogInformation(
                    "RunUpliftBatch: '{Method}' has existing async overload — rewriting body directly",
                    callerMethodName);
                bridgedCallerSource = preCheckSource;
                // callerAsyncNameOverride stays null → Steps B/C use callerMethodName + "Async"
            }
            else
            {
                // Normal path (sync caller, no existing async overload): let ConvertToAsyncBridgeAsync
                // create the async bridge. It handles event handlers, abstract methods, ref/out params,
                // and other edge cases via its own exception throw paths.
                try
                {
                    var result = await _asyncOptimizationEngine.ConvertToAsyncBridgeAsync(
                        callerFilePath, callerMethodName, progress, cancellationToken);
                    bridgedCallerSource = result.UpdatedText ?? throw new InvalidOperationException("ConvertToAsyncBridgeAsync returned null source.");
                }
                catch (InvalidOperationException ex) when (ex.Message.Contains("event handler"))
                {
                    _logger.LogInformation(
                        "RunUpliftBatch: '{Method}' is an event handler — attempting in-place async void conversion",
                        callerMethodName);
                    try
                    {
                        var inPlace = await _asyncOptimizationEngine.ConvertEventHandlerCallerToAsyncVoidAsync(
                            callerFilePath, callerMethodName, cancellationToken: cancellationToken);
                        bridgedCallerSource = inPlace.UpdatedText
                            ?? throw new InvalidOperationException("In-place async void conversion returned null source.");
                        isEventHandlerInPlace = true;
                    }
                    catch (Exception inPlaceEx)
                    {
                        _logger.LogWarning(
                            "In-place async void conversion failed for '{Method}': {Message}",
                            callerMethodName, inPlaceEx.Message);
                        skipped.Add(new UpliftSkippedInfo(
                            callerFilePath, callerMethodName,
                            $"Event handler in-place: {inPlaceEx.Message}", new List<string>()));
                        continue;
                    }
                }
                catch (InvalidOperationException ex)
                {
                    _logger.LogWarning(
                        "Bridge pre-condition failed for caller {Method}: {Message}",
                        callerMethodName, ex.Message);
                    skipped.Add(new UpliftSkippedInfo(
                        callerFilePath, callerMethodName,
                        $"Bridge pre-condition: {ex.Message}", new List<string>()));
                    continue;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex,
                        "Unexpected error bridging caller {Method}", callerMethodName);
                    skipped.Add(new UpliftSkippedInfo(
                        callerFilePath, callerMethodName,
                        $"Unexpected bridge error: {ex.Message}", new List<string>()));
                    continue;
                }
            }

            // Step B: rewrite callerAsync body — replace bridgedMethod(args)
            //         → await bridgedMethodAsync(args, cancellationToken).
            //         Skipped for in-place event handler conversions (already fully rewritten).
            string rewrittenSource;
            if (isEventHandlerInPlace)
            {
                rewrittenSource = bridgedCallerSource;
            }
            else
            {
                var rewriteTargetName = callerAsyncNameOverride ?? callerMethodName + "Async";
                try
                {
                    rewrittenSource = RewriteCallerAsyncBody(
                        bridgedCallerSource,
                        rewriteTargetName,
                        bridgedMethodName);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(
                        "Body rewrite failed for {Method}: {Message}",
                        rewriteTargetName, ex.Message);
                    skipped.Add(new UpliftSkippedInfo(
                        callerFilePath, callerMethodName,
                        $"Body rewrite failed: {ex.Message}", new List<string>()));
                    continue;
                }
            }

            // Step C (optional): propagate CT in the new callerAsync overload.
            //                    Skipped for in-place event handlers — they have no CT to propagate.
            string sourceToValidate = rewrittenSource;
            if (propagateCancellationTokens && !isEventHandlerInPlace)
            {
                try
                {
                    var callerAsyncName = callerAsyncNameOverride ?? callerMethodName + "Async";
                    var (propagatedSource, _) = await _asyncOptimizationEngine
                        .PropagateCancellationTokenInSourceAsync(
                            rewrittenSource, callerFilePath, callerAsyncName, progress, cancellationToken);
                    sourceToValidate = propagatedSource;
                }
                catch (Exception propEx)
                {
                    _logger.LogWarning(propEx,
                        "CT propagation failed for uplifted '{Method}' — using unrewritten source",
                        callerAsyncNameOverride ?? callerMethodName + "Async");
                }
            }

            // Step D: validate in-memory.
            var validation = await _validationEngine.ValidateChangesAsync(
                new Dictionary<FilePath, string> { { callerFilePath, sourceToValidate } },
                progress, cancellationToken);

            if (!validation.Success)
            {
                var diagMessages = validation.Diagnostics
                    .Select(d => $"[{d.Id}] {d.Message}")
                    .ToList();

                _logger.LogWarning(
                    "In-memory validation failed for uplifted {Method} ({DiagCount} errors): {First}",
                    callerMethodName, validation.Diagnostics.Count,
                    diagMessages.FirstOrDefault() ?? "");

                // Flag the original caller for manual review — best effort.
                try
                {
                    var flagResult = await _asyncOptimizationEngine.FlagMigrationCandidateAsync(
                        callerFilePath, callerMethodName, "NeedsManualReview",
                        score: 0,
                        reason: $"Uplift produced {validation.Diagnostics.Count} compiler error(s)",
                        progress: progress,
                        cancellationToken: cancellationToken);
                    await _workspaceManager.ApplyProposedChangesAsync(flagResult.Changes, progress: progress, cancellationToken: cancellationToken);
                }
                catch (Exception flagEx)
                {
                    _logger.LogWarning(flagEx,
                        "Could not flag caller {Method} as NeedsManualReview — continuing",
                        callerMethodName);
                }

                skipped.Add(new UpliftSkippedInfo(
                    callerFilePath, callerMethodName,
                    $"Validation produced {validation.Diagnostics.Count} compiler error(s); flagged NeedsManualReview",
                    diagMessages));
                continue;
            }

            // Step E: write to disk and refresh workspace.
            string? upliftBeforeSource = File.Exists(callerFilePath)
                ? await File.ReadAllTextAsync(callerFilePath, cancellationToken)
                : null;

            await _workspaceManager.ApplyProposedChangesAsync(
                new Dictionary<FilePath, string> { { callerFilePath, sourceToValidate } }, progress: progress, cancellationToken: cancellationToken);

            uplifted.Add(new UpliftCallerInfo(
                callerFilePath, callerMethodName,
                isEventHandlerInPlace ? callerMethodName : (callerAsyncNameOverride ?? callerMethodName + "Async"))
            {
                BeforeSource = upliftBeforeSource,
            });

            progress?.Report($"{uplifted.Count} of {callerPairs.Count}. Uplifted {callerMethodName} in {callerFilePath}");
        }

        // Determine stop reason.
        int totalProcessed = uplifted.Count + skipped.Count;
        string stopReason = totalProcessed >= callerPairs.Count ? "batch_complete" : "budget_exhausted";

        // Remaining = original finder count minus number actually uplifted.
        int remaining = Math.Max(0, totalFound - uplifted.Count);

        return new UpliftBatchResult(uplifted, skipped, remaining, stopReason);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Body rewriter helpers
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Parses <paramref name="source"/>, locates the method named
    /// <paramref name="callerAsyncMethodName"/>, and rewrites every call to
    /// <paramref name="bridgedMethodName"/> inside that method's body as
    /// <c>await bridgedMethodNameAsync(args, cancellationToken)</c>.
    /// Returns the transformed full-file source (or the original if the method is not found).
    /// </summary>
    private static string RewriteCallerAsyncBody(
        string source,
        string callerAsyncMethodName,
        string bridgedMethodName)
    {
        var tree = CSharpSyntaxTree.ParseText(source);
        var root = tree.GetRoot();

        // Locate the callerAsync method to scope the rewrite.
        var callerAsyncMethod = root.DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .FirstOrDefault(m => m.Identifier.Text == callerAsyncMethodName);

        if (callerAsyncMethod == null)
        {
            // Nothing to rewrite — the method was not found (should not normally happen).
            return source;
        }

        // Determine the cancellation-token expression to inject.
        // Use the existing CT param name so the argument binds to a variable already in scope.
        // Fall back to CancellationToken.None for methods that have no CT parameter (e.g. event
        // handlers whose signature is fixed by the delegate contract, or older async helpers).
        var ctParam = callerAsyncMethod.ParameterList.Parameters
            .FirstOrDefault(p => (p.Type?.ToString() ?? "").Contains("CancellationToken"));
        var ctExpression = ctParam?.Identifier.Text ?? "CancellationToken.None";

        // Apply the rewriter scoped to only this method's subtree.
        var rewriter = new BridgeCallRewriter(bridgedMethodName, ctExpression);
        var newMethod = (MethodDeclarationSyntax)rewriter.Visit(callerAsyncMethod)!;
        // Any delegate/lambda that now contains await must itself be marked async.
        newMethod = (MethodDeclarationSyntax)new AsyncOptimizationEngine.AsyncifyAnonymousFunctionsRewriter().Visit(newMethod)!;
        var newRoot = root.ReplaceNode(callerAsyncMethod, newMethod);

        return newRoot.NormalizeWhitespace().ToFullString();
    }

    // ──────────────────────────────────────────────────────────────────────────
    // BridgeCallRewriter — inner CSharpSyntaxRewriter
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A <see cref="CSharpSyntaxRewriter"/> that rewrites calls to a specific sync bridge method
    /// as awaited calls to its async counterpart:
    /// <code>bridgedMethod(args) → await bridgedMethodAsync(args, cancellationToken)</code>
    /// Handles both unqualified calls (<c>method()</c>) and member-access calls
    /// (<c>this.method()</c> or <c>obj.method()</c>). Skips invocations that are already wrapped
    /// in an <c>await</c> expression to prevent double-wrapping.
    /// </summary>
    private sealed class BridgeCallRewriter : CSharpSyntaxRewriter
    {
        private readonly string _bridgedMethodName;
        private readonly string _asyncMethodName;
        private readonly string _cancellationTokenExpression;

        /// <summary>
        /// Initialises the rewriter for the given bridged sync method name.
        /// </summary>
        /// <param name="bridgedMethodName">
        /// The name of the sync bridge method to replace (e.g. <c>"search"</c>).
        /// The async overload name is derived by appending <c>"Async"</c>.
        /// </param>
        /// <param name="cancellationTokenExpression">
        /// Expression to inject as the final CT argument (e.g. <c>"cancellationToken"</c>,
        /// <c>"ct"</c>, or <c>"CancellationToken.None"</c> for event handlers and other
        /// methods whose signature cannot carry a CT parameter).
        /// </param>
        public BridgeCallRewriter(string bridgedMethodName, string cancellationTokenExpression = "cancellationToken")
        {
            _bridgedMethodName = bridgedMethodName;
            _asyncMethodName = bridgedMethodName + "Async";
            _cancellationTokenExpression = cancellationTokenExpression;
        }

        /// <inheritdoc/>
        public override SyntaxNode? VisitInvocationExpression(InvocationExpressionSyntax node)
        {
            // Let the base visitor recurse into children first so nested calls are handled.
            var visited = (InvocationExpressionSyntax)base.VisitInvocationExpression(node)!;

            // Determine whether this invocation targets the bridged method.
            string? calledName;
            ExpressionSyntax newExpression;

            switch (visited.Expression)
            {
                // Unqualified call: search(args)
                case IdentifierNameSyntax id when id.Identifier.Text == _bridgedMethodName:
                    calledName = id.Identifier.Text;
                    newExpression = SyntaxFactory.IdentifierName(_asyncMethodName)
                                                 .WithTriviaFrom(id);
                    break;

                // Member-access call: this.search(args) or obj.search(args)
                case MemberAccessExpressionSyntax ma when ma.Name.Identifier.Text == _bridgedMethodName:
                    calledName = ma.Name.Identifier.Text;
                    newExpression = ma.WithName(
                        SyntaxFactory.IdentifierName(_asyncMethodName)
                                     .WithTriviaFrom(ma.Name));
                    break;

                default:
                    // Not targeting the bridged method — leave unchanged.
                    return visited;
            }

            _ = calledName; // suppress unused-variable warning

            // Use node.Parent (original, tree-rooted) for parent context — visited may be detached.

            // If the invocation is already the direct operand of an await expression,
            // only rename the method — don't add another await or another cancellationToken arg.
            if (node.Parent is AwaitExpressionSyntax)
            {
                return visited.WithExpression(newExpression);
            }

            // Append the cancellation-token expression as the final positional argument.
            // Expression is either an identifier (e.g. "cancellationToken") or a member access
            // (e.g. "CancellationToken.None") — ParseExpression handles both forms.
            var ctArg = SyntaxFactory.Argument(SyntaxFactory.ParseExpression(_cancellationTokenExpression));
            var newArgList = visited.ArgumentList.AddArguments(ctArg);

            var rewrittenCall = visited
                .WithExpression(newExpression)
                .WithArgumentList(newArgList)
                .WithoutLeadingTrivia();

            // When the bridge call is chained — Foo().Rows, Foo().AsDataView(), Foo()[i] — the
            // await must be parenthesised so that member/element access binds to the awaited value,
            // not to the Task:  Foo().Rows → (await FooAsync(ct)).Rows
            // Without parens, `await FooAsync(ct).Rows` is parsed as `await (FooAsync(ct).Rows)`,
            // which fails to compile because Task<T> has no such member.
            bool needsParens = node.Parent is MemberAccessExpressionSyntax
                            || node.Parent is ElementAccessExpressionSyntax
                            || node.Parent is ConditionalAccessExpressionSyntax;

            var awaitExpr = SyntaxFactory.AwaitExpression(
                    SyntaxFactory.Token(SyntaxKind.AwaitKeyword)
                                 .WithTrailingTrivia(SyntaxFactory.Space),
                    rewrittenCall);

            return needsParens
                ? (SyntaxNode)SyntaxFactory.ParenthesizedExpression(awaitExpr)
                    .WithLeadingTrivia(visited.GetLeadingTrivia())
                : awaitExpr.WithLeadingTrivia(visited.GetLeadingTrivia());
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // PropagateCancellationTokenBatchAsync
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Propagates CancellationToken parameters to async callees across a batch of files.
    /// For each file in <paramref name="input"/>:
    /// <list type="number">
    ///   <item>Calls <see cref="AsyncOptimizationEngine.PropagateCancellationTokenInFileAsync"/>
    ///         to compute the updated source.</item>
    ///   <item>Validates the result with in-memory Roslyn compilation.</item>
    ///   <item>On success: writes to disk and refreshes the workspace.</item>
    ///   <item>On error: if <c>FlagFailures=true</c>, flags each modified method with
    ///         <c>[MigrationCandidate("NeedsManualReview")]</c> and continues.</item>
    /// </list>
    /// </summary>
    public async Task<PropagateCtBatchResult> PropagateCancellationTokenBatchAsync(
        PropagateCtBatchInput input,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var batchResult = new PropagateCtBatchResult();

        if (input.DryRun)
        {
            // Dry-run: compute results without writing
            int processed = 0;
            foreach (var target in input.Targets)
            {
                if (cancellationToken.IsCancellationRequested) break;
                if (processed >= input.MaxFiles) break;
                try
                {
                    var (_, fileResult) = await _asyncOptimizationEngine
                        .PropagateCancellationTokenInFileAsync(
                            target.FilePath, target.MethodNames, progress: progress, cancellationToken: cancellationToken);
                    fileResult.FilePath = target.FilePath;
                    batchResult.Applied.Add(fileResult);
                    batchResult.TotalForwarded += fileResult.TotalForwarded;
                    batchResult.TotalSkipped += fileResult.TotalSkipped;
                }
                catch (Exception ex)
                {
                    batchResult.Failed.Add(new PropagateCtFileFailure
                    {
                        FilePath = target.FilePath,
                        Reason = ex.Message
                    });
                }
                processed++;
            }
            batchResult.RemainingFiles = Math.Max(0, input.Targets.Count - processed);
            batchResult.StopReason = "dry_run";
            return batchResult;
        }

        int filesProcessed = 0;
        foreach (var target in input.Targets)
        {
            if (filesProcessed >= input.MaxFiles)
            {
                batchResult.RemainingFiles = input.Targets.Count - filesProcessed;
                batchResult.StopReason = "budget_exhausted";
                break;
            }

            if (cancellationToken.IsCancellationRequested) break;

            string updatedSource;
            PropagateCtFileResult fileResult;
            try
            {
                (updatedSource, fileResult) = await _asyncOptimizationEngine
                    .PropagateCancellationTokenInFileAsync(
                        target.FilePath, target.MethodNames, progress: progress, cancellationToken: cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "PropagateCancellationToken failed for file '{File}'", target.FilePath);
                batchResult.Failed.Add(new PropagateCtFileFailure
                {
                    FilePath = target.FilePath,
                    Reason = ex.Message
                });
                filesProcessed++;
                continue;
            }

            fileResult.FilePath = target.FilePath;

            // If nothing was forwarded, skip validation/write
            if (fileResult.TotalForwarded == 0)
            {
                batchResult.Applied.Add(fileResult);
                batchResult.TotalSkipped += fileResult.TotalSkipped;
                filesProcessed++;
                continue;
            }

            // Validate
            var validation = await _validationEngine.ValidateChangesAsync(
                new Dictionary<FilePath, string> { { target.FilePath, updatedSource } },
                progress: progress,
                cancellationToken: cancellationToken);

            if (!validation.Success)
            {
                var diagMessages = validation.Diagnostics
                    .Select(d => $"[{d.Id}] {d.Message} ({d.FilePath}:{d.StartLine})")
                    .ToList();

                _logger.LogWarning(
                    "PropagateCtBatch validation failed for '{File}' ({N} errors): {First}",
                    target.FilePath, validation.Diagnostics.Count,
                    diagMessages.FirstOrDefault() ?? "");

                var failure = new PropagateCtFileFailure
                {
                    FilePath = target.FilePath,
                    Reason = $"Validation produced {validation.Diagnostics.Count} compiler error(s)",
                    Diagnostics = validation.Diagnostics.ToList()
                };

                // Flag each modified method if requested
                if (input.FlagFailures)
                {
                    foreach (var perMethod in fileResult.PerMethod.Where(m => m.ForwardedCount > 0))
                    {
                        try
                        {
                            var flagResult = await _asyncOptimizationEngine.FlagMigrationCandidateAsync(
                                target.FilePath, perMethod.MethodName, "CancellationTokenForwardCandidate",
                                score: 90,
                                reason: $"PropagateCtBatch produced {validation.Diagnostics.Count} compiler error(s)",
                                cancellationToken: cancellationToken);
                            await _workspaceManager.ApplyProposedChangesAsync(flagResult.Changes);
                            failure.FlaggedMethods.Add(perMethod.MethodName);
                        }
                        catch (Exception flagEx)
                        {
                            _logger.LogWarning(flagEx,
                                "Could not flag method {Method} as NeedsManualReview", perMethod.MethodName);
                        }
                    }
                }

                batchResult.Failed.Add(failure);
                filesProcessed++;
                continue;
            }

            // Write to disk
            string? ctBeforeSource = File.Exists(target.FilePath)
                ? await File.ReadAllTextAsync(target.FilePath, cancellationToken)
                : null;

            await _workspaceManager.ApplyProposedChangesAsync(
                new Dictionary<FilePath, string> { { target.FilePath, updatedSource } },
                progress: progress,
                cancellationToken: cancellationToken);

            fileResult.BeforeSource = ctBeforeSource;
            batchResult.Applied.Add(fileResult);
            batchResult.TotalForwarded += fileResult.TotalForwarded;
            batchResult.TotalSkipped += fileResult.TotalSkipped;
            filesProcessed++;

            progress?.Report($"{filesProcessed} of {input.Targets.Count}. Processed {target.FilePath} with {fileResult.TotalForwarded} forwarded and {fileResult.TotalSkipped} skipped methods.");
        }

        if (string.IsNullOrEmpty(batchResult.StopReason))
        {
            batchResult.StopReason = filesProcessed >= input.Targets.Count
                ? "batch_complete"
                : "budget_exhausted";
        }

        return batchResult;
    }

    // RunUpliftBatchMultiAsync
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Runs <see cref="RunUpliftBatchAsync"/> for each bridged method in <paramref name="input"/>,
    /// collecting per-method and aggregate results. Failed methods do not block subsequent ones.
    /// </summary>
    public async Task<UpliftBatchMultiResult> RunUpliftBatchMultiAsync(
        UpliftBatchMultiInput input,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var result = new UpliftBatchMultiResult();

        foreach (var target in input.Targets)
        {
            if (cancellationToken.IsCancellationRequested) break;

            var methodResult = new UpliftBatchMultiMethodResult
            {
                BridgedMethodName = target.BridgedMethodName,
                ProjectName = target.ProjectName,
            };

            try
            {
                _logger.LogInformation(
                    "RunUpliftBatchMulti: processing '{Method}' (project: {Project})",
                    target.BridgedMethodName, target.ProjectName ?? "all");

                methodResult.Result = await RunUpliftBatchAsync(
                    target.BridgedMethodName,
                    target.ProjectName,
                    input.MaxCallersPerMethod,
                    input.DryRun,
                    input.PropagateCancellationTokens,
                    symbolId: target.SymbolId,
                    progress: progress,
                    cancellationToken: cancellationToken);

                result.TotalUplifted += methodResult.Result.Uplifted.Count;
                result.TotalSkipped += methodResult.Result.Skipped.Count;
                result.TotalRemainingCallers += methodResult.Result.RemainingCallers;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "RunUpliftBatchMulti: error processing '{Method}'", target.BridgedMethodName);
                methodResult.Error = ex.Message;
            }

            result.PerMethod.Add(methodResult);

            progress?.Report($"Completed processing '{target.BridgedMethodName}'. Total uplifted so far: {result.TotalUplifted}, skipped: {result.TotalSkipped}, remaining callers: {result.TotalRemainingCallers}.");
        }

        result.StopReason = input.DryRun ? "dry_run" : "batch_complete";
        return result;
    }
}

