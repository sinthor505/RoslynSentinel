using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace RoslynSentinel.Server;

/// <summary>
/// Return type for source-transform tools that compute an updated file but do NOT write to disk.
/// Always pass <see cref="UpdatedSource"/> to <c>apply_proposed_changes</c> to persist the change.
/// </summary>
/// <param name="UpdatedSource">Full updated source content for the file.</param>
/// <param name="WroteToFile">Always <c>false</c> — these tools never write to disk.</param>
/// <param name="WorkspaceUpdated">Always <c>false</c> — these tools never update the in-memory workspace.</param>
/// <param name="FilePath">Echo of the input file path for routing to <c>apply_proposed_changes</c>.</param>
public record SourceTransformResult(
    string UpdatedSource,
    bool WroteToFile,
    bool WorkspaceUpdated,
    string FilePath
);

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
public record CancellationTokenResult(
    string? ChangeId,
    SourceTransformResult? Source
);

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

/// <summary>
/// A single method flagged by a scout tool via <c>flag_migration_candidate</c>.
/// </summary>
/// <param name="FilePath">Absolute path of the source file containing the method.</param>
/// <param name="MethodName">The method's identifier (case-sensitive).</param>
/// <param name="ClassName">The class that declares the method.</param>
/// <param name="Pattern">
/// The refactoring pattern the method is earmarked for:
/// <c>"AsyncBridgeCandidate"</c>, <c>"HandlerExtract"</c>, <c>"HandlerToAsync"</c>, <c>"AsyncCallerUplift"</c>, etc.
/// </param>
/// <param name="Score">Eligibility score from the scout tool (0 = unscored).</param>
/// <param name="Reason">Human-readable rationale for the flag, or <c>null</c> if none was supplied.</param>
/// <param name="FlaggedDate">ISO date string (yyyy-MM-dd) when the method was flagged, or <c>null</c>.</param>
/// <param name="Line">1-based source line of the method declaration.</param>
public record MigrationCandidateFinding(
    string  FilePath,
    string  MethodName,
    string  ClassName,
    string  Pattern,
    int     Score,
    string? Reason,
    string? FlaggedDate,
    int     Line
)
{
    /// <summary>
    /// Human-readable one-liner combining the most useful fields.
    /// Example: <c>"AsyncBridgeCandidate (score=70): Calls search/isExistedInDB — updateSettlement"</c>.
    /// </summary>
    public string Summary =>
        $"{Pattern} (score={Score})" +
        (string.IsNullOrWhiteSpace(Reason) ? "" : $": {Reason}") +
        $" — {MethodName}";
}

/// <summary>
/// Slim return type for <c>flag_migration_candidate</c>.
/// The attribute has already been written to disk before this result is returned.
/// </summary>
/// <param name="FilePath">Absolute path of the file that was modified.</param>
/// <param name="MethodName">Name of the method that received the <c>[MigrationCandidate]</c> attribute.</param>
/// <param name="Pattern">Migration pattern string that was applied (e.g. <c>"AsyncBridgeCandidate"</c>).</param>
/// <param name="Line">1-based source line of the method declaration after rewriting.</param>
/// <param name="WasAlreadyFlagged">
/// <c>true</c> if the method already carried a <c>[MigrationCandidate]</c> attribute with this exact
/// pattern — the attribute was replaced (idempotent update); <c>false</c> for a fresh flag.
/// </param>
/// <param name="PreviousPattern">
/// The pattern string from a pre-existing <c>[MigrationCandidate]</c> attribute, or <c>null</c>
/// if the method was not previously flagged.
/// </param>
/// <param name="AttributeClassInjected">
/// <c>true</c> if a new <c>MigrationCandidateAttribute.cs</c> file was generated alongside the
/// modification; <c>false</c> if the attribute class was already present in the solution.
/// </param>
/// <param name="Summary">Human-readable summary of the flag action for log display.</param>
public record FlagMigrationCandidateResult(
    string  FilePath,
    string  MethodName,
    string  Pattern,
    int     Line,
    bool    WasAlreadyFlagged,
    string? PreviousPattern,
    bool    AttributeClassInjected,
    string  Summary
);

[McpServerToolType]
public class SentinelQualityTools
{
    private readonly PerformanceEngine _performanceEngine;
    private readonly SecurityEngine _securityEngine;
    private readonly TestingEngine _testingEngine;
    private readonly ControlFlowEngine _controlFlowEngine;
    private readonly LogicOptimizationEngine _logicOptimizationEngine;
    private readonly AnalysisEngine _analysisEngine;
    private readonly AsyncSafetyEngine _asyncSafetyEngine;
    private readonly AntiPatternEngine _antiPatternEngine;
    private readonly AsyncOptimizationEngine _asyncOptimizationEngine;
    private readonly AsyncBatchEngine _asyncBatchEngine;
    private readonly ThreadSafetyEngine _threadSafetyEngine;
    private readonly DiagnosticEngine _diagnosticEngine;
    private readonly CodeStyleAnalysisEngine _codeStyleAnalysisEngine;
    private readonly PathDrivenTestEngine _pathDrivenTestEngine;
    private readonly StackOverflowEngine _stackOverflowEngine;
    private readonly PersistentWorkspaceManager _workspaceManager;
    private readonly ILogger<SentinelQualityTools> _logger;

    public SentinelQualityTools(
        PerformanceEngine performanceEngine,
        SecurityEngine securityEngine,
        TestingEngine testingEngine,
        ControlFlowEngine controlFlowEngine,
        LogicOptimizationEngine logicOptimizationEngine,
        AnalysisEngine analysisEngine,
        AsyncSafetyEngine asyncSafetyEngine,
        AntiPatternEngine antiPatternEngine,
        AsyncOptimizationEngine asyncOptimizationEngine,
        ThreadSafetyEngine threadSafetyEngine,
        DiagnosticEngine diagnosticEngine,
        CodeStyleAnalysisEngine codeStyleAnalysisEngine,
        PathDrivenTestEngine pathDrivenTestEngine,
        StackOverflowEngine stackOverflowEngine,
        AsyncBatchEngine asyncBatchEngine,
        PersistentWorkspaceManager workspaceManager,
        ILogger<SentinelQualityTools> logger)
    {
        _performanceEngine = performanceEngine;
        _securityEngine = securityEngine;
        _testingEngine = testingEngine;
        _controlFlowEngine = controlFlowEngine;
        _logicOptimizationEngine = logicOptimizationEngine;
        _analysisEngine = analysisEngine;
        _asyncSafetyEngine = asyncSafetyEngine;
        _antiPatternEngine = antiPatternEngine;
        _asyncOptimizationEngine = asyncOptimizationEngine;
        _threadSafetyEngine = threadSafetyEngine;
        _diagnosticEngine = diagnosticEngine;
        _codeStyleAnalysisEngine = codeStyleAnalysisEngine;
        _pathDrivenTestEngine = pathDrivenTestEngine;
        _stackOverflowEngine = stackOverflowEngine;
        _asyncBatchEngine = asyncBatchEngine;
        _workspaceManager = workspaceManager;
        _logger = logger;
    }



    [McpServerTool]
    [Description("Extends path coverage analysis with a cross-reference to test methods that exercise the given production method. Finds covering tests by name convention (test method name contains the production method name) and by direct call-site presence in the test body. Returns BranchesToTest (execution paths to cover) and CoveringTests (test file, test method name, line) with HasAnyCoverage flag.")]
    public async Task<TestCoverageMap> GetTestCoverageMap(string filePath, string methodName)
        => await _controlFlowEngine.GetTestCoverageMapAsync(filePath, methodName);

    [McpServerTool]
    [Description("""
        Returns all methods in the solution (or scoped to a file/project) that carry a
        [MigrationCandidate] attribute added by flag_migration_candidate.

        filePath:    restrict to a single file (full or partial path suffix).
        projectName: restrict to a single project (case-insensitive name).
        pattern:     restrict to one pattern — "AsyncBridgeCandidate", "HandlerExtract",
                     "HandlerToAsync", "AsyncCallerUplift", etc. Omit to return all patterns.

        Uses syntax-level analysis — no compilation needed — so results are accurate even
        immediately after flag_migration_candidate without a solution reload.

        Returns one MigrationCandidateFinding per flagged method per attribute.
        Each finding includes a Summary field for human-readable log output.
        A method flagged for two different patterns appears twice.
        """)]
    public async Task<List<MigrationCandidateFinding>> ScanMigrationCandidates(
        string? filePath    = null,
        string? projectName = null,
        string? pattern     = null)
        => await _asyncOptimizationEngine.FindMigrationCandidatesAsync(filePath, projectName, pattern);


    [McpServerTool]
    [Description("Calculates the cyclomatic complexity of a method: 1 + one for each if/else/case/while/for/foreach/catch/&&/||/?? branch. Returns the complexity score and the list of conditionals that contribute to it. Complexity guide: 1–4 = Low (easy to understand and test), 5–7 = Medium, 8–10 = High (refactoring candidate), >10 = Very High (split required). Use before modifying a method to gauge how risky the change is.")]
    public async Task<TestComplexityReport> GetMethodComplexity(string filePath, string methodName)
        => await _testingEngine.CalculateComplexityAsync(filePath, methodName);
    // ── Phase 8: get_async_migration_progress ─────────────────────────────────

    [McpServerTool]
    [Description("""
        Returns async migration progress statistics for the solution or a single project.
        Reports: total async Task/ValueTask methods, how many already have a CancellationToken
        parameter (and how many still need one), percentage coverage, number of Asyncify-bridge
        wrapper methods ([Obsolete("Asyncify-bridge:...")]), number of call sites that still
        invoke those bridge wrappers (CS0618 sites pending migration), and count of async void
        event handlers (informational — their signatures cannot be extended).

        projectName: restrict statistics to a single project; null = entire solution.
        """)]
    public async Task<AsyncMigrationProgressReport> GetAsyncMigrationProgress(
        string? projectName = null,
        CancellationToken cancellationToken = default)
        => await _antiPatternEngine.GetAsyncMigrationProgressAsync(projectName, cancellationToken);

    // ── Phase 4 / Phase 7 — async_migrate internal helpers ─────────────────────

    [Description("""
        Batch-first CancellationToken propagation across multiple files. Supersedes
        propagate_cancellation_token_in_method, propagate_cancellation_token_in_file, and
        propagate_cancellation_token_batch. Checks the circuit breaker before executing;
        records batch outcome; writes a forensic blob.

        input.Targets  — list of { FilePath, MethodNames? }. null MethodNames = all eligible methods.
        input.DryRun   — when true, computes without writing files.
        input.MaxItems — max files to process (default 100).

        Returns BatchResultSummary. Use get_operation_detail(changeId) for per-file details.
        Severity="halt" means the circuit breaker opened — call get_breaker_status then reset_breaker.
        """)]
    private async Task<BatchResultSummary> PropagateCancellationToken(
        BatchTargetInput  input,
        CancellationToken cancellationToken = default)
    {
        var halt = _workspaceManager.CheckBreaker();
        if (halt != null)
        {
            return halt;
        }

        var batchInput = new PropagateCtBatchInput
        {
            Targets = input.Targets.Select(t => new PropagateCtFileTarget
            {
                FilePath    = t.FilePath,
                MethodNames = t.MethodNames,
            }).ToList(),
            DryRun       = input.DryRun,
            MaxFiles     = input.MaxItems,
            FlagFailures = true,
        };

        PropagateCtBatchResult result;
        try
        {
            result = await _asyncBatchEngine.PropagateCancellationTokenBatchAsync(batchInput, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "PropagateCancellationToken batch unexpected exception");
            throw new InvalidOperationException(
                $"PropagateCancellationToken failed: {ex.GetType().Name}: {ex.Message}", ex);
        }

        int succeeded = result.Applied.Count;
        int failed    = result.Failed.Count;
        int skipped   = result.RemainingFiles;

        _workspaceManager.RecordBatchOutcome(succeeded, failed, rolledBack: 0, skipped: skipped);

        var changeId = Guid.NewGuid().ToString("N")[..8];
        var items    = new List<OperationItemRecord>();
        foreach (var a in result.Applied)
        {
            items.Add(new OperationItemRecord
            {
                FilePath = a.FilePath,
                Outcome  = a.TotalForwarded > 0 ? "succeeded" : "skipped",
                Reason   = a.TotalForwarded == 0 ? "no eligible call sites" : null,
            });
        }
        foreach (var f in result.Failed)
        {
            items.Add(new OperationItemRecord { FilePath = f.FilePath, Outcome = "failed", Reason = f.Reason });
        }

        var blobName  = await OperationBlobWriter.WriteAsync(
            "propagate_cancellation_token", changeId, items, _workspaceManager.GetSolutionRoot());
        var status    = _workspaceManager.GetBreakerStatus();
        var failures  = result.Failed
            .Take(15)
            .Select(f => new FailureDetail { FilePath = f.FilePath, Reason = f.Reason, Outcome = "failed" })
            .ToList();

        return new BatchResultSummary
        {
            ChangeId    = changeId,
            BlobName    = blobName,
            Succeeded   = succeeded,
            Failed      = failed,
            Skipped     = skipped,
            RolledBack  = 0,
            Attempted   = succeeded + failed + skipped,
            Failures    = failures,
            Severity    = status.Severity,
            Directive   = status.Directive,
            BreakerOpen = status.Open,
        };
    }

    [Description("""
        Batch-first Asyncify-bridge conversion. Supersedes the single-method
        convert_to_async_bridge_single. Applies the bridge transform to each named method
        across the provided targets. Checks the circuit breaker; records outcome; writes a forensic blob.

        input.Targets  — list of { FilePath, MethodNames } — MethodNames must be specified.
        input.DryRun   — when true, validates without writing files.
        input.MaxItems — max (file×method) items to process (default 100).
        propagateCancellationTokens — when true (default), propagates CT in the new async overload.

        Each method is applied sequentially and written to disk immediately so that later
        methods in the same file see the updated source. Errors on one method do not abort others.
        Returns BatchResultSummary. Use get_operation_detail(changeId) for per-method details.
        """)]
    private async Task<BatchResultSummary> ConvertToAsyncBridge(
        BatchTargetInput  input,
        bool              propagateCancellationTokens = true,
        CancellationToken cancellationToken = default)
    {
        var halt = _workspaceManager.CheckBreaker();
        if (halt != null)
        {
            return halt;
        }

        int succeeded = 0;
        int failed    = 0;
        int processed = 0;
        var items     = new List<OperationItemRecord>();
        var failures  = new List<FailureDetail>();

        foreach (var target in input.Targets)
        {
            if (target.MethodNames == null || target.MethodNames.Length == 0)
            {
                var fd = new FailureDetail
                {
                    FilePath = target.FilePath,
                    Reason   = "MethodNames must be specified for convert_to_async_bridge",
                    Outcome  = "failed",
                };
                items.Add(new OperationItemRecord { FilePath = target.FilePath, Outcome = "failed", Reason = fd.Reason });
                if (failures.Count < 15) { failures.Add(fd); }
                failed++;
                continue;
            }

            foreach (var methodName in target.MethodNames)
            {
                if (processed >= input.MaxItems)
                {
                    break;
                }

                processed++;

                if (input.DryRun)
                {
                    items.Add(new OperationItemRecord
                    {
                        FilePath   = target.FilePath,
                        MethodName = methodName,
                        Outcome    = "skipped",
                        Reason     = "dry_run",
                    });
                    succeeded++;
                    continue;
                }

                try
                {
                    var updatedSource = await _asyncOptimizationEngine.ConvertToAsyncBridgeAsync(
                        target.FilePath, methodName);

                    if (propagateCancellationTokens)
                    {
                        var asyncMethod = methodName + "Async";
                        var (propagated, _) = await _asyncOptimizationEngine
                            .PropagateCancellationTokenInMethodAsync(target.FilePath, asyncMethod, cancellationToken);
                        if (!string.IsNullOrEmpty(propagated))
                        {
                            updatedSource = propagated;
                        }
                    }

                    await _workspaceManager.ApplyProposedChangesAsync(
                        new Dictionary<string, string> { { target.FilePath, updatedSource } });

                    items.Add(new OperationItemRecord
                    {
                        FilePath   = target.FilePath,
                        MethodName = methodName,
                        Outcome    = "succeeded",
                    });
                    succeeded++;
                }
                catch (Exception ex)
                {
                    var reason = ex.Message;
                    items.Add(new OperationItemRecord
                    {
                        FilePath   = target.FilePath,
                        MethodName = methodName,
                        Outcome    = "failed",
                        Reason     = reason,
                    });
                    if (failures.Count < 15)
                    {
                        failures.Add(new FailureDetail
                        {
                            FilePath   = target.FilePath,
                            MethodName = methodName,
                            Reason     = reason,
                            Outcome    = "failed",
                        });
                    }
                    failed++;
                    _logger.LogWarning(
                        "ConvertToAsyncBridge batch: {Method} in {File} failed: {Reason}",
                        methodName, target.FilePath, reason);
                }
            }
        }

        _workspaceManager.RecordBatchOutcome(succeeded, failed, rolledBack: 0, skipped: 0);

        var changeId = Guid.NewGuid().ToString("N")[..8];
        var blobName = await OperationBlobWriter.WriteAsync(
            "convert_to_async_bridge", changeId, items, _workspaceManager.GetSolutionRoot());
        var status   = _workspaceManager.GetBreakerStatus();

        return new BatchResultSummary
        {
            ChangeId    = changeId,
            BlobName    = blobName,
            Succeeded   = succeeded,
            Failed      = failed,
            Skipped     = 0,
            RolledBack  = 0,
            Attempted   = succeeded + failed,
            Failures    = failures,
            Severity    = status.Severity,
            Directive   = status.Directive,
            BreakerOpen = status.Open,
        };
    }

    [Description("""
        Batch-first addition of CancellationToken parameters to async methods across multiple
        files. Supersedes add_cancellation_token_to_method and apply_cancellation_token_to_file.
        Checks the circuit breaker; records outcome; writes a forensic blob.

        input.Targets  — list of { FilePath, MethodNames? }. null MethodNames = all eligible async
                         methods in the file. MethodNames filter = only those specific methods.
        input.DryRun   — when true, computes without writing files.
        input.MaxItems — max files to process (default 100).

        Files are processed independently (no cross-file dependency). Each file's changes are
        collected then applied atomically. Returns BatchResultSummary.
        Use get_operation_detail(changeId) for per-file details.
        """)]
    private async Task<BatchResultSummary> AddCancellationToken(
        BatchTargetInput  input,
        CancellationToken cancellationToken = default)
    {
        var halt = _workspaceManager.CheckBreaker();
        if (halt != null)
        {
            return halt;
        }

        var allChanges = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        int succeeded  = 0;
        int failed     = 0;
        int skipped    = 0;
        var items      = new List<OperationItemRecord>();
        var failures   = new List<FailureDetail>();
        int processed  = 0;

        foreach (var target in input.Targets)
        {
            if (processed >= input.MaxItems)
            {
                skipped++;
                continue;
            }

            processed++;

            try
            {
                var (updatedSource, modified, skippedMethods) =
                    await _asyncOptimizationEngine.ApplyCancellationTokenToFileAsync(
                        target.FilePath, target.MethodNames, cancellationToken);

                if (updatedSource.StartsWith("// Error:"))
                {
                    var reason = updatedSource;
                    items.Add(new OperationItemRecord { FilePath = target.FilePath, Outcome = "failed", Reason = reason });
                    if (failures.Count < 15)
                    {
                        failures.Add(new FailureDetail { FilePath = target.FilePath, Reason = reason, Outcome = "failed" });
                    }
                    failed++;
                }
                else if (modified.Count == 0)
                {
                    items.Add(new OperationItemRecord
                    {
                        FilePath = target.FilePath,
                        Outcome  = "skipped",
                        Reason   = "no eligible async methods",
                    });
                    skipped++;
                }
                else
                {
                    if (!input.DryRun)
                    {
                        allChanges[target.FilePath] = updatedSource;
                    }

                    foreach (var m in modified)
                    {
                        items.Add(new OperationItemRecord
                        {
                            FilePath   = target.FilePath,
                            MethodName = m,
                            Outcome    = input.DryRun ? "skipped" : "succeeded",
                            Reason     = input.DryRun ? "dry_run" : null,
                        });
                    }

                    succeeded++;
                }
            }
            catch (Exception ex)
            {
                var reason = ex.Message;
                items.Add(new OperationItemRecord { FilePath = target.FilePath, Outcome = "failed", Reason = reason });
                if (failures.Count < 15)
                {
                    failures.Add(new FailureDetail { FilePath = target.FilePath, Reason = reason, Outcome = "failed" });
                }
                failed++;
                _logger.LogWarning("AddCancellationToken batch: {File} failed: {Reason}", target.FilePath, reason);
            }
        }

        if (allChanges.Count > 0)
        {
            await _workspaceManager.ApplyProposedChangesAsync(allChanges);
        }

        _workspaceManager.RecordBatchOutcome(succeeded, failed, rolledBack: 0, skipped: skipped);

        var changeId = Guid.NewGuid().ToString("N")[..8];
        var blobName = await OperationBlobWriter.WriteAsync(
            "add_cancellation_token", changeId, items, _workspaceManager.GetSolutionRoot());
        var status   = _workspaceManager.GetBreakerStatus();

        return new BatchResultSummary
        {
            ChangeId    = changeId,
            BlobName    = blobName,
            Succeeded   = succeeded,
            Failed      = failed,
            Skipped     = skipped,
            RolledBack  = 0,
            Attempted   = succeeded + failed + skipped,
            Failures    = failures,
            Severity    = status.Severity,
            Directive   = status.Directive,
            BreakerOpen = status.Open,
        };
    }

    [Description("""
        Batch-first caller uplift — uplifts callers of Asyncify-bridge sync wrappers to use
        their async overloads directly. Supersedes run_uplift_batch and run_uplift_batch_multi.
        Checks the circuit breaker; records outcome; writes a forensic blob.

        input.Targets                    — list of { BridgedMethodName, ProjectName? }.
        input.DryRun                     — when true, reports without writing files.
        input.MaxCallersPerMethod        — max callers per bridged method (default 10).
        input.PropagateCancellationTokens — when true (default), propagates CT in new async overloads.

        Returns BatchResultSummary where Succeeded = total callers uplifted, Failed = total callers
        skipped (flagged NeedsManualReview). Use get_operation_detail(changeId) for details.
        """)]
    private async Task<BatchResultSummary> RunUplift(
        RunUpliftInput    input,
        CancellationToken cancellationToken = default)
    {
        var halt = _workspaceManager.CheckBreaker();
        if (halt != null)
        {
            return halt;
        }

        var multiInput = new UpliftBatchMultiInput
        {
            Targets = input.Targets.Select(t => new UpliftBatchMultiTarget
            {
                BridgedMethodName = t.BridgedMethodName,
                ProjectName       = t.ProjectName,
            }).ToList(),
            MaxCallersPerMethod          = input.MaxCallersPerMethod,
            DryRun                       = input.DryRun,
            PropagateCancellationTokens  = input.PropagateCancellationTokens,
        };

        UpliftBatchMultiResult result;
        try
        {
            result = await _asyncBatchEngine.RunUpliftBatchMultiAsync(multiInput, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "RunUplift batch unexpected exception");
            throw new InvalidOperationException(
                $"RunUplift failed: {ex.GetType().Name}: {ex.Message}", ex);
        }

        int succeeded = result.TotalUplifted;
        int failed    = result.TotalSkipped;

        _workspaceManager.RecordBatchOutcome(succeeded, failed, rolledBack: 0, skipped: 0);

        var changeId = Guid.NewGuid().ToString("N")[..8];
        var items    = new List<OperationItemRecord>();
        foreach (var pm in result.PerMethod)
        {
            foreach (var u in pm.Result.Uplifted)
            {
                items.Add(new OperationItemRecord
                {
                    FilePath   = u.FilePath,
                    MethodName = u.CallerMethod,
                    Outcome    = "succeeded",
                });
            }
            foreach (var s in pm.Result.Skipped)
            {
                items.Add(new OperationItemRecord
                {
                    FilePath   = s.FilePath,
                    MethodName = s.CallerMethod,
                    Outcome    = "failed",
                    Reason     = s.Reason,
                });
            }
            if (pm.Error != null)
            {
                items.Add(new OperationItemRecord
                {
                    FilePath = pm.BridgedMethodName,
                    Outcome  = "failed",
                    Reason   = pm.Error,
                });
            }
        }

        var blobName = await OperationBlobWriter.WriteAsync(
            "run_uplift", changeId, items, _workspaceManager.GetSolutionRoot());
        var status   = _workspaceManager.GetBreakerStatus();
        var failures = result.PerMethod
            .SelectMany(pm => pm.Result.Skipped.Select(s => new FailureDetail
            {
                FilePath   = s.FilePath,
                MethodName = s.CallerMethod,
                Reason     = s.Reason,
                Outcome    = "failed",
            }))
            .Take(15)
            .ToList();

        return new BatchResultSummary
        {
            ChangeId    = changeId,
            BlobName    = blobName,
            Succeeded   = succeeded,
            Failed      = failed,
            Skipped     = 0,
            RolledBack  = 0,
            Attempted   = succeeded + failed,
            Failures    = failures,
            Severity    = status.Severity,
            Directive   = status.Directive,
            BreakerOpen = status.Open,
        };
    }

    [Description("""
        Batch-first migration-candidate flagging. Supersedes flag_migration_candidate (single),
        flag_migration_candidates_batch, and flag_migration_candidates_in_project.
        Checks the circuit breaker; records outcome; writes a forensic blob.

        input.Scope="targets" (default): flags an explicit list of methods.
          input.Targets — list of { FilePath, MethodName, Pattern, Score?, Reason? }.
        input.Scope="project": autonomous scan; scores and flags qualifying methods.
          input.ProjectName — restrict to one project; null = entire solution.
          input.Pattern     — migration pattern to apply (default "AsyncBridgeCandidate").
          input.MinScore    — minimum score to flag (default 50).
          input.DryRun      — when true, reports what would be flagged without writing files.
          input.ForceRescan — when true, ignores existing [MigrationCandidate] attributes.

        Returns BatchResultSummary where Succeeded = methods flagged, Skipped = below-minScore
        or already-flagged, Failed = errors. Use get_operation_detail(changeId) for details.
        """)]
    private async Task<BatchResultSummary> FlagMigrationCandidates(
        FlagCandidatesInput input,
        CancellationToken   cancellationToken = default)
    {
        var halt = _workspaceManager.CheckBreaker();
        if (halt != null)
        {
            return halt;
        }

        int succeeded = 0;
        int failed    = 0;
        int skipped   = 0;
        var items     = new List<OperationItemRecord>();
        var failures  = new List<FailureDetail>();
        var changeId  = Guid.NewGuid().ToString("N")[..8];

        try
        {
            if (input.Scope == "project")
            {
                var engineResult = await _asyncOptimizationEngine.FlagCandidatesInProjectAsync(
                    input.ProjectName, input.Pattern, input.MinScore, input.DryRun, input.ForceRescan);

                if (!input.DryRun && engineResult.Changes.Count > 0)
                {
                    await _workspaceManager.ApplyProposedChangesAsync(engineResult.Changes);
                }

                foreach (var f in engineResult.Flagged)
                {
                    items.Add(new OperationItemRecord
                    {
                        FilePath   = f.FilePath,
                        MethodName = f.MethodName,
                        Outcome    = input.DryRun ? "skipped" : "succeeded",
                        Reason     = input.DryRun ? "dry_run" : null,
                    });
                    succeeded++;
                }
                foreach (var s in engineResult.Skipped)
                {
                    items.Add(new OperationItemRecord
                    {
                        FilePath   = s.FilePath,
                        MethodName = s.MethodName,
                        Outcome    = "skipped",
                        Reason     = $"score {s.Score} below minScore {input.MinScore}",
                    });
                    skipped++;
                }
                foreach (var a in engineResult.AlreadyFlagged)
                {
                    items.Add(new OperationItemRecord
                    {
                        FilePath   = a.FilePath,
                        MethodName = a.MethodName,
                        Outcome    = "skipped",
                        Reason     = "already flagged",
                    });
                    skipped++;
                }
            }
            else
            {
                // scope="targets" — explicit list
                var targets = input.Targets ?? new List<FlagCandidateTarget>();
                var tuples  = targets.Select(t =>
                    (FilePath: t.FilePath, MethodName: t.MethodName,
                     Pattern: t.Pattern, Score: t.Score, Reason: t.Reason))
                    .ToList();

                var (results, errors) = await _asyncOptimizationEngine.FlagMultipleMigrationCandidatesAsync(tuples);

                var allChanges = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < results.Count; i++)
                {
                    var r    = results[i];
                    var tgt  = targets[i];
                    if (r.Line == -1)
                    {
                        continue;
                    }

                    foreach (var kv in r.Changes)
                    {
                        allChanges[kv.Key] = kv.Value;
                    }

                    items.Add(new OperationItemRecord
                    {
                        FilePath   = tgt.FilePath,
                        MethodName = tgt.MethodName,
                        Outcome    = "succeeded",
                    });
                    succeeded++;
                }

                foreach (var (idx, err) in errors)
                {
                    var tgt = targets[idx];
                    items.Add(new OperationItemRecord
                    {
                        FilePath   = tgt.FilePath,
                        MethodName = tgt.MethodName,
                        Outcome    = "failed",
                        Reason     = err,
                    });
                    if (failures.Count < 15)
                    {
                        failures.Add(new FailureDetail
                        {
                            FilePath   = tgt.FilePath,
                            MethodName = tgt.MethodName,
                            Reason     = err,
                            Outcome    = "failed",
                        });
                    }
                    failed++;
                }

                if (allChanges.Count > 0 && !input.DryRun)
                {
                    await _workspaceManager.ApplyProposedChangesAsync(allChanges);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "FlagMigrationCandidates batch unexpected exception");
            throw new InvalidOperationException(
                $"FlagMigrationCandidates failed: {ex.GetType().Name}: {ex.Message}", ex);
        }

        _workspaceManager.RecordBatchOutcome(succeeded, failed, rolledBack: 0, skipped: skipped);

        var blobName = await OperationBlobWriter.WriteAsync(
            "flag_migration_candidates", changeId, items, _workspaceManager.GetSolutionRoot());
        var status   = _workspaceManager.GetBreakerStatus();

        return new BatchResultSummary
        {
            ChangeId    = changeId,
            BlobName    = blobName,
            Succeeded   = succeeded,
            Failed      = failed,
            Skipped     = skipped,
            RolledBack  = 0,
            Attempted   = succeeded + failed + skipped,
            Failures    = failures,
            Severity    = status.Severity,
            Directive   = status.Directive,
            BreakerOpen = status.Open,
        };
    }

    // ── Phase 6 — asyncify macro-workflow ─────────────────────────────────────

    [Description("""
        Runs the full Asyncify migration sequence on a project (or explicit method list) in
        a single call. The server owns and executes the fixed sequence — the agent only supplies
        parameters.

        Fixed internal sequence:
          1. Flag   — discovers qualifying sync methods and flags them
                      [MigrationCandidate("AsyncBridgeCandidate")].
                      Skipped when MethodTargets is provided (pre-selected methods).
          2. Bridge — converts flagged methods to the Asyncify-bridge pattern (sync wrapper +
                      async overload). Each async overload optionally gets CT propagated.
          3. Uplift — uplifts callers of each bridge wrapper to use the async overload directly.
          4. Propagate CT — propagates CancellationToken in all files touched by bridge + uplift.
                      Skipped when PropagateCancellationTokens=false.

        Checks the circuit breaker before starting; records total outcome across all phases;
        writes one forensic blob. Returns BatchResultSummary.
        Succeeded = bridges + uplifts. Skipped = below-minScore / remaining candidates.
        Failed = bridge + uplift failures.

        input.ProjectName              — project to process; null = entire solution.
        input.MethodTargets            — explicit (FilePath, MethodName) list; skips flag phase.
        input.Exclusions               — method names to skip in every phase.
        input.DryRun                   — reports without writing files.
        input.PropagateCancellationTokens — run CT propagation after bridge+uplift (default true).
        input.MaxMethods               — max methods in bridge phase (default 50).
        input.MaxCallersPerMethod      — max callers per bridged method in uplift (default 10).
        input.MinScore                 — minimum score to flag in discovery phase (default 50).
        input.ScoreThreshold           — max score eligible for bridge conversion (default 60).

        Use get_operation_detail(changeId) to inspect per-phase, per-method results.
        """)]
    private async Task<BatchResultSummary> Asyncify(
        AsyncifyInput     input,
        CancellationToken cancellationToken = default)
    {
        var halt = _workspaceManager.CheckBreaker();
        if (halt != null)
        {
            return halt;
        }

        var items    = new List<OperationItemRecord>();
        var failures = new List<FailureDetail>();
        int succeeded = 0, failed = 0, skipped = 0;
        var changeId = Guid.NewGuid().ToString("N")[..8];

        try
        {
            // ── Phase 1: Flag ─────────────────────────────────────────────────
            if (input.MethodTargets == null || input.MethodTargets.Count == 0)
            {
                // Autonomous discovery: scan + flag candidates in the target project/solution.
                var flagResult = await _asyncOptimizationEngine.FlagCandidatesInProjectAsync(
                    input.ProjectName, "AsyncBridgeCandidate", input.MinScore,
                    input.DryRun, forceRescan: false);

                if (!input.DryRun && flagResult.Changes.Count > 0)
                {
                    await _workspaceManager.ApplyProposedChangesAsync(flagResult.Changes);
                }

                foreach (var f in flagResult.Flagged)
                {
                    if (input.Exclusions?.Contains(f.MethodName) == true)
                    {
                        items.Add(new OperationItemRecord
                        {
                            FilePath   = f.FilePath,
                            MethodName = f.MethodName,
                            Outcome    = "skipped",
                            Reason     = "excluded",
                        });
                        skipped++;
                        continue;
                    }

                    items.Add(new OperationItemRecord
                    {
                        FilePath   = f.FilePath,
                        MethodName = f.MethodName,
                        Outcome    = input.DryRun ? "skipped" : "succeeded",
                        Reason     = input.DryRun ? "dry_run:flag" : "phase:flag",
                    });
                    if (!input.DryRun) { succeeded++; }
                    else               { skipped++;   }
                }
                foreach (var s in flagResult.Skipped)
                {
                    items.Add(new OperationItemRecord
                    {
                        FilePath   = s.FilePath,
                        MethodName = s.MethodName,
                        Outcome    = "skipped",
                        Reason     = $"phase:flag — score {s.Score} below minScore {input.MinScore}",
                    });
                    skipped++;
                }
                foreach (var a in flagResult.AlreadyFlagged)
                {
                    items.Add(new OperationItemRecord
                    {
                        FilePath   = a.FilePath,
                        MethodName = a.MethodName,
                        Outcome    = "skipped",
                        Reason     = "phase:flag — already flagged",
                    });
                    skipped++;
                }
            }
            else
            {
                // Explicit targets: flag just the named methods.
                var tuples = input.MethodTargets
                    .Where(t => input.Exclusions?.Contains(t.MethodName) != true)
                    .Select(t => (FilePath: t.FilePath, MethodName: t.MethodName,
                                  Pattern: t.Pattern, Score: t.Score, Reason: t.Reason))
                    .ToList();

                if (tuples.Count > 0)
                {
                    var (flagResults, flagErrors) =
                        await _asyncOptimizationEngine.FlagMultipleMigrationCandidatesAsync(tuples);

                    var allChanges = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    for (int i = 0; i < flagResults.Count; i++)
                    {
                        var r = flagResults[i];
                        if (r.Line == -1) { continue; }

                        foreach (var kv in r.Changes) { allChanges[kv.Key] = kv.Value; }
                        items.Add(new OperationItemRecord
                        {
                            FilePath   = tuples[i].FilePath,
                            MethodName = tuples[i].MethodName,
                            Outcome    = "succeeded",
                            Reason     = "phase:flag",
                        });
                        succeeded++;
                    }
                    foreach (var (idx, err) in flagErrors)
                    {
                        items.Add(new OperationItemRecord
                        {
                            FilePath   = tuples[idx].FilePath,
                            MethodName = tuples[idx].MethodName,
                            Outcome    = "failed",
                            Reason     = $"phase:flag — {err}",
                        });
                        if (failures.Count < 15)
                        {
                            failures.Add(new FailureDetail
                            {
                                FilePath   = tuples[idx].FilePath,
                                MethodName = tuples[idx].MethodName,
                                Reason     = err,
                                Outcome    = "failed",
                            });
                        }
                        failed++;
                    }
                    if (!input.DryRun && allChanges.Count > 0)
                    {
                        await _workspaceManager.ApplyProposedChangesAsync(allChanges);
                    }
                }
            }

            // ── Phase 2: Bridge ───────────────────────────────────────────────
            var bridgeResult = await _asyncBatchEngine.RunBridgeBatchAsync(
                input.ProjectName,
                input.MaxMethods,
                input.ScoreThreshold,
                input.DryRun,
                input.PropagateCancellationTokens,
                cancellationToken);

            foreach (var a in bridgeResult.Applied)
            {
                if (input.Exclusions?.Contains(a.MethodName) == true) { continue; }
                items.Add(new OperationItemRecord
                {
                    FilePath   = a.FilePath,
                    MethodName = a.MethodName,
                    Outcome    = "succeeded",
                    Reason     = "phase:bridge",
                });
                succeeded++;
            }
            foreach (var s in bridgeResult.Skipped)
            {
                items.Add(new OperationItemRecord
                {
                    FilePath   = s.FilePath,
                    MethodName = s.MethodName,
                    Outcome    = "failed",
                    Reason     = $"phase:bridge — {s.Reason}",
                });
                if (failures.Count < 15)
                {
                    failures.Add(new FailureDetail
                    {
                        FilePath   = s.FilePath,
                        MethodName = s.MethodName,
                        Reason     = s.Reason,
                        Outcome    = "failed",
                    });
                }
                failed++;
            }
            if (bridgeResult.RemainingCandidates > 0)
            {
                skipped += bridgeResult.RemainingCandidates;
            }

            // ── Phase 3: Uplift ───────────────────────────────────────────────
            if (bridgeResult.Applied.Count > 0)
            {
                var upliftTargets = bridgeResult.Applied
                    .Where(a => input.Exclusions?.Contains(a.MethodName) != true)
                    .Select(a => new UpliftBatchMultiTarget
                    {
                        BridgedMethodName = a.MethodName,
                        ProjectName       = input.ProjectName,
                    })
                    .ToList();

                if (upliftTargets.Count > 0)
                {
                    var upliftResult = await _asyncBatchEngine.RunUpliftBatchMultiAsync(
                        new UpliftBatchMultiInput
                        {
                            Targets                     = upliftTargets,
                            MaxCallersPerMethod         = input.MaxCallersPerMethod,
                            DryRun                      = input.DryRun,
                            PropagateCancellationTokens = input.PropagateCancellationTokens,
                        },
                        cancellationToken);

                    foreach (var pm in upliftResult.PerMethod)
                    {
                        foreach (var u in pm.Result.Uplifted)
                        {
                            items.Add(new OperationItemRecord
                            {
                                FilePath   = u.FilePath,
                                MethodName = u.CallerMethod,
                                Outcome    = "succeeded",
                                Reason     = "phase:uplift",
                            });
                            succeeded++;
                        }
                        foreach (var s in pm.Result.Skipped)
                        {
                            items.Add(new OperationItemRecord
                            {
                                FilePath   = s.FilePath,
                                MethodName = s.CallerMethod,
                                Outcome    = "failed",
                                Reason     = $"phase:uplift — {s.Reason}",
                            });
                            if (failures.Count < 15)
                            {
                                failures.Add(new FailureDetail
                                {
                                    FilePath   = s.FilePath,
                                    MethodName = s.CallerMethod,
                                    Reason     = s.Reason,
                                    Outcome    = "failed",
                                });
                            }
                            failed++;
                        }
                    }
                }
            }

            // ── Phase 4: Propagate CT ─────────────────────────────────────────
            if (input.PropagateCancellationTokens && !input.DryRun)
            {
                var bridgedFiles = bridgeResult.Applied
                    .Select(a => a.FilePath)
                    .Distinct()
                    .ToList();

                if (bridgedFiles.Count > 0)
                {
                    var ctResult = await _asyncBatchEngine.PropagateCancellationTokenBatchAsync(
                        new PropagateCtBatchInput
                        {
                            Targets = bridgedFiles.Select(fp => new PropagateCtFileTarget
                                { FilePath = fp, MethodNames = null }).ToList(),
                            DryRun       = false,
                            MaxFiles     = bridgedFiles.Count,
                            FlagFailures = false,
                        },
                        cancellationToken);

                    foreach (var a in ctResult.Applied)
                    {
                        items.Add(new OperationItemRecord
                        {
                            FilePath = a.FilePath,
                            Outcome  = "succeeded",
                            Reason   = $"phase:propagate_ct — {a.TotalForwarded} call sites forwarded",
                        });
                    }
                    foreach (var f in ctResult.Failed)
                    {
                        items.Add(new OperationItemRecord
                        {
                            FilePath = f.FilePath,
                            Outcome  = "failed",
                            Reason   = $"phase:propagate_ct — {f.Reason}",
                        });
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Asyncify unexpected exception");
            throw new InvalidOperationException(
                $"Asyncify failed: {ex.GetType().Name}: {ex.Message}", ex);
        }

        _workspaceManager.RecordBatchOutcome(succeeded, failed, rolledBack: 0, skipped: skipped);

        var blobName2 = await OperationBlobWriter.WriteAsync(
            "asyncify", changeId, items, _workspaceManager.GetSolutionRoot());
        var status2   = _workspaceManager.GetBreakerStatus();

        return new BatchResultSummary
        {
            ChangeId    = changeId,
            BlobName    = blobName2,
            Succeeded   = succeeded,
            Failed      = failed,
            Skipped     = skipped,
            RolledBack  = 0,
            Attempted   = succeeded + failed + skipped,
            Failures    = failures,
            Severity    = status2.Severity,
            Directive   = status2.Directive,
            BreakerOpen = status2.Open,
        };
    }

    // ── Phase 7 — async_migrate dispatcher ────────────────────────────────────

    [McpServerTool]
    [Description("""
        Unified dispatcher for the six async-migration operations.

        operation values and required input fields:
          "propagate_cancellation_token"
              input.Targets         — list of { FilePath, MethodNames? }
              input.DryRun          — optional, default false
              input.MaxItems        — optional, default 100
          "convert_to_async_bridge"
              input.Targets         — list of { FilePath, MethodNames } (MethodNames required)
              input.DryRun          — optional, default false
              input.PropagateCancellationTokens — optional, default true
          "add_cancellation_token"
              input.Targets         — list of { FilePath, MethodNames? }
              input.DryRun          — optional, default false
              input.MaxItems        — optional, default 100
          "run_uplift"
              input.UpliftTargets   — list of { BridgedMethodName, ProjectName? }
              input.DryRun          — optional, default false
              input.MaxCallersPerMethod       — optional, default 10
              input.PropagateCancellationTokens — optional, default true
          "flag_migration_candidates"
              input.FlagScope       — "targets" (default) or "project"
              input.FlagTargets     — list of { FilePath, MethodName, Pattern, Score?, Reason? } (scope=targets)
              input.ProjectName     — project name (scope=project); null = entire solution
              input.Pattern         — optional, default "AsyncBridgeCandidate"
              input.MinScore        — optional, default 50
              input.DryRun          — optional, default false
              input.ForceRescan     — optional, default false
          "asyncify"
              input.ProjectName     — project; null = entire solution
              input.MethodTargets   — explicit method targets (skips discovery phase)
              input.Exclusions      — method names to skip
              input.DryRun          — optional, default false
              input.PropagateCancellationTokens — optional, default true
              input.MaxMethods      — optional, default 50
              input.MaxCallersPerMethod       — optional, default 10
              input.MinScore        — optional, default 50
              input.ScoreThreshold  — optional, default 60

        Returns BatchResultSummary. Use get_operation_detail(changeId) for per-item details.
        """)]
    public async Task<BatchResultSummary> AsyncMigrate(
        string            operation,
        AsyncMigrateInput input,
        CancellationToken cancellationToken = default)
    {
        return operation switch
        {
            "propagate_cancellation_token" => await PropagateCancellationToken(
                new BatchTargetInput
                {
                    Targets  = input.Targets ?? [],
                    DryRun   = input.DryRun,
                    MaxItems = input.MaxItems,
                },
                cancellationToken),

            "convert_to_async_bridge" => await ConvertToAsyncBridge(
                new BatchTargetInput
                {
                    Targets  = input.Targets ?? [],
                    DryRun   = input.DryRun,
                    MaxItems = input.MaxItems,
                },
                input.PropagateCancellationTokens,
                cancellationToken),

            "add_cancellation_token" => await AddCancellationToken(
                new BatchTargetInput
                {
                    Targets  = input.Targets ?? [],
                    DryRun   = input.DryRun,
                    MaxItems = input.MaxItems,
                },
                cancellationToken),

            "run_uplift" => await RunUplift(
                new RunUpliftInput
                {
                    Targets                    = input.UpliftTargets ?? [],
                    DryRun                     = input.DryRun,
                    MaxCallersPerMethod        = input.MaxCallersPerMethod,
                    PropagateCancellationTokens = input.PropagateCancellationTokens,
                },
                cancellationToken),

            "flag_migration_candidates" => await FlagMigrationCandidates(
                new FlagCandidatesInput
                {
                    Scope       = input.FlagScope,
                    Targets     = input.FlagTargets,
                    ProjectName = input.ProjectName,
                    Pattern     = input.Pattern,
                    MinScore    = input.MinScore,
                    DryRun      = input.DryRun,
                    ForceRescan = input.ForceRescan,
                },
                cancellationToken),

            "asyncify" => await Asyncify(
                new AsyncifyInput
                {
                    ProjectName                  = input.ProjectName,
                    MethodTargets                = input.MethodTargets,
                    Exclusions                   = input.Exclusions,
                    DryRun                       = input.DryRun,
                    PropagateCancellationTokens  = input.PropagateCancellationTokens,
                    MaxMethods                   = input.MaxMethods,
                    MaxCallersPerMethod          = input.MaxCallersPerMethod,
                    MinScore                     = input.MinScore,
                    ScoreThreshold               = input.ScoreThreshold,
                },
                cancellationToken),

            _ => throw new ArgumentException(
                $"Unknown operation '{operation}'. Valid: propagate_cancellation_token, " +
                "convert_to_async_bridge, add_cancellation_token, run_uplift, " +
                "flag_migration_candidates, asyncify.",
                nameof(operation))
        };
    }
}
